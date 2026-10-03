using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Issue #356 — bounded read-only discovery across an explicit set of KBs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before this, locating an object across two KBs meant one call per KB, each
    /// with its own cursor, merged by the agent. That is fine once and expensive
    /// as a habit. This collapses it into one operation over an <em>explicit</em>
    /// alias list, and it earns that by being honest about what it did.
    /// </para>
    /// <para>
    /// Three rules shape everything below:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>No implicit opens.</b> A KB that is not already open is reported
    /// <c>notOpen</c>, never spawned. Discovery that quietly starts a Worker can
    /// evict a KB the user is working in (<c>WorkerPool</c> has finite capacity),
    /// which is a far worse outcome than a longer answer.
    /// </description></item>
    /// <item><description>
    /// <b>A failed KB is not a KB with no matches.</b> Every entry carries its
    /// own status and <c>complete</c> flag, and the envelope carries an explicit
    /// coverage summary. Collapsing "KbBeta timed out" into "0 results" is the
    /// single most damaging thing this operation could do.
    /// </description></item>
    /// <item><description>
    /// <b>Bounded everywhere.</b> A fan-out whose cost scales with the alias list
    /// is not bounded. Alias count, concurrency, per-KB deadline and total result
    /// count are all capped, and cancellation propagates.
    /// </description></item>
    /// </list>
    /// <para>
    /// No new search engine: each KB runs the existing <c>Search -&gt; Query</c>
    /// route, so federation reuses its ranking, filtering and cursor semantics
    /// rather than defining a second, subtly different notion of a match.
    /// </para>
    /// </remarks>
    internal sealed class MultiKbDiscovery
    {
        internal const int MaxKbCount = 16;
        internal const int DefaultConcurrency = 4;
        internal const int MaxConcurrency = 8;
        internal const int DefaultMaxTotalResults = 500;
        internal const int HardMaxTotalResults = 5000;

        internal const string StatusOk = "ok";
        internal const string StatusNotOpen = "notOpen";
        internal const string StatusUnknownAlias = "unknownAlias";
        internal const string StatusWarming = "warming";
        internal const string StatusError = "error";
        internal const string StatusTimeout = "timeout";
        internal const string StatusBudgetExceeded = "budgetExceeded";
        internal const string StatusCanceled = "canceled";

        /// <summary>
        /// Validates the caller-supplied alias list. Returns <c>false</c> with a
        /// coded reason rather than guessing: a federation over an unrecognised
        /// set would otherwise return a confidently wrong "nothing found".
        /// </summary>
        internal static bool TryResolveAliases(
            JObject args, out List<string> aliases, out string code, out string message)
        {
            aliases = new List<string>();
            code = string.Empty;
            message = string.Empty;

            JToken? token = args?["kbs"];
            if (token == null || token.Type == JTokenType.Null) return false;

            if (!(token is JArray list))
            {
                code = "MultiKbSelectionInvalid";
                message = "kbs must be an array of KB aliases. No KB was searched.";
                return false;
            }
            if (list.Count == 0)
            {
                code = "MultiKbSelectionInvalid";
                message = "kbs must name at least one KB. An empty federation would search nothing and report it as a complete answer; omit kbs instead for a single-KB read.";
                return false;
            }
            if (list.Count > MaxKbCount)
            {
                code = "MultiKbSelectionTooLarge";
                message = $"kbs lists {list.Count} KBs; the bounded maximum is {MaxKbCount}. Narrow the selection — a fan-out wider than the budget would be reported as a complete answer it cannot honour.";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<string>();
            foreach (var item in list)
            {
                string alias = (item?.ToString() ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(alias))
                {
                    code = "MultiKbSelectionInvalid";
                    message = "kbs must contain non-empty alias strings. No KB was searched.";
                    return false;
                }
                if (!seen.Add(alias))
                {
                    // A duplicate would make "searched KBs" disagree with "results
                    // grouped by KB", and would double-count against the budget.
                    code = "MultiKbSelectionInvalid";
                    message = $"kbs lists '{alias}' more than once. Each KB is searched at most once so that per-KB status and per-KB continuation stay unambiguous.";
                    return false;
                }
                ordered.Add(alias);
            }

            if (!string.IsNullOrWhiteSpace(args?["kb"]?.ToString()))
            {
                code = "MultiKbSelectionAmbiguous";
                message = "kb and kbs are mutually exclusive. A federation names its own KBs and does not consult session selection; passing both leaves it undecidable which KB 'kb' was meant to add.";
                return false;
            }

            aliases = ordered;
            return true;
        }

        internal static int ResolveConcurrency(JObject args)
        {
            int requested = args?["maxConcurrency"]?.ToObject<int?>() ?? DefaultConcurrency;
            if (requested <= 0) return DefaultConcurrency;
            return Math.Min(requested, MaxConcurrency);
        }

        internal static int ResolveMaxTotalResults(JObject args)
        {
            int requested = args?["maxTotalResults"]?.ToObject<int?>() ?? DefaultMaxTotalResults;
            if (requested <= 0) return DefaultMaxTotalResults;
            return Math.Min(requested, HardMaxTotalResults);
        }

        /// <summary>
        /// Per-KB continuation cursors, keyed by the alias as the caller spelled
        /// it. An alias that is not in the current selection is rejected rather
        /// than ignored: silently dropping a cursor is how a resumed page silently
        /// repeats its first page.
        /// </summary>
        internal static bool TryResolveCursors(
            JObject args, List<string> aliases, out Dictionary<string, string> cursors, out string code, out string message)
        {
            cursors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            code = string.Empty;
            message = string.Empty;
            JToken? token = args?["cursors"];
            if (token == null || token.Type == JTokenType.Null) return true;
            if (!(token is JObject map))
            {
                code = "MultiKbCursorInvalid";
                message = "cursors must be an object mapping each requested KB alias to that KB's nextCursor. No KB was searched.";
                return false;
            }

            var selected = new HashSet<string>(aliases, StringComparer.OrdinalIgnoreCase);
            foreach (var property in map.Properties())
            {
                string alias = (property.Name ?? string.Empty).Trim();
                if (!selected.Contains(alias))
                {
                    code = "MultiKbCursorInvalid";
                    message = $"cursors names '{property.Name}', which is not in kbs. A cursor for an unsearched KB cannot be resumed and would be silently discarded. No KB was searched.";
                    return false;
                }
                string cursor = property.Value?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(cursor))
                {
                    code = "MultiKbCursorInvalid";
                    message = $"cursors['{property.Name}'] is empty. Omit the alias to restart that KB from the beginning. No KB was searched.";
                    return false;
                }
                cursors[alias] = cursor;
            }
            return true;
        }

        /// <summary>
        /// One requested alias and what the Gateway could determine about it
        /// <em>before</em> any search ran. The status decided here is what makes
        /// "not searched" distinguishable from "searched, nothing matched".
        /// </summary>
        internal sealed class Target
        {
            internal string Alias { get; set; } = string.Empty;
            internal string Status { get; set; } = StatusOk;
            internal string Detail { get; set; } = string.Empty;
            /// <summary>The already-open KB's handle. Never null when the status is ok.</summary>
            internal KbHandle? Handle { get; set; }
        }

        /// <summary>What one KB produced, independent of transport concerns.</summary>
        internal sealed class Outcome
        {
            internal string Alias { get; set; } = string.Empty;
            internal string Status { get; set; } = StatusOk;
            internal string? Detail { get; set; }
            internal JObject? Payload { get; set; }
            internal string? NextCursor { get; set; }
            /// <summary>Results of this KB's page that the federation budget dropped.</summary>
            internal int DroppedByBudget { get; set; }
            /// <summary>
            /// True when a budget cut withdrew a cursor that pointed past the trimmed
            /// remainder of the Worker's page, making those items unreachable.
            /// </summary>
            internal bool BudgetCutStrandedPageRemainder { get; set; }
            internal int Returned { get; set; }
            internal int Total { get; set; }
            internal int ElapsedMs { get; set; }
            /// <summary>False when this KB's coverage is unknown, partial, or was cut short.</summary>
            internal bool Complete { get; set; } = true;

            internal bool Searched => string.Equals(Status, StatusOk, StringComparison.Ordinal);

            /// <summary>The per-KB entry the caller sees. Carries the matches and the evidence.</summary>
            internal JObject ToEntry(int truncatedTo)
            {
                var entry = new JObject
                {
                    ["kb"] = Alias,
                    ["status"] = Status,
                    // Explicit on every entry: "no results" and "did not finish"
                    // must never look alike.
                    ["complete"] = Complete,
                    ["returned"] = Returned,
                    ["total"] = Total,
                    ["hasMore"] = !string.IsNullOrEmpty(NextCursor)
                };
                if (string.IsNullOrEmpty(NextCursor)) entry["nextCursor"] = null;
                else entry["nextCursor"] = NextCursor;
                if (!string.IsNullOrEmpty(Detail)) entry["detail"] = Detail;

                // Issue #377. A cut that drops part of the Worker's own page cannot be
                // resumed with that page's cursor: the cursor points past the whole page,
                // so the items trimmed off it are unreachable in this call. Rather than
                // hand out a cursor that skips them - which is how a caller following
                // every returned cursor silently lost results - the cut is reported as
                // what it is and no cursor is offered.
                if (DroppedByBudget > 0)
                {
                    bool stranded = BudgetCutStrandedPageRemainder;
                    entry["droppedByBudget"] = DroppedByBudget;
                    entry["budgetCut"] = new JObject
                    {
                        ["dropped"] = DroppedByBudget,
                        // True when the remainder of this page is unreachable, as opposed
                        // to merely not being fetched yet.
                        ["resumeSkipsPageRemainder"] = stranded,
                        ["reason"] = "The federation-wide maxTotalResults budget was reached before this KB's page was fully returned. "
                            + (stranded
                                ? "This KB has more pages; the dropped items belong to the page just returned and cannot be fetched with its nextCursor. Repeat with a higher maxTotalResults to collect them."
                                : "The dropped items are the end of this KB's result set.")
                    };
                }

                var results = Payload?["results"] as JArray ?? new JArray();
                entry["results"] = results.Count <= truncatedTo
                    ? new JArray(results)
                    : new JArray(results.Take(truncatedTo));

                if (Complete && results.Count > truncatedTo)
                    entry["resultsTruncated"] = true;

                if (!string.Equals(Status, StatusOk, StringComparison.Ordinal))
                {
                    entry["error"] = new JObject
                    {
                        ["code"] = Status,
                        ["message"] = Detail ?? MultiKbDiscovery.DescribeStatus(Status)
                    };
                }
                return entry;
            }
        }

        internal static string DescribeStatus(string status) => status switch
        {
            StatusNotOpen => "The KB is declared but has no open Worker. Discovery never opens a KB implicitly, because the Worker pool has finite capacity and opening one can evict the KB you are working in. Open it with genexus_kb action=open and repeat.",
            StatusUnknownAlias => "The alias is not a declared or open KB. This is reported per alias so the rest of the selection still runs.",
            StatusWarming => "The KB's Worker is still starting; its SDK was not ready. Repeat once genexus_whoami reports it ready.",
            StatusError => "The KB's search failed. Its own error is reported under error; other KBs in the selection are unaffected.",
            StatusTimeout => "This KB did not answer within the per-KB deadline. It may still be searchable on its own.",
            StatusBudgetExceeded => "The federation-wide result budget was reached before this KB was read in full. Its partial results are present; any nextCursor resumes only whole pages the Worker can still serve.",
            StatusCanceled => "The call was cancelled before this KB completed. Other KBs' results are still valid.",
            _ => "No matches in this KB (search completed)."
        };

        /// <summary>
        /// Runs the per-KB work with a hard concurrency cap and a shared deadline.
        /// The dispatcher is injected so the whole fan-out — including the
        /// interesting cases: one KB slow, one throwing, one cancelled — is
        /// testable without a Worker, and so the ordering guarantee ("one failure
        /// never erases another KB's results") is asserted rather than assumed.
        /// </summary>
        internal static async Task<List<Outcome>> RunAsync(
            List<Target> plan,
            Func<Target, CancellationToken, Task<Outcome>> dispatch,
            int concurrency,
            TimeSpan perKbTimeout,
            CancellationToken cancellationToken)
        {
            var gate = new SemaphoreSlim(Math.Max(1, concurrency));
            var tasks = new List<Task<Outcome>>(plan.Count);

            foreach (var target in plan)
            {
                // No token on Task.Run, and the gate wait inside the try: a KB that is
                // still queued when the caller cancels must settle as a canceled
                // outcome. Letting that cancellation escape would make Task.WhenAll
                // throw and erase the results of the KBs that already finished.
                tasks.Add(Task.Run(async () =>
                {
                    bool entered = false;
                    try
                    {
                        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        entered = true;
                        // SemaphoreSlim can still hand the slot to a waiter whose token
                        // was cancelled while it queued; do not dispatch it.
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!string.Equals(target.Status, StatusOk, StringComparison.Ordinal))
                        {
                            return new Outcome
                            {
                                Alias = target.Alias,
                                Status = target.Status,
                                Detail = string.IsNullOrEmpty(target.Detail) ? DescribeStatus(target.Status) : target.Detail,
                                // Nothing was searched, so nothing is known: claiming
                                // Complete here would turn a skipped KB into a
                                // trustworthy-looking empty result.
                                Complete = false
                            };
                        }

                        using var perKb = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        perKb.CancelAfter(perKbTimeout);
                        var sw = Stopwatch.StartNew();
                        Outcome outcome = await dispatch(target, perKb.Token).ConfigureAwait(false);
                        sw.Stop();
                        outcome.Alias = target.Alias;
                        outcome.ElapsedMs = (int)sw.ElapsedMilliseconds;
                        return outcome;
                    }
                    catch (OperationCanceledException)
                    {
                        return new Outcome
                        {
                            Alias = target.Alias,
                            Status = cancellationToken.IsCancellationRequested ? StatusCanceled : StatusTimeout,
                            Detail = cancellationToken.IsCancellationRequested
                                ? DescribeStatus(StatusCanceled)
                                : $"Exceeded the {perKbTimeout.TotalMilliseconds:0}ms per-KB deadline.",
                            Complete = false
                        };
                    }
                    catch (Exception ex)
                    {
                        return new Outcome
                        {
                            Alias = target.Alias,
                            Status = StatusError,
                            // The exception type is the useful part; the message may
                            // embed a path, so it is not echoed.
                            Detail = ex.GetType().Name,
                            Complete = false
                        };
                    }
                    finally
                    {
                        if (entered) gate.Release();
                    }
                }));
            }

            var settled = await Task.WhenAll(tasks).ConfigureAwait(false);
            var outcomes = new List<Outcome>(settled);
            return outcomes;
        }

        /// <summary>
        /// Builds the response envelope. <paramref name="maxTotalResults"/> is a
        /// real truncation, so it is reported as such: results past the budget are
        /// dropped per KB and those KBs are marked incomplete with their cursor
        /// intact, rather than the answer looking complete and quietly short.
        /// </summary>
        internal static JObject BuildEnvelope(
            List<string> requested,
            List<Outcome> outcomes,
            int maxTotalResults,
            int concurrency,
            TimeSpan perKbTimeout,
            long elapsedMs,
            bool cancelled)
        {
            var ordered = new List<Outcome>(outcomes.Count);
            foreach (var alias in requested)
            {
                var match = outcomes.FirstOrDefault(o => string.Equals(o.Alias, alias, StringComparison.OrdinalIgnoreCase));
                ordered.Add(match ?? new Outcome { Alias = alias, Status = StatusCanceled, Detail = DescribeStatus(StatusCanceled), Complete = false });
            }

            int remaining = Math.Max(0, maxTotalResults);
            var entries = new JArray();
            var searched = new JArray();
            var incomplete = new JArray();
            int matchedTotal = 0;
            bool anyTruncated = false;

            foreach (var outcome in ordered)
            {
                int allowance = outcome.Searched ? remaining : 0;
                if (outcome.Searched)
                {
                    searched.Add(outcome.Alias);
                    if (outcome.Returned > allowance)
                    {
                        outcome.DroppedByBudget = outcome.Returned - allowance;
                        outcome.Returned = allowance;
                        // A budget cut makes this KB's answer partial, whatever the
                        // Worker said about completeness.
                        outcome.Complete = false;
                        outcome.Status = StatusBudgetExceeded;
                        outcome.Detail = DescribeStatus(StatusBudgetExceeded);
                        anyTruncated = true;

                        // Issue #377. The Worker's cursor is positioned after the whole
                        // page it returned, so the items trimmed off this page are not
                        // reachable through it. Offering it would look resumable while
                        // skipping results, which is worse than saying so.
                        if (outcome.DroppedByBudget > 0 && !string.IsNullOrEmpty(outcome.NextCursor))
                        {
                            outcome.BudgetCutStrandedPageRemainder = true;
                            outcome.NextCursor = null;
                        }
                    }
                }
                if (!outcome.Complete) incomplete.Add(outcome.Alias);
                matchedTotal += outcome.Returned;
                remaining -= Math.Min(remaining, outcome.Returned);
                entries.Add(outcome.ToEntry(allowance));
            }

            bool complete = incomplete.Count == 0 && !cancelled;
            return new JObject
            {
                ["results"] = entries,
                ["coverage"] = new JObject
                {
                    ["requested"] = new JArray(requested),
                    ["searched"] = searched,
                    ["incomplete"] = incomplete,
                    ["complete"] = complete,
                    ["matchedTotal"] = matchedTotal,
                    ["cancelled"] = cancelled,
                    ["elapsedMs"] = elapsedMs,
                    // Plain-language guard against reading an incomplete answer as
                    // a negative result.
                    ["note"] = complete
                        ? "Every requested KB was searched to completion."
                        : "This answer is incomplete. Results are present only for the KBs listed in `searched`; an entry with status != ok or complete=false is NOT evidence of zero matches."
                },
                ["budget"] = new JObject
                {
                    ["maxTotalResults"] = maxTotalResults,
                    ["maxConcurrency"] = concurrency,
                    ["perKbTimeoutMs"] = (long)perKbTimeout.TotalMilliseconds,
                    ["truncated"] = anyTruncated
                },
                // Federated results are per-KB identities; a flat name is
                // meaningless across KBs, so there is deliberately no `count`.
                ["federated"] = true
            };
        }
    }
}