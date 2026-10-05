using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class CrossBrowserServiceTests
    {
        [Fact]
        public void Run_EmptyTarget_ReturnsTopLevelError()
        {
            var svc = new CrossBrowserService(runObject: null);
            var j = JObject.Parse(svc.Run("", null, null));
            Assert.Equal("error", (string)j["status"]);
            Assert.Equal("target is required.", (string)j["error"]?["message"]);
        }

        [Fact]
        public void Run_UnknownBrowser_ReturnsPerBrowserUnknownBrowserError()
        {
            // RunObjectService with null deps resolves a URL from the WebRootHook seam (no KB).
            // CrossBrowserService already reads result.url; URL resolves, then the unknown browser
            // is reported as UnknownBrowser in the per-browser results array.
            var runObj = new RunObjectService(objectService: null, kbService: null, previewService: null) { WebRootHook = () => "sample_app_env" };
            var svc = new CrossBrowserService(runObj);

            var j = JObject.Parse(svc.Run("MyPanel", new JArray("explorer42"), null));
            // URL resolves; overall status is ok; per-browser result has UnknownBrowser code.
            Assert.Equal("ok", (string)j["status"]);
            var results = (JArray)j["result"]?["results"];
            Assert.NotNull(results);
            Assert.Contains(results, r => (string)r["code"] == "UnknownBrowser");
        }
    }
}
