using System;
using System.Text;
using GxMcp.Worker;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #347: the shared-transport frame bound is 4 MiB, and when a child emitted a
    /// line exceeding it the broker broadcast an error and cancelled the host. A valid
    /// large read/batch/persisted-content response therefore affected every attachment,
    /// rather than returning a bounded per-request outcome.
    ///
    /// <para>
    /// The fix is a distinction the old code did not make: a large frame that <em>parses</em>
    /// is a valid response that does not fit the transport, and belongs to one requester.
    /// A frame that does not parse, or that is beyond the hard ceiling, is malformed or
    /// abusive and still stops the host. Both halves are pinned here, because an
    /// implementation that only did the first would have opened a hole in the fail-closed
    /// check.
    /// </para>
    /// </summary>
    public class SharedWorkerOversizedFrameTests
    {
        private static string ValidFrame(string id, string payload)
            => "{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"result\":{\"blob\":\"" + payload + "\"}}";

        private static long ByteCount(string s) => Encoding.UTF8.GetByteCount(s);

        /// <summary>
        /// A frame just under, at, and over the bound. Built to a target byte length so
        /// the boundary cases are hit exactly rather than approximately.
        /// </summary>
        private static string FrameOfApproximateBytes(long targetBytes)
        {
            const string prefix = "{\"jsonrpc\":\"2.0\",\"id\":\"probe\",\"result\":{\"blob\":\"";
            const string suffix = "\"}}";
            long pad = targetBytes - ByteCount(prefix) - ByteCount(suffix);
            if (pad < 0) pad = 0;
            return ValidFrame("probe", new string('x', (int)pad));
        }

        [Fact]
        public void A_Frame_At_The_Bound_Is_Not_Oversized()
        {
            string line = FrameOfApproximateBytes(SharedWorkerHostProtocol.MaxFrameBytes);
            Assert.Equal(SharedWorkerHostProtocol.MaxFrameBytes, ByteCount(line));
            // The classifier is only consulted above the bound; at it, nothing happens.
            Assert.True(ByteCount(line) <= SharedWorkerHostProtocol.MaxFrameBytes);
        }

        [Fact]
        public void A_Valid_Oversized_Response_Is_Refused_Per_Request_Not_Fatally()
        {
            string line = FrameOfApproximateBytes(SharedWorkerHostProtocol.MaxFrameBytes + 4096);

            var disposition = SharedWorkerHostProtocol.ClassifyOversizedFrame(
                line, out string id, ByteCount(line));

            Assert.Equal(SharedWorkerHostProtocol.OversizedFrameDisposition.RejectRequest, disposition);
            Assert.Equal("probe", id);
        }

        [Fact]
        public void Malformed_Data_Still_Fails_Closed_Even_When_Oversized()
        {
            // The oversized path must not become a way to smuggle unparseable data past
            // the fail-closed check. This is the security half of the fix.
            string line = "{\"jsonrpc\":\"2.0\",\"id\":\"probe\",\"result\":{\"blob\":\"" + new string('x', 5000);

            var disposition = SharedWorkerHostProtocol.ClassifyOversizedFrame(
                line, out string id, ByteCount(line));

            Assert.Equal(SharedWorkerHostProtocol.OversizedFrameDisposition.FailClosed, disposition);
            Assert.Null(id);
        }

        [Fact]
        public void Valid_Json_That_Is_Not_An_Object_Fails_Closed()
        {
            foreach (string line in new[] { "\"just a string\"", "[1,2,3]", "42", "null" })
            {
                var disposition = SharedWorkerHostProtocol.ClassifyOversizedFrame(
                    line, out string id, SharedWorkerHostProtocol.MaxFrameBytes + 1);
                Assert.Equal(SharedWorkerHostProtocol.OversizedFrameDisposition.FailClosed, disposition);
                Assert.Null(id);
            }
        }

        [Fact]
        public void A_Frame_Beyond_The_Hard_Ceiling_Is_Not_Even_Parsed()
        {
            // Past the ceiling the frame is no longer a plausible response, and refusing
            // to buffer it is the point of having a ceiling at all.
            string line = ValidFrame("probe", new string('x', SharedWorkerHostProtocol.HardFrameCeilingBytes + 10));

            var disposition = SharedWorkerHostProtocol.ClassifyOversizedFrame(
                line, out string id, ByteCount(line));

            Assert.Equal(SharedWorkerHostProtocol.OversizedFrameDisposition.FailClosed, disposition);
            Assert.Null(id);
        }

        [Fact]
        public void The_Ceiling_Is_A_Multiple_Of_The_Frame_Bound()
        {
            // Pinned so the two cannot drift into a relationship that makes one of them
            // unreachable.
            Assert.True(SharedWorkerHostProtocol.HardFrameCeilingBytes > SharedWorkerHostProtocol.MaxFrameBytes);
            Assert.Equal(0, SharedWorkerHostProtocol.HardFrameCeilingBytes % SharedWorkerHostProtocol.MaxFrameBytes);
        }

        [Fact]
        public void An_Oversized_Notification_Is_Dropped_And_The_Host_Survives()
        {
            // No id means no requester to answer. Dropping with a log is right: a valid
            // notification that is too large is not evidence of a broken transport.
            string line = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/worker/build_active\",\"blob\":\""
                + new string('x', 5000) + "\"}";

            var disposition = SharedWorkerHostProtocol.ClassifyOversizedFrame(
                line, out string id, ByteCount(line));

            Assert.Equal(SharedWorkerHostProtocol.OversizedFrameDisposition.DropNotification, disposition);
            Assert.Null(id);
        }

        [Fact]
        public void A_Null_Id_Is_Treated_As_A_Notification_Not_A_Request()
        {
            string line = "{\"jsonrpc\":\"2.0\",\"id\":null,\"result\":{\"blob\":\""
                + new string('x', 5000) + "\"}}";

            Assert.Equal(
                SharedWorkerHostProtocol.OversizedFrameDisposition.DropNotification,
                SharedWorkerHostProtocol.ClassifyOversizedFrame(line, out string id, ByteCount(line)));
        }

        [Fact]
        public void The_Refusal_Correlates_To_The_Original_Request()
        {
            string line = FrameOfApproximateBytes(SharedWorkerHostProtocol.MaxFrameBytes + 4096);
            SharedWorkerHostProtocol.ClassifyOversizedFrame(line, out string id, ByteCount(line));

            var refusal = SharedWorkerHostProtocol.BuildOversizedResponse(
                id, ByteCount(line), SharedWorkerHostProtocol.MaxFrameBytes);

            Assert.Equal("probe", (string)refusal["id"]);
            Assert.Equal("WorkerResponseTooLarge", (string)refusal["error"]!["code"]);
        }

        [Fact]
        public void The_Refusal_Says_The_Result_Is_Incomplete_And_Not_Truncated()
        {
            // The alternative failure is a caller reading a refused-then-truncated payload
            // as the whole result, so both flags are explicit.
            var refusal = SharedWorkerHostProtocol.BuildOversizedResponse("probe", 5_000_000, 4_194_304);
            var data = (JObject)refusal["error"]!["data"]!;

            Assert.False(data["complete"]!.Value<bool>());
            Assert.False(data["truncated"]!.Value<bool>());
            Assert.True(data["retryable"]!.Value<bool>());
            Assert.Equal(5_000_000L, data["responseBytes"]!.Value<long>());
            Assert.Equal(4_194_304L, data["maxFrameBytes"]!.Value<long>());
        }

        [Fact]
        public void The_Refusal_Tells_The_Caller_Not_To_Read_It_As_Empty()
        {
            var refusal = SharedWorkerHostProtocol.BuildOversizedResponse("probe", 5_000_000, 4_194_304);
            string hint = (string)refusal["error"]!["data"]!["hint"]!;

            Assert.Contains("NOT treat this as an empty result", hint, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_Refusal_Is_Bounded_So_It_Cannot_Itself_Exceed_The_Limit()
        {
            // A refusal that did not fit would be refused again, in a loop.
            string json = SharedWorkerHostProtocol.BuildOversizedResponse(
                "probe", (long)SharedWorkerHostProtocol.MaxFrameBytes * 1000L,
                SharedWorkerHostProtocol.MaxFrameBytes).ToString(Newtonsoft.Json.Formatting.None);

            Assert.True(ByteCount(json) < SharedWorkerHostProtocol.MaxFrameBytes);
        }

        [Fact]
        public void A_Numeric_Request_Id_Is_Correlated_Correctly()
        {
            // JSON-RPC ids may be numbers; stringifying must not turn 42 into "42" in a way
            // that fails to match the route key, and must not lose it either.
            string line = "{\"jsonrpc\":\"2.0\",\"id\":42,\"result\":{\"blob\":\""
                + new string('x', 5000) + "\"}}";

            var disposition = SharedWorkerHostProtocol.ClassifyOversizedFrame(line, out string id, ByteCount(line));

            Assert.Equal(SharedWorkerHostProtocol.OversizedFrameDisposition.RejectRequest, disposition);
            Assert.Equal("42", id);
        }

        [Fact]
        public void The_Host_Reader_No_Longer_Cancels_For_A_Valid_Oversized_Frame()
        {
            // The routing guard. The read loop needs a real child process, so the property
            // is pinned on the source: the fatal branch must be reachable only from the
            // fail-closed disposition, and the oversized branch must continue rather than
            // break the loop.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "SharedWorkerHost.cs");

            int classify = source.IndexOf("ClassifyOversizedFrame", StringComparison.Ordinal);
            int failClosed = source.IndexOf(
                "OversizedFrameDisposition.FailClosed", classify, StringComparison.Ordinal);
            int stop = source.IndexOf("_stop.Cancel()", failClosed, StringComparison.Ordinal);
            int keepServing = source.IndexOf("continue;", stop, StringComparison.Ordinal);

            Assert.True(classify > 0, "the oversized frame is not classified");
            Assert.True(failClosed > classify, "no fail-closed branch for the oversized path");
            Assert.True(stop > failClosed, "the host is not stopped from the fail-closed branch");
            Assert.True(keepServing > stop,
                "the oversized path must resume reading after refusing, or every later frame - including other attachments' - is lost");

            // And the refusal is routed to the owning attachment, not broadcast.
            Assert.Contains("SendJson(_attachments, oversizedRoute.AttachmentId", source, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Refusal_Reaches_Only_The_Owning_Attachment()
        {
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "SharedWorkerHost.cs");

            int start = source.IndexOf("OversizedFrameDisposition.RejectRequest", StringComparison.Ordinal);
            Assert.True(start > 0, "no per-request refusal branch");
            var window = source.Substring(start, Math.Min(1600, source.Length - start));

            Assert.Contains("SendJson(_attachments, oversizedRoute.AttachmentId", window, StringComparison.Ordinal);
            // Broadcasting here is precisely the defect: it would push one client's refusal
            // to every other attachment.
            Assert.DoesNotContain("BroadcastJson(", window, StringComparison.Ordinal);
            Assert.DoesNotContain("BroadcastHostError(", window, StringComparison.Ordinal);
        }

        [Fact]
        public void An_Unrouted_Oversized_Response_Is_Logged_Rather_Than_Misrouted()
        {
            // The request may already have completed, timed out or detached. Guessing an
            // owner would deliver one client's error to another client's stream.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "SharedWorkerHost.cs");

            Assert.Contains("unrouted request id=", source, StringComparison.Ordinal);
        }
    }
}