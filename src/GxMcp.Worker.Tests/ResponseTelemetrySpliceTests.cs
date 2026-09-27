using System;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Regression coverage for the response-envelope serialization path.
    //
    // SendResponse used to serialize every response TWICE: once to measure, then again to
    // embed the measured serializeMs/responseBytes into _meta.telemetry. The second pass was
    // a full JsonConvert.SerializeObject of the whole frame — a second reflection-driven walk
    // and a second full string allocation — just to write two small numbers, on every single
    // tool call. It is now a single serialization plus one substring splice of the small
    // telemetry object.
    //
    // The wire payload must be byte-for-byte equivalent in STRUCTURE: same keys, same values,
    // valid JSON. These tests pin that equivalence rather than the timing, so a future change
    // that breaks the frame is caught by a unit test and not by a client.
    public class ResponseTelemetrySpliceTests
    {
        // SendResponse is a private static on the worker's Program; reach it directly so the
        // test exercises the shipped method, not a copy of its logic.
        private static MethodInfo SpliceTelemetryMethod()
        {
            MethodInfo method = typeof(Program).GetMethod(
                "SpliceTelemetry", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            return method!;
        }

        private static string Splice(string serialized, string placeholder, JObject telemetry)
            => (string)SpliceTelemetryMethod().Invoke(null, new object[] { serialized, placeholder, telemetry })!;

        [Fact]
        public void Splices_Telemetry_IntoTheReservedSlot()
        {
            var telemetry = new JObject { ["transformMs"] = 7L };
            string frame = "{\"jsonrpc\":\"2.0\",\"result\":{\"_meta\":{\"telemetry\":\"__gxtel_TOKEN\"}},\"id\":\"1\"}";

            string spliced = Splice(frame, "__gxtel_TOKEN", telemetry);

            var parsed = JObject.Parse(spliced);
            Assert.Equal(7L, parsed["result"]!["_meta"]!["telemetry"]!["transformMs"]!.ToObject<long>());
        }

        [Fact]
        public void Spliced_Frame_RemainsValidJson_WithTelemetryAsAnObject()
        {
            var telemetry = new JObject
            {
                ["transformMs"] = 3L,
                ["serializeMs"] = 11L,
                ["responseBytes"] = 2048L
            };
            string frame = "{\"result\":{\"_meta\":{\"telemetry\":\"__gxtel_T\"}},\"id\":null}";

            var parsed = JObject.Parse(Splice(frame, "__gxtel_T", telemetry));

            // The placeholder was a STRING; after the splice it must be an OBJECT, exactly as
            // the previous double-serialization produced. A client reading
            // _meta.telemetry.serializeMs depends on this.
            Assert.Equal(JTokenType.Object, parsed["result"]!["_meta"]!["telemetry"]!.Type);
            Assert.Equal(11L, parsed["result"]!["_meta"]!["telemetry"]!["serializeMs"]!.ToObject<long>());
        }

        [Fact]
        public void Spliced_Frame_PreservesEverythingAroundTheSlot()
        {
            var telemetry = new JObject { ["serializeMs"] = 2L };
            string frame = "{\"jsonrpc\":\"2.0\",\"result\":{\"a\":1,\"b\":[1,2,3],\"c\":\"text\"},\"id\":\"abc\"}";

            var parsed = JObject.Parse(Splice(frame, "__gxtel_T", telemetry));

            Assert.Equal("2.0", (string?)parsed["jsonrpc"]);
            Assert.Equal(1, parsed["result"]!["a"]!.ToObject<int>());
            Assert.Equal(3, ((JArray)parsed["result"]!["b"]!).Count);
            Assert.Equal("text", (string?)parsed["result"]!["c"]);
            Assert.Equal("abc", (string?)parsed["id"]);
        }

        [Fact]
        public void MissingToken_LeavesTheFrameUntouched()
        {
            // Fail-safe: if the placeholder is not in the text (should be impossible), the
            // original frame is returned rather than a corrupted one.
            string frame = "{\"result\":{\"_meta\":{}},\"id\":\"1\"}";

            Assert.Equal(frame, Splice(frame, "__gxtel_absent", new JObject { ["x"] = 1 }));
        }

        [Fact]
        public void NullOrEmptyPlaceholder_IsANoOp()
        {
            string frame = "{\"result\":{},\"id\":\"1\"}";

            Assert.Equal(frame, Splice(frame, null!, new JObject()));
            Assert.Equal(frame, Splice(frame, string.Empty, new JObject()));
        }

        [Fact]
        public void OnlyTheTelemetrySlotIsReplaced()
        {
            // The splice is anchored on the "telemetry" key, so even a payload that happens to
            // contain the token as an ordinary value elsewhere is left intact.
            var telemetry = new JObject { ["serializeMs"] = 5L };
            string frame = "{\"a\":\"__gxtel_T\",\"_meta\":{\"telemetry\":\"__gxtel_T\"},\"id\":\"1\"}";

            var parsed = JObject.Parse(Splice(frame, "__gxtel_T", telemetry));

            Assert.Equal("__gxtel_T", (string?)parsed["a"]);
            Assert.Equal(5L, parsed["_meta"]!["telemetry"]!["serializeMs"]!.ToObject<long>());
        }

        [Fact]
        public void TelemetryWithSpecialCharacters_StaysValid()
        {
            // The spliced text must be escaped by its own serializer, not concatenated raw.
            var telemetry = new JObject { ["lastError"] = "line\nbreak \"quoted\"" };
            string frame = "{\"_meta\":{\"telemetry\":\"__gxtel_T\"},\"id\":\"1\"}";

            var parsed = JObject.Parse(Splice(frame, "__gxtel_T", telemetry));

            Assert.Equal("line\nbreak \"quoted\"",
                (string?)parsed["_meta"]!["telemetry"]!["lastError"]);
        }
    }
}
