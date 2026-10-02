using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services.Structure
{
    public class IndexService
    {
        private readonly ObjectService _objectService;

        public IndexService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        // issue #39: author a user-defined index on a Transaction's associated table. This is the
        // GeneXus-parity way to enforce attribute uniqueness (there is no `Unique(...)` rule).
        // payload = { attributes: ["Attr1", ...], unique?: true, name?: "IX...", order?: "Ascending" }
        public string CreateIndex(string targetName, string payload, JObject args)
        {
            args = args ?? new JObject();
            bool dryRun = args["dryRun"]?.ToObject<bool?>() ?? false;
            bool rollbackOnFailure = args["rollbackOnFailure"]?.ToObject<bool?>() ?? true;
            string baseVersion = args["baseVersion"]?.ToString();

            try
            {
                KBObject obj = _objectService.FindObject(targetName);
                if (obj == null) return HealingService.FormatNotFoundError(targetName, _objectService.GetKbService().GetIndexCache().GetIndex());

                Table tbl = ResolveTable(obj);
                if (tbl == null) return Models.McpResponse.Err(
                    code: "AssociatedTableNotFound",
                    message: "Index creation requires a Transaction (or Table) with a physical table.",
                    hint: "Only Transactions/Tables have indexes. For SDTs or code objects there is no table to index.",
                    target: targetName);

                if (string.IsNullOrWhiteSpace(payload)) return Models.McpResponse.Err(
                    code: "InvalidIndexPayload",
                    message: "payload is required.",
                    hint: "Pass { \"attributes\": [\"AttrName\"], \"unique\": true }.",
                    target: targetName);

                JObject json;
                try { json = JObject.Parse(payload); }
                catch (Exception ex)
                {
                    return Models.McpResponse.Err(
                        code: "InvalidIndexPayload", message: ex.Message,
                        hint: "Pass a JSON object with name, unique, attributes and optional order.",
                        target: targetName);
                }

                ReloadEntity(tbl);
                ReloadEntity(GetTableIndexesPart(tbl));
                List<TableIndexState> before = CaptureIndexes(tbl);
                string versionBefore = ComputeVersionToken(obj, tbl, before);
                if (!string.IsNullOrWhiteSpace(baseVersion)
                    && !string.Equals(baseVersion, versionBefore, StringComparison.Ordinal))
                    return VersionConflict(targetName, baseVersion, versionBefore);

                IndexCreatePlan plan;
                try
                {
                    plan = IndexMutationPlanner.Create(json, CaptureTableAttributes(tbl), before);
                }
                catch (IndexPlanException ex)
                {
                    return Models.McpResponse.Err(
                        code: ex.Code, message: ex.Message, hint: ex.Hint, target: targetName,
                        extra: new JObject
                        {
                            ["persisted"] = false,
                            ["versionToken"] = versionBefore,
                            ["implicitOperations"] = new JArray()
                        });
                }

                JObject preview = BuildResult(targetName, tbl.Name, plan, before, plan.Projected(),
                    versionBefore, versionBefore, dryRun);

                // The simulation is intentionally complete before this branch: payload types,
                // name, duplicate definitions and table membership were all checked without
                // constructing Index or touching TableIndexesPart.
                if (dryRun)
                {
                    var reread = ReloadContext(targetName);
                    List<TableIndexState> after = reread.Table == null
                        ? new List<TableIndexState>() : CaptureIndexes(reread.Table);
                    string versionAfter = reread.Table == null
                        ? string.Empty : ComputeVersionToken(reread.Target, reread.Table, after);
                    bool unchanged = reread.Table != null
                        && IndexMutationPlanner.SameState(before, after)
                        && string.Equals(versionBefore, versionAfter, StringComparison.Ordinal);
                    if (!unchanged)
                    {
                        // No SDK mutator is reachable in the preview branch. A divergence here is
                        // therefore concurrent persisted state and must never be "rolled back" by
                        // deleting another writer's index.
                        RollbackResult rollback = RollbackResult.NotRequested();
                        return Models.McpResponse.Err(
                            code: "DryRunMutationDetected",
                            message: "The persisted index state changed during dry-run. The operation cannot be reported as a preview.",
                            hint: "Re-read get_indexes and retry with its current versionToken.",
                            target: targetName,
                            extra: new JObject
                            {
                                ["persisted"] = false,
                                ["before"] = IndexMutationPlanner.ToJson(before),
                                ["after"] = IndexMutationPlanner.ToJson(after),
                                ["diff"] = IndexMutationPlanner.Diff(before, after),
                                ["versionBefore"] = versionBefore,
                                ["versionAfter"] = versionAfter,
                                ["versionUnchanged"] = false,
                                ["rollbackAttempted"] = rollback.Attempted,
                                ["rolledBack"] = rollback.Success,
                                ["rollbackVerified"] = rollback.Verified,
                                ["rollbackError"] = rollback.Error,
                                ["implicitOperations"] = new JArray()
                            });
                    }

                    preview["persisted"] = false;
                    preview["saved"] = false;
                    preview["verified"] = true;
                    preview["versionUnchanged"] = true;
                    preview["wouldCreate"] = plan.WouldCreate.ToJson();
                    return Models.McpResponse.Ok(
                        target: targetName, code: "IndexCreatePreview", result: preview);
                }

                // Re-read immediately before the first mutation. This closes the validation/save
                // window and makes two writers using the same baseVersion conflict instead of
                // silently overwriting one another.
                var current = ReloadContext(targetName);
                if (current.Table == null)
                    return Models.McpResponse.Err(
                        code: "AssociatedTableNotFound",
                        message: "The associated table disappeared before the index could be saved.",
                        target: targetName);
                List<TableIndexState> currentIndexes = CaptureIndexes(current.Table);
                string currentVersion = ComputeVersionToken(current.Target, current.Table, currentIndexes);
                if (!string.Equals(versionBefore, currentVersion, StringComparison.Ordinal))
                    return VersionConflict(targetName, versionBefore, currentVersion);

                var model = current.Table.Model;
                var attributes = new List<Artech.Genexus.Common.Objects.Attribute>();
                foreach (string attributeName in plan.Attributes)
                {
                    var attribute = Artech.Genexus.Common.Objects.Attribute.Get(model, attributeName);
                    if (attribute == null || current.Table.TableStructure.GetAttribute(attribute) == null)
                        return Models.McpResponse.Err(
                            code: "AttributeNotInTable",
                            message: $"Attribute '{attributeName}' is no longer part of the associated table.",
                            hint: "Re-read get_indexes and the Transaction structure before retrying.",
                            target: targetName);
                    attributes.Add(attribute);
                }

                Index created = null;
                string createdName = plan.RequestedName;
                string writeError = null;
                string transactionConflict = null;
                using (var sdkTrans = model.KB.BeginTransaction())
                {
                    try
                    {
                        // Repeat the compare after entering the SDK transaction. This is the last
                        // instruction before Index.Create, so the check and save share the same
                        // transaction boundary used by GeneXus.
                        IndexContext locked = ReloadContext(targetName);
                        List<TableIndexState> lockedIndexes = locked.Table == null
                            ? new List<TableIndexState>() : CaptureIndexes(locked.Table);
                        string lockedVersion = locked.Table == null
                            ? string.Empty : ComputeVersionToken(locked.Target, locked.Table, lockedIndexes);
                        if (locked.Table == null
                            || !string.Equals(versionBefore, lockedVersion, StringComparison.Ordinal))
                        {
                            transactionConflict = lockedVersion;
                            sdkTrans.Rollback();
                        }
                        else
                        {
                            current = locked;
                            attributes.Clear();
                            foreach (string attributeName in plan.Attributes)
                            {
                                var attribute = Artech.Genexus.Common.Objects.Attribute.Get(model, attributeName);
                                if (attribute == null || current.Table.TableStructure.GetAttribute(attribute) == null)
                                    throw new InvalidOperationException(
                                        $"Attribute '{attributeName}' is no longer part of the associated table.");
                                attributes.Add(attribute);
                            }

                            created = Index.Create(model);
                            created.IndexType = plan.Unique ? IndexType.Unique : IndexType.Duplicate;
                            created.Source = IndexSource.User;
                            IndexOrder sdkOrder = plan.Order == "Descending"
                                ? IndexOrder.Descending : IndexOrder.Ascending;
                            foreach (var attribute in attributes)
                                created.IndexStructure.Members.Add(new IndexMember(created.IndexStructure)
                                {
                                    Attribute = attribute,
                                    Order = sdkOrder
                                });

                            dynamic tableIndexes = ((dynamic)current.Table).TableIndexes;
                            tableIndexes.AddIndex(created);
                            if (!string.IsNullOrWhiteSpace(plan.RequestedName)) created.Name = plan.RequestedName;
                            else created.CreateIndexName();
                            createdName = created.Name;
                            created.EnsureSave();
                            current.Table.EnsureSave();
                            sdkTrans.Commit();
                            WriteService.NotePerTargetWrite(targetName);
                        }
                    }
                    catch (Exception ex)
                    {
                        try { sdkTrans.Rollback(); } catch { }
                        writeError = ex.Message;
                    }
                }

                if (transactionConflict != null)
                    return VersionConflict(targetName, versionBefore, transactionConflict);

                if (writeError != null)
                {
                    RollbackResult rollback = rollbackOnFailure
                        ? RollbackCreatedIndex(ReloadContext(targetName).Table, plan, before, createdName)
                        : RollbackResult.NotRequested();
                    return Models.McpResponse.Err(
                        code: "IndexCreateFailed", message: writeError,
                        hint: rollbackOnFailure
                            ? "The pre-write index state was restored; inspect rollbackVerified before retrying."
                            : "Rollback was disabled; re-read get_indexes before retrying.",
                        target: targetName,
                        extra: RollbackReceipt(before, rollback, saved: false));
                }

                var persistedContext = ReloadContext(targetName);
                List<TableIndexState> persisted = persistedContext.Table == null
                    ? new List<TableIndexState>() : CaptureIndexes(persistedContext.Table);
                TableIndexState actual = persisted.FirstOrDefault(x =>
                    string.Equals(x.Name, createdName, StringComparison.OrdinalIgnoreCase));
                var expectedCreated = plan.WouldCreate.Clone();
                expectedCreated.Name = createdName ?? string.Empty;
                expectedCreated.NameGeneratedBySdk = string.IsNullOrWhiteSpace(plan.RequestedName);
                var expected = before.Select(x => x.Clone()).ToList();
                expected.Add(expectedCreated);
                bool verified = actual != null
                    && IndexMutationPlanner.EquivalentDefinition(actual, expectedCreated)
                    && string.Equals(actual.Source, "User", StringComparison.OrdinalIgnoreCase)
                    && IndexMutationPlanner.SameState(expected, persisted);

                if (!verified)
                {
                    RollbackResult rollback = rollbackOnFailure
                        ? RollbackCreatedIndex(persistedContext.Table, plan, before, createdName)
                        : RollbackResult.NotRequested();
                    return Models.McpResponse.Err(
                        code: "IndexCreateNotPersisted",
                        message: "The save completed, but the persisted index state does not exactly match the requested name, type, member order and attributes.",
                        hint: rollbackOnFailure
                            ? "The pre-write index state was restored; inspect rollbackVerified."
                            : "Rollback was disabled; inspect persistedState before retrying.",
                        target: targetName,
                        extra: new JObject
                        {
                            ["saved"] = true,
                            ["persisted"] = false,
                            ["verified"] = false,
                            ["before"] = IndexMutationPlanner.ToJson(before),
                            ["expected"] = IndexMutationPlanner.ToJson(expected),
                            ["persistedState"] = IndexMutationPlanner.ToJson(persisted),
                            ["diff"] = IndexMutationPlanner.Diff(expected, persisted),
                            ["rollbackAttempted"] = rollback.Attempted,
                            ["rolledBack"] = rollback.Success,
                            ["rollbackVerified"] = rollback.Verified,
                            ["rollbackError"] = rollback.Error,
                            ["implicitOperations"] = new JArray()
                        });
                }

                string versionAfterSave = ComputeVersionToken(
                    persistedContext.Target, persistedContext.Table, persisted);
                JObject result = BuildResult(targetName, persistedContext.Table.Name, plan,
                    before, persisted, versionBefore, versionAfterSave, dryRun: false);
                result["indexName"] = createdName;
                result["saved"] = true;
                result["persisted"] = true;
                result["verified"] = true;
                result["versionUnchanged"] = string.Equals(
                    versionBefore, versionAfterSave, StringComparison.Ordinal);
                result["note"] = "Run genexus_lifecycle action=reorg explicitly to apply the index to the physical database.";
                return Models.McpResponse.Ok(
                    target: targetName, code: "IndexCreated", result: result);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "IndexCreateFailed",
                    message: ex.Message,
                    hint: "Ensure the target exists and payload is valid JSON { attributes:[...], unique:true }.",
                    target: targetName);
            }
        }

        private static Table ResolveTable(KBObject obj)
        {
            if (obj is Table table) return table;
            if (obj is Transaction transaction) return transaction.Structure.Root.AssociatedTable;
            return null;
        }

        private IndexContext ReloadContext(string targetName)
        {
            KBObject target = _objectService.FindObject(targetName);
            ReloadEntity(target);
            Table table = ResolveTable(target);
            ReloadEntity(table);
            ReloadEntity(GetTableIndexesPart(table));
            return new IndexContext { Target = target, Table = table };
        }

        private static object GetTableIndexesPart(Table table)
        {
            if (table == null) return null;
            try { return ((dynamic)table).TableIndexes; } catch { return null; }
        }

        private static void ReloadEntity(object entity)
        {
            if (entity == null) return;
            Type type = entity.GetType();
            while (type != null)
            {
                MethodInfo method = type.GetMethod("Reload",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null, Type.EmptyTypes, null);
                if (method != null)
                {
                    try { method.Invoke(entity, null); }
                    catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
                    return;
                }
                type = type.BaseType;
            }
        }

        private static IEnumerable<string> CaptureTableAttributes(Table table)
        {
            if (table?.TableStructure?.Attributes == null) return Enumerable.Empty<string>();
            var result = new List<string>();
            foreach (dynamic item in table.TableStructure.Attributes)
            {
                string name = Convert.ToString(item.Attribute != null ? item.Attribute.Name : item.Name);
                if (!string.IsNullOrWhiteSpace(name)) result.Add(name);
            }
            return result;
        }

        private static List<TableIndexState> CaptureIndexes(Table table)
        {
            var result = new List<TableIndexState>();
            dynamic part = GetTableIndexesPart(table);
            if (part?.Indexes == null) return result;
            foreach (dynamic tableIndex in part.Indexes)
            {
                dynamic index = tableIndex.Index;
                if (index == null) continue;
                var state = new TableIndexState
                {
                    Name = Convert.ToString(index.Name) ?? string.Empty,
                    IndexType = index.IndexType == null ? string.Empty : Convert.ToString(index.IndexType),
                    Source = index.Source == null ? string.Empty : Convert.ToString(index.Source)
                };
                if (index.IndexStructure?.Members != null)
                {
                    foreach (dynamic member in index.IndexStructure.Members)
                    {
                        state.Members.Add(new IndexMemberState
                        {
                            Name = member.Attribute != null
                                ? Convert.ToString(member.Attribute.Name)
                                : Convert.ToString(member.Name),
                            Order = member.Order == null ? "Ascending" : Convert.ToString(member.Order)
                        });
                    }
                }
                result.Add(state);
            }
            return result;
        }

        private static string ComputeVersionToken(KBObject target, Table table, IEnumerable<TableIndexState> indexes)
        {
            string targetToken = null;
            string tableToken = null;
            try { targetToken = WriteService.ComputeVersionToken(target); } catch { }
            try { tableToken = WriteService.ComputeVersionToken(table); } catch { }
            return IndexMutationPlanner.ComputeVersionToken(targetToken, tableToken, indexes);
        }

        private static JObject BuildResult(string targetName, string tableName, IndexCreatePlan plan,
            IEnumerable<TableIndexState> before, IEnumerable<TableIndexState> after,
            string versionBefore, string versionAfter, bool dryRun)
        {
            var beforeList = (before ?? Enumerable.Empty<TableIndexState>()).ToList();
            var afterList = (after ?? Enumerable.Empty<TableIndexState>()).ToList();
            return new JObject
            {
                ["dryRun"] = dryRun,
                ["target"] = targetName,
                ["table"] = tableName,
                ["before"] = new JObject { ["indexes"] = IndexMutationPlanner.ToJson(beforeList) },
                ["projected"] = new JObject { ["indexes"] = IndexMutationPlanner.ToJson(afterList) },
                ["diff"] = IndexMutationPlanner.Diff(beforeList, afterList),
                ["wouldCreate"] = plan.WouldCreate.ToJson(),
                ["versionBefore"] = versionBefore,
                ["versionAfter"] = versionAfter,
                ["versionToken"] = versionAfter,
                ["implicitOperations"] = new JArray()
            };
        }

        private static string VersionConflict(string targetName, string expected, string current) =>
            Models.McpResponse.Err(
                code: "VersionConflict",
                message: "The table/index state changed after the supplied baseVersion. The index was not created.",
                hint: "Re-read with genexus_structure action=get_indexes and retry with its versionToken.",
                target: targetName,
                extra: new JObject
                {
                    ["expectedVersion"] = expected,
                    ["currentVersion"] = current,
                    ["persisted"] = false,
                    ["implicitOperations"] = new JArray()
                });

        private static RollbackResult RollbackCreatedIndex(
            Table table, IndexCreatePlan plan, IList<TableIndexState> before, string createdName = null)
        {
            if (table == null) return new RollbackResult { Attempted = true, Error = "Associated table unavailable." };
            try
            {
                dynamic part = GetTableIndexesPart(table);
                Index candidate = null;
                if (part?.Indexes != null)
                {
                    List<TableIndexState> current = CaptureIndexes(table);
                    TableIndexState selected = IndexMutationPlanner.FindRollbackCandidate(
                        before, current, plan.WouldCreate, createdName);
                    foreach (dynamic tableIndex in part.Indexes)
                    {
                        Index index = tableIndex.Index as Index;
                        if (index == null) continue;
                        if (selected != null && string.Equals(
                            index.Name, selected.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            candidate = index;
                            break;
                        }
                    }
                }

                var result = new RollbackResult { Attempted = candidate != null };
                if (candidate != null) candidate.Delete();
                ReloadEntity(table);
                ReloadEntity(GetTableIndexesPart(table));
                result.Verified = IndexMutationPlanner.SameState(before, CaptureIndexes(table));
                result.Success = result.Verified;
                if (!result.Verified) result.Error = "The persisted index state still differs from the snapshot.";
                return result;
            }
            catch (Exception ex)
            {
                return new RollbackResult { Attempted = true, Error = ex.Message };
            }
        }

        private static JObject RollbackReceipt(IEnumerable<TableIndexState> before, RollbackResult rollback, bool saved) =>
            new JObject
            {
                ["saved"] = saved,
                ["persisted"] = false,
                ["verified"] = false,
                ["before"] = IndexMutationPlanner.ToJson(before),
                ["rollbackAttempted"] = rollback.Attempted,
                ["rolledBack"] = rollback.Success,
                ["rollbackVerified"] = rollback.Verified,
                ["rollbackError"] = rollback.Error,
                ["implicitOperations"] = new JArray()
            };

        private sealed class IndexContext
        {
            public KBObject Target { get; set; }
            public Table Table { get; set; }
        }

        private sealed class RollbackResult
        {
            public bool Attempted { get; set; }
            public bool Success { get; set; }
            public bool Verified { get; set; }
            public string Error { get; set; }

            public static RollbackResult NotRequested() => new RollbackResult
            {
                Error = "rollbackOnFailure=false"
            };
        }

        // issue #39: drop a user-defined index (pairs with create_index). A GeneXus index is a
        // KBObject, so removal is Index.Delete(). payload = { indexName: "IX..." }.
        public string DropIndex(string targetName, string payload)
        {
            try
            {
                var obj = _objectService.FindObject(targetName);
                if (obj == null) return HealingService.FormatNotFoundError(targetName, _objectService.GetKbService().GetIndexCache().GetIndex());

                Table tbl = null;
                if (obj is Table t) tbl = t;
                else if (obj is Transaction trn) tbl = trn.Structure.Root.AssociatedTable;
                if (tbl == null) return Models.McpResponse.Err(
                    code: "AssociatedTableNotFound",
                    message: "Index drop requires a Transaction (or Table) with a physical table.",
                    target: targetName);

                string indexName = null;
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    JObject body = JObject.Parse(payload);
                    indexName = body["indexName"]?.ToString() ?? body["name"]?.ToString();
                }
                if (string.IsNullOrWhiteSpace(indexName)) return Models.McpResponse.Err(
                    code: "InvalidIndexPayload",
                    message: "payload.indexName is required.",
                    hint: "Pass { \"indexName\": \"IX...\" }. Read genexus_structure action=get_indexes to see names (only source=User indexes can be dropped).",
                    target: targetName);

                // Locate the TableIndex by name and confirm it is user-defined before deleting.
                dynamic tableIndexes = ((dynamic)tbl).TableIndexes;
                Index target = null;
                bool isUser = false;
                if (tableIndexes != null && tableIndexes.Indexes != null)
                {
                    foreach (dynamic ti in tableIndexes.Indexes)
                    {
                        dynamic idx = ti.Index;
                        if (idx == null) continue;
                        if (string.Equals((string)idx.Name, indexName, StringComparison.OrdinalIgnoreCase))
                        {
                            target = idx as Index;
                            try { isUser = idx.Source != null && idx.Source.ToString().Contains("User"); } catch { }
                            break;
                        }
                    }
                }

                if (target == null) return Models.McpResponse.Err(
                    code: "IndexNotFound",
                    message: $"Index '{indexName}' not found on table '{tbl.Name}'.",
                    hint: "Use genexus_structure action=get_indexes to list index names.",
                    target: targetName);

                if (!isUser) return Models.McpResponse.Err(
                    code: "IndexNotUserDefined",
                    message: $"Index '{indexName}' is SDK-generated (Source=Automatic) and cannot be dropped.",
                    hint: "Only user-defined indexes (source=User, e.g. from create_index) can be dropped. Automatic indexes are managed by GeneXus from the data model.",
                    target: targetName);

                // A GeneXus index is a self-contained KBObject; KBObject.Delete() persists on its
                // own (same pattern as genexus_delete_object) — do NOT wrap it in a table save,
                // which would re-persist the table with the index still attached and resurrect it.
                try
                {
                    target.Delete();
                    return Models.McpResponse.Ok(
                        target: targetName,
                        code: "IndexDropped",
                        result: new JObject
                        {
                            ["indexName"] = indexName,
                            ["table"] = tbl.Name,
                            ["note"] = "Run genexus_lifecycle action=reorg to drop the constraint from the physical database."
                        });
                }
                catch (Exception ex)
                {
                    return Models.McpResponse.Err(
                        code: "IndexDropFailed",
                        message: ex.Message,
                        hint: "Check the worker log for the SDK stack trace.",
                        target: targetName,
                        extra: new JObject { ["stackTrace"] = ex.StackTrace });
                }
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "IndexDropFailed",
                    message: ex.Message,
                    hint: "Ensure the target exists and payload is { indexName: \"...\" }.",
                    target: targetName);
            }
        }

        public string GetVisualIndexes(string targetName)
        {
            try {
                var obj = _objectService.FindObject(targetName);
                if (obj == null) return Models.McpResponse.Err(
                    code: "ObjectNotFound",
                    message: "Object not found.",
                    hint: "The requested object is not available in the active Knowledge Base.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_search",
                        args: new JObject { ["query"] = targetName },
                        why: "Search for objects matching the name to find the correct identifier.")),
                    target: targetName);

                Table tbl = null;
                if (obj is Table t) tbl = t;
                else if (obj is Transaction trn) tbl = trn.Structure.Root.AssociatedTable;

                if (tbl == null) return Models.McpResponse.Err(
                    code: "AssociatedTableNotFound",
                    message: "Associated table not found.",
                    hint: "The requested object does not expose a physical table structure for index inspection.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_inspect",
                        args: new JObject { ["name"] = targetName },
                        why: "Inspect the object to confirm whether it has an associated table.")),
                    target: targetName,
                    extra: new JObject { ["objectName"] = obj.Name, ["objectType"] = obj.TypeDescriptor?.Name });

                ReloadEntity(tbl);
                ReloadEntity(GetTableIndexesPart(tbl));
                List<TableIndexState> persistedState = CaptureIndexes(tbl);
                var result = new JObject();
                result["name"] = tbl.Name;
                var indexes = new JArray();
                dynamic dIndexesPart = ((dynamic)tbl).TableIndexes;
                if (dIndexesPart != null && dIndexesPart.Indexes != null) {
                    foreach (dynamic idxObj in dIndexesPart.Indexes) {
                        dynamic idx = idxObj.Index; if (idx == null) continue;
                        var indexItem = new JObject();
                        indexItem["name"] = idx.Name;

                        string typeStr = idx.IndexType != null ? idx.IndexType.ToString() : "";
                        bool isPrimary = typeStr.Contains("Primary");
                        indexItem["isPrimary"] = isPrimary;
                        indexItem["isUnique"] = typeStr.Contains("Unique") || isPrimary;
                        // issue #39: expose Source so callers can tell user-defined indexes
                        // (droppable via drop_index) apart from SDK-generated ones.
                        try { indexItem["source"] = idx.Source != null ? idx.Source.ToString() : ""; }
                        catch { indexItem["source"] = ""; }

                        var attrs = new JArray();
                        if (idx.IndexStructure != null && idx.IndexStructure.Members != null) {
                            foreach (dynamic m in idx.IndexStructure.Members) {
                                var attrObj = new JObject();
                                attrObj["name"] = m.Attribute != null ? m.Attribute.Name : m.Name;
                                try {
                                    attrObj["isAscending"] = m.Order.ToString().Contains("Ascending");
                                } catch {
                                    attrObj["isAscending"] = true;
                                }
                                attrs.Add(attrObj);
                            }
                        }
                        indexItem["attributes"] = attrs;
                        indexes.Add(indexItem);
                    }
                }
                result["indexes"] = indexes;
                result["versionToken"] = ComputeVersionToken(obj, tbl, persistedState);
                result["persisted"] = true;
                result["implicitOperations"] = new JArray();
                return Models.McpResponse.Ok(target: targetName, code: "IndexesRead", result: result);
            } catch (Exception ex) {
                return Models.McpResponse.Err(
                    code: "IndexesReadFailed",
                    message: ex.Message,
                    hint: "Inspect the worker log; the table index metadata may not be accessible for this object.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_inspect",
                        args: new JObject { ["name"] = targetName },
                        why: "Inspect the object to confirm its structure is accessible before retrying.")),
                    target: targetName);
            }
        }
    }
}
