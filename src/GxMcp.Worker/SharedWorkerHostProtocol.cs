using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker
{
    internal sealed class SharedWorkerEnvelope
    {
        public string Type { get; set; } = string.Empty;
        public int ProtocolVersion { get; set; }
        public string IdentityKey { get; set; } = string.Empty;
        public int GatewayPid { get; set; }
        public long GatewayStartTimeUtcTicks { get; set; }
        public string AttachNonce { get; set; } = string.Empty;
        public string AttachmentId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    internal sealed class SharedWorkerRequestRoute
    {
        public string AttachmentId { get; set; } = string.Empty;
        public JToken ClientRequestId { get; set; } = JValue.CreateNull();
        public string ChildRequestId { get; set; } = string.Empty;
        public JToken ClientProgressToken { get; set; } = JValue.CreateNull();
        public string ChildProgressToken { get; set; } = string.Empty;
    }

    /// <summary>
    /// Pure framing and correlation helpers shared by the host loop and its tests.
    /// The SDK is deliberately not referenced here: host transport failures must be
    /// testable without opening a Knowledge Base.
    /// </summary>
    internal static class SharedWorkerHostProtocol
    {
        internal const int ProtocolVersion = 1;
        internal const int MaxFrameBytes = 4 * 1024 * 1024;

        /// <summary>
        /// Absolute ceiling beyond which a frame is treated as abusive rather than
        /// merely large. Issue #347.
        ///
        /// <para>
        /// Between <see cref="MaxFrameBytes"/> and this ceiling a frame is parsed and,
        /// if it is well-formed, answered with a per-request error - a valid large read
        /// must not be able to affect other attachments. Above the ceiling we stop
        /// fail-closed instead: past a point the frame is no longer a plausible response,
        /// and refusing to buffer it is the point of having a ceiling at all.
        /// </para>
        /// </summary>
        internal const int HardFrameCeilingBytes = 4 * MaxFrameBytes;

        /// <summary>
        /// What the host should do with a child frame that exceeds
        /// <see cref="MaxFrameBytes"/>.
        /// </summary>
        internal enum OversizedFrameDisposition
        {
            /// <summary>
            /// The frame is well-formed JSON-RPC carrying a request id. The requester
            /// gets a bounded structured error and the host keeps serving everyone else.
            /// </summary>
            RejectRequest,

            /// <summary>
            /// The frame is well-formed but has no request id, so there is nobody to
            /// answer. It is dropped with a log; the host survives, because a valid
            /// notification that is too large is not evidence of a broken transport.
            /// </summary>
            DropNotification,

            /// <summary>
            /// The frame is malformed, or beyond the hard ceiling. This is the
            /// fail-closed case and it is unchanged from before: corrupt or compromised
            /// transport data still stops the host.
            /// </summary>
            FailClosed,
        }

        /// <summary>
        /// Decides what to do with an oversized child frame, and recovers the request id
        /// when the frame is answerable.
        ///
        /// <para>
        /// Issue #347. The host used to treat every frame over <see cref="MaxFrameBytes"/>
        /// as fatal, so a legitimate large <c>read</c>, batch or persisted-content result
        /// cancelled the broker and took down every attachment - one client's big
        /// response became every client's outage. The distinction this makes is not about
        /// size: a large frame that parses is a valid response that does not fit the
        /// transport, and the right answer is a bounded per-request error. A frame that
        /// does not parse, or that is past the hard ceiling, is malformed or abusive and
        /// still stops the host.
        /// </para>
        /// </summary>
        /// <param name="line">The raw frame text, already read from the child.</param>
        /// <param name="correlationId">
        /// The frame's JSON-RPC id when the frame is answerable, otherwise null.
        /// </param>
        /// <param name="byteCount">UTF-8 byte length, computed by the caller.</param>
        internal static OversizedFrameDisposition ClassifyOversizedFrame(string line, out string correlationId, long byteCount)
        {
            correlationId = null;
            if (line == null) return OversizedFrameDisposition.FailClosed;
            if (byteCount > HardFrameCeilingBytes) return OversizedFrameDisposition.FailClosed;

            JObject frame;
            try { frame = GxMcp.Common.JsonIngress.ParseObject(line); }
            catch
            {
                // Malformed stays malformed. The oversized path must not become a way to
                // smuggle unparseable data past the fail-closed check.
                return OversizedFrameDisposition.FailClosed;
            }
            if (frame == null) return OversizedFrameDisposition.FailClosed;

            var id = frame["id"];
            if (id == null || id.Type == JTokenType.Null)
                return OversizedFrameDisposition.DropNotification;

            correlationId = id.ToString();
            return string.IsNullOrEmpty(correlationId)
                ? OversizedFrameDisposition.FailClosed
                : OversizedFrameDisposition.RejectRequest;
        }

        /// <summary>
        /// The bounded error sent to the requester whose response did not fit. It is
        /// deliberately small and structured: the point is that the caller learns its own
        /// result was refused and can page or request an artifact, not that it receives a
        /// truncated payload that looks complete.
        /// </summary>
        internal static JObject BuildOversizedResponse(string correlationId, long byteCount, long limitBytes)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = correlationId,
                ["error"] = new JObject
                {
                    ["code"] = "WorkerResponseTooLarge",
                    ["message"] = "This response is " + byteCount + " bytes, over the shared-transport limit of " + limitBytes
                        + " bytes, so it was refused rather than truncated. Other clients are unaffected.",
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
            };
        }

        private const string PipePrefix = "GxMcpShared_";

        private static readonly HashSet<string> HostFrameTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "attach", "attach_ack", "heartbeat", "heartbeat_ack", "detach", "busy", "host_error"
        };

        internal static bool TryParseEnvelope(string line, out SharedWorkerEnvelope envelope, out string error)
        {
            envelope = null;
            error = "empty host frame";
            if (string.IsNullOrWhiteSpace(line)) return false;
            if (Encoding.UTF8.GetByteCount(line) > MaxFrameBytes)
            {
                error = "host frame exceeds the maximum size";
                return false;
            }

            try
            {
                var frame = GxMcp.Common.JsonIngress.ParseObject(line);
                string type = frame.Value<string>("type") ?? frame.Value<string>("gxmcp") ?? string.Empty;
                if (!HostFrameTypes.Contains(type))
                {
                    error = "unknown host frame type";
                    return false;
                }

                envelope = FromJson(frame, type);
                if (envelope.ProtocolVersion != ProtocolVersion)
                {
                    error = "host protocol version mismatch";
                    envelope = null;
                    return false;
                }

                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = "malformed host frame: " + ex.Message;
                envelope = null;
                return false;
            }
        }

        internal static bool TryValidateAttach(SharedWorkerEnvelope envelope, string expectedIdentityKey, out string error)
        {
            error = string.Empty;
            if (envelope == null || !string.Equals(envelope.Type, "attach", StringComparison.Ordinal))
            {
                error = "expected attach frame";
                return false;
            }
            if (envelope.ProtocolVersion != ProtocolVersion)
            {
                error = "host protocol version mismatch";
                return false;
            }
            if (!IsIdentityKey(expectedIdentityKey) || !string.Equals(envelope.IdentityKey, expectedIdentityKey, StringComparison.Ordinal))
            {
                error = "shared Worker identity mismatch";
                return false;
            }
            if (envelope.GatewayPid <= 0 || envelope.GatewayStartTimeUtcTicks <= 0)
            {
                error = "Gateway process identity is missing";
                return false;
            }
            if (string.IsNullOrWhiteSpace(envelope.AttachNonce) || envelope.AttachNonce.Length < 16 || envelope.AttachNonce.Length > 128)
            {
                error = "attach nonce is missing or invalid";
                return false;
            }
            return true;
        }

        internal static bool TryValidateSessionEnvelope(SharedWorkerEnvelope envelope, bool attached, out string error)
        {
            error = string.Empty;
            if (envelope == null || !attached)
            {
                error = "connection is not attached";
                return false;
            }
            if (envelope.ProtocolVersion != ProtocolVersion)
            {
                error = "host protocol version mismatch";
                return false;
            }
            if (envelope.Type != "heartbeat" && envelope.Type != "detach")
            {
                error = "frame is not valid for an attached session";
                return false;
            }
            if (string.IsNullOrWhiteSpace(envelope.AttachmentId))
            {
                error = "attachment id is missing";
                return false;
            }
            return true;
        }

        internal static bool TryValidateSessionEnvelope(
            SharedWorkerEnvelope envelope,
            bool attached,
            string expectedIdentityKey,
            string expectedAttachmentId,
            out string error)
        {
            if (!TryValidateSessionEnvelope(envelope, attached, out error)) return false;
            if (!string.Equals(envelope.IdentityKey, expectedIdentityKey, StringComparison.Ordinal))
            {
                error = "shared Worker identity mismatch";
                return false;
            }
            if (!string.Equals(envelope.AttachmentId, expectedAttachmentId, StringComparison.Ordinal))
            {
                error = "attachment identity mismatch";
                return false;
            }
            return true;
        }

        internal static bool TryRewriteRequestForChild(
            JObject clientRequest,
            string attachmentId,
            long sequence,
            out JObject childRequest,
            out SharedWorkerRequestRoute route,
            out string error)
        {
            childRequest = null;
            route = null;
            error = string.Empty;
            if (clientRequest == null || !string.Equals(clientRequest.Value<string>("jsonrpc"), "2.0", StringComparison.Ordinal))
            {
                error = "request is not JSON-RPC 2.0";
                return false;
            }
            if (clientRequest["id"] == null || clientRequest["id"].Type == JTokenType.Null)
            {
                error = "shared Worker requests require a non-null id";
                return false;
            }
            if (string.IsNullOrWhiteSpace(attachmentId) || sequence <= 0)
            {
                error = "request route identity is invalid";
                return false;
            }

            string childId = "gxmcp-" + attachmentId + "-" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            var rewritten = (JObject)clientRequest.DeepClone();
            rewritten["id"] = childId;

            JToken clientProgress = JValue.CreateNull();
            string childProgress = string.Empty;
            var meta = rewritten["_meta"] as JObject ?? new JObject();
            // The broker is the authoritative owner of the child request.  The
            // scheduler uses this field to create distinct client buckets; without
            // it every shared-host command falls into the default bucket.
            meta["attachmentId"] = attachmentId;
            if (meta["progressToken"] != null && meta["progressToken"].Type != JTokenType.Null)
            {
                clientProgress = clientRequest["_meta"]?["progressToken"]?.DeepClone() ?? JValue.CreateNull();
                childProgress = "gxmcp-progress-" + attachmentId + "-" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
                meta["progressToken"] = childProgress;
            }
            rewritten["_meta"] = meta;

            route = new SharedWorkerRequestRoute
            {
                AttachmentId = attachmentId,
                ClientRequestId = clientRequest["id"].DeepClone(),
                ChildRequestId = childId,
                ClientProgressToken = clientProgress,
                ChildProgressToken = childProgress
            };
            childRequest = rewritten;
            return true;
        }

        internal static bool TryRestoreResponse(
            JObject childResponse,
            SharedWorkerRequestRoute route,
            out JObject restored,
            out string error)
        {
            restored = null;
            error = string.Empty;
            if (childResponse == null || route == null || childResponse["id"] == null)
            {
                error = "response route is incomplete";
                return false;
            }
            if (!string.Equals(childResponse["id"]?.ToString(), route.ChildRequestId, StringComparison.Ordinal))
            {
                error = "response id does not belong to the route";
                return false;
            }
            restored = (JObject)childResponse.DeepClone();
            restored["id"] = route.ClientRequestId.DeepClone();
            return true;
        }

        internal static bool TryRewriteCancellationForChild(
            JObject clientNotification,
            IEnumerable<SharedWorkerRequestRoute> routes,
            string attachmentId,
            out JObject childNotification,
            out string error)
        {
            childNotification = null;
            error = string.Empty;
            if (clientNotification == null
                || !string.Equals(clientNotification.Value<string>("jsonrpc"), "2.0", StringComparison.Ordinal)
                || !string.Equals(clientNotification.Value<string>("method"), "notifications/cancelled", StringComparison.Ordinal))
            {
                error = "request is not a cancellation notification";
                return false;
            }

            JToken clientRequestId = clientNotification["params"]?["requestId"];
            if (clientRequestId == null || clientRequestId.Type == JTokenType.Null || string.IsNullOrWhiteSpace(attachmentId))
            {
                error = "cancellation notification is missing its request id or attachment";
                return false;
            }

            foreach (SharedWorkerRequestRoute route in routes ?? new List<SharedWorkerRequestRoute>())
            {
                if (!string.Equals(route.AttachmentId, attachmentId, StringComparison.Ordinal)
                    || !JToken.DeepEquals(route.ClientRequestId, clientRequestId))
                    continue;

                childNotification = (JObject)clientNotification.DeepClone();
                ((JObject)childNotification["params"])["requestId"] = route.ChildRequestId;
                return true;
            }

            error = "cancellation request is not owned by this attachment";
            return false;
        }

        internal static bool TryRouteChildNotification(
            JObject childNotification,
            IDictionary<string, SharedWorkerRequestRoute> progressOwners,
            out string attachmentId,
            out JObject routed,
            out bool broadcast,
            out string error)
        {
            attachmentId = null;
            routed = null;
            broadcast = false;
            error = string.Empty;
            if (childNotification == null)
            {
                error = "notification is null";
                return false;
            }

            string method = childNotification.Value<string>("method") ?? string.Empty;
            if (IsExplicitBroadcastNotification(method))
            {
                routed = (JObject)childNotification.DeepClone();
                broadcast = true;
                return true;
            }
            if (!string.Equals(method, "notifications/progress", StringComparison.Ordinal))
            {
                error = "notification has no attachment routing rule";
                return false;
            }

            string childToken = childNotification["params"]?["progressToken"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(childToken) || progressOwners == null || !progressOwners.TryGetValue(childToken, out var route))
            {
                error = "progress token is not owned by an attached request";
                return false;
            }
            routed = (JObject)childNotification.DeepClone();
            routed["params"]["progressToken"] = route.ClientProgressToken.DeepClone();
            attachmentId = route.AttachmentId;
            return true;
        }

        private static bool IsExplicitBroadcastNotification(string method)
        {
            return string.Equals(method, "notifications/resources/updated", StringComparison.Ordinal)
                || string.Equals(method, "notifications/worker/sdk_ready", StringComparison.Ordinal)
                || string.Equals(method, "notifications/worker/restarting", StringComparison.Ordinal)
                || string.Equals(method, "notifications/worker/build_active", StringComparison.Ordinal)
                || string.Equals(method, "notifications/worker/index_active", StringComparison.Ordinal);
        }

        internal static bool TryCreateIdentityKey(
            string workerExecutable,
            string kbPath,
            string installationPath,
            string driver,
            string major,
            out string identityKey,
            out string error)
        {
            identityKey = string.Empty;
            error = string.Empty;
            string executable = NormalizePath(workerExecutable);
            string kb = NormalizePath(kbPath);
            string installation = NormalizePath(installationPath);
            string normalizedDriver = NormalizeToken(driver);
            string normalizedMajor = NormalizeToken(major);
            if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(kb) || string.IsNullOrWhiteSpace(installation)
                || string.IsNullOrWhiteSpace(normalizedDriver) || string.IsNullOrWhiteSpace(normalizedMajor))
            {
                error = "shared Worker identity requires executable, KB, installation, driver, and major";
                return false;
            }

            string material = string.Join("\n", executable, kb, installation, normalizedDriver, normalizedMajor);
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (byte value in bytes) builder.Append(value.ToString("x2"));
                identityKey = builder.ToString();
            }
            return true;
        }

        internal static bool TryBuildPipeName(string identityKey, out string pipeName, out string error)
        {
            pipeName = string.Empty;
            error = string.Empty;
            if (!IsIdentityKey(identityKey))
            {
                error = "identity key must be a lowercase SHA-256 hex value";
                return false;
            }
            pipeName = PipePrefix + identityKey;
            return true;
        }

        internal static bool IsValidPipeName(string pipeName)
        {
            if (string.IsNullOrWhiteSpace(pipeName) || !pipeName.StartsWith(PipePrefix, StringComparison.Ordinal)) return false;
            return IsIdentityKey(pipeName.Substring(PipePrefix.Length));
        }

        internal static bool IsIdentityKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!hex) return false;
            }
            return true;
        }

        private static SharedWorkerEnvelope FromJson(JObject frame, string type)
        {
            return new SharedWorkerEnvelope
            {
                Type = type,
                ProtocolVersion = frame.Value<int?>("protocolVersion") ?? frame.Value<int?>("version") ?? 0,
                IdentityKey = frame.Value<string>("identityKey") ?? frame.Value<string>("key") ?? string.Empty,
                GatewayPid = frame.Value<int?>("gatewayPid") ?? 0,
                GatewayStartTimeUtcTicks = frame.Value<long?>("gatewayStartTimeUtcTicks") ?? 0,
                AttachNonce = frame.Value<string>("attachNonce") ?? frame.Value<string>("nonce") ?? string.Empty,
                AttachmentId = frame.Value<string>("attachmentId") ?? frame.Value<string>("attachId") ?? string.Empty,
                Message = frame.Value<string>("message") ?? string.Empty
            };
        }

        private static string NormalizePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            try
            {
                return Path.GetFullPath(value)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .ToLowerInvariant();
            }
            catch
            {
                return value.Trim().TrimEnd('\\', '/').ToLowerInvariant();
            }
        }

        private static string NormalizeToken(string value) => (value ?? string.Empty).Trim().ToLowerInvariant();
    }
}
