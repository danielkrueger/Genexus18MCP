using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 85 — genexus_pr_description action=generate.
    ///
    /// Shells out to `git log` on the KB path (or repo root) to summarise the
    /// last N commits and assemble a structured PR description envelope:
    ///   { title, summary, why, changes: [...] }
    /// </summary>
    public class PrDescriptionService
    {
        private readonly KbService _kbService;

        public PrDescriptionService(KbService kbService)
        {
            _kbService = kbService;
        }

        public string Generate(int last, string workingDir = null)
        {
            if (last <= 0) last = 10;
            if (last > 100) last = 100;

            string cwd = workingDir;
            if (string.IsNullOrEmpty(cwd))
            {
                try { cwd = _kbService?.GetKbPath(); } catch { }
            }
            if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd))
            {
                cwd = Directory.GetCurrentDirectory();
            }

            string raw;
            int exit;
            string stderr;
            try
            {
                raw = RunGit(cwd, "log -" + last + " --pretty=format:%H%x09%s%x09%b%x1e", out exit, out stderr);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "GitInvocationFailed",
                    message: ex.Message,
                    hint: "Ensure git is installed and available in PATH.",
                    nextSteps: new JArray { McpResponse.NextStep("genexus_github", new JObject { ["action"] = "create_pr" }, "Use the GitHub tool for PR creation.") });
            }

            if (exit != 0)
            {
                return McpResponse.Err(
                    code: "GitExitNonZero",
                    message: "git exited with code " + exit + ": " + (stderr ?? "").Trim(),
                    hint: "Check that the working directory is inside a git repository.",
                    extra: new JObject { ["exitCode"] = exit, ["stderr"] = stderr ?? "", ["cwd"] = cwd });
            }

            var commits = ParseCommits(raw);
            var envelope = BuildEnvelope(commits);
            envelope["cwd"] = cwd;
            envelope["commitsRead"] = commits.Count;
            return McpResponse.Ok(
                code: "PrDescriptionGenerated",
                result: envelope);
        }

        internal static List<(string hash, string subject, string body)> ParseCommits(string raw)
        {
            var list = new List<(string, string, string)>();
            if (string.IsNullOrEmpty(raw)) return list;
            // Records separated by ASCII RS (0x1e); fields separated by tab.
            var records = raw.Split(new[] { '\x1e' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rec in records)
            {
                var trimmed = rec.Trim('\n', '\r');
                if (string.IsNullOrEmpty(trimmed)) continue;
                var parts = trimmed.Split(new[] { '\t' }, 3);
                if (parts.Length < 2) continue;
                string hash = parts[0];
                string subject = parts[1];
                string body = parts.Length >= 3 ? parts[2] : "";
                list.Add((hash, subject, body));
            }
            return list;
        }

        internal static JObject BuildEnvelope(List<(string hash, string subject, string body)> commits)
        {
            var changes = new JArray();
            var typeBuckets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in commits)
            {
                string type = ExtractConventionalType(c.subject);
                if (!string.IsNullOrEmpty(type))
                {
                    typeBuckets.TryGetValue(type, out int prev);
                    typeBuckets[type] = prev + 1;
                }
                changes.Add(new JObject
                {
                    ["hash"] = c.hash?.Length > 7 ? c.hash.Substring(0, 7) : c.hash ?? "",
                    ["subject"] = c.subject ?? "",
                    ["type"] = type
                });
            }

            string title;
            if (commits.Count == 0)
            {
                title = "(no commits)";
            }
            else if (commits.Count == 1)
            {
                title = commits[0].subject ?? "";
            }
            else
            {
                // Lead bucket → title prefix.
                var lead = typeBuckets.OrderByDescending(kv => kv.Value).FirstOrDefault();
                string leadType = string.IsNullOrEmpty(lead.Key) ? "chore" : lead.Key;
                title = leadType + ": " + commits.Count + " commits — " + SdkReflection.Truncate(commits[0].subject ?? "", 60);
            }

            var summary = new JArray();
            foreach (var c in commits.Take(8))
            {
                summary.Add("- " + (c.subject ?? ""));
            }

            string why = BuildWhy(commits);

            return new JObject
            {
                ["title"] = title,
                ["summary"] = summary,
                ["why"] = why,
                ["changes"] = changes
            };
        }

        private static string BuildWhy(List<(string hash, string subject, string body)> commits)
        {
            // First non-empty body line gives the "why".
            foreach (var c in commits)
            {
                if (string.IsNullOrWhiteSpace(c.body)) continue;
                var firstLine = c.body
                    .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .FirstOrDefault(l => l.Length > 0);
                if (!string.IsNullOrEmpty(firstLine)) return firstLine;
            }
            return commits.Count == 0
                ? ""
                : "See commit list. Subjects summarise the intent of each change.";
        }

        private static string ExtractConventionalType(string subject)
        {
            if (string.IsNullOrEmpty(subject)) return null;
            // e.g. "feat(mcp): ..." or "fix: ..."
            int colon = subject.IndexOf(':');
            if (colon <= 0 || colon > 20) return null;
            string head = subject.Substring(0, colon);
            int paren = head.IndexOf('(');
            string type = paren > 0 ? head.Substring(0, paren) : head;
            type = type.Trim();
            if (type.Length == 0 || type.Length > 12) return null;
            foreach (char ch in type)
            {
                if (!char.IsLetter(ch)) return null;
            }
            return type.ToLowerInvariant();
        }

        /// <summary>
        /// Mirrors the BlameService.RunGit pattern: spawn `git` with redirected
        /// streams, no shell, capture stdout/stderr and exit code.
        /// </summary>
        private const int GitTimeoutMs = 30000;

        /// <summary>
        /// Blocks the credential and GPG prompts that would otherwise wedge the
        /// worker, and the paginator that would hold the terminal-detect probe open.
        /// </summary>
        private static readonly Dictionary<string, string> NonInteractiveGitEnv = new Dictionary<string, string>
        {
            { "GIT_TERMINAL_PROMPT", "0" },
            { "GIT_PAGER", "cat" },
        };

        private static string RunGit(string cwd, string args, out int exitCode, out string stderr)
        {
            // `--no-pager` so a configured paginator can't block on a terminal-detect
            // probe, and `-c color.ui=false` to keep the output ASCII-clean for the
            // parsers below.
            //
            // 30 s cap - `git log` on a large repo with many commits + GIT_PAGER=cat
            // can take double-digit seconds on first invocation while git refreshes
            // its packed-refs cache. 15 s was tripping legitimate runs.
            var outcome = ProcessLauncher.Run(
                "git",
                "--no-pager -c color.ui=false " + args,
                cwd,
                GitTimeoutMs,
                Encoding.UTF8,
                NonInteractiveGitEnv);

            if (outcome.StartFailed)
                throw new InvalidOperationException("Process.Start returned null for git");

            if (outcome.TimedOut)
                throw new TimeoutException("git " + args + " exceeded 30s in " + cwd);

            exitCode = outcome.ExitCode;
            stderr = outcome.StdErr;
            return outcome.StdOut;
        }
    }
}