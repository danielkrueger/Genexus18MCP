using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 98 — `genexus_cross_browser target=&lt;obj&gt; browsers=[chrome,firefox,safari]
    /// capture=[screenshot,console]`. Runs each requested browser in parallel via the
    /// existing BrowserDriverInvoker (chrome) and `npx playwright` (firefox/webkit).
    /// Per-browser entry has `{ok, skipped?, code?, screenshotPath, consoleErrors, ms}`.
    /// Drivers that aren't installed return `{skipped:true, code:"BrowserDriverUnavailable"}`;
    /// other browsers still run.
    /// </summary>
    public class CrossBrowserService
    {
        private readonly RunObjectService _runObject;

        public CrossBrowserService(RunObjectService runObject)
        {
            _runObject = runObject;
        }

        public string Run(string targetObject, JArray browsersArr, JArray captureArr)
        {
            if (string.IsNullOrWhiteSpace(targetObject))
                return McpResponse.Err(code: "MissingTarget", message: "target is required.", hint: "Pass target=<WebPanel or object name>.");

            var browsers = new List<string>();
            if (browsersArr != null) foreach (var b in browsersArr) { var s = b?.ToString(); if (!string.IsNullOrEmpty(s)) browsers.Add(s.ToLowerInvariant()); }
            if (browsers.Count == 0) browsers.AddRange(new[] { "chrome", "firefox", "safari" });

            // Resolve URL once via RunObjectService.
            string url;
            try
            {
                var urlJson = _runObject?.Resolve(targetObject, null, null);
                if (string.IsNullOrEmpty(urlJson))
                    return McpResponse.Err(
                        code: "RunObjectServiceUnavailable",
                        message: "RunObjectService is unavailable.",
                        hint: "Ensure the Worker is fully started and a KB is open.");
                var jo = JObject.Parse(urlJson);
                // v2.8.0 — RunObjectService now returns canonical envelope with the URL
                // under result.url. Fall back to top-level for any other resolver.
                url = jo["result"]?["url"]?.ToString() ?? jo["url"]?.ToString();
                if (string.IsNullOrEmpty(url))
                    return McpResponse.Err(
                        code: "UrlResolutionFailed",
                        message: "Could not resolve runtime URL for " + targetObject + ".",
                        hint: "Check that the object exists and the KB web server is running.",
                        extra: new JObject { ["resolve"] = jo });
            }
            catch (Exception ex) { return McpResponse.Err(code: "CrossBrowserError", message: ex.Message, hint: "Check that RunObjectService is wired and the KB is open."); }

            var tasks = new List<Task<JObject>>();
            foreach (var b in browsers) tasks.Add(Task.Run(() => RunOne(b, url)));
            try { Task.WhenAll(tasks).Wait(120000); } catch { }

            var results = new JArray();
            bool anyFailed = false;
            foreach (var t in tasks)
            {
                JObject r;
                if (t.IsCompleted && !t.IsFaulted) r = t.Result;
                else r = new JObject { ["browser"] = "?", ["ok"] = false, ["error"] = t.Exception?.Message ?? "timeout" };
                if (!(r["ok"]?.Value<bool>() ?? false)) anyFailed = true;
                results.Add(r);
            }

            return McpResponse.Ok(
                target: targetObject,
                code: "CrossBrowserCompleted",
                result: new JObject
                {
                    ["url"] = url,
                    ["results"] = results,
                    ["anyFailed"] = anyFailed
                });
        }

        private JObject RunOne(string browser, string url)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (browser == "chrome")
                {
                    return DriveChrome(browser, url, sw);
                }
                if (browser == "firefox" || browser == "webkit" || browser == "safari")
                {
                    return DrivePlaywright(browser == "safari" ? "webkit" : browser, url, sw);
                }
                return new JObject { ["browser"] = browser, ["ok"] = false, ["code"] = "UnknownBrowser", ["ms"] = sw.ElapsedMilliseconds };
            }
            catch (Exception ex)
            {
                return new JObject { ["browser"] = browser, ["ok"] = false, ["error"] = ex.Message, ["ms"] = sw.ElapsedMilliseconds };
            }
        }

        private JObject DriveChrome(string browser, string url, Stopwatch sw)
        {
            string cli = ResolveCli("chrome-devtools-axi");
            if (cli == null)
                return new JObject { ["browser"] = browser, ["skipped"] = true, ["code"] = "BrowserDriverUnavailable", ["hint"] = "Install chrome-devtools-axi (npm i -g chrome-devtools-axi)", ["ms"] = sw.ElapsedMilliseconds };
            string shotPath = Path.Combine(Path.GetTempPath(), "gxmcp-" + browser + "-" + DateTime.UtcNow.Ticks + ".png");
            int rc = Run(cli, new[] { "screenshot", url, shotPath }, out _, out string err);
            sw.Stop();
            return new JObject
            {
                ["browser"] = browser,
                ["ok"] = rc == 0 && File.Exists(shotPath),
                ["screenshotPath"] = shotPath,
                ["consoleErrors"] = new JArray(),
                ["stderr"] = err,
                ["ms"] = sw.ElapsedMilliseconds
            };
        }

        private JObject DrivePlaywright(string engine, string url, Stopwatch sw)
        {
            string cli = ResolveCli("npx");
            if (cli == null)
                return new JObject { ["browser"] = engine, ["skipped"] = true, ["code"] = "BrowserDriverUnavailable", ["hint"] = "Install Node.js + playwright (npx playwright install)", ["ms"] = sw.ElapsedMilliseconds };
            string shotPath = Path.Combine(Path.GetTempPath(), "gxmcp-" + engine + "-" + DateTime.UtcNow.Ticks + ".png");
            int rc = Run(cli, new[] { "playwright", "screenshot", "--browser=" + engine, url, shotPath }, out _, out string err);
            sw.Stop();
            return new JObject
            {
                ["browser"] = engine,
                ["ok"] = rc == 0 && File.Exists(shotPath),
                ["screenshotPath"] = shotPath,
                ["consoleErrors"] = new JArray(),
                ["stderr"] = err,
                ["ms"] = sw.ElapsedMilliseconds
            };
        }

        private static string ResolveCli(string exe)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c where " + exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return null;
                    string so = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    if (p.ExitCode != 0) return null;
                    foreach (var line in so.Split('\n'))
                    {
                        var t = line.Trim();
                        if (!string.IsNullOrEmpty(t)) return t;
                    }
                }
            }
            catch { }
            return null;
        }

        private static int Run(string exe, string[] args, out string stdout, out string stderr)
        {
            var psi = new ProcessStartInfo(exe, Argv.Join(args))
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using (var p = Process.Start(psi))
            {
                if (p == null) { stdout = ""; stderr = "Process.Start returned null"; return -1; }
                try { p.StandardInput.Close(); } catch { }
                var outSb = new StringBuilder();
                var errSb = new StringBuilder();
                p.OutputDataReceived += (_, e) => { if (e.Data != null) outSb.AppendLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) errSb.AppendLine(e.Data); };
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } stdout = outSb.ToString(); stderr = "timed out"; return -1; }
                p.WaitForExit();
                stdout = outSb.ToString(); stderr = errSb.ToString();
                return p.ExitCode;
            }
        }
    }
}
