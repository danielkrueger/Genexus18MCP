# Plan 121: Make the documented validation lane list match what CI runs

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise.
>
> **Drift check (run first)**: `git diff --stat 0f0d71a8..HEAD -- AGENTS.md CODING_STANDARDS.md .github/workflows`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P3
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: dx
- **Planned at**: commit `0f0d71a8`, 2026-10-01

## Why this matters

`AGENTS.md` and `CODING_STANDARDS.md` both contain a copy-pasteable block of
validation lanes, described as the authoritative set. Two things are wrong
with it:

1. The block **starts** with `.\build.ps1`, and `AGENTS.md:149` — nine lines
   later — says to run `.\build.ps1` **last**. `CODING_STANDARDS.md:65-69`
   explains exactly why the order matters: running it before `dotnet test`
   rebuilds the Worker outside the x86 platform the solution maps it to, which
   breaks the publish↔source byte identity the release fingerprint depends on,
   so `test-release-preflight.ps1` fails for a reason unrelated to the change.
   Anyone copying the block trips the failure the file warns about.
2. Neither block contains the Nexus-IDE lane (`cd src/nexus-ide; npm ci && npm
   run compile && npm run lint && npm test`, run by CI at
   `.github/workflows/ci.yml:109-123`), even though the built `.vsix` is a
   **required release asset** byte-compared against the copy inside
   `publish.zip` (`.github/workflows/release.yml:92-113` and `:245-248`).
   Neither block contains
   `python scripts/generate-operation-contract-inventory.py --check`, which
   `AGENTS.md:91` mentions only conditionally.

The documented set should equal the CI set. An agent executing the documented
lanes should be able to trust that a green run means what CI's green means.

## Current state

- `AGENTS.md:129-141` — the lane block. Its first line is `.\build.ps1`.
- `AGENTS.md:149` — "Run `.\build.ps1` **last**."
- `AGENTS.md:91` — the tool-change section mentions
  `python scripts/generate-operation-contract-inventory.py --check`
  conditionally, outside the lane block.
- `CODING_STANDARDS.md:55-63` — the second copy of the lane block, same
  problem. `CODING_STANDARDS.md:65-69` — the paragraph explaining the ordering
  constraint.
- `.github/workflows/ci.yml` — the authoritative lane set:
  - `:39-40` `npm test`
  - `:42-43` `npm run lint`
  - `:45-51` release metadata sync verification
  - `:52-93` "Validate quality gate manifests and scripts": 10 PowerShell
    scripts parsed for syntax, then `./scripts/check-build-warning-baseline.ps1 -ValidateOnly`,
    `./scripts/tests/test-upstream-drift.ps1`,
    `./scripts/tests/test-live-matrix.test.ps1`,
    `./scripts/tests/test-integration-preflight.ps1`,
    `./scripts/tests/sdk-validation-contract.tests.ps1`,
    `./scripts/tests/install-contract.tests.ps1`, three Python `ast.parse`
    validations, `python scripts/validate-v3-evaluation.py` (twice),
    `python scripts/validate-v3-plan.py`, `python scripts/validate-tool-contracts.py`
  - `:95-108` benchmark gate regression tests
  - `:109-123` Nexus IDE: `npm ci`, `npm run compile`, `npm run lint`, `npm test`
  - `:125-130` Gateway and Worker coverage
  - `:138-140` `./scripts/mcp_llm_contract_smoke.ps1`
  - `:146-150` MCP smoke script contract test
  - `:151-163` SDK validation lane status
- `AGENTS.md` also documents `pwsh -NoProfile -File scripts\tests\run-release-script-tests.ps1`
  and `python -m unittest discover -s scripts\tests`, which CI does not run in
  that form.

## Commands you will need

| Purpose | Command | Expected on success |
|-----------|---------|---------------------|
| Read CI lanes | `Select-String -Path .github\workflows\ci.yml -Pattern 'run:' -Context 0,3` | the full list |
| Nexus lane | `cd src\nexus-ide; npm ci; npm run compile; npm run lint; npm test` | all exit 0 |
| Contract inventory | `python scripts/generate-operation-contract-inventory.py --check` | exit 0, no drift reported |
| Tool contracts | `python scripts/validate-tool-contracts.py` | exit 0 |
| CLI suite | `npm test` | exit 0 |

`MSB3027`/`MSB3021` naming the Gateway/Worker exe → documented scoped
permission: `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`, retry.
Force `-t:Rebuild` after a compile failure.

## Scope

**In scope**:

- `AGENTS.md`
- `CODING_STANDARDS.md`
- `src/GxMcp.Gateway.Tests/DocumentationLaneListTests.cs` (create)

**Out of scope** (do NOT touch):

- `.github/workflows/**` — CI is the reference here, not the thing being
  changed. If CI is wrong, report it; do not edit the workflow.
- `CONTRIBUTING.md`, `README.md`, `TROUBLESHOOTING.md`, `docs/**`.
- The `.\build.ps1` script itself and the release-preflight fingerprint logic.
- Any production code.

## Git workflow

- Branch `advisor/121-lane-list`, commit in your worktree.
- Prose commit subject, e.g.
  `"Make the documented lane list agree with CI, and stop recommending the order that breaks the fingerprint"`.
- `CHANGELOG.md` under `## Unreleased` → `### Internal`.
- Do NOT push, open a PR, or merge.

## Steps

### Step 1: Fix the ordering in both lane blocks

