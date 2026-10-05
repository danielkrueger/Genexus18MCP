using System;
using System.Collections.Generic;
using System.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>Result of <see cref="ImpactAnalyzer.Analyze"/>: sets, counts and order only; no risk judgement.</summary>
    internal sealed class ImpactResult
    {
        internal readonly List<(string Name, int Depth)> Nodes = new List<(string, int)>();
        internal readonly SortedDictionary<string, int> CountsByType = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        internal readonly List<string> Order = new List<string>();
        internal readonly List<string> CycleNodes = new List<string>();
        internal bool HasCycle => CycleNodes.Count > 0;
        internal bool TruncatedByDepth;
        internal bool TruncatedByNodes;
        internal bool Truncated => TruncatedByDepth || TruncatedByNodes;
    }

    /// <summary>
    /// Affected set of an object over the caller/callee adjacency (#423): a breadth-first walk
    /// bounded by depth and node count, the topological order to rebuild it (an object after
    /// everything it calls), and the cycles that make a strict order impossible.
    /// </summary>
    internal static class ImpactAnalyzer
    {
        internal const string Up = "up";
        internal const string Down = "down";
        internal const string Both = "both";

        internal static ImpactResult Analyze(string root, string direction, int maxDepth, int maxNodes,
            Func<string, IReadOnlyList<string>> callers, Func<string, IReadOnlyList<string>> callees,
            Func<string, string> typeOf, string typeFilter = null)
        {
            var result = new ImpactResult();
            var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [root] = 0 };
            var queue = new Queue<string>();
            queue.Enqueue(root);

            IEnumerable<string> Neighbours(string node)
            {
                var all = new List<string>();
                if (direction != Down) all.AddRange(callers(node) ?? new string[0]);
                if (direction != Up) all.AddRange(callees(node) ?? new string[0]);
                return all.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
            }

            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                int currentDepth = depth[current];
                foreach (string next in Neighbours(current))
                {
                    if (depth.ContainsKey(next)) continue;
                    if (currentDepth >= maxDepth) { result.TruncatedByDepth = true; continue; }
                    if (depth.Count - 1 >= maxNodes) { result.TruncatedByNodes = true; continue; }
                    depth[next] = currentDepth + 1;
                    queue.Enqueue(next);
                }
            }

            var affected = depth.Keys.ToList();
            SortTopologically(affected, callees, result);

            bool Keep(string name) => string.IsNullOrWhiteSpace(typeFilter)
                || string.Equals(typeOf(name), typeFilter, StringComparison.OrdinalIgnoreCase);
            foreach (string name in affected.Where(n => !string.Equals(n, root, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => depth[n]).ThenBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                if (!Keep(name)) continue;
                result.Nodes.Add((name, depth[name]));
                string type = typeOf(name) ?? "Unknown";
                result.CountsByType[type] = result.CountsByType.TryGetValue(type, out int count) ? count + 1 : 1;
            }
            if (!string.IsNullOrWhiteSpace(typeFilter)) result.Order.RemoveAll(n => !Keep(n) && !string.Equals(n, root, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        // Kahn over "calls" edges inside the affected set, dependencies first. Whatever cannot be
        // ordered sits on, or behind, a cycle; the cycle members themselves come from the strongly
        // connected components, and the rest of the leftovers are appended by name.
        private static void SortTopologically(List<string> nodes, Func<string, IReadOnlyList<string>> callees, ImpactResult result)
        {
            var set = new HashSet<string>(nodes, StringComparer.OrdinalIgnoreCase);
            var calls = nodes.ToDictionary(n => n,
                n => (callees(n) ?? new string[0]).Where(c => set.Contains(c) && !string.Equals(c, n, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
            var remaining = nodes.ToDictionary(n => n, n => calls[n].Count, StringComparer.OrdinalIgnoreCase);
            var callersOf = nodes.ToDictionary(n => n, n => new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var pair in calls) foreach (string callee in pair.Value) callersOf[callee].Add(pair.Key);

            var ready = new SortedSet<string>(nodes.Where(n => remaining[n] == 0), StringComparer.OrdinalIgnoreCase);
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (ready.Count > 0)
            {
                string next = ready.Min;
                ready.Remove(next);
                result.Order.Add(next);
                emitted.Add(next);
                foreach (string caller in callersOf[next])
                    if (--remaining[caller] == 0) ready.Add(caller);
            }

            var left = nodes.Where(n => !emitted.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (left.Count == 0) return;
            result.CycleNodes.AddRange(CycleMembers(left, calls));
            result.Order.AddRange(left);
        }

        // Tarjan, restricted to the nodes Kahn could not place.
        private static IEnumerable<string> CycleMembers(List<string> left, Dictionary<string, List<string>> calls)
        {
            var scope = new HashSet<string>(left, StringComparer.OrdinalIgnoreCase);
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var low = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();
            var members = new List<string>();
            int counter = 0;

            void Visit(string node)
            {
                index[node] = low[node] = counter++;
                stack.Push(node);
                onStack.Add(node);
                foreach (string next in calls[node].Where(scope.Contains))
                {
                    if (!index.ContainsKey(next)) { Visit(next); low[node] = Math.Min(low[node], low[next]); }
                    else if (onStack.Contains(next)) low[node] = Math.Min(low[node], index[next]);
                }
                if (low[node] != index[node]) return;
                var component = new List<string>();
                string popped;
                do { popped = stack.Pop(); onStack.Remove(popped); component.Add(popped); }
                while (!string.Equals(popped, node, StringComparison.OrdinalIgnoreCase));
                if (component.Count > 1) members.AddRange(component);
            }

            foreach (string node in left) if (!index.ContainsKey(node)) Visit(node);
            return members.OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        }
    }
}
