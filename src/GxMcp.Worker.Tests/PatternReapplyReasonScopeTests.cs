using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.TestSupport;
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
    /// <para>
    /// Serialized with <c>PatternApplyServiceTests</c> through the shared
    /// <c>InProcessSdkReflection</c> collection. Two of the tests here flip
    /// <c>GenericReapplySupported</c> / <c>GenericFirstApplySupported</c> to exercise a
    /// rejection, and so does that class. xunit runs classes in parallel by default, and
    /// <c>IsSupported</c> reads the switch twice - once for the verdict and once, via
    /// <c>ShouldNameUntestedCandidate</c>, to decide whether to attach the candidate - so a
    /// concurrent flip can return a rejection with no candidate text attached. That is
    /// exactly what happened: every test here passed alone, and
    /// <c>The_Refusal_Records_That_It_Was_Re_Measured_Not_Inherited</c> failed only in the
    /// full run.
    /// </para>
    /// </summary>
    [Collection("InProcessSdkReflection")]
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

        [Fact]
        public void The_Refusal_Records_That_It_Was_Re_Measured_Not_Inherited()
        {
            // The original evidence was GX17 U4 + K2BTools 13.1. Repeating it on GX18
            // without K2BTools is what turns an inherited claim into a confirmed one, and a
            // caller deciding whether to trust this refusal needs to know which it is.
            string reason = GenericReason(PatternApplyService.PatternRoute.Reapply);

            Assert.True(reason.Contains("18.0.10.184260"),
                "the reason must record the combination the refusal was re-measured on, got: " + reason);
            Assert.True(reason.Contains("without K2BTools"),
                "and must say the second measurement did not depend on K2BTools, got: " + reason);
            Assert.True(reason.Contains("confirmed rather than inherited"),
                "the reason must distinguish a re-measured refusal from an inherited one, got: " + reason);
        }

        [Fact]
        public void The_Refusal_Explains_Why_The_Nre_Is_Not_A_Package_Problem()
        {
            // Measured: reapply with no settings NREs because this path passes a null
            // ApplySettings. Telling a caller to check licensing sends them after the wrong
            // thing, and the throw is the first thing they see.
            string reason = GenericReason(PatternApplyService.PatternRoute.Reapply);

            Assert.True(reason.Contains("null-ApplySettings"),
                "the reason must name the actual cause of the NullReferenceException, got: " + reason);
            Assert.True(reason.Contains("stays refused instead of"),
                "the reason must say why the route is not simply 'fixed', got: " + reason);
        }

        [Fact]
        public void The_Apply_Failure_Hint_Stops_Blaming_Licensing_For_The_Nre()
        {
            // Same defect on the other surface: the PatternEngineApplyFailed hint said
            // "Verify the pattern package is installed and the KB is open" for an exception
            // this build causes itself.
            string src = SourceAssert.NormaliseNewlines(
                RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "PatternApplyService.cs"));

            // The condition itself, not just the identifier's presence. A guard that only
            // checked `nullSettingsNre` appears in the source stayed green when the
            // variable was assigned `false` - the branch became dead code and the
            // licensing advice came straight back. Asserting the exact predicate means a
            // disabled branch has to be retyped to hide.
            Assert.True(
                src.Contains("reapply && ex is NullReferenceException"),
                "the NRE branch must be gated on exactly 'reapply && ex is NullReferenceException'; "
                + "anything looser explains away real package failures, anything stricter "
                + "misses this one. Source has: " + Around(src, "nullSettingsNre = "));
            Assert.True(src.Contains("nullApplySettings"),
                "and reported with a machine-readable cause rather than only prose");
            // Asserted on a phrase that survives the string concatenation, not on the
            // sentence around it: a first attempt matched "not a package or licensing
            // problem", which the source splits across a `+` and so never appears whole.
            Assert.True(src.Contains("licensing problem"),
                "the NRE branch must retract the licensing advice");
            // And the genuine-failure branch must still exist, or every apply failure
            // would be explained away as our own null settings.
            Assert.True(src.Contains("package is installed and the KB is open"),
                "the non-NRE branch must keep its original package-and-KB advice");
        }

        private static string Around(string src, string needle)
        {
            int at = src.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0) return "<not found>";
            int start = Math.Max(0, at - 10);
            return src.Substring(start, Math.Min(90, src.Length - start)).Replace("\n", " ").Replace("\r", " ");
        }

        [Fact]
        public void The_Apply_Failure_Next_Step_Points_At_The_Route_That_Works()
        {
            // A refusal that leaves the caller with no route is half a refusal. The
            // instance-save path is the one measured to regenerate on GX18.
            string src = SourceAssert.NormaliseNewlines(
                RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "PatternApplyService.cs"));

            Assert.True(src.Contains("\"genexus_edit\""),
                "the NRE next step must offer the measured route, not a blind retry");
            Assert.True(src.Contains("Saving the instance is the route measured to regenerate"),
                "and must say why that step is the one to take");
        }
    }
}
