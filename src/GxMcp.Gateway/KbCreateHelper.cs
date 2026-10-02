using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Gateway-side helper to create brand new GeneXus Knowledge Bases using native MSBuild tasks.
    /// Operates outside of an active worker because a worker requires an existing KB.
    /// </summary>
    public static class KbCreateHelper
    {
        public sealed class CreateOptions
        {
            public string Path { get; set; } = string.Empty;
            public string? Name { get; set; }
            public string? Alias { get; set; }
            public string? DbServer { get; set; }
            public string? DbName { get; set; }
            public string? DbUser { get; set; }
            public string? DbPassword { get; set; }
            public string? Template { get; set; }
            public string? SdkPath { get; set; }
            public string? Major { get; set; }
            public bool OpenAfterCreate { get; set; } = true;
            public bool Persist { get; set; } = false;
            public bool DryRun { get; set; } = false;
        }

        public static async Task<JObject> CreateKbAsync(
            CreateOptions options,
            Configuration? activeConfig,
            string sessionId,
            bool sessionContextEnabled,
            IWorkerSupervisor? workerPool,
            Action<string, string, string>? setSessionSelectedKb,
            Action<string>? triggerIndexBootstrap)
        {
            if (options == null)
            {
                return Error("InvalidArguments", "Create options cannot be null.");
            }

            if (string.IsNullOrWhiteSpace(options.Path))
            {
                return Error("PathRequired", "The 'path' parameter is required for creating a Knowledge Base.",
                    hint: "Specify an absolute directory path where the Knowledge Base will be created.");
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(options.Path.Trim());
            }
            catch (Exception ex)
            {
                return Error("InvalidPath", $"Invalid path '{options.Path}': {ex.Message}");
            }

            // Guard against overwriting an existing KB
            if (Configuration.IsPlausibleKbPath(fullPath))
            {
                return Error("KbAlreadyExists", $"A GeneXus Knowledge Base already exists at '{fullPath}'.",
                    hint: "Choose a new directory, or use genexus_kb action=open to open the existing Knowledge Base.");
            }

            if (Directory.Exists(fullPath) && Directory.EnumerateFileSystemEntries(fullPath).Any())
            {
                return Error("DirectoryNotEmpty",
                    $"Target directory '{fullPath}' already exists and is not empty.",
                    hint: "Specify an empty directory or a non-existent path to create a new Knowledge Base.");
            }

            // Derive name and alias
            string name = string.IsNullOrWhiteSpace(options.Name)
                ? Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : options.Name.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                return Error("InvalidName", "Could not determine a valid Knowledge Base name from the path.");
            }

            string alias = string.IsNullOrWhiteSpace(options.Alias)
                ? name.ToLowerInvariant()
                : options.Alias.Trim().ToLowerInvariant();

            // Resolve SDK path
            string? sdkPath = ResolveSdkPath(options.SdkPath, options.Major, activeConfig);
            if (options.DryRun)
            {
                string? dryRunTemplatePath = string.IsNullOrWhiteSpace(sdkPath)
                    ? null
                    : ResolveTemplatePath(sdkPath, options.Template);
                string? dryRunMsBuildPath = LocateNetFrameworkMsBuild();
                return new JObject
                {
                    ["status"] = "Plan",
                    ["dryRun"] = true,
                    ["alias"] = alias,
                    ["name"] = name,
                    ["dbServer"] = string.IsNullOrWhiteSpace(options.DbServer)
                        ? @"(LocalDB)\MSSQLLocalDB"
                        : options.DbServer.Trim(),
                    ["dbName"] = string.IsNullOrWhiteSpace(options.DbName)
                        ? $"gx_kb_{name}"
                        : options.DbName.Trim(),
                    ["template"] = dryRunTemplatePath,
                    ["sdkPath"] = sdkPath,
                    ["plan"] = new JObject
                    {
                        ["path"] = fullPath,
                        ["name"] = name,
                        ["alias"] = alias,
                        ["dbServer"] = string.IsNullOrWhiteSpace(options.DbServer)
                            ? @"(LocalDB)\MSSQLLocalDB"
                            : options.DbServer.Trim(),
                        ["dbName"] = string.IsNullOrWhiteSpace(options.DbName)
                            ? $"gx_kb_{name}"
                            : options.DbName.Trim(),
                        ["dbUser"] = options.DbUser,
                        ["template"] = dryRunTemplatePath,
                        ["sdkPath"] = sdkPath,
                        ["msbuildPath"] = dryRunMsBuildPath,
                        ["openAfterCreate"] = options.OpenAfterCreate,
                        ["persist"] = options.Persist
                    }
                };
            }

            if (string.IsNullOrWhiteSpace(sdkPath) || !Directory.Exists(sdkPath))
            {
                return Error("SdkNotFound",
                    $"Could not locate a valid GeneXus SDK installation. Tried: '{sdkPath}'.",
                    hint: "Specify 'sdkPath' (e.g. 'C:\\Program Files (x86)\\GeneXus\\GeneXus18') or 'major' ('16', '17', '18'), or configure GeneXus.InstallationPath in config.json.");
            }

            string tasksDll = Path.Combine(sdkPath, "Genexus.MsBuild.Tasks.dll");
            string targetsFile = Path.Combine(sdkPath, "Genexus.Tasks.targets");
            if (!File.Exists(tasksDll) || !File.Exists(targetsFile))
            {
                return Error("SdkIncomplete",
                    $"GeneXus SDK at '{sdkPath}' is missing required MSBuild assets ({Path.GetFileName(tasksDll)} or {Path.GetFileName(targetsFile)}).");
            }

            // Resolve template
            string? templatePath = ResolveTemplatePath(sdkPath, options.Template);
            if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
            {
                return Error("TemplateNotFound",
                    $"GeneXus KB template was not found: '{options.Template ?? "default csharp/netcore.kbtemplate"}'.",
                    hint: $"Verify that '{templatePath}' exists in the GeneXus Templates directory.");
            }

            // Resolve DB parameters
            string dbServer = string.IsNullOrWhiteSpace(options.DbServer)
                ? @"(LocalDB)\MSSQLLocalDB"
                : options.DbServer.Trim();

            string dbName = string.IsNullOrWhiteSpace(options.DbName)
                ? $"gx_kb_{name}"
                : options.DbName.Trim();

            // Resolve MSBuild.exe (GeneXus tasks are 32-bit x86 net4x)
            string? msbuildPath = LocateNetFrameworkMsBuild();
            if (string.IsNullOrWhiteSpace(msbuildPath) || !File.Exists(msbuildPath))
            {
                return Error("MsBuildNotFound",
                    "Could not locate 32-bit .NET Framework MSBuild.exe (v4.0.30319) required by GeneXus SDK tasks.",
                    hint: "Verify that .NET Framework 4.8 is installed on this Windows machine.");
            }

            // Preflight: If using LocalDB, ensure instance is started
            if (dbServer.IndexOf("LocalDB", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                TryStartLocalDbInstance("MSSQLLocalDB");
            }

            // Create target directory if needed
            try
            {
                if (!Directory.Exists(fullPath))
                {
                    Directory.CreateDirectory(fullPath);
                }
            }
            catch (Exception ex)
            {
                return Error("DirectoryCreationFailed", $"Failed to create target directory '{fullPath}': {ex.Message}");
            }

            // Generate temporary MSBuild project file
            string tempProj = Path.Combine(Path.GetTempPath(), $"gxmcp_create_kb_{Guid.NewGuid():N}.proj");
            // ...and the response file that carries the database password. The password is a
            // credential, not a parameter: a `/p:` value on a Windows command line is readable by
            // any process that can query the running process, and MSBuild echoes its own
            // invocation back in its error output, which used to reach the caller verbatim in
            // ["output"]. The value therefore travels in a response file that MSBuild parses as if
            // the arguments had been typed, and the command line carries only the property NAME.
            // MSBuild reads a response file as UTF-8 only when it is told, via a byte order mark -
            // without one it decodes as ANSI and mangles a non-ASCII password.
            string tempRsp = Path.Combine(Path.GetTempPath(), $"gxmcp_create_kb_{Guid.NewGuid():N}.rsp");
            try
            {
                string projXml = $@"<Project DefaultTargets=""Create"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <Import Project=""{targetsFile}"" />
  <UsingTask AssemblyFile=""{tasksDll}"" TaskName=""Genexus.MsBuild.Tasks.CreateKnowledgeBase"" />
  <Target Name=""Create"">
    <CreateKnowledgeBase
        Directory=""$(KBDirectory)""
        Template=""$(KBTemplate)""
        ServerInstance=""$(KBDbServer)""
        DBName=""$(KBDbName)""
        UserId=""$(KBDbUser)""
        Password=""$(KBDbPassword)""
        IntegratedSecurity=""$(KBIntegratedSecurity)"" />
  </Target>
</Project>";
                File.WriteAllText(tempProj, projXml);

                bool integrated = string.IsNullOrWhiteSpace(options.DbUser);

                string? passwordResponseFile = null;
                if (!integrated)
                {
                    // Written only for a non-integrated login, and deleted in the finally below
                    // even when the spawn fails, so a failed create cannot leave a file with this
                    // shape on disk.
                    passwordResponseFile = tempRsp;
                    File.WriteAllText(
                        passwordResponseFile,
                        "/p:KBDbPassword=" + QuoteResponseValue(options.DbPassword) + Environment.NewLine,
                        ResponseFileEncoding);
                }

                // NOTE: this string must never reach a log, an error envelope or Program.Log.
                // It is the one value on the command line that would carry a credential, so the
                // command line carries only the property name; see BuildMsBuildArguments.
                string arguments = BuildMsBuildArguments(
                    tempProj, fullPath, templatePath, dbServer, dbName, integrated, options.DbUser, passwordResponseFile);

                var psi = new ProcessStartInfo
                {
                    FileName = msbuildPath,
                    Arguments = arguments,
                    WorkingDirectory = fullPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                psi.EnvironmentVariables["GX_PATH"] = sdkPath;
                psi.EnvironmentVariables["GX_PROGRAM_DIR"] = sdkPath;

                var sw = Stopwatch.StartNew();
                using var proc = new Process { StartInfo = psi };
                proc.Start();

                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                bool finished = proc.WaitForExit(300000); // 5 minutes ceiling
                sw.Stop();

                if (!finished)
                {
                    try { proc.Kill(); } catch { }
                    return Error("KbCreationTimeout",
                        $"GeneXus KB creation timed out after 300 seconds at '{fullPath}'.",
                        hint: "Check if SQL Server / LocalDB is responsive.");
                }

                string stdout = await stdoutTask;
                string stderr = await stderrTask;
                string allOutput = (stdout + "\n" + stderr).Trim();

                if (proc.ExitCode != 0 || !Configuration.IsPlausibleKbPath(fullPath))
                {
                    return BuildCreationFailureEnvelope(fullPath, proc.ExitCode, sw.ElapsedMilliseconds, allOutput);
                }

                // Optional persistence to config.json
                bool persisted = false;
                if (options.Persist && !string.IsNullOrWhiteSpace(Configuration.CurrentConfigPath))
                {
                    try
                    {
                        persisted = TryPersistKbEntry(Configuration.CurrentConfigPath!, alias, fullPath, activeConfig);
                    }
                    catch (Exception ex)
                    {
                        Program.Log($"[KbCreateHelper] Failed to persist KB entry: {ex.Message}");
                    }
                }

                // Optional open & session selection
                int? workerPid = null;
                bool opened = false;
                bool selected = false;
                if (options.OpenAfterCreate && workerPool != null)
                {
                    try
                    {
                        var handle = new KbHandle(alias, fullPath);
                        workerPool.RegisterKnown(handle);
                        var worker = await workerPool.AcquireAsync(handle, CancellationToken.None);
                        workerPid = worker?.Pid;
                        opened = true;

                        if (sessionContextEnabled && setSessionSelectedKb != null)
                        {
                            setSessionSelectedKb(sessionId, alias, fullPath);
                            selected = true;
                        }

                        triggerIndexBootstrap?.Invoke(alias);
                    }
                    catch (Exception ex)
                    {
                        Program.Log($"[KbCreateHelper] Created KB succeeded, but auto-open failed: {ex.Message}");
                    }
                }

                return new JObject
                {
                    ["status"] = "Success",
                    ["created"] = true,
                    ["path"] = fullPath,
                    ["name"] = name,
                    ["alias"] = alias,
                    ["dbServer"] = dbServer,
                    ["dbName"] = dbName,
                    ["template"] = templatePath,
                    ["sdkPath"] = sdkPath,
                    ["durationMs"] = sw.ElapsedMilliseconds,
                    ["opened"] = opened,
                    ["selected"] = selected,
                    ["workerPid"] = workerPid,
                    ["persisted"] = persisted
                };
            }
            finally
            {
                // Both temp files go, including the response file when the spawn itself threw:
                // it holds the database password in the clear and must not outlive the call.
                try
                {
                    if (File.Exists(tempProj)) File.Delete(tempProj);
                }
                catch { }
                try
                {
                    if (File.Exists(tempRsp)) File.Delete(tempRsp);
                }
                catch { }
            }
        }

        /// <summary>
        /// The MSBuild command line for a KB create.
        ///
        /// The database password is deliberately absent: it travels in
        /// <paramref name="passwordResponseFile"/> instead, because a <c>/p:</c> value on a
        /// Windows command line is readable by any process that can query the running
        /// process, and MSBuild echoes its own invocation back in its error output. The
        /// <c>@</c> reference is quoted so a response file under a temp path containing
        /// spaces survives the child's command-line split - MSBuild itself takes the whole
        /// token as the path.
        /// </summary>
        internal static string BuildMsBuildArguments(
            string tempProj,
            string fullPath,
            string templatePath,
            string dbServer,
            string dbName,
            bool integrated,
            string? dbUser,
            string? passwordResponseFile)
        {
            var builder = new StringBuilder();
            builder.Append("/nologo /v:minimal \"").Append(tempProj).Append('"');
            builder.Append(" /p:KBDirectory=").Append(fullPath);
            builder.Append(" /p:KBTemplate=").Append(templatePath);
            builder.Append(" /p:KBDbServer=").Append(dbServer);
            builder.Append(" /p:KBDbName=").Append(dbName);
            builder.Append(" /p:KBIntegratedSecurity=").Append(integrated ? "True" : "False");

            // The user is not a secret, so it stays inline where an operator reading a failing
            // invocation can see which login was attempted.
            if (!integrated)
            {
                builder.Append(" /p:KBDbUser=").Append(dbUser);
            }

            if (!integrated && !string.IsNullOrEmpty(passwordResponseFile))
            {
                builder.Append(" \"@").Append(passwordResponseFile).Append('"');
            }

            return builder.ToString();
        }

        /// <summary>
        /// MSBuild parses a response-file line with the same rules the C runtime applies to a
        /// command line, so a value is quoted and its embedded quotes are escaped. Always quoted,
        /// because an unquoted value is split on the semicolons MSBuild treats as property
        /// separators. Measured against .NET Framework MSBuild 4.8: this round-trips values
        /// containing spaces, semicolons, quotes, trailing backslashes and non-ASCII text, which
        /// the previous inline <c>/p:</c> form did not.
        /// </summary>
        internal static string QuoteResponseValue(string? value)
        {
            var sb = new StringBuilder();
            sb.Append('"');
            for (int i = 0; value != null && i < value.Length; i++)
            {
                int backslashes = 0;
                while (i < value.Length && value[i] == '\\') { backslashes++; i++; }

                if (i == value.Length)
                {
                    // Trailing backslashes would otherwise consume the closing quote.
                    sb.Append('\\', backslashes * 2);
                    break;
                }

                if (value[i] == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                }
                else
                {
                    sb.Append('\\', backslashes);
                }
                sb.Append(value[i]);
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// The failure envelope for a KB create, with the child's combined output redacted and
        /// bounded.
        ///
        /// MSBuild's own error output echoes the invocation back, so the raw text is treated as
        /// untrusted: it is run through <see cref="LogRedaction"/> and then capped, because a KB
        /// create that dumps a multi-megabyte log is a nuisance and an absolute SDK path is not
        /// something the caller needs echoed verbatim.
        /// </summary>
        internal static JObject BuildCreationFailureEnvelope(string fullPath, int exitCode, long durationMs, string? childOutput)
        {
            return new JObject
            {
                ["status"] = "Error",
                ["code"] = "KbCreationFailed",
                ["message"] = $"GeneXus KB creation exited with code {exitCode}.",
                ["path"] = fullPath,
                ["durationMs"] = durationMs,
                ["output"] = BoundFailureOutput(childOutput),
                ["hint"] = "Check database permissions, LocalDB instance state, and ensure no conflicting database exists."
            };
        }

        /// <summary>
        /// Cap on the child's combined stdout+stderr before it reaches the response, measured the
        /// same way <see cref="ResponseSizeGuard"/> measures a payload so the two caps are
        /// comparable. 16 KB keeps a useful tail of a real MSBuild failure and still fits the
        /// response budget.
        /// </summary>
        internal const int MaxFailureOutputBytes = 16 * 1024;

        /// <summary>
        /// Runs the child's output through credential redaction and then caps it. Never returns
        /// null: the response contract is that <c>output</c> is a string.
        /// </summary>
        internal static string BoundFailureOutput(string? childOutput, int maxBytes = MaxFailureOutputBytes)
        {
            string redacted = MaskDbPasswordProperty(LogRedaction.Redact(childOutput ?? string.Empty));

            if (ResponseSizeGuard.ByteSize(redacted) <= maxBytes) return redacted;

            // Truncate on a rune boundary rather than a byte offset: a cut mid-sequence would
            // put invalid UTF-8 in the response, which is a worse outcome than losing a few bytes.
            int keep = maxBytes - TruncationMarkerBytes;
            var sb = new StringBuilder();
            int used = 0;
            foreach (Rune rune in redacted.EnumerateRunes())
            {
                if (used + rune.Utf8SequenceLength > keep) break;
                sb.Append(rune);
                used += rune.Utf8SequenceLength;
            }
            sb.Append(TruncationMarker);
            return sb.ToString();
        }

        /// <summary>
        /// <c>KBDbPassword</c> is the one key shape on this command line that
        /// <see cref="LogRedaction"/> does NOT catch, and the gap is worth recording: its pattern
        /// requires a word boundary before <c>password</c>, and in <c>KBDbPassword</c> the
        /// preceding character is a letter, so <c>/p:KBDbPassword=secret</c> passes through
        /// unredacted. Measured, not assumed. Since the project template keeps that exact
        /// property name, masking it here is what actually closes the echo path - the generic
        /// call alone leaves the credential in the response.
        /// </summary>
        private static readonly Regex KbDbPasswordPropertyShape =
            new(@"(?i)(KBDbPassword\s*=\s*)(?:"".*?""|'.*?'|[^\s""']+)", RegexOptions.Compiled);

        /// <summary>
        /// Replaces the value of a <c>KBDbPassword=</c> assignment, keeping the property name so
        /// the failure stays diagnosable.
        /// </summary>
        internal static string MaskDbPasswordProperty(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return KbDbPasswordPropertyShape.Replace(
                text,
                m => m.Groups[1].Value + "<redacted>");
        }

        /// <summary>
        /// Appended when the child's output was capped, so the caller can tell a short failure
        /// from a cut one instead of reading a silent gap as the whole log.
        /// </summary>
        internal const string TruncationMarker = "\n... [output truncated]";

        private static readonly int TruncationMarkerBytes = (int)ResponseSizeGuard.ByteSize(TruncationMarker);

        /// <summary>
        /// MSBuild response files are read as UTF-8 only when the byte order mark says so;
        /// without it MSBuild decodes them as ANSI and mangles a non-ASCII password.
        /// </summary>
        private static readonly Encoding ResponseFileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

        private static string? ResolveSdkPath(string? explicitPath, string? explicitMajor, Configuration? activeConfig)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath) && Directory.Exists(explicitPath.Trim()))
            {
                return Path.GetFullPath(explicitPath.Trim());
            }

            if (!string.IsNullOrWhiteSpace(explicitMajor))
            {
                var diag = GeneXusVersionCatalog.ToDiagnosticObject();
                if (diag["entries"] is JArray entries)
                {
                    foreach (var token in entries)
                    {
                        if (token is JObject entryObj &&
                            string.Equals(entryObj["major"]?.ToString(), explicitMajor.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            string? defaultPath = entryObj["defaultInstallPath"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(defaultPath) && Directory.Exists(defaultPath))
                            {
                                return Path.GetFullPath(defaultPath);
                            }
                        }
                    }
                }
            }

            string? configPath = activeConfig?.GeneXus?.InstallationPath;
            if (!string.IsNullOrWhiteSpace(configPath) && Directory.Exists(configPath))
            {
                return Path.GetFullPath(configPath);
            }

            string primaryPath = GeneXusVersionCatalog.PrimaryInstallPath;
            if (Directory.Exists(primaryPath))
            {
                return Path.GetFullPath(primaryPath);
            }

            return null;
        }

        private static string? ResolveTemplatePath(string sdkPath, string? template)
        {
            string templatesDir = Path.Combine(sdkPath, "Templates");
            if (string.IsNullOrWhiteSpace(template))
            {
                string defaultNetCore = Path.Combine(templatesDir, "netcore.kbtemplate");
                if (File.Exists(defaultNetCore)) return defaultNetCore;
                string defaultCSharp = Path.Combine(templatesDir, "csharp.kbtemplate");
                if (File.Exists(defaultCSharp)) return defaultCSharp;
                string defaultNet = Path.Combine(templatesDir, "net.kbtemplate");
                if (File.Exists(defaultNet)) return defaultNet;

                if (Directory.Exists(templatesDir))
                {
                    var any = Directory.GetFiles(templatesDir, "*.kbtemplate").FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(any)) return any;
                }

                return defaultNetCore;
            }

            string trimmed = template.Trim();
            if (Path.IsPathRooted(trimmed) && File.Exists(trimmed))
            {
                return Path.GetFullPath(trimmed);
            }

            string direct = Path.Combine(templatesDir, trimmed);
            if (File.Exists(direct)) return direct;

            if (!trimmed.EndsWith(".kbtemplate", StringComparison.OrdinalIgnoreCase))
            {
                string withExt = Path.Combine(templatesDir, trimmed + ".kbtemplate");
                if (File.Exists(withExt)) return withExt;
            }

            return Path.Combine(templatesDir, trimmed);
        }

        private static string? LocateNetFrameworkMsBuild()
        {
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string frameworkPath = Path.Combine(winDir, @"Microsoft.NET\Framework\v4.0.30319\MSBuild.exe");
            if (File.Exists(frameworkPath)) return frameworkPath;

            string hardcoded = @"C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe";
            if (File.Exists(hardcoded)) return hardcoded;

            return null;
        }

        private static void TryStartLocalDbInstance(string instanceName)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sqllocaldb",
                    Arguments = $"start {instanceName}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(5000);
            }
            catch
            {
                // Non-critical preflight: sqllocaldb might not be on PATH
            }
        }

        private static bool TryPersistKbEntry(string configPath, string alias, string path, Configuration? activeConfig)
        {
            if (!File.Exists(configPath)) return false;

            string content = File.ReadAllText(configPath);
            var root = JObject.Parse(content);
            if (root["Environment"] is not JObject envObj)
            {
                envObj = new JObject();
                root["Environment"] = envObj;
            }

            var kbsToken = envObj["KBs"];
            bool modified = false;
            if (kbsToken is JObject kbsObj)
            {
                kbsObj[alias] = path;
                modified = true;
            }
            else if (kbsToken is JArray kbsArr)
            {
                var existing = kbsArr.OfType<JObject>()
                    .FirstOrDefault(x => string.Equals(x["alias"]?.ToString(), alias, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing["path"] = path;
                }
                else
                {
                    kbsArr.Add(new JObject { ["alias"] = alias, ["path"] = path });
                }
                modified = true;
            }
            else
            {
                envObj["KBs"] = new JObject { [alias] = path };
                modified = true;
            }

            if (modified)
            {
                AtomicJsonFileWriter.Write(configPath, root.ToString(Formatting.Indented));
                if (activeConfig?.Environment?.KBs != null)
                {
                    var declared = activeConfig.Environment.KBs.FirstOrDefault(
                        k => string.Equals(k.Alias, alias, StringComparison.OrdinalIgnoreCase));
                    if (declared != null) declared.Path = path;
                    else activeConfig.Environment.KBs.Add(new KbEntry { Alias = alias, Path = path });
                }
                return true;
            }

            return false;
        }

        private static JObject Error(string code, string message, string? hint = null)
        {
            var err = new JObject
            {
                ["status"] = "Error",
                ["code"] = code,
                ["message"] = message
            };
            if (!string.IsNullOrEmpty(hint))
            {
                err["hint"] = hint;
            }
            return err;
        }
    }
}
