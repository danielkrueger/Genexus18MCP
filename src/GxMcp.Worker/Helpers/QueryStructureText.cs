using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Artech.Architecture.Common.Objects;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Text form of a Query object's structure (issue #404), built on the part's own
    /// <c>ToSerializedStrings</c>/<c>FromSerializedStrings</c> pair so the SDK, not this class,
    /// owns the element/parameter/filter/order syntax. Each line is one serialized item. Output
    /// formats are not covered.
    /// </summary>
    internal static class QueryStructureText
    {
        private static readonly string[] Sections = { "Elements", "Parameters", "Filters", "Orders" };

        internal static bool IsQueryStructurePart(KBObjectPart part)
            => part != null && string.Equals(part.GetType().Name, "QueryStructurePart", StringComparison.Ordinal);

        internal static KBObjectPart FindPart(KBObject obj)
            => obj?.Parts.Cast<KBObjectPart>().FirstOrDefault(IsQueryStructurePart);

        internal static string Render(KBObjectPart part)
        {
            var args = new object[4];
            part.GetType().GetMethod("ToSerializedStrings").Invoke(part, args);
            var text = new StringBuilder();
            for (int i = 0; i < Sections.Length; i++)
            {
                text.Append('[').Append(Sections[i]).Append("]\n");
                foreach (object item in (IEnumerable)args[i] ?? Array.Empty<object>())
                    text.Append(SerializedText(item)).Append('\n');
            }
            return text.ToString();
        }

        // The SDK returns elements and parameters as strings but filters and orders as compare
        // nodes whose Contents is the same serialized form FromSerializedStrings accepts.
        private static string SerializedText(object item)
        {
            string text = item as string
                ?? (string)item.GetType().GetProperty("Contents")?.GetValue(item, null)
                ?? throw new InvalidOperationException("Unsupported serialized query item " + item.GetType().Name);
            return text.Replace("\r", string.Empty).Replace("\n", " ");
        }

        /// <summary>Splits the text form into the four line lists, in the order the SDK expects.</summary>
        internal static List<string>[] Parse(string text)
        {
            var lists = Sections.Select(_ => new List<string>()).ToArray();
            List<string> current = null;
            foreach (string raw in (text ?? string.Empty).Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int section = line.Length > 2 && line[0] == '[' && line[line.Length - 1] == ']'
                    ? Array.IndexOf(Sections, line.Substring(1, line.Length - 2)) : -1;
                if (section >= 0) { current = lists[section]; continue; }
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (current == null)
                    throw new FormatException("Query structure text must start with a [Elements], [Parameters], [Filters] or [Orders] section.");
                current.Add(line);
            }
            return lists;
        }

        internal static void Apply(KBObjectPart part, string text)
        {
            var lists = Parse(text);
            part.GetType().GetMethod("FromSerializedStrings").Invoke(part, new object[] { lists[0], lists[1], lists[2], lists[3] });
        }
    }
}
