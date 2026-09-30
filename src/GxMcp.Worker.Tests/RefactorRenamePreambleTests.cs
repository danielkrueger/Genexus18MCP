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
    /// The two whole-object renames - <c>RenameAttribute</c> and <c>RenameObject</c>
    /// - resolve different things afterwards (an Attribute versus a filtered
    /// object) but shared their opening guards verbatim: both names present, then
    /// an open KB. Those are now <c>RenamePreamble</c>.
    ///
    /// The order is the contract and the wording is the contract: a rename that
    /// reaches the SDK without an open KB, or that reports a missing-argument
    /// error only after trying the KB, sends the caller down the wrong recovery
    /// path. Both codes are also part of what a client parses.
    /// </summary>
    public class RefactorRenamePreambleTests
    {
        [Fact]
        public void MissingArguments_AreRejectedBeforeTheKbIsConsulted()
        {
            // A missing name is a caller error and needs no KB at all; reporting it
            // as KbNotOpen would send the caller to open a KB it does not need.
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "RefactorService.cs");

            int preamble = src.IndexOf("private string RenamePreamble(", StringComparison.Ordinal);
            Assert.True(preamble > 0, "RenamePreamble not found");

            int argsGuard = src.IndexOf("code: \"RenameArgsMissing\"", preamble, StringComparison.Ordinal);
            int kbGuard = src.IndexOf("code: \"KbNotOpen\"", preamble, StringComparison.Ordinal);
            Assert.True(argsGuard > preamble, "the argument guard is missing from RenamePreamble");
            Assert.True(kbGuard > argsGuard, "the KB guard must follow the argument guard");
        }

        [Theory]
        [InlineData("RenameAttribute")]
        [InlineData("RenameObject")]
        public void EveryWholeObjectRename_GoesThroughTheSharedPreamble(string method)
        {
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "RefactorService.cs");

            int at = src.IndexOf("private string " + method + "(", StringComparison.Ordinal);
            Assert.True(at > 0, method + " not found");
            int next = src.IndexOf("private string ", at + 10, StringComparison.Ordinal);
            string body = next < 0 ? src.Substring(at) : src.Substring(at, next - at);

            Assert.Contains("RenamePreamble(oldName, newName)", body);
            Assert.DoesNotContain("RenameArgsMissing", body);
            Assert.DoesNotContain("KbNotOpen", body);
        }

        [Fact]
        public void ThePreamble_IsUsedExactlyOnce()
        {
            // Two call sites, one implementation. A third copy re-introduces the
            // drift this removed.
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "RefactorService.cs");

            Assert.Equal(1, CountOccurrences(src, "private string RenamePreamble(string oldName, string newName)"));
            // Two call sites; the declaration spells the parameters as
            // "string oldName" and so does not match the call form.
            Assert.Equal(2, CountOccurrences(src, "RenamePreamble(oldName, newName)"));
        }

        [Fact]
        public void ThePreamble_NamesBothGuardsExactlyOnce()
        {
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "RefactorService.cs");

            Assert.Equal(1, CountOccurrences(src, "code: \"RenameArgsMissing\""));
            Assert.Equal(1, CountOccurrences(src, "code: \"KbNotOpen\""));
        }

        [Fact]
        public void TheKbGuard_StillRoutesToGenexusKbOpen()
        {
            // The recovery step is part of the client contract, not decoration.
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "RefactorService.cs");

            int kbGuard = src.IndexOf("code: \"KbNotOpen\"", StringComparison.Ordinal);
            int preambleEnd = src.IndexOf("return null;", kbGuard, StringComparison.Ordinal);
            string block = src.Substring(kbGuard, preambleEnd - kbGuard);

            Assert.Contains("genexus_kb", block);
            Assert.Contains("[\"action\"] = \"open\"", block);
        }

        [Fact]
        public void ThePublicRefactorEntryPoint_IsUnchanged()
        {
            // The consolidation was internal; nothing a client calls moved.
            var surface = typeof(RefactorService)
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Select(m => m.Name)
                .ToList();

            Assert.Contains("Refactor", surface);
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
