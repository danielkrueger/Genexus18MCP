namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The allowlist that guards every caller-supplied path segment before it
    /// reaches the filesystem or a shell. It exists once because the segments are
    /// LLM-controlled: a segment carrying <c>..\..\x</c> escapes the intended
    /// tree, and in <c>genexus_time_travel</c> the same value also reaches
    /// <c>git</c> as an argument.
    ///
    /// It previously existed three times — <c>TimeTravelService.IsSafeObjectName</c>
    /// and <c>ApiIntrospectService.IsSafeBaselineName</c> here, and
    /// <c>KbImportHelper.IsSafeSegment</c> on the Gateway side — with the same
    /// character class but different length ceilings. A guard like this is worth
    /// exactly one copy: an allowlist that drifts is a traversal hole that only
    /// opens on the copy nobody updated.
    ///
    /// The Gateway's own copy lives in <c>GxMcp.Gateway.SafePathSegment</c>; the
    /// two assemblies cannot share a type, and the character class is pinned
    /// identically on both sides by <c>SafePathSegmentTests</c>.
    /// </summary>
    internal static class SafePathSegment
    {
        /// <summary>
        /// Longest accepted segment. The originals used 200 here and on the Gateway
        /// and 64 for baselines; 200 is kept as the default so no caller becomes
        /// stricter than it was, and callers with a tighter requirement pass their
        /// own ceiling.
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
            // Defence in depth: an allowlist admits "." and "..", so reject them
            // explicitly.
            return value != "." && value != "..";
        }
    }
}
