# MCP ↔ GeneXus IDE Parity Roadmap

**Goal:** Anything the GeneXus 18 IDE can do, the MCP can do — with equivalent
quality (same XML structure, same semantic correctness, same generator output
at runtime). Authored 2026-05-19 after the friction-report session that hit
GeneXus-runtime constraints the MCP can't currently model.

## Definition of "IDE parity"

For a given user intent (e.g. "add a popup with radio button options"), the MCP
must produce a KB state that is:

1. **Functionally equivalent** — runtime behavior matches IDE-authored equivalent
2. **Structurally equivalent** — XML/source matches IDE output byte-for-byte
   modulo attribute order, OR routes through SDK APIs that produce the same
   internal state regardless of XML serialization order
3. **Detectable when failing** — if the agent's request can't be expressed via
   SDK-equivalent ops, MCP returns a clear error pointing to the IDE-only path,
   never silently writes a "compiles-but-broken" state
4. **Reusable** — common patterns are first-class operations, not raw-XML recipes
   the agent has to remember

## Where we are today

### Reaches parity
- Variables: create/modify/delete, primitive + SDT + BC + Domain types
- Source parts (Events/Rules): patch and full edits
- Pattern children for WorkWithPlus (BC, transactions): SDK-aware via reconciler
- Property writes routed through SDK (`genexus_properties set`)
- Build/reorg/index via `genexus_lifecycle`
- Inspection: variables, controls, callers, layoutAttIdsInUse, layoutGotchas

### Falls short (mapped in this session)

| Gap | What MCP does | What IDE does |
|---|---|---|
| **Layout XML mutation** | Raw `XElement.SetAttributeValue` then save part | SDK `IWebTag.SetProperties` via property panel — runs PropertyValueConverter, validates against schema, may trigger pattern-aware updates |
| **gxButton event wiring** | Writes `OnClickEvent` attribute (ignored by generator in html-form) | Property panel writes through event-binding API that updates internal event table; generator emits correct `data-gx-evt` |
| **Radio/Combo in webform** | Allows authoring `ControlType="Radio Button"` in `<Form type="html">` (renders disabled) | IDE designer doesn't let you drop radio inside html-form, forces layout-form table |
| **Layout structure changes** | Edit XML element tree directly | Visual designer composes table-responsive layouts with theme-aware sizing |
| **Theme class references** | Agent must know GUID of theme class | IDE picker shows class list, validates compatibility with control type |
| **Render preview** | Agent writes blind, validates via browser after build | IDE shows live preview alongside designer |

### Knock-on impact

