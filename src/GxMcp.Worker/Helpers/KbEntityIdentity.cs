using System;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The three parts of a KB object's <c>EntityKey</c>: its string form, its type
    /// GUID, and its numeric id. They travel together — every index entry, every
    /// "where did this come from" receipt and every not-found suggestion records all
    /// three or none.
    ///
    /// <para>This existed as three byte-identical private helper triples in
    /// <c>IndexCacheService</c>, <c>KbService</c> and <c>ListService</c>. Identical
    /// copies are still three places for a future change to miss: an entry gaining a
    /// fourth key would have been added to one and not the others, and the index
    /// would carry it from whichever path the object was first seen through.</para>
    ///
    /// <para>Typed rather than reflective, because the SDK exposes <c>Key</c> directly
    /// and the Worker already references the SDK's own types. Never throws: a
    /// malformed key on one object must not abort an index build over thousands of
    /// others.</para>
    /// </summary>
    internal static class KbEntityIdentity
    {
        /// <summary>The key's string form, or null when absent or unreadable.</summary>
        internal static string Key(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            try { return obj?.Key?.ToString(); }
            catch { return null; }
        }

        /// <summary>The key's type GUID, or null when absent or unreadable.</summary>
        internal static string TypeGuid(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            try { return obj?.Key?.Type.ToString(); }
            catch { return null; }
        }

        /// <summary>The key's numeric id, or null when absent or unreadable.</summary>
        internal static int? Id(global::Artech.Architecture.Common.Objects.KBObject obj)
        {
            try { return obj?.Key?.Id; }
            catch { return null; }
        }

        /// <summary>Reads all three at once — one <c>Key</c> access instead of three.</summary>
        internal static void ReadAll(
            global::Artech.Architecture.Common.Objects.KBObject obj,
            out string key, out string typeGuid, out int? id)
        {
            key = null;
            typeGuid = null;
            id = null;
            try
            {
                var entityKey = obj?.Key;
                if (entityKey == null) return;
                key = entityKey.ToString();
                typeGuid = entityKey.Type.ToString();
                id = entityKey.Id;
            }
            catch { /* a malformed key yields whatever was read before the throw */ }
        }
    }
}
