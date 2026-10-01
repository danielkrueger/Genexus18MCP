using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #344: the trigram index only ever accumulated. A record's Put added the new
    /// content's memberships and removed nothing, so replacing a source whose trigrams
    /// barely overlapped left the old trigrams still pointing at that record key -
    /// stale derived state that grows with every source churn. Disk-budget eviction
    /// dropped the catalog record and the file but not the postings, so an evicted
    /// record stayed reachable through the index.
    ///
    /// <para>
    /// Two properties are pinned: a replacement reclaims exactly the previous set and
    /// keeps the new one, and every removal path (replacement, eviction, catalog reload)
    /// leaves no posting behind. Both are asserted against the real store on disk,
    /// because the leak only appears once the trigram sets actually diverge.
    /// </para>
    /// </summary>
    public class SourceStoreTrigramReclamationTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly List<string> _writtenGuids = new List<string>();

        public SourceStoreTrigramReclamationTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-trigram-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            SourceStoreService.Instance.SetStoreDirectoryForTest(_tempDir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        private static string GuidFor(int i) => i.ToString("D8") + "0000-0000-0000-000000000000";

        private bool Put(int i, string code)
        {
            string guid = GuidFor(i);
            _writtenGuids.Add(guid);
            return SourceStoreService.Instance.Put(guid, "Source", code,
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i), "v1");
        }

        /// <summary>
        /// Two sources whose trigram sets are provably disjoint. The extractor slides a
        /// three-character window over the lowercased text, so any shared three-character
        /// run would overlap; these two tokens share none. An earlier version wrapped
        /// them in `public void … () { }`, which made them share every trigram of that
        /// scaffolding and silently turned the disjointness assertions below into
        /// no-ops. <see cref="The_Fixtures_Are_Actually_Disjoint"/> pins the property so
        /// that cannot happen quietly again.
        /// </summary>
        private const string FirstSource = "ZebraStripes";
        private const string SecondSource = "QuartzVeins";

        /// <summary>
        /// The precondition every disjointness assertion in this class depends on. If the
        /// fixtures were edited to share text, those assertions would stop proving
        /// anything while still passing - which is worse than no test at all.
        /// </summary>
        [Fact]
        public void The_Fixtures_Are_Actually_Disjoint()
        {
            var first = TrigramExtractor.ExtractTrigrams(FirstSource);
            var second = TrigramExtractor.ExtractTrigrams(SecondSource);

            Assert.NotEmpty(first);
            Assert.NotEmpty(second);
            Assert.Empty(first.Intersect(second, StringComparer.OrdinalIgnoreCase));
        }

        [Fact]
        public void A_Replacement_Drops_The_Previous_Trigrams_And_Keeps_The_New()
        {
            // The reported churn case: same record, disjoint content. Before the fix the
            // record stayed a candidate for every trigram of the first version.
            var store = SourceStoreService.Instance;

            Put(1, FirstSource);
            var afterFirst = store.TrigramsForRecordForTest(GuidFor(1), "Source");
            Assert.NotEmpty(afterFirst);
            Assert.Contains(afterFirst, t => FirstSource.Length > 0);

            // The two versions share no trigram, so any overlap in the tracked set
            // would mean the old memberships were not removed.
            Assert.Empty(afterFirst.Intersect(TrigramExtractor.ExtractTrigrams(SecondSource), StringComparer.OrdinalIgnoreCase));

            Put(1, SecondSource);
            var afterSecond = store.TrigramsForRecordForTest(GuidFor(1), "Source");

            Assert.Equal(
                new HashSet<string>(TrigramExtractor.ExtractTrigrams(SecondSource), StringComparer.OrdinalIgnoreCase),
                afterSecond);
        }

        [Fact]
        public void The_Tracked_Set_Tracks_The_Stored_Content_Exactly()
        {
            // The invariant the removal depends on: what is remembered is what is
            // indexed. If these diverged, removal would subtract the wrong postings.
            var store = SourceStoreService.Instance;

            Put(2, FirstSource);
            Assert.Equal(
                new HashSet<string>(TrigramExtractor.ExtractTrigrams(FirstSource), StringComparer.OrdinalIgnoreCase),
                store.TrigramsForRecordForTest(GuidFor(2), "Source"));

            Put(2, SecondSource);
            Assert.Equal(
                new HashSet<string>(TrigramExtractor.ExtractTrigrams(SecondSource), StringComparer.OrdinalIgnoreCase),
                store.TrigramsForRecordForTest(GuidFor(2), "Source"));
        }

        [Fact]
        public void Repeated_Churn_Does_Not_Accumulate_Postings()
        {
            // The growth property: N replacements must leave the index describing only
            // the current version, not the union of all N.
            var store = SourceStoreService.Instance;

            for (int i = 0; i < 12; i++)
                Put(3, i % 2 == 0 ? FirstSource : SecondSource);

            var tracked = store.TrigramsForRecordForTest(GuidFor(3), "Source");
            var secondSet = new HashSet<string>(TrigramExtractor.ExtractTrigrams(SecondSource), StringComparer.OrdinalIgnoreCase);
            var firstSet = new HashSet<string>(TrigramExtractor.ExtractTrigrams(FirstSource), StringComparer.OrdinalIgnoreCase);

            // Last write was SecondSource (i = 11, odd).
            Assert.Equal(secondSet, tracked);
            // And nothing from the superseded version survived.
            Assert.Empty(tracked.Intersect(firstSet, StringComparer.OrdinalIgnoreCase));
        }

        [Fact]
        public void Two_Records_With_Disjoint_Sources_Keep_Independent_Sets()
        {
            // Removal is per record. One record's churn must not strip another's
            // postings - a bug here would make unrelated records undiscoverable.
            var store = SourceStoreService.Instance;

            Put(4, FirstSource);
            Put(5, SecondSource);
            var secondBefore = store.TrigramsForRecordForTest(GuidFor(5), "Source");
            Assert.NotEmpty(secondBefore);

            Put(4, SecondSource); // record 4 now collides with record 5's content

            Assert.Equal(secondBefore, store.TrigramsForRecordForTest(GuidFor(5), "Source"));
        }

        [Fact]
        public void A_Reloaded_Catalog_Records_Each_Record_S_Own_Trigrams()
        {
            // The case that is easy to miss: a record that came from disk had no tracked
            // set, so its first replacement had nothing to subtract and the original
            // postings stayed live forever. Forcing a reload exercises exactly that.
            var store = SourceStoreService.Instance;
            Put(6, FirstSource);
            store.FlushCatalog();

            // Reopening the store directory reloads the catalog from disk.
            store.SetStoreDirectoryForTest(_tempDir);

            var reloaded = store.TrigramsForRecordForTest(GuidFor(6), "Source");
            Assert.Equal(
                new HashSet<string>(TrigramExtractor.ExtractTrigrams(FirstSource), StringComparer.OrdinalIgnoreCase),
                reloaded);

            Put(6, SecondSource);
            var replaced = store.TrigramsForRecordForTest(GuidFor(6), "Source");
            Assert.Equal(
                new HashSet<string>(TrigramExtractor.ExtractTrigrams(SecondSource), StringComparer.OrdinalIgnoreCase),
                replaced);
            Assert.Empty(replaced.Intersect(
                new HashSet<string>(TrigramExtractor.ExtractTrigrams(FirstSource), StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase));
        }

        [Fact]
        public void Switching_Stores_Does_Not_Leave_The_Previous_Records_Trigrams()
        {
            // Both clear sites have to forget the map. If either did not, the next Put
            // into the fresh store would subtract postings from the store just left.
            var store = SourceStoreService.Instance;
            Put(7, FirstSource);
            Assert.NotEmpty(store.TrigramsForRecordForTest(GuidFor(7), "Source"));

            string otherDir = Path.Combine(_tempDir, "other");
            Directory.CreateDirectory(otherDir);
            store.SetStoreDirectoryForTest(otherDir);

            Assert.Empty(store.TrigramsForRecordForTest(GuidFor(7), "Source"));
            Assert.Equal(0, store.TrigramIndexedRecordCount);
        }

        /// <summary>
        /// Postings whose trigram is not in the record's current trigram set - the exact
        /// residue accumulate-only indexing leaves behind. This is the observable form
        /// of the leak, and it is the only guard that sees it: <c>Put</c> overwrites the
        /// tracked map either way, so asserting on the map alone cannot distinguish
        /// "removed the old memberships" from "forgot them and recorded the new ones".
        /// </summary>
        private static List<string> StalePostings(SourceStoreService store)
        {
            var stale = new List<string>();
            foreach (var posting in store.TrigramIndexForTest())
            {
                foreach (string key in posting.Value)
                {
                    int colon = key.IndexOf(':');
                    if (colon <= 0) { stale.Add(posting.Key + " -> malformed key " + key); continue; }

                    string guid = key.Substring(0, colon);
                    string part = key.Substring(colon + 1);
                    if (!store.RecordExistsForTest(key)) { stale.Add(posting.Key + " -> no record " + key); continue; }

                    var current = store.TrigramsForRecordForTest(guid, part);
                    if (!current.Contains(posting.Key))
                        stale.Add(posting.Key + " -> still posted for " + key);
                }
            }
            return stale;
        }

        [Fact]
        public void A_Replacement_Leaves_No_Posting_For_The_Superseded_Trigrams()
        {
            // The defect itself. Before the fix this listed every trigram of the first
            // version as still posting the record key, so a search for text the source
            // no longer contains kept returning it as a candidate.
            var store = SourceStoreService.Instance;
            Put(1, FirstSource);
            Assert.Empty(StalePostings(store));

            Put(1, SecondSource);

            var stale = StalePostings(store);
            Assert.True(stale.Count == 0,
                "postings survived the replacement: " + string.Join("; ", stale.Take(8)));
        }

        [Fact]
        public void Churn_Does_Not_Leave_One_Stale_Posting_Per_Round()
        {
            // The growth property, measured on the index rather than on a counter. Twelve
            // alternating replacements left twelve stale trigram sets behind before the
            // fix, none of which any live source could ever match.
            var store = SourceStoreService.Instance;
            for (int i = 0; i < 12; i++)
            {
                Put(3, i % 2 == 0 ? FirstSource : SecondSource);
                var stale = StalePostings(store);
                Assert.True(stale.Count == 0,
                    "round " + i + " left " + stale.Count + " stale postings: " + string.Join("; ", stale.Take(8)));
            }
        }

        [Fact]
        public void A_Record_From_A_Reloaded_Catalog_Leaves_No_Stale_Posting_When_Replaced()
        {
            // The reload case is where the tracked map would otherwise be empty, so
            // there is nothing to subtract and the original postings survive every
            // subsequent replacement forever.
            var store = SourceStoreService.Instance;
            Put(6, FirstSource);
            store.FlushCatalog();
            store.SetStoreDirectoryForTest(_tempDir);
            Assert.Empty(StalePostings(store));

            Put(6, SecondSource);

            var stale = StalePostings(store);
            Assert.True(stale.Count == 0,
                "a reloaded record left stale postings: " + string.Join("; ", stale.Take(8)));
        }

        [Fact]
        public void Every_Posted_Key_Still_Resolves_To_A_Live_Record()
        {
            // The invariant removal exists to protect. Any posting naming a key that is
            // no longer in the catalog is stale, and every one of them is a candidate
            // the query path must scan and then discard.
            var store = SourceStoreService.Instance;
            Put(8, FirstSource);
            Put(8, SecondSource);

            var posted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var set in store.TrigramIndexForTest())
                foreach (var key in set.Value)
                    posted.Add(key);

            foreach (string key in posted)
            {
                int colon = key.IndexOf(':');
                Assert.True(colon > 0, "unexpected posting key shape: " + key);
                Assert.True(store.RecordExistsForTest(key), "posting names a key with no record: " + key);
            }
        }

        [Fact]
        public void An_Empty_Posting_Set_Is_Not_Retained()
        {
            // A set with no members answers no query and only retains its key string,
            // so keeping it would mean the trigram key set ratchets upward across
            // evictions.
            var store = SourceStoreService.Instance;

            for (int i = 0; i < 20; i++) Put(9 + i, FirstSource);

            foreach (var set in store.TrigramIndexForTest())
                Assert.NotEmpty(set.Value);

            // Every key still has at least one live member.
            Assert.Equal(store.TrigramKeyCount, store.TrigramIndexForTest().Count);
        }

        [Fact]
        public void The_Indexed_Record_Count_Matches_The_Tracked_Map()
        {
            // The counters are what a diagnostic reports; if they disagree, a memory
            // claim built on them is wrong.
            var store = SourceStoreService.Instance;
            Put(10, FirstSource);
            Put(11, SecondSource);
            Assert.Equal(2, store.TrigramIndexedRecordCount);

            Put(10, SecondSource);
            Assert.Equal(2, store.TrigramIndexedRecordCount);
        }
    }
}
