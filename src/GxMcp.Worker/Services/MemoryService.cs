using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Phase 1 — genexus_memory. Per-KB fact store, explicit save only (no
    /// auto-surfacing, no consolidate/promote — later phases). Records are
    /// append-only JSON-lines under <c>&lt;kbPath&gt;/.gx/memory/memory.jsonl</c>;
    /// edits/tombstones are new lines sharing the original <c>id</c>, and
    /// <see cref="LoadLive"/> folds each id down to its latest non-tombstoned
    /// version.
    /// </summary>
    public class MemoryService
    {
        private readonly KbService _kbService;
        private readonly VectorService _vectorService;

        public MemoryService(KbService kbService, VectorService vectorService = null)
        {
            _kbService = kbService;
            _vectorService = vectorService;
        }

        public string Save(string fact, string objectName, string objectType, string[] tags, string kbPathOverride = null)
        {
            string kbPath = EffectiveKbPath.Resolve(_kbService, kbPathOverride);
            if (string.IsNullOrEmpty(kbPath))
            {
                return Error("NoKbOpen", "No KB is currently open.");
            }
            return SaveCore(kbPath, fact, objectName, objectType, tags);
        }

        public string Recall(string objectName, string objectType, string[] tags, string kbPathOverride = null)
        {
            string kbPath = EffectiveKbPath.Resolve(_kbService, kbPathOverride);
            if (string.IsNullOrEmpty(kbPath))
            {
                return Error("NoKbOpen", "No KB is currently open.");
            }
            return RecallCore(kbPath, objectName, objectType, tags);
        }

        public string List(string kbPathOverride = null)
        {
            string kbPath = EffectiveKbPath.Resolve(_kbService, kbPathOverride);
            if (string.IsNullOrEmpty(kbPath))
            {
                return Error("NoKbOpen", "No KB is currently open.");
            }
            return ListCore(kbPath);
        }

        public string Forget(string id, string kbPathOverride = null)
        {
            string kbPath = EffectiveKbPath.Resolve(_kbService, kbPathOverride);
            if (string.IsNullOrEmpty(kbPath))
            {
                return Error("NoKbOpen", "No KB is currently open.");
            }
            return ForgetCore(kbPath, id);
        }

        /// <summary>
        /// Phase 3 — lifts a chosen friction-log line into a durable memory. Thin
        /// wrapper over <see cref="SaveCore"/> with source="promoted-from-friction"
        /// and the "friction" tag unioned in.
        /// </summary>
        public string Promote(string message, string objectName, string objectType, string[] tags, string kbPathOverride = null)
        {
            string kbPath = EffectiveKbPath.Resolve(_kbService, kbPathOverride);
            if (string.IsNullOrEmpty(kbPath))
            {
                return Error("NoKbOpen", "No KB is currently open.");
            }
            if (string.IsNullOrWhiteSpace(message))
            {
                return Error("MissingMessage", "message is required.");
            }
            tags = (tags ?? Array.Empty<string>())
                .Concat(new[] { "friction" })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return SaveCore(kbPath, message, objectName, objectType, tags, source: "promoted-from-friction");
        }

        /// <summary>
        /// Phase 3 — "dreaming": merges redundant live facts within a scope and,
        /// when <paramref name="dryRun"/> is false, physically compacts
        /// memory.jsonl to one line per surviving record.
        /// </summary>
        public string Consolidate(string objectName, string objectType, string[] tags, bool dryRun, string kbPathOverride = null)
        {
            string kbPath = EffectiveKbPath.Resolve(_kbService, kbPathOverride);
            if (string.IsNullOrEmpty(kbPath))
            {
                return Error("NoKbOpen", "No KB is currently open.");
            }
            return ConsolidateCore(kbPath, objectName, objectType, tags, dryRun);
        }

        // ---- Pure-IO cores (test-friendly) ------------------------------------

        public static string SaveCore(string kbPath, string fact, string objectName, string objectType, string[] tags, string source = "explicit")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fact))
                {
                    return Error("MissingFact", "fact is required.");
                }
                objectName = objectName ?? "";
                objectType = objectType ?? "";
                tags = tags ?? Array.Empty<string>();
                string filePath = MemoryFilePath(kbPath);

                var live = LoadLive(kbPath);
                string normalizedFact = Normalize(fact);
                var existing = live.FirstOrDefault(r =>
                    Normalize(r["fact"]?.ToString()) == normalizedFact &&
                    string.Equals(r["objectName"]?.ToString() ?? "", objectName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r["objectType"]?.ToString() ?? "", objectType, StringComparison.OrdinalIgnoreCase));

                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                string nowIso = DateTime.UtcNow.ToString("o");

                if (existing != null)
                {
                    var mergedTags = new JArray(
                        (existing["tags"] as JArray ?? new JArray())
                            .Select(t => t.ToString())
                            .Concat(tags)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Select(t => (JToken)t));
                    int hits = (existing["hits"]?.Value<int>() ?? 0) + 1;

                    var entry = new JObject
                    {
                        ["id"] = existing["id"]?.ToString(),
                        ["createdUtc"] = existing["createdUtc"]?.ToString(),
                        ["updatedUtc"] = nowIso,
                        ["objectName"] = existing["objectName"]?.ToString() ?? "",
                        ["objectType"] = existing["objectType"]?.ToString() ?? "",
                        ["tags"] = mergedTags,
                        ["fact"] = existing["fact"]?.ToString(),
                        ["source"] = source ?? "explicit",
                        ["hits"] = hits,
                        ["supersedes"] = new JArray(),
                        ["tombstone"] = false
                    };
                    AppendLine(filePath, entry);

                    return McpResponse.Ok(
                        code: "MemoryUpdated",
                        result: new JObject { ["id"] = entry["id"], ["action"] = "bumped", ["hits"] = hits, ["path"] = filePath, ["entry"] = entry });
                }
                else
                {
                    var entry = new JObject
                    {
                        ["id"] = Guid.NewGuid().ToString("N"),
                        ["createdUtc"] = nowIso,
                        ["updatedUtc"] = nowIso,
                        ["objectName"] = objectName,
                        ["objectType"] = objectType,
                        ["tags"] = new JArray(tags.Select(t => (JToken)t)),
                        ["fact"] = fact,
                        ["source"] = source ?? "explicit",
                        ["hits"] = 0,
                        ["supersedes"] = new JArray(),
                        ["tombstone"] = false
                    };
                    AppendLine(filePath, entry);

                    return McpResponse.Ok(
                        code: "MemorySaved",
                        result: new JObject { ["id"] = entry["id"], ["path"] = filePath, ["entry"] = entry });
                }
            }
            catch (Exception ex)
            {
                return Error("SaveFailed", ex.Message);
            }
        }

        public static string RecallCore(string kbPath, string objectName, string objectType, string[] tags)
        {
            try
            {
                tags = tags ?? Array.Empty<string>();
                var live = LoadLive(kbPath);
                int total = live.Count;

                bool noFilters = string.IsNullOrEmpty(objectName) && string.IsNullOrEmpty(objectType) && tags.Length == 0;

                IEnumerable<JObject> matched = noFilters
                    ? live
                    : live.Where(r =>
                        (!string.IsNullOrEmpty(objectName) && string.Equals(r["objectName"]?.ToString() ?? "", objectName, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(objectType) && string.Equals(r["objectType"]?.ToString() ?? "", objectType, StringComparison.OrdinalIgnoreCase)) ||
                        (tags.Length > 0 && (r["tags"] as JArray ?? new JArray())
                            .Select(t => t.ToString())
                            .Any(rt => tags.Any(qt => string.Equals(rt, qt, StringComparison.OrdinalIgnoreCase)))));

                var ranked = matched
                    .OrderByDescending(r => r["hits"]?.Value<int>() ?? 0)
                    .ThenByDescending(r => r["updatedUtc"]?.ToString())
                    .Take(50)
                    .ToList();

                return McpResponse.Ok(
                    code: "MemoryRecalled",
                    result: new JObject
                    {
                        ["path"] = MemoryFilePath(kbPath),
                        ["count"] = ranked.Count,
                        ["total"] = total,
                        ["memories"] = new JArray(ranked)
                    });
            }
            catch (Exception ex)
            {
                return Error("RecallFailed", ex.Message);
            }
        }

        public static string ListCore(string kbPath)
        {
            try
            {
                var live = LoadLive(kbPath)
                    .OrderByDescending(r => r["updatedUtc"]?.ToString())
                    .ToList();

                return McpResponse.Ok(
                    code: "MemoryListed",
                    result: new JObject
                    {
                        ["path"] = MemoryFilePath(kbPath),
                        ["total"] = live.Count,
                        ["memories"] = new JArray(live)
                    });
            }
            catch (Exception ex)
            {
                return Error("ListFailed", ex.Message);
            }
        }

        public static string ForgetCore(string kbPath, string id)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    return Error("MissingId", "id is required.");
                }
                string filePath = MemoryFilePath(kbPath);
                var live = LoadLive(kbPath);
                bool found = live.Any(r => string.Equals(r["id"]?.ToString(), id, StringComparison.OrdinalIgnoreCase));

                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                var tombstone = new JObject
                {
                    ["id"] = id,
                    ["tombstone"] = true,
                    ["updatedUtc"] = DateTime.UtcNow.ToString("o")
                };
                AppendLine(filePath, tombstone);

                var result = new JObject { ["id"] = id, ["path"] = filePath };
                if (!found) result["note"] = "id not found among live memories; tombstone recorded anyway.";
                return McpResponse.Ok(code: "MemoryForgotten", result: result);
            }
            catch (Exception ex)
            {
                return Error("ForgetFailed", ex.Message);
            }
        }

        /// <summary>
        /// Phase 3 "dreaming" — merges redundant live facts within the given scope.
        /// Deterministic core (always runs): groups live records by
        /// (objectName, objectType) case-insensitively, then within each group merges
        /// facts whose normalized text is either equal or a substring of one another.
        /// dryRun=true only reports the proposed merges; dryRun=false rewrites
        /// memory.jsonl to exactly one line per surviving record (crash-safe:
        /// temp-file-then-copy).
        /// </summary>
        // note: AI synthesis (useAi=true) deferred — AiCompleteService lives in the
        // Worker and could in principle be called here, but doing so inside this
        // static, network-free core would trade a deterministic/testable merge for
        // a live HTTP call with no DI seam; the deterministic substring/equality
        // merge is the required deliverable and ships alone for now.
        public static string ConsolidateCore(string kbPath, string objectName, string objectType, string[] tags, bool dryRun)
        {
            try
            {
                tags = tags ?? Array.Empty<string>();
                string filePath = MemoryFilePath(kbPath);
                var live = LoadLive(kbPath);
                int liveBefore = live.Count;

                bool noFilters = string.IsNullOrEmpty(objectName) && string.IsNullOrEmpty(objectType) && tags.Length == 0;
                var scoped = noFilters
                    ? live
                    : live.Where(r =>
                        (!string.IsNullOrEmpty(objectName) && string.Equals(r["objectName"]?.ToString() ?? "", objectName, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(objectType) && string.Equals(r["objectType"]?.ToString() ?? "", objectType, StringComparison.OrdinalIgnoreCase)) ||
                        (tags.Length > 0 && (r["tags"] as JArray ?? new JArray())
                            .Select(t => t.ToString())
                            .Any(rt => tags.Any(qt => string.Equals(rt, qt, StringComparison.OrdinalIgnoreCase)))))
                    .ToList();
                var outOfScope = live.Where(r => !scoped.Contains(r)).ToList();

                var groups = scoped.GroupBy(r => (
                    (r["objectName"]?.ToString() ?? "").ToLowerInvariant(),
                    (r["objectType"]?.ToString() ?? "").ToLowerInvariant()));

                var survivors = new List<JObject>();
                var mergesReport = new JArray();
                int groupCount = 0;

                foreach (var group in groups)
                {
                    groupCount++;
                    var members = group.ToList();
                    // Longest normalized fact first so a superset always gets first shot at absorbing.
                    var ordered = members
                        .Select(r => new { Record = r, Normalized = Normalize(r["fact"]?.ToString()) })
                        .OrderByDescending(x => x.Normalized.Length)
                        .ToList();

                    var absorbed = new HashSet<JObject>();
                    foreach (var candidate in ordered)
                    {
                        if (absorbed.Contains(candidate.Record)) continue;

                        var survivor = candidate.Record;
                        string survivorNorm = candidate.Normalized;
                        var supersededIds = new List<string>();

                        foreach (var other in ordered)
                        {
                            if (other.Record == survivor || absorbed.Contains(other.Record)) continue;
                            if (other.Normalized.Length == 0) continue;

                            bool redundant = other.Normalized == survivorNorm ||
                                IsSupersetRedundant(other.Normalized, survivorNorm);
                            if (!redundant) continue;

                            absorbed.Add(other.Record);
                            supersededIds.Add(other.Record["id"]?.ToString());
                        }

                        if (supersededIds.Count == 0)
                        {
                            survivors.Add(survivor);
                            continue;
                        }

                        var supersededRecords = supersededIds
                            .Select(id => members.First(m => string.Equals(m["id"]?.ToString(), id, StringComparison.OrdinalIgnoreCase)))
                            .ToList();

                        string createdUtc = new[] { survivor }.Concat(supersededRecords)
                            .Select(r => r["createdUtc"]?.ToString())
                            .Where(s => !string.IsNullOrEmpty(s))
                            .OrderBy(s => s, StringComparer.Ordinal)
                            .FirstOrDefault() ?? survivor["createdUtc"]?.ToString();
                        string updatedUtc = new[] { survivor }.Concat(supersededRecords)
                            .Select(r => r["updatedUtc"]?.ToString())
                            .Where(s => !string.IsNullOrEmpty(s))
                            .OrderByDescending(s => s, StringComparer.Ordinal)
                            .FirstOrDefault() ?? survivor["updatedUtc"]?.ToString();
                        int hits = new[] { survivor }.Concat(supersededRecords).Sum(r => r["hits"]?.Value<int>() ?? 0);
                        var mergedTags = new JArray(
                            new[] { survivor }.Concat(supersededRecords)
                                .SelectMany(r => (r["tags"] as JArray ?? new JArray()).Select(t => t.ToString()))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .Select(t => (JToken)t));
                        var mergedSupersedes = new JArray(
                            (survivor["supersedes"] as JArray ?? new JArray())
                                .Select(t => t.ToString())
                                .Concat(supersededIds)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .Select(t => (JToken)t));

                        var merged = new JObject
                        {
                            ["id"] = survivor["id"]?.ToString(),
                            ["createdUtc"] = createdUtc,
                            ["updatedUtc"] = updatedUtc,
                            ["objectName"] = survivor["objectName"]?.ToString() ?? "",
                            ["objectType"] = survivor["objectType"]?.ToString() ?? "",
                            ["tags"] = mergedTags,
                            ["fact"] = survivor["fact"]?.ToString(),
                            ["source"] = survivor["source"]?.ToString() ?? "explicit",
                            ["hits"] = hits,
                            ["supersedes"] = mergedSupersedes,
                            ["tombstone"] = false
                        };
                        survivors.Add(merged);

                        mergesReport.Add(new JObject
                        {
                            ["survivingId"] = merged["id"],
                            ["fact"] = merged["fact"],
                            ["supersededIds"] = new JArray(supersededIds.Select(s => (JToken)s)),
                            ["hits"] = hits
                        });
                    }
                }

                int merged_ = mergesReport.Count;
                int liveAfter = outOfScope.Count + survivors.Count;

                if (dryRun)
                {
                    return McpResponse.Ok(
                        code: "MemoryConsolidationPreview",
                        result: new JObject
                        {
                            ["liveBefore"] = liveBefore,
                            ["groups"] = groupCount,
                            ["merges"] = mergesReport,
                            ["wouldRemove"] = liveBefore - liveAfter,
                            ["note"] = "dryRun — nothing written"
                        });
                }

                var finalRecords = outOfScope.Concat(survivors).ToList();
                WriteCompacted(filePath, finalRecords);

                return McpResponse.Ok(
                    code: "MemoryConsolidated",
                    result: new JObject
                    {
                        ["liveBefore"] = liveBefore,
                        ["liveAfter"] = liveAfter,
                        ["merged"] = merged_,
                        ["path"] = filePath
                    });
            }
            catch (Exception ex)
            {
                return Error("ConsolidateFailed", ex.Message);
            }
        }

        /// <summary>
        /// Crash-safe rewrite: writes every record to a temp file first, then swaps it
        /// onto the real path with an atomic NTFS rename (File.Replace) — a crash at any
        /// point leaves either the old complete file or the new complete file, never a
        /// half-written one. Mirrors the write-temp-then-rename idiom in IndexCacheService.
        /// </summary>
        private static void WriteCompacted(string filePath, List<JObject> records)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            string tmpPath = filePath + ".tmp";
            var sb = new StringBuilder();
            foreach (var r in records)
            {
                sb.Append(r.ToString(Newtonsoft.Json.Formatting.None));
                sb.Append(Environment.NewLine);
            }
            File.WriteAllText(tmpPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.Exists(filePath))
                File.Replace(tmpPath, filePath, null);   // atomic swap on NTFS; consumes tmp
            else
                File.Move(tmpPath, filePath);
        }

        /// <summary>
        /// Folds memory.jsonl down to one live record per id: the record with the
        /// max updatedUtc, dropped entirely when that latest version is a tombstone.
        /// </summary>
        public static List<JObject> LoadLive(string kbPath)
        {
            string filePath = MemoryFilePath(kbPath);
            var result = new List<JObject>();
            if (!File.Exists(filePath)) return result;

            var latestById = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JObject entry;
                try { entry = JObject.Parse(line); }
                catch { continue; }

                string id = entry["id"]?.ToString();
                if (string.IsNullOrEmpty(id)) continue;

                if (!latestById.TryGetValue(id, out var current) ||
                    string.CompareOrdinal(entry["updatedUtc"]?.ToString(), current["updatedUtc"]?.ToString()) >= 0)
                {
                    latestById[id] = entry;
                }
            }

            foreach (var entry in latestById.Values)
            {
                if (entry["tombstone"]?.Value<bool>() == true) continue;
                result.Add(entry);
            }
            return result;
        }

        // ---- Phase 2 — object-scoped auto-surfacing (piggyback on inspect/read) --

        // Session dedup: once a memory id has been surfaced through the
        // inspect/read piggyback, don't attach it again for the life of this
        // worker process. Worker is one-per-KB and lives the whole session, so
        // process-lifetime == session; a worker reload resets the set (acceptable).
        private static readonly HashSet<string> _surfacedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _surfacedLock = new object();

        /// <summary>
        /// Loads live memories matching <paramref name="objectName"/> or
        /// <paramref name="objectType"/>, ranks by hits desc then updatedUtc desc,
        /// takes up to <paramref name="max"/>, and drops any id already surfaced
        /// this session. Never throws — returns an empty list on any error.
        /// </summary>
        public static List<JObject> TakeFreshRelevant(string kbPath, string objectName, string objectType, int max = 5)
        {
            var result = new List<JObject>();
            try
            {
                if (string.IsNullOrEmpty(kbPath)) return result;
                objectName = objectName ?? "";
                objectType = objectType ?? "";
                if (objectName.Length == 0 && objectType.Length == 0) return result;

                var matched = LoadLive(kbPath)
                    .Where(r =>
                        (objectName.Length > 0 && string.Equals(r["objectName"]?.ToString() ?? "", objectName, StringComparison.OrdinalIgnoreCase)) ||
                        (objectType.Length > 0 && string.Equals(r["objectType"]?.ToString() ?? "", objectType, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(r => r["hits"]?.Value<int>() ?? 0)
                    .ThenByDescending(r => r["updatedUtc"]?.ToString());

                lock (_surfacedLock)
                {
                    foreach (var r in matched)
                    {
                        if (result.Count >= max) break;
                        string id = r["id"]?.ToString();
                        if (string.IsNullOrEmpty(id) || !_surfacedIds.Add(id)) continue;

                        result.Add(new JObject
                        {
                            ["id"] = id,
                            ["fact"] = r["fact"],
                            ["tags"] = r["tags"],
                            ["hits"] = r["hits"],
                            ["updatedUtc"] = r["updatedUtc"]
                        });
                    }
                }
            }
            catch { /* memory surfacing must never break the caller */ }
            return result;
        }

        /// <summary>
        /// Parses <paramref name="json"/>, attaches a <c>relevantMemory</c> block when
        /// <see cref="TakeFreshRelevant"/> finds fresh matches, and re-serializes.
        /// Returns the original string unchanged on parse failure, on an error envelope,
        /// or when nothing matches — callers must not cache the result of this call, only
        /// the pre-attach json, so memory surfacing stays always-fresh across cache hits.
        /// </summary>
        public static string AttachRelevantMemory(string kbPath, string json, string objectName, string objectType, int max = 5)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json) || string.IsNullOrEmpty(kbPath)) return json;
                var parsed = JObject.Parse(json);
                if (parsed["error"] != null) return json;
                if (string.Equals(parsed["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase)) return json;

                var memories = TakeFreshRelevant(kbPath, objectName, objectType, max);
                if (memories.Count == 0) return json;

                parsed["relevantMemory"] = new JObject
                {
                    ["count"] = memories.Count,
                    ["note"] = $"Per-KB memories relevant to {objectName}. Saved via genexus_memory.",
                    ["memories"] = new JArray(memories.Select(m => (JToken)m))
                };
                return parsed.ToString();
            }
            catch
            {
                return json;
            }
        }

        private static string Normalize(string fact)
        {
            if (string.IsNullOrEmpty(fact)) return "";
            string collapsed = Regex.Replace(fact.Trim(), @"\s+", " ");
            return collapsed.ToLowerInvariant();
        }

        // Minimum normalized length before a fact may be absorbed by a longer one
        // during consolidation. Guards against a short/generic fragment ("on", "id")
        // being silently destroyed just because its text happens to appear inside a
        // longer, semantically-unrelated fact.
        private const int MinSupersetMergeLength = 15;

        // True when 'shorter' is redundant with the longer 'survivor' — i.e. the
        // survivor contains the shorter fact as a whole-word phrase. Requires a
        // minimum length and word boundaries on both sides so mid-word or generic
        // substring coincidences never trigger an (irreversible) merge.
        private static bool IsSupersetRedundant(string shorter, string survivor)
        {
            if (string.IsNullOrEmpty(shorter) || string.IsNullOrEmpty(survivor)) return false;
            if (shorter.Length >= survivor.Length) return false;
            if (shorter.Length < MinSupersetMergeLength) return false;

            int idx = survivor.IndexOf(shorter, StringComparison.Ordinal);
            if (idx < 0) return false;

            bool leftBoundary = idx == 0 || survivor[idx - 1] == ' ';
            int end = idx + shorter.Length;
            bool rightBoundary = end == survivor.Length || survivor[end] == ' ';
            return leftBoundary && rightBoundary;
        }

        private static void AppendLine(string filePath, JObject entry)
        {
            string line = entry.ToString(Newtonsoft.Json.Formatting.None);
            File.AppendAllText(filePath, line + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private static string MemoryFilePath(string kbPath) =>
            Path.Combine(kbPath, ".gx", "memory", "memory.jsonl");

        private static string Error(string code, string message) =>
            McpResponse.Err(code: code, message: message);
    }
}
