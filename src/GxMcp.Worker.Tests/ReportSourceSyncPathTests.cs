using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The report print-block source synchronisation goes through the in-memory
    /// <c>ISource</c> part, inside the transaction that <c>BeginReportMutation</c>
    /// opens. Two write-through variants used to sit alongside it —
    /// <c>TryRenamePrintCommandInSource</c> and <c>TryInsertPrintCommandInSource</c>
    /// — which read Source as JSON and wrote it back through a temp file and
    /// <c>ImportObjectFromText</c>. Neither had a caller: they were added in the
    /// same commit as the in-memory versions and never wired up. Removing them
    /// also removed their only caller of <c>TryPersistSourceText</c>, the helper
    /// that performed the temp-file write.
    ///
    /// That is worth a guard, because the path they used was not merely redundant.
    /// It wrote outside the caller's transaction, so a change persisted that way
    /// would not have been covered by the snapshot <c>BeginReportMutation</c> took
    /// for rollback.
    /// </summary>
    public class ReportSourceSyncPathTests
    {
        [Theory]
        // The three live mutations, and the synchronisation each relies on.
        [InlineData("TryRenamePrintCommandInSourceInMemory")]
        [InlineData("TryInsertPrintCommandInSourceInMemory")]
        [InlineData("TryRemovePrintCommandFromSourceInMemory")]
        [InlineData("TryNormalizeReportPrintCommandsInSourceInMemory")]
        public void TheInMemorySyncPath_IsPresent(string method)
        {
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "LayoutService.SourcePersistence.cs");

            Assert.Contains("private bool " + method + "(", src);
        }

        [Fact]
        public void TheWriteThroughPath_IsGone()
        {
            // The temp-file write is what made the old path bypass the transaction.
            // If it comes back, the rollback that BeginReportMutation sets up stops
            // covering report print-block edits.
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "LayoutService.SourcePersistence.cs");

            Assert.DoesNotContain("gxmcp-layout-source-", src);
            Assert.DoesNotContain("ImportObjectFromText", src);
            Assert.DoesNotContain("TryPersistSourceText", src);
            // The InMemory names contain the bare names as a prefix, so the live
            // ones are blanked before checking the bare form is absent.
            string withoutLiveNames = src
                .Replace("TryRenamePrintCommandInSourceInMemory(", string.Empty)
                .Replace("TryInsertPrintCommandInSourceInMemory(", string.Empty);
            Assert.DoesNotContain("TryRenamePrintCommandInSource(", withoutLiveNames);
            Assert.DoesNotContain("TryInsertPrintCommandInSource(", withoutLiveNames);
        }

        [Fact]
        public void TheInMemorySyncStillWritesTheSourcePart()
        {
            // Positive direction: the live path assigns straight to the part, which
            // is what the caller's transaction commits.
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "LayoutService.SourcePersistence.cs");

            int writes = CountOccurrences(src, "sourcePart.Source = ");
            Assert.True(writes >= 3,
                "each in-memory print-command sync must assign to sourcePart.Source; found " + writes);
        }

        [Fact]
        public void EverySourceResolution_UsesTheSameCast()
        {
            // The shared preamble, still per-method but now the only shape present.
            // Pinned as "no other spelling exists" rather than as a count, so
            // adding a legitimate method later does not read as a regression while
            // a second, subtly different way of resolving the part still does.
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "LayoutService.SourcePersistence.cs");

            Assert.Contains("var sourcePart = PartAccessor.GetPart(obj, \"Source\") as ISource;", src);
            Assert.DoesNotContain("PartAccessor.GetPart(obj, \"Source\")", src.Replace(
                "PartAccessor.GetPart(obj, \"Source\") as ISource;", string.Empty));
        }

        [Fact]
        public void ThePublicSurfaceIsUnchanged()
        {
            // The deleted methods were private; nothing a client can call moved.
            var surface = typeof(LayoutService)
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Select(m => m.Name)
                .ToList();

            Assert.Contains("RenamePrintBlock", surface);
            Assert.Contains("AddPrintBlock", surface);
            Assert.Contains("DeletePrintBlock", surface);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

    }
}
