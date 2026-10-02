# Plan 119: Make the KB-selection policy one implementation instead of two

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise.
>
> **Drift check (run first)**: `git diff --stat 0f0d71a8..HEAD -- src/GxMcp.Gateway`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1
- **Effort**: S
- **Risk**: MED
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `0f0d71a8`, 2026-10-01

## Why this matters

`genexus_whoami` is the documented first call: an agent runs it to learn which
KB is selected and whether it must pass one. Its answer is produced by a
second, independent copy of the resolution policy, and the two copies have
already diverged in two ways that make whoami lie about what the next call
will do:

- The whoami `available` map is built declared → open → known, so a `known`
  entry **overwrites** an `open` one. `KbResolver` prefers `open`. An alias
  that is both known and open can therefore be reported with the wrong handle.
- In legacy mode with a configured default that cannot be resolved,
  `KbResolver` throws `KB_NOT_FOUND`. whoami falls through to
  `single-open`/`declared-first` and reports `selectionState = "valid"`.

So the probe says "valid" and the next call fails — or resolves to a different
KB. The fields `selectionSource` / `selectionState` are a published
auditability contract with tests pinning them, so this plan must unify the
decision without changing the observable vocabulary.

## Current state

- `src/GxMcp.Gateway/KbResolver.cs:63-175` — the authoritative tree. Order:
  explicit arg → session selection → (strict: single-open, with a
  `DefaultConflict` check; `KB_AMBIGUOUS` / `KB_CONTEXT_REQUIRED`) →
  (legacy: `config-default` → `single-open` → `declared-first`).
  Legacy default resolution at `:115-150` tries `open` (`:117`), then
  `single-open` (`:125`), then declared (`:131`), then known (`:139`), and
  finally `throw new KbResolutionException("KB_NOT_FOUND", ...)` at `:148`.
- `src/GxMcp.Gateway/Program.Whoami.cs:1516-1583` — the duplicate.
  - `:1476-1494` builds `available`: `cfg.Environment.KBs` (declared) at
    `:1477-1484`, then `openKbs` at `:1485-1489`, then `knownKbs` at
    `:1490-1494`. Last write wins, so `known` overwrites `open`.
  - `:1516-1545` strict branch: `openKbs.Count == 1` with a
    `startupDefault` conflict check (`:1521`), else `absent`.
  - `:1547-1583` legacy branch: `available.TryGetValue(startupDefault)` at
    `:1549` (this is the map with the ordering bug), then `single-open` at
    `:1557`, then `declared-first` at `:1566`, else `absent` at `:1575`.
    There is no `KB_NOT_FOUND` equivalent — it falls through to
    `single-open`/`declared-first` and reports `valid`.
  - `:1586` `catch { }` swallows everything, which is why a divergence never
    surfaces.
- The response fields are assembled at `:1597+` (`kb.sessionSelection`,
  `kb.active`, `kb.selected`, plus `selectionSource` / `selectionState` /
  `contextRequired`). These are the published contract.
- Tests: `src/GxMcp.Gateway.Tests/` — find the KbResolver and whoami selection
  tests. `plans/096-kb-selection-route-tests.md` is the end-to-end route-test
  plan for this area and is marked DONE; read it for the vocabulary it
  established before changing anything.

Repo conventions:

- Structured error codes are frozen: `KB_NOT_FOUND`, `KB_AMBIGUOUS`,
  `KB_CONTEXT_REQUIRED`, `DefaultConflict`.
- `selectionSource` values are frozen: `session-select`, `single-open`,
  `config-default`, `declared-first`, `explicit-arg`, `none`.
- `selectionState` values are frozen: `valid`, `absent`, `invalid`,
  `conflicting`.
- Every regression guard gets a mutation check (`AGENTS.md`).

## Commands you will need

| Purpose | Command | Expected on success |
|-----------|---------|---------------------|
| Build Gateway | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | exit 0 |
| Focused tests | `dotnet test src\GxMcp.Gateway.Tests --filter "FullyQualifiedName~KbResolver\|FullyQualifiedName~Whoami\|FullyQualifiedName~KbSelection" --logger "console;verbosity=minimal"` | all pass |
| Full Gateway suite | `dotnet test src\GxMcp.Gateway.Tests --logger "console;verbosity=minimal"` | all pass (880+ before) |
| Solution build | `dotnet build Genexus18MCP.sln -v:minimal` | exit 0 |

