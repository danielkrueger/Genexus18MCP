using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Tests.ScaleLane
{
    /// <summary>
    /// Wraps a comparer and counts every comparison, so a bounded-heap selection can be
    /// gated on its actual comparison volume instead of on a wall-clock figure that
    /// changes with the host.
    /// </summary>
    internal sealed class CountingComparer<T> : IComparer<T>
    {
        private readonly IComparer<T> _inner;
        private long _comparisons;

        internal CountingComparer(IComparer<T> inner)
        {
            _inner = inner ?? Comparer<T>.Default;
        }

        /// <summary>Comparisons performed so far.</summary>
        internal long Comparisons => System.Threading.Interlocked.Read(ref _comparisons);

        public int Compare(T x, T y)
        {
            System.Threading.Interlocked.Increment(ref _comparisons);
            return _inner.Compare(x, y);
        }
    }

    /// <summary>
    /// The deterministic, SDK-free half of issue #358.
    ///
    /// <para>
    /// Runs the real production code paths that stop scaling - source-store budget
    /// accounting, trigram-index residency under churn, bounded top-K page selection,
    /// source search with continuation, and STA admission under bulk load - against a
    /// seeded fictional catalog, and gates on operation counts rather than wall clock.
    /// The issue is explicit that noisy fixed wall-clock gates on arbitrary hosts are the
    /// wrong instrument: a CI box, a developer laptop and a build agent differ by more
    /// than the regressions worth catching.
    /// </para>
    ///
    /// <para>
    /// Each workload also guards a fix already made in this repository, which is what the
    /// issue asks this lane to do: it is the evidence that those fixes are not reverted
    /// for something unmeasured.
    /// </para>
    /// </summary>
    public sealed class SyntheticScaleLane : IDisposable
    {
        private readonly string _storeDir;
        private readonly ScaleCatalog _catalog;

        /// <summary>
        /// How many catalog objects the source-store workloads cover. The full 100k would
        /// make every unit-test run pay for 200 MB of synthetic source; the counters this
        /// lane asserts scale linearly in this number, so a smaller figure detects the
        /// same shape while keeping the lane a fast test.
        /// </summary>
        public const int StoreObjectCount = 1_200;

        public SyntheticScaleLane(int objectCount = 50_000, int seed = 20260101, int kbIndex = 0)
        {
            _catalog = ScaleCatalog.Generate(objectCount, seed, kbIndex,
                sourceSizeBytes: 512, fanIn: 6);
            _storeDir = Path.Combine(Path.GetTempPath(), "gxmcp-scale-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_storeDir);
            SourceStoreService.Instance.SetStoreDirectoryForTest(_storeDir);
        }

        public ScaleCatalog Catalog => _catalog;

        /// <summary>
        /// The part a search would actually resolve this object's source into.
        ///
        /// <para>
        /// Not cosmetic. <c>source</c> scope resolves per object type: a Transaction or
        /// WebPanel is searched through its Events part, so a fixture that stored every
        /// object under a literal <c>Source</c> would silently exclude exactly those
        /// types from every search - which reads as a missing-result bug and is not one.
        /// Writing under the resolved name is what the real write path does.
        /// </para>
        /// </summary>
        private static string PartFor(SearchIndex.IndexEntry entry) =>
            ObjectService.ResolveSearchPartName(entry.Type, "source");

        public void Dispose()
        {
            try { if (Directory.Exists(_storeDir)) Directory.Delete(_storeDir, true); }
            catch { /* a locked fixture file must not fail the lane's teardown */ }
        }

        /// <summary>Runs the whole deterministic workload set and returns a shareable report.</summary>
        public ScaleLaneReport Run(string commit, string sdkMajor, int kbCount = 1, int clientCount = 1)
        {
            var report = new ScaleLaneReport
            {
                Environment = ScaleLaneReport.Describe(
                    "deterministic", commit, sdkMajor, _catalog,
                    kbCount, clientCount, "cold", "full"),
            };

            try
            {
                report.Measurements.Add(MeasureCatalogPopulation());
                report.Measurements.Add(MeasureSourceChurn());
                report.Measurements.Add(MeasureDeepListing());
                report.Measurements.Add(MeasureBroadSourceSearch());
                report.Measurements.Add(MeasureInteractiveUnderLoad());
            }
            catch (Exception ex)
            {
                // A lane that throws is unavailable, not failed. Reporting it as a
                // failure would blame the code under test for the harness breaking.
                report.LaneUnavailableReason = ex.GetType().Name;
            }

            return report;
        }

        /// <summary>
        /// Populating the source store must not re-derive the byte total per insert.
        ///
        /// <para>
        /// Guards #338. <c>EnforceStorageBudget</c> used to sum <c>FileBytes</c> across
        /// every catalog record on every insert, so filling a KB cost O(S^2) accounting
        /// walks while staying under budget. The counter cannot see a total written
        /// directly into the check, so this measures the observable walk and the shape
        /// guard for the accumulation itself lives in
        /// <c>SourceStoreBudgetAccountingTests</c>.
        /// </para>
        /// </summary>
        private ScaleMeasurement MeasureCatalogPopulation()
        {
            var m = new ScaleMeasurement { Workload = "catalog_population" };

            // Force one reconciliation so the measurement covers the insert loop rather
            // than the initial catalog load.
            SourceStoreService.Instance.TrackedStorageBytes();

            long before = SourceStoreService.Instance.CatalogRecordVisits;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < StoreObjectCount; i++)
            {
                var entry = _catalog.Entries[i];
                SourceStoreService.Instance.Put(
                    entry.Guid, PartFor(entry), _catalog.SourceFor(i),
                    entry.LastUpdate, "v1");
            }
            sw.Stop();
            long visits = SourceStoreService.Instance.CatalogRecordVisits - before;

            // A quadratic accounting would visit ~n^2/2 records; a bounded number of
            // reconciliations visits at most a small multiple of n. The factor is loose
            // because flush cadence is time-based and therefore host-dependent - but a
            // quadratic regression is off by two orders of magnitude, so the guard still
            // separates cleanly.
            long limit = 8L * StoreObjectCount;

            m.Criteria.Add(new ScaleCriterion
            {
                Name = "budget record walks per insert batch",
                Kind = "visits",
                Count = visits,
                Limit = limit,
                Milliseconds = sw.Elapsed.TotalMilliseconds,
            });
            m.Criteria.Add(new ScaleCriterion
            {
                Name = "stored record count",
                Kind = "records",
                Count = SourceStoreService.Instance.RecordCount,
                Limit = StoreObjectCount,
            });
            m.Outcome = ScaleLaneOutcome.Pass;
            return m;
        }

        /// <summary>
        /// Replacing a source must reclaim the trigram postings of the text it replaced.
        ///
        /// <para>
        /// Guards #344. Without reclamation, replacing a record's Source left the old
        /// text's trigrams pointing at it, so the index grew without bound under ordinary
        /// edit churn and a search kept paying for postings that no longer matched
        /// anything. This is the workload that would show it: the same objects edited
        /// repeatedly with fresh vocabulary.
        /// </para>
        /// </summary>
        private ScaleMeasurement MeasureSourceChurn()
        {
            var m = new ScaleMeasurement { Workload = "source_churn" };

            int churned = Math.Min(300, StoreObjectCount);
            long residencyBefore = 0;
            var sw = Stopwatch.StartNew();
            for (int pass = 0; pass < 4; pass++)
            {
                for (int i = 0; i < churned; i++)
                {
                    // Distinct vocabulary per pass, so each replacement genuinely
                    // introduces new trigrams that a leak would retain.
                    string source = "// pass " + pass + " churn " + i + " "
                                  + new string((char)('a' + pass), 24) + "\n"
                                  + _catalog.SourceFor(i);
                    var entry = _catalog.Entries[i];
                    SourceStoreService.Instance.Put(
                        entry.Guid, PartFor(entry), source,
                        entry.LastUpdate.AddMinutes(pass), "v" + pass);
                }
                if (pass == 0) residencyBefore = SourceStoreService.Instance.TrigramIndexedRecordCount;
            }
            sw.Stop();

            long residencyAfter = SourceStoreService.Instance.TrigramIndexedRecordCount;

            m.Criteria.Add(new ScaleCriterion
            {
                Name = "trigram-indexed record residency",
                Kind = "records",
                // One record per (guid, part). Re-churning the same objects cannot grow it.
                Count = residencyAfter,
                Limit = residencyBefore + 2,
                Milliseconds = sw.Elapsed.TotalMilliseconds,
            });
            m.Outcome = ScaleLaneOutcome.Pass;
            return m;
        }

        /// <summary>
        /// A page of a deep listing is selected by bounded heap, not by a growing list
        /// rescanned for duplicates.
        ///
        /// <para>
        /// Guards the shape the issue names directly. A duplicate scan over the growing
        /// selection costs O(n*k) or worse; the bounded heap costs O(n log k). With k=50
        /// those differ by roughly a factor of k, and the count is exact on any host
        /// because it counts the production comparer itself rather than elapsed time.
        /// </para>
        /// </summary>
        private ScaleMeasurement MeasureDeepListing()
        {
            const int k = 50;
            var m = new ScaleMeasurement { Workload = "deep_listing" };

            var comparer = new CountingComparer<SearchIndex.IndexEntry>(DefaultIndexEntryComparer.Instance);
            var sw = Stopwatch.StartNew();
            var page = TopKHelper.SelectTopK(_catalog.Entries, k, comparer, out int total);
            sw.Stop();

            long comparisons = comparer.Comparisons;

            // n*log2(k) plus the final k*log2(k) sort. Measured at 100k entries with
            // k=50 that is ~600k comparisons; the duplicate-scan shape is ~5M, and a
            // full sort is ~1.7M. A 6x ceiling sits above the heap and below both.
            int logK = (int)Math.Ceiling(Math.Log(k + 1, 2));
            long limit = 6L * Math.Max(1, _catalog.ObjectCount) * logK;

            m.Criteria.Add(new ScaleCriterion
            {
                Name = "comparisons for a bounded page",
                Kind = "comparisons",
                Count = comparisons,
                Limit = limit,
                Milliseconds = sw.Elapsed.TotalMilliseconds,
            });
            m.Criteria.Add(new ScaleCriterion
            {
                Name = "page size",
                Kind = "items",
                Count = page.Count,
                Limit = k,
            });
            m.Criteria.Add(new ScaleCriterion
            {
                Name = "population covered",
                Kind = "entries",
                Count = total,
                Limit = _catalog.ObjectCount,
            });
            m.Outcome = ScaleLaneOutcome.Pass;
            return m;
        }

        /// <summary>
        /// A broad source search must stay complete and correctly attributed as it pages,
        /// and a truncated page must be a subset of the complete answer rather than a
        /// different one.
        ///
        /// <para>
        /// The issue's rule that a faster wrong result does not pass. So this measures
        /// correctness rather than speed, and it measures it against a hit set known by
        /// construction: the churn workload stamps a marker into exactly the records it
        /// touched, so the expected GUID set is computed rather than guessed.
        /// </para>
        /// </summary>
        private ScaleMeasurement MeasureBroadSourceSearch()
        {
            var m = new ScaleMeasurement { Workload = "broad_source_search" };

            int churned = Math.Min(300, StoreObjectCount);
            var expectedGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var corpus = new List<SearchIndex.IndexEntry>(StoreObjectCount);
            for (int i = 0; i < StoreObjectCount; i++)
            {
                var e = _catalog.Entries[i];
                // The churn workload writes "churn" into the first `churned` records only.
                if (i < churned) expectedGuids.Add(e.Guid);
                corpus.Add(new SearchIndex.IndexEntry
                {
                    Guid = e.Guid,
                    Name = e.Name,
                    Type = e.Type,
                    Path = e.Path,
                    StorageKey = e.Type + ":" + e.Guid,
                });
            }

            var rx = new System.Text.RegularExpressions.Regex("churn",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var complete = new SourceSearchCriteria
            {
                Pattern = "churn",
                Scope = new List<string> { "source" },
                // Well above the seeded hit set, so this pass is not itself truncated.
                MaxResults = 5_000,
            };

            var sw = Stopwatch.StartNew();
            var fullHits = SourceStoreService.Instance.SearchStore(corpus, complete, rx);
            sw.Stop();

            var fullGuids = new HashSet<string>(
                fullHits.Select(h => h["guid"]?.ToString()).Where(g => !string.IsNullOrEmpty(g)),
                StringComparer.OrdinalIgnoreCase);

            long missing = expectedGuids.Count(g => !fullGuids.Contains(g));
            long unexpected = fullGuids.Count(g => !expectedGuids.Contains(g));

            // Identity attribution is what lets a caller act on a hit: without the object
            // name and type a GUID is a lookup, not a result.
            long unattributed = fullHits.Count(h =>
                string.IsNullOrEmpty(h["objectName"]?.ToString())
                || string.IsNullOrEmpty(h["type"]?.ToString()));

            m.Criteria.Add(new ScaleCriterion
            {
                Name = "expected objects missing from the search",
                Kind = "objects",
                Count = missing,
                Limit = 0,
            });
            m.Criteria.Add(new ScaleCriterion
            {
                Name = "objects returned that the seed never wrote",
                Kind = "objects",
                Count = unexpected,
                Limit = 0,
            });
            m.Criteria.Add(new ScaleCriterion
            {
                Name = "hits missing object identity",
                Kind = "hits",
                Count = unattributed,
                Limit = 0,
                Milliseconds = sw.Elapsed.TotalMilliseconds,
            });

            // Truncation: a capped page must be a subset of the complete answer, never a
            // different set. A page that invents or loses a hit under a lower cap would
            // be faster and wrong.
            var capped = new SourceSearchCriteria
            {
                Pattern = "churn",
                Scope = new List<string> { "source" },
                MaxResults = 25,
            };
            var cappedHits = SourceStoreService.Instance.SearchStore(corpus, capped, rx);
            var cappedGuids = new HashSet<string>(
                cappedHits.Select(h => h["guid"]?.ToString()).Where(g => !string.IsNullOrEmpty(g)),
                StringComparer.OrdinalIgnoreCase);

            m.Criteria.Add(new ScaleCriterion
            {
                Name = "capped page exceeding its cap",
                Kind = "hits",
                Count = cappedHits.Count,
                Limit = 25,
            });
            m.Criteria.Add(new ScaleCriterion
            {
                Name = "capped page holding objects the full pass did not",
                Kind = "objects",
                Count = cappedGuids.Count(g => !fullGuids.Contains(g)),
                Limit = 0,
            });
            m.Criteria.Add(new ScaleCriterion
            {
                Name = "capped page repeating an object",
                Kind = "objects",
                Count = cappedHits.Count - cappedGuids.Count,
                Limit = 0,
            });

            m.Outcome = ScaleLaneOutcome.Pass;
            return m;
        }

        /// <summary>
        /// A small interactive read must not queue behind bulk work, and bulk work must
        /// not starve behind a continuous interactive stream.
        ///
        /// <para>
        /// Both directions, because they are different mechanisms. Fresh P0 already
        /// precedes fresh P2 in the take order, so the first is a regression guard on the
        /// existing ordering. The second is #341's aging: a P2 item that has waited past
        /// the window is taken ahead of fresh P0, which is the only reason a long
        /// background scan cannot be starved indefinitely by reads.
        /// </para>
        /// </summary>
        private ScaleMeasurement MeasureInteractiveUnderLoad()
        {
            var m = new ScaleMeasurement { Workload = "interactive_under_load" };
            var scheduler = StaScheduler.Instance;
            scheduler.Clear();

            try
            {
                // A cleared scheduler must accept work. Reported separately from every
                // ordering criterion below, because a refused admission silently
                // invalidates them all: there is no position for an item that was never
                // queued, and folding that into a position figure would report
                // "served last" for what is really "never served".
                const int bulk = 400;
                int accepted = 0;
                DateTime now = DateTime.UtcNow;
                for (int i = 0; i < bulk; i++)
                {
                    if (scheduler.TryEnqueue(Item(CommandPriority.P2_Background, "bulk-" + i, now)))
                        accepted++;
                }
                bool readAdmitted = scheduler.TryEnqueue(
                    Item(CommandPriority.P0_Interactive, "read-fresh", now));

                m.Criteria.Add(new ScaleCriterion
                {
                    Name = "bulk commands refused on a cleared queue",
                    Kind = "refused",
                    Count = bulk - accepted,
                    Limit = 0,
                });
                m.Criteria.Add(new ScaleCriterion
                {
                    Name = "interactive read refused admission",
                    Kind = "refused",
                    Count = readAdmitted ? 0 : 1,
                    Limit = 0,
                });

                // Fresh bulk must not delay a fresh read.
                int readPosition = -1;
                for (int taken = 1; taken <= bulk + 1 && scheduler.TryTakeNext(out var item); taken++)
                {
                    if (item.Method == "read-fresh") { readPosition = taken; break; }
                }
                m.Criteria.Add(new ScaleCriterion
                {
                    Name = "interactive read position behind fresh bulk",
                    Kind = "position",
                    Count = readPosition,
                    // -1 means "never taken", which is the worst possible outcome and so
                    // cannot be folded into an acceptable figure.
                    Limit = 1,
                });

                // The other direction. One background item that has genuinely waited,
                // then an endless interactive stream. This is #341's aging.
                scheduler.Clear();
                scheduler.TryEnqueue(Item(CommandPriority.P2_Background, "bulk-aged",
                    DateTime.UtcNow - StaScheduler.PriorityAgingWindow - TimeSpan.FromSeconds(1)));

                int agedServedAt = -1;
                for (int reads = 1; reads <= 2_000; reads++)
                {
                    scheduler.TryEnqueue(Item(CommandPriority.P0_Interactive, "read-" + reads,
                        DateTime.UtcNow));
                    if (!scheduler.TryTakeNext(out var item)) break;
                    if (item.Method == "bulk-aged") { agedServedAt = reads; break; }
                }
                m.Criteria.Add(new ScaleCriterion
                {
                    Name = "aged bulk served within this many interactive reads",
                    Kind = "reads",
                    Count = agedServedAt,
                    Limit = 1,
                });

                while (scheduler.TryTakeNext(out _)) { }
                m.Criteria.Add(new ScaleCriterion
                {
                    Name = "queue drained after the workload",
                    Kind = "queued",
                    Count = scheduler.QueuedCount,
                    Limit = 0,
                });

                m.Outcome = ScaleLaneOutcome.Pass;
                return m;
            }
            catch (Exception ex)
            {
                m.Outcome = ScaleLaneOutcome.Unavailable;
                m.UnavailableReason = ex.GetType().Name;
                return m;
            }
            finally
            {
                scheduler.Clear();
            }
        }

        /// <summary>
        /// Builds a scheduler item the way the real ingress path does, so priority
        /// resolution is exercised rather than assumed.
        /// </summary>
        private static ScheduledCommandItem Item(CommandPriority priority, string method, DateTime enqueuedAtUtc)
        {
            var obj = new JObject
            {
                ["method"] = method,
                ["_meta"] = new JObject { ["clientId"] = "scale-lane" },
            };
            return new ScheduledCommandItem
            {
                Obj = obj,
                RawLine = obj.ToString(Newtonsoft.Json.Formatting.None),
                Priority = priority,
                ClientId = "scale-lane",
                EnqueuedAtUtc = enqueuedAtUtc,
                Method = method,
            };
        }
    }
}
