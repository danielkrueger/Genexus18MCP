using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services.Structure
{
    /// <summary>
    /// "Show in Default Forms" belongs to the attribute inside the Transaction (Structure row),
    /// stored under <c>IncludeInForms</c> in its property bag, not to the global Attribute (#425).
    /// Reached by reflection so a major without the SDK helpers degrades to "not readable".
    /// </summary>
    internal static class TransactionAttributeFormsProperty
    {
        internal const string JsonName = "showInDefaultForms";
        internal const string PropertyKey = "IncludeInForms";
        private const string HelperTypeName = "Artech.Genexus.Common.Properties+TransactionAttribute";

        private static MethodInfo Helper(string name)
        {
            Type type = typeof(Artech.Genexus.Common.Objects.Transaction).Assembly.GetType(HelperTypeName);
            return type?.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
        }

        private static object Bag(object transactionAttribute)
            => transactionAttribute?.GetType().GetProperty("Properties")?.GetValue(transactionAttribute, null);

        internal static bool TryRead(object transactionAttribute, out bool value)
        {
            value = false;
            try
            {
                MethodInfo getter = Helper("GetShowInDefaultForms");
                object bag = Bag(transactionAttribute);
                if (getter == null || bag == null) return false;
                value = (bool)getter.Invoke(null, new[] { bag });
                return true;
            }
            catch { return false; }
        }

        internal static bool TryWrite(object transactionAttribute, bool value)
        {
            try
            {
                MethodInfo setter = Helper("SetShowInDefaultForms");
                object bag = Bag(transactionAttribute);
                if (setter == null || bag == null) return false;
                setter.Invoke(null, new[] { bag, (object)value });
                return true;
            }
            catch { return false; }
        }

        /// <summary>A JSON boolean (or the strings "true"/"false"); anything else is not accepted.</summary>
        internal static bool TryParse(JToken token, out bool value)
        {
            value = false;
            if (token == null) return false;
            if (token.Type == JTokenType.Boolean) { value = token.Value<bool>(); return true; }
            return token.Type == JTokenType.String && bool.TryParse(token.Value<string>(), out value);
        }

        /// <summary>First requested item (at any depth) whose value is present but not a boolean, or null.</summary>
        internal static string FindInvalid(JArray items)
        {
            foreach (JObject item in (items ?? new JArray()).OfType<JObject>())
            {
                if (item[JsonName] != null && !TryParse(item[JsonName], out _))
                    return item["name"]?.ToString() ?? "?";
                string nested = FindInvalid(item["children"] as JArray);
                if (nested != null) return nested;
            }
            return null;
        }
    }
}
