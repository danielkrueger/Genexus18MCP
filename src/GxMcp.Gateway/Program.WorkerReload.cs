using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;

namespace GxMcp.Gateway
{
    partial class Program
    {

        internal static bool CanReloadWithoutLeaseForTest(JObject? args, int openKbCount)
        {
            return !string.IsNullOrWhiteSpace(args?["kb"]?.ToString())
                || !string.IsNullOrWhiteSpace(args?["alias"]?.ToString())
                || openKbCount == 1;
        }

        internal static bool IsForceHardReloadUnsupported(JObject? args)
        {
            return args?["force"]?.ToObject<bool?>() == true
                && string.Equals(args?["mode"]?.ToString(), "hard", StringComparison.OrdinalIgnoreCase);
        }

        private static bool CanReloadWithoutLease(JObject? args)
        {
            return CanReloadWithoutLeaseForTest(args, _workerPool?.ListOpen().Count ?? 0);
        }

        /// <summary>
        /// Shared force-reload restore core: respawns each handle and records whether
        /// the replacement reached SDK-ready. Both the alias-scoped and the global
        /// force-reload paths report through the same restored/failed arrays.
        /// </summary>
        private static async Task<(JArray Ready, JArray Failed)> RestoreWorkersAsync(
            WorkerPool workerPool, IReadOnlyList<KbHandle> handles, string toolName)
        {
            var ready = new JArray();
            var failed = new JArray();
            foreach (var handle in handles)
            {
                try
                {
                    using var readyCts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
                    var replacement = await workerPool.AcquireAsync(handle, readyCts.Token).ConfigureAwait(false);
                    bool isReady = await McpRouter.AwaitWithHeartbeat(
                        replacement.SdkReadyTask, timeoutMs: 180_000,
                        progressToken: null, heartbeat: null,
                        toolName: toolName).ConfigureAwait(false);
                    if (isReady) ready.Add(handle.Alias);
                    else failed.Add(new JObject { ["alias"] = handle.Alias, ["reason"] = "sdkReadyTimeout" });
                }
                catch (Exception restoreEx)
                {
                    failed.Add(new JObject { ["alias"] = handle.Alias, ["reason"] = restoreEx.Message });
                }
            }
            return (ready, failed);
        }

