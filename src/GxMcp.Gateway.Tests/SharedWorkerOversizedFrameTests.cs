using System;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Issue #347, gateway half. The connection's read loop threw on any frame over the
    /// transport limit, and throwing signalled a disconnect - so one attachment's
    /// legitimately large response took down every other client on the same broker.
    ///
    /// <para>
    /// The classifier is duplicated from the Worker host's rather than shared, because the
    /// two live in different processes and the bound itself is already declared
    /// independently on each side. That duplication is only safe if the two stay in step,
    /// so <see cref="Both_Sides_Declare_The_Same_Limits_And_Disposition_Table"/> pins them
    /// against each other.
    /// </para>
    /// </summary>
    public class SharedWorkerOversizedFrameTests
    {
        private static long ByteCount(string s) => Encoding.UTF8.GetByteCount(s);

        private static string ValidFrame(string id, int approxBytes)
        {
            const string prefix = "{\"jsonrpc\":\"2.0\",\"id\":\"";
            const string suffix = "\",\"result\":{\"blob\":\"";
            const string tail = "\"}}";
            int pad = approxBytes - (int)ByteCount(prefix + id + suffix + tail);
            if (pad < 0) pad = 0;
            return prefix + id + suffix + new string('x', pad) + tail;
        }

        [Fact]
        public void A_Valid_Oversized_Response_Becomes_A_Per_Request_Error()
        {
            string line = ValidFrame("req-A", SharedWorkerConnection.MaxFrameBytes + 2048);

            string? refusal = SharedWorkerOversizedFrame.TryBuildRefusal(
                line, ByteCount(line), SharedWorkerConnection.MaxFrameBytes,
                SharedWorkerConnection.HardFrameCeilingBytes);

            Assert.NotNull(refusal);
            var frame = JObject.Parse(refusal!);
            Assert.Equal("req-A", (string?)frame["id"]);
            Assert.Equal("WorkerResponseTooLarge", (string?)frame["error"]!["code"]);
            // The frame is delivered in place of the oversized one, so the requester's
            // pending request is completed with a real answer rather than left hanging.
            Assert.True(ByteCount(refusal!) < SharedWorkerConnection.MaxFrameBytes);
        }

        [Fact]
        public void Malformed_Oversized_Data_Still_Fails_Closed()
        {
            string line = "{\"jsonrpc\":\"2.0\",\"id\":\"req-A\",\"result\":{\"blob\":\""
                + new string('x', SharedWorkerConnection.MaxFrameBytes + 100);

            Assert.Null(SharedWorkerOversizedFrame.TryBuildRefusal(
                line, ByteCount(line), SharedWorkerConnection.MaxFrameBytes,
                SharedWorkerConnection.HardFrameCeilingBytes));
        }

        [Fact]
        public void A_Frame_Beyond_The_Hard_Ceiling_Fails_Closed()
        {
            string line = ValidFrame("req-A", SharedWorkerConnection.HardFrameCeilingBytes + 1024);

            Assert.Null(SharedWorkerOversizedFrame.TryBuildRefusal(
                line, ByteCount(line), SharedWorkerConnection.MaxFrameBytes,
                SharedWorkerConnection.HardFrameCeilingBytes));
        }

        [Fact]
        public void A_Frame_With_No_Request_Id_Cannot_Be_Refused()
        {
            // No requester to answer. Guessing an owner would deliver one client's error
            // into another client's stream, so this fails closed instead.
            string line = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/worker/build_active\",\"blob\":\""
                + new string('x', SharedWorkerConnection.MaxFrameBytes + 100) + "\"}";

            Assert.Null(SharedWorkerOversizedFrame.TryBuildRefusal(
                line, ByteCount(line), SharedWorkerConnection.MaxFrameBytes,
                SharedWorkerConnection.HardFrameCeilingBytes));
        }

        [Fact]
        public void The_Refusal_Is_Explicitly_Incomplete_And_Not_Truncated()
        {
            string line = ValidFrame("req-A", SharedWorkerConnection.MaxFrameBytes + 2048);
            string refusal = SharedWorkerOversizedFrame.TryBuildRefusal(
                line, ByteCount(line), SharedWorkerConnection.MaxFrameBytes,
                SharedWorkerConnection.HardFrameCeilingBytes)!;

            var data = (JObject)JObject.Parse(refusal)["error"]!["data"]!;
            Assert.False(data["complete"]!.Value<bool>());
            Assert.False(data["truncated"]!.Value<bool>());
            Assert.True(data["retryable"]!.Value<bool>());
        }

        [Fact]
        public void The_Read_Loop_Delivers_The_Refusal_Instead_Of_Disconnecting()
        {
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Gateway", "SharedWorkerConnection.cs");

            int refusal = source.IndexOf("TryBuildRefusal", StringComparison.Ordinal);
            int throwSite = source.IndexOf("InvalidDataException", refusal, StringComparison.Ordinal);
            int deliver = source.IndexOf("LineReceived?.Invoke(oversizedRefusal)", StringComparison.Ordinal);
            int resume = source.IndexOf("continue;", deliver, StringComparison.Ordinal);

            Assert.True(refusal > 0, "the oversized frame is not classified");
            Assert.True(throwSite > refusal, "the unconditional throw was not moved behind the fail-closed branch");
            Assert.True(deliver > 0, "the refusal is not delivered to the requester");
            Assert.True(resume > deliver,
                "the read loop must continue after refusing, or every later frame on the connection is lost");
        }

        [Fact]
        public void Both_Sides_Declare_The_Same_Limits_And_Disposition_Table()
        {
            // The classifier is duplicated because the two run in different processes.
            // That is only safe while the limits and the decision table agree; this is
            // what holds them together.
            string workerHost = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "SharedWorkerHost.cs");
            string workerProtocol = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "SharedWorkerHostProtocol.cs");
            string gatewayConnection = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Gateway", "SharedWorkerConnection.cs");

            // Both bound the frame at 4 MiB.
            Assert.Contains("MaxFrameBytes = 4 * 1024 * 1024", workerProtocol, StringComparison.Ordinal);
            Assert.Contains("MaxFrameBytes = 4 * 1024 * 1024", gatewayConnection, StringComparison.Ordinal);

            // Both refuse to parse past four times the bound.
            Assert.Contains("HardFrameCeilingBytes = 4 * MaxFrameBytes", workerProtocol, StringComparison.Ordinal);
            Assert.Contains("HardFrameCeilingBytes = 4 * MaxFrameBytes", gatewayConnection, StringComparison.Ordinal);

            // And both refuse per request rather than disconnecting.
            Assert.Contains("SendJson(_attachments, oversizedRoute.AttachmentId", workerHost, StringComparison.Ordinal);
            Assert.Contains("LineReceived?.Invoke(oversizedRefusal)", gatewayConnection, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Gateway_Still_Rejects_Oversized_Frames_It_Is_Asked_To_Send()
        {
            // The read side is now lenient about *receiving* an oversized valid frame;
            // the write side must stay strict, or the gateway would be the one producing
            // frames the broker has to refuse.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Gateway", "SharedWorkerConnection.cs");

            int write = source.IndexOf("private async Task WriteLineAsync(string line", StringComparison.Ordinal);
            Assert.True(write > 0, "the write path was not found");
            var window = source.Substring(write, Math.Min(900, source.Length - write));

            Assert.Contains("MaxFrameBytes", window, StringComparison.Ordinal);
            Assert.Contains("InvalidDataException", window, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Two_Client_Scenario_Leaves_The_Second_Client_Receiving_Its_Normal_Response()
        {
            // The acceptance criterion, as far as this assembly can reach it: client A's
            // valid oversized response is refused per request while client B's small
            // response is routed normally, with no correlation between them. The broker
            // itself is a separate process and is covered on the Worker side.
            string bigA = ValidFrame("req-A", SharedWorkerConnection.MaxFrameBytes + 2048);
            string smallB = ValidFrame("req-B", 512);

            string? refusalA = SharedWorkerOversizedFrame.TryBuildRefusal(
                bigA, ByteCount(bigA), SharedWorkerConnection.MaxFrameBytes,
                SharedWorkerConnection.HardFrameCeilingBytes);
            string routedB = JObject.Parse(smallB).ToString(Newtonsoft.Json.Formatting.None);

            Assert.NotNull(refusalA);
            Assert.Equal("req-A", (string?)JObject.Parse(refusalA!)["id"]);

            // B is untouched: still a normal result frame, not a refusal.
            var bFrame = JObject.Parse(routedB);
            Assert.Equal("req-B", (string?)bFrame["id"]);
            Assert.Null(bFrame["error"]);
            Assert.NotNull(bFrame["result"]);
        }

        [Fact]
        public void Unicode_Whose_Byte_Length_Exceeds_Its_Character_Count_Is_Measured_In_Bytes()
        {
            // Acceptance criterion two. The bound is a transport bound, so it must be
            // measured in UTF-8 bytes: 3-byte characters hit the limit at a third of the
            // character count, and a character-count check would let them through.
            // Built arithmetically - measuring a growing buffer on each iteration is
            // quadratic and took minutes.
            const string header = "{\"jsonrpc\":\"2.0\",\"id\":\"req-U\",\"result\":{\"blob\":\"";
            const string tail = "\"}}";
            int charsNeeded = (SharedWorkerConnection.MaxFrameBytes - (int)ByteCount(header + tail)) / 3 + 1;
            string line = header + new string('日', charsNeeded) + tail;

            int characters = line.Length;
            long bytes = ByteCount(line);

            Assert.True(bytes > characters, "the fixture must contain multi-byte text");
            Assert.True(bytes > SharedWorkerConnection.MaxFrameBytes,
                "the fixture must actually exceed the bound in bytes, not just in characters");
            // The point of the criterion: measured in characters it is comfortably under,
            // so a character-count check would have let this frame straight through.
            Assert.True(characters < SharedWorkerConnection.MaxFrameBytes,
                "a character-count check would let this frame through, which is the defect being pinned");

            string? refusal = SharedWorkerOversizedFrame.TryBuildRefusal(
                line, bytes, SharedWorkerConnection.MaxFrameBytes,
                SharedWorkerConnection.HardFrameCeilingBytes);
            Assert.NotNull(refusal);
            Assert.Equal("req-U", (string?)JObject.Parse(refusal!)["id"]);
        }
    }
}