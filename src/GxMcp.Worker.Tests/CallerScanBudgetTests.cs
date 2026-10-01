using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #343: <c>FindCallerSites</c> walked every recorded caller, read
    /// Source/Events/Rules in full and parsed all of it, with no page boundary, no work
    /// budget and no cancellation point. A high-fan-in target could hold the SDK lane past
    /// a client deadline and leave nothing resumable behind.
    ///
    /// <para>
    /// The budget itself is pure and clock-injected, so the stopping and resume rules are
    /// exercised directly here. The loop that uses it lives in
    /// <c>AnalyzeService.FindCallerSites</c> and needs a live SDK index, so its wiring is
    /// pinned by source shape in <see cref="FindCallerSitesBudgetWiringTests"/> - the
    /// honest split, rather than a fake SDK graph that would test the fake.
    /// </para>
    /// </summary>
    public class CallerScanBudgetTests
    {
        /// <summary>
        /// Drives a scan over <paramref name="callerCount"/> synthetic callers of three
        /// parts each, charging the same way the production loop does, and reports what
        /// the budget stopped it at.
        /// </summary>
        private static CallerScanBudget RunScan(
            int callerCount,
            int partBytes,
            int resultsPerCaller,
            CallerScanBudget budget,
            List<string> reads = null)
        {
            budget.Start();
            string[] parts = { "Source", "Events", "Rules" };
            for (int c = 0; c < callerCount; c++)
            {
                if (!budget.TryBeginCaller()) break;
                bool complete = true;
                foreach (string part in parts)
                {
                    reads?.Add("c" + c + "/" + part);
                    if (!budget.TryChargeBytes(partBytes)) { complete = false; break; }
                    for (int r = 0; r < resultsPerCaller; r++)
                        if (!budget.TryChargeResult()) { complete = false; break; }
                    if (!complete) break;
                }
                if (!complete) break;
                budget.CompleteCaller();
            }
            return budget;
        }

        [Fact]
        public void A_Scan_Within_Budget_Completes()
        {
            var budget = new CallerScanBudget(maxCallers: 100, maxSourceBytes: 1_000_000, maxResults: 1000);
            RunScan(callerCount: 10, partBytes: 100, resultsPerCaller: 1, budget: budget);

            Assert.True(budget.IsComplete);
            Assert.Null(budget.StopReason);
            Assert.Equal(10, budget.CallersScanned);
        }

        [Fact]
        public void A_High_Fan_In_Graph_Stops_At_The_Caller_Budget()
        {
            // The reported failure: thousands of callers, no bound, one SDK lane held
            // past the client's deadline.
            var budget = new CallerScanBudget(maxCallers: 25, maxSourceBytes: 100_000_000, maxResults: 1000);
            RunScan(callerCount: 5000, partBytes: 100, resultsPerCaller: 1, budget: budget);

            Assert.False(budget.IsComplete);
            Assert.Equal("max_callers", budget.StopReason);
            Assert.Equal(25, budget.CallersScanned);
        }

        [Fact]
        public void A_Large_Source_Stops_The_Scan_At_The_Byte_Budget()
        {
            // Caller count alone is not a bound: 20 callers with 1 MB bodies each is more
            // work than 20,000 small ones, and it is the bytes that reach the SDK.
            var budget = new CallerScanBudget(maxCallers: 1000, maxSourceBytes: 10_000, maxResults: 1000);
            RunScan(callerCount: 1000, partBytes: 4_000, resultsPerCaller: 0, budget: budget);

            Assert.False(budget.IsComplete);
            Assert.Equal("max_source_bytes", budget.StopReason);
            // Charged before the read, so the total never exceeds the budget.
            Assert.True(budget.SourceBytesRead <= budget.MaxSourceBytes);
        }

        [Fact]
        public void A_Single_Source_Larger_Than_The_Budget_Is_Skipped_Not_Truncated()
        {
            // Half a source silently omits call sites, which is worse than stopping: the
            // answer would look complete and be wrong. The read is refused instead.
            var budget = new CallerScanBudget(maxCallers: 100, maxSourceBytes: 1_000, maxResults: 100);
            budget.Start();
            budget.TryBeginCaller();

            Assert.False(budget.TryChargeBytes(5_000));
            Assert.Equal(0, budget.SourceBytesRead);
            Assert.Equal("max_source_bytes", budget.StopReason);
        }

        [Fact]
        public void A_Dense_Target_Stops_At_The_Result_Budget()
        {
            // A target called from every line of every caller produces a huge result set
            // even when the caller count is small.
            var budget = new CallerScanBudget(maxCallers: 1000, maxSourceBytes: 100_000_000, maxResults: 40);
            RunScan(callerCount: 100, partBytes: 10, resultsPerCaller: 20, budget: budget);

            Assert.False(budget.IsComplete);
            Assert.Equal("max_results", budget.StopReason);
            Assert.Equal(40, budget.Results);
        }

        [Fact]
        public void A_Slow_Disk_Stops_The_Scan_At_The_Elapsed_Budget()
        {
            // The caller-visible failure is a deadline, not an internal counter, so
            // elapsed time is a budget dimension in its own right.
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime now = start;
            var budget = new CallerScanBudget(
                maxCallers: 1000, maxSourceBytes: 100_000_000, maxResults: 1000,
                maxElapsed: TimeSpan.FromSeconds(2), clock: () => now);

            budget.Start();
            Assert.True(budget.TryBeginCaller(), "precondition: work starts immediately");
            budget.CompleteCaller();

            // Nothing consumed yet.
            Assert.True(budget.TryBeginCaller());
            now = start.AddSeconds(3);

            Assert.False(budget.TryBeginCaller(), "an elapsed budget must stop the scan");
            Assert.Equal("max_elapsed_ms", budget.StopReason);
            Assert.True(budget.ElapsedMs >= 3000);
        }

        [Fact]
        public void The_Elapsed_Budget_Does_Not_Apply_Before_The_Scan_Starts()
        {
            // A budget built and inspected is not a scan; a clock that has already moved
            // must not make an unstarted budget look spent.
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime now = start;
            var budget = new CallerScanBudget(
                maxElapsed: TimeSpan.FromSeconds(1), clock: () => now);

            now = start.AddHours(1);
            Assert.True(budget.TryBeginCaller());
            Assert.Equal(0, budget.ElapsedMs);
        }

        [Fact]
        public void Cancellation_Stops_The_Scan_And_Is_Reported()
        {
            var budget = new CallerScanBudget();
            budget.Start();
            Assert.True(budget.TryBeginCaller());
            budget.CompleteCaller();

            budget.Cancel();

            Assert.False(budget.IsComplete);
            Assert.Equal("cancelled", budget.StopReason);
            Assert.False(budget.TryBeginCaller());
            // Work already charged stays charged: the resume offset must reflect it.
            Assert.Equal(1, budget.CallersScanned);
        }

        [Fact]
        public void The_First_Stop_Reason_Wins()
        {
            // Once stopped, a later budget noticing it was already spent must not
            // misattribute why the scan ended - that is the number an operator reads.
            var budget = new CallerScanBudget(maxCallers: 1, maxSourceBytes: 10, maxResults: 1);
            budget.Start();
            budget.TryBeginCaller();
            budget.CompleteCaller();

            Assert.False(budget.TryBeginCaller());
            Assert.Equal("max_callers", budget.StopReason);

            // Once stopped, charging is a no-op: a caller that kept charging past the
            // stop would keep growing the counters it exists to bound, and the reported
            // totals would no longer describe the work that was kept.
            Assert.False(budget.TryChargeBytes(999));
            Assert.Equal("max_callers", budget.StopReason);
            Assert.False(budget.TryChargeResult());
            Assert.Equal("max_callers", budget.StopReason);
            Assert.Equal(0, budget.SourceBytesRead);
            Assert.Equal(0, budget.Results);
        }

        [Fact]
        public void Non_Positive_Budgets_Fall_Back_To_The_Defaults()
        {
            // A caller passing 0 must not get a scan that stops before any work and
            // reports a truncated answer with nothing to resume from.
            var b0 = new CallerScanBudget(maxCallers: 0, maxSourceBytes: 0, maxResults: 0);
            Assert.Equal(CallerScanBudget.DefaultMaxCallers, b0.MaxCallers);
            Assert.Equal(CallerScanBudget.DefaultMaxSourceBytes, b0.MaxSourceBytes);
            Assert.Equal(CallerScanBudget.DefaultMaxResults, b0.MaxResults);
            Assert.Equal(CallerScanBudget.DefaultMaxElapsed, b0.MaxElapsed);

            var bNeg = new CallerScanBudget(maxCallers: -5, maxSourceBytes: -5, maxResults: -5,
                maxElapsed: TimeSpan.Zero);
            Assert.Equal(CallerScanBudget.DefaultMaxCallers, bNeg.MaxCallers);
        }

        [Fact]
        public void A_Resume_At_The_Caller_Budget_Covers_Every_Caller_Exactly_Once()
        {
            // Acceptance criterion two: paging must yield the same sites as an
            // exhaustive baseline, with no skips and no duplicates - across all three
            // parts of each caller.
            const int callerCount = 40;
            var exhaustive = new List<string>();
            for (int c = 0; c < callerCount; c++)
                foreach (string p in new[] { "Source", "Events", "Rules" })
                    exhaustive.Add("c" + c + "/" + p);

            var paged = new List<string>();
            var reads = new List<string>();
            int cursor = 0, guard = 0;
            while (guard++ < 50)
            {
                var budget = new CallerScanBudget(maxCallers: 7, maxSourceBytes: 1_000_000, maxResults: 1000);
                budget.Start();
                bool callerComplete = true;
                for (int c = cursor; c < callerCount; c++)
                {
                    if (!budget.TryBeginCaller()) { callerComplete = false; break; }
                    foreach (string p in new[] { "Source", "Events", "Rules" })
                    {
                        reads.Add("c" + c + "/" + p);
                        if (!budget.TryChargeBytes(10)) { callerComplete = false; break; }
                    }
                    if (!callerComplete) break;
                    budget.CompleteCaller();
                }
                paged.AddRange(reads.Skip(cursor == 0 ? 0 : 0));
                reads.Clear();
                cursor += budget.CallersScanned;
                if (budget.IsComplete && cursor >= callerCount) break;
                if (budget.CallersScanned == 0) break;
            }

            Assert.Equal(exhaustive, paged);
            Assert.Equal(exhaustive.Count, paged.Distinct().Count());
        }

        [Fact]
        public void A_Partially_Scanned_Caller_Does_Not_Advance_The_Resume_Offset()
        {
            // Advancing past a partially scanned caller would skip its remaining parts on
            // the next page - a silent hole in the answer, which is the worst failure a
            // resumable scan can have.
            // 35 bytes lets caller 0 finish its three 10-byte parts and leaves the second
            // caller's first part unable to fit - so the offset must cover exactly one.
            var budget = new CallerScanBudget(maxCallers: 10, maxSourceBytes: 35, maxResults: 100);
            budget.Start();

            string[] parts = { "Source", "Events", "Rules" };
            bool complete = true;
            for (int c = 0; c < 3; c++)
            {
                if (!budget.TryBeginCaller()) break;
                foreach (string p in parts)
                {
                    if (!budget.TryChargeBytes(10)) { complete = false; break; }
                }
                if (!complete) break;
                budget.CompleteCaller();
            }

            Assert.False(complete);
            Assert.False(budget.IsComplete);
            // The caller that hit the byte budget did not complete, so the offset covers
            // only the callers fully scanned before it.
            Assert.Equal(1, budget.CallersScanned);
            Assert.Equal(30, budget.SourceBytesRead);
        }

        [Fact]
        public void The_Default_Budgets_Are_Bounded_And_Usable()
        {
            Assert.InRange(CallerScanBudget.DefaultMaxCallers, 1, 10_000);
            Assert.InRange(CallerScanBudget.DefaultMaxSourceBytes, 1L, 1024L * 1024 * 1024);
            Assert.InRange(CallerScanBudget.DefaultMaxResults, 1, 100_000);
            Assert.InRange(CallerScanBudget.DefaultMaxElapsed, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
        }
    }

    /// <summary>
    /// The production loop's wiring. <c>FindCallerSites</c> needs a live SDK index, so
    /// these assert the properties the loop must have rather than simulating a graph.
    /// </summary>
    public class FindCallerSitesBudgetWiringTests
    {
        private static string Source() => GxMcp.TestSupport.RepoSource.WithoutComments(
            "src", "GxMcp.Worker", "Services", "AnalyzeService.cs");

        /// <summary>
        /// The body of <c>FindCallerSites</c>, comment-stripped. Newlines are normalized
        /// first: <c>RepoSource.WithoutComments</c> preserves them verbatim, so a delimiter
        /// written with a bare "\n" would not match a CRLF file and the slice would run to
        /// the end of the file - which would make every assertion below pass for the wrong
        /// reason.
        ///
        /// <para>
        /// Uses the shared <c>SourceAssert.MethodBody</c> rather than a second copy. The
        /// repository keeps exactly one of that helper, and
        /// <c>SourceAssertSingleSourceTests</c> fails the build when a second appears.
        /// </para>
        /// </summary>
        private static string FindCallerSitesBody()
        {
            string source = Source().Replace("\r\n", "\n");
            return GxMcp.TestSupport.SourceAssert.MethodBody(
                source, "public string FindCallerSites(");
        }

        [Fact]
        public void The_Loop_Uses_The_Budget_Rather_Than_Walking_Every_Caller()
        {
            string body = FindCallerSitesBody();

            Assert.Contains("CallerScanBudget", body, StringComparison.Ordinal);
            Assert.Contains("budget.TryBeginCaller()", body, StringComparison.Ordinal);
            // The old shape iterated the caller collection directly.
            Assert.DoesNotContain("foreach (var callerName in callerNames)", body, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Source_That_Does_Not_Fit_Is_Discarded_Before_It_Is_Parsed()
        {
            // A source's size is only knowable after reading it, so the byte charge
            // necessarily lands after the read. What bounds the answer is that a refused
            // source contributes nothing: the loop must abandon it before parsing its
            // calls, because half a source silently omits call sites and an answer that
            // looks complete while being wrong is the worst outcome available.
            string body = FindCallerSitesBody();

            int charge = body.IndexOf("budget.TryChargeBytes(", StringComparison.Ordinal);
            int parse = body.IndexOf("SourceParser.ParseCalls(src", StringComparison.Ordinal);
            Assert.True(charge > 0, "the byte charge was not found");
            Assert.True(parse > 0, "the source parse was not found");
            Assert.True(charge < parse, "a source is parsed before its budget charge is known to have fit");

            // And the refusal path abandons the caller rather than continuing.
            int refuse = body.IndexOf("callerFullyScanned = false;", charge, StringComparison.Ordinal);
            Assert.True(refuse > 0, "a refused source does not mark the caller incomplete");
        }

        [Fact]
        public void The_Resume_Offset_Only_Advances_Past_A_Fully_Scanned_Caller()
        {
            // The ordering is the property. Advancing the offset before checking whether
            // the caller finished would skip that caller's remaining parts on the next
            // page - a silent hole in the answer, which is the worst failure a resumable
            // scan can have: the response looks complete and is wrong.
            string body = FindCallerSitesBody();

            int advance = body.IndexOf("budget.CompleteCaller()", StringComparison.Ordinal);
            Assert.True(advance > 0, "the resume offset never advances");

            // The guard has to be the statement immediately preceding the advance, not
            // merely somewhere earlier in the method: there are two `break`s on this
            // flag (one leaving the parts loop, one leaving the caller loop), and only the
            // second one guards the offset.
            int windowStart = Math.Max(0, advance - 200);
            var before = body.Substring(windowStart, advance - windowStart);
            Assert.Contains("if (!callerFullyScanned) break;", before, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Response_Carries_The_Scan_Completeness_Metadata()
        {
            // An incomplete scan must be distinguishable from a complete one, or the
            // bound is invisible and a partial answer reads as the whole answer.
            string body = FindCallerSitesBody();

            foreach (var field in new[] { "complete", "callersTotal", "callersScanned",
                                          "sourceBytesRead", "results", "elapsedMs",
                                          "nextCursor", "stopReason" })
                Assert.Contains("\"" + field + "\"", body, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Truncated_Zero_Result_Is_Not_Presented_As_A_Verified_Zero()
        {
            // The failure the issue calls out: a budget stop that found nothing must not
            // reach the guarded-zero path, because that path exists precisely to stop a
            // zero from being read as "safe to delete".
            string body = FindCallerSitesBody();

            // The guard has to be *conditioned* on completeness, not merely present as
            // text. A partial-result block that is unreachable - or reached regardless of
            // completeness - leaves the defect in place while still satisfying a
            // "does this string appear?" check.
            int guard = body.IndexOf("if (!scanComplete)", StringComparison.Ordinal);
            int crossCheck = body.IndexOf("TrySdkReferenceCrossCheck(canonicalName)", StringComparison.Ordinal);
            Assert.True(guard > 0,
                "the cross-check is not guarded by scan completeness, so a budget stop can be reported as a verified zero");
            Assert.True(crossCheck > 0, "the SDK cross-check was not found");
            Assert.True(guard < crossCheck,
                "the incomplete-scan guard must come before the cross-check, or a partial scan still reaches it");
            Assert.Contains("CallerSitesPartial", body, StringComparison.Ordinal);
            Assert.Contains("verifiedZero", body, StringComparison.Ordinal);
        }

        [Fact]
        public void Callers_Are_De_Duplicated_Before_They_Are_Paged()
        {
            // The index can record the same caller twice. Without de-duplication a page
            // boundary could split the duplicates across two responses and the caller
            // would see the same site twice.
            string body = FindCallerSitesBody();

            Assert.Contains("Distinct(StringComparer.OrdinalIgnoreCase)", body, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Method_Accepts_A_Resume_Cursor()
        {
            Assert.Contains("int cursor = 0", Source(), StringComparison.Ordinal);
        }
    }
}
