using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Regression coverage for the index bootstrap that never fired on the modern HTTP
    // transport.
    //
    // The bootstrap used to `await WorkerWarmupCompleted.Task` with no timeout. That task is
    // completed only by TriggerWorkerWarmupOnce's finally block, and that trigger fires only
    // from `initialize` when sessionContextEnabled is true — Program.Http.cs passes `!modern`,
    // so a modern Streamable-HTTP client never started it. The bootstrap had already consumed
    // its one-shot flag by then, so it parked on the await forever and BulkIndex was never
    // sent: a healthy Worker, a KB reporting index Cold/0, and no build in flight. The read
    // gate cannot self-heal that state (Cold is excluded from IsRestoredSnapshotAwaitingDelta),
    // and the one tool that would have started a build from inside the Worker — genexus_query
    // — is itself gated, so only a manual `genexus_lifecycle action=index` recovered.
    public class IndexBootstrapWarmupWaitTests
    {
        [Fact]
        public async Task DoesNotWait_WhenTheWarmPassWasNeverStarted()
        {
            // The HTTP/sessionless case: warmup never ran, so its completion source can never
            // be signalled. The bootstrap must proceed instead of parking on it.
            var neverSignalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            Task wait = Program.AwaitWarmupBeforeBootstrapAsync(
                "KB-Http",
                warmupCompleted: neverSignalled,
                ceilingMsOverride: 60_000,
                warmupStarted: () => false);

            Task finished = await Task.WhenAny(wait, Task.Delay(2_000));

            Assert.Same(wait, finished);
            await wait; // must not throw
        }

        [Fact]
        public async Task Waits_AndContinues_WhenTheWarmPassCompletes()
        {
            var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            Task wait = Program.AwaitWarmupBeforeBootstrapAsync(
                "KB-Stdio",
                warmupCompleted: source,
                ceilingMsOverride: 10_000,
                warmupStarted: () => true);

            // Serialization intent preserved: the bootstrap really does wait for a running
            // warm pass rather than racing it for the KB.
            Assert.False(wait.IsCompleted);
            source.TrySetResult(true);
            await Task.WhenAny(wait, Task.Delay(2_000));

            Assert.True(wait.IsCompletedSuccessfully);
        }

        [Fact]
        public async Task GivesUpOnAWedgedWarmPass_InsteadOfStallingForever()
        {
            // A warm pass that hangs must not be able to hold the index bootstrap hostage.
            var wedged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            Task wait = Program.AwaitWarmupBeforeBootstrapAsync(
                "KB-Wedged",
                warmupCompleted: wedged,
                ceilingMsOverride: 50,
                warmupStarted: () => true);

            Task finished = await Task.WhenAny(wait, Task.Delay(5_000));

            Assert.Same(wait, finished);
            await wait;
        }

        [Fact]
        public void Ceiling_IsBounded()
        {
            // Guards against the ceiling being edited into something effectively infinite,
            // which would reintroduce the original stall.
            Assert.InRange(Program.WarmupBeforeBootstrapWaitCeilingMs, 1_000, 120_000);
        }
    }
}
