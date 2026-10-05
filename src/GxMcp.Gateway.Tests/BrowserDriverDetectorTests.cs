using System.Collections.Generic;
using GxMcp.Gateway.Helpers;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class BrowserDriverDetectorTests
    {
        private sealed class FakeProbe : BrowserDriverDetector.IPathProbe
        {
            private readonly Dictionary<string, string> _map;
            public FakeProbe(Dictionary<string, string> map) { _map = map; }
            public string? Which(string command) =>
                _map.TryGetValue(command, out var v) ? v : null;
            public bool FileExists(string path) => !string.IsNullOrEmpty(path);
        }

        [Fact]
        public void Probe_PrefersChromeDevtoolsAxi_WhenBothAvailable()
        {
            var probe = new FakeProbe(new Dictionary<string, string>
            {
                ["chrome-devtools-axi"] = @"C:\node\chrome-devtools-axi.cmd",
                ["npx"] = @"C:\node\npx.cmd"
            });
            var r = BrowserDriverDetector.Probe(probe);
            Assert.Equal(BrowserDriverDetector.DriverKind.ChromeDevtoolsAxi, r.Kind);
            Assert.Equal("chrome-devtools-axi", r.Command);
            Assert.EndsWith("chrome-devtools-axi.cmd", r.ResolvedPath);
            Assert.Null(r.Hint);
        }

        [Fact]
        public void Probe_FallsBackToPlaywright_WhenAxiMissing()
        {
            var probe = new FakeProbe(new Dictionary<string, string>
            {
                ["npx"] = @"C:\node\npx.cmd"
            });
            var r = BrowserDriverDetector.Probe(probe);
            Assert.Equal(BrowserDriverDetector.DriverKind.Playwright, r.Kind);
            Assert.Equal("npx playwright", r.Command);
            Assert.Null(r.Hint);
        }

        [Fact]
        public void Probe_ReturnsNoneWithHint_WhenNeitherAvailable()
        {
            var probe = new FakeProbe(new Dictionary<string, string>());
            var r = BrowserDriverDetector.Probe(probe);
            Assert.Equal(BrowserDriverDetector.DriverKind.None, r.Kind);
            Assert.Null(r.ResolvedPath);
            Assert.Null(r.Command);
            Assert.NotNull(r.Hint);
            Assert.Contains("chrome-devtools-axi", r.Hint);
            Assert.Contains("playwright", r.Hint);
        }

        [Fact]
        public void Probe_AcceptsCmdSuffix_ForAxi()
        {
            var probe = new FakeProbe(new Dictionary<string, string>
            {
                ["chrome-devtools-axi.cmd"] = @"C:\node\chrome-devtools-axi.cmd"
            });
            var r = BrowserDriverDetector.Probe(probe);
            Assert.Equal(BrowserDriverDetector.DriverKind.ChromeDevtoolsAxi, r.Kind);
        }

        [Fact]
        public void Detect_Cached_DoesNotReProbe()
        {
            BrowserDriverDetector.ResetForTests();
            // First call uses default probe → may return whatever real PATH gives.
            var first = BrowserDriverDetector.Detect();
            var second = BrowserDriverDetector.Detect();
            Assert.Same(first, second);
            BrowserDriverDetector.ResetForTests();
        }
    }
}
