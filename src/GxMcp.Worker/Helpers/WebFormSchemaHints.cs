using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;

namespace GxMcp.Worker.Helpers
{
    // Best-effort element→accepted-attribute hints derived from observed SDK
    // persistence and publish/worker/Definitions. A missing hint is explicitly
    // unverified, never a guarantee that the SDK will sanitize the attribute.
    public static class WebFormSchemaHints
    {
        /// <summary>
        /// Issue #360: the attribute the IDE uses to carry a control's non-XML properties.
        ///
        /// <para>
        /// The SDK's own <c>ControlDefinition.xml</c> declares <c>GxFormat</c> (display
        /// name <c>Format</c>, a Combo Int of 0=Text / 1=HTML / 2=Raw HTML / 3=Text with
        /// meaningful spaces) on <c>HTMLATT</c>, <c>HTMLSPAN</c> and
        /// <c>HTMLSFLCOL</c>. But on a plain WebForm the IDE does not persist it as an
        /// XML attribute - it writes a
        /// <c>&lt;Properties&gt;&lt;Property&gt;&lt;Name&gt;GxFormat&lt;/Name&gt;…</c>
        /// payload into this attribute instead, even with no pattern instance present.
        /// The generator reads it from there, which is why a plain
        /// <c>format="HTML"</c> on the element persists, verifies, and is then ignored.
        /// </para>
        ///
        /// <para>
        /// Not listed as "accepted" for any element: it is a legitimate attribute the SDK
        /// emits, but it is not an authorable one, so a caller writing it directly is
        /// asking for something the SDK will strip.
        /// </para>
        /// </summary>
        internal const string CustomPropertiesAttribute = "PATTERN_ELEMENT_CUSTOM_PROPERTIES";

