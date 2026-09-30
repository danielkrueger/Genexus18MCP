using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Compares requested text with a forced post-save read without confusing
    /// GeneXus' harmless text rendering with a failed persistence operation.
    /// </summary>
    internal static class TextPersistenceVerifier
    {
        internal sealed class Result
        {
            public string Mode { get; set; }
            public bool Matches { get; set; }
            public string RequestedHash { get; set; }
            public string PersistedHash { get; set; }
            public string NormalizedRequestedHash { get; set; }
            public string NormalizedPersistedHash { get; set; }
            public JArray NormalizationApplied { get; set; }
            public string DiffNormalized { get; set; }
            public string NormalizedRequested { get; set; }
            public string NormalizedPersisted { get; set; }

            public JObject ToJson(bool reReadConfirmed)
            {
                return new JObject
                {
                    ["mode"] = Mode,
                    ["normalizationApplied"] = NormalizationApplied ?? new JArray(),
                    ["diffNormalized"] = DiffNormalized == null ? JValue.CreateNull() : (JToken)DiffNormalized,
                    ["reReadConfirmed"] = reReadConfirmed
                };
            }
        }

        internal static string ResolveMode(string requestedMode, string partName)
        {
            string mode = (requestedMode ?? string.Empty).Trim().ToLowerInvariant();
            if (mode.Length == 0)
            {
                return IsCodeOrTextPart(partName) ? "normalized" : "exact";
            }
            if (mode != "exact" && mode != "normalized" && mode != "semantic")
                throw new ArgumentException("verifyMode must be exact, normalized, or semantic.", nameof(requestedMode));
            return mode;
        }

        internal static Result Evaluate(string requested, string persisted, string requestedMode, string partName)
        {
            requested = requested ?? string.Empty;
            persisted = persisted ?? string.Empty;
            string mode = ResolveMode(requestedMode, partName);
            string nr = Normalize(requested);
            string np = Normalize(persisted);
            bool matches;
            if (mode == "exact")
                matches = string.Equals(
                    ExactCanonicalize(requested, partName),
                    ExactCanonicalize(persisted, partName),
                    StringComparison.Ordinal);
            else if (mode == "semantic")
                matches = string.Equals(SemanticCanonicalize(nr), SemanticCanonicalize(np), StringComparison.Ordinal);
            else
                matches = string.Equals(nr, np, StringComparison.Ordinal);

            return new Result
            {
                Mode = mode,
                Matches = matches,
                RequestedHash = Sha256(requested),
                PersistedHash = Sha256(persisted),
                NormalizedRequestedHash = Sha256(nr),
                NormalizedPersistedHash = Sha256(np),
                NormalizationApplied = DetectNormalizations(requested, persisted),
                DiffNormalized = BuildDiff(nr, np),
                NormalizedRequested = nr,
                NormalizedPersisted = np
            };
        }

        internal static string Canonicalize(string text, string requestedMode, string partName)
        {
            string mode = ResolveMode(requestedMode, partName);
            if (mode == "exact") return ExactCanonicalize(text, partName);
            string normalized = Normalize(text);
            return mode == "semantic" ? SemanticCanonicalize(normalized) : normalized;
        }

        internal static string Normalize(string text)
        {
            string value = (text ?? string.Empty).TrimStart('\uFEFF').Normalize(NormalizationForm.FormC)
                .Replace("\r\n", "\n").Replace("\r", "\n");
            string[] lines = value.Split('\n');
            var output = new List<string>(lines.Length);
            foreach (string raw in lines)
            {
                string line = raw.TrimEnd(' ', '\t');
                bool blank = line.Length == 0;
                // Empty lines are formatting-only in Source/Rules and the SDK may
                // insert, collapse, or remove them while rendering a saved part.
                if (blank) continue;
                output.Add(line);
            }
            return string.Join("\n", output);
        }

        private static string ExactCanonicalize(string text, string partName)
        {
            string value = text ?? string.Empty;
            // Logical code text parts (Source, Rules, Events, Conditions) can return LF
            // from the forced post-save SDK read even when the requested payload used CRLF.
            // Canonicalize line endings so carriage return differences don't cause false mismatches.
            return IsCodeOrTextPart(partName)
                ? value.Replace("\r\n", "\n").Replace("\r", "\n")
                : value;
        }

        private static string SemanticCanonicalize(string text)
        {
            var sb = new StringBuilder(text.Length);
            bool inString = false;
            char quote = '\0';
            bool whitespacePending = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    sb.Append(c);
                    if (c == quote)
                    {
                        if (i + 1 < text.Length && text[i + 1] == quote)
                        {
                            sb.Append(text[++i]);
                        }
                        else inString = false;
                    }
                    continue;
                }
                if (c == '\'' || c == '"')
                {
                    if (whitespacePending && sb.Length > 0 && IsWordish(sb[sb.Length - 1])) sb.Append(' ');
                    whitespacePending = false;
                    inString = true;
                    quote = c;
                    sb.Append(c);
                }
                else if (char.IsWhiteSpace(c))
                {
                    whitespacePending = true;
                }
                else
                {
                    if (whitespacePending && sb.Length > 0 && IsWordish(sb[sb.Length - 1]) && IsWordish(c)) sb.Append(' ');
                    whitespacePending = false;
                    sb.Append(char.ToLowerInvariant(c));
                }
            }
            return sb.ToString().Trim();
        }

        private static JArray DetectNormalizations(string requested, string persisted)
        {
            var result = new JArray();
            if (HasDifferentEol(requested, persisted)) result.Add("EOL");
            if (requested.StartsWith("\uFEFF", StringComparison.Ordinal) != persisted.StartsWith("\uFEFF", StringComparison.Ordinal)
                || !string.Equals(requested, requested.Normalize(NormalizationForm.FormC), StringComparison.Ordinal)
                || !string.Equals(persisted, persisted.Normalize(NormalizationForm.FormC), StringComparison.Ordinal))
                result.Add("encoding");
            if (HasTrailingWhitespaceDifference(requested, persisted)) result.Add("trailing-whitespace");
            if (HasBlankLineDifference(requested, persisted)) result.Add("blank-lines");
            return result;
        }

        private static bool HasDifferentEol(string a, string b)
        {
            return Count(a, "\r\n") != Count(b, "\r\n") || CountLoneLf(a) != CountLoneLf(b) || CountLoneCr(a) != CountLoneCr(b);
        }

        private static bool HasTrailingWhitespaceDifference(string a, string b)
        {
            string Strip(string value) => string.Join("\n", (value ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').Select(x => x.TrimEnd(' ', '\t')));
            bool aHasTrailing = (a ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').Any(x => x.Length != x.TrimEnd(' ', '\t').Length);
            bool bHasTrailing = (b ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').Any(x => x.Length != x.TrimEnd(' ', '\t').Length);
            return aHasTrailing != bHasTrailing || (!string.Equals(a, b, StringComparison.Ordinal) && string.Equals(Strip(a), Strip(b), StringComparison.Ordinal));
        }

        private static bool HasBlankLineDifference(string a, string b)
        {
            string Prepare(string value) => (value ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").TrimStart('\uFEFF');
            string Collapse(string value) => string.Join("\n", Prepare(value).Split('\n').Where((line, index) => line.Trim().Length > 0 || index == 0));
            return !string.Equals(Prepare(a), Prepare(b), StringComparison.Ordinal) && string.Equals(Collapse(a), Collapse(b), StringComparison.Ordinal);
        }

        private static string BuildDiff(string requested, string persisted)
        {
            if (string.Equals(requested, persisted, StringComparison.Ordinal)) return null;
            string[] left = requested.Split('\n');
            string[] right = persisted.Split('\n');
            int count = Math.Max(left.Length, right.Length);
            for (int i = 0; i < count; i++)
            {
                string l = i < left.Length ? left[i] : "<missing>";
                string r = i < right.Length ? right[i] : "<missing>";
                if (!string.Equals(l, r, StringComparison.Ordinal))
                    return "line " + (i + 1) + ": requested=" + Clip(l) + "; persisted=" + Clip(r);
            }
            return "normalized content differs";
        }

        private static string Clip(string value) => value.Length <= 160 ? value : value.Substring(0, 160) + "…";
        private static bool IsWordish(char value) => char.IsLetterOrDigit(value) || value == '_' || value == '&' || value == '#';
        internal static string Sha256(string value)
        {
            return SdkReflection.Sha256Hex(value);
        }
        private static int Count(string value, string needle) => (value ?? string.Empty).Split(new[] { needle }, StringSplitOptions.None).Length - 1;
        private static int CountLoneLf(string value) => (value ?? string.Empty).Replace("\r\n", string.Empty).Count(c => c == '\n');
        private static int CountLoneCr(string value) => (value ?? string.Empty).Replace("\r\n", string.Empty).Count(c => c == '\r');
        private static bool IsSourceOrRules(string partName) => IsCodeOrTextPart(partName);
        internal static bool IsCodeOrTextPart(string partName) => string.IsNullOrWhiteSpace(partName)
            || string.Equals(partName, "Source", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "Rules", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "Events", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "Conditions", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "Help", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "Documentation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "DataSelector", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "WSDL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "Styles", StringComparison.OrdinalIgnoreCase);
    }
}
