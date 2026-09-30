using System;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// A lifecycle job's stored result is refreshed from the worker before the
    /// Gateway reports on it, and that rule existed twice - once in the
    /// <c>action=result</c> intercept, once in the <c>action=status</c> long-poll -
    /// as the same eighteen lines each. It is now
    /// <c>RefreshLifecycleResultFromWorkerAsync</c>.
    ///
    /// This is pinned structurally, and the reason belongs in the record rather
    /// than being glossed: the rule needs a worker to reconcile against, so it
    /// cannot be exercised without the process-level fixtures that are
    /// permanently unavailable in this repository. The existing lifecycle tests
    /// cover <c>BuildJobResultEnvelope</c> and the <c>newWarnings</c> shaping, not
    /// this. So what is asserted here is the clause set, which is the substance
    /// of the rule: each condition below decides whether a stored result is
    /// considered stale, and dropping any one of them changes what gets reported.
    ///
    /// A drift between two copies of this condition would mean one action reports
    /// a stale result while the other refreshes - the exact failure Issue #27 item
    /// 1 describes, and one no single-action test would notice.
    /// </summary>
    public class LifecycleResultRefreshRuleTests
    {
        private static string Source() =>
            RepoSource.Read("src", "GxMcp.Gateway", "Program.LifecycleGateway.cs");

        /// <summary>
        /// The body of the shared rule. Comments are stripped: they explain the
        /// conditions by name, and counting prose would report a condition that is
        /// only being talked about.
        /// </summary>
        private static string RuleBody()
        {
            string body = SourceAssert.MethodBody(Source(),
                "private static async Task RefreshLifecycleResultFromWorkerAsync(");

            return RepoSource.WithoutComments(body);
        }

        [Fact]
        public void BothLifecycleActionsGoThroughTheSharedRule()
        {
            string source = RepoSource.WithoutComments(Source());

            // Exactly two: the result intercept and the status long-poll. A third
            // is a copy coming back; one is a path that stopped refreshing.
            Assert.Equal(2, SourceAssert.Count(source, "RefreshLifecycleResultFromWorkerAsync(probe, args, transportCancellation)"));
            Assert.Equal(1, SourceAssert.Count(source, "private static async Task RefreshLifecycleResultFromWorkerAsync("));

            // And neither action re-implements the rule inline any more. Counting
            // the stale-result clause file-wide at exactly one is what proves it:
            // the single occurrence is the helper's, and a second copy anywhere
            // would show up here.
            Assert.Equal(0, SourceAssert.Count(source, @"await ReconcileJobWithWorkerAsync(probe, ""genexus_lifecycle"", args)"));
            Assert.Equal(1, SourceAssert.Count(source, "storedStatus[\"newWarnings\"] != null"));
            Assert.Equal(1, SourceAssert.Count(source, "lock (job.SyncRoot) job.Result = fullResult;"));
        }

        [Fact]
        public void TheRuleStillReconcileTheJobBeforeReportingOnIt()
        {
            // First, because it is unconditional: the background poller may have
            // wedged, and a wedged poller is what leaves a finished build stuck at
            // "running" regardless of what is stored.
            string body = RuleBody();

            Assert.Equal(1, SourceAssert.Count(body, "await ReconcileJobWithWorkerAsync(job, \"genexus_lifecycle\", args);"));

            int reconcile = body.IndexOf("ReconcileJobWithWorkerAsync", StringComparison.Ordinal);
            int condition = body.IndexOf("job.Kind?.StartsWith", StringComparison.Ordinal);
            Assert.True(reconcile >= 0, "the reconciliation call is gone");
            Assert.True(condition > reconcile, "the staleness check must follow the reconciliation it depends on");
        }

        [Fact]
        public void OnlyAWarningBearingResultIsConsideredStale()
        {
            // `newWarnings` is what marks a stored result as a warning-bearing
            // snapshot that can be stale. Without this clause a still-forming
            // result would be re-read on every request, and the warning list the
            // client already holds would be replaced mid-flight.
            string body = RuleBody();

            Assert.Equal(1, SourceAssert.Count(body, "job.Result is JObject storedStatus"));
            Assert.Equal(1, SourceAssert.Count(body, "storedStatus[\"newWarnings\"] != null"));
        }

        [Fact]
        public void ALiveOrQueuedJobIsNeverRefreshed()
        {
            // A running or queued job has nothing final to refresh from; reading
            // its result back would replace live progress with a terminal answer.
            string body = RuleBody();

            Assert.Equal(1, SourceAssert.Count(body, "!string.Equals(job.Status, \"running\", StringComparison.OrdinalIgnoreCase)"));
            Assert.Equal(1, SourceAssert.Count(body, "!string.Equals(job.Status, \"queued\", StringComparison.OrdinalIgnoreCase)"));
        }

        [Fact]
        public void OnlyLifecycleJobsWithAWorkerTaskAreRefreshed()
        {
            string body = RuleBody();

            Assert.Equal(1, SourceAssert.Count(body, "job.Kind?.StartsWith(\"lifecycle/\", StringComparison.OrdinalIgnoreCase) == true"));
            Assert.Equal(1, SourceAssert.Count(body, "!string.IsNullOrWhiteSpace(job.WorkerTaskId)"));
        }

        [Fact]
        public void TheRefreshedResultIsWrittenUnderTheJobsOwnLock()
        {
            // The background poller writes the same field. An unsynchronised write
            // here would be a torn read rather than a visible failure, which is the
            // worst shape a bug can have.
            string body = RuleBody();

            Assert.Equal(1, SourceAssert.Count(body, "lock (job.SyncRoot) job.Result = fullResult;"));

            // And a null read leaves the stored result alone rather than clearing it.
            Assert.Equal(1, SourceAssert.Count(body, "if (fullResult != null)"));
        }

    }
}
