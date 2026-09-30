using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using GxMcp.Worker.Services;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// <c>GetCallersTransitive</c> and <c>GetCalleesTransitive</c> now share one
    /// BFS, differing only in which neighbour set they expand. The traversal
    /// semantics both inherited are pinned here: the node cap and its Truncated
    /// flag, the Depth reported on a truncated walk, the cycle guard, the
    /// cancellation short-circuit, and the fact that a name is included once.
    ///
    /// Both walks feed recompile decisions, so a walk that silently reported a
    /// different node set or a different depth than its twin is the kind of drift
    /// nobody sees until a build is wrong.
    /// </summary>
    public class CallerGraphTransitiveTests
    {
        /// <summary>
        /// An empty index: nothing is indexed, so every neighbour lookup is empty
        /// and each walk returns immediately. That is enough to pin the
        /// short-circuits, which is where the two copies used to be able to
        /// disagree with each other.
        /// </summary>
        private static CallerGraphService EmptyGraph() => new CallerGraphService(new IndexCacheService());

        [Fact]
        public void EmptyRoot_ReturnsAnEmptyResult()
        {
            var result = EmptyGraph().GetCallersTransitive("", 10, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Empty(result.Nodes);
            Assert.False(result.Truncated);
        }

        [Fact]
        public void NonPositiveCap_ReturnsAnEmptyResultRatherThanTheRoot()
        {
            foreach (int cap in new[] { 0, -1 })
            {
                var callers = EmptyGraph().GetCallersTransitive("Root", cap, CancellationToken.None);
                Assert.Empty(callers.Nodes);
                Assert.False(callers.Truncated);

                var callees = EmptyGraph().GetCalleesTransitive("Root", cap, CancellationToken.None);
                Assert.Empty(callees.Nodes);
                Assert.False(callees.Truncated);
            }
        }

        [Fact]
        public void AlreadyCancelled_ReportsTruncatedWithNoNodes()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var callers = EmptyGraph().GetCallersTransitive("Root", 10, cts.Token);
            Assert.True(callers.Truncated);
            Assert.Empty(callers.Nodes);

            var callees = EmptyGraph().GetCalleesTransitive("Root", 10, cts.Token);
            Assert.True(callees.Truncated);
            Assert.Empty(callees.Nodes);
        }

        [Fact]
        public void BothWalks_ShareTheSameSignatureAndTruncationContract()
        {
            // The two entry points differ only in the edge direction they expand.
            // Anything else about them — parameter shape, cap handling, the flags
            // they set — must not differ, or an impact analysis and a dependency
            // listing would report different things about the same object.
            var callersOverloads = typeof(CallerGraphService)
                .GetMethods()
                .Where(m => m.Name == "GetCallersTransitive")
                .Select(m => string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)))
                .OrderBy(s => s)
                .ToList();
            var calleesOverloads = typeof(CallerGraphService)
                .GetMethods()
                .Where(m => m.Name == "GetCalleesTransitive")
                .Select(m => string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)))
                .OrderBy(s => s)
                .ToList();

            Assert.Equal(callersOverloads, calleesOverloads);
            Assert.Contains("String,Int32,CancellationToken", callersOverloads);
        }

        [Fact]
        public void TheTwoWalks_ShareOneTraversal()
        {
            // Source-level guard: the traversal body must not be duplicated back
            // into either entry point. The BFS, the cap and the cycle guard are one
            // contract, and a second copy is how they drift.
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "CallerGraphService.cs");

            // The truncated-depth line, verbatim. A mutation that reported d instead
            // of d + 1 would still read like this traversal, which is why the
            // assertion is on the exact expression rather than on "d" appearing.
            Assert.Equal(1, CountOccurrences(src, "queue.Enqueue((neighbour, d + 1));"));
            Assert.Equal(1, CountOccurrences(src, "result.Depth = Math.Max(maxDepth, d + 1);"));
            Assert.Equal(1, CountOccurrences(src, "ProgressEmitter.Emit("));
            Assert.Equal(1, CountOccurrences(src, "if (!visited.Add(neighbour)) continue;"));
            Assert.Contains("=> WalkTransitive(root, maxNodes, ct, GetCallers);", src);
            Assert.Contains("=> WalkTransitive(root, maxNodes, ct, GetCallees);", src);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        [Fact]
        public void TheTruncatedDepth_IsTheChildDepthNotTheParent()
        {
            // Behavioural guard on the one line a "harmless looking" edit would
            // change. When the cap trips while expanding a node at depth d, the
            // deepest node reached is at depth d + 1 — the neighbour just added.
            // Reporting d understates the blast radius of a truncated walk by one
            // level, and a caller that sizes a recompile from Depth would then
            // miss that level.
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "CallerGraphService.cs");

            int guard = src.IndexOf(
                "result.Truncated = true;\n                        result.Depth = Math.Max(maxDepth, d + 1);",
                StringComparison.Ordinal);
            Assert.True(guard > 0,
                "the truncated-depth assignment must stay 'd + 1'; found instead:\n" +
                ExtractTruncatedDepth(src));
        }

        private static string ExtractTruncatedDepth(string src)
        {
            int at = src.IndexOf("result.Truncated = true;", StringComparison.Ordinal);
            if (at < 0) return "(no truncation branch found)";
            int end = src.IndexOf(';', src.IndexOf("result.Depth", at, StringComparison.Ordinal));
            return end < 0 ? src.Substring(at, 200) : src.Substring(at, end - at + 1);
        }

    }
}
