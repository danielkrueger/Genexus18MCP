using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed partial class SdkTextTreeService
    {
        internal static string BuildObjectDocument(string type, string name, string source)
        {
            return BuildObjectDocument(type, name, source, null);
        }

        internal static string BuildObjectDocument(string type, string name, string source, string indentString)
        {
            if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("Object type is required.", nameof(type));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Object name is required.", nameof(name));

            string normalized = NormalizeNewlines(source ?? string.Empty);
            string indent = indentString ?? string.Empty;
            var document = new StringBuilder(type.Trim().Length + name.Trim().Length + normalized.Length + 8);
            document.Append(type.Trim()).Append(' ').Append(name.Trim()).Append('\n');
            document.Append("{\n");
            if (normalized.Length > 0)
            {
                string[] lines = normalized.Split(new[] { '\n' }, StringSplitOptions.None);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.Length > 0 && indent.Length > 0 && !line.StartsWith(indent, StringComparison.Ordinal))
                        document.Append(indent);
                    document.Append(line);
                    if (i < lines.Length - 1) document.Append('\n');
                }
                if (!normalized.EndsWith("\n", StringComparison.Ordinal)) document.Append('\n');
            }
            document.Append("}\n");
            return document.ToString();
        }

        internal static string BuildObjectDocumentParts(
            string type,
            string name,
            IDictionary<string, string> parts,
            string indentString = null)
        {
            if (parts == null || parts.Count == 0) throw new ArgumentException("At least one object part is required.", nameof(parts));
            if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("Object type is required.", nameof(type));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Object name is required.", nameof(name));

            string indent = indentString ?? string.Empty;
            var orderedNames = NativeAllParts
                .Concat(parts.Keys)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(value => parts.ContainsKey(value))
                .ToList();
            var document = new StringBuilder(type.Trim().Length + name.Trim().Length + 32);
            document.Append(type.Trim()).Append(' ').Append(name.Trim()).Append('\n');
            document.Append("{\n");
            foreach (string part in orderedNames)
            {
                document.Append(indent).Append('#').Append(part).Append('\n');
                AppendIndentedContent(document, NormalizeNewlines(parts[part] ?? string.Empty), indent);
                document.Append(indent).Append("#End\n");
            }
            document.Append("}\n");
            return document.ToString();
        }

        private static void AppendIndentedContent(StringBuilder document, string content, string indent)
        {
            if (content == null) content = string.Empty;
            string[] lines = content.Split(new[] { '\n' }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length > 0 && indent.Length > 0 && !line.StartsWith(indent, StringComparison.Ordinal))
                    document.Append(indent);
                document.Append(line);
                if (i < lines.Length - 1) document.Append('\n');
            }
            if (!content.EndsWith("\n", StringComparison.Ordinal)) document.Append('\n');
        }

        /// <summary>
        /// The header and brace-delimited body of an object text document.
        /// </summary>
        /// <remarks>
        /// Splitting an object document is the first thing both readers do, and they
        /// were each doing all of it: the emptiness check, the newline
        /// normalisation, the header split, the two-part validation, the brace
        /// search and the balance check - with the same three error strings, so a
        /// client parsing malformed text could be told "Object header must contain
        /// type and name." by one reader and something else by the other for the
        /// same document.
        ///
        /// The body is returned raw, between the braces, so each reader can apply
        /// its own interpretation: the source reader strips a leading
        /// <c>#Source</c> marker, and the parts reader walks the body's own
        /// <c>#Part</c> sections.
        /// </remarks>
        private static bool TrySplitObjectDocument(
            string document,
            out string type,
            out string name,
            out string body,
            out string error)
        {
            type = null;
            name = null;
            body = null;
            error = null;

            if (string.IsNullOrWhiteSpace(document))
            {
                error = "Object text is empty.";
                return false;
            }

            string normalized = NormalizeNewlines(document);
            int headerEnd = normalized.IndexOf('\n');
            string header = (headerEnd >= 0 ? normalized.Substring(0, headerEnd) : normalized).Trim();
            string[] headerParts = header.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (headerParts.Length != 2)
            {
                error = "Object header must contain type and name.";
                return false;
            }

            int open = normalized.IndexOf('{', headerEnd >= 0 ? headerEnd : 0);
            int close = normalized.LastIndexOf('}');
            if (open < 0 || close <= open)
            {
                error = "Object document must contain a balanced body enclosed by braces.";
                return false;
            }

            type = headerParts[0].Trim();
            name = headerParts[1].Trim();
            body = normalized.Substring(open + 1, close - open - 1).Trim('\n');
            return true;
        }

        internal static bool TryParseObjectDocument(
            string document,
            out string type,
            out string name,
            out string source,
            out string error)
        {
            source = null;
            if (!TrySplitObjectDocument(document, out type, out name, out string body, out error))
                return false;

            source = body;
            if (source.StartsWith("#Source\n", StringComparison.OrdinalIgnoreCase))
                source = source.Substring("#Source\n".Length);
            else if (string.Equals(source, "#Source", StringComparison.OrdinalIgnoreCase))
                source = string.Empty;
            return true;
        }

        internal static bool TryParseObjectDocumentParts(
            string document,
            out string type,
            out string name,
            out Dictionary<string, string> parts,
            out string error)
        {
            parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!TrySplitObjectDocument(document, out type, out name, out string body, out error))
                return false;

            string current = null;
            string currentPrefix = string.Empty;
            var content = new StringBuilder();
            bool sawSection = false;
            foreach (string rawLine in body.Split(new[] { '\n' }, StringSplitOptions.None))
            {
                string line = rawLine ?? string.Empty;
                string trimmed = line.Trim();
                if (trimmed.StartsWith("#", StringComparison.Ordinal) && trimmed.Length > 1)
                {
                    string marker = trimmed.Substring(1).Trim();
                    if (string.Equals(marker, "End", StringComparison.OrdinalIgnoreCase))
                    {
                        if (current != null) parts[current] = content.ToString().Trim('\n');
                        current = null;
                        currentPrefix = string.Empty;
                        content.Clear();
                        continue;
                    }
                    if (NativeAllParts.Any(value => string.Equals(value, marker, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (current != null) parts[current] = content.ToString().Trim('\n');
                        current = marker;
                        int markerIndex = line.IndexOf('#');
                        currentPrefix = markerIndex > 0 ? line.Substring(0, markerIndex) : string.Empty;
                        content.Clear();
                        sawSection = true;
                        continue;
                    }
                }

                if (current == null) continue;
                if (currentPrefix.Length > 0 && line.StartsWith(currentPrefix, StringComparison.Ordinal))
                    line = line.Substring(currentPrefix.Length);
                content.Append(line).Append('\n');
            }
            if (current != null) parts[current] = content.ToString().Trim('\n');
            if (!sawSection)
            {
                if (body.StartsWith("#Source\n", StringComparison.OrdinalIgnoreCase))
                    body = body.Substring("#Source\n".Length);
                else if (string.Equals(body, "#Source", StringComparison.OrdinalIgnoreCase))
                    body = string.Empty;
                parts["Source"] = body;
            }
            return parts.Count > 0;
        }

        private static string NormalizeNewlines(string value)
        {
            return (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        }
    }
}
