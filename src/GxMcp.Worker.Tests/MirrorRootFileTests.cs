using System;
using System.IO;
using GxMcp.Worker;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The mirror-root containment guard and its delete primitive existed twice,
    /// byte-identical, in <c>SdkTextTreeService.Support</c> and
    /// <c>TextMirrorService.Reconciliation</c>. They are now
    /// <c>MirrorRootFile</c>, reached from eight call sites across both services.
    ///
    /// The resolution half is a containment guard: a manifest on disk names a file,
    /// the file is combined onto the mirror root, and the result has to still be
    /// under that root. Both services then <i>delete</i> what it resolves to. A
    /// guard that decides what may be deleted, duplicated, is a traversal hole
    /// waiting on whichever copy somebody edits next - and there were no tests on
    /// it at all before this, so nothing would have caught that.
    ///
    /// Nothing here touches the GeneXus SDK, so these run against a real temp
    /// directory rather than a KB.
    /// </summary>
    public class MirrorRootFileTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "gxmcp-mirror-" + Guid.NewGuid().ToString("N"));

        public MirrorRootFileTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "nested"));
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { /* best effort */ }
            foreach (string leftover in _outsideFiles)
            {
                try { if (File.Exists(leftover)) File.Delete(leftover); }
                catch { /* best effort */ }
            }
            _outsideFiles.Clear();
        }

        private string Write(string relative, string content = "x")
        {
            string full = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
            return full;
        }

        [Theory]
        [InlineData("a.txt")]
        [InlineData("nested/a.txt")]
        [InlineData("nested/deep/b.txt")]
        public void ARelativePathInsideTheRootResolves(string relative)
        {
            Assert.True(MirrorRootFile.TryResolve(_root, relative, out string full));
            Assert.Equal(
                Path.GetFullPath(Path.Combine(_root, relative)),
                Path.GetFullPath(full));
        }

        [Fact]
        public void AnAbsolutePathIsRejected()
        {
            // Path.Combine discards everything before an absolute second argument,
            // so "C:\Windows\System32\config" combined onto the root is just
            // itself. Rejecting rooted input is the first line of defence; the
            // containment re-check below is the second.
            string outside = Write_outside("system.ini");

            Assert.False(MirrorRootFile.TryResolve(_root, outside, out string full));
            Assert.Null(full);
            Assert.False(MirrorRootFile.TryResolve(_root, "/etc/passwd", out full));
            Assert.Null(full);
        }

        [Theory]
        [InlineData("../escaped.txt")]
        [InlineData("../../escaped.txt")]
        [InlineData("nested/../../escaped.txt")]
        [InlineData("nested/deep/../../../escaped.txt")]
        public void ATraversalOutOfTheRootIsRejected(string relative)
        {
            Assert.False(MirrorRootFile.TryResolve(_root, relative, out string full));
            Assert.Null(full);
        }

        [Fact]
        public void ATraversalThatLandsBackInsideIsStillAccepted()
        {
            // The guard is containment, not a ban on the ".." token: the resolved
            // path is what matters, and this one really is inside the root.
            Write("target.txt");
            Assert.True(MirrorRootFile.TryResolve(_root, "nested/../target.txt", out string full));
            Assert.Equal(Path.GetFullPath(Path.Combine(_root, "target.txt")), Path.GetFullPath(full));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void ABlankPathIsRejected(string relative)
        {
            Assert.False(MirrorRootFile.TryResolve(_root, relative, out string full));
            Assert.Null(full);
        }

        [Fact]
        public void ARootThatIsAlsoAPrefixOfAnotherDirectoryIsNotInside()
        {
            // "...\mirror" must not contain "...\mirror-evil". This is the case a
            // naive StartsWith on the un-trimmed, un-separated root gets wrong.
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-prefix-" + Guid.NewGuid().ToString("N"));
            string sibling = root + "-evil";
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(sibling);
            try
            {
                File.WriteAllText(Path.Combine(sibling, "secret.txt"), "secret");

                Assert.False(MirrorRootFile.TryResolve(root, "../" + Path.GetFileName(sibling) + "/secret.txt", out _));
            }
            finally
            {
                try { Directory.Delete(root, true); Directory.Delete(sibling, true); } catch { }
            }
        }

        [Fact]
        public void AMalformedRootDoesNotThrow()
        {
            // Callers are doing cleanup and counting removals; a throw here would
            // abandon every remaining manifest entry.
            Assert.False(MirrorRootFile.TryResolve("\0invalid", "a.txt", out _));
            Assert.False(MirrorRootFile.TryResolve("", "a.txt", out _));
        }

        [Fact]
        public void DeleteRemovesAnExistingFileAndReportsTrue()
        {
            string full = Write("gone.txt");
            Assert.True(File.Exists(full));

            Assert.True(MirrorRootFile.TryDelete(full));
            Assert.False(File.Exists(full));
        }

        [Fact]
        public void DeleteReportsFalseForAMissingFile()
        {
            // Reconciliation counts removals, so a file that is already gone is a
            // false rather than an error.
            Assert.False(MirrorRootFile.TryDelete(Path.Combine(_root, "never-existed.txt")));
            Assert.False(MirrorRootFile.TryDelete(null));
        }

        [Fact]
        public void ResolvedThenDeletedIsTheOrderTheCallersUse()
        {
            // The pair only matters in this order: resolve-and-contain first, then
            // delete what came back. The file named by "../escaped.txt" is real and
            // is deliberately left on disk - that it survives is the whole point.
            Write("a.txt");
            Write("nested/b.txt");
            string outside = Write_outside("escaped.txt");

            int removed = 0;
            foreach (string relative in new[] { "a.txt", "nested/b.txt", "../escaped.txt" })
                if (MirrorRootFile.TryResolve(_root, relative, out string full) && MirrorRootFile.TryDelete(full)) removed++;

            Assert.Equal(2, removed);
            Assert.False(File.Exists(Path.Combine(_root, "a.txt")));
            Assert.False(File.Exists(Path.Combine(_root, "nested", "b.txt")));
            Assert.True(File.Exists(outside), "a file outside the mirror root must survive reconciliation");
        }

        [Fact]
        public void TheGuardIsDefinedOnceAndReachedFromEveryCaller()
        {
            // Two callers, one implementation. A third inline copy re-opens the hole.
            string support = RepoSource.Read("src", "GxMcp.Worker", "Services", "SdkTextTreeService.Support.cs");
            string export = RepoSource.Read("src", "GxMcp.Worker", "Services", "SdkTextTreeService.Export.cs");
            string reconciliation = RepoSource.Read("src", "GxMcp.Worker", "Services", "TextMirrorService.Reconciliation.cs");

            foreach (string src in new[] { support, export, reconciliation })
            {
                Assert.Contains("MirrorRootFile.TryResolve", src);
                Assert.DoesNotContain("TryGetRootFile", src);
                Assert.DoesNotContain("private static bool DeleteFile", src);
            }

            string helper = RepoSource.Read("src", "GxMcp.Worker", "Helpers", "MirrorRootFile.cs");
            Assert.Equal(1, CountOccurrences(helper, "internal static bool TryResolve("));
            Assert.Equal(1, CountOccurrences(helper, "internal static bool TryDelete("));
            Assert.Equal(0, CountOccurrences(helper, "TryGetRootFile"));
        }

        private string Write_outside(string name)
        {
            string full = Path.Combine(Path.GetDirectoryName(_root), name);
            File.WriteAllText(full, "outside");
            _outsideFiles.Add(full);
            return full;
        }

        private readonly System.Collections.Generic.List<string> _outsideFiles = new System.Collections.Generic.List<string>();

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

    }
}