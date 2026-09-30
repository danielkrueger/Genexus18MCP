using System;
using System.Linq;
using GxMcp.Gateway;
using GxMcp.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// The <c>resources/read</c> completion envelope was written out in full seven
    /// times inside <c>BuildStaticResourceResponse</c>, and the bare lifetime
    /// <c>3600000</c> appeared in eight places across the router. It is now one
    /// <c>ResourceCompletion</c> and two constants.
    ///
    /// The reason to pin it behaviourally rather than by counting source text is
    /// that the existing coverage did not: it asserted <c>resultType == "complete"
    /// </c> and <c>ttlMs &gt; 0</c> for the playbook resources, and nothing at all
    /// about <c>cacheScope</c>, the actual lifetime, the MIME type or the URI the
    /// envelope echoes. Those are the fields a client reads and the ones that drift.
    /// Every assertion here goes through <c>McpRouter.Handle</c>, so it is the
    /// emitted envelope that is checked, not the code that emits it.
    /// </summary>
    public class McpResourceEnvelopeTests
    {
        private const int PublicTtlMs = 3600000;
        private const int LiveTtlMs = 1000;

        private static JObject Read(string uri)
        {
            var request = JObject.Parse(
                "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"resources/read\",\"params\":{\"uri\":\"" + uri + "\"}}");

            var result = McpRouter.Handle(request);
            Assert.NotNull(result);

            return JObject.FromObject(result!);
        }

        private static JObject FirstContent(JObject envelope)
        {
            var contents = envelope["contents"] as JArray;
            Assert.NotNull(contents);
            Assert.NotEmpty(contents!);
            return (JObject)contents![0]!;
        }

        /// <summary>
        /// The five resources reachable by an exact URI, plus one discovered from
        /// each prefix-matched family so the set cannot go stale by hardcoding a
        /// key that no longer exists.
        /// </summary>
        public static TheoryData<string, string> PublicResources()
        {
            var data = new TheoryData<string, string>();

            data.Add("genexus://kb/agent-playbook", "genexus://kb/agent-playbook");
            data.Add("genexus://kb/llm-playbook", "genexus://kb/llm-playbook");

            string? skill = SkillCatalog.All.Select(s => s.Key).FirstOrDefault();
            Assert.False(string.IsNullOrEmpty(skill), "no skill key available to exercise the skills resource");
            data.Add("genexus://kb/skills/" + skill, "genexus://kb/skills/" + skill);

            string? tool = ToolHelpCatalog.KnownTools.FirstOrDefault();
            Assert.False(string.IsNullOrEmpty(tool), "no tool help entry available to exercise tool-help");
            data.Add("genexus://kb/tool-help/" + tool, "genexus://kb/tool-help/" + tool);

            string? gotcha = ToolHelpCatalog.KnownGotchaCodes.FirstOrDefault();
            Assert.False(string.IsNullOrEmpty(gotcha), "no gotcha code available to exercise the gotcha resource");
            data.Add("genexus://kb/tool-help/gotchas/" + gotcha, "genexus://kb/tool-help/gotchas/" + gotcha);

            return data;
        }

        [Theory]
        [MemberData(nameof(PublicResources))]
        public void EveryStaticDocResourceCarriesTheSameCacheContract(string uri, string expectedEcho)
        {
            var envelope = Read(uri);

            Assert.Equal("complete", envelope["resultType"]?.ToString());
            Assert.Equal(PublicTtlMs, envelope["ttlMs"]!.Value<int>());
            Assert.Equal("public", envelope["cacheScope"]?.ToString());

            var content = FirstContent(envelope);
            Assert.Equal(expectedEcho, content["uri"]?.ToString());
            Assert.Equal("text/markdown", content["mimeType"]?.ToString());
            Assert.False(string.IsNullOrWhiteSpace(content["text"]?.ToString()), "resource body was empty for " + uri);
        }

        [Fact]
        public void HealthIsTheOnlyLiveResource()
        {
            // Health reflects the running Gateway, so it is served short-lived and
            // private. Every other static document is an hour and public. If the
            // shared envelope ever absorbed the health values, a client would be
            // entitled to hold a stale health report for an hour.
            var envelope = Read("genexus://kb/health");

            Assert.Equal("complete", envelope["resultType"]?.ToString());
            Assert.Equal(LiveTtlMs, envelope["ttlMs"]!.Value<int>());
            Assert.Equal("private", envelope["cacheScope"]?.ToString());
            Assert.Equal("text/markdown", FirstContent(envelope)["mimeType"]?.ToString());
        }

        [Fact]
        public void NoStaticDocResourceIsServedPrivately()
        {
            // The counterpart to the test above: health being the exception is only
            // meaningful if nothing else drifted into private.
            // TheoryData enumerates as object[], so index rather than deconstruct.
            foreach (var row in PublicResources())
            {
                string uri = (string)row[0];
                var envelope = Read(uri);
                Assert.Equal("public", envelope["cacheScope"]?.ToString());
                Assert.Equal(PublicTtlMs, envelope["ttlMs"]!.Value<int>());
            }
        }

        [Fact]
        public void ExactMatchResourcesEchoTheCanonicalLowercaseUriNotTheRequest()
        {
            // UnscopeResourceUri preserves the caller's casing, so these three are
            // handed a literal rather than the request. Asking in a different case
            // and getting the canonical form back is the current, deliberate
            // behaviour, and it differs from the four prefix-matched resources
            // below. Pinned because normalising either side changes what a client
            // reads in contents[].uri.
            var envelope = Read("GENEXUS://KB/AGENT-PLAYBOOK");

            Assert.Equal(PublicTtlMs, envelope["ttlMs"]!.Value<int>());
            Assert.Equal("genexus://kb/agent-playbook", FirstContent(envelope)["uri"]?.ToString());
        }

        [Fact]
        public void PrefixMatchedResourcesEchoTheRequestUriIncludingItsCase()
        {
            // The other half of the inconsistency, pinned for the same reason.
            string? tool = ToolHelpCatalog.KnownTools.FirstOrDefault();
            Assert.False(string.IsNullOrEmpty(tool));

            var envelope = Read("GENEXUS://KB/TOOL-HELP/" + tool);

            Assert.Equal(PublicTtlMs, envelope["ttlMs"]!.Value<int>());
            Assert.Equal("GENEXUS://KB/TOOL-HELP/" + tool, FirstContent(envelope)["uri"]?.ToString());
        }

        [Fact]
        public void TheCacheLifetimeLiteralIsDefinedOnce()
        {
            // The value a client reads was the bare literal 3600000 in eight places.
            // One constant, one place to change it - and one place for a reader to
            // find out what it means.
            //
            // Comments are stripped first: this constant's own documentation quotes
            // the literal to explain where it came from, and counting prose would
            // report a definition that is not there.
            string source = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Gateway", "McpRouter.cs"));

            Assert.Equal(1, SourceAssert.Count(source, "3600000"));
            Assert.Equal(1, SourceAssert.Count(source, "private const int PublicResourceTtlMs = 3600000;"));
            Assert.Equal(1, SourceAssert.Count(source, "private const int LiveResourceTtlMs = 1000;"));

            // The envelope is assembled in exactly one place. Seven hand-written
            // copies is what this replaced; zero would mean the helper stopped
            // working, which the behavioural tests above would also catch.
            Assert.Equal(1, SourceAssert.Count(source, "contents = new[]"));
            Assert.Equal(1, SourceAssert.Count(source, "private static object ResourceCompletion(string uri, string text, int ttlMs, string cacheScope) =>"));
        }

    }
}
