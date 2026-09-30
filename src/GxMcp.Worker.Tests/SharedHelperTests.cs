using System;
using System.IO;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The KB connection string was built from <c>knowledgebase.connection</c> in
    /// two helpers. These pin the IntegratedSecurity branch, which decides whether
    /// a login is sent to the database server — the one part of that parse that
    /// must not vary between callers.
    /// </summary>
    public class KbConnectionStringTests : IDisposable
    {
        private readonly string _kb = Path.Combine(Path.GetTempPath(),
            "GxMcpKbConn_" + System.Guid.NewGuid().ToString("N"));

        public KbConnectionStringTests() => Directory.CreateDirectory(_kb);

        public void Dispose()
        {
            try { if (Directory.Exists(_kb)) Directory.Delete(_kb, true); } catch { }
        }

        private void WriteConnectionFile(string xml)
        {
            File.WriteAllText(Path.Combine(_kb, "knowledgebase.connection"), xml);
        }

        [Fact]
        public void IntegratedSecurityTrue_AddsSspiAndSendsNoCredentials()
        {
            WriteConnectionFile(
                "<ConnectionInformation><ServerInstance>SQLSRV</ServerInstance>" +
                "<DBName>MyKb</DBName><IntegratedSecurity>True</IntegratedSecurity></ConnectionInformation>");

            string conn = KbConnectionString.Build(_kb, "test");

            Assert.Equal("Server=SQLSRV;Database=MyKb;Integrated Security=SSPI;TrustServerCertificate=true;Connection Timeout=5", conn);
        }

        [Theory]
        [InlineData("False")]
        [InlineData("false")]
        [InlineData("")]
        public void IntegratedSecurityNotTrue_OmitsTheSspiClause(string integrated)
        {
            WriteConnectionFile(
                "<ConnectionInformation><ServerInstance>SQLSRV</ServerInstance>" +
                "<DBName>MyKb</DBName><IntegratedSecurity>" + integrated + "</IntegratedSecurity></ConnectionInformation>");

            string conn = KbConnectionString.Build(_kb, "test");

            Assert.Equal("Server=SQLSRV;Database=MyKb;TrustServerCertificate=true;Connection Timeout=5", conn);
        }

        [Fact]
        public void MissingIntegratedSecurityElement_IsTreatedAsNotIntegrated()
        {
            WriteConnectionFile(
                "<ConnectionInformation><ServerInstance>SQLSRV</ServerInstance><DBName>MyKb</DBName></ConnectionInformation>");

            string conn = KbConnectionString.Build(_kb, "test");

            Assert.DoesNotContain("Integrated Security", conn);
        }

        [Fact]
        public void MissingServerOrDatabase_ReturnsNull()
        {
            WriteConnectionFile(
                "<ConnectionInformation><DBName>MyKb</DBName><IntegratedSecurity>True</IntegratedSecurity></ConnectionInformation>");
            Assert.Null(KbConnectionString.Build(_kb, "test"));

            WriteConnectionFile(
                "<ConnectionInformation><ServerInstance>SQLSRV</ServerInstance><IntegratedSecurity>True</IntegratedSecurity></ConnectionInformation>");
            Assert.Null(KbConnectionString.Build(_kb, "test"));
        }

        [Fact]
        public void MissingConnectionFile_ReturnsNull()
        {
            Assert.Null(KbConnectionString.Build(_kb, "test"));
        }

        [Fact]
        public void MalformedXml_ReturnsNullInsteadOfThrowing()
        {
            // Both original copies caught every exception and returned null; a
            // caller that reaches for the connection string must never see an
            // XML parse exception from a corrupt KB file.
            WriteConnectionFile("<ConnectionInformation><ServerInstance>unclosed");

            Assert.Null(KbConnectionString.Build(_kb, "test"));
        }

        [Fact]
        public void BothOriginalCallers_DelegateToTheSingleParser()
        {
            // Source-level guard: the parse must not be re-inlined into a caller.
            // The IntegratedSecurity decision is only safe while there is one copy.
            foreach (string fileName in new[]
            {
                "SdtModelPropagation.cs",
                "WebFormCompositionRepair.cs",
            })
            {
                string src = RepoSource.Read("src", "GxMcp.Worker", "Helpers", fileName);
                Assert.Contains("KbConnectionString.Build(kbPath,", src);
                Assert.DoesNotContain("ServerInstance", src);
            }

            string helper = RepoSource.Read("src", "GxMcp.Worker", "Helpers", "KbConnectionString.cs");
            // One read of each field and one emission of the SSPI clause. The raw
            // word "IntegratedSecurity" also appears in the class comment, so the
            // counts are pinned on the XPath and the emitted clause instead.
            Assert.Equal(1, CountOccurrences(helper, "/ConnectionInformation/IntegratedSecurity"));
            Assert.Equal(1, CountOccurrences(helper, "Integrated Security=SSPI"));
            Assert.Equal(1, CountOccurrences(helper, "/ConnectionInformation/ServerInstance"));
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, index = 0;
            while ((index = haystack.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

    }

    /// <summary>
    /// The dimension validation and the envelope it produces were pasted at each
    /// add/modify entry point. These pin the pass-through: the code, message, hint
    /// and extra come from <see cref="VariableDimensionSupport.TryValidate"/>
    /// unchanged, and a valid request produces no envelope at all.
    /// </summary>
    public class DimensionValidationFailureTests
    {
        [Fact]
        public void ValidRequest_ProducesNoEnvelope()
        {
            Assert.Null(WriteService.DimensionValidationFailure(1, new JArray(10), null, "MyPanel"));
            Assert.Null(WriteService.DimensionValidationFailure(2, new JArray(3, 4), null, "MyPanel"));
            Assert.Null(WriteService.DimensionValidationFailure(null, null, null, "MyPanel"));
            Assert.Null(WriteService.DimensionValidationFailure(null, null, true, "MyPanel"));
        }

        [Fact]
        public void OutOfRangeDimensions_ForwardTheValidatorEnvelopeVerbatim()
        {
            var json = JObject.Parse(WriteService.DimensionValidationFailure(5, null, null, "MyPanel"));

            Assert.Equal("MyPanel", json["target"]?.ToString());
            Assert.Equal("InvalidVariableDimensions", json["error"]?["code"]?.ToString());
            Assert.Equal("Variable dimensions must be 1 (vector) or 2 (matrix).",
                json["error"]?["message"]?.ToString());
            Assert.False(string.IsNullOrWhiteSpace(json["error"]?["hint"]?.ToString()));
            Assert.Equal(5, json["dimensions"]?.ToObject<int>());
        }

        [Fact]
        public void SizesWithoutDimensions_ReportsTheSizePayload()
        {
            var sizes = new JArray(4, 5);
            var json = JObject.Parse(WriteService.DimensionValidationFailure(null, sizes, null, "MyPanel"));

            Assert.Equal("InvalidVariableDimensions", json["error"]?["code"]?.ToString());
            Assert.Contains("dimensionSizes requires dimensions", json["error"]?["message"]?.ToString());
            Assert.Equal(2, ((JArray)json["dimensionSizes"])?.Count);
        }

        [Fact]
        public void EveryScalarEntryPoint_UsesTheSharedGuard()
        {
            // Source-level guard: the three add/modify entry points must not drift
            // back to hand-rolled out-parameter plumbing. The batch path is a
            // different contract (index-prefixed message, bool + out-param) and
            // stays separate.
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "WriteService.Variables.cs");

            Assert.Equal(3, CountOccurrences(src, "DimensionValidationFailure(dimensions, dimensionSizes, collection, target)"));
            Assert.Equal(1, CountOccurrences(src, "if (!VariableDimensionSupport.TryValidate(dimensions, sizes, collection,"));
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, index = 0;
            while ((index = haystack.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

    }
}