        /// <summary>
        /// SDK control property ids that are only reachable through
        /// <see cref="CustomPropertiesAttribute"/> rather than as an XML attribute.
        ///
        /// <para>
        /// <c>GxFormat</c> is the one confirmed by live observation in issue #360 (the
        /// IDE-written form is quoted there and the generated <c>gx_label_ctrl(..., 0,
        /// 2, ...)</c> confirms it reached the generator). The other two are declared on
        /// the same control objects in <c>ControlDefinition.xml</c> and share the same
        /// custom-property carrier, so they are named here as a warning rather than as a
        /// claim - the diagnostic says "check", not "this is ignored".
        /// </para>
        /// </summary>
        private static readonly Dictionary<string, string> _customPropertyOnlyControls =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "GxFormat", "Format" },
            { "ControlType", "Control Type" },
            { "ControlValues", "Control Values" },
        };

        private static readonly string[] _commonCtrlAttrs =
        {
            "id", "name", "ControlName", "controlName", "AttID", "Class", "classref", "Width", "Height", "Visible", "Tooltip"
        };

        private static string[] WithCommon(params string[] extras)
        {
            var arr = new string[_commonCtrlAttrs.Length + extras.Length];
            System.Array.Copy(_commonCtrlAttrs, arr, _commonCtrlAttrs.Length);
            System.Array.Copy(extras, 0, arr, _commonCtrlAttrs.Length, extras.Length);
            return arr;
        }

        // Lookup is case-insensitive so mixed-case markup ("WIDTH" vs "Width") still resolves.
        private static readonly Dictionary<string, string[]> _accepted = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // Legacy HTML WebForms use a deliberately permissive shape. These are
            // observed SDK-persisted attributes, not a claim that the modern
            // GxMultiForm schema accepts every HTML presentation attribute.
            ["table"]          = new[] { "id", "name", "ControlName", "controlName", "classref", "Class", "AttID", "cellPadding", "cellSpacing", "Width", "Height", "BackColor", "ForeColor", "Border", "AutoGrow", "Background", "BackgroundType", "align", "vAlign", "borderColor", "borderStyle", "title", "style" },
            ["gxAttribute"]    = WithCommon("CaptionExpression", "DataField", "ReadOnly", "ControlType", "Format", "Event", "ReturnOnClick", "GxFormat", "OnClickEvent", "OnEnterEvent", "Caption"),
            ["gxTextBlock"]    = WithCommon("CaptionExpression", "Format", "Event", "ReturnOnClick", "GxFormat", "OnClickEvent", "OnEnterEvent", "Caption", "InnerText", "align", "vAlign"),
            ["gxButton"]       = WithCommon("CaptionExpression", "OnClickEvent", "Event", "Enabled", "ReturnOnClick", "GxFormat", "Caption", "OnEnterEvent", "action", "type"),
            ["gxBitmap"]       = WithCommon("ImageData", "Event", "OnClickEvent", "OnEnterEvent", "ReturnOnClick", "GxFormat"),
            ["gxImage"]        = WithCommon("ImageData", "Event", "OnClickEvent", "OnEnterEvent", "ReturnOnClick", "GxFormat"),
            ["gxGrid"]         = WithCommon("DataField", "Rows", "Columns", "AllowSelection", "AllowOrdering", "CaptionExpression", "Caption", "Event", "OnClickEvent"),
            ["gxTab"]          = WithCommon("CaptionExpression", "Caption", "Event", "OnClickEvent", "Selected", "ReturnOnClick"),
            ["gxCard"]         = WithCommon("CaptionExpression", "Caption", "Event", "OnClickEvent", "ReturnOnClick"),
            ["gxGroup"]        = WithCommon("CaptionExpression", "Caption", "Event", "OnClickEvent", "ReturnOnClick"),
            ["gxEmbeddedPage"] = WithCommon("ObjectCall", "Event", "OnClickEvent", "ReturnOnClick"),
            ["tr"]             = new[] { "Height", "align", "vAlign" },
            ["td"]             = new[] { "id", "ColSpan", "RowSpan", "Width", "Height", "HAlign", "VAlign", "ClassRef", "Class", "align", "vAlign", "nowrap", "title" },
            ["row"]            = new[] { "Height", "HAlign", "VAlign" },
            ["cell"]           = new[] { "id", "ColSpan", "RowSpan", "Width", "Height", "HAlign", "VAlign", "ClassRef", "Class", "align", "vAlign", "nowrap" },
        };

        // null = no hint registered for this element; caller treats as "SDK is authoritative".
        public static string[] GetAcceptedAttributes(string elementName)
        {
            if (string.IsNullOrEmpty(elementName)) return null;
            return _accepted.TryGetValue(elementName, out var attrs) ? attrs : null;
        }

        // Walks the XML, flags any attribute outside the element's accept-list. Silent on
        // parse failure (the writer surfaces XML errors via a more specific code path).
        public static List<SuspectAttribute> ScanForRejectedAttributes(string xml)
        {
            var hits = new List<SuspectAttribute>();
            if (string.IsNullOrWhiteSpace(xml)) return hits;
            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch { return hits; }

            foreach (var el in doc.Descendants())
            {
                var accepted = GetAcceptedAttributes(el.Name.LocalName);
                bool hasHint = accepted != null;
                var acceptedSet = hasHint
                    ? new HashSet<string>(accepted, StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Issue #360: a bare `format`/`GxFormat` attribute is the specific trap.
                // The SDK persists it (so the write verifies) and the generator ignores it,
                // because the value only reaches generation through the custom-properties
                // carrier. That is the worst possible failure: reported success, no effect.
                // Flagged before the generic hint check, because on an element with no hint
                // registered the generic pass says nothing and the attribute would slip
                // through entirely.
                if (IsCustomPropertyOnlyAttribute(el, acceptedSet))
                {
                    hits.Add(new SuspectAttribute
                    {
                        Element = el.Name.LocalName,
                        Attribute = CustomPropertyOnlySurfaces(el),
                        Reason = BuildCustomPropertyReason(el),
                        Fix = CustomPropertiesAttribute + " payload (<Properties><Property>"
                              + "<Name>GxFormat</Name><Value>Raw HTML</Value></Property></Properties>)"
                    });
                }

                if (!hasHint) continue; // no hint registered → can't judge the rest
                foreach (var a in el.Attributes())
                {
                    if (acceptedSet.Contains(a.Name.LocalName)) continue;
                    if (string.Equals(a.Name.LocalName, CustomPropertiesAttribute,
                            StringComparison.OrdinalIgnoreCase))
                        continue; // the legitimate carrier; not an authoring mistake
                    // Already reported by the specific pass above, with a fix attached.
                    // Reporting it again as "unverified" would bury the actionable one.
                    if (IsCustomPropertyOnlyName(a.Name.LocalName)) continue;
                    hits.Add(new SuspectAttribute
                    {
                        Element = el.Name.LocalName,
                        Attribute = a.Name.LocalName,
                        Reason = "Attribute is not in the hint table (unverified); SDK persistence is not guaranteed.",
                    });
                }
            }
            return hits;
        }

        /// <summary>
        /// Whether this element carries a control property bare, when the generator only
        /// reads that property from the custom-properties carrier.
        ///
        /// <para>
        /// The element's own hint table is the deciding evidence, and it is consulted for
        /// the property id as well as the display name. gxTextBlock lists both
        /// <c>GxFormat</c> and <c>Format</c> because the legacy shape really does persist
        /// the value as a plain attribute — that was observed live, and warning there
        /// would be a false positive on a control that works. <c>textblock</c>, the modern
        /// element, has no entry at all, so there the payload is the only representation
        /// and the bare attribute is flagged.
        /// </para>
        ///
        /// <para>
        /// An element that already carries the carrier is never flagged: that is exactly
        /// what the IDE writes, and a warning on correct input teaches the caller to
        /// ignore the warning.
        /// </para>
        /// </summary>
        private static bool IsCustomPropertyOnlyAttribute(XElement el, HashSet<string> acceptedSet)
        {
            if (HasCustomPropertiesCarrier(el)) return false;

            // Identity attributes are never property surfaces. Without this the first one
            // walked (controlName) would decide the answer and no diagnostic was produced.
            foreach (var a in el.Attributes())
            {
                if (IsIdentityAttribute(a.Name.LocalName)) continue;
                if (string.Equals(a.Name.LocalName, CustomPropertiesAttribute,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                if (IsCustomPropertyOnlyName(a.Name.LocalName) && !acceptedSet.Contains(a.Name.LocalName))
                    return true;
            }
            return false;
        }

        private static bool IsIdentityAttribute(string localName)
        {
            foreach (var identity in _commonCtrlAttrs)
            {
                if (string.Equals(identity, "ControlType", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(identity, localName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// Whether this attribute name is one of the custom-property-carried controls,
        /// by property id or display name.
        /// </summary>
        private static bool IsCustomPropertyOnlyName(string localName)
        {
            if (_customPropertyOnlyControls.ContainsKey(localName)) return true;
            return _customPropertyOnlyControls.Values.Any(v =>
                string.Equals(v, localName, StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasCustomPropertiesCarrier(XElement el)
        {
            foreach (var a in el.Attributes())
            {
                if (string.Equals(a.Name.LocalName, CustomPropertiesAttribute,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The attribute spelling actually present on the element, so the diagnostic names
        /// what the caller wrote rather than a canonical form it did not.
        /// </summary>
        private static string CustomPropertyOnlySurfaces(XElement el)
        {
            foreach (var a in el.Attributes())
            {
                string local = a.Name.LocalName;
                if (IsIdentityAttribute(local)) continue;
                if (string.Equals(local, CustomPropertiesAttribute,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsCustomPropertyOnlyName(local)) return local;
            }
            return "GxFormat";
        }

        private static string BuildCustomPropertyReason(XElement el)
        {
            return "Control property '" + CustomPropertyOnlySurfaces(el) + "' is carried by "
                + CustomPropertiesAttribute + " on a WebForm, not as a bare XML attribute. "
                + "The SDK persists this attribute and the write verifies, but the generator "
                + "reads the property from the custom-properties payload, so a bare value has "
                + "no effect on the generated code.";
        }

        public sealed class SuspectAttribute
        {
            public string Element;
            public string Attribute;
            public string Reason;

            /// <summary>
            /// What to write instead, when the diagnostic knows one. Null when it does not.
            /// </summary>
            public string Fix;
        }

        // ── Task 4.5 (v2.3.8) — Ghost-binding diagnostics + [var:N] resolver ─────
        //
        // GeneXus layout XML references variables by an internal numeric id
        // (e.g. AttID="var:64"). When the SDK rejects a delete/modify because a
        // control still binds to the variable, the surfaced message embeds the
        // raw [var:N] token. ResolveVarBindings substitutes those tokens with
        // the symbolic "&Name" form so callers don't have to perform a manual
        // lookup. Unknown ids are tagged "[var:N (unresolved)]" rather than
        // silently dropped, keeping the diagnostic actionable.

        private static readonly Regex _varBindingRegex = new Regex(
            @"\[var:(\d+)\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Substitutes <c>[var:N]</c> tokens in <paramref name="message"/> with
        /// the matching variable's symbolic name (<c>&amp;Name</c>) using
        /// <see cref="VariableInjector.GetVariableInternalId"/> against the
        /// object's VariablesPart. Returns <paramref name="message"/> unchanged
        /// when null/empty; ids that don't resolve become <c>[var:N (unresolved)]</c>.
        /// </summary>
        public static string ResolveVarBindings(string message, KBObject obj)
        {
            if (string.IsNullOrEmpty(message)) return message;
            return ResolveVarBindings(message, BuildLookup(obj));
        }

        /// <summary>
        /// Testable overload: takes a delegate mapping internal id → variable
        /// name (or null when unknown). The tests assembly does not reference
        /// <c>Artech.Genexus.Common</c>, so the KBObject overload cannot be
        /// exercised in unit tests without an SDK install.
        /// </summary>
        public static string ResolveVarBindings(string message, Func<int, string> lookup)
        {
            if (string.IsNullOrEmpty(message)) return message;
            if (lookup == null) lookup = _ => null;
            return _varBindingRegex.Replace(message, m =>
            {
                if (!int.TryParse(m.Groups[1].Value, out var id)) return m.Value;
                var name = lookup(id);
                return !string.IsNullOrEmpty(name)
                    ? "&" + name
                    : $"[var:{id} (unresolved)]";
            });
        }

        /// <summary>
        /// Returns the symbolic variable name for the given internal id, or
        /// null when no variable on the object matches.
        /// </summary>
        public static string LookupVarNameById(KBObject obj, int id)
        {
            return BuildLookup(obj)(id);
        }

        // Walks VariablesPart once, caching id → name so a regex with multiple
        // [var:N] tokens doesn't re-iterate the part. Returns a delegate that
        // always answers null when the part is missing.
        private static Func<int, string> BuildLookup(KBObject obj)
        {
            if (obj == null) return _ => null;
            global::Artech.Genexus.Common.Parts.VariablesPart part;
            try { part = Structure.PartAccessor.GetVariablesPart(obj); }
            catch { return _ => null; }
            if (part == null) return _ => null;

            var map = new Dictionary<int, string>();
            int index = 1;
            foreach (var v in part.Variables)
            {
                int? id = VariableInjector.GetVariableInternalId(v, index);
                if (id.HasValue && !map.ContainsKey(id.Value))
                    map[id.Value] = v.Name;
                index++;
            }
            return key => map.TryGetValue(key, out var name) ? name : null;
        }

        /// <summary>
        /// Scans <paramref name="webFormXml"/> for attribute values referencing
        /// <c>var:<paramref name="variableId"/></c> (typically AttID), returning
        /// one entry per matching element with its element name and the nearest
        /// <c>id</c>/<c>name</c> attribute. Used to build the <c>bindings</c>
        /// array on <c>BoundToControls</c> errors. Returns an empty list on
        /// parse failure or no matches.
        /// </summary>
        public static List<VarBinding> FindVarBindings(string webFormXml, int variableId)
        {
            var hits = new List<VarBinding>();
            if (string.IsNullOrWhiteSpace(webFormXml) || variableId <= 0) return hits;
            XDocument doc;
            try { doc = XDocument.Parse(webFormXml); }
            catch { return hits; }

            string needle = "var:" + variableId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var el in doc.Descendants())
            {
                foreach (var a in el.Attributes())
                {
                    var val = a.Value;
                    if (string.IsNullOrEmpty(val)) continue;
                    if (!TokenMatch(val, needle)) continue;
                    string controlId = el.Attribute("id")?.Value;
                    string controlName = el.Attribute("name")?.Value ?? el.Attribute("Name")?.Value;
                    hits.Add(new VarBinding
                    {
                        Element = el.Name.LocalName,
                        Attribute = a.Name.LocalName,
                        ControlId = controlId,
                        ControlName = controlName,
                    });
                    break; // one entry per element — avoid duplicates from sibling attrs
                }
            }
            return hits;
        }

        // True iff needle appears as a whole token (start/end of string or
        // delimited by non-digit chars). Prevents matching var:6 inside var:64.
        private static bool TokenMatch(string value, string needle)
        {
            int idx = 0;
            while ((idx = value.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                int end = idx + needle.Length;
                bool leftOk = idx == 0 || !char.IsLetterOrDigit(value[idx - 1]);
                bool rightOk = end >= value.Length || !char.IsDigit(value[end]);
                if (leftOk && rightOk) return true;
                idx = end;
            }
            return false;
        }

        public sealed class VarBinding
        {
            public string Element;
            public string Attribute;
            public string ControlId;
            public string ControlName;
        }
    }
}
