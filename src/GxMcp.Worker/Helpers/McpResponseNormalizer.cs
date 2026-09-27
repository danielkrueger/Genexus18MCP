using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Converts legacy service responses into the canonical MCP envelope at the
    /// dispatcher boundary. Domain payloads are not inspected, so an
    /// error-shaped field nested inside a successful result remains untouched.
    /// </summary>
    internal static class McpResponseNormalizer
    {
        private static readonly HashSet<string> LegacyErrorStatuses =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Error", "NotFound", "NotImplemented", "WorkerBusy", "Busy",
                "IndexNotReady", "Reindexing", "IndexCold", "Timeout", "Cancelled"
            };

        internal static string Normalize(
            string json,
            string fallbackCode = "LegacyWorkerError",
            string fallbackHint = "Inspect the worker diagnostics and retry after correcting the reported condition.")
        {
            JToken token = NormalizeToken(json, out bool rewritten, fallbackCode, fallbackHint);
            // Unchanged contract: a payload that needed no rewrite is returned verbatim, so
            // callers that compare or store the string (idempotency replay) see the same bytes.
            return rewritten ? token.ToString(Newtonsoft.Json.Formatting.None) : json;
        }

        // Parses the payload ONCE and hands the token back. The dispatcher used to return a
        // string that SendResponse immediately re-parsed with JsonIngress.ParseToken, so every
        // response paid two full JObject.Parse passes over identical text — expensive on a
        // multi-megabyte genexus_read envelope and pure overhead on every other call.
        internal static JToken NormalizeToken(
            string json,
            out bool rewritten,
            string fallbackCode = "LegacyWorkerError",
            string fallbackHint = "Inspect the worker diagnostics and retry after correcting the reported condition.")
        {
            rewritten = false;
            if (string.IsNullOrWhiteSpace(json))
            {
                rewritten = true;
                return JObject.Parse(McpResponse.Err(fallbackCode, "Worker returned an empty response.", fallbackHint));
            }

            JObject payload;
            try
            {
                payload = JObject.Parse(json);
            }
            catch
            {
                rewritten = true;
                return JObject.Parse(McpResponse.Err(fallbackCode, "Worker returned invalid JSON.", fallbackHint));
            }

            string status = payload["status"]?.ToString();
            JToken errorToken = payload["error"];
            bool hasTopLevelError = errorToken != null && errorToken.Type != JTokenType.Null;
            bool legacyStatus = !string.IsNullOrWhiteSpace(status) && LegacyErrorStatuses.Contains(status);
            if (!hasTopLevelError && !legacyStatus) return payload;

            JObject error = errorToken as JObject;
            if (error == null) error = new JObject();

            string message = error["message"]?.ToString();
            if (string.IsNullOrWhiteSpace(message) && errorToken != null && errorToken.Type != JTokenType.Object)
                message = errorToken.ToString();
            if (string.IsNullOrWhiteSpace(message)) message = payload["message"]?.ToString();
            if (string.IsNullOrWhiteSpace(message)) message = "Worker operation failed.";

            string code = error["code"]?.ToString();
            if (string.IsNullOrWhiteSpace(code)) code = payload["code"]?.ToString();
            if (string.IsNullOrWhiteSpace(code))
            {
                code = legacyStatus && !string.Equals(status, "Error", StringComparison.OrdinalIgnoreCase)
                    ? "Legacy" + status
                    : fallbackCode;
            }

            string hint = error["hint"]?.ToString();
            if (string.IsNullOrWhiteSpace(hint)) hint = payload["hint"]?.ToString();
            if (string.IsNullOrWhiteSpace(hint)) hint = fallbackHint;

            var normalizedError = new JObject
            {
                ["code"] = code,
                ["message"] = message,
                ["hint"] = hint
            };
            foreach (JProperty property in error.Properties())
            {
                if (normalizedError[property.Name] == null)
                    normalizedError[property.Name] = property.Value;
            }
            if (legacyStatus && !string.Equals(status, "error", StringComparison.Ordinal))
                normalizedError["legacyStatus"] = status;

            payload["status"] = "error";
            payload["error"] = normalizedError;
            rewritten = true;
            return payload;
        }
    }
}
