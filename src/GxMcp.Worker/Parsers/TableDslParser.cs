using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Descriptors;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;

namespace GxMcp.Worker.Parsers
{
    public class TableDslParser : IDslParser
    {
        public void Serialize(KBObject obj, StringBuilder sb)
        {
            if (obj is Table tbl)
            {
                try {
                    dynamic dStructure = ((dynamic)tbl).TableStructure;
                    foreach (dynamic attr in dStructure.Attributes)
                    {
                        string keyMarker = (bool)attr.IsKey ? "*" : "";
                        string typeStr = "Unknown";
                        string desc = "";
                        string formula = "";
                        bool isNullable = false;

                        try {
                            if (attr.Attribute != null) {
                                if (attr.Attribute.Type != null) typeStr = attr.Attribute.Type.ToString();
                                int len = attr.Attribute.Length;
                                int dec = attr.Attribute.Decimals;
                                if (len > 0) {
                                    if (dec > 0) typeStr += $"({len},{dec})";
                                    else typeStr += $"({len})";
                                }
                                desc = attr.Attribute.Description?.ToString() ?? "";
                                formula = attr.Attribute.Formula?.ToString() ?? "";
                                try {
                                    int nVal = (int)attr.IsNullable;
                                    isNullable = (nVal == 1);
                                } catch { }
                            }
                        } catch { }

                        var declaration = Helpers.AttributeDeclaration.Render(attr.Name, keyMarker, typeStr, desc, formula, isNullable);
                        sb.AppendLine(string.Format("{0}{1}", declaration.Head, declaration.Comment));
                    }
                } catch (Exception ex) {
                    sb.AppendLine("// Error serializing table: " + ex.Message);
                }
            }
        }

        public void Parse(KBObject obj, string text)
        {
            if (obj is Table tbl)
            {
                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                .Where(l => !string.IsNullOrWhiteSpace(l))
                                .ToList();

                var parsedNodes = Helpers.DslParserUtils.ParseLinesIntoNodes(lines);
                dynamic dStructure = ((dynamic)tbl).TableStructure;

                // 1. Remove attributes not in DSL
                var toRemove = new List<dynamic>();
                foreach (dynamic attr in dStructure.Attributes)
                {
                    if (!parsedNodes.Any(p => p.Name.Equals(attr.Name, StringComparison.OrdinalIgnoreCase)))
                        toRemove.Add(attr);
                }
                foreach (dynamic dead in toRemove) { try { dStructure.Attributes.Remove(dead); } catch { } }

                // 2. Add or Update attributes
                var existingItems = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
                foreach (dynamic attr in dStructure.Attributes) existingItems[attr.Name] = attr;

                foreach (var pNode in parsedNodes)
                {
                    if (existingItems.TryGetValue(pNode.Name, out var existing))
                    {
                        existing.IsKey = pNode.IsKey;
                        ApplyTypeFromDsl(existing, pNode.TypeStr, tbl.Model);
                    }
                    else
                    {
                        Type attrType = dStructure.GetType().Assembly.GetType("Artech.Genexus.Common.Objects.TableAttribute");
                        if (attrType != null)
                        {
                            try
                            {
                                var globalAttr = Artech.Genexus.Common.Objects.Attribute.Get(tbl.Model, pNode.Name);
                                bool createdGlobal = false;
                                if (globalAttr == null)
                                {
                                    // Create a new global Attribute so we have somewhere to put the type.
                                    try
                                    {
                                        var attrGuid = KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Attribute>().Id;
                                        var newAttr = KBObject.Create(tbl.Model, attrGuid);
                                        newAttr.Name = pNode.Name;
                                        globalAttr = newAttr as Artech.Genexus.Common.Objects.Attribute;
                                        if (globalAttr != null)
                                        {
                                            // Apply DSL type BEFORE saving so the persisted Attribute has the right shape from the start.
                                            ApplyTypeFromDsl(globalAttr, pNode.TypeStr, tbl.Model);
                                            newAttr.Save();
                                            createdGlobal = true;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        // If creation failed, fall back to old behavior (TableAttribute with no global).
                                        globalAttr = null;
                                        createdGlobal = false;
                                        System.Diagnostics.Debug.WriteLine("TableDslParser: global Attribute create failed: " + ex.Message);
                                    }
                                }

                                // VERIFICATION GAP (commit 8c8f433 follow-up): unlike TransactionLevel which
                                // exposes a typed `AddAttribute(Attribute)` that performs EnsureSave-aware
                                // bookkeeping, TableStructurePart (the actual runtime type behind
                                // `tbl.TableStructure`) inherits only AddCategory/AddDefinition — no typed
                                // AddAttribute method exists on the SDK proxy. The canonical creation path
                                // is the (TableStructurePart, Attribute) ctor + Attributes.Add. Reflection
                                // probe against Artech.Genexus.Common.dll confirms this — see commit notes.
                                // If a future SDK version adds TableStructurePart.AddAttribute(Attribute),
                                // mirror the TransactionDslParser fix here.
                                dynamic tblAttr;
                                if (globalAttr != null)
                                {
                                    var ctor2 = attrType.GetConstructor(new Type[] { (Type)dStructure.GetType(), typeof(Artech.Genexus.Common.Objects.Attribute) });
                                    if (ctor2 != null)
                                    {
                                        tblAttr = ctor2.Invoke(new object[] { dStructure, globalAttr });
                                    }
                                    else
                                    {
                                        tblAttr = Activator.CreateInstance(attrType, new object[] { dStructure });
                                        // Try to set the Attribute reference via reflection if the property exists.
                                        try
                                        {
                                            var attrProp = attrType.GetProperty("Attribute");
                                            if (attrProp != null && attrProp.CanWrite) attrProp.SetValue(tblAttr, globalAttr, null);
                                        }
                                        catch { }
                                    }
                                }
                                else
                                {
                                    tblAttr = Activator.CreateInstance(attrType, new object[] { dStructure });
                                }

                                tblAttr.Name = pNode.Name;
                                tblAttr.IsKey = pNode.IsKey;
                                dStructure.Attributes.Add(tblAttr);

                                // If the global already existed (not created above), still apply the DSL type
                                // so changing e.g. ColName : Numeric(4) → ColName : VarChar(100) actually mutates the global.
                                if (!createdGlobal && globalAttr != null && !string.IsNullOrEmpty(pNode.TypeStr))
                                {
                                    ApplyTypeFromDsl(globalAttr, pNode.TypeStr, tbl.Model);
                                }
                            } catch { }
                        }
                    }
                }
            }
        }

        private static void ApplyTypeFromDsl(dynamic tblAttrOrAttribute, string typeStr, KBModel model)
        {
            GxMcp.Worker.Helpers.AttributeTypeApplier.ApplyFromDslType(tblAttrOrAttribute, typeStr, model);
        }
    }
}
