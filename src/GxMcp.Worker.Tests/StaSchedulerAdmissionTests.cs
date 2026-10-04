using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GxMcp.Worker.Helpers;
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

        /// <summary>
        /// A command carrying <paramref name="payloadChars"/> of command text, which is
        /// what a real edit / import / patch looks like to the admission budget.
        /// </summary>
        private static ScheduledCommandItem ItemWithPayload(
            CommandPriority priority, string client, string id, int payloadChars)
            => new ScheduledCommandItem
            {
                Priority = priority,
                ClientId = client,
                IdJson = id,
                Method = "object",
                Action = "importtext",
                RawLine = "{\"id\":" + id + ",\"method\":\"object\",\"params\":{\"input\":\"" +
                          new string('x', payloadChars) + "\"}}",
                EnqueuedAtUtc = DateTime.UtcNow
            };

        private static ScheduledCommandItem ItemWithCancelToken(
            CommandPriority priority, string client, string id, string cancelToken)
            => new ScheduledCommandItem
            {
                Priority = priority,
                ClientId = client,
                IdJson = id,
                CancelToken = cancelToken,
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
            // Issue #369 added a per-client cap, so the *global* count budget can only be
            // reached by several clients together - which is the real-world shape anyway,
            // and the point of the per-client bound.
            var s = new StaScheduler();
            int accepted = 0;
            for (int i = 0; i < StaScheduler.MaxQueuedCommands + 50; i++)
                if (s.TryEnqueue(Item(CommandPriority.P1_Normal, "client-" + (i % 8), i.ToString()))) accepted++;

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
                s.TryEnqueue(Item(CommandPriority.P1_Normal, "client-" + (i % 8), "filler" + i));

            var refused = Item(CommandPriority.P0_Interactive, "client-9", "must-not-run");
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

        // ── Issue #369: the byte budget has to bound the bytes it was asked to bound ──
        //
        // MaxQueuedBytes exists to stop the x86 Worker retaining an unbounded backlog of
        // queued command text. It could not do that, because EstimateBytes charged only
        // the id/method/action strings. Those are a few dozen bytes per command, while
        // the queued item also holds the whole RawLine. 512 accepted large edits retained
        // hundreds of megabytes inside a 32-bit process, so the count budget - not the
        // byte budget - was the only thing that ever stopped a burst.

        [Fact]
        public void The_Byte_Budget_Charges_The_Queued_Payload_Not_Just_The_Header()
        {
            var s = new StaScheduler();
            const int oneMiB = 1024 * 1024;

            // Spread across enough clients that the *global* byte budget is the binding
            // constraint rather than a single client's cap, so this measures the budget
            // the issue is about.
            int accepted = 0;
            string error = null;
            for (int i = 0; i < StaScheduler.MaxQueuedCommands; i++)
            {
                if (s.TryEnqueue(ItemWithPayload(
                        CommandPriority.P1_Normal, "client-" + (i % 16), i.ToString(), oneMiB)))
                    accepted++;
                else { error = s.LastAdmissionError; break; }
            }

            // The count budget alone would accept all 512 of these. The byte budget has to
            // bite first, on its own terms, with its own reason - otherwise the 32 MiB
            // budget is decorative.
            Assert.True(accepted < StaScheduler.MaxQueuedCommands,
                $"all {accepted} MiB-payload commands were admitted; the byte budget never engaged");
            Assert.Equal("queue_bytes_exhausted", error);
            Assert.Equal(accepted, s.QueuedCount);

            // And the charge has to be real: about 2 MiB of retained text per item, so the
            // 32 MiB budget cannot hold many more than a handful.
            Assert.InRange(accepted, 1, StaScheduler.MaxQueuedBytes / (oneMiB * 2) + 2);
        }

        [Fact]
        public void A_Command_Larger_Than_The_Whole_Budget_Is_Refused_On_Payload_Size_Even_For_A_Client_At_Zero()
        {
            // The size check has to precede the per-client caps, so an oversized single
            // command is still reported as the size condition it is, not as a saturated
            // client that has done nothing yet.
            var s = new StaScheduler();
            var huge = ItemWithPayload(CommandPriority.P1_Normal, "c1", "huge",
                (int)(StaScheduler.MaxQueuedBytes / 2));

            Assert.False(s.TryEnqueue(huge));
            Assert.Equal("payload_too_large", s.LastAdmissionError);
        }

        [Fact]
        public void One_Client_Cannot_Spend_The_Whole_Global_Byte_Budget()
        {
            // Global-only budgets make admission a denial-of-service primitive: a single
            // client floods background work until every slot is gone, and from then on
            // every other client's interactive read is refused with WorkerQueueSaturated.
            var s = new StaScheduler();

            while (s.TryEnqueue(Item(CommandPriority.P2_Background, "flooder", Guid.NewGuid().ToString("N")))) { }

            // The flooder is capped well short of the global budget, so a second client
            // still has room for an interactive read.
            Assert.True(s.TryEnqueue(Item(CommandPriority.P0_Interactive, "interactive", "read-1")),
                "the interactive read was refused; one client had already consumed the whole global budget");
        }

        [Fact]
        public void A_Client_At_Its_Own_Cap_Can_Still_Read_Interactively()
        {
            // The reserved headroom, and the reason the per-client cap is safe. Capping
            // interactive work per client too would only convert one client's background
            // flood into that same client's self-inflicted denial of service: the flood
            // would consume its quota and then refuse its own reads.
            var s = new StaScheduler();

            while (s.TryEnqueue(Item(CommandPriority.P2_Background, "flooder", Guid.NewGuid().ToString("N")))) { }
            Assert.Equal("client_command_cap", s.LastAdmissionError);

            // Same saturated client, interactive read: admitted.
            Assert.True(s.TryEnqueue(Item(CommandPriority.P0_Interactive, "flooder", "read")));
            Assert.True(s.TryTakeNext(out var taken));
            Assert.Equal("read", taken.IdJson);
        }

[Fact]
        public void A_Client_At_Its_Own_Cap_Is_Refused_For_That_Reason()
        {
            var s = new StaScheduler();

            int accepted = 0;
            string error = null;
            while (accepted < StaScheduler.MaxQueuedCommands + 10)
            {
                if (s.TryEnqueue(Item(CommandPriority.P1_Normal, "flooder", Guid.NewGuid().ToString("N"))))
                    accepted++;
                else { error = s.LastAdmissionError; break; }
            }

            Assert.Equal("client_command_cap", error);
            Assert.InRange(accepted, 1, StaScheduler.MaxQueuedCommands);
            Assert.True(accepted <= StaScheduler.MaxQueuedCommandsPerClient);
        }

        [Fact]
        public void Cancelling_A_Queued_Command_Releases_Its_Capacity_And_It_Never_Executes()
        {
            // A cancel that lands while the command is still queued used to be recorded as
            // a pre-cancellation: the command kept its slot and its bytes until it reached
            // the head of the queue, where it started with an already-cancelled token. So
            // capacity was not released at cancellation time, and whether the cancelled work
            // ran at all depended on each individual handler happening to observe the token.
            var s = new StaScheduler();
            for (int i = 0; i < 20; i++)
                s.TryEnqueue(ItemWithCancelToken(CommandPriority.P1_Normal, "c1", "job-" + i, "job-alpha"));
            s.TryEnqueue(Item(CommandPriority.P1_Normal, "c1", "unrelated"));

            int before = s.QueuedCount;
            long bytesBefore = s.QueuedBytes;

            int dropped = s.DropQueuedForCancelToken("job-alpha");

            Assert.Equal(20, dropped);
            Assert.Equal(before - 20, s.QueuedCount);
            Assert.True(s.QueuedBytes < bytesBefore);

            var ids = new List<string>();
            while (s.TryTakeNext(out var taken)) ids.Add(taken.IdJson);
            Assert.Equal(new[] { "unrelated" }, ids);
        }

        [Fact]
        public void A_Cancel_That_Overtakes_A_Queued_Command_Drops_It_Through_The_Cancellation_Registry()
        {
            // The end-to-end shape: the Gateway sends Control:Cancel, which lands in
            // WorkerCancellationRegistry.Cancel before the command has ever registered a
            // CTS. That path has to reach the scheduler, otherwise a cancelled command
            // keeps its slot and still executes when it reaches the head.
            WorkerCancellationRegistry.Reset();
            StaScheduler.Instance.Clear();
            try
            {
                StaScheduler.Instance.TryEnqueue(ItemWithCancelToken(
                    CommandPriority.P1_Normal, "c1", "queued-1", "job-42"));
                StaScheduler.Instance.TryEnqueue(ItemWithCancelToken(
                    CommandPriority.P1_Normal, "c1", "queued-2", "job-42"));
                StaScheduler.Instance.TryEnqueue(Item(CommandPriority.P1_Normal, "c1", "untouched"));

                Assert.Equal(3, StaScheduler.Instance.QueuedCount);

                // Returns false: no CTS exists yet, because the command has not started.
                Assert.False(WorkerCancellationRegistry.Cancel("job-42"));

                Assert.Equal(1, StaScheduler.Instance.QueuedCount);
                Assert.True(StaScheduler.Instance.TryTakeNext(out var taken));
                Assert.Equal("untouched", taken.IdJson);
            }
            finally
            {
                WorkerCancellationRegistry.Reset();
                StaScheduler.Instance.Clear();
            }
        }

        [Fact]
        public void Dropping_An_Unknown_Cancel_Token_Is_A_No_Op()
        {
            var s = new StaScheduler();
            s.TryEnqueue(Item(CommandPriority.P1_Normal, "c1", "keep"));

            Assert.Equal(0, s.DropQueuedForCancelToken("never-queued"));
            Assert.Equal(1, s.QueuedCount);
        }

        [Fact]
        public void Cancelling_Every_Queued_Command_Leaves_The_Budget_Reusable()
        {
            var s = new StaScheduler();
            int accepted = 0;
            while (accepted < StaScheduler.MaxQueuedCommands)
            {
                if (s.TryEnqueue(ItemWithCancelToken(
                        CommandPriority.P1_Normal, "c1", "j" + accepted, "batch")))
                    accepted++;
                else break;
            }

            s.DropQueuedForCancelToken("batch");

            // Cancelling a full queue must not leave admission permanently exhausted - the
            // refusal is not something the caller can recover from by draining, because
            // there is nothing left to drain.
            Assert.Equal(0, s.QueuedCount);
            Assert.Equal(0L, s.QueuedBytes);
            Assert.True(s.TryEnqueue(Item(CommandPriority.P1_Normal, "c1", "after-cancel")));
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
