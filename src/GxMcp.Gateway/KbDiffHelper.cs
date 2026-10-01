using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Gateway-side comparison of two KB directories at the object-index level.
    ///
    /// <para>
    /// This reads the on-disk <c>Objects/&lt;Type&gt;/&lt;Name&gt;/</c> tree, so it never
    /// contacts the SDK and needs neither KB to be open in a Worker. Two properties
    /// of that design are part of the published contract rather than implementation
    /// detail, and both are reported on every call:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item><description>
    /// <b>Authority.</b> The inventory is the filesystem tree, so the identity it
    /// produces is the directory naming GeneXus happens to use on disk - not a
    /// module-qualified, GUID-backed object identity. <c>identityAuthority</c> says
    /// so, and <c>moduleQualified</c> is false.
    /// </description></item>
    /// <item><description>
    /// <b>Completeness.</b> A directory this tool cannot enumerate is
    /// <c>unavailable</c>, and a tree that enumerates but contains objects it cannot
    /// read is <c>incomplete</c>. Neither is ever reported as a successfully compared
    /// empty model: an empty <c>Objects/</c> is <c>empty</c>, and that is the only
    /// shape that means "nothing to compare".
    /// </description></item>
    /// </list>
    ///
    /// <para>
    /// Modification detection compares <b>content</b> (SHA-256 over each part file,
    /// combined per object), not filesystem metadata. Equal timestamps and equal part
    /// counts with different bytes are a change; equal bytes with different
    /// timestamps are not, and are reported under <c>metadataOnly</c> so a caller
    /// can still see that the files were touched. <c>mode=metadata</c> restores the
    /// old mtime/PartCount heuristic, and is labelled as such in the response.
    /// </para>
    /// </summary>
    internal static class KbDiffHelper
    {
        /// <summary>Upper bound on objects enumerated per side, so a pathologically large tree reports honestly instead of running unbounded.</summary>
        internal const int DefaultMaxObjects = 50000;

        /// <summary>Content comparison: SHA-256 per part file, combined per object. The default.</summary>
        public const string ContentMode = "content";

        /// <summary>Legacy mtime/PartCount heuristic. Cheap, and explicitly not a content comparison.</summary>
        public const string MetadataMode = "metadata";

        public static JObject Diff(string pathA, string pathB) => Diff(pathA, pathB, ContentMode, DefaultMaxObjects);

        public static JObject Diff(string pathA, string pathB, string mode, int maxObjects)
        {
            bool contentMode = !string.Equals(mode, MetadataMode, StringComparison.OrdinalIgnoreCase);
            int limit = maxObjects > 0 ? maxObjects : DefaultMaxObjects;

            var scanA = EnumerateObjects(pathA, limit);
            var scanB = EnumerateObjects(pathB, limit);
            var objsA = scanA.Objects;
            var objsB = scanB.Objects;

            var keysA = new HashSet<string>(objsA.Keys, StringComparer.OrdinalIgnoreCase);
            var keysB = new HashSet<string>(objsB.Keys, StringComparer.OrdinalIgnoreCase);

            var onlyInA = keysA.Except(keysB, StringComparer.OrdinalIgnoreCase).ToList();
            var onlyInB = keysB.Except(keysA, StringComparer.OrdinalIgnoreCase).ToList();
            var modified = new List<JObject>();
            var metadataOnly = new List<JObject>();

            foreach (var key in keysA.Intersect(keysB, StringComparer.OrdinalIgnoreCase))
            {
                var a = objsA[key];
                var b = objsB[key];

                bool differs = contentMode
                    ? !string.Equals(a.ContentHash, b.ContentHash, StringComparison.Ordinal)
                    : Math.Abs((a.LastWriteUtc - b.LastWriteUtc).TotalSeconds) > 1.0 || a.PartCount != b.PartCount;

                if (!differs)
                {
                    // Equal content is never a content change. Surface a pure touch so
                    // the caller can tell "identical" from "never compared".
                    if (contentMode && Math.Abs((a.LastWriteUtc - b.LastWriteUtc).TotalSeconds) > 1.0)
                        metadataOnly.Add(Describe(a, b, key, "equalContentDifferentTimestamp"));
                    continue;
                }

                var entry = Describe(a, b, key, contentMode ? "contentDiffers" : "metadataDiffers");
                if (contentMode && a.Parts != null && b.Parts != null)
                    entry["changedParts"] = JArray.FromObject(ChangedParts(a, b));
                modified.Add(entry);
            }

            bool complete = scanA.Complete && scanB.Complete;
            var notes = new JArray();
            if (contentMode)
                notes.Add("Content comparison: SHA-256 over each part file, combined per object. Equal bytes are equal content regardless of timestamps; line endings are NOT normalized.");
            else
                notes.Add("mode=metadata compares filesystem timestamps and part counts only. It is NOT a content comparison and can miss same-metadata content changes.");
            notes.Add("identityAuthority=filesystem-directory-names: objects are identified by Objects/<Type>/<Name> directory naming, not a module-qualified or GUID-backed SDK identity.");

            return new JObject
            {
                // Additive: callers that only branch on the two legacy "Success"/
                // error cases keep working, while "Incomplete" can no longer be
                // confused with a clean comparison of two empty models.
                ["status"] = complete ? "Success" : "Incomplete",
                ["complete"] = complete,
                ["contentComparison"] = contentMode ? "sha256-raw-part-bytes" : "filesystem-metadata-heuristic",
                ["identityAuthority"] = "filesystem-directory-names",
                ["moduleQualified"] = false,
                ["kbA"] = pathA,
                ["kbB"] = pathB,
                ["countA"] = keysA.Count,
                ["countB"] = keysB.Count,
                ["inventoryA"] = scanA.ToJson(limit),
                ["inventoryB"] = scanB.ToJson(limit),
                ["onlyInA"] = JArray.FromObject(onlyInA.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)),
                ["onlyInB"] = JArray.FromObject(onlyInB.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)),
                ["modified"] = new JArray(modified.OrderBy(o => o["key"]?.ToString(), StringComparer.OrdinalIgnoreCase)),
                ["metadataOnly"] = new JArray(metadataOnly.OrderBy(o => o["key"]?.ToString(), StringComparer.OrdinalIgnoreCase)),
                ["notes"] = notes
            };
        }

        private static JObject Describe(ObjEntry a, ObjEntry b, string key, string reason)
        {
            return new JObject
            {
                ["key"] = key,
                ["name"] = a.Name,
                ["type"] = a.Type,
                ["reason"] = reason,
                ["lastUpdateA"] = a.LastWriteUtc == DateTime.MinValue ? null : a.LastWriteUtc.ToString("o"),
                ["lastUpdateB"] = b.LastWriteUtc == DateTime.MinValue ? null : b.LastWriteUtc.ToString("o"),
                ["partsA"] = a.PartCount,
                ["partsB"] = b.PartCount
            };
        }

        private static JArray ChangedParts(ObjEntry a, ObjEntry b)
        {
            var names = new SortedSet<string>(a.Parts.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var name in b.Parts.Keys) names.Add(name);

            var changed = new JArray();
            foreach (var name in names)
            {
                a.Parts.TryGetValue(name, out var left);
                b.Parts.TryGetValue(name, out var right);
                if (!string.Equals(left, right, StringComparison.Ordinal))
                    changed.Add(new JObject { ["part"] = name, ["inA"] = left != null, ["inB"] = right != null });
            }
            return changed;
        }

        /// <summary>One object and the per-part content hashes that identify it. The dictionary key is the part file name.</summary>
        internal sealed record ObjEntry(
            string Name,
            string Type,
            DateTime LastWriteUtc,
            int PartCount,
            Dictionary<string, string> Parts,
            string ContentHash);

        /// <summary>
        /// An enumeration attempt plus what it is allowed to claim. <c>Complete</c> is
        /// false for every state except a fully readable tree, so callers cannot treat
        /// an unsupported or partially readable layout as an empty model.
        /// </summary>
        internal sealed class InventoryScan
        {
            public Dictionary<string, ObjEntry> Objects { get; } = new Dictionary<string, ObjEntry>(StringComparer.OrdinalIgnoreCase);
            public string State { get; set; } = "complete";
            public string Reason { get; set; }
            public JArray UnreadableObjects { get; } = new JArray();
            public bool Truncated { get; set; }

            public bool Complete => State == "complete" || State == "empty";

            internal JObject ToJson(int limit) => new JObject
            {
                ["state"] = State,
                ["reason"] = Reason,
                ["objectCount"] = Objects.Count,
                ["objectLimit"] = limit,
                ["truncated"] = Truncated,
                ["unreadableObjects"] = UnreadableObjects
            };
        }

        /// <summary>
        /// Walks <c>&lt;kbRoot&gt;/Objects/&lt;Type&gt;/&lt;Name&gt;/</c> and hashes every part
        /// file. Never throws: an absent directory is <c>unavailable</c>, an
        /// unreadable object is <c>incomplete</c>, and exceeding the object limit is
        /// <c>incomplete</c> with <c>truncated</c> set.
        /// </summary>
        internal static InventoryScan EnumerateObjects(string kbPath, int maxObjects)
        {
            var scan = new InventoryScan();
            string objectsDir;
            try { objectsDir = Path.Combine(kbPath, "Objects"); }
            catch (Exception ex)
            {
                scan.State = "unavailable";
                scan.Reason = "The KB root does not resolve to a usable path: " + ex.Message;
                return scan;
            }

            DirectoryInfo objectsRoot;
            try
            {
                if (!Directory.Exists(objectsDir))
                {
                    scan.State = "unavailable";
                    scan.Reason = "No Objects/ directory exists at this KB root. This is not a supported object inventory, so it cannot be compared against an empty model.";
                    return scan;
                }
                objectsRoot = new DirectoryInfo(objectsDir);
            }
            catch (Exception ex)
            {
                scan.State = "unavailable";
                scan.Reason = "Objects/ could not be inspected: " + ex.Message;
                return scan;
            }

            DirectoryInfo[] typeDirs;
            try
            {
                typeDirs = objectsRoot.GetDirectories();
                _ = objectsRoot.GetFiles();
            }
            catch (Exception ex)
            {
                scan.State = "incomplete";
                scan.Reason = "Objects/ could not be enumerated: " + ex.Message;
                return scan;
            }

            if (typeDirs.Length == 0)
            {
                scan.State = "empty";
                scan.Reason = "Objects/ exists and contains no object type directories: a proven empty inventory.";
                return scan;
            }

            int limit = maxObjects > 0 ? maxObjects : DefaultMaxObjects;
            foreach (var typeDir in typeDirs)
            {
                string type = typeDir.Name;
                DirectoryInfo[] objDirs;
                try { objDirs = typeDir.GetDirectories(); }
                catch (Exception ex)
                {
                    scan.State = "incomplete";
                    scan.Reason = $"Type directory '{type}' could not be enumerated: {ex.Message}";
                    return scan;
                }

                foreach (var objDir in objDirs)
                {
                    if (scan.Objects.Count >= limit)
                    {
                        scan.Truncated = true;
                        scan.State = "incomplete";
                        scan.Reason = $"The inventory exceeds the {limit}-object comparison limit; results are partial.";
                        return scan;
                    }

                    string name = objDir.Name;
                    var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    DateTime maxMtime = DateTime.MinValue;
                    bool partFailed = false;
                    string partReason = null;

                    FileInfo[] files;
                    try { files = objDir.GetFiles("*", SearchOption.TopDirectoryOnly); }
                    catch (Exception ex)
                    {
                        partFailed = true;
                        partReason = ex.Message;
                        files = Array.Empty<FileInfo>();
                    }

                    foreach (var file in files)
                    {
                        try
                        {
                            parts[file.Name] = HashFile(file);
                            if (file.LastWriteTimeUtc > maxMtime) maxMtime = file.LastWriteTimeUtc;
                        }
                        catch (Exception ex)
                        {
                            partFailed = true;
                            partReason = ex.Message;
                        }
                    }

                    if (parts.Count == 0)
                    {
                        // An object directory with nothing readable in it is not an
                        // object we compared. Reporting it as absent would invent a
                        // difference, and reporting it as equal would invent a match.
                        if (scan.State == "complete") scan.State = "incomplete";
                        scan.UnreadableObjects.Add(new JObject
                        {
                            ["key"] = $"{type}:{name}",
                            ["reason"] = partReason ?? "The object directory contains no readable part files."
                        });
                        continue;
                    }

                    if (partFailed)
                    {
                        if (scan.State == "complete") scan.State = "incomplete";
                        scan.UnreadableObjects.Add(new JObject
                        {
                            ["key"] = $"{type}:{name}",
                            ["reason"] = "Some part files could not be read: " + partReason
                        });
                    }

                    scan.Objects[$"{type}:{name}"] = new ObjEntry(
                        name, type, maxMtime, parts.Count, parts, HashObject(parts));
                }
            }

            if (scan.Objects.Count == 0 && scan.State == "complete")
            {
                scan.State = "empty";
                scan.Reason = "Objects/ enumerates no readable objects: a proven empty inventory.";
            }

            return scan;
        }

        /// <summary>
        /// Order-independent per-object content hash: every part name is folded into
        /// the digest with its own hash, so a renamed part is a change even when the
        /// byte total is identical.
        /// </summary>
        private static string HashObject(Dictionary<string, string> parts)
        {
            using var sha = SHA256.Create();
            foreach (var part in parts.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                var nameBytes = System.Text.Encoding.UTF8.GetBytes(part.Key.ToUpperInvariant());
                sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
                var hashBytes = System.Text.Encoding.ASCII.GetBytes(part.Value);
                sha.TransformBlock(hashBytes, 0, hashBytes.Length, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return ToHex(sha.Hash);
        }

        private static string HashFile(FileInfo file)
        {
            using var sha = SHA256.Create();
            using var stream = file.OpenRead();
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                sha.TransformBlock(buffer, 0, read, null, 0);
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return ToHex(sha.Hash);
        }

        private static string ToHex(byte[] bytes)
        {
            var chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = HexDigit(bytes[i] >> 4);
                chars[i * 2 + 1] = HexDigit(bytes[i] & 0xF);
            }
            return new string(chars);
        }

        private static char HexDigit(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'a' + (nibble - 10));
    }
}
