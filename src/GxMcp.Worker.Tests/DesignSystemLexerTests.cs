using System.IO;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The CSS lexer in DesignSystemSourceParser existed twice — once in
    /// ValidateBalance, once in FindMatchingBrace — differing only in what they did
    /// with each structural character. They now share one ScanCode.
    ///
    /// The failure this guards against is silent: a brace inside a comment or a
    /// quoted selector shifts the reported nesting depth, which surfaces as a
    /// spurious "unbalanced" diagnostic or, worse, a block boundary found in the
    /// wrong place so one rule body is parsed as two. Neither reads as a lexer bug
    /// at the call site, which is why the lexer itself is pinned here.
    /// </summary>
    public class DesignSystemLexerTests
    {
        // Parse() is the entry point that runs ValidateBalance, so the balance
        // diagnostics are only observable through it.
        private static DesignSystemSourceParseResult Parse(string source) =>
            DesignSystemSourceParser.Parse(source, null);

        private static System.Collections.Generic.Dictionary<string, Newtonsoft.Json.Linq.JObject>
            ParseTokenGroups(string source) => DesignSystemSourceParser.ParseTokens(source);

        [Theory]
        // Balanced in the presence of comments and strings.
        [InlineData(":root { --a: 1px; }")]
        [InlineData(":root { /* } not a close */ --a: 1px; }")]
        [InlineData(":root { // } not a close\n --a: 1px; }")]
        [InlineData(":root { --a: \"}\"; }")]
        [InlineData(":root { --a: '}'; }")]
        // Nested rules.
        [InlineData("@media (min-width: 1px) { :root { --a: 1px; } }")]
        [InlineData(":root { --a: 1px; } :root { --b: 2px; }")]
        public void BalancedSource_ProducesNoUnbalancedDiagnostic(string source)
        {
            var result = Parse(source);

            Assert.DoesNotContain(result.Warnings, w => w.Contains("unclosed brace"));
            Assert.DoesNotContain(result.Warnings, w => w.Contains("without an opening brace"));
        }

        [Theory]
        // Genuinely unbalanced, and must still be reported.
        [InlineData(":root { --a: 1px;")]
        [InlineData(":root { --a: 1px; } }")]
        public void UnbalancedSource_StillReportsTheDiagnostic(string source)
        {
            // The consolidation must not have made the checker lenient: these are
            // the cases a broken lexer would stop catching.
            var result = Parse(source);

            Assert.Contains(result.Warnings,
                w => w.Contains("unclosed brace") || w.Contains("without an opening brace"));
        }

        // Token groups are matched by '#name {' (TokenGroupStartRegex), so the
        // fixtures below use that form.
        private const string GroupOpen = "#theme {\n";

        [Fact]
        public void ABraceInsideACommentDoesNotShiftTheBlockBoundary()
        {
            // The concrete failure: a rule body containing a comment with an
            // unbalanced brace in it. A lexer that counted comment braces would end
            // the block early and stop seeing the real tokens that follow.
            var groups = ParseTokenGroups(
                GroupOpen +
                "  /* a } brace in a comment */\n" +
                "  --gutter: 8px;\n" +
                "  --accent: #0066cc;\n" +
                "}\n");

            var group = groups["theme"];
            Assert.NotNull(group);
            Assert.Equal("8px", group["--gutter"]?.ToString());
            Assert.Equal("#0066cc", group["--accent"]?.ToString());
        }

        [Fact]
        public void ABraceInsideAQuotedValueDoesNotShiftTheBlockBoundary()
        {
            // Same failure, via a quoted value rather than a comment.
            var groups = ParseTokenGroups(
                GroupOpen +
                "  --content: \"a } b\";\n" +
                "  --gutter: 4px;\n" +
                "}\n");

            var group = groups["theme"];
            Assert.NotNull(group);
            Assert.Equal("4px", group["--gutter"]?.ToString());
        }

        [Fact]
        public void AnEscapedQuoteDoesNotEndTheStringEarly()
        {
            // A lexer that did not track escapes would see the backslash-quote as
            // the closing quote, treat the rest as code, and mis-count the braces
            // that follow.
            var groups = ParseTokenGroups(
                GroupOpen +
                "  --content: \"a \\\" } b\";\n" +
                "  --gutter: 12px;\n" +
                "}\n");

            var group = groups["theme"];
            Assert.NotNull(group);
            Assert.Equal("12px", group["--gutter"]?.ToString());
        }

        [Fact]
        public void TheTwoWalkers_ShareOneLexer()
        {
            // Source-level guard: the structural lexer must exist once. A second
            // copy is how the string rules start disagreeing again.
            string src = RepoSource.Read("src", "GxMcp.Worker", "Helpers", "DesignSystemSourceParser.cs");

            // The quote-escape rule and the comment-entry rules appear once each.
            Assert.Equal(1, CountOccurrences(src, "if (escaped) { escaped = false; continue; }"));
            Assert.Equal(1, CountOccurrences(src,
                "if (!inSingle && !inDouble && c == '/' && next == '/') { inLineComment = true; i++; continue; }"));
            Assert.Equal(1, CountOccurrences(src,
                "if (!inSingle && !inDouble && c == '/' && next == '*') { inBlockComment = true; i++; continue; }"));
            // Both consumers go through it.
            Assert.Contains("ScanCode(source, 0, source.Length,", src);
            Assert.Contains("ScanCode(source, openBrace, source.Length,", src);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, index = 0;
            while ((index = haystack.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

    }
}
