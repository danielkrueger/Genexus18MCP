using System;
using System.Linq;
using GxMcp.TestSupport;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Every tool that takes an action ends on the same refusal for an action it
    /// does not serve: the same code, the same two sentences with the tool's name
    /// dropped in, and the same next step - the welcome card. Five handlers carried
    /// that written out in full.
    ///
    /// It matters more than most envelopes do, because this is the one shape that
    /// ends every failed discovery attempt. A caller that guessed an action gets
    /// told, in a fixed form, that nothing was written and where to look instead -
    /// and an agent reads it programmatically. Two handlers disagreeing about that
    /// means one of them is the odd one out, and which one is not obvious to
    /// whoever is reading the transcript afterwards.
    ///
    /// These call the production helper rather than restating the shape, so a
    /// change to the envelope is what they would notice.
    ///
    /// <para>
    /// One thing they deliberately do <em>not</em> assert is that the tools named in
    /// the hints are advertised in <c>tool_definitions.json</c>. Three of them -
    /// <c>genexus_orient</c>, <c>genexus_kbexplorer</c> and <c>genexus_blame</c> -
    /// are not, because they are dispatch method names rather than tool names, and
    /// the next step names one of them too. That is a live defect rather than
    /// intended shape, and encoding it here would make the eventual fix look like a
    /// regression. The set of names is pinned below instead, so renaming or adding a
    /// handler has to be deliberate.
    /// </para>
    /// </summary>
    public class DispatcherUnknownActionTests
    {
        /// <summary>
        /// The tools whose handlers end on the shared refusal, with the short name
        /// each one passes.
        /// </summary>
        public static TheoryData<string> Tools()
        {
            return new TheoryData<string> { "security", "orient", "kbexplorer", "navigation", "blame" };
        }

        [Theory]
        [MemberData(nameof(Tools))]
        public void TheRefusalNamesTheCodeTheActionAndTheTarget(string tool)
        {
            JObject refusal = JObject.Parse(CommandDispatcher.UnsupportedAction(tool, "Frobnicate", "MyObject"));

            // Everything about the failure lives under "error"; "target" is at the
            // envelope's top level because it says what was being addressed, not what
            // went wrong.
            JObject error = (JObject)refusal["error"];
            Assert.Equal("error", refusal["status"].Value<string>());

            Assert.Equal("UnknownAction", error["code"].Value<string>());
            Assert.Equal("Unsupported " + tool + " action 'Frobnicate'.", error["message"].Value<string>());
            Assert.Equal("Call genexus_" + tool + " with no action to see the supported list.", error["hint"].Value<string>());
            Assert.Equal("MyObject", refusal["target"].Value<string>());

            // Nothing else is asserted about the envelope: a refusal that quietly grew
            // a "persisted" or "retryable" field would change what a client infers
            // from it, and there is no such field today.
            Assert.Equal(new[] { "error", "status", "target" }, refusal.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }

        [Theory]
        [MemberData(nameof(Tools))]
        public void EveryRefusalOffersTheWelcomeCardAsItsNextStep(string tool)
        {
            JObject refusal = JObject.Parse(CommandDispatcher.UnsupportedAction(tool, "Frobnicate", null));

            var steps = (JArray)refusal["error"]["nextSteps"];
            Assert.NotNull(steps);
            Assert.Single(steps);

            // With no target the field is absent rather than present-and-null, so a
            // client testing for the key's existence and one testing its value agree.
            Assert.Null(refusal["target"]);
            Assert.False(refusal.ContainsKey("target"));

            JObject step = (JObject)steps[0];
            Assert.Equal("genexus_orient", step["tool"].Value<string>());
            Assert.Equal("Welcome card: KB info, recent edits, and top gotchas.", step["why"].Value<string>());
            Assert.NotNull(step["args"]);
            Assert.Empty((JObject)step["args"]);
        }

        /// <summary>
        /// The action is echoed back the way it arrived, not normalised, trimmed or
        /// reworded.
        ///
        /// Asserted because the echo is what a caller matches on to recognise their
        /// own mistake: a lowercased or trimmed echo would look almost right and stop
        /// matching. It also covers the translation pass <c>Err</c> runs over the
        /// message - English text has to come out the other side unchanged, and no
        /// <c>sourceMessage</c> fallback should be needed.
        /// </summary>
        [Fact]
        public void TheActionIsEchoedVerbatim()
        {
            JObject refusal = JObject.Parse(CommandDispatcher.UnsupportedAction("blame", "Get  History On\tX", null));

            Assert.Equal("Unsupported blame action 'Get  History On\tX'.", refusal["error"]["message"].Value<string>());

            // Absent means the message was not rewritten on the way out.
            Assert.Null(refusal["_meta"]);
        }

        /// <summary>
        /// Two refusals carry the same next step.
        /// </summary>
        /// <remarks>
        /// This reads as though it could prove the next step is not a shared mutable
        /// instance - mutate one response and see whether the other changes. It cannot.
        /// The helper returns a serialised JSON string and every caller re-parses it, so
        /// a single shared <c>JArray</c> behind that boundary is copied per response and
        /// any mutation here would be on a private copy. Confirmed by mutation: caching
        /// the array in a field left this test green.
        ///
        /// So what this actually proves is the content half - that the two refusals agree
        /// on the step - and the instance half is pinned structurally in
        /// <see cref="TheNextStepArrayIsNotHeldInAField"/>, which is where a shared field
        /// does get caught. Both halves are needed: content alone would pass for a single
        /// shared instance, and the absence of a field would pass for two hand-written
        /// copies that happen to match.
        /// </remarks>
        [Fact]
        public void TwoRefusalsCarryTheSameNextStep()
        {
            var first = JObject.Parse(CommandDispatcher.UnsupportedAction("blame", "A", null));
            var second = JObject.Parse(CommandDispatcher.UnsupportedAction("blame", "B", null));

            Assert.True(JToken.DeepEquals(first["error"]["nextSteps"], second["error"]["nextSteps"]));

            // And the echo of the action is the only thing that differs between them.
            Assert.NotEqual(first["error"]["message"].Value<string>(), second["error"]["message"].Value<string>());
        }

        /// <summary>
        /// The structural half: one refusal, used by every handler that has one.
        /// </summary>
        [Fact]
        public void EveryUnknownActionHandlerRoutesThroughTheOneRefusal()
        {
            string source = SourceAssert.NormaliseNewlines(RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "CommandDispatcher.cs")));

            // The envelope exists once and the code string is stated once.
            Assert.Equal(1, SourceAssert.Count(source, "internal static string UnsupportedAction(string tool, string action, string target)"));
            Assert.Equal(1, SourceAssert.Count(source, "code: \"UnknownAction\""));
            Assert.Equal(1, SourceAssert.Count(source, "message: $\"Unsupported {tool} action '{action}'.\""));
            Assert.Equal(1, SourceAssert.Count(source, "hint: $\"Call genexus_{tool} with no action to see the supported list.\""));

            // Five call sites, and the welcome card is written out only inside the
            // helper - a sixth handler that hand-rolls its next step would leave two
            // behind, which is what this counts.
            Assert.Equal(5, SourceAssert.Count(source, "return UnsupportedAction("));
            Assert.Equal(1, SourceAssert.Count(source, "why: \"Welcome card: KB info, recent edits, and top gotchas.\""));

            // And the names are pinned: a new handler using this helper, or an existing
            // one renamed, shows up as a diff here rather than as a silent change to
            // what a caller is told to call.
            foreach (var name in new[] { "security", "orient", "kbexplorer", "navigation", "blame" })
            {
                Assert.Equal(1, SourceAssert.Count(source, "return UnsupportedAction(\"" + name + "\", action, target);"));
            }
        }

        /// <summary>
        /// The dispatcher's own unknown-combination refusal keeps its own code, message
        /// and hint, and shares only the next step.
        ///
        /// It is a different condition - the pair named neither a known tool nor a
        /// known action, so it is not "this tool does not do that" - and collapsing
        /// the two would tell a caller to retry the same call against the same tool,
        /// which is what it just did.
        /// </summary>
        [Fact]
        public void TheUnknownCombinationRefusalKeepsItsOwnShape()
        {
            string source = SourceAssert.NormaliseNewlines(RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "CommandDispatcher.cs")));

            Assert.Equal(1, SourceAssert.Count(source, "code: \"UnknownMethodOrAction\""));
            Assert.Equal(1, SourceAssert.Count(source, "\"Unsupported dispatch combination. Method='{0}', Action='{1}'.\""));
            Assert.Equal(1, SourceAssert.Count(source, "\"Call genexus_help action=route goal=<intent> for the right tool, or genexus_orient for an overview.\""));

            // Both next steps come from one place: the declaration plus one call inside
            // the shared refusal plus this one. A fourth mention means somebody
            // hand-wrote another block.
            Assert.Equal(3, SourceAssert.Count(source, "OrientWelcomeNextSteps()"));
        }

        /// <summary>
        /// The next-step array is built per call, not held in a field.
        /// </summary>
        /// <remarks>
        /// This is the half that cannot be checked through the envelope. The helper
        /// returns serialised JSON, so a cached array is copied into every response and
        /// behaves identically from the outside - the behavioural tests above all stay
        /// green with one. A field would let a later caller mutate the steps a previous
        /// caller is still holding, which no amount of parsing the output would reveal.
        /// Hence the structural check.
        /// </remarks>
        [Fact]
        public void TheNextStepArrayIsNotHeldInAField()
        {
            string source = SourceAssert.NormaliseNewlines(RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "CommandDispatcher.cs")));

            Assert.DoesNotContain("static readonly JArray", source);
            Assert.DoesNotContain("static JArray _", source);
        }

    }
}
