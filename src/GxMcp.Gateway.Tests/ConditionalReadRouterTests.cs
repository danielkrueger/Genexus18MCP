using System;
using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Issue #357 — gateway-side routing for <c>genexus_read ifUnchangedSince</c>.
    /// <para>
    /// Two contracts are pinned here. First, the token reaches the Worker intact
    /// on the one form that supports it. Second — and this is the one that would
    /// fail silently — it is <em>rejected</em> on every form that has no single
    /// representation to bind to. A silently ignored token would let an agent
    /// believe it had a cheap re-read available and pay full cost every time,
    /// with nothing in the response to say so.
    /// </para>
    /// </summary>
    public class ConditionalReadRouterTests
    {
        private static JObject Route(JObject args)
        {
            var routed = new ObjectRouter().ConvertToolCall("genexus_read", args);
            Assert.NotNull(routed);
            return JObject.FromObject(routed!);
        }

        private static string UsageCode(Action act)
        {
            var ex = Assert.Throws<UsageException>(act);
            return ex.Code;
        }

        [Fact]
        public void SinglePartRead_ForwardsTheToken()
        {
            var jo = Route(new JObject
            {
                ["name"] = "Customer",
                ["part"] = "Source",
                ["ifUnchangedSince"] = "grc1.abc"
            });

            Assert.Equal("Read", jo["module"]!.ToString());
            Assert.Equal("ExtractSource", jo["action"]!.ToString());
            Assert.Equal("grc1.abc", jo["ifUnchangedSince"]!.ToString());
        }

        [Fact]
        public void SinglePartRead_WithoutToken_ForwardsNothing()
        {
            // Omitting the token must leave the routed command carrying no usable
            // token value, so an unconditional reader is unaffected. (JObject
            // materialization keeps the property as a JSON null rather than
            // dropping it; what matters downstream is that it reads as empty.)
            var jo = Route(new JObject { ["name"] = "Customer", ["part"] = "Source" });

            Assert.True(
                jo["ifUnchangedSince"] == null || jo["ifUnchangedSince"]!.Type == JTokenType.Null);
            Assert.False(jo["requireAuthoritativeRead"]!.ToObject<bool>());
        }

        [Fact]
        public void PaginationWindow_IsCarriedWithTheToken()
        {
            // offset/limit are part of what the token binds to, so a read that
            // changed the window must still route both halves.
            var jo = Route(new JObject
            {
                ["name"] = "Customer",
                ["part"] = "Source",
                ["offset"] = 200,
                ["limit"] = 200,
                ["ifUnchangedSince"] = "grc1.abc"
            });

            Assert.Equal(200, jo["offset"]!.ToObject<int>());
            Assert.Equal(200, jo["limit"]!.ToObject<int>());
            Assert.Equal("grc1.abc", jo["ifUnchangedSince"]!.ToString());
        }

        [Fact]
        public void AuthoritativeFlag_IsInternalAndDefaultsOff()
        {
            var jo = Route(new JObject
            {
                ["name"] = "Customer",
                ["part"] = "Source",
                ["_requireAuthoritativeRead"] = true
            });

            Assert.True(jo["requireAuthoritativeRead"]!.ToObject<bool>());
        }

        [Fact]
        public void BatchRead_RejectsTheToken()
        {
            string code = UsageCode(() => Route(new JObject
            {
                ["targets"] = new JArray("Customer", "Invoice"),
                ["ifUnchangedSince"] = "grc1.abc"
            }));

            Assert.Equal("ConditionalReadUnsupportedForm", code);
        }

        [Fact]
        public void MultiPartRead_RejectsTheToken()
        {
            string code = UsageCode(() => Route(new JObject
            {
                ["name"] = "Customer",
                ["parts"] = new JArray("Source", "Rules"),
                ["ifUnchangedSince"] = "grc1.abc"
            }));

            Assert.Equal("ConditionalReadUnsupportedForm", code);
        }

        [Theory]
        [InlineData("")]
        [InlineData("summary")]
        [InlineData("full")]
        [InlineData("all")]
        public void FullObjectRead_RejectsTheToken(string part)
        {
            var args = new JObject { ["name"] = "Customer", ["part"] = part, ["ifUnchangedSince"] = "grc1.abc" };
            Assert.Equal("ConditionalReadUnsupportedForm", UsageCode(() => Route(args)));
        }

        [Fact]
        public void FullObjectReadWithNoPartAtAll_RejectsTheToken()
        {
            var args = new JObject { ["name"] = "Customer", ["ifUnchangedSince"] = "grc1.abc" };
            Assert.Equal("ConditionalReadUnsupportedForm", UsageCode(() => Route(args)));
        }

        [Fact]
        public void BlankToken_IsNotARejection()
        {
            // An empty string carries no assertion; rejecting it would break a
            // caller that templated the field to "" for "no token".
            var jo = Route(new JObject
            {
                ["name"] = "Customer",
                ["part"] = "Source",
                ["ifUnchangedSince"] = "   "
            });

            Assert.Equal("ExtractSource", jo["action"]!.ToString());
        }

        [Fact]
        public void UnsupportedForms_RoutedCleanly_WhenNoTokenIsSupplied()
        {
            // The fail-closed rejection must not change the routes these forms had.
            Assert.Equal("BatchRead", Route(new JObject { ["targets"] = new JArray("Customer") })["action"]!.ToString());
            Assert.Equal("ExtractParts", Route(new JObject { ["name"] = "Customer", ["parts"] = new JArray("Source") })["action"]!.ToString());
            Assert.Equal("ExtractFullObject", Route(new JObject { ["name"] = "Customer" })["action"]!.ToString());
        }
    }
}