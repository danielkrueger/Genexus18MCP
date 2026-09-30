using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Utils;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 82 — `genexus_time_travel name=&lt;obj&gt; at=&lt;ISO-or-commit&gt;`.
    /// Restores an object's part bytes from a git history point. NO write to the KB.
    /// </summary>
    public class TimeTravelService
    {
        private readonly KbService _kbService;
        private readonly ObjectService _objectService;

        public TimeTravelService(KbService kbService, ObjectService objectService)
        {
            _kbService = kbService;
            _objectService = objectService;
        }

        // SECURITY: `name` and `at` are LLM-controlled and reach `git`, plus
        // (for `name`) Path.Combine on the KB working tree. Enforce a tight
        // allowlist at the entrypoint so traversal / arg-confusion attempts
        // never reach the shell-out or filesystem layers.
        internal static bool IsSafeObjectName(string name) => SafePathSegment.IsSafe(name);

        internal static bool IsSafeAtValue(string at)
        {
            if (string.IsNullOrWhiteSpace(at) || at.Length > 64) return false;
            // Allowed shapes: 7-40 hex (sha), or an ISO-8601-ish timestamp / date.
            // Both are far narrower than git's actual --before parser, but they
            // cover every legitimate caller and keep meta-characters (' ', '"',
            // '\\', ';', '&', '|', leading '-') out of the git command line.
            foreach (var c in at)
            {
                bool ok = char.IsLetterOrDigit(c) || c == '-' || c == ':' || c == '.'
                       || c == '+' || c == 'T' || c == 'Z' || c == ' ' || c == '/';
                if (!ok) return false;
            }
            if (at.StartsWith("-")) return false;
            return true;
        }

        public string Recover(string name, string at)
        {
            if (string.IsNullOrWhiteSpace(name)) return Err("name is required.");
            if (string.IsNullOrWhiteSpace(at)) return Err("at is required (ISO timestamp or commit sha).");
            if (!IsSafeObjectName(name))
                return McpResponse.Err(code: "InvalidName", message: "name must match [A-Za-z0-9._-]{1,200}.");
            if (!IsSafeAtValue(at))
                return McpResponse.Err(code: "InvalidAt", message: "at must be a commit sha (7-40 hex) or an ISO-8601 timestamp; metacharacters and leading '-' are rejected.");

            string kbPath = null;
            try { kbPath = _kbService?.GetKbPath(); } catch { }
            if (string.IsNullOrEmpty(kbPath) || !Directory.Exists(kbPath))
                return Err("KB path not resolvable.");
            if (!Directory.Exists(Path.Combine(kbPath, ".git")))
                return McpResponse.Err(code: "KbNotInGit", message: "KB is not in a git repository.", hint: "Initialise git in the KB directory (git init) to enable time-travel.");

            // Resolve `at` → commit sha. ISO timestamp uses `git log --before=<iso> -1`.
            string commit;
            if (System.Text.RegularExpressions.Regex.IsMatch(at, "^[0-9a-fA-F]{7,40}$"))
            {
                commit = at;
            }
            else
            {
                int exit;
                string so, se;
                exit = RunGit(kbPath, new[] { "log", "--before=" + at, "-1", "--pretty=%H" }, out so, out se);
                if (exit != 0 || string.IsNullOrWhiteSpace(so))
                    return McpResponse.Err(code: "NoCommitBefore", message: "No commit found before the specified timestamp.", extra: new JObject { ["at"] = at, ["stderr"] = se });
                commit = so.Trim();
            }

            // Find the object's directory on disk via SDK or by walking the KB.
            string objDir = TryFindObjectDirectory(kbPath, name);
            if (string.IsNullOrEmpty(objDir))
                return McpResponse.Err(code: "ObjectNotFoundOnDisk", message: "Object directory not found on disk.", target: name);

            // Make objDir relative to git root for `git show`.
            string rel = MakeRelative(kbPath, objDir);
            int e2;
            string lsOut, lsErr;
            // `--` separator: keep the path positional even if `rel` starts with '-'.
            e2 = RunGit(kbPath, new[] { "ls-tree", "-r", "--name-only", commit, "--", rel }, out lsOut, out lsErr);
            if (e2 != 0)
                return McpResponse.Err(code: "GitLsTreeFailed", message: "git ls-tree failed.", extra: new JObject { ["commit"] = commit, ["stderr"] = lsErr });

            var parts = new JArray();
            foreach (var line in lsOut.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string path = line.Trim();
                int rc;
                string content, ce;
                rc = RunGit(kbPath, new[] { "show", commit + ":" + path }, out content, out ce);
                if (rc != 0) continue;
                parts.Add(new JObject { ["path"] = path, ["bytes"] = content.Length, ["content"] = content });
            }

            return McpResponse.Ok(target: name, code: "TimeTravelRecovered", result: new JObject
            {
                ["recoveredFromCommit"] = commit,
                ["parts"] = parts,
                ["hint"] = "Parts are returned read-only. Call genexus_edit mode=full to restore any of them."
            });
        }

        private string TryFindObjectDirectory(string kbPath, string name)
        {
            try
            {
                var obj = _objectService?.FindObject(name);
                if (obj != null)
                {
                    string typeName = "";
                    try { typeName = obj.TypeDescriptor?.Name ?? ""; } catch { }
                    var dir = Path.Combine(kbPath, "Objects", typeName, name);
                    if (Directory.Exists(dir)) return dir;
                }
            }
            catch { }
            // Best-effort fallback: search for any */<name>/ under kbPath/Objects/
            try
            {
                var objects = Path.Combine(kbPath, "Objects");
                if (Directory.Exists(objects))
                {
                    foreach (var typeDir in Directory.EnumerateDirectories(objects))
                    {
                        var cand = Path.Combine(typeDir, name);
                        if (Directory.Exists(cand)) return cand;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string MakeRelative(string root, string full)
        {
            return PathSafety.MakeRelative(root, full);
        }

        private static string Err(string m) => McpResponse.Err(code: "TimeTravelFailed", message: m);

        private const int GitTimeoutMs = 30000;

        /// <summary>
        /// GIT_TERMINAL_PROMPT stops a credential prompt blocking on the inherited
        /// stdio pipe; GIT_PAGER stops a configured paginator holding the
        /// terminal-detect probe open until the timeout.
        /// </summary>
        private static readonly Dictionary<string, string> NonInteractiveGitEnv = new Dictionary<string, string>
        {
            { "GIT_TERMINAL_PROMPT", "0" },
            { "GIT_PAGER", "cat" },
        };

        private static int RunGit(string cwd, string[] args, out string stdout, out string stderr)
        {
            var outcome = ProcessLauncher.Run(
                "git",
                "--no-pager -c color.ui=false " + Argv.Join(args),
                cwd,
                GitTimeoutMs,
                Encoding.UTF8,
                NonInteractiveGitEnv);

            if (outcome.StartFailed)
            {
                stdout = ""; stderr = "Process.Start returned null"; return -1;
            }

            if (outcome.TimedOut)
            {
                stdout = outcome.StdOut; stderr = "git timed out"; return -1;
            }

            stdout = outcome.StdOut;
            stderr = outcome.StdErr;
            return outcome.ExitCode;
        }
    }
}