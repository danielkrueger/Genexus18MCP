using System;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issue #404: the text form of a Query structure is split into the four lists the SDK's
    // FromSerializedStrings takes, in its order (elements, parameters, filters, orders).
    public class QueryStructureTextTests
    {
        [Fact]
        public void Parse_SplitsEachSectionIntoItsList_IgnoringBlankLinesAndCarriageReturns()
        {
            var lists = QueryStructureText.Parse("[Elements]\r\n<e1/>\r\n\r\n<e2/>\r\n[Parameters]\r\n<p1/>\r\n[Filters]\r\n[Orders]\r\n<o1/>");

            Assert.Equal(new[] { "<e1/>", "<e2/>" }, lists[0]);
            Assert.Equal(new[] { "<p1/>" }, lists[1]);
            Assert.Empty(lists[2]);
            Assert.Equal(new[] { "<o1/>" }, lists[3]);
        }

        [Fact]
        public void Parse_RejectsContentBeforeTheFirstSection()
        {
            Assert.Throws<FormatException>(() => QueryStructureText.Parse("<e1/>\n[Elements]"));
        }

        [Fact]
        public void Parse_TreatsAnUnknownBracketedLineAsContentOfTheCurrentSection()
        {
            var lists = QueryStructureText.Parse("[Elements]\n[Other]\n");

            Assert.Equal(new[] { "[Other]" }, lists[0]);
        }
    }
}
