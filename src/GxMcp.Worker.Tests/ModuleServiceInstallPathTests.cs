using System;
using System.IO;
using System.Linq;
using GxMcp.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// An entire install-preview subsystem had been orphaned inside
    /// <c>ModuleService</c> without anyone noticing.
    ///
    /// <c>install</c> and <c>install_builtin</c> both route to
    /// <c>InstallVerified</c>, which plans through <c>ModuleInstallFlow</c> and
    /// returns that flow's receipt. Four private methods still built their own,
    /// older envelopes - <c>PreviewInstall</c>, <c>PreviewInstallByName</c>,
    /// <c>PackageResultDetails</c> and <c>PackageValidationError</c> - and not one
    /// of them had a caller. They emitted the codes <c>ModuleInstallPreview</c> and
    /// <c>ModuleInstallBuiltInPreview</c>, which nothing else in the repository
    /// produces, so no client and no contract document depended on them either.
    ///
    /// What makes this worth a guard rather than a deletion is that the drift was
    /// invisible: the preview projected <c>package.description</c> and the
    /// post-install result did not, which reads like a bug someone would go looking
    /// for. It was not a bug. Both projections belonged to code no caller could
    /// reach.
    ///
    /// The last test pins the residue that was deliberately left behind, so it is a
    /// decision rather than the next person's unexplained discovery.
    /// </summary>
    public class ModuleServiceInstallPathTests
    {
        private static string Source() =>
            RepoSource.Read("src", "GxMcp.Worker", "Services", "ModuleService.cs");

        [Fact]
        public void BothInstallActionsRouteThroughTheOneInstallPath()
        {
            string src = Source();

            Assert.Equal(1, SourceAssert.Count(src, @"if (action == ""install"" || action == ""install_builtin"") return InstallVerified(args);"));
            Assert.Equal(1, SourceAssert.Count(src, "private string InstallVerified(JObject args)"));
        }

        [Theory]
        [InlineData("PackageValidationError")]
        [InlineData("PreviewInstall")]
        [InlineData("PreviewInstallByName")]
        [InlineData("PackageResultDetails")]
        [InlineData("TryGetPackagedModuleDependencies")]
        [InlineData("ReadDependencyString")]
        [InlineData("ReadDependencyBool")]
        [InlineData("ReadDependencyGuid")]
        public void TheOrphanedPreviewSubsystemStaysGone(string member)
        {
            // These eight were reachable from nothing. A new caller is a change of
            // intent and should arrive with the method, not find it still here.
            Assert.Equal(0, SourceAssert.Count(Source(), member));
        }

        [Theory]
        [InlineData("ModuleInstallPreview")]
        [InlineData("ModuleInstallBuiltInPreview")]
        public void ThePreviewCodesItEmittedAreGoneToo(string code)
        {
            // The codes were the only externally visible trace of the dead code.
            // Leaving one behind would advertise an envelope nothing produces.
            Assert.Equal(0, SourceAssert.Count(Source(), code));
        }

        [Fact]
        public void TheDryRunPreviewStillComesFromTheLiveFlow()
        {
            // The capability is not lost with the dead code: install dryRun is
            // handled by ModuleInstallFlow, which the capabilities table is what it
            // is because of. This asserts the live path is still wired, so the
            // deletion cannot be mistaken for having dropped the preview.
            string flow = RepoSource.Read("src", "GxMcp.Worker", "Services", "ModuleInstallFlow.cs");

            Assert.Contains("dryRun", flow, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheDependencySourceHelpersAreKnownResidueAndNotAnOversight()
        {
            // These two lost their only production caller when the preview went, but
            // they are not deleted: SelectDependencySource is a pure preference
            // function written to be testable without a live KB, and
            // TryGetExportDependencies records which SDK majors expose
            // ModuleContentPart.ExportDependencies. Both carry four tests each.
            //
            // Asserting production-dead + still-tested keeps that a recorded
            // decision. If someone deletes the tests, this fails and the question
            // gets reopened deliberately instead of by accident.
            Assert.Equal(1, ProductionReferences("SelectDependencySource"));
            Assert.Equal(1, ProductionReferences("TryGetExportDependencies"));

            int preference = TestReferences("SelectDependencySource");
            int exportPart = TestReferences("TryGetExportDependencies");
            Assert.True(preference >= 2, "SelectDependencySource lost its test coverage: " + preference + " refs");
            Assert.True(exportPart >= 2, "TryGetExportDependencies lost its test coverage: " + exportPart + " refs");
        }

        [Fact]
        public void TheOnlyUnreachableServiceMembersAreTheKnownUnwiredOnes()
        {
            // The general form of what was fixed, and stronger than re-listing the
            // eight removed names: every unreachable private method across the
            // service files must be one this repository has already decided about.
            //
            // Six are deliberate. They are advertised capabilities whose
            // post-write verification and legacy/WWP wiring never landed
            // (PatchService, StructureService, PatternAnalysisService,
            // PatternApplyService). They are reported, not removed, so they appear
            // here as a named allowlist: a new orphan fails this test, and so does
            // silently deleting one of these, because the list would no longer
            // match what the code contains.
            var actual = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
            foreach (string file in ServiceFiles())
            {
                foreach (string member in UnreachableIn(file))
                    actual.Add(file + ":" + member);
            }

            // The service files are discovered, not listed, so a new service cannot
            // quietly opt out of the check by never being named here.
            Assert.True(ServiceFiles().Length >= 30, "service file discovery collapsed: " + ServiceFiles().Length);

            var expected = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal)
            {
                "PatchService.cs:VerifyPersistedSource",
                "PatchService.cs:AttachPersistedSnippet",
                "PatternAnalysisService.cs:ResolveWWPInstanceFresh",
                "PatternAnalysisService.cs:FindWWPInstance",
                "PatternApplyService.cs:TryInvokeBuildProcessUpdateParent_Legacy",
                "StructureService.cs:VerifyStructurePersisted",
            };

            Assert.True(actual.SetEquals(expected),
                "unreachable members changed.\n  unexpected: " +
                string.Join(", ", actual.Except(expected)) +
                "\n  missing:    " + string.Join(", ", expected.Except(actual)));
        }

        private static string[] _serviceFiles;

        private static string[] ServiceFiles()
        {
            if (_serviceFiles != null) return _serviceFiles;

            _serviceFiles = Directory
                .GetFiles(RepoSource.DirectoryOf("src", "GxMcp.Worker", "Services"), "*.cs")
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            return _serviceFiles;
        }

        /// <summary>
        /// Private methods of one file that nothing outside the file names. Mirrors
        /// the rule <c>scripts/find-unreachable.ps1</c> applies, restricted to a
        /// single file so the test needs no process.
        /// </summary>
        private static string[] UnreachableIn(string fileName)
        {
            string path = RepoSource.PathOf("src", "GxMcp.Worker", "Services", fileName);
            if (path == null) return new string[0]; // renamed or split; nothing to assert

            string text = File.ReadAllText(path);
            var declared = new System.Collections.Generic.List<string>();
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(text, @"private\s+(?:static\s+)?[\w\.<>\[\]\?]+\s+(\w+)\s*\("))
            {
                string name = m.Groups[1].Value;
                if (!declared.Contains(name)) declared.Add(name);
            }

            string repo = ReadAllWorkerSources();

            var unreachable = new System.Collections.Generic.List<string>();
            foreach (string name in declared)
            {
                // The MCP entry point is reached by the dispatcher, not by name.
                if (name.StartsWith("Handle", StringComparison.Ordinal)) continue;
                int uses = System.Text.RegularExpressions.Regex.Matches(repo, @"\b" + System.Text.RegularExpressions.Regex.Escape(name) + @"\b").Count;
                if (uses <= 1) unreachable.Add(name); // only its own declaration
            }
            return unreachable.ToArray();
        }

        private static string _workerSources;

        /// <summary>
        /// Production sources only. The test projects must be excluded, and not as a
        /// formality: this file names the expected-unreachable members in its own
        /// allowlist, so a scan that included it would count those strings as
        /// callers and report every one of them as reachable. The guard would then
        /// pass for the wrong reason - the exact failure it exists to prevent.
        /// </summary>
        private static string ReadAllWorkerSources()
        {
            if (_workerSources != null) return _workerSources;

            var builder = new System.Text.StringBuilder();
            foreach (string file in Directory.GetFiles(RepoSource.DirectoryOf("src", "GxMcp.Worker"), "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file)) continue;
                builder.Append(File.ReadAllText(file)).Append('\n');
            }
            _workerSources = builder.ToString();
            return _workerSources;
        }

        private static bool IsBuildOutput(string path)
        {
            return path.IndexOf(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal) >= 0
                || path.IndexOf(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) >= 0;
        }

        private static int ProductionReferences(string member)
        {
            return System.Text.RegularExpressions.Regex.Matches(
                File.ReadAllText(RepoSource.PathOf("src", "GxMcp.Worker", "Services", "ModuleService.cs")),
                @"\b" + System.Text.RegularExpressions.Regex.Escape(member) + @"\b").Count;
        }

        /// <summary>
        /// References from the test project, excluding this file.
        ///
        /// The exclusion is load-bearing. This file names both members, so counting
        /// itself would make <c>&gt;= 1</c> true on the strength of the guard's own
        /// allowlist and the assertion could never fail - it would report coverage
        /// that is only coverage of the guard.
        /// </summary>
        private static int TestReferences(string member)
        {
            int total = 0;
            foreach (string file in Directory.GetFiles(RepoSource.DirectoryOf("src", "GxMcp.Worker.Tests"), "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file)) continue;
                if (string.Equals(Path.GetFileName(file), "ModuleServiceInstallPathTests.cs", StringComparison.Ordinal)) continue;
                total += System.Text.RegularExpressions.Regex.Matches(
                    File.ReadAllText(file), @"\b" + System.Text.RegularExpressions.Regex.Escape(member) + @"\b").Count;
            }
            return total;
        }
    }
}
