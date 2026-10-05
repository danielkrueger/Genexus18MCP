using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issues #422 (dfd), #423 (impact) and #424 (object_context): the pure cores.
    public class GraphToolsTests
    {
        // ---- impact -----------------------------------------------------------------------

        private static IReadOnlyList<string> Of(Dictionary<string, string[]> map, string key)
            => map.TryGetValue(key, out var v) ? v : new string[0];

        // Post calls Validate and Save; Save calls Log; Validate calls Log. Main calls Post.
        private static readonly Dictionary<string, string[]> Calls = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Main"] = new[] { "Post" }, ["Post"] = new[] { "Validate", "Save" }, ["Save"] = new[] { "Log" }, ["Validate"] = new[] { "Log" }
        };

        private static Dictionary<string, string[]> Inverse(Dictionary<string, string[]> calls)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in calls) foreach (string callee in pair.Value)
                { if (!result.TryGetValue(callee, out var list)) result[callee] = list = new List<string>(); list.Add(pair.Key); }
            return result.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        }

        private static ImpactResult Run(Dictionary<string, string[]> calls, string root, string direction, int depth = 6, int nodes = 100, string filter = null)
        {
            var callers = Inverse(calls);
            return ImpactAnalyzer.Analyze(root, direction, depth, nodes, n => Of(callers, n), n => Of(calls, n),
                n => n.StartsWith("Log") ? "Procedure" : n == "Main" ? "WebPanel" : "Procedure", filter);
        }

        [Fact]
        public void OrderIsATopologicalSortWithDependenciesFirst()
        {
            ImpactResult result = Run(Calls, "Log", ImpactAnalyzer.Up);
            Assert.False(result.HasCycle);
            foreach (var pair in Calls)
            {
                int caller = result.Order.IndexOf(pair.Key);
                foreach (string callee in pair.Value)
                    if (caller >= 0 && result.Order.IndexOf(callee) >= 0)
                        Assert.True(result.Order.IndexOf(callee) < caller, callee + " must precede " + pair.Key);
            }
            Assert.Equal(new[] { "Log", "Post", "Save", "Validate", "Main" }.OrderBy(x => x), result.Order.OrderBy(x => x));
            Assert.Equal("Log", result.Order.First());
            Assert.Equal("Main", result.Order.Last());
        }

        [Fact]
        public void CycleIsReportedWithItsNodesAndDoesNotLoop()
        {
            var cyclic = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["A"] = new[] { "B" }, ["B"] = new[] { "C" }, ["C"] = new[] { "A" }, ["Top"] = new[] { "A" }
            };
            ImpactResult result = Run(cyclic, "A", ImpactAnalyzer.Both);
            Assert.True(result.HasCycle);
            Assert.Equal(new[] { "A", "B", "C" }, result.CycleNodes);
            Assert.Equal(4, result.Order.Count);
        }

        [Fact]
        public void TruncationNamesExactlyTheCapThatWasHit()
        {
            Assert.True(Run(Calls, "Log", ImpactAnalyzer.Up, depth: 1).TruncatedByDepth);
            Assert.False(Run(Calls, "Log", ImpactAnalyzer.Up, depth: 1).TruncatedByNodes);
            ImpactResult byNodes = Run(Calls, "Log", ImpactAnalyzer.Up, depth: 6, nodes: 1);
            Assert.True(byNodes.TruncatedByNodes);
            Assert.False(byNodes.TruncatedByDepth);
            Assert.False(Run(Calls, "Log", ImpactAnalyzer.Up).Truncated);
        }

        [Fact]
        public void TypeFilterNarrowsTheResultWithoutChangingTheTraversal()
        {
            ImpactResult all = Run(Calls, "Log", ImpactAnalyzer.Up);
            ImpactResult webPanels = Run(Calls, "Log", ImpactAnalyzer.Up, filter: "WebPanel");
            Assert.Equal(new[] { "Main" }, webPanels.Nodes.Select(n => n.Name));
            Assert.Equal(4, all.Nodes.Count);
            Assert.Equal(1, webPanels.CountsByType["WebPanel"]);
            // Main is two hops above Post: the walk still crossed the filtered-out nodes to reach it.
            Assert.Equal(3, webPanels.Nodes.Single().Depth);
        }

        [Fact]
        public void DownDirectionWalksCallees()
        {
            ImpactResult down = Run(Calls, "Main", ImpactAnalyzer.Down);
            Assert.Equal(new[] { "Log", "Post", "Save", "Validate" }, down.Nodes.Select(n => n.Name).OrderBy(n => n));
        }

        // ---- dfd --------------------------------------------------------------------------

        private static IEnumerable<DfdEdge> Relations(string table)
        {
            switch (table)
            {
                case "Order":
                    yield return new DfdEdge { From = "Order", To = "Customer", Kind = "extends", JoinOn = { "CustomerId" } };
                    yield return new DfdEdge { From = "Order", To = "Currency", Kind = "extends", JoinOn = { "CurrencyId" } };
                    yield return new DfdEdge { From = "OrderLine", To = "Order", Kind = "subordinates", JoinOn = { "OrderId" } };
                    break;
                case "Customer":
                    yield return new DfdEdge { From = "Order", To = "Customer", Kind = "extends", JoinOn = { "CustomerId" } };
                    yield return new DfdEdge { From = "Customer", To = "Country", Kind = "extends", JoinOn = { "CountryId" } };
                    break;
                case "OrderLine":
                    yield return new DfdEdge { From = "OrderLine", To = "Order", Kind = "subordinates", JoinOn = { "OrderId" } };
                    break;
            }
        }

        [Fact]
        public void EdgesAndTablesAreExactlyTheReportedRelations()
        {
            JObject graph = DfdGraph.Build("Order", 1, 100, Relations);
            Assert.Equal("genexus-dfd/1", (string)graph["schema"]);
            Assert.Equal(new[] { "Order", "Customer", "Currency", "OrderLine" }, graph["tables"]!.Select(t => (string)t!));
            Assert.Equal(new[] { "Order>Customer:extends:CustomerId", "Order>Currency:extends:CurrencyId", "OrderLine>Order:subordinates:OrderId" },
                graph["edges"]!.Select(e => $"{e["from"]}>{e["to"]}:{e["kind"]}:{e["joinOn"]}"));
            Assert.Equal(3, (int)graph["stats"]!["edgeCount"]!);
        }

        [Fact]
        public void MermaidIsGeneratedFromTheEdgesOnly()
        {
            string text = (string)DfdGraph.Build("Order", 1, 100, Relations)["mermaid"]!;
            Assert.StartsWith("erDiagram\n", text);
            Assert.Contains("  Order }o--|| Customer : \"CustomerId\"\n", text);
            Assert.Contains("  OrderLine }o--|| Order : \"OrderId\"\n", text);
            Assert.DoesNotContain("Country", text);
            Assert.Equal(3, text.Split('\n').Count(l => l.Contains("}o--||")));
        }

        [Fact]
        public void TruncationIsReportedWithItsCause()
        {
            JObject byDepth = DfdGraph.Build("Order", 1, 100, Relations);
            Assert.True((bool)byDepth["stats"]!["truncated"]!);   // Customer -> Country is beyond depth 1
            Assert.Equal(new[] { "maxDepth" }, byDepth["stats"]!["truncatedBy"]!.Select(t => (string)t!));
            JObject full = DfdGraph.Build("Order", 3, 100, Relations);
            Assert.False((bool)full["stats"]!["truncated"]!);
            Assert.Contains("Country", full["tables"]!.Select(t => (string)t!));
            JObject byTables = DfdGraph.Build("Order", 3, 2, Relations);
            Assert.Contains("maxTables", byTables["stats"]!["truncatedBy"]!.Select(t => (string)t!));
        }

        [Fact]
        public void IsolatedTableStillGetsAnEntity()
        {
            string text = (string)DfdGraph.Build("Lonely", 3, 10, _ => new DfdEdge[0])["mermaid"]!;
            Assert.Equal("erDiagram\n  Lonely {\n  }\n", text);
        }

        // ---- object_context identity ------------------------------------------------------

        private static SearchIndex IndexWithHomonyms()
        {
            var index = new SearchIndex { Objects = new System.Collections.Concurrent.ConcurrentDictionary<string, SearchIndex.IndexEntry>(StringComparer.OrdinalIgnoreCase) };
            void Add(string type, string name, string guid, string path) =>
                index.Objects[type + ":" + name + ":" + guid] = new SearchIndex.IndexEntry { Type = type, Name = name, Guid = guid, Path = path };
            Add("Table", "Order", "g-table", "Root Module/Order");
            Add("Transaction", "Order", "g-trn", "Root Module/Order");
            Add("Folder", "Order", "g-folder", "Root Module/Sales/Order");
            return index;
        }

        [Fact]
        public void HomonymousNameIsAmbiguousAndGuidOrPathSelectsOne()
        {
            SearchIndex index = IndexWithHomonyms();
            Assert.Null(IndexEntryResolver.Resolve(index, "Order", null, null, null, null, out var candidates));
            Assert.Equal(3, candidates.Count);
            Assert.Equal("g-trn", IndexEntryResolver.Resolve(index, null, null, "g-trn", null, null, out _).Guid);
            Assert.Equal("g-folder", IndexEntryResolver.Resolve(index, null, null, null, null, "Sales/Order", out _).Guid);
            Assert.Equal("g-folder", IndexEntryResolver.Resolve(index, null, null, null, null, "Root Module/Sales/Order", out _).Guid);
            Assert.Equal("g-trn", IndexEntryResolver.Resolve(index, "Order", "Transaction", null, null, null, out _).Guid);
        }

        [Fact]
        public void APathNeverFallsBackToAHomonym()
        {
            SearchIndex index = IndexWithHomonyms();
            Assert.Null(IndexEntryResolver.Resolve(index, "Order", null, null, null, "Other/Order", out _));
            Assert.Null(IndexEntryResolver.Resolve(index, null, null, "missing", null, null, out _));
        }
    }
}
