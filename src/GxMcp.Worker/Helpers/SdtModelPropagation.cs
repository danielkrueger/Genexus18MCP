using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Artech.Architecture.Common.Objects;

namespace GxMcp.Worker.Helpers
{
    // Friction-report 05-13 #2 deeper finding: the SDK persists SDTs created via
    // `KBObject.Create(kb.DesignModel, ...)` + Save into the Design model only.
    // IDE-created SDTs are also registered in the Prototype model (ModelId=2),
    // specifically the SDTLevelEntity (type 94) and SDTItemEntity (type 95) name
    // rows that the validator uses when other objects (Procedure, WebPanel)
    // reference `&Var.FieldName`. When those Model 2 rows are missing, the
    // validator can't resolve the field and fires `src0216 propriedade inválida`.
    //
    // The KB SDK exposes no obvious propagation API for this — `kb.Save()` and
    // `model.Save()` don't surface in our reflection probes. Mirror Model 1 →
    // Model 2 directly in SQL: read the SDT's structure blob to discover its
    // item/level EntityIds, then INSERT the missing ModelEntityVersion rows.
    // Same pattern as WebFormCompositionRepair (post-save SQL surgery to fix a
    // gap in the SDK's own propagation).
    public static class SdtModelPropagation
    {
        private const int SDT_TYPE = 33;
        private const int SDT_STRUCTURE_TYPE = 67;
        private const int SDT_LEVEL_TYPE = 94;
        private const int SDT_ITEM_TYPE = 95;
        private const int GxBlobHeaderLength = 11;

