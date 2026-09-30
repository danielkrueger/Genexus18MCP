using System;
using System.Collections.Generic;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class DatabaseInfoService
    {
        private readonly KbService _kbService;

        public DatabaseInfoService(KbService kbService)
        {
            _kbService = kbService;
        }

        public string GetInfo()
        {
            var env = new JObject();
            try
            {
                dynamic kb = _kbService?.GetKB();
                if (kb == null)
                {
                    return McpResponse.Err(
                        code: "KbNotOpen",
                        message: "No KB is currently open.",
                        hint: "Open a KB first with genexus_kb action=open.",
                        nextSteps: new Newtonsoft.Json.Linq.JArray(KbOpenNextStep.Step(
                            "Open the target KB before calling db_info.",
                            new JObject { ["path"] = "<kb path>" })));
                }

                dynamic environment = TryGet(() => (object)kb.DesignModel.Environment);
                string environmentName = _kbService?.GetActiveEnvironment();
                if (string.IsNullOrWhiteSpace(environmentName))
                    environmentName = TryGet(() => (string)environment.TargetModel.Name);
                if (string.IsNullOrWhiteSpace(environmentName))
                    environmentName = TryGet(() => (string)environment.Name);
                env["environment"] = environmentName;
                env["environmentState"] = string.IsNullOrWhiteSpace(environmentName) ? "unresolved" : "resolved";

                var stores = new JArray();
                JObject defaultStore = null;

                foreach (dynamic ds in EnumerateActiveEnvironmentDataStores(kb))
                {
                    if (ds == null) continue;
                    // Cast the dynamic BuildEntry(ds) result to JObject so the generic
                    // .Value<bool>() below binds statically (dynamic generic call fails).
                    JObject entry = BuildEntry(ds);
                    stores.Add(entry);
                    if (defaultStore == null && entry["isDefault"]?.Value<bool>() == true)
                    {
                        defaultStore = entry;
                    }
                }

                if (defaultStore == null && stores.Count > 0)
                {
                    defaultStore = (JObject)stores[0];
                    defaultStore["isDefault"] = true;
                }

                env["datastores"] = stores;
                env["datastoreState"] = defaultStore == null ? "unresolved" : "resolved";
                if (defaultStore != null)
                {
                    env["default"] = new JObject
                    {
                        ["name"] = defaultStore["name"],
                        ["type"] = defaultStore["type"],
                        ["dialect"] = defaultStore["dialect"],
                        ["provider"] = defaultStore["provider"],
                        ["serverName"] = defaultStore["serverName"],
                        ["schema"] = defaultStore["schema"]
                    };
                    env["dialect"] = defaultStore["dialect"];
                }
                return McpResponse.Ok(code: "DatabaseInfoCollected", result: env);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "DatabaseInfoFailed",
                    message: "The active environment datastore metadata could not be read.",
                    hint: "Check that the KB environment exposes DataStores via the SDK.",
                    errorExtra: new JObject
                    {
                        ["exceptionType"] = ex.GetType().Name,
                        ["state"] = "unresolved"
                    });
            }
        }

        // issue #37 item 4: default-datastore summary reachable without going through
        // the full GetInfo envelope. Returns the default GxDataStore entry (dialect,
        // type, reorganizeServerTables, …) or null when no datastore resolves. Used by
        // BuildService.ReorgPreview to report whether reorg is even possible.
        public static JObject GetDefaultDataStoreInfo(dynamic kb)
        {
            if (kb == null) return null;
            JObject first = null;
            foreach (dynamic ds in EnumerateActiveEnvironmentDataStores(kb))
            {
                if (ds == null) continue;
                // ds is dynamic, so BuildEntry(ds) is a dynamic call — cast to JObject so
                // entry["isDefault"]?.Value<bool>() binds statically (a dynamic generic call
                // fails: "no overload for method 'Value' takes 0 arguments").
                JObject entry = BuildEntry(ds);
                if (first == null) first = entry;
                if (entry["isDefault"]?.Value<bool>() == true) return entry;
            }
            return first;
        }



        // v2.8.2 — enumerate GxDataStores via the DataStoresPart model part. The part lives in
        // model.Parts (a Guid-keyed KBModelPartCollection), so we iterate and match by type name
        // rather than guessing the part's Guid. Tries the design model first, then the
        // environment's target model. Shared with KbService's [KB-OPEN-DATASTORE] diagnostic.
        internal static List<dynamic> EnumerateViaDataStoresPart(dynamic kb, bool activeEnvironmentOnly = false)
        {
            var found = new List<dynamic>();
            if (kb == null) return found;

            var models = new List<dynamic>();
            if (activeEnvironmentOnly)
            {
                try { var tm = kb.DesignModel.Environment.TargetModel; if (tm != null) models.Add(tm); } catch { }
                try { var tm = kb.Environment.TargetModel; if (tm != null) models.Add(tm); } catch { }
            }
            else
            {
                try { var m = kb.DesignModel; if (m != null) models.Add(m); } catch { }
                try { var tm = kb.DesignModel.Environment.TargetModel; if (tm != null) models.Add(tm); } catch { }
            }
            // The DataStoresPart often lives on an environment model that is neither the design
            // model nor the target model. KBEnvironment.Models exposes them all — try each.
            if (!activeEnvironmentOnly)
            {
                try
                {
                    var all = kb.DesignModel.Environment.Models;
                    if (all is System.Collections.IEnumerable me)
                        foreach (var m in me) { if (m != null) models.Add(m); }
                }
                catch { }
            }

            foreach (var model in models)
            {
                dynamic parts = null;
                try { parts = model.Parts; } catch { }
                if (!(parts is System.Collections.IEnumerable seq)) continue;

                foreach (var item in seq)
                {
                    if (item == null) continue;
                    // KBModelPartCollection is IDictionary<Guid, KBModelPart>; the default
                    // enumerator may yield KeyValuePair<Guid, KBModelPart>. Unwrap to the part.
                    object part = item;
                    try
                    {
                        var it = item.GetType();
                        if (it.Name.StartsWith("KeyValuePair", StringComparison.Ordinal))
                            part = it.GetProperty("Value")?.GetValue(item);
                    }
                    catch { }
                    if (part == null) continue;

                    string typeName = null;
                    try { typeName = part.GetType().Name; } catch { }
                    if (!string.Equals(typeName, "DataStoresPart", StringComparison.Ordinal)) continue;

                    dynamic dss = null;
                    try { dss = ((dynamic)part).DataStores; } catch { }
                    if (dss is System.Collections.IEnumerable dsSeq)
                    {
                        foreach (var ds in dsSeq) if (ds != null) found.Add(ds);
                    }
                    if (found.Count > 0) return found;
                }
            }
            return found;
        }

        // Records operations must never silently use the design model's datastore
        // after an environment switch. Keep this resolver target-model-only and
        // fall back only to datastore properties exposed by that same model.
        internal static List<dynamic> EnumerateActiveEnvironmentDataStores(dynamic kb)
        {
            var found = EnumerateViaDataStoresPart(kb, activeEnvironmentOnly: true);
            if (found.Count > 0) return found;

            var targetModels = new List<dynamic>();
            try { var tm = kb?.DesignModel?.Environment?.TargetModel; if (tm != null) targetModels.Add(tm); } catch { }
            try { var tm = kb?.Environment?.TargetModel; if (tm != null) targetModels.Add(tm); } catch { }

            foreach (var model in targetModels)
            {
                try
                {
                    dynamic stores = model.DataStores;
                    if (stores is System.Collections.IEnumerable sequence)
                        foreach (var ds in sequence) if (ds != null) found.Add(ds);
                }
                catch { }
                if (found.Count > 0) return found;

                try
                {
                    dynamic store = model.DataStore;
                    if (store != null) found.Add(store);
                }
                catch { }
                if (found.Count > 0) return found;
            }
            return found;
        }

        private static JObject BuildEntry(dynamic ds)
        {
            // GxDataStore has no direct Name property — fall back to its Category name / Type.
            string name = TryGet(() => (string)ds.Name)
                          ?? TryGet(() => (string)ds.Category.Name)
                          ?? TryGet(() => (string)ds.Type)
                          ?? "";
            int dbmsInt = DatabaseProviderResolver.GetDbmsCode(ds);
            string family = DatabaseProviderResolver.ResolveFamily(ds);
            string typeLabel = DbmsTypeLabel(dbmsInt);
            if (string.Equals(typeLabel, "Unknown", StringComparison.OrdinalIgnoreCase))
                typeLabel = FamilyTypeLabel(family);
            bool isDefault = false;
            try { isDefault = (bool)ds.IsDefault; }
            catch
            {
                try { isDefault = string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase); }
                catch { }
            }

            // issue #37: the GxDataStore stores these under GeneXus internal descriptor
            // names (CS_SERVER, CS_SCHEMA, USER_ID, ACCESS_TECHNO, ADONET_DRIVER/JDBC_DRIVER),
            // confirmed by descriptor dump against a live Oracle KB. The friendly names
            // (ServerName/Schema/UserId) return empty. Try the real names first, keep the
            // friendly ones as cross-version fallbacks.
            string provider = DatabaseProviderResolver.GetProvider(ds) ?? "";
            string serverName = TryProperty(ds, "CS_SERVER")
                              ?? TryProperty(ds, "ServerName")
                              ?? "";
            string schema = TryProperty(ds, "CS_SCHEMA")
                            ?? TryProperty(ds, "DatabaseSchema")
                            ?? TryProperty(ds, "Schema")
                            ?? "";
            string accessTech = TryProperty(ds, "ACCESS_TECHNO")
                            ?? TryProperty(ds, "AccessTechnology")
                            ?? "";
            string userId = TryProperty(ds, "USER_ID")
                            ?? TryProperty(ds, "UserId")
                            ?? TryProperty(ds, "User")
                            ?? "";

            var entry = new JObject
            {
                ["name"] = name,
                ["type"] = typeLabel,
                ["dialect"] = family,
                ["dbmsCode"] = dbmsInt,
                ["isDefault"] = isDefault,
                ["provider"] = provider,
                ["serverName"] = serverName,
                ["schema"] = schema,
                ["accessTechnology"] = accessTech,
                ["userId"] = userId
            };

            // issue #37 item 4: best-effort read of a reorg-server-tables toggle so an
            // agent can detect a DBA-managed no-reorg environment. NOTE: GeneXus 18's SDK
            // does not expose "Reorganize server tables" as a discrete datastore /
            // environment / target-model property (verified by descriptor dump against a
            // live Oracle KB — the reorg-named properties are all generator selectors).
            // This resolver therefore returns null on stock GeneXus 18 and the field is
            // omitted; it is kept so a KB/version that DOES surface the toggle lights it up.
            var reorg = ResolveReorganizeServerTables(ds);
            if (reorg != null)
            {
                entry["reorganizeServerTables"] = reorg.Value; // true = GeneXus applies DDL; false = DBA-managed
                entry["reorgEnabled"] = reorg.Value;
            }
            return entry;
        }

        // Read the datastore's "Reorganize Server tables" property. Returns true when
        // GeneXus is allowed to apply the schema delta itself, false when it's disabled
        // (DBA-managed), or null when the property can't be resolved on this SDK surface.
        internal static bool? ResolveReorganizeServerTables(dynamic ds)
        {
            string[] candidates =
            {
                "ReorganizeServerTables", "ReorganizeServerTable",
                "REORGANIZE_SERVER_TABLES", "ReorgServerTables"
            };
            foreach (var name in candidates)
            {
                var raw = TryProperty(ds, name);
                var parsed = ParseYesNo(raw);
                if (parsed != null) return parsed;
            }
            // Fallback: scan every property for a name containing both "reorganize" and
            // "server". PropertiesObject is not directly IEnumerable — its entries hang off
            // the inner `Properties` IEnumerable, reached by reflection (a plain dynamic
            // foreach throws "cannot convert PropertiesObject to IEnumerable").
            try
            {
                object po = (object)ds.Properties;
                var inner = po?.GetType().GetProperty("Properties")?.GetValue(po) as System.Collections.IEnumerable;
                if (inner != null)
                {
                    foreach (var prop in inner)
                    {
                        var pt = prop?.GetType();
                        string pname = pt?.GetProperty("Name")?.GetValue(prop)?.ToString();
                        if (string.IsNullOrEmpty(pname)) continue;
                        var lower = pname.ToLowerInvariant();
                        if (lower.Contains("reorganize") && lower.Contains("server"))
                        {
                            var parsed = ParseYesNo(pt.GetProperty("Value")?.GetValue(prop)?.ToString());
                            if (parsed != null) return parsed;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        // GeneXus Yes/No properties round-trip as "True"/"False", "Yes"/"No", "1"/"0".
        private static bool? ParseYesNo(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var s = raw.Trim();
            if (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase) || s == "1")
                return true;
            if (s.Equals("false", StringComparison.OrdinalIgnoreCase) || s.Equals("no", StringComparison.OrdinalIgnoreCase) || s == "0")
                return false;
            return null;
        }

        private static string TryProperty(dynamic ds, string propertyName)
        {
            return DatabaseProviderResolver.TryProperty(ds, propertyName);
        }

        private static string FamilyTypeLabel(string family)
        {
            switch (family)
            {
                case "sqlserver": return "SqlServer";
                case "oracle": return "Oracle";
                case "postgres": return "PostgreSQL";
                case "mysql": return "MySQL";
                case "db2": return "Db2";
                case "informix": return "Informix";
                case "saphana": return "SAPHana";
                default: return "Unknown";
            }
        }

        private static T TryGet<T>(Func<T> f)
        {
            try { return f(); } catch { return default(T); }
        }

        public static string DbmsTypeLabel(int dbmsType)
        {
            switch (dbmsType)
            {
                case 1: return "SqlServer";
                case 2: return "Db2";
                case 3: return "Informix";
                case 4: return "Oracle";
                case 5: return "MySQL";
                case 6: case 15: return "PostgreSQL";
                case 7: return "Oracle";
                case 8: return "Db2/AS400";
                case 9: return "Db2Universal";
                case 10: return "SAPHana";
                case 11: return "DynamoDB";
                default: return "Unknown";
            }
        }
    }
}
