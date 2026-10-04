using System;
using System.Threading.Tasks;
using GxMcp.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Issue #369, part 3: <c>WorkerProcess._commandChannel</c> was
    /// <c>Channel.CreateUnbounded</c>, so a client that out-pushed the Worker's pipe
    /// reader retained an arbitrary burst in the Gateway <em>before</em> the Worker ever
    /// applied its own admission control. Bounded at last, the Gateway applies
    /// backpressure and a caller that cannot be served within the timeout gets an
    /// actionable, retryable error instead of unbounded memory growth.
    ///
    /// <para>
    /// The writer loop is stalled in each test, because a stalled pipe is exactly the
    /// production condition under which a bounded channel has to engage. With the loop
    /// running, the channel drains as fast as it is filled and the refusal is unreachable.
    /// </para>
    /// </summary>
    public class WorkerCommandChannelBoundTests
    {
        private static JObject Command(int i)
            => new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = i,
                ["method"] = "object",
                ["params"] = new JObject { ["action"] = "read" }
            };

        private static WorkerProcess NewWorker()
            => new WorkerProcess(new Configuration(), new KbHandle("kb1", "C:/kb1"));

        /// <summary>Fills the bounded channel to capacity.</summary>
        private static async Task Fill(WorkerProcess worker)
        {
            for (int i = 0; i < WorkerProcess.CommandChannelCapacity; i++)
                await worker.SendCommandAsync(Command(i));
        }

        /// <summary>Asserts that one more command past capacity is refused.</summary>
        private static Task<WorkerCommandQueueFullException> ExpectRefusal(WorkerProcess worker)
            => Assert.ThrowsAsync<WorkerCommandQueueFullException>(
                () => worker.SendCommandAsync(Command(9999)));

        [Fact]
        public async Task A_Burst_Is_Refused_Once_The_Channel_Is_Full_Rather_Than_Grown()
        {
            var worker = NewWorker();
            var gate = worker.StallCommandWriterForTest();
            try
            {
                var accepted = 0;
                WorkerCommandQueueFullException? refusal = null;
                try
                {
                    for (int i = 0; i < WorkerProcess.CommandChannelCapacity + 32; i++)
                    {
                        await worker.SendCommandAsync(Command(i));
                        accepted++;
                    }
                }
                catch (WorkerCommandQueueFullException ex)
                {
                    refusal = ex;
                }

                // Retention is capped at the channel capacity: unbounded, all 544 commands
                // would sit in the Gateway queue waiting for the Worker.
                Assert.Equal(WorkerProcess.CommandChannelCapacity, accepted);
                Assert.NotNull(refusal);
            }
            finally
            {
                worker.ResumeCommandWriterForTest();
                gate.Dispose();
                worker.Stop();
            }
        }

        [Fact]
        public async Task The_Refusal_Is_Retryable_And_Says_The_Command_Was_Not_Sent()
        {
            var worker = NewWorker();
            var gate = worker.StallCommandWriterForTest();
            try
            {
                await Fill(worker);
                var refusal = await ExpectRefusal(worker);

                Assert.Equal(WorkerProcess.CommandChannelCapacity, refusal.Capacity);
                Assert.True(refusal.WaitedMs > 0);
                Assert.Equal("kb1", refusal.KbAlias);
                Assert.Equal("object", refusal.Method);
                // The refused command was never enqueued, so a retry cannot duplicate its
                // effect. The caller is told so rather than left to guess.
                Assert.Contains("NOT sent", refusal.Message);
            }
            finally
            {
                worker.ResumeCommandWriterForTest();
                gate.Dispose();
                worker.Stop();
            }
        }

        [Fact]
        public async Task A_Refused_Command_Does_Not_Inflate_The_Queued_Command_Counter()
        {
            // The counter is incremented before the write is attempted, so a refusal has to
            // decrement it. Otherwise every refusal leaks queue depth for a command that was
            // never enqueued, and the depth the Gateway reports climbs forever.
            var worker = NewWorker();
            var gate = worker.StallCommandWriterForTest();
            try
            {
                for (int i = 0; i < WorkerProcess.CommandChannelCapacity; i++)
                    await worker.SendCommandAsync(Command(i));

                Assert.Equal(WorkerProcess.CommandChannelCapacity, worker.QueuedCommandsForTest);

                await ExpectRefusal(worker);

                Assert.Equal(WorkerProcess.CommandChannelCapacity, worker.QueuedCommandsForTest);
            }
            finally
            {
                worker.ResumeCommandWriterForTest();
                gate.Dispose();
                worker.Stop();
            }
        }

        [Fact]
        public void The_Capacity_Is_Bounded_And_Positive()
        {
            Assert.InRange(WorkerProcess.CommandChannelCapacity, 1, 100_000);
            Assert.InRange(WorkerProcess.CommandQueueEnqueueTimeout,
                TimeSpan.FromMilliseconds(1), TimeSpan.FromMinutes(5));
        }

        [Fact]
        public void Both_The_Pipe_And_The_Shared_Broker_Relay_Drain_The_Bounded_Channel()
        {
            // Issue #369 asked for the shared-host relay to be verified for the same
            // property. It holds structurally: there is exactly one command channel, and
            // the shared attachment write and the direct pipe write are both alternatives
            // inside the single writer loop that drains it. A second, unbounded path for
            // shared mode would not be caught by the behavioural tests above.
            var source = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Gateway", "WorkerProcess.cs");

            Assert.Equal(1, SourceAssert.Count(source, "Channel.CreateBounded<QueuedCommand>"));
            Assert.Equal(0, SourceAssert.Count(source, "Channel.CreateUnbounded"));

            // The shared relay sits in the writer loop, downstream of the bounded channel.
            var sharedSend = source.IndexOf("_sharedConnection.SendAsync", StringComparison.Ordinal);
            var channelRead = source.IndexOf("_commandChannel.Reader.TryRead", StringComparison.Ordinal);
            Assert.True(sharedSend > 0, "the shared-host relay write was not found");
            Assert.True(channelRead > 0, "the bounded channel read was not found");
            Assert.True(channelRead < sharedSend,
                "the shared-host relay must be downstream of the bounded command channel");
        }

        [Fact]
        public async Task Draining_The_Channel_Makes_Room_Again_So_The_Refusal_Is_Not_Permanent()
        {
            var worker = NewWorker();
            var gate = worker.StallCommandWriterForTest();
            try
            {
                await Fill(worker);
                await ExpectRefusal(worker);

                for (int i = 0; i < WorkerProcess.CommandChannelCapacity; i++)
                    worker.ReleaseOneQueuedCommandForTest();

                // Capacity is back, so a deliberate retry is accepted rather than refused
                // forever.
                await worker.SendCommandAsync(Command(4242));
            }
            finally
            {
                worker.ResumeCommandWriterForTest();
                gate.Dispose();
                worker.Stop();
            }
        }
    }
}
