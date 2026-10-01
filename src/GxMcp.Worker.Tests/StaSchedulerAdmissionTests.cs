using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #341: the STA scheduler's per-client queues were unbounded and its wait was
    /// strict-priority. A burst could retain an arbitrary number of requests and their
    /// payloads, and a sustained P0 stream could defer already-accepted P1/P2 work
    /// forever - and a caller that was told "accepted" had no signal that would let it
    /// recover.
    ///
    /// <para>
    /// These are behavioural, against the real scheduler: admission budgets, the
    /// guarantee that a rejected command never executes, and the aging rule that bounds
    /// a lower-priority wait. They need no SDK, because the scheduler is pure
    /// bookkeeping.
    /// </para>
    /// </summary>
    public class StaSchedulerAdmissionTests
    {
        private static ScheduledCommandItem Item(CommandPriority priority, string client = "c1", string id = "1")
            => new ScheduledCommandItem
            {
                Priority = priority,
                ClientId = client,
                IdJson = id,
                EnqueuedAtUtc = DateTime.UtcNow
            };

        [Fact]
        public void The_Budgets_Are_Bounded_And_Positive()
        {
            Assert.InRange(StaScheduler.MaxQueuedCommands, 1, 100_000);
            Assert.InRange(StaScheduler.MaxQueuedBytes, 1L, 4L * 1024 * 1024 * 1024);
            Assert.InRange(StaScheduler.PriorityAgingWindow, TimeSpan.FromMilliseconds(1), TimeSpan.FromMinutes(5));
        }

        [Fact]
        public void Admission_Stops_At_The_Count_Budget_And_Says_Why()
        {
            var s = new StaScheduler();
            int accepted = 0;
            for (int i = 0; i < StaScheduler.MaxQueuedCommands + 50; i++)
                if (s.TryEnqueue(Item(CommandPriority.P1_Normal, "c1", i.ToString()))) accepted++;

            Assert.Equal(StaScheduler.MaxQueuedCommands, accepted);
            Assert.Equal(StaScheduler.MaxQueuedCommands, s.QueuedCount);
            Assert.Equal("queue_full", s.LastAdmissionError);
        }

        [Fact]
        public void A_Rejected_Command_Never_Executes_And_A_Deliberate_Retry_Is_Safe()
        {
            // The one outcome a caller cannot recover from: told "refused", then seeing
            // the work happen anyway.
            var s = new StaScheduler();
            for (int i = 0; i < StaScheduler.MaxQueuedCommands; i++)
                s.TryEnqueue(Item(CommandPriority.P1_Normal, "c1", "filler" + i));

            var refused = Item(CommandPriority.P0_Interactive, "c2", "must-not-run");
            Assert.False(s.TryEnqueue(refused));
            Assert.Equal("queue_full", s.LastAdmissionError);

            int drained = 0;
            while (s.TryTakeNext(out var taken)) { drained++; Assert.NotEqual("must-not-run", taken.IdJson); }
            Assert.Equal(StaScheduler.MaxQueuedCommands, drained);

            // Capacity is back, and a deliberate retry is accepted and does run.
            Assert.True(s.TryEnqueue(refused));
            Assert.True(s.TryTakeNext(out var retried));
            Assert.Equal("must-not-run", retried.IdJson);
        }

        [Fact]
        public void Draining_Frees_The_Budget_Rather_Than_Exhausting_It_Permanently()
        {
            // Without releasing accounting on dequeue the counters only grow and the
            // scheduler becomes a self-inflicted denial of service after a while.
            var s = new StaScheduler();
            for (int round = 0; round < 5; round++)
            {
                Assert.True(s.TryEnqueue(Item(CommandPriority.P1_Normal, "c1", "r" + round)));
                Assert.Equal(1, s.QueuedCount);
                Assert.True(s.TryTakeNext(out _));
                Assert.Equal(0, s.QueuedCount);
                Assert.Equal(0L, s.QueuedBytes);
            }
        }

        [Fact]
        public void Accounting_Never_Goes_Negative()
        {
            // A negative byte count would read as "not full" forever, which is how a
            // bounded queue silently becomes unbounded again.
            var s = new StaScheduler();
            s.TryEnqueue(Item(CommandPriority.P1_Normal));
            s.TryTakeNext(out _);
            s.TryTakeNext(out _); // extra take on an empty queue
            Assert.Equal(0, s.QueuedCount);
            Assert.Equal(0L, s.QueuedBytes);
        }

        [Fact]
        public void A_Command_Larger_Than_The_Whole_Budget_Is_Refused_On_Its_Own_Merits()
        {
            // Admitting it would evict the entire backlog for one request, so the refusal
            // must not depend on how much of the budget happens to be spent.
            var s = new StaScheduler();
            // Sized from the budget rather than hard-coded, so the guard keeps its
            // meaning if the budget is ever retuned.
            var huge = Item(CommandPriority.P1_Normal, "c1",
                new string('x', (int)(StaScheduler.MaxQueuedBytes / 2) + 1024));

            Assert.False(s.TryEnqueue(huge));
            Assert.Equal("payload_too_large", s.LastAdmissionError);
            Assert.Equal(0, s.QueuedCount);
        }

        [Fact]
        public void A_Fresh_Lower_Priority_Item_Still_Loses_To_Interactive()
        {
            // Aging must not cost the interactive fast path: a read that has not waited
            // yet is still served before a bulk search that also has not waited.
            var s = new StaScheduler();
            s.TryEnqueue(Item(CommandPriority.P2_Background, "c1", "bulk"));
            s.TryEnqueue(Item(CommandPriority.P0_Interactive, "c1", "read"));

            Assert.True(s.TryTakeNext(out var first));
            Assert.Equal("read", first.IdJson);
        }

        [Fact]
        public void An_Aged_Lower_Priority_Item_Outranks_A_Fresh_Interactive_One()
        {
            // The starvation fix. Without it, a client sending reads continuously defers
            // everything else it submitted indefinitely.
            var s = new StaScheduler();
            s.TryEnqueue(Item(CommandPriority.P1_Normal, "c1", "normal"));
            s.TryEnqueue(Item(CommandPriority.P2_Background, "c1", "bulk"));

            // Age both past the window by rewriting their enqueue time, which is what
            // elapsed waiting means to the selector.
            Thread.Sleep(20);
            AgeAll(s, TimeSpan.FromSeconds(30));

            s.TryEnqueue(Item(CommandPriority.P0_Interactive, "c1", "read"));

            Assert.True(s.TryTakeNext(out var first));
            Assert.Equal("normal", first.IdJson);
        }

        [Fact]
        public void Aging_Keeps_Client_Fairness_Within_A_Priority()
        {
            // Aging promotes a priority, it does not turn the scheduler into FIFO by
            // arrival time across clients.
            var s = new StaScheduler();
            s.TryEnqueue(Item(CommandPriority.P1_Normal, "a", "a1"));
            s.TryEnqueue(Item(CommandPriority.P1_Normal, "b", "b1"));
            AgeAll(s, TimeSpan.FromSeconds(30));

            var order = new List<string>();
            while (s.TryTakeNext(out var item)) order.Add(item.IdJson);
            Assert.Equal(new[] { "a1", "b1" }, order);
        }

        [Fact]
        public void Timed_Out_Items_Release_Their_Capacity()
        {
            // A timeout drop leaves the queue for good, so holding its budget would leak
            // admission capacity on every timeout.
            var s = new StaScheduler();
            s.TryEnqueue(new ScheduledCommandItem
            {
                Priority = CommandPriority.P1_Normal,
                ClientId = "c1",
                IdJson = "stale",
                EnqueuedAtUtc = DateTime.UtcNow.AddSeconds(-60),
                BusyWaitMs = 1
            });
            s.TryEnqueue(Item(CommandPriority.P1_Normal, "c1", "live"));

            var expired = s.ExpireTimedOut(DateTime.UtcNow);

            Assert.Single(expired);
            Assert.Equal("stale", expired[0].IdJson);
            Assert.Equal(1, s.QueuedCount);
        }

        [Fact]
        public void ANull_Item_Is_Refused_RatherThan_Queued()
        {
            var s = new StaScheduler();
            Assert.False(s.TryEnqueue(null));
            Assert.Equal(0, s.QueuedCount);
        }

        [Fact]
        public void Depths_And_Accounting_Agree()
        {
            var s = new StaScheduler();
            s.TryEnqueue(Item(CommandPriority.P0_Interactive, "a", "p0a"));
            s.TryEnqueue(Item(CommandPriority.P1_Normal, "a", "p1a"));
            s.TryEnqueue(Item(CommandPriority.P2_Background, "b", "p2b"));

            var depths = s.GetQueueDepths();
            Assert.Equal(1, depths.p0);
            Assert.Equal(1, depths.p1);
            Assert.Equal(1, depths.p2);
            Assert.Equal(3, depths.total);
            Assert.Equal(depths.total, s.QueuedCount);
        }

        /// <summary>
        /// Rewrites every queued item's enqueue time to <paramref name="by"/>, which is
        /// what "it has waited that long" means to the selector. Exposed as a seam
        /// because the scheduler has no clock seam and injecting one for a five-second
        /// window would mean sleeping in every test.
        /// </summary>
        private static void AgeAll(StaScheduler s, TimeSpan by)
        {
            typeof(StaScheduler)
                .GetField("_p0Queues", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(s);
            foreach (var fieldName in new[] { "_p0Queues", "_p1Queues", "_p2Queues" })
            {
                var queues = (Dictionary<string, Queue<ScheduledCommandItem>>)typeof(StaScheduler)
                    .GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(s);
                foreach (var q in queues.Values)
                    foreach (var item in q)
                        item.EnqueuedAtUtc = DateTime.UtcNow.Subtract(by);
            }
        }
    }
}
