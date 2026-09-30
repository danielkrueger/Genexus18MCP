using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Artech.Architecture.Common.Objects;

namespace GxMcp.Worker.Helpers
{
    // After obj.Save() the SDK inserts MULTIPLE EntityVersion rows per WebFormPart save:
    // some from m_Document (with our mutation), and at least one regenerated downstream
    // that may not carry the mutation. EntityVersionComposition for the parent's freshly
    // bumped version can bind to the regenerated sibling instead of ours, so subsequent
    // reads return the original. This helper finds the row whose decompressed XML carries
    // the expected payload and rebinds composition to it.
    //
    // Session 4 verified the fix path manually:
    //   UPDATE EntityVersionComposition
    //   SET ComponentEntityVersionId = <ours>
    //   WHERE CompoundEntityId=<parent> AND CompoundEntityVersionId=<latest>
    //     AND ComponentEntityTypeId=<part type> AND ComponentEntityId=<part id>;
    public static class WebFormCompositionRepair
    {
        private const int WebFormPartTypeId = 71;

        public static void TryRepair(KBObject obj, string kbPath, string expectedToken, long preSaveMaxEntityVersionId)
        {
            if (obj == null || string.IsNullOrEmpty(kbPath) || string.IsNullOrEmpty(expectedToken))
                return;

            try
            {
                var conn = BuildConnectionString(kbPath);
                if (conn == null)
                {
                    Logger.Info("[CompositionRepair] knowledgebase.connection unavailable, skipping.");
                    return;
                }

                if (!TryResolveIds(obj, out int parentTypeId, out int parentEntityId, out int partEntityId))
                {
                    Logger.Info("[CompositionRepair] could not resolve parent/part EntityId for '" + obj.Name + "', skipping.");
                    return;
                }

                using (var sql = new SqlConnection(conn))
                {
                    sql.Open();

                    if (parentTypeId == 0)
                    {
                        parentTypeId = QueryParentTypeId(sql, parentEntityId);
                        Logger.Info("[CompositionRepair] resolved parentTypeId via SQL: " + parentTypeId);
                        if (parentTypeId == 0)
                        {
                            Logger.Info("[CompositionRepair] parent type id unresolved for EntityId=" + parentEntityId + ", skipping.");
                            return;
                        }
                    }

                    int latestParentVersion = QueryLatestParentVersion(sql, parentTypeId, parentEntityId);
                    if (latestParentVersion <= 0)
                    {
                        Logger.Info("[CompositionRepair] no composition row found for parent " + parentEntityId + " (type " + parentTypeId + "), skipping.");
                        return;
                    }

                    var newRows = QueryNewPartVersions(sql, partEntityId, preSaveMaxEntityVersionId);
                    if (newRows.Count == 0)
                    {
                        Logger.Info("[CompositionRepair] no new EntityVersion rows for part " + partEntityId + " (preSaveMax=" + preSaveMaxEntityVersionId + "), skipping.");
                        return;
                    }

                    int matchedVersionId = -1;
                    foreach (var row in newRows)
                    {
                        if (RowContainsToken(row.Bytes, expectedToken))
                        {
                            matchedVersionId = row.EntityVersionId;
                            // Prefer the LATEST row that still carries our token: if the SDK
                            // inserted ours first and the regenerated sibling last, the latest
                            // matching row is the most stable target after future re-binds.
                        }
                    }

                    if (matchedVersionId <= 0)
                    {
                        Logger.Info("[CompositionRepair] no new row contains expected token '" + SdkReflection.Truncate(expectedToken, 60) + "'; saw " + newRows.Count + " new row(s).");
                        return;
                    }

                    int currentBoundVersionId = QueryCurrentBoundVersion(sql, parentTypeId, parentEntityId, latestParentVersion, partEntityId);
                    if (currentBoundVersionId == matchedVersionId)
                    {
                        Logger.Info("[CompositionRepair] composition already bound to v" + matchedVersionId + ", no UPDATE needed.");
                        return;
                    }

                    int updated = UpdateComposition(sql, parentTypeId, parentEntityId, latestParentVersion, partEntityId, matchedVersionId);
                    Logger.Info("[CompositionRepair] redirected parent v" + latestParentVersion + " WebFormPart -> v" + matchedVersionId + " (was v" + currentBoundVersionId + "), rows affected=" + updated);
                }
            }
            catch (Exception ex)
            {
                Logger.Info("[CompositionRepair] exception: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static long SnapshotMaxEntityVersionId(KBObject obj, string kbPath)
        {
            if (obj == null || string.IsNullOrEmpty(kbPath))
            {
                Logger.Info("[CompositionRepair] snapshot: obj/kbPath missing.");
                return -1;
            }
            try
            {
                if (!TryResolveIds(obj, out int parentTypeId, out int parentEntityId, out int partEntityId))
                {
                    Logger.Info("[CompositionRepair] snapshot: TryResolveIds failed for '" + obj.Name + "'.");
                    return -1;
                }
                Logger.Info("[CompositionRepair] snapshot: parent=(" + parentTypeId + "," + parentEntityId + ") part=" + partEntityId);

                var conn = BuildConnectionString(kbPath);
                if (conn == null) return -1;

                using (var sql = new SqlConnection(conn))
                {
                    sql.Open();
                    using (var cmd = sql.CreateCommand())
                    {
                        cmd.CommandText = "SELECT ISNULL(MAX(EntityVersionId), 0) FROM EntityVersion WHERE EntityId=@e AND EntityTypeId=@t";
                        cmd.Parameters.AddWithValue("@e", partEntityId);
                        cmd.Parameters.AddWithValue("@t", WebFormPartTypeId);
                        var result = cmd.ExecuteScalar();
                        return result == null || result == DBNull.Value ? 0L : Convert.ToInt64(result);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Info("[CompositionRepair] snapshot exception: " + ex.Message);
                return -1;
            }
        }

        private static string BuildConnectionString(string kbPath)
        {
            return KbConnectionString.Build(kbPath, "CompositionRepair");
        }

        private static bool TryResolveIds(KBObject obj, out int parentTypeId, out int parentEntityId, out int partEntityId)
        {
            parentTypeId = 0;
            parentEntityId = 0;
            partEntityId = 0;

            try
            {
                var key = obj.Key;
                if (key == null)
                {
                    Logger.Info("[CompositionRepair] obj.Key is null.");
                    return false;
                }

                DumpMembersOnce("obj.Key:" + key.GetType().FullName, key);

                // `Type` on EntityKey is a Guid, not an int — int TypeId lives in the EntityType
                // table and is resolved later via SQL by the caller.
                parentTypeId = ReadIntMember(key, new[] { "TypeId", "EntityTypeId", "ObjectTypeId" });
                parentEntityId = ReadIntMember(key, new[] { "Id", "EntityId", "ObjectId" });

                Logger.Info("[CompositionRepair] parent ids resolved: type=" + parentTypeId + " id=" + parentEntityId);

                if (parentEntityId == 0) return false;

                Artech.Architecture.Common.Objects.KBObjectPart webPart = null;
                foreach (var p in obj.Parts)
                {
                    if (string.Equals(p.TypeDescriptor?.Name, "WebForm", StringComparison.OrdinalIgnoreCase))
                    {
                        webPart = p;
                        break;
                    }
                }
                if (webPart == null)
                {
                    Logger.Info("[CompositionRepair] WebForm part not found on " + obj.Name);
                    return false;
                }

                var partKey = webPart.Key;
                if (partKey == null)
                {
                    Logger.Info("[CompositionRepair] webPart.Key is null. Trying webPart members directly.");
                    DumpMembersOnce("webPart:" + webPart.GetType().FullName, webPart);
                    partEntityId = ReadIntMember(webPart, new[] { "Id", "EntityId", "PartId" });
                }
                else
                {
                    DumpMembersOnce("webPart.Key:" + partKey.GetType().FullName, partKey);
                    partEntityId = ReadIntMember(partKey, new[] { "Id", "EntityId" });
                }
                return partEntityId != 0;
            }
            catch (Exception ex)
            {
                Logger.Info("[CompositionRepair] TryResolveIds exception: " + ex.Message);
                return false;
            }
        }

        private static readonly HashSet<string> _dumpedTypes = new HashSet<string>(StringComparer.Ordinal);

        private static void DumpMembersOnce(string label, object target)
        {
            if (target == null) return;
            lock (_dumpedTypes)
            {
                if (!_dumpedTypes.Add(label)) return;
            }
            try
            {
                var t = target.GetType();
                var sb = new StringBuilder();
                sb.Append("[CompositionRepair] members of ").Append(label).Append(":");
                foreach (var prop in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    object v = null;
                    try { v = prop.GetValue(target); } catch { v = "<throw>"; }
                    sb.Append(" prop ").Append(prop.PropertyType.Name).Append(" ").Append(prop.Name).Append("=").Append(v ?? "null").Append(";");
                }
                foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                {
                    object v = null;
                    try { v = f.GetValue(target); } catch { v = "<throw>"; }
                    sb.Append(" field ").Append(f.FieldType.Name).Append(" ").Append(f.Name).Append("=").Append(v ?? "null").Append(";");
                }
                Logger.Info(sb.ToString());
            }
            catch (Exception ex)
            {
                Logger.Info("[CompositionRepair] DumpMembersOnce failed for " + label + ": " + ex.Message);
            }
        }

        private static int ReadIntMember(object target, string[] candidateNames)
        {
            if (target == null) return 0;
            var t = target.GetType();
            foreach (var name in candidateNames)
            {
                var prop = t.GetProperty(name);
                if (prop != null && prop.CanRead && IsIntegerType(prop.PropertyType))
                {
                    var v = prop.GetValue(target);
                    if (v != null) return Convert.ToInt32(v);
                }
                var field = t.GetField(name);
                if (field != null && IsIntegerType(field.FieldType))
                {
                    var v = field.GetValue(target);
                    if (v != null) return Convert.ToInt32(v);
                }
            }
            return 0;
        }

        private static bool IsIntegerType(Type t) =>
            t == typeof(int) || t == typeof(long) || t == typeof(short) ||
            t == typeof(int?) || t == typeof(long?) || t == typeof(short?);

        private static int QueryParentTypeId(SqlConnection sql, int parentEntityId)
        {
            // The composition table also reflects the parent's int TypeId. Pick whichever
            // parent type has a WebForm component for this EntityId — uniquely identifies
            // the visual KBObject type.
            using (var cmd = sql.CreateCommand())
            {
                cmd.CommandText = "SELECT TOP 1 CompoundEntityTypeId FROM EntityVersionComposition WHERE CompoundEntityId=@e AND ComponentEntityTypeId=@ct";
                cmd.Parameters.AddWithValue("@e", parentEntityId);
                cmd.Parameters.AddWithValue("@ct", WebFormPartTypeId);
                var result = cmd.ExecuteScalar();
                return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
            }
        }

        private static int QueryLatestParentVersion(SqlConnection sql, int parentTypeId, int parentEntityId)
        {
            using (var cmd = sql.CreateCommand())
            {
                cmd.CommandText = "SELECT ISNULL(MAX(CompoundEntityVersionId), 0) FROM EntityVersionComposition WHERE CompoundEntityTypeId=@t AND CompoundEntityId=@e AND ComponentEntityTypeId=@ct";
                cmd.Parameters.AddWithValue("@t", parentTypeId);
                cmd.Parameters.AddWithValue("@e", parentEntityId);
                cmd.Parameters.AddWithValue("@ct", WebFormPartTypeId);
                var result = cmd.ExecuteScalar();
                return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
            }
        }

        private static int QueryCurrentBoundVersion(SqlConnection sql, int parentTypeId, int parentEntityId, int parentVersion, int partEntityId)
        {
            using (var cmd = sql.CreateCommand())
            {
                cmd.CommandText = "SELECT TOP 1 ComponentEntityVersionId FROM EntityVersionComposition WHERE CompoundEntityTypeId=@t AND CompoundEntityId=@e AND CompoundEntityVersionId=@v AND ComponentEntityTypeId=@ct AND ComponentEntityId=@ce ORDER BY ComponentEntityVersionId DESC";
                cmd.Parameters.AddWithValue("@t", parentTypeId);
                cmd.Parameters.AddWithValue("@e", parentEntityId);
                cmd.Parameters.AddWithValue("@v", parentVersion);
                cmd.Parameters.AddWithValue("@ct", WebFormPartTypeId);
                cmd.Parameters.AddWithValue("@ce", partEntityId);
                var result = cmd.ExecuteScalar();
                return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
            }
        }

        private struct PartRow
        {
            public int EntityVersionId;
            public byte[] Bytes;
        }

        private static List<PartRow> QueryNewPartVersions(SqlConnection sql, int partEntityId, long preSaveMax)
        {
            var rows = new List<PartRow>();
            using (var cmd = sql.CreateCommand())
            {
                cmd.CommandText = "SELECT EntityVersionId, EntityVersionData FROM EntityVersion WHERE EntityId=@e AND EntityTypeId=@t AND EntityVersionId > @m ORDER BY EntityVersionId";
                cmd.Parameters.AddWithValue("@e", partEntityId);
                cmd.Parameters.AddWithValue("@t", WebFormPartTypeId);
                cmd.Parameters.AddWithValue("@m", preSaveMax);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        rows.Add(new PartRow
                        {
                            EntityVersionId = r.GetInt32(0),
                            Bytes = (byte[])r.GetValue(1)
                        });
                    }
                }
            }
            return rows;
        }

        private const int GxBlobHeaderLength = 11; // 4-byte magic 01 02 03 04 + 7 bytes of metadata, gzip stream starts at offset 11.

        private static bool RowContainsToken(byte[] bytes, string token)
        {
            if (bytes == null || bytes.Length <= GxBlobHeaderLength) return false;
            try
            {
                using (var ms = new MemoryStream(bytes, GxBlobHeaderLength, bytes.Length - GxBlobHeaderLength))
                using (var gz = new GZipStream(ms, CompressionMode.Decompress))
                using (var sr = new StreamReader(gz, Encoding.UTF8))
                {
                    string text = sr.ReadToEnd();
                    return text.IndexOf(token, StringComparison.Ordinal) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static int UpdateComposition(SqlConnection sql, int parentTypeId, int parentEntityId, int parentVersion, int partEntityId, int newComponentVersionId)
        {
            using (var cmd = sql.CreateCommand())
            {
                cmd.CommandText = "UPDATE EntityVersionComposition SET ComponentEntityVersionId=@new WHERE CompoundEntityTypeId=@t AND CompoundEntityId=@e AND CompoundEntityVersionId=@v AND ComponentEntityTypeId=@ct AND ComponentEntityId=@ce";
                cmd.Parameters.AddWithValue("@new", newComponentVersionId);
                cmd.Parameters.AddWithValue("@t", parentTypeId);
                cmd.Parameters.AddWithValue("@e", parentEntityId);
                cmd.Parameters.AddWithValue("@v", parentVersion);
                cmd.Parameters.AddWithValue("@ct", WebFormPartTypeId);
                cmd.Parameters.AddWithValue("@ce", partEntityId);
                return cmd.ExecuteNonQuery();
            }
        }

    }
}
