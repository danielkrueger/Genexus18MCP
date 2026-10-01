using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        /// <summary>Tools that accept a `kbs` federation selection.</summary>
        private static readonly HashSet<string> MultiKbCapableTools =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "genexus_query" };

        /// <summary>
        /// Issue #356 — handles `genexus_query kbs=[...]` as one bounded read-only
        /// operation over an explicit KB selection. Returns <c>null</c> for an
        /// ordinary single-KB query, so the existing route is untouched.
        /// </summary>
        /// <remarks>
        /// Placed before the index-readiness gate on purpose. That gate answers
        /// "is <em>the session's</em> KB indexed?", which is the wrong question for
        /// a federation: each KB reports its own status here — including not-open
        /// and warming — instead of the whole call fast-failing on one KB's index
        /// state and hiding the others' results.
        /// </remarks>
        private static async Task<JObject?> TryDispatchMultiKbDiscoveryAsync(
            string tName,
            JObject? tArgs,
            CancellationToken cancellationToken)
        {
            if (tArgs == null || !MultiKbCapableTools.Contains(tName)) return null;
            JToken? selection = tArgs["kbs"];
            if (selection == null || selection.Type == JTokenType.Null) return null;

            if (!MultiKbDiscovery.TryResolveAliases(tArgs, out var aliases, out string code, out string message))
                return BuildMultiKbRejection(tName, tArgs, code, message);

            if (!MultiKbDiscovery.TryResolveCursors(tArgs, aliases, out var cursors, out code, out message))
                return BuildMultiKbRejection(tName, tArgs, code, message);

            var plan = BuildMultiKbPlan(aliases, out var unresolvable);
            int concurrency = MultiKbDiscovery.ResolveConcurrency(tArgs);
            int maxTotalResults = MultiKbDiscovery.ResolveMaxTotalResults(tArgs);
            var perKbTimeout = ResolveMultiKbPerKbTimeout(tArgs);
            var sw = Stopwatch.StartNew();

            var outcomes = await MultiKbDiscovery.RunAsync(
                plan,
                (target, ct) => DispatchOneKbAsync(tName, tArgs, target, cursors, ct),
                concurrency,
                perKbTimeout,
                cancellationToken).ConfigureAwait(false);

            sw.Stop();
            var envelope = MultiKbDiscovery.BuildEnvelope(
                aliases, outcomes, maxTotalResults, concurrency, perKbTimeout,
                sw.ElapsedMilliseconds, cancellationToken.IsCancellationRequested);
            if (unresolvable.Count > 0) envelope["unknownAliases"] = new JArray(unresolvable);

            return BuildToolResultContent(envelope, isError: false, toolName: tName, toolArgs: tArgs);
        }

        private static JObject BuildMultiKbRejection(string tName, JObject? tArgs, string code, string message)
        {
            // A rejected selection searches nothing, so it must not be shaped like a
            // search that found nothing: results is empty and coverage says so.
            return BuildToolResultContent(
                new JObject
                {
                    ["error"] = message,
                    ["code"] = code,
                    ["federated"] = true,
                    ["results"] = new JArray(),
                    ["coverage"] = new JObject
                    {
                        ["complete"] = false,
                        ["note"] = "No KB was searched; this is a rejected selection, not an empty result."
                    }
                },
                isError: true, toolName: tName, toolArgs: tArgs);
        }

        /// <summary>
        /// Resolves each requested alias to an already-open Worker. Resolution
        /// never opens a KB: the Worker pool has finite capacity, so an implicit
        /// open here could evict the KB the caller is working in.
        /// </summary>
        private static List<MultiKbDiscovery.Target> BuildMultiKbPlan(
            List<string> aliases, out List<string> unresolvable)
        {
            unresolvable = new List<string>();
            var plan = new List<MultiKbDiscovery.Target>(aliases.Count);
            var pool = _workerPool;
            var resolver = _kbResolver;
            if (pool == null || resolver == null)
            {
                foreach (var alias in aliases)
                {
                    plan.Add(new MultiKbDiscovery.Target
                    {
                        Alias = alias,
                        Status = MultiKbDiscovery.StatusError,
                        Detail = "The Gateway worker pool is not initialised; no KB can be searched."
                    });
                }
                return plan;
            }

            var open = pool.ListOpen();
            var known = pool.ListKnown();
            foreach (var alias in aliases)
            {
                KbHandle handle;
                try
                {
                    handle = resolver.Resolve(alias, open, known);
                }
                catch (KbResolutionException ex)
                {
                    unresolvable.Add(alias);
                    plan.Add(new MultiKbDiscovery.Target
                    {
                        Alias = alias,
                        Status = MultiKbDiscovery.StatusUnknownAlias,
                        Detail = ex.Message
                    });
                    continue;
                }

                var worker = pool.TryGetWorker(handle.NormalizedAlias);
                if (worker == null)
                {
                    plan.Add(new MultiKbDiscovery.Target
                    {
                        Alias = alias,
                        Status = MultiKbDiscovery.StatusNotOpen,
                        Detail = MultiKbDiscovery.DescribeStatus(MultiKbDiscovery.StatusNotOpen)
                    });
                    continue;
                }
                if (!worker.IsSdkReady)
                {
                    plan.Add(new MultiKbDiscovery.Target
                    {
                        Alias = alias,
                        Status = MultiKbDiscovery.StatusWarming,
                        Detail = MultiKbDiscovery.DescribeStatus(MultiKbDiscovery.StatusWarming)
                    });
                    continue;
                }
                plan.Add(new MultiKbDiscovery.Target
                {
                    Alias = alias,
                    Status = MultiKbDiscovery.StatusOk,
                    Handle = handle
                });
            }
            return plan;
        }

        private static TimeSpan ResolveMultiKbPerKbTimeout(JObject? args)
        {
            int requested = args?["perKbTimeoutMs"]?.ToObject<int?>() ?? 0;
            if (requested <= 0) return TimeSpan.FromSeconds(30);
            return TimeSpan.FromMilliseconds(Math.Min(requested, 120_000));
        }

        /// <summary>
        /// Runs the ordinary <c>Search -&gt; Query</c> route against one KB's
        /// Worker. Reusing the single-KB route (rather than a second search engine)
        /// is what keeps "a match" meaning the same thing in a federated answer.
        /// </summary>
        private static async Task<MultiKbDiscovery.Outcome> DispatchOneKbAsync(
            string toolName,
            JObject toolArgs,
            MultiKbDiscovery.Target target,
            Dictionary<string, string> cursors,
            CancellationToken ct)
        {
            var perKbArgs = (JObject)(toolArgs.DeepClone());
            foreach (var federationOnly in new[] { "kbs", "cursors", "maxConcurrency", "maxTotalResults", "perKbTimeoutMs", "kb" })
            {
                perKbArgs.Remove(federationOnly);
            }
            if (cursors.TryGetValue(target.Alias, out string? cursor)) perKbArgs["cursor"] = cursor;

            object? routed = McpRouter.ConvertToolCall(new JObject
            {
                ["method"] = "tools/call",
                ["params"] = new JObject { ["name"] = toolName, ["arguments"] = perKbArgs }
            });
            var workerCommand = routed != null ? JObject.FromObject(routed) : null;
            if (workerCommand == null)
            {
                return new MultiKbDiscovery.Outcome
                {
                    Status = MultiKbDiscovery.StatusError,
                    Detail = "The KB's search could not be routed.",
                    Complete = false
                };
            }
            workerCommand["client"] = "mcp";
            // Attribution only. Routing is decided by _currentKb below, because the
            // fan-out leg has to reach a specific Worker rather than the session's.
            workerCommand["kbAlias"] = target.Alias;

            int timeoutMs = (int)Math.Max(1, ResolveMultiKbPerKbTimeout(toolArgs).TotalMilliseconds);
            JObject? response;
            try
            {
                // Each leg is its own async invocation, so this AsyncLocal write is
                // scoped to that leg and cannot leak into a sibling or back out to
                // the caller. Read-only, so no lease/owner requirement applies.
                _currentKb.Value = target.Handle;
                _currentOperationRequiresOwner.Value = false;
                response = await SendWorkerCommandAsync(
                    workerCommand,
                    timeoutMs,
                    $"Timeout waiting for federated query on KB '{target.Alias}'",
                    ok => ok,
                    (operationId, correlationId) => new JObject
                    {
                        ["federatedTimeout"] = true,
                        ["operationId"] = operationId,
                        ["correlationId"] = correlationId
                    },
                    toolName,
                    perKbArgs,
                    trackOperation: false,
                    progressToken: null,
                    heartbeat: null,
                    operationIdentity: null,
                    mcpRequestId: null,
                    mcpRequestIdToken: null,
                    mcpSessionId: null,
                    cancellationToken: ct).ConfigureAwait(false);
            }
            finally
            {
                _currentKb.Value = null;
            }

            var payload = response?["result"] as JObject ?? response?["error"] as JObject;
            if (payload == null || payload["federatedTimeout"] != null)
            {
                return new MultiKbDiscovery.Outcome
                {
                    Status = MultiKbDiscovery.StatusTimeout,
                    Detail = MultiKbDiscovery.DescribeStatus(MultiKbDiscovery.StatusTimeout),
                    Complete = false
                };
            }
            if (payload["error"] != null || string.Equals(payload["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase))
            {
                return new MultiKbDiscovery.Outcome
                {
                    Status = MultiKbDiscovery.StatusError,
                    Detail = payload["error"]?.ToString() ?? payload["status"]?.ToString(),
                    Complete = false
                };
            }

            var results = payload["results"] as JArray;
            return new MultiKbDiscovery.Outcome
            {
                Status = MultiKbDiscovery.StatusOk,
                Payload = payload,
                NextCursor = payload["nextCursor"]?.ToString(),
                Returned = results?.Count ?? payload["count"]?.ToObject<int?>() ?? 0,
                Total = payload["total"]?.ToObject<int?>() ?? results?.Count ?? 0,
                Complete = true
            };
        }
    }
}