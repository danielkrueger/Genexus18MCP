using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// A default Transaction WebForm: gxButton carries ControlName and no id, gxTextBlock
    /// carries id and no ControlName. A Caption set on the button once renamed it on save
    /// (ControlName -> Button1, Event/Class dropped) and the read-back could not find it.
    /// </summary>
    public class WebFormTransactionControlLookupTests
    {
        private const string Before =
            "<GxMultiForm><Form><form>"
            + "<gxButton Event=\"Enter\" CaptionExpression=\"c\" ControlName=\"btn_enter\" Class=\"cls-46\" />"
            + "<gxTextBlock id=\"TextBlockSampleTrnNom\" CaptionExpression=\"t\" />"
            + "<gxAttribute AttID=\"att:16069\" />"
            + "<IMG id=\"btn_first\" />"
            + "</form></Form></GxMultiForm>";

        [Fact]
        public void Button_With_ControlName_And_No_Id_Is_Found()
        {
            var el = LayoutService.FindControlElement(XDocument.Parse(Before), "BTN_ENTER");
            Assert.Equal("gxButton", el.Name.LocalName);
            Assert.Equal("Enter", (string)el.Attribute("Event"));
            Assert.Equal("cls-46", (string)el.Attribute("Class"));
        }

        [Fact]
        public void TextBlock_With_Id_And_No_ControlName_Is_Found()
        {
            Assert.NotNull(LayoutService.FindControlElement(XDocument.Parse(Before), "TextBlockSampleTrnNom"));
        }

        [Fact]
        public void A_Rename_On_Save_Is_Reported_As_Missing_Plus_Appeared()
        {
            var after = XDocument.Parse(
                "<GxMultiForm><Form><form>"
                + "<gxButton ControlName=\"Button1\" Caption=\"Confirm\" />"
                + "<gxTextBlock id=\"TextBlockSampleTrnNom\" CaptionExpression=\"t\" />"
                + "<IMG id=\"btn_first\" /></form></Form></GxMultiForm>");
            var drift = LayoutService.DescribeIdentityDrift(Before, after);
            Assert.Equal(new[] { "gxButton:btn_enter" }, drift["missing"].Select(x => (string)x).ToArray());
            Assert.Equal(new[] { "gxButton:Button1" }, drift["appeared"].Select(x => (string)x).ToArray());
        }

        [Fact]
        public void An_Intact_Document_Has_No_Drift()
        {
            var drift = LayoutService.DescribeIdentityDrift(Before, XDocument.Parse(Before));
            Assert.Empty(drift["missing"]);
            Assert.Empty(drift["appeared"]);
        }
    }
}

namespace GxMcp.Worker.Tests
{
    public class WebFormTypedWriteVerifyTests
    {
        private static System.Xml.XmlElement Button(string xml)
        {
            var d = new System.Xml.XmlDocument(); d.LoadXml(xml); return d.DocumentElement;
        }

        private static readonly System.Collections.Generic.List<GxMcp.Worker.Helpers.WebFormPropertyDelta> Wanted =
            new System.Collections.Generic.List<GxMcp.Worker.Helpers.WebFormPropertyDelta>
            { new GxMcp.Worker.Helpers.WebFormPropertyDelta { ControlName = "btn_enter", PropertyName = "Caption", Value = "Confirm" } };

        [Fact]
        public void A_Value_The_Sdk_Reset_Is_A_Mismatch_So_The_Typed_Write_Falls_Back()
        {
            var el = Button("<gxButton ControlName=\"btn_enter\" Caption=\"\" />");
            Assert.Equal(new[] { "Caption" }, GxMcp.Worker.Helpers.WebFormTypedPropertyWriter.FindVerifyMismatches(el, Wanted).ToArray());
        }

        [Fact]
        public void A_Matching_Value_Is_Not_A_Mismatch()
        {
            var el = Button("<gxButton ControlName=\"btn_enter\" Caption=\"Confirm\" />");
            Assert.Empty(GxMcp.Worker.Helpers.WebFormTypedPropertyWriter.FindVerifyMismatches(el, Wanted));
        }

        [Fact]
        public void A_Missing_Element_Is_A_Mismatch()
        {
            Assert.Single(GxMcp.Worker.Helpers.WebFormTypedPropertyWriter.FindVerifyMismatches(null, Wanted));
        }
    }
}
