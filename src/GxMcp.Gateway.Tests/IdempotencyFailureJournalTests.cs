using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// The durable mutation journal writes a 'started' fence before the keyed
    /// factory runs. A failure raised before dispatch provably wrote nothing, so
    /// the fence must be cleared; a failure raised after dispatch leaves a
    /// genuinely unknown outcome and must keep it.
    ///
    /// <para>
    /// Every case asserts on the journal's persisted state, not only on the
    /// exception the caller sees — the defect is invisible from the return value.
    /// </para>
    /// </summary>
    public sealed class IdempotencyFailureJournalTests : IDisposable
    {
        private const string KbPath = @"C:\KB\Orders";
        private const string Tool = "genexus_edit";
        private const string Key = "op-key";
        private const string PayloadHash = "payload-hash";

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "gxmcp-idem-fail-" + Guid.NewGuid().ToString("N"));
        private readonly string _journalPath;

        public IdempotencyFailureJournalTests()
        {
            _journalPath = Path.Combine(_root, "mutation-operations.json");
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public async Task APoolFullFailureLeavesTheKeyReusable()
        {
            var cache = new IdempotencyCache(1, 8, TimeSpan.FromSeconds(1), _journalPath);

            var thrown = await Assert.ThrowsAsync<WorkerPoolFullException>(() => cache.GetOrCompute(
                KbPath, Tool, Key, PayloadHash, () => throw PoolFull()));

            Assert.NotNull(thrown);
            Assert.Equal("not_found", cache.InspectOperation(KbPath, Tool, Key)["status"]?.ToString());
            AssertNoStartedRecord(_journalPath);

            // The regression itself: the same key is usable again, so the caller is
            // not pushed into a manual journal repair over a write that never ran.
            int executions = 0;
            var result = await cache.GetOrCompute(KbPath, Tool, Key, PayloadHash, () =>
            {
                executions++;
                return Task.FromResult(new JObject { ["isError"] = false });
            });
            Assert.False((bool?)result["isError"]);
            Assert.Equal(1, executions);
        }

        [Fact]
        public async Task AKbResolutionFailureLeavesTheKeyReusable()
        {
            var cache = new IdempotencyCache(1, 8, TimeSpan.FromSeconds(1), _journalPath);

            // KbResolutionException is a sibling of UsageException (declared in
            // KbResolver.cs), not a subtype — the predicate lists it explicitly.
            var thrown = await Assert.ThrowsAsync<KbResolutionException>(() => cache.GetOrCompute(
                KbPath, Tool, Key, PayloadHash,
                () => throw new KbResolutionException("KB_CONTEXT_REQUIRED", "No Knowledge Base is open.")));

            Assert.Equal("KB_CONTEXT_REQUIRED", thrown.Code);
            Assert.Equal("not_found", cache.InspectOperation(KbPath, Tool, Key)["status"]?.ToString());
            AssertNoStartedRecord(_journalPath);

            int executions = 0;
            await cache.GetOrCompute(KbPath, Tool, Key, PayloadHash, () =>
            {
                executions++;
                return Task.FromResult(new JObject { ["isError"] = false });
            });
            Assert.Equal(1, executions);
        }

        [Fact]
        public async Task AUsageFailureLeavesTheKeyReusable()
        {
            var cache = new IdempotencyCache(1, 8, TimeSpan.FromSeconds(1), _journalPath);

            var thrown = await Assert.ThrowsAsync<UsageException>(() => cache.GetOrCompute(
                KbPath, Tool, Key, PayloadHash,
                () => throw new UsageException("usage_error", "idempotencyKey charset must be [A-Za-z0-9_-]")));

            Assert.Equal("usage_error", thrown.Code);
            AssertNoStartedRecord(_journalPath);
        }

        [Fact]
        public async Task AFailureAfterDispatchKeepsTheFenceAndStillReportsOperationUnknown()
        {
            var cache = new IdempotencyCache(1, 8, TimeSpan.FromSeconds(1), _journalPath);

            // Not in the pre-dispatch set: the mutation may have committed, so the
            // fence must survive. This is the guard against the fix over-reaching.
            await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrCompute(
                KbPath, Tool, Key, PayloadHash, () => throw new InvalidOperationException("worker pipe closed mid-command")));

            Assert.Equal("started", cache.InspectOperation(KbPath, Tool, Key)["status"]?.ToString());
            Assert.True(cache.InspectOperation(KbPath, Tool, Key)["recoveryRequired"]?.ToObject<bool>());
            AssertSingleStartedRecord(_journalPath);

            var retry = await Assert.ThrowsAsync<UsageException>(() => cache.GetOrCompute(
                KbPath, Tool, Key, PayloadHash, () => Task.FromResult(new JObject { ["isError"] = false })));

            Assert.Equal("operation_unknown", retry.Code);
        }

        [Fact]
        public async Task ACancellationAfterDispatchKeepsTheFence()
        {
            var cache = new IdempotencyCache(1, 8, TimeSpan.FromSeconds(1), _journalPath);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrCompute(
                KbPath, Tool, Key, PayloadHash,
                () => throw new OperationCanceledException("client disconnected after dispatch")));

            Assert.Equal("started", cache.InspectOperation(KbPath, Tool, Key)["status"]?.ToString());
            AssertSingleStartedRecord(_journalPath);
        }

        [Fact]
        public async Task ErrorNotCacheableStillReturnsItsResult()
        {
            var cache = new IdempotencyCache(1, 8, TimeSpan.FromSeconds(1), _journalPath);
            var error = new JObject { ["isError"] = true, ["content"] = "worker rejected the mutation" };

            var returned = await cache.GetOrCompute(KbPath, Tool, Key, PayloadHash,
                () => throw new ErrorNotCacheable(error));

            // Returned, not thrown.
            Assert.True((bool?)returned["isError"]);
            Assert.Equal("worker rejected the mutation", returned["content"]?.ToString());
            AssertNoStartedRecord(_journalPath);

            // Not cached: a later attempt with the same key re-runs the factory.
            int executions = 0;
            await cache.GetOrCompute(KbPath, Tool, Key, PayloadHash, () =>
            {
                executions++;
                return Task.FromResult(new JObject { ["isError"] = false });
            });
            Assert.Equal(1, executions);
        }

        [Fact]
        public void ThePredicateIsNarrowAndDoesNotClaimPostDispatchFailures()
        {
            Assert.True(PreDispatchFailureClassifier.Is(new UsageException("usage_error", "rejected")));
            Assert.True(PreDispatchFailureClassifier.Is(new KbResolutionException("KB_AMBIGUOUS", "pick one")));
            Assert.True(PreDispatchFailureClassifier.Is(PoolFull()));

            Assert.False(PreDispatchFailureClassifier.Is(null));
            Assert.False(PreDispatchFailureClassifier.Is(new Exception("unknown")));
            Assert.False(PreDispatchFailureClassifier.Is(new InvalidOperationException("post-dispatch")));
            Assert.False(PreDispatchFailureClassifier.Is(new ArgumentException("post-dispatch")));
            Assert.False(PreDispatchFailureClassifier.Is(new IdempotencyConflictException("key reused")));
            Assert.False(PreDispatchFailureClassifier.Is(new ErrorNotCacheable(new JObject())));
            Assert.False(PreDispatchFailureClassifier.Is(new OperationCanceledException()));
            Assert.False(PreDispatchFailureClassifier.Is(new TaskCanceledException()));
        }

        private static WorkerPoolFullException PoolFull()
            => new WorkerPoolFullException(new List<KbHandle>
            {
                new KbHandle("orders", @"C:\KB\Orders"),
                new KbHandle("customers", @"C:\KB\Customers")
            });

        /// <summary>
        /// Reads the persisted journal off disk. Fail removes the record outright
        /// rather than writing a terminal status, so no entry may exist.
        /// </summary>
        private static void AssertNoStartedRecord(string path)
        {
            Assert.True(File.Exists(path), "expected the journal file to have been written");
            var document = JObject.Parse(File.ReadAllText(path));
            var entries = (JArray)document["entries"]!;
            Assert.Empty(entries);
        }

        /// <summary>Asserts the on-disk fence survived as exactly one 'started' entry.</summary>
        private static void AssertSingleStartedRecord(string path)
        {
            Assert.True(File.Exists(path), "expected the journal file to have been written");
            var document = JObject.Parse(File.ReadAllText(path));
            var entries = (JArray)document["entries"]!;
            var entry = Assert.IsType<JObject>(Assert.Single(entries));
            Assert.Equal("started", entry["Status"]?.ToString());
        }
    }
}