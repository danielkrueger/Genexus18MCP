using System;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // BUG-03: a worker wedged mid-command (alive, never responds) used to sit forever —
    // ShouldStopForIdle refuses to reap while _inFlightCommands > 0, and the gateway op
    // timeout only marks the operation timed out without touching the worker. These tests
    // cover the extracted decision logic (HasWedgedCommand) and the bookkeeping cleanup
    // (CompleteInFlight removing the timestamp on normal completion), independent of the
    // timer-driven health-check loop itself.
    public class WorkerWedgedDetectionTests
    {
        private const string HealthLoop =
            "private async Task RunHealthCheckAsync(CancellationToken ct)";

        private const string WedgedSilenceProbe =
            "double silentSec = (DateTime.UtcNow - _lastResponse).TotalSeconds;";

        private static Configuration CfgWithCeiling(int minutes) =>
            new Configuration { Server = new ServerConfig { WedgedCommandTimeoutMinutes = minutes } };

        [Fact]
        public void HasWedgedCommand_False_WhenNoCommandsInFlight()
        {
            var worker = new WorkerProcess(CfgWithCeiling(15), new KbHandle("test", "C:\\fake"));

            Assert.False(worker.HasWedgedCommand(out var age));
            Assert.Equal(TimeSpan.Zero, age);
        }

        [Fact]
        public void HasWedgedCommand_False_UnderCeiling()
        {
            var worker = new WorkerProcess(CfgWithCeiling(15), new KbHandle("test", "C:\\fake"));
            // A long-running but legitimate build: 5 minutes in, ceiling is 15.
            worker.SeedInFlightForTest("op-1", DateTime.UtcNow.AddMinutes(-5));

            Assert.False(worker.HasWedgedCommand(out var age));
            Assert.True(age.TotalMinutes < 15);
        }

        [Fact]
        public void HasWedgedCommand_True_PastCeiling()
        {
            var worker = new WorkerProcess(CfgWithCeiling(15), new KbHandle("test", "C:\\fake"));
            // Genuinely wedged: unanswered for 20 minutes against a 15-minute ceiling.
            worker.SeedInFlightForTest("op-1", DateTime.UtcNow.AddMinutes(-20));

            Assert.True(worker.HasWedgedCommand(out var age));
            Assert.True(age.TotalMinutes >= 15);
        }

        [Fact]
        public void HasWedgedCommand_UsesOldestEntry_WhenMultipleInFlight()
        {
            var worker = new WorkerProcess(CfgWithCeiling(15), new KbHandle("test", "C:\\fake"));
            worker.SeedInFlightForTest("recent", DateTime.UtcNow.AddMinutes(-1));
            worker.SeedInFlightForTest("stale", DateTime.UtcNow.AddMinutes(-20));

            Assert.True(worker.HasWedgedCommand(out var age));
            Assert.True(age.TotalMinutes >= 15);
        }

        [Fact]
        public void CompleteInFlight_RemovesTimestamp_OnNormalCompletion()
        {
            var worker = new WorkerProcess(CfgWithCeiling(15), new KbHandle("test", "C:\\fake"));
            worker.SeedInFlightForTest("op-1", DateTime.UtcNow.AddMinutes(-20));
            Assert.Equal(1, worker.InFlightStartTimesCountForTest);

            worker.CompleteInFlightForTest("op-1");

            Assert.Equal(0, worker.InFlightStartTimesCountForTest);
            Assert.False(worker.HasWedgedCommand(out _));
        }

        [Fact]
        public void WedgedCommandTimeoutMinutes_DefaultsTo15_WhenUnset()
        {
            // Config with no Server section at all — constructor must clamp/default
            // rather than throw, mirroring the existing WorkerIdleTimeoutMinutes pattern.
            var worker = new WorkerProcess(new Configuration(), new KbHandle("test", "C:\\fake"));
            worker.SeedInFlightForTest("op-1", DateTime.UtcNow.AddMinutes(-14));

            Assert.False(worker.HasWedgedCommand(out _));
        }

        /// <summary>
        /// <b>Source-shape, and that is a forced choice - not a shortcut.</b>
        ///
        /// <para>
        /// What is being guarded is a control-flow fact inside <c>RunHealthCheckAsync</c>:
        /// the wedged reap is reached only when <c>mayRecycle</c> holds, so a shared
        /// Worker is reaped by the broker-elected Gateway and by no one else. The
        /// decision and its effect are separated by everything a unit test cannot
        /// supply: a live <c>WorkerProcess</c>, a running pipe, two Gateway processes
        /// attached to one broker, and 15-second timer passes. The same reason
        /// <c>WorkerProcess.cs:1839</c> carves out seams for <c>HasWedgedCommand</c> and
        /// nothing else. Adding a seam for the loop itself was a design decision and was
        /// not authorised, so the observable link is the source.
        /// </para>
        ///
        /// <para>
        /// <b>Read through <c>RepoSource.WithoutComments</c>, never
        /// <c>RepoSource.Read</c>.</b> The comment above the gate in production code
        /// names <c>mayRecycle</c>, <c>worker_wedged_shutdown</c>,
        /// <c>worker_wedged_observed</c> and <c>_process.Id</c> in order to explain why
        /// the gate exists - which is the right way to write it and the wrong way to
        /// test it. An unstripped substring assertion here passes with the gate deleted.
        /// Stripping is what makes this a guard instead of a comment echo.
        /// </para>
        ///
        /// <para>
        /// <b>What would be needed for a real test</b>, for the reviewer to judge the
        /// cost: extract the per-pass recovery decision (given the supervision record and
        /// the wedge facts, return the stop reason or none) into a pure function, and
        /// have <c>RunHealthCheckAsync</c> call it. That is a production refactor of the
        /// supervision path, and it is a bigger change than the defect it would cover.
        /// </para>
        /// </summary>
        [Fact]
        public void The_Wedged_Reap_Is_Gated_On_The_Broker_Election()
        {
            string loop = SourceAssert.MethodBody(
                RepoSource.WithoutComments("src", "GxMcp.Gateway", "WorkerProcess.cs"),
                HealthLoop);

            // Anchored on the silence probe rather than on `if (silentSec >= ...)`: the
            // MethodBody scan starts at the first `{` after the anchor and returns when
            // the braces balance, so this hands back the threshold branch's body - the
            // gate and the non-elected fall-through. The trailing `else` is outside that
            // region, which is why its line is asserted from `loop` and by absence from
            // `wedged` rather than by extracting the block.
            string wedged = SourceAssert.MethodBody(loop, WedgedSilenceProbe);

            Assert.True(
                SourceAssert.Count(wedged, "StopProcess(WorkerStopReason.Wedged);") == 1,
                "the wedged branch must contain exactly one reap, so that the gate "
                + "assertion below is about that call and not about one of several.");

            // The load-bearing half: the reap is INSIDE the gate, not beside it. A guard
            // that only counted `if (mayRecycle)` occurrences would stay green with the
            // StopProcess moved out from under it.
            string gate = SourceAssert.MethodBody(wedged, "if (mayRecycle)");
            Assert.Contains("StopProcess(WorkerStopReason.Wedged);", gate, StringComparison.Ordinal);

            // The elected path claims a shutdown; the non-elected path must not be able to
            // borrow that claim. `worker_wedged_shutdown` is the token an operator greps
            // for to establish that a reap happened, and these two strings share no
            // substring, so neither assertion can be satisfied by the other.
            Assert.Contains("worker_wedged_shutdown", gate, StringComparison.Ordinal);
            Assert.DoesNotContain("worker_wedged_observed", gate, StringComparison.Ordinal);

            // ...and the non-elected path is not silent either: a wedged Worker watched
            // by two Gateways would otherwise produce no signal at all from this one.
            Assert.Contains("worker_wedged_observed", wedged, StringComparison.Ordinal);

            // The `else` is part of the same decision, and it is asserted as a property of
            // the region rather than by extracting the block: before the gate the
            // `continue` above made the "still emitting output" line unreachable once the
            // ceiling was crossed. Deleting the `else` keyword would move that line INSIDE
            // the silent branch, so a non-elected Gateway past the ceiling would report a
            // silent Worker as progressing. So the message must exist exactly once in the
            // loop, and must not be reachable from the silent branch at all.
            Assert.True(
                SourceAssert.Count(loop, "still emitting output") == 1,
                "the 'still emitting output' line must be unique in the health loop, so "
                + "that its position outside the silent branch is meaningful.");
            Assert.DoesNotContain("still emitting output", wedged, StringComparison.Ordinal);
        }

        /// <summary>
        /// The context the wedged gate is part of: all three recovery sites in the loop
        /// are broker-gated, so the wedged one is the third of three rather than an
        /// exception. Pinned as full conditions because a bare <c>mayRecycle</c> count
        /// cannot distinguish a gate from a mention, and because a later edit that drops
        /// the gate from either sibling would silently restore the two-Gateways-one-
        /// Worker reap this file is about.
        /// </summary>
        [Theory]
        [InlineData("if (mayRecycle && ShouldStopForIdle() && !sdkProgressing)", "StopProcess(WorkerStopReason.IdleTimeout);")]
        [InlineData("if (mayRecycle && ShouldRecycleForHeap(out long wsBytes) && !sdkProgressing)", "StopProcess(WorkerStopReason.HeapRecycle);")]
        public void Every_Idle_And_Heap_Reap_Stays_Broker_Gated(string condition, string reap)
        {
            string loop = SourceAssert.MethodBody(
                RepoSource.WithoutComments("src", "GxMcp.Gateway", "WorkerProcess.cs"),
                HealthLoop);

            Assert.Equal(1, SourceAssert.Count(loop, condition));
            Assert.Contains(reap, SourceAssert.MethodBody(loop, condition), StringComparison.Ordinal);
        }
    }
}
