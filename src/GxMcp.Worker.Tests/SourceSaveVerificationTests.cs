using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SourceSaveVerificationTests
    {
        [Theory]
        [InlineData("first\r\nsecond", "first\nsecond")]
        [InlineData("first\n", "first")]
        public void ExactNoChangeGuard_DoesNotDropRequestedEolOrFinalNewline(string before, string requested)
        {
            Assert.False(WritePolicy.IsUnchangedSourceWrite(before, requested, exact: true));
            Assert.True(WritePolicy.IsUnchangedSourceWrite(before, requested));
            Assert.True(WritePolicy.IsUnchangedSourceWrite(requested, requested, exact: true));
        }

        private static JObject Receipt(string expected, string actual, string failure = null, bool dryRun = false)
        {
            return WriteService.ApplyTextVerificationReceipt(
                JObject.Parse(GxMcp.Worker.Models.McpResponse.Ok(target: "Sample", code: dryRun ? "WriteDryRun" : "WriteApplied")),
                "Sample", "Source", "original", expected, actual, "new-token", false, failure, "exact", dryRun);
        }

        [Fact]
        public void ExactRoundTrip_PreservesModularSourceAndPhysicalSaveReceipt()
        {
            string source = "For each wms.Order\r\n    wms.Log.Call(&Id)\r\nEndfor";
            var result = Receipt(source, source);
            Assert.Equal("WriteApplied", result["code"]);
            Assert.True(result["sdkSaveCompleted"].Value<bool>());
            Assert.True(result["saved"].Value<bool>());
            Assert.True(result["persistedStateKnown"].Value<bool>());
            Assert.True(result["persisted"].Value<bool>());
            Assert.Equal("genexus_read", result["postSaveVerification"]["representation"]);
            Assert.Equal("new-token", result["versionToken"]);
            Assert.Empty((JArray)result["implicitLifecycleActions"]);
        }

        [Theory]
        [InlineData("For each Foo\nEndfor", "For each wms.Foo\nEndfor", "moduleQualification", 1)]
        [InlineData("a\r\nb", "a\nb", "lineEndings", 1)]
        [InlineData("first\n&n = 1\nlast", "first\n&n = 2\nlast", "contentMismatch", 2)]
        public void ExactDifferences_ReportReasonAndPhysicalSave(string requested, string actual, string reason, int line)
        {
            var result = Receipt(requested, actual);
            Assert.Equal("WriteNotPersisted", result["code"]);
            Assert.True(result["saved"].Value<bool>());
            Assert.True(result["mutation"]["saved"].Value<bool>());
            Assert.True(result["persistedStateKnown"].Value<bool>());
            Assert.False(result["persisted"].Value<bool>());
            Assert.Equal(reason, result["mutation"]["diff"]["reason"]);
            Assert.Equal(line, result["mutation"]["diff"]["firstDifferentLine"].Value<int>());
            Assert.NotNull(result["mutation"]["diff"]["expectedLine"]);
            Assert.NotNull(result["mutation"]["diff"]["readLine"]);
            Assert.Null(result["rollback"]);
        }

        [Fact]
        public void ReadFailure_DoesNotDenyCompletedSaveOrClaimKnownPersistence()
        {
            var result = Receipt("requested", null, "FreshReadUnavailable");
            Assert.Equal("WriteVerificationUnavailable", result["code"]);
            Assert.True(result["sdkSaveCompleted"].Value<bool>());
            Assert.True(result["saved"].Value<bool>());
            Assert.False(result["persistedStateKnown"].Value<bool>());
            Assert.False(result["postSaveVerification"]["reReadConfirmed"].Value<bool>());
            Assert.Null(result["versionToken"].Value<string>());
        }

        [Fact]
        public void DryRun_DoesNotClaimSaveOrPersistence()
        {
            var result = Receipt("changed", "original", dryRun: true);
            Assert.Equal("WriteDryRun", result["code"]);
            Assert.False(result["sdkSaveCompleted"].Value<bool>());
            Assert.False(result["persisted"].Value<bool>());
            Assert.Null(result["postSaveVerification"]);
        }

        [Fact]
        public void FullSource_RequireObjectSave_IsSupported()
        {
            var args = WriteService.NormalizeFacadeArgs(new JObject
            {
                ["mode"] = "full", ["part"] = "Source", ["requireObjectSave"] = true
            });
            Assert.Null(WriteService.ValidateRequireObjectSaveArgs("Sample", args));
        }

        [Theory]
        [InlineData("wms.Log.Call()\n\n// end\n")]
        [InlineData("wms.Log.Call()\r\n\r\n// end\r\n")]
        [InlineData("wms.Log.Call()\r// end\r")]
        [InlineData("")]
        public void PublicFullRead_PreservesEveryCharacterForExactVerification(string source)
        {
            var page = GxMcp.Worker.Helpers.ReadPagination.ApplyDefault(source, 0, 0, "mcp");
            Assert.Equal(source, page.Content);
            Assert.True(Receipt(source, page.Content)["persisted"].Value<bool>());
        }

        [Theory]
        [InlineData(1, "second\nthird\r\n")]
        [InlineData(3, "")]
        [InlineData(10, "")]
        public void FullReadOffset_PreservesOriginalSuffix(int offset, string expected)
        {
            Assert.Equal(expected, GxMcp.Worker.Helpers.ReadPagination.ApplyDefault(
                "first\r\nsecond\nthird\r\n", offset, 0, "mcp").Content);
        }

        [Fact]
        public void EolDiff_ReportsActualLineAndBothTerminators()
        {
            var result = Receipt("same\r\nsecond\r\n", "same\r\nsecond\n");
            var diff = result["mutation"]["diff"];
            Assert.Equal(2, diff["firstDifferentLine"].Value<int>());
            Assert.Equal("CRLF", diff["expectedLineEnding"]);
            Assert.Equal("LF", diff["readLineEnding"]);
            var loneCr = Receipt("first\rsecond", "first\rchanged");
            Assert.Equal(2, loneCr["mutation"]["diff"]["firstDifferentLine"].Value<int>());
            Assert.Equal("second", loneCr["mutation"]["diff"]["expectedLine"]);
        }

        [Fact]
        public void ObjectSaveRequirement_ReportsPhysicalObjectSaveEvenOnMismatch()
        {
            var result = WriteService.ApplyTextVerificationReceipt(
                JObject.Parse(GxMcp.Worker.Models.McpResponse.Ok(target: "Sample", code: "WriteApplied",
                    result: new JObject { ["sdkSaveCompleted"] = true, ["objectSaved"] = true, ["metadataStampPersisted"] = true })),
                "Sample", "Source", "old", "new", "different", "35", false, null, "exact", requireObjectSave: true);
            Assert.True(result["requireObjectSave"].Value<bool>());
            Assert.True(result["objectSaved"].Value<bool>());
            Assert.True(result["metadataStampPersisted"].Value<bool>());
            Assert.False(result["partPersisted"].Value<bool>());
            Assert.Equal("transactional-object-save", result["saveContract"]);
        }

        [Fact]
        public void ObjectSaveRequirement_RejectsMissingTransactionEvidence()
        {
            var result = WriteService.ApplyTextVerificationReceipt(
                JObject.Parse(GxMcp.Worker.Models.McpResponse.Ok(target: "Sample", code: "WriteApplied")),
                "Sample", "Source", "old", "new", "new", "35", false, null, "exact", requireObjectSave: true);
            Assert.Equal("ObjectSaveIncomplete", result["code"]);
            Assert.True(result["persisted"].Value<bool>());
            Assert.Null(result["objectSaved"].Value<bool?>());
        }

        [Fact]
        public void FailedTransaction_DoesNotAssertThatNoSaveWasAttempted()
        {
            var result = WriteService.ApplyTextVerificationReceipt(
                JObject.Parse("{status:'error',error:{code:'TransactionFailed'}}"),
                "Sample", "Source", "old", "new", null, null, false, "readFailure", "exact");
            Assert.Null(result["saveAttempted"]);
            Assert.Null(result["sdkSaveCompleted"].Value<bool?>());
            Assert.Equal("TransactionFailed", result["error"]["code"]);
        }

        [Theory]
        [InlineData("normalized", "For Each wms.Foo\nEndfor", "moduleQualification")]
        [InlineData("semantic", "For Each wms.Foo\nEndfor", "moduleQualification")]
        [InlineData("normalized", "FOR EACH Foo\nENDFOR", "normalization")]
        [InlineData("semantic", "FOR EACH Foo\nENDFOR", "normalization")]
        public void NonExactFullWrites_PreserveLegacySdkTolerance(string mode, string actual, string reason)
        {
            var result = WriteService.EvaluatePersistedVerification("For Each Foo\nEndfor", actual, false, null, mode);
            Assert.True(result.Matches);
            Assert.Equal(reason, result.Reason);
        }

        [Fact]
        public void XmlNormalization_RemainsAcceptedUnlessExactWasRequested()
        {
            const string expected = "<root a=\"1\" b=\"2\" />";
            const string actual = "<root b=\"2\" a=\"1\"/>";
            Assert.True(WriteService.EvaluatePersistedVerification(expected, actual, false, null, "normalized", "WebForm").Matches);
            Assert.False(WriteService.EvaluatePersistedVerification(expected, actual, false, null, "exact", "WebForm").Matches);
        }

        [Theory]
        [InlineData(null, "Structure")]
        [InlineData("", "Structure")]
        [InlineData("  ", "WebForm")]
        public void OmittedOrBlankVerifyMode_TreatsLineEndingOnlyDifferenceAsVerified_ForXmlParts(string mode, string part)
        {
            var result = WriteService.EvaluatePersistedVerification("<a>\n  <b/>\n</a>", "<a>\r\n  <b/>\r\n</a>", false, null, mode, part);
            Assert.True(result.Matches);
            Assert.Equal("verified", result.State);
        }

        [Fact]
        public void ExplicitExactVerifyMode_StillReportsLineEndingsForXmlParts()
        {
            var result = WriteService.EvaluatePersistedVerification("<a>\n  <b/>\n</a>", "<a>\r\n  <b/>\r\n</a>", false, null, "exact", "Structure");
            Assert.False(result.Matches);
            Assert.Equal("lineEndings", result.Reason);
        }

        [Fact]
        public void BlankVerifyMode_StillReportsContentMismatchForGenuinelyDifferentStructure()
        {
            var result = WriteService.EvaluatePersistedVerification("Name\n  Description = New", "Name\r\n  Description = Old", false, null, "", "Structure");
            Assert.False(result.Matches);
            Assert.Equal("contentMismatch", result.Reason);
        }

        [Fact]
        public void DryRunMatchingText_HasConsistentComparisonButNoSave()
        {
            var result = Receipt("same", "same", dryRun: true);
            Assert.True(result["currentState"]["matches"].Value<bool>());
            Assert.True(result["currentState"]["diff"]["matches"].Value<bool>());
            Assert.False(result["saved"].Value<bool>());
        }

        [Fact]
        public void SourceRollback_RefusesNonAtomicRestoreWithoutAccessingSdk()
        {
            var writer = (WriteService)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(WriteService));
            var method = typeof(WriteService).GetMethod("RollbackFullWriteFailure", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var response = JObject.Parse("{status:'error',code:'WriteNotPersisted',source:'newer content',versionToken:'35',postSaveVerification:{reReadConfirmed:true}}");
            string output = (string)method.Invoke(writer, new object[] { response.ToString(), "Sample", "Source", "Procedure", "original" });
            var result = JObject.Parse(output);
            Assert.Equal("AtomicRollbackUnavailable", result["rollback"]["reason"]);
            Assert.False(result["rollback"]["attempted"].Value<bool>());
            Assert.Equal("newer content", result["source"]);
            Assert.Equal("35", result["versionToken"]);
        }

        [Fact]
        public void Issue301_ImportPart_PureLineEndingDifference_ClassifiesAsNormalization()
        {
            const string requested = "Event 'Start'\r\n    &x = 1\r\nEndEvent";
            const string persisted = "Event 'Start'\n    &x = 1\nEndEvent";

            var result = WriteService.EvaluatePersistedVerification(requested, persisted, false, null, verifyMode: null, partName: "Events");
            Assert.True(result.Matches);
            Assert.Equal("normalization", result.Reason);
            Assert.Equal("verified", result.State);
        }

        [Fact]
        public void Issue301_BuildPersistenceDiff_DoesNotReportEmptyFirstDifferentLine_WhenOnlyEolDiffers()
        {
            const string requested = "\r\nEvent 'Start'\r\n    &x = 1\r\nEndEvent";
            const string persisted = "\nEvent 'Start'\n    &x = 1\nEndEvent";

            var verification = WriteService.EvaluatePersistedVerification(requested, persisted, false, null, verifyMode: null, partName: "Events");
            var diff = typeof(WriteService).GetMethod("BuildPersistenceDiff", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, new object[] { requested, persisted, verification, true }) as JObject;

            Assert.NotNull(diff);
            Assert.Null(diff!["firstDifferentLine"]?.Value<int?>());
            Assert.Null(diff["expectedLine"]?.Value<string>());
            Assert.Null(diff["readLine"]?.Value<string>());
            Assert.Equal("CRLF", diff["expectedLineEnding"]?.ToString());
            Assert.Equal("LF", diff["readLineEnding"]?.ToString());
        }

        [Fact]
        public void Issue301_ApplyTextVerificationReceipt_DoesNotReportPartialPersistenceDetected_WhenOnlyEolDiffers()
        {
            const string before = "Event 'Old'\r\nEndEvent";
            const string requested = "Event 'New'\r\n    &y = 2\r\nEndEvent";
            const string actual = "Event 'New'\n    &y = 2\nEndEvent";

            var response = new JObject
            {
                ["status"] = "ok",
                ["code"] = "WriteApplied",
                ["sdkSaveCompleted"] = true
            };

            var receipt = WriteService.ApplyTextVerificationReceipt(response, "WPTest", "Events", before, requested, actual, "v1", false, null, null);
            Assert.True(receipt["verified"]?.Value<bool>());
            Assert.True(receipt["persisted"]?.Value<bool>());
            Assert.Null(receipt["partialPersistenceDetected"]);
        }
    }
}
