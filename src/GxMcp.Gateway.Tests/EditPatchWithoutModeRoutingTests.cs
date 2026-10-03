using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Same data-loss family as issue #43: genexus_edit with `patch` but WITHOUT `mode` skipped the
    // patch branch and fell into the full-write branch, which reads only `content`. The part was
    // persisted EMPTY and the call reported success. These tests pin that a patch payload can never
    // reach a full-part Write, and that a full-part Write can never be built without `content`.
    public class EditPatchWithoutModeRoutingTests
    {
        private static JObject Route(JObject args)
        {
            var routed = new ObjectRouter().ConvertToolCall("genexus_edit", args);
            Assert.NotNull(routed);
            return JObject.FromObject(routed!);
        }

        [Fact]
        public void PatchObject_WithoutMode_RoutesToPatchWithFindReplace()
        {
            var jo = Route(new JObject
            {
                ["name"] = "SampleProc",
                ["part"] = "Source",
                ["patch"] = new JObject { ["find"] = "old text", ["replace"] = "new text" }
            });

            Assert.Equal("Patch", jo["module"]!.ToString());
            Assert.Equal("Apply", jo["action"]!.ToString());
            Assert.Equal("Replace", jo["operation"]!.ToString());
            Assert.Equal("old text", jo["context"]!.ToString());
            Assert.Equal("new text", jo["payload"]!.ToString());
        }

        [Fact]
        public void PatchArray_WithoutMode_RoutesToJsonPatch()
        {
            var jo = Route(new JObject
            {
                ["name"] = "SampleProc",
                ["part"] = "Source",
                ["patch"] = new JArray { new JObject { ["op"] = "add", ["path"] = "/x", ["value"] = "y" } }
            });

            Assert.Equal("JsonPatch", jo["module"]!.ToString());
        }

        [Theory]
        [InlineData("full")]
        [InlineData("ops")]
        [InlineData("xml")]
        public void Patch_WithContradictoryMode_ThrowsUsageError(string mode)
        {
            var args = new JObject
            {
                ["name"] = "SampleProc",
                ["part"] = "Source",
                ["mode"] = mode,
                ["patch"] = new JObject { ["find"] = "old text", ["replace"] = "new text" }
            };

            var ex = Assert.Throws<UsageException>(() => new ObjectRouter().ConvertToolCall("genexus_edit", args));
            Assert.Equal("usage_error", ex.Code);
            Assert.Contains("patch", ex.Message);
        }

        [Fact]
        public void Patch_WithModePatch_StillRoutesToPatch()
        {
            var jo = Route(new JObject
            {
                ["name"] = "SampleProc",
                ["part"] = "Source",
                ["mode"] = "patch",
                ["patch"] = new JObject { ["find"] = "old text", ["replace"] = "new text" }
            });

            Assert.Equal("Patch", jo["module"]!.ToString());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FullWrite_WithoutContent_ThrowsContentRequired(bool explicitNull)
        {
            var args = new JObject { ["name"] = "SampleProc", ["part"] = "Source", ["mode"] = "full" };
            if (explicitNull)
                args["content"] = JValue.CreateNull();

            var ex = Assert.Throws<UsageException>(() => new ObjectRouter().ConvertToolCall("genexus_edit", args));
            Assert.Equal("ContentRequired", ex.Code);
        }

        [Fact]
        public void NoModeNoContentNoPatch_ThrowsContentRequired()
        {
            var args = new JObject { ["name"] = "SampleProc", ["part"] = "Source" };

            var ex = Assert.Throws<UsageException>(() => new ObjectRouter().ConvertToolCall("genexus_edit", args));
            Assert.Equal("ContentRequired", ex.Code);
        }

        [Fact]
        public void FullWrite_WithContent_StillRoutesToWrite()
        {
            var jo = Route(new JObject
            {
                ["name"] = "SampleProc",
                ["part"] = "Source",
                ["mode"] = "full",
                ["content"] = "whole new source"
            });

            Assert.Equal("Write", jo["module"]!.ToString());
            Assert.Equal("whole new source", jo["content"]!.ToString());
        }

        [Fact]
        public void FullWrite_WithExplicitEmptyContent_StillRoutesToWrite()
        {
            var jo = Route(new JObject
            {
                ["name"] = "SampleProc",
                ["part"] = "Source",
                ["mode"] = "full",
                ["content"] = string.Empty
            });

            Assert.Equal("Write", jo["module"]!.ToString());
            Assert.Equal(string.Empty, jo["content"]!.ToString());
        }
    }
}
