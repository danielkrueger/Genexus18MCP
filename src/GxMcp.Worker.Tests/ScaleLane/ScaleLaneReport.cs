using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace GxMcp.Worker.Tests.ScaleLane
{
    /// <summary>
    /// What a scale-lane measurement concluded.
    ///
    /// <para>
    /// The three-valued shape is the issue's requirement, not decoration: a run that
    /// could not execute its workload must not be able to report a pass. "Unavailable" is
    /// a distinct answer from "failed" precisely so that a missing SDK fixture cannot
    /// silently become a green CI run.
    /// </para>
    /// </summary>
    public enum ScaleLaneOutcome
    {
        /// <summary>Executed, and every declared criterion held.</summary>
        Pass,

        /// <summary>Executed, and at least one criterion did not hold.</summary>
        Fail,

        /// <summary>Could not execute. Never counts as a pass.</summary>
        Unavailable,
    }

    /// <summary>
    /// One measured criterion. Deterministic counters carry the gate; wall-clock figures
    /// are recorded for the report but never decide pass/fail on a shared host.
    /// </summary>
    public sealed class ScaleCriterion
    {
        public string Name { get; set; }
        public string Kind { get; set; }

        /// <summary>Host-stable quantity the gate reads. Null when only timed.</summary>
        public long? Count { get; set; }

        /// <summary>Optional ceiling for <see cref="Count"/>.</summary>
        public long? Limit { get; set; }

        /// <summary>Recorded, never gated.</summary>
        public double? Milliseconds { get; set; }

        /// <summary>Why a criterion is unavailable, when it is.</summary>
        public string UnavailableReason { get; set; }

        public bool Held
        {
            get
            {
                if (UnavailableReason != null) return false;
                if (Count.HasValue && Limit.HasValue)
                {
                    // Every Limit in this lane is an "at most" bound on something that
                    // must happen: the position a read was served at, the number of reads
                    // before an aged item ran. A negative measurement is the sentinel for
                    // "that never happened", and -1 <= 1 is true, so the negative case is
                    // rejected before the ceiling is compared. Without this the worst
                    // outcome in a workload would be the one that passes it.
                    if (Count.Value < 0) return false;
                    return Count.Value <= Limit.Value;
                }
                return true;
            }
        }
    }

    /// <summary>
    /// One workload's result.
    /// </summary>
    public sealed class ScaleMeasurement
    {
        public string Workload { get; set; }
        public ScaleLaneOutcome Outcome { get; set; } = ScaleLaneOutcome.Unavailable;
        public string UnavailableReason { get; set; }
        public List<ScaleCriterion> Criteria { get; } = new List<ScaleCriterion>();

        /// <summary>True when every executed criterion held.</summary>
        public bool Passed =>
            Outcome == ScaleLaneOutcome.Pass && Criteria.All(c => c.Held);
    }

    /// <summary>
    /// The lane's environment block.
    ///
    /// <para>
    /// Carries what a reader needs to know whether two runs are comparable, and
    /// deliberately carries nothing else. No absolute path, no KB path, no object
    /// content: a report gets pasted into issues and CI logs, and those outlive the
    /// machine. Fixture alias and seed are enough to reproduce a run; the machine's
    /// directory layout is not.
    /// </para>
    /// </summary>
    public sealed class ScaleEnvironment
    {
        public string Commit { get; set; }
        public string SdkMajor { get; set; }
        public string Architecture { get; set; }
        public string Runtime { get; set; }
        public string Lane { get; set; }
        public int Seed { get; set; }
        public int ObjectCount { get; set; }
        public int KbCount { get; set; }
        public int ClientCount { get; set; }
        public string CacheState { get; set; }
        public string Workload { get; set; }
    }

    /// <summary>
    /// A complete lane run: environment plus per-workload measurements, rendered as a
    /// shareable report.
    /// </summary>
    public sealed class ScaleLaneReport
    {
        public ScaleEnvironment Environment { get; set; } = new ScaleEnvironment();

        /// <summary>
        /// Set when the lane itself could not run - no fixture, no SDK, no permission.
        /// Distinct from a workload being unavailable, which is per-workload.
        /// </summary>
        public string LaneUnavailableReason { get; set; }

        public List<ScaleMeasurement> Measurements { get; } = new List<ScaleMeasurement>();

        public ScaleLaneOutcome Outcome
        {
            get
            {
                if (LaneUnavailableReason != null) return ScaleLaneOutcome.Unavailable;
                if (Measurements.Count == 0) return ScaleLaneOutcome.Unavailable;
                if (Measurements.Any(m => m.Outcome == ScaleLaneOutcome.Fail)) return ScaleLaneOutcome.Fail;
                if (Measurements.Any(m => m.Outcome == ScaleLaneOutcome.Unavailable))
                    return ScaleLaneOutcome.Unavailable;
                return Measurements.All(m => m.Passed) ? ScaleLaneOutcome.Pass : ScaleLaneOutcome.Fail;
            }
        }

        public ScaleMeasurement Workload(string name)
        {
            return Measurements.FirstOrDefault(m => m.Workload == name);
        }

        /// <summary>
        /// The shareable rendering. Fixed key order, one line per criterion, and no
        /// value that could carry a path, a KB name or object source.
        /// </summary>
        public string Render()
        {
            var sb = new StringBuilder();
            sb.Append("=== SYNTHETIC SCALE LANE ===\n");
            foreach (var kv in OrderedEnvironment())
                sb.Append("  ").Append(kv.Key.PadRight(16)).Append(kv.Value).Append('\n');

            sb.Append("  outcome         ").Append(Outcome.ToString().ToUpperInvariant()).Append('\n');

            if (LaneUnavailableReason != null)
            {
                sb.Append("  lane unavailable: ").Append(LaneUnavailableReason).Append('\n');
                return sb.ToString();
            }

            foreach (var m in Measurements)
            {
                sb.Append("  -- ").Append(m.Workload).Append("  [")
                  .Append(m.Outcome.ToString().ToUpperInvariant()).Append("]\n");
                if (m.UnavailableReason != null)
                {
                    sb.Append("     unavailable: ").Append(m.UnavailableReason).Append('\n');
                    continue;
                }
                foreach (var c in m.Criteria)
                {
                    sb.Append("     ")
                      .Append((c.Held ? "ok   " : "FAIL "))
                      .Append(c.Name.PadRight(34));
                    if (c.Count.HasValue)
                        sb.Append(' ').Append(c.Kind).Append('=')
                          .Append(c.Count.Value.ToString(CultureInfo.InvariantCulture));
                    if (c.Limit.HasValue)
                        sb.Append(" (limit ").Append(c.Limit.Value.ToString(CultureInfo.InvariantCulture)).Append(')');
                    if (c.Milliseconds.HasValue)
                        sb.Append("  ").Append(c.Milliseconds.Value.ToString("F2", CultureInfo.InvariantCulture)).Append("ms");
                    sb.Append('\n');
                }
            }
            return sb.ToString();
        }

        private IEnumerable<KeyValuePair<string, string>> OrderedEnvironment()
        {
            var e = Environment;
            yield return Pair("lane", e.Lane);
            yield return Pair("commit", e.Commit);
            yield return Pair("sdkMajor", e.SdkMajor);
            yield return Pair("runtime", e.Runtime);
            yield return Pair("arch", e.Architecture);
            yield return Pair("seed", e.Seed.ToString(CultureInfo.InvariantCulture));
            yield return Pair("objects", e.ObjectCount.ToString(CultureInfo.InvariantCulture));
            yield return Pair("kbs", e.KbCount.ToString(CultureInfo.InvariantCulture));
            yield return Pair("clients", e.ClientCount.ToString(CultureInfo.InvariantCulture));
            yield return Pair("cache", e.CacheState);
            yield return Pair("workload", e.Workload);
        }

        private static KeyValuePair<string, string> Pair(string key, string value) =>
            new KeyValuePair<string, string>(key, value ?? "-");

        /// <summary>
        /// Fills the environment from the running process, with no absolute paths.
        /// The KB alias and seed are the reproduction handle; the store directory is
        /// deliberately not recorded.
        /// </summary>
        public static ScaleEnvironment Describe(
            string lane, string commit, string sdkMajor, ScaleCatalog catalog,
            int kbCount, int clientCount, string cacheState, string workload)
        {
            return new ScaleEnvironment
            {
                Lane = lane,
                Commit = commit,
                SdkMajor = sdkMajor,
                // Fully qualified: this class has a property named Environment, which
                // would otherwise shadow the type and resolve to the instance member.
                Runtime = System.Environment.Version.ToString(),
                Architecture = IntPtr.Size == 8 ? "x64" : "x86",
                Seed = catalog?.Seed ?? 0,
                ObjectCount = catalog?.ObjectCount ?? 0,
                KbCount = kbCount,
                ClientCount = clientCount,
                CacheState = cacheState,
                Workload = workload,
            };
        }
    }
}
