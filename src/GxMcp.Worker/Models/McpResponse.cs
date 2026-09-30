using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Models
{
    /// <summary>
    /// Canonical MCP response envelope (v2.8.0+).
    ///
    /// Wire shape:
    /// <code>
    /// {
    ///   "status":      "ok" | "error" | "partial" | "accepted",
    ///   "code":        "MachineReadableId",     // optional on ok
    ///   "target":      "&lt;object name&gt;",   // optional
    ///   "result":      { ...payload... },       // status in (ok | partial)
    ///   "error": {                              // status == error
    ///     "code":      "StableErrorCode",
    ///     "message":   "Short human sentence.",
    ///     "hint":      "One-line plain-English fix.",
    ///     "nextSteps": [{"tool": "...", "args": {...}, "why": "..."}]
    ///   },
    ///   "operationId": "...",                   // status == accepted
    ///   "pollTarget":  "..."                    // status == accepted
    /// }
    /// </code>
    ///
    /// Use <see cref="Ok"/>, <see cref="Err"/>, <see cref="Partial"/>, <see cref="Accepted"/>.
    /// The legacy <see cref="Success"/> and <see cref="Error(string, string)"/>
    /// methods remain for not-yet-migrated services; they will be removed in
    /// v2.8.0 once the whole worker has switched over.
    /// </summary>
    public class McpResponse
    {
        // ── Canonical helpers (v2.8.0) ──────────────────────────────────

        public static string Ok(
            string target = null,
            string code = null,
            JObject result = null)
        {
            var resp = new JObject { ["status"] = "ok" };
            if (!string.IsNullOrWhiteSpace(code)) resp["code"] = code;
            if (!string.IsNullOrWhiteSpace(target)) resp["target"] = target;
            if (result != null) resp["result"] = result;
            return resp.ToString();
        }

        public static string Partial(
            string target,
            string code,
            JObject result,
            JArray warnings = null)
        {
            var resp = new JObject { ["status"] = "partial" };
            if (!string.IsNullOrWhiteSpace(code)) resp["code"] = code;
            if (!string.IsNullOrWhiteSpace(target)) resp["target"] = target;
            if (result != null) resp["result"] = result;
            if (warnings != null && warnings.Count > 0) resp["warnings"] = warnings;
            return resp.ToString();
        }

        /// <summary>
        /// A refusal envelope.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two of the parameters add caller-supplied fields to the envelope, and they
        /// put them in <em>different places</em>. This is worth stating at the signature
        /// because nothing about the call site tells you which one you are using, and
        /// reading the wrong place for a field yields <c>null</c> with no explanation:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// <c>error</c> always carries <c>code</c>, <c>message</c>, <c>hint</c> and
        /// <c>nextSteps</c>.
        /// </description></item>
        /// <item><description>
        /// <paramref name="extra"/> lands at the <strong>envelope's top level</strong>,
        /// beside <c>status</c> and <c>target</c> - not inside <c>error</c>. So the
        /// retry contract of a stale-object refusal is read as
        /// <c>response["expectedVersion"]</c>, not
        /// <c>response["error"]["expectedVersion"]</c>.
        /// </description></item>
        /// <item><description>
        /// <c>errorExtra</c> lands <strong>inside <c>error</c></strong>, beside the
        /// code and message.
        /// </description></item>
        /// </list>
        ///
        /// <para>
        /// Passing both is legitimate and done deliberately: a refusal that reports
        /// diagnostics about the failure (<c>errorExtra</c>) and the operational state
        /// it left behind (<c>extra</c>) needs both places, and
        /// <c>genexus_module</c>'s install failure is the precedent.
        /// </para>
        ///
        /// <para>
        /// The split is a wart, not a design. It means two operations refusing the same
        /// way can emit different JSON shapes for the same error code, which a client
        /// cannot read uniformly - currently true of <c>WwpSnapshotRequired</c>, where
        /// one site uses each parameter for the same fields. Unifying them changes what
        /// published clients already read, so it is a contract decision rather than a
        /// refactor, and until it is taken the asymmetry is pinned by tests instead.
        /// </para>
        /// </remarks>
        public static string Err(
            string code,
            string message,
            string hint = null,
            JArray nextSteps = null,
            string target = null,
            JObject extra = null,
            int? retryAfterMs = null,
            JObject errorExtra = null,
            bool? retryable = null,
            bool? reconciliationRequired = null)
        {
            string enMsg = GxMcp.Worker.Helpers.ErrorMessages.Translate(message);
            string enHint = GxMcp.Worker.Helpers.ErrorMessages.Translate(hint);
            var err = new JObject
            {
                ["code"] = code ?? "Unknown",
                ["message"] = enMsg
            };
            if (!string.IsNullOrWhiteSpace(enHint)) err["hint"] = enHint;
            if (nextSteps != null && nextSteps.Count > 0) err["nextSteps"] = nextSteps;
            // v2.8.0 — transient error codes carry a retry hint so a weakly-
            // capable LLM stops hammering in a tight loop and waits the
            // recommended interval. Caller passes ms; never negative.
            if (retryAfterMs.HasValue && retryAfterMs.Value > 0) err["retryAfterMs"] = retryAfterMs.Value;
            if (retryable.HasValue) err["retryable"] = retryable.Value;
            if (reconciliationRequired.HasValue) err["reconciliationRequired"] = reconciliationRequired.Value;
            // v2.8.0 — additional error-specific structured fields. Merged
            // into the `error` sub-object so error-related context lives
            // alongside code/message/hint/nextSteps rather than at the
            // envelope top level (which is what `extra:` does).
            if (errorExtra != null)
            {
                foreach (var prop in errorExtra.Properties())
                {
                    if (err[prop.Name] == null) err[prop.Name] = prop.Value;
                }
            }

            var resp = new JObject { ["status"] = "error" };
            if (!string.IsNullOrWhiteSpace(target)) resp["target"] = target;
            resp["error"] = err;

            if (extra != null)
            {
                foreach (var prop in extra.Properties())
                {
                    if (resp[prop.Name] == null) resp[prop.Name] = prop.Value;
                }
            }

            // Preserve untranslated source for support tooling.
            bool msgChanged = !string.Equals(enMsg, message, StringComparison.Ordinal);
            bool hintChanged = !string.IsNullOrEmpty(hint)
                && !string.Equals(enHint, hint, StringComparison.Ordinal);
            if (msgChanged || hintChanged)
            {
                var meta = new JObject();
                if (msgChanged) meta["sourceMessage"] = message;
                if (hintChanged) meta["sourceHint"] = hint;
                resp["_meta"] = meta;
            }
            return resp.ToString();
        }

        public static string Accepted(
            string target,
            string operationId,
            string pollTarget = null,
            JObject extra = null,
            JObject cancelTool = null,
            JObject pollTool = null)
        {
            var resp = new JObject { ["status"] = "accepted" };
            if (!string.IsNullOrWhiteSpace(target)) resp["target"] = target;
            if (!string.IsNullOrWhiteSpace(operationId)) resp["operationId"] = operationId;
            if (!string.IsNullOrWhiteSpace(pollTarget)) resp["pollTarget"] = pollTarget;
            // v2.8.0 — inline ready-to-call shortcuts so a weakly-capable LLM
            // doesn't have to know genexus_lifecycle internals to follow up
            // on an async accept. Default polling shortcut is auto-derived
            // when caller doesn't supply one but operationId is set.
            if (cancelTool == null && !string.IsNullOrWhiteSpace(operationId))
            {
                cancelTool = new JObject
                {
                    ["tool"] = "genexus_lifecycle",
                    ["args"] = new JObject
                    {
                        ["action"] = "cancel",
                        ["target"] = pollTarget ?? operationId
                    }
                };
            }
            if (pollTool == null && !string.IsNullOrWhiteSpace(operationId))
            {
                pollTool = new JObject
                {
                    ["tool"] = "genexus_lifecycle",
                    ["args"] = new JObject
                    {
                        ["action"] = "status",
                        ["target"] = pollTarget ?? operationId
                    }
                };
            }
            if (cancelTool != null) resp["cancelTool"] = cancelTool;
            if (pollTool != null) resp["pollTool"] = pollTool;
            if (extra != null)
            {
                foreach (var prop in extra.Properties())
                {
                    if (resp[prop.Name] == null) resp[prop.Name] = prop.Value;
                }
            }
            return resp.ToString();
        }

        /// <summary>
        /// Convenience: build a nextSteps[] entry for the canonical error envelope.
        /// </summary>
        public static JObject NextStep(string tool, JObject args = null, string why = null)
        {
            var step = new JObject { ["tool"] = tool };
            if (args != null) step["args"] = args;
            if (!string.IsNullOrWhiteSpace(why)) step["why"] = why;
            return step;
        }

        // Legacy McpResponse.Success / McpResponse.Error helpers removed in v2.8.0.
        // All emissions go through Ok / Err / Partial / Accepted above. See docs/envelope.md.
    }
}
