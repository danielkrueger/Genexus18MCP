using System;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #335: a shared Worker had no supervision. The broker monitors child exit and
    /// attachment lifetime, but nothing measured the child's memory or distinguished a
    /// progressing SDK call from a deadlocked one - and the Gateway could not do it either,
    /// because in shared mode it does not own the child process and had no figures at all.
    ///
    /// <para>
    /// These cover the broker's half: the classification and the election rule. The
    /// Gateway's half - that shared startup actually installs the health loop, which it did
    /// not - is a routing fact pinned in <see cref="SharedSupervisionRoutingTests"/>.
    /// </para>
    /// </summary>
    public class SharedWorkerSupervisionTests
    {
        [Fact]
        public void An_Inactive_Lane_Is_Idle()
        {
            Assert.Equal("idle", SharedWorkerHostRuntime.ClassifySdkState(active: false, sawProgress: false, lastProgressMs: -1));
            Assert.Equal("idle", SharedWorkerHostRuntime.ClassifySdkState(active: false, sawProgress: true, lastProgressMs: 0));
        }

        [Fact]
        public void A_Busy_Lane_That_Is_Moving_Is_Progressing_Not_Stalled()
        {
            // The distinction that protects long work. A build that reported movement a
            // second ago is healthy, however long it has been running.
            Assert.Equal("busy-progressing",
                SharedWorkerHostRuntime.ClassifySdkState(active: true, sawProgress: true, lastProgressMs: 1_000));

            // Still progressing right at the edge of the window: inside is inside.
            Assert.Equal("busy-progressing",
                SharedWorkerHostRuntime.ClassifySdkState(active: true, sawProgress: true,
                    lastProgressMs: SharedWorkerHostRuntime.SdkStallAfterMs - 1));
        }

        [Fact]
        public void A_Busy_Lane_That_Went_Quiet_Past_The_Window_Is_Stalled()
        {
            Assert.Equal("busy-stalled",
                SharedWorkerHostRuntime.ClassifySdkState(active: true, sawProgress: true,
                    lastProgressMs: SharedWorkerHostRuntime.SdkStallAfterMs));

            Assert.Equal("busy-stalled",
                SharedWorkerHostRuntime.ClassifySdkState(active: true, sawProgress: true,
                    lastProgressMs: SharedWorkerHostRuntime.SdkStallAfterMs * 10));
        }

        [Fact]
        public void A_Busy_Lane_With_No_Observed_Progress_Is_Unproven_Not_Stalled()
        {
            // One sample cannot tell a healthy short call from a deadlocked one. Calling it
            // stalled would kill a Worker seconds into ordinary work, so it gets its own
            // answer and is never a recovery target.
            Assert.Equal("busy-unproven",
                SharedWorkerHostRuntime.ClassifySdkState(active: true, sawProgress: false, lastProgressMs: -1));

            // -1 means "never observed", which is different from 0 ("observed just now").
            Assert.Equal("busy-unproven",
                SharedWorkerHostRuntime.ClassifySdkState(active: true, sawProgress: true, lastProgressMs: -1));
        }

        [Fact]
        public void The_Stall_Window_Matches_The_Connection_Recover_Window()
        {
            // Both paths judge the same Worker. If these two windows drifted apart, a
            // Gateway could call a lane healthy while connection-recover was recycling it,
            // and the two would disagree about the same lane at the same instant.
            string worker = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "SharedWorkerHost.cs");
            string gateway = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Gateway", "WorkerLivenessClassifier.cs");

            Assert.Contains("SdkStallAfterMs = 90_000", worker, StringComparison.Ordinal);
            Assert.Contains("SdkStallAfterMs = 90_000", gateway, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Broker_Reports_The_Fields_A_Shared_Gateway_Cannot_Obtain_Itself()
        {
            // A shared Gateway owns no Process, so every one of these is unreachable for
            // it by construction. They are the reason the report exists.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "SharedWorkerHost.cs");

            foreach (var field in new[]
            {
                "childPid", "childAlive", "workingSetBytes", "privateBytes",
                "queueTotal", "sdkBusy", "sdkElapsedMs", "sdkLastProgressMs",
                "sdkState", "brokerGeneration", "recycleElected",
            })
                Assert.Contains("[\"" + field + "\"]", source, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Report_Rides_On_The_Existing_Heartbeat_Acknowledgement()
        {
            // Extending heartbeat_ack rather than adding a frame type is what keeps this
            // off the protocol-migration path the issue warns about: an older Gateway
            // ignores the new fields, and an older broker simply sends none.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "SharedWorkerHost.cs");

            Assert.Contains("BuildHeartbeatAck(", source, StringComparison.Ordinal);
            Assert.Contains("\"heartbeat_ack\"", source, StringComparison.Ordinal);
            // The identity/session frame type list must not have grown a new entry.
            string protocol = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "SharedWorkerHostProtocol.cs");
            Assert.DoesNotContain("\"vitals\"", protocol, StringComparison.Ordinal);
            Assert.DoesNotContain("\"supervision\"", protocol, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Report_That_Cannot_Be_Gathered_Degrades_To_Unavailable()
        {
            // A diagnostic that throws must not take down the attachment loop, and must not
            // be reported as a healthy reading either.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "SharedWorkerHost.cs");

            Assert.Contains("[\"available\"] = false", source, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Sdk_Lane_Reading_Reuses_The_Workers_Own_Projection()
        {
            // The broker must not keep a second copy of the busy/progress bookkeeping. If
            // it did, this path and connection-recover could report different states for
            // the same lane at the same instant.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "SharedWorkerHost.cs");

            Assert.Contains("Program.GetSdkBusyStatus()", source, StringComparison.Ordinal);
        }

        [Fact]
        public void A_New_Child_Starts_A_New_Generation_And_Ends_The_Election()
        {
            // The single-recycle property rests entirely on this. If the election flag
            // survived a respawn, every attachment would keep seeing it true and could each
            // act - which is the duplicate election the issue forbids.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "SharedWorkerHost.cs");

            int start = source.IndexOf("_child = child;", StringComparison.Ordinal);
            Assert.True(start > 0, "the child assignment was not found");
            var window = source.Substring(start, Math.Min(500, source.Length - start));

            Assert.Contains("Interlocked.Increment(ref _brokerGeneration)", window, StringComparison.Ordinal);
            Assert.Contains("Volatile.Write(ref _recycleElected, 0)", window, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Stall_Window_Is_Long_Enough_Not_To_Condemn_A_Real_Build()
        {
            // Wrong in the "stalled" direction destroys an operation in progress, which
            // costs more than waiting.
            Assert.InRange(SharedWorkerHostRuntime.SdkStallAfterMs, 30_000, 600_000);
        }
    }

    /// <summary>
    /// The Gateway half of issue #335. The reported defect is a routing one - shared
    /// startup returned before the health task was installed - so these assert the shape of
    /// the production wiring rather than trying to drive a live broker.
    /// </summary>
    public class SharedSupervisionRoutingTests
    {
        private static string WorkerProcess() => GxMcp.TestSupport.RepoSource.WithoutComments(
            "src", "GxMcp.Gateway", "WorkerProcess.cs");

        private static string Connection() => GxMcp.TestSupport.RepoSource.WithoutComments(
            "src", "GxMcp.Gateway", "SharedWorkerConnection.cs");

        [Fact]
        public void Shared_Startup_Installs_The_Health_Task()
        {
            // The defect. `StartShared(...)` used to be followed by a bare `return`, so the
            // loop that owns idle reaping, heap recycling and stall detection was never
            // started for a shared Worker.
            string source = WorkerProcess();

            int shared = source.IndexOf("StartShared(workerPath", StringComparison.Ordinal);
            Assert.True(shared > 0, "shared startup was not found");

            int ret = source.IndexOf("return;", shared, StringComparison.Ordinal);
            Assert.True(ret > shared, "the shared-startup return was not found");

            var between = source.Substring(shared, ret - shared);
            Assert.Contains("EnsureHealthCheckTask()", between, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Health_Loop_Is_Reachable_Without_A_Local_Process()
        {
            // The second half of the gap: the loop was gated on `_process != null`, which a
            // shared Gateway never satisfies because it does not own the child.
            string source = WorkerProcess();

            Assert.DoesNotContain("if (_process != null && !_process.HasExited)\n", source.Replace("\r\n", "\n"), StringComparison.Ordinal);
            Assert.Contains("bool hasVitals = _process != null && !_process.HasExited;", source, StringComparison.Ordinal);
            Assert.Contains("_sharedConnection?.IsConnected == true", source, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Shared_Worker_Cannot_Be_Recycled_On_One_Gateways_Own_Judgement()
        {
            // The two-client requirement: one Gateway must never unilaterally recycle
            // another client's active child.
            string source = WorkerProcess();

            Assert.Contains("bool mayRecycle = !IsSharedHostMode || SupervisionCanRecycle();", source, StringComparison.Ordinal);
            Assert.Contains("recycleElected", source, StringComparison.Ordinal);
            // And every recovery branch is behind that gate, not just the first.
            Assert.Contains("if (mayRecycle && ShouldStopForIdle()", source, StringComparison.Ordinal);
            Assert.Contains("if (mayRecycle && ShouldRecycleForHeap(", source, StringComparison.Ordinal);
        }

        [Fact]
        public void Queued_Work_And_A_Progressing_Lane_Block_Recovery()
        {
            // Active or queued work must prevent unsafe recovery - for every attachment,
            // because the broker elects on the child's state and not one client's view.
            string source = WorkerProcess();

            int canRecycle = source.IndexOf("private bool SupervisionCanRecycle()", StringComparison.Ordinal);
            Assert.True(canRecycle > 0, "the recycle decision was not found");
            var body = source.Substring(canRecycle, Math.Min(1800, source.Length - canRecycle));

            Assert.Contains("\"busy-progressing\"", body, StringComparison.Ordinal);
            Assert.Contains("\"busy-unproven\"", body, StringComparison.Ordinal);
            Assert.Contains("[\"queueTotal\"]", body, StringComparison.Ordinal);
            Assert.Contains("[\"recycleElected\"]", body, StringComparison.Ordinal);
        }

        [Fact]
        public void No_Supervision_Report_Means_No_Recovery_Decision()
        {
            // Before the first heartbeat there is no evidence at all. Acting then would be
            // deciding on nothing, which is the failure this path exists to remove.
            string source = WorkerProcess();

            int canRecycle = source.IndexOf("private bool SupervisionCanRecycle()", StringComparison.Ordinal);
            var body = source.Substring(canRecycle, Math.Min(600, source.Length - canRecycle));

            Assert.Contains("if (s == null)", body, StringComparison.Ordinal);
            int nullCheck = body.IndexOf("if (s == null)", StringComparison.Ordinal);
            var tail = body.Substring(nullCheck, Math.Min(300, body.Length - nullCheck));
            Assert.Contains("return false;", tail, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Heartbeat_Supervision_Block_Is_Not_Swallowed()
        {
            // This acknowledgement used to be discarded - which is exactly why a shared
            // Worker had no vitals: the one party able to answer was being ignored.
            string source = Connection();

            int ack = source.IndexOf("IsControlFrame(frame, \"heartbeat_ack\")", StringComparison.Ordinal);
            Assert.True(ack > 0, "the heartbeat acknowledgement handler was not found");
            var window = source.Substring(ack, Math.Min(2400, source.Length - ack));

            Assert.Contains("SupervisionReceived?.Invoke(supervision)", window, StringComparison.Ordinal);
            // Still a control frame, so it must not fall through to the client.
            Assert.Contains("continue;", window, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Broker_Report_Is_Subscribed_On_The_Shared_Connection()
        {
            string source = WorkerProcess();
            Assert.Contains("connection.SupervisionReceived += supervision =>", source, StringComparison.Ordinal);
            Assert.Contains("ObserveSharedSupervision(supervision)", source, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Observed_Generation_Is_Available_For_Comparison()
        {
            // Clients must be able to tell they are looking at the same child, and that it
            // changed - criterion 4 of the issue.
            string source = WorkerProcess();
            Assert.Contains("internal long SharedBrokerGeneration", source, StringComparison.Ordinal);
            Assert.Contains("[\"brokerGeneration\"]", source, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Child_Exit_And_Ownership_Reconcile_Stay_Isolated_Path_Only()
        {
            // Ownership is this process's own lease. Reconciling it against a broker-owned
            // child would be reconciling the wrong thing.
            string source = WorkerProcess();

            int reconcile = source.IndexOf("WorkerOwnershipRegistry.Reconcile(SpawnedExePath", StringComparison.Ordinal);
            Assert.True(reconcile > 0, "the reconcile call was not found");

            // The reconcile must be reachable only when this Gateway owns the child. The
            // guard is the statement immediately above it, so that is what is checked -
            // an earlier occurrence of "hasVitals" elsewhere would not prove anything.
            int lineStart = source.LastIndexOf('\n', reconcile);
            var preceding = source.Substring(Math.Max(0, lineStart - 400), reconcile - Math.Max(0, lineStart - 400));
            Assert.Contains("hasVitals", preceding, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Progressing_Sdk_Lane_Blocks_The_Idle_And_Heap_Reaps()
        {
            // Recycling a progressing operation destroys the operation, not just the Worker.
            string source = WorkerProcess();

            Assert.Contains("!sdkProgressing", source, StringComparison.Ordinal);
            Assert.Contains("bool sdkProgressing = string.Equals(sdkState, \"busy-progressing\"", source, StringComparison.Ordinal);
        }
    }
}