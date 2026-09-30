using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Helpers;
namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 90 — genexus_kb_readme.
    ///
    /// Walks the KB and produces a Markdown README:
    ///  - KB name + path
    ///  - Primary entities (Transactions sorted by reference count)
    ///  - Main entry points (Startup / DefaultObject)
    ///  - Module dependencies
    ///  - Top 10 most-edited objects (.gx/snapshots/ histogram)
    /// </summary>
    public class KbReadmeService
    {
        private readonly KbService _kbService;
        private readonly IndexCacheService _indexCacheService;

        public KbReadmeService(KbService kbService, IndexCacheService indexCacheService)
        {
            _kbService = kbService;
            _indexCacheService = indexCacheService;
        }

        public string Generate(string action, string outputPath)
        {
            if (!string.Equals(action, "generate", StringComparison.OrdinalIgnoreCase))
                return Models.McpResponse.Err(code: "InvalidAction", message: "Only action='generate' is supported.", hint: "Pass action='generate'.", nextSteps: new JArray(McpResponse.NextStep("genexus_kb_readme", new JObject { ["action"] = "generate" }, "Retry with action='generate'.")));

            string kbPath = null;
            string kbName = null;
            try { kbPath = _kbService?.GetKbPath(); } catch { }
            try { kbName = _kbService?.GetKB()?.Name; } catch { }

            SearchIndex idx = null;
            try { idx = _indexCacheService?.GetIndex(); } catch { }

            string launcher = null;
            try { launcher = _kbService?.GetLauncherObjectName(); } catch { }

            string md = BuildMarkdown(kbName, kbPath, launcher, idx);

            var resultPayload = new JObject
            {
                ["kb"] = new JObject
                {
                    ["name"] = kbName ?? "(no KB open)",
                    ["path"] = kbPath ?? ""
                }
            };

            if (!string.IsNullOrEmpty(outputPath))
            {
                try
                {
                    string dir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(outputPath, md, new UTF8Encoding(false));
                    resultPayload["outputPath"] = outputPath;
                    resultPayload["bytesWritten"] = md.Length;
                }
                catch (Exception ex)
                {
                    return Models.McpResponse.Err(code: "WriteFailed", message: "Could not write README to " + outputPath + ": " + ex.Message, hint: "Check that the output path is writable and the directory exists.", nextSteps: new JArray(McpResponse.NextStep("genexus_kb_readme", new JObject { ["action"] = "generate" }, "Retry without outputPath to get the markdown inline.")));
                }
            }
            else
            {
                resultPayload["markdown"] = md;
            }
            return Models.McpResponse.Ok(code: "ReadmeGenerated", result: resultPayload);
        }

        internal static string BuildMarkdown(string kbName, string kbPath, string launcher, SearchIndex idx)
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(string.IsNullOrEmpty(kbName) ? "Knowledge Base" : kbName).Append('\n').Append('\n');
            sb.Append("- **Path:** `").Append(kbPath ?? "(unknown)").Append("`\n");
            sb.Append("- **Generated:** ").Append(DateTime.UtcNow.ToString("u")).Append('\n');
            if (!string.IsNullOrEmpty(launcher))
                sb.Append("- **Launcher:** `").Append(launcher).Append("`\n");
            sb.Append('\n');

            if (idx != null && idx.Objects != null && idx.Objects.Count > 0)
            {
                var all = idx.Objects.Values.ToList();

                sb.Append("## Primary entities\n\n");
                sb.Append("Transactions sorted by inbound reference count.\n\n");
                var transactions = all
                    .Where(e => string.Equals(e.Type, "Transaction", StringComparison.OrdinalIgnoreCase))
                    .Select(e => new
                    {
                        Entry = e,
                        Refs = (e.CalledBy?.Count ?? 0)
                    })
                    .OrderByDescending(x => x.Refs)
                    .ThenBy(x => x.Entry.Name)
                    .Take(20)
                    .ToList();
                if (transactions.Count == 0)
                {
                    sb.Append("_No transactions found in index._\n\n");
                }
                else
                {
                    sb.Append("| Transaction | Inbound refs | Description |\n");
                    sb.Append("|---|---|---|\n");
                    foreach (var t in transactions)
                    {
                        sb.Append("| `").Append(SafeMd(t.Entry.Name)).Append("` | ")
                          .Append(t.Refs).Append(" | ")
                          .Append(SafeMd(SdkReflection.Truncate(t.Entry.Description, 80))).Append(" |\n");
                    }
                    sb.Append('\n');
                }

                sb.Append("## Main entry points\n\n");
                var entryPoints = all
                    .Where(e => HasTag(e, "Startup") || HasTag(e, "Main") || HasTag(e, "DefaultObject")
                                || string.Equals(e.Name, launcher, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.Name)
                    .Take(15)
                    .ToList();
                if (entryPoints.Count == 0)
                {
                    sb.Append("_No startup/default objects tagged in index._\n\n");
                }
                else
                {
                    foreach (var ep in entryPoints)
                        sb.Append("- **").Append(SafeMd(ep.Name)).Append("** (").Append(ep.Type).Append(")\n");
                    sb.Append('\n');
                }

                sb.Append("## Modules\n\n");
                var modules = all
                    .Where(e => !string.IsNullOrEmpty(e.Module))
                    .GroupBy(e => e.Module, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new { Name = g.Key, Count = g.Count() })
                    .OrderByDescending(g => g.Count)
                    .Take(15)
                    .ToList();
                if (modules.Count == 0)
                {
                    sb.Append("_No modules detected._\n\n");
                }
                else
                {
                    sb.Append("| Module | Objects |\n|---|---|\n");
                    foreach (var m in modules)
                        sb.Append("| ").Append(SafeMd(m.Name)).Append(" | ").Append(m.Count).Append(" |\n");
                    sb.Append('\n');
                }

                sb.Append("## Module dependencies\n\n");
                var deps = ComputeModuleDependencies(all);
                if (deps.Count == 0)
                {
                    sb.Append("_No cross-module call edges detected in index._\n\n");
                }
                else
                {
                    sb.Append("| From | To | Edges |\n|---|---|---|\n");
                    foreach (var d in deps.OrderByDescending(x => x.Value).Take(20))
                    {
                        var parts = d.Key.Split(new[] { "→" }, StringSplitOptions.None);
                        sb.Append("| ").Append(SafeMd(parts[0])).Append(" | ")
                          .Append(SafeMd(parts.Length > 1 ? parts[1] : "?")).Append(" | ")
                          .Append(d.Value).Append(" |\n");
                    }
                    sb.Append('\n');
                }
            }
            else
            {
                sb.Append("_Index not available — open a KB and run `genexus_lifecycle action=index`._\n\n");
            }

            sb.Append("## Top 10 most-edited objects\n\n");
            var topEdited = TopEditedObjects(kbPath, 10);
            if (topEdited.Count == 0)
            {
                sb.Append("_No edit snapshots found in `.gx/snapshots/`._\n\n");
            }
            else
            {
                sb.Append("| Object key | Edits |\n|---|---|\n");
                foreach (var kv in topEdited)
                    sb.Append("| `").Append(SafeMd(kv.Key)).Append("` | ").Append(kv.Value).Append(" |\n");
                sb.Append('\n');
            }

            sb.Append("---\n");
            sb.Append("_Generated by `genexus_kb_readme`._\n");
            return sb.ToString();
        }

        internal static Dictionary<string, int> ComputeModuleDependencies(List<SearchIndex.IndexEntry> all)
        {
            var byName = all.Where(e => !string.IsNullOrEmpty(e.Name))
                .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var edges = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var src in all)
            {
                if (string.IsNullOrEmpty(src.Module) || src.Calls == null) continue;
                foreach (var calleeName in src.Calls)
                {
                    if (!byName.TryGetValue(calleeName, out var callee)) continue;
                    if (string.IsNullOrEmpty(callee.Module)) continue;
                    if (string.Equals(callee.Module, src.Module, StringComparison.OrdinalIgnoreCase)) continue;
                    string key = src.Module + "→" + callee.Module;
                    edges[key] = edges.TryGetValue(key, out int n) ? n + 1 : 1;
                }
            }
            return edges;
        }

        internal static List<KeyValuePair<string, int>> TopEditedObjects(string kbPath, int top)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(kbPath)) return new List<KeyValuePair<string, int>>();
            string snapDir = Path.Combine(kbPath, ".gx", "snapshots");
            if (!Directory.Exists(snapDir)) return new List<KeyValuePair<string, int>>();
            try
            {
                foreach (var path in Directory.EnumerateFiles(snapDir))
                {
                    string fn = Path.GetFileName(path);
                    if (!(fn.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                          || fn.EndsWith(".bak.gz", StringComparison.OrdinalIgnoreCase))) continue;
                    // Shape: <guidSanitized>-<part>-<timestamp>.bak[.gz]
                    var parts = fn.Split('-');
                    if (parts.Length < 3) continue;
                    string key = parts[0] + "/" + parts[1];
                    counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
                }
            }
            catch { }
            return counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(top).ToList();
        }

        private static bool HasTag(SearchIndex.IndexEntry e, string tag)
        {
            if (e?.Tags == null) return false;
            foreach (var t in e.Tags) if (string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string SafeMd(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }

    }
}
