using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Issue #336: the Worker's <c>notifications/resources/updated</c> handler read the
    /// KB's current semantic-cache revision and broadcast it without ever advancing it.
    /// The notification is evidence that an object changed outside this Gateway's own
    /// write path - another client, or the IDE - so the KB's cached reads were stale,
    /// yet they stayed eligible and the broadcast carried the unchanged revision, which
    /// told subscribers nothing had moved.
    ///
    /// <para>
    /// The store primitives are covered by <see cref="SemanticCacheGranularInvalidationTests"/>
    /// and the stale-fill race by the existing dispatch guard. What is new here is the
    /// composition: that a resource-update notification actually drives invalidation,
    /// advances the revision, and leaves other KBs alone.
    /// </para>
    /// </summary>
    public class ResourceUpdateCacheInvalidationTests
    {
        private static void Seed(SemanticCacheStore store, string kb, string tool, string argsJson, JObject value)
        {
            store.Set(kb + "|" + tool + ":" + argsJson, value);
        }

        [Fact]
        public void A_Resource_Update_Invalidates_And_Advances_The_Affected_Kb()
        {
            var store = new SemanticCacheStore(64, TimeSpan.FromMinutes(30));
            Seed(store, "kbalpha", "genexus_read", "{\"name\":\"SyntheticOrder\",\"part\":\"Source\"}",
                new JObject { ["result"] = new JObject { ["source"] = "stale" } });

            long before = store.GetRevision("kbalpha");
            Assert.True(store.TryGet("kbalpha|genexus_read:{\"name\":\"SyntheticOrder\",\"part\":\"Source\"}",
                out JObject _), "precondition: the read was cached");

            // What the handler now does, in the order it does it.
            long after = store.InvalidateScope("kbalpha", out int removed);

            Assert.True(after > before, "the revision must advance, or a subscriber cannot tell anything moved");
            Assert.Equal(1, removed);
            Assert.False(store.TryGet("kbalpha|genexus_read:{\"name\":\"SyntheticOrder\",\"part\":\"Source\"}",
                out JObject _), "the stale entry must be unusable after the notification");
        }

        [Fact]
        public void A_Resource_Update_Leaves_Other_Kbs_Alone()
        {
            // The defect could have been "fixed" with a global wipe, which is both a
            // performance regression and a way for one KB's edit to hide another's
            // cached state behind a spurious miss.
            var store = new SemanticCacheStore(64, TimeSpan.FromMinutes(30));
            Seed(store, "kbalpha", "genexus_read", "{\"name\":\"A\"}", new JObject { ["result"] = 1 });
            Seed(store, "kbbeta", "genexus_read", "{\"name\":\"B\"}", new JObject { ["result"] = 2 });

            long betaBefore = store.GetRevision("kbbeta");
            store.InvalidateScope("kbalpha", out _);

            Assert.Equal(betaBefore, store.GetRevision("kbbeta"));
            Assert.True(store.TryGet("kbbeta|genexus_read:{\"name\":\"B\"}", out JObject beta));
            Assert.Equal(2, (int)beta["result"]!);
        }

        [Fact]
        public void A_Resource_Update_Also_Drops_Derived_Entries_Naming_The_Target()
        {
            // Listings and analyses do not name the mutated object in their arguments,
            // so a scope clear alone can leave them eligible. RemoveByTarget covers
            // them; the handler runs it after the scoped clear.
            var store = new SemanticCacheStore(64, TimeSpan.FromMinutes(30));
            Seed(store, "kbalpha", "genexus_list_objects", "{\"typeFilter\":\"Procedure\"}",
                new JObject { ["result"] = new JArray() });

            int removed = store.RemoveByTarget("kbalpha", "SyntheticOrder");

            Assert.Equal(1, removed);
            Assert.False(store.TryGet("kbalpha|genexus_list_objects:{\"typeFilter\":\"Procedure\"}", out JObject _));
        }

        [Fact]
        public void A_Direct_Read_Of_An_Unrelated_Object_Survives_The_Derived_Pass()
        {
            // The derived pass is targeted, not a blanket clear: a direct read of an
            // object that did not change is still valid, and throwing it away would
            // make the notification a global cache reset.
            var store = new SemanticCacheStore(64, TimeSpan.FromMinutes(30));
            Seed(store, "kbalpha", "genexus_read", "{\"name\":\"Unrelated\",\"part\":\"Source\"}",
                new JObject { ["result"] = new JObject { ["source"] = "still valid" } });
            Seed(store, "kbalpha", "genexus_list_objects", "{\"typeFilter\":\"Procedure\"}",
                new JObject { ["result"] = new JArray() });

            store.RemoveByTarget("kbalpha", "SyntheticOrder");

            Assert.True(store.TryGet("kbalpha|genexus_read:{\"name\":\"Unrelated\",\"part\":\"Source\"}",
                out JObject kept));
            Assert.Equal("still valid", (string)kept["result"]!["source"]!);
        }

        [Fact]
        public void A_Stale_Fill_Cannot_Refill_A_Moved_Generation()
        {
            // The dispatch path captures the revision before a read and refuses to
            // store the result if the revision moved while it was in flight. That guard
            // is only meaningful if something moves the revision - which, before this
            // fix, a resource-update notification did not.
            var store = new SemanticCacheStore(64, TimeSpan.FromMinutes(30));
            long capturedAtDispatch = store.GetRevision("kbalpha");

            store.InvalidateScope("kbalpha", out _);

            Assert.True(store.GetRevision("kbalpha") != capturedAtDispatch,
                "a read that captured the old revision must be rejected by the fill guard");
        }

        [Fact]
        public void An_Unattributable_Update_Falls_Back_To_The_Whole_Store()
        {
            // A change that cannot be attributed to a KB must not be attributed to a
            // narrower one: the empty scope is the store's whole-KB invalidation.
            var store = new SemanticCacheStore(64, TimeSpan.FromMinutes(30));
            Seed(store, "kbalpha", "genexus_read", "{\"name\":\"A\"}", new JObject { ["result"] = 1 });
            Seed(store, "kbbeta", "genexus_read", "{\"name\":\"B\"}", new JObject { ["result"] = 2 });

            store.InvalidateScope(string.Empty, out int removed);

            Assert.Equal(2, removed);
            Assert.False(store.TryGet("kbalpha|genexus_read:{\"name\":\"A\"}", out JObject _));
            Assert.False(store.TryGet("kbbeta|genexus_read:{\"name\":\"B\"}", out JObject _));
        }

        [Fact]
        public void Duplicate_Notifications_Keep_Advancing_Without_Leaking_Entries()
        {
            // The IDE can emit the same update more than once. Each one must advance the
            // revision, and none may resurrect an entry or drive the count negative.
            var store = new SemanticCacheStore(64, TimeSpan.FromMinutes(30));
            Seed(store, "kbalpha", "genexus_read", "{\"name\":\"SyntheticOrder\"}", new JObject { ["result"] = 1 });

            long first = store.InvalidateScope("kbalpha", out int removedFirst);
            long second = store.InvalidateScope("kbalpha", out int removedSecond);
            long third = store.InvalidateScope("kbalpha", out _);

            Assert.Equal(1, removedFirst);
            Assert.Equal(0, removedSecond);
            Assert.True(second > first);
            Assert.True(third > second);
        }

        [Fact]
        public void The_Notification_Handler_Invalidates_Before_It_Broadcasts()
        {
            // The ordering is the fix: broadcasting first would publish a revision that
            // a subscriber compares against and finds unchanged. PatchService-style
            // source inspection, because the ordering lives in an event handler that
            // needs a live Worker frame to exercise.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Gateway", "Program.WorkerLifecycle.cs");

            int notifyAt = source.IndexOf("notifications/resources/updated", StringComparison.Ordinal);
            Assert.True(notifyAt >= 0, "the resource-updated handler was not found");

            int invalidateAt = source.IndexOf("InvalidateCacheScopeForResourceUpdate", notifyAt, StringComparison.Ordinal);
            int broadcastAt = source.IndexOf("BroadcastResourceUpdated", notifyAt, StringComparison.Ordinal);

            Assert.True(invalidateAt > 0, "the handler does not invalidate the cache");
            Assert.True(broadcastAt > 0, "the handler does not broadcast");
            Assert.True(invalidateAt < broadcastAt,
                "the cache must be invalidated before the broadcast, or subscribers are told the revision did not move");

            // And it must broadcast the revision it just produced, not a fresh read of
            // whatever the store happens to hold.
            var broadcastWindow = source.Substring(broadcastAt, Math.Min(300, source.Length - broadcastAt));
            Assert.Contains("updatedRevision", broadcastWindow, StringComparison.Ordinal);
            Assert.DoesNotContain("_semanticCache.GetRevision", broadcastWindow, StringComparison.Ordinal);
        }
    }
}
