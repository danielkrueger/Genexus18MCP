using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Issue #60 — save + specify in a single validated operation.
    ///
    /// After any successful write the caller may opt in via the tool args:
    ///   - validationMode="specify"  → run the SpecifyOneOnly pass (Spec+Gen, no
    ///     Compile/deploy) inline and surface spc*/gen* diagnostics in the same
    ///     call, so a spec-invalid edit is caught before the client moves on.
    ///   - rollbackOnFailure=true     → when the specify pass reports errors,
    ///     restore the pre-write state from the EditSnapshotStore snapshot that
    ///     WriteService captured before persisting, and report the rollback.
    ///
    /// Clean writes get a `_meta.specification` block appended to the write
    /// envelope; failing writes get the structured SpecificationFailed error with
    /// diagnostics in the issue #60 shape: <c>[{code, object, member, message}]</c>.
    /// </summary>
    public class SaveSpecifyOrchestrator
    {
        private readonly BuildService _buildService;
        private readonly HistoryService _historyService;

        public SaveSpecifyOrchestrator(BuildService buildService, HistoryService historyService)
        {
            _buildService = buildService;
            _historyService = historyService;
        }

        /// <summary>
        /// Inspect a completed write response and the tool args; when validationMode=specify
        /// and the write actually succeeded, run the inline specify pass and either decorate
        /// the response with `_meta.specification` (clean) or replace it with the structured
        /// SpecificationFailed error (spec errors; rolled back when rollbackOnFailure=true).
        /// Returns the input response unchanged when validation isn't engaged.
        /// </summary>
        /// <param name="partName">
        /// The part the write touched ("Source", "Variables", "Structure", ...). Used to
        /// locate the pre-write snapshot for rollback — a null part defaults to "Source",
        /// which would miss snapshots taken under other part names.
        /// </param>
        public string MaybeValidateAfterWrite(string writeResponse, string target, JObject args, string partName = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(writeResponse) || args == null || string.IsNullOrWhiteSpace(target))
                    return writeResponse;

                // Engagement gate — only validate when the caller asked for the specify pass.
                string validationMode = args["validationMode"]?.ToString() ?? args["validate"]?.ToString();
                if (!string.Equals(validationMode, "specify", StringComparison.OrdinalIgnoreCase))
                    return writeResponse;

                // Only validate writes that actually persisted. dryRun / validate=only are
                // preview paths — nothing was saved, so there is nothing to spec-check.
                bool dryRun = args["dryRun"]?.ToObject<bool?>() ?? false;
                if (dryRun || string.Equals(args["validate"]?.ToString(), "only", StringComparison.OrdinalIgnoreCase))
                    return writeResponse;

                JObject writeEnv;
                try { writeEnv = JObject.Parse(writeResponse); }
                catch { return writeResponse; }
                string status = writeEnv["status"]?.ToString() ?? writeEnv["Status"]?.ToString() ?? string.Empty;
                if (!IsSuccessStatus(status)) return writeResponse;

                bool rollbackOnFailure = args["rollbackOnFailure"]?.ToObject<bool?>() ?? false;
                int maxWaitSec = ClampSpecifyTimeout(args["specifyTimeoutSec"]?.ToObject<int?>() ?? 90);

                var (ok, diagArr, statusJson, waitedSec, timedOut, taskId) = RunSpecifyCheck(target, maxWaitSec);
                var specWarnings = SpecificationDiagnostics.GetWarnings(statusJson);
                if (ok)
                {
                    // Clean — decorate the write envelope with the specification block.
                    var meta = writeEnv["_meta"] as JObject;
                    if (meta == null) { meta = new JObject(); writeEnv["_meta"] = meta; }
                    var spec = new JObject
                    {
                        ["status"] = "ok",
                        ["diagnostics"] = diagArr,
                        ["elapsedSec"] = waitedSec
                    };
                    AddSpecifyWarnings(spec, specWarnings);
                    meta["specification"] = spec;
                    return writeEnv.ToString(Newtonsoft.Json.Formatting.None);
                }

                // Spec errors (or the specify pass didn't reach a terminal Succeeded state).
                bool rolledBack = false;
                string rollbackNote = null;
                if (rollbackOnFailure)
                {
                    (rolledBack, rollbackNote) = TryRollback(target, partName);
                }

                var errExtra = new JObject
                {
                    ["diagnostics"] = diagArr,
                    ["elapsedSec"] = waitedSec,
                    ["timedOut"] = timedOut,
                    ["rollbackOnFailure"] = rollbackOnFailure,
                    ["rolledBack"] = rolledBack
                };
                AddSpecifyWarnings(errExtra, specWarnings);
                if (rollbackNote != null) errExtra["rollbackNote"] = rollbackNote;
                if (!string.IsNullOrWhiteSpace(taskId)) errExtra["taskId"] = taskId;

                string message = timedOut
                    ? $"The write persisted but the specify pass did not finish within {maxWaitSec}s; the last status was '{SpecificationDiagnostics.GetStatus(statusJson)}'. Poll task '{taskId ?? "<taskId>"}' with genexus_lifecycle action=status to see the final result."
                    : $"The write persisted but the specify pass reported {diagArr.Count} diagnostic(s). The object may fail to build."
                        + (SpecificationDiagnostics.HasStaleEnvironmentCopy(specWarnings)
                            ? " The environment's copy was specified (changed since the last build), so these diagnostics may describe the source as of the last build."
                            : string.Empty);
                string hint = rollbackOnFailure && rolledBack
                    ? "rollbackOnFailure was set and the pre-write state was restored; re-read the object to confirm the pre-edit content."
                    : rollbackOnFailure && !rolledBack
                        ? "rollbackOnFailure was set but no pre-write snapshot was available to restore for part '" + (partName ?? "Source") + "'. Verify the object state with genexus_read."
                        : timedOut
                            ? "The specify pass is still running in the worker; poll genexus_lifecycle action=status target=" + (taskId ?? "<taskId>") + " to get the final diagnostics."
                            : "Fix the diagnostics below, or set rollbackOnFailure=true on the next write to auto-restore the pre-write state.";

                return Models.McpResponse.Err(
                    code: "SpecificationFailed",
                    message: message,
                    hint: hint,
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = target, ["part"] = partName ?? "Source" },
                        why: "Reads the object's current state so you can see what persisted before fixing the diagnostics.")),
                    target: target,
                    errorExtra: errExtra);
            }
            catch (Exception ex)
            {
                Logger.Debug("[SAVE-SPECIFY] " + ex.Message);
                return writeResponse;
            }
        }

        // Surface the specify task's warnings; the stale-copy one also gets a flag because
        // GeneXus specifies the environment's copy, which only a build refreshes.
        internal static void AddSpecifyWarnings(JObject target, JArray warnings)
        {
            if (warnings == null || warnings.Count == 0) return;
            target["warnings"] = warnings;
            if (SpecificationDiagnostics.HasStaleEnvironmentCopy(warnings))
                target["staleEnvironmentCopy"] = true;
        }

        // A result is clean only when it reached the Succeeded terminal state without
        // spc/gen errors. Timeout (still Running), Failed, Error, Cancelled are not clean.
        private static bool IsCleanResult(string statusJson)
        {
            if (string.IsNullOrWhiteSpace(statusJson)) return false;
            string status = SpecificationDiagnostics.GetStatus(statusJson);
            return string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase)
                && !SpecificationDiagnostics.HasSpecErrors(statusJson);
        }

        internal static int ClampSpecifyTimeout(int requestedSeconds)
        {
            return Math.Max(1, Math.Min(120, requestedSeconds));
        }

        internal static string ExtractTaskId(string acceptedJson)
        {
            try
            {
                var envelope = JObject.Parse(acceptedJson ?? string.Empty);
                return envelope["taskId"]?.ToString()
                    ?? (envelope["result"] as JObject)?["taskId"]?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private bool IsSuccessStatus(string status)
        {
            return string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "PartialSuccess", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "partial", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Applied", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "PropertyApplied", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "StructureUpdated", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "VariableAdded", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "DomainUpdated", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Run the SpecifyOneOnly pass and wait (bounded) for a terminal status. Returns
        /// (hasNoSpecErrors, structuredDiagnostics, finalStatusJson, elapsedSeconds,
        /// timedOut, taskId) where timedOut is true when the budget expired before a
        /// terminal state.
        /// </summary>
        private (bool Clean, JArray Diagnostics, string StatusJson, int WaitedSec, bool TimedOut, string TaskId) RunSpecifyCheck(string target, int maxWaitSec)
        {
            try
            {
                string accepted = _buildService.Specify(target);
                string taskId = ExtractTaskId(accepted);

                if (string.IsNullOrEmpty(taskId))
                {
                    // No task id — either the build couldn't start or it completed synchronously.
                    // Use the SAME clean logic as the polled path so an error envelope (status
                    // Error/Failed) is never reported as a passing spec check.
                    return (IsCleanResult(accepted), SpecificationDiagnostics.Parse(accepted), accepted, 0, false, null);
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                string statusJson = null;
                // Chained event-driven wait: GetStatusWait returns IMMEDIATELY when
                // sinceBaseline is null/empty (it has no baseline to wait on). We pass
                // the _meta.snapshot from the previous response as `since` so each call
                // actually blocks until the task transitions away from that baseline —
                // no busy-polling the STA worker.
                string since = null;
                while (sw.Elapsed.TotalSeconds < maxWaitSec)
                {
                    int remain = Math.Max(1, maxWaitSec - (int)sw.Elapsed.TotalSeconds);
                    statusJson = _buildService.GetStatusWait(taskId, remain, since, 1, 50, true);
                    if (string.IsNullOrWhiteSpace(statusJson)) break;
                    if (SpecificationDiagnostics.IsTerminal(statusJson)) break;

                    string newSince = SpecificationDiagnostics.GetSnapshot(statusJson);
                    since = string.IsNullOrWhiteSpace(newSince) ? since : newSince;
                    // Keep waiting on ANY non-terminal status (Running, Accepted, Pending, ...).
                    // A brief back-off guards against spurious early returns from GetStatusWait
                    // (baseline mismatch / no snapshot) without spinning hot on the STA worker.
                    int elapsed = (int)sw.Elapsed.TotalSeconds;
                    if (elapsed < maxWaitSec) System.Threading.Thread.Sleep(250);
                }
                if (string.IsNullOrEmpty(statusJson)) statusJson = _buildService.GetStatus(taskId, 1, 50, true);

                var diagnostics = SpecificationDiagnostics.Parse(statusJson);
                // Clean means the specify pass finished WITHOUT spc/gen errors AND reached a
                // Succeeded terminal state. A still-running task (timeout) or a Failed/Error
                // task is NOT clean — the caller must not see a passing spec check.
                bool clean = IsCleanResult(statusJson);
                bool timedOut = !SpecificationDiagnostics.IsTerminal(statusJson);
                return (clean, diagnostics, statusJson, (int)sw.Elapsed.TotalSeconds, timedOut, taskId);
            }
            catch (Exception ex)
            {
                Logger.Debug("[SAVE-SPECIFY] run failed: " + ex.Message);
                var errDiag = new JArray(new JObject
                {
                    ["code"] = "SpecifyRunFailed",
                    ["object"] = target,
                    ["message"] = ex.Message
                });
                return (false, errDiag, null, 0, false, null);
            }
        }

        /// <summary>
        /// Best-effort restore of the pre-write state via the EditSnapshotStore-backed
        /// history restore (the snapshot WriteService captured before persisting).
        /// Returns (restored, note). Restore is skipped — reported, not fatal — when no
        /// snapshot exists for the target. The part name matters: snapshots are keyed by
        /// (guid, part), so restoring without the written part defaults to "Source" and
        /// would miss a "Variables"/"Structure" snapshot.
        /// </summary>
        private (bool Restored, string Note) TryRollback(string target, string partName)
        {
            try
            {
                string resp = _historyService.Execute(target, "restore", partName: partName, snapshotToken: "latest");
                try
                {
                    var jo = JObject.Parse(resp);
                    string status = jo["status"]?.ToString() ?? jo["Status"]?.ToString() ?? string.Empty;
                    if (IsSuccessStatus(status) || string.Equals(jo["code"]?.ToString(), "SnapshotRestored", StringComparison.OrdinalIgnoreCase))
                        return (true, null);
                    string code = jo["error"]?["code"]?.ToString() ?? jo["code"]?.ToString() ?? string.Empty;
                    return (false, $"restore returned {code}: {jo["error"]?["message"]?.ToString() ?? jo["message"]?.ToString() ?? resp}");
                }
                catch
                {
                    return (false, "restore response unparseable");
                }
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
    }
}
