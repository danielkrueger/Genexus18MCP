using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #337: the lite-index walk deduplicated with
    /// <c>list.RemoveAll(e =&gt; e.Guid == guid)</c> per walked object, so a cold
    /// walk over N unique objects performed N*(N-1)/2 GUID comparisons and found
    /// nothing. These guards assert on the comparison count (an algorithmic budget
    /// that is stable on any host) and on the resulting entry order/content, which
    /// the replacement must preserve exactly.
    /// </summary>
    public class LiteEntryAccumulatorTests
    {
        private static SearchIndex.IndexEntry Entry(string guid, string name) => new SearchIndex.IndexEntry
        {
            Guid = guid,
            Name = name,
            Type = "Procedure"
        };

        private static string Guid(int i) => i.ToString("D8") + "-0000-0000-0000-000000000000";

        [Fact]
        public void Unique_Walk_Comparisons_Stay_Linear_In_Entries()
        {
            // The defect: RemoveAll scans every accumulated position per upsert, so
            // 4000 unique objects cost ~8,000,000 comparisons. The budget below is a
            // small multiple of N, which only a per-entry constant can meet.
            const int n = 4000;
            var accumulator = new LiteEntryAccumulator();
            for (int i = 0; i < n; i++) accumulator.Upsert(Entry(Guid(i), "SyntheticObject" + i));

            long budget = 8L * n;
            Assert.True(accumulator.Comparisons <= budget,
                $"expected at most {budget} GUID comparisons for {n} unique entries, got {accumulator.Comparisons}");
            Assert.Equal(0, accumulator.FullScanFallbacks);
            Assert.Equal(n, accumulator.Entries.Count);
        }

        [Fact]
        public void Comparison_Growth_Is_Linear_Not_Quadratic()
        {
            long At(int n)
            {
                var accumulator = new LiteEntryAccumulator();
                for (int i = 0; i < n; i++) accumulator.Upsert(Entry(Guid(i), "O" + i));
                return accumulator.Comparisons;
            }

            long small = At(2000);
            long large = At(4000);

            // Doubling the catalog must not more than roughly double the work. The
            // defective implementation grows ~4x here.
            Assert.True(large <= small * 3 + 64,
                $"comparisons grew from {small} at N=2000 to {large} at N=4000, which is not linear");
        }

        [Fact]
        public void Unique_Entries_Preserve_Walk_Order()
        {
            var accumulator = new LiteEntryAccumulator();
            for (int i = 0; i < 50; i++) accumulator.Upsert(Entry(Guid(i), "O" + i));

            var names = accumulator.Entries.Select(e => e.Name).ToList();
            Assert.Equal(Enumerable.Range(0, 50).Select(i => "O" + i).ToList(), names);
        }

        [Fact]
        public void Duplicate_Guid_Replaces_Rather_Than_Appends()
        {
            var accumulator = new LiteEntryAccumulator();
            accumulator.Upsert(Entry(Guid(1), "First"));
            accumulator.Upsert(Entry(Guid(2), "Other"));
            accumulator.Upsert(Entry(Guid(1), "Renamed"));

            var entries = accumulator.Entries;
            Assert.Equal(2, entries.Count);
            Assert.Equal("Renamed", entries.Single(e => e.Guid == Guid(1)).Name);
            // Replacement keeps the surviving entry in its original position and puts
            // the replacement last - the same order RemoveAll-then-Add produced.
            Assert.Equal("Other", entries[0].Name);
            Assert.Equal("Renamed", entries[1].Name);
        }

        [Fact]
        public void Guid_Matching_Is_Case_Insensitive_Like_The_Previous_Predicate()
        {
            var accumulator = new LiteEntryAccumulator();
            accumulator.Upsert(Entry("AABBCCDD-0000-0000-0000-000000000000", "First"));
            accumulator.Upsert(Entry("aabbccdd-0000-0000-0000-000000000000", "Second"));

            Assert.Equal(1, accumulator.Entries.Count);
        }

        [Fact]
        public void Entries_Without_A_Guid_Are_Always_Appended()
        {
            var accumulator = new LiteEntryAccumulator();
            accumulator.Upsert(Entry(null, "NoGuidA"));
            accumulator.Upsert(Entry("", "NoGuidB"));

            // They cannot be identified, so they must not collapse into one another.
            Assert.Equal(2, accumulator.Entries.Count);
        }

        [Fact]
        public void Seeded_Resume_Checkpoint_Replaces_By_Guid_And_Keeps_The_Original_Order()
        {
            var seed = new List<SearchIndex.IndexEntry>
            {
                Entry(Guid(1), "O1"),
                Entry(Guid(2), "O2"),
                Entry(Guid(3), "O3")
            };
            var accumulator = new LiteEntryAccumulator(seed);
            accumulator.Upsert(Entry(Guid(2), "O2Renamed"));

            var entries = accumulator.Entries;
            Assert.Equal(3, entries.Count);
            // The replaced entry keeps the surviving neighbours' relative order and the
            // replacement lands last - exactly what RemoveAll-then-Add produced.
            Assert.Equal(new[] { "O1", "O3", "O2Renamed" }, entries.Select(e => e.Name).ToList());
        }

        [Fact]
        public void Duplicate_Guid_Inside_A_Resume_Seed_Collapses_Exactly_Like_RemoveAll()
        {
            // A checkpoint keyed by its own identity can legitimately hold two
            // entries sharing a GUID. The fast path cannot express that removal, so
            // it must fall back and produce the RemoveAll outcome exactly.
            var seed = new List<SearchIndex.IndexEntry>
            {
                Entry(Guid(1), "First"),
                Entry(Guid(2), "Between"),
                Entry(Guid(1), "AlsoFirst")
            };
            var accumulator = new LiteEntryAccumulator(seed);
            accumulator.Upsert(Entry(Guid(1), "Replacement"));

            var entries = accumulator.Entries;
            Assert.Equal(1, accumulator.FullScanFallbacks);
            Assert.Equal(new[] { "Between", "Replacement" }, entries.Select(e => e.Name).ToList());
        }

        [Fact]
        public void No_Tombstone_Survives_Compaction()
        {
            var accumulator = new LiteEntryAccumulator();
            for (int i = 0; i < 20; i++)
            {
                accumulator.Upsert(Entry(Guid(i), "O" + i));
                accumulator.Upsert(Entry(Guid(i), "O" + i + "Rev"));
            }

            var entries = accumulator.Entries;
            Assert.Equal(20, entries.Count);
            Assert.DoesNotContain(entries, e => e == null);
            // Idempotent: reading twice must not compact twice into a shorter list.
            Assert.Equal(20, accumulator.Entries.Count);
        }

        [Fact]
        public void Repeated_Upserts_Of_One_Guid_Stay_Bounded()
        {
            var accumulator = new LiteEntryAccumulator();
            for (int i = 0; i < 5000; i++) accumulator.Upsert(Entry(Guid(7), "Rev" + i));

            Assert.Equal(1, accumulator.Entries.Count);
            Assert.Equal("Rev4999", accumulator.Entries[0].Name);
            Assert.True(accumulator.Comparisons <= 5000 + 16, "replacement must not rescan the list");
        }

        [Fact]
        public void Null_Seed_And_Null_Upsert_Are_Tolerated()
        {
            var accumulator = new LiteEntryAccumulator(new List<SearchIndex.IndexEntry> { null, Entry(Guid(1), "O1") });
            accumulator.Upsert(null);
            Assert.Single(accumulator.Entries);
        }

        // ---- Issue #378: the duplicate-GUID fallback shifted the list under a stale map.

        /// <summary>
        /// The pre-#337 implementation this accumulator must stay bit-identical to:
        /// remove every entry with the GUID, then append.
        /// </summary>
        private static List<SearchIndex.IndexEntry> Reference(List<SearchIndex.IndexEntry> seed,
            IEnumerable<SearchIndex.IndexEntry> upserts)
        {
            var list = seed.Where(e => e != null).ToList();
            foreach (var entry in upserts) ReferenceStep(list, entry);
            return list;
        }

        private static void ReferenceStep(List<SearchIndex.IndexEntry> list, SearchIndex.IndexEntry entry)
        {
            if (entry == null) return;
            if (!string.IsNullOrEmpty(entry.Guid))
                list.RemoveAll(e => e != null && string.Equals(e.Guid, entry.Guid, StringComparison.OrdinalIgnoreCase));
            list.Add(entry);
        }

        private static void AssertMatchesReference(List<SearchIndex.IndexEntry> seed,
            IEnumerable<SearchIndex.IndexEntry> upserts, LiteEntryAccumulator accumulator)
        {
            var expected = Reference(seed, upserts).Select(e => e.Name).ToList();
            var actual = accumulator.Entries.Select(e => e.Name).ToList();
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void Upsert_After_A_Duplicate_Guid_Fallback_Reaches_Every_Remaining_Entry()
        {
            // Removing the two g1 positions shifts everything after them left by one.
            // The old map kept the pre-removal indices, so the next Upsert wrote a
            // tombstone at a stale slot - either throwing past the end or nulling an
            // unrelated entry while leaving D in place.
            var seed = new List<SearchIndex.IndexEntry>
            {
                Entry(Guid(1), "A"), Entry(Guid(2), "B"), Entry(Guid(1), "APrime"),
                Entry(Guid(3), "C"), Entry(Guid(4), "D")
            };
            var upserts = new List<SearchIndex.IndexEntry> { Entry(Guid(1), "ASecond"), Entry(Guid(4), "DPrime") };

            var accumulator = new LiteEntryAccumulator(seed);
            upserts.ForEach(accumulator.Upsert);

            AssertMatchesReference(seed, upserts, accumulator);
            Assert.Equal(new[] { "B", "C", "ASecond", "DPrime" }, accumulator.Entries.Select(e => e.Name).ToList());
        }

        [Fact]
        public void Upsert_After_A_Duplicate_Guid_Fallback_With_A_Trailing_Entry()
        {
            // With one more trailing entry the stale slot is in range, which is worse than
            // throwing: it silently tombstones the freshly appended replacement.
            var seed = new List<SearchIndex.IndexEntry>
            {
                Entry(Guid(1), "A"), Entry(Guid(2), "B"), Entry(Guid(1), "APrime"),
                Entry(Guid(3), "C"), Entry(Guid(4), "D"), Entry(Guid(5), "E")
            };
            var upserts = new List<SearchIndex.IndexEntry> { Entry(Guid(1), "ASecond"), Entry(Guid(4), "DPrime") };

            var accumulator = new LiteEntryAccumulator(seed);
            upserts.ForEach(accumulator.Upsert);

            AssertMatchesReference(seed, upserts, accumulator);
            Assert.Equal(new[] { "B", "C", "E", "ASecond", "DPrime" }, accumulator.Entries.Select(e => e.Name).ToList());
        }

        [Fact]
        public void Upsert_After_Compaction_Reaches_Every_Remaining_Entry()
        {
            // Compact() removed tombstones with RemoveAll and left the map pointing at
            // the pre-compaction indices. Reading Entries used to be the only call after
            // the walk, so nothing caught it; the accumulator has to be safe in any order.
            var accumulator = new LiteEntryAccumulator();
            for (int i = 1; i <= 4; i++) accumulator.Upsert(Entry(Guid(i), "O" + i));
            accumulator.Upsert(Entry(Guid(1), "O1Rev"));
            accumulator.Compact();

            accumulator.Upsert(Entry(Guid(4), "O4Rev"));

            Assert.Equal(new[] { "O2", "O3", "O1Rev", "O4Rev" }, accumulator.Entries.Select(e => e.Name).ToList());
            Assert.True(accumulator.SlotMapIsConsistent);
        }

        [Fact]
        public void Randomized_Seeds_With_Duplicates_Match_The_RemoveAll_Reference()
        {
            var random = new Random(20261003);
            for (int trial = 0; trial < 300; trial++)
            {
                int seedLength = random.Next(0, 8);
                int guidSpace = Math.Max(1, seedLength / 2 + 1);
                var seed = new List<SearchIndex.IndexEntry>();
                for (int i = 0; i < seedLength; i++)
                    seed.Add(Entry(Guid(random.Next(guidSpace)), "S" + i));

                int upsertCount = random.Next(1, 12);
                var upserts = new List<SearchIndex.IndexEntry>();
                for (int i = 0; i < upsertCount; i++)
                    upserts.Add(Entry(Guid(random.Next(guidSpace)), "U" + i));

                var accumulator = new LiteEntryAccumulator(seed);
                var reference = seed.Where(e => e != null).ToList();
                for (int i = 0; i < upserts.Count; i++)
                {
                    accumulator.Upsert(upserts[i]);
                    ReferenceStep(reference, upserts[i]);
                    // Compact at arbitrary points: the reference keeps no tombstones,
                    // Entries does the same, so the two must agree at every read.
                    if (random.Next(3) == 0) accumulator.Compact();

                    Assert.True(accumulator.SlotMapIsConsistent,
                        $"seed/map inconsistent at trial {trial}, upsert {i}");
                    Assert.Equal(reference.Select(e => e.Name).ToList(),
                        accumulator.Entries.Select(e => e.Name).ToList());
                }
            }
        }

        [Fact]
        public void Duplicate_Seed_Fallback_Stays_Occasional_And_The_Unique_Fast_Path_Is_Untouched()
        {
            // The rare path now reindexes, so it is O(n); the common path must still cost
            // a constant per entry.
            var seed = new List<SearchIndex.IndexEntry>();
            for (int i = 0; i < 200; i++) seed.Add(Entry(Guid(i % 100), "S" + i));
            var accumulator = new LiteEntryAccumulator(seed);

            accumulator.Upsert(Entry(Guid(0), "Replacement"));

            Assert.Equal(1, accumulator.FullScanFallbacks);
            // 200 seeded entries holding 100 duplicated GUIDs; replacing one of them
            // removes both of its positions and appends one.
            Assert.Equal(199, accumulator.Entries.Count);
            Assert.Single(accumulator.Entries, e => e.Guid == Guid(0) && e.Name == "Replacement");
            Assert.True(accumulator.SlotMapIsConsistent);
        }
    }
}
