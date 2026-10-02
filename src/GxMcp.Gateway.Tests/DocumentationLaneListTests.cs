using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Guards the copy-pasteable validation-lane block that ships in both
    /// <c>AGENTS.md</c> and <c>CODING_STANDARDS.md</c>.
    ///
    /// <para>Two failure modes, both of which only show up after someone pastes the
    /// block into a terminal:</para>
    ///
    /// <list type="number">
    /// <item><description>The block used to start with <c>.\build.ps1</c> while the
    /// prose nine lines below it said to run it <em>last</em>. Following the block
    /// rebuilds the Worker outside the <c>x86</c> platform the solution maps it to,
    /// which breaks the publish&#8596;source byte identity, so
    /// <c>Get-GxMcpReleaseProcessSmokeFingerprint</c> comes back empty and
    /// <c>test-release-preflight.ps1</c> fails for a reason unrelated to the change
    /// under test. The ordering is asserted here so a reordering is a red test
    /// instead of a confusing release failure.</description></item>
    /// <item><description>The block omitted the Nexus-IDE lane and the
    /// operation-contract-inventory check, both of which CI runs. The built
    /// <c>.vsix</c> is a required release asset that <c>release.yml</c> byte-compares
    /// against the copy inside <c>publish.zip</c>, so "the documented lanes are
    /// green" was weaker than it read.</description></item>
    /// </list>
    ///
    /// <para><b>Why the search is scoped to a fenced block.</b> Both documents name
    /// <c>build.ps1</c> in prose several times outside any fence. A locator that took
    /// the first <c>.\build.ps1</c> in the file would assert against a sentence, and
    /// would keep asserting against a sentence as prose is edited - passing or
    /// failing for reasons unrelated to the lane block. The locator below finds the
    /// fenced code block that <em>runs</em> the script, requires exactly one such
    /// block, and only then looks at the order inside it.</para>
    /// </summary>
    public class DocumentationLaneListTests
    {
        private const string BuildCommand = ".\\build.ps1";

        /// <summary>Documents that carry the lane block, by repo-root-relative name.</summary>
        public static IEnumerable<object[]> LaneDocuments =>
            new[] { new object[] { "AGENTS.md" }, new object[] { "CODING_STANDARDS.md" } };

        [Theory]
        [MemberData(nameof(LaneDocuments))]
        public void LaneBlock_RunsBuildScriptLast(string fileName)
        {
            var block = SingleLaneBlock(fileName);

            List<int> commandLines = Enumerable.Range(0, block.Body.Count)
                .Where(i => IsCommand(block.Body[i]))
                .ToList();
            Assert.True(
                commandLines.Count > 0,
                $"The lane block in {fileName} (fence at line {block.StartLine}) has no command lines.");

            List<int> buildLines = commandLines
                .Where(i => block.Body[i].TrimStart().StartsWith(BuildCommand, StringComparison.Ordinal))
                .ToList();
            Assert.True(
                buildLines.Count == 1,
                $"Expected exactly one '{BuildCommand}' command line in the {fileName} lane block, found {buildLines.Count}.");

            int buildIndex = buildLines[0];
            Assert.True(
                buildIndex == commandLines[commandLines.Count - 1],
                $"'{BuildCommand}' must be the last command in the {fileName} lane block (fence at line "
                + $"{block.StartLine}); it is entry {commandLines.IndexOf(buildIndex) + 1} of {commandLines.Count}. "
                + "A solution-level 'dotnet build'/'dotnet test' after it rebuilds the Worker outside the x86 "
                + "platform the solution maps it to, breaking the publish<->source byte identity that "
                + "test-release-preflight.ps1 depends on.");

            List<string> dotnetAfter = commandLines
                .Where(i => i > buildIndex)
                .Where(i => StartsWithCommand(block.Body[i], "dotnet build")
                         || StartsWithCommand(block.Body[i], "dotnet test"))
                .Select(i => block.Body[i].Trim())
                .ToList();
            Assert.True(
                dotnetAfter.Count == 0,
                $"The {fileName} lane block runs a .NET build/test lane after '{BuildCommand}': "
                + string.Join(" | ", dotnetAfter));
        }

        [Theory]
        [MemberData(nameof(LaneDocuments))]
        public void LaneBlock_NamesInventoryCheckAndNexusLane(string fileName)
        {
            var block = SingleLaneBlock(fileName);
            string blockText = string.Join("\n", block.Body);

            Assert.True(
                blockText.Contains("generate-operation-contract-inventory.py", StringComparison.Ordinal),
                $"The {fileName} lane block must run python scripts/generate-operation-contract-inventory.py --check; "
                + "CI runs it and a stale generated inventory passes every other lane.");

            Assert.True(
                blockText.Contains("nexus-ide", StringComparison.OrdinalIgnoreCase),
                $"The {fileName} lane block must include the Nexus-IDE lane (Push-Location src\\nexus-ide; npm ci; "
                + "npm run compile; npm run lint; npm test). The .vsix it builds is a required release asset that "
                + "release.yml byte-compares against the copy inside publish.zip, so it is not optional.");
        }

        /// <summary>A fenced code block: the 1-based line of its opening fence, and its body lines.</summary>
        private readonly struct FencedBlock
        {
            internal FencedBlock(int startLine, List<string> body)
            {
                StartLine = startLine;
                Body = body;
            }

            internal int StartLine { get; }

            internal List<string> Body { get; }
        }

        /// <summary>
        /// The one fenced block in <paramref name="fileName"/> that runs
        /// <c>.\build.ps1</c>. Ambiguity is a failure, not something to resolve by
        /// picking the first match: a second candidate means the locator and the
        /// document must be fixed together.
        /// </summary>
        private static FencedBlock SingleLaneBlock(string fileName)
        {
            string[] lines = RepoSource.Read(fileName).Replace("\r\n", "\n").Split('\n');

            List<FencedBlock> candidates = ReadFencedBlocks(lines)
                .Where(b => b.Body.Any(line => line.TrimStart().StartsWith(BuildCommand, StringComparison.Ordinal)))
                .ToList();

            Assert.True(
                candidates.Count == 1,
                $"Expected exactly one fenced code block in {fileName} that runs '{BuildCommand}', found "
                + $"{candidates.Count}. These assertions are scoped to that block; if it was split, merged or "
                + "renamed, update the document and this locator in the same change.");

            return candidates[0];
        }

        private static List<FencedBlock> ReadFencedBlocks(IReadOnlyList<string> lines)
        {
            var blocks = new List<FencedBlock>();

            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (!trimmed.StartsWith("```", StringComparison.Ordinal)) continue;

                int bodyStart = i + 1;
                int close = bodyStart;
                while (close < lines.Count && !lines[close].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    close++;
                }

                blocks.Add(new FencedBlock(i + 1, lines.Skip(bodyStart).Take(close - bodyStart).ToList()));
                i = close;
            }

            return blocks;
        }

        /// <summary>A block line an operator would execute: not blank, not a comment.</summary>
        private static bool IsCommand(string line)
        {
            string trimmed = line.TrimStart();
            return trimmed.Length > 0 && !trimmed.StartsWith("#", StringComparison.Ordinal);
        }

        private static bool StartsWithCommand(string line, string command)
        {
            string trimmed = line.TrimStart();
            return trimmed.StartsWith(command, StringComparison.Ordinal)
                && (trimmed.Length == command.Length
                    || char.IsWhiteSpace(trimmed[command.Length]));
        }
    }
}