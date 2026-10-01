using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // #354: genexus_kb_diff used max-mtime + part count as a change proxy, so it
    // reported an unqualified Success for two states that are not "no differences":
    // an unreadable/absent inventory (indistinguishable from a compared empty model)
    // and different bytes behind identical metadata. The helper now compares SHA-256
    // per part and states inventory authority/completeness on every call.
    public class KbDiffHelperTests : IDisposable
    {
        private readonly string _root;

        public KbDiffHelperTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxmcp-kbdiff-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private string CreateKb(string name)
        {
            string p = Path.Combine(_root, name);
            Directory.CreateDirectory(Path.Combine(p, "Objects"));
            return p;
        }

        /// <summary>A KB root with no Objects/ directory at all - the reproduced case 1.</summary>
        private string CreateBareDirectory(string name)
        {
            string p = Path.Combine(_root, name);
            Directory.CreateDirectory(p);
            return p;
        }

        private void AddObject(string kb, string type, string objName, string content = "x")
        {
            string dir = Path.Combine(kb, "Objects", type, objName);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "part.xml"), content);
        }

        private static void PinMtime(string kb, string type, string name, DateTime utc) =>
            File.SetLastWriteTimeUtc(Path.Combine(kb, "Objects", type, name, "part.xml"), utc);

        [Fact]
        public void Identical_Kbs_Produce_Empty_Diff()
        {
            string a = CreateKb("a");
            string b = CreateKb("b");
            AddObject(a, "WebPanel", "Home");
            AddObject(b, "WebPanel", "Home");
            // Set identical mtimes so the modified detector doesn't fire on tiny mtime jitter.
            var t = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(Path.Combine(a, "Objects", "WebPanel", "Home", "part.xml"), t);
            File.SetLastWriteTimeUtc(Path.Combine(b, "Objects", "WebPanel", "Home", "part.xml"), t);

            var diff = KbDiffHelper.Diff(a, b);
            Assert.Equal("Success", (string)diff["status"]);
            Assert.True((bool)diff["complete"]);
            Assert.Empty((JArray)diff["onlyInA"]!);
            Assert.Empty((JArray)diff["onlyInB"]!);
            Assert.Empty((JArray)diff["modified"]!);
            Assert.Empty((JArray)diff["metadataOnly"]!);
            Assert.Equal(1, (int)diff["countA"]!);
            Assert.Equal(1, (int)diff["countB"]!);
            Assert.Equal("sha256-raw-part-bytes", (string)diff["contentComparison"]!);
        }

        [Fact]
        public void Disjoint_Kbs_Everything_Is_OnlyIn()
        {
            string a = CreateKb("a");
            string b = CreateKb("b");
            AddObject(a, "Procedure", "ProcA");
            AddObject(a, "WebPanel", "Home");
            AddObject(b, "Transaction", "TrnB");

            var diff = KbDiffHelper.Diff(a, b);
            var onlyA = (JArray)diff["onlyInA"]!;
            var onlyB = (JArray)diff["onlyInB"]!;
            Assert.Equal(2, onlyA.Count);
            Assert.Single(onlyB);
            Assert.Empty((JArray)diff["modified"]!);
            Assert.Contains(onlyA, t => t.ToString() == "Procedure:ProcA");
            Assert.Contains(onlyA, t => t.ToString() == "WebPanel:Home");
            Assert.Contains(onlyB, t => t.ToString() == "Transaction:TrnB");
        }

        [Fact]
        public void Partial_Overlap_With_Modified_Object()
        {
            string a = CreateKb("a");
            string b = CreateKb("b");
            AddObject(a, "WebPanel", "Shared", "&Amount = 10");
            AddObject(b, "WebPanel", "Shared", "&Amount = 20");
            AddObject(a, "WebPanel", "OnlyInA");
            AddObject(b, "Procedure", "OnlyInB");

            var diff = KbDiffHelper.Diff(a, b);
            Assert.Single((JArray)diff["onlyInA"]!);
            Assert.Single((JArray)diff["onlyInB"]!);
            var mod = (JArray)diff["modified"]!;
            Assert.Single(mod);
            Assert.Equal("Shared", mod[0]!["name"]!.ToString());
            Assert.Equal("WebPanel", mod[0]!["type"]!.ToString());
            Assert.Equal("contentDiffers", (string)mod[0]!["reason"]!);
            var changed = (JArray)mod[0]!["changedParts"]!;
            Assert.Single(changed);
            Assert.Equal("part.xml", (string)changed[0]!["part"]!);
        }

        // ---- #354 reproduced case 1: absent inventory is not an empty model ----

        [Fact]
        public void Missing_Objects_Directory_Is_Unavailable_Not_A_Successful_Empty_Comparison()
        {
            string a = CreateBareDirectory("alpha");
            string b = CreateBareDirectory("beta");

            var diff = KbDiffHelper.Diff(a, b);

            Assert.False((bool)diff["complete"]!);
            Assert.Equal("Incomplete", (string)diff["status"]);
            Assert.Equal("unavailable", (string)diff["inventoryA"]!["state"]!);
            Assert.Equal("unavailable", (string)diff["inventoryB"]!["state"]!);
            Assert.False(string.IsNullOrWhiteSpace((string)diff["inventoryA"]!["reason"]!));
            // The dangerous part: an unqualified empty difference list.
            Assert.Empty((JArray)diff["onlyInA"]!);
            Assert.Empty((JArray)diff["onlyInB"]!);
            Assert.Empty((JArray)diff["modified"]!);
        }

        [Fact]
        public void Missing_Objects_Directory_On_One_Side_Is_Not_Silently_Symmetric()
        {
            string a = CreateBareDirectory("alpha");
            string b = CreateKb("beta");
            AddObject(b, "Procedure", "ProcB");

            var diff = KbDiffHelper.Diff(a, b);

            Assert.False((bool)diff["complete"]!);
            Assert.Equal("unavailable", (string)diff["inventoryA"]!["state"]!);
            Assert.Equal("complete", (string)diff["inventoryB"]!["state"]!);
        }

        [Fact]
        public void Proven_Empty_Objects_Directory_Reports_Empty_And_Is_Complete()
        {
            string a = CreateKb("a");
            string b = CreateKb("b");

            var diff = KbDiffHelper.Diff(a, b);

            Assert.Equal("empty", (string)diff["inventoryA"]!["state"]!);
            Assert.Equal("empty", (string)diff["inventoryB"]!["state"]!);
            Assert.True((bool)diff["complete"]!);
            Assert.Equal("Success", (string)diff["status"]);
        }

        // ---- #354 reproduced case 2: equal metadata, different bytes ----

        [Fact]
        public void Equal_Timestamps_And_Part_Counts_With_Different_Content_Is_A_Change()
        {
            string a = CreateKb("a");
            string b = CreateKb("b");
            AddObject(a, "Procedure", "SyntheticOrder", "&Amount = 10");
            AddObject(b, "Procedure", "SyntheticOrder", "&Amount = 20");
            var t = DateTime.UtcNow;
            PinMtime(a, "Procedure", "SyntheticOrder", t);
            PinMtime(b, "Procedure", "SyntheticOrder", t);

            var diff = KbDiffHelper.Diff(a, b);

            Assert.Equal("Success", (string)diff["status"]);
            var mod = (JArray)diff["modified"]!;
            Assert.Single(mod);
            Assert.Equal("Procedure:SyntheticOrder", (string)mod[0]!["key"]!);
            Assert.Equal(1, (int)diff["countA"]!);
            Assert.Equal(1, (int)diff["countB"]!);
        }

        [Fact]
        public void Equal_Content_With_Different_Timestamps_Is_Not_A_Content_Change()
        {
            string a = CreateKb("a");
            string b = CreateKb("b");
            AddObject(a, "Procedure", "SyntheticOrder", "&Amount = 10");
            AddObject(b, "Procedure", "SyntheticOrder", "&Amount = 10");
            PinMtime(a, "Procedure", "SyntheticOrder", DateTime.UtcNow.AddSeconds(-60));
            PinMtime(b, "Procedure", "SyntheticOrder", DateTime.UtcNow);

            var diff = KbDiffHelper.Diff(a, b);

            Assert.Empty((JArray)diff["modified"]!);
            var meta = (JArray)diff["metadataOnly"]!;
            Assert.Single(meta);
            Assert.Equal("equalContentDifferentTimestamp", (string)meta[0]!["reason"]!);
        }

        [Fact]
        public void Renamed_Part_With_Identical_Bytes_Is_A_Change()
        {
            string a = CreateKb("a");
            string b = CreateKb("b");
            Directory.CreateDirectory(Path.Combine(a, "Objects", "WebPanel", "Home"));
            File.WriteAllText(Path.Combine(a, "Objects", "WebPanel", "Home", "Source.txt"), "same");
            Directory.CreateDirectory(Path.Combine(b, "Objects", "WebPanel", "Home"));
            File.WriteAllText(Path.Combine(b, "Objects", "WebPanel", "Home", "Source.xml"), "same");

            var diff = KbDiffHelper.Diff(a, b);

            var mod = (JArray)diff["modified"]!;
            Assert.Single(mod);
            var changed = (JArray)mod[0]!["changedParts"]!;
            Assert.Equal(2, changed.Count);
        }

        [Fact]
        public void Extra_Part_File_Only_On_One_Side_Is_A_Change()
        {
            string a = CreateKb("a");
            string b = CreateKb("b");
            AddObject(a, "WebPanel", "Home", "same");
            AddObject(b, "WebPanel", "Home", "same");
            File.WriteAllText(Path.Combine(a, "Objects", "WebPanel", "Home", "Extra.txt"), "extra");

            var diff = KbDiffHelper.Diff(a, b);

            var mod = (JArray)diff["modified"]!;
            Assert.Single(mod);
            var changed = (JArray)mod[0]!["changedParts"]!;
            Assert.Equal("Extra.txt", (string)changed[0]!["part"]!);
            Assert.True((bool)changed[0]!["inA"]!);
            Assert.False((bool)changed[0]!["inB"]!);
        }

        // ---- explicit metadata mode: labelled, and known to miss content changes ----

        [Fact]
        public void Metadata_Mode_Keeps_The_Heuristic_But_Labels_It_And_Reports_It_Misses_Content()
        {
            string a = CreateKb("a");
            string b = CreateKb("b");
            AddObject(a, "Procedure", "SyntheticOrder", "&Amount = 10");
            AddObject(b, "Procedure", "SyntheticOrder", "&Amount = 20");
            var t = DateTime.UtcNow;
            PinMtime(a, "Procedure", "SyntheticOrder", t);
            PinMtime(b, "Procedure", "SyntheticOrder", t);

            var diff = KbDiffHelper.Diff(a, b, KbDiffHelper.MetadataMode, KbDiffHelper.DefaultMaxObjects);

            Assert.Equal("filesystem-metadata-heuristic", (string)diff["contentComparison"]!);
            Assert.Empty((JArray)diff["modified"]!);
            Assert.Contains("NOT a content comparison", diff["notes"]!.ToString());
        }

        [Fact]
        public void Metadata_Mode_Still_Detects_A_Timestamp_Change()
        {
            string a = CreateKb("a");
            string b = CreateKb("b");
            AddObject(a, "Procedure", "SyntheticOrder", "same");
            AddObject(b, "Procedure", "SyntheticOrder", "same");
            PinMtime(a, "Procedure", "SyntheticOrder", DateTime.UtcNow.AddSeconds(-600));
            PinMtime(b, "Procedure", "SyntheticOrder", DateTime.UtcNow);

            var diff = KbDiffHelper.Diff(a, b, KbDiffHelper.MetadataMode, KbDiffHelper.DefaultMaxObjects);

            Assert.Single((JArray)diff["modified"]!);
        }

        // ---- unreadable object directories must not become phantom differences ----

        [Fact]
        public void Object_Directory_With_No_Readable_Parts_Makes_Inventory_Incomplete_Not_A_Phantom_Key()
        {
            string a = CreateKb("a");
            string b = CreateKb("b");
            AddObject(a, "Procedure", "Readable", "x");
            AddObject(b, "Procedure", "Readable", "x");
            Directory.CreateDirectory(Path.Combine(a, "Objects", "Procedure", "Ghost"));

            var diff = KbDiffHelper.Diff(a, b);

            Assert.False((bool)diff["complete"]!);
            Assert.Equal("incomplete", (string)diff["inventoryA"]!["state"]!);
            var unreadable = (JArray)diff["inventoryA"]!["unreadableObjects"]!;
            Assert.Single(unreadable);
            Assert.Equal("Procedure:Ghost", (string)unreadable[0]!["key"]!);
            // A ghost must not be reported as onlyInA: nothing proved it exists.
            Assert.DoesNotContain((JArray)diff["onlyInA"]!, t => t.ToString() == "Procedure:Ghost");
        }

        [Fact]
        public void Exceeding_MaxObjects_Truncates_Honestly()
        {
            string a = CreateKb("a");
            string b = CreateKb("b");
            for (int i = 0; i < 4; i++) AddObject(a, "Procedure", "P" + i, "x");
            for (int i = 0; i < 4; i++) AddObject(b, "Procedure", "P" + i, "x");

            var diff = KbDiffHelper.Diff(a, b, KbDiffHelper.ContentMode, 2);

            Assert.False((bool)diff["complete"]!);
            Assert.Equal("Incomplete", (string)diff["status"]);
            Assert.True((bool)diff["inventoryA"]!["truncated"]!);
            Assert.True((bool)diff["inventoryB"]!["truncated"]!);
        }

        [Fact]
        public void Identity_Authority_Is_Declared_And_Never_Claims_Module_Qualification()
        {
            string a = CreateKb("a");
            string b = CreateKb("b");

            var diff = KbDiffHelper.Diff(a, b);

            Assert.Equal("filesystem-directory-names", (string)diff["identityAuthority"]!);
            Assert.False((bool)diff["moduleQualified"]!);
        }

        [Fact]
        public void Homonymous_Objects_In_Different_Types_Are_Distinct_Keys()
        {
            string a = CreateKb("a");
            string b = CreateKb("b");
            AddObject(a, "Procedure", "Synthetic", "fromA");
            AddObject(b, "Procedure", "Synthetic", "fromB");
            AddObject(a, "WebPanel", "Synthetic", "fromA");
            AddObject(b, "WebPanel", "Synthetic", "fromA");

            var diff = KbDiffHelper.Diff(a, b);

            var mod = (JArray)diff["modified"]!;
            Assert.Single(mod);
            Assert.Equal("Procedure:Synthetic", (string)mod[0]!["key"]!);
        }
    }
}