`MSB3027`/`MSB3021` naming the Gateway/Worker exe → documented scoped
permission: `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`, retry.
Force `-t:Rebuild` after a compile failure. Do **not** run `.\build.ps1`.

## Scope

**In scope**:

- `src/GxMcp.Gateway/KbResolver.cs`
- `src/GxMcp.Gateway/Program.Whoami.cs`
- `src/GxMcp.Gateway.Tests/KbSelectionAgreementTests.cs` (create)

**Out of scope** (do NOT touch):

- `SessionKbContextStore.cs`, `Program.KbContext.cs` — session selection
  storage is correct; only the *reading* of it is duplicated.
- The published vocabulary listed above. Do not add, rename or remove a
  `selectionSource`, `selectionState` or error code.
- The strict-mode `DefaultConflict` behaviour — it is correct and tested.
- `MultiKbDiscovery.cs`, `HttpSessionRegistry.cs`, any router.

## Git workflow

- Branch `advisor/119-kb-selection-one-impl`, commit in your worktree.
- Prose commit subject, e.g.
  `"Resolve the KB once, and make whoami report the answer the next call gives"`.
- `CHANGELOG.md` under `## Unreleased` → `### Fixed`, same commit. State both
  divergences that were closed.
- Do NOT push, open a PR, or merge.

## Steps

### Step 1: Give `KbResolver` a non-throwing "describe" entry point

`KbResolver` currently returns a `KbHandle` or throws. whoami needs the same
decision expressed as data — handle, `selectionSource`, `selectionState`,
`contextRequired`, and the error code it *would* have thrown.

Add a method that returns a result object carrying all of those, with **no**
throwing path. Then re-express the existing throwing `Resolve` in terms of it:
`Resolve` throws using the code and message the result object carries. That way
there is exactly one decision tree, and the two callers cannot disagree by
construction.

The result type belongs in `KbResolver.cs` (or a new file in the same folder if
that keeps the file readable — prefer the same file unless it grows past
readability).

### Step 2: Fix the two known divergences in the shared tree

- The legacy default resolution in `KbResolver` already tries `open` before
  `known` at `:117`/`:139`. When whoami projects from the new result, the
  ordering bug disappears because the map is no longer the source of truth.
  Delete whoami's `available` construction (`:1476-1494`) — or reduce it to
  the informational listing it also feeds, if it serves a second purpose. Read
  the surrounding code before deleting; if `available` is used for something
  else, keep it for that and stop using it for selection.
- The legacy `KB_NOT_FOUND` case must now surface in whoami as **not valid**,
  not as a silent fall-through to `single-open`/`declared-first`. whoami
  already has an `absent` state for "nothing selected" — decide, from the
  existing vocabulary, whether `KB_NOT_FOUND` maps to `absent` with
  `contextRequired = true`, and whether the resolved error code should be
  surfaced in the payload. Whatever you choose, add a comment saying whoami
  now reports what the next call will do, and that this is why the fall-through
  was removed.

### Step 3: Make `BuildWhoamiPayload` project from the result

Replace the inline decision tree at `:1516-1583` with a call to the new
resolver method. Keep the whoami-specific formatting local (the `kb` object
shape at `:1597+`, the `kbPath` scaffold fallback at `:1589-1590`, the
`kbExists`/`kbValid` computation) — only the *decision* comes from the
resolver.

Do not remove the `catch { }` at `:1586` without replacing it: it exists so a
diagnostic endpoint never throws. Narrow it to catch only what it must and add
a comment that the decision itself can no longer throw.

### Step 4: Write the agreement tests

Create `src/GxMcp.Gateway.Tests/KbSelectionAgreementTests.cs`. The class of
defect is "two implementations disagree", so the test must compare them, not
just assert each one's output.

1. `WhoamiAndResolveAgreeOnEveryReachableState` — a table-driven test over
   the reachable states: explicit arg; session selection valid / invalid;
   strict with 0/1/2 open; strict single-open conflicting with
   `startupDefault`; legacy with a resolvable default; **legacy with an
   unresolvable default** (the case that diverged); legacy with 1 open;
   legacy with declared entries. For each, assert the whoami projection and
   the resolver result carry the same `selectionSource` and agree on
   validity. This is the regression guard: reintroduce the duplicated tree and
   it fails.
