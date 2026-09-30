using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Three refactors used to build their own envelope when the code block they were
    /// asked to extract was not in the object's source. The duplication mattered less
    /// than the drift: the hint said different things at each, and none described the
    /// condition actually checked - the caller passes a block and the refactor searches
    /// every source part. That reasoning is why the wording changed, and it lives here
    /// rather than in a test, because it is a decision about what the tool should say
    /// and no assertion about the resulting sentence can protect it.
    ///
    /// What these protect is the shape: one builder, three call sites, no fourth copy.
    /// They count identities and never sentences. A count stops a divergent copy; a
    /// sentence pin only fails on a reword, and that tax is what this file used to
    /// charge - 19 of its 32 assertions were of that kind.
    ///
    /// The three <c>ObjectNotFound</c> refusals about a <em>target</em> object stay
    /// separate on purpose: they tell the caller to disambiguate by type, and folding
    /// them in would tell a caller who named a source explicitly to go disambiguate it.
    ///
    /// Source-level because the envelopes are private static methods needing a live SDK
    /// object to reach.
    /// </summary>
    public class RefactorRefusalEnvelopeTests
    {
        private const string CodeBlock = "private static string CodeBlockNotFound(string sourceObjectName)";
        private const string SourceObject = "private static string SourceObjectNotFound(string sourceObjectName)";
        private const string CodeBlockCode = "code: \"CodeBlockNotFound\"";
        private const string NotFoundCode = "code: \"ObjectNotFound\"";

        private static string Source() =>
            RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "RefactorService.cs"));

        [Fact]
        public void OneBuilderEachAndEveryExtractionRefusesThroughIt()
        {
            string source = Source();

            Assert.Equal(1, SourceAssert.Count(source, CodeBlock));
            Assert.Equal(1, SourceAssert.Count(source, SourceObject));

            // Three extractions refuse through the first - procedure pre-flight,
            // procedure post-write, and the subroutine's dry-run check - and two
            // through the second.
            Assert.Equal(3, SourceAssert.Count(source, "return CodeBlockNotFound(sourceObjectName);"));
            Assert.Equal(2, SourceAssert.Count(source, "return SourceObjectNotFound(sourceObjectName);"));

            // The recovery step is built once, in the envelope, not at each call site.
            string envelope = SourceAssert.MethodBody(source, CodeBlock);
            Assert.Equal(1, SourceAssert.Count(envelope, "nextSteps:"));
            Assert.Equal(1, SourceAssert.Count(envelope, @"[""part""] = ""Source"""));
        }

        /// <summary>
        /// No call site rebuilds an envelope, and the target refusals stay separate.
        /// </summary>
        /// <remarks>
        /// <c>CodeBlockNotFound</c> is the one whose code literal is unique to it, so
        /// "every occurrence lives inside the builder" is a uniqueness claim - the
        /// assertion that catches someone reintroducing an inline envelope at a call
        /// site, which is how the drift started. The source-object refusal cannot be
        /// checked that way: it shares the <c>ObjectNotFound</c> code with three
        /// target-object refusals, so it is pinned by identity above and the count of
        /// four here is what proves those three are still built on their own.
        /// </remarks>
        [Fact]
        public void NoCallSiteBuildsItsOwnEnvelope()
        {
            string source = Source();

            Assert.Equal(1, SourceAssert.Count(source, CodeBlockCode));
            Assert.Equal(SourceAssert.Count(source, CodeBlockCode),
                SourceAssert.Count(SourceAssert.MethodBody(source, CodeBlock), CodeBlockCode));

            Assert.Equal(4, SourceAssert.Count(source, NotFoundCode));
            Assert.Equal(3, SourceAssert.Count(source, NotFoundCode)
                - SourceAssert.Count(SourceAssert.MethodBody(source, SourceObject), NotFoundCode));
        }
    }
}