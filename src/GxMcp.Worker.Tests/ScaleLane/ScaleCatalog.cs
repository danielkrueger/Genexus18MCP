using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GxMcp.Worker.Tests.ScaleLane
{
    /// <summary>
    /// A seeded, entirely fictional KB catalog for issue #358.
    ///
    /// <para>
    /// Seeded because a scale lane whose inputs change between runs cannot be compared
    /// to anything. The 40k benchmark already in this repository rebuilt its list from
    /// <c>Guid.NewGuid()</c> and <c>DateTime.UtcNow</c> on every run, so two runs
    /// measured different catalogs and its numbers could only ever be reported, never
    /// gated on.
    /// </para>
    ///
    /// <para>
    /// Fictional because the issue forbids touching a real KB and forbids putting object
    /// contents or absolute paths in a shareable report. Names come from invented word
    /// lists, GUIDs are derived from the seed rather than drawn at random, and timestamps
    /// are offsets from a fixed epoch. Two runs at the same seed produce byte-identical
    /// catalogs on any host, in any time zone.
    /// </para>
    /// </summary>
    public sealed class ScaleCatalog
    {
        /// <summary>
        /// Fixed epoch for generated timestamps. A constant, not the clock: a catalog
        /// whose timestamps move between runs cannot be compared across runs.
        /// </summary>
        public static readonly DateTime Epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// The KB sizes the issue asks for. 10k is the floor that still crosses a shard
        /// boundary in the snapshot publication path; 100k is the top of the matrix.
        /// </summary>
        public static readonly int[] StandardSizes = { 10_000, 50_000, 100_000 };

        // Invented words combined into plausible KB object names. Nothing here is a real
        // GeneXus object, module or customer identifier.
        private static readonly string[] Nouns =
        {
            "Ledger", "Invoice", "Shipment", "Account", "Contract", "Ticket", "Roster",
            "Payment", "Catalog", "Route", "Batch", "Journal", "Statement", "Voucher",
            "Manifest", "Tariff", "Booking", "Allocation", "Reconcile", "Settlement",
        };

        private static readonly string[] Qualifiers =
        {
            "Header", "Detail", "Summary", "Audit", "Snapshot", "Archive", "Draft",
            "Posted", "Pending", "Reversal", "Adjustment", "Consolidation", "Fulfillment",
            "Requisition", "Amendment", "Liquidation",
        };

        private static readonly string[] Modules =
        {
            "SalesModule", "FinanceModule", "LogisticsModule", "SupportModule",
            "InventoryModule", "AnalyticsModule",
        };

        /// <summary>
        /// Object types in roughly the proportion a real KB has: many attributes and data
        /// elements, few entry points. A uniform mix would overstate index cost, because
        /// the type buckets that dominate are not evenly sized in practice.
        /// </summary>
        private static readonly string[] TypeMix =
        {
            "Attribute", "Attribute", "Attribute", "Attribute", "Attribute",
            "Attribute", "Attribute", "Attribute", "Attribute", "Attribute",
            "Table", "Table", "Table",
            "Procedure", "Procedure", "Procedure",
            "Transaction",
            "WebPanel",
            "DataProvider",
            "Domain", "Folder",
        };

        /// <summary>
        /// Builds a catalog of <paramref name="objectCount"/> fictional objects.
        /// </summary>
        /// <param name="objectCount">Objects to generate.</param>
        /// <param name="seed">
        /// Catalog seed. The same seed at the same size yields an identical catalog, which
        /// is what makes a recorded baseline comparable to a later run.
        /// </param>
        /// <param name="kbIndex">
        /// Which KB of the matrix this is. Affects only the alias and the RNG salt, so a
        /// 3-KB run produces three genuinely distinct catalogs instead of three copies -
        /// otherwise the second and third KB would measure a warm cache rather than a
        /// second KB.
        /// </param>
        /// <param name="sourceSizeBytes">
        /// Nominal Source-part size per object, for the source-store workloads.
        /// </param>
        /// <param name="fanIn">
        /// Outgoing references per object. Drives the call-graph and caller-scan shapes,
        /// which are the workloads that stop scaling linearly.
        /// </param>
        public static ScaleCatalog Generate(
            int objectCount,
            int seed = 20260101,
            int kbIndex = 0,
            int sourceSizeBytes = 2_048,
            int fanIn = 8)
        {
            if (objectCount < 1) throw new ArgumentOutOfRangeException(nameof(objectCount));
            if (seed < 0) throw new ArgumentOutOfRangeException(nameof(seed));
            if (kbIndex < 0) throw new ArgumentOutOfRangeException(nameof(kbIndex));
            if (sourceSizeBytes < 64) throw new ArgumentOutOfRangeException(nameof(sourceSizeBytes));
            if (fanIn < 0) throw new ArgumentOutOfRangeException(nameof(fanIn));

            var rng = new DeterministicRandom(unchecked(seed * 397) ^ (kbIndex * 31));

            // Pass 1: identities. Names come from a small noun x qualifier vocabulary, so
            // they necessarily repeat - which is the point. A catalog of globally unique
            // names would never exercise the name index's cross-type disambiguation, and
            // cross-type sharing is ordinary in a real KB: a Transaction and its Table, a
            // Procedure and a Form. Uniqueness is only required *within* a type, so that
            // is the rule applied here; a name already taken by this same type is
            // qualified, and a name taken by another type is reused as-is.
            var entries = new List<GxMcp.Worker.Models.SearchIndex.IndexEntry>(objectCount);
            var names = new string[objectCount];
            var typesByName = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < objectCount; i++)
            {
                string candidate = Nouns[rng.Next(Nouns.Length)] + Qualifiers[rng.Next(Qualifiers.Length)];
                string type = TypeMix[i % TypeMix.Length];

                if (!typesByName.TryGetValue(candidate, out var seenTypes))
                {
                    typesByName[candidate] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { type };
                    names[i] = candidate;
                }
                else if (!seenTypes.Contains(type))
                {
                    seenTypes.Add(type);
                    names[i] = candidate;
                }
                else
                {
                    names[i] = candidate + i.ToString("D5", CultureInfo.InvariantCulture);
                }
            }

            // Pass 2: attributes and references, now that every name exists.
            for (int i = 0; i < objectCount; i++)
            {
                string type = TypeMix[i % TypeMix.Length];
                string module = Modules[rng.Next(Modules.Length)];
                string folder = Modules[rng.Next(Modules.Length)] + "/" + Qualifiers[rng.Next(Qualifiers.Length)];

                var calls = new List<string>(fanIn);
                for (int f = 0; f < fanIn; f++)
                {
                    // Multiplicative scatter, so the graph has both short and long-range
                    // edges. Consecutive targets would make the caller scan trivially
                    // cache-friendly and understate its cost.
                    int target = (int)(((ulong)i * 2654435761UL) % (ulong)objectCount);
                    calls.Add(names[target]);
                }

                bool isDataElement = type == "Attribute" || type == "Table";
                entries.Add(new GxMcp.Worker.Models.SearchIndex.IndexEntry
                {
                    // Derived from the seed, not random. Guid.NewGuid() would make the
                    // catalog differ per run and silently invalidate every baseline.
                    Guid = ScaleGuid.DeterministicGuid(seed, kbIndex, i),
                    Name = names[i],
                    Type = type,
                    Module = module,
                    ParentPath = folder,
                    ParentFolderPath = folder,
                    Path = folder + "/" + names[i],
                    Description = "Synthetic " + type.ToLowerInvariant() + " entry.",
                    Calls = calls,
                    Tables = isDataElement
                        ? new List<string> { names[(int)(((ulong)i * 40503UL) % (ulong)objectCount)] }
                        : new List<string>(),
                    LastUpdate = Epoch.AddMinutes(-rng.Next(0, 5_000_000)),
                    CreatedAt = Epoch.AddMinutes(-rng.Next(5_000_000, 9_000_000)),
                    Length = rng.Next(1, 400),
                    Decimals = type == "Attribute" ? rng.Next(0, 6) : 0,
                    Complexity = rng.Next(0, 100),
                });
            }

            return new ScaleCatalog
            {
                Alias = "synthetic-kb-" + (kbIndex + 1).ToString(CultureInfo.InvariantCulture),
                Seed = seed,
                KbIndex = kbIndex,
                ObjectCount = objectCount,
                SourceSizeBytes = sourceSizeBytes,
                FanIn = fanIn,
                Entries = entries,
            };
        }

        /// <summary>
        /// A synthetic Source part for entry <paramref name="index"/>. Fictional
        /// identifiers only, padded to a controllable size so the source-store workload
        /// has a tunable input.
        /// </summary>
        public string SourceFor(int index)
        {
            string name = Entries[index].Name;
            var sb = new StringBuilder(SourceSizeBytes + 256);
            sb.Append("// synthetic source for ").Append(name).Append('\n');
            sb.Append("do ").Append(name).Append('\n');
            while (sb.Length < SourceSizeBytes)
            {
                sb.Append("  // line ").Append(sb.Length.ToString(CultureInfo.InvariantCulture))
                  .Append(" of ").Append(name).Append('\n');
            }
            sb.Append("end\n");
            return sb.ToString();
        }

        public string Alias { get; private set; }
        public int Seed { get; private set; }
        public int KbIndex { get; private set; }
        public int ObjectCount { get; private set; }
        public int SourceSizeBytes { get; private set; }
        public int FanIn { get; private set; }
        public List<GxMcp.Worker.Models.SearchIndex.IndexEntry> Entries { get; private set; }
    }

    /// <summary>
    /// A fixed-sequence PRNG.
    ///
    /// <para>
    /// Deliberately not <see cref="System.Random"/>. That implementation is not
    /// specified to produce the same sequence across runtimes or framework versions, and
    /// a scale lane whose fixture depends on the host's BCL is not reproducible on the
    /// very host that is supposed to gate it. This is a 64-bit xorshift*, a few lines,
    /// and gives the same numbers everywhere.
    /// </para>
    /// </summary>
    public sealed class DeterministicRandom
    {
        private ulong _state;

        public DeterministicRandom(int seed)
        {
            // Zero is xorshift's fixed point, so displace away from it.
            _state = seed == 0 ? 0x9E3779B97F4A7C15UL : unchecked((ulong)seed);
        }

        public ulong NextUInt64()
        {
            _state ^= _state >> 12;
            _state ^= _state << 25;
            _state ^= _state >> 27;
            return unchecked(_state * 0x2545F4914F6CDD1DUL);
        }

        /// <summary>Non-negative int in [0, exclusiveMax).</summary>
        public int Next(int exclusiveMax)
        {
            if (exclusiveMax <= 0) throw new ArgumentOutOfRangeException(nameof(exclusiveMax));
            return (int)(NextUInt64() % (ulong)exclusiveMax);
        }

        /// <summary>Int in [minInclusive, maxExclusive).</summary>
        public int Next(int minInclusive, int maxExclusive)
        {
            if (minInclusive >= maxExclusive) throw new ArgumentOutOfRangeException(nameof(maxExclusive));
            long span = (long)maxExclusive - minInclusive;
            return (int)(minInclusive + (long)(NextUInt64() % (ulong)span));
        }
    }

    internal static class ScaleGuid
    {
        /// <summary>
        /// A GUID derived from the fixture identity, so the same seed yields the same
        /// GUIDs on any host.
        /// </summary>
        internal static string DeterministicGuid(int seed, int kbIndex, int index)
        {
            unchecked
            {
                ulong hi = ((ulong)(uint)seed << 32) ^ ((ulong)(uint)kbIndex << 16) ^ (uint)index;
                ulong lo = hi * 0x9E3779B97F4A7C15UL;
                var bytes = new byte[16];
                for (int i = 0; i < 8; i++)
                {
                    bytes[i] = (byte)(hi >> (8 * i));
                    bytes[8 + i] = (byte)(lo >> (8 * i));
                }
                return new Guid(bytes).ToString("D");
            }
        }
    }
}
