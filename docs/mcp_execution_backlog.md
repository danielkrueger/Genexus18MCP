# GeneXus MCP Execution Backlog

This backlog reflects the post-migration state of the repository.

## Completed foundation

The following phases are already complete:

- MCP-native transport at `/mcp`
- protocol-version and session-aware HTTP behavior
- MCP discovery surface for tools, resources, prompts, completion, and notifications
- Nexus-IDE migration to MCP-first runtime
- removal of the legacy `/api/command` transport

## Current backlog

The remaining work is not "migrate to MCP". The remaining work is hardening and expansion.

Operational tracking for current MCP limitations and validation status lives in `docs/mcp_limitations_tracking.md`.

### Track 1: Harden MCP contracts

Goal:
make the MCP surface stricter and easier for external clients to consume safely.

Tasks:
- increase contract coverage for tools, resources, prompts, and completion
- tighten schema validation for prompt arguments and tool payloads
- add more protocol-level regression tests for HTTP sessions and notifications

Acceptance:
- protocol regressions are caught by tests before release

### Track 2: Expand editor and exploration resources

Goal:
make resources the default read surface for rich GeneXus exploration.

Tasks:
- expand object and attribute resources where the worker already has stable data
- improve resource templates so external clients can compose URIs without hidden conventions
- enrich summaries and context resources for conversion and review flows

Acceptance:
- common read-only exploration no longer requires ad hoc tool calls

### Track 3: Conversion pipeline maturity

Goal:
stabilize the conversion-oriented surface on top of MCP.

Tasks:
- define and harden the conversion bundle contract
- evolve GeneXus IR toward deterministic translation inputs
- improve target generators and review outputs

Acceptance:
- conversion workflows are structured, testable, and predictable

## Immediate next priorities

1. Add more MCP protocol contract tests around sessions, SSE, prompts, and completion.
2. Expand resource coverage for object exploration and conversion support.
3. Stabilize the conversion-oriented MCP surface.

## UX debt — concrete follow-ups

Specific friction items identified across recent sessions that didn't fit a single release window. Listed so they don't get lost.

- **Error envelope consistency.** Tools return mixed error shapes (`{status:"Error", message}` vs `{code, hint}` vs `{status:"PartialFailure", patternValidationIssues:[…]}`). No central `ErrorBuilder` — each service rolls its own. Agents have to write per-tool recovery logic. Target shape: `{status, code, message, hint, nextStep}` uniformly. Big commitment (touches every service); biggest single UX win.
- **`genexus_preview` / `genexus_browser action=preview` hangs on GAM-authenticated targets.** Headless Chrome stalls at the GAM login form with no progress signal and no clean cancel; the hung call also serializes the worker (blocks unrelated tools like `genexus_telemetry action=friction_append`). Two things: detect the GAM redirect and fail-fast with `code:GamLoginRequired` (or accept an `auth` parm), and stop pinning the worker on a single long SDK op.
  - **Done (fail-fast half).** A preview that lands on an auth wall now returns `status: "auth_required"` plus a `code` that names the recovery: `GamLoginRequired` (GAM redirect, no credentials — pass `auth={mode:"gam",user,pass}` or set `GXMCP_GAM_USER`/`GXMCP_GAM_PASS`), `GamLoginRejected` (credentials submitted and refused — fix the account), or `AuthRequired` (a non-GAM wall — GAM credentials will not help). `authWall`, `credentialsSupplied`, `finalUrl` and a `nextStep` are included; no credential is echoed back. Previously the response was a bare `status: auth_required`, so the three recoveries were indistinguishable. Pinned by `PreviewAuthWallTests` (6 tests, mutation-checked).
  - **Remaining.** The long call still pins the worker. The per-step CLI timeout and wall-clock budget bound it, but a slow target holds the SDK thread, so unrelated tools queue behind it. Fixing this means moving the browser work off the SDK thread, which is a structural change, not a service edit.
- **`genexus_edit part=PatternInstance` returns generic "Pattern write verification failed".** No SDK trace, no field, no hint, same response on `validate=strict` and `validate=best-effort`. Schema is intricate (`childrenOrderedList` ordering codes, theme class GUIDs, `default*` attribute pairs) and undocumented in tool-help. Surface the actual SDK validation error; ship a `validate=only` path that returns diagnostics without writing; consider a higher-level "patch a variable/control INTO existing PatternInstance" tool.
  - **Done — SDK trace and `validate=only`.** Both had already landed: the verify-failed envelope carries `sdkSaveError` (type, message, where, chain), `persistedSnippet`/`requestedSnippet` and a diff, and `validate=only` (mapped to the dry-run path) returns a bounded diff without writing.
  - **Closed — the ordering schema is documented, not applied.** `PatternChildOrderReconciler` looks like unwired code (implemented, 15 passing tests, called by nothing), but that is deliberate and enforced: `PatternReconcileAttachmentTests` asserts the write path does *not* call it, and `docs/pattern-xml-property-edits.md` records why — XML document order is not a reliable description of WorkWithPlus's rendering metadata, since a valid serialized `childrenOrderedList` may contain entries with no direct XML child, so rebuilding it from document order corrupts valid lists. The raw route is a property-edit route only; changing child-order metadata, node identities or structure is rejected as `PatternMetadataChangeUnsupported` / `PatternStructureChangeUnsupported`. The tool-help now states that contract for agents. Wiring the reconciler back in would need live evidence from a real `PatternInstance` that the corruption does not occur, and none is available: the accessible KB has no `WorkWithPlus`/`Pattern`/`PatternInstance` object and applying a pattern requires a licensed WWP package.
  - **Remaining.** A higher-level "patch a variable/control INTO an existing instance" tool, and validation of theme-class references inside an instance. Both need a real pattern fixture before they can be built or verified.
