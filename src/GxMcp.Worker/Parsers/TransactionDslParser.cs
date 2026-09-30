using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Descriptors;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Parsers
{
    // issue #36.1 — raised when the SDK could not remove one or more attributes that a
    // Structure write intended to drop (mode:full replacement, or a remove_attribute op).
    // The interceptor turns this into a StructureAttributeNotRemoved error so the caller
    // never sees a false success for an additive-only no-op.
    public class StructureRemovalException : Exception
    {
        public IReadOnlyList<string> Failures { get; }
        public StructureRemovalException(IReadOnlyList<string> failures)
            : base("Could not remove attribute(s): " + string.Join("; ", failures))
        {
            Failures = failures;
        }
    }

    public class TransactionDslParser : IDslParser
    {
        // Accumulates removal failures across all (recursive) SyncTransactionNodes calls;
        // checked once after the top-level sync in Parse(). A fresh parser is created per
        // ParseFromText call (StructureParser.GetParser), so this is never shared.
        private readonly List<string> _removalFailures = new List<string>();

        public void Serialize(KBObject obj, StringBuilder sb)
        {
            if (obj is Transaction trn)
            {
                SerializeLevel(trn.Structure.Root, sb, 0);
            }
        }

        public void Parse(KBObject obj, string text)
        {
            if (obj is Transaction trn)
            {
                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                .Where(l => !string.IsNullOrWhiteSpace(l))
                                .ToList();
                var parsedNodes = DslParserUtils.ParseLinesIntoNodes(lines);
                
                var targetNodes = parsedNodes;
                if (parsedNodes.Count == 1 && parsedNodes[0].IsCompound)
                {
                    targetNodes = parsedNodes[0].Children;
                }

                SyncTransactionNodes(trn.Structure.Root, targetNodes, obj.Model);

                if (_removalFailures.Count > 0)
                    throw new StructureRemovalException(_removalFailures.AsReadOnly());
            }
        }

        private void SerializeLevel(dynamic level, StringBuilder sb, int indent)
        {
            string indentStr = new string(' ', indent * 4);
            if (indent > 0)
            {
                sb.AppendLine($"{indentStr}{level.Name}");
                sb.AppendLine($"{indentStr}{{");
            }

            if (level.Attributes != null)
            {
                foreach (dynamic attr in level.Attributes)
                {
                    string keyMarker = attr.IsKey ? "*" : "";
                    string typeStr = "Unknown";
                    try {
                        if (attr.Type != null) typeStr = attr.Type.ToString();
                    } catch {
                        try { if (attr.Attribute != null && attr.Attribute.Type != null) typeStr = attr.Attribute.Type.ToString(); } catch { }
                    }

                    try {
                        if (attr.Attribute != null) {
                            int len = attr.Attribute.Length;
                            int dec = attr.Attribute.Decimals;
                            if (len > 0) {
                                if (dec > 0) typeStr += $"({len},{dec})";
                                else typeStr += $"({len})";
                            }
                        }
                    } catch { }

                    string desc = "";
                    string formula = "";
                    bool isNullable = false;
                    try {
                        if (attr.Attribute != null) {
                            desc = attr.Attribute.Description?.ToString() ?? "";
                            formula = attr.Attribute.Formula?.ToString() ?? "";
                            dynamic pNullable = attr.Attribute.Properties.Get("Nullable");
                            if (pNullable != null) {
                                string nVal = pNullable.ToString();
                                isNullable = nVal.Equals("Yes", StringComparison.OrdinalIgnoreCase) || nVal.Equals("Nullable", StringComparison.OrdinalIgnoreCase);
                            }
                        }
                    } catch { }

                    var declaration = AttributeDeclaration.Render(attr.Name, keyMarker, typeStr, desc, formula, isNullable);
                    sb.AppendLine(string.Format("{0}{1}{2}{3}", indentStr, indent > 0 ? "    " : "", declaration.Head, declaration.Comment));
                }
            }

            if (level.Levels != null)
            {
                foreach (dynamic subLevel in level.Levels) SerializeLevel(subLevel, sb, indent + 1);
            }

            if (indent > 0) sb.AppendLine($"{indentStr}}}");
        }

        private void SyncTransactionNodes(dynamic sdkLevel, List<DslParserUtils.ParsedNode> parsedNodes, KBModel model)
        {
            var existingItems = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
            if (sdkLevel.Attributes != null) { foreach (dynamic attr in sdkLevel.Attributes) existingItems[attr.Name] = attr; }

            foreach (var pNode in parsedNodes)
            {
                if (pNode.IsCompound)
                {
                    dynamic targetSubLevel = null;
                    if (sdkLevel.Levels != null) {
                        foreach (dynamic subLvl in sdkLevel.Levels) { if (subLvl.Name.Equals(pNode.Name, StringComparison.OrdinalIgnoreCase)) { targetSubLevel = subLvl; break; } }
                    }

                    if (targetSubLevel == null)
                    {
                        // Use the (TransactionLevel parent) ctor + typed AddLevel(level) so the SDK
                        // wires the new level into the structure with bookkeeping EnsureSave honors.
                        // The previous Activator+Levels.Add path silently dropped new sub-levels —
                        // same shape as the BC attribute regression fixed in 8c8f433.
                        try {
                            targetSubLevel = new TransactionLevel(sdkLevel);
                            targetSubLevel.Name = pNode.Name;
                            sdkLevel.AddLevel(targetSubLevel);
                        } catch (Exception lvlEx) {
                            Logger.Error("[TransactionDslParser] Failed to add sub-level '" + pNode.Name + "': " + (lvlEx.InnerException?.Message ?? lvlEx.Message));
                            targetSubLevel = null;
                        }
                    }
                    if (targetSubLevel != null) SyncTransactionNodes(targetSubLevel, pNode.Children, model);
                }
                else
                {
                    if (existingItems.TryGetValue(pNode.Name, out var existing))
                    {
                        existing.IsKey = pNode.IsKey;
                        // Update existing attribute's type if DSL specifies one.
                        ApplyTypeFromDsl(existing, pNode.TypeStr, model);
                        ApplyMetadataFromDsl(existing, pNode);
                    }
                    else
                    {
                        try {
                            // Resolve (or create) the global Attribute first — the SDK's typed
                            // sdkLevel.AddAttribute(globalAttr) is the only path that links the new
                            // TransactionAttribute into the structure with the bookkeeping that
                            // EnsureSave honors. The previous Activator+Attributes.Add path silently
                            // dropped new items because the SDK's TransactionAttribute proxy needs
                            // to be created by the level itself.
                            var globalAttr = Artech.Genexus.Common.Objects.Attribute.Get(model, pNode.Name);
                            bool createdGlobal = false;
                            if (globalAttr == null)
                            {
                                try
                                {
                                    var attrGuid = KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Attribute>().Id;
                                    var newAttr = KBObject.Create(model, attrGuid);
                                    newAttr.Name = pNode.Name;
                                    globalAttr = newAttr as Artech.Genexus.Common.Objects.Attribute;
                                    if (globalAttr != null)
                                    {
                                        ApplyTypeFromDsl(globalAttr, pNode.TypeStr, model);
                                        ApplyMetadataFromDsl(globalAttr, pNode);
                                        newAttr.Save();
                                        createdGlobal = true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Warn("[TransactionDslParser] global Attribute create failed for '" + pNode.Name + "': " + ex.Message);
                                    globalAttr = null;
                                }
                            }

                            if (globalAttr == null)
                            {
                                Logger.Error("[TransactionDslParser] cannot add attribute '" + pNode.Name + "' — no global Attribute available; skipping.");
                                continue;
                            }

                            dynamic trnAttr = sdkLevel.AddAttribute(globalAttr);
                            try { trnAttr.IsKey = pNode.IsKey; } catch { }

                            // If the global already existed, still apply the DSL type so changes
                            // like TokenUser : Numeric(4) → TokenUser : UserLogin propagate.
                            if (!createdGlobal && !string.IsNullOrEmpty(pNode.TypeStr))
                            {
                                ApplyTypeFromDsl(globalAttr, pNode.TypeStr, model);
                            }
                            ApplyMetadataFromDsl(globalAttr, pNode);
                        } catch (Exception addEx) {
                            Logger.Error("[TransactionDslParser] Failed to add attribute '" + pNode.Name + "': " + (addEx.InnerException?.Message ?? addEx.Message));
                        }
                    }
                }
            }

            // issue #36.1 — removals run AFTER adds/updates so a key REPLACEMENT succeeds:
            // the new key already exists when the old one is dropped (the SDK refuses to
            // leave a transaction momentarily keyless). Failures are no longer swallowed —
            // a removal the SDK rejects (key still in use, referenced by an FK/relation/index)
            // is recorded and surfaced by Parse() as a hard error, instead of a silent merge
            // that leaves the attribute in place and reports success ("additive-only" bug).
            if (sdkLevel.Attributes != null)
            {
                var toRemove = new List<dynamic>();
                foreach (dynamic attr in sdkLevel.Attributes)
                {
                    if (!parsedNodes.Any(p => !p.IsCompound && p.Name.Equals(attr.Name, StringComparison.OrdinalIgnoreCase)))
                        toRemove.Add(attr);
                }
                object attrsColl = sdkLevel.Attributes;
                foreach (dynamic dead in toRemove)
                {
                    string deadName = dead.Name;
                    // B8/B9: the previous `dynamic sdkLevel.Attributes.Remove(dead)` threw a
                    // C# RuntimeBinderException ("...Attribute does not contain a definition
                    // for 'Remove'") when the SDK collection exposes no such member — which
                    // was then mis-reported as "key in use". Probe the real removal API via
                    // reflection and classify the outcome accurately.
                    string reflError;
                    bool apiMissing;
                    bool removed = TryRemoveAttribute(attrsColl, (object)dead, deadName, out apiMissing, out reflError);

                    // Verify (a removal method can no-op without throwing when the SDK refuses).
                    bool stillPresent = false;
                    foreach (dynamic a in sdkLevel.Attributes)
                    {
                        if (((string)a.Name).Equals(deadName, StringComparison.OrdinalIgnoreCase)) { stillPresent = true; break; }
                    }
                    if (removed && !stillPresent) continue;

                    if (apiMissing)
                        _removalFailures.Add(deadName + " (SDK-API-UNSUPPORTED: this GeneXus SDK build exposes no attribute-removal method on the transaction structure — removing an attribute from a Transaction is not writable through the SDK; remove it in the GeneXus IDE)");
                    else if (!string.IsNullOrEmpty(reflError))
                        _removalFailures.Add(deadName + " (" + reflError + ")");
                    else
                        _removalFailures.Add(deadName + " (SDK kept the attribute — it is likely a key in use, or referenced by a relation/index)");
                }
            }
        }

        // B8: probe the SDK collection for a working attribute-removal API instead of a
        // blind `dynamic .Remove(dead)` (which throws RuntimeBinderException — a missing
        // member — when no such method exists, and was mis-reported as "key in use").
        // Tries, in order: collection.Remove(item), collection.Remove(name),
        // collection.RemoveAt(index), then the item's own Remove()/Delete(). Sets
        // apiMissing=true only when NONE of these members exist on the types involved.
        private static bool TryRemoveAttribute(object attrsColl, object dead, string deadName,
            out bool apiMissing, out string error)
        {
            apiMissing = false;
            error = null;
            if (attrsColl == null) { apiMissing = true; return false; }
            var collType = attrsColl.GetType();
            bool anyMemberFound = false;

            // 1) collection.Remove(item)  2) collection.Remove(string)
            foreach (var m in collType.GetMethods().Where(mi => mi.Name == "Remove"))
            {
                var ps = m.GetParameters();
                if (ps.Length != 1) continue;
                object arg = null;
                if (ps[0].ParameterType.IsInstanceOfType(dead)) arg = dead;
                else if (ps[0].ParameterType == typeof(string)) arg = deadName;
                else continue;
                anyMemberFound = true;
                try { m.Invoke(attrsColl, new[] { arg }); return true; }
                catch (Exception ex) { error = (ex.InnerException ?? ex).Message; }
            }

            // 3) collection.RemoveAt(index)
            var removeAt = collType.GetMethod("RemoveAt", new[] { typeof(int) });
            if (removeAt != null)
            {
                anyMemberFound = true;
                try
                {
                    int idx = 0, found = -1;
                    foreach (dynamic a in (System.Collections.IEnumerable)attrsColl)
                    {
                        if (((string)a.Name).Equals(deadName, StringComparison.OrdinalIgnoreCase)) { found = idx; break; }
                        idx++;
                    }
                    if (found >= 0) { removeAt.Invoke(attrsColl, new object[] { found }); return true; }
                }
                catch (Exception ex) { error = (ex.InnerException ?? ex).Message; }
            }

            // 4) item.Remove() / item.Delete()
            foreach (var name in new[] { "Remove", "Delete" })
            {
                var im = dead.GetType().GetMethod(name, Type.EmptyTypes);
                if (im == null) continue;
                anyMemberFound = true;
                try { im.Invoke(dead, null); return true; }
                catch (Exception ex) { error = (ex.InnerException ?? ex).Message; }
            }

            if (!anyMemberFound) apiMissing = true;
            return false;
        }

        private static void ApplyTypeFromDsl(dynamic trnAttrOrAttribute, string typeStr, KBModel model)
        {
            GxMcp.Worker.Helpers.AttributeTypeApplier.ApplyFromDslType(trnAttrOrAttribute, typeStr, model);
        }

        private static void ApplyMetadataFromDsl(object trnAttrOrAttribute, DslParserUtils.ParsedNode node)
        {
            if (trnAttrOrAttribute == null || node == null) return;
            object globalAttr = trnAttrOrAttribute;
            try
            {
                var property = trnAttrOrAttribute.GetType().GetProperty("Attribute");
                var nested = property?.GetValue(trnAttrOrAttribute, null);
                if (nested != null) globalAttr = nested;
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(node.Description))
                TrySetStringProperty(globalAttr, "Description", node.Description);
            if (node.Formula != null)
            {
                var attribute = globalAttr as Artech.Genexus.Common.Objects.Attribute;
                if (attribute == null) throw new InvalidOperationException("DSL formula target is not a global Attribute.");
                attribute.Formula = Formula.Parse(node.Formula, attribute, null);
            }
        }

        private static void TrySetStringProperty(object target, string name, string value)
        {
            try
            {
                var property = target?.GetType().GetProperty(name);
                if (property?.CanWrite == true) property.SetValue(target, value, null);
            }
            catch (Exception ex)
            {
                Logger.Warn("[TransactionDslParser] Could not apply " + name + " metadata: " + ex.Message);
            }
        }
    }
}
