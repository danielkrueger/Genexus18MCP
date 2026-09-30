using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Comprehensive scanner of all loaded GeneXus SDK assemblies. Built for F17 —
    /// hunting the internal "generators" that project a PatternInstance onto a
    /// bound KBObject's WebForm/Layout. Persists three artifacts:
    ///
    ///   docs/sdk-probe/raw.json       — full structured dump (every type + member)
    ///   docs/sdk-probe/INDEX.md       — human-navigable category map
    ///   docs/sdk-probe/generators.md  — focused list of generator/builder/projector
    ///                                   candidates with signatures
    ///
    /// Run via the diagnostic probe endpoint in PatternApplyService; persistence is
    /// relative to the repo root resolved via GX_MCP_REPO_ROOT env var, or falls
    /// back to %TEMP%/gxmcp_sdk_probe/.
    /// </summary>
    internal static class SdkSurfaceProbe
    {
        // Assemblies we care about — GeneXus SDK + WWP package + Artech infra.
        // We scan everything else only to capture cross-references; we don't dump
        // System.* / Microsoft.* / etc.
        private static readonly string[] AssemblyPrefixes = new[]
        {
            "Artech.",
            "Genexus.",
            "DVelop.",
            "GeneXus.",
        };

        // Type-name patterns that indicate the type might participate in code
        // generation, pattern application, or PatternInstance → object projection.
        // Used to filter the focused "generators.md" output.
        private static readonly string[] GeneratorKeywords = new[]
        {
            "Generator", "Builder", "Apply", "Refresh", "Update", "Project",
            "Generate", "Save", "Engine", "Helper", "Service", "Resolver",
            "Process", "Execute", "Render", "Compose", "Materialize",
            "Wire", "Bind", "Attach"
        };

        public sealed class ProbeResult
        {
            public string RawJsonPath { get; set; }
            public string IndexMdPath { get; set; }
            public string GeneratorsMdPath { get; set; }
            public long RawSizeBytes { get; set; }
            public int AssembliesScanned { get; set; }
            public int TypesScanned { get; set; }
            public int GeneratorCandidates { get; set; }
            public List<string> Warnings { get; } = new List<string>();
        }

        public static ProbeResult Run(string outputDirOverride = null)
        {
            var result = new ProbeResult();
            // D20: an omitted arg arrives as null, but an empty/whitespace outputDir
            // string ("") slips past a plain `?? default` and makes
            // Directory.CreateDirectory throw "path cannot be empty" — which then
            // surfaces as a spurious SdkProbeError in whoami.lastError. Treat
            // empty/whitespace exactly like omitted.
            string outDir = string.IsNullOrWhiteSpace(outputDirOverride)
                ? ResolveDefaultOutputDir()
                : outputDirOverride;
            Directory.CreateDirectory(outDir);

            // Pre-load unreferenced GeneXus/WWP SDK assemblies from disk so that tools
            // and generators not yet loaded into the AppDomain are also discovered.
            TryPreloadSdkAssemblies(result);

            var raw = new JObject();
            var asmArr = new JArray();
            int totalTypes = 0;
            var generatorCandidates = new List<(string asm, string type, string members)>();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string asmName = asm.GetName().Name ?? "";
                if (!AssemblyPrefixes.Any(p => asmName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle)
                {
                    types = rtle.Types.Where(t => t != null).ToArray();
                    result.Warnings.Add(asmName + ": partial type load — " + rtle.LoaderExceptions.Length + " loader errors");
                }
                catch (Exception ex)
                {
                    result.Warnings.Add(asmName + ": GetTypes failed (" + ex.Message + ")");
                    continue;
                }

                var typesArr = new JArray();
                foreach (var t in types)
                {
                    if (t == null) continue;
                    if (!t.IsPublic && !t.IsNestedPublic) continue;

                    totalTypes++;
                    var typeEntry = DumpType(t);
                    typesArr.Add(typeEntry);

                    // Identify generator-shaped types for the focused report.
                    if (GeneratorKeywords.Any(k => t.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        var members = string.Join("; ",
                            ((JArray)typeEntry["methods"]).Select(m => m.ToString()).Take(8));
                        generatorCandidates.Add((asmName, t.FullName, members));
                    }
                }

                asmArr.Add(new JObject
                {
                    ["name"] = asmName,
                    ["version"] = asm.GetName().Version?.ToString() ?? "",
                    ["location"] = SafeLocation(asm),
                    ["typeCount"] = types.Length,
                    ["publicTypeCount"] = ((JArray)typesArr).Count,
                    ["types"] = typesArr
                });
                result.AssembliesScanned++;
            }

            raw["scannedAt"] = DateTime.UtcNow.ToString("o");
            raw["assemblies"] = asmArr;

            // 1. raw.json
            string rawPath = Path.Combine(outDir, "raw.json");
            File.WriteAllText(rawPath, raw.ToString(Newtonsoft.Json.Formatting.Indented));
            result.RawJsonPath = rawPath;
            result.RawSizeBytes = new FileInfo(rawPath).Length;
            result.TypesScanned = totalTypes;

            // 2. INDEX.md — categorized navigation
            string indexPath = Path.Combine(outDir, "INDEX.md");
            File.WriteAllText(indexPath, BuildIndexMarkdown(asmArr, result.Warnings, generatorCandidates.Count));
            result.IndexMdPath = indexPath;

            // 3. generators.md — focused candidates
            string generatorsPath = Path.Combine(outDir, "generators.md");
            File.WriteAllText(generatorsPath, BuildGeneratorsMarkdown(generatorCandidates));
            result.GeneratorsMdPath = generatorsPath;
            result.GeneratorCandidates = generatorCandidates.Count;

            return result;
        }

        private static JObject DumpType(Type t)
        {
            var entry = new JObject
            {
                ["fullName"] = t.FullName,
                ["assembly"] = t.Assembly.GetName().Name,
                ["isAbstract"] = t.IsAbstract,
                ["isInterface"] = t.IsInterface,
                ["isSealed"] = t.IsSealed,
                ["baseType"] = t.BaseType?.FullName,
                ["interfaces"] = new JArray(t.GetInterfaces().Select(i => i.FullName).ToArray())
            };

            const BindingFlags AllPublic = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            var methods = new JArray();
            try
            {
                foreach (var m in t.GetMethods(AllPublic))
                {
                    if (m.IsSpecialName) continue;
                    string scope = m.IsStatic ? "static " : "";
                    string sig = scope + m.Name + "(" +
                        string.Join(",", m.GetParameters().Select(p => SafeTypeName.Describe(p.ParameterType) + " " + p.Name)) +
                        ") -> " + SafeTypeName.Describe(m.ReturnType);
                    methods.Add(sig);
                }
            }
            catch { }
            entry["methods"] = methods;

            var props = new JArray();
            try
            {
                foreach (var p in t.GetProperties(AllPublic))
                {
                    props.Add(p.Name + ":" + SafeTypeName.Describe(p.PropertyType) + (p.CanWrite ? " {get;set;}" : " {get;}"));
                }
            }
            catch { }
            entry["properties"] = props;

            var ctors = new JArray();
            try
            {
                foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    string scope = c.IsPublic ? "public" : (c.IsAssembly ? "internal" : "private");
                    ctors.Add(scope + " ctor(" +
                        string.Join(",", c.GetParameters().Select(p => SafeTypeName.Describe(p.ParameterType) + " " + p.Name)) +
                        ")");
                }
            }
            catch { }
            entry["constructors"] = ctors;

            var fields = new JArray();
            try
            {
                foreach (var f in t.GetFields(AllPublic))
                {
                    fields.Add((f.IsStatic ? "static " : "") + f.Name + ":" + SafeTypeName.Describe(f.FieldType));
                }
            }
            catch { }
            entry["fields"] = fields;

            return entry;
        }

        // (SafeTypeName moved to Helpers/SafeTypeName.cs, shared with SdkProbeService)

        private static string SafeLocation(Assembly asm)
        {
            try { return asm.Location ?? ""; }
            catch { return ""; }
        }

        private static string ResolveDefaultOutputDir()
        {
            // Prefer GX_MCP_REPO_ROOT, then walk parent dirs of the worker exe to find
            // a 'docs' folder (heuristic for dev runs), else fall back to %TEMP%.
            string envRepo = Environment.GetEnvironmentVariable("GX_MCP_REPO_ROOT");
            if (!string.IsNullOrEmpty(envRepo) && Directory.Exists(envRepo))
                return Path.Combine(envRepo, "docs", "sdk-probe");

            // Source checkouts keep the probe under docs; installed npm packages
            // must never receive generated diagnostics in their replaceable tree.
            string installDirectory = GxMcp.Worker.Helpers.RuntimePaths.InstallDirectory;
            bool underNodeModules = installDirectory.Replace('/', '\\')
                .IndexOf("\\node_modules\\", StringComparison.OrdinalIgnoreCase) >= 0;
            try
            {
                var dir = underNodeModules ? null : new DirectoryInfo(installDirectory);
                for (int i = 0; i < 6 && dir != null; i++)
                {
                    var probe = Path.Combine(dir.FullName, "docs");
                    if (Directory.Exists(probe))
                        return Path.Combine(probe, "sdk-probe");
                    dir = dir.Parent;
                }
            }
            catch { }

            return Path.Combine(GxMcp.Worker.Helpers.RuntimePaths.TempRoot, "sdk-probe");
        }

        private static string BuildIndexMarkdown(JArray asmArr, List<string> warnings, int generatorCandidates)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# GeneXus SDK surface — probe index");
            sb.AppendLine();
            sb.AppendLine("> Generated by `SdkSurfaceProbe.Run()`. Persisted on every diagnostic apply_pattern.");
            sb.AppendLine("> See `raw.json` for the full structured dump and `generators.md` for the focused candidate list.");
            sb.AppendLine();
            sb.AppendLine("**Generated:** " + DateTime.UtcNow.ToString("o"));
            sb.AppendLine("**Generator candidates:** " + generatorCandidates);
            sb.AppendLine();

            if (warnings.Count > 0)
            {
                sb.AppendLine("## Warnings");
                sb.AppendLine();
                foreach (var w in warnings) sb.AppendLine("- " + w);
                sb.AppendLine();
            }

            sb.AppendLine("## Assemblies");
            sb.AppendLine();
            sb.AppendLine("| Assembly | Version | Types (public) | Location |");
            sb.AppendLine("|---|---|---:|---|");
            foreach (var a in asmArr)
            {
                sb.AppendLine("| `" + a["name"] + "` | " + a["version"] + " | " + a["publicTypeCount"] +
                    " / " + a["typeCount"] + " | `" + a["location"] + "` |");
            }

            // Per-assembly type lists (collapsed to namespaces for navigability).
            foreach (var a in asmArr)
            {
                string asmName = a["name"]?.ToString() ?? "";
                var nsGroups = ((JArray)a["types"])
                    .GroupBy(t => Namespace(t["fullName"]?.ToString()))
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToList();

                sb.AppendLine();
                sb.AppendLine("### `" + asmName + "`");
                sb.AppendLine();
                foreach (var ns in nsGroups)
                {
                    sb.AppendLine("**`" + ns.Key + "`**");
                    sb.AppendLine();
                    foreach (var t in ns.OrderBy(x => x["fullName"]?.ToString()))
                    {
                        string fn = t["fullName"]?.ToString() ?? "";
                        string shortName = fn.Substring((ns.Key + ".").Length);
                        int mc = ((JArray)t["methods"]).Count;
                        int pc = ((JArray)t["properties"]).Count;
                        sb.AppendLine("- `" + shortName + "` — " + mc + " methods, " + pc + " props");
                    }
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        private static string BuildGeneratorsMarkdown(List<(string asm, string type, string members)> candidates)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Generator / Builder / Applier candidates");
            sb.AppendLine();
            sb.AppendLine("> Types whose name contains generator-shaped keywords (Generator/Builder/Apply/Refresh/");
            sb.AppendLine("> Update/Project/Generate/Save/Engine/Helper/Service/Resolver/Process/Execute/Render/");
            sb.AppendLine("> Compose/Materialize/Wire/Bind/Attach). First 8 public methods shown per type — see");
            sb.AppendLine("> `raw.json` for the complete member list.");
            sb.AppendLine();
            sb.AppendLine("**Count:** " + candidates.Count);
            sb.AppendLine();

            foreach (var grp in candidates.GroupBy(c => c.asm).OrderBy(g => g.Key))
            {
                sb.AppendLine("## `" + grp.Key + "`");
                sb.AppendLine();
                foreach (var (_, type, members) in grp.OrderBy(c => c.type))
                {
                    sb.AppendLine("### `" + type + "`");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(members.Replace("; ", "\n"));
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        private static string Namespace(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return "(global)";
            int i = fullName.LastIndexOf('.');
            return i > 0 ? fullName.Substring(0, i) : "(global)";
        }

        private static void TryPreloadSdkAssemblies(ProbeResult result)
        {
            try
            {
                string gxPath = Environment.GetEnvironmentVariable("GX_PROGRAM_DIR")
                    ?? Environment.GetEnvironmentVariable("GX_PATH")
                    ?? @"C:\Program Files (x86)\GeneXus\GeneXus18";

                if (!Directory.Exists(gxPath))
                {
                    result.Warnings.Add("SDK preloading skipped: GeneXus path not found (" + gxPath + "). Probe reflects only already-loaded assemblies.");
                    return;
                }

                var dirsToScan = new List<string> { gxPath };
                string packagesDir = Path.Combine(gxPath, "Packages");
                if (Directory.Exists(packagesDir)) dirsToScan.Add(packagesDir);
                string patternsDir = Path.Combine(gxPath, "Patterns");
                if (Directory.Exists(patternsDir)) dirsToScan.Add(patternsDir);

                var loadedAssemblies = new HashSet<string>(
                    AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a =>
                        {
                            try { return a.GetName().Name; }
                            catch { return null; }
                        })
                        .Where(n => !string.IsNullOrEmpty(n)),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var dir in dirsToScan)
                {
                    string[] files;
                    try { files = Directory.GetFiles(dir, "*.dll"); }
                    catch { continue; }

                    foreach (var file in files)
                    {
                        string fileName = Path.GetFileNameWithoutExtension(file);
                        if (!AssemblyPrefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        if (loadedAssemblies.Contains(fileName))
                            continue;

                        try
                        {
                            var loaded = Assembly.LoadFrom(file);
                            string loadedName = loaded.GetName().Name;
                            if (!string.IsNullOrEmpty(loadedName)) loadedAssemblies.Add(loadedName);
                        }
                        catch (Exception ex)
                        {
                            result.Warnings.Add("Preload " + fileName + " failed: " + ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add("SDK preloading failed: " + ex.Message);
            }
        }
    }
}
