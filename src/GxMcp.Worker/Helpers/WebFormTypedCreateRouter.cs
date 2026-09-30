using System;
using System.Collections.Generic;
using System.Xml;
using GxMcp.Worker.Compatibility;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// W1 step 3: route whole-element control additions through the SDK instead of raw
    /// XML. R&D on GeneXus 18 (KBTeste) showed structure (table/row/cell) persists
    /// through EditableContent while controls (gxTextBlock/gxButton) are stripped by
    /// the SDK unless materialized via <c>WebTagFactory.Create</c> — even the semantic
    /// <c>add_textblock</c> path leaves an empty cell behind.
    ///
    /// <para><b>Blocked, and inert by default.</b> Measured live on GeneXus 18,
    /// <c>Create</c> binds and returns, but <c>SaveProperties</c> returns false and the
    /// control is still stripped on push. The attempt cannot make a write worse (the
    /// raw rewrite still runs and post-write verification still gates) and it cannot
    /// make one better, so it pays an SDK round-trip per created tag for a
    /// known-zero outcome. It is therefore behind
    /// <see cref="WebFormXmlHelper.ApplyEditableXml"/>'s <c>attemptTypedCreate</c>
    /// flag, off unless a caller asks for it, until a GeneXus major is measured to
    /// persist the tag. See docs/mcp-roadmap-ide-parity.md (W1).</para>
    /// </summary>
    internal static class WebFormTypedCreateRouter
    {
        /// <summary>
        /// Pure gate: attempt when the diff adds at least one IDENTIFIED control (gx*
        /// element with a name) and contains no removals or moves. Structural adds
        /// (rows/cells/tables) ride the raw rewrite that always runs afterwards — they
        /// persist through EditableContent — while the attempt materializes the controls
        /// the SDK would otherwise strip. Removals stay raw-only (delete semantics
        /// through the SDK are unverified).
        /// </summary>
        internal static bool ShouldAttemptTypedCreate(IReadOnlyList<WebFormStructuralChange> structural)
        {
            if (structural == null || structural.Count == 0) return false;
            bool anyControlAdd = false;
            foreach (var change in structural)
            {
                if (change == null) return false;
                if (change.Kind != WebFormStructuralChangeKind.Added) return false;
                if (IsControlAdd(change)) anyControlAdd = true;
            }
            return anyControlAdd;
        }

        /// <summary>
        /// True for an added, identified control (gx* with a name). Wrapper structure
        /// and removals/moves never need SDK materialization.
        /// </summary>
        internal static bool IsControlAdd(WebFormStructuralChange change)
        {
            if (change == null) return false;
            if (change.Kind != WebFormStructuralChangeKind.Added) return false;
            if (string.IsNullOrWhiteSpace(change.ControlType)) return false;
            if (!change.ControlType.StartsWith("gx", StringComparison.OrdinalIgnoreCase)) return false;
            return !string.IsNullOrWhiteSpace(change.ControlName);
        }

        /// <summary>
        /// Materialization order: outermost first, so a parent control exists before
        /// its children. Sorts a copy; the input order is preserved for the caller.
        /// </summary>
        internal static WebFormStructuralChange[] OrderByDepth(IEnumerable<WebFormStructuralChange> changes)
        {
            var list = new List<WebFormStructuralChange>();
            foreach (var change in changes) if (IsControlAdd(change)) list.Add(change);
            list.Sort((a, b) => Depth(a).CompareTo(Depth(b)));
            return list.ToArray();
        }

        /// <summary>
        /// Attempts SDK materialization for every added control, in order, appending
        /// each created tag to <paramref name="createdTags"/> so the caller can re-assert
        /// them after the document push. Returns true when every Create bound and
        /// returned; any miss or rejection returns false and the caller keeps the raw
        /// rewrite. Never throws for a probe miss; an SDK rejection propagates
        /// (fail-closed upstream) exactly like <see cref="WebTagFactoryAdapter"/>.
        /// </summary>
        internal static bool TryApplyTypedCreates(
            object webFormPart,
            XmlDocument editableDocument,
            IReadOnlyList<WebFormStructuralChange> adds,
            List<object> createdTags,
            out string failure,
            out int materializedCount)
        {
            failure = null;
            materializedCount = 0;
            if (webFormPart == null) { failure = "part is null"; return false; }
            if (editableDocument?.DocumentElement == null) { failure = "editable document is null"; return false; }
            if (!ShouldAttemptTypedCreate(adds)) { failure = "no identified control add"; return false; }

            Type factoryType = WebTagFactoryAdapter.TryResolveFactoryType();
            if (factoryType == null) { failure = WebTagFactoryAdapter.FactoryTypeName + " type not loaded"; return false; }

            // The node handed to Create must live in the PART's document — that is the
            // document BeforeSaveKBObject persists. A node parsed out of a detached
            // editable document belongs to a different XmlDocument, so the created tag
            // wraps a node the part cannot see, and the push later replaces the document
            // wholesale. Import the requested element into the live document under its
            // parent, then materialize from THAT node.
            XmlDocument partDoc = WebFormSdkReflection.GetPartDocument(webFormPart);
            if (partDoc?.DocumentElement == null) { failure = "part document is null"; return false; }

            var tags = WebFormSdkReflection.IndexLiveTags(webFormPart);
            if (!tags.Succeeded) { failure = tags.Failure; return false; }
            object kbObj = tags.KbObject;
            IDictionary<string, object> tagIndex = tags.Tags;

            int parentResolved = 0;
            foreach (var change in OrderByDepth(adds))
            {
                XmlElement requested = FindAddedElement(editableDocument, change);
                if (requested == null) { failure = "added element '" + change.ControlName + "' not found in requested XML"; return false; }

                XmlElement liveNode = ImportInto(partDoc, requested, out string importFailure);
                if (liveNode == null) { failure = "could not attach '" + change.ControlName + "' to the part document: " + importFailure; return false; }

                string parentIdentity = WebFormSdkReflection.GetControlIdentity(liveNode.ParentNode as XmlElement);
                object parentTag = null;
                if (!string.IsNullOrEmpty(parentIdentity) && tagIndex.TryGetValue(parentIdentity, out parentTag)) parentResolved++;

                try
                {
                    if (!WebTagFactoryAdapter.TryCreateTagOn(factoryType, liveNode, kbObj, parentTag, out object created))
                    {
                        failure = "factory Create absent for '" + change.ControlName + "'";
                        return false;
                    }
                    materializedCount++;
                    // Register under both identities so a later sibling can resolve it as a parent.
                    string id = liveNode.GetAttribute("id");
                    if (!string.IsNullOrEmpty(id)) tagIndex[id] = created;
                    tagIndex[change.ControlName] = created;
                    createdTags?.Add(created);
                    Logger.Info("[TypedCreate] materialized <" + change.ControlType + "> '" + change.ControlName
                        + "' (parent tag " + (parentTag != null ? "resolved" : "null") + ").");
                }
                catch (Exception ex)
                {
                    failure = "factory Create rejected '" + change.ControlName + "': " + ex.GetType().Name + ": " + ex.Message;
                    return false;
                }
            }

            Logger.Info("[TypedCreate] materialized " + materializedCount + " control(s), " + parentResolved + " with a resolved parent tag.");
            return true;
        }

        /// <summary>
        /// Re-asserts tags materialized by <see cref="WebTagFactoryAdapter"/> after the
        /// document push. Measured live on GeneXus 18: the raw rewrite persists the
        /// wrapper structure but <c>DeserializeDataFromDocument</c> drops the control
        /// element itself, because the freshly created tag is not part of the parsed tree
        /// the push rebuilds. Re-running each tag's <c>SaveProperties</c> asks the SDK to
        /// write the tag back into its node, which is what BeforeSaveKBObject persists.
        /// Never throws — the caller's post-write verification remains the gate.
        /// </summary>
        internal static void ReassertCreatedTags(object webFormPart, IReadOnlyList<object> createdTags)
        {
            if (webFormPart == null || createdTags == null || createdTags.Count == 0) return;

            bool anyReverted = false;
            foreach (var tag in createdTags)
            {
                if (tag == null) continue;
                try
                {
                    // A control added to a layout form is only persisted when the SDK's
                    // typed model still knows the tag. SaveProperties on a tag the
                    // reparsing push already dropped returns false (measured live:
                    // "re-asserted WebTagElement (SaveProperties=False)") and leaves the
                    // node out of the persisted tree. Nothing to do about it here — the
                    // post-write verification reports the difference.
                    object result = WebFormSdkReflection.InvokeNoArgs(tag, "SaveProperties");
                    if (result == null && FindNoArgMethod(tag, "SaveProperties") == null)
                    {
                        Logger.Info("[TypedCreate] created tag " + tag.GetType().Name + " has no SaveProperties; skipped re-assert.");
                        continue;
                    }
                    bool saved = result is bool flag && flag;
                    Logger.Info("[TypedCreate] re-asserted " + tag.GetType().Name + " (SaveProperties=" + (result ?? "null") + ").");
                    if (!saved) anyReverted = true;
                }
                catch (Exception ex)
                {
                    Logger.Info("[TypedCreate] re-assert failed for " + tag.GetType().Name + ": " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            if (anyReverted)
            {
                Logger.Info("[TypedCreate] the SDK did not accept the created control(s) back into the stored model; " +
                            "the raw-rewrite result stands and the post-write verification will report the difference.");
            }
        }

        /// <summary>
        /// Locates the added element inside a detached copy of the requested XML by
        /// control identity (id first, then ControlName/controlName).
        /// </summary>
        internal static XmlElement FindAddedElement(XmlDocument document, WebFormStructuralChange change)
        {
            if (document?.DocumentElement == null || change == null) return null;
            if (string.IsNullOrWhiteSpace(change.ControlName)) return null;
            foreach (XmlElement element in document.DocumentElement.GetElementsByTagName(change.ControlType ?? "*"))
            {
                if (!string.Equals(element.LocalName, change.ControlType, StringComparison.OrdinalIgnoreCase)) continue;
                if (MatchesIdentity(element, change.ControlName)) return element;
            }
            return null;
        }

        /// <summary>
        /// Imports an element from the requested document into the part document under
        /// its parent, so the node Create wraps is the one the part persists. The parent
        /// is resolved by identity: an existing element in the same part document, or the
        /// root when the parent is itself part of the same new subtree.
        /// </summary>
        internal static XmlElement ImportInto(XmlDocument partDoc, XmlElement requested, out string failure)
        {
            failure = null;
            if (partDoc?.DocumentElement == null) { failure = "part document has no root"; return null; }
            if (requested == null) { failure = "element is null"; return null; }

            try
            {
                var parentElement = WebFormSdkReflection.FindElementInPartDoc(
                    partDoc, WebFormSdkReflection.GetControlIdentity(requested.ParentNode as XmlElement))
                    ?? partDoc.DocumentElement;
                var imported = partDoc.ImportNode(requested, deep: true);
                parentElement.AppendChild(imported);

                if (!(imported is XmlElement element)) { failure = "imported node is not an element"; return null; }
                if (string.IsNullOrWhiteSpace(element.GetAttribute("id")))
                {
                    // GeneXus gives every element an id at creation time. An element
                    // without one is not addressable by the SDK's tag enumeration
                    // (EnumerateWebTag keys on id/ControlName), so Create would wrap a
                    // node the part can never find again — and the push drops it.
                    element.SetAttribute("id", Guid.NewGuid().ToString("D"));
                }
                return element;
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>Distance from the root, so parents materialize before their children.</summary>
        private static int Depth(WebFormStructuralChange change)
        {
            int depth = 0;
            foreach (char c in change?.Path ?? string.Empty) if (c == '/') depth++;
            return depth;
        }

        private static bool MatchesIdentity(XmlElement element, string identity)
        {
            if (string.Equals(WebFormSdkReflection.GetAttribute(element, "id"), identity, StringComparison.OrdinalIgnoreCase))
                return true;
            return string.Equals(WebFormSdkReflection.GetAttribute(element, "ControlName"), identity, StringComparison.OrdinalIgnoreCase)
                || string.Equals(WebFormSdkReflection.GetAttribute(element, "controlName"), identity, StringComparison.OrdinalIgnoreCase);
        }

        private static System.Reflection.MethodInfo FindNoArgMethod(object target, string name)
        {
            return target?.GetType().GetMethod(name,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                binder: null, types: Type.EmptyTypes, modifiers: null);
        }
    }
}
