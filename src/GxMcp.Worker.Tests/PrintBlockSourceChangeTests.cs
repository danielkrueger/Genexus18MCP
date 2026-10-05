using System.Text.RegularExpressions;
using GxMcp.TestSupport;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// add/rename/delete_printblock edit the Procedure Source as a side effect. The response
    /// must report that edit, and add_printblock must be able to opt out of the insertion.
    /// </summary>
    public class PrintBlockSourceChangeTests
    {
        [Fact]
        public void BuildSourceChange_ReportsTheInsertedPrintLine()
        {
            var change = LayoutService.BuildSourceChange(
                "// Procedure: SampleProc\r\n",
                "// Procedure: SampleProc\r\n\r\nprint PbSample");

            Assert.True((bool)change["changed"]);
            Assert.Contains("print PbSample", change["addedLines"].ToString());
        }

        [Fact]
        public void BuildSourceChange_ReportsLineAndRemovedTextForADelete()
        {
            var change = LayoutService.BuildSourceChange("a\nprint PbOne\nb", "a\nb");

            Assert.True((bool)change["changed"]);
            Assert.Equal(2, (int)change["line"]);
            Assert.Equal("print PbOne", (string)change["removedLines"][0]);
            Assert.Empty(change["addedLines"]);
        }

        [Fact]
        public void BuildSourceChange_ReportsUnchangedWhenSourceIsIdentical()
        {
            var change = LayoutService.BuildSourceChange("print PbOne", "print PbOne");

            Assert.False((bool)change["changed"]);
            Assert.Null(change["line"]);
        }

        [Fact]
        public void AddPrintBlock_SkipsTheSourceInsertionWhenOptedOut_AndEveryOperationReportsTheChange()
        {
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "LayoutService.cs");

            Assert.Contains("bool appendPrintToSource = true)", src);
            Assert.Contains("appendPrintToSource && !TryInsertPrintCommandInSourceInMemory(", src);
            Assert.Equal(3, Regex.Matches(src, @"\[""sourceChanged""\] = ").Count);
        }

        [Fact]
        public void TheDispatcherForwardsAppendPrintToSource_DefaultingToTrue()
        {
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "CommandDispatcher.cs");

            Assert.Contains("args?[\"appendPrintToSource\"]?.ToObject<bool?>() ?? true", src);
        }
    }
}
