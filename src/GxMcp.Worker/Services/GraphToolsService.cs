using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>One index entry among several that match a bare name (#424).</summary>
    internal static class IndexEntryResolver
    {
        private const string RootModulePrefix = "Root Module/";

        /// <summary>
        /// guid / entityKey / path select exactly one entry and never fall back to a homonym;
        /// a bare name that matches several entries is ambiguous.
        /// </summary>
        internal static SearchIndex.IndexEntry Resolve(SearchIndex index, string name, string type, string guid, string entityKey,
            string path, out List<SearchIndex.IndexEntry> candidates)
        {
            candidates = new List<SearchIndex.IndexEntry>();
            if (index == null) return null;
            if (!string.IsNullOrWhiteSpace(guid)) return index.FindByGuid(guid);
            if (!string.IsNullOrWhiteSpace(entityKey)) return index.FindByEntityKey(entityKey);
            if (!string.IsNullOrWhiteSpace(path))
            {
                string wanted = StripRootModule(path.Trim().Replace('\\', '/'));
                string leaf = wanted.Substring(wanted.LastIndexOfAny(new[] { '/', '.' }) + 1);
                return index.FindByName(leaf).FirstOrDefault(e =>
                    string.Equals(StripRootModule((e.Path ?? string.Empty).Replace('\\', '/')), wanted, StringComparison.OrdinalIgnoreCase));
            }
            if (string.IsNullOrWhiteSpace(name)) return null;
            candidates = index.FindByName(name).Where(e => string.IsNullOrWhiteSpace(type)
                || string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase)).ToList();
            return candidates.Count == 1 ? candidates[0] : null;
        }

        private static string StripRootModule(string path)
            => path.StartsWith(RootModulePrefix, StringComparison.OrdinalIgnoreCase) ? path.Substring(RootModulePrefix.Length) : path;

        internal static JArray Describe(IEnumerable<SearchIndex.IndexEntry> entries)
            => new JArray(entries.Select(e => new JObject { ["name"] = e.Name, ["type"] = e.Type, ["guid"] = e.Guid, ["path"] = e.Path }));
    }

    /// <summary>genexus_dfd, genexus_impact and genexus_object_context: read-only views composed from the existing engines.</summary>
    public class GraphToolsService
    {
        private const int DefaultDfdDepth = 3;
        private const int DefaultDfdTables = 100;

        private readonly KbService _kb;
        private readonly ObjectService _objects;
        private readonly IndexCacheService _indexCache;
        private readonly CallerGraphService _graph;

        public GraphToolsService(KbService kb, ObjectService objects, IndexCacheService indexCache, CallerGraphService graph)
        {
            _kb = kb;
            _objects = objects;
            _indexCache = indexCache;
            _graph = graph;
        }

        // ---- genexus_dfd -------------------------------------------------------------------

        public string Dfd(JObject args)
        {
            string name = args?["name"]?.ToString() ?? args?["target"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
                return McpResponse.Err("BadArgs", "genexus_dfd requires a Transaction or Table name.", "Pass name=<Transaction>.");
            int maxDepth = Clamp(args["maxDepth"], DefaultDfdDepth, 1, 6);
            JObject graph = BuildDfd(name, maxDepth, DefaultDfdTables, out string error);
            return error ?? McpResponse.Ok(code: "DfdRetrieved", target: name, result: graph);
        }

        private JObject BuildDfd(string name, int maxDepth, int maxTables, out string error)
        {
            error = null;
            if (!KbModelGuard.TryGetDesignModel(_kb, out _, out string kbError)) { error = kbError; return null; }
            KBObject obj;
            try { obj = _objects.FindObject(name, null); } catch { obj = null; }
            Table root = obj as Table;
            if (root == null && obj is Transaction trn)
                try { root = trn.Structure?.Root?.AssociatedTable; } catch { }
            if (root == null)
            {
                error = obj == null
                    ? McpResponse.Err("ObjectNotFound", "'" + name + "' not found.", "Check the name (genexus_query).", target: name,
                        nextSteps: new JArray(McpResponse.NextStep("genexus_query", new JObject { ["query"] = name }, "Find the object by name.")))
                    : McpResponse.Err("NoAssociatedTable", "'" + name + "' has no associated table (not yet normalized / built).",
                        "Build the KB first (genexus_lifecycle action=build).", target: name);
                return null;
            }

            var tables = new Dictionary<string, Table>(StringComparer.OrdinalIgnoreCase) { [root.Name] = root };
            IEnumerable<DfdEdge> RelationsOf(string tableName)
            {
                if (!tables.TryGetValue(tableName, out Table table)) yield break;
                foreach (var relation in Relations(table, "SuperordinatedTables"))
                {
                    tables[relation.RelatedName] = relation.Related;
                    yield return new DfdEdge { From = table.Name, To = relation.RelatedName, Kind = "extends", JoinOn = relation.Join };
                }
                foreach (var relation in Relations(table, "SubordinatedTables"))
                {
                    tables[relation.RelatedName] = relation.Related;
                    yield return new DfdEdge { From = relation.RelatedName, To = table.Name, Kind = "subordinates", JoinOn = relation.Join };
                }
            }
            return DfdGraph.Build(root.Name, maxDepth, maxTables, RelationsOf);
        }

        private sealed class SdkRelation { internal Table Related; internal string RelatedName; internal List<string> Join; }

        private static IEnumerable<SdkRelation> Relations(Table table, string property)
        {
            IEnumerable raw = null;
            try { raw = table.GetType().GetProperty(property)?.GetValue(table, null) as IEnumerable; } catch { }
            if (raw == null) yield break;
            foreach (object relation in raw)
            {
                Table related = null;
                try { related = relation.GetType().GetProperty("RelatedTable")?.GetValue(relation, null) as Table; } catch { }
                if (related == null) continue;
                var join = new List<string>();
                try
                {
                    if (relation.GetType().GetProperty("Attributes")?.GetValue(relation, null) is IEnumerable attributes)
                        foreach (object attribute in attributes)
                        {
                            string attributeName = attribute?.GetType().GetProperty("Name")?.GetValue(attribute, null)?.ToString();
                            if (!string.IsNullOrEmpty(attributeName)) join.Add(attributeName);
                        }
                }
                catch { }
                yield return new SdkRelation { Related = related, RelatedName = related.Name, Join = join };
            }
        }

        // ---- genexus_impact ----------------------------------------------------------------

        public string Impact(JObject args)
        {
            var index = _indexCache?.GetIndex();
            if (index == null) return McpResponse.Err("IndexNotReady", "The search index is not available yet.", "Retry after genexus_lifecycle action=index completes.");

            var entry = Resolve(index, args, out string error);
            if (entry == null) return error;

            string direction = (args["direction"]?.ToString() ?? ImpactAnalyzer.Up).Trim().ToLowerInvariant();
            if (direction != ImpactAnalyzer.Up && direction != ImpactAnalyzer.Down && direction != ImpactAnalyzer.Both)
                return McpResponse.Err("InvalidDirection", "direction must be up, down or both.", target: entry.Name);
            int maxDepth = Clamp(args["maxDepth"], 2, 1, 6);
            int maxNodes = Clamp(args["maxNodes"], 200, 1, 2000);

            JObject result = RunImpact(index, entry, direction, maxDepth, maxNodes, args["typeFilter"]?.ToString());
            return McpResponse.Ok(code: "ImpactComputed", target: entry.Name, result: result);
        }

        private JObject RunImpact(SearchIndex index, SearchIndex.IndexEntry entry, string direction, int maxDepth, int maxNodes, string typeFilter)
        {
            // The adjacency comes from enrichment: promote the root so its own edges are read.
            if (!entry.IsEnriched)
                try { _indexCache.GetEnrichmentQueue()?.PromoteAsync(entry).GetAwaiter().GetResult(); } catch { }

            string TypeOf(string n) => index.FindByName(n).FirstOrDefault()?.Type;
            ImpactResult impact = ImpactAnalyzer.Analyze(entry.Name, direction, maxDepth, maxNodes,
                n => _graph.GetCallers(n), n => _graph.GetCallees(n), TypeOf, typeFilter);

            int enriched = 0, unenriched = 0;
            foreach (string name in impact.Order)
            {
                bool done = index.FindByName(name).FirstOrDefault()?.IsEnriched == true;
                if (done) enriched++; else unenriched++;
            }
            int total = enriched + unenriched;
            string trust = unenriched == 0 ? "complete" : "partial:" + (total == 0 ? 0 : 100 * enriched / total);

            var truncatedBy = new JArray();
            if (impact.TruncatedByDepth) truncatedBy.Add("maxDepth");
            if (impact.TruncatedByNodes) truncatedBy.Add("maxNodes");
            return new JObject
            {
                ["root"] = new JObject { ["name"] = entry.Name, ["type"] = entry.Type, ["guid"] = entry.Guid, ["path"] = entry.Path },
                ["direction"] = direction,
                ["affected"] = new JArray(impact.Nodes.Select(n => new JObject
                {
                    ["name"] = n.Name, ["type"] = TypeOf(n.Name), ["depth"] = n.Depth
                })),
                ["countsByType"] = JObject.FromObject(impact.CountsByType),
                ["order"] = new JArray(impact.Order),
                ["hasCycle"] = impact.HasCycle,
                ["cycleNodes"] = new JArray(impact.CycleNodes),
                ["stats"] = new JObject
                {
                    ["affectedCount"] = impact.Nodes.Count,
                    ["maxDepth"] = maxDepth,
                    ["maxNodes"] = maxNodes,
                    ["truncated"] = impact.Truncated,
                    ["truncatedBy"] = truncatedBy
                },
                ["fieldTrust"] = trust,
                ["coverageNote"] = unenriched == 0 ? null
                    : "Callers/callees are filled by index enrichment; " + unenriched + " of " + total
                        + " objects were not enriched, so an empty or short set is partial coverage, not a confirmed zero."
            };
        }

        // ---- genexus_object_context --------------------------------------------------------

        public string ObjectContext(JObject args)
        {
            var index = _indexCache?.GetIndex();
            if (index == null) return McpResponse.Err("IndexNotReady", "The search index is not available yet.", "Retry after genexus_lifecycle action=index completes.");
            var entry = Resolve(index, args, out string error);
            if (entry == null) return error;

            int maxDepth = Clamp(args["maxDepth"], 1, 1, 6);
            int maxNodes = Clamp(args["maxNodes"], 100, 1, 2000);
            JObject up = RunImpact(index, entry, ImpactAnalyzer.Up, maxDepth, maxNodes, null);
            JObject down = RunImpact(index, entry, ImpactAnalyzer.Down, maxDepth, maxNodes, null);

            JObject Side(JObject side)
            {
                bool partial = !string.Equals(side["fieldTrust"]?.ToString(), "complete", StringComparison.Ordinal);
                var names = side["affected"].Select(a => (string)a["name"]).ToList();
                return new JObject
                {
                    ["names"] = new JArray(names),
                    ["fieldTrust"] = side["fieldTrust"],
                    // Under partial coverage an empty list was not read; it is not a zero.
                    ["read"] = !(partial && names.Count == 0),
                    ["truncated"] = side["stats"]["truncated"]
                };
            }

            var suppressed = new JArray();
            JToken dataModel = DataModel(entry, suppressed);

            var result = new JObject
            {
                ["object"] = new JObject
                {
                    ["name"] = entry.Name, ["type"] = entry.Type, ["guid"] = entry.Guid, ["entityKey"] = entry.EntityKey,
                    ["path"] = entry.Path, ["module"] = entry.Module, ["description"] = entry.Description,
                    ["lastUpdate"] = entry.LastUpdate == DateTime.MinValue ? null : (JToken)entry.LastUpdate.ToString("o")
                },
                ["references"] = new JObject { ["callers"] = Side(up), ["callees"] = Side(down) },
                ["suppressed"] = suppressed
            };
            if (dataModel != null) result["dataModel"] = dataModel;
            return McpResponse.Ok(code: "ObjectContextRetrieved", target: entry.Name, result: result);
        }

        private JToken DataModel(SearchIndex.IndexEntry entry, JArray suppressed)
        {
            bool hasTable = string.Equals(entry.Type, "Transaction", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Type, "Table", StringComparison.OrdinalIgnoreCase);
            if (hasTable)
            {
                JObject graph = BuildDfd(entry.Name, 1, DefaultDfdTables, out string error);
                if (graph != null) return graph;
                suppressed.Add(new JObject { ["field"] = "dataModel", ["reason"] = "relations unavailable", ["unlockedBy"] = "genexus_dfd name=" + entry.Name });
                return null;
            }
            if (entry.Tables != null && entry.Tables.Count > 0) return new JObject { ["tables"] = new JArray(entry.Tables), ["source"] = "index" };
            suppressed.Add(new JObject { ["field"] = "dataModel", ["reason"] = "no table information in the index for this object", ["unlockedBy"] = "genexus_dfd name=<Transaction>" });
            return null;
        }

        // ---- shared ------------------------------------------------------------------------

        private static SearchIndex.IndexEntry Resolve(SearchIndex index, JObject args, out string error)
        {
            error = null;
            string name = args?["name"]?.ToString() ?? args?["target"]?.ToString();
            string guid = args?["guid"]?.ToString();
            string entityKey = args?["entityKey"]?.ToString();
            string path = args?["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(guid) && string.IsNullOrWhiteSpace(entityKey) && string.IsNullOrWhiteSpace(path))
            {
                error = McpResponse.Err("MissingTarget", "Pass name, guid, entityKey or path.");
                return null;
            }
            var entry = IndexEntryResolver.Resolve(index, name, args?["type"]?.ToString(), guid, entityKey, path, out var candidates);
            if (entry != null) return entry;
            error = candidates.Count > 1
                ? McpResponse.Err("AmbiguousName", "'" + name + "' matches " + candidates.Count + " objects; select one with guid or path.",
                    target: name, extra: new JObject { ["candidates"] = IndexEntryResolver.Describe(candidates) })
                : McpResponse.Err("ObjectNotFound", "Object not found in the index.", "Re-run after genexus_lifecycle action=index completes.", target: name ?? guid ?? path ?? entityKey,
                    nextSteps: new JArray(McpResponse.NextStep("genexus_query", new JObject { ["query"] = name ?? path ?? guid }, "Find the object by name.")));
            return null;
        }

        private static int Clamp(JToken token, int fallback, int min, int max)
        {
            int value = token != null && token.Type == JTokenType.Integer ? token.Value<int>() : fallback;
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
