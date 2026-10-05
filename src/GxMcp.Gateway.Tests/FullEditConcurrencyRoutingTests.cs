using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class FullEditConcurrencyRoutingTests
    {
        [Fact]
        public void FullSourceEdit_PreservesDryRunConcurrencyAndRollbackArguments()
        {
            var routedCall = new ObjectRouter().ConvertToolCall("genexus_edit", new JObject
            {
                ["name"] = "SyntheticProcedure",
                ["part"] = "Source",
                ["mode"] = "full",
                ["content"] = "// synthetic",
                ["dryRun"] = true,
                ["expectedVersion"] = "version-before",
                ["rollbackOnFailure"] = true
            });
            Assert.NotNull(routedCall);
            var routed = JObject.FromObject(routedCall);

            Assert.Equal("Write", routed["module"]?.ToString());
            Assert.Equal("Source", routed["action"]?.ToString());
            Assert.Equal("Source", routed["part"]?.ToString());
            Assert.Equal("full", routed["mode"]?.ToString());
            Assert.Equal("// synthetic", routed["content"]?.ToString());
            Assert.True(routed["dryRun"]?.ToObject<bool>());
            Assert.Equal("version-before", routed["expectedVersion"]?.ToString());
            Assert.True(routed["rollbackOnFailure"]?.ToObject<bool>());
        }
    }
}
