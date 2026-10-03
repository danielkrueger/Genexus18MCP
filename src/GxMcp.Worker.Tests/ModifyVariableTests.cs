using GxMcp.Worker;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // ── Task 4.3 (v2.3.8) — genexus_modify_variable ──────────────────────────────
    // The pre-SDK gates (UnknownType + ObjectNotFound) execute before any KB / SDK
    // call, so they're observable without a real KB fixture. The success path
    // (delete + add + rebind) needs a real KB and is covered indirectly via the
    // VariableTypeResolverTests + the AddVariable / DeleteVariable plumbing tests.
    public class ModifyVariableTests
    {
        private static WriteService BuildIsolatedWriteService()
        {
            var indexCache = new IndexCacheService();
            var build = new BuildService();
            var kb = new KbService(indexCache);
            kb.SetBuildService(build);
            build.SetKbService(kb);
            indexCache.SetBuildService(build);
            var obj = new ObjectService(kb, build);
            return new WriteService(obj);
        }

        [Fact]
        public void ModifyVariable_UnknownType_ReturnsUnknownTypeError()
        {
            var ws = BuildIsolatedWriteService();
            string json;
            try
            {
                json = ws.ModifyVariable("TestProc", "X", "Bogus(99)");
            }
            catch (System.IO.FileNotFoundException) { return; }
            catch (System.TypeLoadException) { return; }

            var obj = JObject.Parse(json);
            Assert.Equal("error", obj["status"]?.ToString());
            Assert.Equal("UnknownType", obj["error"]?["code"]?.ToString());
            Assert.False(string.IsNullOrEmpty(obj["suggestion"]?.ToString()));
            Assert.NotNull(obj["accepted"]);
            Assert.Contains("Bogus", obj["error"]?["message"]?.ToString() ?? "");
        }

        [Fact]
        public void ModifyVariable_EmptyType_ReturnsUnknownTypeError()
        {
            var ws = BuildIsolatedWriteService();
            string json;
            try
            {
                json = ws.ModifyVariable("TestProc", "X", "   ");
            }
            catch (System.IO.FileNotFoundException) { return; }
            catch (System.TypeLoadException) { return; }

            var obj = JObject.Parse(json);
            Assert.Equal("error", obj["status"]?.ToString());
            Assert.Equal("UnknownType", obj["error"]?["code"]?.ToString());
        }

        [Fact]
        public void ModifyVariable_NoTypeAndNoDescription_ReturnsMissingParameter()
        {
            // Nothing to change: a clear usage error, not UnknownType.
            var ws = BuildIsolatedWriteService();
            string json;
            try
            {
                json = ws.ModifyVariable("TestProc", "X", null);
            }
            catch (System.IO.FileNotFoundException) { return; }
            catch (System.TypeLoadException) { return; }

            var obj = JObject.Parse(json);
            Assert.Equal("error", obj["status"]?.ToString());
            Assert.Equal("MissingParameter", obj["error"]?["code"]?.ToString());
        }

        [Fact]
        public void ModifyVariable_NoTypeWithTypeShapeArg_ReturnsMissingParameter()
        {
            var ws = BuildIsolatedWriteService();
            string json;
            try
            {
                json = ws.ModifyVariable("TestProc", "X", null, length: 10, description: "Sample");
            }
            catch (System.IO.FileNotFoundException) { return; }
            catch (System.TypeLoadException) { return; }

            var obj = JObject.Parse(json);
            Assert.Equal("MissingParameter", obj["error"]?["code"]?.ToString());
        }

        // Set-vs-clear is the distinction the description-only path exists to express, and it is
// invisible to a test that stops at the argument gate. `ResolveEffectiveDescription` is the
// shared rule for both the variable and the Business Component modify path, so it is
// pinned directly rather than inferred from an error code.
[Theory]
[InlineData(null, "kept", "kept")]   // not supplied: a retype must not clear it
[InlineData("", "kept", "")]         // explicit empty: an intentional clear
[InlineData("new text", "kept", "new text")]
[InlineData("new text", null, "new text")]
[InlineData(null, null, null)]
[InlineData("", null, "")]
public void ResolveEffectiveDescription_DistinguishesUnsuppliedFromCleared(
    string requested, string preserved, string expected)
{
    Assert.Equal(expected, WriteService.ResolveEffectiveDescription(requested, preserved));
}

// The defect was that the Business Component path had no way to receive a description at
// all: the dispatcher did not forward it and the method had no parameter for it, so the
// call returned success and changed nothing. Pinned on the signature, which fails for the
// original reason and needs no KB to observe.
[Fact]
public void ChangeBusinessComponentVariable_AcceptsAnOptionalDescription()
{
    var method = typeof(WriteService).GetMethod("ChangeBusinessComponentVariable");
    Assert.NotNull(method);
    var parameters = method.GetParameters();
    var last = parameters[parameters.Length - 1];
    Assert.Equal("description", last.Name);
    Assert.Equal(typeof(string), last.ParameterType);
    // Optional, so the existing `add` call site keeps compiling unchanged.
    Assert.True(last.HasDefaultValue);
    Assert.Null(last.DefaultValue);
}

[Fact]
public void ModifyVariable_DescriptionOnly_DoesNotRequireType()
{
    // description without a type must pass the type gate (no UnknownType /
    // MissingParameter) and reach the object-resolution path.
    var ws = BuildIsolatedWriteService();
    string json;
    try
    {
        json = ws.ModifyVariable("NonExistentObj_" + System.Guid.NewGuid().ToString("N"), "X", null, description: "Sample description");
    }
    catch (System.IO.FileNotFoundException) { return; }
    catch (System.TypeLoadException) { return; }

    var obj = JObject.Parse(json);
    string code = obj["error"]?["code"]?.ToString();
    Assert.NotEqual("UnknownType", code);
    Assert.NotEqual("MissingParameter", code);
}

        [Fact]
        public void ModifyVariable_ObjectNotFound_ReturnsError()
        {
            // With a valid (recognized) type but no KB open / object missing, the
            // gate should fall through to the ResolveVariableTarget path and emit
            // the standard write-error envelope (isError=true).
            var ws = BuildIsolatedWriteService();
            string json;
            try
            {
                json = ws.ModifyVariable("NonExistentObj_" + System.Guid.NewGuid().ToString("N"), "X", "Character(40)");
            }
            catch (System.IO.FileNotFoundException) { return; }
            catch (System.TypeLoadException) { return; }

            var obj = JObject.Parse(json);
            // Either the structured "isError" envelope from CreateWriteError, or a
            // legacy "error" key — both indicate the object-not-found path was hit.
            bool isError = obj["isError"]?.ToObject<bool?>() == true
                           || !string.IsNullOrEmpty(obj["error"]?.ToString())
                           || string.Equals(obj["status"]?.ToString(), "Error", System.StringComparison.OrdinalIgnoreCase);
            Assert.True(isError, "Expected error envelope. Got: " + json);
        }
    }
}
