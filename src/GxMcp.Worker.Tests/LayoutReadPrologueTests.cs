using System;
using System.IO;
using System.Linq;
using GxMcp.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Five visual readers resolved their object and its visual context by hand, and
    /// the <c>InvalidVisualXml</c> envelope was written out three times - already
    /// drifted, with one copy dropping "or inspecting the part directly" from the
    /// hint and rewording the recovery step behind the same error code.
    ///
    /// They are now <c>BeginVisualRead</c> and <c>InvalidVisualXml</c>. What is
    /// pinned here is the fail-closed ordering, which is the part that matters: a
    /// reader that skipped a step would walk a document it never confirmed it could
    /// parse.
    ///
    /// The wording kept for the root-missing refusal is the fuller of the two that
    /// existed: its hint keeps "or inspecting the part directly" and its recovery step
    /// points at <c>action=inspect_surface</c>, which is what lets a caller who has
    /// already opened the object find out which parts parse. That is a judgement about
    /// what the tool should say, so it is recorded here rather than asserted - pinning
    /// the sentence would fail on a copy-edit and catch no behavioural difference.
    /// </summary>
    public class LayoutReadPrologueTests
    {
        private static string LayoutService() =>
            RepoSource.Read("src", "GxMcp.Worker", "Services", "LayoutService.cs");

        [Fact]
        public void EveryReaderGoesThroughTheSharedPrologue()
        {
            string src = LayoutService();

            // Five readers: GetTree, FindControls, SetProperty, GetVisualPreview and
            // SetProperties. Four is a regression, six is a copy coming back.
            int callSites = SourceAssert.Count(src, "BeginVisualRead(target)");
            Assert.Equal(5, callSites);

            // The helper itself, plus the declaration.
            Assert.Equal(1, SourceAssert.Count(src, "private VisualReadSetup BeginVisualRead(string target)"));

            // No reader resolves the pair by hand any more: the one remaining
            // call is inside the helper. (There is also a retry path further down
            // that deliberately re-resolves against a different object.)
            Assert.Equal(1, SourceAssert.Count(src, "LoadVisualContext(obj, target, VisualSurface.Any)"));
        }

        [Fact]
        public void ThePrologueFailsClosedInOrder()
        {
            // The order is the contract: no object, then no readable context, and
            // only then does the caller look at the document.
            string body = SourceAssert.MethodBody(LayoutService(), "private VisualReadSetup BeginVisualRead(string target)");

            int findObject = body.IndexOf("_objectService.FindObject(target)", StringComparison.Ordinal);
            int notFound = body.IndexOf("VisualObjectNotFound(target)", StringComparison.Ordinal);
            int load = body.IndexOf("LoadVisualContext(obj, target, VisualSurface.Any)", StringComparison.Ordinal);
            int ctxError = body.IndexOf("context.Error", StringComparison.Ordinal);

            Assert.True(findObject >= 0, "the object lookup is gone");
            Assert.True(notFound > findObject, "the object guard must follow the lookup");
            Assert.True(load > notFound, "the context load must follow the object guard");
            Assert.True(ctxError > load, "the context guard must follow the load");
        }

        [Fact]
        public void ThePrologueNeverThrowsForAMissingObject()
        {
            string body = SourceAssert.MethodBody(LayoutService(), "private VisualReadSetup BeginVisualRead(string target)");

            Assert.Contains("if (obj == null)", body);
            Assert.Contains("return new VisualReadSetup { Error = VisualObjectNotFound(target) };", body);
            Assert.Contains("return new VisualReadSetup { Error = context.Error };", body);
        }

        [Fact]
        public void NeitherGuardIsWeakenedByAnExtraCondition()
        {
            // Ordering alone is not enough: narrowing either guard with an extra
            // condition leaves the steps in the same order while letting a failed
            // read continue past the check. Both guards are pinned to the whole
            // condition, not to its position.
            string body = SourceAssert.MethodBody(LayoutService(), "private VisualReadSetup BeginVisualRead(string target)");

            Assert.Equal(1, SourceAssert.Count(body, "if (obj == null)"));
            Assert.DoesNotContain("if (obj == null &&", body);
            Assert.DoesNotContain("if (obj != null)", body);

            Assert.Equal(1, SourceAssert.Count(body, "if (context.Error != null)"));
            Assert.DoesNotContain("context.Error != null &&", body);
            Assert.DoesNotContain("context.Error == null", body);
        }

        [Fact]
        public void OnlyTreeWalkersRejectAPartWithNoRoot()
        {
            // GetTree and FindControls walk the tree and must reject a rootless part.
            // SetProperty, GetVisualPreview and SetProperties read the document as
            // text and have no reason to; folding the check into the prologue would
            // reject input those three accept today.
            string src = LayoutService();

            Assert.Equal(2, SourceAssert.Count(src, "if (root == null) return InvalidVisualXml(target);"));
            Assert.Equal(1, SourceAssert.Count(src, "public string GetTree("));
            Assert.Equal(1, SourceAssert.Count(src, "public string FindControls("));

            // Root is exposed as a property precisely so the walkers can check it.
            Assert.Contains("public global::System.Xml.Linq.XElement Root =>", src);
        }

        [Fact]
        public void TheMissingRootEnvelopeIsDefinedOnce()
        {
            string src = LayoutService();

            Assert.Equal(1, SourceAssert.Count(src, "private static string InvalidVisualXml(string target)"));
            Assert.Equal(1, SourceAssert.Count(src, "code: \"InvalidVisualXml\""));
        }

        [Fact]
        public void TheVisualContextPartialUsesTheSameEnvelope()
        {
            // The drifted third copy lived here. The one InvalidVisualXml left in
            // this file is a different condition - a parse failure carrying the
            // exception message - and is deliberately not folded in.
            string partial = RepoSource.Read("src", "GxMcp.Worker", "Services", "LayoutService.VisualContext.cs");

            Assert.Contains("ParseResult.FromError(InvalidVisualXml(target))", partial);
            Assert.Equal(1, SourceAssert.Count(partial, "code: \"InvalidVisualXml\""));

            // A parse failure reports the exception, so its message is assembled rather
            // than literal - which is also what distinguishes it from the root-missing
            // refusal it shares the code with.
            Assert.Contains("message: \"Invalid visual XML: \" + ex.Message", partial);
        }

        [Fact]
        public void TheSetupCarriesWhatEveryReaderNeeds()
        {
            string src = LayoutService();

            Assert.Contains("public string Error;", src);
            Assert.Contains("public global::Artech.Architecture.Common.Objects.KBObject Object;", src);
            Assert.Contains("public LayoutContextResult Context;", src);
        }
    }
}
