using System;
using System.IO;
using System.Linq;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Source-shape assertions strip comments before counting, so the stripping has to
    /// be incapable of deleting code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It was not. The previous implementation used <c>/\*.*?\*/</c> with
    /// Singleline, which assumes every <c>/*</c> opens a comment. A source file with
    /// <c>/*</c> inside a string literal breaks that, and the match runs to the next
    /// <c>*/</c> anywhere in the file. Measured on <c>ObjectService.cs</c>: 298,344
    /// characters in, 199,096 out - a third of the file, including a call site added
    /// minutes earlier.
    /// </para>
    ///
    /// <para>
    /// That is the dangerous direction, and it is worth being precise about why. The
    /// assertions these helpers feed ask "is this code present?" and "does this code
    /// appear once?". A stripper that deletes code turns the first into an automatic
    /// yes and leaves the second answering a question about whatever survived. Nothing
    /// fails. A guard built on it is not weak - it is silently wrong, and it looks like
    /// it is covering the change.
    /// </para>
    ///
    /// <para>
    /// So these check the property that matters: the output is the input with comment
    /// characters replaced by spaces and nothing else moved or removed. Length
    /// preserved, line count preserved, code preserved, prose gone. A regex-based
    /// version satisfies none of the first three on the file that broke it.
    /// </para>
    /// </remarks>
    public class RepoSourceCommentStrippingTests
    {
        /// <summary>
        /// The file whose shape broke the old stripper. Named rather than picked,
        /// because the whole point is that this specific file contains the hazard.
        /// </summary>
        private static string[] HazardFile => new[] { "src", "GxMcp.Worker", "Services", "ObjectService.cs" };

        [Fact]
        public void StrippingChangesNoLengthAndNoLineCount()
        {
            string raw = RepoSource.Read(HazardFile);
            string stripped = RepoSource.WithoutComments(raw);

            // Length first, because it is the strongest single statement: if anything
            // were removed rather than blanked, this would differ.
            Assert.True(raw.Length == stripped.Length,
                "length changed: " + raw.Length + " -> " + stripped.Length);

            Assert.True(raw.Count(c => c == '\n') == stripped.Count(c => c == '\n'),
                "line count changed");
        }

        [Fact]
        public void CodeSurvivesAndProseDoesNot()
        {
            string raw = RepoSource.Read(HazardFile);
            string stripped = RepoSource.WithoutComments(raw);

            // Real code, near the start and deep in the file, both still there.
            Assert.Contains("class ObjectService", stripped);
            Assert.Contains("TryPromoteCompleteSourceRead", stripped);
            Assert.Contains("AddReadPart", stripped);

            // And the file's prose is gone. Checked as "no line begins with a comment
            // marker", not "the text // does not appear anywhere": this file has
            // `// ` inside string literals and in regexes, and those are content
            // the scanner is right to preserve. Asserting the text is absent would
            // be asserting that literals get mangled.
            Assert.DoesNotContain("///", stripped);
            Assert.True(
                !stripped.Split('\n').Any(line => line.TrimStart().StartsWith("//", StringComparison.Ordinal)),
                "a line comment survived");
        }

        /// <summary>
        /// Offsets into the stripped text still address the same code, because
        /// characters are blanked in place rather than removed.
        /// </summary>
        [Fact]
        public void OffsetsAreUnchangedSoPositionBasedHelpersStillWork()
        {
            string raw = RepoSource.Read(HazardFile);
            string stripped = RepoSource.WithoutComments(raw);

            // The same needle at the same index in both. Several of the guards that use
            // this compare positions, so a shift would silently change their subject.
            foreach (string needle in new[] { "class ObjectService", "TryPromoteCompleteSourceRead" })
            {
                int inRaw = raw.IndexOf(needle, StringComparison.Ordinal);
                int inStripped = stripped.IndexOf(needle, StringComparison.Ordinal);

                Assert.True(inRaw >= 0, "needle not in raw: " + needle);
                Assert.True(inRaw == inStripped, needle + " moved from " + inRaw + " to " + inStripped);
            }
        }

        /// <summary>
        /// A comment marker inside a string literal is not a comment.
        /// </summary>
        /// <remarks>
        /// This is the exact hazard, tested directly rather than only through the file
        /// that happens to contain it. A literal is copied through untouched, so the
        /// `/*` inside it does not start a block comment and the string survives whole.
        /// </remarks>
        [Theory]
        [InlineData("var a = \"/*\"; // real comment")]
        [InlineData("var a = \"/* not a comment */\";")]
        [InlineData("var a = \"// neither is this\";")]
        [InlineData("var a = \"unterminated /* inside a literal\";")]
        public void CommentMarkersInsideStringLiteralsAreNotComments(string line)
        {
            string stripped = RepoSource.WithoutComments(line);

            // The literal is intact, comment and all.
            int openQuote = line.IndexOf('"');
            int closeQuote = line.LastIndexOf('"');
            string literal = line.Substring(openQuote, closeQuote - openQuote + 1);
            Assert.Contains(literal, stripped);

            // The real comment after it is not.
            if (line.Contains("// real comment"))
            {
                Assert.DoesNotContain("real comment", stripped);
            }

            // Nothing was removed.
            Assert.True(line.Length == stripped.Length);
        }

        /// <summary>
        /// Verbatim and interpolated strings are handled as literals.
        /// </summary>
        /// <remarks>
        /// A verbatim string escapes a quote by doubling it and ends at a newline, not
        /// at the next backslash - so a naive scanner loses its place and starts
        /// treating the code after the string as a comment.
        /// </remarks>
        [Theory]
        [InlineData("var a = @\"c:\\temp\"; // gone")]
        [InlineData("var a = @\"has \"\"quotes\"\" in it\"; // gone")]
        [InlineData("var a = $\"{x}// not a comment\"; // gone")]
        [InlineData("var a = @$\"{x}// nor this\"; // gone")]
        public void VerbatimAndInterpolatedStringsDoNotDerailIt(string line)
        {
            string stripped = RepoSource.WithoutComments(line);

            Assert.True(line.Length == stripped.Length);
            Assert.Equal(line.Split('\n').Length, stripped.Split('\n').Length);

            // The declaration up to the semicolon is untouched; only the comment after
            // it is blanked.
            Assert.Contains("var a = ", stripped);
            if (line.Contains("// gone"))
            {
                Assert.DoesNotContain("gone", stripped);
            }
        }

        [Fact]
        public void BlockCommentsAreBlankedIncludingTheirNewlines()
        {
            // Newlines inside a block comment are kept, so the line count does not
            // change and a failure still points at the right line.
            string source = "a\r\n/* one\r\n   two\r\n   three */\r\nb";
            string stripped = RepoSource.WithoutComments(source);

            Assert.True(source.Length == stripped.Length);

            // "a", the comment's three lines, and "b" - four line breaks in total.
            Assert.Equal(4, stripped.Count(c => c == '\n'));
            Assert.DoesNotContain("two", stripped);
            Assert.Contains("a\r\n", stripped);
            Assert.Contains("b", stripped);
        }

        /// <summary>
        /// The whole production tree still strips cleanly: every file comes back the
        /// same length as it went in.
        /// </summary>
        /// <remarks>
        /// Run over everything rather than the one known-bad file, because the hazard is
        /// not specific to it - any file with a comment marker inside a literal has it,
        /// and the point is that none of them can be silently shortened now.
        /// </remarks>
        [Fact]
        public void NoProductionFileIsShortenedByStripping()
        {
            var files = RepoSource.CsFilesIn("src", "GxMcp.Worker", "Services")
                .Concat(RepoSource.CsFilesIn("src", "GxMcp.Worker", "Helpers"))
                .Concat(RepoSource.CsFilesIn("src", "GxMcp.Worker", "Models"))
                .Concat(RepoSource.CsFilesIn("src", "GxMcp.Worker", "Compatibility"))
                .ToArray();

            Assert.True(files.Length > 100, "expected the production tree, found " + files.Length);

            var shortened = files
                .Select(f => new { File = Path.GetFileName(f), Raw = File.ReadAllText(f) })
                .Select(x => new { x.File, Raw = x.Raw.Length, Stripped = RepoSource.WithoutComments(x.Raw).Length })
                .Where(x => x.Raw != x.Stripped)
                .Select(x => x.File + " " + x.Raw + "->" + x.Stripped)
                .ToArray();

            Assert.True(shortened.Length == 0, "shortened: " + string.Join(", ", shortened));
        }
    }
}