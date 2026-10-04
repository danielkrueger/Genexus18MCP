using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class BoundedStringCacheTests
    {
        [Fact]
        public void TryAdd_EvictsLeastRecentlyUsedItem()
        {
            var cache = new BoundedStringCache(2);

            cache.TryAdd("one", "1");
            cache.TryAdd("two", "2");
            Assert.True(cache.TryGetValue("one", out _));

            cache.TryAdd("three", "3");

            Assert.True(cache.TryGetValue("one", out _));
            Assert.False(cache.TryGetValue("two", out _));
            Assert.True(cache.TryGetValue("three", out _));
        }

        [Fact]
        public void TryRemove_RemovesItemAndSupportsOutValue()
        {
            var cache = new BoundedStringCache(4);
            cache.TryAdd("k1", "v1");
            Assert.True(cache.TryRemove("k1", out var removedVal));
            Assert.Equal("v1", removedVal);
            Assert.False(cache.TryGetValue("k1", out _));
            Assert.False(cache.TryRemove("nonexistent", out _));
        }

        // issue #372: entry count alone does not bound memory.
        [Fact]
        public void TryAdd_EvictsByEstimatedBytesBeforeTheEntryCountIsReached()
        {
            // Budget of 1000 UTF-16 bytes: three 200-char values (400 bytes each) cannot all stay.
            var cache = new BoundedStringCache(512, 1000);
            string big = new string('x', 200);

            cache.TryAdd("a", big);
            cache.TryAdd("b", big);
            cache.TryAdd("c", big);

            Assert.True(cache.EstimatedBytes <= 1000, "estimated bytes " + cache.EstimatedBytes);
            Assert.False(cache.TryGetValue("a", out _));
            Assert.True(cache.TryGetValue("c", out _));
            Assert.True(cache.Evictions >= 1);
        }

        [Fact]
        public void TryAdd_DoesNotCacheAnEntryLargerThanTheWholeBudgetAndKeepsTheRest()
        {
            var cache = new BoundedStringCache(512, 1000);
            cache.TryAdd("small", "v");

            cache.TryAdd("huge", new string('x', 5000));

            Assert.False(cache.TryGetValue("huge", out _));
            Assert.True(cache.TryGetValue("small", out _));
        }

        [Fact]
        public void EstimatedBytes_TracksReplaceRemoveAndClear()
        {
            var cache = new BoundedStringCache(8, 1_000_000);
            cache.TryAdd("k", "1234");
            Assert.Equal(2 * ("k".Length + 4), cache.EstimatedBytes);

            cache.TryAdd("k", "12");
            Assert.Equal(2 * ("k".Length + 2), cache.EstimatedBytes);

            cache.TryRemove("k");
            Assert.Equal(0, cache.EstimatedBytes);

            cache.TryAdd("k", "1234");
            cache.Clear();
            Assert.Equal(0, cache.EstimatedBytes);
        }
    }
}
