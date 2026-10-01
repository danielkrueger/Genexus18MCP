using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Accumulates the lite-index walk's entries with O(1) identity-based replacement.
    ///
    /// <para>
    /// Issue #337. The lite walk used to replace an existing entry with
    /// <c>list.RemoveAll(e =&gt; e.Guid == objectGuid)</c> before appending the new
    /// one. That rescans the whole accumulated list for every walked object, so a
    /// cold walk over N unique objects performs N*(N-1)/2 string comparisons -
    /// 49,995,000 at N=10,000, 799,980,000 at N=40,000 - and finds nothing, because
    /// a fresh walk has no duplicate to find. It is quadratic work that can never
    /// pay for itself.
    /// </para>
    ///
    /// <para>
    /// This keeps a GUID -&gt; slot map instead. Replacing an entry tombstones the
    /// old slot (a <c>List&lt;T&gt;</c> indexer assignment, O(1)) and appends the new
    /// entry, so ordering is identical to the old remove-then-append: the surviving
    /// entries keep their relative order and the replacement is always last. The
    /// tombstones are collapsed in one pass by <see cref="Compact"/>.
    /// </para>
    ///
    /// <para>
    /// A resume checkpoint is the one case where a GUID can legitimately occupy two
    /// positions, because the checkpoint index is keyed by its own identity and a
    /// snapshot could hold two entries sharing a GUID. The single up-front pass that
    /// builds the map detects that, and those GUIDs fall back to the original
    /// full-list removal so the result is bit-identical rather than merely
    /// close.
    /// </para>
    ///
    /// <para>
    /// <see cref="Comparisons"/> counts the GUID string comparisons actually
    /// performed. It exists so a regression guard can assert on algorithmic scaling
    /// rather than on wall-clock time, which is meaningless for a managed loop.
    /// </para>
    /// </summary>
    internal sealed class LiteEntryAccumulator
    {
        private readonly List<SearchIndex.IndexEntry> _entries;
        private readonly Dictionary<string, int> _slotByGuid;
        private readonly HashSet<string> _duplicateGuids;
        private int _tombstones;

        /// <summary>GUID string comparisons performed by dedup. Linear in the number of upserts on the fast path.</summary>
        public long Comparisons { get; private set; }

        /// <summary>Number of upserts that took the rare full-list fallback because their GUID already held two positions.</summary>
        public int FullScanFallbacks { get; private set; }

        public LiteEntryAccumulator(IEnumerable<SearchIndex.IndexEntry> seed = null)
        {
            _entries = seed == null ? new List<SearchIndex.IndexEntry>() : seed.Where(e => e != null).ToList();
            _slotByGuid = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _duplicateGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _entries.Count; i++)
            {
                string guid = _entries[i].Guid;
                if (string.IsNullOrEmpty(guid)) continue;
                Comparisons++;
                if (_slotByGuid.ContainsKey(guid)) _duplicateGuids.Add(guid);
                else _slotByGuid[guid] = i;
            }
        }

        public int Count => _entries.Count;

        /// <summary>The accumulated entries with tombstones removed. Compacts on demand, so it is safe to call repeatedly.</summary>
        public List<SearchIndex.IndexEntry> Entries
        {
            get
            {
                Compact();
                return new List<SearchIndex.IndexEntry>(_entries);
            }
        }

        /// <summary>
        /// Appends <paramref name="entry"/>, replacing any accumulated entry with the
        /// same non-empty GUID. Entries without a GUID cannot be identified, so they
        /// are always appended - matching the previous behaviour, which skipped the
        /// removal entirely for them.
        /// </summary>
        public void Upsert(SearchIndex.IndexEntry entry)
        {
            if (entry == null) return;
            string guid = entry.Guid;
            if (!string.IsNullOrEmpty(guid))
            {
                if (_slotByGuid.TryGetValue(guid, out int slot))
                {
                    if (_duplicateGuids.Contains(guid))
                    {
                        FullScanFallbacks++;
                        int removed = _entries.RemoveAll(e =>
                            e != null && string.Equals(e.Guid, guid, StringComparison.OrdinalIgnoreCase));
                        Comparisons += removed;
                    }
                    else
                    {
                        _entries[slot] = null;
                        _tombstones++;
                    }
                }
                _slotByGuid[guid] = _entries.Count;
            }
            _entries.Add(entry);
        }

        /// <summary>Collapses tombstones left by <see cref="Upsert"/>. Returns the number of slots reclaimed.</summary>
        public int Compact()
        {
            if (_tombstones == 0) return 0;
            int reclaimed = _entries.RemoveAll(e => e == null);
            _tombstones = 0;
            return reclaimed;
        }
    }
}
