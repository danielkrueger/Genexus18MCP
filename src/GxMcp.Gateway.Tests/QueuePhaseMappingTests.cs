using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// The gateway reported a "queue" that was not a queue. It measured the interval
    /// between stamping the request and handing it to a Worker, which is admission,
    /// Worker acquisition and cold-start waiting - and ignored
    /// <c>telemetry.queueWaitMs</c>, the delay the Worker itself measured, even though
    /// <c>sdkMs</c>, <c>transformMs</c> and <c>serializeMs</c> were all read from that
    /// same object.
    ///
    /// Two things were wrong at once, which is why neither was visible. A Worker that
    /// was genuinely queued behind other work reported 7000 ms of delay and the summary
    /// read 0, because the pre-send interval happened to be near zero - so a busy shared
    /// Worker looked idle. And a Worker with no queue at all, whose delay was entirely
    /// cold-start waiting, reported that wait AS queue time.
    ///
    /// The second half of the fix is that the phases are now distinguishable at all:
    /// <c>avgQueueWaitMs</c> is the Worker's measurement averaged over the calls that
    /// reported one, and <c>avgAdmissionMs</c> is the gateway-side interval. A response
    /// with no telemetry contributes to neither queue total nor queue denominator - it is
    /// unmeasured, not zero, and averaging it in reported an idle queue.
    ///
    /// These go through <c>RecordToolLatency</c> rather than calling
    /// <c>ToolLatencyStats.Record</c> directly, because the defect was in which phase
    /// reached which field and feeding phase numbers in by hand cannot observe that.
    /// </summary>
    public class QueuePhaseMappingTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        private static JObject WorkerResponse(string envelope, long queueWaitMs)
        {
            var telemetry = new JObject
            {
                ["sdkMs"] = 40,
                ["transformMs"] = 2,
                ["serializeMs"] = 1,
                ["queueWaitMs"] = queueWaitMs
            };

            var inner = new JObject { ["status"] = "ok", ["_meta"] = new JObject { ["telemetry"] = telemetry } };
            var response = new JObject();

            if (envelope == "result")
                response["result"] = inner;
            else
                response["result"] = new JObject { ["_meta"] = new JObject { ["telemetry"] = telemetry } };

            return response;
        }

        /// <summary>
        /// The acceptance case: a Worker that really was queued must show that queue
        /// delay, in the shared <c>_meta</c> envelope and the isolated one.
        /// </summary>
        [Fact]
        public void AWorkerReportedQueueDelaySurfacesAsQueueDelay()
        {
            foreach (string envelope in new[] { "shared", "result" })
            {
                ToolLatencyStats.ResetForTest();

                // No pre-send interval at all: nothing but the Worker's own queue.
                Program.RecordToolLatency(
                    "genexus_read", Now, Now,
                    WorkerResponse(envelope, 7000), 1024);

                var s = ToolLatencyStats.Summarize();
                Assert.Equal(7000L, s["avgQueueWaitMs"]!.ToObject<long>());
                Assert.Equal(1L, s["queueWaitSamples"]!.ToObject<long>());
                Assert.Equal(0L, s["avgAdmissionMs"]!.ToObject<long>());
            }
        }

        /// <summary>
        /// Cold-start waiting is not queue delay. Before the fix this was the interval
        /// reported as queue, so a Worker that had never queued showed 3000 ms of queue.
        /// </summary>
        [Fact]
        public void ColdStartupIsNotReportedAsWorkerQueueTime()
        {
            ToolLatencyStats.ResetForTest();

            // 3000 ms elapsed between stamping the request and handing it off, and the
            // Worker reports no queue of its own.
            Program.RecordToolLatency(
                "genexus_write", Now.AddMilliseconds(3000), Now,
                WorkerResponse("shared", 0), 2048);

            var s = ToolLatencyStats.Summarize();
            Assert.Equal(0L, s["avgQueueWaitMs"]!.ToObject<long>());
            Assert.Equal(3000L, s["avgAdmissionMs"]!.ToObject<long>());
            // Measured zero is still measured, so it is counted.
            Assert.Equal(1L, s["queueWaitSamples"]!.ToObject<long>());
        }

        /// <summary>
        /// A measured zero and an absent measurement are different facts. A response with
        /// no telemetry - an error before dispatch, or a shape that never carried it -
        /// must not be averaged in as an idle queue.
        /// </summary>
        [Fact]
        public void AnAbsentMeasurementIsNotCountedAsAnIdleQueue()
        {
            ToolLatencyStats.ResetForTest();

            // No telemetry at all.
            Program.RecordToolLatency(
                "genexus_read", Now.AddMilliseconds(10), Now,
                new JObject { ["result"] = new JObject { ["status"] = "error" } }, 64);
            // And one that did report, for contrast.
            Program.RecordToolLatency(
                "genexus_read", Now.AddMilliseconds(20), Now,
                WorkerResponse("shared", 400), 64);

            var s = ToolLatencyStats.Summarize();
            Assert.Equal(2L, s["totalCalls"]!.ToObject<long>());
            // Only the reporting call is in the denominator, so 400 rather than 200.
            Assert.Equal(400L, s["avgQueueWaitMs"]!.ToObject<long>());
            Assert.Equal(1L, s["queueWaitSamples"]!.ToObject<long>());

            var read = ((JArray)s["byTool"]!).Single(t => t!["tool"]!.ToString() == "genexus_read");
            Assert.Equal(400L, read["avgQueueWaitMs"]!.ToObject<long>());
            Assert.Equal(1L, read["queueWaitSamples"]!.ToObject<long>());
        }

        /// <summary>
        /// The other phase timings came from the same telemetry object and must still be
        /// read from it, in both envelope shapes - the fix moved the extraction and could
        /// have broken it.
        /// </summary>
        [Fact]
        public void TheOtherWorkerTimingsStillComeFromTheSameTelemetry()
        {
            foreach (string envelope in new[] { "shared", "result" })
            {
                ToolLatencyStats.ResetForTest();
                Program.RecordToolLatency(
                    "genexus_list", Now.AddMilliseconds(60), Now, WorkerResponse(envelope, 10), 512);

                var list = ((JArray)ToolLatencyStats.Summarize()["byTool"]!)
                    .Single(t => t!["tool"]!.ToString() == "genexus_list");

                Assert.Equal(40L, list["avgSdkMs"]!.ToObject<long>());
                Assert.Equal(2L, list["avgTransformMs"]!.ToObject<long>());
                Assert.Equal(1L, list["avgSerializeMs"]!.ToObject<long>());
            }
        }
    }
}
