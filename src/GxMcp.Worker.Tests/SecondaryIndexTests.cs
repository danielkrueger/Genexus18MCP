using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Plan 002: derived Type/BusinessDomain indexes so SearchService/ListService can
    // intersect a candidate set instead of scanning index.Objects.Values when a
    // type/domain filter narrows the query. These tests pin two things: (1) the
    // indexes themselves are populated correctly by the same mutation hooks that
    // maintain ChildrenByParent, and (2) the indexed prefilter path (built via
    // AddOrUpdateBatch, which builds TypeIndex/DomainIndex) returns EXACTLY the same
    // result set as the full-scan fallback path (built via LoadFromEntries, which
    // leaves TypeIndex/DomainIndex null) — i.e. this is a perf change, not a
    // semantics change.
    public class SecondaryIndexTests
    {
        private static List<SearchIndex.IndexEntry> SampleEntries()
        {
            return new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry { Name = "Proc1", Type = "Procedure", BusinessDomain = "Sales", Description = "handles orders", Guid = "g1" },
                new SearchIndex.IndexEntry { Name = "Proc2", Type = "Procedure", BusinessDomain = "HR", Description = "handles employees", Guid = "g2" },
                new SearchIndex.IndexEntry { Name = "Trn1", Type = "Transaction", BusinessDomain = "Sales", Description = "order transaction", Guid = "g3" },
                new SearchIndex.IndexEntry { Name = "Wp1", Type = "WebPanel", BusinessDomain = "Sales", Description = "dashboard", Guid = "g4" },
                new SearchIndex.IndexEntry { Name = "Attr1", Type = "Attribute", BusinessDomain = "HR", Description = "employee id", Guid = "g5" },
            };
        }

        // ── Step 1: the indexes themselves ──────────────────────────────────────

        [Fact]
        public void TypeIndex_And_DomainIndex_ContainExpectedKeys_AfterInsert()
        {
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(SampleEntries());

            var idx = svc.TryGetLoadedIndex();
            Assert.NotNull(idx.TypeIndex);
            Assert.NotNull(idx.DomainIndex);

            Assert.Equal(new[] { "Procedure:Proc1", "Procedure:Proc2" },
                idx.TypeIndex["Procedure"].OrderBy(k => k));
            Assert.Equal(new[] { "Transaction:Trn1" }, idx.TypeIndex["Transaction"]);
            Assert.Equal(new[] { "WebPanel:Wp1" }, idx.TypeIndex["WebPanel"]);
            Assert.Equal(new[] { "Attribute:Attr1" }, idx.TypeIndex["Attribute"]);

            Assert.Equal(new[] { "Procedure:Proc1", "Transaction:Trn1", "WebPanel:Wp1" },
                idx.DomainIndex["Sales"].OrderBy(k => k));
            Assert.Equal(new[] { "Attribute:Attr1", "Procedure:Proc2" },
                idx.DomainIndex["HR"].OrderBy(k => k));
        }

        [Fact]
        public void RemoveEntryByGuid_DropsKeyFromTypeAndDomainIndex()
        {
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(SampleEntries());

            svc.RemoveEntryByGuid("g1"); // Proc1 (Procedure/Sales)

            var idx = svc.TryGetLoadedIndex();
            Assert.DoesNotContain("Procedure:Proc1", idx.TypeIndex["Procedure"]);
            Assert.DoesNotContain("Procedure:Proc1", idx.DomainIndex["Sales"]);
            // sibling of the same type/domain must survive
            Assert.Contains("Transaction:Trn1", idx.DomainIndex["Sales"]);
        }

        [Fact]
        public void RemoveEntry_ByTypeAndName_DropsKeyFromTypeAndDomainIndex()
        {
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(SampleEntries());

            svc.RemoveEntry("Procedure", "Proc2");

            var idx = svc.TryGetLoadedIndex();
            Assert.DoesNotContain("Procedure:Proc2", idx.TypeIndex["Procedure"]);
            Assert.DoesNotContain("Procedure:Proc2", idx.DomainIndex["HR"]);
        }

        [Fact]
        public void TypeIndex_NotSerialized_RebuiltFromObjectsOnly()
        {
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(SampleEntries());
            var idx = svc.TryGetLoadedIndex();

            string json = idx.ToJson();
            Assert.DoesNotContain("TypeIndex", json);
            Assert.DoesNotContain("DomainIndex", json);

            var reloaded = SearchIndex.FromJson(json);
            Assert.Null(reloaded.TypeIndex); // derived, not persisted — rebuilt on load via BuildParentIndex
        }

        [Fact]
        public void PromoteSourceForSearch_PopulatesFullSourceAndSourceTokens()
        {
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(new[]
            {
                new SearchIndex.IndexEntry { Name = "Proc1", Type = "Procedure", Guid = "g1" }
            });
            svc.EnsureSourceTokenIndex();
            var entry = svc.TryGetLoadedIndex().Objects["Procedure:Proc1"];
            string source = "parm(in:&CustomerId); ProcessInvoicePayment();";

            Assert.True(svc.PromoteSourceForSearch(entry, source));
            Assert.Equal(source, entry.FullSource);
            Assert.Contains("Procedure:Proc1", svc.TryGetLoadedIndex().SourceTokenIndex["processinvoicepayment"]);
            Assert.False(svc.PromoteSourceForSearch(entry, "different source"));

            var reloaded = SearchIndex.FromJson(svc.TryGetLoadedIndex().ToJson());
            Assert.Equal(source, reloaded.Objects["Procedure:Proc1"].FullSource);
        }

        [Fact]
        public void PromoteSourceForSearch_RecordsConfirmedEmptySource()
        {
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(new[]
            {
                new SearchIndex.IndexEntry { Name = "EmptyProc", Type = "Procedure", Guid = "empty-guid" }
            });
            svc.EnsureSourceTokenIndex();
            var entry = svc.TryGetLoadedIndex().Objects["Procedure:EmptyProc"];

            Assert.True(svc.PromoteSourceForSearch(entry, string.Empty));
            Assert.NotNull(entry.FullSource);
            Assert.Empty(entry.FullSource);
            Assert.DoesNotContain("Procedure:EmptyProc", svc.TryGetLoadedIndex().SourceTokenIndex.Values.SelectMany(keys => keys));

            var reloaded = SearchIndex.FromJson(svc.TryGetLoadedIndex().ToJson());
            Assert.NotNull(reloaded.Objects["Procedure:EmptyProc"].FullSource);
            Assert.Empty(reloaded.Objects["Procedure:EmptyProc"].FullSource);
        }

        [Fact]
        public void PromoteSourceForSearch_AllowsBoundedLargeSource()
        {
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(new[]
            {
                new SearchIndex.IndexEntry { Name = "Proc1", Type = "Procedure", Guid = "g1" }
            });
            var entry = svc.TryGetLoadedIndex().Objects["Procedure:Proc1"];

            string source = new string('x', 256 * 1024 + 1);
            Assert.True(svc.PromoteSourceForSearch(entry, source));
            Assert.Equal(source, entry.FullSource);
        }

        [Fact]
        public void PromoteSourceForSearch_RejectsSourceOverPerEntryBound()
        {
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(new[]
            {
                new SearchIndex.IndexEntry { Name = "Proc1", Type = "Procedure", Guid = "g1" }
            });
            var entry = svc.TryGetLoadedIndex().Objects["Procedure:Proc1"];

            Assert.False(svc.PromoteSourceForSearch(entry, new string('x', 2 * 1024 * 1024 + 1)));
            Assert.Null(entry.FullSource);
        }

        [Fact]
        public void PromoteSourceForSearch_RejectsSourceWhenAggregateBudgetIsFull()
        {
            var entries = Enumerable.Range(0, 5)
                .Select(i => new SearchIndex.IndexEntry
                {
                    Name = "Proc" + i,
                    Type = "Procedure",
                    Guid = "g" + i,
                    FullSource = i < 4 ? new string('x', 2 * 1024 * 1024) : null
                })
                .ToArray();
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(entries);
            var entry = svc.TryGetLoadedIndex().Objects["Procedure:Proc4"];

            Assert.False(svc.PromoteSourceForSearch(entry, "new source"));
            Assert.Null(entry.FullSource);
        }

        // ── Issue #364: the aggregate budget used to re-sum every indexed source ──

        /// <summary>
        /// An index of <paramref name="n"/> entries, none promoted, with one ready for it.
        /// Promotion is refused for entries that already carry a body, so the entries
        /// under test are promoted one at a time.
        /// </summary>
        private static IndexCacheService BuildIndexOf(int n)
        {
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(Enumerable.Range(0, n).Select(i => new SearchIndex.IndexEntry
            {
                Name = "SyntheticObject" + i.ToString("D6"),
                Type = "Procedure",
                Guid = "g" + i.ToString("D6")
            }));
            return svc;
        }

        [Fact]
        public void Promotion_Cost_Does_Not_Grow_With_The_Index_Size()
        {
            // The defect: the budget check summed FullSource.Length over every indexed
            // object on every promotion, so total visits were quadratic.
            long VisitsFor(int n, int promotions)
            {
                var svc = BuildIndexOf(n);
                var index = svc.TryGetLoadedIndex();
                long before = svc.FullSourceBudgetVisits;
                for (int i = 0; i < promotions; i++)
                    svc.PromoteSourceForSearch(index.Objects["Procedure:SyntheticObject" + i.ToString("D6")],
                        "// synthetic source " + i);
                return svc.FullSourceBudgetVisits - before;
            }

            long small = VisitsFor(1000, 200);
            long large = VisitsFor(4000, 200);

            Assert.Equal(0, small);
            // Doubling the index must not change what a promotion costs.
            Assert.Equal(small, large);
        }

        /// <summary>
        /// The authoritative guard for #364.
        ///
        /// <para>
        /// <c>FullSourceBudgetVisits</c> counts entries walked by
        /// <c>ReconcileFullSourceChars</c>, so it cannot observe a full-index sum written
        /// directly into the budget check - which is exactly the shape the defect had. The
        /// behavioural guard above would pass against the original code, so this asserts
        /// the shape instead: the check reads the tracked total, and no per-call sum over
        /// the index remains.
        /// </para>
        /// </summary>
        [Fact]
        public void The_Promotion_Budget_Reads_The_Tracked_Total_Instead_Of_Summing_The_Index()
        {
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "IndexCacheService.cs");

            int start = source.IndexOf("internal bool PromoteSourceForSearch", StringComparison.Ordinal);
            Assert.True(start >= 0, "PromoteSourceForSearch not found");
            int end = source.IndexOf("public void UpdateEntry", start, StringComparison.Ordinal);
            Assert.True(end > start, "could not delimit PromoteSourceForSearch");
            string body = source.Substring(start, end - start);

            Assert.Contains("FullSourceChars", body, StringComparison.Ordinal);
            Assert.DoesNotContain("index.Objects.Values", body, StringComparison.Ordinal);
            Assert.DoesNotContain("+= candidate.FullSource.Length", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// The full-index sum is allowed in exactly one place: the reconcile that runs when
        /// the index object itself is swapped, which is already O(N).
        /// </summary>
        [Fact]
        public void The_Full_Index_Char_Sum_Lives_In_Exactly_One_Place()
        {
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "IndexCacheService.cs");

            int sums = 0;
            int i = 0;
            while ((i = source.IndexOf("candidate.FullSource.Length", i, StringComparison.Ordinal)) >= 0)
            {
                sums++;
                i += 5;
            }
            Assert.Equal(1, sums);
        }

        [Fact]
        public void The_Promoted_Character_Total_Matches_The_Index()
        {
            var svc = BuildIndexOf(500);
            var index = svc.TryGetLoadedIndex();
            for (int i = 0; i < 50; i++)
                svc.PromoteSourceForSearch(index.Objects["Procedure:SyntheticObject" + i.ToString("D6")],
                    new string('x', 1000 + i));

            long expected = Enumerable.Range(0, 50).Sum(i => 1000 + i);
            Assert.Equal(expected, svc.FullSourceChars);
        }

        [Fact]
        public void A_Replaced_Entry_Moves_Its_Characters_RatherThan_Adding_Them()
        {
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(new[]
            {
                new SearchIndex.IndexEntry { Name = "Proc1", Type = "Procedure", Guid = "g1", FullSource = new string('a', 500) }
            });
            Assert.Equal(500, svc.FullSourceChars);

            svc.AddOrUpdateBatch(new[]
            {
                new SearchIndex.IndexEntry { Name = "Proc1", Type = "Procedure", Guid = "g1", FullSource = new string('b', 900) }
            });

            Assert.Equal(900, svc.FullSourceChars);
        }

        [Fact]
        public void The_Budget_Boundary_Still_Admits_One_Promotion_And_Refuses_The_Next()
        {
            // The counter must not change the semantics of the budget, only its cost.
            const long budget = 8L * 1024 * 1024;
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(Enumerable.Range(0, 5).Select(i => new SearchIndex.IndexEntry
            {
                Name = "Proc" + i,
                Type = "Procedure",
                Guid = "g" + i,
                // 2 Mi chars each: four exactly fill the 8 MiB budget.
                FullSource = i < 4 ? new string('x', 2 * 1024 * 1024) : null
            }));
            var index = svc.TryGetLoadedIndex();

            Assert.Equal(budget, svc.FullSourceChars);
            Assert.False(svc.PromoteSourceForSearch(index.Objects["Procedure:Proc4"], new string('x', 1)));
            Assert.Null(index.Objects["Procedure:Proc4"].FullSource);
            Assert.Equal(budget, svc.FullSourceChars);
        }

        [Fact]
        public void Concurrent_Promotions_Never_Overshoot_The_Budget()
        {
            // The check and the write now happen under the same lock as the entry, so two
            // promotions cannot both observe a total that still fits.
            var svc = BuildIndexOf(200);
            var index = svc.TryGetLoadedIndex();
            long budget = 8L * 1024 * 1024;
            var entries = Enumerable.Range(0, 200)
                .Select(i => index.Objects["Procedure:SyntheticObject" + i.ToString("D6")])
                .ToArray();
            int sourceLength = 64 * 1024;
            int promoted = 0;

            System.Threading.Tasks.Parallel.For(0, entries.Length, i =>
            {
                if (svc.PromoteSourceForSearch(entries[i], new string('x', sourceLength)))
                    System.Threading.Interlocked.Increment(ref promoted);
            });

            Assert.True(svc.FullSourceChars <= budget,
                $"promoted {svc.FullSourceChars} characters against a {budget} budget");
            Assert.Equal(promoted * sourceLength, svc.FullSourceChars);
        }

        // ── Step 3: indexed prefilter path vs full-scan fallback — same results ──

        private static IndexCacheService BuildIndexed()
        {
            var svc = new IndexCacheService();
            svc.AddOrUpdateBatch(SampleEntries()); // builds TypeIndex/DomainIndex -> prefilter path
            svc.MarkIndexComplete(SampleEntries().Count);
            return svc;
        }

        private static IndexCacheService BuildFullScan()
        {
            var svc = new IndexCacheService();
            svc.LoadFromEntries(SampleEntries()); // TypeIndex/DomainIndex stay null -> fallback path
            return svc;
        }

        private static string[] SearchNames(IndexCacheService cache, string query, string typeFilter, string domainFilter)
        {
            var svc = new SearchService(cache);
            var json = svc.Search(query, typeFilter, domainFilter, limit: 50);
            var results = (JArray)JObject.Parse(json)["results"];
            return results.Select(r => r["name"].ToString()).ToArray();
        }

        private static string[] ListNames(IndexCacheService cache, string typeFilter)
        {
            var svc = new ListService(cache);
            var json = svc.ListObjects(filter: null, limit: 50, offset: 0, typeFilter: typeFilter);
            var results = (JArray)JObject.Parse(json)["results"];
            return results.Select(r => r["name"].ToString()).ToArray();
        }

        [Fact]
        public void Search_TypeOnly_IndexedPathMatchesFullScan()
        {
            var indexed = SearchNames(BuildIndexed(), query: "", typeFilter: "Procedure", domainFilter: null);
            var fullScan = SearchNames(BuildFullScan(), query: "", typeFilter: "Procedure", domainFilter: null);
            Assert.Equal(fullScan, indexed);
            Assert.Equal(new[] { "Proc1", "Proc2" }, indexed.OrderBy(n => n));
        }

        [Fact]
        public void Search_DomainOnly_IndexedPathMatchesFullScan()
        {
            var indexed = SearchNames(BuildIndexed(), query: "", typeFilter: null, domainFilter: "Sales");
            var fullScan = SearchNames(BuildFullScan(), query: "", typeFilter: null, domainFilter: "Sales");
            Assert.Equal(fullScan, indexed);
            Assert.Equal(new[] { "Proc1", "Trn1", "Wp1" }, indexed.OrderBy(n => n));
        }

        [Fact]
        public void Search_TypeAndDomain_IndexedPathMatchesFullScan()
        {
            var indexed = SearchNames(BuildIndexed(), query: "", typeFilter: "Procedure", domainFilter: "Sales");
            var fullScan = SearchNames(BuildFullScan(), query: "", typeFilter: "Procedure", domainFilter: "Sales");
            Assert.Equal(fullScan, indexed);
            Assert.Equal(new[] { "Proc1" }, indexed);
        }

        [Fact]
        public void Search_TypeAndDescriptionSubstring_IndexedPathMatchesFullScan()
        {
            var indexed = SearchNames(BuildIndexed(), query: "description:handles", typeFilter: "Procedure", domainFilter: null);
            var fullScan = SearchNames(BuildFullScan(), query: "description:handles", typeFilter: "Procedure", domainFilter: null);
            Assert.Equal(fullScan, indexed);
            Assert.Equal(new[] { "Proc1", "Proc2" }, indexed.OrderBy(n => n));
        }

        [Fact]
        public void Search_TypeAlias_IndexedPathMatchesFullScan()
        {
            // IsTypeMatch is alias-aware ("prc" contains-matches "Procedure") — the
            // indexed path must resolve aliases against TypeIndex bucket keys, not
            // require an exact key match.
            var indexed = SearchNames(BuildIndexed(), query: "", typeFilter: "prc", domainFilter: null);
            var fullScan = SearchNames(BuildFullScan(), query: "", typeFilter: "prc", domainFilter: null);
            Assert.Equal(fullScan, indexed);
            Assert.Equal(new[] { "Proc1", "Proc2" }, indexed.OrderBy(n => n));
        }

        [Fact]
        public void List_TypeOnly_IndexedPathMatchesFullScan()
        {
            var indexed = ListNames(BuildIndexed(), typeFilter: "Procedure");
            var fullScan = ListNames(BuildFullScan(), typeFilter: "Procedure");
            Assert.Equal(fullScan, indexed);
            Assert.Equal(new[] { "Proc1", "Proc2" }, indexed.OrderBy(n => n));
        }

        [Fact]
        public void FindByGuid_IndexedAndFallback_ReturnExpectedEntry()
        {
            var indexed = BuildIndexed().TryGetLoadedIndex();
            var fullScan = BuildFullScan().TryGetLoadedIndex();

            var foundIndexed = indexed.FindByGuid("g1");
            var foundFullScan = fullScan.FindByGuid("g1");

            Assert.NotNull(foundIndexed);
            Assert.NotNull(foundFullScan);
            Assert.Equal("Proc1", foundIndexed.Name);
            Assert.Equal("Proc1", foundFullScan.Name);

            // Case insensitive lookup
            Assert.NotNull(indexed.FindByGuid("G1"));
            Assert.NotNull(fullScan.FindByGuid("G1"));

            // Non-existent guid returns null
            Assert.Null(indexed.FindByGuid("non-existent"));
            Assert.Null(fullScan.FindByGuid("non-existent"));
            Assert.Null(indexed.FindByGuid(null));
            Assert.Null(indexed.FindByGuid(""));
        }
    }
}
