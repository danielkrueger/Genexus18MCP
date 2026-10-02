# Plan 124: Fix the apply_pattern schema prose that promises a refusal route

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise.
>
> **Drift check (run first)**: `git diff --stat 0f0d71a8..HEAD -- src/GxMcp.Gateway/tool_definitions.json`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none (independent of plan 123, which does not touch the schema)
- **Category**: bug
- **Planned at**: commit `0f0d71a8`, 2026-10-01

## Why this matters

`genexus_apply_pattern`'s schema says the `pattern` argument is "Required for
apply/diagnose; omit with **reapply=true** to infer from the existing
instance". The `reapply` property does exist and is wired — but the `mode`
enum is `apply | diagnose | actions` and the reapply route is **refused
deliberately**, with a measured reason. So the schema advertises a combination
that produces a structured refusal rather than an inference, and a caller
following the schema spends a turn discovering that.

This is the same defect class the project fixed two days ago in a sibling
surface: `CHANGELOG.md:34` records that the `edit_pattern_instance` recipe and
the `genexus_edit` help "no longer promise capabilities the write path does not
have… Both surfaces now show a supported property change". `genexus_apply_pattern`
is the same class, still present, and it is the *published schema* rather than
a recipe.

The refusal itself must not change. It is deliberate, measured on two majors,
and the CHANGELOG says so at length. Only the promise is wrong.

## Current state

`src/GxMcp.Gateway/tool_definitions.json` — `genexus_apply_pattern` is at
line 30. Its `inputSchema.properties`:

- `pattern` — `{"type": "string", "description": "Installed pattern name or
  GUID. Required for apply/diagnose; omit with reapply=true to infer from the
  existing instance."}` — **this is the wrong sentence.**
- `reapply` — `{"type": "boolean", "default": false, "description": "Re-run on
  existing instance."}` — exists, and is honest but does not say the route is
  refused.
- `mode` — enum `apply`, `diagnose`, `actions`.
- Required: `name` only.

The Worker side, which is the truth the schema must describe:

- `src/GxMcp.Worker/Services/CommandDispatcher.cs:2469-2473`:
  `bool reapply = args?["reapply"]?.ToObject<bool?>() ?? false;` then
  `if (reapply) return _patternApplyService.ReapplyPattern(target, patKey, patSettings);`
  else `ApplyPattern(...)`. So the parameter is accepted and dispatched.
- `src/GxMcp.Worker/Services/PatternApplyService.cs:256` —
  `public string ReapplyPattern(string objectName, string patternKey, JObject
  settings = null)`. It performs real checks before refusing (the K2B designer
  rejection at the top, pattern resolution, object resolution).
