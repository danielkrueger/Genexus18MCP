using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Structure;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public partial class LayoutService
    {
        private static readonly char[] CaptionLineBreaks = { (char)13, (char)10 };
        private readonly ObjectService _objectService;

        public LayoutService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        /// <summary>
        /// The single ObjectNotFound envelope for the visual-object tools (tree,
        /// controls, preview, report, mutator scan, catalog). Every one of those
        /// actions resolves its target the same way, so the recovery contract is
        /// built once here rather than copy-pasted at each of the call sites.
        /// </summary>
        internal static string VisualObjectNotFound(string target)
        {
            return Models.McpResponse.Err(
                code: "ObjectNotFound",
                message: "Object not found.",
                hint: "Verify the object name matches an entry in the active Knowledge Base.",
                nextSteps: new JArray(Models.McpResponse.NextStep("genexus_list_objects", null, "Lists all objects in the KB so you can confirm the correct name.")),
                target: target);
        }

        /// <summary>
        /// The recovery step every layout diagnostic and mutation failure offers:
        /// re-read this object's layout tree.
        ///
        /// It was written out 36 times across this class - 28 in this file, the rest
        /// in ReportControls and SourcePersistence - always naming
        /// <c>genexus_layout</c> with <c>action=get_tree</c> and <c>name=target</c>,
        /// and differing only in the prose explaining what the caller was checking.
        ///
        /// The explanation is per-call-site and stays here; the instruction is not,
        /// and is the half worth holding to one copy. A step that drifted to
        /// <c>inspect_surface</c>, or lost its <c>name</c> argument, would send the
        /// caller to a different action than the 35 other failures do, and the
        /// divergence would read as a deliberate difference.
        /// </summary>
        internal static JObject LayoutGetTreeStep(string target, string why)
        {
            return Models.McpResponse.NextStep(
                "genexus_layout",
                new JObject { ["action"] = "get_tree", ["name"] = target },
                why);
        }

        /// <summary>
        /// The object and the visual context a read needs, or the error to return.
        /// </summary>
        private sealed class VisualReadSetup
        {
            /// <summary>Set when the read cannot proceed; return this instead.</summary>
            public string Error;

            public global::Artech.Architecture.Common.Objects.KBObject Object;
            public LayoutContextResult Context;

            /// <summary>
            /// The document root, or null when the part has none. Callers that walk
            /// the tree must check this themselves - see <see cref="InvalidVisualXml"/>.
            /// </summary>
            public global::System.Xml.Linq.XElement Root =>
                Context == null || Context.Document == null ? null : Context.Document.Root;
        }

        /// <summary>
        /// Resolves the target object and its visual context for a read, failing
        /// closed at each step.
        ///
        /// Five readers did this by hand - <c>GetTree</c>, <c>FindControls</c>,
        /// <c>SetProperty</c>, <c>GetVisualPreview</c> and <c>SetProperties</c> - and
        /// the chain is ordered, not incidental: no KB, no object, no readable
        /// surface. A reader that skipped a step would walk a document it never
        /// confirmed it could parse.
        ///
        /// The root-element check is deliberately not folded in here. Only
        /// <c>GetTree</c> and <c>FindControls</c> walk the tree, and they report the
        /// missing root through <see cref="InvalidVisualXml"/>; the other three read
        /// the document as text and have no reason to reject a part with no root.
        /// </summary>
        private VisualReadSetup BeginVisualRead(string target)
        {
            var obj = _objectService.FindObject(target);
            if (obj == null)
                return new VisualReadSetup { Error = VisualObjectNotFound(target) };

            var context = LoadVisualContext(obj, target, VisualSurface.Any);
            if (context.Error != null)
                return new VisualReadSetup { Error = context.Error };

            return new VisualReadSetup { Object = obj, Context = context };
        }

        /// <summary>
        /// The envelope for a visual part whose XML has no root element.
        ///
        /// It was written out three times and had already drifted: the copy in
        /// <c>LayoutService.VisualContext</c> dropped "or inspecting the part
        /// directly" from the hint and reworded the recovery step, behind the same
        /// code. The wording here is the fuller of the two.
        /// </summary>
        private static string InvalidVisualXml(string target)
        {
            return Models.McpResponse.Err(
                code: "InvalidVisualXml",
                message: "Invalid visual XML: root element is missing.",
                hint: "The object's visual part may be corrupted; try re-opening the KB or inspecting the part directly.",
                nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "inspect_surface", ["name"] = target }, "Diagnoses which visual parts are available for this object.")),
                target: target);
        }

        /// <summary>
        /// The best-effort repair a print-block mutation attempts after its
        /// read-back says the SDK committed but the change is not on disk.
        ///
        /// Rename and add both did this by hand, identically: re-resolve the
        /// object, re-read its report surface, re-normalise the source commands
        /// against that document, and flush if the normalisation changed
        /// something. It is a repair, not a rollback - the transaction has already
        /// committed, so this is trying to make the persisted form self-consistent
        /// before the caller is told the change is missing.
        ///
        /// It is shared because the two sites failing differently would be
        /// indistinguishable from the SDK misbehaving in one specific way, and
        /// because the next print-block mutation should not have to decide again
        /// whether to attempt a repair at all. Delete does not call it: proving a
        /// block is *gone* needs no normalisation, and normalising a document in
        /// which a block was expected to have disappeared is not a repair of
        /// anything.
        ///
        /// Never throws, and its failures are deliberately not reported: a
        /// verification failure is the caller's answer already, and a repair that
        /// cannot complete does not change it.
        /// </summary>
        private void TryHealPrintCommandSourceAfterCommit(KBObject obj, string target)
        {
            var healObj = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? obj;
            var healContext = LoadVisualContext(healObj, target, VisualSurface.Report);
            if (healContext.Error == null && healContext.Document != null)
            {
                if (TryNormalizeReportPrintCommandsInSourceInMemory(healObj, healContext.Document.ToString(), out _))
                {
                    TryFlushSourceForLayoutMutation(healObj, out _);
                }
            }
        }

        /// <summary>
        /// A print block in a freshly-read report document, matched by either name
        /// the SDK uses for it.
        ///
        /// GeneXus exposes a print block's name as either <c>Name</c> or
        /// <c>ControlName</c> depending on the major, and every read-back in this
        /// file has to check both. Written out that is a two-clause predicate
        /// repeated at each site, and a site that checked only one would report a
        /// completed mutation as unverified - the exact failure the read-back exists
        /// to catch, caused by the check itself.
        /// </summary>
        private static XElement FindPrintBlockByName(XDocument document, string printBlockName)
        {
            return document.Descendants("PrintBlock")
                .FirstOrDefault(pb => string.Equals(Attr(pb, "Name"), printBlockName, StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(Attr(pb, "ControlName"), printBlockName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Whether a print block with this name is present in a report document.
        /// The negative of <see cref="FindPrintBlockByName"/> over the same
        /// two-attribute match.
        /// </summary>
        private static bool ContainsPrintBlock(XDocument document, string printBlockName)
        {
            return FindPrintBlockByName(document, printBlockName) != null;
        }

        /// <summary>
        /// The two guards every report mutation (rename/add/delete print block)
        /// passes through after resolving the target. They are written out
        /// identically at each call site, and one of them is a curated transient
        /// code whose <c>retryAfterMs</c> is part of the published contract, so both
        /// are built once.
        /// </summary>
        internal static string ReportPartNotFound(string target)
        {
            return Models.McpResponse.Err(
                code: "ReportPartNotFound",
                message: "Report part not found.",
                hint: "This operation requires a Procedure with a report layout part; verify the target is a report-capable Procedure.",
                nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "inspect_surface", ["name"] = target }, "Diagnoses which visual surfaces are present for this object.")),
                target: target);
        }

        internal static string ReportLayoutKbNotOpened(string target, string hint)
        {
            return Models.McpResponse.Err(
                code: "KbNotOpened",
                message: "KB not opened.",
                hint: hint,
                nextSteps: new JArray(KbOpenNextStep.Step("Opens the configured Knowledge Base.")),
                retryAfterMs: 2000,
                target: target);
        }

        /// <summary>
        /// Everything a print-block mutation needs before it may stage a change:
        /// the resolved object, the loaded report surface, the open KB, and the
        /// Procedure source snapshot taken before any edit so a rollback has
        /// something to restore.
        ///
        /// <see cref="Error"/> is non-null when the mutation must not proceed; the
        /// other members are only meaningful once it is null.
        /// </summary>
        private sealed class ReportMutationSetup
        {
            public KBObject Object { get; set; }
            public LayoutContextResult Context { get; set; }
            // dynamic, matching KbService.GetKB(): the concrete KnowledgeBase type
            // is SDK-version-specific.
            public dynamic KnowledgeBase { get; set; }
            public string SourceSnapshot { get; set; }
            public string Error { get; set; }
        }

        /// <summary>
        /// The prologue every print-block mutation (rename, add, delete) shared:
        /// resolve the target, load the report surface, require a report part,
        /// require an open KB, then snapshot the Procedure source.
        ///
        /// It was written out three times, and the order is the point. The snapshot
        /// has to be taken after the KB is confirmed open and before anything is
        /// written, because it is what the rollback path restores; a copy that
        /// dropped or reordered a step would still compile and still look right,
        /// and would fail as a report that could not be put back the way it was.
        /// </summary>
        private ReportMutationSetup BeginReportMutation(string target)
        {
            var setup = new ReportMutationSetup();

            var obj = _objectService.FindObject(target);
            if (obj == null)
            {
                setup.Error = VisualObjectNotFound(target);
                return setup;
            }

            var context = LoadVisualContext(obj, target, VisualSurface.Report);
            if (context.Error != null)
            {
                setup.Error = context.Error;
                return setup;
            }
            if (context.VisualPart == null)
            {
                setup.Error = ReportPartNotFound(target);
                return setup;
            }

            var kb = _objectService.GetKbService().GetKB();
            if (kb == null)
            {
                setup.Error = ReportLayoutKbNotOpened(target, "Open a Knowledge Base before mutating the report layout.");
                return setup;
            }

            setup.Object = obj;
            setup.Context = context;
            setup.SourceSnapshot = GetProcedureSourceSnapshot(obj);
            setup.KnowledgeBase = kb;
            return setup;
        }

        public string GetTree(string target, string controlFilter = null, int limit = 500)
        {
            try
            {
                if (limit <= 0) limit = 500;
                if (limit > 2000) limit = 2000;

                var setup = BeginVisualRead(target);
                if (setup.Error != null) return setup.Error;
                var obj = setup.Object;
                var contextResult = setup.Context;

                var root = setup.Root;
                if (root == null) return InvalidVisualXml(target);

                var nodes = new JArray();
                int total = 0;
                int emitted = 0;
                var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                Walk(root, "/" + root.Name.LocalName, nodes, ref total, ref emitted, limit, controlFilter, null, stats);

                var res = new JObject
                {
                    ["n"] = obj.Name,
                    ["t"] = obj.TypeDescriptor.Name,
                    ["s"] = contextResult.Surface.ToString(),
                    ["total"] = total,
                    ["count"] = emitted,
                    ["stats"] = JObject.FromObject(stats),
                    ["nodes"] = nodes,
                    ["versionToken"] = WriteService.ComputeContentVersionToken(
                        obj, contextResult.Document.ToString(SaveOptions.DisableFormatting)),
                    ["empty"] = emitted == 0,
                    ["help"] = new JArray 
                    {
                        "Use genexus_layout(action='set_property', control='ControlName', propertyName='Caption', value='New Value') to modify.",
                        "Use genexus_layout(action='get_preview') to see visual rendering."
                    }
                };

                return Models.McpResponse.Ok(target: target, code: "LayoutRead", result: res);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "LayoutReadException",
                    message: ex.Message,
                    hint: "Inspect the object type and retry; if the KB is closed reopen it first.",
                    nextSteps: new JArray(Models.McpResponse.NextStep("genexus_inspect", new JObject { ["name"] = target }, "Verify the object exists and has a visual part.")),
                    target: target);
            }
        }

        public string FindControls(string target, string propertyName = null, string query = null, int limit = 200)
        {
            try
            {
                if (limit <= 0) limit = 200;
                if (limit > 2000) limit = 2000;

                var setup = BeginVisualRead(target);
                if (setup.Error != null) return setup.Error;
                var obj = setup.Object;
                var contextResult = setup.Context;

                var root = setup.Root;
                if (root == null) return InvalidVisualXml(target);

                string normalizedProperty = string.IsNullOrWhiteSpace(propertyName) ? null : propertyName;
                string normalizedQuery = string.IsNullOrWhiteSpace(query) ? null : query;

                var nodes = new JArray();
                int total = 0;
                int emitted = 0;
                var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                Walk(root, "/" + root.Name.LocalName, nodes, ref total, ref emitted, limit, null, new FindCriteria
                {
                    PropertyName = normalizedProperty,
                    Query = normalizedQuery
                }, stats);

                var result = new JObject
                {
                    ["n"] = obj.Name,
                    ["t"] = obj.TypeDescriptor.Name,
                    ["s"] = contextResult.Surface.ToString(),
                    ["total"] = total,
                    ["count"] = emitted,
                    ["stats"] = JObject.FromObject(stats),
                    ["nodes"] = nodes,
                    ["versionToken"] = WriteService.ComputeContentVersionToken(
                        obj, contextResult.Document.ToString(SaveOptions.DisableFormatting)),
                    ["empty"] = emitted == 0,
                    ["help"] = new JArray 
                    {
                        "Use genexus_layout(action='set_property', control='ControlName', propertyName='Caption', value='New Value') to modify.",
                        "Use genexus_layout(action='get_preview') to see visual rendering of the layout."
                    }
                };
                return Models.McpResponse.Ok(target: target, code: "LayoutRead", result: result);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "LayoutFindException",
                    message: ex.Message,
                    hint: "Inspect the object type and retry; if the KB is closed reopen it first.",
                    nextSteps: new JArray(Models.McpResponse.NextStep("genexus_inspect", new JObject { ["name"] = target }, "Verify the object exists and has a visual part.")),
                    target: target);
            }
        }

        public string SetProperty(string target, string controlName, string propertyName, string value)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(controlName))
                    return Models.McpResponse.Err(
                        code: "MissingControlName",
                        message: "Missing control name.",
                        hint: "Provide 'control' with the visual control identifier.",
                        nextSteps: new JArray(LayoutGetTreeStep(target, "Returns the tree of control names for this object.")),
                        target: target);
                if (string.IsNullOrWhiteSpace(propertyName))
                    return Models.McpResponse.Err(
                        code: "MissingPropertyName",
                        message: "Missing property name.",
                        hint: "Provide 'propertyName' for the visual mutation (e.g. 'Caption', 'Class', 'Visible').",
                        nextSteps: new JArray(LayoutGetTreeStep(target, "Shows available controls and their current property values.")),
                        target: target);

                // Issue #360: a control property the SDK only carries through the
                // custom-properties payload must not be written as a bare XML attribute.
                // It would persist, verify, and then be ignored by the generator - a
                // successful call with no effect, which is the trap this whole issue is.
                if (IsCustomPropertyOnly(propertyName))
                {
                    return Models.McpResponse.Err(
                        code: "ControlPropertyNeedsCustomProperties",
                        message: "'" + propertyName + "' is not a WebForm XML attribute. On a plain "
                            + "WebPanel the IDE stores it as a "
                            + WebFormSchemaHints.CustomPropertiesAttribute + " payload, and the generator "
                            + "reads it from there.",
                        hint: "Set it by writing the "
                            + WebFormSchemaHints.CustomPropertiesAttribute + " attribute on the control, e.g. "
                            + "PATTERN_ELEMENT_CUSTOM_PROPERTIES=\"&lt;Properties&gt;&lt;Property&gt;"
                            + "&lt;Name&gt;GxFormat&lt;/Name&gt;&lt;Value&gt;Raw HTML&lt;/Value&gt;"
                            + "&lt;/Property&gt;&lt;/Properties&gt;\" for Format = Raw HTML. Valid values are "
                            + "0=Text, 1=HTML, 2=Raw HTML, 3=Text with meaningful spaces.",
                        nextSteps: new JArray(
                            Models.McpResponse.NextStep("genexus_read",
                                new JObject { ["name"] = target, ["part"] = "WebForm" },
                                "Reads the control's current XML, including any existing "
                                + WebFormSchemaHints.CustomPropertiesAttribute + " payload to extend."),
                            Models.McpResponse.NextStep("genexus_io",
                                new JObject { ["action"] = "import_part", ["name"] = target, ["part"] = "WebForm" },
                                "Writes the edited WebForm XML, custom-properties payload included.")),
                        target: target);
                }

                // object + context resolved together by BeginVisualRead

                var setup = BeginVisualRead(target);
                if (setup.Error != null) return setup.Error;
                var obj = setup.Object;
                var contextResult = setup.Context;

                var doc = contextResult.Document;
                string baselineXml = doc.ToString();
                var ambiguous = AmbiguousControlError(doc, target, controlName);
                if (ambiguous != null) return ambiguous;
                var element = FindControlElement(doc, controlName);
                if (element == null)
                    return Models.McpResponse.Err(
                        code: "ControlNotFound",
                        message: "Control not found: '" + controlName + "'.",
                        hint: "Use get_tree to enumerate the control names present in this object's layout.",
                        nextSteps: new JArray(LayoutGetTreeStep(target, "Lists all controls and their ControlName values.")),
                        target: target);

                if (string.Equals(propertyName, "Caption", StringComparison.OrdinalIgnoreCase)
                    && HasCaptionLineBreak(value))
                {
                    return Models.McpResponse.Err(
                        code: "CaptionNewlineUnsupported",
                        message: "Caption values cannot contain embedded line breaks.",
                        hint: "Use a single-line Caption. GeneXus may rename the control when a multiline caption is saved.",
                        nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "set_property", ["name"] = target, ["controlName"] = controlName, ["propertyName"] = "Caption", ["value"] = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ") }, "Retry with a single-line caption.")),
                        target: target);
                }

                string attrName;
                string previous;
                if (IsTextPropertyName(propertyName))
                {
                    attrName = "InnerText";
                    previous = element.Value;
                    element.Value = value ?? string.Empty;
                }
                else
                {
                    attrName = ResolveCanonicalAttributeName(element, propertyName);

                    // gxTextBlock and other legacy controls authoritatively store the caption
                    // as a CaptionExpression Tokens XML, while WebForm controls (like gxButton)
                    // use the Caption attribute directly.
                    // Keep both in sync when CaptionExpression exists, but never delete Caption.
                    if (string.Equals(attrName, "Caption", StringComparison.OrdinalIgnoreCase))
                    {
                        // Issue #360. This branch compared case-insensitively but then wrote
                        // the hardcoded `Caption` spelling, so on a control that spells the
                        // attribute `caption` it *added* `Caption` beside it — and the SDK
                        // save died with "Já foi adicionado um item com a mesma chave",
                        // naming neither the attribute nor the control.
                        previous = Attr(element, "Caption") ?? ExtractConstantCaptionFromTokens(Attr(element, "CaptionExpression"));
                        WriteAttributeCaseInsensitive(element, "Caption", value ?? string.Empty);
                        if (Attr(element, "CaptionExpression") != null)
                        {
                            WriteAttributeCaseInsensitive(element, "CaptionExpression",
                                BuildConstantCaptionTokens(value ?? string.Empty));
                        }
                    }
                    else
                    {
                        // Read through the case-insensitive Attr so a control that spells
                        // this attribute differently reports its real previous value rather
                        // than null, and so no-op detection can see that nothing changed.
                        previous = Attr(element, attrName);

                        // Write in place, collapsing any pre-existing differently-cased
                        // duplicates onto one attribute.
                        //
                        // Setting a property on a control that carried both `caption` and
                        // `Caption` produced "Já foi adicionado um item com a mesma chave"
                        // — the SDK rejects an element holding two attributes that differ
                        // only in case. Resolving the case-insensitively alone made that
                        // reachable (it now finds the control, so it now gets as far as the
                        // write), so the duplicates are merged here rather than left for the
                        // SDK to reject.
                        WriteAttributeCaseInsensitive(element, attrName, value ?? string.Empty);
                    }
                }

                string normalized = doc.ToString();
                Logger.Info($"SetProperty: Target XML updated for {controlName}. attrName={attrName}. Current element attributes: {string.Join(", ", System.Linq.Enumerable.Select(element.Attributes(), a => a.Name.LocalName + "=" + a.Value))}");
                Logger.Info($"SetProperty: New XML Sample (first 500 chars): " + (normalized.Length > 500 ? normalized.Substring(0, 500) : normalized));
                
                var persistError = PersistVisualXml(
                    obj,
                    contextResult,
                    target,
                    normalized,
                    baselineXml: baselineXml,
                    compositionRepairToken: value);
                if (persistError != null) return persistError;

                var persistedObject = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? _objectService.FindObject(target);
                var persistedContext = LoadVisualContext(persistedObject ?? obj, target, VisualSurface.Any);
                if (persistedContext.Error != null) return persistedContext.Error;

                var persistedElement = FindControlElement(persistedContext.Document, controlName);
                if (persistedElement == null)
                {
                    // The write was committed before this read, so a miss here means the
                    // SDK rewrote the control (renamed it, dropped it, or lost attributes
                    // such as Event/Class) - not that nothing was saved. Undo it from the
                    // pre-write XML, prove the undo with the same lookup the write used,
                    // and say what changed instead of a bare "not found".
                    var diff = DescribeIdentityDrift(baselineXml, persistedContext.Document);
                    bool rolledBack = false;
                    bool rollbackVerified = false;
                    if (!string.IsNullOrEmpty(baselineXml))
                    {
                        try
                        {
                            rolledBack = PersistVisualXml(obj, contextResult, target, baselineXml, baselineXml: null) == null;
                            if (rolledBack)
                            {
                                var restoredObject = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? _objectService.FindObject(target);
                                var restoredContext = LoadVisualContext(restoredObject ?? obj, target, VisualSurface.Any);
                                rollbackVerified = restoredContext.Error == null
                                    && FindControlElement(restoredContext.Document, controlName) != null;
                            }
                        }
                        catch (Exception rbEx)
                        {
                            Logger.Warn($"SetProperty: rollback after read-back failure failed: {rbEx.Message}");
                        }
                    }

                    var extra = new JObject
                    {
                        ["rolledBack"] = rolledBack,
                        ["rollbackVerified"] = rollbackVerified,
                        ["control"] = controlName,
                        ["missingAfterSave"] = diff["missing"],
                        ["appearedAfterSave"] = diff["appeared"]
                    };
                    return Models.McpResponse.Err(
                        code: "LayoutReadBackFailed",
                        message: "Layout read-back failed: control '" + controlName + "' was not found after save."
                            + (rolledBack ? (rollbackVerified ? " The write was rolled back and the control is present again." : " A rollback was attempted but the control could not be confirmed afterwards.") : " The write could NOT be rolled back."),
                        hint: "The SDK rewrote the control on save (see missingAfterSave / appearedAfterSave for renamed or dropped controls); use get_tree and compare with genexus_read part=WebForm to verify the persisted layout.",
                        nextSteps: new JArray(LayoutGetTreeStep(target, "Re-reads the persisted layout to confirm the current control names.")),
                        target: target,
                        extra: extra,
                        errorExtra: (JObject)extra.DeepClone());
                }

                string persistedValue;
                if (string.Equals(attrName, "InnerText", StringComparison.Ordinal))
                {
                    persistedValue = persistedElement.Value;
                }
                else if (string.Equals(attrName, "Caption", StringComparison.OrdinalIgnoreCase) || string.Equals(attrName, "CaptionExpression", StringComparison.OrdinalIgnoreCase))
                {
                    // Issue #360. A caption has two homes, and which one the SDK uses
                    // depends on the control family: HTMLATT-shaped controls (textblock,
                    // gxTextBlock) are stored as CaptionExpression Tokens XML, while some
                    // keep the plain attribute. Reading only the attribute reported a stale
                    // value for a control whose caption the SDK had just rewritten as tokens
                    // — so a correct write verified against the old string, failed, and was
                    // rolled back. Both representations are now considered, and the one the
                    // SDK actually used wins.
                    persistedValue = ResolvePersistedCaption(persistedElement);
                }
                else
                {
                    // Issue #360: case-insensitive, or the verification below reads null
                    // for a control that spells the attribute differently and reports a
                    // successful write as a failed one — after rolling it back.
                    persistedValue = Attr(persistedElement, attrName);
                }

                bool match = IsPersistedValueMatch(attrName, value, persistedValue);
                bool isProcedure = string.Equals(obj.TypeDescriptor?.Name, "Procedure", StringComparison.OrdinalIgnoreCase);

                if (!match)
                {
                    if (isProcedure)
                    {
                        // Reports can defer SDK persistence. Retry a few read-backs before failing.
                        // Adaptive backoff: most persistence lands within ~350ms, so probe
                        // fast first (100/200ms) and only fall back to 350ms — cuts the
                        // common-case retry wait from up to 2.1s to under 600ms.
                        int[] backoffMs = { 100, 200, 350, 350, 500, 500 };
                        for (int attempt = 0; attempt < backoffMs.Length && !match; attempt++)
                        {
                            System.Threading.Thread.Sleep(backoffMs[attempt]);
                            var retryObject = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? _objectService.FindObject(target) ?? obj;
                            var retryContext = LoadVisualContext(retryObject, target, VisualSurface.Any);
                            if (retryContext.Error != null) break;

                            var retryElement = FindControlElement(retryContext.Document, controlName);
                            if (retryElement == null) break;

                            persistedValue = string.Equals(attrName, "InnerText", StringComparison.Ordinal)
                                ? retryElement.Value
                                : ((string.Equals(attrName, "Caption", StringComparison.OrdinalIgnoreCase) || string.Equals(attrName, "CaptionExpression", StringComparison.OrdinalIgnoreCase))
                                    ? ResolvePersistedCaption(retryElement)
                                    : Attr(retryElement, attrName));
                            match = IsPersistedValueMatch(attrName, value, persistedValue);
                        }
                    }
                    if (!match)
                    {
                        // Roll back to baseline XML on verification failure
                        if (!string.IsNullOrEmpty(baselineXml))
                        {
                            try
                            {
                                PersistVisualXml(obj, contextResult, target, baselineXml, baselineXml: null);
                            }
                            catch (Exception rbEx)
                            {
                                Logger.Warn($"SetProperty: rollback to baseline failed: {rbEx.Message}");
                            }
                        }

                        return Models.McpResponse.Err(
                            code: "LayoutWriteVerificationFailed",
                            message: "Layout write verification failed: persisted value does not match requested value after SDK save and read-back. Original layout was rolled back.",
                            hint: "The SDK may have normalised the value on save; read back the property to check the canonical form.",
                            nextSteps: new JArray(LayoutGetTreeStep(target, "Reads the current persisted value of the control.")),
                            target: target,
                            extra: new JObject { ["rolledBack"] = true });
                    }
                }

                var result = new JObject
                {
                    ["name"] = obj.Name,
                    ["surface"] = contextResult.Surface.ToString(),
                    ["control"] = controlName,
                    ["propertyName"] = attrName,
                    ["previousValue"] = previous,
                    ["value"] = persistedValue
                };
                GxMcp.Worker.Helpers.WriteResultMeta.TagSdkPath(result, GxMcp.Worker.Helpers.WriteResultMeta.RawXml);
                return Models.McpResponse.Ok(target: target, code: "LayoutWritten", result: result);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "LayoutSetPropertyException",
                    message: ex.Message,
                    hint: "Check the control name and property value, then retry.",
                    nextSteps: new JArray(LayoutGetTreeStep(target, "Re-reads the current layout to confirm the control still exists.")),
                    target: target);
            }
        }

        public string GetVisualPreview(string target)
        {
            try
            {
                var setup = BeginVisualRead(target);
                if (setup.Error != null) return setup.Error;
                var obj = setup.Object;
                var contextResult = setup.Context;

                var snapshotService = new VisualSnapshotService();
                string base64 = snapshotService.GetSnapshotBase64(contextResult.Document.ToString());

                return Models.McpResponse.Ok(target: target, code: "LayoutPreview", result: new JObject
                {
                    ["name"] = obj.Name,
                    ["type"] = obj.TypeDescriptor.Name,
                    ["surface"] = contextResult.Surface.ToString(),
                    ["snapshot"] = base64
                });
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "LayoutPreviewException",
                    message: ex.Message,
                    hint: "Verify the object has a renderable visual part and retry.",
                    nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "inspect_surface", ["name"] = target }, "Checks which visual surfaces are available for this object.")),
                    target: target);
            }
        }

        public string SetProperties(string target, JArray changes)
        {
            try
            {
                if (changes == null || changes.Count == 0)
                    return Models.McpResponse.Err(
                        code: "MissingChanges",
                        message: "Missing changes array.",
                        hint: "Provide 'changes' with at least one mutation item (each requires 'control' and 'propertyName').",
                        // no-nextStep: caller has no prior context to suggest a follow-up before they supply the argument
                        target: target);

                // object + context resolved together by BeginVisualRead

                var setup = BeginVisualRead(target);
                if (setup.Error != null) return setup.Error;
                var obj = setup.Object;
                var contextResult = setup.Context;

                var doc = contextResult.Document;
                string baselineXml = doc.ToString();
                var applied = new JArray();

                foreach (var token in changes)
                {
                    var change = token as JObject;
                    if (change == null) continue;

                    string controlName = change["control"]?.ToString();
                    string propertyName = change["propertyName"]?.ToString();
                    string value = change["value"]?.ToString();

                    if (string.IsNullOrWhiteSpace(controlName) || string.IsNullOrWhiteSpace(propertyName))
                    {
                        return Models.McpResponse.Err(
                            code: "InvalidChangeEntry",
                            message: "Invalid change entry: each item requires 'control' and 'propertyName'.",
                            hint: "Ensure every object in 'changes' has both a 'control' and a 'propertyName' field.",
                            nextSteps: new JArray(LayoutGetTreeStep(target, "Lists control names available for this object.")),
                            target: target);
                    }

                    var ambiguous = AmbiguousControlError(doc, target, controlName);
                    if (ambiguous != null) return ambiguous;
                    var element = FindControlElement(doc, controlName);
                    if (element == null)
                    {
                        return Models.McpResponse.Err(
                        code: "ControlNotFound",
                        message: "Control not found: '" + controlName + "'.",
                        hint: "Use get_tree to enumerate the control names present in this object's layout.",
                        nextSteps: new JArray(LayoutGetTreeStep(target, "Lists all controls and their ControlName values.")),
                        target: target);
                    }

                    string attrName;
                    string previous;
                    if (IsTextPropertyName(propertyName))
                    {
                        attrName = "InnerText";
                        previous = element.Value;
                        element.Value = value ?? string.Empty;
                    }
                    else
                    {
                        attrName = ResolveCanonicalAttributeName(element, propertyName);
                        if (string.Equals(attrName, "Caption", StringComparison.OrdinalIgnoreCase))
                        {
                            // Issue #360: see the single-property branch — matching
                            // case-insensitively and writing the hardcoded spelling was what
                            // produced the duplicate-attribute SDK rejection.
                            previous = Attr(element, "Caption") ?? ExtractConstantCaptionFromTokens(Attr(element, "CaptionExpression"));
                            WriteAttributeCaseInsensitive(element, "Caption", value ?? string.Empty);
                            if (Attr(element, "CaptionExpression") != null)
                            {
                                WriteAttributeCaseInsensitive(element, "CaptionExpression",
                                    BuildConstantCaptionTokens(value ?? string.Empty));
                            }
                        }
                        else
                        {
                            // Issue #360: same case-insensitive read and duplicate-collapsing
                            // write as the single-property path above. The two paths must not
                            // disagree about how a control spells its attributes, or a batch
                            // write reports a different previousValue than the single write
                            // it is meant to mirror.
                            previous = Attr(element, attrName);
                            WriteAttributeCaseInsensitive(element, attrName, value ?? string.Empty);
                        }
                    }

                    applied.Add(new JObject
                    {
                        ["control"] = controlName,
                        ["propertyName"] = attrName,
                        ["previousValue"] = previous,
                        ["value"] = value ?? string.Empty
                    });
                }

                string normalized = doc.ToString();
                var persistError = PersistVisualXml(
                    obj,
                    contextResult,
                    target,
                    normalized,
                    baselineXml: baselineXml);
                if (persistError != null) return persistError;

                var persistedObject = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? _objectService.FindObject(target);
                var persistedContext = LoadVisualContext(persistedObject ?? obj, target, VisualSurface.Any);
                if (persistedContext.Error != null) return persistedContext.Error;

                foreach (var token in applied)
                {
                    var appliedItem = token as JObject;
                    if (appliedItem == null) continue;

                    string controlName = appliedItem["control"]?.ToString();
                    string attrName = appliedItem["propertyName"]?.ToString();
                    string expected = appliedItem["value"]?.ToString() ?? string.Empty;

                    var persistedEl = FindControlElement(persistedContext.Document, controlName);
                    if (persistedEl == null)
                    {
                        bool rolledBack = false;
                        if (!string.IsNullOrEmpty(baselineXml))
                        {
                            try
                            {
                                var rbErr = PersistVisualXml(obj, contextResult, target, baselineXml);
                                rolledBack = rbErr == null;
                            }
                            catch { }
                        }
                        return Models.McpResponse.Err(
                            code: "LayoutReadBackFailed",
                            message: "Layout read-back failed: control '" + controlName + "' was not found after save." + (rolledBack ? " Changes were rolled back." : ""),
                            hint: "The SDK may have renamed or dropped the control on save; use get_tree to verify.",
                            nextSteps: new JArray(LayoutGetTreeStep(target, "Re-reads the persisted layout to confirm current control names.")),
                            target: target,
                            extra: new JObject { ["rolledBack"] = rolledBack, ["control"] = controlName, ["property"] = attrName });
                    }

                    // Issue #360: the same case-insensitive read and the same
                    // caption-home resolution as the single-property path. Left as it was,
                    // a batch set_properties on a control spelling its attributes in the
                    // SDK's lower-case form compared the requested value against null, and
                    // a batch Caption change compared against the stale string the SDK had
                    // already replaced with tokens - either way a correct batch was rolled
                    // back as a whole, which loses every property in it, not just the one
                    // that disagreed.
                    string actual = string.Equals(attrName, "InnerText", StringComparison.Ordinal)
                        ? (persistedEl.Value ?? string.Empty)
                        : ((string.Equals(attrName, "Caption", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(attrName, "CaptionExpression", StringComparison.OrdinalIgnoreCase))
                            ? ResolvePersistedCaption(persistedEl)
                            : (Attr(persistedEl, attrName) ?? string.Empty));
                    if (!IsPersistedValueMatch(attrName, expected, actual))
                    {
                        bool rolledBack = false;
                        if (!string.IsNullOrEmpty(baselineXml))
                        {
                            try
                            {
                                var rbErr = PersistVisualXml(obj, contextResult, target, baselineXml);
                                rolledBack = rbErr == null;
                            }
                            catch { }
                        }
                        return Models.McpResponse.Err(
                            code: "LayoutWriteVerificationFailed",
                            message: "Layout write verification failed: persisted value for control '" + controlName + "' property '" + attrName + "' does not match requested value." + (rolledBack ? " Changes were rolled back." : ""),
                            hint: "The SDK may have normalised the value on save; read back the property to check the canonical form.",
                            nextSteps: new JArray(LayoutGetTreeStep(target, "Reads the current persisted value of the control.")),
                            target: target,
                            extra: new JObject { ["rolledBack"] = rolledBack, ["control"] = controlName, ["property"] = attrName, ["expected"] = expected, ["actual"] = actual });
                    }
                }

                var bulkResult = new JObject
                {
                    ["name"] = obj.Name,
                    ["surface"] = contextResult.Surface.ToString(),
                    ["applied"] = applied,
                    ["count"] = applied.Count
                };
                GxMcp.Worker.Helpers.WriteResultMeta.TagSdkPath(bulkResult, GxMcp.Worker.Helpers.WriteResultMeta.RawXml);
                return Models.McpResponse.Ok(target: target, code: "LayoutWritten", result: bulkResult);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "LayoutSetPropertiesException",
                    message: ex.Message,
                    hint: "Check the change entries and retry; use get_tree to confirm valid control names.",
                    nextSteps: new JArray(LayoutGetTreeStep(target, "Re-reads the layout to confirm control names before retrying.")),
                    target: target);
            }
        }

        public string RenamePrintBlock(string target, string currentName, string newName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(currentName) || string.IsNullOrWhiteSpace(newName))
                {
                    return Models.McpResponse.Err(
                        code: "MissingPrintBlockNames",
                        message: "Missing print block names.",
                        hint: "Provide both 'currentName' and 'newName' to rename a print block.",
                        // no-nextStep: caller must supply the argument values before any tool call is meaningful
                        target: target);
                }

                var setup = BeginReportMutation(target);
                if (setup.Error != null) return setup.Error;

                // Unpacked under the names the mutation body already used, so the
                // transaction block below is unchanged by the prologue's extraction.
                KBObject obj = setup.Object;
                LayoutContextResult context = setup.Context;
                dynamic kb = setup.KnowledgeBase;
                string sourceSnapshot = setup.SourceSnapshot;

                using (var tx = kb.BeginTransaction())
                {
                    try
                    {
                        if (!TryNormalizeReportPrintCommandsInSourceInMemory(obj, context.Document.ToString(), out string normalizeError))
                        {
                            tx.Rollback();
                            return Models.McpResponse.Err(
                                code: "RenamePrintBlockSourceSyncFailed",
                                message: "Rename print block source sync failed: " + normalizeError,
                                hint: "The Procedure source could not be updated to match the renamed print block; the transaction was rolled back.",
                                nextSteps: new JArray(LayoutGetTreeStep(target, "Re-reads the layout to confirm the current print block names.")),
                                target: target);
                        }

                        if (!ReportLayoutHelper.RenamePrintBlock(context.VisualPart, currentName, newName, persist: false))
                        {
                            TryRestoreProcedureSource(obj, sourceSnapshot);
                            tx.Rollback();
                            return Models.McpResponse.Err(
                                code: "RenamePrintBlockFailed",
                                message: "Rename print block failed: the SDK could not stage the rename operation.",
                                hint: "Verify that 'currentName' matches an existing print block in the report layout.",
                                nextSteps: new JArray(LayoutGetTreeStep(target, "Lists all print blocks in the report layout.")),
                                target: target);
                        }

                        if (!TryRenamePrintCommandInSourceInMemory(obj, currentName, newName, out string sourcePrepareError))
                        {
                            TryRestoreProcedureSource(obj, sourceSnapshot);
                            tx.Rollback();
                            return Models.McpResponse.Err(
                                code: "RenamePrintBlockSourceSyncFailed",
                                message: "Rename print block source sync failed: " + sourcePrepareError,
                                hint: "The Procedure source rename step failed; the transaction was rolled back.",
                                nextSteps: new JArray(LayoutGetTreeStep(target, "Re-reads the layout to confirm current print block names.")),
                                target: target);
                        }

                        if (!TrySaveVisualPart(context.VisualPart, out string partSaveError))
                        {
                            TryRestoreProcedureSource(obj, sourceSnapshot);
                            tx.Rollback();
                            return Models.McpResponse.Err(
                                code: "RenamePrintBlockFailed",
                                message: "Rename print block failed: " + partSaveError,
                                hint: "The visual part save failed after staging; the transaction was rolled back.",
                                nextSteps: new JArray(LayoutGetTreeStep(target, "Re-reads the layout to check if the rename partially persisted.")),
                                target: target);
                        }

                        obj.EnsureSave(true);
                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        TryRestoreProcedureSource(obj, sourceSnapshot);
                        tx.Rollback();
                        return Models.McpResponse.Err(
                            code: "RenamePrintBlockFailed",
                            message: "Rename print block failed: " + ex.Message,
                            hint: "An unexpected exception occurred; the transaction was rolled back.",
                            nextSteps: new JArray(LayoutGetTreeStep(target, "Re-reads the layout to confirm the current state.")),
                            target: target);
                    }
                }
                _objectService.MarkReadCacheDirty(obj, "Layout");
                WriteService.NotePerTargetWrite(target);

                var refreshedObj = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? _objectService.FindObject(target) ?? obj;
                var refreshed = LoadVisualContext(refreshedObj, target, VisualSurface.Report);
                if (refreshed.Error != null) return refreshed.Error;

                bool exists = ContainsPrintBlock(refreshed.Document, newName);
                if (!exists)
                {
                    for (int attempt = 0; attempt < 20 && !exists; attempt++)
                    {
                        System.Threading.Thread.Sleep(500);
                        var retryObj = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? _objectService.FindObject(target) ?? obj;
                        var retry = LoadVisualContext(retryObj, target, VisualSurface.Report);
                        if (retry.Error != null) break;
                        exists = ContainsPrintBlock(retry.Document, newName);
                    }
                }
                if (!exists)
                {
                    TryHealPrintCommandSourceAfterCommit(obj, target);

                    return Models.McpResponse.Err(
                        code: "RenamePrintBlockVerificationFailed",
                        message: "Rename print block verification failed: the renamed print block was not found in the persisted report XML.",
                        hint: "The SDK committed the transaction but the read-back did not surface the new name; open the Procedure in the IDE to inspect.",
                        nextSteps: new JArray(LayoutGetTreeStep(target, "Re-reads the layout to see the current print block names.")),
                        target: target);
                }

                return Models.McpResponse.Ok(target: target, code: "PrintBlockRenamed", result: new JObject
                {
                    ["name"] = obj.Name,
                    ["operation"] = "RenamePrintBlock",
                    ["currentName"] = currentName,
                    ["newName"] = newName
                });
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "RenamePrintBlockException",
                    message: ex.Message,
                    hint: "An unexpected exception occurred; retry or inspect the Procedure in the IDE.",
                    nextSteps: new JArray(LayoutGetTreeStep(target, "Re-reads the layout to confirm the current state.")),
                    target: target);
            }
        }

        public string AddPrintBlock(string target, string printBlockName, int? height)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(printBlockName))
                {
                    return Models.McpResponse.Err(
                        code: "MissingPrintBlockName",
                        message: "Missing print block name.",
                        hint: "Provide 'printBlockName' with a non-empty identifier for the new print block.",
                        // no-nextStep: caller must supply the argument value before any tool call is meaningful
                        target: target);
                }

                var setup = BeginReportMutation(target);
                if (setup.Error != null) return setup.Error;

                // Unpacked under the names the mutation body already used, so the
                // transaction block below is unchanged by the prologue's extraction.
                KBObject obj = setup.Object;
                LayoutContextResult context = setup.Context;
                dynamic kb = setup.KnowledgeBase;
                string sourceSnapshot = setup.SourceSnapshot;

                using (var tx = kb.BeginTransaction())
                {
                    try
                    {
                        if (!TryNormalizeReportPrintCommandsInSourceInMemory(obj, context.Document.ToString(), out string normalizeError))
                        {
                            tx.Rollback();
                            return Models.McpResponse.Err(
                                code: "AddPrintBlockSourceSyncFailed",
                                message: "Add print block source sync failed: " + normalizeError,
                                hint: "The Procedure source could not be updated; the transaction was rolled back.",
                                nextSteps: new JArray(LayoutGetTreeStep(target, "Re-reads the layout to confirm the current print blocks.")),
                                target: target);
                        }

                        if (!ReportLayoutHelper.AddPrintBlock(context.VisualPart, printBlockName, height, persist: false))
                        {
                            TryRestoreProcedureSource(obj, sourceSnapshot);
                            tx.Rollback();
                            return Models.McpResponse.Err(
                                code: "AddPrintBlockFailed",
                                message: "Add print block failed: the SDK could not stage the new print block.",
                                hint: "Ensure the Procedure has a report layout part and the printBlockName is unique.",
                                nextSteps: new JArray(LayoutGetTreeStep(target, "Lists existing print blocks to check for name conflicts.")),
                                target: target);
                        }

                        if (!TryInsertPrintCommandInSourceInMemory(obj, printBlockName, out string sourcePrepareError))
                        {
                            TryRestoreProcedureSource(obj, sourceSnapshot);
                            tx.Rollback();
                            return Models.McpResponse.Err(
                                code: "AddPrintBlockSourceSyncFailed",
                                message: "Add print block source sync failed: " + sourcePrepareError,
                                hint: "The Procedure source insertion step failed; the transaction was rolled back.",
                                nextSteps: new JArray(LayoutGetTreeStep(target, "Re-reads the layout to confirm current print blocks.")),
                                target: target);
                        }

                        if (!TrySaveVisualPart(context.VisualPart, out string partSaveError))
                        {
                            TryRestoreProcedureSource(obj, sourceSnapshot);
                            tx.Rollback();
                            return Models.McpResponse.Err(
                                code: "AddPrintBlockFailed",
                                message: "Add print block failed: " + partSaveError,
                                hint: "The visual part save failed after staging; the transaction was rolled back.",
                                nextSteps: new JArray(LayoutGetTreeStep(target, "Re-reads the layout to check if the block partially persisted.")),
                                target: target);
                        }

                        obj.EnsureSave(true);
                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        TryRestoreProcedureSource(obj, sourceSnapshot);
                        tx.Rollback();
                        return Models.McpResponse.Err(
                            code: "AddPrintBlockFailed",
                            message: "Add print block failed: " + ex.Message,
                            hint: "An unexpected exception occurred; the transaction was rolled back.",
                            nextSteps: new JArray(LayoutGetTreeStep(target, "Re-reads the layout to confirm the current state.")),
                            target: target);
                    }
                }
                _objectService.MarkReadCacheDirty(obj, "Layout");
                WriteService.NotePerTargetWrite(target);

                var refreshedObj = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? _objectService.FindObject(target) ?? obj;
                var refreshed = LoadVisualContext(refreshedObj, target, VisualSurface.Report);
                if (refreshed.Error != null) return refreshed.Error;

                var added = FindPrintBlockByName(refreshed.Document, printBlockName);
                if (added == null)
                {
                    for (int attempt = 0; attempt < 20 && added == null; attempt++)
                    {
                        System.Threading.Thread.Sleep(500);
                        var retryObj = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? _objectService.FindObject(target) ?? obj;
                        var retry = LoadVisualContext(retryObj, target, VisualSurface.Report);
                        if (retry.Error != null) break;
                        added = FindPrintBlockByName(retry.Document, printBlockName);
                    }
                }
                if (added == null)
                {
                    TryHealPrintCommandSourceAfterCommit(obj, target);

                    return Models.McpResponse.Err(
                        code: "AddPrintBlockVerificationFailed",
                        message: "Add print block verification failed: the new print block was not found in the persisted report XML.",
                        hint: "The SDK committed the transaction but the read-back did not surface the new block; open the Procedure in the IDE to inspect.",
                        nextSteps: new JArray(LayoutGetTreeStep(target, "Re-reads the layout to see current print blocks.")),
                        target: target);
                }

                return Models.McpResponse.Ok(target: target, code: "PrintBlockAdded", result: new JObject
                {
                    ["name"] = obj.Name,
                    ["operation"] = "AddPrintBlock",
                    ["printBlockName"] = printBlockName,
                    ["height"] = Attr(added, "Height")
                });
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "AddPrintBlockException",
                    message: ex.Message,
                    hint: "An unexpected exception occurred; retry or inspect the Procedure in the IDE.",
                    nextSteps: new JArray(LayoutGetTreeStep(target, "Re-reads the layout to confirm the current state.")),
                    target: target);
            }
        }

        public string DeletePrintBlock(string target, string printBlockName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(printBlockName))
                {
                    return Models.McpResponse.Err(
                        code: "MissingArgument",
                        message: "printBlockName is required.",
                        hint: "Pass the name of the print block to remove, e.g. printBlockName=header.",
                        nextSteps: new JArray(LayoutGetTreeStep(target, "Lists all print blocks in the report layout.")),
                        target: target);
                }

                var setup = BeginReportMutation(target);
                if (setup.Error != null) return setup.Error;

                // Unpacked under the names the mutation body already used, so the
                // transaction block below is unchanged by the prologue's extraction.
                KBObject obj = setup.Object;
                LayoutContextResult context = setup.Context;
                dynamic kb = setup.KnowledgeBase;
                string sourceSnapshot = setup.SourceSnapshot;

                using (var tx = kb.BeginTransaction())
                {
                    try
                    {
                        if (!ReportLayoutHelper.DeletePrintBlock(context.VisualPart, printBlockName, persist: false))
                        {
                            tx.Rollback();
                            return Models.McpResponse.Err(
                                code: "DeletePrintBlockFailed",
                                message: "Delete print block failed: the SDK could not stage the removal of '" + printBlockName + "'.",
                                hint: "Ensure the print block exists and is not protected (Header/Footer bands may be required by the report).",
                                nextSteps: new JArray(LayoutGetTreeStep(target, "Lists existing print blocks.")),
                                target: target);
                        }

                        if (!TryRemovePrintCommandFromSourceInMemory(obj, printBlockName, out string sourceSyncError))
                        {
                            TryRestoreProcedureSource(obj, sourceSnapshot);
                            tx.Rollback();
                            return Models.McpResponse.Err(
                                code: "DeletePrintBlockSourceSyncFailed",
                                message: "Delete print block source sync failed: " + sourceSyncError,
                                hint: "The Procedure source could not be updated; the transaction was rolled back.",
                                nextSteps: new JArray(LayoutGetTreeStep(target, "Re-reads the layout to confirm current print blocks.")),
                                target: target);
                        }

                        if (!TrySaveVisualPart(context.VisualPart, out string partSaveError))
                        {
                            TryRestoreProcedureSource(obj, sourceSnapshot);
                            tx.Rollback();
                            return Models.McpResponse.Err(
                                code: "DeletePrintBlockPersistFailed",
                                message: "Delete print block persistence failed: " + partSaveError,
                                hint: "The SDK could not save the layout part; the transaction was rolled back.",
                                nextSteps: new JArray(LayoutGetTreeStep(target, "Re-reads the layout to confirm current print blocks.")),
                                target: target);
                        }

                        obj.EnsureSave(true);
                        tx.Commit();
                    }
                    catch
                    {
                        try { tx.Rollback(); } catch { }
                        throw;
                    }
                }
                _objectService.MarkReadCacheDirty(obj, "Layout");
                WriteService.NotePerTargetWrite(target);

                // Cold read-back to prove the block is really gone from disk.
                var refreshedObj = _objectService.FindObject(obj.Name, obj.TypeDescriptor?.Name) ?? obj;
                var refreshed = LoadVisualContext(refreshedObj, target, VisualSurface.Report);
                bool stillThere = refreshed.Error == null && refreshed.Document != null
                    && ContainsPrintBlock(refreshed.Document, printBlockName);
                if (stillThere)
                {
                    return Models.McpResponse.Err(
                        code: "DeletePrintBlockVerificationFailed",
                        message: "Delete print block verification failed: '" + printBlockName + "' is still present after commit.",
                        hint: "The transaction committed but the read-back still shows the block; open the Procedure in the IDE to inspect.",
                        nextSteps: new JArray(LayoutGetTreeStep(target, "Re-reads the layout to see current print blocks.")),
                        target: target);
                }

                return Models.McpResponse.Ok(target: target, code: "PrintBlockDeleted", result: new JObject
                {
                    ["name"] = obj.Name,
                    ["operation"] = "DeletePrintBlock",
                    ["printBlockName"] = printBlockName
                });
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "DeletePrintBlockException",
                    message: ex.Message,
                    hint: "An unexpected exception occurred; retry or inspect the Procedure in the IDE.",
                    nextSteps: new JArray(LayoutGetTreeStep(target, "Re-reads the layout to confirm the current state.")),
                    target: target);
            }
        }

        public string InspectSurface(string target, int limit = 50)
        {
            try
            {
                var obj = _objectService.FindObject(target);
                if (obj == null)
                {
                    return VisualObjectNotFound(target);
                }

                var parts = new[] { "Layout", "PatternVirtual", "WebForm" };
                var surfaces = new JArray();
                var partsCatalog = new JArray();

                foreach (KBObjectPart p in obj.Parts)
                {
                    partsCatalog.Add(new JObject
                    {
                        ["name"] = p.TypeDescriptor?.Name ?? p.GetType().Name,
                        ["guid"] = p.Type.ToString(),
                        ["type"] = p.GetType().FullName,
                        ["isSource"] = p is ISource
                    });
                }

                int totalCandidates = 0;

                foreach (var partName in parts)
                {
                    var part = PartAccessor.GetPart(obj, partName);
                    if (part == null) continue;

                    var partInfo = new JObject
                    {
                        ["part"] = partName,
                        ["type"] = part.GetType().FullName,
                        ["isSource"] = part is ISource
                    };

                    var xmlCandidates = new JArray();
                    var candidatesCollected = CollectXmlCandidates(part, includeNonPublic: true, includeNested: true);
                    totalCandidates += candidatesCollected.Count;

                    foreach (var candidate in candidatesCollected.OrderByDescending(c => c.Score).Take(limit))
                    {
                        xmlCandidates.Add(new JObject
                        {
                            ["member"] = candidate.MemberName,
                            ["sourcePath"] = candidate.SourcePath,
                            ["kind"] = candidate.MemberKind,
                            ["writable"] = candidate.MemberWritable,
                            ["depth"] = candidate.Depth,
                            ["score"] = candidate.Score,
                            ["root"] = candidate.Document?.Root?.Name.LocalName,
                            ["nodes"] = candidate.Document?.Descendants().Count() ?? 0,
                            // Issue #360: counted through the case-sensitive overload, so a
                            // form that spells its identity `controlName` - which is how the
                            // SDK and the IDE both write it - reported zero controls in the
                            // surface diagnostic. That is the same "your control is not
                            // there" report the issue was filed about, in the one place a
                            // caller might have gone to check whether it was true.
                            ["controlAttrs"] = candidate.Document?.Descendants()
                                .Count(e => Attr(e, "ControlName") != null) ?? 0
                        });
                    }

                    partInfo["candidatesCount"] = candidatesCollected.Count;
                    partInfo["candidatesReturned"] = xmlCandidates.Count;
                    partInfo["xmlCandidates"] = xmlCandidates;
                    surfaces.Add(partInfo);
                }

                bool isEmpty = surfaces.Count == 0;

                var resultObj = new JObject();
                resultObj["name"] = obj.Name;
                resultObj["type"] = obj.TypeDescriptor.Name;
                resultObj["empty"] = isEmpty;
                resultObj["totalSurfaces"] = surfaces.Count;
                resultObj["totalParts"] = partsCatalog.Count;
                resultObj["totalCandidates"] = totalCandidates;
                resultObj["partsCatalog"] = partsCatalog;
                resultObj["surfaces"] = surfaces;
                
                if (isEmpty)
                {
                    resultObj["help"] = "No structural definitions found. Object lacks supported visual XML parts.";
                }
                else if (totalCandidates > limit)
                {
                    resultObj["help"] = $"Output truncated ({limit} out of {totalCandidates} candidates shown per surface).";
                }

                return Models.McpResponse.Ok(target: target, code: "LayoutSurfaceInspected", result: resultObj);
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "LayoutInspectSurfaceException",
                    message: ex.Message,
                    hint: "Verify the object exists in the active KB and retry.",
                    nextSteps: new JArray(Models.McpResponse.NextStep("genexus_inspect", new JObject { ["name"] = target }, "Confirms the object is present in the KB.")),
                    target: target);
            }
        }


        private static void Walk(XElement current, string path, JArray nodes, ref int total, ref int emitted, int limit, string controlFilter, FindCriteria findCriteria, Dictionary<string, int> stats)
        {
            if (current == null) return;
            total++;

            string tag = current.Name.LocalName;
            if (stats != null)
            {
                stats[tag] = stats.TryGetValue(tag, out int currentCount) ? currentCount + 1 : 1;
            }

            string controlName = Attr(current, "ControlName");
            bool matchesByControl = string.IsNullOrWhiteSpace(controlFilter) ||
                                    string.Equals(controlName, controlFilter, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(Attr(current, "InternalName"), controlFilter, StringComparison.OrdinalIgnoreCase);
            bool matchesByCriteria = MatchesCriteria(current, findCriteria);
            bool matches = matchesByControl && matchesByCriteria;

            if (matches && emitted < limit)
            {
                var node = new JObject
                {
                    ["p"] = path,
                    ["t"] = tag,
                    ["n"] = controlName,
                    ["c"] = Attr(current, "Caption") ?? Attr(current, "Text"),
                    ["k"] = Attr(current, "Class"),
                    ["v"] = Attr(current, "Attribute") ?? Attr(current, "Variable"),
                    ["left"] = Attr(current, "Left") ?? Attr(current, "X"),
                    ["top"] = Attr(current, "Top") ?? Attr(current, "Y"),
                    ["width"] = Attr(current, "Width"),
                    ["height"] = Attr(current, "Height"),
                    ["font"] = Attr(current, "Font") ?? Attr(current, "FontName"),
                    ["fontSize"] = Attr(current, "FontSize"),
                    ["alignment"] = Attr(current, "Alignment"),
                    ["picture"] = Attr(current, "Picture")
                };
                nodes.Add(node);
                emitted++;
            }

            int idx = 0;
            foreach (var child in current.Elements())
            {
                idx++;
                Walk(child, path + "/" + child.Name.LocalName + "[" + idx + "]", nodes, ref total, ref emitted, limit, controlFilter, findCriteria, stats);
            }
        }

        private static bool MatchesCriteria(XElement element, FindCriteria criteria)
        {
            if (criteria == null) return true;
            if (string.IsNullOrWhiteSpace(criteria.PropertyName) && string.IsNullOrWhiteSpace(criteria.Query)) return true;

            string searchValue;
            if (!string.IsNullOrWhiteSpace(criteria.PropertyName))
            {
                if (IsTextPropertyName(criteria.PropertyName))
                {
                    searchValue = element.Value;
                }
                else
                {
                    var resolved = ResolveCanonicalAttributeName(element, criteria.PropertyName);
                    searchValue = Attr(element, resolved);
                }
            }
            else
            {
                searchValue = string.Join(" ",
                    Attr(element, "ControlName"),
                    Attr(element, "InternalName"),
                    Attr(element, "Caption"),
                    Attr(element, "Class"),
                    Attr(element, "Attribute"),
                    Attr(element, "Variable"),
                    element.Name.LocalName);
            }

            if (string.IsNullOrWhiteSpace(criteria.Query)) return true;
            return (searchValue ?? string.Empty).IndexOf(criteria.Query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Locates a layout control by name, path, or legacy id.
        ///
        /// <para>
        /// Internal rather than private so the identity-casing regression is covered
        /// behaviourally: the defect was a one-line overload choice inside
        /// <c>Attr</c>, and a source-shape assertion over this method could not fail for
        /// it.
        /// </para>
        /// </summary>
        internal static XElement FindControlElement(XDocument doc, string controlName)
        {
            if (string.IsNullOrWhiteSpace(controlName)) return null;

            if (controlName.StartsWith("/", StringComparison.Ordinal))
            {
                return FindElementByPath(doc, controlName);
            }

            var match = doc
                .Descendants()
                .FirstOrDefault(el =>
                    string.Equals(Attr(el, "ControlName"), controlName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Attr(el, "InternalName"), controlName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            // Fallback: legacy gxTextBlock / fieldset / table emit an `id` attribute instead of
            // ControlName. Search those only after the canonical fields miss so we don't
            // accidentally hijack a name when both forms coexist on different elements.
            return doc
                .Descendants()
                .FirstOrDefault(el =>
                    string.Equals(Attr(el, "id"), controlName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The control identities (ControlName, else id, else InternalName) in a document.
        /// </summary>
        private static HashSet<string> ControlIdentities(XDocument doc)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (doc == null) return set;
            foreach (var el in doc.Descendants())
            {
                string id = Attr(el, "ControlName") ?? Attr(el, "InternalName") ?? Attr(el, "id");
                if (!string.IsNullOrEmpty(id)) set.Add(el.Name.LocalName + ":" + id);
            }
            return set;
        }

        /// <summary>
        /// Which controls (as "element:identity") the baseline had that the persisted
        /// document lost, and which the persisted document has that the baseline did not.
        /// A rename on save shows up as one of each.
        /// </summary>
        internal static JObject DescribeIdentityDrift(string baselineXml, XDocument persisted)
        {
            XDocument baseline = null;
            try { if (!string.IsNullOrWhiteSpace(baselineXml)) baseline = XDocument.Parse(baselineXml); } catch { }
            var before = ControlIdentities(baseline);
            var after = ControlIdentities(persisted);
            return new JObject
            {
                ["missing"] = new JArray(before.Where(x => !after.Contains(x))),
                ["appeared"] = new JArray(after.Where(x => !before.Contains(x)))
            };
        }

        private static XElement FindElementByPath(XDocument doc, string path)
        {
            if (doc?.Root == null || string.IsNullOrWhiteSpace(path) || !path.StartsWith("/", StringComparison.Ordinal))
            {
                return null;
            }

            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return null;

            XElement current = doc.Root;
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                string name = segment;
                int index = 1;

                int idxStart = segment.LastIndexOf('[');
                int idxEnd = segment.LastIndexOf(']');
                if (idxStart > 0 && idxEnd > idxStart)
                {
                    name = segment.Substring(0, idxStart);
                    int parsedIndex;
                    if (int.TryParse(segment.Substring(idxStart + 1, idxEnd - idxStart - 1), out parsedIndex) && parsedIndex > 0)
                    {
                        index = parsedIndex;
                    }
                }

                if (i == 0)
                {
                    if (!string.Equals(current.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }
                    continue;
                }

                var byName = current.Elements(name).ElementAtOrDefault(index - 1);
                if (byName != null)
                {
                    current = byName;
                    continue;
                }

                var byAbsoluteIndex = current.Elements().ElementAtOrDefault(index - 1);
                if (byAbsoluteIndex != null && string.Equals(byAbsoluteIndex.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
                {
                    current = byAbsoluteIndex;
                    continue;
                }

                current = null;
                if (current == null) return null;
            }

            return current;
        }

        /// <summary>
        /// Report layouts only: refuses a bare control name that matches more than one
        /// control (the same label name in two print blocks). <see cref="FindControlElement"/>
        /// would silently take the first, so the caller could not tell which one changed.
        /// A path ("/Report/PrintBlock[2]/Control[1]", as emitted by get_tree 'p') bypasses
        /// the check. Returns null when the name is unique or the layout is not a report.
        /// </summary>
        internal static string AmbiguousControlError(XDocument doc, string target, string controlName)
        {
            if (doc == null || string.IsNullOrWhiteSpace(controlName)
                || controlName.StartsWith("/", StringComparison.Ordinal)
                || !doc.Descendants("PrintBlock").Any())
                return null;

            var matches = doc.Descendants()
                .Where(el =>
                    string.Equals(Attr(el, "ControlName"), controlName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Attr(el, "InternalName"), controlName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count < 2) return null;

            var paths = new JArray();
            foreach (var m in matches)
            {
                var segments = new List<string>();
                for (var cur = m; cur != null; cur = cur.Parent)
                {
                    int idx = cur.Parent == null ? 1 : cur.Parent.Elements(cur.Name).TakeWhile(x => x != cur).Count() + 1;
                    // Same shape as get_tree 'p': the root carries no index.
                    segments.Insert(0, cur.Parent == null ? cur.Name.LocalName : cur.Name.LocalName + "[" + idx + "]");
                }
                var block = m.Ancestors("PrintBlock").FirstOrDefault();
                paths.Add(new JObject
                {
                    ["path"] = "/" + string.Join("/", segments),
                    ["printBlock"] = block == null ? null : (Attr(block, "Name") ?? Attr(block, "ControlName"))
                });
            }

            return Models.McpResponse.Err(
                code: "AmbiguousControl",
                message: "Control name '" + controlName + "' matches " + matches.Count
                    + " controls in this report layout; refusing to guess which one to change. Matches: "
                    + paths.ToString(Newtonsoft.Json.Formatting.None),
                hint: "Pass the full path of the intended control (the 'p' value from get_tree, e.g. "
                    + "/Report/PrintBlock[2]/Control[1]) as 'control' instead of the bare name.",
                nextSteps: new JArray(LayoutGetTreeStep(target, "Lists every control with its path ('p') to disambiguate.")),
                target: target);
        }

        private static string ResolveCanonicalAttributeName(XElement element, string requested)
        {
            var knownAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "caption", "Caption" },
                { "text", "Caption" },
                { "class", "Class" },
                { "visible", "Visible" },
                { "enabled", "Enabled" },
                { "readonly", "ReadOnly" },
                { "x", "Left" },
                { "left", "Left" },
                { "y", "Top" },
                { "top", "Top" }
            };

            if (knownAliases.TryGetValue(requested ?? string.Empty, out string alias))
            {
                requested = alias;
            }

            var existing = element.Attributes()
                .FirstOrDefault(a => string.Equals(a.Name.LocalName, requested, StringComparison.OrdinalIgnoreCase));

            return existing != null ? existing.Name.LocalName : (requested ?? string.Empty);
        }

        /// <summary>
        /// An attribute's value, or null when absent.
        ///
        /// <para>
        /// Issue #360. This was <c>element.Attribute(name)</c>, and XLinq's single-argument
        /// <c>Attribute</c> overload is <b>case-sensitive</b> — so reading a WebForm whose
        /// control spells its identity <c>controlName</c> (which is how the SDK and the IDE
        /// both write it) returned null even though the attribute was right there. Every
        /// caller then concluded the control did not exist: <c>genexus_layout
        /// action=set_property</c> and <c>genexus_properties action=set control=...</c>
        /// both answered <c>ControlNotFound</c> for a control <c>get_tree</c> listed, on a
        /// plain WebPanel with no pattern instance.
        /// </para>
        ///
        /// <para>
        /// The casing a KB uses is not ours to fix — it varies by generator vintage and by
        /// which family wrote the form — so the read is made case-insensitive instead. The
        /// sibling helper on the typed property path, <c>WebFormSdkReflection</c>, already
        /// had to list <c>id | ControlName | controlName | InternalName | name</c> by hand
        /// for exactly this reason; that duplication is what made the two paths disagree
        /// about whether a control exists.
        /// </para>
        /// </summary>
        private static string Attr(XElement element, string name)
        {
            if (element == null || string.IsNullOrEmpty(name)) return null;

            var attr = element.Attribute(name);
            if (attr != null) return attr.Value;

            // Namespace-prefixed spellings (gx:ControlName) resolve on LocalName only.
            foreach (var candidate in element.Attributes())
            {
                if (string.Equals(candidate.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
                    return candidate.Value;
            }
            return null;
        }

        /// <summary>
        /// The caption as the SDK actually stored it, given a control that can hold it in
        /// either of two places.
        ///
        /// <para>
        /// Issue #360. A caption is <c>CaptionExpression</c> Tokens XML on the
        /// HTMLATT-shaped controls (textblock, gxTextBlock) and a plain attribute on
        /// others. The write goes through the SDK, which rewrites whichever representation
        /// that control uses — so verifying against only the attribute read the stale value
        /// of a control whose caption had just been rewritten as tokens. A correct write
        /// then failed verification and was rolled back, which is a worse outcome than the
        /// original bug: the caller's change was discarded.
        /// </para>
        ///
        /// <para>
        /// So the tokens are read first. When they carry a constant, that is the SDK's
        /// value; the plain attribute is only consulted when there is no constant token to
        /// read, which is the case for controls that genuinely store it there.
        /// </para>
        /// </summary>
        internal static string ResolvePersistedCaption(XElement element)
        {
            var fromTokens = ExtractConstantCaptionFromTokens(Attr(element, "CaptionExpression"));
            if (!string.IsNullOrEmpty(fromTokens)) return fromTokens;
            return Attr(element, "Caption");
        }

        /// <summary>
        /// Sets an attribute, reusing whichever spelling the element already uses and
        /// collapsing case-only duplicates onto one.
        ///
        /// <para>
        /// Issue #360. An element can legitimately arrive holding both <c>caption</c> and
        /// <c>Caption</c> — the SDK's own export uses the lower-case form, and an earlier
        /// pass wrote the canonical one. XLinq is happy with that; the SDK is not, and the
        /// save dies with "Já foi adicionado um item com a mesma chave" ("an item with the
        /// same key has already been added") — a message that names neither the attribute
        /// nor the control.
        /// </para>
        ///
        /// <para>
        /// So the duplicates are merged here: the first spelling found wins and keeps its
        /// position in document order, the rest are removed. That is the same XML the SDK
        /// would have accepted, arrived at without an SDK round-trip to discover the
        /// constraint.
        /// </para>
        /// </summary>
        internal static void WriteAttributeCaseInsensitive(XElement element, string attrName, string value)
        {
            if (element == null || string.IsNullOrEmpty(attrName)) return;

            XAttribute survivor = null;
            var duplicates = new List<XAttribute>();

            foreach (var candidate in element.Attributes())
            {
                if (candidate.IsNamespaceDeclaration) continue;
                if (!string.Equals(candidate.Name.LocalName, attrName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (survivor == null) survivor = candidate;
                else duplicates.Add(candidate);
            }

            if (survivor != null) survivor.Value = value;
            else element.SetAttributeValue(attrName, value);

            foreach (var duplicate in duplicates) duplicate.Remove();
        }

        /// <summary>
        /// Whether this property name is an SDK control property that the WebForm carries
        /// through <see cref="WebFormSchemaHints.CustomPropertiesAttribute"/> rather than
        /// as an XML attribute.
        ///
        /// <para>
        /// Issue #360. Matched on both the property id (<c>GxFormat</c>) and its display
        /// name (<c>Format</c>), because the two routes in the report used both spellings
        /// and neither worked. Deliberately narrow: a rejection is worse than a redundant
        /// hint, so this names only the properties confirmed to live in the carrier.
        /// </para>
        /// </summary>
        internal static bool IsCustomPropertyOnly(string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName)) return false;
            string normalized = propertyName.Trim();

            if (string.Equals(normalized, "GxFormat", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(normalized, "Format", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(normalized, "ControlType", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(normalized, "ControlValues", StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        private static bool IsTextPropertyName(string propertyName)
        {
            return string.Equals(propertyName, "text", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(propertyName, "innertext", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(propertyName, "nodevalue", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(propertyName, "value", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool HasCaptionLineBreak(string value)
        {
            return (value ?? string.Empty).IndexOfAny(CaptionLineBreaks) >= 0;
        }

        private static string BuildConstantCaptionTokens(string value)
        {
            var tokens = new XElement("Tokens",
                new XElement("Token",
                    new XElement("Type", "Constant"),
                    new XElement("Data", new XCData(value ?? string.Empty))));
            return tokens.ToString(SaveOptions.DisableFormatting);
        }

        private static string ExtractConstantCaptionFromTokens(string captionExpression)
        {
            if (string.IsNullOrEmpty(captionExpression)) return string.Empty;
            try
            {
                var tokens = XElement.Parse(captionExpression);
                var data = tokens
                    .Elements("Token")
                    .Elements("Data")
                    .FirstOrDefault();
                return data?.Value ?? string.Empty;
            }
            catch
            {
                return captionExpression;
            }
        }

        private static bool IsPersistedValueMatch(string propertyName, string expected, string actual)
        {
            string normalizedExpected = expected ?? string.Empty;
            string normalizedActual = actual ?? string.Empty;

            if (string.Equals(normalizedExpected, normalizedActual, StringComparison.Ordinal))
            {
                return true;
            }

            if ((string.Equals(propertyName, "Left", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(propertyName, "Top", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(propertyName, "Width", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(propertyName, "Height", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(propertyName, "BorderWidth", StringComparison.OrdinalIgnoreCase)) &&
                int.TryParse(normalizedExpected, out int expectedInt) &&
                int.TryParse(normalizedActual, out int actualInt) &&
                expectedInt == actualInt)
            {
                return true;
            }

            // A Font reads back as Font.ToString(); compare by name/size/units (style when both state it).
            if (FontHelper.IsFontAttributeName(propertyName) && FontHelper.AreEquivalent(normalizedExpected, normalizedActual))
            {
                return true;
            }

            // The report SDK often serializes colors as nested "Color [ ... ]" descriptors or RGB tokens.
            if (ColorHelper.IsColorAttributeName(propertyName))
            {
                if (ColorHelper.IsColorEquivalent(normalizedExpected, normalizedActual))
                {
                    return true;
                }

                if (normalizedActual.IndexOf(normalizedExpected, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string ExtractColorLeafToken(string raw)
            => ColorHelper.ExtractColorLeafToken(raw);

        private sealed class FindCriteria
        {
            public string PropertyName { get; set; }
            public string Query { get; set; }
        }

        private sealed class ParseResult
        {
            public XDocument Document { get; private set; }
            public string Error { get; private set; }

            public static ParseResult FromDocument(XDocument document) => new ParseResult { Document = document };
            public static ParseResult FromError(string error) => new ParseResult { Error = error };
        }

        private sealed class LayoutContextResult
        {
            public VisualSurface Surface { get; private set; }
            public dynamic WebFormPart { get; private set; }
            public ISource SourcePart { get; private set; }
            public KBObjectPart VisualPart { get; private set; }
            public string PartName { get; private set; }
            public string MemberName { get; private set; }
            public string MemberSourcePath { get; private set; }
            public bool MemberWritable { get; private set; }
            public XDocument Document { get; private set; }
            public string Error { get; private set; }

            public static LayoutContextResult FromError(string error) => new LayoutContextResult { Error = error };

            public static LayoutContextResult FromWebForm(dynamic webFormPart, XDocument document)
            {
                return new LayoutContextResult
                {
                    Surface = VisualSurface.WebForm,
                    WebFormPart = webFormPart,
                    Document = document
                };
            }

            public static LayoutContextResult FromLayoutSource(ISource sourcePart, XDocument document)
            {
                return new LayoutContextResult
                {
                    Surface = VisualSurface.LayoutSource,
                    PartName = "Layout",
                    SourcePart = sourcePart,
                    Document = document
                };
            }

            public static LayoutContextResult FromLayoutSource(string partName, ISource sourcePart, XDocument document)
            {
                return new LayoutContextResult
                {
                    Surface = VisualSurface.LayoutSource,
                    PartName = partName,
                    SourcePart = sourcePart,
                    Document = document
                };
            }

            public static LayoutContextResult FromReport(KBObjectPart reportPart, XDocument document)
            {
                return new LayoutContextResult
                {
                    Surface = VisualSurface.Report,
                    PartName = "Layout",
                    VisualPart = reportPart,
                    Document = document
                };
            }

            public static LayoutContextResult FromPartXml(string partName, KBObjectPart part, XDocument document)
            {
                return new LayoutContextResult
                {
                    Surface = VisualSurface.PartXml,
                    PartName = partName,
                    VisualPart = part,
                    Document = document
                };
            }

            public static LayoutContextResult FromPartMemberXml(string partName, KBObjectPart part, XDocument document, string memberName, string memberSourcePath, bool memberWritable)
            {
                return new LayoutContextResult
                {
                    Surface = VisualSurface.PartMemberXml,
                    PartName = partName,
                    VisualPart = part,
                    MemberName = memberName,
                    MemberSourcePath = memberSourcePath,
                    MemberWritable = memberWritable,
                    Document = document
                };
            }
        }

        private sealed class MemberXmlCandidate
        {
            public string Xml { get; set; }
            public XDocument Document { get; set; }
            public int Score { get; set; }
            public PropertyInfo Property { get; set; }
            public MethodInfo GetterMethod { get; set; }
            public string MemberName { get; set; }
            public string SourcePath { get; set; }
            public int Depth { get; set; }
            public string MemberKind { get; set; }
            public bool MemberWritable { get; set; }
        }

        private sealed class ReferenceObjectComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceObjectComparer Instance = new ReferenceObjectComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }

        private enum VisualSurface
        {
            Any,
            Report,
            WebForm,
            LayoutSource,
            PartXml,
            PartMemberXml
        }
    }
}
