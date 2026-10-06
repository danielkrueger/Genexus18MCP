using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Tests
{
    // issue #60 — structured-diagnostics parser over the BuildService status envelope.
    // The envelope mixes PascalCase CLR property names (Status, ErrorsDetailed) with
    // JsonProperty-renamed computed getters, so the parser must look up case-insensitively.
    public class SpecificationDiagnosticsTests
    {
        [Fact]
        public void GetStatus_ReadsPascalCaseEnvelope()
        {
            string json = "{\"Status\":\"Succeeded\",\"ErrorCount\":0}";
            Assert.Equal("Succeeded", SpecificationDiagnostics.GetStatus(json));
        }

        [Fact]
        public void GetStatus_ReadsLowercaseEnvelope()
        {
            string json = "{\"status\":\"Failed\",\"errorCount\":1}";
            Assert.Equal("Failed", SpecificationDiagnostics.GetStatus(json));
        }

        [Fact]
        public void GetStatus_Unparseable_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, SpecificationDiagnostics.GetStatus("garbage"));
        }

        [Fact]
        public void IsTerminal_TrueForSucceededFailedCancelledError()
        {
            Assert.True(SpecificationDiagnostics.IsTerminal("{\"Status\":\"Succeeded\"}"));
            Assert.True(SpecificationDiagnostics.IsTerminal("{\"Status\":\"Failed\"}"));
            Assert.True(SpecificationDiagnostics.IsTerminal("{\"Status\":\"Cancelled\"}"));
            Assert.True(SpecificationDiagnostics.IsTerminal("{\"Status\":\"Error\"}"));
        }

        [Fact]
        public void IsTerminal_FalseWhileRunning()
        {
            Assert.False(SpecificationDiagnostics.IsTerminal("{\"Status\":\"Running\"}"));
            Assert.False(SpecificationDiagnostics.IsTerminal("{\"Status\":\"Accepted\"}"));
        }

        [Fact]
        public void GetSnapshot_ReadsMetaBaseline()
        {
            string json = "{\"Status\":\"Running\",\"_meta\":{\"snapshot\":\"abc123\"}}";
            Assert.Equal("abc123", SpecificationDiagnostics.GetSnapshot(json));
        }

        [Fact]
        public void GetSnapshot_NoBaseline_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, SpecificationDiagnostics.GetSnapshot("{\"Status\":\"Succeeded\"}"));
        }

        [Fact]
        public void Parse_PascalCaseErrorsDetailed_RowsBecomeDiagnostics()
        {
            string json = "{\"Status\":\"Failed\",\"ErrorsDetailed\":[" +
                          "{\"raw\":\"error spc0056: variable &EmpresaID definition is incorrect or not available\",\"rewritten\":\"[gx-object=MyProc phase=spec] error spc0056: variable &EmpresaID definition is incorrect or not available\",\"gxObject\":\"MyProc\"}," +
                          "{\"raw\":\"error gen0022: something else\",\"gxObject\":\"MyProc\"}]}";
            var diags = SpecificationDiagnostics.Parse(json);
            Assert.Equal(2, diags.Count);

            Assert.Equal("spc0056", diags[0]["code"]?.ToString());
            Assert.Equal("MyProc", diags[0]["object"]?.ToString());
            Assert.Equal("EmpresaID", diags[0]["member"]?.ToString());
            Assert.False(string.IsNullOrWhiteSpace(diags[0]["message"]?.ToString()));
            Assert.DoesNotContain("error spc0056:", diags[0]["message"]?.ToString());

            Assert.Equal("gen0022", diags[1]["code"]?.ToString());
        }

        [Fact]
        public void Parse_LowercaseErrors_RowsBecomeDiagnostics()
        {
            string json = "{\"status\":\"Failed\",\"errors\":[\"error spc0056: &EmpresaID definition is incorrect\",\"warning gen0022: noise\"]}";
            var diags = SpecificationDiagnostics.Parse(json);
            Assert.Equal(2, diags.Count);
            Assert.Equal("spc0056", diags[0]["code"]?.ToString());
            Assert.Equal("EmpresaID", diags[0]["member"]?.ToString());
        }

        [Fact]
        public void Parse_CsError_KeepsCodeAndMessage()
        {
            string json = "{\"Status\":\"Failed\",\"ErrorsDetailed\":[{\"raw\":\"error CS0246: The type or namespace name 'Foo' could not be found\",\"gxObject\":\"MyProc\"}]}";
            var diags = SpecificationDiagnostics.Parse(json);
            Assert.Single(diags);
            Assert.Equal("CS0246", diags[0]["code"]?.ToString());
        }

        [Fact]
        public void Parse_FallsBackToFlatErrors_WhenDetailedRowsHaveNoText()
        {
            string json = "{\"ErrorsDetailed\":[{\"gxObject\":\"MyProc\"}],\"Errors\":[\"error spc0056: fallback\"]}";

            var diags = SpecificationDiagnostics.Parse(json);

            Assert.Single(diags);
            Assert.Equal("spc0056", diags[0]["code"]?.ToString());
        }

        [Fact]
        public void Parse_ItemizesRawCodeObjectAndLine()
        {
            string json = "{\"Status\":\"Failed\",\"Errors\":[\"error spc0010: Type mismatch in assignment [SampleProc, line 1]\"]}";
            var diags = SpecificationDiagnostics.Parse(json);
            Assert.Single(diags);
            Assert.Equal("spc0010", diags[0]["code"]?.ToString());
            Assert.Equal("SampleProc", diags[0]["object"]?.ToString());
            Assert.Equal(1, diags[0]["line"]?.ToObject<int>());
            Assert.Equal("error spc0010: Type mismatch in assignment [SampleProc, line 1]", diags[0]["raw"]?.ToString());
        }

        [Fact]
        public void GetWarnings_AndStaleDetection_ReadWarningsArray()
        {
            string json = "{\"Status\":\"Failed\",\"warnings\":[\"warning : changed since the last build: SampleProc. GeneXus specifies the environment's copy of an object\",\"other\"]}";
            var warnings = SpecificationDiagnostics.GetWarnings(json);
            Assert.Equal(2, warnings.Count);
            Assert.True(SpecificationDiagnostics.HasStaleEnvironmentCopy(warnings));
            Assert.False(SpecificationDiagnostics.HasStaleEnvironmentCopy(new JArray("other")));
            Assert.Empty(SpecificationDiagnostics.GetWarnings("nope"));
        }

        // The compact status polled after an edit empties warnings and carries the
        // lines in newWarnings (shape observed on a live specify task).
        [Fact]
        public void GetWarnings_ReadsTheCompactStatusNewWarnings()
        {
            string json = "{\"Status\":\"Failed\",\"compact\":true,\"warnings\":[],\"warningCount\":1,"
                + "\"newWarnings\":[\"warning : changed since the last build: SampleProc. GeneXus specifies the environment's copy of an object\"]}";
            var warnings = SpecificationDiagnostics.GetWarnings(json);
            Assert.Single(warnings);
            Assert.True(SpecificationDiagnostics.HasStaleEnvironmentCopy(warnings));
        }

        [Fact]
        public void Parse_Unparseable_ReturnsEmptyArray()
        {
            Assert.Empty(SpecificationDiagnostics.Parse("nope"));
            Assert.Empty(SpecificationDiagnostics.Parse(null));
        }

        [Fact]
        public void HasSpecErrors_CountsSourceAndQueryFamilies_ButNotInfrastructure()
        {
            string spc = "{\"Errors\":[\"error spc0056: bad\"]}";
            string gen = "{\"Errors\":[\"error gen0022: bad\"]}";
            string src = "{\"Errors\":[\"error src0294: bad\"]}";
            string qry = "{\"Errors\":[\"error qry0001: bad\"]}";
            string cs = "{\"Errors\":[\"error CS0246: bad\"]}";
            string msb = "{\"Errors\":[\"error MSB3027: locked\"]}";
            string gtm = "{\"Errors\":[\"error gtm0092: restore failed\"]}";
            Assert.True(SpecificationDiagnostics.HasSpecErrors(spc));
            Assert.True(SpecificationDiagnostics.HasSpecErrors(gen));
            Assert.True(SpecificationDiagnostics.HasSpecErrors(src));
            Assert.True(SpecificationDiagnostics.HasSpecErrors(qry));
            Assert.False(SpecificationDiagnostics.HasSpecErrors(cs));
            Assert.False(SpecificationDiagnostics.HasSpecErrors(msb));
            Assert.False(SpecificationDiagnostics.HasSpecErrors(gtm));
        }
    }
}
