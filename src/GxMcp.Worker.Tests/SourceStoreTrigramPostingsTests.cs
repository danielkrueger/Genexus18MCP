using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxMcp.Worker.Services;
using GxMcp.Worker.Models;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #374: on catalog load (warm reopen) the store read, decompressed and
    /// hash-checked every stored record to rebuild the trigram index, so startup I/O
    /// scaled with the whole store on every Worker start. The derived postings are now
    /// persisted beside the catalog, stamped with the catalog's own digest.
    ///
    /// <para>
    /// The guards cover both halves: a reopen of an unchanged store must not read bodies,
    /// and anything unproven about the postings file - missing, stale, partial, corrupt -
    /// must fall back to the full rebuild rather than answer from it.
    /// </para>
    /// </summary>
    public class SourceStoreTrigramPostingsTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly List<string> _guids = new List<string>();

        public SourceStoreTrigramPostingsTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-postings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            SourceStoreService.Instance.SetStoreDirectoryForTest(_tempDir);
        }

        public void Dispose()
        {
            SourceStoreService.Instance.SetStoreDirectoryForTest(null);
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        private static string GuidFor(int i) => i.ToString("D8") + "-0000-0000-0000-000000000000";

        /// <summary>Distinct text per record, so each has its own trigram set.</summary>
        private string Body(int i) =>
            "// synthetic source " + i + " " + new string((char)('a' + i % 26), 200);

        private bool Put(int i)
        {
            string guid = GuidFor(i);
            _guids.Add(guid);
            return SourceStoreService.Instance.Put(guid, "Source", Body(i),
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i), "v1");
        }

        private string PostingsPath => Path.Combine(_tempDir, "trigram-postings.json.gz");

        /// <summary>Force the catalog to disk so a simulated reopen has something to read.</summary>
        private void FlushCatalog()
        {
            SourceStoreService.Instance.FlushCatalogForTest();
            Assert.True(File.Exists(Path.Combine(_tempDir, "catalog.json.gz")), "the catalog was not written");
        }

        [Fact]
        public void A_Warm_Reopen_Of_An_Unchanged_Store_Reads_No_Bodies()
        {
            for (int i = 0; i < 40; i++) Assert.True(Put(i));
            FlushCatalog();
            Assert.True(File.Exists(PostingsPath), "postings were not persisted beside the catalog");

            // Count every body file read during the reopen. The store's own directory is
            // left in place, so a body read here is real work the postings should have
            // replaced.
            long before = SourceStoreService.Instance.ContentValidations;
            SourceStoreService.Instance.ReloadCatalogForTest();

            Assert.Equal(before, SourceStoreService.Instance.ContentValidations);
            Assert.Equal(40, SourceStoreService.Instance.TrigramIndexedRecordCount);
        }

        [Fact]
        public void Postings_Are_Rejected_When_They_Describe_A_Different_Catalog()
        {
            for (int i = 0; i < 10; i++) Assert.True(Put(i));
            FlushCatalog();

            // The crash-between-writes case: the catalog lands but the postings file is
            // still the one written for the previous catalog, so its stamp no longer
            // matches. Trusting it would answer from postings that describe bodies that
            // are no longer stored - a false negative.
            var stalePostings = File.ReadAllBytes(PostingsPath);
            Assert.True(Put(0));
            Assert.True(SourceStoreService.Instance.Put(GuidFor(0), "Source", "// replaced body",
                new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), "v2"));
            FlushCatalog();
            File.WriteAllBytes(PostingsPath, stalePostings);

            SourceStoreService.Instance.ReloadCatalogForTest();

            Assert.Equal(10, SourceStoreService.Instance.TrigramIndexedRecordCount);

            // And the replaced record answers for its NEW content, not the old set.
            var trigrams = SourceStoreService.Instance.TrigramsForRecordForTest(GuidFor(0), "Source");
            Assert.NotNull(trigrams);
            Assert.Contains("rep", trigrams);
        }

        [Fact]
        public void A_Corrupt_Postings_File_Falls_Back_To_A_Full_Rebuild()
        {
            for (int i = 0; i < 10; i++) Assert.True(Put(i));
            FlushCatalog();
            File.WriteAllBytes(PostingsPath, new byte[] { 0x1f, 0x8b, 0x00, 0xFF, 0xFF });

            SourceStoreService.Instance.ReloadCatalogForTest();

            Assert.Equal(10, SourceStoreService.Instance.TrigramIndexedRecordCount);
        }

        [Fact]
        public void A_Partial_Postings_File_Falls_Back_To_A_Full_Rebuild()
        {
            for (int i = 0; i < 10; i++) Assert.True(Put(i));
            FlushCatalog();
            SourceStoreService.Instance.DropHalfThePostingsForTest();

            SourceStoreService.Instance.ReloadCatalogForTest();

            // A file that covers only some records cannot answer absence for the rest.
            Assert.Equal(10, SourceStoreService.Instance.TrigramIndexedRecordCount);
        }

        [Fact]
        public void A_Reopen_Starts_Uncertified_So_A_Postings_Hit_Is_Not_A_Freshness_Claim()
        {
            for (int i = 0; i < 5; i++) Assert.True(Put(i));
            FlushCatalog();
            Assert.Equal(0, SourceStoreService.Instance.CertificationCount);

            SourceStoreService.Instance.ReloadCatalogForTest();

            // Certifications live in memory by design, so a reopen must not be able to
            // answer "fresh" without validating the body again. Loading postings does not
            // certify anything.
            Assert.Equal(0, SourceStoreService.Instance.CertificationCount);
        }

        [Fact]
        public void The_Postings_Reopen_Produces_Exactly_The_Sets_A_Rebuild_Produces()
        {
            // No false negatives: the persisted path and the from-bodies path must agree
            // for every record, which is what "correct search results either way" means.
            for (int i = 0; i < 30; i++) Assert.True(Put(i));
            FlushCatalog();

            SourceStoreService.Instance.ReloadCatalogForTest();
            var fromPostings = Snapshot();

            SourceStoreService.Instance.DiscardPostingsForTest();
            SourceStoreService.Instance.ReloadCatalogForTest();
            var fromRebuild = Snapshot();

            Assert.Equal(fromRebuild.Count, fromPostings.Count);
            foreach (var key in fromRebuild.Keys)
            {
                Assert.True(fromPostings.ContainsKey(key), $"record {key} is missing after a postings reopen");
                Assert.Equal(
                    fromRebuild[key].OrderBy(x => x, StringComparer.Ordinal),
                    fromPostings[key].OrderBy(x => x, StringComparer.Ordinal));
            }
        }

        private static Dictionary<string, HashSet<string>> Snapshot()
        {
            var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < 30; i++)
                map[GuidFor(i)] = SourceStoreService.Instance.TrigramsForRecordForTest(GuidFor(i), "Source");
            return map;
        }

        [Fact]
        public void Replacing_A_Record_After_A_Reopen_Still_Subtracts_Its_Old_Postings()
        {
            for (int i = 0; i < 10; i++) Assert.True(Put(i));
            FlushCatalog();
            SourceStoreService.Instance.ReloadCatalogForTest();

            // #344's reclamation has to work against the persisted sets: a record that came
            // from disk must still know what to subtract.
            var before = SourceStoreService.Instance.TrigramsForRecordForTest(GuidFor(0), "Source");
            Assert.NotNull(before);
            Assert.NotEmpty(before);

            // A body with a disjoint trigram set, so the old ones must actually go.
            Assert.True(SourceStoreService.Instance.Put(GuidFor(0), "Source", "zzzqqqxxx",
                new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), "v2"));

            var index = SourceStoreService.Instance.TrigramIndexForTest();
            var key = SourceStoreService.Instance.MakeKeyForTest(GuidFor(0), "Source");
            // Stale means the old trigram STILL points at this record. A trigram that has
            // disappeared from the index entirely is the desired outcome, not a leak.
            var stillMapped = before
                .Where(t => index.TryGetValue(t, out var set) && set.Contains(key))
                .ToList();
            Assert.Empty(stillMapped);
        }
    }
}