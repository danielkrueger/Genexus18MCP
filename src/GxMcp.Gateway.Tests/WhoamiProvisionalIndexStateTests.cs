using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Regression coverage for the whoami "phantom Cold" that made the server recommend a
    // full forced reindex of an already-indexed KB.
    //
    // When the 400ms index-state round-trip times out right after a KB open, whoami stamped
    // a placeholder carrying Status="Cold", TotalObjects=0 and RefreshedAtUtc=UtcNow. The
    // timestamp made the snapshot look fresh (suppressing the round-trip for the whole 15s
    // window — a legitimate optimisation), but the Cold was a guess, indistinguishable from a
    // real one. BuildSuggestedNextBlock then routed it to `genexus_lifecycle action=index
    // force=true`. Observed live against KBTeste: first whoami reported Cold/0 and advised a
    // forced reindex; the very next second the worker reported Ready/current, 620 objects,
    // canDelta=true, slotGeneration=145 — the advised reindex was pure waste, and on a large
    // KB it is a multi-minute rebuild.
    public class WhoamiProvisionalIndexStateTests
    {
        [Fact]
        public void TheProductionPlaceholder_Itself_IsFlaggedProvisional()
        {
            // Guards the real construction site (not a test stand-in): the snapshot whoami
            // actually writes when the index-state round-trip times out.
            var snapshot = Program.CreateProvisionalIndexSnapshot("KB-Placeholder", null);

            Assert.True(snapshot.Provisional,
                "A snapshot the gateway invented must carry the provenance flag.");
            // The gate must keep failing closed, so the Cold status is retained on purpose.
            Assert.Equal("Cold", snapshot.Status);
            Assert.Equal(0, snapshot.TotalObjects);
            Assert.NotEqual(DateTime.MinValue, snapshot.RefreshedAtUtc);
        }

        [Fact]
        public void AdviceRouting_IgnoresProvenance_WithoutTheFix()
        {
            // The routing itself: a provisional snapshot must reach the re-read advice, while a
            // worker-observed Cold keeps its force=true nudge. This is the decision point that
            // BuildSuggestedNextBlock calls, so it is what decides whether an agent reindexes.
            var provisional = Program.CreateProvisionalIndexSnapshot("KB-Route", null);
            var observed = Program.CreateProvisionalIndexSnapshot("KB-Route", null);
            observed.Provisional = false;

            var onProvisional = Program.BuildIndexSuggestionForSnapshot(provisional);
            var onObserved = Program.BuildIndexSuggestionForSnapshot(observed);

            Assert.NotNull(onProvisional);
            Assert.NotNull(onObserved);
            Assert.Equal("genexus_whoami", (string?)onProvisional!["tool"]);
            Assert.DoesNotContain("force", onProvisional!.ToString());

            Assert.Equal("genexus_lifecycle", (string?)onObserved!["tool"]);
            Assert.True(onObserved!["args"]?["force"]?.ToObject<bool>());
        }

        [Fact]
        public void AdviceRouting_HandlesANullSnapshot()
        {
            Assert.NotNull(Program.BuildIndexSuggestionForSnapshot(null));
        }

        [Fact]
        public void Placeholder_SurfacesProvisionalFlag()
        {
            Program.ResetIndexStateMirrorForTest();
            try
            {
                Program.MarkIndexStateProvisionalForTest("KB-Provisional");

                var block = Program.BuildIndexBlockForTest("KB-Provisional");

                // The placeholder still reads Cold so the read gate keeps failing closed...
                Assert.Equal("Cold", (string?)block["status"]);
                // ...but the payload now says the values were invented, not observed.
                Assert.True(block["provisional"]?.ToObject<bool>(),
                    "A gateway-invented snapshot must be flagged provisional.");
            }
            finally
            {
                Program.ResetIndexStateMirrorForTest();
            }
        }

        [Fact]
        public void WorkerReportedState_IsNotProvisional()
        {
            Program.ResetIndexStateMirrorForTest();
            try
            {
                Program.UpdateLastKnownIndexStateForTest(
                    "KB-Real", "Ready", 620, DateTime.UtcNow, "current");

                var block = Program.BuildIndexBlockForTest("KB-Real");

                Assert.Equal("Ready", (string?)block["status"]);
                Assert.Equal(620, block["totalObjects"]?.ToObject<int>());
                Assert.False(block["provisional"]?.ToObject<bool>() ?? true,
                    "A state read from the worker must never be flagged provisional.");
            }
            finally
            {
                Program.ResetIndexStateMirrorForTest();
            }
        }

        [Fact]
        public void ProvisionalState_AsksForAReRead_NotAReindex()
        {
            var suggestion = Program.BuildProvisionalIndexSuggestion();

            Assert.NotNull(suggestion);
            // The whole point: never a mutating reindex on invented evidence.
            Assert.Equal("genexus_whoami", (string?)suggestion["tool"]);
            Assert.DoesNotContain("genexus_lifecycle", suggestion.ToString());
            Assert.DoesNotContain("force", suggestion.ToString());
            Assert.Contains("provisional", (string?)suggestion["why"]);
        }

        [Fact]
        public void ProvisionalState_IsDistinguishableFromARealCold()
        {
            // The genuine-cold path must keep its force=true nudge — that advice is correct
            // when the worker actually says Cold. The two cases differ only in provenance, so
            // both must remain reachable and distinguishable.
            var real = Program.BuildIndexSuggestion("Cold", 0);
            var provisional = Program.BuildProvisionalIndexSuggestion();

            Assert.NotNull(real);
            Assert.Equal("genexus_lifecycle", (string?)real["tool"]);
            Assert.True(real["args"]?["force"]?.ToObject<bool>());
            Assert.NotEqual((string?)real["tool"], (string?)provisional["tool"]);
        }

        [Fact]
        public void ProvisionalState_DoesNotOpenTheReadGate()
        {
            // Fail-closed is preserved: a placeholder must keep blocking index-dependent
            // reads exactly as the invented Cold did. Only the ADVICE changed.
            Program.ResetIndexStateMirrorForTest();
            try
            {
                Program.MarkIndexStateProvisionalForTest("KB-Gate");

                Assert.True(Program.IsIndexUsableForReadsForTest("KB-Gate") == false,
                    "A provisional snapshot must never be treated as usable for reads.");
            }
            finally
            {
                Program.ResetIndexStateMirrorForTest();
            }
        }
    }
}
