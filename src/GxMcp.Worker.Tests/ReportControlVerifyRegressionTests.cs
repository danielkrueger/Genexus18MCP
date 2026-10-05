using System.Drawing;
using System.Reflection;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // add_report_control reported ReportControlWriteVerificationFailed for controls the SDK had
    // persisted exactly as requested: geometry was verified against X/Y while the SDK read-back
    // projects Left/Top, and kind=variable asked for a ReportVariable type the SDK does not have.
    public class ReportControlVerifyRegressionTests
    {
        private static object Call(string name, params object[] args)
        {
            var method = typeof(LayoutService).GetMethod(name,
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(method);
            return method.Invoke(null, args);
        }

        [Theory]
        [InlineData("variable", "", "ReportAttribute")]
        [InlineData("", "ReportVariable", "ReportAttribute")]
        [InlineData("attribute", "", "ReportAttribute")]
        [InlineData("label", "", "ReportLabel")]
        public void VariableKindResolvesToTheSdkReportAttributeControl(string kind, string requestedType, string expected)
        {
            Assert.Equal(expected, (string)Call("ResolveReportControlType", kind, requestedType));
        }

        // GeneXus renames a ReportAttribute control after its reference on save
        // (VarTexto bound to &Texto persists as "&Texto"), so it is created and
        // verified under that name; other controls keep the requested name.
        [Theory]
        [InlineData("ReportAttribute", "VarTexto", "&Texto", "&Texto")]
        [InlineData("ReportAttribute", "AttCode", "SampleCode", "SampleCode")]
        [InlineData("ReportLabel", "LblTitle", null, "LblTitle")]
        [InlineData("ReportAttribute", "AttCode", "", "AttCode")]
        public void AttributeControlIsNamedAfterItsReference(string type, string name, string binding, string expected)
        {
            Assert.Equal(expected, (string)Call("EffectiveReportControlName", type, name, binding));
        }

        [Fact]
        public void VariableControlIsBoundThroughAttributeReference()
        {
            var control = (XElement)Call("CreateReportControl",
                "ReportAttribute", "VarTexto", "variable", "&Texto", string.Empty, new JObject());

            Assert.Equal("&Texto", (string)control.Attribute("AttributeReference"));
            Assert.Null(control.Attribute("ControlSource"));
        }

        [Fact]
        public void GeometryVerificationReadsTheLeftTopProjectionOfTheSdkReadBack()
        {
            var readBack = XElement.Parse("<Control Left=\"10\" Top=\"5\" Width=\"120\" Height=\"20\" />");
            var args = new JObject { ["left"] = 10, ["top"] = 5, ["width"] = 120, ["height"] = 20 };

            Assert.True((bool)Call("VerifyReportNumber", readBack, args, "left", new[] { "Left", "X" }));
            Assert.True((bool)Call("VerifyReportNumber", readBack, args, "top", new[] { "Top", "Y" }));
            Assert.True((bool)Call("VerifyReportNumber", readBack, args, "width", new[] { "Width" }));
            Assert.True((bool)Call("VerifyReportNumber", readBack, args, "height", new[] { "Height" }));

            var moved = new JObject { ["left"] = 11 };
            Assert.False((bool)Call("VerifyReportNumber", readBack, moved, "left", new[] { "Left", "X" }));
        }

        [Fact]
        public void FontVerificationReadsTheFontNameFontSizeProjection()
        {
            var readBack = XElement.Parse("<Control FontName=\"Arial\" FontSize=\"12\" />");

            Assert.True((bool)Call("ReportFontMatches", readBack, "fontName", "Arial"));
            Assert.True((bool)Call("ReportFontMatches", readBack, "font", "Arial"));
            Assert.True((bool)Call("ReportFontMatches", readBack, "fontSize", "12"));
            Assert.False((bool)Call("ReportFontMatches", readBack, "fontName", "Verdana"));
            Assert.False((bool)Call("ReportFontMatches", readBack, "fontSize", "8"));
        }

        [Fact]
        public void ComposeFontChangesOnlyTheRequestedPart()
        {
            var current = new Font("MS Sans Serif", 8f, FontStyle.Bold);

            var named = ReportLayoutHelper.ComposeFont(current, "FontName", "Arial");
            Assert.Equal("Arial", named.Name);
            Assert.Equal(8f, named.Size);
            Assert.Equal(FontStyle.Bold, named.Style);

            var sized = ReportLayoutHelper.ComposeFont(named, "FontSize", "12");
            Assert.Equal("Arial", sized.Name);
            Assert.Equal(12f, sized.Size);

            Assert.Null(ReportLayoutHelper.ComposeFont(current, "FontSize", "not-a-number"));
            Assert.Null(ReportLayoutHelper.ComposeFont(current, "Font", "[Font: Name=Arial, Size=12]"));
            Assert.Null(ReportLayoutHelper.ComposeFont(current, "Width", "10"));
        }
    }
}
