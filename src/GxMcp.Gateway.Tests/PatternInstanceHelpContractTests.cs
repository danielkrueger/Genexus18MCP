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

        [Fact]
        public void Help_States_That_Saving_Does_Not_Regenerate_Derived_Objects()
        {
            string help = EditHelp();
            Assert.Contains("does not regenerate", help, StringComparison.OrdinalIgnoreCase);
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
