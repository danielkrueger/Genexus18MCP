using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// v2.8.0 (#24) — long-running tools push canonical streaming progress
    /// via MCP `notifications/progress`. The MCP spec shape carries
    /// `{progressToken, progress, total, message}`; we additionally include
    /// `stage` (a free-form short label like "indexing", "compiling",
    /// "projecting") and `elapsedMs` so clients can render a multi-stage
    /// progress bar without parsing the message string.
    ///
    /// Calls to <see cref="Emit"/> are no-ops when no progressToken is set
    /// (synchronous tool calls that didn't opt-in to progress). Calls to
    /// <see cref="EmitStage"/> are similarly token-gated.
    /// </summary>
    public static class ProgressEmitter
    {
        // Per-operation start-time tracking so EmitStage can compute
        // elapsedMs without the caller having to thread a Stopwatch.
        private static readonly Dictionary<string, DateTime> _opStarts =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly object _opStartsLock = new object();

        public static void Emit(int progress, int total, string message = null)
        {
            EmitInternal(progress, total, message, stage: null, elapsedMs: null);
        }

        public static void Emit(string token, int progress, int total, string message = null)
        {
            using (ProgressContext.Use(token))
            {
                Emit(progress, total, message);
            }
        }

        /// <summary>
        /// Stage-aware emit. Caller passes a short stage label and (optionally)
        /// the operation's start time so elapsedMs can be computed. When
        /// startedAtUtc is null, EmitStage looks up the previously-recorded
        /// start (via <see cref="MarkOperationStart"/>) for the current
        /// progress token; if none, elapsedMs is omitted.
        /// </summary>
        public static void EmitStage(string stage, int progress, int total, string message = null, DateTime? startedAtUtc = null)
        {
            long? elapsedMs = null;
            if (startedAtUtc.HasValue)
            {
                elapsedMs = (long)(DateTime.UtcNow - startedAtUtc.Value).TotalMilliseconds;
            }
            else
            {
                string token = ProgressContext.CurrentToken;
                if (!string.IsNullOrEmpty(token))
                {
                    lock (_opStartsLock)
                    {
                        if (_opStarts.TryGetValue(token, out var start))
                            elapsedMs = (long)(DateTime.UtcNow - start).TotalMilliseconds;
                    }
                }
            }
            EmitInternal(progress, total, message, stage, elapsedMs);
        }

        /// <summary>
        /// Record the operation start for the current progress token so
        /// subsequent <see cref="EmitStage"/> calls can compute elapsedMs
        /// automatically. Safe to call multiple times — the first one wins.
        /// </summary>
        public static void MarkOperationStart()
        {
            string token = ProgressContext.CurrentToken;
            if (string.IsNullOrEmpty(token)) return;
            lock (_opStartsLock)
            {
                if (!_opStarts.ContainsKey(token)) _opStarts[token] = DateTime.UtcNow;
            }
        }

        /// <summary>Clear the operation start record (called at operation end).</summary>
        public static void ClearOperationStart()
        {
            string token = ProgressContext.CurrentToken;
            if (string.IsNullOrEmpty(token)) return;
            lock (_opStartsLock) { _opStarts.Remove(token); }
        }

        private static void EmitInternal(int progress, int total, string message, string stage, long? elapsedMs)
        {
            string token = ProgressContext.CurrentToken;
            if (string.IsNullOrWhiteSpace(token)) return;

            // Issue #342: every progress emission is evidence that the operation running
            // on the SDK lane is still moving. Recording it here - the single choke point
            // all progress reporting passes through - is what lets the gateway tell a
            // slow-but-progressing build from a wedged SDK call. Without such a signal
            // the only honest thing a health check could say about a busy lane is
            // "busy", and treating that as a fault recycles legitimate long operations.
            Program.NoteSdkProgress();

            // Build params dict explicitly so we can omit optional fields
            // when they're null (keeps the wire payload small for clients
            // that don't render stage/elapsedMs).
            var p = new Dictionary<string, object>
            {
                ["progressToken"] = token,
                ["progress"] = progress,
                ["total"] = total,
                ["message"] = message ?? string.Empty
            };
            if (!string.IsNullOrEmpty(stage)) p["stage"] = stage;
            if (elapsedMs.HasValue) p["elapsedMs"] = elapsedMs.Value;

            var envelope = new
            {
                jsonrpc = "2.0",
                method = "notifications/progress",
                @params = p
            };

            try
            {
                Console.Out.WriteLine(JsonConvert.SerializeObject(envelope));
                Console.Out.Flush();
            }
            catch
            {
                // stdout might be closed during shutdown — silently drop.
            }
        }

        // Test seam — observe the most recent emitted envelope without going
        // through stdout. Production code path stays unchanged.
        internal static string LastEmittedJsonForTests { get; private set; }
        internal static void EmitForTests(int progress, int total, string message, string stage, long? elapsedMs)
        {
            string token = ProgressContext.CurrentToken;
            if (string.IsNullOrWhiteSpace(token)) { LastEmittedJsonForTests = null; return; }
            var p = new Dictionary<string, object>
            {
                ["progressToken"] = token,
                ["progress"] = progress,
                ["total"] = total,
                ["message"] = message ?? string.Empty
            };
            if (!string.IsNullOrEmpty(stage)) p["stage"] = stage;
            if (elapsedMs.HasValue) p["elapsedMs"] = elapsedMs.Value;
            LastEmittedJsonForTests = JsonConvert.SerializeObject(new
            {
                jsonrpc = "2.0",
                method = "notifications/progress",
                @params = p
            });
        }
    }
}
