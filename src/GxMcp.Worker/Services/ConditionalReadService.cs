using System;
using System.Globalization;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using KBObject = global::Artech.Architecture.Common.Objects.KBObject;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Issue #357 — the conditional half of a single-part <c>genexus_read</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An agent that already holds an object part can send
    /// <c>ifUnchangedSince: "&lt;contentToken&gt;"</c> and, when the
    /// representation is provably identical, receive identity and freshness
    /// metadata with no body at all. This is a transport- and context-saving
    /// affordance layered on the existing read path; it never changes what a
    /// read returns when the content did move.
    /// </para>
    /// <para>
    /// The safety rule this class exists to enforce: <b>a body is omitted only
    /// when unchanged can be proven</b>. Every other outcome — token unreadable,
    /// token bound to a different KB/model/Worker/object/part/window, revision
    /// unavailable, or an authoritative read forced by an outstanding write
    /// fence — returns the complete body plus a machine-readable
    /// <c>conditional.reason</c>. There is no "probably unchanged" branch.
    /// </para>
    /// <para>
    /// The object is resolved by <see cref="ObjectService.ReadObjectSource"/>
    /// and handed in already-resolved on purpose. Resolving it a second time
    /// here would let a concurrent rename resolve one object for the freshness
    /// observation and a different one for the body, which would suppress a real
    /// change on the next read.
    /// </para>
    /// </remarks>
    public sealed class ConditionalReadService
    {
        /// <summary>
        /// Identifies this Worker process for the life of the process. A reload or
        /// a respawn mints a new id, which retires every outstanding token: the
        /// next read returns a full body instead of trusting an observation made
        /// against a Worker that no longer exists.
        /// </summary>
        private static readonly string WorkerInstance = Guid.NewGuid().ToString("N");

        private readonly KbService _kb;
        private readonly ObjectService _objects;

        internal ConditionalReadService(KbService kb, ObjectService objects)
        {
            _kb = kb;
            _objects = objects;
        }

        /// <summary>
        /// What the caller asked for: the token it holds and whether the Gateway
        /// forced an authoritative read because an earlier write on this target
        /// still has an unresolved outcome.
        /// </summary>
        public sealed class Request
        {
            internal string Token { get; set; }
            internal bool RequireAuthoritativeRead { get; set; }
        }

        /// <summary>
        /// Issues a token for a representation that was just produced. Called on
        /// every conditional-capable read so the very first read gives the caller
        /// something to echo; a plain read is unaffected.
        /// <para>
        /// <paramref name="observed"/> is the state captured <em>before</em> the
        /// body was read. It is threaded through rather than rebuilt here on
        /// purpose: re-deriving the revision after the read would bind the token
        /// to a newer revision than the bytes it describes, and the next
        /// conditional read would then suppress a change that really happened.
        /// </para>
        /// </summary>
        internal string Decorate(
            string payloadJson, ConditionalReadToken.State observed, string reason)
        {
            if (string.IsNullOrWhiteSpace(payloadJson)) return payloadJson;
            JObject payload;
            try { payload = JObject.Parse(payloadJson); }
            catch { return payloadJson; }
            if (payload["error"] != null) return payloadJson;

            string served = payload["source"]?.Type == JTokenType.String
                ? payload["source"].ToString()
                : null;
            if (served == null) return payloadJson;

            var issued = observed;
            issued.BodyFingerprint = ConditionalReadToken.BodyFingerprint(served);
            issued.VersionToken = payload["versionToken"]?.ToString();
            issued.TotalLines = payload["totalLines"]?.ToObject<int?>() ?? ConditionalReadToken.Unspecified;
            issued.TotalBytes = payload["totalBytes"]?.ToObject<long?>() ?? ConditionalReadToken.Unspecified;
            issued.Truncated = payload["truncated"]?.ToObject<bool?>() ?? false;

            string token = ConditionalReadToken.Issue(issued);
            if (token != null) payload["contentToken"] = token;
            payload["conditional"] = BuildConditionalBlock(reason, issued);
            // Default formatting on purpose: it matches what
            // ReadObjectSourceInternal and MemoryService.AttachRelevantMemory
            // already emit on this path, so adding a token does not silently
            // re-shape the envelope a caller was already parsing.
            return payload.ToString();
        }

        /// <summary>
        /// The suppressed response: no body, but enough to keep working — identity,
        /// the still-current <c>versionToken</c> for a subsequent write, and the
        /// pagination shape of what was withheld.
        /// </summary>
        internal string BuildNotModified(KBObject obj, ConditionalReadToken.State tokenState)
        {
            return new JObject
            {
                ["notModified"] = true,
                ["part"] = tokenState.Part,
                ["identity"] = new JObject
                {
                    ["guid"] = tokenState.ObjectGuid,
                    ["type"] = tokenState.ObjectType,
                    ["name"] = SafeName(obj)
                },
                // The revision that gated the suppression has not advanced, so the
                // versionToken issued with that body is still the object's current
                // one and stays usable as a write's baseVersion.
                ["versionToken"] = tokenState.VersionToken,
                ["contentToken"] = ConditionalReadToken.Issue(tokenState),
                ["offset"] = OptionalInt(tokenState.Offset),
                ["limit"] = OptionalInt(tokenState.Limit),
                ["totalLines"] = OptionalInt(tokenState.TotalLines),
                ["totalBytes"] = OptionalLong(tokenState.TotalBytes),
                ["truncated"] = tokenState.Truncated,
                ["message"] = "Body omitted: the conditional token proves this exact representation is unchanged.",
                ["conditional"] = BuildConditionalBlock(ConditionalReadToken.ReasonMatched, tokenState)
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Builds the binding for the current request. <paramref name="revision"/>
        /// must be observed <em>before</em> the body is produced: binding a token
        /// to a newer revision than the bytes it describes would suppress a real
        /// change on the next read.
        /// </summary>
        internal ConditionalReadToken.State BuildState(
            KBObject obj, string resolvedPart, int? offset, int? limit, string revision)
        {
            return new ConditionalReadToken.State
            {
                KbFingerprint = ConditionalReadToken.FingerprintKbPath(SafeKbPath()),
                WorkerInstance = WorkerInstance,
                ModelVersion = SafeModelVersion(),
                ObjectGuid = SafeGuid(obj),
                ObjectType = SafeTypeName(obj),
                Part = ConditionalReadToken.NormalizePart(resolvedPart),
                Offset = ConditionalReadToken.NormalizePage(offset),
                Limit = ConditionalReadToken.NormalizePage(limit),
                Revision = revision
            };
        }

        private static JObject BuildConditionalBlock(string reason, ConditionalReadToken.State state)
        {
            bool matched = string.Equals(reason, ConditionalReadToken.ReasonMatched, StringComparison.Ordinal);
            return new JObject
            {
                ["status"] = matched ? "notModified" : "bodyReturned",
                ["reason"] = reason,
                ["explanation"] = ConditionalReadToken.DescribeReason(reason),
                ["checkedAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["freshness"] = new JObject
                {
                    // Named so a reader can tell a revision-stamp decision from a
                    // TTL or a cache-hit heuristic. Both of those keep claiming
                    // "unchanged" after an external IDE edit; this one does not.
                    ["source"] = "objectRevisionStamp",
                    ["revision"] = state?.Revision,
                    ["workerInstance"] = state?.WorkerInstance,
                    ["kbFingerprint"] = state?.KbFingerprint,
                    ["modelVersion"] = state?.ModelVersion
                },
                ["omittedBodyBytes"] = matched ? state?.TotalBytes : null,
                ["contentFingerprint"] = state?.BodyFingerprint
            };
        }

        // Every SDK touch below is individually catchable on purpose: a
        // conditional read is an optimization, so losing an identity field must
        // cost the caller a full read, never an exception.
        private string SafeKbPath()
        {
            try { return _kb?.GetKbPath(); }
            catch { return null; }
        }

        private string SafeModelVersion()
        {
            try
            {
                string version = _kb?.GetActiveEnvironmentVersion();
                return string.IsNullOrEmpty(version) ? null : version;
            }
            catch { return null; }
        }

        private static string SafeGuid(KBObject obj)
        {
            try { return obj?.Guid.ToString("N"); }
            catch { return null; }
        }

        private static string SafeTypeName(KBObject obj)
        {
            try { return obj?.TypeDescriptor?.Name ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string SafeName(KBObject obj)
        {
            try { return obj?.Name; }
            catch { return null; }
        }

        private static JToken OptionalInt(int value) =>
            value == ConditionalReadToken.Unspecified ? JValue.CreateNull() : new JValue(value);

        private static JToken OptionalLong(long value) =>
            value == ConditionalReadToken.Unspecified ? JValue.CreateNull() : new JValue(value);
    }
}