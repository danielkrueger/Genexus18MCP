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
    }
}
