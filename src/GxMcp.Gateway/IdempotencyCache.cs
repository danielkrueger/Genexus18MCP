using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    public sealed class IdempotencyCache
    {
        private readonly TimeSpan _gateAcquisitionTimeout;

        private readonly TimeSpan _ttl;
        private readonly int _capacity;
        private readonly MutationOperationJournal? _journal;
        private readonly OperationalStateKey? _owner;
        private readonly ConcurrentDictionary<string, KbBucket> _buckets = new ConcurrentDictionary<string, KbBucket>();
        private readonly object _gateLock = new object();
        private readonly Dictionary<(string, string, string), GateEntry> _gates =
            new Dictionary<(string, string, string), GateEntry>();

        public IdempotencyCache(int ttlMinutes, int capacity)
            : this(ttlMinutes, capacity, TimeSpan.FromSeconds(30), null) { }

        internal IdempotencyCache(int ttlMinutes, int capacity, TimeSpan gateAcquisitionTimeout)
            : this(ttlMinutes, capacity, gateAcquisitionTimeout, null) { }

        internal IdempotencyCache(int ttlMinutes, int capacity, TimeSpan gateAcquisitionTimeout, string? journalPath)
            : this(ttlMinutes, capacity, gateAcquisitionTimeout, journalPath, null) { }

        internal IdempotencyCache(int ttlMinutes, int capacity, TimeSpan gateAcquisitionTimeout,
            StateScope scope, string kbId, long generation)
            : this(ttlMinutes, capacity, gateAcquisitionTimeout,
                scope.JournalPath(kbId, generation, "mutation-operations.json"),
                scope.ForKb(kbId, generation)) { }

        private IdempotencyCache(int ttlMinutes, int capacity, TimeSpan gateAcquisitionTimeout,
            string? journalPath, OperationalStateKey? owner)
        {
            _gateAcquisitionTimeout = gateAcquisitionTimeout;
            _ttl = TimeSpan.FromMinutes(ttlMinutes);
            _capacity = capacity;
            _owner = owner;
            _journal = string.IsNullOrWhiteSpace(journalPath)
                ? null
                : owner.HasValue ? new MutationOperationJournal(journalPath, owner.Value) : new MutationOperationJournal(journalPath);
        }

        // Plan 028: test-only visibility into gate accumulation (InternalsVisibleTo
        // GxMcp.Gateway.Tests is already configured in the csproj).
        internal int GateCount { get { lock (_gateLock) return _gates.Count; } }

        internal JObject InspectOperation(string kbPath, string tool, string key)
        {
            if (_journal == null)
            {
                return new JObject
                {
                    ["journalHealthy"] = false,
                    ["code"] = "operation_journal_disabled",
                    ["message"] = "This Gateway instance has no durable operation journal configured."
                };
            }
            return _journal.Inspect(kbPath, tool, key);
        }

        internal JObject ReconcileOperation(
            string kbPath,
            string tool,
            string key,
            string verification,
            MutationOperationEvidence? observedEvidence = null)
        {
            if (_journal == null)
            {
                return new JObject
                {
                    ["status"] = "Blocked",
                    ["code"] = "operation_journal_disabled",
                    ["message"] = "This Gateway instance has no durable operation journal configured; no state changed."
                };
            }
            return _journal.Reconcile(kbPath, tool, key, verification, observedEvidence);
        }

        public bool TryGet(string kbPath, string tool, string key,
                           string payloadHash, out JObject? cached)
        {
            cached = null;
            var bucket = _buckets.GetOrAdd(kbPath, _ => new KbBucket(_capacity, _ttl));
            return bucket.TryGet(tool, key, payloadHash, out cached);
        }

        internal bool TryGet(StateScopeId stateScopeId, string kbId, long generation,
                             string tool, string key, string payloadHash, out JObject? cached)
            => TryGet(ScopedIdentity(stateScopeId, kbId, generation), tool, key, payloadHash, out cached);

        public void Put(string kbPath, string tool, string key,
                        string payloadHash, JObject result)
        {
            var bucket = _buckets.GetOrAdd(kbPath, _ => new KbBucket(_capacity, _ttl));
            bucket.Put(tool, key, payloadHash, result);
        }

        internal void Put(StateScopeId stateScopeId, string kbId, long generation,
                          string tool, string key, string payloadHash, JObject result)
            => Put(ScopedIdentity(stateScopeId, kbId, generation), tool, key, payloadHash, result);

        public async Task<JObject> GetOrCompute(
            string kbPath, string tool, string key, string payloadHash,
            Func<Task<JObject>> factory,
            MutationOperationEvidence? evidence = null)
        {
            if (TryGet(kbPath, tool, key, payloadHash, out var cached))
                return cached!;

            var gateKey = (kbPath, tool, key);
            GateEntry gate;
            lock (_gateLock)
            {
                if (!_gates.TryGetValue(gateKey, out gate!))
                    _gates.Add(gateKey, gate = new GateEntry());
                gate.Users++;
            }

            bool gateAcquired = false;
            try
            {
                gateAcquired = await gate.Semaphore.WaitAsync(_gateAcquisitionTimeout).ConfigureAwait(false);
                if (!gateAcquired)
                    throw new UsageException("idempotency_in_progress",
                        "An operation with this idempotency key is still in progress. " +
                        "This request was not executed; retry with the same key and payload to retrieve its result.");

                if (TryGet(kbPath, tool, key, payloadHash, out cached))
                    return cached!;
                if (_journal != null)
                {
                    switch (BeginJournal(kbPath, tool, key, payloadHash, evidence))
                    {
                        case MutationOperationJournal.BeginResult.Conflict:
                            throw new IdempotencyConflictException(
                                $"idempotency key '{key}' reused with different payload");
                        case MutationOperationJournal.BeginResult.Completed:
                        case MutationOperationJournal.BeginResult.UnknownAfterRestart:
                            throw new UsageException(
                                "operation_unknown",
                                "A previous process may have committed this mutation, but its response is unavailable. " +
                                "Inspect the affected target before retrying with a new authorization.");
                        case MutationOperationJournal.BeginResult.JournalUnavailable:
                            throw new UsageException(
                                "operation_journal_unavailable",
                                "The durable mutation journal is unavailable or corrupt; this write was not executed. " +
                                "Repair the Gateway state journal and inspect the target before retrying.");
                    }
                }
                try
                {
                    var result = await factory().ConfigureAwait(false);
                    Put(kbPath, tool, key, payloadHash, result);
                    CompleteJournal(kbPath, tool, key, payloadHash);
                    return result;
                }
                catch (ErrorNotCacheable ex)
                {
                    FailJournal(kbPath, tool, key, payloadHash);
                    return ex.Result;
                }
                catch (Exception ex) when (PreDispatchFailureClassifier.Is(ex))
                {
                    // The mutation provably never reached the SDK, so the 'started'
                    // fence is describing a write that did not happen. Clear it;
                    // leaving it poisons this key for the life of the process with a
                    // false operation_unknown. The contrast is named in
                    // PreDispatchFailureClassifier: a failure raised AFTER dispatch
                    // deliberately keeps the fence, because "may have committed" is
                    // then the truth.
                    FailJournal(kbPath, tool, key, payloadHash);
                    // The caller still sees the original failure. This branch only
                    // corrects the durable journal state, never the response.
                    throw;
                }
            }
            finally
            {
                if (gateAcquired) gate.Semaphore.Release();
                // Retain one shared gate while any owner or waiter can still use it.
                // Registration and last-user eviction must be atomic with each other.
                lock (_gateLock)
                {
                    if (--gate.Users == 0)
                    {
                        _gates.Remove(gateKey);
                        gate.Semaphore.Dispose();
                    }
                }
            }
        }

        internal Task<JObject> GetOrCompute(
            StateScopeId stateScopeId, string kbId, long generation,
            string tool, string key, string payloadHash,
            Func<Task<JObject>> factory,
            MutationOperationEvidence? evidence = null)
            => GetOrCompute(ScopedIdentity(stateScopeId, kbId, generation), tool, key, payloadHash, factory, evidence);

        private MutationOperationJournal.BeginResult BeginJournal(string kbPath, string tool, string key,
            string payloadHash, MutationOperationEvidence? evidence)
            => _owner.HasValue
                ? _journal!.Begin(_owner.Value, tool, key, payloadHash, evidence)
                : _journal!.Begin(kbPath, tool, key, payloadHash, evidence);

        private void CompleteJournal(string kbPath, string tool, string key, string payloadHash)
        {
            if (_journal == null) return;
            if (_owner.HasValue) _journal.Complete(_owner.Value, tool, key, payloadHash);
            else _journal.Complete(kbPath, tool, key, payloadHash);
        }

        private void FailJournal(string kbPath, string tool, string key, string payloadHash)
        {
            if (_journal == null) return;
            if (_owner.HasValue) _journal.Fail(_owner.Value, tool, key, payloadHash);
            else _journal.Fail(kbPath, tool, key, payloadHash);
        }

        private static string ScopedIdentity(StateScopeId stateScopeId, string kbId, long generation)
            => StateScopedCacheKey.Create(stateScopeId, kbId, generation, "idempotency").ToString();

        private sealed class GateEntry
        {
            public readonly SemaphoreSlim Semaphore = new SemaphoreSlim(1, 1);
            public int Users;
        }

        // PERFORMANCE (G-M4): KbBucket now shards its state across N independent LRU slots.
        // Each shard has its own lock, dictionary, linked-list, and capacity slice. Strict
        // global LRU becomes per-shard LRU, which is acceptable for an idempotency cache
        // (semantics: "don't re-run the same key twice in the TTL window"). Hot-key contention
        // drops by 1/N because two threads hitting different shards never block each other.
        private sealed class KbBucket
        {
            private const int ShardCount = 16; // power of two for cheap hash masking
            private readonly Shard[] _shards;
            private readonly TimeSpan _ttl;

            public KbBucket(int capacity, TimeSpan ttl)
            {
                _ttl = ttl;
                _shards = new Shard[ShardCount];
                int perShard = Math.Max(1, (capacity + ShardCount - 1) / ShardCount);
                for (int i = 0; i < ShardCount; i++) _shards[i] = new Shard(perShard, ttl);
            }

            private Shard PickShard(string tool, string key)
            {
                // Stable, low-overhead hash; deterministic across processes is not required.
                int h = unchecked((tool?.GetHashCode() ?? 0) * 397) ^ (key?.GetHashCode() ?? 0);
                return _shards[(h & int.MaxValue) % ShardCount];
            }

            public bool TryGet(string tool, string key, string payloadHash, out JObject? cached)
                => PickShard(tool, key).TryGet(tool, key, payloadHash, out cached);

            public void Put(string tool, string key, string payloadHash, JObject result)
                => PickShard(tool, key).Put(tool, key, payloadHash, result);

            private sealed class Shard
            {
                private readonly int _capacity;
                private readonly TimeSpan _ttl;
                private readonly LinkedList<(string Tool, string Key)> _lru = new LinkedList<(string Tool, string Key)>();
                private readonly Dictionary<(string, string), Entry> _map = new Dictionary<(string, string), Entry>();
                private readonly object _lock = new object();

                public Shard(int capacity, TimeSpan ttl) { _capacity = capacity; _ttl = ttl; }

                public bool TryGet(string tool, string key, string payloadHash, out JObject? cached)
                {
                    cached = null;
                    lock (_lock)
                    {
                        if (!_map.TryGetValue((tool, key), out var entry)) return false;
                        if (DateTime.UtcNow - entry.LastAccessedAt > _ttl)
                        {
                            _map.Remove((tool, key));
                            _lru.Remove(entry.Node);
                            return false;
                        }
                        if (entry.PayloadHash != payloadHash)
                            throw new IdempotencyConflictException(
                                $"idempotency key '{key}' reused with different payload");
                        entry.LastAccessedAt = DateTime.UtcNow;
                        _lru.Remove(entry.Node);
                        _lru.AddFirst(entry.Node);
                        cached = entry.Result;
                        return true;
                    }
                }

                public void Put(string tool, string key, string payloadHash, JObject result)
                {
                    lock (_lock)
                    {
                        if (_map.TryGetValue((tool, key), out var existing))
                        {
                            _lru.Remove(existing.Node);
                            _map.Remove((tool, key));
                        }
                        while (_map.Count >= _capacity)
                        {
                            var oldest = _lru.Last!;
                            _lru.RemoveLast();
                            _map.Remove(oldest.Value);
                        }
                        var node = new LinkedListNode<(string, string)>((tool, key));
                        _lru.AddFirst(node);
                        _map[(tool, key)] = new Entry
                        {
                            PayloadHash = payloadHash,
                            Result = result,
                            LastAccessedAt = DateTime.UtcNow,
                            Node = node
                        };
                    }
                }
            }

            private sealed class Entry
            {
                public string PayloadHash = "";
                public JObject Result = new JObject();
                public DateTime LastAccessedAt;
                public LinkedListNode<(string, string)> Node = null!;
            }
        }
    }

    /// <summary>
    /// Decides whether an exception escaping a keyed mutation factory means the
    /// mutation <em>provably did not run</em>, so the durable journal's
    /// <c>started</c> fence should be cleared.
    ///
    /// <para>
    /// <see cref="IdempotencyCache.GetOrCompute"/> writes a <c>started</c> record
    /// before running the factory and only clears it on <c>Complete</c> or on
    /// <see cref="ErrorNotCacheable"/>. Any other exception used to escape with
    /// the record still <c>started</c>, and the next attempt under the same key
    /// then mapped it to <c>UnknownAfterRestart</c> — reported to the caller as
    /// <c>operation_unknown</c>, "a previous process may have committed this
    /// mutation".
    /// </para>
    ///
    /// <para>
    /// That fence is correct for a genuinely unknown outcome and a lie for a
    /// known one. A pool that is full, an unresolvable KB, or a rejected argument
    /// never reaches the SDK, so no write happened and the key should stay usable.
    /// Membership here is deliberately narrow: a too-broad predicate deletes the
    /// fence for a write that may have committed, which is strictly worse than
    /// the false <c>operation_unknown</c> this fixes.
    /// </para>
    ///
    /// <para>
    /// This is a pure function of the exception type, kept as a named predicate so
    /// it can be exercised without a live Worker — the same reason
    /// <see cref="WorkerLivenessClassifier"/> is its own type.
    /// </para>
    /// </summary>
    internal static class PreDispatchFailureClassifier
    {
        /// <summary>
        /// True when <paramref name="error"/> means the keyed mutation provably
        /// never reached a worker.
        /// </summary>
        /// <remarks>
        /// Membership is derived from the exception hierarchy and the call sites,
        /// not assumed. Add a new exception type here only when it can be raised
        /// <em>before</em> a worker command is written to the pipe, and say so in
        /// the addition — a later author needs to know this list exists.
        /// </remarks>
        internal static bool Is(Exception? error)
            => error is UsageException
                // Gateway-side only: GxMcp.Worker has no reference to GxMcp.Gateway
                // and declares its own UsageException, so worker-side usage errors
                // cross the pipe as JSON error envelopes and can never match here.
                // Every Gateway-side UsageException is raised by validation, KB
                // context, or journal state before dispatch.
                || error is KbResolutionException
                // A sibling, not a UsageException: declared in KbResolver.cs and
                // thrown while choosing the KB context, which is definitionally
                // before a command exists.
                || error is WorkerPoolFullException;

        // Deliberately NOT classified as pre-dispatch:
        //
        //   * OperationCanceledException / TaskCanceledException — a cancellation
        //     from RequestAborted can arrive either side of dispatch and is
        //     indistinguishable here, so a cancellation observed after the command
        //     was written leaves a genuinely unknown outcome. Keeping the fence is
        //     the safe direction.
        //   * IdempotencyConflictException — a payload-conflict signal, not a
        //     dispatch-position signal; it is raised on both sides of the factory.
        //   * ArgumentException and every other framework exception — the BCL
        //     raises these from argument marshalling, which happens after the
        //     command is written. Too broad to classify as pre-dispatch.
        //   * Anything thrown once the command has been handed to a worker. If the
        //     outcome is unknown, the fence must stay 'started'.
    }
}
