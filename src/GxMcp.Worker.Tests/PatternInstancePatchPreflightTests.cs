using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #350: a <c>PatternInstance</c> patch dry run reported <c>Applied</c> for a
    /// payload the write path's structural preflight refuses, because the patch branch
    /// returned its dry-run envelope before reaching the plan. One logical edit
    /// therefore previewed green through <c>mode=patch</c> and failed red through
    /// <c>mode=full</c> on the same bytes.
    ///
    /// <para>
    /// The decision itself belongs to <see cref="PatternXmlEditPlan"/>, which is covered
    /// directly by <see cref="PatternXmlEditPlanTests"/>. What is new here is the
    /// <em>routing</em>: that the patch path runs the same plan over the same normalized
    /// bytes the write would send, and says so. PatchService needs a live SDK object to
    /// resolve the instance, so the guard is a source-shape assertion on the production
    /// call site plus a behavioural check of the plan on the exact bytes the reported
    /// reproduction used - which is the honest split, rather than a fake Worker claiming
    /// to have exercised the SDK.
    /// </para>
    /// </summary>
    public class PatternInstancePatchPreflightTests
    {
        // The reproduction's shape: a filter with one attribute, plus a second one.
        // Inserting the second increases the child count under <attributes/>, which is
        // what the structural preflight refuses.
        private const string Current =
            "<instance><level><selection><filter><attributes>" +
            "<filterAttribute name='TemExistente' description='Tem Existente'/>" +
            "</attributes></filter></selection></level></instance>";

        private const string WithAddedFilterAttribute =
            "<instance><level><selection><filter><attributes>" +
            "<filterAttribute name='TemExistente' description='Tem Existente'/>" +
            "<filterAttribute name='Probe' description='Preview'/>" +
            "</attributes></filter></selection></level></instance>";

        private const string WithChangedDescription =
            "<instance><level><selection><filter><attributes>" +
            "<filterAttribute name='TemExistente' description='Tem Existente (rev)'/>" +
            "</attributes></filter></selection></level></instance>";

        [Fact]
        public void The_Reported_Payload_Is_Refused_By_The_Shared_Preflight()
        {
            // This is the asymmetry the issue reported: the same bytes that the patch
            // preview approved are refused here. Pinning it on the plan keeps the
            // reproduction honest even though the patch routing itself needs an SDK.
            var plan = PatternXmlEditPlan.Create(Current, WithAddedFilterAttribute, allowGridStructure: false);

            Assert.Equal("PatternStructureChangeUnsupported", plan.ErrorCode);
            Assert.Empty(plan.Changes);
        }

        [Fact]
        public void A_Supported_Property_Change_Still_Passes_The_Shared_Preflight()
        {
            // The fix must not turn the preview into a blanket refusal: the supported
            // edit has to keep previewing as Applied.
            var plan = PatternXmlEditPlan.Create(Current, WithChangedDescription, allowGridStructure: false);

            Assert.Null(plan.ErrorCode);
            Assert.False(plan.IsNoChange);
            Assert.Single(plan.Changes);
            Assert.Equal("Tem Existente", (string)plan.Changes[0]["before"]);
            Assert.Equal("Tem Existente (rev)", (string)plan.Changes[0]["after"]);
        }

        [Fact]
        public void Invalid_Xml_Is_Refused_Rather_Than_Reported_As_A_Change()
        {
            var plan = PatternXmlEditPlan.Create(Current, "<instance><unclosed>", allowGridStructure: false);

            Assert.NotNull(plan.ErrorCode);
            Assert.Empty(plan.Changes);
        }

        [Fact]
        public void The_Patch_Path_Runs_The_Same_Preflight_For_PatternInstance()
        {
            // The routing guard. PatchService cannot be driven without a live SDK
            // object, so the assertion is that the production call site exists, runs
            // BEFORE the dry-run envelope returns, and passes the same
            // ToSdkLineEndings normalization the write path sends - a preflight over
            // differently-normalized bytes could approve what the write refuses.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "PatchService.cs");

            int planAt = source.IndexOf("PatternXmlEditPlan.Create", StringComparison.Ordinal);
            Assert.True(planAt >= 0,
                "PatchService does not run PatternXmlEditPlan at all, so a patch preview can approve a structural edit");

            int dryRunAt = source.IndexOf("if (dryRun)", StringComparison.Ordinal);
            Assert.True(dryRunAt > 0, "dry-run branch not found");
            Assert.True(planAt < dryRunAt,
                "the structural preflight must run before the dry-run envelope returns, otherwise the preview still approves it");

            // The shared normalization. If this diverges from the write path the
            // preview and the write are checking different documents.
            var window = source.Substring(planAt, Math.Min(400, source.Length - planAt));
            Assert.Contains("ToSdkPatternLineEndings(updatedSource)", window, StringComparison.Ordinal);
            Assert.Contains("allowGridStructure: false", window, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Preflight_Is_Gated_To_PatternInstance_Only()
        {
            // PatternVirtual keeps its structural authoring contract, so folding it
            // into this gate would remove a capability the contract grants.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "PatchService.cs");

            int planAt = source.IndexOf("PatternXmlEditPlan.Create", StringComparison.Ordinal);
            var gate = source.Substring(Math.Max(0, planAt - 900), Math.Min(900, planAt));

            Assert.Contains("PatternInstance", gate, StringComparison.Ordinal);
            Assert.DoesNotContain("PatternVirtual", gate, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Dry_Run_That_Passes_Reports_Which_Preflight_Ran()
        {
            // A preview that does not name its coverage is indistinguishable from a
            // textual-only check, which is the ambiguity that hid this defect.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "PatchService.cs");

            Assert.Contains("structuralPreflight", source, StringComparison.Ordinal);
            Assert.Contains("structuralChanges", source, StringComparison.Ordinal);
        }

        [Fact]
        public void Both_Routes_Hand_The_Plan_The_Same_Normalization()
        {
            // "The preview matches the write" is a claim about two documents, so both
            // call sites must name the same normalizer. Before this they did not: the
            // patch route normalized and the write route took raw decoded content, so
            // the same edit could be compared in two encodings.
            string patch = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "PatchService.cs");
            string write = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "WriteService.PatternWrite.cs");

            var patchCall = Regex.Match(patch, @"PatternXmlEditPlan\.Create\([^;]*?\)");
            var writeCall = Regex.Match(write, @"PatternXmlEditPlan\.Create\([^;]*?\)");
            Assert.True(patchCall.Success, "PatchService does not call the plan");
            Assert.True(writeCall.Success, "WriteService does not call the plan");

            Assert.Contains("ToSdkPatternLineEndings", patchCall.Value, StringComparison.Ordinal);
            Assert.Contains("ToSdkPatternLineEndings", writeCall.Value, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Shared_Normalizer_Is_Crlf_And_Leaves_Crlf_Alone()
        {
            // The SDK stores CRLF. The normalizer must be idempotent, or normalizing
            // twice would double every line ending and the preflight would report a
            // change on a payload nothing changed.
            string once = WriteService.ToSdkPatternLineEndings("<a>\n<b/>\n</a>");
            Assert.Equal("<a>\r\n<b/>\r\n</a>", once);
            Assert.Equal(once, WriteService.ToSdkPatternLineEndings(once));
            Assert.Equal(once, WriteService.ToSdkPatternLineEndings("<a>\r\n<b/>\r\n</a>"));
            Assert.Equal(string.Empty, WriteService.ToSdkPatternLineEndings(string.Empty));
            Assert.Null(WriteService.ToSdkPatternLineEndings(null));
        }

        [Fact]
        public void A_Line_Ending_Difference_Alone_Is_Not_A_Change()
        {
            // The failure the shared normalizer exists to prevent: the same document in
            // two encodings must not read as a structural or property change.
            string crlf = "<instance><level><selection><filter><attributes>"
                + "<filterAttribute name='TemExistente' description='Tem Existente'/>"
                + "</attributes></filter></selection></level></instance>".Replace("\n", "\r\n");
            string lf = crlf.Replace("\r\n", "\n");

            var plan = PatternXmlEditPlan.Create(
                crlf, WriteService.ToSdkPatternLineEndings(lf), allowGridStructure: false);

            Assert.Null(plan.ErrorCode);
            Assert.True(plan.IsNoChange);
        }
    }
}
