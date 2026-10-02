using System;
using System.Collections;
using System.Reflection;
using GxMcp.Gateway;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Bounding the lease registry is only safe if it is invisible to a client. These
    /// tests pin the two published surfaces an eviction could have broken: the
    /// <c>leaseState</c> string reported for an expired lease a session still holds, and
    /// the <c>Expired</c> result that drives explicit-KB recovery. Both depend on the
    /// entry still being present, so both must survive a trim.
    /// </summary>
    public sealed class KbUseLeaseRegistryTrimTests
    {
        [Fact]
        public void AnExpiredLeaseIsStillReportedWhileItsSessionReferencesIt()
        {
            // Uses Program's own registry, store and leaseState mapping, so this asserts
            // the published string end to end rather than a registry-level stand-in.
            string session = "trim-" + Guid.NewGuid().ToString("N");
            try
            {
                Program.SetSessionSelectedKb(session, "orders", "C:/KB/Orders");
                Assert.True(Program.TryGetSessionSnapshotForTest(session, out var snapshot));
                string token = snapshot!.Lease!.Token;

                ExpireInPlace(token);
                Assert.Equal("expired", Program.GetSessionLeaseState(session));

                TrimProgramRegistry();

                // "expired", not "invalid": an evicted entry would fall through Get's
                // null check and be reported as an unknown token.
                Assert.Equal("expired", Program.GetSessionLeaseState(session));
            }
            finally
            {
                Program.ClearSessionSelectedKb(session);
            }
        }

        [Fact]
        public void ATrimmedRegistryDoesNotBreakExplicitKbRecovery()
        {
            var clock = new FakeClock();
            var store = new SessionKbContextStore(TimeSpan.FromMinutes(10));
            var registry = new KbUseLeaseRegistry(clock, store.IsLeaseTokenReferenced);
            string session = "recovery-session";

            var lease = registry.Open(session, "orders", 1, "identity", "open-1", TimeSpan.FromSeconds(10));
            store.Set(session, "orders", "orders", lease);
            clock.Advance(TimeSpan.FromSeconds(11));

            // Trim with a target of 0, so the registry is swept as hard as it can be.
            registry.TrimUnreferencedTerminalEntries(0);

            // Without the entry, Renew reports InvalidToken and Validate raises
            // KB_LEASE_INVALID; the explicit-KB recovery in Program.RequestLoop keys off
            // this exact Expired result to mint a fresh lease.
            var renewal = registry.Renew(lease.Token, session, TimeSpan.FromMinutes(10));
            Assert.Equal(KbUseLeaseOperationStatus.Expired, renewal.Status);
            Assert.Equal(KbUseLeaseState.Expired, renewal.Lease!.State);

            var validation = Assert.Throws<KbLeaseValidationException>(
                () => registry.Validate(lease.Token, session, "orders", 1, "identity"));
            Assert.Equal("KB_LEASE_EXPIRED", validation.Code);
        }

        [Fact]
        public void AnUnreferencedTerminalLeaseIsReclaimed()
        {
            var clock = new FakeClock();
            var store = new SessionKbContextStore(TimeSpan.FromMinutes(10));
            var registry = new KbUseLeaseRegistry(clock, store.IsLeaseTokenReferenced);

            // Closed, and never handed to a session, so no read can reach it.
            var reclaimed = registry.Open("owner-a", "kb-1", 1, "identity-a", "open-1", TimeSpan.FromMinutes(10));
            Assert.Equal(KbUseLeaseOperationStatus.Success, registry.Close(reclaimed.Token, "owner-a").Status);
            var survivor = registry.Open("owner-b", "kb-2", 1, "identity-b", "open-1", TimeSpan.FromMinutes(10));

            int before = registry.Count;
            int removed = registry.TrimUnreferencedTerminalEntries(1);

            Assert.Equal(1, removed);
            Assert.Equal(before - 1, registry.Count);
            Assert.Null(registry.Get(reclaimed.Token));
            // The active lease is never a candidate, whatever the target count is.
            Assert.NotNull(registry.Get(survivor.Token));
        }

        [Fact]
        public void AnExpiredButNeverTouchedLeaseIsAlsoReclaimed()
        {
            var clock = new FakeClock();
            var store = new SessionKbContextStore(TimeSpan.FromMinutes(10));
            var registry = new KbUseLeaseRegistry(clock, store.IsLeaseTokenReferenced);

            var lease = registry.Open("owner-a", "kb-1", 1, "identity-a", "open-1", TimeSpan.FromSeconds(10));

            // Advance the clock without touching the registry. Nothing has called
            // RefreshState for this entry, so it still reads Active in memory even though
            // it expired ten seconds ago. A sweep filtering on State != Active alone
            // cannot see it; only the clock clause can.
            clock.Advance(TimeSpan.FromSeconds(11));
            Assert.Equal(KbUseLeaseState.Active, PeekStateWithoutRefresh(registry, lease.Token));

            int removed = registry.TrimUnreferencedTerminalEntries(0);

            Assert.Equal(1, removed);
            Assert.Null(registry.Get(lease.Token));
        }

        [Fact]
        public void ARegistryWithoutThePredicateBehavesAsBefore()
        {
            var clock = new FakeClock();
            var registry = new KbUseLeaseRegistry(clock);

            var lease = registry.Open("owner-a", "kb-1", 1, "identity-a", "open-1", TimeSpan.FromSeconds(10));
            clock.Advance(TimeSpan.FromSeconds(11));

            // No predicate configured means "assume every token is still referenced",
            // so a trim must be a no-op rather than a guess.
            Assert.Equal(0, registry.TrimUnreferencedTerminalEntries(0));
            Assert.Equal(1, registry.Count);
            Assert.Equal(KbUseLeaseState.Expired, registry.Get(lease.Token)!.State);
            Assert.Equal(KbUseLeaseOperationStatus.Expired, registry.Renew(lease.Token, "owner-a", TimeSpan.FromSeconds(10)).Status);
        }

        [Fact]
        public void OpeningALeaseSweepsOnceTheHighWaterMarkIsCrossed()
        {
            var clock = new FakeClock();
            var store = new SessionKbContextStore(TimeSpan.FromMinutes(10));
            var registry = new KbUseLeaseRegistry(clock, store.IsLeaseTokenReferenced);

            // One lease per KB alias, each closed and unreferenced: the shape of a long
            // lived gateway that keeps being handed fresh KBs.
            for (int i = 0; i < KbUseLeaseRegistry.EntryHighWaterMark + 8; i++)
            {
                string alias = "kb-" + i;
                var lease = registry.Open("owner-" + i, alias, 1, "identity-" + i, "open-" + i, TimeSpan.FromMinutes(10));
                registry.Close(lease.Token, "owner-" + i);
            }

            // The registry stays bounded instead of tracking the 1032 opens: the sweep
            // trims to the low-water mark before each add, so the working set settles at
            // one entry above it and a quarter below the high-water mark that triggers.
            Assert.InRange(registry.Count,
                KbUseLeaseRegistry.EntryLowWaterMark + 1,
                KbUseLeaseRegistry.EntryHighWaterMark + 1);
        }

        private static void TrimProgramRegistry()
        {
            var registry = (KbUseLeaseRegistry)typeof(Program)
                .GetField("_kbLeases", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetValue(null)!;
            registry.TrimUnreferencedTerminalEntries(0);
        }

        /// <summary>
        /// Forces expiry by rewriting ExpiresAt on the live entry, the same seam
        /// Issue192LeaseRecoveryTests uses, because Program's clock is a real Stopwatch.
        /// </summary>
        private static void ExpireInPlace(string token)
        {
            var registry = (KbUseLeaseRegistry)typeof(Program)
                .GetField("_kbLeases", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetValue(null)!;
            var byToken = (IDictionary)typeof(KbUseLeaseRegistry)
                .GetField("_byToken", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(registry)!;
            var entry = byToken[token]!;
            entry.GetType().GetField("ExpiresAt", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(entry, TimeSpan.Zero);
        }

        /// <summary>Reads the stored state field directly, without triggering RefreshState.</summary>
        private static KbUseLeaseState PeekStateWithoutRefresh(KbUseLeaseRegistry registry, string token)
        {
            var byToken = (IDictionary)typeof(KbUseLeaseRegistry)
                .GetField("_byToken", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(registry)!;
            var entry = byToken[token]!;
            return (KbUseLeaseState)entry.GetType()
                .GetField("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .GetValue(entry)!;
        }

        private sealed class FakeClock : IMonotonicClock
        {
            public TimeSpan Now { get; private set; }
            public void Advance(TimeSpan amount) => Now += amount;
        }
    }
}