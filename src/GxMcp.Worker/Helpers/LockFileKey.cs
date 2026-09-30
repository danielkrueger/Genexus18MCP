using System.Text;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The filename-safe key that <c>WritePipeline</c> and
    /// <c>MultiAgentLockService</c> both use to name their per-object lock files.
    ///
    /// It existed twice, identically. That matters more than the duplication: two
    /// implementations of a lock-file key can disagree on one character class, and
    /// a disagreement means two processes holding different filenames believe they
    /// hold the same object — or the reverse, two names for one lock. Either way
    /// the per-target mutual exclusion silently stops working, and it fails as a
    /// lost update rather than as an error anyone would see.
    /// </summary>
    internal static class LockFileKey
    {
        /// <summary>
        /// <c>target__part</c> with anything outside <c>[A-Za-z0-9_.-]</c> replaced
        /// by <c>_</c>. Null target or part becomes <c>_</c>.
        /// </summary>
        internal static string Sanitize(string target, string part)
        {
            string combined = (target ?? "_") + "__" + (part ?? "_");
            var sb = new StringBuilder(combined.Length);
            foreach (char c in combined)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')
                    sb.Append(c);
                else
                    sb.Append('_');
            }
            return sb.ToString();
        }
    }
}
