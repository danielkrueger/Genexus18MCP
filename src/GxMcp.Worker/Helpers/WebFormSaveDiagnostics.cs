using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Surgical diagnostics around obj.Save(prefs) for the WebForm write fix.
    /// Logs byte-level state of SerializeData() and a probe element value at each
    /// save lifecycle checkpoint so we can localize where the mutation is lost.
    /// </summary>
    internal static class WebFormSaveDiagnostics
    {
        private const string ProbeId = "TextBlockSaldoHoras";

        public static void DumpState(object webFormPart, object kbObject, string tag)
        {
            try
            {
                // 1. m_Document field via reflection
                var docField = webFormPart.GetType().GetField("m_Document",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var doc = docField?.GetValue(webFormPart) as XmlDocument;

                // 2. Document property
                var docProp = webFormPart.GetType().GetProperty("Document",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var docPropVal = docProp?.GetValue(webFormPart, null) as XmlDocument;

                string fieldXml = doc?.OuterXml ?? "(null)";
                string propXml = docPropVal?.OuterXml ?? "(null)";
                bool sameRef = ReferenceEquals(doc, docPropVal);

                string fieldProbe = ExtractProbeAttr(doc);
                string propProbe = ExtractProbeAttr(docPropVal);

                Logger.Info($"[Diag/{tag}] m_Document len={fieldXml.Length} hash={Sha1(fieldXml)} probe={fieldProbe}");
                Logger.Info($"[Diag/{tag}] Document(prop) len={propXml.Length} hash={Sha1(propXml)} probe={propProbe} sameRef={sameRef}");

                // 3. SerializeData() bytes (what the SDK actually persists)
                try
                {
                    var serializeMi = SdkReflection.FindMethod(webFormPart.GetType(), "SerializeData");
                    if (serializeMi != null)
                    {
                        var bytes = serializeMi.Invoke(webFormPart, null) as byte[];
                        if (bytes != null)
                        {
                            string asXml = SafeBytesToXmlString(bytes);
                            string bytesProbe = ExtractProbeAttrFromXml(asXml);
                            Logger.Info($"[Diag/{tag}] SerializeData() bytes={bytes.Length} hash={Sha1Bytes(bytes)} probe={bytesProbe}");
                        }
                        else
                        {
                            Logger.Info($"[Diag/{tag}] SerializeData() returned null");
                        }
                    }
                    else
                    {
                        Logger.Info($"[Diag/{tag}] SerializeData method not found");
                    }
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException ?? ex;
                    Logger.Info($"[Diag/{tag}] SerializeData() threw: {inner.GetType().Name}: {inner.Message}");
                }

                // 4. Mode / Modifications enum values on part + kbObject
                LogModeAndModifications(webFormPart, $"[Diag/{tag}] part");
                LogModeAndModifications(kbObject, $"[Diag/{tag}] obj");

                // 5. StructurePart presence (gates the OnBeforeSaveEntity clobber)
                try
                {
                    var partsProp = kbObject.GetType().GetProperty("Parts");
                    var parts = partsProp?.GetValue(kbObject, null);
                    if (parts != null)
                    {
                        var structurePartType = FindType("Artech.Genexus.Common.Parts.StructurePart");
                        if (structurePartType != null)
                        {
                            var getGeneric = parts.GetType().GetMethods().FirstOrDefault(m => m.Name == "Get" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                            if (getGeneric != null)
                            {
                                var structPart = getGeneric.MakeGenericMethod(structurePartType).Invoke(parts, null);
                                Logger.Info($"[Diag/{tag}] StructurePart present={structPart != null}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Info($"[Diag/{tag}] StructurePart check threw: {ex.Message}");
                }

                // 6. Dirty flag + flags
                try
                {
                    var dirtyProp = webFormPart.GetType().GetProperty("Dirty",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (dirtyProp != null && dirtyProp.CanRead)
                    {
                        Logger.Info($"[Diag/{tag}] part.Dirty={dirtyProp.GetValue(webFormPart, null)}");
                    }
                }
                catch { }

                // 7. PerformSave gate flags on THIS part — these silently skip SaveWithParent
                //    if true (Layers.BL.KBObjectManager.PerformSave line 684-692).
                LogGateFlag(webFormPart, "IsVirtualPart", $"[Diag/{tag}] part");
                LogGateFlag(webFormPart, "ShouldIgnorePart", $"[Diag/{tag}] part");
                LogReadProp(webFormPart, "IsDefault", $"[Diag/{tag}] part");

                // 8. Iterate kbObject.Parts — is the part we mutated actually IN this iteration,
                //    by reference? If not, our mutation is invisible to PerformSave.
                try
                {
                    var partsProp = kbObject.GetType().GetProperty("Parts");
                    var parts = partsProp?.GetValue(kbObject, null) as IEnumerable;
                    if (parts != null)
                    {
                        int idx = 0;
                        foreach (var p in parts)
                        {
                            if (p == null) { idx++; continue; }
                            bool same = ReferenceEquals(p, webFormPart);
                            string t = p.GetType().Name;
                            string mode = SafeReadString(p, "Mode");
                            string mods = SafeReadString(p, "Modifications");
                            bool isVirt = SafeInvokeBool(p, "IsVirtualPart");
                            bool isIgn = SafeInvokeBool(p, "ShouldIgnorePart");
                            bool isDef = SafeReadBool(p, "IsDefault");
                            int? typeId = TryReadInt(p, "TypeId");
                            int? verId = TryReadInt(p, "TypeVersionId");
                            Logger.Info($"[Diag/{tag}] Parts[{idx}] {t} sameAsTarget={same} IsVirtual={isVirt} ShouldIgnore={isIgn} IsDefault={isDef} Mode={mode} Mods={mods} TypeId={typeId} Ver={verId}");
                            idx++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Info($"[Diag/{tag}] Parts iteration threw: {ex.Message}");
                }

                // 9. Model / KB read-only state
                try
                {
                    var modelProp = kbObject.GetType().GetProperty("Model");
                    var model = modelProp?.GetValue(kbObject, null);
                    if (model != null)
                    {
                        Logger.Info($"[Diag/{tag}] obj.Model.IsReadOnly={SafeReadString(model, "IsReadOnly")} IsImporting={SafeReadString(model, "IsImporting")}");
                    }
                    var kbProp = kbObject.GetType().GetProperty("KB");
                    var kb = kbProp?.GetValue(kbObject, null);
                    if (kb != null)
                    {
                        Logger.Info($"[Diag/{tag}] obj.KB.IsReadOnly={SafeReadString(kb, "IsReadOnly")}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Info($"[Diag/{tag}] Model/KB state check threw: {ex.Message}");
                }

                // 10. KB storage file mtime + size — definitive evidence whether a SQL write
                //     actually touched disk between BEFORE and AFTER. If unchanged, no write.
                TryLogKbFile(tag);

                // 11. ROUND-TRIP: read back what's currently in the output cache via
                //     Entity.LoadModelEntityOutput(71, 4). If after our Save the loaded bytes
                //     match our mutation, the in-memory cache holds them (DB-flush is the gate).
                //     If they match the original, even the in-memory output write didn't land.
                TryLogLoadedOutput(webFormPart, tag);

                // 12. BUCKET SCAN: WebForm bytes are NOT at TypeId=71/Version=4. Scan
                //     LoadModelEntityOutput and LoadVersionIndependentOutput across a range
                //     of (typeId, version) and log any bucket that returns data — this reveals
                //     where the real WebForm bytes live.
                if (tag == "BEFORE-SAVE") TryScanBuckets(webFormPart, tag);
            }
            catch (Exception ex)
            {
                Logger.Info($"[Diag/{tag}] DumpState top-level threw: {ex.Message}");
            }
        }

        private static void LogModeAndModifications(object o, string prefix)
        {
            if (o == null) return;
            try
            {
                foreach (var name in new[] { "Mode", "PartsMode", "Modifications" })
                {
                    var p = o.GetType().GetProperty(name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (p != null && p.CanRead)
                    {
                        try { Logger.Info($"{prefix}.{name}={p.GetValue(o, null)}"); }
                        catch (Exception ex) { Logger.Info($"{prefix}.{name} threw: {ex.Message}"); }
                    }
                }
            }
            catch { }
        }

        private static string ExtractProbeAttr(XmlDocument doc)
        {
            if (doc?.DocumentElement == null) return "(no doc)";
            try
            {
                var xp = $"//*[@id='{ProbeId}']";
                var node = doc.SelectSingleNode(xp) as XmlElement;
                if (node == null) return "(no probe)";
                var capExpr = node.Attributes["CaptionExpression"]?.Value;
                return SdkReflection.Truncate(capExpr ?? "(null)", 120);
            }
            catch (Exception ex)
            {
                return "(xpath threw: " + ex.Message + ")";
            }
        }

        private static string ExtractProbeAttrFromXml(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return "(empty)";
            try
            {
                var d = new XmlDocument();
                d.LoadXml(xml);
                return ExtractProbeAttr(d);
            }
            catch (Exception ex)
            {
                return "(parse threw: " + SdkReflection.Truncate(ex.Message, 60) + ")";
            }
        }

        private static string SafeBytesToXmlString(byte[] bytes)
        {
            // WebFormPart serializes m_Document via Artech.Common.Helpers.Convert.ToByteArray.
            // Typically UTF-8 XML; sometimes a binary header. Try UTF-8 first, fall back to scanning for '<'.
            try
            {
                var s = Encoding.UTF8.GetString(bytes);
                int idx = s.IndexOf('<');
                return idx >= 0 ? s.Substring(idx) : s;
            }
            catch { return string.Empty; }
        }

        private static string Sha1(string s)
        {
            using (var sha = SHA1.Create())
            {
                var b = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? string.Empty));
                return BitConverter.ToString(b, 0, 6).Replace("-", "");
            }
        }

        private static string Sha1Bytes(byte[] b)
        {
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(b ?? Array.Empty<byte>());
                return BitConverter.ToString(h, 0, 6).Replace("-", "");
            }
        }

        private static Type FindType(string fullName)
        {
            return SdkReflection.FindType(fullName);
        }

        /// <summary>
        /// Bypass path: directly call Entity.SaveModelEntityOutput(outputTypeId, version, ts, bytes)
        /// on the WebFormPart entity. This is the lowest-level persistence primitive on Entity —
        /// SaveWithParent presumably routes through it after computing the bytes via SerializeData.
        /// If we call it ourselves with the fresh bytes, we sidestep whatever gate in SaveWithParent
        /// is dropping our write.
        /// </summary>
        public static void TryDirectSaveModelEntityOutput(object webFormPart, object kbObject)
        {
            try
            {
                // Read TypeId / TypeVersionId from the entity (inherited Entity properties).
                int? typeId = TryReadInt(webFormPart, "TypeId");
                int? versionId = TryReadInt(webFormPart, "TypeVersionId");
                Logger.Info($"[DirectSave] part.TypeId={typeId} part.TypeVersionId={versionId}");
                if (typeId == null || versionId == null)
                {
                    Logger.Info("[DirectSave] TypeId/TypeVersionId unavailable — abort");
                    return;
                }

                // Fresh bytes from the part (with our mutation).
                var serializeMi = SdkReflection.FindMethod(webFormPart.GetType(), "SerializeData");
                if (serializeMi == null) { Logger.Info("[DirectSave] SerializeData not found"); return; }
                var bytes = serializeMi.Invoke(webFormPart, null) as byte[];
                if (bytes == null || bytes.Length == 0) { Logger.Info("[DirectSave] SerializeData returned empty"); return; }
                Logger.Info($"[DirectSave] SerializeData bytes={bytes.Length} hash={Sha1Bytes(bytes)}");

                // Find SaveModelEntityOutput(int, int, DateTime, byte[]) on the part (inherited from Entity).
                var saveMi = SdkReflection.FindMethod(webFormPart.GetType(), "SaveModelEntityOutput",
                    typeof(int), typeof(int), typeof(DateTime), typeof(byte[]));
                if (saveMi == null)
                {
                    // Try on kbObject as fallback.
                    saveMi = SdkReflection.FindMethod(kbObject.GetType(), "SaveModelEntityOutput",
                        typeof(int), typeof(int), typeof(DateTime), typeof(byte[]));
                    if (saveMi == null)
                    {
                        Logger.Info("[DirectSave] SaveModelEntityOutput method not found on part or kbObject");
                        return;
                    }
                    Logger.Info("[DirectSave] using kbObject.SaveModelEntityOutput");
                    saveMi.Invoke(kbObject, new object[] { typeId.Value, versionId.Value, DateTime.UtcNow, bytes });
                }
                else
                {
                    Logger.Info("[DirectSave] using part.SaveModelEntityOutput");
                    saveMi.Invoke(webFormPart, new object[] { typeId.Value, versionId.Value, DateTime.UtcNow, bytes });
                }
                Logger.Info($"[DirectSave] SaveModelEntityOutput(typeId={typeId}, version={versionId}, bytes={bytes.Length}) completed.");
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                Logger.Info($"[DirectSave] threw: {inner.GetType().Name}: {inner.Message}");
                if (inner.StackTrace != null)
                    Logger.Info("[DirectSave]   at " + inner.StackTrace.Split('\n')[0].Trim());
            }
        }

        private static int? TryReadInt(object o, string propName)
        {
            try
            {
                for (var t = o.GetType(); t != null && t != typeof(object); t = t.BaseType)
                {
                    var p = t.GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (p != null && p.CanRead && p.PropertyType == typeof(int))
                        return (int)p.GetValue(o, null);
                }
            }
            catch { }
            return null;
        }

        private static void LogGateFlag(object o, string methodName, string prefix)
        {
            // This file had two byte-identical base-chain finders, FindNonPublicMethod
            // and FindMethod, and every call site wrote
            //     FindNonPublicMethod(...) ?? FindMethod(...)
            // The second operand could never be reached, so the fallback read as
            // defensive coverage that did not exist. One finder, no dead fallback.
            try
            {
                var v = SdkReflection.TryInvokeNoArgs(o, methodName);
                Logger.Info($"{prefix}.{methodName}=" + (v == null ? "<no method>" : v + "()"));
            }
            catch (Exception ex) { Logger.Info($"{prefix}.{methodName}() threw: {(ex.InnerException ?? ex).Message}"); }
        }

        private static void LogReadProp(object o, string propName, string prefix)
        {
            try
            {
                var p = o.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p == null || !p.CanRead) { Logger.Info($"{prefix}.{propName}=<no prop>"); return; }
                Logger.Info($"{prefix}.{propName}={p.GetValue(o, null)}");
            }
            catch (Exception ex) { Logger.Info($"{prefix}.{propName} threw: {(ex.InnerException ?? ex).Message}"); }
        }

        private static string SafeReadString(object o, string propName)
        {
            try
            {
                var p = o.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p == null || !p.CanRead) return "<no prop>";
                var v = p.GetValue(o, null);
                return v?.ToString() ?? "null";
            }
            catch (Exception ex) { return "threw:" + ex.Message; }
        }

        private static bool SafeReadBool(object o, string propName)
        {
            try
            {
                var p = o.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p == null || !p.CanRead) return false;
                return (bool)p.GetValue(o, null);
            }
            catch { return false; }
        }

        private static bool SafeInvokeBool(object o, string methodName)
        {
            return SdkReflection.TryInvokeBool(o, methodName);
        }

        private static void TryLogKbFile(string tag)
        {
            try
            {
                var path = @"C:\KBs\AcademicoHomolog1\GX_KB_AcademicoHomolog1.mdf";
                var fi = new System.IO.FileInfo(path);
                if (!fi.Exists) { Logger.Info($"[Diag/{tag}] KB mdf NOT FOUND at {path}"); return; }
                Logger.Info($"[Diag/{tag}] KB mdf size={fi.Length} mtime={fi.LastWriteTimeUtc:O}");
                var ldfPath = @"C:\KBs\AcademicoHomolog1\GX_KB_AcademicoHomolog1_log.ldf";
                var li = new System.IO.FileInfo(ldfPath);
                if (li.Exists)
                    Logger.Info($"[Diag/{tag}] KB ldf size={li.Length} mtime={li.LastWriteTimeUtc:O}");
                var kbDataPath = @"C:\KBs\AcademicoHomolog1\kb.data";
                var kd = new System.IO.FileInfo(kbDataPath);
                if (kd.Exists)
                    Logger.Info($"[Diag/{tag}] KB kb.data size={kd.Length} mtime={kd.LastWriteTimeUtc:O}");
            }
            catch (Exception ex) { Logger.Info($"[Diag/{tag}] KB file probe threw: {ex.Message}"); }
        }

        /// <summary>
        /// Bypass: reflect EntityManager and call SaveWithParent(part, kbObject, prefs) directly
        /// with our mutated part instance. This sidesteps the kbObject.Parts iteration and the
        /// PerformSave IsVirtualPart/ShouldIgnorePart gates. Used after obj.Save returned cleanly
        /// but bytes did not reach disk.
        /// </summary>
        public static void TryDirectSaveWithParent(object webFormPart, object kbObject)
        {
            try
            {
                Type emType = FindType("Artech.Layers.BL.EntityManager")
                            ?? FindType("Artech.Udm.Framework.EntityManager")
                            ?? FindFirstType("EntityManager");
                if (emType == null) { Logger.Info("[DirectSWP] EntityManager type not found"); return; }
                Logger.Info("[DirectSWP] EntityManager type = " + emType.AssemblyQualifiedName);

                // Find SaveWithParent overload (static or instance). Prefer 3-arg
                // (part, kbObject, prefs) where prefs is some KBObjectSavePreferences-like type.
                MethodInfo target = null;
                object emInstance = null;
                foreach (var mi in emType.GetMethods(Compatibility.SdkMemberProbe.StaticOrInstanceAnyVisibility))
                {
                    if (mi.Name != "SaveWithParent") continue;
                    var ps = mi.GetParameters();
                    if (ps.Length < 2 || ps.Length > 4) continue;
                    if (!ps[0].ParameterType.IsInstanceOfType(webFormPart)) continue;
                    if (!ps[1].ParameterType.IsInstanceOfType(kbObject)) continue;
                    target = mi;
                    break;
                }
                if (target == null)
                {
                    Logger.Info("[DirectSWP] no SaveWithParent overload matched (part, kbObject, ...). Dumping candidates:");
                    foreach (var mi in emType.GetMethods(Compatibility.SdkMemberProbe.StaticOrInstanceAnyVisibility))
                        if (mi.Name.Contains("Save"))
                            Logger.Info("[DirectSWP]   " + mi);
                    return;
                }
                Logger.Info("[DirectSWP] target = " + target);

                if (!target.IsStatic)
                {
                    // Try resolve a singleton: EntityManager.Instance or static field.
                    var instProp = emType.GetProperty("Instance", Compatibility.SdkMemberProbe.Static);
                    if (instProp != null) emInstance = instProp.GetValue(null, null);
                    if (emInstance == null)
                    {
                        var instField = emType.GetField("Instance", Compatibility.SdkMemberProbe.Static)
                                      ?? emType.GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)
                                      ?? emType.GetField("m_Instance", BindingFlags.NonPublic | BindingFlags.Static);
                        if (instField != null) emInstance = instField.GetValue(null);
                    }
                    if (emInstance == null) { Logger.Info("[DirectSWP] could not resolve EntityManager instance"); return; }
                    Logger.Info("[DirectSWP] resolved EntityManager instance");
                }

                // Build args. Third param (if present) is typically KBObjectSavePreferences;
                // fourth (if present) is rare. We'll fabricate a ForceSave-true prefs.
                var ps2 = target.GetParameters();
                var args = new object[ps2.Length];
                args[0] = webFormPart;
                args[1] = kbObject;
                for (int i = 2; i < ps2.Length; i++)
                {
                    var pt = ps2[i].ParameterType;
                    if (pt.Name.Contains("Preferences"))
                    {
                        try
                        {
                            var prefs = Activator.CreateInstance(pt);
                            TrySetBool(prefs, "ForceSave", true);
                            TrySetBool(prefs, "ForceSaveDefaultParts", true);
                            TrySetBool(prefs, "SkipValidation", true);
                            args[i] = prefs;
                        }
                        catch (Exception ex) { Logger.Info("[DirectSWP] prefs ctor threw: " + ex.Message); args[i] = null; }
                    }
                    else
                    {
                        args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                    }
                }

                target.Invoke(emInstance, args);
                Logger.Info("[DirectSWP] SaveWithParent invoked successfully.");
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                Logger.Info($"[DirectSWP] threw: {inner.GetType().Name}: {inner.Message}");
                if (inner.StackTrace != null)
                    Logger.Info("[DirectSWP]   at " + inner.StackTrace.Split('\n')[0].Trim());
            }
        }

        /// <summary>
        /// Try calling SaveHeader() / SaveHeader(SavePreferences) on the entity directly.
        /// Per SdkProbe, these exist on Entity and likely write the entity's PRIMARY row —
        /// possibly with a BLOB data column. SaveModelEntityOutput only writes outputs;
        /// SaveHeader may be the actual byte-→disk path for the part data.
        /// </summary>
        public static void TryDirectSaveHeader(object webFormPart)
        {
            try
            {
                // 0-arg SaveHeader
                var miNoArg = webFormPart.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                    .FirstOrDefault(m => m.Name == "SaveHeader" && m.GetParameters().Length == 0);
                if (miNoArg != null)
                {
                    miNoArg.Invoke(webFormPart, null);
                    Logger.Info("[DirectHeader] SaveHeader() invoked.");
                }
                else
                {
                    Logger.Info("[DirectHeader] SaveHeader() not found.");
                }

                // 1-arg SaveHeader(SavePreferences)
                var miArg = webFormPart.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                    .FirstOrDefault(m => m.Name == "SaveHeader" && m.GetParameters().Length == 1);
                if (miArg != null)
                {
                    var prefsType = miArg.GetParameters()[0].ParameterType;
                    object prefs = null;
                    try
                    {
                        prefs = Activator.CreateInstance(prefsType);
                        TrySetBool(prefs, "ForceSave", true);
                        TrySetBool(prefs, "SkipValidation", true);
                    }
                    catch { }
                    miArg.Invoke(webFormPart, new[] { prefs });
                    Logger.Info("[DirectHeader] SaveHeader(SavePreferences) invoked.");
                }

                // Also try Save(SavePreferences) on the part — the 1-arg overload (not Save()).
                var miSavePrefs = webFormPart.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                    .FirstOrDefault(m => m.Name == "Save" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name.Contains("Preferences"));
                if (miSavePrefs != null)
                {
                    var prefsType = miSavePrefs.GetParameters()[0].ParameterType;
                    object prefs = null;
                    try
                    {
                        prefs = Activator.CreateInstance(prefsType);
                        TrySetBool(prefs, "ForceSave", true);
                        TrySetBool(prefs, "SkipValidation", true);
                    }
                    catch { }
                    miSavePrefs.Invoke(webFormPart, new[] { prefs });
                    Logger.Info("[DirectHeader] Save(SavePreferences) invoked on part.");
                }
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                Logger.Info($"[DirectHeader] threw: {inner.GetType().Name}: {inner.Message}");
            }
        }

        private static Type FindFirstType(string simpleName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types = null;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }
                if (types == null) continue;
                foreach (var t in types)
                    if (t != null && t.Name == simpleName) return t;
            }
            return null;
        }

        private static void TryLogLoadedOutput(object webFormPart, string tag)
        {
            try
            {
                int? typeId = TryReadInt(webFormPart, "TypeId");
                int? versionId = TryReadInt(webFormPart, "TypeVersionId");
                if (typeId == null || versionId == null) { Logger.Info($"[Diag/{tag}] LoadOutput: no TypeId/VersionId"); return; }

                // LoadModelEntityOutput(int, int, ref/out byte[]) — 3-arg overload.
                var miByteOut = webFormPart.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "LoadModelEntityOutput") return false;
                        var ps = m.GetParameters();
                        return ps.Length == 3
                            && ps[0].ParameterType == typeof(int)
                            && ps[1].ParameterType == typeof(int)
                            && ps[2].ParameterType == typeof(byte[]).MakeByRefType();
                    });
                if (miByteOut != null)
                {
                    var args = new object[] { typeId.Value, versionId.Value, null };
                    var ret = miByteOut.Invoke(webFormPart, args);
                    var bytes = args[2] as byte[];
                    string probe = bytes == null ? "(null)" : ExtractProbeAttrFromXml(SafeBytesToXmlString(bytes));
                    Logger.Info($"[Diag/{tag}] LoadModelEntityOutput(71,4) returned {ret} bytes={(bytes?.Length ?? -1)} hash={(bytes == null ? "n/a" : Sha1Bytes(bytes))} probe={probe}");
                }
                else
                {
                    Logger.Info($"[Diag/{tag}] LoadModelEntityOutput(int,int,out byte[]) not found");
                }
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                Logger.Info($"[Diag/{tag}] LoadOutput threw: {inner.GetType().Name}: {inner.Message}");
            }
        }

        private static void TryScanBuckets(object webFormPart, string tag)
        {
            try
            {
                var miByteOut = webFormPart.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "LoadModelEntityOutput") return false;
                        var ps = m.GetParameters();
                        return ps.Length == 3
                            && ps[0].ParameterType == typeof(int)
                            && ps[1].ParameterType == typeof(int)
                            && ps[2].ParameterType == typeof(byte[]).MakeByRefType();
                    });

                var miVerIndep = webFormPart.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "LoadVersionIndependentOutput") return false;
                        var ps = m.GetParameters();
                        return ps.Length == 4
                            && ps[0].ParameterType == typeof(int)
                            && ps[1].ParameterType == typeof(int)
                            && ps[2].ParameterType == typeof(DateTime).MakeByRefType()
                            && ps[3].ParameterType == typeof(byte[]).MakeByRefType();
                    });

                int hitsModel = 0, hitsIndep = 0;
                for (int tid = 0; tid <= 200; tid++)
                {
                    for (int ver = 0; ver <= 10; ver++)
                    {
                        if (miByteOut != null)
                        {
                            var args = new object[] { tid, ver, null };
                            try
                            {
                                var ret = (bool)miByteOut.Invoke(webFormPart, args);
                                if (ret && args[2] is byte[] bytes && bytes.Length > 0)
                                {
                                    string probe = ExtractProbeAttrFromXml(SafeBytesToXmlString(bytes));
                                    Logger.Info($"[Diag/{tag}] BUCKET-HIT LoadModelEntityOutput({tid},{ver}) bytes={bytes.Length} hash={Sha1Bytes(bytes)} probe={probe}");
                                    hitsModel++;
                                }
                            }
                            catch { }
                        }
                        if (miVerIndep != null)
                        {
                            var args = new object[] { tid, ver, default(DateTime), null };
                            try
                            {
                                var ret = (bool)miVerIndep.Invoke(webFormPart, args);
                                if (ret && args[3] is byte[] bytes && bytes.Length > 0)
                                {
                                    string probe = ExtractProbeAttrFromXml(SafeBytesToXmlString(bytes));
                                    Logger.Info($"[Diag/{tag}] BUCKET-HIT LoadVersionIndependentOutput({tid},{ver}) bytes={bytes.Length} hash={Sha1Bytes(bytes)} probe={probe}");
                                    hitsIndep++;
                                }
                            }
                            catch { }
                        }
                    }
                }
                Logger.Info($"[Diag/{tag}] Bucket scan done. ModelEntity hits={hitsModel}, VersionIndep hits={hitsIndep}");
            }
            catch (Exception ex)
            {
                Logger.Info($"[Diag/{tag}] Bucket scan threw: {ex.Message}");
            }
        }

        private static void TrySetBool(object o, string propName, bool value)
        {
            try
            {
                var p = o.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.CanWrite && p.PropertyType == typeof(bool)) p.SetValue(o, value, null);
            }
            catch { }
        }
    }
}
