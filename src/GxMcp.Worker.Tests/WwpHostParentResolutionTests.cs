using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issue #413: a same-named Folder/Table must never be taken as the WWP parent.
    public class WwpHostParentResolutionTests
    {
        private const string Guid = "1db606f2-af09-4cf9-a3b5-b481519d28f6";

        [Theory]
        [InlineData("Transaction", true)]
        [InlineData("WebPanel", true)]
        [InlineData("Folder", false)]
        [InlineData("Table", false)]
        [InlineData("Module", false)]
        [InlineData("Attribute", false)]
        [InlineData("Domain", false)]
        [InlineData(null, false)]
        public void OnlyTransactionAndWebPanelAreParents(string type, bool expected)
            => Assert.Equal(expected, WwpProjectionHelper.IsAcceptedParentType(type));

        [Fact]
        public void NameIsReadFromTransactionAttributeFirst()
        {
            string xml = "<transaction transaction=\"" + Guid + "-Sales.Order\"/>";
            var names = WwpProjectionHelper.ParentNameCandidates("WorkWithPlusOrder", xml);
            Assert.Equal(new[] { "Sales.Order", "Order" }, names);
        }

        [Fact]
        public void WebPanelInstanceFallsBackToConvention()
        {
            var names = WwpProjectionHelper.ParentNameCandidates("WorkWithPlusOrderEntry", "<instance/>");
            Assert.Equal(new[] { "OrderEntry" }, names);
        }

        [Fact]
        public void MalformedAttributeOrXmlFallsBackToConvention()
        {
            Assert.Equal(new[] { "X" }, WwpProjectionHelper.ParentNameCandidates("WorkWithPlusX", "<t transaction=\"nope\"/>"));
            Assert.Equal(new[] { "X" }, WwpProjectionHelper.ParentNameCandidates("WorkWithPlusX", "<not xml"));
            Assert.Empty(WwpProjectionHelper.ParentNameCandidates("Other", null));
        }
    }
}
