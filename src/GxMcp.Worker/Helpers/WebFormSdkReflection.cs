using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Reflection and node-resolution helpers shared by the two typed WebForm
    /// writers (<see cref="WebFormTypedPropertyWriter"/> and
    /// <see cref="WebFormTypedCreateRouter"/>).
    ///
    /// They were duplicated once and the copies had already drifted: one
    /// <c>FindElementInPartDoc</c> matched <c>id | ControlName | controlName |
    /// InternalName | name</c> and accepted a <c>|</c>-separated name list while the
    /// other matched only three of those attributes; one tag index preferred the
    /// <c>(KBObject, XmlDocument)</c> <c>EnumerateWebTag</c> overload and the other
    /// only looked for the single-argument form. A control that resolved on the
    /// property path could therefore miss on the create path for reasons no test
    /// could explain. One implementation, one set of semantics, both writers.
    /// </summary>
    internal static class WebFormSdkReflection
    {
        internal const string HelperTypeName = "Artech.Genexus.Common.Parts.WebForm.WebFormHelper";

        /// <summary>Attribute names that can carry a control's identity, most specific first.</summary>
        private static readonly string[] IdentityAttributes =
            { "id", "ControlName", "controlName", "InternalName", "name" };

        /// <summary>
        /// The part's live <c>XmlDocument</c>. The <c>m_Document</c> FIELD is read
        /// before the <c>Document</c> property because the property may clone, and the
        /// SDK's save pass iterates the field — a node that only exists in a clone is
        /// not persisted.
        /// </summary>
        internal static XmlDocument GetPartDocument(object webFormPart)
        {
            try
            {
                var docField = webFormPart?.GetType().GetField("m_Document",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var fromField = docField?.GetValue(webFormPart) as XmlDocument;
                if (fromField != null) return fromField;
            }
            catch { }
            return GetReadProperty(webFormPart, "Document") as XmlDocument;
        }

        /// <summary>
        /// Resolves the owning KBObject across the member names the SDK has used for
        /// it across majors.
        /// </summary>
        internal static object GetKbObject(object webFormPart)
        {
            return GetReadProperty(webFormPart, "KBObject")
                ?? GetReadProperty(webFormPart, "ContainerObject")
                ?? GetReadProperty(webFormPart, "Parent")
                ?? GetReadProperty(webFormPart, "Container");
        }

        /// <summary>
        /// Finds a control element in the part document by identity. <paramref name="identity"/>
        /// may be a single name or a <c>|</c>-separated list of aliases; the first element
        /// carrying any of them wins.
        /// </summary>
        internal static XmlElement FindElementInPartDoc(XmlDocument doc, string identity)
        {
            if (doc?.DocumentElement == null || string.IsNullOrWhiteSpace(identity)) return null;
            var names = identity.Split('|');
            XmlNodeList candidates;
            try { candidates = doc.SelectNodes("//*"); }
            catch { return null; }
            if (candidates == null) return null;

            foreach (XmlNode candidate in candidates)
            {
                if (!(candidate is XmlElement element)) continue;
                foreach (var attribute in IdentityAttributes)
                {
                    string value = GetAttribute(element, attribute);
                    if (value == null) continue;
                    if (names.Any(alias => string.Equals(value, alias, StringComparison.OrdinalIgnoreCase)))
                        return element;
                }
            }
            return null;
        }

        /// <summary>An attribute value, or null when absent or empty.</summary>
        internal static string GetAttribute(XmlElement element, string name)
        {
            if (element == null || string.IsNullOrEmpty(name)) return null;
            var attribute = element.GetAttribute(name);
            return string.IsNullOrEmpty(attribute) ? null : attribute;
        }

        /// <summary>
        /// The control's author-stable identity: <c>ControlName</c> before <c>id</c>,
        /// because ids are regenerated when the SDK materializes a replaced subtree.
        /// </summary>
        internal static string GetControlIdentity(XmlElement element)
        {
            return GetAttribute(element, "ControlName")
                ?? GetAttribute(element, "controlName")
                ?? GetAttribute(element, "id")
                ?? GetAttribute(element, "InternalName");
        }

        /// <summary>
        /// The part's live tag set plus the document and KBObject the enumeration was
        /// rooted in. Both typed writers need the document as well as the tags, so it
        /// travels with the result instead of being re-resolved (and possibly resolved
        /// differently) at each call site.
        /// </summary>
        internal sealed class TagEnumeration
        {
            /// <summary>Tags by every identity they carry (id and ControlName).</summary>
            public IDictionary<string, object> Tags { get; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            /// <summary>
            /// The distinct tags in enumeration order. Use this to visit every tag once —
            /// <see cref="Tags"/> holds each tag under every identity it answers to, so
            /// iterating its values would process a control twice.
            /// </summary>
            public List<object> OrderedTags { get; } = new List<object>();
            public XmlDocument PartDocument { get; internal set; }
            public object KbObject { get; internal set; }
            /// <summary>Which EnumerateWebTag overload bound, for diagnostics.</summary>
            public string Overload { get; internal set; }
            public int TagCount { get; internal set; }
            public string Failure { get; internal set; }
            public bool Succeeded => Failure == null;
        }

        /// <summary>
        /// Enumerates the part's live <c>IWebTag</c>s and indexes them by every identity
        /// they carry, so a caller can resolve a tag by id or by ControlName without
        /// knowing which one the SDK used.
        ///
        /// The <c>(KBObject, XmlDocument)</c> <c>EnumerateWebTag</c> overload is
        /// preferred when the part exposes both — it roots the tags in the document the
        /// SDK actually persists — with the single-argument form as the fallback.
        /// </summary>
        internal static TagEnumeration IndexLiveTags(object webFormPart)
        {
            var result = new TagEnumeration();
            if (webFormPart == null) { result.Failure = "part is null"; return result; }

            Type helperType = FindType(HelperTypeName);
            if (helperType == null) { result.Failure = HelperTypeName + " type not loaded"; return result; }

            result.PartDocument = GetPartDocument(webFormPart);
            result.KbObject = GetKbObject(webFormPart);

            MethodInfo enumerate = null;
            object[] enumArgs = null;
            if (result.PartDocument != null && result.KbObject != null)
            {
                enumerate = FindEnumerateWebTag(helperType, ps =>
                    ps.Length == 2 && ps[1].ParameterType == typeof(XmlDocument) && ps[0].ParameterType.IsInstanceOfType(result.KbObject));
                if (enumerate != null) enumArgs = new object[] { result.KbObject, result.PartDocument };
            }
            if (enumerate == null)
            {
                enumerate = FindEnumerateWebTag(helperType, ps =>
                    ps.Length == 1 && ps[0].ParameterType.IsInstanceOfType(webFormPart));
                enumArgs = new object[] { webFormPart };
            }
            if (enumerate == null) { result.Failure = "no EnumerateWebTag overload found"; return result; }

            try
            {
                foreach (object tag in (IEnumerable)enumerate.Invoke(null, enumArgs))
                {
                    result.TagCount++;
                    if (tag == null) continue;
                    result.OrderedTags.Add(tag);
                    if (!(GetReadProperty(tag, "Node") is XmlNode node) || node.Attributes == null) continue;
                    string id = node.Attributes["id"]?.Value;
                    string controlName = node.Attributes["ControlName"]?.Value ?? node.Attributes["controlName"]?.Value;
                    if (!string.IsNullOrEmpty(id) && !result.Tags.ContainsKey(id)) result.Tags[id] = tag;
                    if (!string.IsNullOrEmpty(controlName) && !result.Tags.ContainsKey(controlName)) result.Tags[controlName] = tag;
                }
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                result.Failure = "EnumerateWebTag threw: " + inner.GetType().Name + ": " + inner.Message;
                return result;
            }

            result.Overload = "EnumerateWebTag(" + string.Join(",", enumerate.GetParameters().Select(p => p.ParameterType.Name)) + ")";
            return result;
        }

        private static MethodInfo FindEnumerateWebTag(Type helperType, Func<ParameterInfo[], bool> matches)
        {
            return helperType.GetMethods(Compatibility.SdkMemberProbe.Static)
                .FirstOrDefault(m => m.Name == "EnumerateWebTag" && matches(m.GetParameters()));
        }

        /// <summary>Locates an SDK type by full name in the loaded assemblies.</summary>
        internal static Type FindType(string fullName) => SdkReflection.FindType(fullName);

        /// <summary>Reads a parameterless instance property, public or not. Never throws.</summary>
        internal static object GetReadProperty(object target, string name)
        {
            if (target == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                var property = target.GetType().GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property == null || !property.CanRead || property.GetIndexParameters().Length != 0) return null;
                return property.GetValue(target);
            }
            catch { return null; }
        }

        /// <summary>Reads a field, public or not. Never throws.</summary>
        internal static object GetFieldValue(object target, string name)
        {
            if (target == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                return target.GetType().GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);
            }
            catch { return null; }
        }

        /// <summary>Invokes a no-argument instance method, unwrapping the reflection wrapper.</summary>
        internal static object InvokeNoArgs(object target, string name)
        {
            if (target == null) return null;
            try
            {
                var method = target.GetType().GetMethod(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null, types: Type.EmptyTypes, modifiers: null);
                return method?.Invoke(target, null);
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
            catch { return null; }
        }
    }
}
