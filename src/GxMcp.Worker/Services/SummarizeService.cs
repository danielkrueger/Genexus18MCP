using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class SummarizeService
    {
        private static readonly BoundedStringCache _summaryCache = new BoundedStringCache(256, 16L * 1024 * 1024);
        internal static long ResponseCacheBytes => _summaryCache.EstimatedBytes;

        public static void InvalidateCache()
        {
            _summaryCache.Clear();
        }

        private readonly KbService _kbService;
        private readonly ObjectService _objectService;

        public SummarizeService(KbService kbService, ObjectService objectService)
        {
            _kbService = kbService;
            _objectService = objectService;
        }

        public string Summarize(string target, string typeFilter = null)
        {
            string cacheKey = (target ?? "") + "|" + (typeFilter ?? "");
            if (_summaryCache.TryGetValue(cacheKey, out var cached))
            {
                if (!WriteService.WasTargetWrittenSince(target, DateTime.UtcNow.AddMinutes(-5)))
                {
                    return cached;
                }
                _summaryCache.TryRemove(cacheKey, out _);
            }

            try
            {
                var obj = _objectService.FindObject(target, typeFilter);
                if (obj == null) return Models.McpResponse.Err(code: "ObjectNotFound", message: "Object not found.", hint: "Verify the object name with genexus_query.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_query", new JObject { ["name"] = target }, "Search for the object by name.")), target: target);

                var result = new JObject();
                result["name"] = obj.Name;
                result["type"] = obj.TypeDescriptor.Name;
                result["description"] = obj.Description;

                // 1. Signature & Parms
                var (parmRule, parms) = _objectService.GetParametersInternal(obj);
                result["parmRule"] = parmRule;
                var parmList = new JArray();
                foreach (var p in parms) parmList.Add(new JObject { ["name"] = p.Name, ["accessor"] = p.Accessor, ["type"] = p.Type });
                result["parameters"] = parmList;

                // Extract source once for both intents and metrics
                string source = GetSourceSafe(obj);

                // 2. Extract Logic Intents
                result["intents"] = ExtractIntents(source);

                // 3. Key Dependencies (Semantic)
                result["criticalDependencies"] = ExtractCriticalDependencies(obj);

                // 4. Complexity & Risk
                result["metrics"] = CalculateMetrics(source);

                string json = result.ToString(Newtonsoft.Json.Formatting.None);
                _summaryCache.TryAdd(cacheKey, json);
                return json;
            }
            catch (Exception ex)
            {
                return "{\"status\":\"Error\",\"message\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private static string GetSourceSafe(KBObject obj)
        {
            if (obj == null) return "";
            try
            {
                if (obj is Procedure proc)
                {
                    try { return proc.ProcedurePart?.Source ?? ""; }
                    catch { return ""; }
                }
                if (obj is WebPanel wbp)
                {
                    try { return wbp.Parts.Get<EventsPart>()?.Source ?? ""; }
                    catch { return ""; }
                }
                if (obj is Transaction trn)
                {
                    try { return trn.Parts.Get<EventsPart>()?.Source ?? ""; }
                    catch { return ""; }
                }
            }
            catch { }
            return "";
        }

        private JArray ExtractIntents(string source)
        {
            var intents = new JArray();
            if (string.IsNullOrEmpty(source)) return intents;

            // Simple Pattern Matching for common GeneXus logic
            if (source.IndexOf("for each", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var match = Regex.Match(source, @"for each\s+([^\n\r]*)", RegexOptions.IgnoreCase);
                intents.Add($"Iterates over data: {match.Groups[1].Value.Trim()}");
            }

            if (source.IndexOf("new", StringComparison.OrdinalIgnoreCase) >= 0 && source.IndexOf("endnew", StringComparison.OrdinalIgnoreCase) >= 0)
                intents.Add("Inserts new records");

            if (source.IndexOf("delete", StringComparison.OrdinalIgnoreCase) >= 0)
                intents.Add("Deletes records");

            if (source.IndexOf("call(", StringComparison.OrdinalIgnoreCase) >= 0 || source.IndexOf(".call(", StringComparison.OrdinalIgnoreCase) >= 0)
                intents.Add("Orchestrates other processes");

            if (source.IndexOf("msg(", StringComparison.OrdinalIgnoreCase) >= 0)
                intents.Add("Communicates with user/logs");

            if (source.IndexOf("error(", StringComparison.OrdinalIgnoreCase) >= 0)
                intents.Add("Includes validation logic");

            return intents;
        }

        private JArray ExtractCriticalDependencies(KBObject obj)
        {
            var deps = new JArray();
            var kb = _kbService.GetKB();
            if (kb == null) return deps;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var references = obj.GetReferences();
                if (references != null)
                {
                    foreach (var r in references)
                    {
                        try
                        {
                            var targetObj = kb.DesignModel.Objects.Get(r.To);
                            if (targetObj != null && !string.IsNullOrEmpty(targetObj.Name))
                            {
                                if (seen.Add(targetObj.Name))
                                {
                                    deps.Add(targetObj.Name);
                                    if (deps.Count >= 10)
                                        break;
                                }
                            }
                        }
                        catch
                        {
                            // Ignore unresolved entities (e.g. Attribute/Domain/Key not in Objects)
                        }
                    }
                }
            }
            catch
            {
                // Ignore failure getting references
            }

            return deps;
        }

        private JObject CalculateMetrics(string source)
        {
            var metrics = new JObject();
            int lines = 0;
            if (!string.IsNullOrEmpty(source))
            {
                lines = 1;
                for (int i = 0; i < source.Length; i++)
                {
                    if (source[i] == '\n') lines++;
                }
            }

            metrics["linesOfCode"] = lines;
            metrics["complexity"] = lines > 500 ? "High" : (lines > 100 ? "Medium" : "Low");
            
            return metrics;
        }
    }
}
