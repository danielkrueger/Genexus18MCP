using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Attribution and refusal for an oversized shared-transport frame. Issue #347.
    ///
    /// <para>
    /// The read loop used to throw on any frame over the transport limit, which signalled
    /// a disconnect and took down every attachment on the broker - so one client's
    /// legitimately large read, batch or persisted-content result became every client's
    /// outage. The distinction that fixes it is not about size. A frame that parses is a
    /// valid response that does not fit the transport, and the answer is a bounded
    /// per-request error delivered to the requester. A frame that does not parse, or that
    /// is beyond the hard ceiling, is malformed or abusive and still fails closed - the
    /// oversized path must not become a way past that check.
    /// </para>
    ///
    /// <para>
    /// Deliberately duplicated from the Worker host's classifier rather than shared:
    /// the two live in different processes, and the frame bound itself is already declared
    /// independently on both sides. Keeping the two in step is covered by a test that
    /// pins the same limits and the same disposition table on each side.
    /// </para>
    /// </summary>
    internal static class SharedWorkerOversizedFrame
    {
        /// <summary>
        /// Builds the refusal to send back for an oversized frame, or null when the frame
        /// cannot be attributed to a request and the caller must fail closed.
        /// </summary>
        /// <param name="line">The raw frame text, already read from the transport.</param>
        /// <param name="byteCount">UTF-8 byte length.</param>
        /// <param name="limitBytes">The frame bound the response exceeded.</param>
        /// <param name="ceilingBytes">Absolute ceiling past which frames are not parsed.</param>
        internal static string? TryBuildRefusal(string line, long byteCount, long limitBytes, long ceilingBytes)
        {
            if (line == null || byteCount > ceilingBytes) return null;

            JObject frame;
            try { frame = GxMcp.Common.JsonIngress.ParseObject(line); }
            catch { return null; } // malformed stays malformed
            if (frame == null) return null;

            var id = frame["id"];
            // No id means nothing to answer. There is no requester to refuse, and
            // guessing an owner would misroute data across attachments.
            if (id == null || id.Type == JTokenType.Null) return null;

            string correlationId = id.ToString();
            if (string.IsNullOrEmpty(correlationId)) return null;

            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id.DeepClone(),
                ["error"] = new JObject
                {
                    ["code"] = "WorkerResponseTooLarge",
                    ["message"] = "This response is " + byteCount + " bytes, over the shared-transport limit of "
                        + limitBytes + " bytes, so it was refused rather than truncated. Other clients are unaffected.",
                    ["data"] = new JObject
                    {
                        ["responseBytes"] = byteCount,
                        ["maxFrameBytes"] = limitBytes,
                        // Explicit, because the alternative failure is a caller reading a
                        // truncated payload as the whole result.
                        ["complete"] = false,
                        ["truncated"] = false,
                        ["retryable"] = true,
                        ["hint"] = "Narrow the request (smaller page/limit), or write the full result to a KB-scoped artifact and read it in pieces. Do NOT treat this as an empty result."
                    }
                }
            }.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}