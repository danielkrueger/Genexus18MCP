using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Issue #342, part three: a responsive transport could not be distinguished from a
    /// healthy SDK. <c>Ping</c> answers on the transport/MTA path, so a Worker whose SDK
    /// lane was deadlocked still replied - and <c>genexus_connection_recover</c> reported
    /// an unqualified <c>Healthy</c> for exactly that Worker.
    ///
    /// <para>
    /// These are pure-function tests on the classifier: no live Worker, and deliberately
    /// no wedged SDK call to set up, because a classifier that needed one to test would
    /// never be tested.
    /// </para>
    /// </summary>
    public class WorkerLivenessClassifierTests
    {
        private static JObject SdkProbe(bool active, bool sawProgress, long? lastProgressMs, string op = "build")
            => new JObject
            {
                ["result"] = new JObject
                {
                    ["active"] = active,
                    ["sawProgress"] = sawProgress,
                    ["lastProgressMs"] = lastProgressMs,
                    ["elapsedMs"] = lastProgressMs.HasValue ? lastProgressMs.Value + 1000 : 500,
                    ["operation"] = op
                }
            };

        [Fact]
        public void A_Live_Transport_And_An_Idle_Sdk_Is_Healthy()
        {
            var d = WorkerLivenessClassifier.Classify("kb1", transportAlive: true, SdkProbe(false, false, null));

            Assert.Equal("alive", (string)d["transport"]);
            Assert.Equal("idle", (string)d["sdk"]);
            Assert.False(WorkerLivenessClassifier.Recovers(d));
        }

        [Fact]
        public void A_Live_Transport_And_A_Stalled_Sdk_Is_Not_Reported_As_Healthy()
        {
            // The defect. A responsive transport plus an SDK that has stopped moving for
            // longer than the stall window is the case the old code called "Healthy",
            // because the old code only asked the transport.
            var d = WorkerLivenessClassifier.Classify(
                "kb1",
                transportAlive: true,
                SdkProbe(active: true, sawProgress: true,
                         lastProgressMs: WorkerLivenessClassifier.SdkStallAfterMs + 1_000));

            Assert.Equal("alive", (string)d["transport"]);
            Assert.Equal("busy-stalled", (string)d["sdk"]);
            // And it is the one busy state that justifies recycling.
            Assert.True(WorkerLivenessClassifier.Recovers(d));
        }

        [Fact]
        public void A_Live_Transport_And_A_Progressing_Sdk_Is_Not_A_Recovery_Target()
        {
            // The converse error, and the more expensive one: treating any busy lane as a
            // fault kills a legitimate long build together with its in-flight work.
            // Progress three seconds ago is unambiguously healthy.
            var d = WorkerLivenessClassifier.Classify(
                "kb1", transportAlive: true,
                SdkProbe(active: true, sawProgress: true, lastProgressMs: 3_000));

            Assert.Equal("busy-progressing", (string)d["sdk"]);
            Assert.False(WorkerLivenessClassifier.Recovers(d));
        }

        [Fact]
        public void A_Progressing_Lane_Is_Not_Stalled_However_Long_It_Has_Run()
        {
            // Elapsed time alone must never condemn a lane. A build that reports progress
            // every second for an hour is progressing, not stuck, and the classifier has
            // no reason to prefer elapsed over evidence of movement.
            var probe = SdkProbe(active: true, sawProgress: true, lastProgressMs: 2_000);
            probe["result"]!["elapsedMs"] = 3_600_000;

            var d = WorkerLivenessClassifier.Classify("kb1", transportAlive: true, probe);

            Assert.Equal("busy-progressing", (string)d["sdk"]);
            Assert.False(WorkerLivenessClassifier.Recovers(d));
        }

        [Fact]
        public void A_Busy_Lane_That_Has_Not_Been_Observed_To_Move_Is_Unproven_Not_Stalled()
        {
            // A single sample cannot distinguish a healthy three-second SDK call from a
            // deadlocked one. Reporting this as "stalled" would recycle a Worker mid-call,
            // so it is named for what it is and left alone.
            var d = WorkerLivenessClassifier.Classify(
                "kb1", transportAlive: true,
                SdkProbe(active: true, sawProgress: false, lastProgressMs: null));

            Assert.Equal("busy-unproven", (string)d["sdk"]);
            Assert.False(WorkerLivenessClassifier.Recovers(d));
        }

        [Fact]
        public void A_Dead_Transport_Is_Recovered_Whatever_The_Sdk_Says()
        {
            // Transport first: with no transport the SDK lane is unobservable, and the
            // Worker is unusable either way.
            var d = WorkerLivenessClassifier.Classify(
                "kb1", transportAlive: false,
                SdkProbe(active: false, sawProgress: false, lastProgressMs: null));

            Assert.Equal("dead", (string)d["transport"]);
            Assert.Equal("unknown", (string)d["sdk"]);
            Assert.True(WorkerLivenessClassifier.Recovers(d));
        }

        [Fact]
        public void A_Live_Transport_With_No_Sdk_Answer_Is_Unknown_Not_Fine()
        {
            // Folding this into either verdict is what made the old answer untrustworthy:
            // the probe not coming back is itself a finding.
            var timedOut = new JObject { ["__timeout"] = true };

            Assert.Equal("unknown", (string)WorkerLivenessClassifier.Classify("kb1", true, timedOut)["sdk"]);
            Assert.Equal("unknown", (string)WorkerLivenessClassifier.Classify("kb1", true, null)["sdk"]);
        }

        [Fact]
        public void An_Unknown_Sdk_Lane_Is_Not_Recycled()
        {
            // Recycling on an unknown would make the classifier aggressive exactly when
            // it knows least.
            Assert.False(WorkerLivenessClassifier.Recovers(
                WorkerLivenessClassifier.Classify("kb1", true, new JObject { ["__timeout"] = true })));
        }

        [Fact]
        public void The_Back_Compat_State_Field_Is_Derived_From_The_Transport_Only()
        {
            // Deliberately preserved, and deliberately insufficient: it is the reduction
            // that made the old verdict untrustworthy. Pinned so a future refactor that
            // quietly "improves" it to a composite does not break existing consumers
            // without anyone noticing the field's meaning changed.
            var d = WorkerLivenessClassifier.Classify(
                "kb1", transportAlive: true,
                SdkProbe(active: true, sawProgress: true, lastProgressMs: 1_000));

            Assert.Equal("responsive", (string)d["state"]);
            Assert.Equal("busy-progressing", (string)d["sdk"]);
        }

        [Fact]
        public void The_Stall_Window_Is_Long_Enough_Not_To_Condemn_A_Real_Build()
        {
            // Generous on purpose. Being wrong in the "stalled" direction recycles a
            // Worker that was about to finish, which costs the in-flight work.
            Assert.InRange(WorkerLivenessClassifier.SdkStallAfterMs, 30_000, 600_000);
        }

        [Fact]
        public void The_Diagnosis_Carries_The_Evidence_It_Reached_The_Verdict_From()
        {
            // A verdict with no numbers attached cannot be argued with or re-evaluated,
            // which is what made the original "responsive"/"Healthy" pair unusable.
            var d = WorkerLivenessClassifier.Classify(
                "kb1", transportAlive: true,
                SdkProbe(active: true, sawProgress: true, lastProgressMs: 7_000, op: "build/run"));

            Assert.Equal("build/run", (string)d["sdkOperation"]);
            Assert.Equal(7_000L, (long)d["sdkLastProgressMs"]);
            Assert.Equal(8_000L, (long)d["sdkElapsedMs"]);
            Assert.Equal(WorkerLivenessClassifier.SdkStallAfterMs, (int)d["sdkStallAfterMs"]);
        }

        [Fact]
        public void The_Recovery_Predicate_Reads_Only_The_Sdk_Dimension_It_Judges_On()
        {
            // A transport that reports dead but an sdk value of "idle" must still recover:
            // the predicate must not be satisfiable by the SDK field alone.
            var d = new JObject { ["transport"] = "dead", ["sdk"] = "idle" };

            Assert.True(WorkerLivenessClassifier.Recovers(d));
        }

        [Fact]
        public void The_Verdict_Is_Not_An_Unqualified_Healthy_Anywhere()
        {
            // The issue's acceptance criterion, stated as the absence of the failure: for
            // every combination of probe outcomes, the classification must be reachable
            // without ever concluding "fine" from the transport alone.
            var transports = new[] { true, false };
            var probes = new JObject[]
            {
                SdkProbe(false, false, null),
                SdkProbe(true, true, 1_000),
                SdkProbe(true, true, WorkerLivenessClassifier.SdkStallAfterMs + 1),
                SdkProbe(true, false, null),
                new JObject { ["__timeout"] = true },
                null,
            };

            foreach (bool transport in transports)
                foreach (var probe in probes)
                {
                    var d = WorkerLivenessClassifier.Classify("kb", transport, probe);
                    string sdk = (string)d["sdk"];
                    Assert.False(string.IsNullOrEmpty(sdk));
                    // A live transport is never enough on its own to imply an idle SDK.
                    if (transport && !ReferenceEquals(probe, null))
                        Assert.NotEqual("healthy", sdk);
                }
        }
    }
}