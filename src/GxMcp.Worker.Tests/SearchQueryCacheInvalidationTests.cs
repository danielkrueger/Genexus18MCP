using System;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Regression coverage for a stale-read bug in the genexus_query response cache.
    //
    // SearchService's 512-entry LRU has no TTL and was only dropped when index.LastUpdated
    // advanced. But a write never advances LastUpdated: the write path (genexus_edit /
    // genexus_io / pattern apply) goes through IndexCacheService.UpdateEntry, which calls
    // TouchGraph and bumps GraphRevision via Interlocked.Increment. LastUpdated only moves on
    // a bulk walk (AddOrUpdateBatch / ReplaceAll). So a query issued before an edit kept
    // serving the pre-edit answer until its LRU slot was evicted.
    //
    // ListService already keyed its own cache on both counters; this locks SearchService to
    // the same rule so the two can never disagree again.
    public class SearchQueryCacheInvalidationTests
    {
        private static SearchIndex NewIndex(DateTime lastUpdated, long graphRevision)
            => new SearchIndex { LastUpdated = lastUpdated, GraphRevision = graphRevision };

        [Fact]
        public void PristineIndex_NeedsNoFlush_BecauseTheCacheIsAlreadyEmpty()
        {
            // After a reset the observed counters equal a brand-new index's, and the cache
            // holds nothing, so reporting "unchanged" is the correct answer — not a missed
            // invalidation. Asserted so the reset semantics are pinned down.
            SearchService.ResetQueryCacheForTest();

            Assert.False(SearchService.InvalidateQueryCacheIfStale(NewIndex(DateTime.MinValue, 0)));
        }

        [Fact]
        public void UnchangedIndex_KeepsTheCache()
        {
            SearchService.ResetQueryCacheForTest();
            var index = NewIndex(new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc), 7);

            Assert.True(SearchService.InvalidateQueryCacheIfStale(index));
            // A second search against the very same index generation is a hit, not a flush.
            Assert.False(SearchService.InvalidateQueryCacheIfStale(index));
        }

        [Fact]
        public void GraphRevisionBump_Invalidates_EvenThoughLastUpdatedIsUnchanged()
        {
            // This is the bug: the write bumps GraphRevision only. Before the fix the
            // LastUpdated-only comparison returned false and the stale entry survived.
            SearchService.ResetQueryCacheForTest();
            var lastUpdated = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
            var beforeEdit = NewIndex(lastUpdated, 7);

            Assert.True(SearchService.InvalidateQueryCacheIfStale(beforeEdit));
            Assert.False(SearchService.InvalidateQueryCacheIfStale(beforeEdit));

            // genexus_edit lands: UpdateEntry -> TouchGraph. LastUpdated is byte-identical.
            var afterEdit = NewIndex(lastUpdated, 8);
            Assert.Equal(beforeEdit.LastUpdated, afterEdit.LastUpdated);

            Assert.True(SearchService.InvalidateQueryCacheIfStale(afterEdit));
        }

        [Fact]
        public void LastUpdatedAdvance_StillInvalidates()
        {
            // Guards the original behaviour: a bulk walk must keep flushing the cache.
            SearchService.ResetQueryCacheForTest();
            var index = NewIndex(new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc), 3);

            Assert.True(SearchService.InvalidateQueryCacheIfStale(index));
            Assert.True(SearchService.InvalidateQueryCacheIfStale(
                NewIndex(new DateTime(2026, 9, 27, 12, 5, 0, DateTimeKind.Utc), 3)));
        }

        [Fact]
        public void GraphRevisionBump_IsDetected_WhenItMovesBackwards()
        {
            // TouchGraph is a monotonic increment, but a rebuilt index restarts at 1. An
            // inequality check (rather than ">") must still treat that as a change.
            SearchService.ResetQueryCacheForTest();
            var index = NewIndex(new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc), 500);

            Assert.True(SearchService.InvalidateQueryCacheIfStale(index));
            Assert.True(SearchService.InvalidateQueryCacheIfStale(
                NewIndex(new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc), 1)));
        }

        [Fact]
        public void NullIndex_DoesNotThrow()
        {
            SearchService.ResetQueryCacheForTest();

            Assert.False(SearchService.InvalidateQueryCacheIfStale(null));
        }
    }
}
