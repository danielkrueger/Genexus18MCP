using System.Diagnostics;
using System.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Console probes used to locate a CLI. PreviewService and VisualVerifyService
    /// each carried a byte-identical <c>Which</c> and a copy of the same
    /// "run it, return the first non-blank stdout line" shape; the runner process
    /// setup, the 5 s wait and the first-non-blank-line rule are one contract, so
    /// they live here once.
    ///
    /// Does not swallow exceptions: a timeout makes <see cref="Process.ExitCode"/>
    /// throw, and each caller already decides whether that is worth a catch. No
    /// caller passes request data through here — both use fixed command text.
    /// </summary>
    internal static class ConsoleProbe
    {
        /// <summary>
        /// Runs <c>cmd.exe /c &lt;arguments&gt;</c> and returns its first non-blank
        /// stdout line, or null when the command exits non-zero or prints nothing.
        /// </summary>
        internal static string FirstOutputLine(string arguments, int timeoutMs)
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var p = Process.Start(psi))
            {
                string so = p.StandardOutput.ReadToEnd();
                p.WaitForExit(timeoutMs);
                if (p.ExitCode == 0)
                {
                    return so.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
                }
            }
            return null;
        }

        /// <summary>Absolute path of <paramref name="command"/> on PATH, or null.</summary>
        internal static string Which(string command)
        {
            return FirstOutputLine("where " + command, 5000);
        }
    }
}
