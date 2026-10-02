using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The four XML parse sites whose input comes out of a KB or an imported archive,
    /// routed through <see cref="SafeXml"/> so a <c>DOCTYPE</c> cannot be used to make
    /// the parser resolve an external entity.
    ///
    /// <para>
    /// Eight other parse sites in the Worker already prohibited DTDs. That made the
    /// pattern look exhaustive when it was not, which is the cost of the rule having
    /// lived in eight places instead of one. The source-shape test below is what makes
    /// it exhaustive: it fails on a <c>new XmlDocument()</c> reappearing in any of the
    /// four files.
    /// </para>
    ///
    /// <para>
    /// This is defence in depth against an inconsistency, not a demonstrated exploit
    /// path. The input is KB-sourced rather than directly request-sourced: it requires
    /// a KB the user opened, or an archive they imported. What is certain is the drift
    /// itself - four sites reading the same kind of input with a weaker setting than
    /// their eight siblings.
    /// </para>
    /// </summary>
    public sealed class KbSourcedXmlDtdTests : IDisposable
    {
        /// <summary>
        /// A DTD declaring an internal entity. It needs no network or filesystem to
        /// resolve, so the test cannot pass or fail on whether the referenced target
        /// happens to exist - it reports only whether the parser processed the DTD at
        /// all. An external <c>SYSTEM</c> entity would make the same assertion depend on
        /// an I/O failure the reader raises after it has already decided.
        /// </summary>
        private const string Dtd = "<!DOCTYPE ModulePackage [<!ENTITY x 'Injected'>]>";

        /// <summary>
        /// Shaped like the KB's own connection metadata, with no DTD - the happy path
        /// that must keep working.
        /// </summary>
        private static string Connection(string integratedSecurity) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            + "<ConnectionInformation>"
            + "<ServerInstance>.</ServerInstance>"
            + "<DBName>SampleKb</DBName>"
            + "<IntegratedSecurity>" + integratedSecurity + "</IntegratedSecurity>"
            + "</ConnectionInformation>";

        private readonly string tempDir = Path.Combine(Path.GetTempPath(), "GxMcpKbXmlDtd_" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }

        private string WriteConnection(string xml)
        {
            Directory.CreateDirectory(tempDir);
            string path = Path.Combine(tempDir, KbConnectionString.ConnectionFileName);
            File.WriteAllText(path, xml);
            return path;
        }

        [Theory]
        [InlineData("src", "GxMcp.Worker", "Helpers", "KbConnectionString.cs")]
        [InlineData("src", "GxMcp.Worker", "Helpers", "SdtModelPropagation.cs")]
        [InlineData("src", "GxMcp.Worker", "Helpers", "WebFormSaveDiagnostics.cs")]
        [InlineData("src", "GxMcp.Worker", "Services", "ModuleService.cs")]
        public void EveryKbSourcedXmlLoadProhibitsDtds(string a, string b, string c, string file)
        {
            // Comments blanked so the assertion counts code: a file may name
            // XmlDocument in prose, and only a parse is the finding.
            string source = RepoSource.WithoutComments(a, b, c, file);

            Assert.Equal(0, SourceAssert.Count(source, "new XmlDocument()"));
        }

        [Fact]
        public void ASafeXmlReaderRejectsADoctype()
        {
            string xml = "<?xml version=\"1.0\"?>" + Dtd + "<ModulePackage><Name>&x;</Name></ModulePackage>";

            // XmlException, not a document: the reader refuses the DTD before any
            // entity in it can be expanded.
            Assert.Throws<XmlException>(() => SafeXml.LoadText(xml));
            Assert.Throws<XmlException>(() => SafeXml.LoadFile(WriteConnection(xml)));
        }

        [Fact]
        public void ASafeXmlReaderStillReadsOrdinaryKbMetadata()
        {
            // The hardened path still produces the string both callers depend on,
            // Integrated-Security branch included. A guard that only checked rejection
            // would also pass with a reader that rejects everything.
            WriteConnection(Connection("false"));
            Assert.Equal(
                "Server=.;Database=SampleKb;TrustServerCertificate=true;Connection Timeout=5",
                KbConnectionString.Build(tempDir, "TEST"));

            WriteConnection(Connection("True"));
            Assert.Equal(
                "Server=.;Database=SampleKb;Integrated Security=SSPI;TrustServerCertificate=true;Connection Timeout=5",
                KbConnectionString.Build(tempDir, "TEST"));
        }

        [Fact]
        public void TheModuleManifestRejectionShapeIsPreserved()
        {
            // A DTD-bearing manifest must surface the way a malformed one already did:
            // the curated InvalidDataException the caller already handles, not a raw
            // XmlException escaping the parse.
            string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Dtd
                + "<ModulePackage><ID>44fbb6d2-0a48-44e7-98e9-64d0e359861f</ID>"
                + "<Name>SecurityAPICommons</Name><Version>3.10.20.183754</Version></ModulePackage>";

            Directory.CreateDirectory(tempDir);
            string path = Path.Combine(tempDir, "Sample_1.0.0.0.opc");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("ModuleManifest.mf").Open(), Encoding.UTF8))
                writer.Write(xml);

            var error = Assert.Throws<InvalidDataException>(() => ModuleService.ReadOpcPackage(path));
            Assert.Contains("is not valid XML", error.Message);
        }
    }
}