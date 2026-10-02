using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #353: the reapply rejection claimed more than the evidence supports.
    ///
    /// <para>
    /// The switch is driven by a real measurement - two named overloads, on GX17 U4 with
    /// K2BTools 13.1 - but the reason string it produced read "through the pattern engine
    /// is not supported by this MCP build: headless reapply does not regenerate the
    /// pattern's objects". That is a statement about the engine, and the issue's reporter
    /// correctly read it as a dead end. It is not one: a third entry point exists.
    /// </para>
    ///
    /// <para>
    /// So these assert the <b>shape</b> of the claim rather than its wording. Wording
    /// alone would fail on a copy-edit and catch nothing, and a substring assertion that
    /// only forbids the old sentence would happily pass the day somebody replaced it with
    /// a different over-broad one. What is pinned here is that the reason names the
    /// overloads it actually measured, and that the unmeasured candidate is reported as a
    /// lead with an explicit unverified status instead of being silently absent.
    /// </para>
    /// </summary>
    public class PatternReapplyReasonScopeTests
    {
        private static string GenericReason(PatternApplyService.PatternRoute route)
        {
            string reason;
            Assert.False(PatternApplyService.PatternRouteCapabilities.IsSupported(
                null, route, out reason));
            Assert.False(string.IsNullOrEmpty(reason), "a rejection must carry a reason");
            return reason;
        }

        [Fact]
        public void The_Rejection_Names_The_Overloads_It_Actually_Measured()
        {
            string reason = GenericReason(PatternApplyService.PatternRoute.Reapply);

            Assert.True(reason.Contains("ApplyPattern overloads"),
                "the reason must scope itself to the overloads that were measured, got: " + reason);
            Assert.True(reason.Contains("ApplyPattern(PatternInstance, ApplySettings)"),
                "the reason must name the overload that threw, got: " + reason);
            Assert.True(reason.Contains("ApplyPattern(KBObject, PatternDefinition)"),
                "the reason must name the overload that only re-saved, got: " + reason);
        }

        [Fact]
        public void The_Rejection_Records_Which_Combination_Was_Measured()
        {
            // A negative result that does not say what it covers cannot be told apart from
            // the next one, which is how this issue had to be re-asked.
            string reason = GenericReason(PatternApplyService.PatternRoute.Reapply);

            Assert.True(reason.Contains("GX17") && reason.Contains("K2BTools"),
                "the reason must record the combination the evidence came from, got: " + reason);
        }

        [Fact]
        public void The_Rejection_Disclaims_The_Blanket_Engine_Claim()
        {
            string reason = GenericReason(PatternApplyService.PatternRoute.Reapply);

            // The specific over-broad claim that #353 was filed about.
            Assert.False(reason.Contains("through the pattern engine is not supported"),
                "the reason must not present the measurement as a property of the whole "
                + "engine, got: " + reason);

            // And the same over-claim in its other grammatical form.
            Assert.False(reason.Contains("headless reapply does not regenerate"),
                "the reason must not assert that reapply can never regenerate, got: " + reason);
        }

        [Fact]
        public void The_Rejection_Names_The_Untested_Candidate_And_Says_It_Is_Unverified()
        {
            string reason = GenericReason(PatternApplyService.PatternRoute.Reapply);

            Assert.Contains(PatternApplyService.PatternRouteCapabilities.UntestedRegenerationEntryPoint,
                reason, StringComparison.Ordinal);
            Assert.Contains("unverified", reason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("does not call", reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_Rejection_Keeps_The_Out_Of_Box_Route()
        {
            // Narrowing the claim must not turn a refusal into a shrug. The IDE route is
            // the one thing known to work, so it stays in the sentence.
            Assert.Contains("GeneXus IDE", GenericReason(PatternApplyService.PatternRoute.Reapply),
                StringComparison.Ordinal);
        }

        [Fact]
        public void A_First_Apply_Rejection_Does_Not_Advertise_A_Regeneration_Candidate()
        {
            // Different problem, different remedy. Advertising the reapply candidate on a
            // first-apply refusal would point at the wrong route, so this has to be
            // checked against a real first-apply rejection - which means turning that
            // switch off, since it is on today and would otherwise short-circuit before
            // a reason exists.
            bool original = PatternApplyService.PatternRouteCapabilities.GenericFirstApplySupported;
            try
            {
                PatternApplyService.PatternRouteCapabilities.GenericFirstApplySupported = false;

                Assert.False(PatternApplyService.PatternRouteCapabilities.ShouldNameUntestedCandidate(
                    PatternApplyService.PatternRoute.FirstApply));

                string reason = GenericReason(PatternApplyService.PatternRoute.FirstApply);
                Assert.False(reason.Contains(
                        PatternApplyService.PatternRouteCapabilities.UntestedRegenerationEntryPoint),
                    "first apply must not advertise a reapply candidate, got: " + reason);
                Assert.False(reason.Contains("unverified"),
                    "first apply carries no unmeasured regeneration claim at all, got: " + reason);
            }
            finally
            {
                PatternApplyService.PatternRouteCapabilities.GenericFirstApplySupported = original;
            }
        }

        [Fact]
        public void The_Candidate_Is_Only_Named_While_Reapply_Is_Unsupported()
        {
            bool original = PatternApplyService.PatternRouteCapabilities.GenericReapplySupported;
            try
            {
                PatternApplyService.PatternRouteCapabilities.GenericReapplySupported = true;
                Assert.False(PatternApplyService.PatternRouteCapabilities.ShouldNameUntestedCandidate(
                    PatternApplyService.PatternRoute.Reapply),
                    "a supported route has no rejection to annotate");

                string reason;
                Assert.True(PatternApplyService.PatternRouteCapabilities.IsSupported(
                    null, PatternApplyService.PatternRoute.Reapply, out reason));
                Assert.True(string.IsNullOrEmpty(reason), "a supported route carries no reason");
            }
            finally
            {
                PatternApplyService.PatternRouteCapabilities.GenericReapplySupported = original;
            }
        }

        [Fact]
        public void The_Capability_Envelope_Reports_The_Candidate_Beside_The_Verdict()
        {
            var json = PatternApplyService.PatternRouteCapabilities.ToJson(null);
            var reapply = (JObject)json["reapply"];

            Assert.False(reapply.Value<bool>("supported"));
            Assert.NotNull(reapply["reason"]);

            // Beside the verdict, so a client branching on `supported` is unaffected.
            Assert.Equal(
                PatternApplyService.PatternRouteCapabilities.UntestedRegenerationEntryPoint,
                reapply.Value<string>("unverifiedRegenerationCandidate"));
            Assert.Contains("never called",
                reapply.Value<string>("candidateStatus"), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_First_Apply_Envelope_Entry_Is_Left_Alone()
        {
            var json = PatternApplyService.PatternRouteCapabilities.ToJson(null);
            var firstApply = (JObject)json["firstApply"];

            Assert.True(firstApply.Value<bool>("supported"));
            Assert.Null(firstApply["unverifiedRegenerationCandidate"]);
            Assert.Null(firstApply["candidateStatus"]);
        }

        [Fact]
        public void The_Switch_Is_Still_Off()
        {
            // #353 asked for a candidate, not for the gate to be flipped. This pins that
            // answering the question did not quietly enable the route.
            Assert.False(PatternApplyService.PatternRouteCapabilities.GenericReapplySupported,
                "reapply stays unsupported until an experiment proves the candidate; none "
                + "has been run, because no KB with a standard WorkWith exists on this machine");
            Assert.True(PatternApplyService.PatternRouteCapabilities.GenericFirstApplySupported);
        }

        [Fact]
        public void The_Candidate_Name_Is_A_Real_Engine_Member_And_Not_A_Description()
        {
            // Pinned as a dotted member so it cannot decay into prose that a reader might
            // take for a verified call. The name itself is asserted against the SDK when
            // the probe runs; here it just has to stay a member-shaped string.
            var name = PatternApplyService.PatternRouteCapabilities.UntestedRegenerationEntryPoint;

            Assert.Contains("PatternEngine", name, StringComparison.Ordinal);
            Assert.Contains("GenerateInstanceObjects", name, StringComparison.Ordinal);
            Assert.DoesNotContain(" ", name);
            Assert.Equal(name, name.Trim());
        }
    }
}
