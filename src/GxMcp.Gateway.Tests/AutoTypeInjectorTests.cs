using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Unit tests for AutoTypeInjector.TryInject.
    /// </summary>
    [Collection("AutoTypeInjectorState")]
    public class AutoTypeInjectorTests
    {
        // Plan 038: _nameLookup is now keyed per KB alias; these tests exercise
        // single-KB behavior (unchanged) under one fixed alias.
        private const string Kb = "testkb";

        // Wipe all cached state before each test so tests are independent.
        public AutoTypeInjectorTests()
        {
            AutoTypeInjector.ClearAll();
        }

        // ── 1. Unique name → inject type ─────────────────────────────────────

        [Fact]
        public void UniqueNameMatch_InjectsType_ReturnsTrue()
        {
            AutoTypeInjector.PrimeIndex(Kb, new (string, string?)[]
            {
                ("WPMain", "WebPanel"),
                ("CustomerTransaction", "Transaction"),
            });
            AutoTypeInjector.PrimeToolAcceptsType("genexus_read", true);

            var args = new JObject { ["name"] = "WPMain" };
            bool result = AutoTypeInjector.TryInject(Kb, "genexus_read", args, out string injected);

            Assert.True(result);
            Assert.Equal("WebPanel", injected);
            Assert.Equal("WebPanel", args["type"]?.ToString());
        }

        // ── 2. Ambiguous name (2+ objects with same name) → no inject ────────

        [Fact]
        public void AmbiguousName_NoInject_ReturnsFalse()
        {
            // Two entries with the same name but different types → ambiguous
            AutoTypeInjector.PrimeIndex(Kb, new (string, string?)[]
            {
                ("SharedName", "WebPanel"),
                ("SharedName", null),     // null signals ambiguous in the map
            });
            AutoTypeInjector.PrimeToolAcceptsType("genexus_inspect", true);

            var args = new JObject { ["name"] = "SharedName" };
            bool result = AutoTypeInjector.TryInject(Kb, "genexus_inspect", args, out _);

            Assert.False(result);
            Assert.Null(args["type"]);
        }

        // ── 3. Unknown name → no inject ───────────────────────────────────────

        [Fact]
        public void UnknownName_NoInject_ReturnsFalse()
        {
            AutoTypeInjector.PrimeIndex(Kb, new (string, string?)[]
            {
                ("KnownObject", "Procedure"),
            });
            AutoTypeInjector.PrimeToolAcceptsType("genexus_edit", true);

            var args = new JObject { ["name"] = "NonExistent" };
            bool result = AutoTypeInjector.TryInject(Kb, "genexus_edit", args, out _);

            Assert.False(result);
            Assert.Null(args["type"]);
        }

        // ── 4. Tool doesn't accept 'type' → no inject ─────────────────────────

        [Fact]
        public void ToolDoesNotAcceptType_NoInject_ReturnsFalse()
        {
            AutoTypeInjector.PrimeIndex(Kb, new (string, string?)[]
            {
                ("MyProc", "Procedure"),
            });
            AutoTypeInjector.PrimeToolAcceptsType("genexus_run_object", false);

            var args = new JObject { ["name"] = "MyProc" };
            bool result = AutoTypeInjector.TryInject(Kb, "genexus_run_object", args, out _);

            Assert.False(result);
            Assert.Null(args["type"]);
        }

        // ── 5. Caller already supplied 'type' → no inject (don't override) ───

        [Fact]
        public void CallerSuppliedType_NoInject_ReturnsFalse()
        {
            AutoTypeInjector.PrimeIndex(Kb, new (string, string?)[]
            {
                ("MyProc", "Procedure"),
            });
            AutoTypeInjector.PrimeToolAcceptsType("genexus_read", true);

            var args = new JObject { ["name"] = "MyProc", ["type"] = "Transaction" };
            bool result = AutoTypeInjector.TryInject(Kb, "genexus_read", args, out _);

            Assert.False(result);
            // Original caller value must be preserved unchanged
            Assert.Equal("Transaction", args["type"]?.ToString());
        }

        // ── 6. Empty index → no inject ────────────────────────────────────────

        [Fact]
        public void EmptyIndex_NoInject_ReturnsFalse()
        {
            // No PrimeIndex call → empty map
            AutoTypeInjector.PrimeToolAcceptsType("genexus_inspect", true);

            var args = new JObject { ["name"] = "AnyObject" };
            bool result = AutoTypeInjector.TryInject(Kb, "genexus_inspect", args, out _);

            Assert.False(result);
            Assert.Null(args["type"]);
        }

        // ── 7. Skip tool (exempt list) → no inject ────────────────────────────

        [Fact]
        public void SkipTool_NoInject_ReturnsFalse()
        {
            AutoTypeInjector.PrimeIndex(Kb, new (string, string?)[]
            {
                ("SomeObject", "WebPanel"),
            });
            // genexus_kb is in the skip list even if it somehow gets a 'name' arg
            var args = new JObject { ["name"] = "SomeObject" };
            bool result = AutoTypeInjector.TryInject(Kb, "genexus_kb", args, out _);

            Assert.False(result);
            Assert.Null(args["type"]);
        }

        // ── 8. RefreshFromRecentlyChanged feeds unique names ──────────────────

        [Fact]
        public void RefreshFromRecentlyChanged_UniqueEntry_EnablesInject()
        {
            var recent = new JArray
            {
                new JObject { ["Name"] = "MyWebPanel", ["Type"] = "WebPanel" },
                new JObject { ["Name"] = "MyProc", ["Type"] = "Procedure" },
            };
            AutoTypeInjector.RefreshFromRecentlyChanged(Kb, recent);
            AutoTypeInjector.PrimeToolAcceptsType("genexus_read", true);

            var args = new JObject { ["name"] = "MyWebPanel" };
            bool result = AutoTypeInjector.TryInject(Kb, "genexus_read", args, out string injected);

            Assert.True(result);
            Assert.Equal("WebPanel", injected);
        }

        // ── 9. RefreshFromRecentlyChanged: conflicting types → ambiguous ──────

        [Fact]
        public void RefreshFromRecentlyChanged_ConflictingTypes_Ambiguous()
        {
            var recent = new JArray
            {
                new JObject { ["Name"] = "Duplicate", ["Type"] = "WebPanel" },
                new JObject { ["Name"] = "Duplicate", ["Type"] = "Procedure" },
            };
            AutoTypeInjector.RefreshFromRecentlyChanged(Kb, recent);
            AutoTypeInjector.PrimeToolAcceptsType("genexus_inspect", true);

            var args = new JObject { ["name"] = "Duplicate" };
            bool result = AutoTypeInjector.TryInject(Kb, "genexus_inspect", args, out _);

            Assert.False(result);
        }

        // ── 10. Null arguments → no inject (guard) ────────────────────────────

        [Fact]
        public void NullArguments_NoInject_ReturnsFalse()
        {
            AutoTypeInjector.PrimeIndex(Kb, new (string, string?)[] { ("X", "Procedure") });
            AutoTypeInjector.PrimeToolAcceptsType("genexus_read", true);

            bool result = AutoTypeInjector.TryInject(Kb, "genexus_read", null, out _);

            Assert.False(result);
        }

        // ── 11. Resolved type is a Table shadow → no inject ───────────────────

        [Fact]
        public void TableShadowType_NoInject_ReturnsFalse()
        {
            // A Transaction's physical Table shadow can win the name→type map
            // when the top-5 RecentlyChanged window surfaces the table and not
            // the transaction. Injecting "Table" would resolve object tools to
            // the table object (no Source/Rules/Events part) — the observed
            // PatchReadFailed class of failures.
            AutoTypeInjector.PrimeIndex(Kb, new (string, string?)[]
            {
                ("TrnShadow", "Table"),
            });
            AutoTypeInjector.PrimeToolAcceptsType("genexus_edit", true);

            var args = new JObject { ["name"] = "TrnShadow" };
            bool result = AutoTypeInjector.TryInject(Kb, "genexus_edit", args, out _);

            Assert.False(result);
            Assert.Null(args["type"]);
        }

        // ── 12. Source-bearing types still inject (regression guard) ──────────

        [Fact]
        public void TransactionType_StillInjects_ReturnsTrue()
        {
            AutoTypeInjector.PrimeIndex(Kb, new (string, string?)[]
            {
                ("CustomerTransaction", "Transaction"),
            });
            AutoTypeInjector.PrimeToolAcceptsType("genexus_edit", true);

            var args = new JObject { ["name"] = "CustomerTransaction" };
            bool result = AutoTypeInjector.TryInject(Kb, "genexus_edit", args, out string injected);

            Assert.True(result);
            Assert.Equal("Transaction", injected);
            Assert.Equal("Transaction", args["type"]?.ToString());
        }

        // ── 13. RefreshFromRecentlyChanged feeding a Table entry → no inject ──

        [Fact]
        public void RefreshFromRecentlyChanged_TableEntry_NoInject()
        {
            // The real-world feed path: the worker's RecentlyChanged projection
            // carries the Table entry for a recently-reorganized transaction.
            var recent = new JArray
            {
                new JObject { ["Name"] = "TrnShadow", ["Type"] = "Table" },
            };
            AutoTypeInjector.RefreshFromRecentlyChanged(Kb, recent);
            AutoTypeInjector.PrimeToolAcceptsType("genexus_edit", true);

            var args = new JObject { ["name"] = "TrnShadow" };
            bool result = AutoTypeInjector.TryInject(Kb, "genexus_edit", args, out _);

            Assert.False(result);
            Assert.Null(args["type"]);
        }

        // ── 14. ApplyFullNameTypeMap: single type → inject ────────────────────

        [Fact]
        public void ApplyFullNameTypeMap_SingleType_Injects()
        {
            var map = new JObject
            {
                ["MyWebPanel"] = new JArray("WebPanel"),
                ["MyProc"] = new JArray("Procedure"),
            };
            AutoTypeInjector.ApplyFullNameTypeMap(Kb, map);
            AutoTypeInjector.PrimeToolAcceptsType("genexus_read", true);

            var args = new JObject { ["name"] = "MyWebPanel" };
            bool result = AutoTypeInjector.TryInject(Kb, "genexus_read", args, out string injected);

            Assert.True(result);
            Assert.Equal("WebPanel", injected);
        }

        // ── 15. Transaction + Table shadow → resolves to Transaction ──────────

        [Fact]
        public void ApplyFullNameTypeMap_TransactionPlusTable_ResolvesToTransaction()
        {
            // The root-cause case: the design model indexes a Transaction AND its
            // physical Table shadow under the same name. With the full map we can
            // see both and resolve to the source-bearing Transaction.
            var map = new JObject
            {
                ["CustomerTransaction"] = new JArray("Transaction", "Table"),
            };
            AutoTypeInjector.ApplyFullNameTypeMap(Kb, map);
            AutoTypeInjector.PrimeToolAcceptsType("genexus_edit", true);

            var args = new JObject { ["name"] = "CustomerTransaction" };
            bool result = AutoTypeInjector.TryInject(Kb, "genexus_edit", args, out string injected);

            Assert.True(result);
            Assert.Equal("Transaction", injected);
            Assert.Equal("Transaction", args["type"]?.ToString());
        }

        // ── 16. Table-only name → no inject (guard still applies) ─────────────

        [Fact]
        public void ApplyFullNameTypeMap_TableOnly_NoInject()
        {
            // A name that is ONLY a Table (no Transaction sibling) stays "Table" in
            // the map; the _shadowTypesNoInject guard refuses to inject it, leaving
            // the call type-less for the worker's global resolution.
            var map = new JObject
            {
                ["PureTable"] = new JArray("Table"),
            };
            AutoTypeInjector.ApplyFullNameTypeMap(Kb, map);
            AutoTypeInjector.PrimeToolAcceptsType("genexus_edit", true);

            var args = new JObject { ["name"] = "PureTable" };
            bool result = AutoTypeInjector.TryInject(Kb, "genexus_edit", args, out _);

            Assert.False(result);
            Assert.Null(args["type"]);
        }

        // ── 17. Two source-bearing types → ambiguous, no inject ───────────────

        [Fact]
        public void ApplyFullNameTypeMap_Ambiguous_NoInject()
        {
            var map = new JObject
            {
                ["SharedName"] = new JArray("WebPanel", "Procedure"),
            };
            AutoTypeInjector.ApplyFullNameTypeMap(Kb, map);
            AutoTypeInjector.PrimeToolAcceptsType("genexus_inspect", true);

            var args = new JObject { ["name"] = "SharedName" };
            bool result = AutoTypeInjector.TryInject(Kb, "genexus_inspect", args, out _);

            Assert.False(result);
            Assert.Null(args["type"]);
        }

        // ── 17b. Attribute shadow → no inject (same class as Table) ─────────

        [Fact]
        public void AttributeShadowType_NoInject_ReturnsFalse()
        {
            // A Transaction's attributes are indexed as physical "Attribute" model
            // objects (live-confirmed: type-less genexus_edit on "GpBaseId"
            // auto-injected type=Attribute and resolved to the part-less artifact,
            // whose Source read falls back to empty Documentation). Injecting
            // "Attribute" would point object tools at a non-source-bearing object
            // — the same PatchReadFailed class as the Table shadow.
            AutoTypeInjector.PrimeIndex(Kb, new (string, string?)[]
            {
                ("GpBaseId", "Attribute"),
            });
            AutoTypeInjector.PrimeToolAcceptsType("genexus_edit", true);

            var args = new JObject { ["name"] = "GpBaseId" };
            bool result = AutoTypeInjector.TryInject(Kb, "genexus_edit", args, out _);

            Assert.False(result);
            Assert.Null(args["type"]);
        }

        // ── 18. ApplyFullNameTypeMap rebuilds wholesale (clears stale entries) ─

        [Fact]
        public void ApplyFullNameTypeMap_RebuildClearsStaleEntries()
        {
            // Prime via the recent-window path, then apply a full map that does NOT
            // contain the window-only name → the name must disappear (no inject).
            var recent = new JArray
            {
                new JObject { ["Name"] = "WindowOnly", ["Type"] = "WebPanel" },
            };
            AutoTypeInjector.RefreshFromRecentlyChanged(Kb, recent);

            AutoTypeInjector.ApplyFullNameTypeMap(Kb, new JObject
            {
                ["RealObject"] = new JArray("Procedure"),
            });
            AutoTypeInjector.PrimeToolAcceptsType("genexus_edit", true);

            var stale = new JObject { ["name"] = "WindowOnly" };
            Assert.False(AutoTypeInjector.TryInject(Kb, "genexus_edit", stale, out _));
            Assert.Null(stale["type"]);

            var fresh = new JObject { ["name"] = "RealObject" };
            Assert.True(AutoTypeInjector.TryInject(Kb, "genexus_edit", fresh, out string injected));
            Assert.Equal("Procedure", injected);
        }
    }
}
