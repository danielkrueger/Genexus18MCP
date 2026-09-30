using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Items 5 + 37 (friction 2026-05-22): drives a headless browser
    /// (chrome-devtools-axi preferred, npx playwright fallback) right after a
    /// WebForm edit so the agent gets a visual confirmation without burning
    /// turns on "did the change pixel-render?". When a prior baseline exists
    /// under <c>&lt;kb&gt;/.gx/visual-baselines/&lt;obj&gt;/&lt;part&gt;/</c>, attaches
    /// a pure-pixel-equality diff (changedPixels / totalPixels + a red-overlay
    /// diff.png).
    ///
    /// Everything that shells out goes through <see cref="ICliRunner"/> so
    /// tests inject a fake. The class never throws — failures are reported
    /// inline via a <c>{skipped:true, reason}</c> envelope so the edit
    /// response is never lost.
    /// </summary>
    public class VisualVerifyService
    {
        public interface ICliRunner
        {
            CliResult Run(string fileName, string arguments, int timeoutMs);
            /// <summary>Returns absolute path to the resolved CLI shim, or null when not on PATH.</summary>
            string Which(string command);
        }

        public class CliResult
        {
            public int ExitCode;
            public string StdOut;
            public string StdErr;
            public bool TimedOut;
        }

        public class DefaultCliRunner : ICliRunner
        {
            // chrome-devtools-axi cold-start commonly takes 25-60 s; 90 s is a
            // safe upper bound. Mirrors PreviewService.DefaultCliTimeoutMs.
            public CliResult Run(string fileName, string arguments, int timeoutMs)
            {
                ProcessStartInfo psi;
                var ext = Path.GetExtension(fileName);
                bool isNativeExe = string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(ext, ".com", StringComparison.OrdinalIgnoreCase);
                if (!isNativeExe)
                {
                    // .cmd/.bat/.ps1 shims need cmd.exe + PATHEXT lookup.
                    psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + fileName + "\" " + arguments + "\"");
                }
                else
                {
                    psi = new ProcessStartInfo(fileName, arguments);
                }
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;

                try
                {
                    using (var p = Process.Start(psi))
                    {
                        var so = new System.Text.StringBuilder();
                        var se = new System.Text.StringBuilder();
                        p.OutputDataReceived += (s, e) => { if (e.Data != null) so.AppendLine(e.Data); };
                        p.ErrorDataReceived += (s, e) => { if (e.Data != null) se.AppendLine(e.Data); };
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                        if (!p.WaitForExit(timeoutMs))
                        {
                            try { p.Kill(); } catch { }
                            return new CliResult { ExitCode = -1, StdOut = so.ToString(), StdErr = se.ToString(), TimedOut = true };
                        }
                        try { p.WaitForExit(500); } catch { }
                        return new CliResult { ExitCode = p.ExitCode, StdOut = so.ToString(), StdErr = se.ToString() };
                    }
                }
                catch (Exception ex)
                {
                    return new CliResult { ExitCode = -1, StdErr = ex.Message };
                }
            }

            public string Which(string command)
            {
                try
                {
                    return ConsoleProbe.Which(command);
                }
                catch { }
                return null;
            }
        }

        public sealed class DriverInfo
        {
            public string Name;            // "chrome-devtools-axi" | "playwright" | null
            public string CliPath;         // resolved shim absolute path (for chrome-devtools-axi)
            public bool Available => !string.IsNullOrEmpty(Name);
        }

        public sealed class VerifyResult
        {
            public bool Skipped;
            public string SkipReason;
            public string ScreenshotPath;
            public string Base64Truncated;
            public string CapturedAtUtc;
            public string UrlOpened;
            public PixelDiff Diff;
            public string Error;
        }

        public sealed class PixelDiff
        {
            public long ChangedPixels;
            public long TotalPixels;
            public string DiffPath;
            public string AgainstBaseline;
        }

        // ~8 KB base64 cap — keeps the response payload reasonable for the
        // gateway transport.
        internal const int Base64TruncationBytes = 8 * 1024;
        // Cap of 10 baselines per (obj, part) keeps the .gx dir bounded.
        internal const int BaselineRetention = 10;
        // Default total budget per verify call — 90 s open + 15 s screenshot.
        private const int DefaultCliTimeoutMs = 90000;

        private readonly ICliRunner _runner;
        private readonly Func<string> _launcherResolver;
        private readonly Func<string> _kbPathResolver;
        private readonly Func<string, string> _objectAspxResolver;
        private readonly string _baseUrlOverride;

        public VisualVerifyService(KbService kbService, ObjectService objectService)
            : this(new DefaultCliRunner(),
                   () => { try { return kbService?.GetLauncherObjectName(); } catch { return null; } },
                   () => { try { return kbService?.GetKbPath(); } catch { return null; } },
                   name => DefaultAspxFor(name),
                   null)
        { }

        public VisualVerifyService(
            ICliRunner runner,
            Func<string> launcherResolver,
            Func<string> kbPathResolver,
            Func<string, string> objectAspxResolver,
            string baseUrlOverride)
        {
            _runner = runner ?? new DefaultCliRunner();
            _launcherResolver = launcherResolver ?? (() => null);
            _kbPathResolver = kbPathResolver ?? (() => null);
            _objectAspxResolver = objectAspxResolver ?? (name => DefaultAspxFor(name));
            _baseUrlOverride = baseUrlOverride;
        }

        /// <summary>
        /// Detects which browser driver is on PATH. Tries chrome-devtools-axi
        /// first (preferred — purpose-built for this codebase), then falls
        /// back to npx playwright.
        /// </summary>
        public DriverInfo DetectDriver()
        {
            try
            {
                var axi = _runner.Which("chrome-devtools-axi") ?? _runner.Which("chrome-devtools-axi.cmd");
                if (!string.IsNullOrEmpty(axi))
                {
                    return new DriverInfo { Name = "chrome-devtools-axi", CliPath = axi };
                }
                // playwright comes in via npx; we don't try to invoke `playwright`
                // as a bare command because the npm shim is usually only present
                // under @playwright/test. `npx` itself is the gate.
                var npx = _runner.Which("npx") ?? _runner.Which("npx.cmd");
                if (!string.IsNullOrEmpty(npx))
                {
                    return new DriverInfo { Name = "playwright", CliPath = npx };
                }
            }
            catch { }
            return new DriverInfo();
        }

        /// <summary>
        /// Captures a screenshot of <paramref name="objectName"/> and writes
        /// it under <c>.gx/visual-baselines/&lt;obj&gt;/&lt;part&gt;/</c>. When
        /// a prior baseline exists, computes a pure-pixel-equality diff
        /// against it and emits a red-overlay diff.png alongside.
        /// </summary>
        public VerifyResult Verify(string objectName, string part)
        {
            var result = new VerifyResult { CapturedAtUtc = DateTime.UtcNow.ToString("o") };
            try
            {
                var driver = DetectDriver();
                if (!driver.Available)
                {
                    result.Skipped = true;
                    result.SkipReason = "BrowserDriverUnavailable";
                    return result;
                }

                string target = objectName;
                if (string.IsNullOrWhiteSpace(target))
                {
                    target = _launcherResolver?.Invoke();
                }
                if (string.IsNullOrWhiteSpace(target))
                {
                    result.Skipped = true;
                    result.SkipReason = "NoLauncher";
                    return result;
                }

                string baseUrl = ResolveBaseUrl();
                string url = baseUrl.TrimEnd('/') + "/" + _objectAspxResolver(target).TrimStart('/');
                result.UrlOpened = url;

                string baselinesRoot = ResolveBaselinesRoot();
                if (string.IsNullOrEmpty(baselinesRoot))
                {
                    result.Skipped = true;
                    result.SkipReason = "KbPathUnresolved";
                    return result;
                }

                string partKey = string.IsNullOrWhiteSpace(part) ? "default" : SanitizeSegment(part);
                string objKey = SanitizeSegment(target);
                string dir = Path.Combine(baselinesRoot, objKey, partKey);
                try { Directory.CreateDirectory(dir); } catch { /* tolerated; capture may still succeed if dir exists */ }

                string utcIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ss-fffZ");
                string shotPath = Path.Combine(dir, utcIso + ".png");

                if (!CaptureScreenshot(driver, url, shotPath, out string captureErr))
                {
                    result.Skipped = true;
                    result.SkipReason = "CaptureFailed";
                    result.Error = captureErr;
                    return result;
                }

                result.ScreenshotPath = shotPath;
                result.Base64Truncated = TryReadBase64Truncated(shotPath);

                // Pixel-diff against the most recent prior baseline (i.e. the
                // chronologically previous PNG in the same directory).
                string priorBaseline = FindPriorBaseline(dir, shotPath);
                if (!string.IsNullOrEmpty(priorBaseline))
                {
                    var diff = ComputePixelDiff(priorBaseline, shotPath, dir, utcIso);
                    if (diff != null)
                    {
                        result.Diff = diff;
                    }
                }

                // Retention: keep last 10 baselines per (obj, part).
                TrimBaselineRetention(dir);

                return result;
            }
            catch (Exception ex)
            {
                result.Skipped = true;
                result.SkipReason = "VerifyError";
                result.Error = ex.Message;
                return result;
            }
        }

        /// <summary>Convenience — produces the response JObject block in the
        /// shape contracted by the tool schema.</summary>
        public JObject VerifyAsJObject(string objectName, string part)
        {
            var r = Verify(objectName, part);
            if (r.Skipped)
            {
                var skip = new JObject
                {
                    ["skipped"] = true,
                    ["reason"] = r.SkipReason ?? "Unknown"
                };
                if (!string.IsNullOrEmpty(r.Error)) skip["error"] = r.Error;
                if (!string.IsNullOrEmpty(r.UrlOpened)) skip["urlOpened"] = r.UrlOpened;
                return skip;
            }

            var obj = new JObject
            {
                ["path"] = r.ScreenshotPath,
                ["base64Truncated"] = r.Base64Truncated ?? "",
                ["capturedAtUtc"] = r.CapturedAtUtc,
                ["urlOpened"] = r.UrlOpened
            };
            if (r.Diff != null)
            {
                obj["pixelDiff"] = new JObject
                {
                    ["changedPixels"] = r.Diff.ChangedPixels,
                    ["totalPixels"] = r.Diff.TotalPixels,
                    ["diffPath"] = r.Diff.DiffPath,
                    ["againstBaseline"] = r.Diff.AgainstBaseline
                };
            }
            return obj;
        }

        // -------- screenshot capture per driver ---------------------------

        private bool CaptureScreenshot(DriverInfo driver, string url, string outPath, out string err)
        {
            err = null;
            if (string.Equals(driver.Name, "chrome-devtools-axi", StringComparison.OrdinalIgnoreCase))
            {
                var openRes = _runner.Run(driver.CliPath, "open " + Quote(url), DefaultCliTimeoutMs);
                if (openRes.TimedOut || openRes.ExitCode != 0)
                {
                    err = "open: " + (openRes.StdErr ?? "(no stderr)");
                    return false;
                }
                var shotRes = _runner.Run(driver.CliPath, "screenshot " + Quote(outPath), DefaultCliTimeoutMs);
                if (shotRes.TimedOut || shotRes.ExitCode != 0)
                {
                    err = "screenshot: " + (shotRes.StdErr ?? "(no stderr)");
                    return false;
                }
                return File.Exists(outPath);
            }
            if (string.Equals(driver.Name, "playwright", StringComparison.OrdinalIgnoreCase))
            {
                // npx playwright screenshot <url> <out>. Single-shot — playwright
                // CLI exits after writing the PNG.
                string args = "playwright screenshot " + Quote(url) + " " + Quote(outPath);
                var res = _runner.Run(driver.CliPath, args, DefaultCliTimeoutMs);
                if (res.TimedOut || res.ExitCode != 0)
                {
                    err = "playwright: " + (res.StdErr ?? "(no stderr)");
                    return false;
                }
                return File.Exists(outPath);
            }
            err = "unknown driver: " + driver.Name;
            return false;
        }

        // -------- pixel diff ----------------------------------------------

        /// <summary>
        /// Pure pixel-equality comparison. We don't do perceptual hashing on
        /// purpose — false positives from JPEG-style softness are acceptable
        /// for the agent's "did the edit visually land?" question, and the
        /// numerical changedPixels figure is easier to reason about.
        /// </summary>
        internal static PixelDiff ComputePixelDiff(string baselinePath, string currentPath, string dir, string utcIso)
        {
            if (!File.Exists(baselinePath) || !File.Exists(currentPath)) return null;
            try
            {
                using (var a = new Bitmap(baselinePath))
                using (var b = new Bitmap(currentPath))
                {
                    int w = Math.Min(a.Width, b.Width);
                    int h = Math.Min(a.Height, b.Height);
                    if (w == 0 || h == 0) return null;

                    long changed = 0;
                    long total = (long)w * h;
                    using (var diffBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                    {
                        for (int y = 0; y < h; y++)
                        {
                            for (int x = 0; x < w; x++)
                            {
                                Color pa = a.GetPixel(x, y);
                                Color pb = b.GetPixel(x, y);
                                if (pa.ToArgb() != pb.ToArgb())
                                {
                                    changed++;
                                    diffBmp.SetPixel(x, y, Color.FromArgb(200, 255, 0, 0));
                                }
                                else
                                {
                                    diffBmp.SetPixel(x, y, Color.FromArgb(32, pb.R, pb.G, pb.B));
                                }
                            }
                        }
                        string diffPath = Path.Combine(dir, utcIso + ".diff.png");
                        diffBmp.Save(diffPath, ImageFormat.Png);
                        return new PixelDiff
                        {
                            ChangedPixels = changed,
                            TotalPixels = total,
                            DiffPath = diffPath,
                            AgainstBaseline = baselinePath
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("[VisualVerify] pixel-diff failed: " + ex.Message);
                return null;
            }
        }

        // -------- helpers --------------------------------------------------

        private string TryReadBase64Truncated(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                string b64 = Convert.ToBase64String(bytes);
                if (b64.Length <= Base64TruncationBytes) return b64;
                return b64.Substring(0, Base64TruncationBytes);
            }
            catch
            {
                return null;
            }
        }

        internal static string FindPriorBaseline(string dir, string currentPath)
        {
            try
            {
                var current = Path.GetFileName(currentPath);
                var pngs = Directory.GetFiles(dir, "*.png")
                    .Where(p =>
                    {
                        var n = Path.GetFileName(p);
                        // Skip the screenshot we just wrote and skip the diff
                        // overlays so a future run doesn't diff against a diff.
                        return !string.Equals(n, current, StringComparison.OrdinalIgnoreCase)
                            && !n.EndsWith(".diff.png", StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderByDescending(p => p, StringComparer.Ordinal)
                    .ToList();
                return pngs.FirstOrDefault();
            }
            catch { return null; }
        }

        internal static void TrimBaselineRetention(string dir)
        {
            try
            {
                // Group by base PNG; delete any (and their diff.png companion)
                // beyond the most recent BaselineRetention.
                var pngs = Directory.GetFiles(dir, "*.png")
                    .Where(p => !Path.GetFileName(p).EndsWith(".diff.png", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(p => p, StringComparer.Ordinal)
                    .ToList();
                if (pngs.Count <= BaselineRetention) return;
                foreach (var stale in pngs.Skip(BaselineRetention))
                {
                    try { File.Delete(stale); } catch { }
                    var diff = stale.Replace(".png", ".diff.png");
                    try { if (File.Exists(diff)) File.Delete(diff); } catch { }
                }
            }
            catch { /* best-effort */ }
        }

        private string ResolveBaseUrl()
        {
            if (!string.IsNullOrEmpty(_baseUrlOverride)) return _baseUrlOverride;
            // Mirror PreviewService default; the same KB-level config applies.
            return "http://localhost/portal3_desenv";
        }

        private string ResolveBaselinesRoot()
        {
            string kb = _kbPathResolver?.Invoke();
            if (string.IsNullOrWhiteSpace(kb)) return null;
            return Path.Combine(kb, ".gx", "visual-baselines");
        }

        internal static string DefaultAspxFor(string objectName)
        {
            if (string.IsNullOrWhiteSpace(objectName)) return "";
            // GeneXus emits lowercase aspx filenames for WebPanel objects.
            return objectName.ToLowerInvariant() + ".aspx";
        }

        internal static string SanitizeSegment(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            var chars = s.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray();
            return new string(chars);
        }

        private static string Quote(string s) =>
            "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
