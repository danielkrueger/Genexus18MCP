using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Issue #357 — the opaque token behind <c>genexus_read ifUnchangedSince</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A token answers one question and one question only: <em>would this exact
    /// representation, served right now, be byte-identical to the one the caller
    /// already holds?</em> Answering that cheaply is the whole point — an agent
    /// re-reading a 256 KiB generated part to discover nothing changed pays the
    /// transport bytes and the context window for no information.
    /// </para>
    /// <para>
    /// The token therefore binds two independent things:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>Binding</b> — which representation is being claimed: physical KB,
    /// model/environment version, Worker instance, object GUID, object type,
    /// resolved part, and the pagination window. A mismatch on any of those means
    /// the token describes a <em>different</em> representation, so it can never be
    /// reused for this request.
    /// </description></item>
    /// <item><description>
    /// <b>Evidence</b> — the object revision stamp observed <em>before</em> the
    /// body was produced, plus the fingerprint of the body that was actually
    /// served. The stamp is what a later read re-derives cheaply; the fingerprint
    /// pins the bytes without trusting a name.
    /// </description></item>
    /// </list>
    /// <para>
    /// Evidence is deliberately <b>not</b> a TTL and deliberately <b>not</b> a
    /// cached hash. Both keep answering "unchanged" long after an external IDE
    /// edit. The stamp is the same signal <c>genexus_edit</c>'s <c>baseVersion</c>
    /// check already refuses to write without, which is precisely the trust
    /// boundary this project already documents: the SDK keeps
    /// <c>LastUpdate</c> current for externally-modified objects. When no stamp
    /// can be derived the answer is <see cref="ReasonRevisionUnknown"/> — a full
    /// read — never <see cref="ReasonMatched"/>.
    /// </para>
    /// <para>
    /// Encoding follows the existing cursor convention in this codebase
    /// (base64url of a compact JSON state, see
    /// <c>SourceSearchService.BuildResumeCursor</c>): debuggable in a log, not
    /// tempting to parse. The client contract is opacity; these fields exist so a
    /// stale token can explain <em>why</em> it went stale instead of silently
    /// degrading to a full read.
    /// </para>
    /// </remarks>
    internal static class ConditionalReadToken
    {
        /// <summary>Format version prefix. Bump when the binding changes meaning.</summary>
        internal const string Prefix = "grc1.";

        /// <summary>
        /// A token is ~300 bytes; anything longer is a caller error or an attempt
        /// to smuggle a payload through a field that is documented as opaque.
        /// </summary>
        internal const int MaxTokenLength = 2048;

        internal const string ReasonMatched = "matched";
        internal const string ReasonAbsent = "absent";
        internal const string ReasonMalformed = "tokenMalformed";
        internal const string ReasonKbChanged = "kbChanged";
        internal const string ReasonModelChanged = "modelChanged";
        internal const string ReasonWorkerRestarted = "workerRestarted";
        internal const string ReasonObjectReplaced = "objectReplaced";
        internal const string ReasonObjectTypeChanged = "objectTypeChanged";
        internal const string ReasonPartChanged = "partChanged";
        internal const string ReasonWindowChanged = "paginationChanged";
        internal const string ReasonRevisionAdvanced = "revisionAdvanced";
        internal const string ReasonRevisionUnknown = "revisionUnknown";
        internal const string ReasonAuthoritativeRequired = "authoritativeReadRequired";

        /// <summary>Unspecified pagination, matching <c>BuildReadCacheKey</c>'s -1 sentinel.</summary>
        internal const int Unspecified = -1;

        /// <summary>
        /// Everything a token asserts. <see cref="BodyFingerprint"/> and
        /// <see cref="VersionToken"/> are informational; only the fields compared
        /// by <see cref="Compare"/> gate a suppression.
        /// </summary>
        internal sealed class State
        {
            internal string KbFingerprint { get; set; }
            internal string WorkerInstance { get; set; }
            internal string ModelVersion { get; set; }
            internal string ObjectGuid { get; set; }
            internal string ObjectType { get; set; }
            internal string Part { get; set; }
            internal int Offset { get; set; } = Unspecified;
            internal int Limit { get; set; } = Unspecified;
            /// <summary>Object revision stamp observed BEFORE the body was read.</summary>
            internal string Revision { get; set; }
            /// <summary>SHA-256 of the exact <c>source</c> string that was served.</summary>
            internal string BodyFingerprint { get; set; }
            /// <summary>The <c>versionToken</c> issued alongside that body.</summary>
            internal string VersionToken { get; set; }
            internal int TotalLines { get; set; } = Unspecified;
            internal long TotalBytes { get; set; } = Unspecified;
            internal bool Truncated { get; set; }

            /// <summary>True when the binding fields needed to gate a suppression are present.</summary>
            internal bool IsBindable =>
                !string.IsNullOrEmpty(KbFingerprint)
                && !string.IsNullOrEmpty(WorkerInstance)
                && !string.IsNullOrEmpty(ObjectGuid)
                && !string.IsNullOrEmpty(Part)
                && !string.IsNullOrEmpty(Revision)
                && !string.IsNullOrEmpty(BodyFingerprint);
        }

        /// <summary>
        /// Normalizes a physical KB location to the same shape
        /// <c>SourceSearchService.GetKbIdentity</c> hashes, so both cursors and
        /// read tokens agree on what "the same KB" means. A <c>.gxw</c> points at
        /// its containing directory.
        /// </summary>
        internal static string NormalizeKbPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                if (path.EndsWith(".gxw", StringComparison.OrdinalIgnoreCase))
                    path = Path.GetDirectoryName(path);
                return Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/')
                    .ToUpperInvariant();
            }
            catch
            {
                return (path ?? string.Empty).Replace('\\', '/').TrimEnd('/', '\\').ToUpperInvariant();
            }
        }

        internal static string FingerprintKbPath(string path)
        {
            string normalized = NormalizeKbPath(path);
            return string.IsNullOrEmpty(normalized) ? null : SdkReflection.Sha256Hex(normalized);
        }

        internal static string NormalizePart(string partName) =>
            string.IsNullOrWhiteSpace(partName) ? "source" : partName.Trim().ToLowerInvariant();

        internal static int NormalizePage(int? value) => value.HasValue ? value.Value : Unspecified;

        /// <summary>
        /// Fingerprint of the exact text served. Centralised so issuance and its
        /// tests cannot disagree about what "the bytes" means.
        /// </summary>
        internal static string BodyFingerprint(string servedContent) =>
            SdkReflection.Sha256Hex(servedContent ?? string.Empty);

        /// <summary>
        /// Serializes a state to the opaque token, or returns <c>null</c> when the
        /// state cannot support a suppression decision. Failing to issue is always
        /// the safe direction: the caller then re-reads unconditionally.
        /// </summary>
        internal static string Issue(State state)
        {
            if (state == null || !state.IsBindable) return null;

            var json = new JObject
            {
                ["kb"] = state.KbFingerprint,
                ["wk"] = state.WorkerInstance,
                ["mdo"] = state.ModelVersion ?? string.Empty,
                ["g"] = state.ObjectGuid,
                ["t"] = state.ObjectType ?? string.Empty,
                ["p"] = state.Part,
                ["o"] = state.Offset,
                ["l"] = state.Limit,
                ["rev"] = state.Revision,
                ["src"] = state.BodyFingerprint,
                ["vt"] = state.VersionToken ?? string.Empty,
                ["ln"] = state.TotalLines,
                ["by"] = state.TotalBytes,
                ["tr"] = state.Truncated
            };
            return Prefix + ToBase64Url(Encoding.UTF8.GetBytes(json.ToString(Newtonsoft.Json.Formatting.None)));
        }

        /// <summary>
        /// Decodes a token. Returns <c>false</c> with <paramref name="reason"/>
        /// set to <see cref="ReasonMalformed"/> for anything this version cannot
        /// read — callers must then read unconditionally, never suppress.
        /// </summary>
        internal static bool TryParse(string token, out State state, out string reason)
        {
            state = null;
            reason = ReasonMalformed;
            if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength) return false;
            string trimmed = token.Trim();
            if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal)) return false;

            byte[] bytes;
            try { bytes = FromBase64Url(trimmed.Substring(Prefix.Length)); }
            catch { return false; }

            try
            {
                var json = JObject.Parse(Encoding.UTF8.GetString(bytes));
                state = new State
                {
                    KbFingerprint = json["kb"]?.ToString(),
                    WorkerInstance = json["wk"]?.ToString(),
                    ModelVersion = json["mdo"]?.Type == JTokenType.Null ? null : json["mdo"]?.ToString(),
                    ObjectGuid = json["g"]?.ToString(),
                    ObjectType = json["t"]?.ToString(),
                    Part = json["p"]?.ToString(),
                    Offset = json["o"]?.ToObject<int?>() ?? Unspecified,
                    Limit = json["l"]?.ToObject<int?>() ?? Unspecified,
                    Revision = json["rev"]?.ToString(),
                    BodyFingerprint = json["src"]?.ToString(),
                    VersionToken = json["vt"]?.Type == JTokenType.Null ? null : json["vt"]?.ToString(),
                    TotalLines = json["ln"]?.ToObject<int?>() ?? Unspecified,
                    TotalBytes = json["by"]?.ToObject<long?>() ?? Unspecified,
                    Truncated = json["tr"]?.ToObject<bool?>() ?? false
                };
            }
            catch
            {
                state = null;
                return false;
            }

            if (!state.IsBindable)
            {
                state = null;
                return false;
            }
            reason = ReasonMatched;
            return true;
        }

        /// <summary>
        /// Decides whether the token still describes the representation the caller
        /// is asking for now. <paramref name="current"/> carries the binding and
        /// the freshly observed revision; its <see cref="State.BodyFingerprint"/>
        /// is ignored because the body has not been read at this point — that is
        /// the entire reason the revision stamp exists.
        /// </summary>
        /// <returns>A reason code; <see cref="ReasonMatched"/> means "suppress the body".</returns>
        internal static string Compare(State token, State current)
        {
            if (token == null || current == null) return ReasonMalformed;
            if (!string.Equals(token.KbFingerprint, current.KbFingerprint, StringComparison.Ordinal))
                return ReasonKbChanged;
            if (!string.Equals(token.ModelVersion ?? string.Empty, current.ModelVersion ?? string.Empty, StringComparison.Ordinal))
                return ReasonModelChanged;
            if (!string.Equals(token.WorkerInstance, current.WorkerInstance, StringComparison.Ordinal))
                return ReasonWorkerRestarted;
            if (!string.Equals(token.ObjectGuid, current.ObjectGuid, StringComparison.Ordinal))
                return ReasonObjectReplaced;
            if (!string.Equals(token.ObjectType ?? string.Empty, current.ObjectType ?? string.Empty, StringComparison.Ordinal))
                return ReasonObjectTypeChanged;
            if (!string.Equals(token.Part, current.Part, StringComparison.Ordinal))
                return ReasonPartChanged;
            if (token.Offset != current.Offset || token.Limit != current.Limit)
                return ReasonWindowChanged;

            // Freshness, not binding: checked last so a caller gets the binding
            // mismatch that actually explains the stale token. An absent stamp is
            // never "unchanged" — we cannot prove it, so we read.
            if (string.IsNullOrEmpty(current.Revision)) return ReasonRevisionUnknown;
            if (!string.Equals(token.Revision, current.Revision, StringComparison.Ordinal))
                return ReasonRevisionAdvanced;
            return ReasonMatched;
        }

        private static string ToBase64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] FromBase64Url(string value)
        {
            string padded = value.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
                case 1: throw new FormatException("base64url length");
            }
            return Convert.FromBase64String(padded);
        }

        internal static string DescribeReason(string reason) => reason switch
        {
            ReasonMatched => "The representation bound to this token is unchanged; the body was omitted.",
            ReasonAbsent => "No conditional token was supplied; the body was returned unconditionally.",
            ReasonMalformed => "The supplied token could not be read; the body was returned unconditionally.",
            ReasonKbChanged => "The token belongs to a different physical KB; the body was returned.",
            ReasonModelChanged => "The token belongs to a different model/environment version; the body was returned.",
            ReasonWorkerRestarted => "The token was issued by a Worker instance that is no longer current; the body was returned.",
            ReasonObjectReplaced => "The token belongs to a different persisted object (GUID), such as a recreated same-named object; the body was returned.",
            ReasonObjectTypeChanged => "The token belongs to a different object type; the body was returned.",
            ReasonPartChanged => "The token belongs to a different part; the body was returned.",
            ReasonWindowChanged => "The token belongs to a different pagination window (offset/limit); the body was returned.",
            ReasonRevisionAdvanced => "The object's persisted revision advanced since the token was issued; the body was returned.",
            ReasonRevisionUnknown => "A fresh revision stamp could not be established, so unchanged content cannot be proven; the body was returned.",
            ReasonAuthoritativeRequired => "An outstanding post-write recovery requirement forced an authoritative read; the body was returned.",
            _ => "The body was returned unconditionally."
        };
    }
}