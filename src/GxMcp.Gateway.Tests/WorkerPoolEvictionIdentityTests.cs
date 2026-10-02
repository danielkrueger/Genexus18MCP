using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Capacity eviction must remove the entry it selected, not whatever entry happens to
    /// hold the alias by the time it gets there.
    ///
    /// <para>
    /// <see cref="WorkerPool.EvictEntry"/> stops the victim's Worker, so the plain
    /// <c>TryRemove(alias)</c> it used to call removed the alias unconditionally. The
    /// victim's own <c>OnWorkerExited</c> handler removes from <c>_entries</c> without
    /// taking the capacity lock, and <c>AcquireAsync</c>'s <c>GetOrAdd</c> does not take
    /// it either, so a worker exiting between <c>SelectVictim()</c> and the removal let a
    /// concurrent acquire install a replacement. The eviction then dropped the
    /// replacement's entry and never touched its process: an untracked
    /// <c>GxMcp.Worker.exe</c>, invisible to <c>ListOpen()</c>/<c>Snapshot()</c>/whoami
    /// and unreachable by <c>StopAll()</c>.
    /// </para>
    ///
    /// <para>
    /// The interleaving is reproduced through the real code paths — the pool's own exit
    /// handler and a real <c>AcquireAsync</c> — with no real process started, so the
    /// window is deterministic rather than raced for.
    /// </para>
    /// </summary>
    public sealed class WorkerPoolEvictionIdentityTests
    {
        private static Configuration Cfg(int maxOpenKbs = 3) =>
            new Configuration { Server = new ServerConfig { MaxOpenKbs = maxOpenKbs } };

        /// <summary>
        /// A Worker the pool treats as alive. <c>exitConfirmed: false</c> keeps the stop
        /// path from firing the exit notification (and writing a crash-ledger record), so
        /// the only thing these tests observe is whether a stop was requested.
        /// </summary>
        private static WorkerProcess LiveWorker(Configuration cfg, KbHandle handle)
        {
            var worker = new WorkerProcess(cfg, handle);
            worker.SetProcessStateForTest(alive: true, exitConfirmed: false);
            return worker;
        }

        /// <summary>
        /// Reproduces the eviction race end to end and returns the pool in the
        /// post-eviction state.
        ///
        /// <ol>
        /// <item>The victim is live under its alias and is the entry the capacity scan
        /// selects; the capacity window captures it.</item>
        /// <item>Its Worker exits. The <c>OnWorkerExited</c> handler wired in
        /// <c>SpawnWorkerAsync</c> removes that entry from <c>_entries</c> without taking
        /// <c>_capacityLock</c> — the window the unconditional removal fell into.</item>
        /// <li>A concurrent <c>AcquireAsync</c> <c>GetOrAdd</c>s a replacement entry and
        /// spawns a fresh Worker for the same alias.</item>
        /// <li>The stale eviction now acts on the entry captured in step 1.</li>
        /// </ol>
        /// </summary>
        private static async Task<(WorkerPool pool, KbHandle handle, WorkerProcess victim, WorkerProcess replacement)>
            PoolAfterStaleEvictionAsync()
        {
            var cfg = Cfg();
            var handle = new KbHandle("victim", "C:/Victim");
            var pool = new WorkerPool(cfg);
            pool.SpawnFactoryForTest = h => LiveWorker(cfg, h);

            var victim = await pool.AcquireAsync(handle, CancellationToken.None);
            Assert.Same(handle.Alias, pool.SelectEvictableVictimForTest()?.Alias);
            object captured = pool.CaptureEntryForTest(handle.Alias);

            victim.SimulateUnexpectedExitForTest();
            // The alias really was released, so the acquire below installs a new entry
            // rather than reusing the victim's.
            Assert.Null(pool.TryGet(handle.Alias));

            var replacement = await pool.AcquireAsync(handle, CancellationToken.None);
            Assert.NotSame(victim, replacement);

            pool.EvictCapturedEntryForTest(captured);

            return (pool, handle, victim, replacement);
        }

        [Fact]
        public async Task EvictingAStaleEntryDoesNotRemoveAReplacement()
        {
            var (pool, handle, _, replacement) = await PoolAfterStaleEvictionAsync();

            // The eviction acted on the entry it selected, so the replacement that took
            // the alias afterwards must still be reachable through it.
            Assert.Same(replacement, pool.TryGet(handle.Alias));
            Assert.Contains(pool.ListOpen(), h => h.Alias == handle.Alias);
        }

        [Fact]
        public async Task EvictingTheCurrentEntryStillRemovesIt()
        {
            // The unconditional removal was only wrong for a replacement. Evicting the
            // entry that really is registered has to keep working, or the capacity window
            // would never free a slot.
            var cfg = Cfg();
            var handle = new KbHandle("idle", "C:/Idle");
            var pool = new WorkerPool(cfg);
            pool.SpawnFactoryForTest = h => LiveWorker(cfg, h);

            var worker = await pool.AcquireAsync(handle, CancellationToken.None);
            Assert.Same(worker, pool.TryGet(handle.Alias));

            pool.EvictCapturedEntryForTest(pool.CaptureEntryForTest(handle.Alias));

            Assert.Null(pool.TryGet(handle.Alias));
            Assert.DoesNotContain(pool.ListOpen(), h => h.Alias == handle.Alias);
            Assert.DoesNotContain(pool.GetKnownAliases(), a => a == handle.Alias);
            // Stopped, not just deregistered.
            Assert.True(worker.CancellationRequestedForTest);
        }

        [Fact]
        public async Task AWorkerWhoseEntryWasReplacedIsNotStoppedByTheStaleEviction()
        {
            // The point of the bug was a lost process handle, not a lost dictionary entry:
            // the eviction stops the Worker it holds, so the replacement's process was
            // never stopped - it was simply no longer reachable by anything that could
            // stop it. Assert on the Worker's liveness, then on the shutdown path.
            var (pool, _, victim, replacement) = await PoolAfterStaleEvictionAsync();

            // The victim was the entry being evicted, so its Worker is the one stopped -
            // even though its exit already tore the entry out of the pool.
            Assert.True(victim.CancellationRequestedForTest);
            Assert.False(replacement.CancellationRequestedForTest);

            // And the replacement's process is still reapable by the real shutdown path,
            // which is what the bug destroyed.
            pool.StopAll();
            Assert.True(replacement.CancellationRequestedForTest);
        }

        [Fact]
        public void StopAllReachesEveryStillRegisteredEntry()
        {
            // StopAll iterates _entries and nothing else, so a Worker whose entry is no
            // longer registered is unreachable by it. Pin the half that bounds the orphan
            // scenario: every entry that IS still registered is stopped on shutdown.
            var cfg = Cfg(maxOpenKbs: 2);
            var pool = new WorkerPool(cfg);
            var first = new KbHandle("first", "C:/First");
            var second = new KbHandle("second", "C:/Second");
            var firstWorker = LiveWorker(cfg, first);
            var secondWorker = LiveWorker(cfg, second);
            pool.RegisterForTest(first, worker: firstWorker);
            pool.RegisterForTest(second, worker: secondWorker);
            Assert.True(pool.IsAtCapacity());

            pool.StopAll();

            Assert.True(firstWorker.CancellationRequestedForTest);
            Assert.True(secondWorker.CancellationRequestedForTest);
            Assert.Empty(pool.ListOpen());
            Assert.Empty(pool.Snapshot());
            Assert.False(pool.IsAtCapacity());
        }
    }
}