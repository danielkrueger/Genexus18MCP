using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// <c>tool_definitions.json</c> is the source of truth for the tool schemas.
    /// Two Gateway components read it through a copy-pasted private
    /// <c>LocateToolDefinitions</c> - <c>GatewayArgsValidator</c> and
    /// <c>ToolIdentity</c> - code-identical, and <c>ToolIdentity</c>'s comment said
    /// it mirrored the other. Both are now <c>ToolDefinitionsLocator.Locate</c>.
    ///
    /// The property is agreement: the validator that rejects a malformed argument
    /// and the identity check that decides which tool names are canonical must be
    /// reading the same file. Located two ways, they can drift onto different ones
    /// and neither notices.
    ///
    /// A third loader, in <c>McpRouter</c>, uses a deliberately different strategy
    /// and is left alone; <see cref="McpRouterStillUsesItsOwnStrategy"/> records
    /// that rather than letting it look like an oversight.
    /// </summary>
    public class ToolDefinitionsLocatorTests
    {
        [Fact]
        public void TheLocatorFindsTheSchemasInThisLayout()
        {
            // The dev/test layout the upward walk exists for: the JSON lives under
            // the project, not next to the test binary.
            string? located = ToolDefinitionsLocator.Locate();

            Assert.NotNull(located);
            Assert.True(File.Exists(located!));
            Assert.Equal("tool_definitions.json", Path.GetFileName(located));
        }

        [Fact]
        public void TheLocatorReturnsParseableSchemasWithTheExpectedShape()
        {
            string? located = ToolDefinitionsLocator.Locate();
            Assert.NotNull(located);

            var parsed = Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(located!));

            Assert.NotEmpty(parsed);
            Assert.All(parsed.OfType<Newtonsoft.Json.Linq.JObject>(),
                tool => Assert.False(string.IsNullOrWhiteSpace(tool["name"]?.ToString())));
        }

        [Fact]
        public void TheUpwardWalkFindsTheProjectLayout()
        {
            // This is the branch the test project cannot reach: it copies the JSON
            // next to its own binary, so the beside-the-assembly candidate always
            // wins and the walk never runs. Built by hand here, in the shape the
            // dev layout actually has.
            WithTempTree(root =>
            {
                string projectDir = Path.Combine(root.FullName, "src", "GxMcp.Gateway");
                Directory.CreateDirectory(projectDir);
                File.WriteAllText(Path.Combine(projectDir, "tool_definitions.json"), "[]");
                string deep = Path.Combine(root.FullName, "a", "b", "c");
                Directory.CreateDirectory(deep);

                Assert.Equal(
                    Path.Combine(projectDir, "tool_definitions.json"),
                    ToolDefinitionsLocator.LocateFrom(deep));
            });
        }

        [Fact]
        public void TheUpwardWalkAlsoFindsTheFlattenedProjectLayout()
        {
            // The first candidate is "<dir>/GxMcp.Gateway/...", which is what a
            // checkout containing several projects looks like from inside one.
            WithTempTree(root =>
            {
                string gatewayDir = Path.Combine(root.FullName, "GxMcp.Gateway");
                Directory.CreateDirectory(gatewayDir);
                File.WriteAllText(Path.Combine(gatewayDir, "tool_definitions.json"), "[]");
                string deep = Path.Combine(root.FullName, "x", "y");
                Directory.CreateDirectory(deep);

                Assert.Equal(
                    Path.Combine(gatewayDir, "tool_definitions.json"),
                    ToolDefinitionsLocator.LocateFrom(deep));
            });
        }

        [Fact]
        public void BesideTheAssemblyWinsOverTheUpwardWalk()
        {
            // Precedence is the deployed contract: next to the binary is the file
            // that was shipped, and it must not be shadowed by a stale copy in the
            // source tree several levels up.
            WithTempTree(root =>
            {
                Directory.CreateDirectory(Path.Combine(root.FullName, "src", "GxMcp.Gateway"));
                File.WriteAllText(Path.Combine(root.FullName, "src", "GxMcp.Gateway", "tool_definitions.json"), "[]");
                string beside = Path.Combine(root.FullName, "src", "GxMcp.Gateway", "bin");
                Directory.CreateDirectory(beside);
                File.WriteAllText(Path.Combine(beside, "tool_definitions.json"), "[]");

                Assert.Equal(
                    Path.Combine(beside, "tool_definitions.json"),
                    ToolDefinitionsLocator.LocateFrom(beside));
            });
        }

        [Fact]
        public void TheWalkStopsAfterEightLevels()
        {
            // The cap is real behaviour, not an accident: past it, the search gives
            // up rather than walking a drive root.
            WithTempTree(root =>
            {
                string projectDir = Path.Combine(root.FullName, "src", "GxMcp.Gateway");
                Directory.CreateDirectory(projectDir);
                File.WriteAllText(Path.Combine(projectDir, "tool_definitions.json"), "[]");

                string deep = root.FullName;
                for (int i = 0; i < 10; i++)
                {
                    deep = Path.Combine(deep, "d" + i);
                    Directory.CreateDirectory(deep);
                }

                Assert.Null(ToolDefinitionsLocator.LocateFrom(deep));
            });
        }

        [Fact]
        public void AMissingFileYieldsNullAndAnUnusableRootDoesNotThrow()
        {
            WithTempTree(root =>
            {
                Assert.Null(ToolDefinitionsLocator.LocateFrom(Path.Combine(root.FullName, "nothing-here")));
                Assert.Null(ToolDefinitionsLocator.LocateFrom(string.Empty));
                Assert.Null(ToolDefinitionsLocator.LocateFrom("   "));
            });
        }

        private static void WithTempTree(Action<DirectoryInfo> body)
        {
            var root = new DirectoryInfo(Path.Combine(
                Path.GetTempPath(), "gxmcp-defs-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root.FullName);
            try { body(root); }
            finally
            {
                try { Directory.Delete(root.FullName, true); }
                catch { /* best effort */ }
            }
        }

        [Fact]
        public void TheLocatorReturnsAPathBothCallersCanRead()
        {
            // Both consumers do the same thing with the result: read it and parse
            // it. If the locator handed back a directory or an unreadable path,
            // they would fail identically and silently.
            string? located = ToolDefinitionsLocator.Locate();
            Assert.NotNull(located);
            Assert.True(File.Exists(located!));

            // Parse throws on malformed JSON, which fails the test directly - the
            // same thing both callers do with what the locator hands back.
            Assert.NotNull(Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(located!)));
        }

        [Fact]
        public void BothCallersGoThroughTheSharedLocator()
        {
            foreach (string file in new[] { "GatewayArgsValidator.cs", "ToolIdentity.cs" })
            {
                string src = RepoSource.Read("src", "GxMcp.Gateway", file);

                Assert.Contains("ToolDefinitionsLocator.Locate()", src);
                Assert.DoesNotContain("private static string? LocateToolDefinitions", src);
                // The upward walk belongs to the locator and nowhere else.
                Assert.DoesNotContain("AppContext.BaseDirectory", src);
            }
        }

        [Fact]
        public void NoComponentCarriesItsOwnCopyOfTheWalk()
        {
            string helper = RepoSource.Read("src", "GxMcp.Gateway", "Helpers", "ToolDefinitionsLocator.cs");

            Assert.Equal(1, SourceAssert.Count(helper, "internal static string? Locate()"));
            Assert.Contains("for (int i = 0; i < 8; i++)", helper);
            Assert.Contains("\"GxMcp.Gateway\", \"tool_definitions.json\"", helper);
            Assert.Contains("\"src\", \"GxMcp.Gateway\", \"tool_definitions.json\"", helper);

            foreach (string src in Directory.GetFiles(RepoSource.DirectoryOf("src", "GxMcp.Gateway"), "*.cs", SearchOption.AllDirectories))
            {
                if (src.Replace('\\', '/').EndsWith("/Helpers/ToolDefinitionsLocator.cs")) continue;
                if (Path.GetFileName(src) == "ToolDefinitionsLocatorTests.cs") continue;
                Assert.DoesNotContain("for (int i = 0; i < 8; i++)", File.ReadAllText(src));
            }
        }

        [Fact]
        public void TheLoaderStillCachesTheDefinitions()
        {
            // Both callers memoise into their own _toolDefs and return early once
            // set; the consolidation must not have turned a cached read into a
            // re-read per call.
            foreach (string file in new[] { "GatewayArgsValidator.cs", "ToolIdentity.cs" })
            {
                string src = RepoSource.Read("src", "GxMcp.Gateway", file);
                Assert.Contains("if (_toolDefs != null) return;", src);
            }
        }

        [Fact]
        public void McpRouterStillUsesItsOwnStrategy()
        {
            // Recorded, not changed: McpRouter reads the JSON from beside the
            // executing assembly with no upward walk, so in a layout where the file
            // is only under the project it loads nothing while the validator and
            // the identity check still work. Pointing it at the shared locator is a
            // behaviour change and needs its own decision.
            string router = RepoSource.Read("src", "GxMcp.Gateway", "McpRouter.cs");

            Assert.Contains("defPath = Path.Combine(exeDir, \"tool_definitions.json\")", router);
            Assert.DoesNotContain("ToolDefinitionsLocator.Locate();", router);
            Assert.Contains("THIRD way the Gateway looks for tool_definitions.json", router);
        }

    }
}
