using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    // Item 56: import an object from another KB at the FILESYSTEM level.
    // Full SDK-routed import (resolve dependencies via genexus_analyze
    // mode=impact on the source KB) is structurally infeasible from the
    // gateway because the per-KB worker can only host one open KB at a time —
    // opening the source KB inside the active worker would evict the target
    // and break the import. Shipping the limited-but-real filesystem variant:
    // copy <source>/Objects/<Type>/<Name>/* to <target>/Objects/<Type>/<Name>/.
    // Caller is expected to follow up with genexus_lifecycle action=index
    // (force=true) so the SDK picks up the new object.
    internal static class KbImportHelper
    {
        // SECURITY: `name` and `type` are LLM-controlled and flow into
        // Path.Combine + Directory.Delete/CreateDirectory/CopyTo. Without an
        // allowlist, "..\\..\\x" escapes the Objects/ tree and can delete then
        // overwrite an arbitrary directory. The allowlist itself is shared - see
        // SafePathSegment, which also serves the Worker-side time-travel and
        // baseline paths that previously had their own copies.
        internal static bool IsSafeSegment(string value) => SafePathSegment.IsSafe(value);

        // Defence in depth: even after the allowlist, confirm the resolved path is
        // still rooted under <targetKbPath>/Objects/ before any delete/copy.
        private static bool IsWithin(string root, string candidate)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
            var candFull = Path.GetFullPath(candidate);
            return candFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
        }

        public static JObject ImportObject(string sourceKbPath, string targetKbPath, string name, string type)
        {
            if (!IsSafeSegment(type) || !IsSafeSegment(name))
            {
                return new JObject
                {
                    ["status"] = "Error",
                    ["code"] = "InvalidName",
                    ["message"] = "'type' and 'name' must match [A-Za-z0-9._-]{1,200} (no path separators or traversal).",
                    ["hint"] = "Pass exact directory names from Objects/<Type>/<Name>/."
                };
            }
            string sourceObjDir = Path.Combine(sourceKbPath, "Objects", type, name);
            string targetObjRoot = Path.Combine(targetKbPath, "Objects");
            if (!Directory.Exists(sourceObjDir))
            {
                return new JObject
                {
                    ["status"] = "Error",
                    ["code"] = "ObjectNotFound",
                    ["message"] = $"Object '{type}:{name}' was not found in the source Knowledge Base.",
                    ["hint"] = "Check 'type' (case-sensitive) and 'name'; pass exact directory names from Objects/<Type>/<Name>/."
                };
            }
            string targetObjDir = Path.Combine(targetObjRoot, type, name);
            if (!IsWithin(targetObjRoot, targetObjDir) || !IsWithin(sourceKbPath, sourceObjDir))
            {
                return new JObject
                {
                    ["status"] = "Error",
                    ["code"] = "PathEscape",
                    ["message"] = "Resolved import path escapes the KB Objects/ tree; refusing.",
                };
            }
            bool overwrote = Directory.Exists(targetObjDir);
            try
            {
                if (overwrote)
                {
                    Directory.Delete(targetObjDir, true);
                }
                Directory.CreateDirectory(targetObjDir);
                int files = 0;
                long bytes = 0;
                foreach (var f in new DirectoryInfo(sourceObjDir).GetFiles("*", SearchOption.TopDirectoryOnly))
                {
                    string targetFile = Path.Combine(targetObjDir, f.Name);
                    f.CopyTo(targetFile, overwrite: true);
                    files++;
                    bytes += f.Length;
                }
                return new JObject
                {
                    ["status"] = "Success",
                    ["code"] = "PartialFeature",
                    ["name"] = name,
                    ["type"] = type,
                    ["filesCopied"] = files,
                    ["bytesCopied"] = bytes,
                    ["overwroteExisting"] = overwrote,
                    ["targetPath"] = targetObjDir,
                    ["dependencies"] = new JArray(), // see hint below
                    ["hint"] = "Filesystem-level import only — dependencies are NOT resolved. " +
                               "After import, run genexus_lifecycle action=index force=true to refresh the worker's " +
                               "index, then validate references via genexus_analyze mode=impact target=" + name + "."
                };
            }
            catch (Exception ex)
            {
                string operationId = Guid.NewGuid().ToString("N");
                Program.Log($"{{\"event\":\"kb_import_failed\",\"operationId\":\"{operationId}\",\"source\":\"{LogValue(sourceKbPath)}\",\"target\":\"{LogValue(targetKbPath)}\",\"objectType\":\"{LogValue(type)}\",\"objectName\":\"{LogValue(name)}\",\"exceptionType\":\"{ex.GetType().FullName}\",\"exception\":\"{LogValue(ex.ToString())}\"}}");
                return new JObject
                {
                    ["status"] = "Error",
                    ["code"] = "IoError",
                    ["message"] = "Import failed while accessing the Knowledge Base. See server logs for details.",
                    ["operationId"] = operationId
                };
            }
        }

        internal static string LogValue(string value)
        {
            // Shared with MacroSuggestionService, Program.Http and the Worker's
            // PreviewService/SharedWorkerHost: one redaction rule, not five.
            string redacted = LogRedaction.Redact(value);
            return redacted.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace(((char)13).ToString(), "\r").Replace(((char)10).ToString(), "\n");
        }
    }
}