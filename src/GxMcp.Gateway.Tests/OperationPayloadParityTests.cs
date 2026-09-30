using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// The status and result responses for a tracked operation used to be written
    /// out separately: the same unknown-id envelope, then the same twelve identity
    /// and lifecycle fields, then the same error projection. They now share
    /// <c>BuildNotFound</c> and <c>BuildBasePayload</c>.
    ///
    /// The property that matters is parity: a caller polling status and a caller
    /// fetching the result must see the same identity, timestamps and error for
    /// the same operation, or a long-poll that reports "still Running" disagrees
    /// with the result it is polling for.
    /// </summary>
    public class OperationPayloadParityTests
    {
        private static readonly string[] SharedFields =
        {
            "status", "operationId", "phase", "progressMessage", "toolName",
            "correlationId", "timedOut", "timeoutCount",
            "startedAtUtc", "updatedAtUtc", "completedAtUtc",
        };

        private static (OperationTracker Tracker, string OpId) Started(string requestId, string tool = "genexus_read")
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
            string opId = tracker.StartOperation(requestId, tool, null, "cid-" + requestId);
            return (tracker, opId);
        }

        [Fact]
        public void StatusAndResult_AgreeOnEverySharedField()
        {
            var (tracker, opId) = Started("parity-running");

            var status = tracker.BuildOperationStatus(opId);
            var result = tracker.BuildOperationResult(opId);

            foreach (string field in SharedFields)
            {
                Assert.True(JToken.DeepEquals(status[field], result[field]),
                    "field '" + field + "' differs between status and result: "
                    + status[field] + " vs " + result[field]);
            }
        }

        [Fact]
        public void StatusAndResult_AgreeOnTheSharedFieldSet()
        {
            // A field added to one projection and not the other is the drift this
            // consolidation exists to prevent, so the key sets are compared too -
            // result is allowed exactly the two fields it layers on.
            var (tracker, opId) = Started("parity-keys");

            var statusKeys = tracker.BuildOperationStatus(opId).Properties().Select(p => p.Name).ToList();
            var resultKeys = tracker.BuildOperationResult(opId).Properties().Select(p => p.Name).ToList();

            Assert.Equal(SharedFields.Length, statusKeys.Count);
            Assert.All(SharedFields, f => Assert.Contains(f, statusKeys));

            var extra = resultKeys.Except(statusKeys).ToList();
            Assert.All(extra, f => Assert.Contains(f, new[] { "workerPayload", "message" }));
        }

        [Fact]
        public void BothProjections_SurfaceTheRecordedError()
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));
            string opId = tracker.StartOperation("err-request", "genexus_read", null, "cid");

            tracker.MarkFailedByRequest("err-request", "boom");

            var status = tracker.BuildOperationStatus(opId);
            var result = tracker.BuildOperationResult(opId);

            Assert.False(string.IsNullOrWhiteSpace(status["error"]?.ToString()));
            Assert.Equal(status["error"]?.ToString(), result["error"]?.ToString());
        }

        [Fact]
        public void AnUnknownOperation_YieldsTheSameEnvelopeFromBoth()
        {
            var tracker = new OperationTracker(TimeSpan.FromMinutes(5));

            var status = tracker.BuildOperationStatus("op:does-not-exist");
            var result = tracker.BuildOperationResult("op:does-not-exist");

            Assert.Equal("NotFound", status["status"]?.ToString());
            Assert.Equal("op:does-not-exist", status["operationId"]?.ToString());
            Assert.Equal("Operation not found or expired.", status["message"]?.ToString());

            // Byte-identical, not merely similar: a caller polling one and reading
            // the other must not see different guidance.
            Assert.Equal(
                status.ToString(Newtonsoft.Json.Formatting.None),
                result.ToString(Newtonsoft.Json.Formatting.None));
        }

        [Fact]
        public void Result_StillLayersTheWorkerPayloadAndRunningMessage()
        {
            // The consolidation must not have cost the result response the two
            // fields it adds on top of the shared projection.
            var (tracker, opId) = Started("layer-running");

            var running = tracker.BuildOperationResult(opId);
            Assert.Null(running["workerPayload"]);
            Assert.Contains("still running", running["message"]?.ToString());
            // Status does not carry the running message - that is the difference.
            Assert.Null(tracker.BuildOperationStatus(opId)["message"]);

            tracker.CompleteFromWorker("layer-running", new JObject
            {
                ["id"] = "layer-running",
                ["result"] = new JObject { ["status"] = "ReadOk" }
            });

            var done = tracker.BuildOperationResult(opId);
            Assert.Equal("ReadOk", done["workerPayload"]?["result"]?["status"]?.ToString());
            Assert.Null(done["message"]);
        }

        [Fact]
        public void BothProjections_StillRunUnderTheRecordLock()
        {
            // An OperationRecord is mutable and written by the polling and
            // completion paths. BuildBasePayload is a projection, not an accessor,
            // so it must be called from inside lock (record.SyncRoot) - otherwise
            // the twelve field reads race with a concurrent update.
            string src = RepoSource.Read("src", "GxMcp.Gateway", "OperationTracker.cs");

            foreach (string method in new[] { "BuildOperationStatus", "BuildOperationResult" })
            {
                int at = src.IndexOf("public JObject " + method + "(", StringComparison.Ordinal);
                Assert.True(at > 0, method + " not found");
                int lockAt = src.IndexOf("lock (record.SyncRoot)", at, StringComparison.Ordinal);
                int payloadAt = src.IndexOf("BuildBasePayload(record)", at, StringComparison.Ordinal);
                int end = src.IndexOf("public ", at + 10, StringComparison.Ordinal);

                Assert.True(lockAt > at, method + " must take the record lock");
                Assert.True(payloadAt > lockAt,
                    method + " must project the record inside lock (record.SyncRoot)");
                Assert.True(end < 0 || payloadAt < end,
                    method + " must project the record before returning");
            }
        }

    }
}
