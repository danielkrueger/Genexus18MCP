using System;
using System.Collections.Generic;
using System.IO;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The prologue every print-block mutation (rename, add, delete) shares:
    /// resolve the target, load the report surface, require a report part,
    /// require an open KB, snapshot the Procedure source.
    ///
    /// The order is the contract, not the individual steps. The snapshot has to be
    /// taken after the KB is confirmed open and before anything is written, since
    /// it is what the rollback path restores — a copy that dropped or reordered a
    /// step would still compile and still look right, and would fail as a report
    /// that could not be put back the way it was. These pin the produced guard
    /// envelopes and the fact that all three mutations route through the one
    /// prologue.
    /// </summary>
    public class ReportMutationPrologueTests
    {
        [Fact]
        public void ReportPartNotFound_NamesTheSurfaceThatDiagnosesIt()
        {
            var json = JObject.Parse(LayoutService.ReportPartNotFound("MyReport"));

            Assert.Equal("ReportPartNotFound", json["error"]?["code"]?.ToString());
            Assert.Equal("MyReport", json["target"]?.ToString());

            var steps = (JArray)json["error"]?["nextSteps"];
            Assert.NotNull(steps);
            Assert.Single(steps);
            Assert.Equal("genexus_layout", steps[0]?["tool"]?.ToString());
            Assert.Equal("inspect_surface", steps[0]?["args"]?["action"]?.ToString());
        }

        [Fact]
        public void ReportLayoutKbNotOpened_IsTransientAndNamedPerCaller()
        {
            // KbNotOpened is a curated transient code, so retryAfterMs is part of
            // the published contract, not a detail.
            var json = JObject.Parse(LayoutService.ReportLayoutKbNotOpened(
                "MyReport", "Open a Knowledge Base before mutating the report layout."));

            Assert.Equal("KbNotOpened", json["error"]?["code"]?.ToString());
            Assert.Equal(2000, json["error"]?["retryAfterMs"]?.ToObject<int>());

            var steps = (JArray)json["error"]?["nextSteps"];
            Assert.Single(steps);
            Assert.Equal("genexus_kb", steps[0]?["tool"]?.ToString());
            Assert.Equal("open", steps[0]?["args"]?["action"]?.ToString());
        }

        [Theory]
        [InlineData("RenamePrintBlock")]
        [InlineData("AddPrintBlock")]
        [InlineData("DeletePrintBlock")]
        public void EveryPrintBlockMutation_GoesThroughTheSharedPrologue(string method)
        {
            // Source-level guard: the five-step prologue must exist once. Three
            // copies is three chances for the snapshot step to drift out from under
            // the rollback path.
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "LayoutService.cs");

            Assert.Equal(3, CountOccurrences(src, "var setup = BeginReportMutation(target);"));

            // The guard is counted as the pair, not on its own: `BeginVisualRead`
            // uses the identical line `if (setup.Error != null) return
            // setup.Error;`, so counting that line alone no longer says which
            // prologue it belongs to. Matching it immediately after its own call
            // does.
            Assert.Equal(3, CountMatches(src,
                @"var setup = BeginReportMutation\(target\);\s*if \(setup\.Error != null\) return setup\.Error;"));

            // The steps themselves live only in BeginReportMutation.
            Assert.Equal(1, CountOccurrences(src, "setup.SourceSnapshot = GetProcedureSourceSnapshot(obj);"));
            Assert.Equal(1, CountOccurrences(src, "setup.Error = ReportPartNotFound(target);"));
            Assert.Equal(1, CountOccurrences(src, "private ReportMutationSetup BeginReportMutation(string target)"));
            Assert.Contains("public string " + method, src);
        }

        [Fact]
        public void TheSnapshotIsTakenAfterTheKbCheckAndBeforeAnythingIsWritten()
        {
            // The order is the whole point of the extraction, so it is asserted as
            // positions in the source rather than left to inspection.
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "LayoutService.cs");

            int begin = src.IndexOf("private ReportMutationSetup BeginReportMutation", StringComparison.Ordinal);
            Assert.True(begin > 0, "BeginReportMutation not found");

            int resolve = src.IndexOf("setup.Error = VisualObjectNotFound(target);", begin, StringComparison.Ordinal);
            int loadSurface = src.IndexOf("var context = LoadVisualContext(obj, target, VisualSurface.Report);", begin, StringComparison.Ordinal);
            int requirePart = src.IndexOf("setup.Error = ReportPartNotFound(target);", begin, StringComparison.Ordinal);
            int requireKb = src.IndexOf("setup.Error = ReportLayoutKbNotOpened(target,", begin, StringComparison.Ordinal);
            int snapshot = src.IndexOf("setup.SourceSnapshot = GetProcedureSourceSnapshot(obj);", begin, StringComparison.Ordinal);

            Assert.True(resolve > 0, "target resolution missing from the prologue");
            Assert.True(loadSurface > resolve, "the report surface must load after the target resolves");
            Assert.True(requirePart > loadSurface, "the report-part guard must follow the surface load");
            Assert.True(requireKb > requirePart, "the KB guard must follow the report-part guard");

            // Ordering the snapshot against a *guard* is not a matter of text
            // position: the guard is an early return, so a snapshot moved above it
            // still reads as being "after" it. The assertion that actually holds is
            // that the snapshot sits in the success path — past the `return setup;`
            // that closes the KB guard.
            int kbGuardReturn = src.IndexOf("return setup;", requireKb, StringComparison.Ordinal);
            Assert.True(kbGuardReturn > requireKb, "the KB guard must return early when no KB is open");
            Assert.True(snapshot > kbGuardReturn,
                "the source snapshot must be taken after the KB is confirmed open, " +
                "because it is what the rollback path restores");
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

        /// <summary>
        /// Counts regex matches, for guards whose subject spans lines and must not
        /// depend on this file's line endings.
        /// </summary>
        private static int CountMatches(string haystack, string pattern)
        {
            return System.Text.RegularExpressions.Regex.Matches(haystack, pattern).Count;
        }

    }
}
