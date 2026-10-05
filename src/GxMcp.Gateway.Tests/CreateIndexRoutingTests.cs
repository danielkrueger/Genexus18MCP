using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class CreateIndexRoutingTests
    {
        [Fact]
        public void StructureCreateIndex_ForwardsDryRunConcurrencyAndRollback()
        {
            var args = new JObject
            {
                ["action"] = "create_index",
                ["name"] = "Queue",
                ["dryRun"] = true,
                ["baseVersion"] = "index-state-token",
                ["rollbackOnFailure"] = true,
                ["payload"] = new JObject
                {
                    ["name"] = "UQueuePending",
                    ["unique"] = false,
                    ["attributes"] = new JArray("QueueStartedAt", "QueueCreatedAt")
                }
            };

            object? routed = new OperationsRouter().ConvertToolCall("genexus_structure", args);
            Assert.NotNull(routed);
            var json = JObject.FromObject(routed);

            Assert.Equal("Structure", json["module"]?.ToString());
            Assert.Equal("CreateIndex", json["action"]?.ToString());
            Assert.True(json["dryRun"]?.Value<bool>());
            Assert.Equal("index-state-token", json["baseVersion"]?.ToString());
            Assert.True(json["rollbackOnFailure"]?.Value<bool>());
            Assert.Contains("UQueuePending", json["payload"]?.ToString());
        }

        [Fact]
        public void StructureCreateIndex_DefaultsRollbackOnFailureToTrue()
        {
            object? routed = new OperationsRouter().ConvertToolCall("genexus_structure", new JObject
            {
                ["action"] = "create_index",
                ["name"] = "Queue",
                ["payload"] = new JObject { ["attributes"] = new JArray("QueueId") }
            });
            Assert.NotNull(routed);

            Assert.True(JObject.FromObject(routed)["rollbackOnFailure"]?.Value<bool>());
        }
    }
}
