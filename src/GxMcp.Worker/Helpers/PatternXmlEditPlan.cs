using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    // Pure preflight for raw XML edits. SDK-owned order/defaults are not derivable
    // from document order. Keep the caller's payload intact; never repair metadata.
    public sealed class PatternXmlEditPlan
    {
        public string Xml { get; private set; }
        public string ErrorCode { get; private set; }
        public string Error { get; private set; }
        public JArray Changes { get; } = new JArray();
        public bool IsNoChange => ErrorCode == null && Changes.Count == 0;

        /// <summary>
        /// Element identities added and removed by this plan. Instance state on purpose: a
        /// static pair would leak one plan's move into the next, and these plans are built
        /// concurrently per request.
        /// </summary>
        private readonly Dictionary<string, string> _insertedIdentities =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _removedIdentities =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static PatternXmlEditPlan Create(string currentXml, string requestedXml, bool allowGridStructure = false)
        {
            var plan = new PatternXmlEditPlan { Xml = requestedXml };
            XDocument current, requested;
            try { requested = XDocument.Parse(requestedXml, LoadOptions.PreserveWhitespace); }
            catch (Exception ex) { return plan.Reject("PatternInvalidXml", ex.Message); }
            try { current = XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace); }
            catch (Exception ex) { return plan.Reject("PatternReadFailed", "Cannot compare current pattern: " + ex.Message); }
            var oldOutside = current.Nodes().Where(n => !(n is XElement)).ToArray();
            var newOutside = requested.Nodes().Where(n => !(n is XElement)).ToArray();
            if (current.DocumentType != null || requested.DocumentType != null
                || current.Declaration?.ToString() != requested.Declaration?.ToString()
                || oldOutside.Length != newOutside.Length
                || oldOutside.Where((n, i) => !XNode.DeepEquals(n, newOutside[i])).Any())
                return plan.Reject("PatternStructureChangeUnsupported", "Document declarations or nodes outside the pattern root changed (DTDs are unsupported).");
            plan.Compare(current.Root, requested.Root, "/", allowGridStructure);
            if (plan.ErrorCode == null) plan.PromoteMatchedInsertRemoveAsMove();
            return plan;
        }

        private PatternXmlEditPlan Reject(string code, string detail)
        {
            ErrorCode = code;
            Error = detail;
            Changes.Clear();
            return this;
        }

        private void Compare(XElement before, XElement after, string path, bool allowGridStructure)
        {
            if (ErrorCode != null) return;
            path += before.Name + "/";
            if (before.Name != after.Name)
            {
                Reject("PatternStructureChangeUnsupported", "Element identity changed at " + path);
                return;
            }
            foreach (var name in before.Attributes().Select(a => a.Name).Union(after.Attributes().Select(a => a.Name)))
            {
                var left = before.Attribute(name);
                var right = after.Attribute(name);
                if ((string)left == (string)right) continue;
                if (allowGridStructure
                    && IsGrid(before)
                    && string.Equals(name.LocalName, "childrenOrderedList", StringComparison.OrdinalIgnoreCase))
                {
                    Changes.Add(new JObject
                    {
                        ["path"] = path + "@" + name.LocalName,
                        ["operation"] = "GridOrder",
                        ["before"] = (string)left,
                        ["after"] = (string)right
                    });
                    continue;
                }
                if (IsProtected(name.LocalName) || left?.IsNamespaceDeclaration == true || right?.IsNamespaceDeclaration == true)
                {
                    Reject("PatternMetadataChangeUnsupported", DescribeMetadataChange(path, name.LocalName));
                    return;
                }
                Changes.Add(new JObject { ["path"] = path + "@" + name, ["before"] = (string)left, ["after"] = (string)right });
            }
            var oldNodes = before.Nodes().Where(Significant).ToArray();
            var newNodes = after.Nodes().Where(Significant).ToArray();
            if (oldNodes.Length != newNodes.Length)
            {
                if (allowGridStructure && TryAllowGridVariableInsertion(before, oldNodes, newNodes, path))
                    return;
                if (TryAllowWebComponentInsertion(before, after, oldNodes, newNodes, path))
                    return;
                // Issue #349: structural layout authoring. Insert / remove / move of a
                // single leaf layout element inside an existing container.
                if (TryAllowStructuralLayoutEdit(oldNodes, newNodes, path, allowGridStructure))
                    return;
                Reject("PatternStructureChangeUnsupported", DescribeStructureChange(path, oldNodes, newNodes, allowGridStructure));
                return;
            }
            if (allowGridStructure && IsGrid(before)
                && TryCompareGridReorder(oldNodes, newNodes, path, allowGridStructure))
                return;
            for (int i = 0; i < oldNodes.Length; i++)
            {
                if (oldNodes[i] is XElement oldElement && newNodes[i] is XElement newElement)
                {
                    if (IsMetadata(oldElement.Name.LocalName) && !XNode.DeepEquals(oldElement, newElement))
                        Reject("PatternMetadataChangeUnsupported", "SDK-owned metadata changed at " + path + oldElement.Name);
                    else
                        Compare(oldElement, newElement, path + "[" + i + "]/", allowGridStructure);
                }
                else if (!XNode.DeepEquals(oldNodes[i], newNodes[i]))
                    Reject("PatternStructureChangeUnsupported", "Text or node structure changed at " + path);
                if (ErrorCode != null) return;
            }
        }

        /// <summary>
        /// Explains a protected-attribute rejection in terms of what the caller should do
        /// instead.
        ///
        /// <para>
        /// Issue #349. The old message was "SDK-owned metadata or identity changed at
        /// /instance/.../@childrenOrderedList" and then pointed at "the appropriate SDK
        /// pattern authoring action" - which for this attribute does not exist. The
        /// reporter's move was in fact a correct structural edit that the caller had
        /// completed the hard way: they had also updated both <c>childrenOrderedList</c>
        /// values, because that is what the attribute looks like it wants. It does not.
        /// </para>
        ///
        /// <para>
        /// The SDK rebuilds that list from the children on save - which is why
        /// <c>PatternChildOrderReconciler</c> is deliberately kept out of the write path
        /// (see <c>PatternReconcileAttachmentTests</c>). So the fix is for the caller to
        /// leave it alone, and the message now says so.
        /// </para>
        /// </summary>
        private static string DescribeMetadataChange(string path, string attributeName)
        {
            if (string.Equals(attributeName, "childrenOrderedList", StringComparison.OrdinalIgnoreCase))
            {
                return "Do not edit childrenOrderedList at " + path
                    + ". WorkWithPlus rebuilds it from the child elements on save, so a"
                    + " hand-written value is both unnecessary and the reason a correct"
                    + " move or reorder is rejected. Move or reorder the child element"
                    + " itself and leave this attribute out of the edit; the ordering is"
                    + " then derived from document order.";
            }
            return "SDK-owned metadata or identity changed at " + path + "@" + attributeName
                + ". This attribute is maintained by the SDK and cannot be authored through"
                + " a raw XML edit.";
        }

        /// <summary>
        /// Names what actually changed, because "Child structure changed at /path" was true
        /// and useless: it did not say which child, nor whether the added element was
        /// malformed or simply not on the allow-list.
        /// </summary>
        private static string DescribeStructureChange(
            string path, XNode[] oldNodes, XNode[] newNodes, bool allowGridStructure)
        {
            int delta = newNodes.Length - oldNodes.Length;
            string verb = delta > 0 ? "added" : "removed";
            int count = Math.Abs(delta);

            var added = Names(newNodes.Except(oldNodes, StructuralNodeKeyComparer.Instance));
            var removed = Names(oldNodes.Except(newNodes, StructuralNodeKeyComparer.Instance));

            var sb = new System.Text.StringBuilder();
            sb.Append("Structural layout edit at ").Append(path).Append(": ")
              .Append(count).Append(count == 1 ? " child element " : " child elements ")
              .Append(verb).Append('.');
            if (removed.Count > 0) sb.Append(" Removed: ").Append(string.Join(", ", removed)).Append('.');
            if (added.Count > 0) sb.Append(" Added: ").Append(string.Join(", ", added)).Append('.');

            if (!allowGridStructure
                && added.Any(n => string.Equals(n, "gridVariable", StringComparison.OrdinalIgnoreCase)))
                sb.Append(" gridVariable insertion is only permitted on a grid container.");

            sb.Append(" Structural edits accept one leaf element per container, from the")
              .Append(" permitted kinds, carrying only that kind's documented attributes.")
              .Append(" Nested additions and container-valued additions are rejected because")
              .Append(" their generated layout cannot be predicted from the XML alone.");
            return sb.ToString();
        }

        private static List<string> Names(IEnumerable<XNode> nodes) => nodes
            .OfType<XElement>()
            .Select(e => e.Name.LocalName)
            .ToList();

        /// <summary>
        /// Structural layout authoring: one leaf element added to, or removed from, an
        /// existing container.
        ///
        /// <para>
        /// Issue #349. The reporter could set attributes but not add elements: a
        /// <c>&lt;textBlock&gt;</c> added inside an existing table was refused as
        /// <c>PatternStructureChangeUnsupported</c> even though it touched nothing but
        /// itself. The gate already permitted exactly this shape for two kinds
        /// (<c>gridVariable</c> into a grid, <c>webComponent</c> into a table); this
        /// generalises the same rule to the layout kinds the issue names.
        /// </para>
        ///
        /// <para>
        /// Deliberately narrow, because this cannot be verified against a real
        /// PatternInstance without the WorkWithPlus package installed, and the cost of
        /// being too permissive is a corrupt pattern rather than a rejected edit. So:
        /// exactly one node of difference, that node must be a child element of a
        /// permitted kind, it must be a leaf, and every attribute on it must be in that
        /// kind's allow-list. A nested addition, a container arriving with children, or an
        /// unknown attribute is rejected - and the message names the offending attribute,
        /// so the caller can correct it rather than guess.
        /// </para>
        ///
        /// <para>
        /// <b>Cross-container moves fall out of this for free.</b> A move is one parent
        /// losing a child and a sibling gaining one, and the SDK derives both
        /// <c>childrenOrderedList</c> values from the resulting document order on save.
        /// <see cref="PromoteMatchedInsertRemoveAsMove"/> then reports it as the single
        /// <c>Move</c> operation it is, instead of leaving the caller to notice.
        /// </para>
        /// </summary>
        private bool TryAllowStructuralLayoutEdit(
            XNode[] oldNodes, XNode[] newNodes, string path, bool allowGridStructure)
        {
            // Exactly one node of difference, on one side only.
            if (Math.Abs(newNodes.Length - oldNodes.Length) != 1) return false;
            bool insertion = newNodes.Length > oldNodes.Length;

            // First divergence is the changed node. Scanning it once keeps this O(n)
            // rather than the O(n^2) a "try removing each index" search would cost on a
            // container with hundreds of children.
            int limit = Math.Min(oldNodes.Length, newNodes.Length);
            int firstDiff = limit;
            for (int i = 0; i < limit; i++)
            {
                if (XNode.DeepEquals(oldNodes[i], newNodes[i])) continue;
                firstDiff = i;
                break;
            }

            if (insertion)
            {
                if (firstDiff >= newNodes.Length) return false;
                if (!(newNodes[firstDiff] is XElement inserted)) return false;
                // Everything but the inserted node must be untouched and in order, or this
                // is a rewrite rather than an insertion.
                for (int i = 0, j = 0; i < newNodes.Length; i++)
                {
                    if (i == firstDiff) continue;
                    if (j >= oldNodes.Length || !XNode.DeepEquals(oldNodes[j], newNodes[i])) return false;
                    j++;
                }

                string kind = inserted.Name.LocalName;
                if (!StructuralLayoutRules.IsPermittedKind(kind)) return false;
                if (inserted.HasElements) return false;

                string offender = StructuralLayoutRules.FirstDisallowedAttribute(kind, inserted);
                if (offender != null)
                {
                    Reject("PatternStructureChangeUnsupported",
                        "Attribute '" + offender + "' is not permitted on a <" + kind + ">"
                        + " added at " + path + ". Permitted: "
                        + string.Join(", ", StructuralLayoutRules.AllowedAttributes(kind))
                        + ".");
                    return true;
                }
                string identity = StructuralLayoutRules.Identity(kind, inserted);
                if (identity == null)
                {
                    Reject("PatternStructureChangeUnsupported",
                        "A <" + kind + "> added at " + path + " needs "
                        + StructuralLayoutRules.RequiredIdentityAttribute(kind) + ".");
                    return true;
                }

                Changes.Add(new JObject
                {
                    ["path"] = path,
                    ["operation"] = "Insert",
                    ["element"] = kind,
                    ["identity"] = identity,
                    ["index"] = firstDiff,
                    ["after"] = firstDiff == 0 ? null : "existing child at index " + (firstDiff - 1),
                    ["ordering"] = OrderingNote
                });
                _insertedIdentities[identity] = kind;
                return true;
            }

            if (firstDiff >= oldNodes.Length) return false;
            if (!(oldNodes[firstDiff] is XElement removed)) return false;
            // Everything but the removed node must be untouched and in order.
            for (int i = 0, j = 0; i < oldNodes.Length; i++)
            {
                if (i == firstDiff) continue;
                if (j >= newNodes.Length || !XNode.DeepEquals(newNodes[j], oldNodes[i])) return false;
                j++;
            }

            string removedKind = removed.Name.LocalName;
            if (!StructuralLayoutRules.IsPermittedKind(removedKind)) return false;

            string removedIdentity = StructuralLayoutRules.ReportIdentity(removedKind, removed);
            Changes.Add(new JObject
            {
                ["path"] = path,
                ["operation"] = "Remove",
                ["element"] = removedKind,
                ["identity"] = removedIdentity,
                ["index"] = firstDiff,
                ["ordering"] = OrderingNote
            });
            _removedIdentities[removedIdentity] = removedKind;
            return true;
        }

        private const string OrderingNote =
            "childrenOrderedList is not authored; the SDK rebuilds it from document order";

        /// <summary>
        /// Collapses a matched Insert + Remove of the same element identity into one
        /// <c>Move</c>, because that is what the caller did and what the issue asked to
        /// see reported.
        ///
        /// <para>
        /// Purely a reporting refinement - each half is already an independently permitted
        /// single-leaf operation, so safety never depended on recognising the pair.
        /// </para>
        /// </summary>
        internal void PromoteMatchedInsertRemoveAsMove()
        {
            if (_removedIdentities.Count == 0 || _insertedIdentities.Count == 0) return;

            foreach (var change in Changes.ToArray())
            {
                if ((string)change["operation"] != "Insert") continue;
                string identity = (string)change["identity"];
                if (identity == null) continue;
                if (!_removedIdentities.ContainsKey(identity)) continue;

                var removal = Changes.FirstOrDefault(c =>
                    (string)c["operation"] == "Remove"
                    && string.Equals((string)c["identity"], identity, StringComparison.OrdinalIgnoreCase));
                if (removal == null) continue;

                change["operation"] = "Move";
                change["fromPath"] = removal["path"]?.ToString();
                change["fromIndex"] = removal["index"];
                change["reason"] = "same element removed from one container and inserted into another";
                removal.Remove();
                _removedIdentities.Remove(identity);
                _insertedIdentities.Remove(identity);
            }
        }

        /// <summary>
        /// Compares two nodes by element kind and identity rather than by content, so a
        /// rewritten-but-same-named child is recognised as the same child when describing
        /// a structural change.
        /// </summary>
        private sealed class StructuralNodeKeyComparer : IEqualityComparer<XNode>
        {
            internal static readonly StructuralNodeKeyComparer Instance = new StructuralNodeKeyComparer();

            public bool Equals(XNode x, XNode y)
            {
                if (x is XElement ex && y is XElement ey)
                {
                    return string.Equals(ex.Name.LocalName, ey.Name.LocalName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(
                            StructuralLayoutRules.Identity(ex.Name.LocalName, ex),
                            StructuralLayoutRules.Identity(ey.Name.LocalName, ey),
                            StringComparison.OrdinalIgnoreCase);
                }
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(XNode obj)
            {
                if (obj is XElement e) return (e.Name.LocalName ?? "").GetHashCode();
                return 0;
            }
        }

        private static bool IsGrid(XElement element)
            => element != null && string.Equals(element.Name.LocalName, "grid", StringComparison.OrdinalIgnoreCase);

        private static bool IsGridColumn(XElement element)
            => element != null && (string.Equals(element.Name.LocalName, "gridAttribute", StringComparison.OrdinalIgnoreCase)
                || string.Equals(element.Name.LocalName, "gridVariable", StringComparison.OrdinalIgnoreCase));

        private static string GridColumnIdentity(XElement element)
        {
            if (!IsGridColumn(element)) return null;
            string identity = (string)element.Attribute("variable")
                ?? (string)element.Attribute("attribute")
                ?? (string)element.Attribute("name");
            return string.IsNullOrWhiteSpace(identity) ? null : identity.Trim();
        }

        private bool TryAllowGridVariableInsertion(XElement parent, XNode[] oldNodes, XNode[] newNodes, string path)
        {
            if (!IsGrid(parent) || newNodes.Length != oldNodes.Length + 1) return false;
            int insertedIndex = -1;
            for (int i = 0; i < newNodes.Length; i++)
            {
                if (newNodes[i] is XElement element
                    && string.Equals(element.Name.LocalName, "gridVariable", StringComparison.OrdinalIgnoreCase))
                {
                    if (insertedIndex >= 0) return false;
                    insertedIndex = i;
                }
            }
            if (insertedIndex < 0 || !(newNodes[insertedIndex] is XElement inserted)
                || inserted.HasElements) return false;
            if (string.IsNullOrWhiteSpace((string)inserted.Attribute("variable"))
                || string.IsNullOrWhiteSpace((string)inserted.Attribute("name"))) return false;
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "variable", "name", "description", "caption", "basicType", "basicCLength", "type", "length"
            };
            if (inserted.Attributes().Any(a => !allowed.Contains(a.Name.LocalName))) return false;
            int oldIndex = 0;
            for (int newIndex = 0; newIndex < newNodes.Length; newIndex++)
            {
                if (newIndex == insertedIndex) continue;
                if (oldIndex >= oldNodes.Length || !XNode.DeepEquals(oldNodes[oldIndex], newNodes[newIndex])) return false;
                oldIndex++;
            }
            if (oldIndex != oldNodes.Length) return false;
            Changes.Add(new JObject
            {
                ["path"] = path + "gridVariable[@name='" + (string)inserted.Attribute("name") + "']",
                ["operation"] = "Insert",
                ["name"] = (string)inserted.Attribute("name")
            });
            return true;
        }

        private bool TryCompareGridReorder(XNode[] oldNodes, XNode[] newNodes, string path, bool allowGridStructure)
        {
            if (!allowGridStructure || oldNodes.Length != newNodes.Length) return false;

            // A grid can contain action groups, filters, sort metadata, and other
            // authored children alongside its columns.  A valid move is one column
            // moving through that sequence; removing that column must leave every
            // other direct child in exactly the same order and with the same data.
            var oldColumns = oldNodes.OfType<XElement>().Where(IsGridColumn).ToList();
            var newColumns = newNodes.OfType<XElement>().Where(IsGridColumn).ToList();
            if (oldColumns.Count == 0 || oldColumns.Count != newColumns.Count) return false;

            var oldMap = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
            var oldIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < oldNodes.Length; i++)
            {
                if (!(oldNodes[i] is XElement column) || !IsGridColumn(column)) continue;
                string identity = GridColumnIdentity(column);
                if (string.IsNullOrWhiteSpace(identity) || oldMap.ContainsKey(identity)) return false;
                oldMap[identity] = column;
                oldIndexes[identity] = i;
            }

            var newMap = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
            var newIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < newNodes.Length; i++)
            {
                if (!(newNodes[i] is XElement column) || !IsGridColumn(column)) continue;
                string identity = GridColumnIdentity(column);
                if (string.IsNullOrWhiteSpace(identity) || newMap.ContainsKey(identity)
                    || !oldMap.ContainsKey(identity)) return false;
                newMap[identity] = column;
                newIndexes[identity] = i;
            }
            if (oldMap.Count != newMap.Count) return false;

            bool columnPositionChanged = oldMap.Keys.Any(identity =>
                oldIndexes[identity] != newIndexes[identity]);
            if (!columnPositionChanged) return false;

            int moveCandidates = 0;
            string movedIdentity = null;
            foreach (string identity in oldMap.Keys)
            {
                int oldIndex = oldIndexes[identity];
                int newIndex = newIndexes[identity];
                if (oldIndex == newIndex) continue;

                var oldRemaining = oldNodes.Where((node, index) => index != oldIndex).ToArray();
                var newRemaining = newNodes.Where((node, index) => index != newIndex).ToArray();
                if (!SameNodeSequence(oldRemaining, newRemaining)) continue;
                moveCandidates++;
                movedIdentity = movedIdentity ?? identity;
            }
            if (moveCandidates == 0)
            {
                Reject("PatternStructureChangeUnsupported",
                    "Grid column movement changed another direct child at " + path);
                return true;
            }

            Changes.Add(new JObject { ["path"] = path, ["operation"] = "GridOrder" });
            Compare(oldMap[movedIdentity], newMap[movedIdentity],
                path + "[@" + movedIdentity + "]/", allowGridStructure);
            return true;
        }

        private static bool SameNodeSequence(XNode[] left, XNode[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
            {
                if (!XNode.DeepEquals(left[i], right[i])) return false;
            }
            return true;
        }

        // containers. Allow only the narrow structural operation needed to add
        // one such component to an existing container: no removals, moves,
        // replacements, metadata changes, or childrenOrderedList edits. The
        // normal PatternInstance SDK deserializer/save path still performs the
        // actual persistence and the later WWP projection verifies the result.
        private bool TryAllowWebComponentInsertion(XElement before, XElement after,
            XNode[] oldNodes, XNode[] newNodes, string path)
        {
            if (!string.Equals(before.Name.LocalName, "table", StringComparison.OrdinalIgnoreCase)
                || newNodes.Length != oldNodes.Length + 1)
                return false;

            int insertedIndex = -1;
            for (int i = 0; i < newNodes.Length; i++)
            {
                if (!(newNodes[i] is XElement element)
                    || !string.Equals(element.Name.LocalName, "webComponent", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (insertedIndex >= 0) return false;
                insertedIndex = i;
            }
            if (insertedIndex < 0) return false;

            var inserted = (XElement)newNodes[insertedIndex];
            if (inserted.HasElements
                || inserted.Attributes().Any(a => !string.Equals(a.Name.LocalName, "name", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(a.Name.LocalName, "gxobject", StringComparison.OrdinalIgnoreCase)))
                return false;

            string name = (string)inserted.Attribute("name");
            string gxobject = (string)inserted.Attribute("gxobject");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(gxobject))
                return false;

            int oldIndex = 0;
            for (int newIndex = 0; newIndex < newNodes.Length; newIndex++)
            {
                if (newIndex == insertedIndex) continue;
                if (oldIndex >= oldNodes.Length || !XNode.DeepEquals(oldNodes[oldIndex], newNodes[newIndex]))
                    return false;
                oldIndex++;
            }
            if (oldIndex != oldNodes.Length) return false;

            Changes.Add(new JObject
            {
                ["path"] = path + "webComponent[@name='" + name + "']",
                ["operation"] = "Insert",
                ["after"] = insertedIndex == 0 ? null : "existing child at index " + (insertedIndex - 1),
                ["name"] = name,
                ["gxobject"] = gxobject
            });
            return true;
        }

        private static bool Significant(XNode node) => !(node is XText text)
            || node is XCData || !string.IsNullOrWhiteSpace(text.Value)
            || !node.Parent.Elements().Any()
            || node.Parent.Nodes().OfType<XText>().Any(t => t is XCData || !string.IsNullOrWhiteSpace(t.Value))
            || node.Parent.AncestorsAndSelf().Any(e => (string)e.Attribute(XNamespace.Xml + "space") == "preserve");

        private static bool IsMetadata(string name) => name.StartsWith("default", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("template", StringComparison.OrdinalIgnoreCase)
            || name.Equals("childrenOrderedList", StringComparison.OrdinalIgnoreCase);

        private static bool IsProtected(string name)
        {
            return IsMetadata(name)
                || name.Equals("name", StringComparison.OrdinalIgnoreCase)
                || name.Equals("controlName", StringComparison.OrdinalIgnoreCase)
                || name.Equals("blockName", StringComparison.OrdinalIgnoreCase)
                || name.Equals("attribute", StringComparison.OrdinalIgnoreCase)
                || name.Equals("id", StringComparison.OrdinalIgnoreCase)
                || name.Equals("type", StringComparison.OrdinalIgnoreCase)
                || name.Equals("identifier", StringComparison.OrdinalIgnoreCase)
                || name.Equals("patternId", StringComparison.OrdinalIgnoreCase)
                || name.Equals("guid", StringComparison.OrdinalIgnoreCase)
                || name.Equals("key", StringComparison.OrdinalIgnoreCase)
                || name.Equals("entityKey", StringComparison.OrdinalIgnoreCase);
        }
    }
}
