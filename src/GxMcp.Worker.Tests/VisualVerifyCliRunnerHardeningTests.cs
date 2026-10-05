using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using GxMcp.Worker.Services;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// <c>VisualVerifyService.DefaultCliRunner</c> had its own cmd.exe command
    /// line and its own quoter:
    ///
    /// <code>
    /// psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + fileName + "\" " + arguments + "\"");
    /// private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    /// </code>
    ///
    /// The quoter escapes a backslash and a quote and nothing else, so a url or a
    /// screenshot path built from the object name the caller asked to verify
    /// reached cmd.exe with <c>%</c> expansion and <c>&amp; | &lt; &gt; ^ ( )</c>
    /// interpretation still live. <c>PreviewService</c> spawns the same class of
    /// shim through <c>BrowserDriverProcess.BuildShimArguments</c>, which refuses
    /// those arguments instead of interpreting them; the two runners had drifted.
    ///
    /// The three tests pin the one builder, the structured refusal, and the
    /// command shape for input that was always valid - a hardening that quietly
    /// turned a working screenshot into a broken one would be the other failure
    /// worth having.
    /// </summary>
    public class VisualVerifyCliRunnerHardeningTests
    {
        [Fact]
        public void TheShimCommandLineIsBuiltInOnePlaceAndThisRunnerUsesIt()
        {
            // Source shape, so the invariant is about code rather than about one
            // call site: comments are blanked first, or a comment that mentions
            // the old shape would satisfy a needle meant to find the code.
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "VisualVerifyService.cs");

            // The hand-rolled `/c ""..."" ...""` fragment. A cmd.exe interpreter
            // is still involved - BuildShimArguments returns a whole `/d /s /c
            // "..."` line - so what has to be gone is a command line *this file*
            // concatenates, not the string "cmd.exe".
            Assert.Equal(0, SourceAssert.Count(source, "\"/c \\\"\\\"\""));

            Assert.True(SourceAssert.Count(source, "BuildShimArguments(") >= 1,
                "the shim branch must reach the shared hardened builder");

            Assert.DoesNotContain("private static string Quote", source);

            // The branch that was never the problem stays as it was: a native exe
            // or com is started directly, with no interpreter.
            Assert.Contains("new ProcessStartInfo(fileName, arguments)", source);
        }

        [Fact]
        public void AnUnsafeArgumentIsRefusedStructurallyAndNothingIsSpawned()
        {
            string sentinel = Path.Combine(Path.GetTempPath(), "gx-visualverify-sentinel-" + Guid.NewGuid().ToString("N") + ".txt");
            string shim = Path.Combine(Path.GetTempPath(), "gx-visualverify-" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(shim, "@echo off\r\necho spawned>\"" + sentinel + "\"\r\nexit /b 0\r\n");

            VisualVerifyService.CliResult refusal = null;
            try
            {
                var runner = new VisualVerifyService.DefaultCliRunner();
                refusal = runner.Run(shim, "open http://localhost/fake/panel%path%.aspx", 5000);

                // A refusal, not a throw and not a spawn.
                Assert.True(refusal.ArgumentRefused);
                Assert.Equal(-1, refusal.ExitCode);
                Assert.False(refusal.TimedOut);
                Assert.Contains("refused", refusal.StdErr, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(shim, refusal.StdErr, StringComparison.OrdinalIgnoreCase);
                // The rejected value is not echoed back into the response.
                Assert.DoesNotContain("%path%", refusal.StdErr, StringComparison.OrdinalIgnoreCase);
                Assert.False(File.Exists(sentinel), "a refused argument must not reach cmd.exe");
            }
            finally
            {
                Delete(shim);
                Delete(sentinel);
            }

            // Same refusal, carried through the service. An object name is
            // caller-supplied and lands in the url unsanitized - SanitizeSegment
            // applies to the baseline directory, not to the url - so this is the
            // value that reaches the driver.
            var recorder = new RecordingRunner
            {
                WhichResult = "C:/fake/chrome-devtools-axi.cmd",
                Answer = refusal
            };
            string kb = FreshKb();
            var svc = new VisualVerifyService(
                recorder,
                () => "Panel%username%Off",
                () => kb,
                VisualVerifyService.DefaultAspxFor,
                "http://localhost/fake");

            var result = svc.Verify("Panel%username%Off", "WebForm");

            Assert.True(result.Skipped);
            Assert.Equal(VisualVerifyService.ArgumentRefusedReason, result.SkipReason);
            Assert.NotNull(result.Error);
            Assert.Contains("refused", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("%username%", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Null(result.ScreenshotPath);
            // Only the open step was attempted: the refusal short-circuits rather
            // than falling through to a screenshot call with the same bad value.
            Assert.Single(recorder.Calls);
            Assert.Contains("%username%", recorder.Calls[0], StringComparison.OrdinalIgnoreCase);

            var envelope = svc.VerifyAsJObject("Panel%username%Off", "WebForm");
            Assert.True((bool)envelope["skipped"]);
            Assert.Equal(VisualVerifyService.ArgumentRefusedReason, (string)envelope["reason"]);
        }

        [Fact]
        public void AWellFormedUrlAndPathProduceTheCommandShapePreviewServiceProduces()
        {
            var recorder = new RecordingRunner { WhichResult = "C:/fake/chrome-devtools-axi.cmd" };
            var svc = new VisualVerifyService(
                recorder,
                () => "AcademicoHomolog1",
                () => FreshKb(),
                VisualVerifyService.DefaultAspxFor,
                "http://localhost/fake");

            var result = svc.Verify("AcademicoHomolog1", "WebForm");

            Assert.False(result.Skipped, result.Error);
            string url = "http://localhost/fake/academicohomolog1.aspx";
            string outPath = result.ScreenshotPath;

            // What the service hands the runner is discrete logical arguments
            // joined once, and it has to re-tokenize losslessly - otherwise the
            // builder is handed something other than what was asked for, which is
            // what the old quoter did to every Windows path by doubling its
            // backslashes.
            Assert.Equal(new[] { "open", url }, DefaultBrowserDriverInvoker.ParseLegacyArguments(recorder.Calls[0]));
            Assert.Equal(new[] { "screenshot", outPath }, DefaultBrowserDriverInvoker.ParseLegacyArguments(recorder.Calls[1]));
            Assert.DoesNotContain("\\\\", recorder.Calls[1]);

            // And the command the runner builds from them is the shared builder's,
            // byte for byte - which is the command PreviewService builds.
            string shim = Path.Combine(Path.GetTempPath(), "gx-visualverify-" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(shim, "@echo off\r\necho [%*]\r\nexit /b 0\r\n");
            try
            {
                Assert.Equal(
                    "/d /s /c \"\"" + shim + "\" \"screenshot\" \"" + outPath + "\"\"",
                    BrowserDriverProcess.BuildShimArguments(shim, new[] { "screenshot", outPath }));

                var fromVisualVerify = new VisualVerifyService.DefaultCliRunner().Run(shim, recorder.Calls[1], 20000);
                var fromPreview = new PreviewService.DefaultCliRunner().Run(shim, recorder.Calls[1], 20000);
                Assert.Equal(0, fromVisualVerify.ExitCode);
                Assert.Equal(0, fromPreview.ExitCode);
                Assert.Equal(fromPreview.StdOut, fromVisualVerify.StdOut);
                Assert.Equal("[\"screenshot\" \"" + outPath + "\"]", (fromVisualVerify.StdOut ?? "").Trim());
            }
            finally
            {
                Delete(shim);
            }
        }

        // ---- seams -------------------------------------------------------

        /// <summary>
        /// Records what the service asked the driver to do, writes the PNG a
        /// screenshot step implies, and answers with a fixed <see cref="Answer"/>
        /// when one is set.
        /// </summary>
        private class RecordingRunner : VisualVerifyService.ICliRunner
        {
            public readonly List<string> Calls = new List<string>();
            public string WhichResult;
            public VisualVerifyService.CliResult Answer;

            public VisualVerifyService.CliResult Run(string fileName, string arguments, int timeoutMs)
            {
                Calls.Add(arguments);
                if (Answer != null) return Answer;
                if (arguments != null && arguments.StartsWith("screenshot ", StringComparison.Ordinal))
                {
                    string outPath = arguments.Substring("screenshot ".Length).Trim('"');
                    using (var bmp = new Bitmap(2, 2, PixelFormat.Format32bppArgb))
                        bmp.Save(outPath, ImageFormat.Png);
                }
                return new VisualVerifyService.CliResult { ExitCode = 0 };
            }

            public string Which(string command) => WhichResult;
        }

        // ---- helpers -----------------------------------------------------

        private static void Delete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        private static string FreshKb()
        {
            string path = Path.Combine(Path.GetTempPath(), "VisualVerifyHardening_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}