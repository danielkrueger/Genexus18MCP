using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    // Self-extending recipe catalog: scan OperationTracker history for repeated multi-step
    // shapes (same sequence of (tool, sorted arg-keys)), then offer to crystallize them as
    // named recipes under <configRoot>/recipes/user-macros/<name>.json.
    //
    // Algorithm (suggest_macro):
    //   1. Pull ops from the last N minutes.
    //   2. Slide a window of size K in 2..5 over the ordered history. Build a shape
    //      = sequence of "tool|key1,key2,key3" tuples.
    //   3. Count shape repetitions. Filter to shapes with count >= minRepetitions.
    //   4. For each repeating shape, classify each arg as constant (same value across
    //      all occurrences) or parameterized (varies → <arg:NAME>).
    //   5. Skip shapes containing only read-only tools (genexus_query, genexus_read, …).
    //   6. Generate proposedName from tool verbs + constant discriminators.
    internal sealed class MacroSuggestionService
    {


        private readonly OperationTracker _tracker;
        private readonly string _userMacroDir;

        public MacroSuggestionService(OperationTracker tracker, string userMacroDir)
        {
            _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
            _userMacroDir = userMacroDir ?? throw new ArgumentNullException(nameof(userMacroDir));
        }

        public string UserMacroDirectory => _userMacroDir;

        // --- suggest_macro ---
        public JObject Suggest(int windowMinutes, int minRepetitions)
        {
            if (windowMinutes <= 0) windowMinutes = 30;
            if (minRepetitions < 2) minRepetitions = 3;

            DateTime since = DateTime.UtcNow - TimeSpan.FromMinutes(windowMinutes);
            var ops = _tracker.SnapshotRecentOperations(since);

            var candidates = new List<JObject>();

            // Try window sizes 2..5. Larger windows are likely whole workflows, not macros.
            for (int k = 2; k <= 5; k++)
            {
                if (ops.Count < k * minRepetitions) continue;

                // shapeKey → list of occurrences (each occurrence is a list of OperationSnapshot, length k).
                var groups = new Dictionary<string, List<List<OperationTracker.OperationSnapshot>>>(StringComparer.Ordinal);
                for (int i = 0; i + k <= ops.Count; i++)
                {
                    var window = new List<OperationTracker.OperationSnapshot>(k);
                    for (int j = 0; j < k; j++) window.Add(ops[i + j]);
                    string shapeKey = BuildShapeKey(window);
                    if (!groups.TryGetValue(shapeKey, out var list))
                    {
                        list = new List<List<OperationTracker.OperationSnapshot>>();
                        groups[shapeKey] = list;
                    }
                    list.Add(window);
                }

                // Dedupe overlapping windows: a single op can only be in one occurrence per shape.
                foreach (var kvp in groups)
                {
                    var nonOverlapping = SelectNonOverlapping(kvp.Value);
                    if (nonOverlapping.Count < minRepetitions) continue;

                    // Skip shapes whose every tool is read-only.
                    if (nonOverlapping[0].All(s => OperationClassifier.IsReadOnly(s.ToolName, s.ToolArguments))) continue;

                    var candidate = BuildCandidate(nonOverlapping);
                    if (candidate != null) candidates.Add(candidate);
                }
            }

            // Sort by observedRepetitions desc, then by step count desc (longer macros = more compression).
            candidates.Sort((a, b) =>
            {
                int repCmp = (int)(b["observedRepetitions"] ?? 0) - (int)(a["observedRepetitions"] ?? 0);
                if (repCmp != 0) return repCmp;
                return ((b["steps"] as JArray)?.Count ?? 0) - ((a["steps"] as JArray)?.Count ?? 0);
            });

            return new JObject
            {
                ["status"] = "Success",
                ["windowMinutes"] = windowMinutes,
                ["minRepetitions"] = minRepetitions,
                ["scannedOperations"] = ops.Count,
                ["candidateMacros"] = new JArray(candidates.Cast<JToken>().ToArray()),
                ["hint"] = "Crystallize with action=crystallize macroName=<proposedName>."
            };
        }

        // --- crystallize ---
        public JObject Crystallize(string? macroName, string? description, JArray? steps)
        {
            if (string.IsNullOrWhiteSpace(macroName))
                return ErrorEnvelope("macroName is required.");
            if (steps == null || steps.Count == 0)
                return ErrorEnvelope("steps array is required and must be non-empty.");
            if (!Regex.IsMatch(macroName, "^[A-Za-z0-9_-]+$"))
                return ErrorEnvelope("macroName must match [A-Za-z0-9_-]+ (no spaces/path-separators).");

            try
            {
                Directory.CreateDirectory(_userMacroDir);

                string recipeKey = macroName.ToLowerInvariant();
                string path = Path.Combine(_userMacroDir, recipeKey + ".json");

                var recipe = new JObject
                {
                    ["name"] = recipeKey,
                    ["description"] = string.IsNullOrWhiteSpace(description)
                        ? "User macro crystallized from observed tool sequence."
                        : description,
                    ["source"] = "user-macro",
                    ["version"] = "v1",
                    ["crystallizedAt"] = DateTime.UtcNow.ToString("o"),
                    ["goal"] = string.IsNullOrWhiteSpace(description)
                        ? "User-defined macro."
                        : description,
                    ["steps"] = steps
                };

                File.WriteAllText(path, recipe.ToString(Newtonsoft.Json.Formatting.Indented));
                RecipeCatalog.RefreshUserMacros();

                return new JObject
                {
                    ["status"] = "Success",
                    ["recipeKey"] = recipeKey,
                    ["path"] = path,
                    ["source"] = "user-macro",
                    ["hint"] = "Call genexus_recipe { action: 'describe', name: '" + recipeKey + "' } to inspect."
                };
            }
            catch (Exception ex)
            {
                string operationId = Guid.NewGuid().ToString("N");
                Program.Log($"{{\"event\":\"macro_crystallize_failed\",\"operationId\":\"{operationId}\",\"exceptionType\":\"{ex.GetType().FullName}\",\"exception\":\"{LogValue(ex.ToString())}\"}}");
                return ErrorEnvelope("Macro crystallization failed. See server logs for details.", operationId);
            }
        }

        // --- helpers ---

        private static JObject ErrorEnvelope(string message, string? operationId = null)
        {
            var result = new JObject
            {
                ["status"] = "Error",
                ["error"] = message
            };
            if (!string.IsNullOrEmpty(operationId)) result["operationId"] = operationId;
            return result;
        }

        internal static string LogValue(string value)
        {
            // Shared with KbImportHelper, Program.Http and the Worker's
            // PreviewService/SharedWorkerHost: one redaction rule, not five.
            string redacted = LogRedaction.Redact(value);
            return redacted.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace(((char)13).ToString(), "\r").Replace(((char)10).ToString(), "\n");
        }

        // Shape = pipe-joined "tool|sortedKey,sortedKey" tuples. Values not included
        // (those are what we want to parameterize).
        private static string BuildShapeKey(List<OperationTracker.OperationSnapshot> window)
        {
            var parts = new List<string>(window.Count);
            foreach (var s in window)
            {
                var keys = s.ToolArguments != null
                    ? s.ToolArguments.Properties().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray()
                    : Array.Empty<string>();
                parts.Add(s.ToolName + "|" + string.Join(",", keys));
            }
            return string.Join(">>", parts);
        }

        // Greedy non-overlap: sort occurrences by start time, accept if its first op
        // is strictly after the previous accepted occurrence's last op.
        private static List<List<OperationTracker.OperationSnapshot>> SelectNonOverlapping(
            List<List<OperationTracker.OperationSnapshot>> occurrences)
        {
            var sorted = occurrences.OrderBy(o => o[0].AtUtc).ToList();
            var accepted = new List<List<OperationTracker.OperationSnapshot>>();
            DateTime cutoff = DateTime.MinValue;
            foreach (var occ in sorted)
            {
                if (occ[0].AtUtc <= cutoff) continue;
                accepted.Add(occ);
                cutoff = occ[occ.Count - 1].AtUtc;
            }
            return accepted;
        }

        private static JObject BuildCandidate(List<List<OperationTracker.OperationSnapshot>> occurrences)
        {
            int k = occurrences[0].Count;
            var stepTemplates = new JArray();
            var allVaryingArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? firstToolDiscriminator = null;

            for (int step = 0; step < k; step++)
            {
                string toolName = occurrences[0][step].ToolName;
                var argKeys = occurrences[0][step].ToolArguments?.Properties().Select(p => p.Name).ToList()
                              ?? new List<string>();

                var argsTemplate = new JObject();
                foreach (var argKey in argKeys)
                {
                    // Collect all observed values for this (step, arg).
                    var values = new HashSet<string>(StringComparer.Ordinal);
                    JToken? firstToken = null;
                    foreach (var occ in occurrences)
                    {
                        JToken? val = occ[step].ToolArguments?[argKey];
                        firstToken ??= val;
                        values.Add(val?.ToString(Newtonsoft.Json.Formatting.None) ?? "");
                    }
                    if (values.Count == 1)
                    {
                        // Constant across all occurrences → literal in template.
                        argsTemplate[argKey] = firstToken?.DeepClone() ?? JValue.CreateNull();
                        if (step == 0 && firstToolDiscriminator == null && firstToken != null && firstToken.Type == JTokenType.String)
                        {
                            string s = firstToken.ToString();
                            if (!string.IsNullOrEmpty(s) && Regex.IsMatch(s, "^[A-Za-z0-9_]+$"))
                                firstToolDiscriminator = s;
                        }
                    }
                    else
                    {
                        // Varies → parameterize.
                        argsTemplate[argKey] = "<arg:" + argKey + ">";
                        allVaryingArgs.Add(argKey);
                    }
                }

                stepTemplates.Add(new JObject
                {
                    ["tool"] = toolName,
                    ["args_template"] = argsTemplate
                });
            }

            string proposedName = BuildProposedName(occurrences[0], firstToolDiscriminator);

            return new JObject
            {
                ["proposedName"] = proposedName,
                ["steps"] = stepTemplates,
                ["observedRepetitions"] = occurrences.Count,
                ["lastSeenAtUtc"] = occurrences[occurrences.Count - 1][k - 1].AtUtc,
                ["argsToParameterize"] = new JArray(allVaryingArgs.Select(s => (JToken)new JValue(s)).ToArray()),
                ["suggestedDescription"] = BuildDescription(occurrences[0])
            };
        }

        private static string BuildProposedName(List<OperationTracker.OperationSnapshot> sample, string? discriminator)
        {
            // Strip "genexus_" prefix from each tool, join with "_and_".
            var verbs = sample.Select(s =>
            {
                string n = s.ToolName ?? "tool";
                if (n.StartsWith("genexus_", StringComparison.OrdinalIgnoreCase)) n = n.Substring("genexus_".Length);
                return n;
            }).ToList();

            string name = string.Join("_and_", verbs);
            if (!string.IsNullOrEmpty(discriminator))
                name += "_" + discriminator.ToLowerInvariant();
            return name;
        }

        private static string BuildDescription(List<OperationTracker.OperationSnapshot> sample)
        {
            // Simple template-based description.
            var verbs = sample.Select(s => s.ToolName ?? "tool").ToList();
            return "Observed sequence: " + string.Join(" → ", verbs) + ".";
        }
    }
}
