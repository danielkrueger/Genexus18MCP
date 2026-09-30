using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GxMcp.Worker.Structure;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // Reflection-based mutator discovery (genexus_analyze mode=mutators) extracted
    // from LayoutService.cs (plan TECHDEBT-03). Pure move, no logic changes — see
    // plans/README.md TECHDEBT-03.
    public partial class LayoutService
    {
        public string ScanMutators(string target, int limit = 100)
        {
            try
            {
                if (limit <= 0) limit = 100;
                if (limit > 500) limit = 500;

                var obj = _objectService.FindObject(target);
                if (obj == null) return VisualObjectNotFound(target);

                var partNames = new[] { "Layout", "PatternVirtual", "WebForm" };
                var results = new JArray();
                int totalMutators = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                const long budgetMs = 15000;
                bool timedOut = false;

                foreach (var partName in partNames)
                {
                    if (sw.ElapsedMilliseconds > budgetMs) { timedOut = true; break; }

                    var part = PartAccessor.GetPart(obj, partName);
                    if (part == null) continue;

                    var partResult = new JObject
                    {
                        ["part"] = partName,
                        ["partType"] = part.GetType().FullName
                    };

                    var mutators = new JArray();
                    var visited = new HashSet<object>(ReferenceObjectComparer.Instance);
                    ScanObjectMutators(part, part.GetType().Name, 0, 2, mutators, visited, sw, budgetMs);

                    totalMutators += mutators.Count;

                    var sortedMutators = mutators
                        .Cast<JObject>()
                        .OrderByDescending(m => (int)(m["relevance"] ?? 0))
                        .Take(limit)
                        .ToList();

                    var limitedMutators = new JArray();
                    foreach (var m in sortedMutators) limitedMutators.Add(m);

                    partResult["mutatorsTotal"] = mutators.Count;
                    partResult["mutatorsReturned"] = limitedMutators.Count;
                    partResult["mutators"] = limitedMutators;
                    results.Add(partResult);
                }

                bool isEmpty = totalMutators == 0;

                var resultObj = new JObject();
                resultObj["name"] = obj.Name;
                resultObj["type"] = obj.TypeDescriptor.Name;
                resultObj["empty"] = isEmpty;
                resultObj["totalMutators"] = totalMutators;
                resultObj["timedOut"] = timedOut;
                resultObj["elapsedMs"] = sw.ElapsedMilliseconds;
                resultObj["surfaces"] = results;

                if (timedOut)
                {
                    resultObj["help"] = $"Scan aborted after {budgetMs}ms budget. Partial results returned ({totalMutators} endpoints found before timeout).";
                }
                else if (isEmpty)
                {
                    resultObj["help"] = "0 mutation endpoints found. The SDK does not expose writable control paths for this object type.";
                }
                else
                {
                    resultObj["help"] = $"Found {totalMutators} mutation endpoint(s). Look for 'writable_property' and 'setter_method' kinds with high relevance scores for persistent mutation candidates.";
                }

                return Models.McpResponse.Ok(target: target, code: "LayoutMutatorsScanned", result: resultObj);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "LayoutScanMutatorsException",
                    message: ex.Message,
                    hint: "Verify the object exists in the active KB and retry.",
                    nextSteps: new JArray(Models.McpResponse.NextStep("genexus_inspect", new JObject { ["name"] = target }, "Confirms the object is present in the KB.")),
                    target: target);
            }
        }

        private static readonly HashSet<string> DangerousTypeFragments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "KnowledgeBase", "Model", "DesignModel", "EntityManager", "BLServices",
            "Transaction", "DataStore", "Environment", "GxModel", "KBModule",
            "Artech.Architecture.Common.Services", "Artech.Architecture.BL",
            "Artech.Common.Framework", "Artech.Architecture.Common.Descriptors"
        };

        private static bool IsDangerousTraversalType(Type type)
        {
            if (type == null) return true;
            string fullName = type.FullName ?? type.Name ?? "";
            foreach (var frag in DangerousTypeFragments)
            {
                if (fullName.IndexOf(frag, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }


        private static void ScanObjectMutators(
            object instance,
            string sourcePath,
            int depth,
            int maxDepth,
            JArray mutators,
            HashSet<object> visited,
            System.Diagnostics.Stopwatch sw,
            long budgetMs)
        {
            if (instance == null || depth > maxDepth) return;
            if (sw.ElapsedMilliseconds > budgetMs) return;
            if (!visited.Add(instance)) return;

            var type = instance.GetType();
            if (IsDangerousTraversalType(type)) return;

            // At depth 0/1, scan the actual instance members.
            // For nested traversal, switch to static type-only scanning to avoid COM deadlocks.
            ScanTypeMembers(type, sourcePath, depth, maxDepth, mutators, sw, budgetMs, new HashSet<string>());
        }

        /// <summary>
        /// Pure metadata scan — enumerates members by Type reflection only, never calls GetValue.
        /// Safe against COM STA deadlocks.
        /// </summary>
        private static void ScanTypeMembers(
            Type type,
            string sourcePath,
            int depth,
            int maxDepth,
            JArray mutators,
            System.Diagnostics.Stopwatch sw,
            long budgetMs,
            HashSet<string> visitedTypes)
        {
            if (type == null || depth > maxDepth) return;
            if (sw.ElapsedMilliseconds > budgetMs) return;

            string typeKey = type.FullName ?? type.Name;
            if (!visitedTypes.Add(typeKey)) return;
            if (IsDangerousTraversalType(type)) return;

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            // 1. Writable properties
            foreach (var prop in type.GetProperties(flags))
            {
                if (sw.ElapsedMilliseconds > budgetMs) return;
                if (prop.GetIndexParameters().Length > 0) continue;

                bool canRead = prop.CanRead && prop.GetMethod != null;
                bool canWrite = prop.CanWrite && prop.SetMethod != null;

                string propPath = sourcePath + "." + prop.Name;

                if (canWrite)
                {
                    int relevance = ScoreMutatorRelevance(prop.Name, prop.PropertyType, "property");
                    if (relevance > 0 || depth <= 1)
                    {
                        mutators.Add(new JObject
                        {
                            ["kind"] = prop.SetMethod.IsPublic ? "writable_property" : "writable_property_nonpublic",
                            ["path"] = propPath,
                            ["depth"] = depth,
                            ["valueType"] = prop.PropertyType.Name,
                            ["valueTypeFullName"] = prop.PropertyType.FullName,
                            ["getterPublic"] = canRead && prop.GetMethod.IsPublic,
                            ["setterPublic"] = prop.SetMethod.IsPublic,
                            ["relevance"] = relevance
                        });
                    }
                }

                // Traverse into nested type (static only — no GetValue)
                if (canRead && depth < maxDepth && ShouldTraverseMutatorTarget(prop.Name, prop.PropertyType))
                {
                    if (!IsDangerousTraversalType(prop.PropertyType))
                    {
                        ScanTypeMembers(prop.PropertyType, propPath, depth + 1, maxDepth, mutators, sw, budgetMs, visitedTypes);
                    }
                }
            }

            // 2. Methods that accept parameters (potential mutators)
            foreach (var method in type.GetMethods(flags))
            {
                if (sw.ElapsedMilliseconds > budgetMs) return;
                if (method.IsSpecialName) continue;
                if (method.DeclaringType == typeof(object)) continue;

                var parameters = method.GetParameters();
                string methodPath = sourcePath + "." + method.Name + "(" + string.Join(", ", parameters.Select(p => p.ParameterType.Name)) + ")";
                int relevance = ScoreMutatorRelevance(method.Name, method.ReturnType, "method");

                // Setter methods (accept 1+ params)
                if (parameters.Length >= 1 && parameters.Length <= 4)
                {
                    string mName = method.Name.ToLowerInvariant();
                    bool looksLikeMutator =
                        mName.StartsWith("set", StringComparison.Ordinal) ||
                        mName.StartsWith("add", StringComparison.Ordinal) ||
                        mName.StartsWith("remove", StringComparison.Ordinal) ||
                        mName.StartsWith("insert", StringComparison.Ordinal) ||
                        mName.StartsWith("delete", StringComparison.Ordinal) ||
                        mName.StartsWith("update", StringComparison.Ordinal) ||
                        mName.StartsWith("apply", StringComparison.Ordinal) ||
                        mName.StartsWith("load", StringComparison.Ordinal) ||
                        mName.StartsWith("deserialize", StringComparison.Ordinal) ||
                        mName.StartsWith("clear", StringComparison.Ordinal) ||
                        mName.StartsWith("move", StringComparison.Ordinal) ||
                        mName.StartsWith("replace", StringComparison.Ordinal) ||
                        mName.StartsWith("create", StringComparison.Ordinal) ||
                        mName.Contains("control") ||
                        mName.Contains("layout") ||
                        mName.Contains("xml") ||
                        mName.Contains("form");

                    if (looksLikeMutator || relevance > 10)
                    {
                        var paramArray = new JArray();
                        foreach (var p in parameters)
                        {
                            paramArray.Add(new JObject
                            {
                                ["name"] = p.Name,
                                ["type"] = p.ParameterType.Name,
                                ["typeFullName"] = p.ParameterType.FullName,
                                ["isOptional"] = p.IsOptional
                            });
                        }

                        mutators.Add(new JObject
                        {
                            ["kind"] = method.IsPublic ? "setter_method" : "setter_method_nonpublic",
                            ["path"] = methodPath,
                            ["depth"] = depth,
                            ["returnType"] = method.ReturnType.Name,
                            ["isPublic"] = method.IsPublic,
                            ["parameters"] = paramArray,
                            ["relevance"] = relevance + (looksLikeMutator ? 20 : 0)
                        });
                    }
                }

                // Parameterless methods returning collections
                if (parameters.Length == 0 && relevance > 5)
                {
                    bool returnsCollection =
                        typeof(IEnumerable).IsAssignableFrom(method.ReturnType) &&
                        method.ReturnType != typeof(string) &&
                        method.ReturnType != typeof(byte[]);

                    if (returnsCollection)
                    {
                        mutators.Add(new JObject
                        {
                            ["kind"] = method.IsPublic ? "collection_accessor" : "collection_accessor_nonpublic",
                            ["path"] = methodPath,
                            ["depth"] = depth,
                            ["returnType"] = method.ReturnType.Name,
                            ["returnTypeFullName"] = method.ReturnType.FullName,
                            ["isPublic"] = method.IsPublic,
                            ["relevance"] = relevance + 15
                        });
                    }
                }
            }

            // 3. Collection properties (IList, ICollection patterns) — metadata only, no GetValue
            foreach (var prop in type.GetProperties(flags))
            {
                if (sw.ElapsedMilliseconds > budgetMs) return;
                if (prop.GetIndexParameters().Length > 0 || !prop.CanRead) continue;

                bool returnsCollection =
                    typeof(IEnumerable).IsAssignableFrom(prop.PropertyType) &&
                    prop.PropertyType != typeof(string) &&
                    prop.PropertyType != typeof(byte[]);

                if (!returnsCollection) continue;

                string propPath = sourcePath + "." + prop.Name;
                int relevance = ScoreMutatorRelevance(prop.Name, prop.PropertyType, "collection");

                if (relevance > 0 || depth <= 1)
                {
                    // Check if the collection type has Add/Remove methods — metadata-only, no invocation
                    var collectionMethods = prop.PropertyType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(m => m.Name == "Add" || m.Name == "Remove" || m.Name == "Insert" || m.Name == "Clear" || m.Name == "RemoveAt")
                        .Select(m => m.Name)
                        .Distinct()
                        .ToArray();

                    mutators.Add(new JObject
                    {
                        ["kind"] = "collection_property",
                        ["path"] = propPath,
                        ["depth"] = depth,
                        ["collectionType"] = prop.PropertyType.Name,
                        ["collectionTypeFullName"] = prop.PropertyType.FullName,
                        ["isPublic"] = prop.GetMethod?.IsPublic ?? false,
                        ["mutationMethods"] = new JArray(collectionMethods),
                        ["relevance"] = relevance + (collectionMethods.Length > 0 ? 25 : 0)
                    });
                }
            }
        }

        private static int ScoreMutatorRelevance(string name, Type type, string category)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;

            string n = name.ToLowerInvariant();
            string t = (type?.FullName ?? type?.Name ?? "").ToLowerInvariant();
            int score = 0;

            // Name-based scoring
            if (n.Contains("control")) score += 40;
            if (n.Contains("layout")) score += 35;
            if (n.Contains("xml")) score += 30;
            if (n.Contains("form")) score += 25;
            if (n.Contains("source")) score += 20;
            if (n.Contains("caption")) score += 15;
            if (n.Contains("visible")) score += 15;
            if (n.Contains("style")) score += 10;
            if (n.Contains("class")) score += 10;
            if (n.Contains("position")) score += 10;
            if (n.Contains("size")) score += 8;
            if (n.Contains("width") || n.Contains("height")) score += 8;
            if (n.Contains("row") || n.Contains("column") || n.Contains("cell")) score += 12;
            if (n.Contains("table")) score += 15;
            if (n.Contains("report")) score += 12;
            if (n.Contains("printblock")) score += 20;
            if (n.Contains("band")) score += 15;
            if (n.Contains("attribute")) score += 10;
            if (n.Contains("variable")) score += 10;
            if (n.Contains("metadata")) score += 8;
            if (n.Contains("serialize") || n.Contains("deserialize")) score += 25;
            if (n.Contains("load") || n.Contains("apply")) score += 15;

            // Type-based scoring
            if (t.Contains("artech.genexus")) score += 15;
            if (t.Contains("layout")) score += 15;
            if (t.Contains("control")) score += 15;
            if (t.Contains("form")) score += 10;
            if (t.Contains("reportband") || t.Contains("printblock")) score += 20;

            // Penalty for obvious noise
            if (n.StartsWith("get_", StringComparison.Ordinal) && category == "method") score -= 10;
            if (n == "tostring" || n == "gethashcode" || n == "equals" || n == "gettype") score = 0;

            return Math.Max(score, 0);
        }

        private static bool ShouldTraverseMutatorTarget(string memberName, Type memberType)
        {
            if (memberType == null) return false;
            if (memberType == typeof(string)) return false;
            if (memberType.IsPrimitive || memberType.IsEnum) return false;
            if (memberType == typeof(Guid) || memberType == typeof(DateTime)) return false;

            string typeName = (memberType.FullName ?? memberType.Name ?? "").ToLowerInvariant();
            string lowerName = (memberName ?? "").ToLowerInvariant();

            bool strongHint =
                lowerName.Contains("layout") ||
                lowerName.Contains("form") ||
                lowerName.Contains("xml") ||
                lowerName.Contains("control") ||
                lowerName.Contains("meta") ||
                lowerName.Contains("report") ||
                lowerName.Contains("band") ||
                lowerName.Contains("printblock") ||
                typeName.Contains("layout") ||
                typeName.Contains("form") ||
                typeName.Contains("control") ||
                typeName.Contains("report") ||
                typeName.Contains("artech.genexus");

            return strongHint;
        }
    }
}
