using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Benchmarks
{
    [Config(typeof(ResponseEnvelopeBenchmarkConfig))]
    [MemoryDiagnoser]
    public class ResponseEnvelopeSerializationBenchmark
    {
        // Faithful ports of the worker's SendResponse pipeline, before and after the
        // single-serialization change (GxMcp.Worker/Program.cs). Both variants run the
        // identical JObject -> string work so the delta is attributable to the pipeline
        // shape, not to the payload.
        //
        // Before: JsonIngress.ParseToken (full parse) -> serialize the whole envelope ->
        //         embed serializeMs/responseBytes -> serialize the whole envelope AGAIN.
        // After:  reuse the already-parsed token   -> serialize the whole envelope once ->
        //         splice the small telemetry object into the reserved slot.
        //
        // Payload sizes are the ones that matter in practice: a small JSON envelope, a
        // mid-size listing/query reply, and a large genexus_read source envelope. The old
        // second pass scaled with payload size; the new splice does not.

        private string _tiny = null!;
        private string _medium = null!;
        private string _large = null!;

        [GlobalSetup]
        public void Setup()
        {
            // Both variants start from the SAME text: what DispatchInternal handed the
            // normalizer. That is the only fair starting point — the two pipelines differ
            // in what they do with this string, not in how it was produced.
            _tiny = BuildEnvelope(1).ToString(Formatting.None);
            _medium = BuildEnvelope(400).ToString(Formatting.None);
            _large = BuildEnvelope(20_000).ToString(Formatting.None);
        }

        private static JObject BuildEnvelope(int rows)
        {
            var items = new JArray();
            for (int i = 0; i < rows; i++)
            {
                items.Add(new JObject
                {
                    ["name"] = "Transaction" + i,
                    ["type"] = i % 3 == 0 ? "Procedure" : "Transaction",
                    ["description"] = "Processes billing and invoice records for customer account " + i,
                    ["source"] = "// Source code for object " + i + "\nFor each Customer\n  CustomerTotal += InvoiceTotal\nEndfor\n"
                });
            }

            return new JObject
            {
                ["status"] = "ok",
                ["code"] = "OK",
                ["result"] = new JObject { ["items"] = items, ["total"] = rows }
            };
        }

        private static JObject NewTelemetry() => new JObject
        {
            ["sdkMs"] = 12,
            ["queueWaitMs"] = 0,
            ["cacheOutcome"] = "unknown",
            ["transformMs"] = 0,
            ["serializeMs"] = 0L,
            ["responseBytes"] = 0L
        };

        private static void ReserveTelemetrySlot(JObject resultObject, JObject telemetry, out string placeholder)
        {
            JObject meta = resultObject["_meta"] as JObject ?? new JObject();
            resultObject["_meta"] = meta;
            placeholder = "__gxtel_" + Guid.NewGuid().ToString("N");
            meta["telemetry"] = placeholder;
        }

        // ---- previous pipeline -------------------------------------------------------------

        [Benchmark(Baseline = true)]
        public string Before_Tiny() => OldPath(_tiny);

        [Benchmark]
        public string Before_Medium() => OldPath(_medium);

        [Benchmark]
        public string Before_Large() => OldPath(_large);

        private static string OldPath(string raw)
        {
            // 1) McpResponseNormalizer.Normalize parses the whole payload to inspect the
            //    top-level status/error, then returns the ORIGINAL string on success.
            JObject parsedForNormalize = JObject.Parse(raw);

            // 2) SendResponse then re-parses that very same string with JsonIngress.ParseToken.
            object resultObj = JObject.Parse(raw);

            JObject resultObject = (JObject)resultObj;
            JObject telemetry = NewTelemetry();
            resultObject["_meta"] = new JObject { ["telemetry"] = telemetry };

            var response = new { jsonrpc = "2.0", result = resultObj, id = "1" };
            string serialized = JsonConvert.SerializeObject(response, Formatting.None);
            telemetry["serializeMs"] = 0L;
            telemetry["responseBytes"] = System.Text.Encoding.UTF8.GetByteCount(serialized);
            // 3) The second full serialization that used to run on every response.
            return JsonConvert.SerializeObject(response, Formatting.None);
        }

        // ---- current pipeline --------------------------------------------------------------

        [Benchmark]
        public string After_Tiny() => NewPath(_tiny);

        [Benchmark]
        public string After_Medium() => NewPath(_medium);

        [Benchmark]
        public string After_Large() => NewPath(_large);

        private static string NewPath(string raw)
        {
            // 1) The single parse. The token is kept and handed forward.
            object resultObj = JObject.Parse(raw);

            JObject resultObject = (JObject)resultObj;
            JObject telemetry = NewTelemetry();
            ReserveTelemetrySlot(resultObject, telemetry, out string placeholder);

            var response = new { jsonrpc = "2.0", result = resultObj, id = "1" };
            // 2) One serialization instead of two.
            string serialized = JsonConvert.SerializeObject(response, Formatting.None);
            telemetry["serializeMs"] = 0L;
            telemetry["responseBytes"] = System.Text.Encoding.UTF8.GetByteCount(serialized);

            // 3) Splice the small telemetry object into its reserved slot.
            string anchor = "\"telemetry\":\"" + placeholder + "\"";
            int at = serialized.IndexOf(anchor, StringComparison.Ordinal);
            if (at < 0) return serialized;
            return serialized.Substring(0, at)
                + "\"telemetry\":" + telemetry.ToString(Formatting.None)
                + serialized.Substring(at + anchor.Length);
        }
    }

    public sealed class ResponseEnvelopeBenchmarkConfig : ManualConfig
    {
        public ResponseEnvelopeBenchmarkConfig()
        {
            AddJob(Job.ShortRun.WithToolchain(InProcessNoEmitToolchain.Instance));
        }
    }
}
