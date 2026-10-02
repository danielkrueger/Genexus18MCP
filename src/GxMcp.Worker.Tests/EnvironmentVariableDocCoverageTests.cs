using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Keeps <c>docs/environment_variables.md</c> equal to the set of environment
    /// variables the code actually reads.
    ///
    /// <para>
    /// The doc is the only place an operator can discover that a variable exists,
    /// and it drifted: at the commit this test was added, 46 of the 103 read
    /// names were absent - including the five TeamDev credentials and the
    /// watchdogs that force-fail a wedged build. The note at the bottom of the
    /// doc asked authors to keep it current and nothing checked, so the next
    /// author did not.
    /// </para>
    ///
    /// <para>
    /// <b>What this scan can and cannot see - deliberately stated rather than
    /// claimed away.</b> It resolves two shapes:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <c>GetEnvironmentVariable("NAME")</c> with a literal argument, which is
    /// every call site in the tree except the five listed below.
    /// </description></item>
    /// <item><description>
    /// <c>const string &lt;id&gt; = "NAME"</c> where the identifier ends in
    /// <c>EnvVar</c> / <c>Variable</c> / <c>EnvironmentVariable</c> and the value
    /// is passed to <c>GetEnvironmentVariable</c>. This covers
    /// <c>WriteDestinationGuard.PathVariable</c>, the two
    /// <c>SemanticCacheStore</c> cache caps and
    /// <c>ArtifactPathResolver.OutputDirectoryEnvironmentVariable</c>.
    /// </description></item>
    /// </list>
    /// <para>
    /// It cannot see three shapes, and this test does not pretend otherwise.
    /// A name read through a loop variable or a local array
    /// (<c>Configuration.RejectStrictStructuralEnvironment</c> - every name in
    /// that array is documented anyway); a name passed as an argument to a
    /// helper that forwards it (<c>Worker.ResolveQueueCapacity("GXMCP_MTA_CONCURRENCY", 8)</c>
    /// and three siblings); and a name that arrives at runtime, which in this
    /// tree is exactly one case - the datastore-alias credential references,
    /// where the profile declares the variable name, so no fixed name exists to
    /// scan for. All three are described in the doc's maintenance note. If a new
    /// one appears, the doc's coverage claim drifts silently: that is the known
    /// limit of a source scan, and the alternative (a runtime probe per variable)
    /// would not see an opt-in branch either.
    /// </para>
    ///
    /// <para>
    /// The scope is all of <c>src/</c>, test projects included: a variable the
    /// live harness reads is a variable an operator sees in a skip message and
    /// searches for. The prefix filter is what excludes OS and third-party
    /// names such as <c>PATHEXT</c>; those are documented separately under the
    /// third-party passthrough section.
    /// </para>
    /// </summary>
    public class EnvironmentVariableDocCoverageTests
    {
        private static readonly string[] Prefixes =
        {
            "GXMCP_",
            "GENEXUS_MCP_",
            "GX_MCP_",
            "GX_",
        };

        // A literal argument at the call site.
        private static readonly Regex LiteralRead = new Regex(
            @"GetEnvironmentVariable\(\s*""(?<name>[A-Z][A-Z0-9_]*)""",
            RegexOptions.CultureInvariant);

        // A name held in a constant whose identifier says it holds a variable
        // name. The identifier suffix is the filter: a broad
        // `const string = "ANY_UPPER"` scan also matches diagnostic codes such
        // as GXMCP_SDK_UNKNOWN, which are not environment variables.
        private static readonly Regex ConstBoundName = new Regex(
            @"const\s+string\s+\w*(?:EnvVar|Variable|EnvironmentVariable)\s*=\s*""(?<name>[A-Z][A-Z0-9_]*)""",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// Names that are deliberately internal - the Gateway or the shared-host
        /// broker injects them into the Worker's environment, so nobody sets them
        /// by hand. They are still required in the doc, and this list adds the
        /// stronger requirement that they live under "Set internally (do not set
        /// by hand)" rather than in an operator-facing table: a Gateway-injected
        /// value presented as a knob is worse than an undocumented one, because
        /// it invites an operator to pin it.
        /// </summary>
        private static readonly Dictionary<string, string> Allowlist = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "GXMCP_SERVER_VERSION", "injected" },
            { "GXMCP_DRIVER", "injected" },
            { "GXMCP_TARGET_MAJOR", "injected" },
            { "GXMCP_PROFILE_CONFIG_PATH", "injected" },
            { "GXMCP_OPERATIONAL_STATE_KEY", "injected" },
            { "GXMCP_SHARED_CHILD", "injected" },
            { "GXMCP_KB_ID", "injected" },
            { "GXMCP_KB_GENERATION", "injected" },
            { "GXMCP_STATE_SCOPE_ID", "injected" },
        };

        private const string InternalHeading = "## Set internally (do not set by hand)";

        [Fact]
        public void EveryEnvironmentVariableReadUnderSrcIsDocumented()
        {
            string doc = RepoSource.Read("docs", "environment_variables.md");
            string[] missing = ReadVariableNames()
                .Where(name => !IsBackticked(doc, name))
                .ToArray();

            Assert.True(
                missing.Length == 0,
                "docs/environment_variables.md does not document " + missing.Length +
                " environment variable(s) read under src/. Add a row under the section that matches the" +
                " variable's classification (operator-facing, harness/test-only, third-party passthrough)," +
                " or - if it is deliberately internal - add it to this test's Allowlist with its reason:" +
                Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", missing));
        }

        /// <summary>
        /// The allowlist is a record of intent, not an escape hatch. Each entry
        /// must still be a live read (a stale entry means the allowlist is
        /// accumulating dead names) and must be documented under the internal
        /// heading, which is where a Gateway-injected value belongs.
        /// </summary>
        [Fact]
        public void AllowlistedNamesAreStillDocumentedAsInternal()
        {
            HashSet<string> read = new HashSet<string>(ReadVariableNames(), StringComparer.Ordinal);
            string doc = RepoSource.Read("docs", "environment_variables.md");
            string internalSection = SectionOf(doc, InternalHeading);

            Assert.NotNull(internalSection);
            Assert.NotEmpty(internalSection);

            var problems = new List<string>();

            foreach (var entry in Allowlist.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (!read.Contains(entry.Key))
                    problems.Add(entry.Key + ": allowlisted but no longer read under src/ - remove it and say why in the changelog.");

                if (!IsBackticked(doc, entry.Key))
                    problems.Add(entry.Key + ": allowlisted but not documented in docs/environment_variables.md.");

                if (!IsBackticked(internalSection, entry.Key))
                    problems.Add(entry.Key + ": allowlisted but not documented under '" + InternalHeading + "'.");
            }

            Assert.True(
                problems.Count == 0,
                problems.Count + " allowlist problem(s) in EnvironmentVariableDocCoverageTests:" +
                Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", problems));
        }

        /// <summary>
        /// Every distinct, prefix-filtered name the two resolvable call shapes
        /// name, across every <c>.cs</c> file under <c>src/</c>.
        /// </summary>
        private static string[] ReadVariableNames()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (string file in SourceFiles())
            {
                // Comments and string literals can both contain the pattern, and
                // a commented-out example is not a read. Blank the comments first
                // - RepoSource is deliberate about why that step exists.
                string source = RepoSource.WithoutComments(File.ReadAllText(file));

                foreach (Regex pattern in new[] { LiteralRead, ConstBoundName })
                {
                    foreach (Match match in pattern.Matches(source))
                    {
                        string name = match.Groups["name"].Value;
                        if (Prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
                            names.Add(name);
                    }
                }
            }

            return names.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }

        /// <summary>
        /// Every production and test source file under <c>src/</c>. The build
        /// output directories hold copies of the same sources and would only
        /// duplicate names.
        /// </summary>
        private static string[] SourceFiles()
        {
            string srcRoot = RepoSource.DirectoryOf("src");
            return Directory
                .GetFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
                .Where(f => f.IndexOf(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0)
                .Where(f => f.IndexOf(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();
        }

        private static bool IsBackticked(string text, string name)
        {
            return text.IndexOf('`' + name + '`', StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// The text from <paramref name="heading"/> to the next level-2 heading,
        /// or null when the heading is absent.
        /// </summary>
        private static string SectionOf(string doc, string heading)
        {
            int start = doc.IndexOf(heading, StringComparison.Ordinal);
            if (start < 0) return null;

            int end = doc.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
            return end < 0 ? doc.Substring(start) : doc.Substring(start, end - start);
        }
    }
}