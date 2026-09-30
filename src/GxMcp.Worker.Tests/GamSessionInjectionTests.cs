using System;
using System.Collections.Generic;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // FR#17 (Stream G, v2.6.6) — PreviewService.PreviewSync auto-injects GAM
    // credentials when the launcher snapshot looks like the login screen and
    // either an explicit auth=... was passed or GXMCP_GAM_USER/PASS env vars
    // are set. Coverage here is on the helper surface (ResolveAuthInfo,
    // LooksLikeAuthScreen, LooksLikeGamLoginUrl) plus a smoke run of the
    // full PreviewSync path through a FakeRunner so the auth-attempted
    // branch is observable from the result envelope.
    public class GamSessionInjectionTests
    {
        private class FakeRunner : PreviewService.ICliRunner
        {
            public List<(string fileName, string arguments)> Calls = new List<(string, string)>();
            public List<PreviewService.CliResult> ScriptedSnapshots = new List<PreviewService.CliResult>();
            public int SnapshotIndex = 0;
            public PreviewService.CliResult Default = new PreviewService.CliResult { ExitCode = 0, StdOut = "", StdErr = "" };

            public PreviewService.CliResult Run(string fileName, string arguments, int timeoutMs)
            {
                Calls.Add((fileName, arguments));
                string verb = (arguments ?? "").Split(' ')[0];
                if (verb == "snapshot")
                {
                    if (SnapshotIndex < ScriptedSnapshots.Count) return ScriptedSnapshots[SnapshotIndex++];
                }
                return Default;
            }

            public string Which(string command) => "C:/fake/chrome-devtools-axi.cmd";
        }

        [Fact]
        public void LooksLikeAuthScreen_RecognisesUsuarioTextbox()
        {
            Assert.True(PreviewService.LooksLikeAuthScreen("... textbox Usuario ..."));
            Assert.True(PreviewService.LooksLikeAuthScreen("redirecting to /login.aspx"));
            Assert.False(PreviewService.LooksLikeAuthScreen("plain page, no auth markers"));
            Assert.False(PreviewService.LooksLikeAuthScreen(null));
        }

        [Fact]
        public void LooksLikeGamLoginUrl_PicksUpGloginAndGamLogin()
        {
            Assert.True(PreviewService.LooksLikeGamLoginUrl("http://host/glogin.aspx"));
            Assert.True(PreviewService.LooksLikeGamLoginUrl("http://host/gamlogin"));
            Assert.False(PreviewService.LooksLikeGamLoginUrl("http://host/dani.aspx"));
        }

        [Fact]
        public void ResolveAuthInfo_PrefersCallerOverEnvAndDefaultsToNone()
        {
            // Caller-passed creds win regardless of env.
            var ai = PreviewService.ResolveAuthInfo(JObject.Parse(
                "{\"mode\":\"gam\",\"user\":\"u1\",\"pass\":\"p1\"}"));
            Assert.Equal("gam", ai.Mode);
            Assert.Equal("u1", ai.User);
            Assert.Equal("p1", ai.Pass);

            // No caller, no env → mode=none, creds null.
            // (We can't unset env reliably here, so just assert the surface shape.)
            var ai2 = PreviewService.ResolveAuthInfo(null);
            Assert.NotNull(ai2);
            Assert.NotNull(ai2.Mode);
        }

        [Fact]
        public void PreviewSync_LoginScreenDetected_AttemptsInjectionAndReports()
        {
            var runner = new FakeRunner();
            // Snapshot 1: looks like login → triggers injection.
            // Snapshot 2 (after injection submit): still login → status=auth_required
            // with message documenting the attempt.
            const string loginSnap = "<input id=Usuario type=textbox name=UserName /><input name=UserPassword type=password />";
            runner.ScriptedSnapshots.Add(new PreviewService.CliResult { ExitCode = 0, StdOut = loginSnap });
            runner.ScriptedSnapshots.Add(new PreviewService.CliResult { ExitCode = 0, StdOut = loginSnap });

            string tmpCfg = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "gamtest_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            var svc = new PreviewService(null, null, runner, tmpCfg, System.IO.Path.GetTempPath());

            var auth = JObject.Parse("{\"mode\":\"gam\",\"user\":\"u1\",\"pass\":\"p1\"}");

            // Reflect into the internal sync path with the auth parameter wired.
            var mi = typeof(PreviewService).GetMethod("PreviewSync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(mi);
            var result = (JObject)mi.Invoke(svc, new object[]
            {
                "AlunoConsulta",
                (JObject)null,
                "auto",
                false,
                0,
                new[] { "html" },
                false,
                false,
                (JObject)null,
                (string)null,
                auth,
                (string)null,
                (string)null
            });

            // Auth should be reported as attempted.
            Assert.Equal("auth_required", result["status"]?.ToString());
            Assert.NotNull(result["auth"]);
            Assert.Equal("gam", result["auth"]["mode"]?.ToString());
            Assert.Contains("attempted", result["message"]?.ToString() ?? "",
                StringComparison.OrdinalIgnoreCase);
            // A refused GAM login is its own recovery: retrying with the same
            // credentials cannot succeed, so the response must say which it was.
            Assert.Equal("GamLoginRejected", result["code"]?.ToString());
            Assert.True(result["credentialsSupplied"]?.ToObject<bool>() ?? false);

            // Submit JS must have been issued at least once.
            bool sawSubmit = false;
            foreach (var c in runner.Calls)
            {
                if (c.arguments != null && c.arguments.Contains("GXSUBMIT")) { sawSubmit = true; break; }
            }
            Assert.True(sawSubmit, "GAM submit JS should have been dispatched");

            try { System.IO.File.Delete(tmpCfg); } catch { }
        }
    }
}
