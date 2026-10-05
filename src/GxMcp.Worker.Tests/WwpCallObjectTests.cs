using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issue #421: add_user_action callObject / popup / parameters.
    public class WwpCallObjectTests
    {
        private const string Reference = "c9584656-94b6-4ccd-890f-332d11fc2c25-Sales.ReceiveOrderWP";

        private static JObject Args(string json)
        {
            var args = JObject.Parse(json);
            args["actionName"] = "Receive";
            args["caption"] = "Receive";
            args["containerName"] = "TableActions";
            return args;
        }

        private static XDocument Instance() => XDocument.Parse("<instance><table name='TableActions'/></instance>");

        [Fact]
        public void StoresTheIdeShapeWithNoDerivedEvent()
        {
            var doc = Instance();
            JObject args = Args("{callObject:'WebPanel:ReceiveOrderWP',popup:true,parameters:['CompanyId','&OrderId']}");
            args["_callObjectReference"] = Reference;

            JObject result = WwpActionService.Apply(doc, "add_user_action", args, null);

            Assert.True(result["error"] == null, result.ToString());
            Assert.Equal(JTokenType.Null, result["event"]!.Type);
            XElement action = doc.Descendants("userAction").Single();
            Assert.Equal(Reference, action.Attribute("gxobject")!.Value);
            Assert.Equal("True", action.Attribute("popup")!.Value);
            Assert.Equal(new[] { "CompanyId", "&OrderId" }, action.Element("parameters")!.Elements("parameter").Select(p => p.Attribute("name")!.Value));
            Assert.Null(action.Attribute("event"));
        }

        [Theory]
        [InlineData("{popup:true}")]
        [InlineData("{parameters:['A']}")]
        public void PopupAndParametersWithoutCallObjectAreRefused(string json)
            => Assert.Equal("FormActionCallObjectRequired", WwpActionService.Apply(Instance(), "add_user_action", Args(json), null)["code"]?.ToString());

        [Theory]
        [InlineData("{callObject:'X',popup:'maybe'}", "InvalidPopup")]
        [InlineData("{callObject:'X',parameters:'A'}", "InvalidParameters")]
        [InlineData("{callObject:'X',parameters:['A','']}", "InvalidParameters")]
        [InlineData("{callObject:'X',procedure:'P'}", "FormActionProcedureConflict")]
        public void InvalidCombinationsAreRefused(string json, string code)
        {
            JObject args = Args(json);
            args["_callObjectReference"] = Reference;
            Assert.Equal(code, WwpActionService.Apply(Instance(), "add_user_action", args, null)["code"]?.ToString());
        }

        [Fact]
        public void UnresolvedCallObjectIsRefused()
            => Assert.Equal("CallObjectUnresolved", WwpActionService.Apply(Instance(), "add_user_action", Args("{callObject:'X'}"), null)["code"]?.ToString());

        [Theory]
        [InlineData("WebPanel:ReceiveOrderWP", "WebPanel", "ReceiveOrderWP")]
        [InlineData("ReceiveOrderWP", null, "ReceiveOrderWP")]
        public void CallObjectIsNameOrTypeColonName(string spec, string type, string name)
        {
            WwpActionService.SplitCallObject(spec, out string t, out string n);
            Assert.Equal((type, name), (t, n));
        }

        [Fact]
        public void OnlyOpenableObjectsAreCallable()
        {
            Assert.True(WwpActionService.IsCallableType("WebPanel"));
            Assert.False(WwpActionService.IsCallableType("Folder"));
            Assert.False(WwpActionService.IsCallableType("Table"));
        }

        [Fact]
        public void VerificationRejectsLostObjectPopupOrReorderedParameters()
        {
            XElement Persisted(string gx, string popup, params string[] parameters) =>
                XElement.Parse("<userAction gxobject='" + gx + "' popup='" + popup + "'><parameters>" +
                    string.Concat(parameters.Select(p => "<parameter name='" + p + "'/>")) + "</parameters></userAction>");
            var wanted = new[] { "A", "B" };

            Assert.Null(WwpActionService.VerifyCallObject(Persisted(Reference, "True", "A", "B"), Reference, true, wanted));
            Assert.NotNull(WwpActionService.VerifyCallObject(Persisted("other", "True", "A", "B"), Reference, true, wanted));
            Assert.NotNull(WwpActionService.VerifyCallObject(Persisted(Reference, "False", "A", "B"), Reference, true, wanted));
            Assert.NotNull(WwpActionService.VerifyCallObject(Persisted(Reference, "True", "B", "A"), Reference, true, wanted));
        }
    }
}
