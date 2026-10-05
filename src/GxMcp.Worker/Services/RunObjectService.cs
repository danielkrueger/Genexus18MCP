using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 11 (improvements 2026-05-22): resolves the runtime URL for a KB
    /// object — the baseUrl set by the user in preview.config.json, else the
    /// active Environment's WebRoot (IIS virtual directory) + the object's generated
    /// aspx file name + URL-encoded positional parms — without opening a
    /// browser. Optionally performs an HTTP-level GAM login and returns the
    /// resulting cookie set so the agent can pipe the URL into
    /// <c>chrome-devtools-axi open</c> with the cookies already warm.
    /// </summary>
    public class RunObjectService
    {
        private readonly ObjectService _objectService;
        private readonly KbService _kbService;
        private readonly PreviewService _previewService;
        // Test seam — when set, HTTP login is delegated to the fake rather than
        // going to a live HTTP server. Receives (loginUrl, user, pass), returns
        // (cookieHeader, signedIn, error).
        public Func<string, string, string, (string cookieHeader, bool signedIn, string error)> LoginHook;
        // Test seam — replaces the KB lookup of the active environment's WebRoot.
        public Func<string> WebRootHook;

        // preview.config.json used to be auto-created with this value; it is not a
        // user choice, so it must not shadow the environment's WebRoot.
        private const string LegacyAutoBaseUrl = "http://localhost/portal3_desenv";

        public RunObjectService(ObjectService objectService, KbService kbService, PreviewService previewService)
        {
            _objectService = objectService;
            _kbService = kbService;
            _previewService = previewService;
        }

        /// <summary>Resolves URL + parm encoding + optional GAM login cookies.</summary>
        public string Resolve(string name, JArray args, JToken gamSession, bool dryRun = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name))
                    return McpResponse.Err(
                        code: "MissingName",
                        message: "name is required.",
                        nextSteps: new JArray { McpResponse.NextStep("genexus_run_object", new JObject { ["name"] = "<objectName>" }, "Provide the object name to resolve its URL.") });

                // 1) Resolve aspx filename. GeneXus generates lowercase <name>.aspx.
                string aspxName = name.ToLowerInvariant() + ".aspx";

                // 2) Resolve baseUrl: explicit user baseUrl in preview.config.json wins,
                //    else http://localhost/<active environment WebRoot>. No invented default.
                JObject cfg = null;
                try { cfg = _previewService?.LoadConfig(); } catch { }
                string baseUrl = ExplicitBaseUrl(cfg);
                string baseUrlSource = "config";
                if (baseUrl == null)
                {
                    string webRoot = null;
                    try { webRoot = WebRootHook != null ? WebRootHook() : _kbService?.GetActiveEnvironmentWebRoot(); } catch { }
                    if (string.IsNullOrWhiteSpace(webRoot))
                        return McpResponse.Err(
                            code: "BaseUrlUnresolved",
                            message: "Could not determine the base URL: no baseUrl in preview.config.json and the active environment exposes no WebRoot.",
                            hint: "Open a KB with an active environment, or set \"baseUrl\" (e.g. http://localhost/<virtual directory>) in preview.config.json.",
                            nextSteps: new JArray { McpResponse.NextStep("genexus_run_object", new JObject { ["name"] = name }, "Retry after the KB is open, or set baseUrl in preview.config.json.") });
                    baseUrl = BaseUrlFromWebRoot(webRoot);
                    baseUrlSource = "environment";
                }

                // 3) Build positional query string. Signature lookup is best-effort:
                //    when ObjectService is unavailable (tests) or the object has no
                //    Parm rule, fall back to p1,p2,p3.
                var parmNames = TryResolveParmNames(name);
                string query = EncodeArgs(args, parmNames);

                string url = baseUrl + "/" + aspxName + (string.IsNullOrEmpty(query) ? string.Empty : "?" + query);

                // Bug #5: the URL points at the IIS vroot (e.g. localhost/portal3_desenv),
                // which serves the last FULL deploy. A fast-path MCP build (genexus_lifecycle
                // action=build) compiles the object but does NOT publish the generated .aspx
                // to that vroot, so the page opened here may not reflect a recent MCP edit.
                const string deploymentNote =
                    "This URL is served by the IIS virtual directory (its own vroot), which reflects the last FULL deploy — not necessarily your most recent MCP build. Fast-path builds (genexus_lifecycle action=build) compile+targeted-deploy but skip publishing the .aspx to the vroot. If the page looks stale, run genexus_lifecycle action=build with deploy=true (or action=rebuild), or publish from the GeneXus IDE (F5 / Run).";

                // dryRun: return the resolved URL without performing GAM login.
                if (dryRun)
                {
                    return McpResponse.Ok(
                        target: name,
                        code: "DryRun",
                        result: new JObject
                        {
                            ["preview"] = new JObject
                            {
                                ["url"] = url,
                                ["aspxName"] = aspxName,
                                ["baseUrl"] = baseUrl,
                                ["baseUrlSource"] = baseUrlSource,
                                ["note"] = "dryRun=true: GAM login not performed.",
                                ["deploymentNote"] = deploymentNote
                            }
                        });
                }

                var resultPayload = new JObject
                {
                    ["url"] = url,
                    ["baseUrlSource"] = baseUrlSource,
                    ["signedIn"] = false,
                    ["hint"] = "Pass the url to `chrome-devtools-axi open <url>` to drive the object in a browser.",
                    ["deploymentNote"] = deploymentNote
                };

                // 4) Optional GAM login. Accepted shapes:
                //    "auto" → use env GXMCP_GAM_USER/PASS
                //    {user, pass, repository?, loginUrl?} → explicit creds
                if (gamSession != null && gamSession.Type != JTokenType.Null)
                {
                    var auth = NormaliseGamArg(gamSession);
                    if (auth != null && !string.IsNullOrEmpty(auth.User) && !string.IsNullOrEmpty(auth.Pass))
                    {
                        string loginUrl = !string.IsNullOrEmpty(auth.LoginUrl)
                            ? auth.LoginUrl
                            : baseUrl + "/glogin.aspx";
                        var (cookieHeader, signedIn, error) = LoginHook != null
                            ? LoginHook(loginUrl, auth.User, auth.Pass)
                            : DoGamLogin(loginUrl, auth.User, auth.Pass);
                        resultPayload["signedIn"] = signedIn;
                        if (!string.IsNullOrEmpty(cookieHeader))
                            resultPayload["cookies"] = ParseCookies(cookieHeader);
                        if (!string.IsNullOrEmpty(error))
                            resultPayload["loginError"] = error;
                        if (signedIn)
                            resultPayload["hint"] = "Cookies captured — pass them via `chrome-devtools-axi` or curl with the cookie header to skip the login screen.";
                    }
                    else
                    {
                        resultPayload["loginError"] = "gamSession requested but no credentials resolved (pass {user, pass} or set GXMCP_GAM_USER / GXMCP_GAM_PASS).";
                    }
                }

                return McpResponse.Ok(target: name, code: "ObjectResolved", result: resultPayload);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "RunObjectFailed",
                    message: ex.Message,
                    nextSteps: new JArray { McpResponse.NextStep("genexus_run_object", new JObject { ["name"] = name }, "Retry after checking the object exists and the KB is open.") });
            }
        }

        internal static string ExplicitBaseUrl(JObject cfg)
        {
            string v = cfg?["baseUrl"]?.ToString()?.Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(v)) return null;
            return string.Equals(v, LegacyAutoBaseUrl, StringComparison.OrdinalIgnoreCase) ? null : v;
        }

        // The generator's Web Root is normally a full URL (http://host/vdir/);
        // a bare virtual directory name is served from localhost.
        internal static string BaseUrlFromWebRoot(string webRoot)
        {
            string v = webRoot.Trim().TrimEnd('/', '\\');
            if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return v;
            return "http://localhost/" + v.TrimStart('/', '\\');
        }

        // Resolve parm names from the object's Parm rule. Returns null on any
        // failure — callers fall back to positional p1..pN keys.
        private List<string> TryResolveParmNames(string name)
        {
            if (_objectService == null) return null;
            try
            {
                var obj = _objectService.FindObject(name);
                if (obj == null) return null;
                var (_, parms) = _objectService.GetParametersInternal(obj);
                if (parms == null) return null;
                var list = new List<string>();
                foreach (var p in parms)
                {
                    string n = (p.Name ?? string.Empty).TrimStart('&');
                    if (string.IsNullOrEmpty(n)) continue;
                    list.Add(n);
                }
                return list;
            }
            catch { return null; }
        }

        internal static string EncodeArgs(JArray args, List<string> parmNames)
        {
            if (args == null || args.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            for (int i = 0; i < args.Count; i++)
            {
                string key = (parmNames != null && i < parmNames.Count && !string.IsNullOrEmpty(parmNames[i]))
                    ? parmNames[i]
                    : "p" + (i + 1);
                string val = args[i]?.ToString() ?? string.Empty;
                if (sb.Length > 0) sb.Append('&');
                sb.Append(WebUtility.UrlEncode(key));
                sb.Append('=');
                sb.Append(WebUtility.UrlEncode(val));
            }
            return sb.ToString();
        }

        internal sealed class GamCreds
        {
            public string User;
            public string Pass;
            public string Repository;
            public string LoginUrl;
        }

        internal static GamCreds NormaliseGamArg(JToken token)
        {
            if (token == null) return null;
            if (token.Type == JTokenType.String)
            {
                string s = token.ToString();
                if (!string.Equals(s, "auto", StringComparison.OrdinalIgnoreCase)) return null;
                var creds = new GamCreds
                {
                    User = Environment.GetEnvironmentVariable("GXMCP_GAM_USER"),
                    Pass = Environment.GetEnvironmentVariable("GXMCP_GAM_PASS"),
                    LoginUrl = Environment.GetEnvironmentVariable("GXMCP_GAM_LOGIN_URL")
                };
                return creds;
            }
            if (token is JObject jo)
            {
                return new GamCreds
                {
                    User = jo["user"]?.ToString() ?? Environment.GetEnvironmentVariable("GXMCP_GAM_USER"),
                    Pass = jo["pass"]?.ToString() ?? Environment.GetEnvironmentVariable("GXMCP_GAM_PASS"),
                    Repository = jo["repository"]?.ToString(),
                    LoginUrl = jo["loginUrl"]?.ToString() ?? Environment.GetEnvironmentVariable("GXMCP_GAM_LOGIN_URL")
                };
            }
            return null;
        }

        // Best-effort HTTP-level GAM login. POSTs the standard glogin.aspx form
        // shape (UserName / UserPassword / GXSUBMIT) and captures Set-Cookie.
        // Returns (cookieHeader, signedIn, error). signedIn is heuristic: the
        // server returned a session cookie AND the response was not the login
        // page itself.
        private static (string cookieHeader, bool signedIn, string error) DoGamLogin(string loginUrl, string user, string pass)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var cookies = new CookieContainer();
                var req = (HttpWebRequest)WebRequest.Create(loginUrl);
                req.Method = "POST";
                req.ContentType = "application/x-www-form-urlencoded";
                req.CookieContainer = cookies;
                req.AllowAutoRedirect = true;
                req.Timeout = 15000;
                req.UserAgent = "GenexusMCP/run_object";

                string body =
                    "UserName=" + WebUtility.UrlEncode(user ?? string.Empty) +
                    "&UserPassword=" + WebUtility.UrlEncode(pass ?? string.Empty) +
                    "&GXSUBMIT=Login";
                var data = Encoding.UTF8.GetBytes(body);
                req.ContentLength = data.Length;
                using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);

                string responseBody;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream() ?? new MemoryStream()))
                {
                    responseBody = reader.ReadToEnd();
                }

                var cookieList = new List<string>();
                foreach (Cookie c in cookies.GetCookies(new Uri(loginUrl)))
                {
                    cookieList.Add(c.Name + "=" + c.Value);
                }

                string header = string.Join("; ", cookieList);
                bool loginPageReturned = LooksLikeLoginPage(responseBody);
                bool hasSession = cookieList.Any(c =>
                    c.IndexOf("session", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    c.IndexOf("GAM", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    c.IndexOf("ASP.NET", StringComparison.OrdinalIgnoreCase) >= 0);
                bool signedIn = hasSession && !loginPageReturned;
                return (header, signedIn, signedIn ? null : "Login response still looks like the login screen (check credentials).");
            }
            catch (Exception ex)
            {
                return (null, false, ex.Message);
            }
        }

        private static bool LooksLikeLoginPage(string body)
        {
            if (string.IsNullOrEmpty(body)) return false;
            return body.IndexOf("UserPassword", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   body.IndexOf("glogin.aspx", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static JObject ParseCookies(string header)
        {
            var obj = new JObject();
            if (string.IsNullOrEmpty(header)) return obj;
            foreach (var part in header.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string k = part.Substring(0, eq).Trim();
                string v = part.Substring(eq + 1).Trim();
                if (k.Length > 0) obj[k] = v;
            }
            return obj;
        }
    }
}
