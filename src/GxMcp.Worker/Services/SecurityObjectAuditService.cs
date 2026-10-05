using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>What the rules look at for one object: its type, the properties they read, and its source.</summary>
    internal sealed class ObjectAuditFacts
    {
        internal string TypeName;
        internal IDictionary<string, string> Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal string Source;
    }

    /// <summary>
    /// genexus_security action=audit_object (#427): a fast in-memory check of one object against
    /// the IDE Security Scanner rules whose definition is known here. It complements scan_native;
    /// a rule absent from <see cref="CoveredRules"/> is not evaluated, and is never reported clean.
    /// </summary>
    internal static class SecurityObjectRules
    {
        internal static readonly string[] CoveredRules = { "100", "108" };

        // IDE rules whose Checks.xml definition is not available in a headless install; listed so
        // the caller knows they were not run.
        internal static readonly string[] NotCoveredRules = { "105", "139" };

        private static readonly Regex NativeBlock = new Regex(@"^\s*(csharp|java)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline);

        internal static List<JObject> Evaluate(ObjectAuditFacts facts)
        {
            var findings = new List<JObject>();
            if (facts == null) return findings;

            // #100: URL parameters are not encrypted. A Transaction or WebPanel always renders
            // URLs; a Procedure only when it is exposed over HTTP.
            if (Applies100(facts) && string.Equals(Prop(facts, "USE_ENCRYPTION"), "NO", StringComparison.OrdinalIgnoreCase))
                findings.Add(Finding("100", "UrlParametersNotEncrypted",
                    "Encrypt URL parameters is set to No on this " + facts.TypeName + ".",
                    "Set the object's Encrypt URL parameters property to Session or Site (or use the environment value)."));

            // #108: native code in the source.
            if (!string.IsNullOrEmpty(facts.Source))
            {
                string code = StripLineComments(facts.Source);
                Match native = NativeBlock.Match(code);
                if (native.Success)
                    findings.Add(Finding("108", "NativeCodeUsed",
                        "The source contains a native " + native.Groups[1].Value.ToLowerInvariant() + " block.",
                        "Replace the native block with GeneXus code or an External Object so the scanner can reason about it.",
                        LineOf(code, native.Index)));
            }
            return findings;
        }

        private static bool Applies100(ObjectAuditFacts facts)
        {
            if (facts.TypeName.Equals("Transaction", StringComparison.OrdinalIgnoreCase)
                || facts.TypeName.Equals("WebPanel", StringComparison.OrdinalIgnoreCase)) return true;
            return facts.TypeName.Equals("Procedure", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Prop(facts, "CALL_PROTOCOL"), "HTTP", StringComparison.OrdinalIgnoreCase);
        }

        private static string Prop(ObjectAuditFacts facts, string name)
            => facts.Properties != null && facts.Properties.TryGetValue(name, out string value) ? value : null;

        // A commented line is blanked, not removed, so line numbers stay true.
        private static string StripLineComments(string source)
            => string.Join("\n", source.Split('\n').Select(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : l));

        private static int LineOf(string text, int index) => text.Substring(0, index).Count(c => c == '\n') + 1;

        private static JObject Finding(string ideRule, string code, string message, string remediation, int? line = null)
        {
            var finding = new JObject
            {
                ["rule"] = "IDE#" + ideRule,
                ["code"] = code,
                ["severity"] = "warning",
                ["message"] = message,
                ["remediation"] = remediation
            };
            if (line.HasValue) finding["line"] = line.Value;
            return finding;
        }
    }

    public class SecurityObjectAuditService
    {
        private readonly ObjectService _objects;

        public SecurityObjectAuditService(ObjectService objects) { _objects = objects; }

        public string Run(JObject args)
        {
            string name = args?["name"]?.ToString() ?? args?["target"]?.ToString();
            string guid = args?["guid"]?.ToString();
            string entityKey = args?["entityKey"]?.ToString();
            string path = args?["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(guid) && string.IsNullOrWhiteSpace(entityKey) && string.IsNullOrWhiteSpace(path))
                return McpResponse.Err(code: "MissingTarget", message: "audit_object needs name (or guid/entityKey/path).");

            KBObject obj = _objects.FindObject(name, args?["type"]?.ToString(), guid, entityKey, path);
            if (obj == null)
                return McpResponse.Err(code: "ObjectNotFound", message: "Object not found.", target: name ?? guid ?? path,
                    nextSteps: new JArray(McpResponse.NextStep("genexus_query", new JObject { ["query"] = name ?? path ?? guid }, "Find the object by name.")));

            var facts = new ObjectAuditFacts { TypeName = obj.TypeDescriptor?.Name ?? string.Empty };
            foreach (string property in new[] { "USE_ENCRYPTION", "CALL_PROTOCOL" })
            {
                try { facts.Properties[property] = obj.GetPropertyValue(property)?.ToString(); } catch { }
            }
            facts.Source = ReadSources(obj);

            List<JObject> findings = SecurityObjectRules.Evaluate(facts);
            return McpResponse.Ok(code: "SecurityObjectAudit", target: obj.Name, result: new JObject
            {
                ["object"] = new JObject { ["name"] = obj.Name, ["type"] = facts.TypeName, ["guid"] = obj.Guid.ToString() },
                ["findings"] = new JArray(findings),
                ["findingCount"] = findings.Count,
                ["rulesEvaluated"] = new JArray(SecurityObjectRules.CoveredRules.Select(r => "IDE#" + r)),
                ["rulesNotEvaluated"] = new JArray(SecurityObjectRules.NotCoveredRules.Select(r => "IDE#" + r)),
                ["scope"] = "in-memory single-object audit; use action=scan_native for the authoritative IDE scan"
            });
        }

        private static string ReadSources(KBObject obj)
        {
            var text = new System.Text.StringBuilder();
            foreach (KBObjectPart part in obj.Parts)
            {
                try
                {
                    if (part.GetType().GetProperty("Source")?.GetValue(part, null) is string source && source.Length > 0)
                        text.AppendLine(source);
                }
                catch { }
            }
            return text.ToString();
        }
    }
}
