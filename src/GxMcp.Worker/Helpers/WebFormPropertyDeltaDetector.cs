using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace GxMcp.Worker.Helpers
{
    public sealed class WebFormPropertyDelta
    {
        public string ControlName { get; set; }   // id (preferred) or ControlName from XML
        public string PropertyName { get; set; }  // XML attribute name (= SDK property name)
        public string Value { get; set; }         // raw XML attribute value
    }

    public sealed class WebFormPropertyDeltaResult
    {
        public bool IsSupported { get; set; }
        public IReadOnlyList<WebFormPropertyDelta> Deltas { get; set; }
        public string Reason { get; set; }
        /// <summary>
        /// W1 step 1: every whole-element add/remove/move found by identity-key
        /// matching. Empty when the diff is attribute-only (or unparseable).
        /// Present for diagnostics while <see cref="IsSupported"/> stays false
        /// for structural diffs — the typed writer has no Create/Remove path yet.
        /// </summary>
        public IReadOnlyList<WebFormStructuralChange> StructuralChanges { get; set; }
            = new List<WebFormStructuralChange>();
    }

    /// <summary>What happened to a whole element. A closed set, so it is an enum:
    /// the write gate asks "is every change an add?" and string comparison made that
    /// a question about literals scattered across three files.</summary>
    public enum WebFormStructuralChangeKind
    {
        Added,
        Removed,
        Moved
    }

    public sealed class WebFormStructuralChange
    {
        public WebFormStructuralChangeKind Kind { get; set; }
        public string ControlType { get; set; }  // element local name
        public string ControlName { get; set; }  // id / ControlName / InternalName (may be null)
        public string Path { get; set; }         // parent element path, e.g. /GxMultiForm/Form/body
    }

    /// <summary>
    /// Compares two GxMultiForm XML documents structurally. If they differ ONLY in attribute
    /// values on existing controls (no add/remove/move of elements, no text-node changes),
    /// returns IsSupported=true plus the per-attribute deltas. Whole-element add/remove/move
    /// differences are classified by identity-key matching into StructuralChanges (with a
    /// "structural: …" Reason) while IsSupported stays false — the caller falls back to the
    /// raw XML rewrite path. Attribute deltas are never reported alongside structural
    /// changes, so a consumer can trust a non-empty Deltas as purely attribute-level.
    /// </summary>
    public static class WebFormPropertyDeltaDetector
    {
        public static WebFormPropertyDeltaResult DetectSupportedPropertyDeltas(string currentXml, string updatedXml)
        {
            var empty = new WebFormPropertyDeltaResult
            {
                IsSupported = false,
                Deltas = new List<WebFormPropertyDelta>(),
                StructuralChanges = new List<WebFormStructuralChange>(),
                Reason = "empty input"
            };
            if (string.IsNullOrWhiteSpace(currentXml) || string.IsNullOrWhiteSpace(updatedXml)) return empty;

            XDocument current, updated;
            try
            {
                current = XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace);
                updated = XDocument.Parse(updatedXml, LoadOptions.PreserveWhitespace);
            }
            catch (Exception ex)
            {
                return new WebFormPropertyDeltaResult { IsSupported = false, Deltas = new List<WebFormPropertyDelta>(), StructuralChanges = new List<WebFormStructuralChange>(), Reason = "parse: " + ex.Message };
            }

            var deltas = new List<WebFormPropertyDelta>();
            var structural = new List<WebFormStructuralChange>();
            string failure = null;
            bool ok = CompareElements(current.Root, updated.Root, string.Empty, deltas, structural, ref failure);
            if (structural.Count > 0)
            {
                return new WebFormPropertyDeltaResult
                {
                    IsSupported = false,
                    Deltas = new List<WebFormPropertyDelta>(),
                    StructuralChanges = structural,
                    Reason = SummarizeStructural(structural)
                };
            }
            return new WebFormPropertyDeltaResult
            {
                IsSupported = ok,
                Deltas = ok ? deltas : new List<WebFormPropertyDelta>(),
                StructuralChanges = new List<WebFormStructuralChange>(),
                Reason = ok ? "ok" : failure
            };
        }

        private static string SummarizeStructural(List<WebFormStructuralChange> structural)
        {
            var parts = structural
                .Take(5)
                .Select(c => c.Kind + " <" + (c.ControlType ?? "?") + "> '" + (c.ControlName ?? "?") + "' under " + (c.Path ?? "?"));
            string summary = "structural: " + string.Join("; ", parts);
            if (structural.Count > 5) summary += "; +" + (structural.Count - 5) + " more";
            return summary;
        }

        private static void RecordSubtree(XElement element, WebFormStructuralChangeKind kind, string parentPath, List<WebFormStructuralChange> structural)
        {
            if (element == null) return;
            string here = parentPath + "/" + element.Name.LocalName;
            structural.Add(new WebFormStructuralChange
            {
                Kind = kind,
                ControlType = element.Name.LocalName,
                ControlName = GetControlName(element),
                Path = parentPath
            });
            foreach (XElement child in element.Elements())
            {
                RecordSubtree(child, kind, here, structural);
            }
        }

        /// <summary>
        /// How one parent's children line up between the two documents. Named because
        /// the move test, the structural report and the recursion each need it, and
        /// deriving it three times from parallel arrays is where an off-by-one hides.
        /// </summary>
        private sealed class ChildPairing
        {
            /// <summary>Matched children, in CURRENT document order.</summary>
            public List<(XElement Current, XElement Updated)> Matched { get; } = new List<(XElement, XElement)>();
            public List<XElement> Removed { get; } = new List<XElement>();
            public List<XElement> Added { get; } = new List<XElement>();
            /// <summary>True when the same set of children is present in a different order.</summary>
            public bool HasReorder { get; private set; }

            /// <summary>
            /// Pairs each current child with the first still-unmatched updated child
            /// carrying the same element name + control identity. Leftovers are whole-element
            /// removals / additions. Identical twins (same key twice) pair first-to-first;
            /// genuinely indistinguishable leftovers surface as an add+remove pair rather
            /// than a silent match.
            /// </summary>
            public static ChildPairing Build(List<XElement> currentChildren, List<XElement> updatedChildren)
            {
                var pairing = new ChildPairing();
                var taken = new bool[updatedChildren.Count];
                var positions = new List<int>();

                for (int i = 0; i < currentChildren.Count; i++)
                {
                    string key = ChildKey(currentChildren[i]);
                    int match = -1;
                    for (int j = 0; j < updatedChildren.Count; j++)
                    {
                        if (taken[j]) continue;
                        if (!KeysEqual(key, ChildKey(updatedChildren[j]))) continue;
                        taken[j] = true;
                        match = j;
                        break;
                    }
                    if (match < 0) pairing.Removed.Add(currentChildren[i]);
                    else { pairing.Matched.Add((currentChildren[i], updatedChildren[match])); positions.Add(match); }
                }
                for (int j = 0; j < updatedChildren.Count; j++)
                {
                    if (!taken[j]) pairing.Added.Add(updatedChildren[j]);
                }

                // Document order in the request is the source of truth for display order,
                // so a matching multiset at different positions is a move.
                for (int k = 1; k < positions.Count; k++)
                {
                    if (positions[k] < positions[k - 1]) { pairing.HasReorder = true; break; }
                }
                return pairing;
            }
        }

        private static string ChildKey(XElement element)
        {
            // Pairing identity excludes id: ids regenerate on subtree materialization,
            // so keying on them reports phantom removed+added pairs after every
            // full-tree write. ControlName is author-stable; unnamed same-type
            // siblings pair positionally (first unmatched wins).
            return element.Name.LocalName + "\0" + (PairingName(element) ?? string.Empty);
        }

        private static string PairingName(XElement element)
        {
            return Attr(element, "ControlName") ?? Attr(element, "controlName") ?? Attr(element, "InternalName");
        }

        private static bool KeysEqual(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static bool CompareElements(XElement current, XElement updated, string path, List<WebFormPropertyDelta> deltas, List<WebFormStructuralChange> structural, ref string failure)
        {
            if (current == null || updated == null)
            {
                if (current != updated) { failure = "one side null"; return false; }
                return true;
            }
            if (!string.Equals(current.Name.LocalName, updated.Name.LocalName, StringComparison.Ordinal))
            {
                failure = "element name differs: " + current.Name.LocalName + " vs " + updated.Name.LocalName;
                return false;
            }

            string here = path + "/" + current.Name.LocalName;
            var pairing = ChildPairing.Build(current.Elements().ToList(), updated.Elements().ToList());

            // Record whole subtrees: children of an added/removed element are added or
            // removed with it, and the recursion below only visits paired elements.
            foreach (var removed in pairing.Removed) RecordSubtree(removed, WebFormStructuralChangeKind.Removed, here, structural);
            foreach (var added in pairing.Added) RecordSubtree(added, WebFormStructuralChangeKind.Added, here, structural);

            // Pair identity follows the same stable naming as ChildKey (ids churn on
            // materialization): two paired elements always share it by construction,
            // so this is a cheap invariant, not a second lookup.
            string pairName = PairingName(current);
            string updatedPairName = PairingName(updated);
            if (!string.Equals(pairName, updatedPairName, StringComparison.OrdinalIgnoreCase))
            {
                failure = "control identity differs: '" + (pairName ?? "?") + "' vs '" + (updatedPairName ?? "?") + "'";
                return false;
            }

            // Delta keying stays id-first (GetControlName): the typed writer resolves
            // by id first and ControlName second, and id-only elements (rows/cells)
            // need a key for their attribute deltas.
            string controlName = GetControlName(current);

            if (!CompareAttributes(current, updated, controlName, deltas, ref failure)) return false;

            string currentText = string.Concat(current.Nodes().OfType<XText>().Where(t => !string.IsNullOrWhiteSpace(t.Value)).Select(t => t.Value));
            string updatedText = string.Concat(updated.Nodes().OfType<XText>().Where(t => !string.IsNullOrWhiteSpace(t.Value)).Select(t => t.Value));
            if (!string.Equals(currentText, updatedText, StringComparison.Ordinal))
            {
                failure = "text content differs at <" + current.Name.LocalName + ">";
                return false;
            }

            // Same children in a different order is a move, which the typed writer cannot
            // express either.
            if (pairing.HasReorder)
            {
                // Report the children that actually changed position, not every match:
                // "moved" on an element that stayed put is noise the agent must re-read.
                foreach (var (matchedCurrent, matchedUpdated) in pairing.Matched)
                {
                    if (matchedCurrent == matchedUpdated) continue;
                    structural.Add(new WebFormStructuralChange
                    {
                        Kind = WebFormStructuralChangeKind.Moved,
                        ControlType = matchedCurrent.Name.LocalName,
                        ControlName = GetControlName(matchedCurrent),
                        Path = here
                    });
                }
            }

            foreach (var (matchedCurrent, matchedUpdated) in pairing.Matched)
            {
                if (!CompareElements(matchedCurrent, matchedUpdated, here, deltas, structural, ref failure)) return false;
            }
            return true;
        }

        private static bool CompareAttributes(XElement current, XElement updated, string controlName, List<WebFormPropertyDelta> deltas, ref string failure)
        {
            var currentAttrs = current.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.OrdinalIgnoreCase);
            var updatedAttrs = updated.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.OrdinalIgnoreCase);

            // Newly added attribute
            foreach (var item in updatedAttrs)
            {
                if (IsSdkManagedAttribute(item.Key)) continue;
                if (!currentAttrs.ContainsKey(item.Key))
                {
                    if (string.IsNullOrWhiteSpace(controlName))
                    {
                        failure = "new attribute '" + item.Key + "' on unnamed <" + current.Name.LocalName + ">";
                        return false;
                    }
                    deltas.Add(new WebFormPropertyDelta { ControlName = controlName, PropertyName = item.Key, Value = item.Value });
                }
            }

            // Removed / changed attribute
            foreach (var item in currentAttrs)
            {
                if (IsSdkManagedAttribute(item.Key)) continue;
                string updatedValue;
                if (!updatedAttrs.TryGetValue(item.Key, out updatedValue))
                {
                    if (string.IsNullOrWhiteSpace(controlName))
                    {
                        failure = "removed attribute '" + item.Key + "' on unnamed <" + current.Name.LocalName + ">";
                        return false;
                    }
                    deltas.Add(new WebFormPropertyDelta { ControlName = controlName, PropertyName = item.Key, Value = null });
                    continue;
                }
                if (string.Equals(item.Value, updatedValue, StringComparison.Ordinal)) continue;
                if (string.IsNullOrWhiteSpace(controlName))
                {
                    failure = "attribute '" + item.Key + "' changed on unnamed <" + current.Name.LocalName + ">";
                    return false;
                }
                deltas.Add(new WebFormPropertyDelta { ControlName = controlName, PropertyName = item.Key, Value = updatedValue });
            }

            return true;
        }

        /// <summary>
        /// Attributes the SDK owns: element ids regenerate on subtree materialization,
        /// so they are never authorable deltas (and pairing no longer keys on them).
        /// </summary>
        private static bool IsSdkManagedAttribute(string localName)
        {
            return string.Equals(localName, "id", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetControlName(XElement element)
        {
            // ControlName first: element ids regenerate when the SDK materializes a
            // replaced subtree (observed live: layout/table/row ids differ between the
            // read representation and the stored document), while ControlName is
            // author-stable. Pairing or keying by id would report phantom
            // removed+added pairs after every full-tree write.
            //
            // Issue #360: Attr is case-insensitive, so "controlName" and "ControlName"
            // are one identity rather than two spellings to enumerate here.
            return Attr(element, "ControlName") ?? Attr(element, "id") ?? Attr(element, "InternalName");
        }

        /// <summary>
        /// An attribute's value, or null when absent.
        ///
        /// <para>
        /// Issue #360. This was <c>element.Attribute(name)</c>, which XLinq resolves
        /// case-sensitively, so it needed the explicit <c>?? Attr(el, "controlName")</c>
        /// fallback above — the reason a WebForm written with one casing paired by the
        /// other would look like every control was added and removed at once. One
        /// case-insensitive read removes the need to enumerate spellings.
        /// </para>
        /// </summary>
        private static string Attr(XElement element, string name)
        {
            if (element == null || string.IsNullOrEmpty(name)) return null;

            var attr = element.Attribute(name);
            if (attr != null) return attr.Value;

            foreach (var candidate in element.Attributes())
            {
                if (string.Equals(candidate.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
                    return candidate.Value;
            }
            return null;
        }
    }
}
