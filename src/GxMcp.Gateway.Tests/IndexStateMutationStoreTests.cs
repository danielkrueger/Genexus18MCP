using System;
using GxMcp.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// An index-state snapshot lives in the per-KB map when its alias resolves and
    /// in a single global field when it does not. Two writers resolved that
    /// location, loaded-or-created the snapshot, mutated it and wrote it back -
    /// all twelve lines of it, verbatim, twice.
    ///
    /// It is now <c>MutateLastKnownIndexState</c>, and these tests assert the thing
    /// that motivated the extraction rather than the shape of the code: that a
    /// diagnostic update lands on the snapshot for the alias it was given, and
    /// nowhere else. A writer that read from one place and wrote to the other
    /// would surface as a KB that never stops reporting a stale index - no
    /// exception, no log line, just an index that never becomes usable - so it is
    /// worth pinning behaviourally through <c>BuildIndexBlockForTest</c>.
    /// </summary>
    public class IndexStateMutationStoreTests : IDisposable
    {
        public IndexStateMutationStoreTests()
        {
            Program.ResetIndexStateMirrorForTest();
        }

        public void Dispose()
        {
            Program.ResetIndexStateMirrorForTest();
        }

        private static JObject IndexBlock(string? kbAlias) => Program.BuildIndexBlockForTest(kbAlias);

        /// <summary>
        /// An absent field is emitted as an explicit JSON null rather than omitted,
        /// so "not set" is a JValue of type Null - not a missing token. Asserting
        /// Assert.Null on the token would pass for a missing key and fail for a
        /// null one, which is the wrong way round for a test about where state
        /// landed.
        /// </summary>
        private static bool IsUnset(JToken? token) => token == null || token.Type == JTokenType.Null;

        [Fact]
        public void ASourceStoreUpdateLandsOnTheNamedKbAndNotOnTheGlobal()
        {
            Program.UpdateLastKnownSourceStoreForTest("kb-one", JObject.Parse("""{"store":"alpha"}"""));

            var named = IndexBlock("kb-one");
            Assert.Equal("alpha", named["sourceStore"]?["store"]?.ToString());

            // The other half, and the reason the helper exists: the global snapshot
            // must be untouched, or one KB's store would answer for another.
            Assert.True(IsUnset(IndexBlock("kb-two")["sourceStore"]), "kb-two saw kb-one's source store");
        }

        [Fact]
        public void DiagnosticsUpdateLandsOnTheNamedKbAndNotOnTheGlobal()
        {
            Program.UpdateLastKnownIndexDiagnosticsForTest("kb-one", resumedFrom: 7, checkpointActive: true, cacheValidation: null);

            var named = IndexBlock("kb-one");
            Assert.Equal(7, named["resumedFrom"]?.ToObject<int>());
            Assert.True(named["checkpoint"]?["active"]?.Value<bool>());

            Assert.True(IsUnset(IndexBlock("kb-two")["resumedFrom"]), "kb-two saw kb-one's resumedFrom");
            Assert.False(IndexBlock("kb-two")["checkpoint"]?["active"]?.Value<bool>() ?? false);
        }

        [Fact]
        public void UpdatesToDifferentKbsDoNotOverwriteEachOther()
        {
            Program.UpdateLastKnownSourceStoreForTest("kb-one", JObject.Parse("""{"store":"alpha"}"""));
            Program.UpdateLastKnownSourceStoreForTest("kb-two", JObject.Parse("""{"store":"beta"}"""));

            Assert.Equal("alpha", IndexBlock("kb-one")["sourceStore"]?["store"]?.ToString());
            Assert.Equal("beta", IndexBlock("kb-two")["sourceStore"]?["store"]?.ToString());
        }

        [Fact]
        public void TheStoredSourceStoreIsACopyNotTheCallersObject()
        {
            // The DeepClone in the mutator is load-bearing: the caller's JObject
            // outlives the call in real use, and a stored reference would let a
            // later edit to it rewrite already-recorded diagnostics.
            var caller = JObject.Parse("""{"store":"alpha"}""");
            Program.UpdateLastKnownSourceStoreForTest("kb-one", caller);

            caller["store"] = "mutated-after-the-call";

            Assert.Equal("alpha", IndexBlock("kb-one")["sourceStore"]?["store"]?.ToString());
        }

        [Fact]
        public void AnAbsentSourceStoreIsIgnoredRatherThanClearingTheStoredOne()
        {
            Program.UpdateLastKnownSourceStoreForTest("kb-one", JObject.Parse("""{"store":"alpha"}"""));

            Program.UpdateLastKnownSourceStoreForTest("kb-one", sourceStore: null);

            Assert.Equal("alpha", IndexBlock("kb-one")["sourceStore"]?["store"]?.ToString());
        }

        [Fact]
        public void AnOmittedDiagnosticFieldKeepsItsStoredValue()
        {
            // These are "only overwrite what you were given" updates, not
            // replacements: a caller that reports a resumed position must not erase
            // the checkpoint state the next caller recorded.
            Program.UpdateLastKnownIndexDiagnosticsForTest("kb-one", resumedFrom: 7, checkpointActive: true, cacheValidation: null);
            Program.UpdateLastKnownIndexDiagnosticsForTest("kb-one", resumedFrom: null, checkpointActive: null, cacheValidation: null);

            var named = IndexBlock("kb-one");
            Assert.Equal(7, named["resumedFrom"]?.ToObject<int>());
            Assert.True(named["checkpoint"]?["active"]?.Value<bool>());
        }

        [Fact]
        public void TheCacheValidationPayloadIsAlsoCopied()
        {
            var caller = JObject.Parse("""{"reason":"probe"}""");
            Program.UpdateLastKnownIndexDiagnosticsForTest("kb-one", resumedFrom: null, checkpointActive: null, cacheValidation: caller);

            caller["reason"] = "mutated-after-the-call";

            Assert.Equal("probe", IndexBlock("kb-one")["cacheValidation"]?["reason"]?.ToString());
        }

        [Fact]
        public void InvalidateIndexStateForKbKeepsItsNarrowerResolutionRule()
        {
            // Pinned structurally, and deliberately so.
            //
            // InvalidateIndexStateForKb is the one writer that resolves the alias
            // WITHOUT the ResolveKbAliasForIndexRefresh fallback, so it can never
            // land on the global snapshot. That difference is only observable when
            // the fallback returns something, which needs _currentKb or a worker
            // pool with exactly one open KB - process-level state, and the live
            // fixtures are permanently unavailable here. Adding the fallback to
            // that method changed nothing across all 130 index-filtered tests,
            // which is exactly why this assertion is here rather than left to
            // behaviour: without it the narrowing is one refactor away from
            // silently disappearing.
            // Comments are stripped before the check: the method documents, in
            // place, exactly why it does not use that fallback, and the explanation
            // necessarily names the thing being excluded.
            string body = RepoSource.WithoutComments(SourceAssert.MethodBody(
                GxMcp.TestSupport.RepoSource.Read("src", "GxMcp.Gateway", "Program.Whoami.cs"),
                "internal static void InvalidateIndexStateForKb(string? kbAlias)"));

            Assert.Contains("NormalizeKbAlias(kbAlias)", body);
            Assert.DoesNotContain("ResolveKbAliasForIndexRefresh", body);
            Assert.Contains("if (string.IsNullOrEmpty(alias)) return;", body);
        }

    }
}
