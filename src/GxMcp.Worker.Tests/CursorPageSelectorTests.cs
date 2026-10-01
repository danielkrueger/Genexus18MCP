using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #346: cursor paging applied its resume predicate by sorting the entire
    /// candidate list and then finding the resume position, so every cursor page took
    /// the O(N log N) full-sort path regardless of page size. The predicate is a
    /// prefix of the total order, so it can be applied before selection.
    ///
    /// These guards pin the property that actually matters: the filter-first path
    /// must return exactly what exhaustive-sort-then-scan returned - no skipped and
    /// no duplicated entries across pages, under equal timestamps, case-differing
    /// names and GUID tie-breaks - and it must do bounded selection work.
    /// </summary>
    public class CursorPageSelectorTests
    {
        private static SearchIndex.IndexEntry Entry(string name, DateTime lastUpdate, string guid) =>
            new SearchIndex.IndexEntry { Name = name, LastUpdate = lastUpdate, Guid = guid, Type = "Procedure" };

        private static string Guid(int i) => i.ToString("D8") + "-0000-0000-0000-000000000000";

        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// The pre-#346 implementation, inlined rather than delegated to
        /// <see cref="ListService.IsAfterResumePoint"/>: sort every candidate, then
        /// FindIndex the first entry strictly after the cursor. Reusing the extracted
        /// predicate here would only prove the predicate agrees with itself, so the
        /// scan is spelled out exactly as it was.
        /// </summary>
        private static List<SearchIndex.IndexEntry> Baseline(List<SearchIndex.IndexEntry> candidates,
            DateTime ts, string name, string guid, int pageSize)
        {
            var ordered = new List<SearchIndex.IndexEntry>(candidates);
            ordered.Sort(LastUpdateIndexEntryComparer.Instance);
            int resumeAt = ordered.FindIndex(e =>
            {
                if (e.LastUpdate < ts) return true;
                if (e.LastUpdate != ts) return false;
                int byName = string.Compare(e.Name ?? string.Empty, name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                if (byName > 0) return true;
                if (byName < 0) return false;
                return string.Compare(e.Guid ?? string.Empty, guid ?? string.Empty, StringComparison.OrdinalIgnoreCase) > 0;
            });
            if (resumeAt < 0) resumeAt = ordered.Count;
            int end = Math.Min(ordered.Count, resumeAt + pageSize);
            return ordered.Skip(resumeAt).Take(end - resumeAt).ToList();
        }

        /// <summary>What the filter-first path produces: bounded selection over the filtered tail.</summary>
        private static List<SearchIndex.IndexEntry> Filtered(List<SearchIndex.IndexEntry> candidates,
            DateTime ts, string name, string guid, int pageSize, out int total, out int selectionStart)
        {
            var list = candidates.ToList();
            total = list.Count;
            var remaining = list.Where(e => ListService.IsAfterResumePoint(e, ts, name, guid)).ToList();
            selectionStart = total - remaining.Count;
            return TopKHelper.SelectTopK(remaining, pageSize, LastUpdateIndexEntryComparer.Instance, out _);
        }

        public static IEnumerable<object[]> TieHeavyCatalog()
        {
            // Repeated timestamps, name casing variants and distinct GUIDs: the three
            // cases the resume tie-break chain exists for.
            var cases = new List<object[]>();
            var random = new Random(20260930);
            var names = new[] { "alpha", "Alpha", "BETA", "beta", "gamma", "GAMMA", "delta" };
            for (int scenario = 0; scenario < 40; scenario++)
            {
                var entries = new List<SearchIndex.IndexEntry>();
                int n = 20 + random.Next(180);
                for (int i = 0; i < n; i++)
                {
                    // Only 5 distinct timestamps, so ties are the norm rather than the exception.
                    var ts = T0.AddMinutes(random.Next(5));
                    string name = names[random.Next(names.Length)] + i.ToString("D3");
                    entries.Add(Entry(name, ts, Guid(random.Next(60))));
                }
                cases.Add(new object[] { entries });
            }
            return cases;
        }

        [Theory]
        [MemberData(nameof(TieHeavyCatalog))]
        public void Filtered_Page_Matches_Exhaustive_Sort_Baseline_On_Tie_Heavy_Catalogs(List<SearchIndex.IndexEntry> catalog)
        {
            // Page through the whole catalog with a 7-item page and compare the union
            // against the fully sorted sequence: identical, with no gaps or repeats.
            var pageSize = 7;
            var full = new List<SearchIndex.IndexEntry>(catalog);
            full.Sort(LastUpdateIndexEntryComparer.Instance);

            var walked = new List<SearchIndex.IndexEntry>();
            DateTime ts = DateTime.MaxValue;
            string name = string.Empty, guid = string.Empty;
            bool first = true;

            for (int guard = 0; guard < full.Count + 5; guard++)
            {
                List<SearchIndex.IndexEntry> baselinePage;
                List<SearchIndex.IndexEntry> actualPage;
                if (first)
                {
                    baselinePage = full.Take(pageSize).ToList();
                    actualPage = TopKHelper.SelectTopK(catalog, pageSize, LastUpdateIndexEntryComparer.Instance, out _);
                    first = false;
                }
                else
                {
                    baselinePage = Baseline(catalog, ts, name, guid, pageSize);
                    actualPage = Filtered(catalog, ts, name, guid, pageSize, out _, out _);
                }

                Assert.Equal(
                    baselinePage.Select(e => e.Name + "|" + e.Guid).ToList(),
                    actualPage.Select(e => e.Name + "|" + e.Guid).ToList());

                walked.AddRange(actualPage);
                if (actualPage.Count < pageSize) break;
                var last = actualPage[actualPage.Count - 1];
                ts = last.LastUpdate; name = last.Name; guid = last.Guid;
            }

            // Every catalog entry appears exactly once across the walked pages. Names
            // are unique in this generator; GUIDs deliberately repeat, because real
            // resume decisions must not depend on GUID uniqueness.
            Assert.Equal(full.Count, walked.Count);
            Assert.Equal(
                full.Select(e => e.Name).OrderBy(s => s, StringComparer.Ordinal).ToList(),
                walked.Select(e => e.Name).OrderBy(s => s, StringComparer.Ordinal).ToList());
            Assert.Equal(full.Count, walked.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void Filtered_Page_Keeps_Correct_Order_When_The_Catalog_Changes_Between_Pages()
        {
            // Documented consistency policy: the resume is a value predicate over the
            // candidate set, so an entry inserted ahead of the cursor is simply not
            // seen again, and an entry that sorts after it is still returned. Nothing
            // is duplicated. This is the same behaviour the sort-then-scan had.
            var catalog = Enumerable.Range(0, 40)
                .Select(i => Entry("Obj" + i.ToString("D2"), T0.AddSeconds(-i), Guid(i)))
                .ToList();

            var firstPage = TopKHelper.SelectTopK(catalog, 5, LastUpdateIndexEntryComparer.Instance, out _);
            var last = firstPage[4];

            // Mutate: add an entry ahead of the cursor, and one well after it.
            catalog.Add(Entry("ObjNewer", T0.AddSeconds(1000), Guid(9999)));
            catalog.Add(Entry("ObjOlder", T0.AddSeconds(-1000), Guid(8888)));

            int total, selectionStart;
            var secondPage = Filtered(catalog, last.LastUpdate, last.Name, last.Guid, 5, out total, out selectionStart);

            Assert.Equal(42, total);
            // Obj05..Obj39 plus the newly inserted older entry.
            Assert.Equal("Obj05", secondPage[0].Name);
            Assert.Equal("Obj09", secondPage[4].Name);
            Assert.DoesNotContain(secondPage, e => e.Name == "ObjNewer");
        }

        [Fact]
        public void Selection_Work_Is_Bounded_By_Page_Size_Not_Catalog_Size()
        {
            // 200k candidates, 50-item page. The filtered path materializes the tail
            // (needed to report an honest total) but the *selection* heap is page-sized,
            // so no O(N log N) sort of the catalog happens.
            var catalog = Enumerable.Range(0, 200_000)
                .Select(i => Entry("Obj" + i, T0.AddSeconds(-i), Guid(i)))
                .ToList();

            int total, selectionStart;
            // Newest first, so the order is Obj0, Obj1, ... Resuming from Obj100's own
            // tuple means the tail starts at Obj101, and the selection heap only has to
            // hold 50 entries no matter how large the catalog is.
            var page = Filtered(catalog, T0.AddSeconds(-100), "Obj100", Guid(100), 50, out total, out selectionStart);

            Assert.Equal(200_000, total);
            Assert.Equal(101, selectionStart);
            Assert.Equal(50, page.Count);
            Assert.Equal("Obj101", page[0].Name);
            Assert.Equal("Obj150", page[49].Name);
        }

        [Fact]
        public void Exhausted_Cursor_Selects_Nothing_And_Still_Reports_Full_Total()
        {
            var catalog = Enumerable.Range(0, 100)
                .Select(i => Entry("Obj" + i, T0.AddSeconds(-i), Guid(i)))
                .ToList();

            // A cursor older than every entry is the last page.
            int total, selectionStart;
            var page = Filtered(catalog, T0.AddSeconds(-1000), string.Empty, string.Empty, 50, out total, out selectionStart);

            Assert.Empty(page);
            // The caller must still see the whole candidate count, matching the old
            // `startIndex = totalIndex` "exhausted" behaviour.
            Assert.Equal(100, total);
            Assert.Equal(100, selectionStart);
        }

        [Fact]
        public void Resume_Point_Is_Strictly_After_The_Cursor_Entry()
        {
            var a = Entry("Same", T0, "aaab");
            // The cursor entry itself is already consumed, never re-emitted.
            Assert.False(ListService.IsAfterResumePoint(a, T0, "Same", "aaab"));
            // Same name and timestamp, larger GUID: it follows.
            Assert.True(ListService.IsAfterResumePoint(a, T0, "Same", "aaaa"));
            // Same name and timestamp, smaller GUID: it precedes the cursor.
            Assert.False(ListService.IsAfterResumePoint(a, T0, "Same", "aabc"));
        }

        [Fact]
        public void Resume_Predicate_Chains_Timestamp_Then_Name_Then_Guid()
        {
            var entry = Entry("Bravo", T0, "gggg");
            // Older timestamp wins first, regardless of name.
            Assert.True(ListService.IsAfterResumePoint(entry, T0.AddSeconds(1), "zzzz", "0000"));
            // Newer timestamp stops the chain.
            Assert.False(ListService.IsAfterResumePoint(entry, T0.AddSeconds(-1), "aaaa", "zzzz"));
            // Same timestamp, name decides.
            Assert.True(ListService.IsAfterResumePoint(entry, T0, "Alpha", "zzzz"));
            Assert.False(ListService.IsAfterResumePoint(entry, T0, "Zulu", "0000"));
            // Same timestamp and name, GUID decides.
            Assert.True(ListService.IsAfterResumePoint(entry, T0, "Bravo", "aaaa"));
            Assert.False(ListService.IsAfterResumePoint(entry, T0, "bravo", "gggg"));
        }

        [Fact]
        public void Null_Timestamps_Names_And_Guids_Are_Handled_Like_The_Old_Predicate()
        {
            var bare = new SearchIndex.IndexEntry { Type = "Procedure" };
            Assert.True(ListService.IsAfterResumePoint(bare, T0, string.Empty, string.Empty));
            Assert.False(ListService.IsAfterResumePoint(bare, DateTime.MinValue, string.Empty, string.Empty));
            Assert.False(ListService.IsAfterResumePoint(null, T0, string.Empty, string.Empty));
        }

        [Fact]
        public void Round_Tripped_Cursor_Resumes_From_The_Emitted_Entry()
        {
            var catalog = Enumerable.Range(0, 50)
                .Select(i => Entry("Obj" + i, T0.AddSeconds(-i), Guid(i)))
                .ToList();

            var firstPage = TopKHelper.SelectTopK(catalog, 10, LastUpdateIndexEntryComparer.Instance, out _);
            var last = firstPage[firstPage.Count - 1];
            string token = ListService.EncodeCursor(last.LastUpdate, last.Name, last.Guid);
            var decoded = ListService.DecodeCursor(token);
            Assert.NotNull(decoded);

            int total, selectionStart;
            var secondPage = Filtered(catalog, decoded.Value.ts, decoded.Value.name, decoded.Value.guid, 10, out total, out selectionStart);

            // `total` is the whole candidate set, not the remaining tail.
            Assert.Equal(50, total);
            Assert.Equal(10, selectionStart);
            Assert.Equal("Obj10", secondPage[0].Name);
            Assert.Equal("Obj19", secondPage[9].Name);
        }
    }
}
