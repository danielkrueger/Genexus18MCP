using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using GxMcp.Worker.Tests.ScaleLane;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #358: a reproducible synthetic scale lane with measured pass criteria.
    ///
    /// <para>
    /// The repository had a 40k benchmark and two live benchmark scripts, and neither
    /// could gate a change. The benchmark rebuilt its catalog from <c>Guid.NewGuid()</c>
    /// and <c>DateTime.UtcNow</c> on every run, so two runs measured different inputs;
    /// it reported before/after figures with no assertion, no pass/fail and no
    /// unavailable state. The live scripts drive one KB through one client against a
    /// fixed local fixture, so they cannot express a 1/3-KB by 1/2-client matrix.
    /// </para>
    ///
    /// <para>
    /// These cover the fast deterministic half: real production code, a seeded fictional
    /// catalog, and gates on operation counts rather than wall clock, because a CI box and
    /// a developer laptop differ by more than the regressions worth catching.
    /// </para>
    /// </summary>
    public class SyntheticScaleLaneTests
    {
        private readonly ITestOutputHelper _output;

        public SyntheticScaleLaneTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static string Commit() => "test";

        [Theory]
        [InlineData(10_000)]
        [InlineData(50_000)]
        [InlineData(100_000)]
        public void The_Lane_Holds_At_Every_Requested_Catalog_Size(int size)
        {
            using (var lane = new SyntheticScaleLane(size))
            {
                var report = lane.Run(Commit(), "18");

                _output.WriteLine(report.Render());

                var failed = report.Measurements
                    .SelectMany(m => m.Criteria.Select(c => (m.Workload, c)))
                    .Where(t => !t.c.Held)
                    .Select(t => $"{t.Workload}: {t.c.Name} = {t.c.Count} (limit {t.c.Limit})")
                    .ToList();

                Assert.True(report.Outcome == ScaleLaneOutcome.Pass,
                    "lane outcome " + report.Outcome + "; failing criteria: "
                    + (failed.Count == 0 ? "(none)" : string.Join("; ", failed)));
            }
        }

        [Fact]
        public void A_Seeded_Catalog_Is_Reproducible_On_Any_Host()
        {
            // The property the previous 40k benchmark lacked, and the reason a recorded
            // baseline is comparable to a later run. Compared by content, not by
            // reference: the point is that two independent builds agree.
            var a = ScaleCatalog.Generate(2_000, seed: 4242);
            var b = ScaleCatalog.Generate(2_000, seed: 4242);
            var c = ScaleCatalog.Generate(2_000, seed: 4243);

            for (int i = 0; i < 2_000; i++)
            {
                Assert.Equal(a.Entries[i].Guid, b.Entries[i].Guid);
                Assert.Equal(a.Entries[i].Name, b.Entries[i].Name);
                Assert.Equal(a.Entries[i].Type, b.Entries[i].Type);
                Assert.Equal(a.Entries[i].Path, b.Entries[i].Path);
                Assert.Equal(a.Entries[i].LastUpdate, b.Entries[i].LastUpdate);
                Assert.Equal(a.Entries[i].Calls, b.Entries[i].Calls);
            }

            // And the seed actually matters, or "reproducible" would be trivially true.
            Assert.NotEqual(a.Entries[0].Name, c.Entries[0].Name);
            Assert.NotEqual(a.Entries[0].Guid, c.Entries[0].Guid);
        }

        [Fact]
        public void Each_Kb_Of_The_Matrix_Is_A_Distinct_Catalog()
        {
            // Otherwise a 3-KB run measures one KB twice against a warm cache and
            // reports the result as a three-KB figure.
            var first = ScaleCatalog.Generate(500, seed: 99, kbIndex: 0);
            var second = ScaleCatalog.Generate(500, seed: 99, kbIndex: 1);

            Assert.NotEqual(first.Alias, second.Alias);
            Assert.NotEqual(first.Entries[0].Guid, second.Entries[0].Guid);
            Assert.NotEqual(first.Entries[0].Name, second.Entries[0].Name);
        }

        [Fact]
        public void A_Duplicate_Name_Exists_Across_Types()
        {
            // A catalog of globally unique names would never exercise the name index's
            // disambiguation path, which is one of the things that stops scaling. The
            // generator draws names from a small vocabulary, so this must hold or the
            // fixture is not exercising what it claims to.
            var catalog = ScaleCatalog.Generate(20_000);
            var byName = catalog.Entries
                .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Select(e => e.Type).Distinct().Count() > 1);

            Assert.True(byName.Any(), "the generated catalog has no cross-type name collisions");
        }

        // ---- the three mutations the issue requires guards against ----------------

        [Fact]
        public void Budget_Accounting_Does_Not_Reread_The_Catalog_Per_Insert()
        {
            // Mutation target: full-catalog budget recomputation. The shape is O(n^2) in
            // the insert count, so comparing two batch sizes separates it decisively
            // rather than by a factor that could be noise.
            using (var lane = new SyntheticScaleLane(50_000))
            {
                SourceStoreService.Instance.TrackedStorageBytes();

                long VisitsFor(int from, int count)
                {
                    long before = SourceStoreService.Instance.CatalogRecordVisits;
                    for (int i = from; i < from + count; i++)
                    {
                        var entry = lane.Catalog.Entries[i];
                        SourceStoreService.Instance.Put(entry.Guid,
                            ObjectService.ResolveSearchPartName(entry.Type, "source"),
                            lane.Catalog.SourceFor(i), entry.LastUpdate, "v1");
                    }
                    return SourceStoreService.Instance.CatalogRecordVisits - before;
                }

                long small = VisitsFor(0, 300);
                long large = VisitsFor(300, 600);

                Assert.True(small <= SourceStoreService.Instance.RecordCount + 2,
                    $"a 300-insert batch walked {small} catalog records");
                Assert.True(large <= small + 2,
                    $"record walks grew from {small} for 300 inserts to {large} for 600; "
                    + "that is the per-insert catalog rescan this lane exists to detect");
            }
        }

        [Fact]
        public void A_Page_Is_Selected_By_Bounded_Heap_Not_By_A_Growing_Duplicate_Scan()
        {
            // Mutation target: growing-list duplicate scan. Measured through the
            // production comparer itself, so the count is exact on any host.
            const int k = 50;
            foreach (int size in new[] { 10_000, 100_000 })
            {
                var catalog = ScaleCatalog.Generate(size);
                var comparer = new CountingComparer<SearchIndex.IndexEntry>(DefaultIndexEntryComparer.Instance);
                TopKHelper.SelectTopK(catalog.Entries, k, comparer, out int total);

                long comparisons = comparer.Comparisons;
                int logK = (int)Math.Ceiling(Math.Log(k + 1, 2));
                long heapBound = 6L * size * logK;
                // A duplicate scan over the growing selection is O(n*k); a full sort is
                // O(n log n). Both are excluded by the bound, the heap is not.
                long duplicateScanCost = (long)size * k;

                Assert.Equal(size, total);
                Assert.True(comparisons <= heapBound,
                    $"{size} entries: {comparisons} comparisons exceeds the bounded-heap bound {heapBound}");
                Assert.True(comparisons < duplicateScanCost,
                    $"{size} entries: {comparisons} comparisons is at or above the "
                    + $"{duplicateScanCost} a growing-list duplicate scan would cost");
            }
        }

        [Fact]
        public void An_Interactive_Read_Is_Not_Starved_By_Bulk_Work()
        {
            // Mutation target: starvation of a small read during bulk work.
            var scheduler = StaScheduler.Instance;
            scheduler.Clear();
            try
            {
                DateTime now = DateTime.UtcNow;
                for (int i = 0; i < 400; i++)
                    Assert.True(scheduler.TryEnqueue(Item(CommandPriority.P2_Background, "bulk-" + i, now, "bulk-" + i)));
                Assert.True(scheduler.TryEnqueue(Item(CommandPriority.P0_Interactive, "read-fresh", now)),
                    "the read was refused admission, so its position could not be measured");

                int position = -1;
                for (int taken = 1; taken <= 401 && scheduler.TryTakeNext(out var item); taken++)
                {
                    if (item.Method == "read-fresh") { position = taken; break; }
                }

                Assert.True(position >= 1, "the interactive read was never served behind fresh bulk");
                Assert.Equal(1, position);
            }
            finally
            {
                while (scheduler.TryTakeNext(out _)) { }
                scheduler.Clear();
            }
        }

        [Fact]
        public void An_Aged_Bulk_Item_Is_Not_Starved_By_A_Continuous_Read_Stream()
        {
            // The mirror image, and the reason #341 exists. Without aging, a sustained
            // P0 stream defers accepted background work forever and the caller told
            // "accepted" has no way to learn it will never run.
            var scheduler = StaScheduler.Instance;
            scheduler.Clear();
            try
            {
                Assert.True(scheduler.TryEnqueue(Item(CommandPriority.P2_Background, "bulk-aged",
                    DateTime.UtcNow - StaScheduler.PriorityAgingWindow - TimeSpan.FromSeconds(1))));

                int servedAt = -1;
                for (int reads = 1; reads <= 5_000; reads++)
                {
                    scheduler.TryEnqueue(Item(CommandPriority.P0_Interactive, "read-" + reads, DateTime.UtcNow));
                    if (!scheduler.TryTakeNext(out var item)) break;
                    if (item.Method == "bulk-aged") { servedAt = reads; break; }
                }

                Assert.True(servedAt >= 1, "the aged bulk item was starved by 5000 interactive reads");
                Assert.Equal(1, servedAt);
            }
            finally
            {
                while (scheduler.TryTakeNext(out _)) { }
                scheduler.Clear();
            }
        }

        [Fact]
        public void A_Cleared_Scheduler_Still_Admits_Work()
        {
            // Found by this lane. Clear() emptied the three queues but left the
            // accounting counters populated, so TryEnqueue kept answering `queue_full`
            // to fresh commands against an empty queue - and the caller has no way to
            // drain a queue that is already empty, so the refusal never self-heals.
            var scheduler = StaScheduler.Instance;
            scheduler.Clear();

            for (int i = 0; i < 300; i++)
                Assert.True(scheduler.TryEnqueue(Item(CommandPriority.P2_Background, "fill-" + i, DateTime.UtcNow, "fill-" + i)));
            Assert.Equal(300, scheduler.QueuedCount);

            scheduler.Clear();
            Assert.Equal(0, scheduler.QueuedCount);
            Assert.Equal(0, scheduler.QueuedBytes);

            for (int i = 0; i < 300; i++)
            {
                Assert.True(scheduler.TryEnqueue(Item(CommandPriority.P2_Background, "after-clear-" + i, DateTime.UtcNow, "after-clear-" + i)),
                    $"command {i} was refused with '{scheduler.LastAdmissionError}' on a cleared queue");
            }
            Assert.Equal(300, scheduler.QueuedCount);

            while (scheduler.TryTakeNext(out _)) { }
            scheduler.Clear();
        }

        [Fact]
        public void Trigram_Residency_Does_Not_Grow_With_Edit_Churn()
        {
            // Guards #344 at scale. Replacing a Source used to leave the old text's
            // trigrams pointing at the record, so the index grew without bound under
            // ordinary editing and search kept paying for postings that matched nothing.
            int churned = 300;
            string store = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "gxmcp-scale-churn-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(store);
            try
            {
                SourceStoreService.Instance.SetStoreDirectoryForTest(store);
                var catalog = ScaleCatalog.Generate(10_000, sourceSizeBytes: 512);

                long residencyAt = -1;
                for (int pass = 0; pass < 6; pass++)
                {
                    for (int i = 0; i < churned; i++)
                    {
                        var entry = catalog.Entries[i];
                        SourceStoreService.Instance.Put(entry.Guid,
                            ObjectService.ResolveSearchPartName(entry.Type, "source"),
                            "// pass " + pass + " " + new string((char)('a' + pass), 32),
                            entry.LastUpdate.AddMinutes(pass), "v" + pass);
                    }
                    long residency = SourceStoreService.Instance.TrigramIndexedRecordCount;
                    if (pass == 0) residencyAt = residency;
                    else
                        Assert.True(residency <= residencyAt,
                            $"after churn pass {pass} the index holds {residency} records; "
                            + $"pass 0 left {residencyAt}");
                }
            }
            finally
            {
                try { if (System.IO.Directory.Exists(store)) System.IO.Directory.Delete(store, true); }
                catch { }
            }
        }

        [Fact]
        public void A_Truncated_Page_Is_A_Subset_Of_The_Complete_Answer()
        {
            // "A faster wrong result does not pass." The complete answer is computed
            // first and the capped answer must be contained by it: never a different
            // set, never a repeat, never over its own cap.
            using (var lane = new SyntheticScaleLane(10_000))
            {
                var report = lane.Run(Commit(), "18");
                var search = report.Workload("broad_source_search");

                Assert.NotNull(search);
                Assert.True(search.Passed,
                    "search workload failed: " + string.Join("; ",
                        search.Criteria.Where(c => !c.Held).Select(c => c.Name)));

                var capped = search.Criteria.First(c => c.Name == "capped page exceeding its cap");
                Assert.Equal(25, capped.Count);
            }
        }

        /// <summary>
        /// The shape half of the budget guard, which the counter cannot cover.
        ///
        /// <para>
        /// <c>catalog_population</c> reports <c>CatalogRecordVisits</c>, which counts the
        /// records walked by <c>SumRecordBytes</c>. A total written directly into
        /// <c>EnforceStorageBudget</c> walks no counter at all, so the lane's measured
        /// criterion stays green against exactly the regression the workload is named
        /// for - confirmed by mutation: restoring
        /// <c>foreach (var r in _records.Values) currentBytes += r.FileBytes;</c> leaves
        /// every behavioural assertion in this file passing.
        /// </para>
        ///
        /// <para>
        /// So the lane carries a structural criterion for it as well. A measurement
        /// criterion that cannot fail for the failure it names is not evidence, and the
        /// alternative - dropping the workload - would leave the lane reporting coverage
        /// it does not have.
        /// </para>
        ///
        /// <para>
        /// <c>SourceStoreBudgetAccountingTests</c> owns the same invariant from the unit
        /// side. It is asserted here too, deliberately: this suite's contract is that a
        /// report claiming budget-accounting coverage is telling the truth, which is a
        /// different question from whether the method is covered by a test.
        /// </para>
        /// </summary>
        [Fact]
        public void The_Budget_Check_Reads_The_Tracked_Total_And_Does_Not_Rescan_The_Catalog()
        {
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "SourceStoreService.cs");

            int start = source.IndexOf("private void EnforceStorageBudget", StringComparison.Ordinal);
            Assert.True(start >= 0, "EnforceStorageBudget not found");
            int end = source.IndexOf("\n        private ", start + 10, StringComparison.Ordinal);
            Assert.True(end > start, "could not delimit EnforceStorageBudget");
            string body = source.Substring(start, end - start);

            Assert.Contains("CurrentStorageBytes()", body, StringComparison.Ordinal);
            Assert.DoesNotContain("+= r.FileBytes", body, StringComparison.Ordinal);
            Assert.DoesNotContain("+= rec.FileBytes", body, StringComparison.Ordinal);
        }

        // ---- the lane's own honesty contracts --------------------------------------

        [Fact]
        public void An_Unavailable_Run_Is_Never_Reported_As_A_Pass()
        {
            // The issue's rule that missing SDK fixtures are not passes. Without the
            // third state a lane that could not run has only two honest-looking answers
            // and green is the tempting one.
            var report = new ScaleLaneReport { LaneUnavailableReason = "no_sdk_fixture" };
            Assert.Equal(ScaleLaneOutcome.Unavailable, report.Outcome);
            Assert.NotEqual(ScaleLaneOutcome.Pass, report.Outcome);

            var empty = new ScaleLaneReport();
            Assert.Equal(ScaleLaneOutcome.Unavailable, empty.Outcome);

            var unavailable = new ScaleMeasurement { Workload = "w", Outcome = ScaleLaneOutcome.Unavailable };
            unavailable.Criteria.Add(new ScaleCriterion
            {
                Name = "c", Kind = "x", Count = 0, Limit = 0,
                UnavailableReason = "missing_fixture",
            });
            var mixed = new ScaleLaneReport();
            mixed.Measurements.Add(unavailable);
            Assert.Equal(ScaleLaneOutcome.Unavailable, mixed.Outcome);
            Assert.False(unavailable.Criteria[0].Held);
        }

        [Fact]
        public void A_Negative_Measurement_Never_Satisfies_A_Limit()
        {
            // The sentinel for "that never happened" has to fail, and -1 <= 1 is true.
            // Without this, the worst outcome in a workload would pass it.
            var criterion = new ScaleCriterion { Name = "position", Kind = "position", Count = -1, Limit = 1 };
            Assert.False(criterion.Held);

            var served = new ScaleCriterion { Name = "position", Kind = "position", Count = 1, Limit = 1 };
            Assert.True(served.Held);
        }

        [Fact]
        public void A_Failing_Criterion_Fails_The_Lane()
        {
            var m = new ScaleMeasurement { Workload = "w", Outcome = ScaleLaneOutcome.Pass };
            m.Criteria.Add(new ScaleCriterion { Name = "ok", Kind = "k", Count = 1, Limit = 2 });
            m.Criteria.Add(new ScaleCriterion { Name = "bad", Kind = "k", Count = 3, Limit = 2 });
            Assert.False(m.Passed);

            var report = new ScaleLaneReport();
            report.Measurements.Add(m);
            Assert.Equal(ScaleLaneOutcome.Fail, report.Outcome);
        }

        [Fact]
        public void The_Report_Records_What_A_Reader_Needs_And_Nothing_Else()
        {
            using (var lane = new SyntheticScaleLane(10_000))
            {
                var report = lane.Run("abc1234", "18", kbCount: 3, clientCount: 2);
                string text = report.Render();

                foreach (var expected in new[] { "abc1234", "sdkMajor", "seed", "objects", "kbs", "clients", "cache", "arch" })
                    Assert.Contains(expected, text, StringComparison.Ordinal);

                Assert.Contains("3", text, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void The_Report_Leaks_No_Paths_And_No_Object_Content()
        {
            // The issue forbids absolute machine/KB paths and object contents in a
            // shareable report: reports get pasted into issues and CI logs, which outlive
            // the machine they were measured on.
            string laneDir = System.IO.Path.GetTempPath();
            string rendered;
            using (var lane = new SyntheticScaleLane(10_000))
            {
                rendered = lane.Run("abc1234", "18").Render();
            }

            Assert.DoesNotContain(laneDir, rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(":\\", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("/", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic source for", rendered, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_Kb_And_Client_Matrix_Is_Declared_Not_Assumed()
        {
            // The issue asks for a 1/3-KB by 1/2-client matrix. The deterministic lane
            // runs in-process, so it declares the shape it represents rather than
            // silently measuring something narrower than it claims.
            foreach (int kbs in new[] { 1, 3 })
            {
                foreach (int clients in new[] { 1, 2 })
                {
                    var env = new ScaleEnvironment
                    {
                        Lane = "deterministic",
                        KbCount = kbs,
                        ClientCount = clients,
                        CacheState = "cold",
                    };
                    var report = new ScaleLaneReport { Environment = env };
                    string text = report.Render();
                    Assert.Equal(kbs.ToString(), ValueOf(text, "kbs"));
                    Assert.Equal(clients.ToString(), ValueOf(text, "clients"));
                    Assert.Equal("cold", ValueOf(text, "cache"));
                }
            }
        }

        /// <summary>
        /// Reads one environment value out of the rendered report. Matched on the trimmed
        /// line rather than on a fixed column width, so a layout change cannot turn a
        /// meaningful assertion into a silent pass.
        /// </summary>
        private static string ValueOf(string report, string key)
        {
            var line = report.Split('\n')
                .Select(l => l.TrimEnd('\r').Trim())
                .FirstOrDefault(l => l.StartsWith(key + " ", StringComparison.Ordinal));

            Assert.True(line != null, "the report has no '" + key + "' line");
            return line.Substring(key.Length).Trim();
        }

        private static ScheduledCommandItem Item(CommandPriority priority, string method, DateTime at)
            => Item(priority, method, at, "scale-tests");

        /// <summary>
        /// Issue #369 capped one client's background work at half the global admission
        /// budget, so a single client can no longer hold 300-400 bulk items at once. That is
        /// the point of the cap, so the starvation and Clear() fixtures model the realistic
        /// multi-producer shape - one client per unit of work - instead of one client
        /// monopolising the lane. Their assertions and intent are unchanged.
        /// </summary>
        private static ScheduledCommandItem Item(CommandPriority priority, string method, DateTime at, string clientId)
        {
            var obj = new Newtonsoft.Json.Linq.JObject
            {
                ["method"] = method,
                ["_meta"] = new Newtonsoft.Json.Linq.JObject { ["clientId"] = clientId },
            };
            return new ScheduledCommandItem
            {
                Obj = obj,
                RawLine = obj.ToString(Newtonsoft.Json.Formatting.None),
                Priority = priority,
                ClientId = clientId,
                EnqueuedAtUtc = at,
                Method = method,
            };
        }
    }
}
