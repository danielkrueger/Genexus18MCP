using System;
using System.Collections.Generic;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Helpers
{
    /// <summary>One class defined in the KB.</summary>
    public sealed class ThemeClassRef
    {
        /// <summary>Class name as the IDE shows it (e.g. "TableDragging").</summary>
        public string Name { get; set; }
        /// <summary>
        /// The ThemeClass KB object's own GUID. This is an identity, NOT the value a
        /// layout <c>class</c> attribute takes: a measured layout attribute
        /// (<c>&lt;guid&gt;-&lt;suffix&gt;</c>) matched none of the KB's class GUIDs, so
        /// the layout attribute is an SDK style reference this Server cannot resolve
        /// headlessly. Exposed for correlation only.
        /// </summary>
        public string ObjectGuid { get; set; }
    }

    public sealed class ThemeClassCatalogResult
    {
        public IReadOnlyList<ThemeClassRef> Classes { get; set; } = new List<ThemeClassRef>();
        /// <summary>Classes the KB has, ignoring <c>limit</c> — lets the caller mark truncation.</summary>
        public int TotalCount { get; set; }
        /// <summary>Non-null when the catalog could not be built; Classes is empty.</summary>
        public string UnavailableReason { get; set; }
    }

    /// <summary>
    /// W6 (theme/class introspection): an agent cannot know which classes a KB defines,
    /// so it either guesses a class name or invents an identifier. This catalog removes
    /// the guess by listing the KB's ThemeClass objects.
    ///
    /// It deliberately does NOT claim to produce an authorable layout class value: a
    /// measured layout <c>class</c> attribute (<c>&lt;guid&gt;-&lt;suffix&gt;</c>) matches
    /// none of the KB's class GUIDs, so that mapping is unresolved rather than merely
    /// unimplemented. Callers must surface that instead of presenting the GUID as
    /// authorable.
    ///
    /// The loaded index is the only complete enumeration available headlessly. A direct
    /// model walk (<c>KBModel.Objects.GetAll()</c>) surfaces just the theme-root object
    /// where the same KB's index holds 129 ThemeClass entries, and a theme's own style
    /// tree yields that same single root — so neither is a usable fallback and neither
    /// is used here.
    ///
    /// The entry <c>Type</c> is the discriminator, not the <c>TypeIndex</c> bucket: the
    /// bucket is a candidate set whose storage-key format is internal to
    /// IndexCacheService, and a key-based read resolved to 1 entry where the KB has 129.
    /// </summary>
    public static class ThemeClassCatalog
    {
        private const string ThemeClassType = "ThemeClass";

        public static ThemeClassCatalogResult BuildFromIndex(SearchIndex index, int limit = 200)
        {
            var result = new ThemeClassCatalogResult();
            if (index?.Objects == null)
            {
                result.UnavailableReason = "the KB index is not loaded";
                return result;
            }
            if (limit <= 0) limit = 1;

            var all = new List<ThemeClassRef>();
            foreach (var entry in index.Objects.Values)
            {
                if (entry == null) continue;
                if (!string.Equals(entry.Type, ThemeClassType, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                all.Add(new ThemeClassRef
                {
                    Name = entry.Name,
                    ObjectGuid = string.IsNullOrWhiteSpace(entry.Guid) ? null : entry.Guid
                });
            }
            // Sort before truncating: the index is a dictionary, so without this a
            // limit would return an arbitrary subset that changes between calls.
            all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            result.TotalCount = all.Count;
            if (all.Count > limit) all.RemoveRange(limit, all.Count - limit);
            result.Classes = all;
            if (all.Count == 0)
            {
                result.UnavailableReason = "this KB exposes no ThemeClass objects";
            }
            return result;
        }
    }
}
