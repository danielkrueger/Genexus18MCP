using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services.Structure
{
    public static class VisualStructureMapper
    {
        public static JObject MapAttribute(dynamic attr)
        {
            string name = "Unknown";
            bool isKey = false;
            try { name = attr.Name; } catch { }
            try { isKey = attr.IsKey; } catch { }
            
            var item = new JObject { ["name"] = name, ["isKey"] = isKey, ["isLevel"] = false };
            string typeStr = "Unknown";
            string desc = "";
            string formula = "";
            string isNullable = "No";

            try {
                // Try to get the underlying Attribute object
                Artech.Genexus.Common.Objects.Attribute attrObj = null;
                try { 
                    if (attr is TransactionAttribute trnAttr) attrObj = trnAttr.Attribute;
                    else if (attr is TableAttribute tblAttr) attrObj = tblAttr.Attribute;
                    else {
                        // Reflection as fallback for "Attribute" property
                        var p = attr.GetType().GetProperty("Attribute");
                        if (p != null) attrObj = p.GetValue(attr) as Artech.Genexus.Common.Objects.Attribute;
                    }
                } catch { }

                if (attrObj != null) {
                    try { 
                        typeStr = attrObj.Type.ToString(); 
                    } catch { }

                    try { desc = attrObj.Description?.ToString() ?? ""; } catch { }
                    try { formula = (string)attrObj.GetType().GetProperty("Formula")?.GetValue(attrObj)?.ToString() ?? ""; } catch { }
                    
                    try {
                        int len = Convert.ToInt32(attrObj.Length);
                        int dec = Convert.ToInt32(attrObj.Decimals);
                        if (len > 0) typeStr += (dec > 0) ? $"({len},{dec})" : $"({len})";
                    } catch { }
                }

                // If typeStr is still Unknown, try to get it from the item itself (for Tables)
                if (typeStr == "Unknown") {
                    try { if (attr.Type != null) typeStr = attr.Type.ToString(); } catch { }
                }

                // Nullable handling
                try {
                    int nVal = Convert.ToInt32(attr.IsNullable);
                    isNullable = (nVal == 1) ? "Yes" : (nVal == 2 ? "Managed" : "No");
                } catch { }
            } catch (Exception ex) {
                Logger.Error($"[VisualStructureMapper] Fatal error mapping {name}: {ex.Message}");
            }

            // Only a Transaction row has it; an unreadable value is omitted, not defaulted.
            bool showInForms = false;
            if (attr is TransactionAttribute && TransactionAttributeFormsProperty.TryRead((object)attr, out showInForms))
                item[TransactionAttributeFormsProperty.JsonName] = showInForms;

            item["type"] = typeStr;
            item["description"] = desc;
            item["formula"] = formula;
            item["nullable"] = isNullable;
            return item;
        }

        public static void SyncAttributeProperties(dynamic targetAttr, JToken vItem, HashSet<KBObject> modifiedTracker)
        {
            Artech.Genexus.Common.Objects.Attribute attrObj = null;
            try { attrObj = targetAttr.Attribute; } catch { }
            if (attrObj == null) return;

            string desc = vItem["description"]?.ToString();
            string formula = vItem["formula"]?.ToString();
            string nullable = vItem["nullable"]?.ToString();
            string userType = vItem["type"]?.ToString();

            bool isModified = false;

            if (desc != null) {
                string currentDesc = attrObj.Description ?? "";
                if (currentDesc != desc) {
                    attrObj.Description = desc; isModified = true;
                }
            }

            if (formula != null) {
                string current = "";
                try { current = attrObj.GetType().GetProperty("Formula")?.GetValue(attrObj)?.ToString() ?? ""; } catch { }
                
                if (string.IsNullOrWhiteSpace(formula)) {
                    var fProp = attrObj.GetType().GetProperty("Formula");
                    if (fProp != null && fProp.GetValue(attrObj) != null) { fProp.SetValue(attrObj, null); isModified = true; }
                } else if (formula != current) {
                    try {
                        // Use static method if possible or reflection to find Parse
                        var formulaType = typeof(Artech.Genexus.Common.Objects.Procedure).Assembly.GetType("Artech.Genexus.Common.Objects.Formula");
                        if (formulaType != null) {
                            var parseMethod = formulaType.GetMethod("Parse", new[] { typeof(string), typeof(Artech.Architecture.Common.Objects.KBObject), typeof(object) });
                            if (parseMethod != null) {
                                object parsed = parseMethod.Invoke(null, new object[] { formula, attrObj, null });
                                attrObj.GetType().GetProperty("Formula")?.SetValue(attrObj, parsed);
                                try { targetAttr.IsKey = false; } catch { }
                                isModified = true;
                            }
                        }
                    } catch { }
                }
            }

            if (nullable != null)
            {
                // Same accepted forms as genexus_properties (issue #57): canonical strings
                // ("Yes"/"No"/"Managed"), the JSON-boolean forms Newtonsoft renders as
                // "True"/"False" (previously fell through to 0 and silently no-oped), and
                // the numeric enum values ("1"/"2"). Single source of truth:
                // PropertyService.ParseIsNullableValue.
                int val = GxMcp.Worker.Services.PropertyService.ParseIsNullableValue(nullable);
                int current = 0;
                try { current = Convert.ToInt32(targetAttr.IsNullable); } catch { }
                if (current != val)
                {
                    // TableAttribute.IsNullableValue: False=0, True=1, Compatible=2. The
                    // direct `IsNullable = val` int assignment throws a runtime binder error
                    // ("Cannot implicitly convert type 'int' to
                    // 'Artech.Genexus.Common.Parts.TableAttribute.IsNullableValue'"), so the
                    // value is cast to the enum explicitly.
                    // Nullable belongs to the TransactionAttribute occurrence, not to the
                    // global Attribute.  Writing an integer/property-bag value can be ignored
                    // by the SDK and also leaks the setting to unrelated transactions.
                    targetAttr.IsNullable = (Artech.Genexus.Common.Parts.TableAttribute.IsNullableValue)val;
                    isModified = true;
                }
            }

            if (vItem[TransactionAttributeFormsProperty.JsonName] != null)
            {
                if (!TransactionAttributeFormsProperty.TryParse(vItem[TransactionAttributeFormsProperty.JsonName], out bool wantedShow))
                    throw new ArgumentException("showInDefaultForms must be a boolean.");
                bool currentShow;
                if (!TransactionAttributeFormsProperty.TryRead((object)targetAttr, out currentShow))
                    throw new InvalidOperationException("ShowInDefaultFormsUnsupported: this GeneXus SDK cannot read 'Show in Default Forms'.");
                if (currentShow != wantedShow)
                {
                    if (!TransactionAttributeFormsProperty.TryWrite((object)targetAttr, wantedShow))
                        throw new InvalidOperationException("ShowInDefaultFormsUnsupported: this GeneXus SDK cannot write 'Show in Default Forms'.");
                }
            }

            if (!string.IsNullOrWhiteSpace(userType)) {
                var match = System.Text.RegularExpressions.Regex.Match(userType, @"^([a-zA-Z]+)(?:\((\d+)(?:[,.](\d+))?\))?");
                if (match.Success) {
                    string tName = match.Groups[1].Value.ToUpper();
                    if (Enum.TryParse<Artech.Genexus.Common.eDBType>(tName, true, out var eType)) {
                        if (attrObj.Type != eType) { attrObj.Type = eType; isModified = true; }
                        int len = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
                        if (len > 0 && Convert.ToInt32(attrObj.Length) != len) { attrObj.Length = len; isModified = true; }
                        int dec = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
                        if (dec > 0 && Convert.ToInt32(attrObj.Decimals) != dec) { attrObj.Decimals = dec; isModified = true; }
                    }
                }
            }

            if (isModified) modifiedTracker.Add(attrObj);
        }
    }
}
