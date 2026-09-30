using System;
using GxMcp.Gateway;
using GxMcp.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// The Gateway's operation tracker has two "has this finished?" questions, and
    /// they were the same three literals written out twice: an
    /// <c>IsTerminalOperationStatus</c> helper used by the status-poll paths, and a
    /// hand-rolled copy of the identical three comparisons in each of the two
    /// cancellation paths.
    ///
    /// The two are not the same question. A record can only ever hold
    /// <c>Completed</c>, <c>Failed</c> or <c>Cancelled</c> as terminal states -
    /// those are what this tracker assigns, and nothing assigns them back. The
    /// polled helper additionally recognises <c>NotFound</c>, which is an envelope
    /// this tracker emits for an id it does not know rather than a state a record
    /// holds, and <c>Stalled</c>, which no path in the class can produce at all.
    ///
    /// That last term is the reason the split matters rather than being tidiness.
    /// If the cancellation paths had simply called the polled helper, a
    /// <c>NotFound</c>-shaped status would have made a cancel request report
    /// "already terminal" for an operation that is still live. They cannot get that
    /// status today, so it is not a live bug - but the distinction is exactly the
    /// kind that erodes, and it is now named and asserted rather than implied by
    /// which literals happened to be typed.
    ///
    /// These drive the real tracker through its public surface, so what is asserted
    /// is the status a caller observes rather than the shape of the code.
    /// </summary>
    public class OperationTrackerTerminalStatusTests
    {
        // Retention is the sweep interval for finished records; the tests poll
        // synchronously and never wait on it, so a long window is enough to keep the
        // records alive for the duration of each test.
        private readonly OperationTracker _tracker = new OperationTracker(TimeSpan.FromHours(1));

        private static JObject Status(OperationTracker tracker, string operationId) =>
            tracker.BuildOperationStatus(operationId);

        /// <summary>
        /// Starts an operation and returns both halves of its identity.
        ///
        /// They are different ids, which is the trap: StartOperation takes a
        /// <c>requestId</c> and returns an <c>operationId</c>, the id a caller polls
        /// with BuildOperationStatus and MarkCancelled. CompleteFromWorker is keyed
        /// by the *request* id, not the operation id - passing the operation id
        /// leaves the record stranded at "Running" forever, which is exactly the
        /// failure LinkRequest's own comment describes for a retried request.
        /// </summary>
        private (string OperationId, string RequestId) Start(string toolName)
        {
            string requestId = "req-" + Guid.NewGuid().ToString("N");
            string operationId = _tracker.StartOperation(requestId, toolName, new JObject(), "corr-" + toolName);
            return (operationId, requestId);
        }

        [Fact]
        public void AnUnknownOperationIdIsNotFoundRatherThanTerminal()
        {
            // The not-found envelope. IsTerminalOperationStatus counts it, because a
            // poll should stop; the record-level predicate deliberately does not.
            var payload = Status(_tracker, "no-such-operation");

            Assert.Equal("NotFound", payload["status"]?.ToString());
            Assert.Equal("no-such-operation", payload["operationId"]?.ToString());
        }

        [Fact]
        public void ANewOperationIsRunningAndNotTerminal()
        {
            var started = Start("genexus_write");
            string id = started.OperationId;

            var payload = Status(_tracker, id);

            Assert.Equal("Running", payload["status"]?.ToString());
            Assert.Equal(id, payload["operationId"]?.ToString());
        }

        [Fact]
        public void CancellingAnOperationReachesCancelledAndIsIdempotent()
        {
            // The idempotence both cancellation paths rely on: a second cancel for
            // an already-terminal operation reports success without changing it.
            string id = Start("genexus_write").OperationId;

            Assert.True(_tracker.MarkCancelled(id, "first"));
            Assert.Equal("Cancelled", Status(_tracker, id)["status"]?.ToString());

            Assert.True(_tracker.MarkCancelled(id, "second"));
            Assert.Equal("Cancelled", Status(_tracker, id)["status"]?.ToString());
        }

        [Fact]
        public void CancellationRequestedIsNotTerminalAndCanStillBeCompleted()
        {
            // MarkCancellationRequested deliberately leaves the operation live: the
            // SDK call is non-preemptible, so the truthful terminal state is
            // published when it returns. That is why it is not one of the three
            // terminal record states, and why a cancel requested first must not
            // stop the later completion from being recorded.
            string id = Start("genexus_edit_form").OperationId;

            Assert.True(_tracker.MarkCancellationRequested(id, "client asked"));

            var requested = Status(_tracker, id);
            Assert.Equal("CancellationRequested", requested["status"]?.ToString());

            // The request is recorded, not acted on: the operation is still
            // cancellable, because it has not finished.
            Assert.True(_tracker.MarkCancelled(id, "then cancelled"));
            Assert.Equal("Cancelled", Status(_tracker, id)["status"]?.ToString());
        }

        [Fact]
        public void AFailedOperationIsTerminalForBothCancellationPaths()
        {
            // CompleteFromWorker's failure path assigns Failed. Both cancellation
            // paths must treat it as finished, or a late cancel would overwrite a
            // recorded failure and the operation would report as cancelled.
            var started = Start("genexus_patch");
            string id = started.OperationId;

            // Keyed by the request id, which is the other half of the pair.
            _tracker.CompleteFromWorker(started.RequestId, JObject.Parse("""{"error":{"message":"boom"}}"""));

            Assert.Equal("Failed", Status(_tracker, id)["status"]?.ToString());
            Assert.True(_tracker.MarkCancelled(id, "too late"));
            Assert.Equal("Failed", Status(_tracker, id)["status"]?.ToString());
        }

        [Fact]
        public void ACompletedOperationIsTerminalForBothCancellationPaths()
        {
            var started = Start("genexus_layout");
            string id = started.OperationId;

            _tracker.CompleteFromWorker(started.RequestId, JObject.Parse("""{"result":{"ok":true}}"""));

            Assert.Equal("Completed", Status(_tracker, id)["status"]?.ToString());
            Assert.True(_tracker.MarkCancellationRequested(id, "too late"));
            Assert.Equal("Completed", Status(_tracker, id)["status"]?.ToString());
        }

        [Fact]
        public void TheTwoTerminalPredicatesAnswerDifferentQuestions()
        {
            // Pinned at the source, because the behavioural tests above cannot
            // reach a status this tracker never produces: the polled predicate
            // must keep recognising the not-found envelope and the defensive
            // stalled clause, and the record-level predicate must not, since a
            // cancel request must not treat a still-live operation as finished.
            string source = RepoSource.WithoutComments(
                GxMcp.TestSupport.RepoSource.Read("src", "GxMcp.Gateway", "OperationTracker.cs"));

            Assert.Equal(1, SourceAssert.Count(source, "private static bool IsTerminalRecordState(string status)"));

            // The two hand-rolled copies of the three comparisons are gone.
            Assert.Equal(0, SourceAssert.Count(source, "record.Status, \"Completed\""));
            Assert.Equal(2, SourceAssert.Count(source, "IsTerminalRecordState(record.Status)"));

            // The polled helper is unchanged in what it accepts.
            string polled = SourceAssert.MethodBody(source, "private static bool IsTerminalOperationStatus(string status)");
            foreach (string terminal in new[] { "Completed", "Failed", "Cancelled", "Stalled", "NotFound" })
                Assert.Contains("\"" + terminal + "\"", polled);

            // And the record-level one is exactly the three a record can hold.
            string record = SourceAssert.MethodBody(source, "private static bool IsTerminalRecordState(string status)");
            foreach (string terminal in new[] { "Completed", "Failed", "Cancelled" })
                Assert.Contains("\"" + terminal + "\"", record);
            Assert.DoesNotContain("\"NotFound\"", record);
            Assert.DoesNotContain("\"Stalled\"", record);
        }

    }
}
