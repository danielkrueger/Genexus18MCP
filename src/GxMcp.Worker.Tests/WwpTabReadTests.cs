using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issue #426: list_tabs reports what the instance has; conventions only what all tabs share.
    public class WwpTabReadTests
    {
        private const string Instance =
            "<transaction><tabs>" +
            "<gridTab ControlName='Lines' title='Lines' tabClass='Tab' defaultX='1'><table/></gridTab>" +
            "<gridTab ControlName='Notes' title='Notes' tabClass='Tab'><table/></gridTab>" +
            "<webComponentTab ControlName='Docs' title='Docs' gxobject='x-Docs'/>" +
            "<tabularTab ControlName='Totals' title='Totals'/>" +
            "</tabs></transaction>";

        [Fact]
        public void EveryTabKindIsReportedWithItsPosition()
        {
            JObject result = WwpActionService.ListTabs(XDocument.Parse(Instance));
            var tabs = result["tabs"]!.Select(t => ((string)t["name"]!, (string)t["kind"]!, (int)t["position"]!)).ToArray();
            Assert.Equal(new[] { ("Lines", "grid", 0), ("Notes", "grid", 1), ("Docs", "webcomponent", 2), ("Totals", "tabular", 3) }, tabs);
            Assert.Equal(4, (int)result["tabCount"]!);
        }

        [Fact]
        public void KeyAttributesSkipDefaultsAndOrdering()
        {
            JObject first = (JObject)WwpActionService.ListTabs(XDocument.Parse(Instance))["tabs"]![0]!;
            Assert.Null(first["attributes"]!["defaultX"]);
            Assert.Equal("Tab", (string?)first["attributes"]!["tabClass"]);
        }

        [Fact]
        public void ConventionsReportOnlyWhatAllTabsOfAKindShare()
        {
            JObject conventions = (JObject)WwpActionService.ListTabs(XDocument.Parse(Instance))["conventions"]!;
            Assert.Equal("Tab", (string?)conventions["grid"]!["tabClass"]);
            Assert.Null(conventions["grid"]!["title"]);          // differs between the two grid tabs
            Assert.Null(conventions["grid"]!["ControlName"]);
            Assert.Null(conventions["webcomponent"]);            // one tab has nothing to agree with
        }

        [Fact]
        public void InstanceWithoutTabsIsEmptyNotAnError()
        {
            JObject result = WwpActionService.ListTabs(XDocument.Parse("<transaction/>"));
            Assert.Empty(result["tabs"]!);
            Assert.Empty(result["conventions"]!);
        }
    }
}
