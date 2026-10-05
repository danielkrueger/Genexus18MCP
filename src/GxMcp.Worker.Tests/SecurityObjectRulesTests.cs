using System.Linq;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issue #427: each rule fires only under the IDE's applicability condition.
    public class SecurityObjectRulesTests
    {
        private static ObjectAuditFacts Facts(string type, string useEncryption = null, string callProtocol = null, string source = null)
        {
            var facts = new ObjectAuditFacts { TypeName = type, Source = source };
            if (useEncryption != null) facts.Properties["USE_ENCRYPTION"] = useEncryption;
            if (callProtocol != null) facts.Properties["CALL_PROTOCOL"] = callProtocol;
            return facts;
        }

        private static string[] Rules(ObjectAuditFacts facts)
            => SecurityObjectRules.Evaluate(facts).Select(f => (string)f["rule"]).ToArray();

        [Theory]
        [InlineData("Transaction")]
        [InlineData("WebPanel")]
        public void Rule100FiresOnTransactionAndWebPanelWithoutAProtocol(string type)
            => Assert.Equal(new[] { "IDE#100" }, Rules(Facts(type, useEncryption: "NO")));

        [Fact]
        public void Rule100OnAProcedureNeedsTheHttpProtocol()
        {
            Assert.Empty(Rules(Facts("Procedure", useEncryption: "NO")));
            Assert.Empty(Rules(Facts("Procedure", useEncryption: "NO", callProtocol: "INTERNAL")));
            Assert.Equal(new[] { "IDE#100" }, Rules(Facts("Procedure", useEncryption: "NO", callProtocol: "HTTP")));
        }

        [Theory]
        [InlineData("SESSION")]
        [InlineData("SITE")]
        [InlineData("UMPV")]
        [InlineData(null)]
        public void Rule100DoesNotFireWhenEncryptionIsOnOrInherited(string value)
            => Assert.Empty(Rules(Facts("Transaction", useEncryption: value)));

        [Fact]
        public void Rule100DoesNotApplyToOtherTypes()
            => Assert.Empty(Rules(Facts("DataProvider", useEncryption: "NO", callProtocol: "HTTP")));

        [Fact]
        public void Rule108FiresOnNativeBlocksWithTheirLine()
        {
            var finding = SecurityObjectRules.Evaluate(Facts("Procedure", source: "x = 1\ncsharp\n  Foo();\nendcsharp\n")).Single();
            Assert.Equal("IDE#108", (string)finding["rule"]);
            Assert.Equal(2, (int)finding["line"]);
        }

        [Fact]
        public void Rule108IgnoresCommentedAndPlainSource()
        {
            Assert.Empty(Rules(Facts("Procedure", source: "// csharp\nmessage('java is fun')")));
            Assert.Empty(Rules(Facts("Procedure", source: "&javaVersion = 1")));
        }

        [Fact]
        public void RulesNotEvaluatedAreNamedNotImplied()
        {
            Assert.DoesNotContain(SecurityObjectRules.NotCoveredRules, r => SecurityObjectRules.CoveredRules.Contains(r));
            Assert.NotEmpty(SecurityObjectRules.NotCoveredRules);
        }
    }
}
