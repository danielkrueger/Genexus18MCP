using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Classifies a Worker's health along the two axes that are actually independent.
    ///
    /// <para>
    /// Issue #342. <c>genexus_connection_recover</c> used to probe <c>Ping</c>, call the
    /// result "responsive", and report an unqualified <c>Healthy</c> when every Worker
    /// replied. But <c>Ping</c> answers on the transport/MTA path: a Worker whose SDK
    /// lane is deadlocked still replies, because the ping never reaches the thread that
    /// is stuck. So a wedged SDK produced exactly the same <c>Healthy</c> as a genuinely
    /// fine one - and the one dimension an operator most needs was the one nothing
    /// measured.
    /// </para>
    ///
    /// <para>
    /// The converse error matters just as much. If "busy" is treated as a fault, then
    /// recycling a Worker recycles a legitimate long build with it. The three busy
    /// states are therefore distinguished rather than collapsed:
    /// <c>idle</c>, <c>busy-progressing</c> and <c>busy-stalled</c>, plus
    /// <c>busy-unproven</c> for a busy lane that has not yet been observed to move.
    /// Only <c>busy-stalled</c> is a recovery target.
    /// </para>
    ///
    /// <para>
    /// This is a pure function of the two probe results, kept in its own file so it can
    /// be exercised without a live Worker - a classifier that needed a wedged SDK call to
    /// test would never be tested.
    /// </para>
    /// </summary>
    internal static class WorkerLivenessClassifier
    {
        /// <summary>
        /// How long a busy SDK lane may go without reporting movement before it counts as
        /// stalled. A build that emits progress every few seconds stays well inside this.
        /// Generous on purpose: being wrong in the "stalled" direction recycles a Worker
        /// that was about to finish, which costs the in-flight work.
        /// </summary>
        internal const int SdkStallAfterMs = 90_000;

        /// <summary>
        /// Issue #371. How long a busy SDK lane that has never reported progress may run
        /// before it counts as stalled.
        ///
        /// <para>
        /// The progress marker is reset when each command starts and is only set by
        /// <c>ProgressEmitter</c>, so it is set by builds and bulk indexing - the
        /// operations that emit progress notifications. Most SDK calls never emit any: a
        /// single object read, a save, an inspect, or a COM call blocked behind a modal
        /// dialog. Those used to stay <c>busy-unproven</c> for as long as they lasted -
        /// minutes or hours - and were therefore never recovered, by
        /// <c>genexus_connection_recover</c> or by shared-host supervision, which applies
        /// the same rule.
        /// </para>
        ///
        /// <para>
        /// This ceiling is deliberately several times <see cref="SdkStallAfterMs"/>: a
        /// lane that has proved it reports progress is trusted to keep doing so on a much
        /// shorter window, while one that has never spoken is given a long grace period
        /// before being called stuck. Wrong in the "stalled" direction still costs the
        /// in-flight work, so the default is minutes rather than seconds.
        /// </para>
        /// </summary>
        internal const int NoProgressStallAfterMsDefault = 600_000;

        internal static readonly int NoProgressStallAfterMs = ResolveNoProgressStallAfterMs();

        private const string NoProgressStallEnvVar = "GXMCP_SDK_NO_PROGRESS_STALL_MS";

        private static int ResolveNoProgressStallAfterMs()
        {
            var raw = Environment.GetEnvironmentVariable(NoProgressStallEnvVar);
            if (int.TryParse(raw, out int configured) && configured > 0) return configured;
            return NoProgressStallAfterMsDefault;
        }

        /// <summary>
        /// Builds the qualified liveness record for one Worker.
        /// </summary>
        /// <param name="alias">KB alias, carried through for the caller's convenience.</param>
        /// <param name="transportAlive">Whether the transport ping answered.</param>
        /// <param name="sdkProbe">
        /// The SDK-lane probe result, a timeout sentinel (<c>__timeout</c>), or null when
        /// the probe did not return at all.
        /// </param>
        internal static JObject Classify(string alias, bool transportAlive, JObject? sdkProbe)
        {
            string sdk;
            string operation = null;
            string stallReason = null;
            long? elapsedMs = null;
            long? lastProgressMs = null;

            if (!transportAlive)
            {
                // Transport first: with no transport there is nothing meaningful to say
                // about the SDK lane, and a timeout there says nothing either way.
                sdk = "unknown";
            }
            else if (sdkProbe == null || sdkProbe["__timeout"]?.ToObject<bool>() == true)
            {
                // A live transport with no SDK answer is its own finding. Reporting it as
                // "fine" would reintroduce the unqualified claim this replaces.
                sdk = "unknown";
            }
            else
            {
                var busy = sdkProbe["result"] as JObject ?? sdkProbe;
                bool active = busy["active"]?.ToObject<bool?>() ?? false;
                operation = busy["operation"]?.ToString();
                elapsedMs = busy["elapsedMs"]?.ToObject<long?>();
                lastProgressMs = busy["lastProgressMs"]?.ToObject<long?>();
                bool sawProgress = busy["sawProgress"]?.ToObject<bool?>() == true;

                if (!active)
                {
                    sdk = "idle";
                }
                else if (!sawProgress || lastProgressMs == null)
                {
                    // Busy, and we have never seen it move. Issue #371: one sample cannot
                    // tell a healthy three-second SDK call from a wedged one, so a short
                    // lane is left alone and named as unproven - calling it "stalled" would
                    // recycle a Worker mid-call. Past the ceiling it is a different
                    // question: this operation has had no way to prove progress in ten
                    // minutes, and most operations that legitimately run that long are the
                    // ones that emit progress. A distinct state, because it is a recovery
                    // candidate while plain busy-unproven is not.
                    sdk = elapsedMs != null && elapsedMs.Value >= NoProgressStallAfterMs
                        ? "busy-stalled-unproven"
                        : "busy-unproven";
                    if (sdk == "busy-stalled-unproven") stallReason = "no-progress-ceiling";
                }
                else if (lastProgressMs.Value >= SdkStallAfterMs)
                {
                    sdk = "busy-stalled";
                    stallReason = "progress-stopped";
                }
                else
                {
                    sdk = "busy-progressing";
                }
            }

            return new JObject
            {
                ["alias"] = alias,
                // `state` is retained for back-compat with existing consumers. It is
                // derived from the transport alone - which is precisely the reduction
                // that made the old verdict untrustworthy, so the new fields must be read
                // instead of it.
                ["state"] = transportAlive ? "responsive" : "unresponsive",
                ["transport"] = transportAlive ? "alive" : "dead",
                ["sdk"] = sdk,
                ["sdkOperation"] = operation,
                ["sdkElapsedMs"] = elapsedMs,
                ["sdkLastProgressMs"] = lastProgressMs,
                ["sdkStallAfterMs"] = SdkStallAfterMs,
                // Issue #371. Why the lane was called stalled, or null when it was not.
                // "no-progress-ceiling" means the operation never reported progress and
                // ran past the grace period; "progress-stopped" means it had been
                // reporting and then went quiet.
                ["sdkStallReason"] = stallReason,
                ["sdkNoProgressStallAfterMs"] = NoProgressStallAfterMs
            };
        }

        /// <summary>
        /// Whether a diagnosis justifies recycling the Worker. A progressing lane never
        /// does, however long it has been running - that is the difference between "slow"
        /// and "stuck", and conflating them is how a legitimate long operation gets
        /// killed.
        /// </summary>
        internal static bool Recovers(JToken diagnosis)
        {
            if (diagnosis == null) return true;
            bool transportAlive = string.Equals(diagnosis["transport"]?.ToString(), "alive", StringComparison.Ordinal);
            if (!transportAlive) return true;
            string sdk = diagnosis["sdk"]?.ToString();
            return string.Equals(sdk, "busy-stalled", StringComparison.Ordinal)
                // Issue #371: a lane that has never reported progress is a recovery
                // candidate once it passes the ceiling, otherwise a deadlocked SDK that
                // emits nothing is never recovered at all.
                || string.Equals(sdk, "busy-stalled-unproven", StringComparison.Ordinal);
        }
    }
}