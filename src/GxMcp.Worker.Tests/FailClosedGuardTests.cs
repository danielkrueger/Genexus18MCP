using System.IO;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Fail-closed guards: the points where a mutation refuses to proceed rather
    /// than stage a change it cannot save. Each was written out at every call site;
    /// they now live in one builder per guard, and these pin both the produced
    /// envelope and the fact that every call site still routes through it.
    /// </summary>
    public class FailClosedGuardTests
    {
        // ── WwpActionService: PatternInstance not re-resolvable under the lock ──

        [Fact]
        public void WwpInstanceNotResolvable_IsTheBareGuardWithNoRecoverySteps()
        {
            // The original carried code + message + target and deliberately nothing
            // else. Adding a hint or nextSteps here would be a contract change: the
            // caller is mid-mutation and the operator needs the stage, not a detour.
            var json = JObject.Parse(WwpActionService.BuildWwpInstanceNotResolvable("MyPanel"));

            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("MyPanel", json["target"]?.ToString());
            Assert.Equal("WWPInstanceNotFound", json["error"]?["code"]?.ToString());
            Assert.Equal("The WorkWithPlus PatternInstance could not be re-resolved before save.",
                json["error"]?["message"]?.ToString());
            Assert.Null(json["error"]?["hint"]);
            Assert.Null(json["error"]?["nextSteps"]);
        }

        [Fact]
        public void WwpInstanceNotResolvable_StaysDistinctFromTheMisroutedObjectEnvelope()
        {
            // BuildWwpInstanceNotFound is the other WWPInstanceNotFound: the object
            // resolved but is not a WorkWithPlus instance, so it must route the
            // caller to another tool. Collapsing the two would delete that routing.
            var routed = JObject.Parse(WwpActionService.BuildWwpInstanceNotFound(
                "Customer", "Customer", "Transaction", null));
            var guard = JObject.Parse(WwpActionService.BuildWwpInstanceNotResolvable("Customer"));

            Assert.Equal("WWPInstanceNotFound", routed["error"]?["code"]?.ToString());
            Assert.Equal("WWPInstanceNotFound", guard["error"]?["code"]?.ToString());
            Assert.NotEqual(routed["error"]?["message"]?.ToString(), guard["error"]?["message"]?.ToString());
            Assert.NotNull(routed["error"]?["nextSteps"]);
            Assert.Null(guard["error"]?["nextSteps"]);
        }

        // ── LayoutService: report mutation guards ──

        [Fact]
        public void ReportPartNotFound_PointsAtInspectSurface()
        {
            var json = JObject.Parse(LayoutService.ReportPartNotFound("MyReport"));

            Assert.Equal("MyReport", json["target"]?.ToString());
            Assert.Equal("ReportPartNotFound", json["error"]?["code"]?.ToString());
            Assert.Equal("Report part not found.", json["error"]?["message"]?.ToString());

            var steps = (JArray)json["error"]?["nextSteps"];
            Assert.NotNull(steps);
            Assert.Single(steps);
            Assert.Equal("genexus_layout", steps[0]?["tool"]?.ToString());
            Assert.Equal("inspect_surface", steps[0]?["args"]?["action"]?.ToString());
            Assert.Equal("MyReport", steps[0]?["args"]?["name"]?.ToString());
        }

        [Fact]
        public void ReportLayoutKbNotOpened_CarriesTheTransientRetryHint()
        {
            // KbNotOpened is a curated transient code: NextStepsCurationGuardTests
            // requires retryAfterMs at every emission site, and that guard reads the
            // source. This asserts the produced envelope keeps it.
            var json = JObject.Parse(LayoutService.ReportLayoutKbNotOpened(
                "MyReport", "Open a Knowledge Base before mutating the report layout."));

            Assert.Equal("KbNotOpened", json["error"]?["code"]?.ToString());
            Assert.Equal("KB not opened.", json["error"]?["message"]?.ToString());
            Assert.Equal("Open a Knowledge Base before mutating the report layout.",
                json["error"]?["hint"]?.ToString());
            Assert.Equal(2000, json["error"]?["retryAfterMs"]?.ToObject<int>());

            var steps = (JArray)json["error"]?["nextSteps"];
            Assert.NotNull(steps);
            Assert.Single(steps);
            Assert.Equal("genexus_kb", steps[0]?["tool"]?.ToString());
            Assert.Equal("open", steps[0]?["args"]?["action"]?.ToString());
        }

        [Fact]
        public void ReportLayoutKbNotOpened_PassesEachCallerHintThroughUnchanged()
        {
            // The report-mutation sites and the source-persistence site share the
            // envelope but name different work; that text is per-caller and must
            // survive verbatim.
            var mutation = JObject.Parse(LayoutService.ReportLayoutKbNotOpened(
                "T", "Open a Knowledge Base before mutating the report layout."));
            var persist = JObject.Parse(LayoutService.ReportLayoutKbNotOpened(
                "T", "Open a Knowledge Base before writing visual metadata."));

            Assert.Equal("Open a Knowledge Base before mutating the report layout.",
                mutation["error"]?["hint"]?.ToString());
            Assert.Equal("Open a Knowledge Base before writing visual metadata.",
                persist["error"]?["hint"]?.ToString());
        }

        // ── Source-level: every call site still routes through the builders ──

        [Theory]
        [InlineData("WwpActionService.Grid.cs")]
        [InlineData("WwpActionService.Tables.cs")]
        [InlineData("WwpActionService.Tabs.cs")]
        [InlineData("WwpActionService.WebComponentReplacement.cs")]
        [InlineData("WwpActionService.FormActions.cs")]
        public void EveryWwpMutatingPartial_CallsTheSharedGuard(string fileName)
        {
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", fileName);

            Assert.Contains("return BuildWwpInstanceNotResolvable(target);", src);
            // The hand-rolled copy must not creep back into a mutation path.
            Assert.DoesNotContain("could not be re-resolved before save", src);
        }

        [Fact]
        public void EveryReportMutation_ReachesBothGuards()
        {
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "LayoutService.cs");

            // Rename / add / delete print block each reach both guards. They reach
            // them through BeginReportMutation rather than calling the guard
            // envelopes directly, so the invariant is "three mutations, one
            // prologue, one envelope each" — ReportMutationPrologueTests pins the
            // prologue's own ordering.
            Assert.Equal(3, CountOccurrences(src, "var setup = BeginReportMutation(target);"));
            Assert.Equal(1, CountOccurrences(src, "setup.Error = ReportPartNotFound(target);"));
            Assert.Equal(1, CountOccurrences(src, "setup.Error = ReportLayoutKbNotOpened(target,"));
            Assert.Equal(1, CountOccurrences(src, "code: \"ReportPartNotFound\""));
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, index = 0;
            while ((index = haystack.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

    }
}
