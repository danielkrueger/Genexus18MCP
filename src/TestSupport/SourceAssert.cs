using System;
using Xunit;

namespace GxMcp.TestSupport
{
    /// <summary>
    /// The source-shape helpers the guards are built from, in one place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each of these was a private copy in two dozen test classes. That is more than a
    /// tidiness complaint, because a copied helper is a helper that can diverge: the
    /// comment-stripper in this file's sibling <c>RepoSource</c> had seventeen copies,
    /// and one of them had quietly started behaving differently - which is how a third
    /// of a source file ended up being deleted by a test helper while every test using
    /// it still passed. A single implementation cannot drift.
    /// </para>
    ///
    /// <para>
    /// Deliberately not here: anything that reads production <em>behaviour</em>. These
    /// only look at source text, so a failure means a shape changed and never means the
    /// code stopped working. A guard that cannot tell those apart is the wrong shape
    /// for a test to have.
    /// </para>
    /// </remarks>
    internal static class SourceAssert
    {
        /// <summary>
        /// How many times <paramref name="needle"/> occurs in <paramref name="haystack"/>,
        /// compared ordinally and without overlap.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Non-overlapping on purpose. It is a count of occurrences, not of positions:
        /// for the shapes these guards check, two occurrences in a row would mean the
        /// shape was duplicated, and counting them as three would hide that.
        /// </para>
        ///
        /// <para>
        /// An empty needle throws rather than returning a number. The search advances by
        /// the needle's length, so an empty one never moves - and the loop that the
        /// twenty-four previous copies all shared would spin forever. None of them was
        /// ever called that way, which is exactly why it survived; a helper used by
        /// twenty test classes is exactly where that kind of thing stays hidden. Throwing
        /// says the assertion was built wrong, where a count of <c>haystack.Length + 1</c>
        /// would read like a finding about the code.
        /// </para>
        /// </remarks>
        internal static int Count(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(needle))
            {
                throw new ArgumentException("counting needs a non-empty needle", "needle");
            }

            int count = 0, index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        /// <summary>
        /// The text of one member - method, property or nested type - from its
        /// declaration through its closing brace.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Takes the declaration as a literal prefix rather than a name, because the
        /// callers need to pin the signature - an overload, a return type or an
        /// accessibility modifier is part of what is being asserted. A name-only lookup
        /// would silently accept the wrong one of several.
        /// </para>
        /// <para>
        /// Braces are balanced rather than found by scanning for a terminator, so a
        /// body containing a nested type or a lambda does not end the search early.
        /// </para>
        ///
        /// <para>
        /// String and character literals are skipped while counting. This is not a
        /// refinement - a body containing <c>var t = "{";</c> has an unmatched brace in
        /// it, and a naive scan never returns to depth zero, so the extracted "body"
        /// runs on into the members that follow. The thirteen private copies this
        /// replaced all had that bug; none of their tests happened to contain a brace
        /// inside a string, so none of them noticed.
        /// </para>
        ///
        /// <para>
        /// Callers should pass source with comments already blanked, since a brace in a
        /// comment would be counted.
        /// </para>
        /// </remarks>
        /// <param name="source">The file's text, with comments already blanked.</param>
        /// <param name="declaration">
        /// A literal prefix unique to the member - typically its signature up to the
        /// opening brace.
        /// </param>
        internal static string MethodBody(string source, string declaration)
        {
            int at = source.IndexOf(declaration, StringComparison.Ordinal);
            Assert.True(at >= 0, "declaration not found: " + declaration);

            int depth = 0;
            bool sawBrace = false;

            for (int i = source.IndexOf('{', at); i < source.Length; i++)
            {
                char c = source[i];

                // A literal is skipped whole: its contents are not code, so a brace in
                // one must not affect the nesting.
                if (c == '"' || c == '\'')
                {
                    bool verbatim = c == '"' && i > 0 && source[i - 1] == '@';
                    i = SkipLiteral(source, i, c, verbatim);
                    continue;
                }

                if (c == '{') { depth++; sawBrace = true; }
                else if (c == '}')
                {
                    depth--;
                    if (sawBrace && depth == 0) return source.Substring(at, i - at + 1);
                }
            }

            // Only reachable from a test-authoring mistake - an unbalanced file, or a
            // declaration that matched something other than a member.
            throw new InvalidOperationException("unbalanced member: " + declaration);
        }

        /// <summary>
        /// The index just past a string or character literal starting at
        /// <paramref name="at"/>.
        /// </summary>
        private static int SkipLiteral(string source, int at, char quote, bool verbatim)
        {
            int i = at + 1;
            while (i < source.Length)
            {
                char c = source[i];

                if (verbatim)
                {
                    if (c == quote)
                    {
                        // A doubled quote is an escaped one, not the end.
                        if (i + 1 < source.Length && source[i + 1] == quote) { i += 2; continue; }
                        return i + 1;
                    }
                }
                else
                {
                    if (c == '\\' && i + 1 < source.Length) { i += 2; continue; }
                    if (c == quote) return i + 1;
                }

                // An unterminated literal ends at the newline, so a stray quote in the
                // text cannot swallow the rest of the file.
                if (c == '\n') return i;
                i++;
            }
            return i;
        }

        /// <summary>
        /// Newlines reduced to <c>\n</c>, so a needle written with one line ending
        /// matches whichever the file happens to use.
        /// </summary>
        /// <remarks>
        /// Needed because the repository is CRLF and a needle containing <c>\n</c> will
        /// not match one containing <c>\r\n</c> at all - the assertion then fails on a
        /// checkout's line endings rather than on anything about the code.
        /// </remarks>
        internal static string NormaliseNewlines(string source)
        {
            return source.Replace("\r\n", "\n");
        }
    }
}