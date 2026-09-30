using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxMcp.Worker.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed partial class TextMirrorService
    {
        private static JObject RemoveMirroredObjects(string root, List<PendingChange> deleted, out string error)
        {
            error = null;
            string manifestPath = Path.Combine(root, SdkTextTreeService.ManifestFileName);
            var summary = new JObject
            {
                ["requested"] = deleted?.Count ?? 0,
                ["removedObjects"] = 0,
                ["removedFiles"] = 0,
                ["manifestUpdated"] = false
            };
            if (deleted == null || deleted.Count == 0 || !File.Exists(manifestPath)) return summary;

            JObject manifest;
            try { manifest = JObject.Parse(File.ReadAllText(manifestPath)); }
            catch (Exception ex)
            {
                error = "Mirror manifest read failed: " + ex.Message;
                summary["reconciliationRequired"] = true;
                return summary;
            }
            JArray objects = manifest["objects"] as JArray;
            if (objects == null) return summary;

            var deletedKeys = new HashSet<string>(
                deleted.Select(change => (change.Type ?? string.Empty) + ":" + change.Name),
                StringComparer.OrdinalIgnoreCase);
            var retained = new JArray();
            int removedObjects = 0;
            int removedFiles = 0;
            foreach (JObject item in objects.OfType<JObject>())
            {
                string key = (item["type"]?.ToString() ?? string.Empty) + ":" + item["name"]?.ToString();
                if (!deletedKeys.Contains(key))
                {
                    retained.Add(item);
                    continue;
                }

                bool itemOk = true;
                if (!DeleteMirroredFileIfPresent(root, item["file"]?.ToString(), ref removedFiles))
                {
                    itemOk = false;
                    error = error ?? "Could not remove mirrored file for " + key + ".";
                }
                if (item["companions"] is JArray companions)
                {
                    foreach (JObject companion in companions.OfType<JObject>())
                        if (!DeleteMirroredFileIfPresent(root, companion["file"]?.ToString(), ref removedFiles))
                        {
                            itemOk = false;
                            error = error ?? "Could not remove mirrored companion for " + key + ".";
                        }
                }
                if (itemOk) removedObjects++;
                else retained.Add(item);
            }

            manifest["objects"] = retained;
            try
            {
                WriteTextAtomically(manifestPath, manifest.ToString(Formatting.Indented));
                summary["manifestUpdated"] = true;
            }
            catch (Exception ex)
            {
                error = "Mirror manifest update failed: " + ex.Message;
                summary["reconciliationRequired"] = true;
            }
            summary["removedObjects"] = removedObjects;
            summary["removedFiles"] = removedFiles;
            if (error != null) summary["reconciliationRequired"] = true;
            return summary;
        }

        internal static JObject RemoveMirroredObjectsForTest(string root, IEnumerable<Tuple<string, string>> identities, out string error)
        {
            var deleted = (identities ?? Enumerable.Empty<Tuple<string, string>>())
                .Where(pair => pair != null && !string.IsNullOrWhiteSpace(pair.Item2))
                .Select(pair => new PendingChange { Name = pair.Item1, Type = pair.Item2, Deleted = true })
                .ToList();
            return RemoveMirroredObjects(root, deleted, out error);
        }

        private static bool DeleteMirroredFileIfPresent(string root, string relative, ref int removedFiles)
        {
            if (string.IsNullOrWhiteSpace(relative)) return true;
            if (!MirrorRootFile.TryResolve(root, relative, out string full)) return false;
            if (!File.Exists(full)) return true;
            if (!MirrorRootFile.TryDelete(full)) return false;
            removedFiles++;
            return true;
        }

        private static void WriteTextAtomically(string path, string content)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, content ?? string.Empty, new System.Text.UTF8Encoding(false));
                if (File.Exists(path))
                {
                    try { File.Replace(temporary, path, null); }
                    catch { File.Delete(path); File.Move(temporary, path); }
                }
                else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        private int RemoveOrphanedFiles(string root, bool includeReferences)
        {
            SearchIndex index = _indexCacheService?.TryGetLoadedIndex();
            if (index == null) return 0;
            string manifestPath = Path.Combine(root, SdkTextTreeService.ManifestFileName);
            if (!File.Exists(manifestPath)) return 0;
            int removed = 0;
            try
            {
                JArray objects = JObject.Parse(File.ReadAllText(manifestPath))["objects"] as JArray;
                if (objects == null) return 0;
                foreach (JObject item in objects.OfType<JObject>())
                {
                    string role = item["role"]?.ToString();
                    if (string.Equals(role, "ref", StringComparison.OrdinalIgnoreCase) && !includeReferences) continue;
                    string name = item["name"]?.ToString();
                    string type = item["type"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name) || index.FindByName(name).Any(entry => string.Equals(entry.Type, type, StringComparison.OrdinalIgnoreCase))) continue;
                    if (MirrorRootFile.TryResolve(root, item["file"]?.ToString(), out string file) && MirrorRootFile.TryDelete(file)) removed++;
                    if (item["companions"] is JArray companions)
                    {
                        foreach (JObject companion in companions.OfType<JObject>())
                            if (MirrorRootFile.TryResolve(root, companion["file"]?.ToString(), out string companionFile) && MirrorRootFile.TryDelete(companionFile)) removed++;
                    }
                }
            }
            catch (Exception ex) { lock (_gate) { _lastError = "Orphan cleanup failed: " + ex.Message; } }
            return removed;
        }

        }
}
