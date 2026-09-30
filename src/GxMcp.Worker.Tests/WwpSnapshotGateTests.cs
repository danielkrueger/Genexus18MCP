using System;
using System.Linq;
using System.Runtime.Serialization;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// A WWP mutation may only proceed when it captured everything it needs to prove
    /// what it changed: the object's own native bytes, its resolved parent, that
    /// parent's WebForm text, and before-snapshots of both the PatternInstance and the
    /// WebForm. If any is missing the operation refuses rather than stage a change it
    /// cannot verify or roll back.
    ///
    /// That five-term check was written out identically at all five call sites. Five
    /// terms is enough that dropping one is easy and invisible: the term disappears,
    /// every test still passes because nothing exercised a missing snapshot, and the
    /// next real failure is a mutation applied without the state needed to roll it
    /// back. So it is now <c>WwpSnapshotsIncomplete</c>, and these check all five terms
    /// individually - which a source-level check could not do, since it cannot tell a
    /// dropped term from a renamed one.
    ///
    /// <para>
    /// Only the condition is shared. The five refusals that follow it are three
    /// different contracts - two report only <c>snapshot</c> and <c>persisted</c>, three
    /// add diagnostic booleans, and one reports them through <c>errorExtra</c> so they
    /// land inside <c>error</c> rather than at the envelope's top level. Unifying those
    /// would change where a client reads the fields from, so each site keeps its own
    /// and the tests below pin that they stayed apart.
    /// </para>
    /// </summary>
    public class WwpSnapshotGateTests
    {
        private static WwpActionService.SnapshotBundle Complete()
        {
            return new WwpActionService.SnapshotBundle
            {
                Pattern = new EditSnapshotStore.SnapshotInfo(),
                WebForm = new EditSnapshotStore.SnapshotInfo(),
            };
        }

        /// <summary>
        /// A non-null <c>KBObject</c> to stand in for a resolved parent.
        /// </summary>
        /// <remarks>
        /// The gate only ever compares this to null, so an uninitialised instance is
        /// the whole of what the predicate needs - and the SDK type has no constructor
        /// reachable from a test. <c>FormatterServices.GetUninitializedObject</c> is how
        /// the rest of this suite builds SDK objects. Nothing here reads a property on
        /// it, so no field being unset is not a hazard.
        /// </remarks>
        private static Artech.Architecture.Common.Objects.KBObject Parent()
        {
            return (Artech.Architecture.Common.Objects.KBObject)
                FormatterServices.GetUninitializedObject(typeof(Artech.Architecture.Common.Objects.KBObject));
        }

        /// <summary>
        /// Nothing missing: the mutation may proceed. The baseline every other case is
        /// measured against - a gate that refused here would break all five operations,
        /// and one that passed with something missing would let them stage an
        /// unverifiable change.
        /// </summary>
        [Fact]
        public void NothingMissingMeansTheGateIsSatisfied()
        {
            Assert.False(WwpActionService.WwpSnapshotsIncomplete(
                new byte[] { 1 }, Parent(), "webForm", Complete()));
        }

        /// <summary>
        /// Each of the five, missing one at a time.
        /// </summary>
        /// <remarks>
        /// Every term is checked on its own, because the failure mode this guards
        /// against is precisely one term quietly going away. A single "everything
        /// present" case would pass with any subset of the terms left in place.
        /// </remarks>
        [Fact]
        public void EachMissingThingAloneIsEnoughToRefuse()
        {
            var parent = Parent();
            var bytes = new byte[] { 1 };

            // The object's own native bytes.
            Assert.True(WwpActionService.WwpSnapshotsIncomplete(
                null, parent, "webForm", Complete()));

            // Its resolved parent.
            Assert.True(WwpActionService.WwpSnapshotsIncomplete(
                bytes, null, "webForm", Complete()));

            // The parent's WebForm text.
            Assert.True(WwpActionService.WwpSnapshotsIncomplete(
                bytes, parent, null, Complete()));

            // And each of the two snapshots individually - a bundle where only the
            // PatternInstance was captured still cannot prove the change.
            var noPattern = Complete();
            noPattern.Pattern = null;
            Assert.True(WwpActionService.WwpSnapshotsIncomplete(bytes, parent, "webForm", noPattern));

            var noWebForm = Complete();
            noWebForm.WebForm = null;
            Assert.True(WwpActionService.WwpSnapshotsIncomplete(bytes, parent, "webForm", noWebForm));
        }

        [Fact]
        public void SeveralThingsMissingAtOnceIsStillJustARefusal()
        {
            // The gate is a boolean, not a count: callers use it to choose one of two
            // paths, and nothing downstream asks how many things were absent.
            Assert.True(WwpActionService.WwpSnapshotsIncomplete(null, null, null, new WwpActionService.SnapshotBundle()));
        }

        /// <summary>
        /// The condition is stated once and every WWP operation goes through it.
        ///
        /// Per file, so a new operation cannot arrive with its own copy and an existing
        /// one cannot quietly go back to spelling out the terms.
        /// </summary>
        [Fact]
        public void TheConditionIsStatedOnceAndEveryOperationUsesIt()
        {
            string router = RepoSource.WithoutComments(Read("WwpActionService.cs"));
            Assert.Equal(1, SourceAssert.Count(router, "internal static bool WwpSnapshotsIncomplete("));

            // All five terms, in the helper. Counted here as well as behaviourally,
            // because the source is what a reader consults.
            string body = SourceAssert.MethodBody(router, "internal static bool WwpSnapshotsIncomplete(");
            Assert.Equal(1, SourceAssert.Count(body, "nativeBytes == null"));
            Assert.Equal(1, SourceAssert.Count(body, "parent == null"));
            Assert.Equal(1, SourceAssert.Count(body, "parentWebFormBefore == null"));
            Assert.Equal(1, SourceAssert.Count(body, "snapshots.Pattern == null"));
            Assert.Equal(1, SourceAssert.Count(body, "snapshots.WebForm == null"));

            foreach (string file in new[]
            {
                "WwpActionService.FormActions.cs",
                "WwpActionService.Grid.cs",
                "WwpActionService.Tables.cs",
                "WwpActionService.Tabs.cs",
                "WwpActionService.WebComponentReplacement.cs",
            })
            {
                string source = RepoSource.WithoutComments(Read(file));
                Assert.True(SourceAssert.Count(source, "WwpSnapshotsIncomplete(nativeBytes, parent, parentWebFormBefore, snapshots)") == 1, file + ": call site");
                Assert.True(source.IndexOf("nativeBytes == null || parent == null", StringComparison.Ordinal) < 0, file + ": the terms are spelled out again");
            }
        }

        /// <summary>
        /// The five refusals stayed three distinct contracts.
        /// </summary>
        /// <remarks>
        /// This is the part the extraction deliberately did not do. If a future change
        /// merges the refusals for tidiness, a client reading
        /// <c>nativeBytesAvailable</c> from grid's response would go from a clear
        /// <c>null</c> to a field it did not expect, or - worse for the one that uses
        /// <c>errorExtra</c> - would find the diagnostics moved from inside
        /// <c>error</c> to the envelope's top level. Pinned per file so that is a
        /// decision rather than an accident.
        /// </remarks>
        [Fact]
        public void TheFiveRefusalsRemainThreeDistinctContracts()
        {
            // Two report only the snapshot and that nothing was persisted.
            foreach (string file in new[] { "WwpActionService.Grid.cs", "WwpActionService.Tabs.cs" })
            {
                string source = RepoSource.WithoutComments(Read(file));
                string refusal = Refusal(source);

                Assert.True(SourceAssert.Count(refusal, "[\"snapshot\"]") == 1, file + ": snapshot field");
                Assert.True(SourceAssert.Count(refusal, "[\"persisted\"]") == 1, file + ": persisted field");
                Assert.True(SourceAssert.Count(refusal, "nativeBytesAvailable") == 0, file + ": gained a diagnostic");
            }

            // Three add the four diagnostic booleans...
            foreach (string file in new[]
            {
                "WwpActionService.FormActions.cs",
                "WwpActionService.Tables.cs",
                "WwpActionService.WebComponentReplacement.cs",
            })
            {
                string refusal = Refusal(RepoSource.WithoutComments(Read(file)));
                Assert.True(SourceAssert.Count(refusal, "nativeBytesAvailable") == 1, file + ": nativeBytesAvailable");
                Assert.True(SourceAssert.Count(refusal, "parentResolved") == 1, file + ": parentResolved");
                Assert.True(SourceAssert.Count(refusal, "parentWebFormAvailable") == 1, file + ": parentWebFormAvailable");
            }

            // ...and one of those three reports them through errorExtra, so they land
            // inside "error" rather than at the envelope's top level. That asymmetry is
            // the clearest evidence these were never one contract: same code, same
            // intent, two different JSON shapes.
            string replace = RepoSource.WithoutComments(Read("WwpActionService.WebComponentReplacement.cs"));
            Assert.True(SourceAssert.Count(Refusal(replace), "errorExtra:") == 1);
            foreach (string file in new[] { "WwpActionService.FormActions.cs", "WwpActionService.Tables.cs" })
            {
                string source = RepoSource.WithoutComments(Read(file));
                Assert.True(SourceAssert.Count(source, "errorExtra:") == 0, file + ": switched to errorExtra");
                Assert.True(SourceAssert.Count(Refusal(source), "extra: new JObject") == 1, file + ": still uses extra");
            }

            // Two of them also report whether each snapshot was captured, which the
            // other three do not.
            Assert.True(SourceAssert.Count(Refusal(replace), "patternSnapshotAvailable") == 1);
            Assert.True(SourceAssert.Count(Refusal(replace), "webFormSnapshotAvailable") == 1);
        }

        /// <summary>
        /// The bundle is dereferenced, not null-guarded, and that is correct.
        /// </summary>
        /// <remarks>
        /// <c>CaptureSnapshots</c> always constructs one - a throw inside it happens at
        /// the call site, before the gate. A <c>?.</c> here would imply a case that
        /// cannot occur and would quietly convert a would-be crash into a refusal that
        /// says the snapshots were unavailable, which is a different diagnosis. Pinned
        /// so the choice is deliberate rather than forgotten.
        /// </remarks>
        [Fact]
        public void TheBundleIsNotNullGuardedBecauseItCannotBeNull()
        {
            string router = RepoSource.WithoutComments(Read("WwpActionService.cs"));
            string body = SourceAssert.MethodBody(router, "internal static bool WwpSnapshotsIncomplete(");

            Assert.Equal(0, SourceAssert.Count(body, "snapshots?."));
            Assert.Equal(0, SourceAssert.Count(body, "snapshots == null"));

            // And the producer really does always return one.
            string tabs = RepoSource.WithoutComments(Read("WwpActionService.Tabs.cs"));
            string capture = SourceAssert.MethodBody(tabs, "private SnapshotBundle CaptureSnapshots(");
            Assert.True(SourceAssert.Count(capture, "return new SnapshotBundle") == 1);
            Assert.Equal(0, SourceAssert.Count(capture, "return null"));
        }

        /// <summary>
        /// The refusal each site returns is not folded into the shared helper.
        /// </summary>
        [Fact]
        public void TheRefusalsAreNotSharedAndThatIsIntentional()
        {
            string router = RepoSource.WithoutComments(Read("WwpActionService.cs"));

            // The helper returns a bool and nothing else - it does not build an
            // envelope, so there is no shared envelope to grow fields onto.
            Assert.True(router.IndexOf("static string BuildWwpSnapshotRequired", StringComparison.Ordinal) < 0);
            Assert.Equal(1, SourceAssert.Count(router, "internal static bool WwpSnapshotsIncomplete("));

            // Five messages, all different: each names what that operation did not do.
            var messages = new[]
            {
                "WwpActionService.FormActions.cs",
                "WwpActionService.Grid.cs",
                "WwpActionService.Tables.cs",
                "WwpActionService.Tabs.cs",
                "WwpActionService.WebComponentReplacement.cs",
            }.Select(f => Message(Refusal(RepoSource.WithoutComments(Read(f))))).ToArray();

            Assert.Equal(5, messages.Length);
            Assert.Equal(5, messages.Distinct(StringComparer.Ordinal).Count());
        }

        private static string Read(string file)
        {
            return RepoSource.Read("src", "GxMcp.Worker", "Services", file);
        }

        /// <summary>
        /// The text of the WwpSnapshotRequired refusal in one file.
        /// </summary>
        /// <remarks>
        /// Found by balancing the <c>McpResponse.Err</c> call rather than by looking
        /// for a closing sequence. Two of the five end in <c>})</c> rather than
        /// <c>}))</c> - they pass <c>errorExtra</c> or open an object literal - so any
        /// fixed marker would either stop short of the fields or run on into whatever
        /// follows and count them.
        /// </remarks>
        private static string Refusal(string source)
        {
            int code = source.IndexOf("code: \"WwpSnapshotRequired\"", StringComparison.Ordinal);
            Assert.True(code >= 0, "no WwpSnapshotRequired refusal found");

            int open = source.LastIndexOf("McpResponse.Err(", code, StringComparison.Ordinal);
            Assert.True(open >= 0, "no enclosing McpResponse.Err call");

            int depth = 0;
            for (int i = open + "McpResponse.Err".Length; i < source.Length; i++)
            {
                if (source[i] == '(') depth++;
                else if (source[i] == ')')
                {
                    depth--;
                    if (depth == 0) return source.Substring(open, i - open + 1);
                }
            }
            throw new InvalidOperationException("unbalanced McpResponse.Err call");
        }

        /// <summary>The one message inside a refusal.</summary>
        private static string Message(string refusal)
        {
            int at = refusal.IndexOf("message:", StringComparison.Ordinal);
            Assert.True(at >= 0, "no message in refusal");
            int end = refusal.IndexOf(',', at);
            Assert.True(end > at, "unterminated message");
            return refusal.Substring(at, end - at).Trim();
        }

    }
}
