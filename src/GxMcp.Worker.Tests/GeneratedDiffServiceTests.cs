using System;
using System.Collections.Generic;
using System.IO;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class GeneratedDiffServiceTests
    {
        private sealed class FakeGitShell : IGitShell
        {
            public bool RepoFlag { get; set; }
            public Dictionary<string, string> HeadByPath { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public bool IsGitRepo(string workingDir) => RepoFlag;
            public bool TryShowHead(string workingDir, string repoRelativePath, out string content, out string error)
            {
                error = null;
                string norm = repoRelativePath.Replace("\\", "/");
                if (HeadByPath.TryGetValue(norm, out content)) return true;
                content = null;
                error = "not in HEAD";
                return false;
            }
        }

        [Fact]
        public void Diff_MissingTarget_ReturnsError()
        {
            var svc = new GeneratedDiffService(kbService: null, git: new FakeGitShell());
            var json = JObject.Parse(svc.Diff(null, "last-build"));
            Assert.Equal("error", (string)json["status"]!);
        }

        [Fact]
        public void Diff_InvalidAgainst_ReturnsError()
        {
            var svc = new GeneratedDiffService(kbService: null, git: new FakeGitShell());
            var json = JObject.Parse(svc.Diff("Foo", "yesterday"));
            Assert.Equal("error", (string)json["status"]!);
            Assert.Equal("InvalidAgainst", (string)json["error"]?["code"]!);
        }

        [Fact]
        public void UnifiedDiff_NoChange_ReturnsEmpty()
        {
            var (diff, added, removed) = GeneratedDiffService.UnifiedDiff(
                "line1\nline2\n", "line1\nline2\n", "x.cs");
            Assert.Equal(0, added);
            Assert.Equal(0, removed);
            Assert.Equal(string.Empty, diff);
        }

        [Fact]
        public void UnifiedDiff_CountsAddedAndRemoved()
        {
            var (diff, added, removed) = GeneratedDiffService.UnifiedDiff(
                "a\nb\nc\n", "a\nX\nc\nd\n", "x.cs");
            Assert.Equal(2, added);   // X, d
            Assert.Equal(1, removed); // b
            Assert.Contains("--- a/x.cs", diff);
            Assert.Contains("+++ b/x.cs", diff);
            Assert.Contains("-b", diff);
            Assert.Contains("+X", diff);
            Assert.Contains("+d", diff);
        }

        [Fact]
        public void FindGeneratedFiles_LocatesUnderCSharpModelWeb()
        {
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-test-" + Guid.NewGuid().ToString("N"));
            string web = Path.Combine(tempKb, "CSharpModel", "Web");
            Directory.CreateDirectory(web);
            string objName = "Foo";
            File.WriteAllText(Path.Combine(web, objName + ".cs"), "class Foo {}");
            File.WriteAllText(Path.Combine(web, objName + ".aspx"), "<%@ %>");
            File.WriteAllText(Path.Combine(web, "Other.cs"), "class Other {}");
            try
            {
                var files = GeneratedDiffService.FindGeneratedFiles(tempKb, objName);
                Assert.Equal(2, files.Count);
            }
            finally
            {
                try { Directory.Delete(tempKb, true); } catch { }
            }
        }

        [Fact]
        public void FindGeneratedFiles_SingleWalk_ExcludesOverMatchesAndUnrelatedFiles()
        {
            // Plan 039: FindGeneratedFiles walks each root once and filters extensions
            // in memory. "Foo.*" is a broad glob, so this proves the precise-filename
            // filter still excludes target.cs.bak and targetX.cs style over-matches.
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-test-" + Guid.NewGuid().ToString("N"));
            string web = Path.Combine(tempKb, "CSharpModel", "Web");
            Directory.CreateDirectory(web);
            string objName = "Foo";
            File.WriteAllText(Path.Combine(web, objName + ".cs"), "class Foo {}");
            File.WriteAllText(Path.Combine(web, objName + ".aspx"), "<%@ %>");
            File.WriteAllText(Path.Combine(web, objName + ".cs.bak"), "stale");
            File.WriteAllText(Path.Combine(web, "FooBar.cs"), "class FooBar {}");
            try
            {
                var files = GeneratedDiffService.FindGeneratedFiles(tempKb, objName);
                Assert.Equal(2, files.Count);
                Assert.Contains(files, f => f.EndsWith("Foo.cs", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(files, f => f.EndsWith("Foo.aspx", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(files, f => f.EndsWith("Foo.cs.bak", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(files, f => f.EndsWith("FooBar.cs", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                try { Directory.Delete(tempKb, true); } catch { }
            }
        }

        // ── issue #42: environment web-dir discovery + freshness gate ─────────

        [Fact]
        public void DiscoverEnvironmentWebDirs_FindsEnvWebFolder()
        {
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(tempKb, "NETCoreMySQL", "web"));
            Directory.CreateDirectory(Path.Combine(tempKb, ".gx", "web")); // must be skipped (dotdir)
            Directory.CreateDirectory(Path.Combine(tempKb, "SomeOther"));   // no web subdir
            try
            {
                var dirs = new List<string>(GeneratedDiffService.DiscoverEnvironmentWebDirs(tempKb));
                Assert.Contains(dirs, d => d.EndsWith(Path.Combine("NETCoreMySQL", "web"), StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(dirs, d => d.Contains(".gx"));
            }
            finally { try { Directory.Delete(tempKb, true); } catch { } }
        }

        [Fact]
        public void FindGeneratedFiles_AllRoots_LocatesUnderNetCoreEnvWeb()
        {
            // Reporter's env: generated .cs lands in <KB>\NETCoreMySQL\web, which the
            // fixed candidate list does not cover — discovery must find it.
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-test-" + Guid.NewGuid().ToString("N"));
            string web = Path.Combine(tempKb, "NETCoreMySQL", "web");
            Directory.CreateDirectory(web);
            File.WriteAllText(Path.Combine(web, "MyProc.cs"), "class MyProc {}");
            try
            {
                var files = GeneratedDiffService.FindGeneratedFiles(tempKb, "MyProc", allRoots: true);
                Assert.Single(files);
                Assert.EndsWith("MyProc.cs", files[0]);
            }
            finally { try { Directory.Delete(tempKb, true); } catch { } }
        }

        [Fact]
        public void ProbeGeneratedFreshness_FreshFile_IsFresh()
        {
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-test-" + Guid.NewGuid().ToString("N"));
            string web = Path.Combine(tempKb, "NETCoreMySQL", "web");
            Directory.CreateDirectory(web);
            string f = Path.Combine(web, "Fresh.cs");
            File.WriteAllText(f, "class Fresh {}");
            var sinceUtc = DateTime.UtcNow.AddMinutes(-5); // file written after this
            try
            {
                var ev = GeneratedDiffService.ProbeGeneratedFreshness(tempKb, "Fresh", sinceUtc);
                Assert.True(ev.Found);
                Assert.True(ev.Fresh);
                Assert.Equal(1, ev.FileCount);
            }
            finally { try { Directory.Delete(tempKb, true); } catch { } }
        }

        [Fact]
        public void ProbeGeneratedFreshness_StaleFile_IsNotFresh()
        {
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-test-" + Guid.NewGuid().ToString("N"));
            string web = Path.Combine(tempKb, "NETCoreMySQL", "web");
            Directory.CreateDirectory(web);
            string f = Path.Combine(web, "Stale.cs");
            File.WriteAllText(f, "class Stale {}");
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddHours(-2)); // written well before the build
            var sinceUtc = DateTime.UtcNow.AddMinutes(-1);            // build started 1 min ago
            try
            {
                var ev = GeneratedDiffService.ProbeGeneratedFreshness(tempKb, "Stale", sinceUtc);
                Assert.True(ev.Found);       // file exists...
                Assert.False(ev.Fresh);      // ...but is older than the build → gap
            }
            finally { try { Directory.Delete(tempKb, true); } catch { } }
        }

        [Fact]
        public void ProbeGeneratedFreshness_MissingFile_NotFoundNotFresh()
        {
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(tempKb, "NETCoreMySQL", "web"));
            try
            {
                var ev = GeneratedDiffService.ProbeGeneratedFreshness(tempKb, "NeverGenerated", DateTime.UtcNow.AddMinutes(-1));
                Assert.False(ev.Found);
                Assert.False(ev.Fresh);
                Assert.Equal(0, ev.FileCount);
            }
            finally { try { Directory.Delete(tempKb, true); } catch { } }
        }

        // ── issue #42 hardening (C): freshness via pre-build mtime snapshot ────

        [Fact]
        public void ProbeGeneratedFreshness_PriorMtime_MtimeAdvanced_IsFresh()
        {
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-test-" + Guid.NewGuid().ToString("N"));
            string web = Path.Combine(tempKb, "NETCoreMySQL", "web");
            Directory.CreateDirectory(web);
            string f = Path.Combine(web, "Moved.cs");
            File.WriteAllText(f, "class Moved {}");
            var prior = DateTime.UtcNow.AddHours(-1);      // snapshot BEFORE build
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow);  // generator rewrote it
            try
            {
                // sinceUtc deliberately in the future so the absolute path would say
                // "stale" — the snapshot comparison must win and report fresh.
                var ev = GeneratedDiffService.ProbeGeneratedFreshness(tempKb, "Moved", DateTime.UtcNow.AddYears(1), prior);
                Assert.True(ev.Fresh);
            }
            finally { try { Directory.Delete(tempKb, true); } catch { } }
        }

        [Fact]
        public void ProbeGeneratedFreshness_PriorMtime_MtimeUnchanged_IsNotFresh()
        {
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-test-" + Guid.NewGuid().ToString("N"));
            string web = Path.Combine(tempKb, "NETCoreMySQL", "web");
            Directory.CreateDirectory(web);
            string f = Path.Combine(web, "Untouched.cs");
            File.WriteAllText(f, "class Untouched {}");
            var mtime = DateTime.UtcNow.AddMinutes(-30);
            File.SetLastWriteTimeUtc(f, mtime);
            try
            {
                // Incremental generator skipped the file: mtime == snapshot. Even with a
                // generous absolute slack it must not be reported fresh.
                var ev = GeneratedDiffService.ProbeGeneratedFreshness(tempKb, "Untouched", DateTime.UtcNow.AddYears(-1), mtime);
                Assert.True(ev.Found);
                Assert.False(ev.Fresh);
            }
            finally { try { Directory.Delete(tempKb, true); } catch { } }
        }

        [Fact]
        public void ListGeneratedFiles_MainProcedure_ListsStubProgramAndDlls_WithBuildFlag()
        {
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-test-" + Guid.NewGuid().ToString("N"));
            string web = Path.Combine(tempKb, "NETCoreMySQL", "web");
            string bin = Path.Combine(web, "bin");
            Directory.CreateDirectory(bin);
            var buildStart = DateTime.UtcNow.AddMinutes(-5);
            string stub = Path.Combine(web, "sampleproc.cs");
            string program = Path.Combine(web, "asampleproc.cs");
            string dll = Path.Combine(bin, "asampleproc.dll");
            string oldDll = Path.Combine(bin, "sampleproc.dll");
            foreach (var f in new[] { stub, program, dll, oldDll }) File.WriteAllText(f, "x");
            File.SetLastWriteTimeUtc(oldDll, buildStart.AddHours(-1));
            File.WriteAllText(Path.Combine(web, "sampleprocother.cs"), "x");
            try
            {
                var files = GeneratedDiffService.ListGeneratedFiles(tempKb, "sampleproc", buildStart);
                var byName = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
                foreach (JObject o in files) byName[Path.GetFileName((string)o["path"])] = o;

                Assert.Equal(4, files.Count);
                Assert.True((bool)byName["sampleproc.cs"]["writtenDuringBuild"]);
                Assert.True((bool)byName["asampleproc.cs"]["writtenDuringBuild"]);
                Assert.True((bool)byName["asampleproc.dll"]["writtenDuringBuild"]);
                Assert.False((bool)byName["sampleproc.dll"]["writtenDuringBuild"]);
                Assert.NotNull(byName["asampleproc.cs"]["lastWriteUtc"]);
            }
            finally { try { Directory.Delete(tempKb, true); } catch { } }
        }

        [Fact]
        public void ListGeneratedFiles_ScopesToPreferredEnvironment()
        {
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-test-" + Guid.NewGuid().ToString("N"));
            string devWeb = Path.Combine(tempKb, "DevelopmentWeb", "web");
            string prodWeb = Path.Combine(tempKb, "ProductionWeb", "web");
            Directory.CreateDirectory(Path.Combine(devWeb, "bin"));
            Directory.CreateDirectory(Path.Combine(prodWeb, "bin"));
            File.WriteAllText(Path.Combine(devWeb, "Scoped.cs"), "x");
            File.WriteAllText(Path.Combine(devWeb, "bin", "Scoped.dll"), "x");
            File.WriteAllText(Path.Combine(prodWeb, "Scoped.cs"), "x");
            File.WriteAllText(Path.Combine(prodWeb, "bin", "Scoped.dll"), "x");
            try
            {
                var files = GeneratedDiffService.ListGeneratedFiles(tempKb, "Scoped", DateTime.UtcNow.AddMinutes(-5), devWeb);
                Assert.Equal(2, files.Count);
                foreach (JObject o in files) Assert.Contains("DevelopmentWeb", (string)o["path"]);
            }
            finally { try { Directory.Delete(tempKb, true); } catch { } }
        }

        [Fact]
        public void ProbeGeneratedFreshness_ScopesEvidenceToPreferredEnvironment()
        {
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-test-" + Guid.NewGuid().ToString("N"));
            string devWeb = Path.Combine(tempKb, "DevelopmentWeb", "web");
            string prodWeb = Path.Combine(tempKb, "ProductionWeb", "web");
            Directory.CreateDirectory(devWeb);
            Directory.CreateDirectory(prodWeb);
            File.WriteAllText(Path.Combine(devWeb, "Scoped.cs"), "class Dev {} ");
            File.WriteAllText(Path.Combine(prodWeb, "Scoped.cs"), "class Prod {} ");
            File.SetLastWriteTimeUtc(Path.Combine(devWeb, "Scoped.cs"), DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(Path.Combine(prodWeb, "Scoped.cs"), DateTime.UtcNow.AddMinutes(-1));
            try
            {
                var ev = GeneratedDiffService.ProbeGeneratedFreshness(
                    tempKb, "Scoped", DateTime.UtcNow.AddHours(-3), null, devWeb);
                Assert.True(ev.Found);
                Assert.EndsWith("DevelopmentWeb/web/Scoped.cs", ev.FreshestPath.Replace('\\', '/'));
                Assert.DoesNotContain("ProductionWeb", ev.FreshestPath, StringComparison.OrdinalIgnoreCase);
            }
            finally { try { Directory.Delete(tempKb, true); } catch { } }
        }

        // ── issue #42 hardening (B): scan pruning + no full-tree fallback ─────

        [Fact]
        public void FindGeneratedFiles_SkipsNoiseDirs()
        {
            // A same-named .cs sitting in a VCS/backup/build dir must never be matched.
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-test-" + Guid.NewGuid().ToString("N"));
            string web = Path.Combine(tempKb, "CSharpModel", "Web");
            Directory.CreateDirectory(web);
            File.WriteAllText(Path.Combine(web, "Widget.cs"), "class Widget {}");
            // Noise copies that must be pruned:
            foreach (var noise in new[] { "GXcvt", ".git", "obj" })
            {
                string nd = Path.Combine(web, noise);
                Directory.CreateDirectory(nd);
                File.WriteAllText(Path.Combine(nd, "Widget.cs"), "class WidgetBackup {}");
            }
            try
            {
                var files = GeneratedDiffService.FindGeneratedFiles(tempKb, "Widget", allRoots: true);
                Assert.Single(files);
                Assert.DoesNotContain(files, f => f.Contains("GXcvt") || f.Contains(".git") || f.Contains("obj"));
            }
            finally { try { Directory.Delete(tempKb, true); } catch { } }
        }

        [Fact]
        public void BuildCandidateRoots_EnvWebPresent_OmitsFullTreeFallback()
        {
            // When an environment web dir exists, the expensive last-resort full-KB scan
            // (kbPath root) must not be appended — the generated .cs lives under env web.
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(tempKb, "NETCoreMySQL", "web"));
            try
            {
                var roots = GeneratedDiffService.BuildCandidateRoots(tempKb);
                Assert.DoesNotContain(roots, r => string.Equals(r, tempKb, StringComparison.OrdinalIgnoreCase));
            }
            finally { try { Directory.Delete(tempKb, true); } catch { } }
        }

        [Fact]
        public void BuildCandidateRoots_NoEnvWebNoWellKnown_KeepsFullTreeFallback()
        {
            // With neither an env web dir nor a well-known output dir on disk, the
            // full-tree fallback is the only way to locate anything — keep it.
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempKb);
            try
            {
                var roots = GeneratedDiffService.BuildCandidateRoots(tempKb);
                Assert.Contains(roots, r => string.Equals(r, tempKb, StringComparison.OrdinalIgnoreCase));
            }
            finally { try { Directory.Delete(tempKb, true); } catch { } }
        }
    }
}
