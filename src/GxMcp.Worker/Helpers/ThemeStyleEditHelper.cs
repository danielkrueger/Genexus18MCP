using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Structure;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Reflection bridge for the SDK's two style authoring surfaces:
    /// Theme.ThemeStyles (ThemeStylesPart) and the regular DesignStylesPart
    /// used by objects that expose a StyleSheet. The public SDK types changed
    /// namespace/shape between GeneXus updates, while these methods have stayed
    /// stable, so the bridge avoids a version-specific compile dependency.
    /// </summary>
    public static class ThemeStyleEditHelper
    {
        // The SDK lexer can terminate the x86 Worker on long inline data URIs.
        // Keep these below the observed failure range; larger assets belong in Image objects.
        private const int MaxInlineDataUriLength = 1024;

        public static bool Applies(KBObject obj, string partName, out object part)
        {
            part = null;
            if (obj == null) return false;
            try { part = PartAccessor.GetPart(obj, partName); } catch { }

            bool themePart = IsThemeStylesPart(part);
            bool designPart = IsDesignStylesPart(part);
            bool themeName = ThemeInspector.IsTheme(obj)
                && (string.Equals(partName, "ThemeStyles", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(partName, "Theme", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(partName, "Styles", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(partName, "StyleSheet", StringComparison.OrdinalIgnoreCase));
            return themePart || designPart || themeName;
        }

        public static string ReadText(object part)
        {
            if (part == null) return null;
            if (IsThemeStylesPart(part))
            {
                string css = InvokeString(part, "GetCssSource");
                if (css != null) return css;
                css = ReadStringProperty(part, "Source");
                return css ?? string.Empty;
            }
            if (IsDesignStylesPart(part))
                return ReadStringProperty(part, "Source") ?? string.Empty;
            return ReadStringProperty(part, "Source") ?? ReadStringProperty(part, "Content");
        }

        /// <summary>
        /// Returns null when the request is structurally valid, otherwise an
        /// actionable validation message. This is pure and is used by unit tests.
        /// </summary>
        public static string ValidateRequest(string content)
        {
            if (content == null) return "Style content is required.";
            if (content.IndexOf('\0') >= 0) return "Style content contains a NUL character.";

            JObject request = TryParseStructured(content, out string parseError);
            if (parseError != null) return parseError;
            string css = request?["css"]?.ToString() ?? request?["source"]?.ToString();
            if (css != null) return ValidateCss(css);
            if (request != null && JsonUtil.IsSupplied(request["properties"]) && request["properties"].Type != JTokenType.Object)
                return "Style properties must be a JSON object.";
            return request == null ? ValidateCss(content) : null;
        }

        /// <summary>
        /// Applies either raw CSS/source or a structured class edit to an SDK part.
        /// No Save is performed here; the caller owns the transaction and object save.
        /// </summary>
        public static bool TryApply(object part, string content, out JObject details, out string error)
        {
            details = new JObject();
            error = ValidateRequest(content);
            if (error != null) return false;
            if (part == null) { error = "Style part is not available on the object."; return false; }

            JObject request = TryParseStructured(content, out _);
            bool isThemeStyles = IsThemeStylesPart(part);
            bool isDesignStyles = IsDesignStylesPart(part);

            if (request != null && (request["className"] != null || request["name"] != null))
            {
                if (!isThemeStyles)
                {
                    error = "Structured class edits are supported only for a ThemeStylesPart; use source/css for a StyleSheet.";
                    return false;
                }

                string className = request["className"]?.ToString() ?? request["name"]?.ToString();
                object style = Invoke(part, "GetStyle", new object[] { className });
                if (style == null)
                {
                    error = "Theme class not found: " + className;
                    return false;
                }

                int propertiesApplied = 0;
                JObject properties = request["properties"] as JObject;
                if (properties != null)
                {
                    object data = ReadProperty(style, "Data");
                    if (data == null)
                    {
                        error = "Theme class does not expose editable style data: " + className;
                        return false;
                    }
                    foreach (var property in properties.Properties())
                    {
                        if (!TrySetPropertyValue(data, property.Name, property.Value, out error)) return false;
                        propertiesApplied++;
                    }
                }

                if (request["description"] != null && !TrySetMember(style, "Description", request["description"].ToString(), out error)) return false;
                if (request["baseClass"] != null && !TrySetMember(style, "BaseClass", request["baseClass"].ToString(), out error)) return false;
                if (request["css"] != null)
                {
                    if (!TryInvokeTextImport(style, request["css"].ToString(), out error)) return false;
                }

                details["mode"] = "class";
                details["className"] = className;
                details["propertiesApplied"] = propertiesApplied;
                return true;
            }

            string text = request?["css"]?.ToString() ?? request?["source"]?.ToString() ?? content;
            if (isDesignStyles)
            {
                if (!TryValidateNewSource(part, text, out error)) return false;
                PropertyInfo source = part.GetType().GetProperty("Source", BindingFlags.Public | BindingFlags.Instance);
                if (source == null || !source.CanWrite)
                {
                    error = "The StyleSheet part does not expose a writable Source property.";
                    return false;
                }
                source.SetValue(part, text, null);
                details["mode"] = "stylesheet";
                details["sourceLength"] = text?.Length ?? 0;
                return true;
            }

            if (isThemeStyles)
            {
                if (!TryInvokeTextImport(part, text, out error)) return false;
                details["mode"] = "themeCss";
                details["sourceLength"] = text?.Length ?? 0;
                return true;
            }

            error = "The resolved part is not a supported ThemeStylesPart or DesignStylesPart.";
            return false;
        }

        public static string ValidateCss(string css)
        {
            if (css == null) return "Style content is required.";
            string payloadError = ValidateInlineDataUris(css);
            if (payloadError != null) return payloadError;
            int depth = 0;
            bool quoted = false;
            char quote = '\0';
            bool escaped = false;
            for (int i = 0; i < css.Length; i++)
            {
                char c = css[i];
                if (quoted)
                {
                    if (escaped) { escaped = false; continue; }
                    if (c == '\\') { escaped = true; continue; }
                    if (c == quote) quoted = false;
                    continue;
                }
                if (c == '\'' || c == '"') { quoted = true; quote = c; continue; }
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth < 0) return "Style content contains an unmatched closing brace.";
                }
            }
            return depth == 0 ? null : "Style content contains an unmatched opening brace.";
        }

        internal static bool IsStylePartName(string partName) =>
            string.Equals(partName, "Styles", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "StyleSheet", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "ThemeStyles", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "Theme", StringComparison.OrdinalIgnoreCase);

        internal static string ValidateInlineDataUris(string css)
        {
            if (css == null) return null;
            for (int start = css.IndexOf("data:", StringComparison.OrdinalIgnoreCase); start >= 0;)
            {
                if (start > 0 && (char.IsLetterOrDigit(css[start - 1]) || css[start - 1] == '-' || css[start - 1] == '_'))
                {
                    start = css.IndexOf("data:", start + 5, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                bool quotedValue = start > 0 && (css[start - 1] == '\'' || css[start - 1] == '"');
                bool payload = false;
                int end = start;
                while (end < css.Length && !char.IsWhiteSpace(css[end])
                    && css[end] != '\'' && css[end] != '"' && css[end] != ')'
                    && css[end] != '{' && css[end] != '}'
                    && (css[end] != ';' || !payload || quotedValue))
                {
                    if (css[end] == ',') payload = true;
                    if (++end - start > MaxInlineDataUriLength)
                        return "Inline data URIs longer than " + MaxInlineDataUriLength
                            + " characters are not sent to the GeneXus style parser because they can terminate the Worker. Use a GeneXus Image object or an external asset URL.";
                }
                start = css.IndexOf("data:", end, StringComparison.OrdinalIgnoreCase);
            }
            return null;
        }

        private static JObject TryParseStructured(string content, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(content) || content.TrimStart()[0] != '{') return null;
            try { return JObject.Parse(content); }
            catch (Exception ex) { error = "Structured style request is not valid JSON: " + ex.Message; return null; }
        }

        private static bool TryValidateNewSource(object part, string source, out string error)
        {
            error = null;
            try
            {
                MethodInfo method = part.GetType().GetMethod("ValidateNewSource", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                if (method == null) return true;
                object result = method.Invoke(part, new object[] { source });
                if (result is bool && !(bool)result)
                {
                    error = "The GeneXus SDK rejected the StyleSheet source during validation.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "StyleSheet validation failed: " + (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        private static bool TryInvokeTextImport(object target, string text, out string error)
        {
            error = null;
            try
            {
                MethodInfo method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => (m.Name == "ImportCss" || m.Name == "ImportCSS")
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType == typeof(string));
                if (method == null) { error = "The SDK style part does not expose ImportCss(string)."; return false; }
                object result = method.Invoke(target, new object[] { text });
                if (result is bool && !(bool)result)
                {
                    error = "The GeneXus SDK rejected the CSS source.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "The GeneXus SDK rejected the CSS source: " + (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        private static bool TrySetPropertyValue(object data, string name, JToken value, out string error)
        {
            error = null;
            try
            {
                MethodInfo method = data.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "SetPropertyValue" && m.GetParameters().Length == 2);
                if (method == null) { error = "Theme style data does not expose SetPropertyValue for '" + name + "'."; return false; }
                Type targetType = method.GetParameters()[1].ParameterType;
                object converted = ConvertToken(value, targetType);
                method.Invoke(data, new[] { (object)name, converted });
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not set theme property '" + name + "': " + (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        private static object ConvertToken(JToken value, Type targetType)
        {
            if (value == null || value.Type == JTokenType.Null) return null;
            if (targetType == typeof(string) || targetType == typeof(object)) return value.ToString();
            if (targetType.IsEnum) return Enum.Parse(targetType, value.ToString(), true);
            return Convert.ChangeType(value.ToObject<object>(), targetType, CultureInfo.InvariantCulture);
        }

        private static bool TrySetMember(object target, string name, string value, out string error)
        {
            error = null;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanWrite)
                {
                    error = "Theme class does not expose a writable '" + name + "' property.";
                    return false;
                }
                if (property.PropertyType == typeof(string)) property.SetValue(target, value, null);
                else property.SetValue(target, Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture), null);
                return true;
            }
            catch (Exception ex) { error = "Could not set theme member '" + name + "': " + ex.Message; return false; }
        }

        private static object ReadProperty(object target, string name)
        {
            try { return target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target, null); }
            catch { return null; }
        }

        private static string ReadStringProperty(object target, string name)
        {
            try
            {
                PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                return property?.GetValue(target, null)?.ToString();
            }
            catch { return null; }
        }

        private static string InvokeString(object target, string name)
        {
            try
            {
                MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                return method?.Invoke(target, null)?.ToString();
            }
            catch { return null; }
        }

        private static bool IsThemeStylesPart(object part)
        {
            return IsPartType(part, "ThemeStylesPart");
        }

        private static bool IsDesignStylesPart(object part)
        {
            return IsPartType(part, "DesignStylesPart");
        }

        private static bool IsPartType(object part, string typeName)
        {
            return part != null
                && part.GetType().Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static object Invoke(object target, string name, object[] args)
        {
            try
            {
                foreach (MethodInfo method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (method.Name != name || method.GetParameters().Length != (args?.Length ?? 0)) continue;
                    try { return method.Invoke(target, args); } catch { }
                }
            }
            catch { }
            return null;
        }
    }
}
