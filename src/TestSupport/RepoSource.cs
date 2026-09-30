using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GxMcp.TestSupport
{
    /// <summary>
    /// Locates files in this repository from a test assembly's output directory.
    ///
    /// It existed 34 times across the test projects - as <c>FindWorkerFile</c>,
    /// <c>WorkerService</c>, <c>RepoFile</c>, <c>ReadRepoFile</c>,
    /// <c>FindRepoFile</c>, <c>GatewayFile</c> and half a dozen other spellings -
    /// each walking up from the assembly's base directory looking for a file under
    /// <c>src/</c>.
    ///
    /// <para><b>Read is the default name on purpose.</b> Every one of those copies
    /// returned a PATH, so reading a source file meant writing
    /// <c>File.ReadAllText(Helper(...))</c> and forgetting the wrapper silently
    /// turned an assertion into a search through a filename - which is green, and
    /// tests nothing. Two guards were written that way before it was caught. A
    /// helper named <c>Read</c> that returns the contents removes the mistake
    /// rather than documenting it.</para>
    ///
    /// <para>Linked into both test projects as one source file. They target different
    /// frameworks (net48 and net10.0) and cannot reference each other, so the source
    /// is shared rather than duplicated.</para>
    /// </summary>
    internal static class RepoSource
    {
        /// <summary>
        /// The full path of a repository file, located by walking up from this
        /// assembly's output directory. Throws when it is not found.
        /// </summary>
        internal static string PathOf(params string[] segments)
        {
            var parts = new List<string>();
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

            while (dir != null)
            {
                parts.Clear();
                parts.Add(dir.FullName);
                parts.AddRange(segments);

                string candidate = Path.Combine(parts.ToArray());
                if (File.Exists(candidate)) return candidate;

                dir = dir.Parent;
            }

            throw new FileNotFoundException(
                "Could not locate " + string.Join("/", segments)
                + " walking up from " + AppDomain.CurrentDomain.BaseDirectory);
        }

        /// <summary>
        /// The contents of a repository file. This is what a source-shape assertion
        /// wants; see the note on the type.
        /// </summary>
        internal static string Read(params string[] segments)
        {
            return File.ReadAllText(PathOf(segments));
        }

        /// <summary>
        /// A repository file's contents with its comments blanked out, so a
        /// source-shape assertion counts code rather than prose.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This exists because regexes alone cannot do it safely, and the unsafe
        /// version is a trap worth spelling out. Stripping block comments with
        /// <c>/\*.*?\*/</c> assumes every <c>/*</c> opens a comment - but a source
        /// file containing <c>"/*"</c> inside a string literal breaks that, and the
        /// match then runs to the <em>next</em> <c>*/</c> anywhere in the file,
        /// deleting tens of thousands of characters of real code. That is the
        /// dangerous direction: an assertion about code that <em>should</em> be present
        /// passes because the stripper deleted the code it was looking for, and
        /// nothing fails. It was measured doing exactly that - a third of one service
        /// file, including a call site added moments earlier.
        /// </para>
        ///
        /// <para>
        /// So this is a scanner rather than a pattern. It tracks the four states that
        /// actually matter - code, line comment, block comment, and string or char
        /// literal in its regular, verbatim and interpolated forms - and blanks
        /// characters only while genuinely inside a comment. Newlines are preserved
        /// outside comments so line-based helpers keep working.
        /// </para>
        ///
        /// <para>
        /// Comments are replaced with spaces rather than removed, so offsets and line
        /// numbers of the surviving code do not move. That matters because these
        /// callers compare positions.
        /// </para>
        /// </remarks>
        internal static string WithoutComments(params string[] segments)
        {
            return WithoutComments(Read(segments));
        }

        /// <summary>
        /// The same blanking, over text the caller already has.
        /// </summary>
        internal static string WithoutComments(string source)
        {
            var output = new System.Text.StringBuilder(source.Length);
            int i = 0;

            while (i < source.Length)
            {
                char c = source[i];
                char next = i + 1 < source.Length ? source[i + 1] : '\0';

                // A comment marker outside any literal.
                if (c == '/' && next == '/')
                {
                    while (i < source.Length && source[i] != '\n') { output.Append(' '); i++; }
                    continue;
                }
                if (c == '/' && next == '*')
                {
                    output.Append("  ");
                    i += 2;
                    while (i < source.Length)
                    {
                        if (source[i] == '\n') { output.Append('\n'); i++; continue; }
                        if (source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/')
                        {
                            output.Append("  ");
                            i += 2;
                            break;
                        }
                        output.Append(' ');
                        i++;
                    }
                    continue;
                }

                // A string or char literal: copy it through untouched, so a comment
                // marker inside one is not mistaken for a comment. Verbatim strings
                // double their quote instead of escaping it, and run to the next
                // newline; a regular one honours backslash escapes. An interpolated
                // literal needs no case of its own - the `$` is just a character
                // before the quote.
                if (c == '"' || c == '\'')
                {
                    bool verbatim = c == '"' && i > 0 && source[i - 1] == '@';
                    output.Append(c);
                    i++;

                    while (i < source.Length)
                    {
                        char s = source[i];

                        if (verbatim)
                        {
                            if (s == '"')
                            {
                                // "" is an escaped quote inside a verbatim string.
                                if (i + 1 < source.Length && source[i + 1] == '"')
                                {
                                    output.Append('"').Append('"');
                                    i += 2;
                                    continue;
                                }
                                output.Append('"');
                                i++;
                                break;
                            }
                            if (s == '\n') break;
                        }
                        else
                        {
                            if (s == '\\' && i + 1 < source.Length)
                            {
                                output.Append(s).Append(source[i + 1]);
                                i += 2;
                                continue;
                            }
                            if (s == c) { output.Append(s); i++; break; }
                            if (s == '\n') break;
                        }
                        output.Append(s);
                        i++;
                    }
                    continue;
                }

                output.Append(c);
                i++;
            }

            return output.ToString();
        }

        /// <summary>
        /// The full path of a repository directory, located the same way.
        /// </summary>
        internal static string DirectoryOf(params string[] segments)
        {
            var parts = new List<string>();
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

            while (dir != null)
            {
                parts.Clear();
                parts.Add(dir.FullName);
                parts.AddRange(segments);

                string candidate = Path.Combine(parts.ToArray());
                if (Directory.Exists(candidate)) return candidate;

                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                "Could not locate directory " + string.Join("/", segments)
                + " walking up from " + AppDomain.CurrentDomain.BaseDirectory);
        }

        /// <summary>
        /// Every production <c>.cs</c> file directly inside a repository directory,
        /// ordered so a scan over them is deterministic.
        /// </summary>
        internal static string[] CsFilesIn(params string[] segments)
        {
            return Directory.GetFiles(DirectoryOf(segments), "*.cs", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();
        }
    }
}