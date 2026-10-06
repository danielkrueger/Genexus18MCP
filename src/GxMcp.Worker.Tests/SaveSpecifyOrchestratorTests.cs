using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SaveSpecifyOrchestratorTests
    {
        [Theory]
        [InlineData(-10, 1)]
        [InlineData(0, 1)]
        [InlineData(30, 30)]
        [InlineData(999, 120)]
        public void ClampSpecifyTimeout_StaysWithinWorkerBudget(int requested, int expected)
        {
            Assert.Equal(expected, SaveSpecifyOrchestrator.ClampSpecifyTimeout(requested));
        }

        [Theory]
        [InlineData("{\"taskId\":\"task-1\"}", "task-1")]
        [InlineData("{\"result\":{\"taskId\":\"task-2\"}}", "task-2")]
        [InlineData("{\"status\":\"error\"}", null)]
        [InlineData("not-json", null)]
        public void ExtractTaskId_HandlesCanonicalAndNestedAcceptedEnvelopes(string json, string expected)
        {
            Assert.Equal(expected, SaveSpecifyOrchestrator.ExtractTaskId(json));
        }

        [Fact]
        public void AddSpecifyWarnings_FlagsStaleEnvironmentCopy()
        {
            var target = new Newtonsoft.Json.Linq.JObject();
            SaveSpecifyOrchestrator.AddSpecifyWarnings(target, new Newtonsoft.Json.Linq.JArray(
                "warning : changed since the last build: SampleProc. GeneXus specifies the environment's copy"));
            Assert.Single(target["warnings"]);
            Assert.True((bool)target["staleEnvironmentCopy"]);
        }

        [Fact]
        public void AddSpecifyWarnings_NoWarnings_AddsNothing()
        {
            var target = new Newtonsoft.Json.Linq.JObject();
            SaveSpecifyOrchestrator.AddSpecifyWarnings(target, new Newtonsoft.Json.Linq.JArray());
            Assert.Null(target["warnings"]);
            Assert.Null(target["staleEnvironmentCopy"]);
        }
    }
}
