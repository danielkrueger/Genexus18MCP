using System;
using System.Text;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #357 — token issuance on a body that was actually served.
    /// <para>
    /// The critical property is that the token carries the revision observed
    /// <em>before</em> the read, because the token's whole job is to describe the
    /// bytes it was issued with. Deriving it after the read looks equivalent and
    /// is not: it binds the token to a newer revision than the body, and the next
    /// conditional read then suppresses a change that really happened. That is
    /// silent, and it fails on exactly the input — "did my write land?" — an
    /// agent is most likely to ask.
    /// </para>
    /// </summary>
    public class ConditionalReadTokenIssuanceTests
    {
        // Decorate touches no SDK state: identity and revision arrive already
        // observed, and the KB accessors it used to call were the thing that made
        // it untestable.
        private static ConditionalReadService Service() => new ConditionalReadService(null, null);

        private static ConditionalReadToken.State Observed(string revision) => new ConditionalReadToken.State
        {
            KbFingerprint = "kbhash",
            WorkerInstance = "worker-1",
            ModelVersion = "18.0.10",
            ObjectGuid = "0123456789abcdef0123456789abcdef",
            ObjectType = "Procedure",
            Part = "source",
            Offset = ConditionalReadToken.Unspecified,
            Limit = 0,
            Revision = revision
        };

        private static JObject Body(string source, int totalLines = 100, long totalBytes = 4096)
        {
            return new JObject
            {
                ["part"] = "Source",
                ["source"] = source,
                ["versionToken"] = "638000000000000000:root:sha",
                ["totalLines"] = totalLines,
                ["totalBytes"] = totalBytes,
                ["truncated"] = false
            };
        }

        [Fact]
        public void IssuesATokenThatPinsTheServedBody()
        {
            var payload = Service().Decorate(
                Body("parm P1()\nend\n").ToString(),
                Observed("638000000000000000:root"),
                ConditionalReadToken.ReasonAbsent);

            var result = JObject.Parse(payload);
            string token = result["contentToken"]!.ToString();
            Assert.True(ConditionalReadToken.TryParse(token, out var parsed, out _));

            // The fingerprint must be of the string the client received, not of
            // the envelope: that is the only thing pinning "these exact bytes".
            Assert.Equal(
                ConditionalReadToken.BodyFingerprint("parm P1()\nend\n"),
                parsed.BodyFingerprint);
            Assert.Equal("638000000000000000:root:sha", parsed.VersionToken);
            Assert.Equal(100, parsed.TotalLines);
            Assert.Equal(4096, parsed.TotalBytes);
        }

        [Fact]
        public void TheTokenCarriesTheRevisionObservedBeforeTheRead()
        {
            // Regression guard. Re-deriving the revision inside issuance would
            // make Issue() refuse (no revision) and hand back no token at all, so
            // the first read would never give the caller anything to echo.
            var payload = Service().Decorate(
                Body("source").ToString(),
                Observed("638000000000000111:root"),
                ConditionalReadToken.ReasonAbsent);

            var result = JObject.Parse(payload);
            Assert.True(ConditionalReadToken.TryParse(result["contentToken"]!.ToString(), out var parsed, out _));
            Assert.Equal("638000000000000111:root", parsed.Revision);
        }

        [Fact]
        public void ATokenIssuedNow_ComparesAsUnchangedAgainstTheSameObservation()
        {
            // End-to-end on the pure parts: issue, then compare against an
            // identical observation. This is the shape of the second read that
            // returns notModified.
            var observed = Observed("638000000000000111:root");
            var payload = JObject.Parse(Service().Decorate(
                Body("source").ToString(), observed, ConditionalReadToken.ReasonAbsent));

            Assert.True(ConditionalReadToken.TryParse(payload["contentToken"]!.ToString(), out var parsed, out _));
            Assert.Equal(ConditionalReadToken.ReasonMatched, ConditionalReadToken.Compare(parsed, Observed("638000000000000111:root")));
            // And the edit case: the next revision must not suppress the body.
            Assert.Equal(
                ConditionalReadToken.ReasonRevisionAdvanced,
                ConditionalReadToken.Compare(parsed, Observed("638000000000000222:root")));
        }

        [Fact]
        public void ReportsTheReasonOnEveryIssuedBody()
        {
            var result = JObject.Parse(Service().Decorate(
                Body("source").ToString(),
                Observed("638000000000000111:root"),
                ConditionalReadToken.ReasonRevisionAdvanced));

            var conditional = result["conditional"]!;
            Assert.Equal("bodyReturned", conditional["status"]!.ToString());
            Assert.Equal(ConditionalReadToken.ReasonRevisionAdvanced, conditional["reason"]!.ToString());
            Assert.False(string.IsNullOrWhiteSpace(conditional["explanation"]!.ToString()));
            // Named, so a reader can tell a revision-stamp decision from a TTL or
            // a cache-hit heuristic.
            Assert.Equal("objectRevisionStamp", conditional["freshness"]!["source"]!.ToString());
            // Nothing was omitted, so nothing may be reported as omitted.
            Assert.True(conditional["omittedBodyBytes"] == null || conditional["omittedBodyBytes"]!.Type == JTokenType.Null);
        }

        [Fact]
        public void NoTokenIsIssuedWithoutARevisionToProveItAgainst()
        {
            // A revision-less observation cannot gate a suppression, so it must
            // not mint a token that claims it can.
            var result = JObject.Parse(Service().Decorate(
                Body("source").ToString(),
                Observed(revision: null),
                ConditionalReadToken.ReasonRevisionUnknown));

            Assert.True(result["contentToken"] == null || result["contentToken"]!.Type == JTokenType.Null);
            Assert.Equal(ConditionalReadToken.ReasonRevisionUnknown, result["conditional"]!["reason"]!.ToString());
        }

        [Fact]
        public void AnErrorPayloadIsPassedThroughUntouched()
        {
            var error = new JObject { ["error"] = "Part 'Source' not found on 'X'." }.ToString();
            Assert.Equal(error, Service().Decorate(error, Observed("r"), ConditionalReadToken.ReasonAbsent));
        }

        [Fact]
        public void APayloadWithNoTextBodyIsPassedThroughUntouched()
        {
            // Binding a token to a representation whose bytes cannot be fingerprinted
            // would be a token over nothing.
            var nonText = new JObject { ["part"] = "Source", ["source"] = new JObject { ["unexpected"] = "shape" } }.ToString();
            Assert.Equal(nonText, Service().Decorate(nonText, Observed("r"), ConditionalReadToken.ReasonAbsent));
        }

        [Fact]
        public void AMalformedPayloadIsPassedThroughUntouched()
        {
            Assert.Equal("not json", Service().Decorate("not json", Observed("r"), ConditionalReadToken.ReasonAbsent));
            Assert.Equal("   ", Service().Decorate("   ", Observed("r"), ConditionalReadToken.ReasonAbsent));
        }
    }
}