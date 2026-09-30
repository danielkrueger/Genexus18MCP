using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace GxMcp.Worker
{
    /// <summary>
    /// How a child process ended. The launcher reports what happened; the caller
    /// decides what that means for its own contract, because the git callers do
    /// not agree - <c>TimeTravelService</c> returns -1 with a message on stderr,
    /// <c>PrDescriptionService</c> throws.
    /// </summary>
    internal sealed class ProcessOutcome
    {
        /// <summary>Exit code, or -1 when the process never ran to completion.</summary>
        public int ExitCode;

        /// <summary>Whatever the process wrote to stdout before it exited or was killed.</summary>
        public string StdOut = string.Empty;

        /// <summary>Whatever the process wrote to stderr before it exited or was killed.</summary>
        public string StdErr = string.Empty;

        /// <summary>The process outlived the timeout and was killed.</summary>
        public bool TimedOut;

        /// <summary><c>Process.Start</c> returned null, so nothing ran at all.</summary>
        public bool StartFailed;
    }

    /// <summary>
    /// Runs a child process with the capture settings the worker needs, and
    /// nothing else.
    ///
    /// These settings are not incidental. Each one was added because leaving it out
    /// broke something:
    ///
    /// <list type="bullet">
    /// <item><c>UseShellExecute = false</c> keeps the executable and its arguments
    /// off <c>cmd.exe</c>. With it true the argument string is re-parsed by the
    /// shell, so quoting that <see cref="Argv"/> got right is undone before
    /// <c>git</c> ever sees it.</item>
    /// <item>All three streams are redirected and stdin is closed, so the child
    /// cannot inherit the Gateway's MCP stdio pipe and block a prompt on it -
    /// which it would do, silently, until the timeout.</item>
    /// <item>stdout and stderr are drained by async event handlers in parallel. A
    /// sequential <c>ReadToEnd</c> then <c>ReadToEnd</c> deadlocks as soon as one
    /// pipe buffer fills while the other is still being read.</item>
    /// <item>On timeout the process is killed rather than abandoned, so it does not
    /// outlive the tool call.</item>
    /// <item>After a clean exit, <c>WaitForExit()</c> is awaited a second time with
    /// no timeout. That is the documented way to block until the async readers have
    /// finished; without it, output still buffered is silently dropped.</item>
    /// </list>
    ///
    /// The mechanics were written out four times - in <c>BlameService</c>,
    /// <c>GithubService</c>, <c>PrDescriptionService</c> and
    /// <c>TimeTravelService</c> - and the timeout path is exactly the part most
    /// likely to be lost in a copy, because it is the branch a normal run never
    /// takes. The callers keep their own executable, arguments, environment and
    /// outcome mapping; only the launch is shared.
    /// </summary>
    internal static class ProcessLauncher
    {
        /// <summary>
        /// Starts <paramref name="executable"/>, waits up to
        /// <paramref name="timeoutMs"/>, and returns what it produced.
        ///
        /// A child that runs but fails or overruns comes back as
        /// <see cref="ProcessOutcome.ExitCode"/> and
        /// <see cref="ProcessOutcome.TimedOut"/>. An executable that does not exist
        /// is different: the platform raises that before any process exists, and
        /// that exception is left to propagate rather than flattened into the
        /// outcome - a missing tool is a configuration error, and swallowing it
        /// here would report it as an ordinary non-zero exit.
        /// </summary>
        internal static ProcessOutcome Run(
            string executable,
            string arguments,
            string workingDirectory,
            int timeoutMs,
            Encoding outputEncoding = null,
            IDictionary<string, string> environment = null)
        {
            var psi = new ProcessStartInfo(executable, arguments ?? string.Empty)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // Left unset rather than defaulted: a caller that did not ask for a
            // specific encoding has been inheriting the console codepage, and
            // forcing UTF-8 here would change what it reads.
            if (outputEncoding != null)
            {
                psi.StandardOutputEncoding = outputEncoding;
                psi.StandardErrorEncoding = outputEncoding;
            }

            if (environment != null)
            {
                foreach (var pair in environment)
                    psi.EnvironmentVariables[pair.Key] = pair.Value;
            }

            var outcome = new ProcessOutcome { ExitCode = -1 };

            using (Process p = Process.Start(psi))
            {
                if (p == null)
                {
                    outcome.StartFailed = true;
                    return outcome;
                }

                try { p.StandardInput.Close(); } catch { }

                var outSb = new StringBuilder();
                var errSb = new StringBuilder();
                p.OutputDataReceived += (s, e) => { if (e.Data != null) outSb.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) errSb.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    outcome.TimedOut = true;
                    outcome.StdOut = outSb.ToString();
                    outcome.StdErr = errSb.ToString();
                    return outcome;
                }

                // Wait for the async readers to drain before reading the buffers.
                p.WaitForExit();

                outcome.ExitCode = p.ExitCode;
                outcome.StdOut = outSb.ToString();
                outcome.StdErr = errSb.ToString();
                return outcome;
            }
        }
    }
}