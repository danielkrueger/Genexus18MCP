using System;
using System.IO;
using GxMcp.Gateway;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// SafePathSegment is the single allowlist standing between an LLM-supplied
    /// name and Path.Combine / Directory.Delete / CopyTo. The three copies it
    /// replaced each had the same character class, so a drift in one was a
    /// traversal hole on exactly one entry point. These pin the class exhaustively
    /// rather than sampling it, and pin the traversal cases that matter.
    /// </summary>
    public class SafePathSegmentTests
    {
        [Theory]
        [InlineData("WebPanel")]
        [InlineData("My_Object")]
        [InlineData("v2-final")]
        [InlineData("a.b.c")]
        [InlineData("A0")]
        public void AcceptsOrdinaryObjectNames(string value)
        {
            Assert.True(SafePathSegment.IsSafe(value));
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("   ")]
        [InlineData(null)]
        public void RejectsEmptyAndWhitespace(string? value)
        {
            // null is a deliberate input: IsSafe must reject it despite its non-nullable signature.
            Assert.False(SafePathSegment.IsSafe(value!));
        }

        [Theory]
        [InlineData(".")]
        [InlineData("..")]
        public void RejectsTraversalMarkersThatTheAllowlistWouldOtherwiseAdmit(string value)
        {
            // "." and ".." consist entirely of admitted characters, so the
            // character class alone does not stop them.
            Assert.False(SafePathSegment.IsSafe(value));
        }

        [Theory]
        [InlineData("a\\b")]
        [InlineData("a/b")]
        [InlineData("a:b")]
        [InlineData("..\\..\\x")]
        [InlineData("../../x")]
        [InlineData("a b")]
        [InlineData("a\tb")]
        [InlineData("a;b")]
        [InlineData("a*b")]
        [InlineData("a|b")]
        [InlineData("a$b")]
        [InlineData("a%b")]
        [InlineData("a\0b")]
        [InlineData("a'b")]
        [InlineData("a\"b")]
        [InlineData("a(b")]
        [InlineData("a[b")]
        [InlineData("a{b")]
        [InlineData("a<b")]
        [InlineData("a?b")]
        [InlineData("a#b")]
        public void RejectsEverySeparatorAndShellMetacharacter(string value)
        {
            Assert.False(SafePathSegment.IsSafe(value));
        }

        [Fact]
        public void RejectsAnythingOverTheDefaultCeiling()
        {
            Assert.True(SafePathSegment.IsSafe(new string('a', SafePathSegment.MaxLength)));
            Assert.False(SafePathSegment.IsSafe(new string('a', SafePathSegment.MaxLength + 1)));
        }

        [Fact]
        public void HonoursACallerSuppliedTighterCeiling()
        {
            // ApiIntrospectService kept a 64-character baseline ceiling even though
            // the shared default is 200; passing it must still take effect.
            Assert.False(SafePathSegment.IsSafe(new string('a', 100), 64));
            Assert.True(SafePathSegment.IsSafe(new string('a', 100), 200));
        }

        [Fact]
        public void AcceptsAccentedLetters()
        {
            // char.IsLetterOrDigit is Unicode-aware, matching the pre-existing
            // behavior of all three originals.
            Assert.True(SafePathSegment.IsSafe("Ordensaché"));
        }

        [Fact]
        public void ImportHelper_DelegatesToTheSharedAllowlist()
        {
            // The import path deletes then copies under Objects/<Type>/<Name>/.
            Assert.True(KbImportHelper.IsSafeSegment("My_Object.v2-final"));
            Assert.False(KbImportHelper.IsSafeSegment(".."));
            Assert.False(KbImportHelper.IsSafeSegment("a\\b"));
        }
    }
}
