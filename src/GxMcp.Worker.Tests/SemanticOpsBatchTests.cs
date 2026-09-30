using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Both SemanticOpsService entry points — the XML form and the Transaction
    /// structure DSL form — now run one batch loop, differing only in the delegate
    /// they dispatch through. That loop carries the client-facing contract: which
    /// error code a failure reports, whether a failure aborts the rest, and what
    /// order results come back in. Duplicated, a client would get a different
    /// contract from each surface, so it is pinned here once.
    /// </summary>
    public class SemanticOpsBatchTests
    {
        private static SemanticOpsService Service() => new SemanticOpsService();

        private static IList<SemanticOp> Ops(params string[] ops)
        {
            return ops.Select(o => new SemanticOp { Op = o }).ToList();
        }

        [Fact]
        public void NormalizeMode_DefaultsToStrictAndFoldsTheAliases()
        {
            // An unrecognised or absent validate mode must fall back to strict,
            // because strict is the safe answer: stop rather than half-apply.
            Assert.Equal("strict", SemanticOpsService.NormalizeMode(null));
            Assert.Equal("strict", SemanticOpsService.NormalizeMode("  "));
            Assert.Equal("strict", SemanticOpsService.NormalizeMode("nonsense"));

            Assert.Equal("strict", SemanticOpsService.NormalizeMode("STRICT"));
            Assert.Equal("best-effort", SemanticOpsService.NormalizeMode("best_effort"));
            Assert.Equal("best-effort", SemanticOpsService.NormalizeMode("besteffort"));
            Assert.Equal("best-effort", SemanticOpsService.NormalizeMode("best-effort"));
            Assert.Equal("only", SemanticOpsService.NormalizeMode("validate-only"));
            Assert.Equal("only", SemanticOpsService.NormalizeMode("validate_only"));
        }

        [Fact]
        public void BothSurfaces_ReportTheSameModeTheyWereGiven()
        {
            // The outcome echoes the normalized mode; the two surfaces must not
            // disagree about it.
            foreach (string mode in new[] { "strict", "best_effort", "validate-only", "garbage" })
            {
                var xml = Service().ApplyWithResults(
                    "<K2BEntityServices><![CDATA[]]></K2BEntityServices>", "Transaction", Ops(), mode);
                var dsl = Service().ApplyTransactionStructureDsl("", Ops(), mode);

                Assert.Equal(SemanticOpsService.NormalizeMode(mode), xml.Mode);
                Assert.Equal(SemanticOpsService.NormalizeMode(mode), dsl.Mode);
                Assert.Equal(xml.Mode, dsl.Mode);
            }
        }

        [Fact]
        public void AnUnknownOp_FailsInStrictModeAndAbortsTheRest()
        {
            // The second op must not be attempted: strict stops at the first
            // failure, and the result list must show only what actually ran.
            var outcome = Service().ApplyTransactionStructureDsl(
                "", Ops("nosuchop", "add_attribute"), "strict");

            Assert.True(outcome.Aborted);
            Assert.Single(outcome.Results);
            Assert.Equal(0, outcome.Results[0].Index);
            Assert.False(outcome.Results[0].Ok);
            Assert.False(string.IsNullOrWhiteSpace(outcome.Results[0].Code));
        }

        [Fact]
        public void AnUnknownOp_ContinuesInBestEffortMode()
        {
            // best-effort records the failure and keeps going. The op that failed
            // must still appear in the results, in its original position, so a
            // client can map results[Index] back to the request it sent.
            var outcome = Service().ApplyTransactionStructureDsl(
                "", Ops("nosuchop", "nosuchop2"), "best_effort");

            Assert.False(outcome.Aborted);
            Assert.Equal(2, outcome.Results.Count);
            Assert.Equal(0, outcome.Results[0].Index);
            Assert.Equal(1, outcome.Results[1].Index);
            Assert.All(outcome.Results, r => Assert.False(r.Ok));
        }

        [Fact]
        public void ResultIndices_TrackTheRequestOrderOnBothSurfaces()
        {
            // A client sends N ops and reads N results back. The index must be the
            // request position, so it has to line up even when an op in the middle
            // failed and the loop carried on.
            var dsl = Service().ApplyTransactionStructureDsl(
                "", Ops("nosuchop", "nosuchop2", "nosuchop3"), "best_effort");

            Assert.Equal(new[] { 0, 1, 2 }, dsl.Results.Select(r => r.Index).ToArray());
            Assert.All(dsl.Results, r => Assert.False(string.IsNullOrWhiteSpace(r.Op)));
        }

        [Fact]
        public void AnEmptyOpList_IsAcceptedAndNotAborted()
        {
            var xml = Service().ApplyWithResults("<a/>", "Transaction", Ops(), "strict");
            var dsl = Service().ApplyTransactionStructureDsl("", Ops(), "strict");

            Assert.Empty(xml.Results);
            Assert.Empty(dsl.Results);
            Assert.False(xml.Aborted);
            Assert.False(dsl.Aborted);
        }

        [Fact]
        public void TheTwoSurfaces_ShareOneBatchLoop()
        {
            // Source-level guard: the abort boundary, the two error codes and the
            // result record were the client-facing contract, written out twice.
            // A second copy is how the two surfaces start disagreeing.
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "SemanticOpsService.cs");

            // Counted in code only: the doc comment on ApplyOps names the same
            // strings, and the abort check legitimately appears once per catch.
            Assert.Equal(1, CountOccurrences(src, "Reason = ux.Message, Code = ux.Code }"));
            Assert.Equal(1, CountOccurrences(src, "Code = \"internal_error\" }"));
            Assert.Equal(2, CountOccurrences(src, "if (mode == \"strict\") { aborted = true; break; }"));
            Assert.Contains("ApplyOps(ops, mode, op => Dispatch(doc, objectKind, op), out bool aborted)", src);
            Assert.Contains("ApplyOps(ops, mode, op => ApplyDslOp(lines, op), out bool aborted)", src);
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
