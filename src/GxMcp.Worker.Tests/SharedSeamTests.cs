using System;
using System.IO;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// ConsoleProbe replaces a byte-identical Which in PreviewService and
    /// VisualVerifyService plus a third copy of the same "run it, return the first
    /// non-blank stdout line" shape. It shells out for real, so these assert the
    /// three rules the duplicated copies relied on: first non-blank line wins, a
    /// non-zero exit yields null even when it printed something, and Which is
    /// `where` (rooted path) or null.
    /// </summary>
    public class ConsoleProbeTests
    {
        [Fact]
        public void FirstOutputLine_ReturnsFirstNonBlankLineTrimmed()
        {
            // Leading blank lines must be skipped, and the trailing \r trimmed.
            string line = ConsoleProbe.FirstOutputLine("(echo.) & (echo first) & (echo second)", 10000);

            Assert.Equal("first", line);
        }

        [Fact]
        public void FirstOutputLine_ReturnsNullWhenCommandExitsNonZeroDespiteOutput()
        {
            // Proves the gate is the exit code, not merely "did it print something".
            Assert.Null(ConsoleProbe.FirstOutputLine("echo hi & exit 1", 10000));
        }

        [Fact]
        public void FirstOutputLine_ReturnsNullWhenCommandPrintsNothing()
        {
            Assert.Null(ConsoleProbe.FirstOutputLine("exit 0", 10000));
        }

        [Fact]
        public void Which_ResolvesARootedPathForACommandOnPath()
        {
            string resolved = ConsoleProbe.Which("cmd");

            Assert.NotNull(resolved);
            Assert.True(Path.IsPathRooted(resolved), "expected a rooted path, got: " + resolved);
            Assert.EndsWith("cmd.exe", resolved, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Which_ReturnsNullForAnAbsentCommand()
        {
            Assert.Null(ConsoleProbe.Which("__gxmcp_no_such_command_9f3a__"));
        }
    }

    /// <summary>
    /// The VariableTypeNotPersisted envelope was written out three times in
    /// WriteService.Variables.cs (Domain, Attribute, SDT/Business Component) with
    /// identical code, nextSteps and extra and only the message/hint differing.
    /// The three shared fields are the recovery contract, so they are pinned here.
    /// </summary>
    public class VariableTypeNotPersistedEnvelopeTests
    {
        [Fact]
        public void CarriesTheCodeAndTheVariablesPartReadBackStep()
        {
            var invalid = new JArray(new JObject { ["name"] = "&vLocal", ["reason"] = "display-only" });
            var json = JObject.Parse(WriteService.VariableTypeNotPersisted(
                "MyPanel", invalid, "msg", "hint"));

            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("MyPanel", json["target"]?.ToString());
            Assert.Equal("VariableTypeNotPersisted", json["error"]?["code"]?.ToString());
            Assert.Equal("msg", json["error"]?["message"]?.ToString());
            Assert.Equal("hint", json["error"]?["hint"]?.ToString());

            var steps = (JArray)json["error"]?["nextSteps"];
            Assert.NotNull(steps);
            Assert.Single(steps);
            Assert.Equal("genexus_read", steps[0]?["tool"]?.ToString());
            Assert.Equal("MyPanel", steps[0]?["args"]?["name"]?.ToString());
            Assert.Equal("Variables", steps[0]?["args"]?["part"]?.ToString());
            Assert.False(string.IsNullOrWhiteSpace(steps[0]?["why"]?.ToString()));

            Assert.Equal("&vLocal", json["variables"]?[0]?["name"]?.ToString());
        }

        [Fact]
        public void PassesTheMessageAndHintThroughVerbatim()
        {
            // The three call sites name a different reference kind; that text is the
            // only thing allowed to vary, and it must not be rewritten.
            var json = JObject.Parse(WriteService.VariableTypeNotPersisted(
                "MyPanel", new JArray(),
                "The SDK did not persist the requested Domain as a native entity reference.",
                "The operation cannot be completed safely on this GeneXus build."));

            Assert.Contains("Domain", json["error"]?["message"]?.ToString());
            Assert.Contains("cannot be completed safely", json["error"]?["hint"]?.ToString());
        }

        [Fact]
        public void EveryPostSaveReadBack_CallsTheSharedBuilder()
        {
            // Source-level guard: the three post-save reference read-backs (Domain,
            // Attribute, SDT/Business Component) must not drift back to hand-rolled
            // VariableTypeNotPersisted envelopes. The pre-save "could not be
            // represented" envelope at the VarBuildResult check is a different
            // contract (no nextSteps, typeName/details extra) and stays separate, so
            // this counts the builder call sites rather than the code literals.
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "WriteService.Variables.cs");

            Assert.Equal(3, CountOccurrences(src, "return VariableTypeNotPersisted("));
            Assert.Equal(2, CountOccurrences(src, "code: \"VariableTypeNotPersisted\""));
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
