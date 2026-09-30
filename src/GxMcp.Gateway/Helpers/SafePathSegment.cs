namespace GxMcp.Gateway
{
    /// <summary>
    /// The allowlist that guards every caller-supplied path segment before it
    /// reaches <see cref="System.IO.Path"/>. It exists once because the segments
    /// are LLM-controlled: a segment carrying <c>..\..\x</c> escapes the Objects
    /// tree and turns a delete-and-copy into an arbitrary-directory overwrite.
    ///
    /// It previously existed three times — <c>KbImportHelper.IsSafeSegment</c>
    /// (Gateway), <c>TimeTravelService.IsSafeObjectName</c> and
    /// <c>ApiIntrospectService.IsSafeBaselineName</c> (Worker) — with the same
    /// character class but three different length ceilings. A guard like this is
    /// worth exactly one copy: an allowlist that drifts is a traversal hole that
    /// only opens on the copy nobody updated.
    /// </summary>
    internal static class SafePathSegment
    {
        /// <summary>
        /// Longest accepted segment. The three originals used 200, 200 and 64; 200
        /// is the loosest of those and is kept so no caller becomes stricter than
        /// it was. <c>ApiIntrospectService</c> passes its own tighter ceiling via
        /// <see cref="IsSafeBaselineName"/>.
        /// </summary>
        internal const int MaxLength = 200;

        /// <summary>
        /// True when <paramref name="value"/> is a single path segment safe to
        /// combine: 1..<paramref name="maxLength"/> characters, each a letter,
        /// digit, <c>_</c>, <c>.</c> or <c>-</c>, and not a traversal marker.
        /// </summary>
        internal static bool IsSafe(string value, int maxLength = MaxLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength) return false;
            foreach (var c in value)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-')) return false;
            }
            return value != "." && value != "..";
        }
    }
}

