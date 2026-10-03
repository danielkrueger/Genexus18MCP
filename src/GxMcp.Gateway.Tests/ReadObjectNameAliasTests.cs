using System;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway.Routers;

namespace GxMcp.Gateway.Tests
{
    public sealed class ReadObjectNameAliasTests
    {
        [Fact]
        public void Read_ObjectName_IsAliasOfName()
        {
            var routed = JObject.FromObject(new ObjectRouter().ConvertToolCall("genexus_read",
                new JObject { ["objectName"] = "SampleProc", ["part"] = "Source" })!);

            Assert.Equal("ExtractSource", routed["action"]?.ToString());
            Assert.Equal("SampleProc", routed["target"]?.ToString());
        }

        [Fact]
        public void Read_NameWinsOverObjectName()
        {
            var routed = JObject.FromObject(new ObjectRouter().ConvertToolCall("genexus_read",
                new JObject { ["name"] = "SampleProc", ["objectName"] = "Other", ["part"] = "Source" })!);

            Assert.Equal("SampleProc", routed["target"]?.ToString());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Read_MissingOrEmptyName_IsUsageError(string? name)
        {
            var args = new JObject { ["part"] = "Source" };
            if (name != null) args["name"] = name;

            var ex = Assert.Throws<UsageException>(() => new ObjectRouter().ConvertToolCall("genexus_read", args));
            Assert.Equal("usage_error", ex.Code);
        }

        [Fact]
        public void IndexGate_TreatsObjectNameLikeNameForRead()
        {
            Assert.True(Program.IsIdentityBoundReadAvailableDuringIndexingForTest(
                "genexus_read", new JObject { ["objectName"] = "SampleProc" }));
            Assert.False(Program.IsIdentityBoundReadAvailableDuringIndexingForTest(
                "genexus_inspect", new JObject { ["objectName"] = "SampleProc" }));
        }
    }
}
