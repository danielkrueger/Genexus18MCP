using System.Collections.Generic;
using System.Text;

namespace GxMcp.Worker
{
    /// <summary>
    /// Builds the single command-line string a child process is started with.
    ///
    /// <c>ArgvQuote</c> lived in <c>GithubService</c> and was called from five
    /// services - <c>BlameService</c>, <c>CrossBrowserService</c>,
    /// <c>GeneratedDiffService</c>, <c>TimeTravelService</c> and
    /// <c>GithubService</c> itself - because a general primitive had no home of its
    /// own and sat in whichever service needed it first. It belongs here, next to
    /// the other worker-wide rules, and it is worth a test of its own: this is the
    /// function every <c>git</c> and <c>gh</c> argument the server builds passes
    /// through, and a quoting bug in it is an argument-injection bug.
    /// </summary>
    internal static class Argv
    {
        /// <summary>
        /// Quotes one argument so the Windows C runtime's
        /// <c>CommandLineToArgvW</c> recovers it byte-for-byte.
        ///
        /// Two rules are doing the work, and both are load-bearing:
        ///
        /// <para>A value containing a space, tab, newline, vertical tab or quote is
        /// wrapped in double quotes. Without that, <c>--message=a b</c> arrives as
        /// two arguments and <c>git</c> sees a flag the caller never sent.</para>
        ///
        /// <para>Every run of backslashes immediately before a quote is doubled.
        /// The C runtime treats backslashes as literal unless a quote follows, so
        /// 2n+1 backslashes plus a quote collapse to n backslashes plus one literal
        /// quote - meaning a value ending in a backslash, unescaped, would consume
        /// the closing quote and let the next token bleed into it. That is the
        /// classic Windows argument confusion, and it is reachable from a
        /// caller-supplied file or branch name.</para>
        /// </summary>
        internal static string Quote(string arg)
        {
            if (arg == null) arg = string.Empty;

            // Nothing the runtime would reinterpret: hand it back untouched rather
            // than adding quotes that would themselves have to be unescaped.
            if (arg.Length > 0 && arg.IndexOfAny(Problematic) < 0)
                return arg;

            var sb = new StringBuilder();
            sb.Append('"');
            for (int i = 0; i < arg.Length; i++)
            {
                int backslashes = 0;
                while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }

                if (i == arg.Length)
                {
                    // Trailing backslashes: escape them all, and let the terminating
                    // quote be appended below.
                    sb.Append('\\', backslashes * 2);
                    break;
                }

                if (arg[i] == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append(arg[i]);
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(arg[i]);
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// Joins arguments into one command-line string, quoting each.
        ///
        /// net48 has no <c>ArgumentList</c>, so every caller reached for the same
        /// four-line loop; one caller quoted its argument twice, because the loop
        /// and its own quoting were separate steps.
        /// </summary>
        internal static string Join(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            if (args == null) return string.Empty;

            foreach (string arg in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Quote(arg));
            }
            return sb.ToString();
        }

        private static readonly char[] Problematic = { ' ', '\t', '\n', '\v', '"' };
    }
}