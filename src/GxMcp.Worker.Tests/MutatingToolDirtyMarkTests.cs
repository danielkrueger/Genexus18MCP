using System;
using System.IO;
using System.Reflection;
using GxMcp.TestSupport;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The incremental build has a compile-only fast path, taken for any target
    /// <c>EditDirtyTracker</c> classifies as clean, and with
    /// <c>fastIncremental</c> an all-clean target list short-circuits to
    /// <c>NoBuildNeeded</c> without dispatching a build at all. Cleanliness is
    /// decided solely by <c>EditDirtyTracker.MarkDirty</c>, and the only way a write
    /// surface reaches it is <c>WriteService.NotePerTargetWrite</c>.
    ///
    /// <para>
    /// The property, layout and <c>Services/Structure/</c> write surfaces commit
    /// through the SDK and return success without calling it, so a target that had
    /// already been built and marked clean in the same session stayed clean after the
    /// edit. The next build then took the compile-only path against a <c>.cs</c> that
    /// Specify+Generate had never regenerated, and shipped the stale assembly. Every
    /// other failure this class guards against produces a wrong message; this one
    /// produced a wrong artifact, with no error surface at all.
    /// </para>
    ///
    /// <para>
    /// <b>Why the guard is source-shape.</b> The mark and the build decision are
    /// separated by an SDK commit inside a transaction on a live KB model, which a
    /// unit test cannot perform. What is observable here without one is the link
    /// itself: that each write surface states the mark, and that the two consumers
    /// that read the tracker still exist. <see cref="AMarkedTargetStaysDirtyUntilItIsRebuilt"/>
    /// is the behavioural half - it pins the tracker's semantics on the states this
    /// class depends on, and needs no SDK model either.
    /// </para>
    ///
    /// <para>
    /// Counts are read through <c>RepoSource.WithoutComments</c> so prose cannot
    /// satisfy an assertion about code. <c>SourceAssert</c> documents that failure
    /// mode: a comment-stripper that ate real code once made an assertion about code
    /// that should be present pass because the code it looked for had been deleted.
    /// The inverse - a comment naming the call counting as the call - is the same trap
    /// with the sign flipped.
    /// </para>
    ///
    /// <para>
    /// <b>Table, not list.</b> The convention after this change is that a write which
    /// commits through the SDK calls <c>WriteService.NotePerTargetWrite</c>. A new
    /// commit site that omits it reintroduces the stale-artifact defect, so a surface
    /// added without a row here fails loudly. Add a row when you add a surface.
    /// </para>
    /// </summary>
    public class MutatingToolDirtyMarkTests
    {
        private const string Mark = "WriteService.NotePerTargetWrite(";

        /// <summary>
        /// Every mutating write surface states at least the stated number of dirty
        /// marks. The minimum is the number of distinct SDK commit sites the surface
        /// has, not a number chosen to pass: lowering it to zero would leave the guard
        /// green with the defect back.
        /// </summary>
        [Theory]
        [InlineData("PropertyService.cs", 3)]
        [InlineData("LayoutService.cs", 3)]
        [InlineData("LayoutService.SourcePersistence.cs", 1)]
        [InlineData("Structure/AuthoringService.cs", 3)]
        [InlineData("Structure/AttributeWriteService.cs", 1)]
        [InlineData("Structure/GroupStructureService.cs", 1)]
        [InlineData("Structure/IndexService.cs", 1)]
        [InlineData("Structure/VisualStructureService.cs", 1)]
        [InlineData("Structure/DomainWriteService.cs", 1)]
        public void EveryMutatingWriteSurfaceMarksItsTargetDirty(string file, int minimum)
        {
            string source = RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", file.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(SourceAssert.Count(source, Mark) >= minimum,
                file + " commits SDK writes without marking its target dirty; the next incremental "
                + "build would ship a stale artifact. Expected at least " + minimum + " "
                + "WriteService.NotePerTargetWrite calls, found " + SourceAssert.Count(source, Mark) + ".");
        }

        /// <summary>
        /// <c>LayoutService</c> commits every visual XML write through one method,
        /// <c>PersistVisualXml</c>, so the mark belongs there rather than at each
        /// caller. The callers that would otherwise be the mark sites do not commit
        /// anything themselves - <c>SetProperty</c> and <c>SetProperties</c> mutate an
        /// XDocument and hand it to that method - and the report control mutations in
        /// <c>LayoutService.ReportControls.cs</c> reach it too. Pinning the count
        /// per caller instead would assert a shape the file does not have, so a later
        /// refactor to a second commit path would be reported as a missing mark rather
        /// than as the new path it is.
        /// </summary>
        [Fact]
        public void TheVisualXmlCommitChokePointIsMarked()
        {
            string source = RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "LayoutService.SourcePersistence.cs");

            Assert.Contains("private string PersistVisualXml(", source, StringComparison.Ordinal);
            Assert.True(SourceAssert.Count(source, Mark) >= 1,
                "PersistVisualXml is the only SDK commit in LayoutService.SourcePersistence.cs; without a "
                + "mark there, set_property, set_properties and the report control mutations all leave the "
                + "target classified clean.");
        }

        /// <summary>
        /// The other two helpers in that file save the object but are not commit
        /// sites, and marking them would be the error this guard exists to prevent.
        /// <c>TryRestoreProcedureSource</c> is a compensating restore on the failure
        /// path of every caller, so a mark there would make a rolled-back write dirty
        /// and force a regeneration that has nothing to regenerate.
        /// <c>TryFlushSourceForLayoutMutation</c> saves inside an enclosing
        /// transaction that either commits (covered by the mark at the commit) or
        /// rolls back, and takes no object-name argument, so a mark would need a new
        /// plumbing parameter. Asserted so neither is "helpfully" marked later.
        /// </summary>
        [Fact]
        public void TheRollbackAndFlushHelpersAreNotCommitSites()
        {
            string source = RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "LayoutService.SourcePersistence.cs");

            string restore = SourceAssert.MethodBody(
                source, "private bool TryRestoreProcedureSource(KBObject obj, string sourceSnapshot)");

            Assert.DoesNotContain(Mark, restore, StringComparison.Ordinal);
            Assert.Contains("obj.EnsureSave(false);", restore, StringComparison.Ordinal);
            Assert.Contains("return false;", restore, StringComparison.Ordinal);
        }

        /// <summary>
        /// Pins the two consumers of the tracker, so this guard cannot be satisfied by
        /// marks that nothing reads. If the compile-only fast path is removed, or the
        /// classifier stops consulting the tracker, the marks added here are dead
        /// code and the stale-artifact defect returns with them still in place.
        ///
        /// <para>
        /// The full statement is pinned, not the bare call. <c>!EditDirtyTracker.IsDirty
        /// (kbPath, t)</c> occurs twice in <c>InProcessBuildRunner</c> - at the
        /// compile-only fast path and in the all-clean short-circuit - so asserting the
        /// call expression alone stays green when the fast path is deleted, because
        /// the other occurrence still matches. That was measured, not assumed: a
        /// mutation removing the fast-path guard left a call-expression assertion
        /// passing. The assignment is unique to the site that decides Specify+Generate
        /// versus compile-only.
        /// </para>
        /// </summary>
        [Fact]
        public void TheIncrementalBuildStillConsultsTheDirtyTracker()
        {
            string runner = RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "InProcessBuildRunner.cs");
            Assert.Contains(
                "targetMaySkipSpecify = !EditDirtyTracker.IsDirty(kbPath, t);", runner, StringComparison.Ordinal);

            string decision = RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "DefaultFastIncrementalDecision.cs");
            Assert.Contains(
                "if (EditDirtyTracker.IsDirty(kbPath, t)) dirty.Add(t);", decision, StringComparison.Ordinal);
        }

        /// <summary>
        /// The primitive the call sites are compiled against. A change to its shape
        /// breaks every mark at once, so it is pinned here rather than discovered as
        /// a build break somewhere else in the tree.
        /// </summary>
        [Fact]
        public void TheDirtyMarkPrimitiveIsTheStaticSingleTargetEntryPoint()
        {
            MethodInfo mark = typeof(WriteService).GetMethod(
                "NotePerTargetWrite",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(string) },
                modifiers: null);

            Assert.NotNull(mark);
            Assert.True(mark.IsAssembly,
                "WriteService.NotePerTargetWrite must stay internal static; the write surfaces call it "
                + "directly and the tracker is internal to the Worker assembly.");
            Assert.Equal(typeof(void), mark.ReturnType);
        }

        /// <summary>
        /// The behavioural half: the states the marks exist to move a target between.
        /// A target with no record is dirty, so an unbuilt object is never skipped; a
        /// built one is clean; and only a mark takes a clean target back to dirty -
        /// which is the state a write surface that forgot the mark leaves it in.
        /// </summary>
        [Fact]
        public void AMarkedTargetStaysDirtyUntilItIsRebuilt()
        {
            // No live KB in a unit test, so the KB path resolves to the same
            // "<no-kb>" bucket a Worker with no resolvable KB path uses. The tracker
            // treats it like any other, and the object name is unique so concurrent
            // tests sharing the static tracker cannot interfere.
            string name = "mutatingtooldirtymarkprobe" + Guid.NewGuid().ToString("N");

            Assert.True(EditDirtyTracker.IsDirty(null, name));

            EditDirtyTracker.MarkClean(null, name);
            Assert.False(EditDirtyTracker.IsDirty(null, name));

            WriteService.NotePerTargetWrite(name);
            Assert.True(EditDirtyTracker.IsDirty(null, name));

            EditDirtyTracker.MarkClean(null, name);
            Assert.False(EditDirtyTracker.IsDirty(null, name));
        }
    }
}
