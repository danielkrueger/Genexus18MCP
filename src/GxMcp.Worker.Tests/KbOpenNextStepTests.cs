using System;
using System.Linq;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// When a call cannot proceed because no KB is open, the refusal points the caller
    /// at the same thing: <c>genexus_kb</c> with <c>action=open</c>. Six tools were
    /// each writing that step out by hand, in four different formats.
    ///
    /// The tool name and the action are not the caller's to choose. A next step naming
    /// a tool that does not exist, or spelling the action differently, strands exactly
    /// the caller who is already lost - and the caller here is an agent choosing its
    /// next move from what the tool told it. So those two facts are now stated once,
    /// and the parts that are genuinely per-tool stay at the call sites.
    ///
    /// What stays per-tool is the substance, not the boilerplate: the <c>why</c>, which
    /// is what makes the step usable rather than generic, and any extra arguments a
    /// tool wants to show. "Open the target KB before calling db_info" and "before
    /// deleting" tell the agent something; "Open a KB" does not.
    /// </summary>
    public class KbOpenNextStepTests
    {
        [Fact]
        public void TheStepAlwaysNamesGenexusKbAndTheOpenAction()
        {
            JObject step = KbOpenNextStep.Step("because");

            Assert.Equal("genexus_kb", step["tool"].Value<string>());
            Assert.Equal("open", step["args"]["action"].Value<string>());
            Assert.Equal("because", step["why"].Value<string>());
        }

        [Fact]
        public void TheWhyIsCarriedThroughUnchanged()
        {
            // The one field that is per-tool, so it has to survive verbatim - including
            // the four different tools' wordings and the one that is a sentence about
            // the KB rather than about the caller.
            foreach (string why in new[]
            {
                "Open a KB.",
                "Open the target KB before calling db_info.",
                "Open the target KB before deleting.",
                "Open a KB before calling diff.",
                "Opens the configured Knowledge Base.",
            })
            {
                Assert.Equal(why, KbOpenNextStep.Step(why)["why"].Value<string>());
            }
        }

        [Fact]
        public void ExtraArgumentsAreMergedIntoTheStepArgs()
        {
            // db_info shows the caller the shape of the call it could have made, which
            // the others do not. The action must survive alongside it.
            JObject step = KbOpenNextStep.Step("why", new JObject { ["path"] = "<kb path>" });

            Assert.Equal("open", step["args"]["action"].Value<string>());
            Assert.Equal("<kb path>", step["args"]["path"].Value<string>());
            Assert.Equal(2, Args(step).Properties().Count());
        }

        [Fact]
        public void ExtraArgumentsCannotOverwriteTheAction()
        {
            // Merged with the action winning, so a caller passing an "action" of its own
            // cannot produce a step that asks for something other than opening a KB -
            // which would defeat the point of sharing it.
            JObject step = KbOpenNextStep.Step("why", new JObject { ["action"] = "delete" });

            Assert.Equal("open", step["args"]["action"].Value<string>());
            Assert.Single(Args(step).Properties());
        }

        [Fact]
        public void NoExtraArgumentsMeansOnlyTheAction()
        {
            // Checked separately because "extra args absent" and "extra args merged"
            // take different branches, and a step that grew a null-valued extra field
            // would be a change in what a client reads.
            Assert.Single(Args(KbOpenNextStep.Step("why")).Properties());
        }

        /// <summary>
        /// Every tool that refuses for want of an open KB goes through the shared step,
        /// and none writes its own.
        /// </summary>
        [Fact]
        public void EveryToolOfferingToOpenTheKbUsesTheSharedStep()
        {
            string helper = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Helpers", "KbOpenNextStep.cs"));
            Assert.Equal(1, SourceAssert.Count(helper, "internal static class KbOpenNextStep"));
            Assert.Equal(1, SourceAssert.Count(helper, "internal static JObject Step(string why, JObject extraArgs = null)"));

            // The tool name and the action, stated once each.
            Assert.Equal(1, SourceAssert.Count(helper, "var args = new JObject { [\"action\"] = \"open\" };"));
            Assert.Equal(1, SourceAssert.Count(helper, "McpResponse.NextStep(\"genexus_kb\", args, why)"));

            // Every tool that offers it, in one place. Counted from the call sites
            // rather than listed, so a seventh tool shows up as a diff here.
            var callers = new[]
            {
                "DatabaseInfoService.cs",
                "GeneratedDiffService.cs",
                "LayoutService.cs",
                "LearningReportService.cs",
                "MultiAgentLockService.cs",
                "ObjectService.cs",
            };

            foreach (string file in callers)
            {
                string source = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", file));

                Assert.True(SourceAssert.Count(source, "KbOpenNextStep.Step(") == 1, file + ": call site");
                Assert.True(source.IndexOf("NextStep(\"genexus_kb\", new JObject", StringComparison.Ordinal) < 0, file + ": writes its own step");
                Assert.True(source.IndexOf("new JObject { [\"action\"] = \"open\" }", StringComparison.Ordinal) < 0, file + ": spells the args out again");
            }

            // And nothing anywhere else still writes this step by hand - the six callers
            // above are the whole set. This is the check that would have caught the two
            // sites that use a different format from the other four.
            //
            // The needle looks for the hand-written shape specifically - a second
            // argument built inline as a `new JObject`. The helper passes a variable,
            // so it does not match itself, which is better than excluding the file by
            // name: this asserts the defect's actual shape instead of whitelisting
            // whatever happens to be allowed to contain it.
            foreach (string path in AllProductionFiles())
            {
                string source = RepoSource.WithoutComments(System.IO.File.ReadAllText(path));
                Assert.True(source.IndexOf("NextStep(\"genexus_kb\", new JObject", StringComparison.Ordinal) < 0,
                    System.IO.Path.GetFileName(path) + ": a hand-written open-KB step survived");
            }
        }

        /// <summary>
        /// The refusals themselves are deliberately not shared, and this pins why.
        /// </summary>
        /// <remarks>
        /// The same condition is reported under two different codes - <c>NoKbOpen</c> in
        /// four services and <c>KbNotOpen</c> in four others - with nine spellings of
        /// the message between them. That is a real inconsistency and it is not fixed
        /// here: a client matches on the code, so collapsing the two would change what
        /// existing callers see. It is recorded in the helper's remarks as the reason
        /// only the next step is shared.
        /// </remarks>
        [Fact]
        public void TheRefusalCodesAndWordingAreLeftAloneOnPurpose()
        {
            string helper = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Helpers", "KbOpenNextStep.cs"));

            // The helper builds a step, not an envelope: no code, no message, no hint.
            // If it ever grew one, it would be about to become the shared refusal.
            Assert.Equal(0, SourceAssert.Count(helper, "McpResponse.Err("));
            Assert.Equal(0, SourceAssert.Count(helper, "code:"));
            Assert.Equal(0, SourceAssert.Count(helper, "hint:"));

            // And both codes are still in use, so the decision is visibly a decision
            // rather than something that quietly resolved itself.
            Assert.True(SourceFilesWith("NoKbOpen").Length >= 4);
            Assert.True(SourceFilesWith("KbNotOpen").Length >= 4);
        }

        /// <summary>The step's args as an object, rather than the JToken the indexer returns.</summary>
        private static JObject Args(JObject step)
        {
            return (JObject)step["args"];
        }

        /// <summary>
        /// Every production file in both directories a refusal or a step can live in.
        /// </summary>
        /// <remarks>
        /// Enumerated rather than listed by name, because a hand-maintained list is how
        /// this assertion first came out wrong: it claimed four <c>NoKbOpen</c> sites
        /// while scanning only <c>Services</c>, and the fourth is in <c>KbModelGuard</c>
        /// under <c>Helpers</c>. A directory a fixed list forgets is exactly the case
        /// that fails silently.
        /// </remarks>
        private static string[] AllProductionFiles()
        {
            return RepoSource.CsFilesIn("src", "GxMcp.Worker", "Services")
                .Concat(RepoSource.CsFilesIn("src", "GxMcp.Worker", "Helpers"))
                .ToArray();
        }

        /// <summary>The production files that still report the given code.</summary>
        private static string[] SourceFilesWith(string code)
        {
            return AllProductionFiles()
                .Where(f => System.IO.File.ReadAllText(f).Contains("code: \"" + code + "\""))
                .ToArray();
        }

    }
}
