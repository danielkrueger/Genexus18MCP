using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Wave 3 — IDE "Save As..." parity. Clones a KBObject's parts under a new
    /// name (same type, same module/folder), optionally also cloning a linked
    /// WorkWithPlus pattern instance.
    ///
    /// All SDK access is hidden behind <see cref="IObjectCloner"/> so the
    /// service can be unit-tested without a live KB. The production cloner
    /// (<see cref="SdkObjectCloner"/>) wires through the existing
    /// ObjectService / WriteService / PatternApplyService code paths so we
    /// preserve every IDE-compatibility fix already living there.
    /// </summary>
    public class SaveAsService
    {
        /// <summary>
        /// Seam used by tests. Production code uses <see cref="SdkObjectCloner"/>.
        /// All methods return a JSON string (Success/Error envelope) to mirror
        /// the rest of the worker service surface.
        /// </summary>
        public interface IObjectCloner
        {
            /// <summary>Resolve a source object by name (+ optional type). Returns null when not found.</summary>
            SourceDescriptor FindSource(string name, string typeFilter);

            /// <summary>Returns true when an object with that name already exists in the KB.</summary>
            bool TargetExists(string newName);

            /// <summary>Create a new empty object of the given type and host name. Returns Success/Error envelope JSON.</summary>
            string CreateObject(string type, string newName);

            /// <summary>Clone a single part's content from source → target. Returns Success/Error envelope JSON.</summary>
            string ClonePart(string sourceName, string newName, string partName, string typeFilter);

            /// <summary>Remove an incomplete target after a clone failure. Returns Success/Error envelope JSON.</summary>
            string DeleteTarget(string newName, string typeFilter);

            /// <summary>Return the WorkWithPlus pattern instance bound to the source, or null when none.</summary>
            PatternInstanceDescriptor FindWwpInstance(string sourceName);

            /// <summary>Apply WorkWithPlus pattern to the new host. Returns Success/Error envelope JSON.</summary>
            string ApplyWwpPattern(string newName, PatternInstanceDescriptor sourceInstance);
        }

        public sealed class SourceDescriptor
        {
            public string Name;
            public string Type;
            public IList<string> Parts;
        }

        public sealed class PatternInstanceDescriptor
        {
            public string PatternKey;   // e.g. "WorkWithPlus"
            public string HostName;     // source host name
        }

        private readonly IObjectCloner _cloner;

        public SaveAsService(IObjectCloner cloner)
        {
            if (cloner == null) throw new ArgumentNullException("cloner");
            _cloner = cloner;
        }

        public string SaveAs(JObject args)
        {
            string sourceName = args?["name"]?.ToString();
            string newName = args?["newName"]?.ToString();
            string typeFilter = args?["type"]?.ToString();
            bool includePattern = args?["includePatternInstance"]?.ToObject<bool?>() ?? false;
            bool overwrite = args?["overwrite"]?.ToObject<bool?>() ?? false;
            bool dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false;

            if (string.IsNullOrWhiteSpace(sourceName))
                return Err("usage_error", "'name' is required (source object).", null);
            if (string.IsNullOrWhiteSpace(newName))
                return Err("usage_error", "'newName' is required (target object).", null);
            if (string.Equals(sourceName, newName, StringComparison.OrdinalIgnoreCase))
                return Err("usage_error", "newName must differ from source name.", null);

            var src = _cloner.FindSource(sourceName, typeFilter);
            if (src == null)
            {
                return McpResponse.Err(
                    code: "NotFound",
                    message: "Source object not found: " + sourceName,
                    hint: "Check the object name and type filter, or use genexus_list_objects to enumerate available objects.",
                    nextSteps: new JArray {
                        McpResponse.NextStep("genexus_list_objects", null, "Enumerate available objects to verify the source name.")
                    },
                    extra: new JObject { ["sourceName"] = sourceName });
            }

            if (_cloner.TargetExists(newName))
            {
                // v1: refuse even with overwrite=true — deleting an existing
                // object is destructive enough that we want the agent to call
                // genexus_delete_object explicitly. overwrite=true currently
                // just surfaces a clearer hint.
                string hint = overwrite
                    ? "overwrite=true is reserved for a future revision. For now, delete the existing object first via genexus_delete_object name=" + newName + " confirm=true, then re-run genexus_save_as."
                    : "Pick a different newName, or delete the existing object via genexus_delete_object name=" + newName + " confirm=true.";
                return McpResponse.Err(
                    code: "TargetExists",
                    message: "An object named '" + newName + "' already exists.",
                    hint: hint,
                    nextSteps: new JArray {
                        McpResponse.NextStep("genexus_delete_object", new JObject { ["name"] = newName, ["confirm"] = true }, "Delete the existing target before cloning."),
                        McpResponse.NextStep("genexus_save_as", new JObject { ["name"] = sourceName, ["newName"] = "<differentName>" }, "Use a different target name.")
                    },
                    extra: new JObject { ["sourceName"] = sourceName, ["newName"] = newName });
            }

            // ---- dryRun: build the plan, never touch the SDK. ----
            if (dryRun)
            {
                return McpResponse.Ok(
                    target: sourceName,
                    code: "DryRun",
                    result: new JObject
                    {
                        ["sourceName"] = sourceName,
                        ["plan"] = new JObject
                        {
                            ["createType"] = src.Type,
                            ["newName"] = newName,
                            ["partsToClone"] = new JArray(src.Parts ?? new List<string>()),
                            ["includePatternInstance"] = includePattern
                        }
                    });
            }

            // ---- Live path. Track progress so a partial failure surfaces a
            //      clear "where it stopped" + undo hint envelope. ----
            var completedSteps = new JArray();
            var partsCloned = new JArray();
            var partsSkipped = new JArray();

            // Step 1: create empty target object of the same type.
            string createResult = _cloner.CreateObject(src.Type, newName);
            if (!IsSuccess(createResult))
            {
                return Partial(sourceName, newName, completedSteps, "create:" + newName, createResult);
            }
            completedSteps.Add("create:" + newName);

            // Step 2: clone each part. Explicitly unsupported/empty parts are skipped (issue #45),
            // but a real write failure means the target is not a functional clone. Roll it back
            // immediately instead of returning SavedAs over a partial object (issue #118).
            foreach (var part in (src.Parts ?? new List<string>()))
            {
                string r = _cloner.ClonePart(sourceName, newName, part, typeFilter);
                if (IsSkipped(r))
                {
                    partsSkipped.Add(new JObject { ["part"] = part, ["detail"] = SafeParseOrString(r) });
                    continue;
                }
                if (!IsSuccess(r))
                {
                    return RollbackFailedClone(sourceName, newName, src.Type, completedSteps,
                        partsCloned, partsSkipped, part, r);
                }
                completedSteps.Add("clonePart:" + part);
                partsCloned.Add(part);
            }

            // An object with no cloned part is not a clone. Remove the empty target too.
            if (partsCloned.Count == 0)
            {
                return RollbackFailedClone(sourceName, newName, src.Type, completedSteps,
                    partsCloned, partsSkipped, "<all>", "No cloneable parts on source.");
            }

            // Step 3: optionally clone the WWP pattern instance.
            JObject patternBlock = null;
            if (includePattern)
            {
                var inst = _cloner.FindWwpInstance(sourceName);
                if (inst != null)
                {
                    string applyResult = _cloner.ApplyWwpPattern(newName, inst);
                    bool ok = IsSuccess(applyResult);
                    patternBlock = new JObject
                    {
                        ["name"] = newName,
                        ["pattern"] = inst.PatternKey,
                        ["applied"] = ok,
                        ["detail"] = SafeParseOrString(applyResult)
                    };
                    if (ok) completedSteps.Add("applyPattern:" + inst.PatternKey);
                }
                // No WWP instance on source → silently omit the block (per spec).
            }

            var payload = new JObject
            {
                ["sourceName"] = sourceName,
                ["created"] = new JObject
                {
                    ["name"] = newName,
                    ["type"] = src.Type,
                    ["partsCloned"] = partsCloned
                }
            };
            if (partsSkipped.Count > 0) ((JObject)payload["created"])["partsSkipped"] = partsSkipped;
            if (patternBlock != null) payload["patternInstance"] = patternBlock;
            return McpResponse.Ok(target: newName, code: "SavedAs", result: payload);
        }

        private string RollbackFailedClone(string sourceName, string newName, string type,
            JArray completedSteps, JArray partsCloned, JArray partsSkipped, string failedPart, string innerResult)
        {
            string cleanupResult;
            try { cleanupResult = _cloner.DeleteTarget(newName, type); }
            catch (Exception ex) { cleanupResult = ex.Message; }
            bool removed = IsSuccess(cleanupResult);

            var nextSteps = new JArray();
            if (!removed)
            {
                nextSteps.Add(McpResponse.NextStep("genexus_delete_object",
                    new JObject { ["name"] = newName, ["type"] = type, ["confirm"] = true },
                    "Remove the incomplete clone before retrying."));
            }

            return McpResponse.Err(
                code: removed ? "SaveAsPartFailed" : "PartialFailure",
                message: removed
                    ? "Save-as failed while cloning part '" + failedPart + "'; the incomplete target was removed."
                    : "Save-as failed while cloning part '" + failedPart + "', and automatic cleanup also failed.",
                hint: removed
                    ? "Fix the source-part validation error and retry; no partial target remains."
                    : "Delete the incomplete target before retrying.",
                nextSteps: nextSteps,
                target: newName,
                extra: new JObject
                {
                    ["sourceName"] = sourceName,
                    ["newName"] = newName,
                    ["completedSteps"] = completedSteps,
                    ["partsCloned"] = partsCloned,
                    ["partsSkipped"] = partsSkipped,
                    ["failedPart"] = failedPart,
                    ["detail"] = SafeParseOrString(innerResult),
                    ["cleanup"] = new JObject
                    {
                        ["attempted"] = true,
                        ["removed"] = removed,
                        ["detail"] = SafeParseOrString(cleanupResult)
                    }
                });
        }

        private static string Partial(string sourceName, string newName, JArray completedSteps, string failedStep, string innerResult)
        {
            return McpResponse.Err(
                code: "PartialFailure",
                message: "Save-as failed at step '" + failedStep + "'; " + completedSteps.Count + " step(s) already completed.",
                hint: "Use genexus_delete_object name=" + newName + " confirm=true to remove the half-cloned target, then retry.",
                nextSteps: new JArray {
                    McpResponse.NextStep("genexus_delete_object", new JObject { ["name"] = newName, ["confirm"] = true }, "Remove the partially cloned object before retrying."),
                    McpResponse.NextStep("genexus_save_as", new JObject { ["name"] = sourceName, ["newName"] = newName }, "Retry the clone after cleanup.")
                },
                extra: new JObject
                {
                    ["sourceName"] = sourceName,
                    ["newName"] = newName,
                    ["completedSteps"] = completedSteps,
                    ["failedStep"] = failedStep,
                    ["detail"] = SafeParseOrString(innerResult)
                });
        }

        private static string Err(string code, string message, string sourceName)
        {
            return McpResponse.Err(
                code: code,
                message: message,
                nextSteps: new JArray {
                    McpResponse.NextStep("genexus_save_as", new JObject { ["name"] = sourceName ?? "<name>", ["newName"] = "<newName>" }, "Retry with valid arguments.")
                },
                extra: sourceName != null ? new JObject { ["sourceName"] = sourceName } : null);
        }

        private static bool IsSuccess(string envelope)
        {
            if (string.IsNullOrEmpty(envelope)) return false;
            try
            {
                var j = JObject.Parse(envelope);
                string status = j["status"]?.ToString();
                // Accept both v2.8.0 canonical "ok" and legacy "Success".
                if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase)) return true;
                // Some services return no status on success and only set "error" on failure.
                return j["error"] == null && j["code"] == null;
            }
            catch { return false; }
        }

        private static bool IsSkipped(string envelope)
        {
            if (!IsSuccess(envelope)) return false;
            try
            {
                var j = JObject.Parse(envelope);
                return string.Equals(j["code"]?.ToString(), "Skipped", StringComparison.OrdinalIgnoreCase)
                    || (j["result"]?["skipped"]?.ToObject<bool?>() ?? false);
            }
            catch { return false; }
        }

        private static JToken SafeParseOrString(string s)
        {
            if (s == null) return JValue.CreateNull();
            try { return JToken.Parse(s); } catch { return new JValue(s); }
        }
    }

    /// <summary>
    /// Production cloner — wires <see cref="SaveAsService.IObjectCloner"/> to
    /// the existing worker services so every IDE-compat fix already living in
    /// ObjectService/WriteService applies on the cloned target too.
    ///
    /// Note: clone-via-source-text is the safe, type-agnostic strategy that
    /// works for every object type the SDK exposes a textual Source for
    /// (Transaction, Procedure, WebPanel, SDPanel, SDT, DataProvider, Domain,
    /// Dashboard, Theme, MasterPage). Binary-only parts (assets, theme
    /// images) are not cloned — same limitation as genexus_export_unified.
    /// </summary>
    public sealed class SdkObjectCloner : SaveAsService.IObjectCloner
    {
        private readonly ObjectService _objects;
        private readonly WriteService _writes;
        private readonly PatternApplyService _patterns;

        public SdkObjectCloner(ObjectService objects, WriteService writes, PatternApplyService patterns)
        {
            _objects = objects;
            _writes = writes;
            _patterns = patterns;
        }

        public SaveAsService.SourceDescriptor FindSource(string name, string typeFilter)
        {
            var obj = _objects.FindObject(name, typeFilter);
            if (obj == null) return null;
            string[] parts;
            try { parts = GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj); }
            catch { parts = new[] { "Source" }; }
            return new SaveAsService.SourceDescriptor
            {
                Name = obj.Name,
                Type = obj.TypeDescriptor?.Name ?? typeFilter ?? "Unknown",
                Parts = parts
            };
        }

        public bool TargetExists(string newName)
        {
            try { return _objects.FindObject(newName, null) != null; }
            catch { return false; }
        }

        public string CreateObject(string type, string newName)
        {
            return _objects.CreateObject(type, newName);
        }

        public string ClonePart(string sourceName, string newName, string partName, string typeFilter)
        {
            // issue #51: the SDT structure part cannot be cloned through the textual DSL without
            // dropping the root Collection flag, the collection item name, and Domain/SDT-typed
            // members. Clone it natively (SerializeToXml -> DeserializeFromXml) instead. Returns
            // null when not applicable (non-SDT, or the native round-trip didn't stick), in which
            // case we fall through to the text path below.
            bool isStructureAlias = partName != null &&
                (partName.Equals("SDTStructure", StringComparison.OrdinalIgnoreCase) ||
                 partName.Equals("DataSelectorStructure", StringComparison.OrdinalIgnoreCase) ||
                 partName.Equals("Structure", StringComparison.OrdinalIgnoreCase));
            if (isStructureAlias)
            {
                string native = _objects.CloneSdtStructurePart(sourceName, newName);
                if (native != null) return native;

                string dsNative = _objects.CloneDataSelectorStructurePart(sourceName, newName);
                if (dsNative != null) return dsNative;
            }

            // issue #406: a report Procedure's Layout is cloned natively, not through the WebForm write path.
            if (string.Equals(partName, "Layout", StringComparison.OrdinalIgnoreCase))
            {
                string layout = _objects.CloneReportLayoutPart(sourceName, newName, typeFilter);
                if (layout != null) return layout;
            }

            // Read source-part as text, write to new object via the same
            // WriteService pipeline a normal genexus_edit goes through.
            string readJson = _objects.ReadObjectSource(sourceName, partName, null, null, "mcp", false, typeFilter);
            JObject readObj;
            try { readObj = JObject.Parse(readJson); }
            catch { return readJson; }

            var srcToken = readObj["source"];
            if (srcToken == null)
            {
                // Nothing to clone for this part (binary / unsupported) — that's not an error.
                return McpResponse.Ok(code: "Skipped", result: new JObject { ["skipped"] = true, ["part"] = partName });
            }

            string code = srcToken.ToString();
            return _writes.WriteObject(newName, partName, code, typeFilter);
        }

        public string DeleteTarget(string newName, string typeFilter)
        {
            return _objects.DeleteObject(newName, typeFilter, confirm: true);
        }

        public SaveAsService.PatternInstanceDescriptor FindWwpInstance(string sourceName)
        {
            // The SDK exposes pattern-instance metadata through the object's
            // PatternInstance property, but the discovery path differs per
            // KB. Conservative approach: rely on PatternApplyService's
            // existing detection, which already powers genexus_apply_pattern
            // reapply / diagnose. If the source has a WWP instance, the
            // service knows; otherwise we return null and SaveAs skips the
            // pattern block.
            try
            {
                var obj = _objects.FindObject(sourceName, null);
                if (obj == null) return null;
                // Walk the source object's parts looking for a PatternInstance member.
                // Mirrors the detection in PatternApplyService.ApplyPattern (~line 357)
                // so the two paths agree on what constitutes an "already-patterned" object.
                bool hasInstance = false;
                try
                {
                    foreach (var part in obj.Parts)
                    {
                        var p = part as Artech.Architecture.Common.Objects.KBObjectPart;
                        if (p == null) continue;
                        if (string.Equals(p.Name, "PatternInstance", StringComparison.OrdinalIgnoreCase) ||
                            p.GetType().Name.IndexOf("PatternInstance", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            hasInstance = true;
                            break;
                        }
                    }
                }
                catch { /* best-effort */ }
                if (!hasInstance) return null;
                return new SaveAsService.PatternInstanceDescriptor
                {
                    PatternKey = "WorkWithPlus",
                    HostName = sourceName
                };
            }
            catch { return null; }
        }

        public string ApplyWwpPattern(string newName, SaveAsService.PatternInstanceDescriptor sourceInstance)
        {
            // Direct call to the canonical PatternApplyService.ApplyPattern signature.
            // settings=null means "use defaults" — same behaviour as the IDE's Save-As.
            if (_patterns == null)
            {
                return McpResponse.Err(
                    code: "PatternServiceUnavailable",
                    message: "PatternApplyService is not wired in this worker build.",
                    nextSteps: new JArray { McpResponse.NextStep("genexus_apply_pattern", new JObject { ["name"] = newName }, "Apply the pattern manually after the object is created.") });
            }
            try
            {
                string key = sourceInstance?.PatternKey ?? "WorkWithPlus";
                string result = _patterns.ApplyPattern(newName, key, settings: null);
                return string.IsNullOrEmpty(result) ? McpResponse.Ok(target: newName, code: "PatternApplied") : result;
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "PatternApplyFailed",
                    message: ex.Message,
                    nextSteps: new JArray { McpResponse.NextStep("genexus_apply_pattern", new JObject { ["name"] = newName }, "Apply the pattern manually.") });
            }
        }
    }
}
