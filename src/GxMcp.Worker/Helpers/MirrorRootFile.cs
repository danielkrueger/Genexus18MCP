using System;
using System.IO;

namespace GxMcp.Worker
{
    /// <summary>
    /// Resolves a mirror-manifest relative file against the mirror root without
    /// letting it escape, and deletes a resolved file.
    ///
    /// Both existed twice, byte-identical: in
    /// <c>SdkTextTreeService.Support</c> and in
    /// <c>TextMirrorService.Reconciliation</c>. The resolution half is a
    /// containment guard - it rejects a rooted path and re-checks that the
    /// combined, fully-resolved path is still under the root - and a containment
    /// guard that exists twice is a traversal hole waiting on whichever copy
    /// somebody edits next. Two services both orphan-clean their mirror, so both
    /// delete files named by a manifest on disk; a divergence would mean one of
    /// them can be talked into deleting outside its own root.
    ///
    /// Nothing here touches the GeneXus SDK, so the guard is directly testable.
    /// </summary>
    internal static class MirrorRootFile
    {
        /// <summary>
        /// Resolves <paramref name="relative"/> under <paramref name="root"/>,
        /// yielding the full path only when the result is genuinely inside it.
        ///
        /// Returns false - with <paramref name="full"/> null - for a blank or
        /// absolute <paramref name="relative"/>, and for any path that resolves
        /// outside the root. <c>Path.Combine</c> discards everything before an
        /// absolute second argument, which is why an absolute relative-path is
        /// rejected outright rather than trusted, and why the combined result is
        /// re-checked rather than assumed to be contained.
        ///
        /// Never throws: callers are doing cleanup, and a throw there would skip
        /// the remaining entries.
        /// </summary>
        internal static bool TryResolve(string root, string relative, out string full)
        {
            full = null;
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return false;
            try
            {
                string basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string candidate = Path.GetFullPath(Path.Combine(basePath, relative));
                if (!candidate.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) return false;
                full = candidate;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Deletes <paramref name="path"/>, reporting whether a file was actually
        /// removed. A missing file is false, not an error - the caller is
        /// reconciling a manifest against what is on disk and counts removals.
        /// </summary>
        internal static bool TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch { return false; }
        }
    }
}