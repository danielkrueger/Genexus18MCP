using System;
using System.IO;
using System.IO.Compression;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class TransferImportTests
    {
        private sealed class FakeExportItem
        {
            public string ObjectName { get; set; }
            public string DisplayName { get; set; }
            public string Guid { get; set; }
            public string BaseOperation { get; set; }
            public string Status { get; set; }
        }

        [Fact]
        public void DescribeExportItem_ProjectsStableSdkMetadataWithoutCallingObject()
        {
            var descriptor = TransferService.DescribeExportItem(new FakeExportItem
            {
                ObjectName = "SamplePanel",
                DisplayName = "Sample panel",
                Guid = "9b7e0f20-39a2-45a4-9b21-000000000001",
                BaseOperation = "Insert",
                Status = "Ready"
            });

            Assert.Equal("SamplePanel", descriptor["name"]?.ToString());
            Assert.Equal("Sample panel", descriptor["displayName"]?.ToString());
            Assert.Equal("Insert", descriptor["baseOperation"]?.ToString());
            Assert.True(descriptor["identityAvailable"]?.ToObject<bool>() ?? false);
        }

        [Fact]
        public void SilentImportOptions_DefaultsToLosslessOverwrite()
        {
            Assert.Equal("Overwrite", TransferService.ResolveImportThemeBehavior(new JObject()));
            Assert.Equal("UseFromExport", TransferService.ResolveImportClassConflicts(new JObject()));

            // ImportOptions properties are backed by the GeneXus property
            // context; reading them in this isolated net48 test attempts to
            // load the IDE configuration and throws. The effective values are
            // verified by the real-KB import test, while these assertions keep
            // the silent-options factory covered without requiring a KB.
            Assert.NotNull(TransferService.SilentImportOptions(new JObject()));
        }

        [Fact]
        public void SilentImportOptions_AllowsExplicitIncrementalThemeIntegration()
        {
            Assert.Equal(
                "IncrementalIntegration",
                TransferService.ResolveImportThemeBehavior(
                    JObject.Parse("{\"themeImportBehavior\":\"IncrementalIntegration\"}")));
        }

        [Fact]
        public void SilentImportOptions_RejectsUnknownExplicitValues()
        {
            Assert.Throws<ArgumentException>(() =>
                TransferService.SilentImportOptions(
                    JObject.Parse("{\"classConflicts\":\"IgnoreEverything\"}")));
        }

        [Fact]
        public void ReadExportWebForms_UsesRawSourceFromXpz()
        {
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-transfer-" + Guid.NewGuid().ToString("N") + ".xpz");
            try
            {
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(archive.CreateEntry("export.xml").Open()))
                {
                    writer.Write("<ExportFile><Objects>"
                        + "<Object name=\"SampleWebPanel\"><Part><Source><![CDATA["
                        + "<GxMultiForm><gxAttribute GxWidth=\"30chr\" GxHeight=\"1row\" />"
                        + "</GxMultiForm>]]></Source></Part></Object>"
                        + "</Objects></ExportFile>");
                }

                var forms = TransferService.ReadExportWebForms(path);
                Assert.True(forms.ContainsKey("SampleWebPanel"));
                Assert.Contains("GxWidth=\"30chr\"", forms["SampleWebPanel"]);
                Assert.Contains("GxHeight=\"1row\"", forms["SampleWebPanel"]);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [Theory]
        [InlineData("Artech.Genexus.Common.Parts.ReportPart")]
        [InlineData("Artech.Genexus.Common.Parts.LayoutPart")]
        public void IsWebFormFidelityCandidate_SkipsProcedurePrintLayout(string partClass)
        {
            Assert.False(TransferService.IsWebFormFidelityCandidate(partClass));
        }

        [Theory]
        [InlineData("Artech.Genexus.Common.Parts.WebFormPart")]
        [InlineData("Artech.Genexus.Common.Parts.WebForm.WebFormPart")]
        public void IsWebFormFidelityCandidate_KeepsRealWebFormParts(string partClass)
        {
            Assert.True(TransferService.IsWebFormFidelityCandidate(partClass));
        }

        [Fact]
        public void IsWebFormFidelityCandidate_UnknownPartTypeStaysCandidate()
        {
            Assert.True(TransferService.IsWebFormFidelityCandidate(null));
        }

        [Fact]
        public void ReadExportWebForms_IgnoresProcedureLayoutSerializedOutsideSource()
        {
            // A Procedure's print layout is a direct <Layout> child of <Part>, never a <Source> CDATA,
            // so no WebForm payload exists for it; import fidelity must not demand one.
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-transfer-proc-" + Guid.NewGuid().ToString("N") + ".xpz");
            try
            {
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(archive.CreateEntry("export.xml").Open()))
                {
                    writer.Write("<ExportFile><Objects>"
                        + "<Object name=\"SampleProc\">"
                        + "<Part type=\"c414ed00-8cc4-4f44-8820-4baf93547173\"><Layout><Bands><PrintBlock name=\"Detail\" /></Bands></Layout></Part>"
                        + "<Part type=\"00000000-0000-0000-0000-000000000000\"><Properties /></Part>"
                        + "</Object></Objects></ExportFile>");
                }

                Assert.Empty(TransferService.ReadExportWebForms(path));
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void ReadExportWebForms_HandlesNamespacesAndXmlDeclaration()
        {
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-transfer-ns-" + Guid.NewGuid().ToString("N") + ".xpz");
            try
            {
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(archive.CreateEntry("namespaced.xml").Open()))
                {
                    writer.Write("<?xml version=\"1.0\"?><gx:ExportFile xmlns:gx=\"urn:test\"><gx:Object name=\"NamespacedPanel\"><gx:Part><gx:Source><![CDATA[<?xml version=\"1.0\"?><gx:GxMultiForm xmlns:gx=\"urn:test\"><gx:gxAttribute GxWidth=\"30chr\" /></gx:GxMultiForm>]]></gx:Source></gx:Part></gx:Object></gx:ExportFile>");
                }

                var forms = TransferService.ReadExportWebForms(path);
                Assert.True(forms.ContainsKey("NamespacedPanel"));
                Assert.Contains("GxWidth=\"30chr\"", forms["NamespacedPanel"]);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }
    }
}
