using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class CacheRevisionTests
    {
        [Fact]
        public void AbsoluteTtl_DoesNotExtendWhenEntryIsHit()
        {
            long now = 0;
            var store = new SemanticCacheStore(8, TimeSpan.FromMilliseconds(10), () => now);
            store.Set("kb1|genexus_read:{}", new JObject { ["ok"] = true });

            now = 9;
            Assert.True(store.TryGet("kb1|genexus_read:{}", out _));

            now = 10;
            Assert.False(store.TryGet("kb1|genexus_read:{}", out _));
        }

        [Fact]
        public void InvalidateScope_AdvancesOnlyAffectedKbGeneration()
        {
            var store = new SemanticCacheStore(8, TimeSpan.FromMinutes(30));
            store.Set("kb1|genexus_query:{}", new JObject { ["kb"] = 1 });
            store.Set("kb2|genexus_query:{}", new JObject { ["kb"] = 2 });

            long before = store.GetRevision("KB1");
            long after = store.InvalidateScope("KB1", out int removed);

            Assert.Equal(before + 1, after);
            Assert.Equal(1, removed);
            Assert.Equal(after, store.GetRevision("kb1"));
            Assert.False(store.TryGet("kb1|genexus_query:{}", out _));
            Assert.True(store.TryGet("kb2|genexus_query:{}", out _));
            Assert.Equal(0, store.GetRevision("kb2"));
        }

        [Fact]
        public void ClearScope_AlsoAdvancesGenerationForDirectCallers()
        {
            var store = new SemanticCacheStore(8, TimeSpan.FromMinutes(30));
            store.Set("kb1|genexus_query:{}", new JObject());

            Assert.Equal(1, store.ClearScope("KB1"));
            Assert.Equal(1, store.GetRevision("kb1"));
            Assert.False(store.TryGet("kb1|genexus_query:{}", out _));
        }

        [Fact]
        public void CanonicalKey_SortsObjectArgumentsAndIncludesIdentityAndRevision()
        {
            var first = new JObject { ["limit"] = 10, ["filter"] = new JObject { ["b"] = 2, ["a"] = 1 } };
            var second = new JObject { ["filter"] = new JObject { ["a"] = 1, ["b"] = 2 }, ["limit"] = 10 };

            string? firstKey = Program.CreateSemanticCacheKey(
                "KB1", "GENEXUS_QUERY", first, false, false, 4, "model-v2", "development");
            string? secondKey = Program.CreateSemanticCacheKey(
                "kb1", "genexus_query", second, false, false, 4, "MODEL-V2", "Development");
            string? nextRevisionKey = Program.CreateSemanticCacheKey(
                "kb1", "genexus_query", second, false, false, 5, "model-v2", "development");

            Assert.NotNull(firstKey);
            Assert.Equal(firstKey, secondKey);
            Assert.NotEqual(firstKey, nextRevisionKey);
            Assert.Contains("|kb1|gen=4|", firstKey);
            Assert.Contains("|model=model-v2|env=development", firstKey);
        }

        [Fact]
        public void DoctorNeverUsesTheSemanticCache()
        {
            Assert.True(Program.IsLiveToolForCache("genexus_doctor", null));
            Assert.True(Program.IsLiveToolForCache("genexus_lifecycle", "status"));
            Assert.False(Program.IsLiveToolForCache("genexus_query", null));
        }

        // ---- Issue #376: a cached notModified outlived the edit it claimed had not happened.

        private static string? ReadKey(JObject args) => Program.CreateSemanticCacheKey(
            "kb1", "genexus_read", args, false, false, 1, null, null);

        [Fact]
        public void A_Conditional_Read_Never_Reaches_The_Semantic_Cache()
        {
            // The Worker's revision stamp is what makes `notModified` provable. Answering
            // it from the Gateway cache skips that check, so an edit this Gateway never
            // observed left a stale "unchanged" in place for the whole 30-minute TTL.
            Assert.Null(ReadKey(new JObject
            {
                ["name"] = "SyntheticOrder",
                ["part"] = "Source",
                ["ifUnchangedSince"] = "content-token-T"
            }));
        }

        [Fact]
        public void An_Unconditional_Read_Keeps_Its_Cache_Entry()
        {
            Assert.NotNull(ReadKey(new JObject { ["name"] = "SyntheticOrder", ["part"] = "Source" }));
        }

        [Fact]
        public void An_Empty_Or_Absent_Token_Leaves_The_Read_Cacheable()
        {
            // The classifier keys on "this call carries a token", so a client that sends
            // an empty string still gets the ordinary cached behaviour rather than
            // silently losing its cache.
            Assert.NotNull(ReadKey(new JObject { ["name"] = "SyntheticOrder", ["part"] = "Source", ["ifUnchangedSince"] = "" }));
            Assert.NotNull(ReadKey(new JObject { ["name"] = "SyntheticOrder", ["part"] = "Source", ["ifUnchangedSince"] = JValue.CreateNull() }));
        }

        [Fact]
        public void A_Token_On_Another_Tool_Is_Not_A_Conditional_Read()
        {
            // Only genexus_read defines this shape; a same-named argument elsewhere is not
            // evidence that the caller is asking for a provable freshness claim.
            Assert.NotNull(Program.CreateSemanticCacheKey(
                "kb1", "genexus_query", new JObject { ["ifUnchangedSince"] = "token" },
                false, false, 1, null, null));
        }

        [Fact]
        public void The_Legacy_Key_Builder_Applies_The_Same_Rule()
        {
            // The 5-arg overload feeds the classifier tests; a rule that lived only in the
            // dispatch overload would leave that path able to key a conditional read.
            Assert.Null(Program.CreateSemanticCacheKey(
                "kb1", "genexus_read", new JObject { ["ifUnchangedSince"] = "token" }, false, false));
            Assert.NotNull(Program.CreateSemanticCacheKey(
                "kb1", "genexus_read", new JObject { ["name"] = "SyntheticOrder" }, false, false));
        }

        [Fact]
        public void A_Conditional_Read_Answered_By_A_Cached_Unconditional_Read_Still_Cannot_Claim_Not_Modified()
        {
            // An unconditional read is still cached, and it hands back a contentToken
            // describing the revision it actually served. Replaying it is safe precisely
            // because that token stops matching: the next conditional read reaches the
            // Worker, mismatches, and returns the fresh body. Documented here so the
            // choice is a decision rather than an omission.
            string? key = ReadKey(new JObject { ["name"] = "SyntheticOrder", ["part"] = "Source" });
            Assert.NotNull(key);

            var store = new SemanticCacheStore(8, TimeSpan.FromMinutes(30));
            store.Set(key!, new JObject { ["source"] = "// stale", ["contentToken"] = "token-for-stale-revision" });
            Assert.True(store.TryGet(key!, out var cached));

            // The replayed token is the stale revision's, so it cannot certify anything.
            Assert.Equal("token-for-stale-revision", cached!["contentToken"]!.ToString());
            Assert.True(Program.IsConditionalRead("genexus_read", new JObject { ["ifUnchangedSince"] = "token-for-stale-revision" }));
        }

        [Fact]
        public void CanonicalKey_PreservesArrayOrder()
        {
            var first = Program.CreateSemanticCacheKey(
                "kb1", "genexus_query", new JObject { ["targets"] = new JArray("A", "B") }, false, false,
                1, null, null);
            var reordered = Program.CreateSemanticCacheKey(
                "kb1", "genexus_query", new JObject { ["targets"] = new JArray("B", "A") }, false, false,
                1, null, null);

            Assert.NotEqual(first, reordered);
        }

        [Fact]
        public void DispatchKey_CanonicalizesArgumentsAtInitialGeneration()
        {
            var first = Program.CreateSemanticCacheKey(
                "KB1", "GENEXUS_QUERY", new JObject { ["b"] = 2, ["a"] = 1 }, false, false,
                0, null, null);
            var reordered = Program.CreateSemanticCacheKey(
                "kb1", "genexus_query", new JObject { ["a"] = 1, ["b"] = 2 }, false, false,
                0, null, null);

            Assert.Equal(first, reordered);
            Assert.Contains("|kb1|gen=0|", first);
            Assert.Contains("|model=|env=", first);
        }

        [Fact]
        public void ExternalWatcherScope_ResolvesConfiguredAliasByCanonicalPath()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-cache-kb");
            var config = new Configuration
            {
                Environment = new EnvironmentConfig
                {
                    KBs = new System.Collections.Generic.List<KbEntry>
                    {
                        new KbEntry { Alias = "Sales", Path = root + Path.DirectorySeparatorChar }
                    }
                }
            };

            Assert.Equal("sales", Program.ResolveConfiguredKbAlias(config, root));
            Assert.Null(Program.ResolveConfiguredKbAlias(config, Path.Combine(root, "other")));
        }
    }
}
