using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using GxMcp.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// The read path and the write gate refresh the shared recovery journal
    /// differently, on purpose: a write refused because a peer Gateway held the
    /// journal mid-commit is a false block, while a read is a cache-freshness check
    /// on the hottest path in the product.
    /// </summary>
    /// <remarks>
    /// Two properties, and they are not equally important.
    /// <c>AReadWithAnUnchangedJournalTakesNoCrossProcessLease</c> is the latency
    /// claim. <c>AJournalChangedByAnotherProcessIsStillPickedUpOnTheReadPath</c> is
    /// the correctness claim: without it the optimisation would serve a cached answer
    /// while a fence written elsewhere went unseen, and the suite would stay green.
    /// A change that keeps the first green while breaking the second has made the
    /// product wrong and fast.
    /// </remarks>
    public class MutationRecoveryRefreshPolicyTests
    {
        // Generous on purpose: these bound a *stall*, and a loaded machine is slow.
        // The production bound is MutationRecoveryRegistry.ReadPathLeaseBudgetMs, and
        // every assertion here is at least an order of magnitude above it so a slow
        // run cannot turn a timing guard into a flake.
        private const int UnblockedBudgetMs = 1000;
        private const int StillBlockedProbeMs = 300;

        [Fact]
        public async Task AReadWithAnUnchangedJournalTakesNoCrossProcessLease()
        {
            using var fixture = new PeerJournal();
            // The write gate's registry is built first so the registry under test is
            // the last thing constructed: the change signal also re-reads the journal
            // on a timer, and a call made later than that timer is a scheduled
            // recheck rather than the unchanged-journal case this is about.
            var writeGateRegistry = new MutationRecoveryRegistry(fixture.Path);
            var readRegistry = new MutationRecoveryRegistry(fixture.Path);
            readRegistry.Refresh();

            using FileStream heldByAnotherProcess = fixture.HoldLease();
            var stopwatch = Stopwatch.StartNew();
            readRegistry.RefreshIfChanged();
            stopwatch.Stop();

            // The claim is about the lease, not about elapsed time: a refresh that
            // lost the race would latch the busy flag, so still being healthy is
            // proof the lease was never taken rather than taken quickly.
            Assert.True(readRegistry.IsHealthy,
                "an unchanged journal must not be behind a lease another process holds");
            Assert.True(stopwatch.ElapsedMilliseconds < UnblockedBudgetMs,
                "the read path waited " + stopwatch.ElapsedMilliseconds
                    + " ms for a lease held by another process");
            // And the state it is guarding is still the state on disk.
            Assert.True(readRegistry.TryGet("kb", "Peer", "Source", out var seen));
            Assert.Equal(fixture.OperationId, seen.OperationId);

            // Same held lease, the write gate's route: the lease really is exclusive,
            // so the unconditional refresh has to wait for it. This is what the read
            // path above is avoiding, asserted so the contrast is not a claim about
            // a file nobody is holding.
            Task writeGate = Task.Run(() => writeGateRegistry.Refresh());
            Assert.NotSame(writeGate, await Task.WhenAny(writeGate, Task.Delay(StillBlockedProbeMs)));
            heldByAnotherProcess.Dispose();
            Assert.Same(writeGate, await Task.WhenAny(writeGate,
                Task.Delay(MutationRecoveryRegistry.JournalLeaseBudgetMs + StillBlockedProbeMs)));
        }

        [Fact]
        public void AJournalChangedByAnotherProcessIsStillPickedUpOnTheReadPath()
        {
            using var fixture = new PeerJournal();
            var registry = new MutationRecoveryRegistry(fixture.Path);
            Assert.True(registry.TryGet("kb", "Peer", "Source", out var seeded));
            Assert.Equal("peer-op-1", seeded.OperationId);

            // A peer commits a fence. The new one is the same byte length as the old,
            // so only a timestamp comparison can see it - and the second commit below
            // pins the timestamp instead, so only the length comparison can see that
            // one. Either half of the signal on its own fails this test.
            fixture.CommitPeerFence("peer-op-2");
            registry.RefreshIfChanged();
            Assert.True(registry.TryGet("kb", "Peer", "Source", out var byTimestamp));
            Assert.Equal("peer-op-2", byTimestamp.OperationId);

            DateTime stampTheReaderJustRecorded = File.GetLastWriteTimeUtc(fixture.Path);
            fixture.CommitPeerFence("peer-op-2-with-a-longer-operation-id", stampTheReaderJustRecorded);
            registry.RefreshIfChanged();
            Assert.True(registry.TryGet("kb", "Peer", "Source", out var byLength));
            Assert.Equal("peer-op-2-with-a-longer-operation-id", byLength.OperationId);
        }

        [Fact]
        public void TheWriteGateStillRefreshesUnconditionally()
        {
            // The source shape, because the asymmetry is the thing being protected: a
            // reviewer cannot tell from behaviour alone which branch stopped paying
            // for a lock, and the next reader of this file is the one most likely to
            // "simplify" the two call sites into one.
            string body = SourceAssert.NormaliseNewlines(RepoSource.WithoutComments(
                "src", "GxMcp.Gateway", "Program.ToolDispatch.cs"));
            string dispatch = SourceAssert.MethodBody(body,
                "private static async Task<JObject> DispatchToolCallCoreAsync(");

            // Mutually exclusive needles: "Refresh();" cannot occur inside
            // "RefreshIfChanged();", so each of these matches exactly one call site.
            const string Unconditional = "_mutationRecovery.Refresh();";
            const string Conditional = "_mutationRecovery.RefreshIfChanged();";
            int writeGate = dispatch.IndexOf(Unconditional, StringComparison.Ordinal);
            int readPath = dispatch.IndexOf(Conditional, StringComparison.Ordinal);
            Assert.True(writeGate >= 0, "the write gate must keep the unconditional refresh");
            Assert.True(readPath >= 0, "the read path must use the conditional refresh");
            Assert.Equal(1, SourceAssert.Count(dispatch, Unconditional));
            Assert.Equal(1, SourceAssert.Count(dispatch, Conditional));
            // Exactly two refresh call sites in the dispatch method, so a third one
            // cannot appear without changing the count that is asserted here.
            Assert.Equal(2, SourceAssert.Count(dispatch, "_mutationRecovery.Refresh"));

            // Each call site is introduced by its own branch, which is what makes the
            // two needles specific rather than merely present.
            Assert.StartsWith("if (", EnclosingBranch(dispatch, writeGate), StringComparison.Ordinal);
            Assert.StartsWith("if (", EnclosingBranch(dispatch, readPath), StringComparison.Ordinal);
            Assert.Contains("isMutating && !IsMutationPreview", EnclosingBranch(dispatch, writeGate), StringComparison.Ordinal);
            Assert.Contains("genexus_read", EnclosingBranch(dispatch, readPath), StringComparison.Ordinal);
        }

        [Fact]
        public async Task AContendedReadDoesNotStallForSeconds()
        {
            using var fixture = new PeerJournal();
            var writeGateRegistry = new MutationRecoveryRegistry(fixture.Path);
            var readRegistry = new MutationRecoveryRegistry(fixture.Path);
            // A peer commits, so the read path has to take the lease - and finds it
            // held by someone else.
            fixture.CommitPeerFence("peer-op-2");

            using FileStream heldByAnotherProcess = fixture.HoldLease();
            var stopwatch = Stopwatch.StartNew();
            readRegistry.RefreshIfChanged();
            stopwatch.Stop();

            Assert.True(stopwatch.ElapsedMilliseconds < UnblockedBudgetMs,
                "a contended read waited " + stopwatch.ElapsedMilliseconds
                    + " ms for a lease; the read path's bound is "
                    + MutationRecoveryRegistry.ReadPathLeaseBudgetMs + " ms");
            // Which way it gave up is the part that has to be right. Reporting the
            // journal untrusted is the fail-safe direction: Program.ToolDispatch
            // turns it into `isLiveTool |= !IsHealthy`, so the read goes to the worker
            // rather than being answered from a cache that cannot vouch for the
            // journal. Silently skipping the reload would be the other direction -
            // still "healthy", still countable as zero, cached answer served.
            Assert.False(readRegistry.IsHealthy,
                "an unresolved lease must leave the journal untrusted, not silently trusted");
            Assert.True(readRegistry.JournalError.StartsWith("Mutation recovery journal busy", StringComparison.Ordinal),
                readRegistry.JournalError);

            // The write gate, same held lease: it keeps the full budget, because a
            // write that gives up early is the false block the asymmetry avoids.
            Task writeGate = Task.Run(() => writeGateRegistry.Refresh());
            Assert.NotSame(writeGate, await Task.WhenAny(writeGate, Task.Delay(StillBlockedProbeMs)));
            heldByAnotherProcess.Dispose();
            Assert.Same(writeGate, await Task.WhenAny(writeGate,
                Task.Delay(MutationRecoveryRegistry.JournalLeaseBudgetMs + StillBlockedProbeMs)));
        }

        /// <summary>
        /// The code lines immediately above a call site, up to the <c>if</c> that
        /// introduces it.
        /// </summary>
        /// <remarks>
        /// A fixed-size window would not do: this source carries comments explaining
        /// the asymmetry above both call sites, and <c>RepoSource.WithoutComments</c>
        /// blanks those to spaces, so a window wide enough to reach the branch would
        /// be mostly padding whose length depends on how long the comment happens to
        /// be. Walking back over blank lines until the <c>if</c> is immune to both.
        /// </remarks>
        private static string EnclosingBranch(string dispatch, int at)
        {
            string[] lines = dispatch.Substring(0, at).Split('\n');
            var branch = new List<string>();
            for (int i = lines.Length - 1; i >= 0 && branch.Count < 6; i--)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                branch.Insert(0, line);
                if (line.StartsWith("if (", StringComparison.Ordinal)) break;
            }
            return string.Join("\n", branch);
        }

        /// <summary>
        /// A journal file owned by "another process": every commit goes through a
        /// candidate file renamed over the journal, the way PersistJournal commits, so
        /// the read path is confronted with a peer's commit rather than an in-place
        /// edit that no metadata check would ever see.
        /// </summary>
        private sealed class PeerJournal : IDisposable
        {
            private readonly string _directory;
            private string _operationId;

            internal string Path { get; }

            /// <summary>The operation id currently committed to disk.</summary>
            internal string OperationId => _operationId;

            internal PeerJournal()
            {
                _directory = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "gxmcp-refresh-policy-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_directory);
                Path = System.IO.Path.Combine(_directory, "recovery.json");
                _operationId = "peer-op-1";
                // Seeded through the registry, so the bytes on disk are the format a
                // real writer produces rather than one this fixture invented.
                new MutationRecoveryRegistry(Path).RequireRead("kb", "Peer", "Source", _operationId);
            }

            /// <summary>
            /// Replaces the committed fence with a new one, committed atomically.
            /// </summary>
            /// <param name="operationId">The fence to commit.</param>
            /// <param name="lastWriteUtc">
            /// The timestamp to leave on the file. NTFS stamps a write from a coarse
            /// system clock, so a commit landing microseconds after the one the reader
            /// loaded can share its timestamp; a peer that took a measurable amount of
            /// time would be seen. Callers that mean to isolate one half of the change
            /// signal pass an explicit stamp, and the default just moves the clock on.
            /// </param>
            internal void CommitPeerFence(string operationId, DateTime? lastWriteUtc = null)
            {
                string updated = File.ReadAllText(Path).Replace(_operationId, operationId);
                Assert.NotEqual(File.ReadAllText(Path), updated);
                _operationId = operationId;
                string candidate = Path + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(candidate, updated);
                File.Move(candidate, Path, overwrite: true);
                File.SetLastWriteTimeUtc(Path, lastWriteUtc
                    ?? new DateTime(File.GetLastWriteTimeUtc(Path).Ticks + TimeSpan.TicksPerSecond, DateTimeKind.Utc));
            }

            /// <summary>
            /// Takes the cross-process lease the registry would take, from outside it.
            /// </summary>
            internal FileStream HoldLease()
                => new FileStream(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            public void Dispose()
            {
                try { Directory.Delete(_directory, recursive: true); } catch { }
            }
        }
    }
}