- `CHANGELOG.md:18` (the `## Unreleased` → `### Fixed` entry for issue #353)
  states the reapply route is refused **deliberately**, re-measured on a second
  major, that the `NullReferenceException` was this build's own null
  `ApplySettings`, that `PatternEngineApplyFailed` now reports
  `cause: nullApplySettings`, and that supplying settings removes the throw and
  returns `PatternApplied` **while regenerating nothing** — hence the refusal
  stands. It also records `GenericReapplySupported` stays `false` and that
  `derivedObjectRegeneration` replaced the falsified
  `generatedObjectsRegenerated` field.
- `CHANGELOG.md:34` — the sibling fix for `edit_pattern_instance`, which is the
  precedent and tone to match.

Schema budget and generated artifacts that depend on this file:

- `src/GxMcp.Gateway.Tests/ToolSchemaSizeTests.cs` — total `~34500` tokens,
  per-profile byte budgets, 8000 bytes per tool. Shortening a description can
  only help; lengthening must stay under budget.
- `src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json`
  — the discovery golden fixture, alphabetically sorted. It must be regenerated
  after an intentional schema change, per `AGENTS.md`:

  ```powershell
  $env:GXMCP_UPDATE_GOLDEN='1'; dotnet test src\GxMcp.Gateway.Tests --filter McpDiscoveryContractTests; Remove-Item Env:\GXMCP_UPDATE_GOLDEN
  ```

- `docs/mcp_capabilities_inventory.md` — the human-readable table, validated by
  `python scripts/validate-tool-contracts.py`.
- `docs/operation-contract-inventory.json` — generated; checked with
  `python scripts/generate-operation-contract-inventory.py --check`.
- `docs/agent_playbook.md` — the SDK authoring reference; check whether it
  repeats the same promise about `reapply` before deciding it is in scope.

## Commands you will need

| Purpose | Command | Expected on success |
|-----------|---------|---------------------|
| Inspect the tool | `$j = Get-Content src\GxMcp.Gateway\tool_definitions.json -Raw \| ConvertFrom-Json; ($j \| Where-Object { $_.name -eq 'genexus_apply_pattern' }) \| ConvertTo-Json -Depth 8` | shows the current shape |
| Golden refresh | see `AGENTS.md` command above | fixture rewritten, test green |
| Contract validation | `python scripts/validate-tool-contracts.py` | exit 0 |
| Inventory check | `python scripts/generate-operation-contract-inventory.py --check` | exit 0, no drift |
| Focused tests | `dotnet test src\GxMcp.Gateway.Tests --filter "FullyQualifiedName~ToolSchemaSize\|FullyQualifiedName~McpDiscoveryContract\|FullyQualifiedName~ToolActionContract" --logger "console;verbosity=minimal"` | all pass |
| Full Gateway suite | `dotnet test src\GxMcp.Gateway.Tests --logger "console;verbosity=minimal"` | all pass (880+ before) |

`MSB3027`/`MSB3021` naming the Gateway/Worker exe → documented scoped
permission: `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`, retry.
Force `-t:Rebuild` after a compile failure. Do **not** run `.\build.ps1`.

## Scope

**In scope**:

- `src/GxMcp.Gateway/tool_definitions.json`
- `src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json`
  (regenerated only)
- `docs/mcp_capabilities_inventory.md` (only if the current text repeats the
  same false promise)
- `src/GxMcp.Gateway.Tests/ApplyPatternSchemaPromiseTests.cs` (create)

**Out of scope** (do NOT touch**):

- `src/GxMcp.Worker/**` — including `PatternApplyService.ReapplyPattern`. The
  refusal is the measured, intended behaviour. Do not make reapply work, do not
  change `GenericReapplySupported`, do not change the rejection code or hint.
- `docs/operation-contract-inventory.json` — generated; never hand-edited.
- `src/GxMcp.Gateway/ToolHelpCatalog.cs` unless the reapply recipe there makes
  the same false promise; if it does, report it rather than expanding scope
  without saying so. If you do change it, say so explicitly in NOTES.
- The schema budget constants in `ToolSchemaSizeTests.cs`. This change shrinks
  text; it must not need a budget bump, and adding one is a STOP condition.
- Any other tool's description.

## Git workflow

- Branch `advisor/124-apply-pattern-schema`, commit in your worktree.
- Prose commit subject, e.g.
  `"Stop the apply_pattern schema promising a reapply inference the route refuses"`.
- `CHANGELOG.md` under `## Unreleased` → `### Fixed`, same commit, under the
  existing #353 entry or a new bullet — the fix continues that work, so follow
  whichever the file's structure calls for and reference
  `https://github.com/lennix1337/Genexus18MCP/issues/353` per `AGENTS.md`.
- Do NOT push, open a PR, or merge.

## Steps

### Step 1: Correct the `pattern` description

Rewrite `pattern`'s description so it no longer instructs the caller to omit it
with `reapply=true`. It should say what `pattern` is for on the supported
routes, and — if it mentions reapply at all — say that the reapply route is not
supported and names the reason at the level the schema can carry.

The project has an established way to express a refusal in prose. Find it
(`CommandDispatcher.UnsupportedAction` around `:370-407` produces a
`nextSteps`-bearing refusal, and `PatternApplyService` produces the
reapply-specific reason) and mirror its wording so the schema, the help and
the runtime envelope agree. Read `docs/agent_playbook.md` for how refusals are
supposed to be described to an agent.

### Step 2: Make the `reapply` description honest

`reapply`'s current description — "Re-run on existing instance." — does not
say the route is refused. Update it to say so plainly, and to point at what a
caller should do instead. This is the property an agent will look at when it
decides whether to try the route, so the refusal belongs here specifically.

Keep both descriptions within the existing byte budget. The `all` profile is
already near its ceiling per `CHANGELOG.md:14`, and a budget bump needs a
`CHANGELOG` explanation per `AGENTS.md` — so prefer **shorter and truer** over
longer. If your replacement text is longer than what it replaces, say why and
confirm `ToolSchemaSizeTests` still passes.

### Step 3: Check the other two surfaces for the same promise

- `docs/mcp_capabilities_inventory.md` — does it describe reapply as a
  supported inference? If yes, correct it in place.
- `docs/agent_playbook.md` and `src/GxMcp.Gateway/ToolHelpCatalog.cs` — do
  they repeat it? The playbook is out of scope; **report** a match rather than
  editing it. The help catalog is conditionally in scope; if you change it,
  say so in NOTES.

### Step 4: Regenerate the discovery golden fixture

Run the `GXMCP_UPDATE_GOLDEN` command from the command table, then re-run
without the env var to confirm it is green. Inspect the fixture diff: it must
contain only the description strings you changed, and the file must remain
alphabetically sorted (`AGENTS.md`).

**Verify**: `git diff` on the fixture shows only description-string changes.

### Step 5: Write the regression test

Create `src/GxMcp.Gateway.Tests/ApplyPatternSchemaPromiseTests.cs`. The defect
is "the schema promises something the route does not do", so the guard must
compare the schema against the route.

1. `TheSchemaDoesNotInstructTheCallerToOmitPatternForReapply` — read
   `tool_definitions.json` in the test (the pattern
   `ToolSchemaSizeTests.cs:37` already uses to locate it), find
   `genexus_apply_pattern`, and assert the `pattern` description does not tell
   the caller to omit it when `reapply` is true. Assert on the actual property
   description, not on a substring of the whole file, so a legitimate mention
   elsewhere does not fail the test.
2. `TheReapplyDescriptionStatesTheRouteIsRefused` — assert `reapply`'s own
   description carries the refusal. Decide the assertion by what the text
   actually says: prefer asserting that the description and the runtime
   rejection code agree, over asserting a specific English phrase. Read
   `PatternApplyService`'s rejection code and tie the test to that.
3. `TheSchemaStillAdvertisesReapplyAsAParameter` — a guard against
   over-correcting: the parameter exists and is dispatched
   (`CommandDispatcher.cs:2469-2472`), so removing it from the schema would be
   a *different* wrong answer. This test pins that it stays.

### Step 6: Mutation-check every new test

Restore the original `pattern` description and confirm test 1 fails. Restore
the original `reapply` description and confirm test 2 fails. Delete the
`reapply` property from the schema and confirm test 3 fails. Record all three.

**Verify**: with the original `pattern` description restored, test 1 FAILS.

### Step 7: Validate

```powershell
dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal
dotnet test src\GxMcp.Gateway.Tests --logger "console;verbosity=minimal"
python scripts/validate-tool-contracts.py
python scripts/generate-operation-contract-inventory.py --check
dotnet build Genexus18MCP.sln -v:minimal
```

**Verify**: all exit 0. Report the schema-size numbers before and after, and
confirm the budget constants were not changed.

## Test plan

- New file: `src/GxMcp.Gateway.Tests/ApplyPatternSchemaPromiseTests.cs`
  - 3 cases as above.
- Modified (regenerated only): `src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json`
- Structural pattern: `src/GxMcp.Gateway.Tests/ToolSchemaSizeTests.cs`
  (locating and asserting on the schema JSON in a test).
- Verification: focused filter → all pass; full Gateway suite → no new failures;
  both Python contract gates → exit 0.

## Done criteria

- [ ] The `pattern` description no longer instructs the caller to omit it with `reapply=true`.
- [ ] The `reapply` description states that the route is refused and what to do instead.
- [ ] The `reapply` property is still present (it is dispatched at `CommandDispatcher.cs:2469`).
- [ ] The Worker, `PatternApplyService`, the rejection code and `GenericReapplySupported` are unchanged.
- [ ] The discovery golden fixture is regenerated and its diff contains only the intended description changes.
- [ ] `ToolSchemaSizeTests` budget constants are unchanged and still pass.
- [ ] `docs/mcp_capabilities_inventory.md` is corrected if it repeated the promise; `docs/agent_playbook.md` is untouched and any match reported.
- [ ] 3 tests exist and pass; all three mutations make them red.
- [ ] `python scripts/validate-tool-contracts.py` and `python scripts/generate-operation-contract-inventory.py --check` both exit 0.
- [ ] `dotnet test src\GxMcp.Gateway.Tests` exits 0, no new failures.
- [ ] `CHANGELOG.md` has an entry under `## Unreleased` referencing issue #353.
- [ ] `git status --short` shows nothing outside the in-scope list.

## STOP conditions

Stop and report back if:

- Making the descriptions honest requires a budget bump in
  `ToolSchemaSizeTests.cs`. Try harder to be shorter first; if it is genuinely
  impossible, report the size delta and the budget headroom rather than bumping
  the constant.
- `docs/agent_playbook.md` repeats the false promise. It is out of scope —
  report the exact text and location, and leave it.
- `ToolHelpCatalog.cs` repeats the false promise and you judge that fixing only
  the schema leaves a worse inconsistency. Report your reasoning; make the
  minimal change only if it is in the same class of surface, and say so.
- The inventory check reports drift caused by your change. That would mean
  `Describe()` or the inventory generator depends on the description text.
  Report it — do not regenerate the inventory to make it pass.
- The fix appears to require modifying a file listed as out of scope.

## Maintenance notes

- Interacts with: issue #353's open work. The refusal is measured and
  deliberate; only the promise was wrong. If someone later makes reapply work,
  the schema text must be revisited in the other direction.
- A reviewer should scrutinise the wording against the runtime envelope. The
  schema, the tool help and `PatternApplyService`'s rejection must say the same
  thing about why, in the same terms. Three surfaces disagreeing is the defect
  this plan removes; two surfaces agreeing while a third differs is not a fix.
- Test 3 exists to stop an over-correction. A schema that hides a parameter the
  server still dispatches is wrong in a new way.
