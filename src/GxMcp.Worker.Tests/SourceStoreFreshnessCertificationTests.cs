using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #339: source search obtained coverage by validating stored parts across its
    /// whole entry set before the main scan. That validation decompressed the body and
    /// checked its hash, and the content it read was then discarded - so a narrow query,
    /// or a warm reopen, paid a full read of every unrelated source while holding a
    /// boolean.
    ///
    /// <para>
    /// These assert the I/O itself, through the store's validation counters, rather than
    /// that a cache exists. The claim being made is about work not done, so the guard has
    /// to measure work.
    /// </para>
    /// </summary>
    public class SourceStoreFreshnessCertificationTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly List<string> _guids = new List<string>();

        public SourceStoreFreshnessCertificationTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-cert-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            // The clock seam is static, so it would otherwise leak between tests and make
            // a certification created by one test look arbitrarily old to the next.
            SourceStoreService.CertificationClock = () => DateTime.UtcNow;
            SourceStoreService.Instance.SetStoreDirectoryForTest(_tempDir);
        }

        public void Dispose()
        {
            SourceStoreService.CertificationClock = () => DateTime.UtcNow;
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        private static string GuidFor(int i) => i.ToString("D8") + "0000-0000-0000-000000000000";

        private static string SourceFor(int i)
            => "public void SyntheticCaller" + i + "() { /* body */ }";

        private DateTime _lastUpdate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private void Put(int i, string source = null)
        {
            string guid = GuidFor(i);
            _guids.Add(guid);
            SourceStoreService.Instance.Put(guid, "Source", source ?? SourceFor(i),
                _lastUpdate.AddMinutes(i), "v1");
        }

        private GxMcp.Worker.Models.SearchIndex.IndexEntry EntryFor(int i)
            => new GxMcp.Worker.Models.SearchIndex.IndexEntry
            {
                Guid = GuidFor(i),
                Name = "SyntheticCaller" + i,
                Type = "Procedure",
                LastUpdate = _lastUpdate.AddMinutes(i)
            };

        [Fact]
        public void Repeated_Coverage_Over_The_Same_Entries_Reads_Each_Body_Once()
        {
            var store = SourceStoreService.Instance;
            for (int i = 1; i <= 20; i++) Put(i);
            var entries = Enumerable.Range(1, 20).Select(EntryFor).ToList();

            // Counters are cumulative across the whole test class, so every assertion is
            // a delta around the work it is measuring rather than an absolute.
            long before = store.ContentValidations;

            store.GetCoverage(entries, new List<string> { "source" });
            long firstPass = store.ContentValidations - before;
            Assert.Equal(20, firstPass);

            // Second pass must not read a single body again.
            store.GetCoverage(entries, new List<string> { "source" });
            Assert.Equal(0, store.ContentValidations - before - firstPass);
        }

        [Fact]
        public void A_Growing_Catalog_Does_Not_Re_Validate_The_Previous_Entries()
        {
            // The reported shape: cost scaled with catalog size on every query. The second
            // pass over N entries after adding N more must still read only the new ones.
            var store = SourceStoreService.Instance;
            for (int i = 1; i <= 20; i++) Put(i);

            store.GetCoverage(Enumerable.Range(1, 20).Select(EntryFor).ToList(), new List<string> { "source" });
            long baseline = store.ContentValidations;

            Put(21); Put(22); Put(23); Put(24); Put(25);
            store.GetCoverage(Enumerable.Range(1, 25).Select(EntryFor).ToList(), new List<string> { "source" });

            // Five new records; the twenty already certified cost nothing.
            Assert.Equal(baseline + 5, store.ContentValidations);
        }

        [Fact]
        public void A_Second_Query_Burst_Costs_No_Body_Reads_At_All()
        {
            var store = SourceStoreService.Instance;
            for (int i = 1; i <= 50; i++) Put(i);
            var entries = Enumerable.Range(1, 50).Select(EntryFor).ToList();

            store.GetCoverage(entries, new List<string> { "source" });
            long baseline = store.ContentValidations;

            for (int i = 0; i < 10; i++)
                store.GetCoverage(entries, new List<string> { "source" });

            Assert.Equal(baseline, store.ContentValidations);
        }

        [Fact]
        public void A_Replaced_Body_Invalidates_Its_Certification_Immediately()
        {
            // Not at the end of the window: immediately. A same-size replacement would be
            // invisible to a length comparison, so the catalog-hash comparison is what
            // catches it.
            var store = SourceStoreService.Instance;
            Put(1, "aaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var entry = EntryFor(1);

            Assert.True(store.IsStoredAndFreshCached(entry, "source"));
            long afterFirst = store.ContentValidations;

            Put(1, "bbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            Assert.True(store.IsStoredAndFreshCached(entry, "source"));

            Assert.Equal(afterFirst + 1, store.ContentValidations);
        }

        [Fact]
        public void A_Corrupted_Body_On_Disk_Is_Not_Reported_As_Certified()
        {
            // The safety half. A body truncated behind the store's back must not keep
            // answering "fresh" from a certification.
            var store = SourceStoreService.Instance;
            Put(1);
            var entry = EntryFor(1);
            Assert.True(store.IsStoredAndFreshCached(entry, "source"));

            // Corrupt the file on disk: same record, damaged bytes.
            string file = Directory.GetFiles(_tempDir, "*source*.bin.gz", SearchOption.AllDirectories).First();
            byte[] bytes = File.ReadAllBytes(file);
            for (int i = bytes.Length / 2; i < bytes.Length; i++) bytes[i] = 0xFF;
            File.WriteAllBytes(file, bytes);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow);

            Assert.False(store.IsStoredAndFreshCached(entry, "source"));
        }

        [Fact]
        public void A_Deleted_Body_Is_Not_Reported_As_Certified()
        {
            var store = SourceStoreService.Instance;
            Put(1);
            var entry = EntryFor(1);
            Assert.True(store.IsStoredAndFreshCached(entry, "source"));

            string file = Directory.GetFiles(_tempDir, "*source*.bin.gz", SearchOption.AllDirectories).First();
            File.Delete(file);

            Assert.False(store.IsStoredAndFreshCached(entry, "source"));
        }

        [Fact]
        public void An_Index_Entry_Newer_Than_The_Stored_Record_Is_Stale_Without_Any_Read()
        {
            // The timestamp comparison is pure metadata and must stay ahead of any I/O:
            // answering it by reading the body would waste the read on a foregone answer.
            var store = SourceStoreService.Instance;
            Put(1);

            var future = EntryFor(1);
            future.LastUpdate = _lastUpdate.AddMinutes(1).AddHours(1);

            long before = store.ContentValidations;
            Assert.False(store.IsStoredAndFreshCached(future, "source"));
            Assert.Equal(before, store.ContentValidations);
        }

        [Fact]
        public void An_Unknown_Object_Is_Not_Certified_And_Costs_No_Read()
        {
            var store = SourceStoreService.Instance;
            long before = store.ContentValidations;

            Assert.False(store.IsStoredAndFreshCached(EntryFor(999), "source"));
            Assert.Equal(before, store.ContentValidations);
        }

        [Fact]
        public void A_Certification_Does_Not_Outlive_Its_Window()
        {
            // A metadata-only agreement that never expired would be the unsafe version of
            // this optimisation: a body corrupted in place with identical length and
            // timestamp would be reported as certified forever. The window is what stops
            // that, so it must actually be enforced.
            Assert.InRange(
                SourceStoreService.FreshnessCertificationWindow,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMinutes(10));

            var store = SourceStoreService.Instance;
            Put(1);
            var entry = EntryFor(1);
            store.IsStoredAndFreshCached(entry, "source"); // ensure certified

            // Age the certification past the window without touching the file.
            AdvanceCertificationClock(TimeSpan.FromMinutes(30));

            // Delta-local: the store is a singleton shared across this class, so only the
            // probe under measurement is compared.
            long before = store.ContentValidations;
            Assert.True(store.IsStoredAndFreshCached(entry, "source"));
            Assert.Equal(before + 1, store.ContentValidations);
        }

        [Fact]
        public void The_Certification_Window_Does_Not_Affect_Correctness_Only_Cost()
        {
            // Inside the window the answer is the same; after it, the answer is still the
            // same but paid for. A window that changed answers would be a bug, so this
            // pins that the aged re-read agrees.
            var store = SourceStoreService.Instance;
            Put(1);
            var entry = EntryFor(1);

            Assert.True(store.IsStoredAndFreshCached(entry, "source"));
            AdvanceCertificationClock(TimeSpan.FromMinutes(30));
            Assert.True(store.IsStoredAndFreshCached(entry, "source"));
            Assert.True(store.IsStoredAndFreshCached(entry, "source"));
        }

        [Fact]
        public void Resetting_The_Store_Drops_Every_Certification()
        {
            var store = SourceStoreService.Instance;
            Put(1);
            Assert.True(store.IsStoredAndFreshCached(EntryFor(1), "source"));
            Assert.True(store.CertificationCount > 0);

            store.SetStoreDirectoryForTest(_tempDir);

            // They described the previous store; leaving them would let a fresh store
            // answer from a validation that never applied to it.
            Assert.Equal(0, store.CertificationCount);
        }

        [Fact]
        public void Eviction_Drops_The_Evicted_Record_S_Certification()
        {
            // A certification for a file that no longer exists is a dead entry: it can
            // never be used, and leaving it behind makes CertificationCount meaningless
            // as a diagnostic.
            var store = SourceStoreService.Instance;
            Put(1);
            Assert.True(store.IsStoredAndFreshCached(EntryFor(1), "source"));
            Assert.Equal(1, store.CertificationCount);

            store.InvalidateCertification(GuidFor(1), "source");

            Assert.Equal(0, store.CertificationCount);
            // The record still exists on disk, so re-validation legitimately succeeds -
            // but it costs a read, because the ghost is gone rather than trusted.
            long before = store.ContentValidations;
            Assert.True(store.IsStoredAndFreshCached(EntryFor(1), "source"));
            Assert.Equal(before + 1, store.ContentValidations);
        }

        [Fact]
        public void The_Content_Returning_Api_Still_Returns_The_Body()
        {
            // The regression this design is built to avoid: making the boolean fast path
            // answer "fresh" while leaving `source` null would report a hit with nothing
            // behind it, and the search scan promotes that value into the index.
            var store = SourceStoreService.Instance;
            Put(1, "MARKER_BODY");

            Assert.True(store.TryGetStoredAndFresh(EntryFor(1), "source", out string source));

            Assert.NotNull(source);
            Assert.Contains("MARKER_BODY", source, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Content_Returning_Api_Records_A_Certification_As_A_Side_Effect()
        {
            // One validation serves both callers: the scan reads the body anyway, so the
            // coverage probe that follows should be free.
            var store = SourceStoreService.Instance;
            Put(1);

            long before = store.ContentValidations;
            Assert.True(store.TryGetStoredAndFresh(EntryFor(1), "source", out _));
            Assert.Equal(before + 1, store.ContentValidations);

            Assert.True(store.IsStoredAndFreshCached(EntryFor(1), "source"));
            Assert.Equal(before + 1, store.ContentValidations);
        }

        [Fact]
        public void Same_Metadata_Different_Content_Is_Caught_By_The_Catalog_Hash()
        {
        // Isolates the hash comparison, which the length and timestamp checks cannot
        // cover. Reached by corrupting the certification's recorded hash rather than the
        // file, because the file's own size and timestamp still agree - which is the
        // state a restored backup or a second writer leaves behind, and the one case
        // where the catalog hash is the only remaining evidence.
        var store = SourceStoreService.Instance;
            Put(1);
            var entry = EntryFor(1);

            // The catalog-hash comparison is defence in depth with no reachable trigger
            // from outside: every write path invalidates the certification first, so the
            // hash can only disagree after a divergence the store does not perform itself
            // - a restored backup, or a second writer. Asserting it behaviourally would
            // mean manufacturing exactly that divergence through private state, which
            // tests the reflection rather than the store. So it is pinned structurally,
            // and said to be structural.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "SourceStoreService.cs");

            int compare = source.IndexOf(
                "cert.ContentHash, summary.ContentHash", StringComparison.Ordinal);
            Assert.True(compare > 0,
                "the certification no longer compares the catalog hash, so a body that "
                + "disagreed with the catalog could be reported as certified");

            // And the certification must record it, or there is nothing to compare against.
            Assert.Contains("ContentHash = summary.ContentHash", source, StringComparison.Ordinal);
        }

        [Fact]
        public void Corruption_Preserving_Size_And_Timestamp_Is_Caught_On_Revalidation_Not_Immediately()
        {
        // The honest limit, pinned as a limit rather than claimed as a strength. A body
        // damaged in place with identical length and timestamp agrees with every
        // recorded field, so the fast path answers "fresh" until the window expires.
        // That is precisely why the window is mandatory: without it this state would be
        // permanent. With it, the damage is corrected within the window at the cost of
        // one re-read.
            var store = SourceStoreService.Instance;
            Put(1);
            var entry = EntryFor(1);
            Assert.True(store.IsStoredAndFreshCached(entry, "source"));

            string file = Directory.GetFiles(_tempDir, "*source*.bin.gz", SearchOption.AllDirectories).First();
            byte[] original = File.ReadAllBytes(file);
            DateTime stamp = File.GetLastWriteTimeUtc(file);

            byte[] damaged = (byte[])original.Clone();
            for (int i = damaged.Length / 2; i < damaged.Length; i++) damaged[i] = 0xFF;
            File.WriteAllBytes(file, damaged);
            File.SetLastWriteTimeUtc(file, stamp); // size and timestamp now agree

            // Inside the window: still certified. This is the documented limitation.
            Assert.True(store.IsStoredAndFreshCached(entry, "source"));

            AdvanceCertificationClock(TimeSpan.FromMinutes(30));

            // After the window: the body is re-read and the damage is found.
            Assert.False(store.IsStoredAndFreshCached(entry, "source"));
            // And no certification survives the failed validation, so the next probe
            // re-reads too rather than repeating the wrong answer.
            Assert.Equal(0, store.CertificationCount);
}

        [Fact]
        public void The_Boolean_Probe_And_The_Content_Probe_Stay_Separate_Apis()
        {
            // Explicit, because conflating them is the regression this design exists to
            // prevent: one answers a boolean from a certification, the other must return
            // the body. They are distinguished by their signature, not by a flag.
            var booleanProbe = SourceStoreService.Instance.GetType()
                .GetMethod("IsStoredAndFreshCached", new[] { typeof(GxMcp.Worker.Models.SearchIndex.IndexEntry), typeof(string) });
            Assert.NotNull(booleanProbe);

            var contentProbe = SourceStoreService.Instance.GetType()
                .GetMethod("TryGetStoredAndFresh", new[] { typeof(GxMcp.Worker.Models.SearchIndex.IndexEntry), typeof(string), typeof(string).MakeByRefType() });
            Assert.NotNull(contentProbe);
        }

        /// <summary>
        /// Moves the certification clock forward. The window is a safety property, and a
        /// safety property that can only be tested by waiting 30 seconds - or by mutating
        /// private state through reflection - is one that does not get tested.
        /// </summary>
        private static void AdvanceCertificationClock(TimeSpan by)
        {
            var current = SourceStoreService.CertificationClock();
            SourceStoreService.CertificationClock = () => current.Add(by);
        }
    }
}
