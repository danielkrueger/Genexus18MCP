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
                    // Busy, and we have never seen it move. Whether that is a healthy
                    // three-second SDK call or a wedged one is genuinely undecidable from
                    // a single sample, so it is left alone and named as unproven. Calling
                    // this "stalled" would recycle a Worker mid-call.
                    sdk = "busy-unproven";
                }
                else if (lastProgressMs.Value >= SdkStallAfterMs)
                {
                    sdk = "busy-stalled";
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
                ["sdkStallAfterMs"] = SdkStallAfterMs
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
            return string.Equals(diagnosis["sdk"]?.ToString(), "busy-stalled", StringComparison.Ordinal);
        }
    }
}