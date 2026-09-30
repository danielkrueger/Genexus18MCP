using System;
using System.IO;
using GxMcp.Worker.Models;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Utils;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 89 — genexus_screenshot_publish. Local-only: copies a screenshot
    /// PNG to <c>&lt;kbPath&gt;/.gx/published-screenshots/&lt;UTC&gt;-&lt;basename&gt;</c>
    /// and returns the destination path. No remote upload.
    /// </summary>
    public class ScreenshotPublishService
    {
        private readonly KbService _kbService;

        public ScreenshotPublishService(KbService kbService)
        {
            _kbService = kbService;
        }

        public string Publish(string sourcePath, string kbPathOverride = null)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return Error("MissingPath", "sourcePath is required.");
            }

            string kbPath = EffectiveKbPath.Resolve(_kbService, kbPathOverride);
            if (string.IsNullOrEmpty(kbPath))
            {
                return Error("NoKbOpen", "No KB is currently open; pass kbPathOverride or open a KB first.");
            }
            if (!File.Exists(sourcePath))
            {
                return Error("SourceNotFound", "Screenshot file does not exist: " + sourcePath);
            }

            return PublishCore(sourcePath, kbPath);
        }

        private static readonly string[] AllowedExt = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };
        private const long MaxBytes = 25L * 1024 * 1024;

        // Exposed for tests so they don't need a live KbService.
        public static string PublishCore(string sourcePath, string kbPath)
        {
            // Plan 013: only image files, only from a root screenshots plausibly
            // originate from (OS temp dir, the open KB, or an explicit override),
            // and only up to a sane size — before ever touching File.Copy.
            string ext = Path.GetExtension(sourcePath ?? "").ToLowerInvariant();
            if (Array.IndexOf(AllowedExt, ext) < 0)
                return Error("SourceNotAllowed",
                    "screenshot_publish only accepts image files (" + string.Join(", ", AllowedExt) + ").");

            string full;
            try { full = Path.GetFullPath(sourcePath); }
            catch { return Error("SourceNotAllowed", "Invalid source path."); }

            string envDir = Environment.GetEnvironmentVariable("GXMCP_SCREENSHOT_DIR");
            bool withinTemp = PathSafety.TryResolveWithinRoot(Path.GetTempPath(), full, out _);
            bool withinKb = !string.IsNullOrEmpty(kbPath) && PathSafety.TryResolveWithinRoot(kbPath, full, out _);
            bool withinEnvDir = !string.IsNullOrEmpty(envDir) && PathSafety.TryResolveWithinRoot(envDir, full, out _);
            if (!(withinTemp || withinKb || withinEnvDir))
                return Error("SourceNotAllowed",
                    "screenshot source must be under the OS temp dir, the open KB, or GXMCP_SCREENSHOT_DIR.");

            try
            {
                if (File.Exists(sourcePath) && new FileInfo(sourcePath).Length > MaxBytes)
                    return Error("SourceTooLarge", "Screenshot exceeds 25 MB.");
            }
            catch { /* fall through; File.Copy will surface a real IO error */ }

            try
            {
                string destDir = Path.Combine(kbPath, ".gx", "published-screenshots");
                Directory.CreateDirectory(destDir);

                string basename = Path.GetFileName(sourcePath);
                string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
                string destName = stamp + "-" + basename;
                string destPath = Path.Combine(destDir, destName);
                File.Copy(sourcePath, destPath, overwrite: false);

                long size = 0;
                try { size = new FileInfo(destPath).Length; } catch { }

                return McpResponse.Ok(
                    code: "ScreenshotPublished",
                    result: new JObject
                    {
                        ["sourcePath"] = sourcePath,
                        ["publishedPath"] = destPath,
                        ["basename"] = destName,
                        ["sizeBytes"] = size,
                        ["publishedAtUtc"] = DateTime.UtcNow.ToString("o")
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "PublishFailed",
                    message: ex.Message,
                    hint: "Check that the source file exists and the KB path is writable.",
                    nextSteps: new JArray {
                        McpResponse.NextStep("genexus_screenshot_publish", new JObject { ["sourcePath"] = sourcePath ?? "" }, "Retry after verifying the source path.")
                    });
            }
        }

        private static string Error(string code, string message) =>
            McpResponse.Err(
                code: code,
                message: message,
                nextSteps: new JArray {
                    McpResponse.NextStep("genexus_screenshot_publish", new JObject { ["sourcePath"] = "<path to PNG>" }, "Retry with a valid source path and an open KB.")
                });
    }
}
