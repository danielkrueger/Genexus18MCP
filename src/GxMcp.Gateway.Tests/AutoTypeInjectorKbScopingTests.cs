using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Plan 038: AutoTypeInjector's name→type cache must be scoped per KB alias so
    /// two KBs open in the same gateway can't leak each other's resolutions (e.g.
    /// "Customer" = Transaction in KB-A, Business Component in KB-B).
    /// </summary>
    [Collection("AutoTypeInjectorState")]
    public class AutoTypeInjectorKbScopingTests
    {
        private const string KbA = "kb-a";
        private const string KbB = "kb-b";

        public AutoTypeInjectorKbScopingTests()
        {
            AutoTypeInjector.ClearAll();
        }

        [Fact]
        public void TryInject_TwoKbsWithSameName_ResolveToTheirOwnType_NoCrossContamination()
        {
            AutoTypeInjector.PrimeIndex(KbA, new (string, string?)[] { ("Customer", "Transaction") });
            AutoTypeInjector.PrimeIndex(KbB, new (string, string?)[] { ("Customer", "BusinessComponent") });
            AutoTypeInjector.PrimeToolAcceptsType("genexus_read", true);

            var argsA = new JObject { ["name"] = "Customer" };
            bool resultA = AutoTypeInjector.TryInject(KbA, "genexus_read", argsA, out string injectedA);

            var argsB = new JObject { ["name"] = "Customer" };
            bool resultB = AutoTypeInjector.TryInject(KbB, "genexus_read", argsB, out string injectedB);

            Assert.True(resultA);
            Assert.Equal("Transaction", injectedA);
            Assert.Equal("Transaction", argsA["type"]?.ToString());

            Assert.True(resultB);
            Assert.Equal("BusinessComponent", injectedB);
            Assert.Equal("BusinessComponent", argsB["type"]?.ToString());
        }

        [Fact]
        public void TryInject_SingleKb_RegressionUnchanged()
        {
            AutoTypeInjector.PrimeIndex(KbA, new (string, string?)[] { ("WPMain", "WebPanel") });
            AutoTypeInjector.PrimeToolAcceptsType("genexus_read", true);

            var args = new JObject { ["name"] = "WPMain" };
            bool result = AutoTypeInjector.TryInject(KbA, "genexus_read", args, out string injected);

            Assert.True(result);
            Assert.Equal("WebPanel", injected);
        }

        [Fact]
        public void ClearAll_WithAlias_OnlyClearsThatKb()
        {
            AutoTypeInjector.PrimeIndex(KbA, new (string, string?)[] { ("Customer", "Transaction") });
            AutoTypeInjector.PrimeIndex(KbB, new (string, string?)[] { ("Customer", "BusinessComponent") });
            AutoTypeInjector.PrimeToolAcceptsType("genexus_read", true);

            AutoTypeInjector.ClearAll(KbA);

            var argsA = new JObject { ["name"] = "Customer" };
            bool resultA = AutoTypeInjector.TryInject(KbA, "genexus_read", argsA, out _);
            Assert.False(resultA); // KB-A's map was cleared

            var argsB = new JObject { ["name"] = "Customer" };
            bool resultB = AutoTypeInjector.TryInject(KbB, "genexus_read", argsB, out string injectedB);
            Assert.True(resultB); // KB-B untouched
            Assert.Equal("BusinessComponent", injectedB);
        }

        [Fact]
        public void CompleteName_ScopedPerKb_NoCrossContamination()
        {
            AutoTypeInjector.PrimeIndex(KbA, new (string, string?)[] { ("CustomerOrder", "Transaction") });
            AutoTypeInjector.PrimeIndex(KbB, new (string, string?)[] { ("CustomerInvoice", "BusinessComponent") });

            var matchesA = AutoTypeInjector.CompleteName(KbA, "Customer");
            var matchesB = AutoTypeInjector.CompleteName(KbB, "Customer");

            Assert.Contains("CustomerOrder", matchesA);
            Assert.DoesNotContain("CustomerInvoice", matchesA);

            Assert.Contains("CustomerInvoice", matchesB);
            Assert.DoesNotContain("CustomerOrder", matchesB);
        }
    }
}
