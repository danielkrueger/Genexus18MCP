using System.Collections.Generic;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #410: <c>genexus_edit part=Variables mode=patch</c>, adding lines, answered
    /// <c>WriteNotPersisted</c> - while a re-read showed the lines <em>were</em> written,
    /// only at a different position.
    ///
    /// <para>
    /// The write was always fine; the verification disagreed. Every persistence
    /// comparator is positional - <c>normalized</c> is a verbatim string compare, and
    /// <c>NormalizedCodeEquals</c> / <c>ModuleQualificationEquals</c> both bail on a length
    /// change and then compare index-wise. The SDK declares a new variable wherever it
    /// places it, so all of them failed and a successful write was reported as a failure.
    /// The caller, having been told the write did not land, retries.
    ///
    /// <para>
    /// These call the real verification entry point rather than the comparator in
    /// isolation, because the part-scoping is part of the fix: the set comparison is only
    /// sound where line order is not content, and that decision is made by the caller.
    /// </para>
    /// </summary>
    public class VariablesPatchPersistenceTests
    {
        private const string Part = "Variables";

        /// <summary>The SDK's own renderings of the same three declarations.</summary>
        private const string Before =
            "Variables\n" +
            "&Today : Date\n" +
            "&Page : Numeric(4)\n";

        /// <summary>Requested: the three new declarations appended at the end.</summary>
        private const string RequestedAppended =
            "Variables\n" +
            "&Today : Date\n" +
            "&Page : Numeric(4)\n" +
            "&clddes : Character(30)\n" +
            "&cldcnpj : Character(15)\n" +
            "&fildes : Date\n";

        /// <summary>Persisted: the same five declarations, the new ones inserted after.</summary>
        private const string PersistedReordered =
            "Variables\n" +
            "&Today : Date\n" +
            "&clddes : Character(30)\n" +
            "&cldcnpj : Character(15)\n" +
            "&Page : Numeric(4)\n" +
            "&fildes : Date\n";

        private static WriteService.PersistedVerificationResult Verify(
            string requested, string persisted, string part = Part, string mode = null)
            => WriteService.EvaluatePersistedVerification(
                requested, persisted, readTruncated: false, readFailure: null,
                verifyMode: mode, partName: part);

        [Fact]
        public void A_Patch_Whose_Lines_Landed_At_Another_Index_Verifies()
        {
            // The reported false negative.
            var result = Verify(RequestedAppended, PersistedReordered);

            Assert.True(result.Matches, $"a successful write was reported as '{result.Reason}'");
            Assert.Equal("verified", result.State);
        }

        [Fact]
        public void A_Patch_That_Did_Not_Apply_Every_Requested_Line_Still_Mismatches()
        {
            // The relaxation must not turn a real failure into a success: the inserted set
            // has to be present, not merely plausible.
            var shortWrite = PersistedReordered.Replace("&fildes : Date\n", "");

            var result = Verify(RequestedAppended, shortWrite);

            Assert.False(result.Matches);
            Assert.Equal("mismatch", result.State);
        }

        [Fact]
        public void A_Patch_That_Dropped_A_Pre_Existing_Line_Still_Mismatches()
        {
            // Containment alone would pass this, which is why the comparison is a multiset
            // and not a "requested lines are present" check.
            var droppedExisting = PersistedReordered.Replace("&Page : Numeric(4)\n", "");

            var result = Verify(RequestedAppended, droppedExisting);

            Assert.False(result.Matches);
            Assert.Equal("mismatch", result.State);
        }

        [Fact]
        public void A_Line_Whose_Content_Changed_Still_Mismatches()
        {
            var edited = PersistedReordered.Replace("&clddes : Character(30)", "&clddes : Character(40)");

            var result = Verify(RequestedAppended, edited);

            Assert.False(result.Matches);
        }

        [Fact]
        public void The_Relaxation_Does_Not_Apply_To_A_Part_Where_Order_Is_Content()
        {
            // The safety boundary. Rule order in a Source part is real content, so a
            // reordered Source must keep failing verification.
            var sourceRequested = "Rules\nRule one\nRule two\nRule three\n";
            var sourcePersisted = "Rules\nRule three\nRule one\nRule two\n";

            var result = Verify(sourceRequested, sourcePersisted, part: "Source");

            Assert.False(result.Matches);
        }

        [Fact]
        public void An_Explicit_Exact_Mode_Still_Rejects_A_Reordered_Part()
        {
            // A caller that asks for exact verification has asked for positional truth, and
            // the relaxation must not quietly answer a stricter question.
            var result = Verify(RequestedAppended, PersistedReordered, mode: "exact");

            Assert.False(result.Matches);
        }

        [Fact]
        public void Identical_Content_Still_Verifies_And_Ordinary_Patches_Are_Unaffected()
        {
            Assert.True(Verify(Before, Before).Matches);
            Assert.True(Verify(RequestedAppended, RequestedAppended).Matches);

            // Whitespace and line-ending drift keep verifying through the pre-existing path.
            Assert.True(Verify(Before, "Variables\r\n&Today : Date\r\n&Page : Numeric(4)\r\n").Matches);
        }

        [Fact]
        public void The_Comparator_Handles_Duplicate_Declarations()
        {
            // Multiset, not set: dropping one of two identical lines must still mismatch.
            string dup = "Variables\n&a : Date\n&a : Date\n";
            string oneFewer = "Variables\n&a : Date\n";

            Assert.True(WriteService.VariablesDeclarationSetEquals(dup, dup));
            Assert.False(WriteService.VariablesDeclarationSetEquals(dup, oneFewer));
        }

        [Fact]
        public void The_Comparator_Ignores_Blank_Lines_And_Surrounding_Whitespace()
        {
            Assert.True(WriteService.VariablesDeclarationSetEquals(
                "Variables\n\n&a : Date\n\n", "\nVariables\n  &a : Date  \n\n\n"));
        }
    }
}
