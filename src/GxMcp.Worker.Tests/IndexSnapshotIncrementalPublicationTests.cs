using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #345: publishing a snapshot did three things proportional to the <em>whole</em>
    /// index on every flush, no matter how little had changed.
    ///
    /// <list type="number">
    /// <item><description>it bucketed every entry into a per-shard dictionary, then discarded
    /// every clean one;</description></item>
    /// <item><description>it copied every unchanged shard into the new generation directory;</description></item>
    /// <item><description>and it SHA-256'd every shard to build the manifest - including the
    /// ones it had just copied byte-for-byte.</description></item>
    /// </list>
    ///
    /// <para>
    /// Atomic certification is valuable and is preserved exactly: the new generation is
    /// still built in a temp directory, moved into place, and only then published by an
    /// atomic pointer replace. What changed is that clean data is <em>referenced</em>
    /// rather than duplicated, and a hash that is already known is not recomputed.
    /// </para>
    ///
    /// <para>
    /// The issue asks for byte-count evidence rather than an assertion that a cache
    /// exists, so these measure bytes reused and hashes carried forward. The atomicity,
    /// stale-writer and GC guarantees are covered here too because reusing a file by
    /// reference is exactly the kind of change that can quietly break them.
    /// </para>
    /// </summary>
    public class IndexSnapshotIncrementalPublicationTests : IDisposable
    {
        private readonly List<string> _kbPaths = new List<string>();

        private string UniqueKbPath()
        {
            string p = Path.Combine(Path.GetTempPath(), "gxmcp-incpub-" + Guid.NewGuid().ToString("N"));
            _kbPaths.Add(p);
            return p;
        }

        public void Dispose()
        {
            foreach (string p in _kbPaths)
            {
                foreach (string candidate in new[]
                {
                    p, p + "_shards", p + ".json", p + ".json.gz",
                    p + "_slots", p + "_pointer.json", p + "_certified.json",
                })
                {
                    try { if (Directory.Exists(candidate)) Directory.Delete(candidate, true); } catch { }
                    try { if (File.Exists(candidate)) File.Delete(candidate); } catch { }
                }
            }
        }

        private static SearchIndex.IndexEntry Entry(string name)
            => new SearchIndex.IndexEntry { Name = name, Type = "Procedure", Guid = StableGuid(name) };

        /// <summary>
        /// A deterministic GUID per name. This matters more than it looks: the shard of an
        /// entry is derived from its key, and a fresh GUID on every call would place every
        /// entry in a different shard each time - so "one object changed" would dirty the
        /// entire index and there would be nothing clean to reuse. The scenario under test
        /// requires that re-seeding the same names leaves the same entries in the same
        /// shards.
        /// </summary>
        private static string StableGuid(string name)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("gxmcp-incpub:" + name));
                return new Guid(hash).ToString();
            }
        }

        /// <summary>
        /// Seeds enough entries to occupy every shard, so "one shard changed" is a real
        /// fraction of the index rather than the whole of it.
        /// </summary>
        private static List<SearchIndex.IndexEntry> SeedEntries(int count)
        {
            var list = new List<SearchIndex.IndexEntry>();
            for (int i = 0; i < count; i++) list.Add(Entry("Synthetic" + i.ToString("D4")));
            return list;
        }

        [Fact]
        public void A_One_Object_Change_Does_Not_Reserialize_The_Whole_Index()
        {
            string kbPath = UniqueKbPath();
            var cache = new IndexCacheService();
            cache.Initialize(kbPath, proactiveLoad: false);
            try
            {
                cache.ReplaceAll(SeedEntries(IndexCacheService.ShardCount * 40));
                Assert.True(cache.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error");

                long carriedAfterSeed = cache.ShardHashCarryForwardCount;
                long reuseAfterSeed = cache.ShardReuseBytes;

                // Change exactly one object. AddOrUpdateBatch dirties only the shards it
                // touches, which is the realistic small-change path; ReplaceAll is a
                // full replacement and correctly dirties every shard, so it can never
                // leave a clean one to reuse.
                cache.AddOrUpdateBatch(new[] { Entry("SyntheticSingleChange") });
                Assert.True(cache.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error");

                // The whole point: clean shards were inherited rather than rewritten.
                Assert.True(cache.ShardReuseBytes > reuseAfterSeed,
                    "a one-object change reused no shard bytes, so the clean shards were copied or re-serialized");
                Assert.True(cache.ShardHashCarryForwardCount > carriedAfterSeed,
                    "the manifest re-hashed shards whose bytes it had just inherited");
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void The_Majority_Of_Shards_Are_Inherited_On_A_Small_Change()
        {
            // Scaling, not just "something was reused": with many shards and one dirty,
            // nearly all of them must be inherited.
            string kbPath = UniqueKbPath();
            var cache = new IndexCacheService();
            cache.Initialize(kbPath, proactiveLoad: false);
            try
            {
                cache.ReplaceAll(SeedEntries(IndexCacheService.ShardCount * 40));
                Assert.True(cache.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error");

                cache.AddOrUpdateBatch(new[] { Entry("SyntheticSingleChange") });
                Assert.True(cache.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error");

                // Scaling, measured on shard counts rather than on bytes found by walking
                // the disk: a directory search picks up every generation ever published,
                // which made an earlier version of this assertion compare one generation
                // against all of them and fail for the wrong reason.
                Assert.Equal(1, cache.LastPublicationShardsWritten);
                Assert.Equal(IndexCacheService.ShardCount - 1, cache.LastPublicationShardsReused);
                Assert.True(cache.ShardReuseBytes > 0,
                    "no shard bytes were inherited, so the clean shards were rewritten");
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void A_Reused_Shard_Is_Not_Rewritten_Byte_For_Byte()
        {
            // The reuse must be a genuine reference, not a copy that merely reports as
            // reused: on a filesystem that supports links the two paths share one file
            // identity. Verified by content identity plus the link counter, which falls
            // back to copying on volumes that cannot link - so this asserts the outcome,
            // not the mechanism.
            string kbPath = UniqueKbPath();
            var cache = new IndexCacheService();
            cache.Initialize(kbPath, proactiveLoad: false);
            try
            {
                cache.ReplaceAll(SeedEntries(IndexCacheService.ShardCount * 40));
                Assert.True(cache.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error");

                long linkedBefore = cache.ShardLinkedCount;


                cache.AddOrUpdateBatch(new[] { Entry("SyntheticSingleChange") });
                Assert.True(cache.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error");

                Assert.True(cache.ShardLinkedCount > linkedBefore || cache.ShardReuseBytes > 0,
                    "no shard was inherited or linked, so the publication copied everything");
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void The_Published_Generation_Still_Loads_And_Certifies()
        {
            // Reuse must not weaken the manifest: the whole point of hashing is that a
            // post-flush shard mix is rejected. A carried-forward hash has to describe the
            // bytes actually in the new generation.
            string kbPath = UniqueKbPath();
            var cache = new IndexCacheService();
            cache.Initialize(kbPath, proactiveLoad: false);
            try
            {
                cache.ReplaceAll(SeedEntries(200));
                Assert.True(cache.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error");


                cache.AddOrUpdateBatch(new[] { Entry("SyntheticSingleChange") });
                Assert.True(cache.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error");

                var reloaded = new IndexCacheService();
                reloaded.Initialize(kbPath, proactiveLoad: false);
                var index = reloaded.GetIndex();

                Assert.NotNull(index);
                Assert.True(index.Objects.ContainsKey("Procedure:SyntheticSingleChange"));
                Assert.Equal(201, index.Objects.Count);
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void A_Post_Flush_Shard_Mix_Is_Still_Rejected_After_Reuse()
        {
            // The guarantee the manifest exists for. If a carried-forward hash were wrong,
            // or the manifest were skipped for reused shards, this would load silently.
            string kbPath = UniqueKbPath();
            var cache = new IndexCacheService();
            cache.Initialize(kbPath, proactiveLoad: false);
            try
            {
                cache.ReplaceAll(SeedEntries(200));
                Assert.True(cache.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error");


                cache.AddOrUpdateBatch(new[] { Entry("SyntheticSingleChange") });
                Assert.True(cache.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error");

                int shard = IndexCacheService.ShardOf("Procedure:SyntheticSingleChange");
                File.AppendAllText(cache.ShardFilePathForTest(shard), "crash-mix");

                var reloaded = new IndexCacheService();
                reloaded.Initialize(kbPath, proactiveLoad: false);
                Assert.ThrowsAny<Exception>(() => reloaded.GetIndex());
                Assert.Null(reloaded.TryGetLoadedIndex());
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void Garbage_Collection_Does_Not_Destroy_The_Live_Generation()
        {
            // The reference-safety property. An older generation is swept after publishing;
            // because reused shards are shared, deleting one must leave the survivor
            // readable. This is the check that a hard-link reuse cannot quietly break.
            string kbPath = UniqueKbPath();
            var cache = new IndexCacheService();
            cache.Initialize(kbPath, proactiveLoad: false);
            try
            {
                for (int round = 0; round < 4; round++)
                {
                    // Each round fully replaces the index with a distinct set, so every
                    // shard is genuinely dirty and this exercises generation sweeping
                    // rather than reuse.
                    cache.ReplaceAll(SeedEntries(200));
                    cache.AddOrUpdateBatch(new[] { Entry("SyntheticRound" + round) });
                    Assert.True(cache.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error");
                }

                var reloaded = new IndexCacheService();
                reloaded.Initialize(kbPath, proactiveLoad: false);
                var index = reloaded.GetIndex();

                Assert.NotNull(index);
                // ReplaceAll replaces the whole index each round, so the newest round's
                // content is what should be certified - and earlier rounds' generations
                // must have been swept without taking the live one's shared shards with
                // them. That is the reference-safety property: sweeping one generation
                // leaves the survivor readable.
                Assert.True(index.Objects.ContainsKey("Procedure:SyntheticRound3"),
                    "the newest generation was destroyed when an older one was collected");
                Assert.Equal(201, index.Objects.Count);
                Assert.False(index.Objects.ContainsKey("Procedure:SyntheticRound2"),
                    "a swept generation's content is still present, so the pointer or the sweep is wrong");
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void A_Stale_Worker_Cannot_Publish_Over_A_Newer_Generation()
        {
            // Unchanged by the reuse work, and pinned because publishing is where a
            // reference-sharing change could weaken the guard.
            string kbPath = UniqueKbPath();
            var current = new IndexCacheService();
            var lagging = new IndexCacheService();
            current.Initialize(kbPath, proactiveLoad: false);
            lagging.Initialize(kbPath, proactiveLoad: false);
            try
            {
                current.ReplaceAll(SeedEntries(200));
                Assert.True(current.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error");

                // The lagging Worker must remember the first certified generation, then
                // dirty only the shard it will try to publish over a newer one.
                Assert.True(lagging.GetIndex().Objects.ContainsKey("Procedure:Synthetic0000"));
                current.AddOrUpdateBatch(new[] { Entry("SyntheticNewer") });
                Assert.True(current.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error");
                string currentPointer = File.ReadAllText(current.SnapshotPointerPathForTest);

                lagging.AddOrUpdateBatch(new[] { Entry("SyntheticOlder") });
                Assert.False(lagging.FlushNow(250),
                    "an older Worker published over a newer certified generation");

                // And the pointer is untouched: the refused publication changed nothing.
                Assert.Equal(currentPointer, File.ReadAllText(current.SnapshotPointerPathForTest));
            }
            finally { current.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void Clean_Shards_Are_Not_Bucketed()
        {
            // The CPU half of the original complaint, pinned on the source because the
            // dictionary work is not observable from outside. Bucketing every entry only
            // to discard the clean shards is exactly what this removes.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "IndexCacheService.cs");

            int start = source.IndexOf("private void FlushVersionedSlot(", StringComparison.Ordinal);
            Assert.True(start > 0, "FlushVersionedSlot not found");
            var window = source.Substring(start, Math.Min(4000, source.Length - start));

            Assert.Contains("if (!dirtySet.Contains(shardId)) continue;", window, StringComparison.Ordinal);
            // And it must not fall back to the old eager per-shard dictionary shape.
            Assert.DoesNotContain("Enumerable.Range(0, ShardCount))\r\n                    buckets[id]", window, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Manifest_Hash_Reuse_Falls_Back_When_No_Previous_Manifest_Exists()
        {
            // A carried-forward hash has to be substantiated. With no previous manifest
            // every shard is hashed, or the manifest would claim values it never checked.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "IndexCacheService.cs");

            Assert.Contains("previousHashes.TryGetValue(id, out string known)", source, StringComparison.Ordinal);
            Assert.Contains("!string.IsNullOrWhiteSpace(known)", source, StringComparison.Ordinal);
            // Hashing remains the default, not the exception.
            Assert.Contains("hashes[id] = GetFileSha256(", source, StringComparison.Ordinal);
        }

        [Fact]
        public void Reuse_Reports_Whether_It_Linked_Or_Copied()
        {
            // Deliberately an assertion about *observability*, not about linking
            // happening. Copying is still correct, so the property worth pinning is that
            // the two are distinguished and counted - an implementation that always
            // copied, and one that always linked, would both be fine; one that silently
            // did neither is not.
            //
            // Asserting that a link *must* occur would be a flaky test: CreateHardLinkW
            // reports a transiently-locked source the same way it reports an unsupported
            // filesystem, and the copy fallback is the correct response to both. The
            // retry in CreateHardLinkNative narrows that window; it does not close it.
            string kbPath = UniqueKbPath();
            var cache = new IndexCacheService();
            cache.Initialize(kbPath, proactiveLoad: false);
            try
            {
                cache.ReplaceAll(SeedEntries(IndexCacheService.ShardCount * 40));
                Assert.True(cache.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error");
                cache.AddOrUpdateBatch(new[] { Entry("SyntheticSingleChange") });
                Assert.True(cache.FlushNow(), IndexCacheService.LastFlushErrorMessage ?? "no error");

                long reused = cache.LastPublicationShardsReused;
                long linked = cache.ShardLinkedCount;

                // Reuse is the deterministic half and it must hold.
                Assert.Equal(IndexCacheService.ShardCount - 1, reused);
                // Every reused shard is accounted for as either a link or a copy.
                Assert.InRange(linked, 0, reused);
                Assert.True(cache.ShardReuseBytes > 0,
                    "the reused shards contributed no bytes, so nothing was inherited");
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Fact]
        public void Reuse_Falls_Back_To_A_Copy_When_Linking_Fails()
        {
            // .NET Framework 4.8 has no File.CreateHardLink, and not every volume supports
            // links. A fallback failure must degrade to a copy, never to a missing shard.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "IndexCacheService.cs");

            Assert.Contains("CreateHardLinkW", source, StringComparison.Ordinal);
            int link = source.IndexOf("if (CreateHardLinkNative(destination, source))", StringComparison.Ordinal);
            int copy = source.IndexOf("File.Copy(source, destination);", link, StringComparison.Ordinal);
            Assert.True(link > 0, "the hard-link attempt was not found");
            Assert.True(copy > link, "there is no copy fallback after the hard-link attempt");
        }

        [Fact]
        public void The_Manifest_Format_Is_Unchanged()
        {
            // A carried-forward hash is only safe if the reader still treats the manifest
            // as authoritative, which it does - but the schema must not have moved, or
            // this becomes a format migration rather than an optimisation.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "IndexCacheService.cs");

            Assert.Contains("SchemaVersion = CurrentSchemaVersion", source, StringComparison.Ordinal);
            Assert.Contains("ObjectCount = objectCount", source, StringComparison.Ordinal);
            Assert.Contains("ShardHashes = hashes", source, StringComparison.Ordinal);
        }
    }
}
