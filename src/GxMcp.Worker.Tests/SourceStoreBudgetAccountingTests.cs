using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #338: every stored-source insert called <c>EnforceStorageBudget</c>, which
    /// summed <c>FileBytes</c> across every catalog record to decide whether the budget
    /// was exceeded. Populating a KB therefore cost O(S^2) accounting iterations while
    /// staying below the budget - quadratic work whose only purpose is to recompute a
    /// total it already knows.
    ///
    /// The guards assert on the reconciliation count (an algorithmic quantity that is
    /// stable on any host) and, just as importantly, that the incremental total stays
    /// exact: a counter that is fast and wrong is worse than a scan.
    /// </summary>
    public class SourceStoreBudgetAccountingTests : IDisposable
    {
        private readonly string _tempDir;
        // Concurrent: Accounting_Survives_Concurrent_Inserts drives Put from
        // Parallel.For, and a plain List<T>.Add is not thread-safe. The pre-existing
        // helper only survived because of the capacity it happened to have grown to.
        private readonly System.Collections.Concurrent.ConcurrentBag<string> _writtenGuids
            = new System.Collections.Concurrent.ConcurrentBag<string>();

        public SourceStoreBudgetAccountingTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-budget-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            SourceStoreService.Instance.SetStoreDirectoryForTest(_tempDir);
        }

        public void Dispose()
        {
            foreach (var guid in _writtenGuids)
            {
                try { SourceStoreService.Instance.TryGet(guid, "Source", out _); } catch { }
            }
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        private static string GuidFor(int i) => i.ToString("D8") + "0000-0000-0000-000000000000";

        private bool Put(int i, string code) => Put(i, code, i);

        private bool Put(int i, string code, int storedAtMinute)
        {
            string guid = GuidFor(i);
            _writtenGuids.Add(guid);
            return SourceStoreService.Instance.Put(guid, "Source", code,
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(storedAtMinute), "v1");
        }

        /// <summary>
        /// The exact total the removed implementation computed on every insert. Read
        /// from the files on disk rather than from the catalog, so it checks the
        /// counter instead of agreeing with it by construction.
        /// </summary>
        private long ExhaustiveTotal()
        {
            long total = 0;
            foreach (var f in Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories))
                total += new FileInfo(f).Length;
            return total;
        }

        [Fact]
        public void Below_Budget_Population_Reconciles_The_Catalog_At_Most_Once()
        {
            // Force one reconciliation up front so the measurement covers the insert
            // loop, not the initial catalog load.
            SourceStoreService.Instance.TrackedStorageBytes();
            long visitsBefore = SourceStoreService.Instance.CatalogRecordVisits;

            const int n = 120;
            for (int i = 0; i < n; i++) Assert.True(Put(i, "// synthetic source " + i));

            long visits = SourceStoreService.Instance.CatalogRecordVisits - visitsBefore;
            long onePass = SourceStoreService.Instance.RecordCount + 1L;
            Assert.True(visits <= onePass,
                $"{n} inserts walked {visits} catalog records; a single reconciliation pass is at most {onePass}");
        }

        [Fact]
        public void Accounting_Does_Not_Reconcile_Grow_With_The_Insert_Batch()
        {
            SourceStoreService.Instance.TrackedStorageBytes();

            long VisitsFor(int n)
            {
                long before = SourceStoreService.Instance.CatalogRecordVisits;
                for (int i = 0; i < n; i++) Put(1000 + i, "// batch source " + i);
                return SourceStoreService.Instance.CatalogRecordVisits - before;
            }

            long small = VisitsFor(40);
            long large = VisitsFor(80);

            // Both batches reconcile at most once, so the second is not
            // proportionally dearer than the first.
            Assert.True(small <= SourceStoreService.Instance.RecordCount + 1,
                $"40 inserts walked {small} catalog records");
            Assert.True(large <= small + 2,
                $"record visits grew from {small} to {large} between a 40-insert and an 80-insert batch");
        }

        /// <summary>
        /// The authoritative complexity guard.
        ///
        /// <para>
        /// <see cref="SourceStoreService.CatalogRecordVisits"/> counts records walked by
        /// <c>SumRecordBytes</c>, so it cannot observe a full-catalog sum written
        /// directly into <c>EnforceStorageBudget</c> - and that is precisely the shape
        /// the defect had. An in-process counter is therefore not sufficient on its own,
        /// and the behavioural guards above would pass against the original code. This
        /// asserts the shape instead: the budget check reads the tracked total, and the
        /// catalog byte sum exists in exactly one place.
        /// </para>
        ///
        /// <para>
        /// The guard is deliberately about the accumulation, not about
        /// <c>_records.Values</c>: LRU eviction legitimately enumerates the catalog to
        /// order candidates by StoredAtUtc, and only does so once pressure actually
        /// requires a victim. Forbidding the enumeration outright would forbid the fix
        /// this issue asks for.
        /// </para>
        /// </summary>
        [Fact]
        public void EnforceStorage_Budget_Reads_The_Tracked_Total_Instead_Of_Summing_The_Catalog()
        {
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "SourceStoreService.cs");

            int start = source.IndexOf("private void EnforceStorageBudget", StringComparison.Ordinal);
            Assert.True(start >= 0, "EnforceStorageBudget not found");
            int end = source.IndexOf("\n        private ", start + 10, StringComparison.Ordinal);
            Assert.True(end > start, "could not delimit EnforceStorageBudget");
            string body = source.Substring(start, end - start);

            Assert.Contains("CurrentStorageBytes()", body, StringComparison.Ordinal);
            // The defect was `foreach (var r in _records.Values) currentBytes += r.FileBytes;`.
            Assert.DoesNotContain("+= r.FileBytes", body, StringComparison.Ordinal);
            Assert.DoesNotContain("+= rec.FileBytes", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// The only place a full-catalog byte sum may happen. A second one is the
        /// quadratic accounting this issue removed coming back somewhere else.
        /// </summary>
        [Fact]
        public void The_Catalog_Byte_Sum_Lives_In_Exactly_One_Place()
        {
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "SourceStoreService.cs");

            int sums = 0;
            int i = 0;
            while ((i = source.IndexOf("+= r.FileBytes", i, StringComparison.Ordinal)) >= 0)
            {
                sums++;
                i += 5;
            }
            Assert.Equal(1, sums);
        }

        [Fact]
        public void Tracked_Total_Matches_The_Catalog_After_Inserts()
        {
            for (int i = 0; i < 25; i++) Assert.True(Put(i, "// synthetic source " + i));

            long tracked = SourceStoreService.Instance.TrackedStorageBytes();
            Assert.Equal(ExhaustiveTotal(), tracked);
        }

        [Fact]
        public void Tracked_Total_Stays_Exact_Across_A_Longer_Replacement()
        {
            for (int i = 0; i < 20; i++) Assert.True(Put(i, "short"));

            long afterShort = SourceStoreService.Instance.TrackedStorageBytes();

            // Replace every record with a much longer body. A replacement that adds the
            // new bytes without subtracting the old ones double-counts here.
            for (int i = 0; i < 20; i++)
                Assert.True(Put(i, new string('x', 20000) + " replacement " + i));

            long afterLong = SourceStoreService.Instance.TrackedStorageBytes();
            Assert.True(afterLong > afterShort, "a longer replacement must increase the total");
            Assert.Equal(ExhaustiveTotal(), afterLong);
        }

        [Fact]
        public void Tracked_Total_Stays_Exact_Across_A_Shorter_Replacement()
        {
            for (int i = 0; i < 20; i++)
                Assert.True(Put(i, new string('y', 20000) + " original " + i));
            long afterLong = SourceStoreService.Instance.TrackedStorageBytes();

            for (int i = 0; i < 20; i++) Assert.True(Put(i, "tiny"));

            long afterShort = SourceStoreService.Instance.TrackedStorageBytes();
            Assert.True(afterShort < afterLong, "a shorter replacement must decrease the total");
            Assert.Equal(ExhaustiveTotal(), afterShort);
        }

        [Fact]
        public void Repeated_Identical_Put_Is_A_NoOp_For_The_Total()
        {
            Assert.True(Put(7, "// identical body"));
            long first = SourceStoreService.Instance.TrackedStorageBytes();

            // The unchanged-content fast path returns before touching accounting; the
            // total must not move in either direction.
            for (int i = 0; i < 5; i++) Assert.True(Put(7, "// identical body"));

            Assert.Equal(first, SourceStoreService.Instance.TrackedStorageBytes());
        }

        [Fact]
        public void Accounting_Survives_Concurrent_Inserts()
        {
            SourceStoreService.Instance.TrackedStorageBytes();

            // Racy promotion/insertion: the counter must end up describing the catalog,
            // not the sequence of interleaved deltas it happened to observe.
            System.Threading.Tasks.Parallel.For(0, 64, i => Put(2000 + i, "// concurrent " + i));

            Assert.Equal(ExhaustiveTotal(), SourceStoreService.Instance.TrackedStorageBytes());
        }

        [Fact]
        public void Record_Count_Tracks_The_Catalog()
        {
            int before = SourceStoreService.Instance.RecordCount;
            for (int i = 0; i < 15; i++) Assert.True(Put(3000 + i, "// counted " + i));
            Assert.Equal(before + 15, SourceStoreService.Instance.RecordCount);
        }

        // ---- Issue #363: eviction never subtracted the evicted bytes from the counter.

        /// <summary>
        /// Roughly <paramref name="bytes"/> of incompressible ASCII. Bodies have to
        /// survive gzip near their original size, otherwise 40 of them would not reach
        /// a 1 MB budget and nothing would ever be evicted.
        /// </summary>
        private static string Incompressible(int seed, int bytes)
        {
            var random = new Random(seed);
            const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .;";
            var chars = new char[bytes];
            for (int i = 0; i < bytes; i++) chars[i] = alphabet[random.Next(alphabet.Length)];
            return new string(chars);
        }

        private static IDisposable BudgetMb(int megabytes)
        {
            string previous = Environment.GetEnvironmentVariable("GXMCP_SOURCE_STORE_MAX_MB");
            Environment.SetEnvironmentVariable("GXMCP_SOURCE_STORE_MAX_MB", megabytes.ToString());
            return new ActionOnDispose(() =>
            {
                Environment.SetEnvironmentVariable("GXMCP_SOURCE_STORE_MAX_MB", previous);
            });
        }

        private sealed class ActionOnDispose : IDisposable
        {
            private readonly Action _action;
            public ActionOnDispose(Action action) => _action = action;
            public void Dispose() => _action();
        }

        [Fact]
        public void Eviction_Leaves_The_Tracked_Total_Equal_To_The_Catalog()
        {
            using (BudgetMb(1))
            {
                // ~2.5 MiB against a 1 MiB budget: the first pass over the catalog.
                for (int i = 0; i < 40; i++)
                    Assert.True(Put(4000 + i, Incompressible(4000 + i, 64 * 1024)));

                Assert.True(SourceStoreService.Instance.TrackedStorageBytes() <= 1L * 1024 * 1024,
                    "the budget must actually have been enforced");
                Assert.True(SourceStoreService.Instance.TrackedBytesMatchCatalog(),
                    "the tracked total drifted away from the catalog during eviction");
                Assert.Equal(ExhaustiveTotal(), SourceStoreService.Instance.TrackedStorageBytes());
            }
        }

        [Fact]
        public void Steady_State_Inserts_Evict_Only_What_They_Added()
        {
            using (BudgetMb(1))
            {
                for (int i = 0; i < 40; i++)
                    Assert.True(Put(4100 + i, Incompressible(4100 + i, 64 * 1024)));

                int settled = SourceStoreService.Instance.RecordCount;
                Assert.True(settled > 0, "the store must retain something after the first pass");

                // The second batch has to be the newest: eviction is LRU by StoredAtUtc,
                // so a batch stamped older than the first would simply retire its own
                // records and never exercise the settled state.
                // With the counter drifting upward, every insert read a total that was
                // still over budget and drained another slice of real records, so the
                // store collapsed to a handful. A settled counter only fires once the
                // store is genuinely over budget, and each pass returns it to 85%.
                const int inserts = 20;
                int retired = 0;
                for (int i = 0; i < inserts; i++)
                {
                    int before = SourceStoreService.Instance.RecordCount;
                    Assert.True(Put(4200 + i, Incompressible(4200 + i, 64 * 1024), 1000 + i));
                    int after = SourceStoreService.Instance.RecordCount;

                    Assert.True(SourceStoreService.Instance.TrackedBytesMatchCatalog());
                    retired += Math.Max(0, before - after);
                }

                // A settled store returns to its 85% high-water mark each pass, so the
                // inserts it retires are proportional to the inserts it accepted. A
                // drifting counter keeps seeing a stale over-budget total and retires
                // far more than it accepts.
                Assert.True(retired <= inserts * 2,
                    $"{inserts} inserts retired {retired} records; a settled store retires about one per insert");

                // The signature of the defect: the count never recovers, because each
                // insert evicts more real records than it adds.
                int remaining = SourceStoreService.Instance.RecordCount;
                Assert.True(remaining >= settled / 2,
                    $"the store drained from {settled} to {remaining} records over {inserts} inserts");
                Assert.Equal(ExhaustiveTotal(), SourceStoreService.Instance.TrackedStorageBytes());
            }
        }

        [Fact]
        public void Every_Insert_Keeps_The_Tracked_Total_Equal_To_The_Catalog()
        {
            using (BudgetMb(1))
            {
                for (int i = 0; i < 60; i++)
                {
                    Assert.True(Put(4300 + i, Incompressible(4300 + i, 64 * 1024)));
                    Assert.True(SourceStoreService.Instance.TrackedBytesMatchCatalog(),
                        $"the counter desynced at insert {i}");
                    Assert.Equal(ExhaustiveTotal(), SourceStoreService.Instance.TrackedStorageBytes());
                }
            }
        }

        [Fact]
        public void The_Store_Settles_Between_The_High_Water_Marks_Instead_Of_Draining()
        {
            using (BudgetMb(1))
            {
                for (int i = 0; i < 40; i++)
                    Assert.True(Put(4400 + i, Incompressible(4400 + i, 64 * 1024)));

                long budget = 1L * 1024 * 1024;
                long tracked = SourceStoreService.Instance.TrackedStorageBytes();
                Assert.True(tracked <= budget, $"tracked {tracked} is above the {budget} budget");
                Assert.True(tracked >= budget * 0.5,
                    $"tracked {tracked} fell below the 85% target; the store is draining instead of settling");

                // Roughly budget / body records should survive, not a handful.
                int remaining = SourceStoreService.Instance.RecordCount;
                Assert.True(remaining >= 8, $"only {remaining} records survived; the store is being drained");
            }
        }
    }
}
