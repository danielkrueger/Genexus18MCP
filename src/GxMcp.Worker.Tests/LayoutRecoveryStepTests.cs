using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Every layout failure offers the same recovery: re-read this object's layout
    /// tree. That instruction was written out 36 times across
    /// <c>LayoutService</c>'s five partials, always naming <c>genexus_layout</c>
    /// with <c>action=get_tree</c> and <c>name=target</c>, differing only in the
    /// prose explaining what the caller was checking.
    ///
    /// The prose is per-call-site and stays at the call site. The instruction is
    /// not: a step that drifted to <c>inspect_surface</c>, or lost its
    /// <c>name</c>, would send the caller somewhere the other 35 do not, and the
    /// difference would read as deliberate.
    /// </summary>
    public class LayoutRecoveryStepTests
    {
        private static readonly string[] LayoutPartials =
        {
            "LayoutService.cs",
            "LayoutService.MutatorScan.cs",
            "LayoutService.ReportControls.cs",
            "LayoutService.SourcePersistence.cs",
            "LayoutService.VisualContext.cs",
        };

        [Fact]
        public void TheStepNamesLayoutGetTreeForTheTarget()
        {
            var step = LayoutService.LayoutGetTreeStep("OrdersForm", "why it matters");

            Assert.Equal("genexus_layout", step["tool"]?.ToString());
            Assert.Equal("get_tree", step["args"]?["action"]?.ToString());
            Assert.Equal("OrdersForm", step["args"]?["name"]?.ToString());
            Assert.Equal("why it matters", step["why"]?.ToString());
        }

        [Fact]
        public void TheStepCarriesNoOtherArguments()
        {
            // The call sites passed exactly action+name. An extra argument here
            // would be sent to a tool that never received it before.
            var step = LayoutService.LayoutGetTreeStep("OrdersForm", "why");

            Assert.Equal(new[] { "action", "name" },
                ((JObject)step["args"]).Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public void TheExplanationIsPassedThroughVerbatim()
        {
            // The per-site prose is the part that legitimately differs; the helper
            // must not normalise, trim or reword it.
            const string why = "Re-reads the layout to confirm the control still exists.";

            Assert.Equal(why, LayoutService.LayoutGetTreeStep("X", why)["why"]?.ToString());
        }

        [Fact]
        public void TheTargetIsCarriedThroughUnchanged()
        {
            // An empty or odd target still has to reach the step: the guard is that
            // it is passed, not that it is validated here.
            foreach (string target in new[] { "OrdersForm", "", "name with spaces" })
                Assert.Equal(target, LayoutService.LayoutGetTreeStep(target, "why")["args"]?["name"]?.ToString());
        }

        [Fact]
        public void EveryLayoutPartialReachesTheHelperInsteadOfWritingTheStepOut()
        {
            var sources = new Dictionary<string, string>();
            foreach (string file in LayoutPartials)
                sources[file] = RepoSource.Read("src", "GxMcp.Worker", "Services", file);

            int totalCallSites = 0;
            foreach (var pair in sources)
            {
                string src = pair.Value;
                int calls = CountOccurrences(src, "LayoutGetTreeStep(");
                totalCallSites += calls;

                // The declaration is the only place allowed to spell the instruction
                // out; anywhere else it is an un-migrated copy.
                int spelledOut = CountOccurrences(src, "[\"action\"] = \"get_tree\"");
                int expectedSpelledOut = pair.Key == "LayoutService.cs" ? 1 : 0;

                Assert.True(spelledOut <= expectedSpelledOut,
                    pair.Key + " spells out genexus_layout get_tree " + spelledOut
                    + " time(s); only the helper itself may");
                Assert.Equal(calls, CountOccurrences(src, "LayoutGetTreeStep(target,") + (pair.Key == "LayoutService.cs" ? 1 : 0));
            }

            // The point of the consolidation: the step used to be written out 36
            // times, so a regression below this count means the migration was
            // undone rather than merely extended.
            Assert.True(totalCallSites >= 37,
                "expected at least 36 migrated call sites plus the declaration, found " + totalCallSites);
        }

        [Fact]
        public void NoOtherLayoutStepWasDisturbed()
        {
            // inspect_surface is a different instruction with different prose and
            // stays written out - this migration must not have swept it up.
            int inspectSurface = 0;
            foreach (string file in LayoutPartials)
            {
                string src = RepoSource.Read("src", "GxMcp.Worker", "Services", file);
                inspectSurface += CountOccurrences(src, "\"action\"] = \"inspect_surface\"");
                Assert.DoesNotContain("LayoutGetTreeStep(target, \"Diagnoses which visual", src);
            }

            Assert.True(inspectSurface >= 3,
                "expected the inspect_surface step to survive, found " + inspectSurface);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

    }
}