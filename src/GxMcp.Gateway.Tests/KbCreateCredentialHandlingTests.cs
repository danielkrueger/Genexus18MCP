using System;
using System.Text;
using GxMcp.Gateway;
using GxMcp.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// <c>genexus_kb action=create</c> spawns MSBuild to instantiate a Knowledge Base, and the
    /// database password it was given used to travel as a <c>/p:</c> property on that child's
    /// command line. On Windows that is readable by any process able to query the process, and
    /// MSBuild echoes its own invocation back in its error output - which the failure envelope
    /// then returned to the caller verbatim in <c>["output"]</c>. A caller-supplied credential
    /// that rarely rotates makes that a burn-and-rotate event rather than a transient.
    ///
    /// <para>These guards are mostly source-shape, because the property being absent from a string
    /// is not something a behavioural test can observe without a real GeneXus SDK and a real
    /// MSBuild spawn. The behavioural half is the envelope builder, which is where the echo
    /// actually reached the caller.</para>
    ///
    /// <para>The sentinel below is synthetic on purpose. It must never be a real or a plausible
    /// password: a test artifact that ships a working credential is a leak with a test runner's
    /// half-life.</para>
    /// </summary>
    public class KbCreateCredentialHandlingTests
    {
        /// <summary>Synthetic, never real. Its value is irrelevant; only its absence matters.</summary>
        private const string Sentinel = "SENTINEL-PW-DO-NOT-LEAK";

        private static string Helper() =>
            RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Gateway", "KbCreateHelper.cs"));

        [Fact]
        public void ThePasswordIsNotOnTheChildCommandLine()
        {
            string source = Helper();

            // The old shape, verbatim. A `/p:` whose value is interpolated carries the password
            // onto a command line any process can read.
            Assert.Equal(0, SourceAssert.Count(source, "/p:KBDbPassword={"));
            Assert.Equal(0, SourceAssert.Count(source, "$@\"/p:KBDbPassword="));
            Assert.Equal(0, SourceAssert.Count(source, "{options.DbPassword}"));

            // The property now reaches MSBuild by name, and only by name.
            Assert.Equal(1, SourceAssert.Count(source, "\"/p:KBDbPassword=\" + QuoteResponseValue(options.DbPassword)"));

            // The value is read exactly once, at the point it is written to the response file.
            Assert.Equal(1, SourceAssert.Count(source, "options.DbPassword"));

            // The response file reference is what travels on the command line instead, and it is
            // quoted: MSBuild takes the whole token as the path, so an unquoted reference under a
            // temp path containing spaces is split by the child's command-line parser.
            Assert.Equal(1, SourceAssert.Count(source, "Append(\" \\\"@\")"));

            // And the file it names is deleted in the existing finally, so a failed spawn cannot
            // leave a file holding the password in the clear on disk.
            string finallyBody = SourceAssert.MethodBody(source, "finally");
            Assert.Equal(1, SourceAssert.Count(finallyBody, "File.Delete(tempProj)"));
            Assert.Equal(1, SourceAssert.Count(finallyBody, "File.Delete(tempRsp)"));
        }

        [Fact]
        public void TheFailureEnvelopeDoesNotEchoThePassword()
        {
            // MSBuild's own failure output echoes the invocation back. That echo is what reached
            // the caller, so the shape it takes is the one that has to be safe.
            string echoedInvocation =
                "MSBUILD : error MSB1006: Property is not valid.\r\n" +
                "Switch: /p:KBDbPassword=" + Sentinel + "\r\n";

            var envelope = KbCreateHelper.BuildCreationFailureEnvelope(
                @"C:\kb\demo", 1, 12, echoedInvocation);

            Assert.Equal("Error", envelope["status"]?.ToString());
            Assert.Equal("KbCreationFailed", envelope["code"]?.ToString());

            string output = Assert.IsType<string>(envelope["output"]?.ToString());
            Assert.DoesNotContain(Sentinel, output);
            // The key survives so the failure is still diagnosable; only the value goes.
            Assert.Contains("<redacted>", output);
        }

        [Fact]
        public void TheOutputFieldIsBounded()
        {
            // A KB create that dumps a multi-megabyte log is a nuisance, and the absolute SDK path
            // inside it is not something the caller needs echoed verbatim.
            string huge = new string('x', KbCreateHelper.MaxFailureOutputBytes * 4);

            var envelope = KbCreateHelper.BuildCreationFailureEnvelope(@"C:\kb\demo", 1, 5, huge);
            string output = Assert.IsType<string>(envelope["output"]?.ToString());

            Assert.True(output.Length < huge.Length, "output was not truncated");
            Assert.True(
                Encoding.UTF8.GetByteCount(output) <= KbCreateHelper.MaxFailureOutputBytes,
                "output exceeded the documented cap after truncation");
            Assert.Contains(KbCreateHelper.TruncationMarker, output);

            // A cut that lands mid-UTF-8 sequence would put invalid text in the response, which is
            // worse than losing a few bytes: the cap is applied on a rune boundary.
            string multibyte = new string('a', KbCreateHelper.MaxFailureOutputBytes) + "\U0001F600" + "tail";
            string bounded = KbCreateHelper.BoundFailureOutput(multibyte);
            Assert.Contains(KbCreateHelper.TruncationMarker, bounded);
            // The emoji sits one byte past the cap, so a rune-blind cut would leave half of it.
            Assert.False(
                bounded.Contains("\U0001F600"),
                "the truncation boundary must not land inside a surrogate pair");
        }

        [Fact]
        public void LogRedactionStillCoversThePasswordShape()
        {
            // Tests 2 and 3 assert on the sentinel specifically, so they would stay green if a
            // refactor replaced BoundFailureOutput with something that only truncates. This one
            // pins the redaction call itself: a bare password-shaped key, with no sentinel in it.
            string echoed = "error MSB4014: login failed for Password=SENTINEL-PW-DO-NOT-LEAK";

            string bounded = KbCreateHelper.BoundFailureOutput(echoed);

            Assert.DoesNotContain(Sentinel, bounded);
            Assert.Contains("<redacted>", bounded);

            // The shared redaction primitive is reached from here, so its key list stays the one
            // place that decides what a credential shape looks like. Pinned by shape rather than
            // by behaviour because a behaviour-only assertion cannot tell "no password was passed"
            // apart from "the sentinel never reached this code".
            string body = SourceAssert.MethodBody(Helper(), "internal static string BoundFailureOutput(");
            Assert.Equal(1, SourceAssert.Count(body, "LogRedaction.Redact("));

            // The other half of the same finding, pinned: the generic pattern's word boundary
            // means `KBDbPassword=` slips past it, so the call-site mask is what actually
            // redacts the property the template uses. Dropping it leaves the echo in the
            // response while every other assertion here stays green.
            Assert.Equal(1, SourceAssert.Count(body, "MaskDbPasswordProperty("));
            Assert.Equal(
                "x /p:KBDbPassword=<redacted>",
                KbCreateHelper.MaskDbPasswordProperty("x /p:KBDbPassword=" + Sentinel));
        }
    }
}