using System;
using System.Threading;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// v2.6.6 Stream H (FR#26) — KbHandle active-environment cache. Verifies
    /// the 60s TTL semantics and explicit invalidation hook that
    /// KbWatcherService.OnEnvironmentChanged drives.
    /// </summary>
    public class EnvCacheTtlTests
    {
        [Fact]
        public void ActiveEnvironment_CachesValue_WithinTtl()
        {
            var h = new KbHandle("kb", "C:/KB/Sample");
            int fetchCount = 0;
            h.ConfigureEnvFetcher(() =>
            {
                Interlocked.Increment(ref fetchCount);
                return ("Prototype", "1.0");
            }, TimeSpan.FromSeconds(60));

            string? first = h.ActiveEnvironment;
            string? second = h.ActiveEnvironment;
            string? thirdVer =h.ActiveEnvironmentVersion;

            Assert.Equal("Prototype", first);
            Assert.Equal("Prototype", second);
            Assert.Equal("1.0", thirdVer);
            // Reads within TTL must collapse onto a single fetcher invocation.
            Assert.Equal(1, fetchCount);
            Assert.True(h.IsEnvCacheFresh);
        }

        [Fact]
        public void ActiveEnvironment_RefetchesAfterTtlExpiry()
        {
            var h = new KbHandle("kb", "C:/KB/Sample");
            int fetchCount = 0;
            h.ConfigureEnvFetcher(() =>
            {
                Interlocked.Increment(ref fetchCount);
                return ("Env" + fetchCount, "v" + fetchCount);
            }, TimeSpan.FromMilliseconds(50));

            string? first = h.ActiveEnvironment;
            Assert.Equal("Env1", first);

            Thread.Sleep(120);
            string? second = h.ActiveEnvironment;
            Assert.Equal("Env2", second);
            Assert.Equal(2, fetchCount);
        }

        [Fact]
        public void Invalidate_ForcesRefetchEvenWithinTtl()
        {
            var h = new KbHandle("kb", "C:/KB/Sample");
            int fetchCount = 0;
            h.ConfigureEnvFetcher(() =>
            {
                Interlocked.Increment(ref fetchCount);
                return ("Env" + fetchCount, "v" + fetchCount);
            }, TimeSpan.FromMinutes(10));

            _ = h.ActiveEnvironment;
            Assert.True(h.IsEnvCacheFresh);

            h.Invalidate();
            Assert.False(h.IsEnvCacheFresh);

            string? after =h.ActiveEnvironment;
            Assert.Equal("Env2", after);
            Assert.Equal(2, fetchCount);
        }

        [Fact]
        public void ActiveEnvironment_NullWhenNoFetcherWired()
        {
            var h = new KbHandle("kb", "C:/KB/Sample");
            Assert.Null(h.ActiveEnvironment);
            Assert.Null(h.ActiveEnvironmentVersion);
            Assert.False(h.IsEnvCacheFresh);
        }

        [Fact]
        public void ActiveEnvironment_SwallowsFetcherExceptions()
        {
            var h = new KbHandle("kb", "C:/KB/Sample");
            h.ConfigureEnvFetcher(() => throw new InvalidOperationException("worker offline"),
                TimeSpan.FromMinutes(1));

            // Must not throw and must return null; subsequent reads also tolerate the error.
            Assert.Null(h.ActiveEnvironment);
            Assert.Null(h.ActiveEnvironment);
        }

        [Fact]
        public void KbWatcherInvalidation_ForcesRefetchWithinTtlWindow()
        {
            // Simulates the wire-up: gateway subscribes Invalidate to the worker's
            // OnEnvironmentChanged event. Stream H sets TTL high; environment flip
            // must still invalidate the cache before the next read.
            var h = new KbHandle("kb", "C:/KB/Sample");
            int fetchCount = 0;
            string currentEnv = "Prototype";
            h.ConfigureEnvFetcher(() =>
            {
                Interlocked.Increment(ref fetchCount);
                return (currentEnv, "1.0");
            }, TimeSpan.FromMinutes(10));

            Assert.Equal("Prototype", h.ActiveEnvironment);
            Assert.Equal(1, fetchCount);

            // Worker-side flip — environment switched in IDE.
            currentEnv = "Java";
            Action<string, string> invalidator = (_, __) => h.Invalidate();
            invalidator("Java", "1.0"); // mimics KbWatcherService.OnEnvironmentChanged

            Assert.Equal("Java", h.ActiveEnvironment);
            Assert.Equal(2, fetchCount);
        }
    }
}
