using System;
using System.Linq;
using GxMcp.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// <c>ObjectService.ReadFullObject</c> dispatches on object type, and every
    /// branch was repeating the same six lines with a different part name - twelve
    /// times, in two shapes. They are now <c>AddReadPart</c> calls.
    ///
    /// The distinction between the two shapes is what these tests are for. A part is
    /// either authored code, which goes into the combined source returned alongside
    /// <c>parts</c>, or metadata describing the object, which is reported but kept
    /// out of it. A Transaction's, a Table's and an SDT's <c>Structure</c> part is
    /// the second kind: a DSL description, not code. Written out by hand that was the
    /// <em>absence</em> of one line in an otherwise identical block, so a reader had
    /// to reconstruct which parts were code rather than read it - and a stray
    /// <c>combinedCode +=</c> would put a DSL description inside the combined source
    /// of a Transaction, which is the kind of wrong that reads as plausible.
    ///
    /// The helper is private and <c>ReadFullObject</c> needs a live SDK object, so
    /// the decision is pinned at the call sites instead: every metadata part is
    /// named with <c>isCode: false</c>, and no others are.
    /// </summary>
    public class ReadFullObjectSharedPartTests
    {
        private static string Source() =>
            RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "ObjectService.cs"));

        private static string ReadFullObjectBody() =>
            SourceAssert.MethodBody(Source(), "public string ReadFullObject(string target, string typeFilter = null,");

        [Fact]
        public void EveryPartIsStoredThroughTheOneHelper()
        {
            string body = ReadFullObjectBody();

            // The six-line block, written out twelve times, is gone: the only place
            // in the method that appends to the combined source is the helper.
            Assert.Equal(0, SourceAssert.Count(body, "combinedCode +="));
            Assert.Equal(0, SourceAssert.Count(body, "parts["));
        }

        [Fact]
        public void TheHelperAppendsOnlyCodeParts()
        {
            // The rule, stated once. A metadata part is reported under parts[] and
            // does not reach the combined source.
            string helper = SourceAssert.MethodBody(Source(),
                "private static void AddReadPart(JObject parts, ref string combinedCode, string key, string text, bool isCode = true)");

            Assert.Contains("if (string.IsNullOrWhiteSpace(text)) return;", helper);
            Assert.Contains("parts[key] = text;", helper);
            Assert.Contains("if (isCode) combinedCode += \"\\n\" + text;", helper);
        }

        [Fact]
        public void StructureIsMetadataForEveryTypeThatHasIt()
        {
            // A Transaction, a Table and an SDT each read their Structure part, and
            // for all three it is metadata. This is the assertion that would fail
            // if one of them started appending a DSL description to combinedCode.
            string body = ReadFullObjectBody();

            Assert.Equal(3, SourceAssert.Count(body, "AddReadPart(parts, ref combinedCode, \"structure\", ReadPartTextSafe(obj, \"Structure\"), isCode: false);"));
            Assert.Equal(0, SourceAssert.Count(body, "AddReadPart(parts, ref combinedCode, \"structure\", ReadPartTextSafe(obj, \"Structure\"));"));
        }

        [Fact]
        public void NoOtherPartIsTreatedAsMetadata()
        {
            // The converse: isCode: false is only ever about Structure. A new part
            // has to make this decision explicitly rather than by omission.
            string body = ReadFullObjectBody();

            int metadataCalls = SourceAssert.Count(body, "isCode: false");
            int structureCalls = SourceAssert.Count(body, @"AddReadPart(parts, ref combinedCode, ""structure"",");
            Assert.Equal(structureCalls, metadataCalls);
        }

        [Fact]
        public void TheSdeEventsFallbackIsStillOnlyForTheUiTypes()
        {
            // The one genuinely per-type read that the helper does not hide: these
            // five types spell the part SDEEvents when Events is absent, and the
            // fallback is the reason their branch cannot be a list of part names.
            string body = ReadFullObjectBody();

            Assert.Equal(1, SourceAssert.Count(body, "if (string.IsNullOrWhiteSpace(events)) events = ReadPartTextSafe(obj, \"SDEEvents\");"));
        }

        [Fact]
        public void ThePartUnavailableDiagnosticIsDefinedOnce()
        {
            // Two different failures - an unreadable visual part and an unresolvable
            // pattern instance - carried the same extra block, and the
            // availableParts list in it is what the client is told to recover with.
            // A drift would leave one error advertising a way out that the other
            // does not, behind the same field name.
            string source = Source();

            Assert.Equal(1, SourceAssert.Count(source, "private static JObject PartUnavailableExtra(KBObject obj, string partName)"));
            Assert.Equal(2, SourceAssert.Count(source, "extra: PartUnavailableExtra(obj, partName)"));

            // The field set itself, so a field leaving the diagnostic is deliberate.
            string helper = SourceAssert.MethodBody(source, "private static JObject PartUnavailableExtra(KBObject obj, string partName)");
            Assert.Contains(@"[""part""] = partName,", helper);
            Assert.Contains(@"[""objectName""] = obj.Name,", helper);
            Assert.Contains(@"[""objectType""] = obj.TypeDescriptor?.Name,", helper);
            Assert.Contains(@"[""availableParts""] = new JArray(GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj))", helper);
        }

    }
}
