using System;
using System.Linq;
using GxMcp.Worker.Services.Structure;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Plan 071 — issue-#59 post-save persistence verification for Group structure writes.
    // UpdateGroupStructure now re-reads the Group after Commit and compares the requested
    // membership against what actually persisted (GroupUpdateNotPersisted on mismatch).
    // CompareGroupMembership is the pure diff seam; these tests pin its semantics so a
    // regression in the diff can't silently turn a dropped write back into a false success.
    public class GroupStructureVerificationTests
    {
        // genexus_read part=GroupStructure used to return only the IsDefault property XML.
        [Fact]
        public void RenderMembers_ListsEachSubtypeWithItsSupertype()
        {
            var text = GroupStructureService.RenderMembers(new[]
            {
                ("SampleCode", "BaseCode"),
                ("SampleName", (string)null),
                ((string)null, "Ignored")
            });

            Assert.Equal("SampleCode -> BaseCode" + Environment.NewLine + "SampleName", text);
        }

        [Fact]
        public void RenderMembers_NoMembers_IsEmpty()
        {
            Assert.Equal(string.Empty, GroupStructureService.RenderMembers(null));
        }

        [Fact]
        public void CompareGroupMembership_AllRequestedPersisted_EmptyDiff()
        {
            var diff = GroupStructureService.CompareGroupMembership(
                requestedMemberNames: new[] { "orgao_exercicio_id", "unidade_id" },
                requestedRemovals: new[] { "old_attr_id" },
                actualMemberNames: new[] { "orgao_exercicio_id", "unidade_id" });

            Assert.Empty(diff["missing"] as JArray ?? new JArray());
            Assert.Empty(diff["unremoved"] as JArray ?? new JArray());
        }

        [Fact]
        public void CompareGroupMembership_MemberDroppedBySave_FlaggedMissing()
        {
            // The SDK silently dropped the member write — the diff must report it so the
            // caller can return GroupUpdateNotPersisted instead of a false GroupUpdated.
            var diff = GroupStructureService.CompareGroupMembership(
                requestedMemberNames: new[] { "orgao_exercicio_id", "unidade_id" },
                requestedRemovals: Enumerable.Empty<string>(),
                actualMemberNames: new[] { "unidade_id" });

            var missing = diff["missing"] as JArray;
            Assert.NotNull(missing);
            Assert.Single(missing!);
            Assert.Equal("orgao_exercicio_id", missing![0]!.ToString());
            Assert.Empty(diff["unremoved"] as JArray ?? new JArray());
        }

        [Fact]
        public void CompareGroupMembership_RemovalNotApplied_FlaggedUnremoved()
        {
            var diff = GroupStructureService.CompareGroupMembership(
                requestedMemberNames: Enumerable.Empty<string>(),
                requestedRemovals: new[] { "old_attr_id" },
                actualMemberNames: new[] { "old_attr_id", "kept_attr_id" });

            var unremoved = diff["unremoved"] as JArray;
            Assert.NotNull(unremoved);
            Assert.Single(unremoved!);
            Assert.Equal("old_attr_id", unremoved![0]!.ToString());
            Assert.Empty(diff["missing"] as JArray ?? new JArray());
        }

        [Fact]
        public void CompareGroupMembership_CaseInsensitive()
        {
            // Group memberships are name-matched case-insensitively in the SDK.
            var diff = GroupStructureService.CompareGroupMembership(
                requestedMemberNames: new[] { "Orgao_Exercicio_Id" },
                requestedRemovals: new[] { "OLD_ATTR_ID" },
                actualMemberNames: new[] { "orgao_exercicio_id" });

            Assert.Empty(diff["missing"] as JArray ?? new JArray());
            Assert.Empty(diff["unremoved"] as JArray ?? new JArray());
        }

        [Fact]
        public void CompareGroupMembership_NullInputs_DoNotThrow()
        {
            var diff = GroupStructureService.CompareGroupMembership(null, null, null);
            Assert.Empty(diff["missing"] as JArray ?? new JArray());
            Assert.Empty(diff["unremoved"] as JArray ?? new JArray());
        }

        [Fact]
        public void CompareGroupMembership_DeduplicatesRequestedNames()
        {
            // A payload listing the same member twice must not double-count in the diff.
            var diff = GroupStructureService.CompareGroupMembership(
                requestedMemberNames: new[] { "attr_id", "attr_id" },
                requestedRemovals: Enumerable.Empty<string>(),
                actualMemberNames: new[] { "attr_id" });

            Assert.Empty(diff["missing"] as JArray ?? new JArray());
        }

        [Fact]
        public void CompareGroupSupertypes_LinkNotPersisted_FlaggedWithActual()
        {
            // The Group part stores only the subtype id; when the subtype attribute's own
            // SuperTypeKey is not saved the member is present but points at nothing.
            var wrong = GroupStructureService.CompareGroupSupertypes(
                JArray.Parse("[{\"name\":\"ChildId\",\"subtypeOf\":\"ParentId\"},{\"name\":\"ChildName\",\"subtypeOf\":\"ParentName\"}]"),
                new System.Collections.Generic.Dictionary<string, string>
                {
                    ["ChildId"] = null,
                    ["ChildName"] = "ParentName"
                });

            Assert.Single(wrong);
            Assert.Equal("ChildId", wrong[0]!["name"]!.ToString());
            Assert.Equal("ParentId", wrong[0]!["expected"]!.ToString());
            Assert.Equal(JTokenType.Null, wrong[0]!["actual"]!.Type);
        }

        [Fact]
        public void CompareGroupSupertypes_AllLinked_CaseInsensitive_Empty()
        {
            var wrong = GroupStructureService.CompareGroupSupertypes(
                JArray.Parse("[{\"name\":\"childid\",\"subtypeOf\":\"PARENTID\"}]"),
                new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
                {
                    ["ChildId"] = "ParentId"
                });

            Assert.Empty(wrong);
        }

        [Fact]
        public void CompareGroupSupertypes_MemberAbsentOrNullInputs_NotReportedHere()
        {
            // Absent members are CompareGroupMembership's "missing", not a supertype mismatch.
            Assert.Empty(GroupStructureService.CompareGroupSupertypes(
                JArray.Parse("[{\"name\":\"ChildId\",\"subtypeOf\":\"ParentId\"}]"),
                new System.Collections.Generic.Dictionary<string, string>()));
            Assert.Empty(GroupStructureService.CompareGroupSupertypes(null, null));
        }
    }
}
