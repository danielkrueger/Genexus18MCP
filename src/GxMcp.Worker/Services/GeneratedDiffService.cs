using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Utils;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 12 — genexus_diff_generated.
    ///
    /// Unified diffs of GENERATED files (&lt;obj&gt;.cs/.aspx/.js/.html) vs a baseline:
    ///   - last-build: snapshot under .gx/build-baselines/&lt;obj&gt;/&lt;UTC&gt;/&lt;file&gt;.txt
    ///   - git-head:   shell out to `git show HEAD:&lt;path&gt;`; surfaces KbNotInGit when
    ///                 the KB is not a git repo.
    /// </summary>
    public interface IGitShell
    {
        bool IsGitRepo(string workingDir);
        bool TryShowHead(string workingDir, string repoRelativePath, out string content, out string error);
    }

    internal class GitShell : IGitShell
    {
        public bool IsGitRepo(string workingDir)
        {
            if (string.IsNullOrEmpty(workingDir) || !Directory.Exists(workingDir)) return false;
            // Walk up looking for a .git directory or file (worktrees).
            var dir = new DirectoryInfo(workingDir);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                    || File.Exists(Path.Combine(dir.FullName, ".git"))) return true;
                dir = dir.Parent;
            }
            return false;
        }

        public bool TryShowHead(string workingDir, string repoRelativePath, out string content, out string error)
        {
            content = null;
            error = null;
            try
            {
                // net48 has no ProcessStartInfo.ArgumentList — quote via the
                // shared CommandLineToArgv-compatible helper (handles trailing
                // backslashes correctly, unlike a naive Replace("\"","\\\"")).
                // Also prepend `--` so a path beginning with '-' can't be
                // re-interpreted as a flag, and refuse paths that escape the
                // working tree.
                string norm = (repoRelativePath ?? string.Empty).Replace("\\", "/");
                string quoted = Argv.Quote("HEAD:" + norm);
                var psi = new ProcessStartInfo("git", "show " + quoted)
                {
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    if (p.ExitCode == 0)
                    {
                        content = stdout;
                        return true;
                    }
                    error = string.IsNullOrEmpty(stderr) ? "git exit " + p.ExitCode : stderr.Trim();
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    public class GeneratedDiffService
    {
        private readonly KbService _kbService;
        private readonly IGitShell _git;

        // Generated-file extensions we diff for an object.
        private static readonly string[] GeneratedExtensions = { ".cs", ".aspx", ".js", ".html" };

        public GeneratedDiffService(KbService kbService, IGitShell git = null)
        {
            _kbService = kbService;
            _git = git ?? new GitShell();
        }

        public string Diff(string target, string against)
        {
            if (string.IsNullOrEmpty(target))
                return Models.McpResponse.Err(code: "MissingTarget", message: "target object name is required.", hint: "Pass the name of the KB object to diff.", nextSteps: new Newtonsoft.Json.Linq.JArray(Models.McpResponse.NextStep("genexus_list_objects", null, "List available objects to find the correct name.")), target: target);

            string baseline = (against ?? "last-build").Trim().ToLowerInvariant();
            if (baseline != "last-build" && baseline != "git-head")
                return Models.McpResponse.Err(code: "InvalidAgainst", message: "against must be 'last-build' or 'git-head'.", hint: "Pass against='last-build' or against='git-head'.", nextSteps: new Newtonsoft.Json.Linq.JArray(Models.McpResponse.NextStep("genexus_diff_generated", new JObject { ["name"] = target, ["against"] = "last-build" }, "Retry with a valid against value.")), target: target);

            string kbPath = null;
            try { kbPath = _kbService?.GetKbPath(); } catch { }
            if (string.IsNullOrEmpty(kbPath) || !Directory.Exists(kbPath))
            {
                return Models.McpResponse.Err(code: "KbPathUnknown", message: "No KB is currently open or KB path does not exist.", hint: "Open a KB first via genexus_kb action=open.", nextSteps: new Newtonsoft.Json.Linq.JArray(KbOpenNextStep.Step("Open a KB before calling diff.")), target: target);
            }

            // Locate current generated files for the object.
            var currentFiles = FindGeneratedFiles(kbPath, target);
            if (currentFiles.Count == 0)
            {
                return Models.McpResponse.Ok(target: target, code: "NoGeneratedFiles", result: new JObject
                {
                    ["against"] = baseline,
                    ["files"] = new JArray(),
                    ["totalChangedLines"] = 0,
                    ["note"] = "No generated files found for the object. Build the KB first."
                });
            }

            JArray files = new JArray();
            int totalChangedLines = 0;

            if (baseline == "git-head")
            {
                if (!_git.IsGitRepo(kbPath))
                {
                    return Models.McpResponse.Err(code: "KbNotInGit", message: "KB path is not inside a git working tree.", hint: "Initialize a git repository in the KB directory before using against='git-head'.", nextSteps: new Newtonsoft.Json.Linq.JArray(Models.McpResponse.NextStep("genexus_diff_generated", new JObject { ["name"] = target, ["against"] = "last-build" }, "Use against='last-build' instead of git-head.")), target: target);
                }
                foreach (var file in currentFiles)
                {
                    string rel = MakeRelative(kbPath, file);
                    if (!_git.TryShowHead(kbPath, rel, out string baselineContent, out string err))
                    {
                        // missing-from-HEAD → treat baseline as empty.
                        baselineContent = string.Empty;
                    }
                    string current = SafeRead(file);
                    AddDiffEntry(files, Path.GetFileName(file), baselineContent ?? string.Empty, current, ref totalChangedLines);
                }
            }
            else // last-build
            {
                string baselineDir = ResolveLatestBaselineDir(kbPath, target);
                foreach (var file in currentFiles)
                {
                    string fname = Path.GetFileName(file);
                    string baselineFile = baselineDir == null ? null : Path.Combine(baselineDir, fname + ".txt");
                    string baselineContent = (baselineFile != null && File.Exists(baselineFile))
                        ? SafeRead(baselineFile)
                        : string.Empty;
                    string current = SafeRead(file);
                    AddDiffEntry(files, fname, baselineContent, current, ref totalChangedLines);
                }
            }

            return Models.McpResponse.Ok(target: target, code: "DiffOk", result: new JObject
            {
                ["against"] = baseline,
                ["files"] = files,
                ["totalChangedLines"] = totalChangedLines
            });
        }

        /// <summary>
        /// Capture a baseline snapshot of all generated files for the object under
        /// .gx/build-baselines/&lt;obj&gt;/&lt;UTC&gt;/. Called by the build pipeline; safe to
        /// call from tests or manually.
        /// </summary>
        public string CaptureBaseline(string kbPath, string target)
        {
            if (string.IsNullOrEmpty(kbPath) || !Directory.Exists(kbPath)) return null;
            if (string.IsNullOrEmpty(target)) return null;

            var files = FindGeneratedFiles(kbPath, target);
            if (files.Count == 0) return null;

            string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
            string dir = Path.Combine(kbPath, ".gx", "build-baselines", SanitizeName(target), stamp);
            Directory.CreateDirectory(dir);
            foreach (var f in files)
            {
                try { File.Copy(f, Path.Combine(dir, Path.GetFileName(f) + ".txt"), overwrite: true); } catch { }
            }
            return dir;
        }

        /// <summary>
        /// Ordered list of candidate output roots to probe for an object's generated
        /// files. Combines the well-known GeneXus 18 folders with any environment
        /// output directory discovered under the KB root (e.g. the NetCore generator
        /// emits to &lt;Env&gt;\web, such as "NETCoreMySQL\web", which the fixed list
        /// below does not cover).
        /// </summary>
        internal static List<string> BuildCandidateRoots(string kbPath)
        {
            var candidates = new List<string>
            {
                Path.Combine(kbPath, "CSharpModel", "Web"),
                Path.Combine(kbPath, "CSharpModel"),
                Path.Combine(kbPath, "DotNetClassWeb"),
                Path.Combine(kbPath, "JavaModel", "Web"),
            };
            // Discover per-environment web output dirs one level under the KB root
            // (<KB>\<Env>\web). Cheap: a single directory listing, no recursion.
            bool anyEnvWeb = false;
            foreach (var envWeb in DiscoverEnvironmentWebDirs(kbPath))
            {
                anyEnvWeb = true;
                if (!candidates.Contains(envWeb, StringComparer.OrdinalIgnoreCase))
                    candidates.Add(envWeb);
            }
            // Last-resort full-tree scan is expensive on large KBs and can match an
            // unrelated same-named .cs. Only fall back to it when no environment web
            // output dir was discovered — if one exists, the generated .cs lives there.
            bool anyWellKnown = candidates.Take(4).Any(Directory.Exists);
            if (!anyEnvWeb && !anyWellKnown)
                candidates.Add(kbPath);
            return candidates;
        }

        // Directory/file names we never descend into or match when scanning for
        // generated files: VCS metadata, the MCP's own .gx cache, and GeneXus
        // conversion/backup trees. Skipping them keeps the (rare) full-tree fallback
        // bounded and avoids matching stale/backed-up copies of a .cs.
        private static readonly HashSet<string> ScanNoiseDirs = new HashSet<string>(
            new[] { ".git", ".gx", ".svn", ".hg", "GXcvt", "node_modules", "bin", "obj", ".vs" },
            StringComparer.OrdinalIgnoreCase);

        // Manual pruned recursive file walk (Directory.EnumerateFiles offers no
        // dir-skip hook). Yields files matching <namePattern> under <root>, never
        // descending into ScanNoiseDirs. Swallows per-directory access errors.
        private static IEnumerable<string> EnumerateFilesPruned(string root, string namePattern)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] files = null;
                try { files = Directory.GetFiles(dir, namePattern); } catch { }
                if (files != null)
                    foreach (var f in files) yield return f;
                string[] subs = null;
                try { subs = Directory.GetDirectories(dir); } catch { }
                if (subs == null) continue;
                foreach (var s in subs)
                {
                    string name = Path.GetFileName(s);
                    if (!string.IsNullOrEmpty(name) && !ScanNoiseDirs.Contains(name))
                        stack.Push(s);
                }
            }
        }

        /// <summary>
        /// Immediate &lt;KB&gt;\&lt;Env&gt;\web directories (one directory-listing deep, no
        /// recursion). These are the generator output roots the NetCore/.NET
        /// generators write to. Skips the .gx metadata dir.
        /// </summary>
        internal static IEnumerable<string> DiscoverEnvironmentWebDirs(string kbPath)
        {
            if (string.IsNullOrEmpty(kbPath) || !Directory.Exists(kbPath)) yield break;
            string[] subdirs;
            try { subdirs = Directory.GetDirectories(kbPath); }
            catch { yield break; }
            foreach (var sub in subdirs)
            {
                string name = Path.GetFileName(sub);
                if (string.IsNullOrEmpty(name) || name.StartsWith(".")) continue;
                string web = Path.Combine(sub, "web");
                if (Directory.Exists(web)) yield return web;
            }
        }

        internal static List<string> FindGeneratedFiles(string kbPath, string target)
        {
            return FindGeneratedFiles(kbPath, target, allRoots: false);
        }

        /// <summary>
        /// Locate an object's generated files. With <paramref name="allRoots"/> false
        /// the first candidate root with a match wins (diff use — one file set). With
        /// true, matches are collected across every candidate root (freshness-gate use
        /// — the build-evidence check needs the freshest file wherever it landed).
        /// </summary>
        internal static List<string> FindGeneratedFiles(string kbPath, string target, bool allRoots)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(kbPath) || string.IsNullOrEmpty(target)) return found;
            foreach (var c in BuildCandidateRoots(kbPath))
            {
                if (!Directory.Exists(c)) continue;
                try
                {
                    foreach (var match in EnumerateFilesPruned(c, target + ".*"))
                    {
                        if (IsGeneratedFileFor(match, target)
                            && !found.Contains(match, StringComparer.OrdinalIgnoreCase))
                        {
                            found.Add(match);
                        }
                    }
                    // issue #409: the generator prefixes a Main object's .cs with "a", so
                    // "rcliser2sql" is only ever on disk as "arcliser2sql.cs". The glob
                    // target + ".*" cannot see that, so the freshness probe reported the file
                    // missing and every Main report build raised a false [generate-gap]
                    // warning. Probe the prefixed spelling too.
                    foreach (var prefixed in EnumerateFilesPruned(c, MainFilePrefix + target + ".*"))
                    {
                        if (IsGeneratedFileFor(prefixed, target)
                            && !found.Contains(prefixed, StringComparer.OrdinalIgnoreCase))
                        {
                            found.Add(prefixed);
                        }
                    }
                }
                catch { }
                if (!allRoots && found.Count > 0) break; // first matching root wins
            }
            return found;
        }

        /// <summary>
        /// The prefix the GeneXus .NET generator puts in front of a Main object's source
        /// file: a procedure named <c>rcliser2sql</c> generates <c>arcliser2sql.cs</c>.
        /// </summary>
        internal const string MainFilePrefix = "a";

        /// <summary>
        /// Whether <paramref name="file"/> is the generated file for <paramref name="target"/>,
        /// accepting either the bare spelling or the generator's Main-object prefix. The name
        /// is matched exactly and the extension must be one we diff, so a similarly-named
        /// sibling object - or the same stem with some other extension - is never mistaken
        /// for it.
        /// </summary>
        internal static bool IsGeneratedFileFor(string file, string target)
        {
            if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(target)) return false;
            string name = Path.GetFileName(file);
            string ext = Path.GetExtension(name);
            if (!GeneratedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return false;
            return string.Equals(name, target + ext, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, MainFilePrefix + target + ext, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Result of a generated-file freshness probe for one object.</summary>
        public class GeneratedFileEvidence
        {
            public string Target;
            public bool Found;                 // any generated file exists for the target
            public bool Fresh;                 // at least one file written at/after sinceUtc
            public string FreshestPath;        // KB-relative path of the freshest match
            public DateTime? FreshestWriteUtc;
            public int FileCount;
        }

        /// <summary>
        /// Every generated file of <paramref name="target"/>, restricted to the active environment's
        /// web root once it is known. A KB can retain generated files for several environments, and a
        /// newer file from a different environment must never count as evidence for this build.
        /// </summary>
        private static List<string> FindGeneratedFilesInEnvironment(string kbPath, string target, string preferredWebPath)
        {
            var files = FindGeneratedFiles(kbPath, target, allRoots: true);
            if (string.IsNullOrWhiteSpace(preferredWebPath)) return files;
            string preferred = Path.GetFullPath(preferredWebPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return files.Where(f =>
            {
                try
                {
                    string full = Path.GetFullPath(f);
                    return full.StartsWith(preferred, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }).ToList();
        }

        /// <summary>
        /// Every file the generator can emit for <paramref name="target"/>: the matching
        /// .cs/.aspx/.js/.html (including the Main-object "a" variant) plus the compiled
        /// bin\NAME.dll and bin\aNAME.dll next to them. A Main procedure with
        /// Call protocol HTTP is a stub (NAME.cs) plus the real program (aNAME.cs and
        /// bin\aNAME.dll), so reporting only the freshest file hid the program itself.
        /// Each entry carries its last write time and whether it was written during the build
        /// (last write at or after <paramref name="buildStartUtc"/>).
        /// </summary>
        internal static JArray ListGeneratedFiles(string kbPath, string target, DateTime buildStartUtc, string preferredWebPath = null)
        {
            var result = new JArray();
            var files = FindGeneratedFilesInEnvironment(kbPath, target, preferredWebPath);
            foreach (var dir in files.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                foreach (var name in new[] { target, MainFilePrefix + target })
                {
                    string dll = Path.Combine(dir, "bin", name + ".dll");
                    if (File.Exists(dll) && !files.Contains(dll, StringComparer.OrdinalIgnoreCase)) files.Add(dll);
                }
            }
            foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                DateTime w;
                try { w = File.GetLastWriteTimeUtc(f); }
                catch { continue; }
                result.Add(new JObject
                {
                    ["path"] = MakeRelative(kbPath, f),
                    ["lastWriteUtc"] = w.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ["writtenDuringBuild"] = w >= buildStartUtc
                });
            }
            return result;
        }

        /// <summary>
        /// Probe whether an object's generated files exist and were (re)written at or
        /// after <paramref name="sinceUtc"/> (the build start). Used by the build
        /// evidence gate: a build can report Succeeded while the .cs on disk is stale
        /// or missing. Scans every candidate root and reports the freshest match.
        ///
        /// When <paramref name="priorMtimeUtc"/> is supplied (a pre-build snapshot of
        /// the freshest .cs mtime for this object), freshness is decided by "did the
        /// file's mtime move past the snapshot?" — immune to wall-clock / filesystem
        /// timestamp-granularity skew, unlike the absolute <paramref name="sinceUtc"/>
        /// comparison used when no snapshot is available.
        /// </summary>
        internal static GeneratedFileEvidence ProbeGeneratedFreshness(string kbPath, string target, DateTime sinceUtc, DateTime? priorMtimeUtc = null, string preferredWebPath = null)
        {
            var ev = new GeneratedFileEvidence { Target = target };
            var files = FindGeneratedFilesInEnvironment(kbPath, target, preferredWebPath);
            ev.FileCount = files.Count;
            ev.Found = files.Count > 0;
            foreach (var f in files)
            {
                DateTime w;
                try { w = File.GetLastWriteTimeUtc(f); }
                catch { continue; }
                if (ev.FreshestWriteUtc == null || w > ev.FreshestWriteUtc.Value)
                {
                    ev.FreshestWriteUtc = w;
                    ev.FreshestPath = MakeRelative(kbPath, f);
                }
            }
            if (ev.FreshestWriteUtc != null)
            {
                if (priorMtimeUtc != null)
                {
                    // File was (re)generated iff its mtime advanced past the pre-build
                    // snapshot. Strictly-greater: an untouched file keeps its mtime.
                    ev.Fresh = ev.FreshestWriteUtc.Value > priorMtimeUtc.Value;
                }
                else
                {
                    // No snapshot (object had no .cs before, or capture failed): fall
                    // back to absolute compare with a small clock-skew slack so a file
                    // written in the same second as build start is not flagged stale.
                    ev.Fresh = ev.FreshestWriteUtc.Value >= sinceUtc.AddSeconds(-2);
                }
            }
            return ev;
        }

        internal static string ResolveLatestBaselineDir(string kbPath, string target)
        {
            string root = Path.Combine(kbPath, ".gx", "build-baselines", SanitizeName(target));
            if (!Directory.Exists(root)) return null;
            try
            {
                return Directory.GetDirectories(root)
                    .OrderByDescending(d => d, StringComparer.Ordinal)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        private static string SanitizeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        private static string MakeRelative(string root, string full)
        {
            return PathSafety.MakeRelative(root, full);
        }

        private static string SafeRead(string path)
        {
            try { return File.ReadAllText(path); } catch { return string.Empty; }
        }

        private static void AddDiffEntry(JArray files, string name, string baseline, string current, ref int totalChanged)
        {
            var (diff, added, removed) = UnifiedDiff(baseline ?? string.Empty, current ?? string.Empty, name);
            int changed = added + removed;
            totalChanged += changed;
            files.Add(new JObject
            {
                ["name"] = name,
                ["diff"] = diff,
                ["addedLines"] = added,
                ["removedLines"] = removed
            });
        }

        /// <summary>
        /// Minimal-but-readable unified diff. Not byte-for-byte compatible with GNU diff,
        /// but produces a single hunk in standard unified format that downstream readers
        /// (and humans) handle. Good enough for surfacing generated-file deltas.
        /// </summary>
        internal static (string diff, int added, int removed) UnifiedDiff(string left, string right, string fileLabel)
        {
            string[] a = (left ?? "").Split('\n');
            string[] b = (right ?? "").Split('\n');
            // LCS-based diff (small inputs in practice — a single generated file).
            int[,] lcs = new int[a.Length + 1, b.Length + 1];
            for (int i = a.Length - 1; i >= 0; i--)
                for (int j = b.Length - 1; j >= 0; j--)
                    lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1
                              : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

            var sb = new StringBuilder();
            sb.Append("--- a/").Append(fileLabel).Append('\n');
            sb.Append("+++ b/").Append(fileLabel).Append('\n');
            // Whole-file hunk header — simpler than computing per-hunk windows.
            sb.AppendFormat("@@ -1,{0} +1,{1} @@\n", a.Length, b.Length);

            int added = 0, removed = 0;
            int ii = 0, jj = 0;
            while (ii < a.Length && jj < b.Length)
            {
                if (a[ii] == b[jj])
                {
                    sb.Append(' ').Append(a[ii]).Append('\n');
                    ii++; jj++;
                }
                else if (lcs[ii + 1, jj] >= lcs[ii, jj + 1])
                {
                    sb.Append('-').Append(a[ii]).Append('\n');
                    ii++; removed++;
                }
                else
                {
                    sb.Append('+').Append(b[jj]).Append('\n');
                    jj++; added++;
                }
            }
            while (ii < a.Length) { sb.Append('-').Append(a[ii++]).Append('\n'); removed++; }
            while (jj < b.Length) { sb.Append('+').Append(b[jj++]).Append('\n'); added++; }

            if (added == 0 && removed == 0)
            {
                // No changes — emit empty diff (drop the @@ header to keep output small).
                return (string.Empty, 0, 0);
            }
            return (sb.ToString(), added, removed);
        }
    }
}
