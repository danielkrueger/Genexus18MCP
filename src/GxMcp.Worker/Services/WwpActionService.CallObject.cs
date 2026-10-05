using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // add_user_action callObject / popup / parameters (#421): a form button that opens another
    // object instead of firing its derived Do<actionName> event.
    public sealed partial class WwpActionService
    {
        // Objects a button can open. A Folder, Table, Module or Attribute may share the name.
        private static readonly string[] CallableTypes = { "WebPanel", "Procedure", "Transaction", "SDPanel", "WebComponent" };

        internal static bool IsCallableType(string typeName)
            => CallableTypes.Any(t => string.Equals(t, typeName, StringComparison.OrdinalIgnoreCase));

        /// <summary>"Name" or "Type:Name".</summary>
        internal static void SplitCallObject(string spec, out string type, out string name)
        {
            type = null;
            name = spec?.Trim();
            int colon = name?.IndexOf(':') ?? -1;
            if (colon > 0)
            {
                type = name.Substring(0, colon).Trim();
                name = name.Substring(colon + 1).Trim();
            }
        }

        internal static bool HasCallObject(JObject args) => !string.IsNullOrWhiteSpace(args?["callObject"]?.ToString());

        /// <summary>popup and parameters only make sense together with callObject, and not with procedure.</summary>
        internal static JObject ValidateCallObjectArgs(JObject args)
        {
            bool hasCall = HasCallObject(args);
            bool hasPopup = args?["popup"] != null && args["popup"].Type != JTokenType.Null;
            bool hasParameters = args?["parameters"] != null && args["parameters"].Type != JTokenType.Null;
            if (!hasCall)
                return hasPopup || hasParameters
                    ? Error("FormActionCallObjectRequired", "popup and parameters apply only with callObject.")
                    : null;
            if (!string.IsNullOrWhiteSpace(args["procedure"]?.ToString()))
                return Error("FormActionProcedureConflict", "callObject and procedure are mutually exclusive.");
            if (hasPopup && !TryReadPopup(args, out _))
                return Error("InvalidPopup", "popup must be true or false.");
            if (hasParameters && !TryReadParameters(args, out _))
                return Error("InvalidParameters", "parameters must be an array of non-empty strings (attribute, &variable or expression).");
            return null;
        }

        internal static bool TryReadPopup(JObject args, out bool? popup)
        {
            popup = null;
            JToken token = args?["popup"];
            if (token == null || token.Type == JTokenType.Null) return true;
            if (token.Type == JTokenType.Boolean) { popup = token.Value<bool>(); return true; }
            if (token.Type == JTokenType.String && bool.TryParse(token.Value<string>(), out bool parsed)) { popup = parsed; return true; }
            return false;
        }

        internal static bool TryReadParameters(JObject args, out List<string> parameters)
        {
            parameters = new List<string>();
            JToken token = args?["parameters"];
            if (token == null || token.Type == JTokenType.Null) return true;
            if (!(token is JArray array)) return false;
            foreach (JToken item in array)
            {
                string value = item.Type == JTokenType.String ? item.Value<string>() : null;
                if (string.IsNullOrWhiteSpace(value)) return false;
                parameters.Add(value.Trim());
            }
            return true;
        }

        private KBObject ResolveCallObject(string spec)
        {
            SplitCallObject(spec, out string type, out string name);
            if (string.IsNullOrWhiteSpace(name)) return null;
            KBObject obj = _objects.FindObject(name, type);
            return obj != null && IsCallableType(obj.TypeDescriptor?.Name) ? obj : null;
        }

        private static void ApplyCallObjectXml(XElement action, JObject args)
        {
            action.SetAttributeValue("gxobject", args["_callObjectReference"]?.ToString());
            TryReadPopup(args, out bool? popup);
            if (popup.HasValue) action.SetAttributeValue("popup", popup.Value ? "True" : "False");
            TryReadParameters(args, out List<string> parameters);
            if (parameters.Count == 0) return;
            XNamespace ns = action.GetDefaultNamespace();
            action.Add(new XElement(ns + "parameters", parameters.Select(p => new XElement(ns + "parameter", new XAttribute("name", p)))));
        }

        private static void ApplyCallObjectNative(object created, JObject args, KBObject callObject)
        {
            if (!Helpers.PatternSemanticAttributeWriter.ApplySemanticAttributeObject(created, "gxobject", callObject))
                throw new WwpTabException("WwpAttributeRejected", "WorkWithPlus rejected the called object as the user action's gxobject.");
            TryReadPopup(args, out bool? popup);
            if (popup.HasValue
                && !Helpers.PatternSemanticAttributeWriter.ApplySemanticAttributeObject(created, "popup", popup.Value))
                throw new WwpTabException("WwpAttributeRejected", "WorkWithPlus rejected the popup flag on the user action.");
            TryReadParameters(args, out List<string> parameters);
            if (parameters.Count == 0) return;
            object container = CreateNativeChild(created, "parameters");
            foreach (string parameter in parameters)
            {
                object element = CreateNativeChild(container, "parameter");
                SetNativeAttribute(element, "name", parameter);
                ExecuteElementCommand(container, element, "AddElementCommand", null);
            }
            ExecuteElementCommand(created, container, "AddElementCommand", null);
        }

        /// <summary>Fails when the re-read action lost the called object, the popup flag or the parameter order.</summary>
        internal static JObject VerifyCallObject(XElement action, string reference, bool? popup, IList<string> parameters)
        {
            if (!string.Equals(Attr(action, "gxobject"), reference, StringComparison.OrdinalIgnoreCase))
                return FormActionError("WwpFormActionNotPersisted", "The re-read UserAction does not retain the called object.");
            if (popup.HasValue && !string.Equals(Attr(action, "popup"), popup.Value ? "True" : "False", StringComparison.OrdinalIgnoreCase))
                return FormActionError("WwpFormActionNotPersisted", "The re-read UserAction does not retain the popup flag.");
            List<string> persisted = action.Elements().Where(e => Is(e, "parameters"))
                .SelectMany(p => p.Elements().Where(e => Is(e, "parameter"))).Select(e => Attr(e, "name")).ToList();
            if (!persisted.SequenceEqual(parameters ?? new List<string>(), StringComparer.Ordinal))
                return FormActionError("WwpFormActionNotPersisted", "The re-read UserAction does not retain the parameters in order.");
            return null;
        }
    }
}
