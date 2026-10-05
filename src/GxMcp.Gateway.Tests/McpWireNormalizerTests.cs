using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class McpWireNormalizerTests
    {
        [Fact]
        public void CreateResponse_ConformsToJsonRpc2()
        {
            var res = McpWireNormalizer.CreateResponse(1, new JObject { ["hello"] = "world" });

            Assert.Equal("2.0", (string?)res["jsonrpc"]);
            Assert.Equal(1, (int)res["id"]!);
            Assert.Equal("world", (string?)res["result"]?["hello"]);
        }

        [Fact]
        public void CreateError_FormatsStandardErrorEnvelope()
        {
            var err = McpWireNormalizer.CreateError(2, -32600, "Invalid Request");

            Assert.Equal("2.0", (string?)err["jsonrpc"]);
            Assert.Equal(2, (int)err["id"]!);
            Assert.Equal(-32600, (int)err["error"]?["code"]!);
            Assert.Equal("Invalid Request", (string?)err["error"]?["message"]);
        }

        [Fact]
        public void CreateToolResult_FormatsMcpToolCallContent()
        {
            var toolRes = McpWireNormalizer.CreateToolResult(3, "{\"status\":\"ok\"}");

            Assert.Equal("2.0", (string?)toolRes["jsonrpc"]);
            Assert.Equal(3, (int)toolRes["id"]!);
            var content = toolRes["result"]?["content"] as JArray;
            Assert.NotNull(content);
            Assert.Single(content);
            Assert.Equal("text", (string?)content[0]["type"]);
            Assert.Equal("{\"status\":\"ok\"}", (string?)content[0]["text"]);
        }
    }
}
