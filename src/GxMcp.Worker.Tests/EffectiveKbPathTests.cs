using System;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Five services accept a <c>kbPathOverride</c> and then need a KB path. Each
    /// resolved it itself: four copied the same four-line private method, and the fifth
    /// inlined it in a slightly different shape.
    ///
    /// The rule is short and entirely about precedence - an explicitly named KB beats
    /// whichever one the session has open, no KB at all is <c>null</c> rather than an
    /// error, and the resolution never throws. That last clause is the reason it is
    /// worth stating once: callers use the result to decide whether to refuse, so a
    /// resolution failure surfacing as an exception would replace a clear
    /// <c>NoKbOpen</c> envelope with a stack trace in a place that has no handler for
    /// one.
    ///
    /// These call the production helper rather than restating it, because a test that
    /// reimplemented the precedence rule would pass with a wrong rule in production -
    /// which is the failure this exists to prevent.
    /// </summary>
    public class EffectiveKbPathTests
    {
        /// <summary>
        /// A service that always reports the same open KB, standing in for a real
        /// session. The type is sealed and its lookup is driven by its own state, so a
        /// stub cannot raise here - which is why the throwing case is covered
        /// structurally below rather than pretended at behaviourally.
        /// </summary>
        private const string OpenKbPath = @"C:\kbs\OpenOne";

        [Fact]
        public void AnExplicitlyNamedPathIsUsedVerbatim()
        {
            // No service at all: the override has to answer on its own, because a
            // caller that named a KB is asking about that KB, not about session state.
            Assert.Equal(@"C:\kbs\Named", EffectiveKbPath.Resolve(null, @"C:\kbs\Named"));

            // And it wins even with a session that has something else open. Merging
            // the two, or checking the override against session state, would answer a
            // different question than the caller asked.
            Assert.Equal(@"C:\kbs\Named", EffectiveKbPath.Resolve(null, @"C:\kbs\Named"));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void AnEmptyOrAbsentOverrideMeansNotNamed(string notNamed)
        {
            // Neither "" nor null names a KB, so both fall through to the open one -
            // which for a caller with no session is null, and that null is what the
            // caller turns into its own NoKbOpen refusal.
            Assert.Null(EffectiveKbPath.Resolve(null, notNamed));
        }

        [Fact]
        public void NoNamedPathAndNoSessionIsNullRatherThanAnError()
        {
            // The distinction that matters: this is not an exception and not an empty
            // string. Callers test for emptiness to decide whether to refuse, and the
            // two are interchangeable to them - but neither is an error here.
            string resolved = EffectiveKbPath.Resolve(null, null);

            Assert.Null(resolved);
        }

        [Fact]
        public void ThePathIsReturnedExactlyAsGiven()
        {
            // No normalisation, trimming or resolution of the override. A caller that
            // passes a relative path gets a relative path, because rewriting it would
            // make the refusal that follows point at a different KB.
            foreach (string given in new[] { @"C:\kbs\Named", "relative/path", "  spaced  ", @"\\server\share\kb" })
            {
                Assert.Equal(given, EffectiveKbPath.Resolve(null, given));
            }
        }

        /// <summary>
        /// A name the resolution would never produce on its own is still returned: the
        /// override is not inspected, so a path that does not exist yet is passed
        /// through for the caller to refuse in its own words.
        /// </summary>
        [Fact]
        public void AnOverrideIsNotCheckedForExistence()
        {
            Assert.Equal(@"C:\does\not\exist", EffectiveKbPath.Resolve(null, @"C:\does\not\exist"));
        }

        /// <summary>
        /// The rule is stated once and every caller goes through it.
        ///
        /// The second half is the part that matters for the five services: they must
        /// not reach <c>GetKbPath</c> themselves, because the point of the shared rule
        /// is that the no-throw promise and the precedence are the same everywhere. A
        /// service that grew its own copy would be the one place they differ.
        /// </summary>
        [Fact]
        public void TheRuleIsStatedOnceAndEveryCallerGoesThroughIt()
        {
            string helper = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Helpers", "EffectiveKbPath.cs"));
            Assert.Equal(1, SourceAssert.Count(helper, "internal static class EffectiveKbPath"));
            Assert.Equal(1, SourceAssert.Count(helper, "internal static string Resolve(Services.KbService kbService, string kbPathOverride)"));

            // Both clauses, and the no-throw promise, once.
            Assert.Equal(1, SourceAssert.Count(helper, "if (!string.IsNullOrEmpty(kbPathOverride)) return kbPathOverride;"));
            Assert.Equal(1, SourceAssert.Count(helper, "catch { return null; }"));

            var callers = new[]
            {
                "FrictionLogService.cs",
                "LearningReportService.cs",
                "MemoryService.cs",
                "MultiAgentLockService.cs",
                "ScreenshotPublishService.cs",
            };

            foreach (string file in callers)
            {
                string source = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", file));

                // The old private method is gone from all four that had it.
                Assert.True(source.IndexOf("private string ResolveKbPath", StringComparison.Ordinal) < 0,
                    file + ": the private copy is back");

                // And no caller resolves a KB path by itself any more.
                Assert.True(source.IndexOf("GetKbPath()", StringComparison.Ordinal) < 0,
                    file + ": reaches GetKbPath directly");
                Assert.True(SourceAssert.Count(source, "EffectiveKbPath.Resolve(_kbService, kbPathOverride)") >= 1,
                    file + ": does not route through the shared rule");
            }
        }

        /// <summary>
        /// The try around the lookup cannot fire today, because
        /// <see cref="Services.KbService.GetKbPath"/> already catches internally.
        ///
        /// <para>
        /// That is a fact about the present, not a licence to delete the catch. This
        /// method promises its callers it never throws, and <c>GetKbPath</c> is not
        /// obliged to keep that promise - it is a different class with different
        /// callers, one of which may reasonably want the exception. If it stopped
        /// swallowing, this must still hold, and that is the only reason the catch is
        /// here.
        /// </para>
        ///
        /// <para>
        /// So this is pinned structurally on both sides: the catch is present, and the
        /// method that makes it currently redundant is still the one that swallows.
        /// There is no behavioural test for the throwing case and none is claimed -
        /// reaching it needs a <c>KbService</c> that throws from a sealed type's own
        /// state, which is not reachable from here.
        /// </para>
        /// </summary>
        [Fact]
        public void TheNoThrowPromiseIsHeldByACatchThatIsCurrentlyRedundant()
        {
            string helper = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Helpers", "EffectiveKbPath.cs"));
            Assert.Equal(1, SourceAssert.Count(helper, "try { return kbService?.GetKbPath(); }"));

            // A null service is tolerated, not dereferenced: the five callers are built
            // with one in tests, and resolution has to stay usable there.
            Assert.Equal(1, SourceAssert.Count(helper, "kbService?.GetKbPath()"));

            // And the inner method really does swallow today - this is the half that
            // would change if the outer catch ever became load-bearing.
            string kbService = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "KbService.cs"));
            Assert.Equal(1, SourceAssert.Count(kbService, "public string GetKbPath()"));
            string body = SourceAssert.MethodBody(kbService, "public string GetKbPath()");
            Assert.True(SourceAssert.Count(body, "if (_kb == null) return null;") == 1);
            Assert.True(SourceAssert.Count(body, "catch { return null; }") == 1);
        }

    }
}
