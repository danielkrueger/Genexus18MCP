using System;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Issue #352: the <c>genexus_edit</c> help stated that raw
    /// <c>PatternInstance</c> edits are limited to existing property changes and that
    /// structure is rejected, then showed two examples that were exactly structural
    /// raw-XML edits (insert a <c>userAction</c> by Insert_After, wrap attributes in a
    /// <c>&lt;table isGroup="True"&gt;</c> group by full rewrite). The preflight was
    /// right; the examples were not.
    ///
    /// A textual guard alone would let the examples drift back, so these assert on
    /// capability: every structural example must name the published typed action, and
    /// no example may demonstrate a node insertion through the raw route.
    /// </summary>
    public class PatternInstanceHelpContractTests
    {
        private static string EditHelp() => ToolHelpCatalog.Get("genexus_edit") ?? string.Empty;

        [Fact]
        public void Help_Still_States_The_Raw_Property_Only_Restriction()
        {
            string help = EditHelp();
            // The restriction itself is correct and must survive the example fix: it is
            // what protects the SDK-owned structure.
            Assert.Contains("limited to existing property changes", help, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("PatternStructureChangeUnsupported", help, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Help_DoesNot_Demonstrate_Raw_Node_Insertion()
        {
            string help = EditHelp();
            Assert.DoesNotContain("operation: 'Insert_After'", help, StringComparison.Ordinal);
            Assert.DoesNotContain("operation: \"Insert_After\"", help, StringComparison.Ordinal);
        }

        [Fact]
        public void Structural_Example_Names_The_Typed_Action_Instead_Of_Raw_Xml()
        {
            string help = EditHelp();
            // The custom-button example must route through the published action and
            // must say the raw route refuses it.
            Assert.Contains("add_user_action", help, StringComparison.Ordinal);
            Assert.Contains("genexus_wwp", help, StringComparison.Ordinal);
        }

        [Fact]
        public void Help_DoesNot_Present_Group_Insertion_As_Supported_Anywhere()
        {
            string help = EditHelp();
            // The element-kinds section documents isGroup as a valid element, which is
            // true, but it must not be offered as something raw PatternInstance XML can
            // insert. The example section is what misled callers.
            var examples = help.Substring(help.IndexOf("### Pattern examples", StringComparison.Ordinal));
            Assert.DoesNotContain("mode: 'full'", examples, StringComparison.Ordinal);
            Assert.Contains("GeneXus IDE", examples, StringComparison.Ordinal);
        }

        /// <summary>
        /// Issue #353 rewrote this one. It used to assert the help says saving "does not
        /// regenerate" - which was true of the recorded GX17 U4 + K2BTools 13.1 evidence
        /// and false of GX18, where a <c>PatternInstance</c> save was measured
        /// regenerating the derived objects three times in both directions.
        ///
        /// <para>
        /// The intent survives: the help must be truthful about regeneration. What changed
        /// is that a flat negative is no longer truthful on any single environment, so the
        /// contract is now that the help reports the split, refuses to pick a side, and
        /// keeps the out-of-box route for a caller whose objects did not follow.
        /// </para>
        /// </summary>
        [Fact]
        public void Help_Does_Not_Claim_A_Flat_Regeneration_Verdict()
        {
            string help = EditHelp();

            Assert.False(help.Contains("does not regenerate", StringComparison.OrdinalIgnoreCase),
                "a flat 'saving does not regenerate' is falsified on GX18 - see "
                + "docs/sdk-probe/workwith-regeneration-candidate.md");
            Assert.False(help.Contains("generatedObjectsRegenerated", StringComparison.Ordinal),
                "the response no longer carries that field, so the help must not name it");
        }

        [Fact]
        public void Help_Reports_The_Regeneration_Evidence_And_Its_Split()
        {
            string help = EditHelp();

            Assert.Contains("derivedObjectRegeneration", help, StringComparison.Ordinal);
            Assert.Contains("GX18", help, StringComparison.Ordinal);
            Assert.Contains("GX17", help, StringComparison.Ordinal);
            Assert.Contains("GeneXus IDE", help, StringComparison.Ordinal);
        }

        [Fact]
        public void Help_Keeps_The_ChildrenOrderedList_Contract_Accurate()
        {
            string help = EditHelp();
            // The accurate statement is that raw edits do not rebuild it AND that
            // hand-editing it is rejected as SDK-owned metadata.
            Assert.Contains("PatternMetadataChangeUnsupported", help, StringComparison.Ordinal);
            Assert.Contains("SDK-owned metadata", help, StringComparison.OrdinalIgnoreCase);
        }
    }
}
