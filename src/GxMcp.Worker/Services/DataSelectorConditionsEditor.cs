using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Pure helpers for writing a Data Selector's condition list through
    /// genexus_edit part=Conditions. One condition per line; the SDK stores the
    /// bare expression (no trailing ';'), so a trailing ';' is accepted on input and stripped.
    /// </summary>
    internal static class DataSelectorConditionsEditor
    {
        internal const string PartName = "Conditions";

        internal static bool IsConditionsPart(string partName)
            => string.Equals(partName?.Trim(), PartName, StringComparison.OrdinalIgnoreCase);

        internal static bool Applies(object obj, string partName)
            => obj is DataSelector && IsConditionsPart(partName);

        internal static string NormalizeExpression(string expression)
        {
            string value = (expression ?? string.Empty).Trim();
            while (value.EndsWith(";", StringComparison.Ordinal))
                value = value.Substring(0, value.Length - 1).TrimEnd();
            return value;
        }

        /// <summary>Splits the edit content into normalized, non-blank condition expressions.</summary>
        internal static List<string> Parse(string content)
        {
            return (content ?? string.Empty)
                .Replace("\r\n", "\n").Replace('\r', '\n')
                .Split('\n')
                .Select(NormalizeExpression)
                .Where(line => line.Length > 0)
                .ToList();
        }

        /// <summary>Canonical text form (one normalized condition per line) shared by read, write and verification.</summary>
        internal static string Join(IEnumerable<string> expressions)
            => string.Join("\n", (expressions ?? Enumerable.Empty<string>())
                .Select(NormalizeExpression)
                .Where(line => line.Length > 0));

        internal static string Canonicalize(string content) => Join(Parse(content));

        internal static bool SameConditions(IEnumerable<string> left, IEnumerable<string> right)
            => string.Equals(Join(left), Join(right), StringComparison.Ordinal);

        /// <summary>Reads conditions through the same SDK path DataSelectorReadService uses.</summary>
        internal static List<string> ReadExpressions(DataSelectorStructurePart part)
        {
            var result = new List<string>();
            if (part == null) return result;
            foreach (DataSelectorCondition condition in part.GetConditions() ?? Enumerable.Empty<DataSelectorCondition>())
                result.Add(condition.Source?.Source ?? condition.ToString() ?? string.Empty);
            return result;
        }
    }
}
