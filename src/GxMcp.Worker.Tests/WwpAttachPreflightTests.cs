using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #330 — genexus_apply_pattern pattern=WorkWithPlus returned PatternNoOp on
    // every WebPanel while mode=diagnose reported "All pre-apply checks passed".
    //
    // On a non-Transaction target the engine's ApplyPattern is a documented silent
    // no-op (docs/sdk-probe/wwp-projection-discovery.md, dead end #1), so the
    // PatternInstancePackageInterface pipeline is the only route. WwpAttachPreflight
    // is the single resolver both the attach and mode=diagnose run; these tests pin
    // the invariant that made the bug invisible: a target the attach cannot serve is
    // reported as blocked, with the stage that blocks and the members that failed to
    // resolve. The SDK package is not needed — every touchpoint is a parameter.
    public class WwpAttachPreflightTests
    {
        private sealed class FakeModel { }
        private sealed class FakeParent { }
        private enum FakeSettingsView { NativeMobile, Web }

        // Shape of DVelop.Patterns.WorkWithPlus.Helpers.PatternInstancePackageInterface
        // in a U16+ package: both a four- and a five-parameter Create.
        private static class U16PackageInterface
        {
            public static bool CreatePatternInstanceWithTemplate(
                FakeModel model, FakeParent parent, string template, out object instance)
            {
                instance = null;
                return true;
            }

            public static bool CreatePatternInstanceWithTemplate(
                FakeModel model, FakeParent parent, FakeSettingsView settings, string template, out object instance)
            {
                instance = null;
                return true;
            }

            public static bool SetPatternApplyOnSave(FakeParent host) => true;
            public static bool ValidateAndSave(FakeParent host) => true;
        }

        // A package that only ever shipped the four-parameter create — a WebPanel can
        // never be attached through it, and diagnose has to say so.
        private static class NativeMobileOnlyPackageInterface
        {
            public static bool CreatePatternInstanceWithTemplate(
                FakeModel model, FakeParent parent, string template, out object instance)
            {
                instance = null;
                return true;
            }

            public static bool SetPatternApplyOnSave(FakeParent host) => true;
            public static bool ValidateAndSave(FakeParent host) => true;
        }

        // Missing SetPatternApplyOnSave: the host would be created but its edits would
        // never regenerate the parent's WebForm.
        private static class NoApplyOnSavePackageInterface
        {
            public static bool CreatePatternInstanceWithTemplate(
                FakeModel model, FakeParent parent, FakeSettingsView settings, string template, out object instance)
            {
                instance = null;
                return true;
            }

            public static bool ValidateAndSave(FakeParent host) => true;
        }

        // Shape of DVelop.Patterns.WorkWithPlus.WorkWithPattern.
        private sealed class FakeWorkWithPattern
        {
            public FakeWorkWithPattern() { }
            public object GetBuildProcess() => new object();
        }

        private sealed class NoParameterlessCtorWorkWithPattern
        {
            public NoParameterlessCtorWorkWithPattern(string _required) { }
            public object GetBuildProcess() => new object();
        }

        private sealed class NoBuildProcessWorkWithPattern
        {
            public NoBuildProcessWorkWithPattern() { }
        }

        private const string Template = "MatIsoTemplate";

        /// <summary>
        /// Runs the preflight against an explicitly-assembled package surface. Both
        /// type parameters are required: a <c>null</c> means "this member is missing",
        /// which is itself a case under test, so there is no healthy default to fall
        /// back to. Use <see cref="RunCompletePackage"/> for the all-green baseline
        /// and pass the healthy counterpart explicitly when breaking one member.
        /// </summary>
        private static WwpAttachPreflight Run(
            Type packageInterface,
            Type workWithPattern,
            string parentType = "WebPanel",
            string template = Template)
        {
            return WwpAttachPreflight.Run(
                packageInterface,
                workWithPattern,
                parentType,
                new FakeModel(),
                new FakeParent(),
                template);
        }

        private static WwpAttachPreflight RunCompletePackage(
            string parentType = "WebPanel", string template = Template) =>
            Run(typeof(U16PackageInterface), typeof(FakeWorkWithPattern), parentType, template);

        private static JObject OnlyFinding(WwpAttachPreflight preflight) =>
            (JObject)preflight.Findings.Single();

        // ── the resolvable case ────────────────────────────────────────────────

        [Theory]
        [InlineData("WebPanel")]
        [InlineData("WebComponent")]
        [InlineData("SDPanel")]
        public void CompletePackage_ResolvesEveryStage(string parentType)
        {
            var preflight = RunCompletePackage(parentType);

            Assert.True(preflight.CanAttach);
            Assert.Null(preflight.BlockedStage);
            Assert.Empty(preflight.Findings);
            Assert.NotNull(preflight.CreateMethod);
            Assert.NotNull(preflight.CreateArguments);
            Assert.NotNull(preflight.SetApplyOnSaveMethod);
            Assert.NotNull(preflight.ValidateAndSaveMethod);
        }

        [Fact]
        public void WebPanel_UsesTheFiveParameterWebSettingsViewOverload()
        {
            var preflight = RunCompletePackage("WebPanel");

            Assert.True(preflight.CanAttach);
            Assert.Equal(5, preflight.CreateMethod.GetParameters().Length);
            Assert.Equal(4, preflight.CreateByRefArgumentIndex);
            Assert.Equal(FakeSettingsView.Web, preflight.CreateArguments[2]);
            Assert.Equal(Template, preflight.CreateArguments[3]);
        }

        [Fact]
        public void SdPanel_UsesTheFourParameterNativeMobileOverload()
        {
            var preflight = RunCompletePackage("SDPanel");

            Assert.True(preflight.CanAttach);
            Assert.Equal(4, preflight.CreateMethod.GetParameters().Length);
            Assert.Equal(3, preflight.CreateByRefArgumentIndex);
        }

        // ── every stage that can block a WebPanel apply ─────────────────────────

        [Fact]
        public void MissingPackageInterface_BlocksAndNamesTheType()
        {
            var preflight = Run(packageInterface: null, workWithPattern: typeof(FakeWorkWithPattern));

            Assert.False(preflight.CanAttach);
            Assert.Equal(WwpAttachPreflight.StagePackageInterface, preflight.BlockedStage);
            var finding = OnlyFinding(preflight);
            Assert.Equal("critical", finding["severity"]?.ToString());
            Assert.Equal("wwpPackageInterfaceMissing", finding["reason"]?.ToString());
            Assert.Contains("PatternInstancePackageInterface", finding["detail"]?.ToString());
        }

        [Fact]
        public void MissingWorkWithPattern_BlocksBeforeAnythingIsMutated()
        {
            var preflight = Run(typeof(U16PackageInterface), workWithPattern: null);

            Assert.False(preflight.CanAttach);
            Assert.Equal(WwpAttachPreflight.StageProjection, preflight.BlockedStage);
            var finding = OnlyFinding(preflight);
            Assert.Equal("critical", finding["severity"]?.ToString());
            // The parent type is named so the message says which target is affected.
            Assert.Contains("WebPanel", finding["detail"]?.ToString());
        }

        [Fact]
        public void WorkWithPatternWithoutParameterlessCtor_Blocks()
        {
            var preflight = Run(typeof(U16PackageInterface), typeof(NoParameterlessCtorWorkWithPattern));

            Assert.False(preflight.CanAttach);
            Assert.Equal(WwpAttachPreflight.StageProjection, preflight.BlockedStage);
            Assert.Equal("wwpProjectionCtorMissing", OnlyFinding(preflight)["reason"]?.ToString());
        }

        [Fact]
        public void WorkWithPatternWithoutGetBuildProcess_Blocks()
        {
            var preflight = Run(typeof(U16PackageInterface), typeof(NoBuildProcessWorkWithPattern));

            Assert.False(preflight.CanAttach);
            Assert.Equal(WwpAttachPreflight.StageProjection, preflight.BlockedStage);
            Assert.Equal("wwpBuildProcessMissing", OnlyFinding(preflight)["reason"]?.ToString());
        }
        [Fact]
        public void NoTemplateInKb_BlocksWithATemplateRemediation()
        {
            var preflight = RunCompletePackage(template: null);

            Assert.False(preflight.CanAttach);
            Assert.Equal(WwpAttachPreflight.StageTemplate, preflight.BlockedStage);
            var finding = OnlyFinding(preflight);
            Assert.Equal("wwpTemplateMissing", finding["reason"]?.ToString());
            // Must NOT tell the caller to pass a different template — there are none.
            Assert.Contains("Import or create", finding["remediation"]?.ToString());
        }

        [Fact]
        public void WebPanelAgainstNativeMobileOnlyPackage_BlocksAndListsCandidates()
        {
            var preflight = Run(typeof(NativeMobileOnlyPackageInterface), typeof(FakeWorkWithPattern));

            Assert.False(preflight.CanAttach);
            Assert.Equal(WwpAttachPreflight.StageCreateOverload, preflight.BlockedStage);
            var finding = OnlyFinding(preflight);
            Assert.Equal("wwpCreateOverloadUnresolved", finding["reason"]?.ToString());
            // The rejected candidates are the evidence an agent needs to act on.
            Assert.Contains("CreatePatternInstanceWithTemplate", finding["detail"]?.ToString());
        }

        [Fact]
        public void MissingSetPatternApplyOnSave_Blocks()
        {
            var preflight = Run(typeof(NoApplyOnSavePackageInterface), typeof(FakeWorkWithPattern));

            Assert.False(preflight.CanAttach);
            Assert.Equal(WwpAttachPreflight.StageSetApplyOnSaveOverload, preflight.BlockedStage);
            Assert.Equal("wwpSetApplyOnSaveUnresolved", OnlyFinding(preflight)["reason"]?.ToString());
        }

        [Fact]
        public void CreateCandidates_AreRecordedOnBothTheResolvableAndTheBlockedPath()
        {
            // A create that throws or returns false has to be able to report which
            // overloads the installed package offers — the same evidence a
            // resolution failure carries. Losing it is how the original envelope
            // became untraceable.
            var resolved = RunCompletePackage();
            var blocked = Run(typeof(NativeMobileOnlyPackageInterface), typeof(FakeWorkWithPattern));

            Assert.Contains("CreatePatternInstanceWithTemplate", resolved.CreateCandidates);
            Assert.Contains("FakeSettingsView", resolved.CreateCandidates);
            Assert.Contains("CreatePatternInstanceWithTemplate", blocked.CreateCandidates);
        }

        [Fact]
        public void EveryBlockingStageIsCritical()
        {
            // A blocking stage reported as anything but critical would let diagnose
            // still answer "ok" — the exact failure mode of issue #330.
            var blocked = new[]
            {
                Run(packageInterface: null, workWithPattern: typeof(FakeWorkWithPattern)),
                Run(typeof(U16PackageInterface), workWithPattern: null),
                Run(typeof(U16PackageInterface), typeof(NoParameterlessCtorWorkWithPattern)),
                Run(typeof(U16PackageInterface), typeof(NoBuildProcessWorkWithPattern)),
                RunCompletePackage(template: ""),
                Run(typeof(NativeMobileOnlyPackageInterface), typeof(FakeWorkWithPattern)),
                Run(typeof(NoApplyOnSavePackageInterface), typeof(FakeWorkWithPattern))
            };

            foreach (var preflight in blocked)
            {
                Assert.False(preflight.CanAttach);
                Assert.Single(preflight.Findings);
                Assert.Equal("critical", preflight.Findings[0]["severity"]?.ToString());
                Assert.False(string.IsNullOrEmpty(preflight.Findings[0]["remediation"]?.ToString()));
                Assert.False(string.IsNullOrEmpty(preflight.Findings[0]["detail"]?.ToString()));
            }
        }
    }
}
