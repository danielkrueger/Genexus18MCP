using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// A preview that lands on an auth wall used to answer with a bare
    /// <c>status: auth_required</c>, leaving an agent to string-match the message to
    /// learn whether it was a GAM redirect and whether credentials were even tried.
    /// The three cases have three different recoveries, so each gets its own code.
    /// </summary>
    public class PreviewAuthWallTests
    {
        private const string GamLauncher = "http://host:8080/dani.aspx";
        private const string GamLogin = "http://host:8080/gamlogin.aspx";

        [Fact]
        public void GamRedirectWithNoCredentials_AsksForCredentials()
        {
            var r = PreviewService.AuthRequiredResult(new JObject(), GamLauncher, GamLogin, authAttempted: false);

            Assert.Equal("auth_required", (string)r["status"]);
            Assert.Equal("GamLoginRequired", (string)r["code"]);
            Assert.Equal("gam", (string)r["authWall"]);
            Assert.False((bool)r["credentialsSupplied"]);
            Assert.Contains("auth=", (string)r["hint"]);
            Assert.Contains("GXMCP_GAM_USER", (string)r["hint"]);
        }

        [Fact]
        public void GamRedirectWithRejectedCredentials_SaysTheCredentialsWereTried()
        {
            // Distinct from GamLoginRequired: retrying with the same credentials is
            // useless here, so the hint must point at the account, not at the parm.
            var r = PreviewService.AuthRequiredResult(new JObject(), GamLauncher, GamLogin, authAttempted: true);

            Assert.Equal("GamLoginRejected", (string)r["code"]);
            Assert.True((bool)r["credentialsSupplied"]);
            Assert.DoesNotContain("GXMCP_GAM_USER", (string)r["hint"]);
        }

        [Fact]
        public void NonGamAuthWall_DoesNotAdviseGamCredentials()
        {
            var r = PreviewService.AuthRequiredResult(
                new JObject(), GamLauncher, "http://host:8080/intranet/login", authAttempted: false);

            Assert.Equal("AuthRequired", (string)r["code"]);
            Assert.Equal("unknown", (string)r["authWall"]);
            // Offering GAM credentials for a non-GAM wall sends the agent in circles.
            Assert.DoesNotContain("GXMCP_GAM_USER", (string)r["hint"]);
        }

        [Fact]
        public void EveryCaseCarriesAUrlAndANextStep()
        {
            foreach (var attempted in new[] { false, true })
            {
                var r = PreviewService.AuthRequiredResult(new JObject(), GamLauncher, GamLogin, attempted);

                Assert.Equal(GamLauncher, (string)r["url"]);
                Assert.Equal(GamLogin, (string)r["finalUrl"]);
                Assert.False(string.IsNullOrWhiteSpace((string)r["nextStep"]));
                Assert.False(string.IsNullOrWhiteSpace((string)r["message"]));
            }
        }

        [Fact]
        public void TheResponseNeverEchoesCredentials()
        {
            // The wall report is returned to the agent; a password in it would be a
            // credential leak into a transcript.
            var r = PreviewService.AuthRequiredResult(new JObject(), GamLauncher, GamLogin, authAttempted: true);

            var text = r.ToString(Newtonsoft.Json.Formatting.None);
            Assert.DoesNotContain("pass", text.Replace("password", ""));
            Assert.Equal(new[] { "authWall", "code", "credentialsSupplied", "finalUrl", "hint", "message", "nextStep", "status", "url" }.OrderBy(k => k, System.StringComparer.Ordinal),
                         r.Properties().Select(p => p.Name).OrderBy(k => k, System.StringComparer.Ordinal));
        }

        [Fact]
        public void GamUrlIsDetectedFromEitherUrl()
        {
            // The launcher can be the login page itself (no redirect), or the redirect
            // can only be visible in the final URL. Both must classify as GAM.
            Assert.True(PreviewService.LooksLikeGamLoginUrl(GamLogin));
            Assert.True(PreviewService.LooksLikeGamLoginUrl("http://host/GAMLOGIN/index.aspx"));
            Assert.False(PreviewService.LooksLikeGamLoginUrl(GamLauncher));
            Assert.False(PreviewService.LooksLikeGamLoginUrl(null));
        }
    }
}
