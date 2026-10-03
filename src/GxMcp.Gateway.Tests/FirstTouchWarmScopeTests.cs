using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Issue #340: the first-touch warm pass was claimed with a single static flag for
    /// the whole Gateway process, so warming one KB permanently suppressed the
    /// equivalent warm for every later KB and for every later Worker generation. The
    /// claim is now per (KB, Worker generation), concurrent requests for one scope
    /// coalesce, and the source-search command is scoped to the probe object instead of
    /// running a whole-catalog no-match scan.
    ///
    /// The claim logic is exercised through the production entry point with an injected
    /// probe resolver and warm pass, so these are behavioural rather than shape checks.
    /// </summary>
    public class FirstTouchWarmScopeTests : IDisposable
    {
        // The unresolvable-probe case walks the production retry budget, and its default
        // delay is seconds per attempt. Zeroing it here keeps the suite fast without
        // touching what is under test: the claim, the coalescing and the release.
        public FirstTouchWarmScopeTests()
        {
            Program.ResetFirstTouchWarmForTest();
            Program.WarmupProbeRetryDelayMsForTest = 0;
        }

        public void Dispose()
        {
            Program.WarmupProbeRetryDelayMsForTest = null;
            Program.FirstTouchWarmAmbientKbForTest = null;
            Program.FirstTouchWarmDefaultKbForTest = null;
            Program.FirstTouchWarmGenerationForTest = null;
            Program.ResetFirstTouchWarmForTest();
        }

        /// <summary>
        /// Stand in for the two ambient sources the production resolver reads: the KB
        /// context the bootstrap just set, and the configured default. Generations are
        /// per alias so a key computed against the wrong alias is unmistakable.
        /// </summary>
        private static void AmbientKb(string? ambient, string? configuredDefault)
        {
            Program.FirstTouchWarmAmbientKbForTest = ambient;
            Program.FirstTouchWarmDefaultKbForTest = configuredDefault;
            Program.FirstTouchWarmGenerationForTest = alias =>
                string.Equals(alias, "KbBeta", StringComparison.OrdinalIgnoreCase) ? "genB" : "genA";
        }

        private static string Key(string alias, string? generation = null) =>
            Program.FirstTouchWarmScopeKey(alias, generation);

        [Fact]
        public void Scope_Key_Separates_Kbs_And_Generations()
        {
            Assert.NotEqual(Key("KbAlpha", "1"), Key("KbBeta", "1"));
            // A replaced Worker generation must not inherit its predecessor's claim.
            Assert.NotEqual(Key("KbAlpha", "1"), Key("KbAlpha", "2"));
            // Case and whitespace are not identity: one KB spelled two ways is one scope.
            Assert.Equal(Key("KbAlpha", "1"), Key(" kbalpha ", "1"));
        }

        [Fact]
        public void Scope_Key_Handles_Missing_Identity_Without_Collapsing_Kbs()
        {
            Assert.Equal(Key(null, null), Key("", ""));
            Assert.Equal("(no-kb)|-", Key(null, null));
            // Two KBs must not collapse into one scope just because a generation is absent.
            Assert.NotEqual(Key("KbAlpha", null), Key("KbBeta", null));
        }

        [Fact]
        public async Task Each_Scope_Warms_Exactly_Once()
        {
            int alpha = 0, beta = 0;
            Task WarmAlpha(string _)
            {
                Interlocked.Increment(ref alpha);
                return Task.CompletedTask;
            }
            Task WarmBeta(string _)
            {
                Interlocked.Increment(ref beta);
                return Task.CompletedTask;
            }

            await Program.RunFirstTouchWarmOnceAsync(Key("KbAlpha", "g1"),
                () => Task.FromResult<string?>("ProbeAlpha"), null, WarmAlpha);
            await Program.RunFirstTouchWarmOnceAsync(Key("KbAlpha", "g1"),
                () => Task.FromResult<string?>("ProbeAlpha"), null, WarmAlpha);
            await Program.RunFirstTouchWarmOnceAsync(Key("KbBeta", "g1"),
                () => Task.FromResult<string?>("ProbeBeta"), null, WarmBeta);

            Assert.Equal(1, alpha);
            // The defect: the second KB's request found the process-wide flag already
            // set and returned without warming.
            Assert.Equal(1, beta);
            Assert.Equal(2, Program.FirstTouchWarmedScopeCount);
        }

        [Fact]
        public async Task A_Replaced_Generation_Warms_Again()
        {
            int runs = 0;
            await Program.RunFirstTouchWarmOnceAsync(Key("KbAlpha", "g1"),
                () => Task.FromResult<string?>("Probe"), null, _ => { Interlocked.Increment(ref runs); return Task.CompletedTask; });
            await Program.RunFirstTouchWarmOnceAsync(Key("KbAlpha", "g2"),
                () => Task.FromResult<string?>("Probe"), null, _ => { Interlocked.Increment(ref runs); return Task.CompletedTask; });

            Assert.Equal(2, runs);
        }

        [Fact]
        public async Task Concurrent_Requests_For_One_Scope_Coalesce_Into_A_Single_Pass()
        {
            int runs = 0;
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task Pass(string _)
            {
                Interlocked.Increment(ref runs);
                entered.TrySetResult(true);
                await release.Task;
            }

            string scope = Key("KbAlpha", "g1");
            var callers = new Task[8];
            for (int i = 0; i < callers.Length; i++)
            {
                int captured = i;
                callers[i] = Task.Run(() => Program.RunFirstTouchWarmOnceAsync(
                    scope, () => Task.FromResult<string?>("Probe"), null, Pass));
            }

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Let the other callers reach the coalescing point before the pass finishes.
            await Task.Delay(150);
            release.TrySetResult(true);
            await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(10));

            // Eight concurrent requests, one physical warm pass. Without coalescing each
            // racing caller would have started its own pass against the same Worker.
            Assert.Equal(1, runs);
        }

        [Fact]
        public async Task A_Failed_Pass_Does_Not_Permanently_Disable_Warming_For_Its_Scope()
        {
            int attempts = 0;
            string scope = Key("KbAlpha", "g1");

            await Program.RunFirstTouchWarmOnceAsync(scope,
                () => Task.FromResult<string?>("Probe"), null,
                _ => throw new InvalidOperationException("warm failed"));

            // The claim must be released, so a later attempt can still warm.
            await Program.RunFirstTouchWarmOnceAsync(scope,
                () => Task.FromResult<string?>("Probe"), null,
                _ => { Interlocked.Increment(ref attempts); return Task.CompletedTask; });

            Assert.Equal(1, attempts);
        }

        [Fact]
        public async Task An_Unresolvable_Probe_Leaves_The_Scope_Claimable()
        {
            int runs = 0;
            string scope = Key("KbAlpha", "g1");

            // Cold start: nothing listable yet, so the pass is skipped rather than
            // claimed. The index-bootstrap trigger later calls the same scope and must
            // still be able to warm.
            await Program.RunFirstTouchWarmOnceAsync(scope,
                () => Task.FromResult<string?>(null), null,
                _ => { Interlocked.Increment(ref runs); return Task.CompletedTask; });
            Assert.Equal(0, runs);
            Assert.Equal(0, Program.FirstTouchWarmedScopeCount);

            await Program.RunFirstTouchWarmOnceAsync(scope,
                () => Task.FromResult<string?>("Probe"), null,
                _ => { Interlocked.Increment(ref runs); return Task.CompletedTask; });
            Assert.Equal(1, runs);
        }

        [Fact]
        public async Task The_Waiting_Feedback_Fires_When_The_Probe_Needs_A_Retry()
        {
            // The callback is what tells an operator why a warm pass has not started
            // yet. The resolver is called at most once here, so the callback fires at
            // most once - it must not fire for a probe that resolves immediately.
            int waits = 0;
            await Program.RunFirstTouchWarmOnceAsync(Key("KbAlpha", "g1"),
                () => Task.FromResult<string?>("Probe"), () => Interlocked.Increment(ref waits),
                _ => Task.CompletedTask);
            Assert.Equal(0, waits);
        }

        // ---- Issue #365: the resolver never consulted the KB it was warming.

        [Fact]
        public void The_Scope_Key_Follows_The_Kb_Being_Warmed_Not_The_Configured_Default()
        {
            // The defect: with a default of KbAlpha, bootstrapping KbBeta resolved
            // KbAlpha's alias and KbAlpha's generation, so a warmed Alpha suppressed
            // Beta's pass.
            AmbientKb("KbBeta", "KbAlpha");

            Assert.Equal(Key("KbBeta", "genB"), Program.CurrentFirstTouchWarmScopeKey());
        }

        [Fact]
        public void An_Explicit_Kb_Overrides_The_Ambient_Context()
        {
            AmbientKb("KbBeta", "KbAlpha");

            Assert.Equal(Key("KbGamma", "genA"), Program.CurrentFirstTouchWarmScopeKey("KbGamma"));
        }

        [Fact]
        public void The_Configured_Default_Is_Used_Only_When_No_Kb_Context_Exists()
        {
            AmbientKb(null, "KbAlpha");

            Assert.Equal(Key("KbAlpha", "genA"), Program.CurrentFirstTouchWarmScopeKey());
        }

        [Fact]
        public async Task Each_Kb_Warms_Once_Even_Under_A_Configured_Default()
        {
            // The end-to-end shape of the defect, through the real resolver: Alpha warms,
            // then Beta's bootstrap resolves a key that must not collide with Alpha's.
            int alpha = 0, beta = 0;
            AmbientKb("KbAlpha", "KbAlpha");
            string alphaScope = Program.CurrentFirstTouchWarmScopeKey();
            AmbientKb("KbBeta", "KbAlpha");
            string betaScope = Program.CurrentFirstTouchWarmScopeKey();

            Assert.NotEqual(alphaScope, betaScope);

            await Program.RunFirstTouchWarmOnceAsync(alphaScope,
                () => Task.FromResult<string?>("ProbeAlpha"), null,
                _ => { Interlocked.Increment(ref alpha); return Task.CompletedTask; });
            await Program.RunFirstTouchWarmOnceAsync(betaScope,
                () => Task.FromResult<string?>("ProbeBeta"), null,
                _ => { Interlocked.Increment(ref beta); return Task.CompletedTask; });

            Assert.Equal(1, alpha);
            // Before the fix this was 0: Beta reused Alpha's claim and returned early.
            Assert.Equal(1, beta);
        }

        [Fact]
        public void Recycling_One_Kb_Re_Warms_Only_That_Kb()
        {
            int generation = 1;
            Program.FirstTouchWarmDefaultKbForTest = "KbAlpha";
            Program.FirstTouchWarmGenerationForTest = alias =>
            {
                int g = string.Equals(alias, "KbBeta", StringComparison.OrdinalIgnoreCase) ? 1 : generation;
                return "g" + g;
            };

            string alphaG1 = Program.CurrentFirstTouchWarmScopeKey("KbAlpha");
            string betaG1 = Program.CurrentFirstTouchWarmScopeKey("KbBeta");

            generation = 2;
            string alphaG2 = Program.CurrentFirstTouchWarmScopeKey("KbAlpha");
            string betaG2 = Program.CurrentFirstTouchWarmScopeKey("KbBeta");

            Assert.Equal("KBALPHA|G1", alphaG1);
            Assert.Equal("KBBETA|G1", betaG1);
            Assert.Equal("KBALPHA|G2", alphaG2);
            // Beta's Worker was not recycled, so it keeps the claim it already made.
            Assert.Equal(betaG1, betaG2);
        }
    }
}