        /// <summary>
        /// Gateway-owned <c>genexus_worker_reload</c> handling: argument validation,
        /// forced kill+respawn (alias or global scope) and the graceful drain+replace
        /// path. Always returns a response for this tool.
        /// </summary>
        private static async Task<JObject> HandleWorkerReloadToolAsync(JToken? idToken, string toolName, JObject? args)
        {
            string reloadMode = args?["mode"]?.ToString()?.Trim().ToLowerInvariant() ?? "soft";
            if (reloadMode != "soft" && reloadMode != "hard")
            {
                return BuildToolTextResponse(
                    idToken,
                    new JObject
                    {
                        ["status"] = "error",
                        ["error"] = new JObject
                        {
                            ["code"] = "UnsupportedReloadMode",
                            ["message"] = "genexus_worker_reload supports mode=soft or mode=hard.",
                            ["hint"] = "Use mode=soft for a graceful restart, or mode=hard with sourceDir to hot-swap Worker binaries."
                        }
                    },
                    isError: true, toolName: toolName, toolArgs: args, payloadOwned: true);
            }
            if (reloadMode == "hard" && string.IsNullOrWhiteSpace(args?["sourceDir"]?.ToString()))
            {
                return BuildToolTextResponse(
                    idToken,
                    new JObject
                    {
                        ["status"] = "error",
                        ["error"] = new JObject
                        {
                            ["code"] = "ReloadSourceRequired",
                            ["message"] = "mode=hard requires sourceDir.",
                            ["hint"] = "Pass the Worker build directory in sourceDir, or use mode=soft."
                        }
                    },
                    isError: true, toolName: toolName, toolArgs: args, payloadOwned: true);
            }
            if (IsForceHardReloadUnsupported(args))
            {
                return BuildToolTextResponse(
                    idToken,
                    new JObject
                    {
                        ["status"] = "error",
                        ["error"] = new JObject
                        {
                            ["code"] = "ForceHardReloadUnsupported",
                            ["message"] = "force=true cannot be combined with mode=hard.",
                            ["hint"] = "Use mode=hard without force to copy sourceDir during a graceful drain, or use force=true with mode=soft when the Worker is wedged. No Worker was stopped."
                        }
                    },
                    isError: true, toolName: toolName, toolArgs: args, payloadOwned: true);
            }
            // Friction 2026-05-22: genexus_worker_reload force=true bypasses
            // the JSON-RPC pipe and kills the worker directly. The soft path
            // is unreachable when the worker is wedged on a hung preview
            // subprocess — by definition it can't ACK the reload command.
            if (args?["force"]?.ToObject<bool?>() == true)
            {
            // Refuse the force path when configuration isn't loaded — otherwise we'd
            // kill the worker pool with no way to bring it back up and the caller
            // would only learn from subsequent tool failures.
            if (_activeConfig == null)
            {
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = idToken?.DeepClone(),
                    ["error"] = JToken.FromObject(new { code = -32603, message = "Force-reload refused: no active configuration. The gateway hasn't completed startup or a prior config load failed; respawning the worker without a config would leave the pool empty." })
                };
            }
            try
            {
                string? forcedAlias = args?["alias"]?.ToString() ?? args?["kb"]?.ToString();
                if (!string.IsNullOrWhiteSpace(forcedAlias))
                {
                    var forcedHandle = _workerPool?.ListOpen().FirstOrDefault(h =>
                        string.Equals(h.NormalizedAlias, forcedAlias, StringComparison.OrdinalIgnoreCase));
                    if (forcedHandle == null)
                    {
                        return BuildToolTextResponse(idToken,
                            new JObject
                            {
                                ["status"] = "error",
                                ["error"] = new JObject
                                {
                                    ["code"] = "ReloadTargetNotFound",
                                    ["message"] = $"No open Worker exists for alias '{forcedAlias}'.",
                                    ["hint"] = "Pass an alias/kb from genexus_kb action=list."
                                }
                            },
                            isError: true, toolName: toolName, toolArgs: args, payloadOwned: true);
                    }

                    InvalidateIndexStateForKb(forcedHandle.NormalizedAlias);
                    ResetIndexBootstrapForAlias(forcedHandle.NormalizedAlias);
                    bool recycled;
                    using (SuppressEagerRespawn())
                        recycled = _workerPool!.RecycleStalledWorker(forcedHandle.NormalizedAlias);
                    if (!recycled)
                        throw new InvalidOperationException($"Worker for alias '{forcedHandle.Alias}' was not live when force reload started.");

                    (JArray forcedReady, JArray forcedFailed) = await RestoreWorkersAsync(
                        _workerPool!, new[] { forcedHandle }, "worker_reload force").ConfigureAwait(false);
                    bool ready = forcedReady.Count > 0;
                    if (ready) TriggerIndexBootstrapOnce(forcedHandle.NormalizedAlias);
                    BroadcastToolsListChanged("worker_reloaded_force", forcedHandle.NormalizedAlias,
                        _semanticCache.GetRevision(forcedHandle.NormalizedAlias));
                    BroadcastResourcesListChanged("worker_reloaded_force", forcedHandle.NormalizedAlias,
                        _semanticCache.GetRevision(forcedHandle.NormalizedAlias));
                    return BuildToolTextResponse(idToken,
                        new JObject
                        {
                            ["status"] = ready ? "Forced" : "ReloadFailed",
                            ["scope"] = "alias",
                            ["affectedAliases"] = new JArray(forcedHandle.Alias),
                            ["abandonedJobs"] = true,
                            ["cacheInvalidated"] = true,
                            ["restoredWorkers"] = forcedReady,
                            ["failedWorkers"] = forcedFailed
                        },
                        isError: !ready, toolName: toolName, toolArgs: args);
                }
                var handlesToRestore = _workerPool?.ListOpen().ToList() ?? new List<KbHandle>();
                if (_workerPool != null)
                {
                    using (SuppressEagerRespawn())
                    {
                        _workerPool.StopAll();
                    }
                }
                _semanticCache.InvalidateScope(string.Empty);
                System.Threading.Interlocked.Increment(ref SemanticCacheEpoch);
                StartWorker(_activeConfig);
                (JArray readyAliases, JArray failedAliases) = await RestoreWorkersAsync(
                    _workerPool!, handlesToRestore, "worker_reload force").ConfigureAwait(false);
                BroadcastToolsListChanged("worker_reloaded_force");
                BroadcastResourcesListChanged("worker_reloaded_force");
                var ok = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = idToken?.DeepClone(),
                    ["result"] = BuildToolResultContent(
                        new JObject
                        {
                            ["status"] = failedAliases.Count == 0 ? "Forced" : "ReloadFailed",
                            ["online"] = handlesToRestore.Count > 0 && readyAliases.Count > 0 && failedAliases.Count == 0,
                            ["restoredWorkers"] = readyAliases,
                            ["failedWorkers"] = failedAliases,
                            ["scope"] = "global",
                            ["affectedAliases"] = new JArray(handlesToRestore.Select(h => JToken.FromObject(h.Alias)).ToArray()),
                            ["abandonedJobs"] = true,
                            ["cacheInvalidated"] = true,
                            ["detail"] = handlesToRestore.Count == 0
                                ? "Worker pool was reset; no previously-open worker existed to restore. A worker will start on the next KB request."
                                : failedAliases.Count == 0
                                    ? "Worker process(es) were killed, respawned, and confirmed SDK-ready. Any in-flight worker job was abandoned."
                                    : "Worker process(es) were killed, but one or more replacements did not become SDK-ready."
                        },
                        isError: failedAliases.Count > 0,
                        toolName: toolName,
                        toolArgs: args)
                };
                return ok;
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = idToken?.DeepClone(),
                    ["error"] = JToken.FromObject(new { code = -32603, message = "Force-reload failed: " + ex.Message })
                };
            }
            }


            // Non-force genexus_worker_reload: gateway-orchestrated graceful drain + respawn.
            // Previously this fell through to the worker as a normal RPC; the worker ACK'd
            // then exited, leaving a window where AcquireAsync returned the dying process.
            // Now the gateway marks the pool entry as draining (blocking concurrent
            // AcquireAsync callers), sends StopWithReason(PlannedReload), waits for the OS
            // process to exit, spawns a fresh worker, awaits its SdkReadyTask, then responds.
            if (args?["force"]?.ToObject<bool?>() != true)
            {
            if (_activeConfig == null || _workerPool == null)
            {
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = idToken?.DeepClone(),
                    ["error"] = JToken.FromObject(new { code = -32603, message = "Reload refused: gateway has no active configuration." })
                };
            }

            string? reloadAlias = args?["alias"]?.ToString() ?? args?["kb"]?.ToString();
            KbHandle? reloadKb = _currentKb.Value;
            if (reloadKb == null && !string.IsNullOrWhiteSpace(reloadAlias))
            {
                reloadKb = _workerPool.ListOpen().FirstOrDefault(h =>
                    string.Equals(h.NormalizedAlias, reloadAlias, StringComparison.OrdinalIgnoreCase));
            }
            if (reloadKb == null)
            {
                var openKbs = _workerPool.ListOpen().ToList();
                if (openKbs.Count == 1)
                    reloadKb = openKbs[0];
                else if (openKbs.Count > 1)
                {
                    return BuildToolTextResponse(idToken,
                        new JObject
                        {
                            ["status"] = "error",
                            ["error"] = new JObject
                            {
                                ["code"] = "KB_AMBIGUOUS",
                                ["message"] = "Multiple KB workers are open; select one or pass alias/kb explicitly.",
                                ["hint"] = "Run genexus_kb action=select alias=<alias>, or pass alias=<alias> to genexus_worker_reload."
                            }
                        },
                        isError: true, toolName: toolName, toolArgs: args, payloadOwned: true);
                }
            }
            if (reloadKb == null)
            {
                return BuildToolTextResponse(idToken,
                    new JObject { ["status"] = "NoWorker", ["detail"] = "No open KB worker found to reload." },
                    isError: false, toolName: toolName, toolArgs: args);
            }
            string softReloadMode = args?["mode"]?.ToString()?.Trim().ToLowerInvariant() ?? "soft";
            // mode=hard: sourceDir carries new worker binaries to swap in. The gateway
            // does the copy in the drain window (old worker exited → exe unlocked → eager
            // respawn suppressed) so it can't lose the race to a respawn. Previously
            // sourceDir was ignored here (plain drain+respawn ran the OLD binary).
            string? reloadSrcDir = softReloadMode == "hard" ? args?["sourceDir"]?.ToString() : null;
            // Reject an unusable sourceDir BEFORE the drain. The swap itself runs inside the
            // drain window (see DrainAndReplaceAsync), so a typo'd path used to stop a healthy
            // worker, clear the pool entry and then fail — leaving the KB with no live worker
            // and the old binary. Validate first so that failure mode cannot happen.
            if (!string.IsNullOrWhiteSpace(reloadSrcDir))
            {
                try
                {
                    ValidateWorkerBinarySwapSource(reloadSrcDir);
                }
                catch (Exception ex)
                {
                    return BuildToolTextResponse(idToken,
                        new JObject
                        {
                            ["status"] = "Error",
                            ["code"] = "WorkerSwapSourceInvalid",
                            ["error"] = new JObject
                            {
                                ["code"] = "WorkerSwapSourceInvalid",
                                ["message"] = ex.Message,
                                ["hint"] = "Point sourceDir at a built Worker output directory containing GxMcp.Worker.exe and GxMcp.Worker.exe.config (for example src\\GxMcp.Worker\\bin\\Debug). The running worker was left untouched."
                            },
                            ["workerRestarted"] = false
                        },
                        isError: true, toolName: toolName, toolArgs: args, payloadOwned: true);
                }
            }
            try
            {
                InvalidateIndexStateForKb(reloadKb.NormalizedAlias);
                ResetIndexBootstrapForAlias(reloadKb.NormalizedAlias);
                using (SuppressEagerRespawn())
                {
                    Func<WorkerProcess?, Task>? swapHook = string.IsNullOrWhiteSpace(reloadSrcDir)
                        ? (Func<WorkerProcess?, Task>?)null
                        : (oldW =>
                        {
                            string? tgtDir = null;
                            try { tgtDir = System.IO.Path.GetDirectoryName(oldW?.SpawnedExePath); } catch { }
                            CopyWorkerBinaries(reloadSrcDir!, tgtDir);
                            return Task.CompletedTask;
                        });
                    // Drain timeout: 30s for the old worker process to exit.
                    var newWorker = await _workerPool.DrainAndReplaceAsync(reloadKb, drainTimeoutMs: 30_000, ct: CancellationToken.None, afterDrainBeforeSpawn: swapHook).ConfigureAwait(false);
                    // Wait for the new worker's SDK to be ready (cap at 180s to avoid hanging).
                    bool sdkReady = await McpRouter.AwaitWithHeartbeat(
                        newWorker.SdkReadyTask, timeoutMs: 180_000,
                        progressToken: null, heartbeat: null,
                        toolName: "worker_reload").ConfigureAwait(false);
                    if (!sdkReady)
                    {
                        return BuildToolTextResponse(idToken,
                            new JObject
                            {
                                ["status"] = "NotReady",
                                ["swappedAndReady"] = false,
                                ["detail"] = "Worker replaced but new worker did not signal SDK-ready within 180s.",
                                ["retryable"] = true
                            },
                            isError: true, toolName: toolName, toolArgs: args, payloadOwned: true);
                    }
                    TriggerIndexBootstrapOnce(reloadKb.NormalizedAlias);
                    BroadcastToolsListChanged(
                        "worker_reloaded_soft",
                        reloadKb.NormalizedAlias,
                        _semanticCache.GetRevision(reloadKb.NormalizedAlias));
                    BroadcastResourcesListChanged(
                        "worker_reloaded_soft",
                        reloadKb.NormalizedAlias,
                        _semanticCache.GetRevision(reloadKb.NormalizedAlias));
                    return BuildToolTextResponse(idToken,
                        new JObject
                        {
                            ["status"] = "Reloaded",
                            ["swappedAndReady"] = sdkReady,
                            ["detail"] = "Worker gracefully drained and replaced; new worker is SDK-ready."
                        },
                        isError: false, toolName: toolName, toolArgs: args);
                }
            }
            catch (Exception ex)
            {
                Log($"[Reload] Soft reload failed for KB '{reloadKb.Alias}': {ex.Message}");
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = idToken?.DeepClone(),
                    ["error"] = JToken.FromObject(new { code = -32603, message = "Soft reload failed: " + ex.Message })
                };
            }
            }

            throw new InvalidOperationException("genexus_worker_reload handling must return a response.");
        }
    }
}