In `AGENTS.md:129-141` and `CODING_STANDARDS.md:55-63`, move `.\build.ps1`
to the **end** of the block, keeping the existing `# last` comment style the
file already uses elsewhere. Keep the rest of the ordering as documented — the
`.NET` lanes come before it, which is the point.

### Step 2: Add the missing lanes to both blocks

Append, in both files:

- The Nexus-IDE lane, with a one-line note that the `.vsix` is a required
  release asset so this lane is not optional. Use `pwsh`-compatible or
  platform-appropriate form matching how the file writes other multi-step
  blocks.
- `python scripts/generate-operation-contract-inventory.py --check`.

Then reconcile the rest of each block against the CI list from Step "Read CI
lanes" and add anything CI runs that the block omits. Do **not** remove
anything the block documents that CI does not run — `AGENTS.md` deliberately
documents a broader set for an operator than CI enforces. If the two differ,
keep the union and add one line saying the block is the operator's full set
and CI is a subset. That is the honest framing and it removes the ambiguity
without pretending CI and the docs are identical.

### Step 3: Make the ordering constraint machine-checkable

The whole point is that a copied block should not be able to reintroduce the
wrong order. Add `src/GxMcp.Gateway.Tests/DocumentationLaneListTests.cs` with
a test that reads `AGENTS.md` and `CODING_STANDARDS.md` and asserts, for each:

- `.\build.ps1` appears in the lane block **after** the last `dotnet test` /
  `dotnet build` line in that block, i.e. it is the final entry.
- The block contains `generate-operation-contract-inventory.py` and a
  `nexus-ide` reference.

Read the file structure first and scope the search to the fenced code block
that contains the lanes, so a prose mention of `build.ps1` elsewhere in the
document (there are several: `AGENTS.md:266`, `:276`, `:277`, `:288`, `:291`)
does not confuse the assertion. That scoping is the part that will be easy to
get wrong — make the test find the block, not the first occurrence.

Model the style on `src/GxMcp.Worker.Tests/StaSchedulerTests.cs:216-227`,
which already reads a documentation file from a test.

### Step 4: Verify the new lanes actually pass

Run the two lanes you added, unmodified, before documenting them. If
`generate-operation-contract-inventory.py --check` currently reports drift, that
is a **pre-existing condition, not a doc bug** — do not "fix" the inventory as
part of this plan. Report the drift and leave the drift visible; the point of
adding the lane is that a future reader learns about it.

**Verify**: `python scripts/generate-operation-contract-inventory.py --check` and the Nexus-IDE lane both run; report their actual output, including any drift, in NOTES.

### Step 5: Mutation-check the new test

Move `.\build.ps1` back to the top of the `AGENTS.md` block and confirm the
test goes red. Restore it.

**Verify**: with the wrong order restored, the test FAILS.

### Step 6: Validate

```powershell
npm test
cd src\nexus-ide; npm run compile; npm run lint; npm test
```

**Verify**: all exit 0.

## Test plan

- New file: `src/GxMcp.Gateway.Tests/DocumentationLaneListTests.cs`
  - 1 test: `build.ps1` is the last entry in the lane block of each file.
  - 1 test: each block mentions the operation-contract-inventory check and the
    Nexus-IDE lane.
- Structural pattern: `src/GxMcp.Worker.Tests/StaSchedulerTests.cs:216-227`
  (doc read from a test) and
  `src/GxMcp.Gateway.Tests/ToolSchemaSizeTests.cs` (a test whose subject is a
  tracked file's shape).
- Verification: focused filter → all pass; full Gateway suite → no new failures.

## Done criteria

- [ ] `.\build.ps1` is the last line of the lane block in both `AGENTS.md` and `CODING_STANDARDS.md`.
- [ ] Both blocks include the Nexus-IDE lane and the operation-contract-inventory check.
- [ ] Any other CI lane missing from the blocks has been added, or a line explains that the block is the operator's superset of CI.
- [ ] A test asserts the ordering and the two new lanes, scoped to the lane code block rather than the whole file.
- [ ] The two newly documented lanes were run and their real output reported.
- [ ] No `.github/workflows/**` file was modified.
- [ ] `dotnet test src\GxMcp.Gateway.Tests` exits 0, no new failures.
- [ ] `npm test` exits 0.
- [ ] `CHANGELOG.md` has an entry under `## Unreleased` → `### Internal`.
- [ ] `git status --short` shows nothing outside the in-scope list.

## STOP conditions

Stop and report back if:

- `python scripts/generate-operation-contract-inventory.py --check` currently
  reports drift. Report the drift in full and do **not** regenerate the
  inventory — that is a separate fix with its own changelog obligation.
- The Nexus-IDE lane fails. Report the failure; do not fix
  `src/nexus-ide/**` here.
- The lane block cannot be located unambiguously in one of the documents (e.g.
  it is a list, not a fenced block, or there are two candidate blocks).
  Report the structure rather than writing a brittle locator.
- The fix appears to require modifying a file listed as out of scope.

## Maintenance notes

- Interacts with: `.github/workflows/ci.yml`. This plan makes the docs describe
  CI; if CI gains a lane, the docs should follow in the same change. The test
  does not enforce that automatically, and adding a CI-parity test is a
  reasonable follow-up rather than part of this plan.
- A reviewer should scrutinise the block-scoping in Step 3. A locator that
  matches the first `build.ps1` in the file will pass or fail for the wrong
  reason and will break the next time prose is added.
- `AGENTS.md` is read by every agent that touches this repo. A wrong lane order
  in it has a real cost beyond documentation.
