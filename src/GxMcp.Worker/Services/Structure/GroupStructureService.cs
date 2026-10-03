using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services.Structure
{
    // issue #54: SubType Group membership. A Group is a KBObject whose GroupStructurePart holds
    // a BaseCollection<GroupMember>; each member binds a subtype Attribute to its supertype
    // (GroupMember.Supertype setter writes Subtype.SuperType — one call registers the member
    // AND asserts the attribute's subtype link, exactly like the IDE's Group editor).
    public class GroupStructureService
    {
        private readonly ObjectService _objectService;

        public GroupStructureService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        // payload = { members?: [{ name, subtypeOf }], remove?: [subtypeName] }
        // members upsert by subtype name; the subtype's SuperType is (re)asserted.
        public string UpdateGroupStructure(string groupName, string payload)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(payload)) return Models.McpResponse.Err(
                    code: "InvalidPayload", message: "payload is required.",
                    hint: "e.g. { \"members\": [ { \"name\": \"orgao_exercicio_id\", \"subtypeOf\": \"exercicio_id\" } ] }.",
                    target: groupName);

                var obj = _objectService.FindObject(groupName);
                if (obj == null) return HealingService.FormatNotFoundError(groupName, _objectService.GetKbService().GetIndexCache().GetIndex());

                if (!(obj is Group group)) return Models.McpResponse.Err(
                    code: "NotAGroup",
                    message: $"Object '{groupName}' is not a Group.",
                    hint: "Create one with genexus_create type=Group first.",
                    target: groupName);

                var json = JObject.Parse(payload);
                var members = json["members"] as JArray;
                var remove = json["remove"] as JArray;
                if ((members == null || members.Count == 0) && (remove == null || remove.Count == 0))
                    return Models.McpResponse.Err(
                        code: "NoMembersToApply",
                        message: "payload contained no members to add or remove.",
                        hint: "Pass members:[{name,subtypeOf}] to upsert, remove:[\"name\"] to detach.",
                        target: groupName);

                var part = group.Parts.Get<GroupStructurePart>();
                if (part == null) return Models.McpResponse.Err(
                    code: "GroupStructurePartMissing",
                    message: $"Group '{groupName}' has no GroupStructure part.",
                    target: groupName);

                var applied = new JArray();
                var removed = new JArray();
                // The Group part serializes only each member's subtype id; the supertype link
                // lives on the subtype Attribute (SuperTypeKey), so those attributes need their
                // own save or the link is lost when the Group is re-read.
                var relinked = new System.Collections.Generic.List<Artech.Genexus.Common.Objects.Attribute>();

                using (var sdkTrans = group.Model.KB.BeginTransaction())
                {
                    try
                    {
                        if (members != null)
                        {
                            foreach (var m in members)
                            {
                                string name = m["name"]?.ToString();
                                string superName = m["subtypeOf"]?.ToString();
                                if (string.IsNullOrWhiteSpace(name)) { sdkTrans.Rollback(); return Models.McpResponse.Err(
                                    code: "MemberNameRequired", message: "Each member needs a 'name' (the subtype attribute).", target: groupName); }
                                if (string.IsNullOrWhiteSpace(superName)) { sdkTrans.Rollback(); return Models.McpResponse.Err(
                                    code: "MemberSubtypeOfRequired", message: $"Member '{name}' needs 'subtypeOf' (the supertype attribute).", target: groupName); }
                                if (name.Equals(superName, StringComparison.OrdinalIgnoreCase)) { sdkTrans.Rollback(); return Models.McpResponse.Err(
                                    code: "CyclicSubtype", message: $"Member '{name}' cannot be its own supertype.", target: groupName); }

                                var subtype = Artech.Genexus.Common.Objects.Attribute.Get(group.Model, name);
                                if (subtype == null) { sdkTrans.Rollback(); return Models.McpResponse.Err(
                                    code: "MemberAttributeNotFound",
                                    message: $"Subtype attribute '{name}' does not exist in the KB.",
                                    hint: "Create it inside a Transaction structure first (genexus_edit part=Structure).",
                                    target: groupName); }
                                var super = Artech.Genexus.Common.Objects.Attribute.Get(group.Model, superName);
                                if (super == null) { sdkTrans.Rollback(); return Models.McpResponse.Err(
                                    code: "SupertypeNotFound",
                                    message: $"Supertype attribute '{superName}' does not exist in the KB.",
                                    hint: "A subtype must point at an existing base attribute.",
                                    target: groupName); }

                                var existing = part.Members.FirstOrDefault(mm => mm.Subtype != null
                                    && string.Equals(mm.Subtype.Name, name, StringComparison.OrdinalIgnoreCase));
                                if (existing == null)
                                {
                                    existing = new GroupMember(part) { Subtype = subtype };
                                    part.Members.Add(existing);
                                }
                                if (!SameKey(existing.Subtype.SuperTypeKey, super.Key))
                                {
                                    existing.Supertype = super; // writes Subtype.SuperType
                                    relinked.Add(existing.Subtype);
                                }
                                applied.Add(new JObject { ["name"] = name, ["subtypeOf"] = superName });
                            }
                        }

                        if (remove != null)
                        {
                            foreach (string rn in remove.Select(r => r.ToString()))
                            {
                                var victim = part.Members.FirstOrDefault(mm => mm.Subtype != null
                                    && string.Equals(mm.Subtype.Name, rn, StringComparison.OrdinalIgnoreCase));
                                if (victim == null) { sdkTrans.Rollback(); return Models.McpResponse.Err(
                                    code: "MemberNotFound",
                                    message: $"'{rn}' is not a member of Group '{groupName}'.",
                                    hint: "Read members with genexus_structure action=get_visual.",
                                    target: groupName); }
                                part.Members.Remove(victim);
                                removed.Add(rn);
                            }
                        }

                        group.EnsureSave();
                        foreach (var att in relinked) att.EnsureSave();
                        sdkTrans.Commit();
                        WriteService.NotePerTargetWrite(groupName);

                        try { _objectService.GetKbService().GetIndexCache().UpdateEntry(group); }
                        catch (Exception iex) { Logger.Warn("[GroupStructureService] index UpdateEntry failed: " + iex.Message); }

                        // issue #59 (plan 071) — post-save persistence verification. Re-find
                        // the Group (fresh instance, not the mutated one) and confirm every
                        // requested member add/removal actually persisted. A save the SDK
                        // silently drops (the issue-#59 class) returns GroupUpdateNotPersisted
                        // instead of a false GroupUpdated success.
                        string verifyErr = VerifyGroupMembersPersisted(groupName, applied, removed, group, out bool membershipVerified);
                        if (verifyErr != null) return verifyErr;

                        var okResult = new JObject
                        {
                            ["group"] = group.Name,
                            ["members"] = applied,
                            ["removed"] = removed
                        };
                        // Honest flag: only claim the write was verified when a fresh re-read
                        // actually compared the membership. When the SDK returns the same
                        // in-memory instance (or none) the re-read is meaningless — surface
                        // that instead of a false persistedVerified:true.
                        if (membershipVerified) okResult["persistedVerified"] = true;
                        else okResult["verificationNote"] = "Post-save re-read returned the same in-memory Group instance (or none) — the membership write could not be independently confirmed; re-read with genexus_structure action=get_visual if in doubt.";
                        return Models.McpResponse.Ok(
                            target: groupName,
                            code: "GroupUpdated",
                            result: okResult);
                    }
                    catch (Exception ex)
                    {
                        try { sdkTrans.Rollback(); } catch { }
                        return Models.McpResponse.Err(
                            code: "GroupUpdateFailed",
                            message: ex.Message,
                            hint: "A member bind that creates a cyclic subtype chain is rejected by the SDK.",
                            target: groupName,
                            extra: new JObject { ["stackTrace"] = ex.StackTrace });
                    }
                }
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "GroupUpdateFailed",
                    message: ex.Message,
                    hint: "Ensure the Group exists and payload is valid JSON.",
                    target: groupName);
            }
        }

        // Compare the requested membership (adds + removals) against the set that actually
        // persisted. `missing` = requested members absent from the re-read; `unremoved` =
        // requested removals still present. Extracted as a pure static seam so the diff logic
        // is unit-testable without an SDK Group instance.
        internal static JObject CompareGroupMembership(
            System.Collections.Generic.IEnumerable<string> requestedMemberNames,
            System.Collections.Generic.IEnumerable<string> requestedRemovals,
            System.Collections.Generic.IEnumerable<string> actualMemberNames)
        {
            var actual = new System.Collections.Generic.HashSet<string>(
                actualMemberNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var missing = new JArray((requestedMemberNames ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Where(n => !actual.Contains(n))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            var unremoved = new JArray((requestedRemovals ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Where(actual.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            return new JObject { ["missing"] = missing, ["unremoved"] = unremoved };
        }

        // Compare each requested {name, subtypeOf} against the supertype the re-read attribute
        // actually carries (null = no persisted supertype). Members absent from
        // actualSupertypes are reported by CompareGroupMembership as missing, not here.
        internal static JArray CompareGroupSupertypes(
            JArray requestedMembers,
            System.Collections.Generic.IDictionary<string, string> actualSupertypes)
        {
            var wrong = new JArray();
            if (requestedMembers == null || actualSupertypes == null) return wrong;
            foreach (var m in requestedMembers)
            {
                string name = m["name"]?.ToString();
                string expected = m["subtypeOf"]?.ToString();
                if (string.IsNullOrWhiteSpace(name) || !actualSupertypes.TryGetValue(name, out string actual)) continue;
                if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) continue;
                wrong.Add(new JObject { ["name"] = name, ["expected"] = expected, ["actual"] = actual == null ? JValue.CreateNull() : new JValue(actual) });
            }
            return wrong;
        }

        private static bool SameKey(Artech.Udm.Framework.EntityKey a, Artech.Udm.Framework.EntityKey b)
        {
            return a == null ? b == null : a.Equals(b);
        }

        // issue #59 (plan 071) — post-save re-read of a Group's membership. Returns a
        // GroupUpdateNotPersisted envelope when the requested membership differs from the
        // persisted members, or null when the write is confirmed (or unverifiable — the
        // same in-memory instance back from FindObject would trivially mirror the mutated
        // part, so the re-read is only meaningful for a genuinely fresh instance).
        private string VerifyGroupMembersPersisted(string groupName, JArray applied, JArray removed, Group original, out bool verified)
        {
            verified = false;
            try
            {
                var fresh = _objectService.FindObject(groupName) as Group;
                if (fresh == null) return null; // unverifiable

                // Circularity guard: same in-memory instance back from FindObject means the
                // re-read would trivially mirror the mutated part — treat as unverifiable.
                if (original != null && object.ReferenceEquals(fresh, original)) return null;

                var part = fresh.Parts.Get<GroupStructurePart>();
                if (part == null) return null; // unverifiable

                var actual = new System.Collections.Generic.List<string>();
                var actualSupertypes = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var member in part.Members)
                {
                    if (member?.Subtype?.Name == null) continue;
                    actual.Add(member.Subtype.Name);
                    // Re-read the attribute itself: the member's Supertype getter falls back to
                    // the subtype when SuperTypeKey did not persist.
                    var att = Artech.Genexus.Common.Objects.Attribute.Get(fresh.Model, member.Subtype.Id);
                    var sup = att?.SuperType;
                    actualSupertypes[member.Subtype.Name] = sup == null || sup.Key.Equals(att.Key) ? null : sup.Name;
                }

                var requestedAdds = applied == null
                    ? new System.Collections.Generic.List<string>()
                    : applied.Select(m => m["name"]?.ToString()).Where(n => n != null).ToList();
                var requestedRemovals = removed == null
                    ? new System.Collections.Generic.List<string>()
                    : removed.Select(r => r.ToString()).ToList();

                verified = true; // a genuinely fresh instance was re-read and compared
                var diff = CompareGroupMembership(requestedAdds, requestedRemovals, actual);
                var missing = diff["missing"] as JArray ?? new JArray();
                var unremoved = diff["unremoved"] as JArray ?? new JArray();
                var wrongSupertype = CompareGroupSupertypes(applied, actualSupertypes);
                if (missing.Count == 0 && unremoved.Count == 0 && wrongSupertype.Count == 0) return null;

                return Models.McpResponse.Err(
                    code: "GroupUpdateNotPersisted",
                    message: $"The SDK saved the Group but the re-read did not confirm the requested membership ({missing.Count} member(s) missing, {unremoved.Count} removal(s) not applied, {wrongSupertype.Count} supertype link(s) not persisted).",
                    hint: "On this GeneXus build the Group-structure write may not have fully survived. Re-read with genexus_structure action=get_visual and fix any missing members in the IDE's Group editor if they recur.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_structure",
                        args: new JObject { ["action"] = "get_visual", ["name"] = groupName },
                        why: "Shows the persisted Group members so you can see exactly which items landed.")),
                    target: groupName,
                    extra: new JObject
                    {
                        ["missing"] = missing,
                        ["unremoved"] = unremoved,
                        ["wrongSupertype"] = wrongSupertype,
                        ["requested"] = new JArray(requestedAdds),
                        ["persisted"] = new JArray(actual),
                        ["saved"] = false
                    });
            }
            catch (Exception ex)
            {
                Logger.Debug("[GROUP-VERIFY] " + ex.Message);
                return null;
            }
        }

        // Serializes GroupStructurePart.Members as children: [{ name, subtypeOf }] so
        // genexus_structure get_visual gives a write-verify-read round trip.
        public string GetGroupStructure(string groupName)
        {
            try
            {
                var obj = _objectService.FindObject(groupName);
                if (obj == null) return HealingService.FormatNotFoundError(groupName, _objectService.GetKbService().GetIndexCache().GetIndex());
                if (!(obj is Group group)) return Models.McpResponse.Err(
                    code: "NotAGroup", message: $"Object '{groupName}' is not a Group.", target: groupName);

                var part = group.Parts.Get<GroupStructurePart>();
                var children = new JArray();
                if (part != null)
                {
                    foreach (var member in part.Members)
                    {
                        if (member?.Subtype == null) continue;
                        children.Add(new JObject
                        {
                            ["name"] = member.Subtype.Name,
                            ["subtypeOf"] = member.Supertype?.Name
                        });
                    }
                }

                return Models.McpResponse.Ok(
                    target: groupName,
                    code: "StructureRead",
                    result: new JObject
                    {
                        ["name"] = group.Name,
                        ["type"] = "Group",
                        ["description"] = group.Description,
                        ["children"] = children,
                        ["_meta"] = new JObject
                        {
                            ["suggested_next"] = new JObject
                            {
                                ["tool"] = "genexus_analyze",
                                ["args"] = new JObject { ["name"] = group.Name },
                                ["why"] = "Confirms member-based dependencies for the Group."
                            }
                        }
                    });
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "StructureReadFailed",
                    message: ex.Message,
                    hint: "Ensure the target is a Group.",
                    target: groupName);
            }
        }
    }
}
