using System;
using System.Drawing;
using System.Reflection;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // A report control's Font is a System.Drawing.Font. set_property Font used to be rejected
    // with LayoutWriteVerificationFailed because ConvertValue had no Font branch and the
    // read-back (Font.ToString()) never string-matched the requested value.
    public class ReportLayoutFontTests
    {
        private const string ToStringForm = "[Font: Name=Arial, Size=12, Units=3, GdiCharSet=1, GdiVerticalFont=False]";

        private static object Convert(string value)
        {
            var m = typeof(ReportLayoutHelper).GetMethod("ConvertValue", BindingFlags.NonPublic | BindingFlags.Static);
            return m.Invoke(null, new object[] { value, typeof(Font) });
        }

        private static bool Match(string expected, string actual)
        {
            var m = typeof(LayoutService).GetMethod("IsPersistedValueMatch", BindingFlags.NonPublic | BindingFlags.Static);
            return (bool)m.Invoke(null, new object[] { "Font", expected, actual });
        }

        [Theory]
        [InlineData(ToStringForm, "Arial", 12f, FontStyle.Regular)]
        [InlineData("Arial, 12pt", "Arial", 12f, FontStyle.Regular)]
        [InlineData("Arial, 12pt, style=Bold", "Arial", 12f, FontStyle.Bold)]
        [InlineData("Arial, 9.5pt, style=Bold, Italic", "Arial", 9.5f, FontStyle.Bold | FontStyle.Italic)]
        public void ConvertValue_BuildsFontFromAcceptedForms(string raw, string name, float size, FontStyle style)
        {
            using var font = Assert.IsType<Font>(Convert(raw));
            Assert.Equal(name, font.Name);
            Assert.Equal(size, font.Size);
            Assert.Equal(GraphicsUnit.Point, font.Unit);
            Assert.Equal(style, font.Style);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not a font")]
        [InlineData("[Font: Name=Arial]")]
        public void ConvertValue_RejectsGarbage(string raw)
        {
            Assert.Null(Convert(raw));
        }

        [Fact]
        public void Verification_MatchesRequestedFontAgainstToStringReadBack()
        {
            Assert.True(Match("Arial, 12pt", ToStringForm));
            Assert.True(Match("Arial, 12pt, style=Bold", ToStringForm)); // read-back carries no style
            Assert.True(Match(ToStringForm, ToStringForm));
        }

        [Fact]
        public void Verification_RejectsDifferentFont()
        {
            Assert.False(Match("Arial, 14pt", ToStringForm));
            Assert.False(Match("Tahoma, 12pt", ToStringForm));
            Assert.False(Match("Arial, 12pt, style=Bold", "Arial, 12pt"));
        }

        [Fact]
        public void IsPropertyEquivalent_NeverSkipsFontWrite()
        {
            var m = typeof(ReportLayoutHelper).GetMethod("IsPropertyEquivalent", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.False((bool)m.Invoke(null, new object[] { "Font", "Font", ToStringForm, ToStringForm }));
        }
    }
}
