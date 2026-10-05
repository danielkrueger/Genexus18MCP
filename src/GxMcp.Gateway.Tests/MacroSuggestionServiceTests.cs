using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class MacroSuggestionServiceTests
    {
        private static string MakeTempUserMacroDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "gxmcp-macro-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static OperationTracker MakeTrackerWithSequence(
            (string tool, JObject args)[] sequence, int repetitions)
        {
            var t = new OperationTracker(TimeSpan.FromHours(1));
            for (int r = 0; r < repetitions; r++)
            {
                foreach (var (tool, args) in sequence)
                {
                    t.RecordSyntheticCompletion(tool, elapsedMs: 5, isError: false, toolArguments: args);
                }
            }
            return t;
        }

        [Fact]
        public void Suggest_DetectsTwoStepMacroRepeatedThreeTimes()
        {
            var seq = new (string, JObject)[]
            {
                ("genexus_create_object", new JObject { ["type"] = "WebPanel", ["name"] = "PanelA" }),
                ("genexus_apply_pattern", new JObject { ["name"] = "PanelA", ["pattern"] = "WorkWithPlus" })
            };
            var t = MakeTrackerWithSequence(seq, repetitions: 1);
            // Add two more with different names
            t.RecordSyntheticCompletion("genexus_create_object", 5, false, new JObject { ["type"] = "WebPanel", ["name"] = "PanelB" });
            t.RecordSyntheticCompletion("genexus_apply_pattern", 5, false, new JObject { ["name"] = "PanelB", ["pattern"] = "WorkWithPlus" });
            t.RecordSyntheticCompletion("genexus_create_object", 5, false, new JObject { ["type"] = "WebPanel", ["name"] = "PanelC" });
            t.RecordSyntheticCompletion("genexus_apply_pattern", 5, false, new JObject { ["name"] = "PanelC", ["pattern"] = "WorkWithPlus" });

            var svc = new MacroSuggestionService(t, MakeTempUserMacroDir());
            JObject result = svc.Suggest(windowMinutes: 60, minRepetitions: 3);

            Assert.Equal("Success", result["status"]?.ToString());
            var candidates = result["candidateMacros"] as JArray;
            Assert.NotNull(candidates);
            Assert.NotEmpty(candidates);
            var first = candidates.OfType<JObject>().FirstOrDefault(c =>
                ((JArray)c["steps"]!).Count == 2);
            Assert.NotNull(first);
            Assert.Equal(3, (int)first["observedRepetitions"]!);
        }

        [Fact]
        public void Suggest_SkipsWhenOnlyTwoRepetitions()
        {
            var seq = new (string, JObject)[]
            {
                ("genexus_create_object", new JObject { ["type"] = "WebPanel", ["name"] = "PanelA" }),
                ("genexus_apply_pattern", new JObject { ["name"] = "PanelA", ["pattern"] = "WorkWithPlus" })
            };
            var t = MakeTrackerWithSequence(seq, repetitions: 2);

            var svc = new MacroSuggestionService(t, MakeTempUserMacroDir());
            JObject result = svc.Suggest(windowMinutes: 60, minRepetitions: 3);

            var candidates = result["candidateMacros"] as JArray;
            Assert.NotNull(candidates);
            Assert.Empty(candidates);
        }

        [Fact]
        public void Suggest_SkipsWhenAllCallsReadOnly()
        {
            var t = new OperationTracker(TimeSpan.FromHours(1));
            for (int i = 0; i < 5; i++)
            {
                t.RecordSyntheticCompletion("genexus_query", 1, false, new JObject { ["query"] = "x" + i });
                t.RecordSyntheticCompletion("genexus_read", 1, false, new JObject { ["name"] = "Obj" + i });
            }

            var svc = new MacroSuggestionService(t, MakeTempUserMacroDir());
            JObject result = svc.Suggest(windowMinutes: 60, minRepetitions: 3);

            var candidates = result["candidateMacros"] as JArray;
            Assert.NotNull(candidates);
            // No candidate should be entirely read-only.
            foreach (var c in candidates.OfType<JObject>())
            {
                bool hasMutating = false;
                foreach (var s in (JArray)c["steps"]!)
                {
                    string tool = s["tool"]?.ToString() ?? "";
                    if (!IsReadOnlyTool(tool)) { hasMutating = true; break; }
                }
                Assert.True(hasMutating, "Found a candidate macro composed only of read-only tools.");
            }
        }

        private static bool IsReadOnlyTool(string tool)
        {
            return tool is "genexus_query" or "genexus_read" or "genexus_list_objects"
                or "genexus_inspect" or "genexus_analyze" or "genexus_whoami"
                or "genexus_recipe" or "genexus_doc" or "genexus_logs"
                or "genexus_history" or "genexus_doctor";
        }

        [Fact]
        public void Suggest_ArgsParameterization_VaryingArgBecomesArgToken()
        {
            var t = new OperationTracker(TimeSpan.FromHours(1));
            // pattern is constant (WorkWithPlus), name varies
            for (int i = 0; i < 3; i++)
            {
                t.RecordSyntheticCompletion("genexus_create_object", 1, false,
                    new JObject { ["type"] = "WebPanel", ["name"] = "Panel" + i });
                t.RecordSyntheticCompletion("genexus_apply_pattern", 1, false,
                    new JObject { ["name"] = "Panel" + i, ["pattern"] = "WorkWithPlus" });
            }

            var svc = new MacroSuggestionService(t, MakeTempUserMacroDir());
            JObject result = svc.Suggest(60, 3);

            var candidates = (JArray)result["candidateMacros"]!;
            var twoStep = candidates.OfType<JObject>().First(c => ((JArray)c["steps"]!).Count == 2);
            var step0 = (JObject)((JArray)twoStep["steps"]!)[0];
            var step1 = (JObject)((JArray)twoStep["steps"]!)[1];

            // type is constant → literal
            Assert.Equal("WebPanel", step0["args_template"]!["type"]?.ToString());
            // name varies → parameterized
            Assert.Equal("<arg:name>", step0["args_template"]!["name"]?.ToString());
            // pattern is constant
            Assert.Equal("WorkWithPlus", step1["args_template"]!["pattern"]?.ToString());
            // name varies in step 2 too
            Assert.Equal("<arg:name>", step1["args_template"]!["name"]?.ToString());

            var varying = (JArray)twoStep["argsToParameterize"]!;
            Assert.Contains(varying.Select(v => v.ToString()), n => string.Equals(n, "name", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Crystallize_WritesParseableRecipeFile()
        {
            string dir = MakeTempUserMacroDir();
            var t = new OperationTracker(TimeSpan.FromHours(1));
            var svc = new MacroSuggestionService(t, dir);

            var steps = new JArray(
                new JObject
                {
                    ["tool"] = "genexus_create_object",
                    ["args_template"] = new JObject { ["type"] = "WebPanel", ["name"] = "<arg:name>" }
                },
                new JObject
                {
                    ["tool"] = "genexus_apply_pattern",
                    ["args_template"] = new JObject { ["name"] = "<arg:name>", ["pattern"] = "WorkWithPlus" }
                });

            JObject result = svc.Crystallize("create_and_apply_wwp", "Create a WebPanel + apply WWP.", steps);
            Assert.Equal("Success", result["status"]?.ToString());

            string? path = result["path"]?.ToString();
            Assert.NotNull(path);
            Assert.True(File.Exists(path));

            string raw = File.ReadAllText(path);
            JObject parsed = JObject.Parse(raw);
            Assert.Equal("create_and_apply_wwp", parsed["name"]?.ToString());
            Assert.Equal("user-macro", parsed["source"]?.ToString());
            Assert.NotNull(parsed["crystallizedAt"]);
            Assert.Equal(2, ((JArray)parsed["steps"]!).Count);
        }

        [Fact]
        public void RecipeCatalog_DiscoversUserMacroAfterCrystallize()
        {
            string dir = MakeTempUserMacroDir();
            var t = new OperationTracker(TimeSpan.FromHours(1));
            var svc = new MacroSuggestionService(t, dir);

            string uniqueName = "macro_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var steps = new JArray(new JObject
            {
                ["tool"] = "genexus_create_object",
                ["args_template"] = new JObject { ["name"] = "<arg:name>" }
            });

            svc.Crystallize(uniqueName, "Test macro.", steps);
            RecipeCatalog.ConfigureUserMacroDirectory(dir);

            JObject lookup = RecipeCatalog.Get(uniqueName);
            // Either the macro got resolved (returning its body) OR — if the registry
            // doesn't surface it through Get — at least it shouldn't return an "error"
            // envelope when we run through Dispatch:list, where user macros are listed.
            JObject list = RecipeCatalog.Dispatch("list", null);
            var recipes = (JArray)list["recipes"]!;
            bool found = recipes.OfType<JObject>().Any(r =>
                string.Equals(r["name"]?.ToString(), uniqueName, StringComparison.OrdinalIgnoreCase));
            Assert.True(found, "Crystallized user macro was not discovered by RecipeCatalog.");
        }

        [Fact]
        public void Crystallize_IoFailure_HidesExceptionTextAndReturnsOperationId()
        {
            string dir = MakeTempUserMacroDir();
            Directory.Delete(dir);
            File.WriteAllText(dir, "not a directory");
            var svc = new MacroSuggestionService(new OperationTracker(TimeSpan.FromHours(1)), dir);
            JObject result = svc.Crystallize("io_failure", "", new JArray(new JObject { ["tool"] = "x" }));
            Assert.Equal("Error", result["status"]?.ToString());
            Assert.Equal("Macro crystallization failed. See server logs for details.", result["error"]?.ToString());
            Assert.DoesNotContain(dir, result.ToString());
            Assert.NotNull(result["operationId"]);
        }

        [Fact]
        public void LogValue_RedactsQuotedPasswordTokenAndAuthorizationValues()
        {
            const string input = "IOException: {\"password\":\"password-value\", \"token\": \"token-value\", \"authorization\": \"Bearer auth-value\"}";

            string result = MacroSuggestionService.LogValue(input);

            Assert.DoesNotContain("password-value", result);
            Assert.DoesNotContain("token-value", result);
            Assert.DoesNotContain("auth-value", result);
            Assert.Contains("<redacted>", result);
        }
    }
}
