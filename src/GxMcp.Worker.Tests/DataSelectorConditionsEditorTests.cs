using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class DataSelectorConditionsEditorTests
    {
        // A whole-list replace cannot preserve a nested AND/OR group: the request format
        // is one condition per line and carries no grouping, so applying it would empty the
        // nested level and leave it behind. The write refuses instead of flattening, and
        // this is the predicate that decides. Before this guard the write proceeded and the
        // grouping was lost silently, so "flat" must be the only shape that proceeds.
        [Fact]
        public void NestedConditionExpressions_IsEmptyForAFlatConditionList()
        {
            var conditions = new List<KeyValuePair<string, bool>>
            {
                new KeyValuePair<string, bool>("SampleId = &SampleId", false),
                new KeyValuePair<string, bool>("SampleDate >= &From", false)
            };

            Assert.Empty(WriteService.NestedConditionExpressions(conditions));
        }

        [Fact]
        public void NestedConditionExpressions_ReportsTheGroupedConditions()
        {
            var conditions = new List<KeyValuePair<string, bool>>
            {
                new KeyValuePair<string, bool>("SampleId = &SampleId", false),
                new KeyValuePair<string, bool>("SampleDate >= &From", true),
                new KeyValuePair<string, bool>("SampleDate <= &To", true)
            };

            var nested = WriteService.NestedConditionExpressions(conditions);

            Assert.Equal(2, nested.Count);
            Assert.Contains("SampleDate >= &From", nested);
            Assert.Contains("SampleDate <= &To", nested);
            Assert.DoesNotContain("SampleId = &SampleId", nested);
        }

        [Fact]
        public void NestedConditionExpressions_ToleratesNullAndEmptyInput()
        {
            Assert.Empty(WriteService.NestedConditionExpressions(null));
            Assert.Empty(WriteService.NestedConditionExpressions(
                new List<KeyValuePair<string, bool>> { new KeyValuePair<string, bool>(null, true) }));
        }
        [Theory]
        [InlineData("Conditions")]
        [InlineData("conditions")]
        [InlineData(" CONDITIONS ")]
        public void IsConditionsPart_IsCaseInsensitive(string name)
        {
            Assert.True(DataSelectorConditionsEditor.IsConditionsPart(name));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Source")]
        [InlineData("DataSelectorStructure")]
        public void IsConditionsPart_RejectsOtherParts(string name)
        {
            Assert.False(DataSelectorConditionsEditor.IsConditionsPart(name));
        }

        [Fact]
        public void Parse_SplitsLinesStripsTrailingSemicolonAndDropsBlanks()
        {
            var list = DataSelectorConditionsEditor.Parse("SampleId = 1;\r\n\r\n  SampleName = &SampleName ;;  \nSampleFlag\r");

            Assert.Equal(new[] { "SampleId = 1", "SampleName = &SampleName", "SampleFlag" }, list);
        }

        [Fact]
        public void Parse_EmptyContentMeansNoConditions()
        {
            Assert.Empty(DataSelectorConditionsEditor.Parse(null));
            Assert.Empty(DataSelectorConditionsEditor.Parse("  \r\n ; \n"));
        }

        [Fact]
        public void NormalizeExpression_KeepsSemicolonInsideLiteral()
        {
            Assert.Equal("SampleName = 'a;b'", DataSelectorConditionsEditor.NormalizeExpression("SampleName = 'a;b';"));
        }

        [Fact]
        public void SameConditions_IgnoresTrailingNewlineAndSemicolonButNotOrder()
        {
            var stored = new[] { "SampleId = &SampleId\n", "SampleName = 'x'\n" };

            Assert.True(DataSelectorConditionsEditor.SameConditions(stored, new[] { "SampleId = &SampleId;", "SampleName = 'x'" }));
            Assert.False(DataSelectorConditionsEditor.SameConditions(stored, new[] { "SampleName = 'x'", "SampleId = &SampleId" }));
            Assert.False(DataSelectorConditionsEditor.SameConditions(stored, new[] { "SampleId = &SampleId" }));
        }

        [Fact]
        public void Canonicalize_MatchesJoinOfSdkExpressions()
        {
            Assert.Equal(
                DataSelectorConditionsEditor.Join(new[] { "SampleId = 1\n", "SampleName = 'x'\n" }),
                DataSelectorConditionsEditor.Canonicalize("SampleId = 1;\r\nSampleName = 'x'"));
        }

        [Fact]
        public void Read_ConditionsOnlyExposesEditableSourceThatRoundTripsThroughCanonicalize()
        {
            var snapshot = new DataSelectorReadService.Snapshot { Name = "SampleDs" };
            snapshot.Conditions.Add(new DataSelectorReadService.ExpressionSnapshot { Expression = "SampleId = &SampleId\n", Ordinal = 1 });
            snapshot.Conditions.Add(new DataSelectorReadService.ExpressionSnapshot { Expression = "SampleName = 'x'\n", Ordinal = 2 });

            JObject only = DataSelectorReadService.BuildResponse(snapshot, new[] { "conditions" });
            string source = (string)only["source"];

            Assert.Equal("SampleId = &SampleId\nSampleName = 'x'", source);
            Assert.Equal(source, DataSelectorConditionsEditor.Canonicalize(source));
            Assert.Equal(2, ((JArray)only["conditions"]).Count);
        }

        [Fact]
        public void Read_MultiPartDoesNotExposeSource()
        {
            var snapshot = new DataSelectorReadService.Snapshot { Name = "SampleDs" };
            snapshot.Conditions.Add(new DataSelectorReadService.ExpressionSnapshot { Expression = "SampleId = 1", Ordinal = 1 });

            Assert.Null(DataSelectorReadService.BuildResponse(snapshot, new[] { "conditions", "orders" })["source"]);
            Assert.Null(DataSelectorReadService.BuildResponse(snapshot, null)["source"]);
        }
    }
}
