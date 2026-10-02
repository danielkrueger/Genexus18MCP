using System.Xml.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ReportHomonymControlTests
    {
        private const string ReportXml =
            "<Report><PrintBlock Name=\"PbA\"><Control ControlName=\"lblSame\" Caption=\"a\"/><Control ControlName=\"lblOnlyA\"/></PrintBlock>" +
            "<PrintBlock Name=\"PbB\"><Control ControlName=\"lblSame\" Caption=\"b\"/></PrintBlock></Report>";

        [Fact]
        public void BareHomonymNameIsRefusedWithMatchesListed()
        {
            var doc = XDocument.Parse(ReportXml);
            var json = JObject.Parse(LayoutService.AmbiguousControlError(doc, "SampleProc", "lblSame"));
            Assert.Equal("AmbiguousControl", json["error"]?["code"]?.ToString());
            var text = json.ToString();
            Assert.Contains("PbA", text);
            Assert.Contains("PbB", text);
            Assert.Contains("/Report[1]/PrintBlock[2]/Control[1]", text);
        }

        [Fact]
        public void UniqueNameAndPathAreNotRefused()
        {
            var doc = XDocument.Parse(ReportXml);
            Assert.Null(LayoutService.AmbiguousControlError(doc, "SampleProc", "lblOnlyA"));
            Assert.Null(LayoutService.AmbiguousControlError(doc, "SampleProc", "/Report/PrintBlock[2]/Control[1]"));
            Assert.Equal("b", (string)LayoutService.FindControlElement(doc, "/Report/PrintBlock[2]/Control[1]").Attribute("Caption"));
        }

        [Fact]
        public void NonReportLayoutIsNotAffected()
        {
            var doc = XDocument.Parse("<Form><Table><Control ControlName=\"x\"/><Control ControlName=\"x\"/></Table></Form>");
            Assert.Null(LayoutService.AmbiguousControlError(doc, "SampleTrn", "x"));
        }
    }
}
