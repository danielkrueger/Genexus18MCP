using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// W6: an agent cannot know which classes a KB defines, so it guesses. The catalog
    /// lists them. It must NOT present a class object's GUID as the value a layout
    /// <c>class</c> attribute takes — a measured layout attribute matched none of the
    /// KB's class GUIDs — and it must fail closed rather than answer with a partial list
    /// that looks complete.
    /// </summary>
    public class ThemeClassCatalogTests
    {
        private static SearchIndex IndexWith(params KeyValuePair<string, string>[] classes)
        {
            var index = new SearchIndex();
            foreach (var c in classes)
            {
                index.Objects["ThemeClass:" + c.Key] = new SearchIndex.IndexEntry
                {
                    Name = c.Key,
                    Type = "ThemeClass",
                    Guid = c.Value
                };
            }
            return index;
        }

        [Fact]
        public void BuildFromIndex_ReadsEveryThemeClassEntry()
        {
            // Live shape on GeneXus 18 / KBTeste: the index holds 129 ThemeClass
            // entries while a model walk sees only the theme root.
            var index = IndexWith(
                new KeyValuePair<string, string>("Animation", "ad0729cb-5dfa-4c77-a2b5-f8570977fd3b"),
                new KeyValuePair<string, string>("ActionGroup", "e1cfcfce-7c09-4709-946f-723697315299"));

            var result = ThemeClassCatalog.BuildFromIndex(index);

            Assert.Null(result.UnavailableReason);
            Assert.Equal(2, result.Classes.Count);
            Assert.Equal(2, result.TotalCount);
            // Sorted by name so the response is stable across calls.
            Assert.Equal("ActionGroup", result.Classes[0].Name);
            Assert.Equal("e1cfcfce-7c09-4709-946f-723697315299", result.Classes[0].ObjectGuid);
            Assert.Equal("Animation", result.Classes[1].Name);
            Assert.Equal("ad0729cb-5dfa-4c77-a2b5-f8570977fd3b", result.Classes[1].ObjectGuid);
        }

        [Fact]
        public void BuildFromIndex_DoesNotDependOnTheTypeIndexBucket()
        {
            // TypeIndex is a candidate set whose storage-key format is internal to
            // IndexCacheService. A key-based read resolved to 1 entry where the KB has
            // 129, so the catalog must read the entries themselves. This fake has no
            // TypeIndex at all — a bucket-dependent implementation returns
            // "unavailable" here and fails.
            var result = ThemeClassCatalog.BuildFromIndex(
                IndexWith(new KeyValuePair<string, string>("TableDragging", "563f97e8-082b-592f-99f3-1ffb9505e43a")));

            Assert.Null(result.UnavailableReason);
            Assert.Single(result.Classes);
        }

        [Fact]
        public void BuildFromIndex_IgnoresOtherTypes()
        {
            var index = new SearchIndex();
            index.Objects["Transaction:Aluno"] = new SearchIndex.IndexEntry
            {
                Name = "Aluno", Type = "Transaction", Guid = "guid-aluno"
            };
            index.Objects["ThemeClass:Menu"] = new SearchIndex.IndexEntry
            {
                Name = "Menu", Type = "ThemeClass", Guid = "guid-menu"
            };

            var result = ThemeClassCatalog.BuildFromIndex(index);

            Assert.Single(result.Classes);
            Assert.Equal("Menu", result.Classes[0].Name);
        }

        [Fact]
        public void BuildFromIndex_TruncatesButStillReportsTheTrueTotal()
        {
            // A caller must be able to say "here are 3 of 129" instead of implying the
            // KB defines three classes.
            var index = new SearchIndex();
            for (int i = 0; i < 10; i++)
            {
                index.Objects["ThemeClass:Class" + i] = new SearchIndex.IndexEntry
                {
                    Name = "Class" + i.ToString("00"), Type = "ThemeClass", Guid = "guid" + i
                };
            }

            var result = ThemeClassCatalog.BuildFromIndex(index, limit: 3);

            Assert.Equal(3, result.Classes.Count);
            Assert.Equal(10, result.TotalCount);
        }

        [Fact]
        public void BuildFromIndex_LimitCutsDeterministically()
        {
            var index = IndexWith(
                new KeyValuePair<string, string>("Zeta", "guid-z"),
                new KeyValuePair<string, string>("Alpha", "guid-a"),
                new KeyValuePair<string, string>("Mu", "guid-m"));

            var result = ThemeClassCatalog.BuildFromIndex(index, limit: 2);

            Assert.Equal(new[] { "Alpha", "Mu" }, new[] { result.Classes[0].Name, result.Classes[1].Name });
        }

        [Fact]
        public void BuildFromIndex_NonPositiveLimit_StillReturnsSomething()
        {
            var result = ThemeClassCatalog.BuildFromIndex(
                IndexWith(new KeyValuePair<string, string>("Menu", "guid-menu")), limit: 0);

            Assert.Single(result.Classes);
        }

        [Fact]
        public void BuildFromIndex_NullIndex_ReportsUnavailable()
        {
            var result = ThemeClassCatalog.BuildFromIndex(null);

            Assert.Contains("index", result.UnavailableReason);
            Assert.Empty(result.Classes);
        }

        [Fact]
        public void BuildFromIndex_IndexWithoutThemeClasses_ReportsUnavailableNotAnEmptyCatalog()
        {
            // An empty list would read as "this KB has no styling" and an agent would
            // author a class reference that does not exist.
            var result = ThemeClassCatalog.BuildFromIndex(new SearchIndex());

            Assert.NotNull(result.UnavailableReason);
            Assert.Empty(result.Classes);
        }

        [Fact]
        public void BuildFromIndex_SkipsUnnamedEntries()
        {
            var index = new SearchIndex();
            index.Objects["ThemeClass:nameless"] = new SearchIndex.IndexEntry
            {
                Type = "ThemeClass", Guid = "guid-x"
            };

            var result = ThemeClassCatalog.BuildFromIndex(index);

            Assert.Empty(result.Classes);
            Assert.NotNull(result.UnavailableReason);
        }
    }

    public class ThemeClassCatalogRoutingTests
    {
        [Fact]
        public void TheCatalogIsItsOwnAnalyzeModeNotAControlListFlag()
        {
            // It reads the search index; the control list reads
            // IUserControlsManagerService. Folding one into the other made the response
            // shape depend on a boolean and left one service answering two unrelated
            // questions, so the catalog moved to genexus_analyze mode=theme_classes.
            var analyze = ReadToolDefinition("genexus_analyze");
            var modes = (Newtonsoft.Json.Linq.JArray)analyze["inputSchema"]!["properties"]!["mode"]!["enum"]!;
            Assert.Contains("theme_classes", modes.Select(m => m.ToString()));

            var layout = ReadToolDefinition("genexus_layout");
            Assert.Null(layout["inputSchema"]!["properties"]!["includeThemeClasses"]);

            // The control list keeps exactly one response shape again.
            var controls = (Newtonsoft.Json.Linq.JArray)layout["inputSchema"]!["properties"]!["action"]!["enum"]!;
            Assert.Contains("list_controls", controls.Select(a => a.ToString()));
        }

        private static Newtonsoft.Json.Linq.JObject ReadToolDefinition(string tool)
        {
            string root = RepoSource.PathOf("src", "GxMcp.Gateway", "tool_definitions.json");
            var all = Newtonsoft.Json.Linq.JArray.Parse(System.IO.File.ReadAllText(root));
            return (Newtonsoft.Json.Linq.JObject)all.First(t => (string)t["name"]! == tool);
        }

    }
}
