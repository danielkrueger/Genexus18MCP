using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Issue #340: the first-touch warm pass's source-search command supplied the probe
    /// object's name as a text <c>pattern</c> with <c>maxResults=1</c> and no object
    /// scope. The result limit caps the reply, not the work: when that name does not
    /// occur in any stored source - which is the normal case for a generated name - the
    /// search is a whole-catalog no-match scan, so the warm pass paid the very KB-wide
    /// scan it exists to pay, against a pattern that cannot match.
    ///
    /// The command list is private, so this asserts on the source. That is the honest
    /// option here: the property is "this command carries an object scope", which is a
    /// statement about the emitted arguments and not about any behaviour reachable
    /// without a live Worker.
    /// </summary>
    public class WarmupSourceSearchScopeTests
    {
        private static string WarmupSource() => GxMcp.TestSupport.RepoSource.WithoutComments(
            "src", "GxMcp.Gateway", "Program.Warmup.cs");

        [Fact]
        public void The_Source_Search_Warm_Command_Is_Scoped_To_The_Probe_Object()
        {
            string command = SourceSearchWarmCommand();

            Assert.Contains("[\"objectName\"] = probeObjectName", command, StringComparison.Ordinal);
            Assert.Contains("[\"maxResults\"] = 1", command, StringComparison.Ordinal);
        }

        /// <summary>
        /// The <c>genexus_search_source</c> warm entry, delimited by the tuple that
        /// closes it. Scanning forward to the closing <c>}),</c> would swallow every
        /// later entry, which is what made an earlier version of this guard pass
        /// against code that had no object scope at all.
        /// </summary>
        private static string SourceSearchWarmCommand()
        {
            string source = WarmupSource();

            int at = source.IndexOf("\"genexus_search_source\"", StringComparison.Ordinal);
            Assert.True(at >= 0, "the source-search warm command was not found");

            int end = source.IndexOf("}),", at, StringComparison.Ordinal);
            Assert.True(end > at, "could not delimit the source-search warm command");
            return source.Substring(at, end - at);
        }

        [Fact]
        public void Every_Warm_Command_Names_A_Known_Tool()
        {
            // A typo in a warm command would silently warm nothing while reporting
            // success, which is the failure mode a warm pass must not have.
            // The command list is the argument tuple feeding WarmFirstTouchPathsAsync.
            string source = WarmupSource();
            int start = source.IndexOf("WarmFirstTouchPathsAsync", StringComparison.Ordinal);
            Assert.True(start >= 0, "WarmFirstTouchPathsAsync not found");

            var known = Newtonsoft.Json.JsonConvert
                    .DeserializeObject<JArray>(System.IO.File.ReadAllText(
                        GxMcp.TestSupport.RepoSource.PathOf("src", "GxMcp.Gateway", "tool_definitions.json")))!
                .Select(t => (string)t["name"]!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (System.Text.RegularExpressions.Match match in
                System.Text.RegularExpressions.Regex.Matches(source, "\\(\"(genexus_[a-z_]+)\""))
            {
                string tool = match.Groups[1].Value;
                if (tool == "genexus_search_source") continue; // the entry under test
                Assert.True(known.Contains(tool), $"warm command names unknown tool '{tool}'");
            }
        }
    }
}
