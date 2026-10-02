using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Artech.Genexus.Common.Objects;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services.Structure
{
    // issue #39 follow-up: edit the enum values (and optionally base type) of an EXISTING Domain.
    // Domain creation already accepts enumValues; this closes the edit-after gap. ApplyEnumValues
    // replaces the whole enum set, so callers pass the full desired list.
    public class DomainWriteService
    {
        private readonly ObjectService _objectService;

        public DomainWriteService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        // payload = { enumValues:[{name,value,description?}], dataType?, length?, decimals?, signed? }
        public string SetDomainProperties(string domainName, string payload)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(domainName)) return Models.McpResponse.Err(
                    code: "InvalidDomain", message: "Domain name (name) is required.", target: domainName);
                if (string.IsNullOrWhiteSpace(payload)) return Models.McpResponse.Err(
                    code: "InvalidPayload", message: "payload is required.",
                    hint: "e.g. { \"enumValues\": [{\"name\":\"Active\",\"value\":\"A\"}] }.",
                    target: domainName);

                // GetKB() is dynamic; type the model so EnsureSave binds statically.
                Artech.Architecture.Common.Objects.KBModel model =
                    _objectService.GetKbService().GetKB().DesignModel;
                var domain = _objectService.FindObject(domainName) as Domain;
                if (domain == null) return Models.McpResponse.Err(
                    code: "DomainNotFound",
                    message: $"Domain '{domainName}' not found.",
                    hint: "Create it first with genexus_create type=Domain.",
                    target: domainName);

                var json = JObject.Parse(payload);
                var applied = new JArray();
                var before = SnapshotDomain(domain);
                // Hoisted so the post-commit verification (issue #59) can reference the
                // parsed specs after the transaction block ends.
                System.Collections.Generic.List<DomainEnumValueSpec> parsedSpecs = null;

                using (var sdkTrans = model.KB.BeginTransaction())
                {
                    try
                    {
                        string dataType = json["dataType"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(dataType))
                        {
                            int? len = json["length"]?.ToObject<int?>();
                            int? dec = json["decimals"]?.ToObject<int?>();
                            bool? signed = json["signed"]?.ToObject<bool?>();
                            if (DomainPropertyApplier.ApplyPrimitive(domain, dataType, len, dec, signed))
                                applied.Add("dataType");
                            else { try { sdkTrans.Rollback(); } catch { } return Models.McpResponse.Err(
                                code: "DomainTypeFailed",
                                message: $"Could not apply dataType '{dataType}'.",
                                hint: "Use a canonical type: Character, VarChar, Numeric, Date, DateTime, Boolean, etc.",
                                target: domainName); }
                        }

                        var enumArr = json["enumValues"] as JArray;
                        if (enumArr != null)
                        {
                            // ISSUE-55 ground truth (2026-07-31, GeneXus 18.0.10): enum values
                            // are stored RAW in the version XML for every family (the template's
                            // own HttpMethod char enum stores <Value>GET</Value>). Quoted values
                            // are silently dropped by the bag write, so values pass verbatim.
                            parsedSpecs = DomainEnumValues.FromJson(enumArr);
                            int n = DomainPropertyApplier.ApplyEnumValues(domain, parsedSpecs);
                            if (n < 0) { try { sdkTrans.Rollback(); } catch { } return Models.McpResponse.Err(
                                code: "EnumWriteFailed",
                                message: "Could not write EnumValues — SDK helper not resolvable.",
                                target: domainName); }
                            applied.Add("enumValues");
                        }

                        if (applied.Count == 0) { try { sdkTrans.Rollback(); } catch { } return Models.McpResponse.Err(
                            code: "NoPropertiesToApply",
                            message: "payload contained no recognized domain properties.",
                            hint: "Recognized: enumValues, dataType (+length/decimals/signed).",
                            target: domainName); }

                        // Property-bag updates (notably EnumValues) do not always mark the
                        // object dirty. ForceSave makes the persisted record, not the in-memory
                        // instance, the source of truth.
                        domain.Save(new Artech.Architecture.Common.Objects.KBObjectSavePreferences
                        {
                            ForceSave = true,
                            ForceSaveDefaultParts = true,
                            SkipValidation = false
                        });
                        sdkTrans.Commit();
                        WriteService.NotePerTargetWrite(domainName);

                        var persistedDomain = _objectService.FindObject(domainName, "Domain") as Domain;
                        var persisted = SnapshotDomain(persistedDomain);
                        var requested = BuildRequestedSnapshot(json, persistedDomain ?? domain);
                        var diff = BuildDiff(requested, persisted);
                        if (diff.Count > 0)
                        {
                            return Models.McpResponse.Err(
                                code: "DomainUpdateNotPersisted",
                                message: "The SDK save completed, but the persisted Domain does not match the requested values.",
                                target: domainName,
                                extra: new JObject
                                {
                                    ["before"] = before,
                                    ["requested"] = requested,
                                    ["persisted"] = persisted,
                                    ["diff"] = diff,
                                    ["saved"] = false
                                });
                        }
                        // issue #59 — post-save persistence verification for enum values
                        // (issue #55). Re-read the Domain from the KB store (fresh instance,
                        // not the mutated one) and compare every requested value against what
                        // persisted. A mismatch returns the structured DomainUpdateNotPersisted
                        // envelope instead of a false DomainUpdated success.
                        if (enumArr != null && parsedSpecs != null)
                        {
                            var verifyErr = VerifyEnumValuesPersisted(domainName, parsedSpecs, domain);
                            if (verifyErr != null) return verifyErr;
                        }

                        return Models.McpResponse.Ok(
                            target: domainName,
                            code: "DomainUpdated",
                            result: new JObject
                            {
                                ["domain"] = domain.Name,
                                ["applied"] = applied,
                                ["before"] = before,
                                ["requested"] = requested,
                                ["persisted"] = persisted,
                                ["diff"] = diff,
                                ["saved"] = true
                            });
                    }
                    catch (Exception ex)
                    {
                        try { sdkTrans.Rollback(); } catch { }
                        return Models.McpResponse.Err(
                            code: "DomainUpdateFailed",
                            message: ex.Message,
                            hint: "Check the worker log for the SDK stack trace.",
                            target: domainName,
                            extra: new JObject { ["stackTrace"] = ex.StackTrace });
                    }
                }
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "DomainUpdateFailed",
                    message: ex.Message,
                    hint: "Ensure the domain exists and payload is valid JSON.",
                    target: domainName);
            }
        }
        private static JObject SnapshotDomain(Domain domain)
        {
            if (domain == null) return new JObject();
            var snapshot = new JObject { ["name"] = domain.Name };
            try { snapshot["dataType"] = domain.Type.ToString(); } catch { }
            try { snapshot["length"] = Convert.ToInt32(domain.Length); } catch { }
            try { snapshot["decimals"] = Convert.ToInt32(domain.Decimals); } catch { }
            try { snapshot["signed"] = Convert.ToBoolean(domain.Signed); } catch { }
            snapshot["enumValues"] = DomainPropertyApplier.ReadEnumValues(domain);
            return snapshot;
        }

        private static JObject BuildRequestedSnapshot(JObject input, Domain domain)
        {
            var requested = new JObject();
            foreach (string name in new[] { "dataType", "length", "decimals", "signed" })
                if (input[name] != null) requested[name] = input[name].DeepClone();
            if (input["enumValues"] is JArray values)
            {
                var normalized = new JArray();
                foreach (var value in DomainEnumValues.FromJson(values))
                {
                    normalized.Add(new JObject
                    {
                        ["name"] = value.Name,
                        ["value"] = value.Value,
                        ["description"] = value.Description
                    });
                }
                requested["enumValues"] = normalized;
            }
            return requested;
        }

        private static JArray BuildDiff(JObject requested, JObject persisted)
        {
            var diff = new JArray();
            foreach (var property in requested.Properties())
            {
                JToken actual = persisted[property.Name];
                if (!JToken.DeepEquals(Normalize(property.Value), Normalize(actual)))
                    diff.Add(new JObject { ["path"] = "/" + property.Name, ["requested"] = property.Value.DeepClone(), ["persisted"] = actual?.DeepClone() ?? JValue.CreateNull() });
            }
            return diff;
        }

        private static JToken Normalize(JToken token)
        {
            if (token == null) return JValue.CreateNull();
            if (token.Type == JTokenType.String) return token.ToString().Trim();
            if (token is JArray array)
                return new JArray(array.OfType<JObject>().OrderBy(v => v["name"]?.ToString(), StringComparer.OrdinalIgnoreCase)
                    .Select(v => new JObject(v.Properties().Where(p => p.Value.Type != JTokenType.Null && p.Value.ToString() != string.Empty)
                        .Select(p => new JProperty(p.Name, Normalize(p.Value))))));
            return token.DeepClone();
            }

        // issue #59 — post-save re-read of a Domain's enum values. `requested` is the full
        // desired set (ApplyEnumValues replaces the whole set). Re-find the Domain (fresh
        // instance) and walk its EnumValues, checking each requested {name,value} is present
        // with a normalization-aware value match. Returns a DomainUpdateNotPersisted envelope
        // listing every dropped/diverging value, or null when the write is confirmed (or
        // unverifiable — never falsely accuse a write that can't be read back).
        private string VerifyEnumValuesPersisted(string domainName,
            System.Collections.Generic.List<GxMcp.Worker.Helpers.DomainEnumValueSpec> requested,
            Domain original)
        {
            if (requested == null) return null;
            try
            {
                var fresh = _objectService.FindObject(domainName) as Domain;
                if (fresh == null) return null; // unverifiable

                // Circularity guard: same in-memory instance back from FindObject means the
                // re-read would trivially mirror the mutated enum set — treat as unverifiable.
                if (original != null && object.ReferenceEquals(fresh, original)) return null;

                var persistedPairs = new System.Collections.Generic.List<(string Name, string Value)>();
                try
                {
                    dynamic d = fresh;
                    foreach (var ev in d.EnumValues)
                    {
                        string n = ev.Name?.ToString() ?? string.Empty;
                        string v = ev.Value?.ToString() ?? string.Empty;
                        persistedPairs.Add((n, v));
                    }
                }
                catch { return null; } // SDK shape differs — unverifiable

                var dropped = new JArray();
                foreach (var spec in requested)
                {
                    bool found = false;
                    foreach (var (n, v) in persistedPairs)
                    {
                        if (string.Equals(n, spec.Name, StringComparison.OrdinalIgnoreCase)
                            && GxMcp.Worker.Helpers.PersistenceVerifier.ValuesMatch(v, spec.Value))
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                        dropped.Add(new JObject { ["name"] = spec.Name, ["value"] = spec.Value });
                }

                var unexpected = new JArray();
                foreach (var (name, value) in persistedPairs)
                {
                    bool expected = requested.Any(spec =>
                        string.Equals(name, spec.Name, StringComparison.OrdinalIgnoreCase)
                        && PersistenceVerifier.ValuesMatch(value, spec.Value));
                    if (!expected)
                        unexpected.Add(new JObject { ["name"] = name, ["value"] = value });
                }
                if (dropped.Count == 0 && unexpected.Count == 0) return null;

                return Models.McpResponse.Err(
                    code: "DomainUpdateNotPersisted",
                    message: $"The SDK saved the Domain but the re-read did not confirm the requested enum set ({dropped.Count} missing, {unexpected.Count} unexpected).",
                    hint: "On this GeneXus build the enum write may not have survived the save. Re-read the domain with genexus_analyze, and if the values are missing set them in the GeneXus IDE Domain editor.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_analyze",
                        args: new JObject { ["name"] = domainName },
                        why: "Shows the persisted domain enum values so you can confirm what landed.")),
                    target: domainName,
                    extra: new JObject
                    {
                        ["domain"] = domainName,
                        ["requested"] = new JArray(requested.Select(s => new JObject { ["name"] = s.Name, ["value"] = s.Value })),
                        ["dropped"] = dropped,
                        ["unexpected"] = unexpected,
                        ["persistedCount"] = persistedPairs.Count,
                        ["saved"] = false
                    });
            }
            catch (Exception ex)
            {
                Logger.Debug("[DOMAIN-VERIFY] " + ex.Message);
                return null;
            }
        }
    }
}
