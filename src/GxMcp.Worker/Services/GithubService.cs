using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 71 — `genexus_github action=create_pr`. Shells out to the `gh` CLI.
    /// Returns the PR URL on success or a typed `GhCliNotInstalled` envelope.
    /// </summary>
    public class GithubService
    {
        private readonly KbService _kbService;

        public GithubService(KbService kbService) { _kbService = kbService; }

        public string CreatePr(string title, string body, string baseBranch, string workingDir, bool dryRun = false)
        {
            if (string.IsNullOrWhiteSpace(title))
                return Err("title is required.");

            // SECURITY: title / body / baseBranch are LLM-controlled. They flow
            // into a Windows command line; if any leading dash makes them look
            // like a `gh` flag the call semantics change silently. Reject
            // values that begin with '-' so they can't be re-parsed as flags
            // (the legitimate values for these never start with '-').
            if (title.TrimStart().StartsWith("-"))
                return McpResponse.Err(code: "InvalidTitle", message: "title may not start with '-'.", hint: "Provide a PR title that does not begin with a dash.");
            if (!string.IsNullOrWhiteSpace(baseBranch) && baseBranch.TrimStart().StartsWith("-"))
                return McpResponse.Err(code: "InvalidBaseBranch", message: "baseBranch may not start with '-'.", hint: "Provide a valid branch name that does not begin with a dash.");

            string cwd = string.IsNullOrEmpty(workingDir) ? (TryGetKbPath() ?? Directory.GetCurrentDirectory()) : workingDir;

            var argList = new System.Collections.Generic.List<string> { "pr", "create", "--title", title };
            if (!string.IsNullOrWhiteSpace(body)) { argList.Add("--body"); argList.Add(body); }
            if (!string.IsNullOrWhiteSpace(baseBranch)) { argList.Add("--base"); argList.Add(baseBranch); }

            // dryRun: return the resolved command without shelling out.
            if (dryRun)
            {
                return McpResponse.Ok(
                    code: "DryRun",
                    result: new JObject
                    {
                        ["preview"] = new JObject
                        {
                            ["exe"] = "gh",
                            ["args"] = new Newtonsoft.Json.Linq.JArray(argList.ToArray()),
                            ["cwd"] = cwd,
                            ["note"] = "dryRun=true: gh pr create was not executed."
                        }
                    });
            }

            int exit;
            string stdout, stderr;
            try
            {
                exit = Run("gh", argList, cwd, out stdout, out stderr);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return McpResponse.Err(
                    code: "GhCliNotInstalled",
                    message: "GitHub CLI (gh) is not installed or not on PATH.",
                    hint: "Install GitHub CLI from https://cli.github.com/ and run `gh auth login`.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        "genexus_github",
                        new JObject { ["action"] = "create_pr", ["title"] = title },
                        "Retry after installing and authenticating the gh CLI.")));
            }
            catch (Exception ex) { return Err(ex.Message); }

            if (exit != 0)
            {
                return McpResponse.Err(
                    code: "GhExitNonZero",
                    message: "gh pr create exited with code " + exit + ".",
                    hint: "Check stderr for authentication or remote-push errors.",
                    extra: new JObject { ["exitCode"] = exit, ["stderr"] = stderr ?? "", ["cwd"] = cwd });
            }
            string url = (stdout ?? "").Trim();
            return McpResponse.Ok(
                code: "GithubPrCreated",
                result: new JObject { ["url"] = url, ["cwd"] = cwd });
        }

        private string TryGetKbPath() { try { return _kbService?.GetKbPath(); } catch { return null; } }
        private static string Err(string m) => McpResponse.Err(code: "GithubError", message: m);

        private static int Run(string exe, System.Collections.Generic.List<string> args, string cwd, out string stdout, out string stderr)
        {
            var outcome = ProcessLauncher.Run(
                exe,
                Argv.Join(args),
                cwd,
                GhTimeoutMs,
                Encoding.UTF8,
                // Stops gh blocking on a credential or device-code prompt reading
                // the inherited stdio pipe.
                new Dictionary<string, string> { { "GIT_TERMINAL_PROMPT", "0" } });

            if (outcome.StartFailed)
            {
                stdout = ""; stderr = "Process.Start returned null"; return -1;
            }

            if (outcome.TimedOut)
            {
                stdout = outcome.StdOut; stderr = "gh timed out"; return -1;
            }

            stdout = outcome.StdOut;
            stderr = outcome.StdErr;
            return outcome.ExitCode;
        }

        private const int GhTimeoutMs = 30000;
    }
}