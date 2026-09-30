using System;
using System.IO;
using System.Linq;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The source-shape helpers the guards are built from, defined once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because of what happened when they were not. The comment-stripper
    /// had seventeen private copies, and one of them had diverged enough to delete a
    /// third of a production file - while every test using it still passed, because an
    /// assertion that code is <em>present</em> answers yes automatically once the
    /// stripper has removed it. Nothing about that failure looks like a test problem.
    /// </para>
    ///
    /// <para>
    /// A copied helper is a helper that can drift, and drift is invisible in both
    /// directions at once: a stricter copy fails tests that should pass, and a looser
    /// one passes tests that should fail. The second is the dangerous one.
    /// </para>
    /// </remarks>
    public class SourceAssertSingleSourceTests
    {
        /// <summary>Both test projects, which share the linked helper files.</summary>
        private static string[][] TestProjectDirectories()
        {
            return new[]
            {
                new[] { "src", "GxMcp.Worker.Tests" },
                new[] { "src", "GxMcp.Gateway.Tests" },
            };
        }

        /// <summary>Every test source file, by full path.</summary>
        private static string[] AllTestSources()
        {
            return TestProjectDirectories()
                .SelectMany(d => Directory.GetFiles(RepoSource.DirectoryOf(d), "*.cs", SearchOption.TopDirectoryOnly))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>Whether a path is the file declaring the given test class.</summary>
        private static bool SameFile(string path, Type declaring)
        {
            return string.Equals(Path.GetFileNameWithoutExtension(path),
                declaring.Name, StringComparison.Ordinal);
        }

        [Fact]
        public void TheThreeHelpersAreDefinedOnceAndInTheSharedFile()
        {
            string shared = RepoSource.Read("src", "TestSupport", "SourceAssert.cs");

            Assert.Equal(1, SourceAssert.Count(shared, "internal static int Count(string haystack, string needle)"));
            Assert.Equal(1, SourceAssert.Count(shared, "internal static string MethodBody(string source, string declaration)"));
            Assert.Equal(1, SourceAssert.Count(shared, "internal static string NormaliseNewlines(string source)"));

            // And it is linked into both test projects, so both get it rather than each
            // carrying its own - which is what a linked file is for.
            foreach (string project in new[] { "GxMcp.Worker.Tests", "GxMcp.Gateway.Tests" })
            {
                string csproj = RepoSource.Read("src", project, project + ".csproj");
                Assert.True(SourceAssert.Count(csproj, @"..\TestSupport\SourceAssert.cs") == 1, project);
            }
        }

        [Fact]
        public void NoTestFileDefinesItsOwnCopy()
        {
            // This file is excluded because it necessarily contains the very
            // declarations it searches for - it names them in its assertions. A guard
            // that counts mentions of a symbol and also mentions the symbol finds
            // itself, which is the same self-defeating shape as a reference count that
            // a test's own mention silences.
            var redeclared = AllTestSources()
                .Where(f => !SameFile(f, typeof(SourceAssertSingleSourceTests)))
                .Select(f => new
                {
                    File = Path.GetFileName(f),
                    Text = RepoSource.WithoutComments(File.ReadAllText(f)),
                })
                .Where(x =>
                    x.Text.Contains("private static int Count(") ||
                    x.Text.Contains("private static string NormaliseNewlines("))
                .Select(x => x.File)
                .ToArray();

            // A copy of MethodBody is allowed in exactly one place, and
            // AOneMethodBodyIsDeliberatelyDifferent explains why.
            Assert.Empty(redeclared);

            var copies = AllTestSources()
                .Where(f => !SameFile(f, typeof(SourceAssertSingleSourceTests)))
                .Where(f => RepoSource.WithoutComments(File.ReadAllText(f))
                    .Contains("private static string MethodBody("))
                .Select(f => Path.GetFileName(f))
                .ToArray();

            Assert.Single(copies);
            Assert.Equal("TeamDevBaselineGuardTests.cs", copies[0]);
        }

        /// <summary>
        /// The one <c>MethodBody</c> left in a test class is a different function, and
        /// is left alone on purpose.
        /// </summary>
        /// <remarks>
        /// It locates a method by <em>name</em> using a regex over any accessibility,
        /// and returns the body with its braces <em>removed</em>. The shared one takes a
        /// declaration prefix - so an overload or a return type is part of what gets
        /// pinned - and returns the member including its braces. Swapping one for the
        /// other would silently change what its assertions read: the braces would either
        /// appear in the text being searched for a pattern, or the name lookup would
        /// start matching a member the test did not mean.
        ///
        /// So it stays, and this pins both halves of the difference rather than leaving
        /// it as something to be tidied away later.
        /// </remarks>
        [Fact]
        public void AOneMethodBodyIsDeliberatelyDifferent()
        {
            string source = RepoSource.WithoutComments(
                RepoSource.Read("src", "GxMcp.Worker.Tests", "TeamDevBaselineGuardTests.cs"));

            // Located by name through a regex, not by a declaration prefix.
            Assert.Equal(1, SourceAssert.Count(source, "Regex.Match("));
            Assert.Equal(1, SourceAssert.Count(source, "string methodName)"));

            // And the body comes back without its braces.
            Assert.Equal(1, SourceAssert.Count(source, "source.Substring(openBrace + 1, i - openBrace - 1)"));

            // The shared one is the opposite on both counts, so the two cannot be
            // interchanged even by accident.
            string shared = RepoSource.Read("src", "TestSupport", "SourceAssert.cs");
            Assert.Equal(0, SourceAssert.Count(shared, "Regex.Match("));
            Assert.Equal(1, SourceAssert.Count(shared, "i - at + 1"));
        }

        [Fact]
        public void CountIsOrdinalAndDoesNotCountOverlaps()
        {
            Assert.Equal(0, SourceAssert.Count("abc", "z"));
            Assert.Equal(1, SourceAssert.Count("abc", "abc"));

            // "aaaa" holds "aa" twice, not three times: the search resumes after each
            // hit, so occurrences never overlap. Counting the overlapping third would
            // turn a duplicated shape into a report of three copies when there are two.
            Assert.Equal(2, SourceAssert.Count("aaaa", "aa"));
            Assert.Equal(2, SourceAssert.Count("aaaaaaa", "aaa"));

            // Case-sensitive: the guards use this to assert a specific spelling exists,
            // so a case-insensitive count would pass on the wrong one.
            Assert.Equal(0, SourceAssert.Count("StaleObject", "staleobject"));
            Assert.Equal(1, SourceAssert.Count("StaleObject", "StaleObject"));

            // An empty needle is refused rather than counted. The search advances by the
            // needle's length, so an empty one never moves - the loop spins forever.
            // All twenty-four copies this replaced had that, and none was ever called
            // that way, which is how it stayed hidden in twenty test classes.
            Exception empty = null;
            try { SourceAssert.Count("abcd", ""); }
            catch (Exception ex) { empty = ex; }
            Assert.True(empty != null, "an empty needle was counted instead of refused");

            Assert.Equal(0, SourceAssert.Count("", "a"));
            Assert.Equal(0, SourceAssert.Count("abc", "d"));
        }

        /// <summary>
        /// The body extractor balances braces and skips string literals, so a body
        /// containing a lambda, a nested type or a brace inside a string is still
        /// delimited correctly.
        /// </summary>
        /// <remarks>
        /// The string case is the one that matters, and it is a bug rather than a
        /// refinement. <c>var t = "{";</c> holds an unmatched brace, so a naive scan
        /// never returns to depth zero and the extracted "body" runs on through every
        /// member after it - turning an assertion about the body into one about the
        /// rest of the file. All thirteen copies this replaced had that behaviour; none
        /// of their tests contained a brace inside a string, so none noticed.
        /// </remarks>
        [Fact]
        public void MethodBodyBraceBalancesThroughStringsLambdasAndNestedTypes()
        {
            string source = string.Join("\n",
                "class C",
                "{",
                "    void A() { var s = \"}}}\"; }",
                "    void B() { Func<int,int> f = x => x + 1; }",
                "    class Nested { void D() { } }",
                "    void Target(int x)",
                "    {",
                "        if (x > 0) { x--; }",
                "        var t = \"{\";",
                "    }",
                "    void After() { }",
                "}");

            string body = SourceAssert.MethodBody(source, "void Target(int x)");

            Assert.Contains("if (x > 0) { x--; }", body);
            Assert.Contains("var t = \"{\";", body);
            Assert.DoesNotContain("void After()", body);

            // Ends at the member's own closing brace, inclusive.
            Assert.EndsWith("}", body.TrimEnd());
        }

        [Fact]
        public void MethodBodyFailsLoudlyOnAMissingDeclaration()
        {
            // An absent declaration has to be a failure with the declaration named, not
            // a null that the caller's own assertion then reports as something else.
            Exception failure = null;
            try
            {
                SourceAssert.MethodBody("class C { }", "void Missing()");
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            Assert.True(failure != null, "a missing declaration returned instead of failing");
            Assert.Contains("void Missing()", failure.Message);
        }

        [Fact]
        public void NormaliseNewlinesMakesOneSpellingMatchEitherLineEnding()
        {
            // The repository is CRLF, so a needle written with \n would not match a file
            // containing \r\n at all - and the assertion would then be reporting on the
            // checkout rather than on the code.
            string lf = SourceAssert.NormaliseNewlines("a\r\nb");
            string crlf = SourceAssert.NormaliseNewlines("a\nb");

            Assert.Equal("a\nb", lf);
            Assert.Equal("a\nb", crlf);
            Assert.Equal(1, SourceAssert.Count(lf, "a\nb"));
            Assert.Equal(1, SourceAssert.Count(crlf, "a\nb"));

            // A lone \r is left alone: it is not a line ending this repository uses, and
            // rewriting it would change text that is not a line break.
            Assert.Equal("a\rb", SourceAssert.NormaliseNewlines("a\rb"));
        }
    }
}