        public static void TryPropagateToPrototypeModel(KBObject sdt, string kbPath)
        {
            if (sdt == null || string.IsNullOrEmpty(kbPath)) return;

            int sdtEntityId;
            try
            {
                var keyType = sdt.Key.GetType();
                var idProp = AttributeTypeApplier.GetPropertyUnambiguous(keyType, "Id");
                if (idProp == null) throw new InvalidOperationException("EntityKey.Id property not found");
                sdtEntityId = Convert.ToInt32(idProp.GetValue(sdt.Key, null));
            }
            catch (Exception ex)
            {
                Logger.Debug("[SDT-PROP] failed to resolve SDT entityId: " + ex.Message);
                return;
            }
            if (sdtEntityId <= 0) return;

            string conn = BuildConnectionString(kbPath);
            if (conn == null)
            {
                Logger.Info("[SDT-PROP] connection string unresolved (knowledgebase.connection missing), skipping.");
                return;
            }

            try
            {
                using (var sql = new SqlConnection(conn))
                {
                    sql.Open();

                    // 1) Mirror the SDT (type 33) Model 1 row into Model 2.
                    int n1 = MirrorRows(sql, sdtEntityId, SDT_TYPE);

                    // 2) Discover the structure part entityId by composition.
                    int structEntityId = FindStructureEntityId(sql, sdtEntityId);
                    if (structEntityId > 0)
                    {
                        // 3) Mirror the SDTStructure (type 67) Model 1 row into Model 2.
                        int n2 = MirrorRows(sql, structEntityId, SDT_STRUCTURE_TYPE);

                        // 4) Read the structure blob from the latest EntityVersion to find
                        //    Level/Item EntityIds embedded in the XML.
                        var ids = ReadStructureItemIds(sql, structEntityId);

                        // 5) Mirror Level (type 94) and Item (type 95) Model 1 rows into Model 2.
                        int n3 = 0, n4 = 0;
                        foreach (int levelId in ids.LevelIds)
                        {
                            n3 += MirrorRows(sql, levelId, SDT_LEVEL_TYPE);
                        }
                        foreach (int itemId in ids.ItemIds)
                        {
                            n4 += MirrorRows(sql, itemId, SDT_ITEM_TYPE);
                        }

                        Logger.Info(string.Format(
                            "[SDT-PROP] {0}: model2 mirrored sdt={1} struct={2} levels={3} items={4} (levelIds=[{5}] itemIds=[{6}])",
                            sdt.Name, n1, n2, n3, n4,
                            string.Join(",", ids.LevelIds), string.Join(",", ids.ItemIds)));
                    }
                    else
                    {
                        Logger.Info("[SDT-PROP] " + sdt.Name + ": structure part not found in composition (model2 mirror sdt only=" + n1 + ").");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[SDT-PROP] " + sdt.Name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static int MirrorRows(SqlConnection sql, int entityId, int entityTypeId)
        {
            // Copies every Model 1 row for (entityTypeId, entityId) into Model 2 where the
            // (modelId=2, entityTypeId, entityId, name) tuple doesn't already exist.
            using (var cmd = sql.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO ModelEntityVersion (ModelId, EntityTypeId, EntityId, EntityVersionId, ModelEntityVersionTimestamp, ModelEntityVersionName, ModelEntityVersionDescription, ModelParentEntityTypeId, ModelParentEntityId, ModelUserId)
SELECT 2, m1.EntityTypeId, m1.EntityId, m1.EntityVersionId, m1.ModelEntityVersionTimestamp, m1.ModelEntityVersionName, m1.ModelEntityVersionDescription, m1.ModelParentEntityTypeId, m1.ModelParentEntityId, m1.ModelUserId
FROM ModelEntityVersion m1
WHERE m1.ModelId=1 AND m1.EntityTypeId=@t AND m1.EntityId=@e
  AND NOT EXISTS (
    SELECT 1 FROM ModelEntityVersion m2
    WHERE m2.ModelId=2 AND m2.EntityTypeId=m1.EntityTypeId AND m2.EntityId=m1.EntityId
      AND m2.ModelEntityVersionName = m1.ModelEntityVersionName);
SELECT @@ROWCOUNT;";
                cmd.Parameters.AddWithValue("@t", entityTypeId);
                cmd.Parameters.AddWithValue("@e", entityId);
                var result = cmd.ExecuteScalar();
                return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
            }
        }

        private static int FindStructureEntityId(SqlConnection sql, int sdtEntityId)
        {
            using (var cmd = sql.CreateCommand())
            {
                cmd.CommandText = "SELECT TOP 1 ComponentEntityId FROM EntityVersionComposition WHERE CompoundEntityTypeId=@s AND CompoundEntityId=@e AND ComponentEntityTypeId=@p ORDER BY CompoundEntityVersionId DESC";
                cmd.Parameters.AddWithValue("@s", SDT_TYPE);
                cmd.Parameters.AddWithValue("@e", sdtEntityId);
                cmd.Parameters.AddWithValue("@p", SDT_STRUCTURE_TYPE);
                var result = cmd.ExecuteScalar();
                return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
            }
        }

        public struct StructureItemIds
        {
            public List<int> LevelIds;
            public List<int> ItemIds;
        }

        private static StructureItemIds ReadStructureItemIds(SqlConnection sql, int structEntityId)
        {
            var ids = new StructureItemIds { LevelIds = new List<int>(), ItemIds = new List<int>() };
            byte[] blob;
            using (var cmd = sql.CreateCommand())
            {
                cmd.CommandText = "SELECT TOP 1 EntityVersionData FROM EntityVersion WHERE EntityTypeId=@t AND EntityId=@e ORDER BY EntityVersionId DESC";
                cmd.Parameters.AddWithValue("@t", SDT_STRUCTURE_TYPE);
                cmd.Parameters.AddWithValue("@e", structEntityId);
                var result = cmd.ExecuteScalar();
                if (result == null || result == DBNull.Value) return ids;
                blob = (byte[])result;
            }
            if (blob == null || blob.Length <= GxBlobHeaderLength) return ids;

            string xml;
            try
            {
                using (var ms = new MemoryStream(blob, GxBlobHeaderLength, blob.Length - GxBlobHeaderLength))
                using (var gz = new GZipStream(ms, CompressionMode.Decompress))
                using (var sr = new StreamReader(gz, Encoding.UTF8))
                {
                    xml = sr.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("[SDT-PROP] structure blob decompress failed: " + ex.Message);
                return ids;
            }
            if (string.IsNullOrEmpty(xml)) return ids;

            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                CollectStructureIds(doc.DocumentElement, ids);
            }
            catch (Exception ex)
            {
                Logger.Debug("[SDT-PROP] structure XML parse failed: " + ex.Message);
            }
            return ids;
        }

        private static void CollectStructureIds(XmlNode node, StructureItemIds ids)
        {
            if (node == null) return;
            if (node.NodeType == XmlNodeType.Element)
            {
                string idAttr = node.Attributes?["Id"]?.Value;
                if (!string.IsNullOrEmpty(idAttr) && int.TryParse(idAttr, out int parsed) && parsed > 0)
                {
                    if (node.Name.Equals("Level", StringComparison.OrdinalIgnoreCase)) ids.LevelIds.Add(parsed);
                    else if (node.Name.Equals("Item", StringComparison.OrdinalIgnoreCase)) ids.ItemIds.Add(parsed);
                }
            }
            foreach (XmlNode child in node.ChildNodes)
            {
                CollectStructureIds(child, ids);
            }
        }

        private static string BuildConnectionString(string kbPath)
        {
            return KbConnectionString.Build(kbPath, "SDT-PROP");
        }
    }
}
