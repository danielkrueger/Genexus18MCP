using System;
using System.Collections.Generic;
using GxMcp.TestSupport;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Two readers split an object text document - one for the source, one for the
    /// named parts - and each was doing all of it: the emptiness check, the newline
    /// normalisation, the header split, the two-part validation, the brace search
    /// and the balance check.
    ///
    /// The three rejection messages were duplicated with them, which is the part
    /// that matters. A client parsing malformed text could be told "Object header
    /// must contain type and name." by the source reader and something else by the
    /// parts reader for the same document, and there is nothing in a parse failure
    /// that tells a caller which reader rejected it. The split is now
    /// TrySplitObjectDocument, with the body returned raw so each reader keeps its
    /// own interpretation of it.
    ///
    /// These assert the two readers agree on what is a document - the same document
    /// is either acceptable to both or to neither, and the refusal is the same
    /// sentence - and that each still applies its own reading of the body
    /// afterwards.
    /// </summary>
    public class ObjectTextDocumentSplitTests
    {
        private const string EmptyMessage = "Object text is empty.";
        private const string HeaderMessage = "Object header must contain type and name.";
        private const string BodyMessage = "Object document must contain a balanced body enclosed by braces.";

        private static bool Source(string document, out string error) =>
            SdkTextTreeService.TryParseObjectDocument(document, out _, out _, out _, out error);

        private static bool Parts(string document, out string error) =>
            SdkTextTreeService.TryParseObjectDocumentParts(document, out _, out _, out _, out error);

        private static void BothReject(string document, string expectedError)
        {
            Assert.False(Source(document, out string sourceError));
            Assert.False(Parts(document, out string partsError));

            // The same sentence from both readers, not merely a failure from both.
            Assert.Equal(expectedError, sourceError);
            Assert.Equal(expectedError, partsError);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\n\n")]
        public void EmptyTextIsRefusedWithTheSameMessageByBothReaders(string document)
        {
            BothReject(document, EmptyMessage);
        }

        [Theory]
        [InlineData("Procedure\n{\n}")]              // one token: no name
        [InlineData("\t\t\n{\n}")]                     // header is whitespace only
        public void AHeaderWithoutExactlyTwoPartsIsRefusedByBothReaders(string document)
        {
            // The split is by the *first* space or tab with a limit of two, so a
            // three-token header is accepted as type="Procedure", name="Hello
            // World" rather than refused - the name is whatever follows the first
            // separator. That is the format's rule and both readers share it, which
            // is the point; a header that yields fewer than two tokens is the case
            // with no valid split at all.
            BothReject(document, HeaderMessage);
        }

        [Fact]
        public void ANameContainingSpacesIsKeptWholeByBothReaders()
        {
            // Consequence of the split above, pinned because it is observable: the
            // name is the remainder of the header, not its second token.
            const string document = "Procedure Hello World\n{\n}";

            Assert.True(SdkTextTreeService.TryParseObjectDocument(document,
                out string sourceType, out string sourceName, out _, out string sourceError), sourceError);
            Assert.True(SdkTextTreeService.TryParseObjectDocumentParts(document,
                out string partsType, out string partsName, out _, out string partsError), partsError);

            Assert.Equal("Procedure", sourceType);
            Assert.Equal("Procedure", partsType);
            Assert.Equal("Hello World", sourceName);
            Assert.Equal("Hello World", partsName);
        }

        [Theory]
        [InlineData("Procedure Hello")]              // no body at all
        [InlineData("Procedure Hello\n{\nsource")]    // unclosed
        [InlineData("Procedure Hello\nsource\n}")]    // unopened
        [InlineData("Procedure Hello\n}{")]           // closing brace first
        public void AnUnbalancedBodyIsRefusedByBothReaders(string document)
        {
            BothReject(document, BodyMessage);
        }

        [Fact]
        public void BothReadersAgreeOnTheHeaderForAValidDocument()
        {
            const string document = "Transaction MyTrn\n{\n#Source\nmsg(\"hi\");\n#End\n}";

            Assert.True(SdkTextTreeService.TryParseObjectDocument(document,
                out string sourceType, out string sourceName, out _, out string sourceError));
            Assert.True(SdkTextTreeService.TryParseObjectDocumentParts(document,
                out string partsType, out string partsName, out _, out string partsError));

            Assert.Null(sourceError);
            Assert.Null(partsError);

            // The same header from both, or a caller reading `type` from one reader
            // and `name` from the other has a document that describes two objects.
            Assert.Equal("Transaction", sourceType);
            Assert.Equal("Transaction", partsType);
            Assert.Equal("MyTrn", sourceName);
            Assert.Equal("MyTrn", partsName);
        }

        [Fact]
        public void EachReaderStillReadsTheBodyItsOwnWay()
        {
            // The split hands back the body raw; the source reader strips the
            // #Source marker and the parts reader walks #Part sections. Neither
            // interpretation leaked into the shared step.
            //
            // The source reader is deliberately less structured than the parts
            // reader: it strips a leading #Source and returns everything after it,
            // so its output here carries the #End marker and the #Rules section
            // too. That is pre-existing behaviour, recorded rather than asserted
            // away - the point of these tests is that the two readers differ, not
            // that they agree.
            const string document =
                "Procedure Hello\n{\n#Source\nmsg(\"hello\");\n#End\n#Rules\nparm(a);\n}";

            Assert.True(SdkTextTreeService.TryParseObjectDocument(document,
                out _, out _, out string source, out string sourceError));
            Assert.Null(sourceError);
            Assert.StartsWith("msg(\"hello\");", source);
            Assert.DoesNotContain("#Source", source);

            Assert.True(SdkTextTreeService.TryParseObjectDocumentParts(document,
                out _, out _, out Dictionary<string, string> parts, out string partsError));
            Assert.Null(partsError);

            // The parts reader does interpret the sections, and keeps them apart.
            Assert.True(parts.ContainsKey("Source"), "the parts reader lost the Source section");
            Assert.True(parts.ContainsKey("Rules"), "the parts reader lost the Rules section");
            Assert.Equal("msg(\"hello\");", parts["Source"].Trim());
            Assert.DoesNotContain("#End", parts["Source"]);
        }

        [Fact]
        public void BothReadersSeeTheSameCarriageReturnHandling()
        {
            // Newline normalisation is part of the shared step, so a document
            // written with CRLF must round-trip through both readers. If it were
            // left to each, one reader would accept a Windows-authored file and the
            // other would reject it.
            string crlf = "Procedure Hello\r\n{\r\n#Source\r\nmsg(\"hello\");\r\n#End\r\n}";

            Assert.True(SdkTextTreeService.TryParseObjectDocument(crlf, out _, out _, out string source, out string sourceError), sourceError);
            Assert.True(SdkTextTreeService.TryParseObjectDocumentParts(crlf, out _, out _, out Dictionary<string, string> parts, out string partsError), partsError);

            Assert.StartsWith("msg(\"hello\");", source);
            Assert.Equal("msg(\"hello\");", parts["Source"].Trim());
            Assert.DoesNotContain("\r", source);
            Assert.DoesNotContain("\r", parts["Source"]);
        }

        [Fact]
        public void BothReadersStillProduceTheirOwnOutputType()
        {
            // Stated so the consolidation cannot collapse the two readers into one
            // that answers with a single out-parameter shape: one yields source text,
            // the other a part map, and each caller's code depends on which.
            string source = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "SdkTextTreeService.Document.cs"));

            Assert.Equal(1, SourceAssert.Count(source, "private static bool TrySplitObjectDocument("));
            Assert.Equal(1, SourceAssert.Count(source, "out string source,"));
            Assert.Equal(1, SourceAssert.Count(source, "out Dictionary<string, string> parts,"));

            // Both go through the shared split, and the three refusal messages live
            // only there.
            Assert.Equal(2, SourceAssert.Count(source, "TrySplitObjectDocument(document, out type, out name, out string body, out error)"));
            foreach (string message in new[] { EmptyMessage, HeaderMessage, BodyMessage })
                Assert.Equal(1, SourceAssert.Count(source, "\"" + message + "\""));
        }

    }
}