2. `AKnownAliasDoesNotShadowAnOpenOne` — an alias present in both `known` and
   `open`, with different handles. Assert the resolution prefers `open` in
   **both** callers. This kills the map-ordering bug specifically.
3. `AnUnresolvableLegacyDefaultIsReportedRatherThanFallingThrough` — assert
   whoami does not report `valid` when the next call would raise
   `KB_NOT_FOUND`.
4. `ThePublishedVocabularyIsUnchanged` — a source-shape guard over
   `KbResolver.cs` and `Program.Whoami.cs` asserting the exact
   `selectionSource` / `selectionState` string literals still present, via
   `SourceAssert.Count` from `src/TestSupport/`. This stops a refactor from
   quietly renaming a published value.

How to drive both callers in one test: read the existing whoami and resolver
tests for the construction seams they use (the resolver takes config + open +
known collections; whoami reads from the pool and config). If they cannot be
driven together without a heavy harness, assert against the resolver result
object directly and use the source-shape guard to prove whoami projects from
it — say which you did in NOTES.

### Step 5: Mutation-check every new test

Reintroduce the `known`-after-`open` map ordering and confirm test 2 fails.
Restore the legacy fall-through and confirm test 3 fails. Confirm test 1 fails
if either is reintroduced. Record all.

**Verify**: with the ordering bug restored, test 2 FAILS.

### Step 6: Full validation

```powershell
dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal
dotnet test src\GxMcp.Gateway.Tests --logger "console;verbosity=minimal"
dotnet build Genexus18MCP.sln -v:minimal
```

**Verify**: all exit 0, no new failures. In particular, the existing KB-selection
route tests from plan 096 must still pass — they pin the published contract.

## Test plan

- New file: `src/GxMcp.Gateway.Tests/KbSelectionAgreementTests.cs`
  - 4 cases as above.
- Structural pattern: the existing KbResolver and whoami selection tests;
  `ArgvQuotingTests.cs` for the source-shape idiom.
- Verification: focused filter → all pass; full suite → no new failures.

## Done criteria

- [ ] One decision tree exists; `KbResolver` has a non-throwing describe entry point and its throwing path is expressed in terms of it.
- [ ] `BuildWhoamiPayload` contains no inline resolution branches.
- [ ] A `known` alias no longer shadows an `open` one in any caller.
- [ ] An unresolvable legacy default is reported, not silently replaced by `single-open`/`declared-first`.
- [ ] `KB_NOT_FOUND`, `KB_AMBIGUOUS`, `KB_CONTEXT_REQUIRED`, `DefaultConflict` are unchanged.
- [ ] Every `selectionSource` and `selectionState` value is unchanged.
- [ ] 4 tests exist and pass; all mutations make them red.
- [ ] `SessionKbContextStore.cs` and `Program.KbContext.cs` are untouched.
- [ ] `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` exits 0.
- [ ] `dotnet test src\GxMcp.Gateway.Tests` exits 0, no new failures.
- [ ] `dotnet build Genexus18MCP.sln -v:minimal` exits 0.
- [ ] `CHANGELOG.md` has an entry under `## Unreleased` → `### Fixed`.
- [ ] `git status --short` shows nothing outside the in-scope list.

## STOP conditions

Stop and report back if:

- The published `selectionState` vocabulary has no value that can honestly
  express "the configured default does not resolve" without adding a new one.
  Report the options; adding a vocabulary value is a contract change and is not
  this plan's call.
- The existing whoami tests assert a specific `selectionState` for the
  unresolvable-legacy-default case, meaning the current (wrong) answer is
  pinned by a test. Report the test and its assertion — it must be updated
  deliberately, not silently.
- `available` at `Program.Whoami.cs:1476` turns out to serve a second purpose
  beyond selection. Report what; do not delete it blindly.
- The fix appears to require modifying a file listed as out of scope.

## Maintenance notes

- Interacts with: `plans/096-kb-selection-route-tests.md` (DONE) — those tests
  pin the contract this plan must preserve. Re-read them before changing the
  vocabulary, and if you do change one, treat it as a contract change requiring
  a `CHANGELOG` note under `### Changed` as well.
- A reviewer should scrutinise Step 2's two divergences specifically; they are
  the reason the plan exists, and a unification that preserves either one has
  not fixed anything.
- The `catch { }` at `:1586` is load-bearing for a diagnostic endpoint. Narrow
  it, do not delete it.
