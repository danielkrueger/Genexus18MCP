using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issue #415: empty tables and action groups are containers, and a repeated bare
    // name is disambiguated by a path of named ancestors.
    public class WwpFormContainerPathTests
    {
        private const string Transaction =
            "<transaction>" +
            "<tab name='General'><table name='Table'><actionGroup name='Actions'/></table></tab>" +
            "<tab name='Lines'><table name='Table'><actionGroup name='Actions'/></table></tab>" +
            "</transaction>";

        private static JObject Add(XDocument doc, string container) => WwpActionService.Apply(doc, "add_user_action", new JObject
        {
            ["containerName"] = container, ["actionName"] = "Go", ["caption"] = "Go"
        }, null);

        [Fact]
        public void EmptyTableIsFound()
        {
            var doc = XDocument.Parse("<instance><table name='TableContent'/></instance>");
            Assert.Null(Add(doc, "TableContent")["error"]);
            Assert.Single(doc.Descendants("userAction"));
        }

        [Fact]
        public void PathSelectsOneGroupAmongRepeatedChains()
        {
            var doc = XDocument.Parse(Transaction);
            Assert.Null(Add(doc, "General/Table/Actions")["error"]);
            Assert.Single(doc.Descendants("userAction"));
            Assert.Equal("General", doc.Descendants("userAction").Single().Ancestors("tab").Single().Attribute("name")!.Value);
        }

        [Fact]
        public void AmbiguousBareNameListsEachPath()
        {
            JObject result = Add(XDocument.Parse(Transaction), "Actions");
            Assert.Equal("FormActionContainerAmbiguous", result["code"]?.ToString());
            Assert.Equal(new[] { "General/Table/Actions", "Lines/Table/Actions" },
                result["matchingContainers"]!.Select(c => c["path"]!.ToString()));
        }

        [Fact]
        public void NotFoundListsAvailablePaths()
        {
            JObject result = Add(XDocument.Parse(Transaction), "Nope/Actions");
            Assert.Equal("FormActionContainerNotFound", result["code"]?.ToString());
            Assert.Contains("Lines/Table/Actions", result["availablePaths"]!.Values<string>());
        }

        [Fact]
        public void ControlNameStillMatchesAndPathIsSegmentExact()
        {
            Assert.True(WwpActionService.ContainerPathMatches(new[] { "Tabs", "General" }, "General", null, "general"));
            Assert.True(WwpActionService.ContainerPathMatches(new[] { "A", "B", "C" }, "C", null, "B/C"));
            Assert.False(WwpActionService.ContainerPathMatches(new[] { "A", "XB", "C" }, "C", null, "B/C"));
        }
    }
}
