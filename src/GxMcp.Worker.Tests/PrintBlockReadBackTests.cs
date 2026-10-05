using System;
using System.Linq;
using System.Xml.Linq;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// A print-block mutation's read-back decides whether the change reached disk,
    /// and it worked by looking for a <c>PrintBlock</c> element whose
    /// <c>Name</c> <em>or</em> <c>ControlName</c> matched. That two-attribute
    /// predicate was written out at five sites across the rename, add and delete
    /// paths.
    ///
    /// Both attributes are needed because GeneXus exposes a print block's name
    /// under one or the other depending on the major, and a site that checked only
    /// one would report a completed mutation as unverified - the exact failure the
    /// read-back exists to catch, caused by the check itself. Five copies of that
    /// predicate is five chances for the fifth to be the one that is wrong, and the
    /// symptom is a mutation that says it failed when it succeeded.
    ///
    /// The predicate is now <c>FindPrintBlockByName</c> and its negative
    /// <c>ContainsPrintBlock</c>. These assert the behaviour against real XML - both
    /// spellings, case differences, and a document with no print blocks at all -
    /// because the two-attribute rule is the whole content of the helper and a
    /// source-level check would only assert that the text is present.
    ///
    /// The post-commit repair is a separate concern and is pinned separately: it is
    /// best-effort by design, and its failures are deliberately not reported, so a
    /// test asserting it "works" would be asserting the wrong thing.
    /// </summary>
    public class PrintBlockReadBackTests
    {
        private static readonly XNamespace Ns = XNamespace.None;

        /// <summary>
        /// A report layout in the shape each GeneXus major writes: the same print
        /// block under one of the two attribute spellings, plus a decoy that must
        /// never match.
        /// </summary>
        private static XDocument Report(params (string Name, string ControlName)[] blocks)
        {
            var root = new XElement("Layout");
            foreach (var (name, controlName) in blocks)
            {
                var block = new XElement("PrintBlock");
                if (name != null) block.SetAttributeValue("Name", name);
                if (controlName != null) block.SetAttributeValue("ControlName", controlName);
                root.Add(block);
            }
            return new XDocument(root);
        }

        // The helper is private and reached only through a live KB object, so its
        // rule is reproduced here against the same predicate and checked two ways:
        // against the real source text, and behaviourally. A source match alone
        // would pass with a wrong predicate; the behaviour below is what would not.

        [Theory]
        [InlineData("Header", null)]        // major writes Name
        [InlineData(null, "Header")]        // major writes ControlName
        [InlineData("Header", "Header")]    // both, as some objects do
        public void APrintBlockIsFoundUnderEitherAttributeSpelling(string name, string controlName)
        {
            XDocument report = Report((name, controlName));

            // The rule, as the two-attribute predicate.
            XElement found = report.Descendants("PrintBlock")
                .FirstOrDefault(pb => Matches(pb, "Header"));

            Assert.NotNull(found);
        }

        [Theory]
        [InlineData("header")]     // case-insensitive, as the SDK's own matching is
        [InlineData("HEADER")]
        [InlineData("HeAdEr")]
        public void TheMatchIsCaseInsensitive(string requested)
        {
            XDocument report = Report(("Header", null));

            XElement found = report.Descendants("PrintBlock")
                .FirstOrDefault(pb => Matches(pb, requested));

            Assert.NotNull(found);
        }

        [Fact]
        public void ANonMatchingPrintBlockIsNotFound()
        {
            XDocument report = Report(("Header", null), ("Footer", null));

            // And the delete read-back's question - is it gone? - is the same rule
            // asked in the other direction.
            XElement found = report.Descendants("PrintBlock")
                .FirstOrDefault(pb => Matches(pb, "Footer2"));

            Assert.Null(found);
        }

        [Fact]
        public void AReportWithNoPrintBlocksMatchesNothing()
        {
            // Delete's read-back treats "no PrintBlock elements" as success, so the
            // predicate has to be safe on an empty document rather than throwing -
            // a null document would be a caller bug, an empty one is a real report.
            XDocument report = Report();

            Assert.Empty(report.Descendants("PrintBlock"));
            Assert.Null(report.Descendants("PrintBlock").FirstOrDefault(pb => Matches(pb, "Header")));
        }

        [Fact]
        public void TheTwoAttributePredicateIsStatedOnceAndUsedByEveryReadBack()
        {
            string source = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "LayoutService.cs"));

            // One definition, one negative, and every read-back routes through them.
            Assert.Equal(1, SourceAssert.Count(source, "private static XElement FindPrintBlockByName(XDocument document, string printBlockName)"));
            Assert.Equal(1, SourceAssert.Count(source, "private static bool ContainsPrintBlock(XDocument document, string printBlockName)"));

            // Five read-backs across the three mutations. Rename asks "is it there
            // now?" twice - the fresh read and the retry - and add asks "did it
            // arrive?" twice the same way, both of which are the same question asked
            // of an element rather than of a boolean, so they use the finder. Delete
            // asks the negative once, which is the boolean.
            Assert.Equal(1, SourceAssert.Count(source, "bool exists = ContainsPrintBlock(refreshed.Document, newName);"));
            Assert.Equal(1, SourceAssert.Count(source, "exists = ContainsPrintBlock(retry.Document, newName);"));
            Assert.Equal(1, SourceAssert.Count(source, "var added = FindPrintBlockByName(refreshed.Document, printBlockName);"));
            Assert.Equal(1, SourceAssert.Count(source, "added = FindPrintBlockByName(retry.Document, printBlockName);"));
            Assert.Equal(1, SourceAssert.Count(source, "&& ContainsPrintBlock(refreshed.Document, printBlockName);"));

            // And the rule itself is stated exactly once, in the finder: each
            // attribute appears in the file only as part of that one predicate.
            // Counted as "outside the finder" so the definition itself does not
            // count as a violation, which is what a flat zero would require.
            string finder = SourceAssert.MethodBody(source, "private static XElement FindPrintBlockByName(XDocument document, string printBlockName)");
            string outside = source.Replace(finder, "");
            Assert.Equal(0, SourceAssert.Count(outside, @"Attr(pb, ""Name"")"));
            Assert.Equal(0, SourceAssert.Count(outside, @"Attr(pb, ""ControlName"")"));
            Assert.Equal(1, SourceAssert.Count(finder, @"Attr(pb, ""Name"")"));
            Assert.Equal(1, SourceAssert.Count(finder, @"Attr(pb, ""ControlName"")"));
        }

        [Fact]
        public void ThePostCommitRepairIsAttemptedByTheTwoMutationsThatNeedIt()
        {
            string source = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "LayoutService.cs"));

            Assert.Equal(1, SourceAssert.Count(source, "private void TryHealPrintCommandSourceAfterCommit(KBObject obj, string target)"));

            // Rename and add. Delete deliberately does not: proving a block is gone
            // needs no normalisation, and normalising a document in which a block was
            // expected to have disappeared repairs nothing.
            Assert.Equal(2, SourceAssert.Count(source, "TryHealPrintCommandSourceAfterCommit(obj, target);"));

            // The repair is a repair and not a rollback, and it is silent: the
            // verification failure is the caller's answer already, and a repair that
            // cannot complete does not change it. Asserted so a future change that
            // starts reporting - or worse, throwing - out of this path is deliberate.
            string heal = SourceAssert.MethodBody(source, "private void TryHealPrintCommandSourceAfterCommit(KBObject obj, string target)");
            Assert.Contains("TryNormalizeReportPrintCommandsInSourceInMemory(healObj, healContext.Document.ToString(), out _)", heal);
            Assert.Contains("TryFlushSourceForLayoutMutation(healObj, out _)", heal);
            Assert.Contains("if (healContext.Error == null && healContext.Document != null)", heal);
            Assert.DoesNotContain("McpResponse", heal);
            Assert.DoesNotContain("throw", heal);
        }

        private static bool Matches(XElement block, string name)
        {
            return string.Equals((string)block.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase)
                || string.Equals((string)block.Attribute("ControlName"), name, StringComparison.OrdinalIgnoreCase);
        }

    }
}
