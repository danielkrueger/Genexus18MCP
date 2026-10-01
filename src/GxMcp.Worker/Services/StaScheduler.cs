using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public enum CommandPriority
    {
        P0_Interactive = 0,
        P1_Normal = 1,
        P2_Background = 2
    }

    public sealed class ScheduledCommandItem
    {
        public JObject Obj { get; set; }
        public string RawLine { get; set; }
        public CommandPriority Priority { get; set; }
        public string ClientId { get; set; }
        public DateTime EnqueuedAtUtc { get; set; }
        public int BusyWaitMs { get; set; }
        public string IdJson { get; set; }
        public string Method { get; set; }
        public string Action { get; set; }
    }

    public sealed class StaScheduler
    {
        private static readonly Lazy<StaScheduler> _instance =
            new Lazy<StaScheduler>(() => new StaScheduler(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static StaScheduler Instance => _instance.Value;

        private readonly object _lock = new object();

        // ── Issue #341: bounded admission ───────────────────────────────────────
        //
        // The queues were unbounded and the wait was strict-priority: P0 was always
        // checked before P1, and P1 before P2. Two consequences. A burst could
        // accumulate an arbitrary number of retained requests and their payloads with
        // nothing to stop it, and a sustained P0 stream could defer accepted P1/P2 work
        // indefinitely - work the caller was already told had been accepted.
        //
        // Admission is now bounded by both count and retained payload bytes, and the
        // refusal happens before the item is queued, so a rejected command cannot
        // execute later. Service is still priority-first for responsiveness, but a
        // lower-priority item that has waited past the aging window is promoted, which
        // bounds its wait without giving up interactive latency for the common case.
        internal const int MaxQueuedCommands = 512;
        internal const long MaxQueuedBytes = 32L * 1024 * 1024;
        internal static readonly TimeSpan PriorityAgingWindow = TimeSpan.FromSeconds(5);

        private int _queuedCount;
        private long _queuedBytes;

        /// <summary>
        /// Why an admission was refused, so the caller can answer with something
        /// actionable. Null when the item was accepted.
        /// </summary>
        internal string LastAdmissionError { get; private set; }

        /// <summary>Commands currently retained across all three queues.</summary>
        public int QueuedCount { get { lock (_lock) return _queuedCount; } }

        /// <summary>Retained payload bytes currently held across all three queues.</summary>
        public long QueuedBytes { get { lock (_lock) return _queuedBytes; } }

        // Queues per priority, mapped by ClientId for fair round-robin scheduling
        private readonly Dictionary<string, Queue<ScheduledCommandItem>> _p0Queues =
            new Dictionary<string, Queue<ScheduledCommandItem>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _p0ClientOrder = new List<string>();
        private int _p0Cursor;

        private readonly Dictionary<string, Queue<ScheduledCommandItem>> _p1Queues =
            new Dictionary<string, Queue<ScheduledCommandItem>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _p1ClientOrder = new List<string>();
        private int _p1Cursor;

        private readonly Dictionary<string, Queue<ScheduledCommandItem>> _p2Queues =
            new Dictionary<string, Queue<ScheduledCommandItem>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _p2ClientOrder = new List<string>();
        private int _p2Cursor;

        public static CommandPriority ResolvePriority(JObject obj, string line = null)
        {
            if (obj == null && !string.IsNullOrEmpty(line))
            {
                try { obj = GxMcp.Common.JsonIngress.ParseObject(line); } catch { }
            }
            if (obj == null) return CommandPriority.P1_Normal;

            // 1. Explicit priority override
            string explicitPrio = obj["_meta"]?["priority"]?.ToString() ?? obj["params"]?["priority"]?.ToString();
            if (!string.IsNullOrEmpty(explicitPrio))
            {
                if (string.Equals(explicitPrio, "p0", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(explicitPrio, "interactive", StringComparison.OrdinalIgnoreCase))
                    return CommandPriority.P0_Interactive;
                if (string.Equals(explicitPrio, "p2", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(explicitPrio, "background", StringComparison.OrdinalIgnoreCase))
                    return CommandPriority.P2_Background;
                return CommandPriority.P1_Normal;
            }

            string method = obj["method"]?.ToString()?.ToLowerInvariant() ?? "";
            string action = (obj["action"]?.ToString() ?? obj["params"]?["action"]?.ToString())?.ToLowerInvariant() ?? "";

            // P0: Exact reads, inspect, properties get, lightweight status/whoami
            if (method == "object")
            {
                if (action == "extractsource" || action == "extractparts" ||
                    action == "extractfullobject" || action == "getvariables" ||
                    action == "getattribute")
                    return CommandPriority.P0_Interactive;
            }
            if (method == "inspect" || method == "whoami")
                return CommandPriority.P0_Interactive;
            if (method == "properties" && (action == "get" || string.IsNullOrEmpty(action)))
                return CommandPriority.P0_Interactive;
            if (method == "kb" && (action == "list" || action == "getindexstatus" || action == "getindexstate"))
                return CommandPriority.P0_Interactive;
            if (method == "history" && (action == "list" || action == "read"))
                return CommandPriority.P0_Interactive;
            if (method == "structure" && (action == "getlogicstructure" || action == "getvisualstructure"))
                return CommandPriority.P0_Interactive;

            // P2: Background scans, unscoped source search, doc generation
            if (method == "search" && action == "searchsource")
                return CommandPriority.P2_Background;
            if (method == "doc")
                return CommandPriority.P2_Background;

            // P1: Writes, compilations, validations, refactors, creates
            return CommandPriority.P1_Normal;
        }

        public static int ResolveBusyWaitMs(JObject obj)
        {
            if (obj != null)
            {
                var val = obj["params"]?["busyWaitMs"] ?? obj["_meta"]?["busyWaitMs"];
                if (val != null && int.TryParse(val.ToString(), out int parsed) && parsed > 0)
                    return parsed;
            }
            var env = Environment.GetEnvironmentVariable("GXMCP_BUSY_WAIT_MS");
            if (int.TryParse(env, out int envParsed) && envParsed > 0)
                return envParsed;

            // Default bounded wait: 15 seconds
            return 15000;
        }

        public static string ResolveClientId(JObject obj)
        {
            if (obj == null) return "default";
            string id = obj["_meta"]?["clientId"]?.ToString()
                ?? obj["_meta"]?["attachmentId"]?.ToString()
                ?? obj["_meta"]?["sessionId"]?.ToString()
                ?? obj["params"]?["clientId"]?.ToString();
            return string.IsNullOrEmpty(id) ? "default" : id;
        }

        /// <summary>
        /// Queues <paramref name="item"/>, or refuses it and records why.
        ///
        /// <para>
        /// Issue #341. The refusal is decided and applied inside the same critical
        /// section as the enqueue, so there is no window in which a command is reported
        /// as rejected but still ends up in a queue - which is the one outcome a caller
        /// cannot recover from, because it would later execute work it believes was
        /// cancelled.
        /// </para>
        /// </summary>
        /// <returns>true when accepted.</returns>
        public bool TryEnqueue(ScheduledCommandItem item)
        {
            if (item == null) return false;
            string client = string.IsNullOrEmpty(item.ClientId) ? "default" : item.ClientId;
            long bytes = EstimateBytes(item);

            lock (_lock)
            {
                if (_queuedCount >= MaxQueuedCommands)
                {
                    LastAdmissionError = "queue_full";
                    return false;
                }
                // A single item larger than the whole budget is refused on its own
                // merits, not because the budget is nearly spent: admitting it would
                // evict the entire backlog for one request.
                if (bytes > MaxQueuedBytes)
                {
                    LastAdmissionError = "payload_too_large";
                    return false;
                }
                if (_queuedBytes + bytes > MaxQueuedBytes)
                {
                    LastAdmissionError = "queue_bytes_exhausted";
                    return false;
                }

                switch (item.Priority)
                {
                    case CommandPriority.P0_Interactive:
                        EnqueueInternal(_p0Queues, _p0ClientOrder, client, item);
                        break;
                    case CommandPriority.P1_Normal:
                        EnqueueInternal(_p1Queues, _p1ClientOrder, client, item);
                        break;
                    case CommandPriority.P2_Background:
                        EnqueueInternal(_p2Queues, _p2ClientOrder, client, item);
                        break;
                    default:
                        LastAdmissionError = "unknown_priority";
                        return false;
                }
                _queuedCount++;
                _queuedBytes += bytes;
                LastAdmissionError = null;
                return true;
            }
        }

        /// <summary>
        /// Retained bytes attributed to a queued item. The raw command line is the
        /// payload that actually occupies memory; <see cref="ScheduledCommandItem.IdJson"/>
        /// is a subset of it.
        /// </summary>
        private static long EstimateBytes(ScheduledCommandItem item)
        {
            long bytes = 0;
            if (item?.IdJson != null) bytes += (item.IdJson.Length + 1) * 2L; // UTF-16 chars
            if (item?.Method != null) bytes += (item.Method.Length + 1) * 2L;
            if (item?.Action != null) bytes += (item.Action.Length + 1) * 2L;
            return bytes;
        }

        /// <summary>
        /// Queues <paramref name="item"/> unconditionally. Retained for the admission
        /// paths that have already bounded their own submission (and for tests); the
        /// effective SDK admission path uses <see cref="TryEnqueue"/>, because this
        /// method cannot refuse and therefore cannot report a rejection the caller would
        /// act on.
        /// </summary>
        public void Enqueue(ScheduledCommandItem item)
        {
            TryEnqueue(item);
        }

        private static void EnqueueInternal(Dictionary<string, Queue<ScheduledCommandItem>> dict,
            List<string> order, string client, ScheduledCommandItem item)
        {
            if (!dict.TryGetValue(client, out var q))
            {
                q = new Queue<ScheduledCommandItem>();
                dict[client] = q;
                order.Add(client);
            }
            q.Enqueue(item);
        }

        public bool HasPendingInteractive
        {
            get
            {
                lock (_lock)
                {
                    return _p0Queues.Values.Any(q => q.Count > 0);
                }
            }
        }

        /// <summary>
        /// Takes the next command.
        ///
        /// <para>
        /// Issue #341. Strict priority is retained, because a read must not queue behind
        /// a bulk search. What changes is that it is no longer absolute: a lower-priority
        /// item that has waited longer than <see cref="PriorityAgingWindow"/> outranks a
        /// freshly-arrived higher-priority one. Without that, a sustained P0 stream
        /// deferred accepted P1/P2 work forever, and a caller told "accepted" has no
        /// signal that will let it recover.
        /// </para>
        /// </summary>
        public bool TryTakeNext(out ScheduledCommandItem item)
        {
            lock (_lock)
            {
                item = null;
                if (TryTakeAged(out item)) return true;
                if (TryTakeRoundRobin(_p0Queues, _p0ClientOrder, ref _p0Cursor, out item)) return true;
                if (TryTakeRoundRobin(_p1Queues, _p1ClientOrder, ref _p1Cursor, out item)) return true;
                if (TryTakeRoundRobin(_p2Queues, _p2ClientOrder, ref _p2Cursor, out item)) return true;

                item = null;
                return false;
            }
        }

        /// <summary>
        /// Whether any client in this priority has an item that has waited past the
        /// aging window. Each per-client queue is FIFO, so its head is the oldest.
        ///
        /// <para>
        /// A default timestamp is not treated as a long wait. It is missing data, and
        /// reading it as "waited since the beginning of time" would promote an item
        /// whose actual age nobody knows. Items that reach this path from the real
        /// admission point always carry a timestamp; the guard matters for the
        /// constructed items tests and any future caller that forgets to set one.
        /// </para>
        /// </summary>
        private static bool QueueIsAged(Dictionary<string, Queue<ScheduledCommandItem>> dict, DateTime nowUtc)
        {
            foreach (var q in dict.Values)
            {
                if (q.Count == 0) continue;
                var head = q.Peek();
                if (head.EnqueuedAtUtc == default(DateTime)) continue;
                if (nowUtc - head.EnqueuedAtUtc >= PriorityAgingWindow) return true;
            }
            return false;
        }

        /// <summary>
        /// Takes a lower-priority item that has aged out, so a continuous higher-priority
        /// stream cannot starve it. Aged work is taken before fresh P0 - that is the whole
        /// point - but only once it has actually waited, so the interactive fast path is
        /// unaffected for the common case.
        /// </summary>
        private bool TryTakeAged(out ScheduledCommandItem item)
        {
            item = null;
            var now = DateTime.UtcNow;
            if (QueueIsAged(_p1Queues, now)
                && TryTakeRoundRobin(_p1Queues, _p1ClientOrder, ref _p1Cursor, out item)) return true;
            if (QueueIsAged(_p2Queues, now)
                && TryTakeRoundRobin(_p2Queues, _p2ClientOrder, ref _p2Cursor, out item)) return true;
            item = null;
            return false;
        }

        public bool TryTakeInteractive(out ScheduledCommandItem item)
        {
            lock (_lock)
            {
                return TryTakeRoundRobin(_p0Queues, _p0ClientOrder, ref _p0Cursor, out item);
            }
        }

        public int DrainPendingInteractive(Action<ScheduledCommandItem> processor)
        {
            if (processor == null) return 0;
            int count = 0;
            while (TryTakeInteractive(out var item))
            {
                count++;
                processor(item);
            }
            return count;
        }

        public List<ScheduledCommandItem> ExpireTimedOut(DateTime nowUtc)
        {
            var expired = new List<ScheduledCommandItem>();
            lock (_lock)
            {
                ExpireFromQueue(_p0Queues, _p0ClientOrder, nowUtc, expired);
                ExpireFromQueue(_p1Queues, _p1ClientOrder, nowUtc, expired);
                ExpireFromQueue(_p2Queues, _p2ClientOrder, nowUtc, expired);
            }
            return expired;
        }

        private void ExpireFromQueue(Dictionary<string, Queue<ScheduledCommandItem>> dict,
            List<string> order, DateTime nowUtc, List<ScheduledCommandItem> expired)
        {
            foreach (var kvp in dict)
            {
                var q = kvp.Value;
                int count = q.Count;
                for (int i = 0; i < count; i++)
                {
                    var item = q.Dequeue();
                    double ageMs = (nowUtc - item.EnqueuedAtUtc).TotalMilliseconds;
                    if (item.BusyWaitMs > 0 && ageMs > item.BusyWaitMs)
                    {
                        // Issue #341: a timeout drop leaves the queue for good, so it
                        // releases its admission accounting. Re-enqueued items keep
                        // theirs, since they are still retained.
                        expired.Add(item);
                        ReleaseAccounting(item);
                    }
                    else
                    {
                        q.Enqueue(item);
                    }
                }
            }
        }

        private bool TryTakeRoundRobin(Dictionary<string, Queue<ScheduledCommandItem>> dict,
            List<string> order, ref int cursor, out ScheduledCommandItem item)
        {
            item = null;
            if (order.Count == 0) return false;

            int checkedCount = 0;
            while (checkedCount < order.Count)
            {
                if (cursor >= order.Count) cursor = 0;
                string client = order[cursor];
                cursor++;
                checkedCount++;

                if (dict.TryGetValue(client, out var q) && q.Count > 0)
                {
                    item = q.Dequeue();
                    // Issue #341: release the admission accounting as the item leaves
                    // the queue. Without this the counters only ever grow, the budget
                    // would be permanently exhausted, and the refusal would become
                    // permanent too - which is a self-inflicted denial of service.
                    ReleaseAccounting(item);
                    return true;
                }
            }
            return false;
        }

        private void ReleaseAccounting(ScheduledCommandItem item)
        {
            if (item == null) return;
            _queuedCount--;
            if (_queuedCount < 0) _queuedCount = 0;
            long bytes = EstimateBytes(item);
            _queuedBytes -= bytes;
            if (_queuedBytes < 0) _queuedBytes = 0;
        }

        public (int p0, int p1, int p2, int total) GetQueueDepths()
        {
            lock (_lock)
            {
                int p0 = _p0Queues.Values.Sum(q => q.Count);
                int p1 = _p1Queues.Values.Sum(q => q.Count);
                int p2 = _p2Queues.Values.Sum(q => q.Count);
                return (p0, p1, p2, p0 + p1 + p2);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _p0Queues.Clear();
                _p0ClientOrder.Clear();
                _p0Cursor = 0;
                _p1Queues.Clear();
                _p1ClientOrder.Clear();
                _p1Cursor = 0;
                _p2Queues.Clear();
                _p2ClientOrder.Clear();
                _p2Cursor = 0;
            }
        }
    }
}
