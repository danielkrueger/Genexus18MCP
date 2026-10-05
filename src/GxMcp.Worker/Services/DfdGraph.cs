using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>One relation reported by the SDK between two tables, oriented from -> to.</summary>
    internal sealed class DfdEdge
    {
        internal string From;
        internal string To;
        /// <summary>"extends" (from's extended table includes to) or "subordinates" (from is subordinated to to).</summary>
        internal string Kind;
        internal List<string> JoinOn = new List<string>();
    }

    /// <summary>
    /// Entity-relationship view of a table (#422), built only from the relations the SDK reports:
    /// no relation is inferred, and the diagram text is generated from those edges alone.
    /// </summary>
    internal static class DfdGraph
    {
        internal const string Schema = "genexus-dfd/1";

        internal static JObject Build(string root, int maxDepth, int maxTables, Func<string, IEnumerable<DfdEdge>> relationsOf)
        {
            var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [root] = 0 };
            var order = new List<string> { root };
            var edges = new List<DfdEdge>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue(root);
            bool byDepth = false, byTables = false;

            while (queue.Count > 0)
            {
                string table = queue.Dequeue();
                foreach (DfdEdge edge in relationsOf(table))
                {
                    string other = string.Equals(edge.From, table, StringComparison.OrdinalIgnoreCase) ? edge.To : edge.From;
                    bool known = depth.ContainsKey(other);
                    if (!known)
                    {
                        if (depth[table] >= maxDepth) { byDepth = true; continue; }
                        if (order.Count >= maxTables) { byTables = true; continue; }
                        depth[other] = depth[table] + 1;
                        order.Add(other);
                        queue.Enqueue(other);
                    }
                    if (seen.Add(edge.From + "\u0001" + edge.To + "\u0001" + edge.Kind)) edges.Add(edge);
                }
            }

            var truncatedBy = new JArray();
            if (byDepth) truncatedBy.Add("maxDepth");
            if (byTables) truncatedBy.Add("maxTables");
            return new JObject
            {
                ["schema"] = Schema,
                ["root"] = root,
                ["tables"] = new JArray(order),
                ["edges"] = new JArray(edges.Select(e => new JObject
                {
                    ["from"] = e.From, ["to"] = e.To, ["kind"] = e.Kind, ["joinOn"] = string.Join(", ", e.JoinOn)
                })),
                ["mermaid"] = Mermaid(order, edges),
                ["stats"] = new JObject
                {
                    ["tableCount"] = order.Count,
                    ["edgeCount"] = edges.Count,
                    ["truncated"] = byDepth || byTables,
                    ["truncatedBy"] = truncatedBy
                }
            };
        }

        // Many-to-one from the table that holds the key to the one it points at, for both kinds:
        // an extended table is the one pointed at; a subordinated table points at its superordinate.
        internal static string Mermaid(IList<string> tables, IList<DfdEdge> edges)
        {
            var text = new StringBuilder("erDiagram\n");
            var linked = new HashSet<string>(edges.SelectMany(e => new[] { e.From, e.To }), StringComparer.OrdinalIgnoreCase);
            foreach (string table in tables.Where(t => !linked.Contains(t)))
                text.Append("  ").Append(Id(table)).Append(" {\n  }\n");
            foreach (DfdEdge edge in edges)
                text.Append("  ").Append(Id(edge.From)).Append(" }o--|| ").Append(Id(edge.To))
                    .Append(" : \"").Append(string.Join(", ", edge.JoinOn).Replace("\"", "'")).Append("\"\n");
            return text.ToString();
        }

        private static string Id(string name) => Regex.Replace(name ?? string.Empty, @"[^A-Za-z0-9_]", "_");
    }
}
