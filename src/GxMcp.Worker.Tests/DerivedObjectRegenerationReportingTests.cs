using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #353: a write-path field stated a flat fact that turned out to be false.
    ///
    /// <para>
    /// The PatternInstance write reported <c>generatedObjectsRegenerated: false</c> for every
    /// non-WorkWithPlus pattern, from evidence recorded on GX17 U4 + K2BTools 13.1. Measuring
    /// the same operation on GX18 18.0.10.184260 showed the derived objects following the
    /// instance - three times, in both directions, the change landing in a generated control.
    /// So the field asserted something untrue about the environment it was answering in.
    /// </para>
    ///
    /// <para>
    /// The field is gone rather than flipped. These assert that it is gone, that the
    /// replacement refuses to carry a verdict either way, and that the two environments'
    /// contradictory evidence is both reported - because a caller on GX17 still needs the
    /// old behaviour to be expected, and a caller on GX18 must not be told the opposite.
    /// </para>
    /// </summary>
    public class DerivedObjectRegenerationReportingTests
    {
        private static string PatternWrite() =>
            SourceAssert.NormaliseNewlines(
                RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "WriteService.PatternWrite.cs"));

        [Fact]
        public void The_Flat_False_Verdict_Is_Gone()
        {
            string src = PatternWrite();

            Assert.False(src.Contains("generatedObjectsRegenerated"),
                "the field asserted a regeneration result nobody measured at this call site, "
                + "and it was falsified on GX18 - see docs/sdk-probe/workwith-regeneration-candidate.md");
        }

        [Fact]
        public void The_Replacement_Reports_Evidence_And_Explicitly_Declines_To_Measure()
        {
            string src = PatternWrite();

            Assert.True(src.Contains("derivedObjectRegeneration"),
                "callers need to be able to ask what is known about the generated objects");
            Assert.True(src.Contains("measuredAtThisCallSite"),
                "the replacement must say plainly that it did not measure");
            Assert.True(src.Contains("reapplyStillRefused"),
                "reporting the regeneration question must not imply reapply is now available");
        }

        [Fact]
        public void Both_Environments_Evidence_Is_Reported_So_Neither_Is_A_Default()
        {
            string src = PatternWrite();

            Assert.True(src.Contains("GX17 U4 + K2BTools 13.1"),
                "the original evidence must stay visible for callers on that combination");
            Assert.True(src.Contains("18.0.10.184260"),
                "the contradicting evidence must stay visible for callers on this one");
            Assert.True(src.Contains("regenerated"),
                "the contradicting observation must be stated, not merely implied by a version number");
        }

        [Fact]
        public void The_Verdict_Is_Not_Flipped_To_True_Either()
        {
            // Flipping the constant would be the same error in the other direction, and
            // would also be wrong on the GX17 U4 + K2BTools combination this build supports.
            //
            // Pinned as the literal's exact key set rather than by forbidding one spelling
            // of a verdict. A first attempt asserted only `derivedObjectRegeneration"] =
            // true` was absent, and a mutation that added `["verdict"] = true` to the same
            // object stayed green - the guard had a hole exactly where a future editor
            // would put it. Naming every permitted key closes that.
            string literal = ExtractRegenerationLiteral(PatternWrite());

            var keys = System.Text.RegularExpressions.Regex.Matches(literal, @"\[""(\w+)""\]")
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();

            // Ordinal order, so the expectation below is the exact key set rather than a
            // set-equality that would tolerate a rename.
            Assert.Equal(
                new[] { "contradictedOn", "measuredAtThisCallSite", "note", "reapplyStillRefused", "recordedEvidence" },
                keys);
        }

        /// <summary>
        /// The source text of the JObject literal assigned to <c>derivedObjectRegeneration</c>,
        /// from the opening brace to its close.
        /// </summary>
        private static string ExtractRegenerationLiteral(string src)
        {
            int at = src.IndexOf("derivedObjectRegeneration", StringComparison.Ordinal);
            Assert.True(at >= 0, "the replacement field must exist");
            int open = src.IndexOf("new JObject", at, StringComparison.Ordinal);
            Assert.True(open > at, "the field must be assigned a JObject literal");

            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0) return src.Substring(open, i - open + 1);
            }
            throw new InvalidOperationException("unbalanced JObject literal");
        }

        [Fact]
        public void The_Idle_Route_Is_Still_Recommended_For_A_Non_Follow()
        {
            // Narrowing the claim must not leave a caller with no recourse when their
            // objects genuinely do not follow.
            string src = PatternWrite();
            Assert.True(src.Contains("GeneXus IDE"),
                "the out-of-box route must stay in the warning");
        }

        [Fact]
        public void No_Surface_Still_Advertises_The_Removed_Field()
        {
            // A help string that still names the field teaches callers to branch on a
            // value the response no longer carries.
            foreach (var file in new[] { "ToolHelpCatalog.cs" })
            {
                string src = SourceAssert.NormaliseNewlines(
                    RepoSource.WithoutComments("src", "GxMcp.Gateway", file));
                Assert.False(src.Contains("generatedObjectsRegenerated"),
                    file + " still advertises the removed generatedObjectsRegenerated field");
            }
        }

        [Fact]
        public void The_Help_Text_Offers_Replacement_Guidance_Instead()
        {
            string src = SourceAssert.NormaliseNewlines(
                RepoSource.WithoutComments("src", "GxMcp.Gateway", "ToolHelpCatalog.cs"));

            Assert.True(src.Contains("derivedObjectRegeneration"),
                "the help must name what the response actually carries now");
            Assert.True(src.Contains("GeneXus IDE"),
                "and must keep the out-of-box route available");
        }
    }
}
