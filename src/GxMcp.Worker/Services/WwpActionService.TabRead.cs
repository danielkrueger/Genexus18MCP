using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // Read-only views of a WorkWithPlus instance's tabs (#426): what it has, what the pattern
    // allows, and what the instance's own tabs agree on. No recommendation is made.
    public sealed partial class WwpActionService
    {
        private static readonly Dictionary<string, string> TabKindElements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["grid"] = "gridTab",
            ["tabular"] = "tabularTab",
            ["webcomponent"] = "webComponentTab"
        };

        private static bool IsTabRead(string operation) => operation == "list_tabs" || operation == "tab_schema";

        internal static bool IsTabElement(string localName)
            => localName != null && (localName.Equals("tab", StringComparison.OrdinalIgnoreCase)
                || (localName.Length > 3 && localName.EndsWith("Tab", StringComparison.Ordinal)));

        internal static string TabKind(string localName)
        {
            foreach (var pair in TabKindElements)
                if (pair.Value.Equals(localName, StringComparison.OrdinalIgnoreCase)) return pair.Key;
            return localName.Equals("tab", StringComparison.OrdinalIgnoreCase) ? "tab" : localName;
        }

        private static bool IsKeyTabAttribute(string name)
            => !name.StartsWith("default", StringComparison.OrdinalIgnoreCase)
               && !name.Equals("childrenOrderedList", StringComparison.OrdinalIgnoreCase);

        internal static JObject ListTabs(XDocument document)
        {
            var tabs = new JArray();
            var byKind = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement tab in document.Descendants().Where(e => IsTabElement(e.Name.LocalName)))
            {
                string kind = TabKind(tab.Name.LocalName);
                var attributes = tab.Attributes().Where(a => IsKeyTabAttribute(a.Name.LocalName))
                    .ToDictionary(a => a.Name.LocalName, a => a.Value);
                string name = Attr(tab, "ControlName");
                if (name.Length == 0) name = Attr(tab, "name");
                tabs.Add(new JObject
                {
                    ["name"] = name,
                    ["kind"] = kind,
                    ["element"] = tab.Name.LocalName,
                    ["position"] = tab.Parent == null ? 0 : tab.Parent.Elements().Where(e => IsTabElement(e.Name.LocalName)).ToList().IndexOf(tab),
                    ["attributes"] = JObject.FromObject(attributes)
                });
                if (!byKind.TryGetValue(kind, out var list)) byKind[kind] = list = new List<Dictionary<string, string>>();
                list.Add(attributes);
            }
            return new JObject
            {
                ["tabs"] = tabs,
                ["tabCount"] = tabs.Count,
                ["conventions"] = Conventions(byKind)
            };
        }

        // Only what every observed tab of a kind shares (same attribute, same value); a kind seen
        // once has nothing to agree with, so it reports nothing.
        internal static JObject Conventions(Dictionary<string, List<Dictionary<string, string>>> byKind)
        {
            var result = new JObject();
            foreach (var pair in byKind)
            {
                if (pair.Value.Count < 2) continue;
                var shared = new JObject();
                foreach (var attribute in pair.Value[0])
                    if (pair.Value.All(other => other.TryGetValue(attribute.Key, out string v) && v == attribute.Value))
                        shared[attribute.Key] = attribute.Value;
                if (shared.Count > 0) result[pair.Key] = shared;
            }
            return result;
        }

        private string RunTabRead(string target, KBObject instance, KBObjectPart instancePart, string xml, string operation, JObject args)
        {
            if (operation == "list_tabs")
                return McpResponse.Ok(target: target, code: "WwpTabsRead", result: new JObject
                {
                    ["instance"] = instance.Name,
                    ["tabs"] = ListTabs(XDocument.Parse(xml, LoadOptions.PreserveWhitespace))
                });

            string kind = args?["kind"]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(kind) || !TabKindElements.TryGetValue(kind, out string element))
                return McpResponse.Err(code: "InvalidTabKind", message: "kind must be grid, tabular or webcomponent.", target: target);

            JObject schema = ReadNativeTabSchema(instancePart, element);
            if (schema["error"] != null)
                return McpResponse.Err(code: schema["code"].ToString(), message: schema["error"].ToString(), target: target, extra: schema);
            schema["kind"] = kind;
            schema["element"] = element;
            return McpResponse.Ok(target: target, code: "WwpTabSchemaRead", result: schema);
        }

        // The pattern's own definition of an element type is reachable from a native element of
        // that type. A kind the instance has no element of, or a package that does not expose its
        // definition, is a typed error: never an empty schema that reads as "nothing is allowed".
        private static JObject ReadNativeTabSchema(KBObjectPart part, string element)
        {
            object root = GetProperty(part, "RootElement");
            if (root == null) return TabError("WwpNativeRootUnavailable", "PatternInstance RootElement is unavailable.");
            object sample = Walk(root).FirstOrDefault(e => NativeType(e).Equals(element, StringComparison.OrdinalIgnoreCase)
                || NativeElementXml(e)?.Name.LocalName.Equals(element, StringComparison.OrdinalIgnoreCase) == true);
            if (sample == null)
                return TabError("WwpTabKindNotInInstance",
                    "The instance has no " + element + " to read the pattern definition from; add one first or read a sibling instance.");

            object definition = GetProperty(sample, "ElementType") ?? GetProperty(sample, "Definition");
            if (definition == null)
                return TabError("WwpPatternSchemaUnavailable",
                    "The installed WorkWithPlus package does not expose the element type definition.");

            var attributes = Names(GetProperty(definition, "Attributes"));
            var children = Names(GetProperty(definition, "Elements") ?? GetProperty(definition, "Children"));
            if (attributes.Count == 0 && children.Count == 0)
                return TabError("WwpPatternSchemaUnavailable",
                    "The element type definition was found but lists no attributes or child elements.");
            return new JObject { ["attributes"] = new JArray(attributes), ["children"] = new JArray(children) };
        }

        private static List<string> Names(object collection)
        {
            var names = new List<string>();
            if (!(collection is IEnumerable items)) return names;
            foreach (object item in items)
            {
                string name = GetProperty(item, "Name")?.ToString() ?? item?.ToString();
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
            return names;
        }
    }
}
