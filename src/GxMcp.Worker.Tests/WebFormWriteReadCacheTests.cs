using System;
using System.Reflection;
using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// A committed WebForm write must not leave a cached pre-write body behind.
    /// The WebForm body is readable as part=WebForm and as part=Layout, and the
    /// read cache keys each under its own name; marking only "Layout" dirty left
    /// part=WebForm serving the old body (old versionToken) until the Worker restarted.
    /// </summary>
    public class WebFormWriteReadCacheTests
    {
        private static void Seed(string key, string payload)
        {
            typeof(ObjectService)
                .GetMethod("SetReadCache", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { key, payload });
        }

        [Fact]
        public void WebFormWriteDropsEveryCachedReadAliasOfTheObject()
        {
            var guid = Guid.NewGuid();
            string webForm = ObjectService.BuildReadCacheKey(guid, "WebForm", null, null, "mcp", false);
            string layout = ObjectService.BuildReadCacheKey(guid, "Layout", null, null, "mcp", false);
            Seed(webForm, "{\"source\":\"before\"}");
            Seed(layout, "{\"source\":\"before\"}");

            ObjectService.RemoveReadCacheEntries(guid, LayoutService.ReadCachePartToInvalidate(true, null));

            Assert.False(ObjectService.TryGetReadCache(webForm, out _));
            Assert.False(ObjectService.TryGetReadCache(layout, out _));
        }

        [Fact]
        public void OtherVisualSurfacesKeepTheirPartScopedInvalidation()
        {
            Assert.Equal("Layout", LayoutService.ReadCachePartToInvalidate(false, null));
            Assert.Equal("Layout", LayoutService.ReadCachePartToInvalidate(false, "Layout"));
            Assert.Equal("PatternInstance", LayoutService.ReadCachePartToInvalidate(false, "PatternInstance"));
        }

        [Fact]
        public void PartScopedRemovalLeavesOtherObjectsAndPartsCached()
        {
            var guid = Guid.NewGuid();
            var other = Guid.NewGuid();
            string events = ObjectService.BuildReadCacheKey(guid, "Events", null, null, "mcp", false);
            string layout = ObjectService.BuildReadCacheKey(guid, "Layout", null, null, "mcp", false);
            string otherWebForm = ObjectService.BuildReadCacheKey(other, "WebForm", null, null, "mcp", false);
            Seed(events, "{\"source\":\"e\"}");
            Seed(layout, "{\"source\":\"l\"}");
            Seed(otherWebForm, "{\"source\":\"o\"}");
            try
            {
                ObjectService.RemoveReadCacheEntries(guid, "Layout");

                Assert.False(ObjectService.TryGetReadCache(layout, out _));
                Assert.True(ObjectService.TryGetReadCache(events, out _));
                Assert.True(ObjectService.TryGetReadCache(otherWebForm, out _));
            }
            finally
            {
                ObjectService.RemoveReadCacheEntries(guid, null);
                ObjectService.RemoveReadCacheEntries(other, null);
            }
        }
    }
}
