using System;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Regression coverage for the single-parse response path.
    //
    // McpResponseNormalizer.Normalize used to JObject.Parse the whole payload and, on the
    // success path, throw the parsed tree away and return the original string — which
    // SendResponse then re-parsed with JsonIngress.ParseToken. Two full parse passes over
    // identical text on every response, for nothing. NormalizeToken now hands the token
    // forward so the envelope is serialized straight from it.
    //
    // These tests pin the equivalence: NormalizeToken(json) must be the same JSON document
    // that parsing Normalize(json) would have produced, for every payload shape the worker
    // emits. That is the property the optimization is allowed to assume.
    public class McpResponseNormalizerSingleParseTests
    {
        private const string LegacyNotFound = @"{""status"":""NotFound"",""message"":""no such object""}";
        private const string LegacyWorkerBusy = @"{""status"":""WorkerBusy"",""message"":""busy""}";
        private const string AlreadyCanonicalError = @"{""status"":""error"",""error"":{""code"":""KBNotOpen"",""message"":""KB is not open"",""hint"":""open it""}}";
        private const string IndexNotReady = @"{""status"":""Indexing"",""code"":""IndexNotReady"",""indexStatus"":""Cold"",""retryAfterMs"":5000}";

        [Theory]
        [InlineData(@"{}")]
        [InlineData(@"{""status"":""ok"",""code"":""OK"",""result"":{""a"":1}}")]
        [InlineData(IndexNotReady)]
        [InlineData(LegacyNotFound)]
        [InlineData(LegacyWorkerBusy)]
        [InlineData(AlreadyCanonicalError)]
        [InlineData(@"{""status"":""Timeout"",""message"":""t""}")]
        [InlineData(@"{""status"":""Cancelled"",""message"":""c""}")]
        [InlineData(@"{""error"":{""code"":""X"",""message"":""m""},""data"":[1,2,3]}")]
        [InlineData(@"{""result"":{""nested"":{""deep"":{""status"":""Error""}}}}")]
        public void TokenPath_MatchesTheStringPath(string json)
        {
            string viaString = McpResponseNormalizer.Normalize(json);
            JToken viaToken = McpResponseNormalizer.NormalizeToken(json, out _);

            // The property the optimization relies on: parsing the string Normalize() returns
            // yields exactly the token NormalizeToken() handed back.
            Assert.True(
                JToken.DeepEquals(JToken.Parse(viaString), viaToken),
                "NormalizeToken must be the same JSON document as parsing Normalize's output.");
        }

        [Fact]
        public void SuccessPayload_IsNotFlaggedAsRewritten()
        {
            // Drives the dispatcher: not-rewritten means the caller keeps the ORIGINAL bytes
            // for the idempotency cache, so a replay is byte-identical to the first response.
            McpResponseNormalizer.NormalizeToken(
                @"{""status"":""ok"",""code"":""OK"",""result"":{""a"":1}}", out bool rewritten);

            Assert.False(rewritten);
        }

        [Fact]
        public void LegacyErrorPayload_IsFlaggedAsRewritten()
        {
            McpResponseNormalizer.NormalizeToken(LegacyNotFound, out bool rewritten);

            Assert.True(rewritten);
        }

        [Fact]
        public void Normalize_StillReturnsTheOriginalBytes_WhenNothingIsRewritten()
        {
            // Preserves the pre-existing string contract exactly, including whitespace.
            const string spaced = @"{ ""status"": ""ok"", ""code"": ""OK"" }";

            Assert.Equal(spaced, McpResponseNormalizer.Normalize(spaced));
        }

        [Fact]
        public void InvalidJson_BecomesAnErrorToken()
        {
            JToken token = McpResponseNormalizer.NormalizeToken("{not json", out bool rewritten);

            Assert.True(rewritten);
            Assert.Equal("error", (string)token["status"]);
            Assert.Equal(JTokenType.Object, token["error"].Type);
        }

        [Fact]
        public void EmptyJson_BecomesAnErrorToken()
        {
            JToken token = McpResponseNormalizer.NormalizeToken("   ", out bool rewritten);

            Assert.True(rewritten);
            Assert.Equal("error", (string)token["status"]);
        }

        [Fact]
        public void LegacyStatus_IsPromotedToTheCanonicalErrorEnvelope()
        {
            JToken token = McpResponseNormalizer.NormalizeToken(LegacyNotFound, out _);

            Assert.Equal("error", (string)token["status"]);
            Assert.Equal("LegacyNotFound", (string)token["error"]["code"]);
            Assert.Equal("NotFound", (string)token["error"]["legacyStatus"]);
            Assert.Equal("no such object", (string)token["error"]["message"]);
        }

        [Fact]
        public void DomainFieldsNamedError_AreNotMistakenForAFailure()
        {
            // A successful result carrying a nested field called "error" must stay untouched —
            // the normalizer only ever inspects TOP-LEVEL status/error.
            const string json = @"{""status"":""ok"",""result"":{""error"":""none"",""code"":""OK""}}";

            JToken token = McpResponseNormalizer.NormalizeToken(json, out bool rewritten);

            Assert.False(rewritten);
            Assert.Equal("none", (string)token["result"]["error"]);
        }
    }
}
