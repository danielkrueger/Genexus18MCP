using System;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #332: <c>genexus_wwp action=list</c> threw a raw
    /// <c>NullReferenceException</c> when <c>name</c> was the WebPanel carrying the
    /// WorkWithPlus instance, while the same call succeeded when <c>name</c> was the
    /// pattern host. The tool schema documents <c>name</c> as "Transaction, WebPanel,
    /// or ...", so the parent name was supposed to resolve.
    ///
    /// <para>
    /// Two properties are pinned here. The first is the defect's actual mechanism:
    /// <see cref="PatternAnalysisService.FindPatternPart"/> walked
    /// <c>instanceObj.Parts</c> and dereferenced <c>p.Name</c> unguarded, so a part
    /// with a null name - which the SDK does not rule out, and a WebPanel carries many
    /// parts - threw from inside a predicate that only meant to compare strings. The
    /// second is that a framework exception must never again be the answer: every
    /// resolution failure returns a typed error naming the host to use, and the
    /// catch-all names the stage instead of repeating <c>ex.Message</c>.
    /// </para>
    ///
    /// <para>
    /// The SDK-touching path needs a live Worker, so the routing guards are source-shape
    /// assertions on the production methods. That is the honest limit here: this build
    /// cannot be exercised against the reporter's KB, and a fake SDK object would test
    /// the fake rather than the SDK.
    /// </para>
    /// </summary>
    public class WwpListParentResolutionTests
    {
        /// <summary>
        /// The body of <c>FindPatternPart</c>, comment-stripped. Newlines are
        /// normalized first: <see cref="GxMcp.TestSupport.RepoSource.WithoutComments"/>
        /// preserves them verbatim, so a delimiter written with bare "\n" would not
        /// match a CRLF file and the slice would silently run to the end of the file -
        /// which would make every assertion below pass for the wrong reason.
        /// </summary>
        private static string FindPatternPartBody()
        {
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "PatternAnalysisService.cs")
                .Replace("\r\n", "\n");

            const string signature = "public KBObjectPart FindPatternPart";
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, "FindPatternPart not found");

            // Bound the slice at the next method declaration, which is a stable
            // delimiter whatever the file's line endings are.
            int end = source.IndexOf("\n        public ", start + signature.Length, StringComparison.Ordinal);
            Assert.True(end > start, "could not delimit FindPatternPart");
            return source.Substring(start, end - start);
        }

        [Fact]
        public void FindPatternPart_Does_Not_Dereference_A_Null_Part_Name()
        {
            // The defect, stated as a property of the source. A part with a null Name
            // must simply fail to match.
            string body = FindPatternPartBody();

            // The unguarded forms that produced the NullReferenceException.
            Assert.DoesNotContain("p.Name.Equals(", body, StringComparison.Ordinal);
            // And a part collection that cannot be enumerated is "absent", not a crash.
            Assert.Contains("catch (Exception)", body, StringComparison.Ordinal);
        }

        [Fact]
        public void FindPatternPart_Keeps_Matching_Non_Null_Part_Names()
        {
            // Null-safety must not weaken the match: the three-way comparison the
            // method performs is unchanged, only guarded.
            string body = FindPatternPartBody();

            Assert.Contains("string.Equals(p.Name, \"PatternInstance\"", body, StringComparison.Ordinal);
            Assert.Contains("p.GetType().Name.Contains(\"PatternInstance\")", body, StringComparison.Ordinal);
            Assert.Contains("p.Type == PatternInstancePartGuid", body, StringComparison.Ordinal);
            Assert.Contains("p.TypeDescriptor?.Name", body, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Null_Part_Element_Itself_Is_Ignored()
        {
            // A collection can also contain a null element; the predicate must not
            // dereference it either.
            Assert.Contains("if (p == null) return false;", FindPatternPartBody(), StringComparison.Ordinal);
        }

        [Fact]
        public void The_Catch_All_Does_Not_Repeat_A_Framework_Message()
        {
            // The reported symptom was an envelope whose message was the localized .NET
            // string. That is not actionable, and it is what made the issue look
            // unreproducible: "nothing to correct on the caller side".
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "WwpActionService.cs");

            Assert.DoesNotContain("code: \"WwpActionFailed\", message: ex.Message", source, StringComparison.Ordinal);
            Assert.Contains("exceptionType", source, StringComparison.Ordinal);
            // And it must tell the caller what to try instead.
            Assert.Contains("WorkWithPlus<Panel>", source, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Parent_That_Resolves_Without_An_Instance_Names_The_Host_To_Use()
        {
            // The documented alternative outcome: a typed error that names the host,
            // not a framework exception. This is the envelope the resolver path returns
            // when the object exists but carries no editable WorkWithPlus instance.
            var env = JObject.Parse(WwpActionService.BuildWwpInstanceNotFound(
                "WebSenDad", "WebSenDad", "WebPanel", new PatternInstanceMatch[0]));

            Assert.Equal("WWPInstanceNotFound", env["error"]?["code"]?.ToString());
            Assert.Equal("WebPanel", env["objectType"]?.ToString());
            Assert.False(string.IsNullOrWhiteSpace(env["error"]?["hint"]?.ToString()));
        }

        [Fact]
        public void A_Parent_Carrying_A_WorkWithPlus_Instance_Is_Named_In_The_Not_Found_Envelope()
        {
            // When the parent has a WorkWithPlus instance under a different name, the
            // envelope must name it - that is the answer the reporter wanted instead of
            // "name the host yourself" guesswork.
            var registry = new PatternRegistry(new[]
            {
                PatternRegistry.ParseManifest(
                    "<Pattern Id=\"" + PatternRegistry.WorkWithPlusPatternId + "\" Name=\"WorkWithPlus\">" +
                    "<Definition><InstanceName>WorkWithPlus{0}</InstanceName>" +
                    "<ParentObjects><ParentObject Type=\"WebPanel\" /></ParentObjects></Definition></Pattern>",
                    "wwp.Pattern")
            });

            var detected = PatternAnalysisService.MatchPatternInstances(new[]
            {
                new PatternInstanceCandidate("WorkWithPlusWebSenDad", "WorkWithPlus",
                    PatternRegistry.WorkWithPlusPatternId)
            }, registry);

            var env = JObject.Parse(WwpActionService.BuildWwpInstanceNotFound(
                "WebSenDad", "WebSenDad", "WebPanel", detected));

            var next = env["error"]?["nextSteps"]?[0];
            Assert.Equal("genexus_wwp", next?["tool"]?.ToString());
            Assert.Equal("WorkWithPlusWebSenDad", next?["args"]?["name"]?.ToString());
        }
    }
}