Today's UG popup session burned ~3 hours hitting these gaps:
- Radio buttons disabled (couldn't predict from XML, learned only via browser)
- gxButton custom OnClickEvent silently dropped (compile clean, broken at runtime)
- `var:N` → variable mapping wrong without `Variable.Id` reflection (fixed in
  commit `67b40ef`)
- gxAttribute without `id` had `name: null` in inspect (fixed in commit `51d3019`)

The `LayoutGotchaScanner` (commit `2614fdc`) raises a warning for the first two,
which short-circuits future debugging but doesn't actually let the MCP produce
the working pattern.

## Workstreams

Ordered by impact-to-effort ratio. Each delivers value standalone.

### W1 — SDK-routed layout writes (P0, ~1 week)

**Outcome:** Every layout property change goes through `WebFormTypedPropertyWriter`
(SDK `SetTagProperty`) instead of raw XML mutation. This is what unlocks
gxButton OnClickEvent and other event-wiring properties.

**Current state (2026-09-29):** steps 1-3 landed; step 4 (acceptance) open.

- `WebFormPropertyDeltaDetector` classifies whole-element add/remove/move into
  `StructuralChanges[]` instead of failing with an opaque count message.
  Pairing is id-free (the SDK regenerates element ids on materialization) and `id`
  is SDK-managed, never an authorable delta.
- `Compatibility.WebTagFactoryAdapter` probes `WebTagFactory.Create` structurally
  (GeneXus 18: static `Create(XmlNode, KBObject, IWebTag, bool)`, verified by
  reflection against the installed SDK; `IWebTag` carries no Remove/Delete, so
  removal stays on the document path).
- `Helpers.WebFormTypedCreateRouter` implements the attempt but is **off by
  default** (`ApplyEditableXml(..., attemptTypedCreate: false)`). It runs only
  when a caller opts in, and whenever it does the raw rewrite still runs
  afterwards, so the existing post-write verification stays the fail-closed gate.
  Live on GeneXus 18: `Create` binds and returns, the node is imported into
  the part document with an SDK-resolvable `id`, and the created tag is
  re-asserted after the push (`SaveProperties`) — but the SDK still refuses the
  control back (`SaveProperties=False`) and persists the structure without it.
  Persisting an added control is therefore **blocked** on GeneXus 18 headless.
  It is gated rather than deleted because the attempt cannot make a write worse
  but also cannot make one better on any measured major, so charging every
  structural WebForm write an SDK round-trip for a known-zero outcome is not
  justified; the flag keeps the experiment reproducible when a major is
  measured to persist the tag. Steps 2-3 are complete rather than pending.

**Steps:**
1. ~~Extend `WebFormPropertyDeltaDetector` to recognize add/remove of controls~~
2. `IWebTag.Create` / `IWebTag.Remove` via reflection — `Create` done (static
   factory probe, structural matcher, fail-closed on ambiguity). `Remove` has no
   SDK member (`IWebTag` exposes only Get/SetProperties/SaveProperties and
   value accessors) and stays on the document path.
3. `LayoutService.SetProperty` / `genexus_edit part=layout` through the typed
   writer when available, falling back to XML only on probe failure — done for
   property deltas and attempted for control adds (see the blocked note above)
4. Acceptance test: `gxButton OnClickEvent="'Foo'"` in html-form produces
   `data-gx-evt=<correct N>` at runtime (currently always 5/Enter)

**Risk:** SDK API may not expose Create/Remove cleanly. If it doesn't, this
workstream gets de-scoped — but the W1 layer would still help for property-only
changes.

### W2 — Pattern engine programmatic access (P0, ~1-2 weeks)

**Outcome:** MCP invokes the same WorkWithPlus / Standard pattern application
that the IDE's "Apply Pattern" menu runs. Result: when MCP creates a transaction
and applies WWP, the output is byte-for-byte identical to IDE workflow.

**Current state:** `genexus_apply_template` exists but operates on raw template
XML files. The SDK's pattern engine isn't invoked.

**Steps:**
1. Probe `Artech.Genexus.Patterns.*` namespace for the apply API (likely
   `PatternApplicator.Apply(KBObject, Instance)`)
2. Wrap it in a new tool `genexus_apply_pattern name=<obj> pattern=<patternKey>
   instance=<serialized config>`
3. Audit existing pattern operations (CRUD over `WorkWithPlus` instances) to
   ensure they re-trigger reconciliation when properties change
4. Acceptance test: `genexus_create_object type=Transaction
   pattern=WorkWithPlus.Standard` produces a KB state matching the IDE's
   "Right-click → Apply Pattern WorkWithPlus" output, verified by diff of
   generated XML

**Risk:** Pattern engine has internal state (cache, UI feedback) — running
headless may need extra adapter layer.

### W3 — Common-UI templates (P1, ~3 days)

**Outcome:** First-class operations for frequent patterns the agent currently
hand-rolls in raw XML:

- `genexus_create_popup` — popup webpanel with Form type="layout", radio /
  combo / text inputs editable, Confirmar action, optional groups with
  conditional visibility
- `genexus_add_option_picker` — drops a radio/combo bound to a variable into
  an existing layout
- `genexus_add_confirmation_dialog` — yes/no dialog popup

Each template is a tested, IDE-equivalent recipe. The agent calls one tool with
domain-level inputs (caption, options, target variable); MCP emits the proper
layout-form XML.

**Steps:**
1. Reverse-engineer 3-5 IDE-authored popups in `AcademicoHomolog1` to extract
   the canonical layout-form structure
2. Author Cookiecutter-style templates with parameter slots
3. Wire as `genexus_create_object subtype=popup template=option-picker ...`
4. Acceptance test: tool output matches IDE-generated popup byte-for-byte
   (modulo GUIDs and attribute order)

**Risk:** Templates can drift if IDE updates layout pattern. Mitigate with
contract tests against pristine IDE-authored fixtures.

### W4 — Render preview (P1, ~1 week)

**Outcome:** Before persisting a layout change, MCP optionally calls a headless
browser (via `chrome-devtools-axi` already integrated) to fetch the rendered
HTML and report visual differences vs baseline. Agent gets a "this is what it
looks like" snapshot instead of writing blind.

**Steps:**
1. New tool `genexus_preview name=<webpanel>` — builds object, opens via test
   URL with mock parms, returns screenshot + accessibility tree dump
2. Optional `dryRun=true` mode in `genexus_edit part=layout` — applies in-memory
   build, captures preview, returns diff vs baseline, doesn't persist
3. Cache preview snapshots per object so visual regressions surface immediately

**Risk:** Test harness in the academic KB needs a stable mock auth path
(currently uses `dani.aspx` with hardcoded PesCod 5171369). Generalize.

### W5 — Schema-aware validation (P2, ~3 days)

**Outcome:** Before write, MCP validates the proposed layout XML against the
SDK schema and refuses if invalid. Today, XML may persist successfully but the
generator silently strips invalid attributes or produces broken runtime — agent
finds out post-build.

**Steps:**
1. Use SDK `WebFormHelper` introspection to enumerate valid attributes per
   element + valid combinations (e.g., `gxAttribute ControlType="Radio Button"`
   only valid in `<Form type="layout">`)
2. Extend `WebFormSchemaHints` with combination rules, not just per-element
   attribute lists
3. Promote known gotchas (LayoutGotchaScanner) from warnings to errors when
   they'd produce non-functional runtime — agent gets blocked at write time

**Risk:** False positives if schema rules are too strict. Need a force-write
escape hatch.

### W6 — Theme/class introspection (P2, ~2 days)

**Outcome:** the agent can see which classes a KB defines, instead of guessing
a class name or inventing an identifier.

**Steps:**
1. **DONE** — list the KB's `ThemeClass` objects
   (`genexus_analyze mode=theme_classes`). Live on GX18/KBTeste: 129 classes,
   sorted by name, `truncated` marks a cut. The loaded index is the only
   complete enumeration available headlessly; a `KBModel.Objects.GetAll()`
   walk and a theme's style tree each yield a single theme-root object, so
   neither is a fallback. The discriminator is the index entry `Type`, not the
   `TypeIndex` bucket (whose storage-key format is internal to
   `IndexCacheService`). It was first built as
   `genexus_layout action=list_controls includeThemeClasses=true`, which mixed
   an index read into a service that answers from
   `IUserControlsManagerService`; it is now its own analyze mode.
2. **BLOCKED** — group classes by applicable control type. `ThemeTypes` exists
   on the SDK object but is not in the index, and resolving 129 objects per call
   to read one cosmetic field is not worth the cost. The response reports
   `controlTypesAvailable: false` instead of guessing.
3. **BLOCKED** — `class=<name>` shorthand. Measured on GX18/KBTeste, a layout
   `class` attribute is `<guid>-<suffix>` and matches **none** of the KB's 129
   class GUIDs, so it is an SDK style reference this Server cannot resolve from
   a name. The response carries `authorableInLayoutClassAttribute: false` and
   authoring `class` stays on the layout-document path. A wrong class value
   silently loses styling, so this stays blocked until the style-reference
   mapping is measured, not guessed.

## Milestones

- **M1 (target: 2 weeks):** W1 done. The UG popup migrates cleanly via MCP.
  Acceptance: `RegProfAlunoUGPopup` radio is editable in browser after
  `genexus_migrate_popup_to_layout` (built on W1+W3).
- **M2 (target: 1 month):** W2 + W3 done. New popups/transactions can be
  generated via templates that match IDE output. The friction report
  2026-05-19 gaps #1 and #2 are downgraded from "warning" to "not reproducible
  via MCP" because MCP simply doesn't let you author the broken pattern.
- **M3 (target: 6 weeks):** W4 + W5 + W6 done. MCP has render preview,
  schema enforcement, theme introspection. Agent operates with IDE-equivalent
  feedback loop.

## Acceptance test — full parity

A blind test: a developer pairs with the agent on an arbitrary GeneXus task
that an experienced human would do via IDE in ~30min. The agent completes it
via MCP-only in comparable time, and the resulting KB state, opened in the
IDE, looks indistinguishable from what the human would have produced.

This is the bar. We're not there yet, but the roadmap closes the visible gaps.

## What's deliberately NOT in scope

- Replacing the IDE for visual design — humans still use IDE designer when they
  need to draft UX from scratch. MCP is for automation, not creative design.
- Reverse-engineering closed SDK internals beyond what reflection allows.
- Cross-version compatibility — target GeneXus 18.0.7.179127 (current).
- Real-time collaboration — single-session per KB-open.

## Open questions

- **SDK API stability:** Pattern engine APIs may not be public. If they aren't,
  W2 falls back to template approach (W3) only. Need to probe early.
- **Visual fidelity vs functional fidelity:** Should W3 templates produce
  pixel-perfect IDE-equivalent layouts, or functionally-equivalent with
  acceptable visual differences? Lean toward functional + iterate.
- **Telemetry:** Do we add metrics to track which tool calls hit raw-XML paths
  vs SDK paths, so we can measure parity progress over time?
