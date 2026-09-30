using System;
using System.IO;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The Worker-side segment allowlist. It guarded <c>genexus_time_travel</c>
    /// (where the value also reaches <c>git</c> as an argument) and the
    /// <c>genexus_api</c> baseline name. Two copies existed; both are now this one.
    ///
    /// The Gateway has its own type with the same class because the two assemblies
    /// cannot share one, and the matching test lives in GxMcp.Gateway.Tests. These
    /// two files together are the guard for that duplicated type — a source check
    /// asserts the two stay character-identical.
    /// </summary>
    public class SafePathSegmentTests
    {
        [Theory]
        [InlineData("WebPanel")]
        [InlineData("My_Object")]
        [InlineData("v2-final")]
        [InlineData("a.b.c")]
        public void AcceptsOrdinaryObjectNames(string value)
        {
            Assert.True(SafePathSegment.IsSafe(value));
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData(null)]
        [InlineData(".")]
        [InlineData("..")]
        public void RejectsEmptyWhitespaceAndTraversalMarkers(string value)
        {
            Assert.False(SafePathSegment.IsSafe(value));
        }

        [Theory]
        [InlineData("a\\b")]
        [InlineData("a/b")]
        [InlineData("a:b")]
        [InlineData("..\\..\\x")]
        [InlineData("../../x")]
        [InlineData("a b")]
        [InlineData("a;b")]
        [InlineData("a|b")]
        [InlineData("a$b")]
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
            Assert.False(SafePathSegment.IsSafe(new string('a', 100), 64));
            Assert.True(SafePathSegment.IsSafe(new string('a', 100), 200));
        }

        [Fact]
        public void TimeTravel_DelegatesToTheSharedAllowlist()
        {
            Assert.True(TimeTravelService.IsSafeObjectName("My_Object.v2-final"));
            Assert.False(TimeTravelService.IsSafeObjectName(".."));
            Assert.False(TimeTravelService.IsSafeObjectName("a\\b"));
            Assert.False(TimeTravelService.IsSafeObjectName(new string('a', 201)));
        }

        [Fact]
        public void ApiBaseline_KeepsItsOwnTighterCeiling()
        {
            Assert.True(ApiIntrospectService.IsSafeBaselineName("baseline_v2"));
            Assert.False(ApiIntrospectService.IsSafeBaselineName(".."));
            Assert.False(ApiIntrospectService.IsSafeBaselineName("a/b"));
            // 64 was this call site's ceiling before the consolidation and must
            // not have silently loosened to the shared 200.
            Assert.False(ApiIntrospectService.IsSafeBaselineName(new string('a', 65)));
            Assert.True(ApiIntrospectService.IsSafeBaselineName(new string('a', 64)));
        }

        [Fact]
        public void GatewayCopy_StaysCharacterIdenticalToThisOne()
        {
            // The two assemblies cannot share a type. A divergence between them
            // would reopen the traversal hole on whichever side drifted, so the
            // admitted character class is compared literally.
            string gateway = File.ReadAllText(FindGatewayFile("Helpers", "SafePathSegment.cs"));
            string worker = RepoSource.Read("src", "GxMcp.Worker", "Helpers", "SafePathSegment.cs");

            const string admitted = "char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-'";
            Assert.Contains(admitted, gateway);
            Assert.Contains(admitted, worker);

            // The traversal rejection and the ceiling must match too.
            foreach (string src in new[] { gateway, worker })
            {
                Assert.Contains("value != \".\" && value != \"..\"", src);
                Assert.Contains("MaxLength = 200", src);
            }
        }

        // Sibling assemblies sit next to each other under src/, so a Gateway
        // source file cannot be reached by anchoring only on the Worker tree.
        private static string FindGatewayFile(params string[] subPaths)
        {
            return Find("GxMcp.Gateway", subPaths);
        }

        private static string Find(string project, params string[] relative)
        {
            var segments = new System.Collections.Generic.List<string>();
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                segments.Clear();
                segments.Add(dir.FullName);
                segments.Add("src");
                segments.Add(project);
                segments.AddRange(relative);
                string candidate = Path.Combine(segments.ToArray());
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new FileNotFoundException(
                "Could not locate " + project + "/" + string.Join("/", relative)
                + " starting from " + AppDomain.CurrentDomain.BaseDirectory);
        }
    }
}
