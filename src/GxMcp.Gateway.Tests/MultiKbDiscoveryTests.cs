using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Issue #356 — the bounded multi-KB federation, driven through a fake
    /// dispatcher so every interesting case (a slow KB, a throwing KB, a KB that
    /// was never opened, a cancelled caller, a budget cut) is deterministic and
    /// runs without a GeneXus Worker.
    /// <para>
    /// The assertions concentrate on one thing: <b>a KB that was not searched is
    /// never shaped like a KB that was searched and found nothing.</b> Everything
    /// else — concurrency caps, deadlines, cursor validation — matters only
    /// because it protects that distinction.
    /// </para>
    /// </summary>
    public class MultiKbDiscoveryTests
    {
        private static MultiKbDiscovery.Target Ok(string alias) => new MultiKbDiscovery.Target
        {
            Alias = alias,
            Status = MultiKbDiscovery.StatusOk,
            Handle = new KbHandle(alias, $@"C:\kb\{alias}")
        };

        private static MultiKbDiscovery.Target Skipped(string alias, string status) => new MultiKbDiscovery.Target
        {
            Alias = alias,
            Status = status,
            Detail = MultiKbDiscovery.DescribeStatus(status)
        };

        private static MultiKbDiscovery.Outcome Matches(string alias, params string[] names)
        {
            var results = new JArray(names.Select(n => new JObject { ["name"] = n, ["objectGuid"] = n + "-guid" }));
            return new MultiKbDiscovery.Outcome
            {
                Status = MultiKbDiscovery.StatusOk,
                Payload = new JObject { ["results"] = results, ["total"] = names.Length, ["count"] = names.Length },
                Returned = names.Length,
                Total = names.Length,
                Complete = true
            };
        }

        /// <summary>
        /// A KB whose Worker returned a cursor, mirroring what the dispatcher now
        /// produces: more pages exist, so the KB is not covered to completion.
        /// </summary>
        private static MultiKbDiscovery.Outcome Paged(string alias, string cursor, params string[] names)
        {
            var outcome = Matches(alias, names);
            outcome.Payload!["nextCursor"] = cursor;
            outcome.NextCursor = cursor;
            outcome.Complete = false;
            return outcome;
        }

        private static List<MultiKbDiscovery.Target> Plan(params MultiKbDiscovery.Target[] targets) => targets.ToList();

        private static async Task<List<MultiKbDiscovery.Outcome>> RunAsync(
            List<MultiKbDiscovery.Target> plan,
            Func<MultiKbDiscovery.Target, CancellationToken, Task<MultiKbDiscovery.Outcome>> dispatch,
            int concurrency = 4,
            int perKbMs = 5000)
        {
            return await MultiKbDiscovery.RunAsync(
                plan, dispatch, concurrency, TimeSpan.FromMilliseconds(perKbMs), CancellationToken.None);
        }

        // ------------------------------------------------------------ selection

        [Fact]
        public void Aliases_AreResolvedInCallerOrder()
        {
            Assert.True(MultiKbDiscovery.TryResolveAliases(
                new JObject { ["kbs"] = new JArray("KbBeta", "KbAlpha") },
                out var aliases, out _, out _));
            Assert.Equal(new[] { "KbBeta", "KbAlpha" }, aliases);
        }

        [Fact]
        public void NonArraySelection_IsRejectedWithACode()
        {
            Assert.False(MultiKbDiscovery.TryResolveAliases(
                new JObject { ["kbs"] = "KbAlpha" }, out _, out string code, out string message));
            Assert.Equal("MultiKbSelectionInvalid", code);
            Assert.Contains("No KB was searched", message);
        }

        [Fact]
        public void EmptySelection_IsRejectedRatherThanReportingZeroMatches()
        {
            Assert.False(MultiKbDiscovery.TryResolveAliases(
                new JObject { ["kbs"] = new JArray() }, out _, out string code, out string message));
            Assert.Equal("MultiKbSelectionInvalid", code);
            Assert.Contains("complete answer", message);
        }

        [Fact]
        public void DuplicateAlias_IsRejected()
        {
            // A duplicate would make "searched KBs" disagree with "grouped by KB"
            // and would bill the same KB twice against the result budget.
            Assert.False(MultiKbDiscovery.TryResolveAliases(
                new JObject { ["kbs"] = new JArray("KbAlpha", "kbalpha") },
                out _, out string code, out string message));
            Assert.Equal("MultiKbSelectionInvalid", code);
            Assert.Contains("more than once", message);
        }

        [Fact]
        public void OversizedSelection_IsRejected()
        {
            var many = new JArray(Enumerable.Range(0, MultiKbDiscovery.MaxKbCount + 1).Select(i => $"Kb{i}"));
            Assert.False(MultiKbDiscovery.TryResolveAliases(
                new JObject { ["kbs"] = many }, out _, out string code, out string message));
            Assert.Equal("MultiKbSelectionTooLarge", code);
            Assert.Contains("complete answer", message);
        }

        [Fact]
        public void EmptyAliasEntry_IsRejected()
        {
            Assert.False(MultiKbDiscovery.TryResolveAliases(
                new JObject { ["kbs"] = new JArray("KbAlpha", "  ") },
                out _, out string code, out _));
            Assert.Equal("MultiKbSelectionInvalid", code);
        }

        [Fact]
        public void KbAndKbs_TogetherAreRejected()
        {
            // A federation names its own KBs and never consults session selection,
            // so an accompanying `kb` is undecidable rather than additive.
            Assert.False(MultiKbDiscovery.TryResolveAliases(
                new JObject { ["kbs"] = new JArray("KbAlpha"), ["kb"] = "KbBeta" },
                out _, out string code, out string message));
            Assert.Equal("MultiKbSelectionAmbiguous", code);
            Assert.Contains("mutually exclusive", message);
        }

        [Fact]
        public void NoKbs_IsNotAFederation()
        {
            // The single-KB route must stay untouched when the field is absent.
            Assert.False(MultiKbDiscovery.TryResolveAliases(new JObject(), out var aliases, out _, out _));
            Assert.Empty(aliases);
        }

        // -------------------------------------------------------------- cursors

        [Fact]
        public void CursorsAreMatchedToTheSelection()
        {
            Assert.True(MultiKbDiscovery.TryResolveCursors(
                new JObject { ["cursors"] = new JObject { ["KbAlpha"] = "abc", ["KbBeta"] = "def" } },
                new List<string> { "KbAlpha", "KbBeta" },
                out var cursors, out _, out _));
            Assert.Equal("abc", cursors["KbAlpha"]);
            Assert.Equal("def", cursors["KbBeta"]);
        }

        [Fact]
        public void CursorForAnUnselectedKb_IsRejected()
        {
            // Silently dropping it is how a resumed page repeats its first page.
            Assert.False(MultiKbDiscovery.TryResolveCursors(
                new JObject { ["cursors"] = new JObject { ["KbGamma"] = "abc" } },
                new List<string> { "KbAlpha" },
                out var cursors, out string code, out string message));
            Assert.Empty(cursors);
            Assert.Equal("MultiKbCursorInvalid", code);
            Assert.Contains("not in kbs", message);
        }

        [Fact]
        public void EmptyCursorValue_IsRejected()
        {
            Assert.False(MultiKbDiscovery.TryResolveCursors(
                new JObject { ["cursors"] = new JObject { ["KbAlpha"] = "" } },
                new List<string> { "KbAlpha" },
                out _, out string code, out string message));
            Assert.Equal("MultiKbCursorInvalid", code);
            Assert.Contains("restart that KB", message);
        }

        // ------------------------------------------------------- orchestration

        [Fact]
        public async Task OneFailingKb_DoesNotEraseTheOthers()
        {
            var plan = Plan(
                Ok("KbAlpha"),
                Ok("KbBeta"),
                Ok("KbGamma"));

            var outcomes = await RunAsync(plan, (target, _) =>
            {
                if (target.Alias == "KbBeta") throw new InvalidOperationException("boom");
                return Task.FromResult(Matches(target.Alias, target.Alias + "Object"));
            });

            var alpha = outcomes.Single(o => o.Alias == "KbAlpha");
            var beta = outcomes.Single(o => o.Alias == "KbBeta");
            var gamma = outcomes.Single(o => o.Alias == "KbGamma");

            Assert.Equal(1, alpha.Returned);
            Assert.Equal(1, gamma.Returned);
            Assert.Equal(MultiKbDiscovery.StatusError, beta.Status);
            Assert.False(beta.Complete);
            // The exception message is not echoed: it can embed a filesystem path.
            Assert.Equal(nameof(InvalidOperationException), beta.Detail);
        }

        [Fact]
        public async Task ASkippedKb_ReportsItsOwnStatusWithoutDispatching()
        {
            bool dispatched = false;
            var outcomes = await RunAsync(
                Plan(Skipped("KbAlpha", MultiKbDiscovery.StatusNotOpen), Ok("KbBeta")),
                (target, _) => { dispatched = true; return Task.FromResult(Matches(target.Alias, "X")); });

            var alpha = outcomes.Single(o => o.Alias == "KbAlpha");
            Assert.Equal(MultiKbDiscovery.StatusNotOpen, alpha.Status);
            // Never complete: nothing was searched, so nothing is known.
            Assert.False(alpha.Complete);
            Assert.Equal(0, alpha.Returned);
            Assert.Equal(MultiKbDiscovery.StatusOk, outcomes.Single(o => o.Alias == "KbBeta").Status);
            Assert.True(dispatched);
        }

        [Fact]
        public async Task ASlowKb_TimesOutOnItsOwnWithoutLosingTheOthers()
        {
            var outcomes = await RunAsync(
                Plan(Ok("KbSlow"), Ok("KbFast")),
                async (target, ct) =>
                {
                    if (target.Alias == "KbSlow")
                        await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    return Matches(target.Alias, "X");
                },
                concurrency: 4,
                perKbMs: 120);

            var slow = outcomes.Single(o => o.Alias == "KbSlow");
            var fast = outcomes.Single(o => o.Alias == "KbFast");

            Assert.Equal(MultiKbDiscovery.StatusTimeout, slow.Status);
            Assert.False(slow.Complete);
            Assert.Contains("120ms", slow.Detail);
            Assert.Equal(1, fast.Returned);
        }

        [Fact]
        public async Task ConcurrencyIsCapped()
        {
            int inFlight = 0, peak = 0;
            var gate = new object();
            var plan = Plan(Enumerable.Range(0, 10).Select(i => Ok("Kb" + i)).ToArray());

            await RunAsync(plan, async (target, _) =>
            {
                lock (gate)
                {
                    inFlight++;
                    peak = Math.Max(peak, inFlight);
                }
                await Task.Delay(20);
                lock (gate) { inFlight--; }
                return Matches(target.Alias, "X");
            }, concurrency: 3);

            Assert.True(peak <= 3, $"peak concurrency was {peak}, cap is 3");
        }

        [Fact]
        public async Task CancellationIsReportedPerUnfinishedKb()
        {
            using var cts = new CancellationTokenSource();
            // KbB cancels only after KbA was dispatched; otherwise, on a loaded
            // machine, KbA may still be queued and is (correctly) canceled too.
            var kbADispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var plan = Plan(Ok("KbA"), Ok("KbB"));
            var outcomes = await MultiKbDiscovery.RunAsync(
                plan,
                async (target, ct) =>
                {
                    if (target.Alias == "KbA")
                    {
                        kbADispatched.SetResult();
                        return Matches(target.Alias, "X");
                    }
                    await kbADispatched.Task;
                    cts.Cancel();
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    return Matches(target.Alias, "Y");
                },
                4,
                TimeSpan.FromSeconds(30),
                cts.Token);

            Assert.Equal(MultiKbDiscovery.StatusOk, outcomes.Single(o => o.Alias == "KbA").Status);
            var cancelled = outcomes.Single(o => o.Alias == "KbB");
            Assert.Equal(MultiKbDiscovery.StatusCanceled, cancelled.Status);
            Assert.False(cancelled.Complete);
        }

        [Fact]
        public async Task CancellationOfAKbStillQueuedKeepsTheFinishedKbResults()
        {
            // Concurrency 1: whichever KB runs first cancels the caller and still
            // returns its matches; the other one is still queued (at the gate, or
            // not even scheduled). Before the fix the queued KB's cancellation
            // escaped RunAsync and Task.WhenAll threw, erasing the finished KB's
            // results along with it.
            using var cts = new CancellationTokenSource();
            var plan = Plan(Ok("KbA"), Ok("KbB"));
            var outcomes = await MultiKbDiscovery.RunAsync(
                plan,
                (target, ct) =>
                {
                    cts.Cancel();
                    return Task.FromResult(Matches(target.Alias, "X"));
                },
                1,
                TimeSpan.FromSeconds(30),
                cts.Token);

            Assert.Equal(2, outcomes.Count);
            Assert.Single(outcomes, o => o.Status == MultiKbDiscovery.StatusOk);
            var queued = Assert.Single(outcomes, o => o.Status == MultiKbDiscovery.StatusCanceled);
            Assert.False(queued.Complete);
        }

        // ------------------------------------------------------------- envelope

        [Fact]
        public async Task Envelope_ReportsCoverageAndNeverClaimsCompletenessWhenItIsNot()
        {
            var outcomes = await RunAsync(
                Plan(Ok("KbAlpha"), Skipped("KbBeta", MultiKbDiscovery.StatusNotOpen)),
                (target, _) => Task.FromResult(Matches(target.Alias, "Shared", "SharedDetail")));

            var envelope = MultiKbDiscovery.BuildEnvelope(
                new List<string> { "KbAlpha", "KbBeta" }, outcomes,
                maxTotalResults: 500, concurrency: 4, perKbTimeout: TimeSpan.FromSeconds(30),
                elapsedMs: 42, cancelled: false);

            var coverage = envelope["coverage"]!;
            Assert.False(coverage["complete"]!.Value<bool>());
            Assert.Equal(new[] { "KbBeta" }, coverage["incomplete"]!.Values<string>());
            Assert.Equal(new[] { "KbAlpha" }, coverage["searched"]!.Values<string>());
            // The plain-language guard against reading this as a negative result.
            Assert.Contains("NOT evidence of zero matches", coverage["note"]!.ToString());

            var entries = (JArray)envelope["results"]!;
            Assert.Equal(new[] { "KbAlpha", "KbBeta" }, entries.Select(e => e["kb"]!.ToString()));
            var alpha = entries[0] as JObject;
            var beta = entries[1] as JObject;
            Assert.NotNull(alpha);
            Assert.NotNull(beta);
            Assert.Equal(2, alpha!["results"]!.Count());
            Assert.Empty(beta!["results"]!);
            Assert.False(beta!["complete"]!.Value<bool>());
            Assert.Equal(MultiKbDiscovery.StatusNotOpen, beta!["error"]!["code"]!.ToString());
            // Federated identities are per-KB; a flat name would be meaningless.
            Assert.True(envelope["federated"]!.Value<bool>());
        }

        [Fact]
        public async Task Envelope_PreservesCallerOrderAndEveryRequestedAlias()
        {
            var outcomes = await RunAsync(
                Plan(Ok("KbBeta"), Ok("KbAlpha")),
                (target, _) => Task.FromResult(Matches(target.Alias)));

            var envelope = MultiKbDiscovery.BuildEnvelope(
                new List<string> { "KbAlpha", "KbBeta" }, outcomes,
                500, 4, TimeSpan.FromSeconds(30), 1, false);

            var entries = (JArray)envelope["results"]!;
            Assert.Equal(new[] { "KbAlpha", "KbBeta" }, entries.Select(e => e["kb"]!.ToString()));
        }

        [Fact]
        public async Task BudgetCut_MarksTheAffectedKbIncomplete_AndDoesNotOfferASkippingCursor()
        {
            var outcomes = await RunAsync(
                Plan(Ok("KbAlpha"), Ok("KbBeta")),
                (target, _) => Task.FromResult(Paged(target.Alias, "cursor-" + target.Alias, "A", "B", "C", "D")));

            var envelope = MultiKbDiscovery.BuildEnvelope(
                new List<string> { "KbAlpha", "KbBeta" }, outcomes,
                maxTotalResults: 5, concurrency: 4, perKbTimeout: TimeSpan.FromSeconds(30),
                elapsedMs: 10, cancelled: false);

            var alpha = (JObject)envelope["results"]![0]!;
            var beta = (JObject)envelope["results"]![1]!;

            Assert.Equal(4, alpha["results"]!.Count());
            Assert.Single(beta["results"]!);
            // The KB that lost its tail is the one that says so.
            Assert.Equal(MultiKbDiscovery.StatusBudgetExceeded, beta["status"]!.ToString());
            Assert.False(beta["complete"]!.Value<bool>());
            Assert.True(envelope["budget"]!["truncated"]!.Value<bool>());
            Assert.Equal(5, envelope["coverage"]!["matchedTotal"]!.Value<int>());

            // Issue #377. Beta's Worker cursor points past all four items of the page it
            // returned, so the three trimmed off are not reachable through it. Handing it
            // out claimed the rest was resumable while skipping results.
            Assert.Equal(JTokenType.Null, beta["nextCursor"]!.Type);
            Assert.Equal(3, beta["droppedByBudget"]!.Value<int>());
            Assert.True(beta["budgetCut"]!["resumeSkipsPageRemainder"]!.Value<bool>());
            // Alpha kept its page and is not complete either: it has more pages.
            Assert.False(alpha["complete"]!.Value<bool>());
            Assert.True(alpha["hasMore"]!.Value<bool>());
            Assert.Equal("cursor-KbAlpha", alpha["nextCursor"]!.ToString());
            Assert.False(envelope["coverage"]!["complete"]!.Value<bool>());
        }

        [Fact]
        public async Task A_Budget_Cut_On_A_Last_Page_Reports_No_Cursor_And_No_Skip()
        {
            // The worker's page was its last one, so there is nothing to resume anyway;
            // the dropped items are simply the end of this KB's result set.
            var outcomes = await RunAsync(
                Plan(Ok("KbAlpha")),
                (target, _) =>
                {
                    var outcome = Matches(target.Alias, "A", "B", "C", "D");
                    outcome.NextCursor = null;
                    return Task.FromResult(outcome);
                });

            var envelope = MultiKbDiscovery.BuildEnvelope(
                new List<string> { "KbAlpha" }, outcomes,
                maxTotalResults: 2, concurrency: 4, perKbTimeout: TimeSpan.FromSeconds(30),
                elapsedMs: 10, cancelled: false);

            var alpha = (JObject)envelope["results"]![0]!;
            Assert.Equal(2, alpha["results"]!.Count());
            Assert.Equal(2, alpha["droppedByBudget"]!.Value<int>());
            Assert.False(alpha["budgetCut"]!["resumeSkipsPageRemainder"]!.Value<bool>());
        }

        [Fact]
        public async Task A_Paged_Kb_Is_Never_Reported_Complete()
        {
            // The defect this guards: DispatchOneKbAsync set Complete = true for every
            // successful per-KB response, so the entry carried complete=true next to
            // hasMore=true and coverage.complete could be true while results were missing.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Gateway", "Program.MultiKbDiscovery.cs");

            int start = source.IndexOf("var results = payload[\"results\"] as JArray;", StringComparison.Ordinal);
            Assert.True(start > 0, "the per-KB outcome projection was not found");
            int end = source.IndexOf("};", start, StringComparison.Ordinal);
            string body = source.Substring(start, end - start);

            Assert.Contains("string.IsNullOrEmpty(nextCursor)", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Complete = true", body, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Paged_Outcome_Built_Directly_Is_Not_Complete()
        {
            // A workerTruncated page is incomplete for the same reason a cursor is.
            var outcome = new MultiKbDiscovery.Outcome
            {
                Alias = "KbAlpha",
                Status = MultiKbDiscovery.StatusOk,
                Payload = new JObject { ["results"] = new JArray(1, 2), ["nextCursor"] = "c2" },
                NextCursor = "c2",
                Returned = 2,
                Total = 9,
                Complete = false
            };

            var entry = outcome.ToEntry(2);
            Assert.False(entry["complete"]!.Value<bool>());
            Assert.True(entry["hasMore"]!.Value<bool>());
            Assert.Equal("c2", entry["nextCursor"]!.ToString());
        }

        [Fact]
        public async Task CompleteFederation_SaysSoExplicitly()
        {
            var outcomes = await RunAsync(
                Plan(Ok("KbAlpha"), Ok("KbBeta")),
                (target, _) => Task.FromResult(Matches(target.Alias, "X")));

            var envelope = MultiKbDiscovery.BuildEnvelope(
                new List<string> { "KbAlpha", "KbBeta" }, outcomes,
                500, 4, TimeSpan.FromSeconds(30), 7, false);

            Assert.True(envelope["coverage"]!["complete"]!.Value<bool>());
            Assert.Empty(envelope["coverage"]!["incomplete"]!.Values<string>());
            Assert.Equal(2, envelope["coverage"]!["matchedTotal"]!.Value<int>());
            Assert.Contains("searched to completion", envelope["coverage"]!["note"]!.ToString());
        }

        [Fact]
        public async Task ACancelledCall_NeverReportsACompleteAnswer()
        {
            var outcomes = await RunAsync(
                Plan(Ok("KbAlpha")),
                (target, _) => Task.FromResult(Matches(target.Alias, "X")));

            var envelope = MultiKbDiscovery.BuildEnvelope(
                new List<string> { "KbAlpha" }, outcomes,
                500, 4, TimeSpan.FromSeconds(30), 3, cancelled: true);

            Assert.False(envelope["coverage"]!["complete"]!.Value<bool>());
            Assert.True(envelope["coverage"]!["cancelled"]!.Value<bool>());
        }

        [Fact]
        public async Task AMissingOutcome_DoesNotSilentlyVanish()
        {
            // If a leg never reported, the answer must still account for it.
            var outcomes = await RunAsync(Plan(Ok("KbAlpha")), (target, _) => Task.FromResult(Matches(target.Alias)));

            var envelope = MultiKbDiscovery.BuildEnvelope(
                new List<string> { "KbAlpha", "KbGhost" }, outcomes,
                500, 4, TimeSpan.FromSeconds(30), 1, false);

            var entries = (JArray)envelope["results"]!;
            Assert.Equal(new[] { "KbAlpha", "KbGhost" }, entries.Select(e => e["kb"]!.ToString()));
            Assert.Equal(MultiKbDiscovery.StatusCanceled, entries[1]!["status"]!.ToString());
            Assert.False(envelope["coverage"]!["complete"]!.Value<bool>());
        }

        // -------------------------------------------------------------- budgets

        [Theory]
        [InlineData(null, MultiKbDiscovery.DefaultConcurrency)]
        [InlineData(0, MultiKbDiscovery.DefaultConcurrency)]
        [InlineData(-3, MultiKbDiscovery.DefaultConcurrency)]
        [InlineData(1, 1)]
        [InlineData(3, 3)]
        [InlineData(999, MultiKbDiscovery.MaxConcurrency)]
        public void ConcurrencyIsClampedToItsCeiling(int? requested, int expected)
        {
            var args = new JObject();
            if (requested.HasValue) args["maxConcurrency"] = requested.Value;
            Assert.Equal(expected, MultiKbDiscovery.ResolveConcurrency(args));
        }

        [Theory]
        [InlineData(null, MultiKbDiscovery.DefaultMaxTotalResults)]
        [InlineData(0, MultiKbDiscovery.DefaultMaxTotalResults)]
        [InlineData(25, 25)]
        [InlineData(10_000_000, MultiKbDiscovery.HardMaxTotalResults)]
        public void ResultBudgetIsClampedToItsCeiling(int? requested, int expected)
        {
            var args = new JObject();
            if (requested.HasValue) args["maxTotalResults"] = requested.Value;
            Assert.Equal(expected, MultiKbDiscovery.ResolveMaxTotalResults(args));
        }

        [Fact]
        public void EveryStatusHasAnActionableExplanation()
        {
            foreach (var status in new[]
            {
                MultiKbDiscovery.StatusNotOpen, MultiKbDiscovery.StatusUnknownAlias,
                MultiKbDiscovery.StatusWarming, MultiKbDiscovery.StatusError,
                MultiKbDiscovery.StatusTimeout, MultiKbDiscovery.StatusBudgetExceeded,
                MultiKbDiscovery.StatusCanceled
            })
            {
                string text = MultiKbDiscovery.DescribeStatus(status);
                Assert.False(string.IsNullOrWhiteSpace(text), $"missing explanation for {status}");
                // Every non-ok status must leave the caller knowing what to do next.
                Assert.Contains(".", text);
            }
        }
    }
}