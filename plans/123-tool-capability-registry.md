# Plan 123: Make the contract-inventory generator's policy read tolerant, not duplicated

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise.
>
> **Drift check (run first)**:
> `git diff --stat 0f0d71a8..HEAD -- scripts/generate-operation-contract-inventory.py scripts/tests/test_operation_contract_inventory.py src/GxMcp.Gateway/OperationClassifier.cs`
> If any changed, compare the "Current state" excerpts against live code before
> proceeding; on a mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW — Python-only, plus one guard. No production C# changes.
- **Depends on**: none
- **Category**: mainainability
- **Planned at**: commit `0f0d71a8`, 2026-10-02
- **Revision**: 2. Revision 1 was self-contradictory; its executor proved it by
  running the generator. That proof is correct and this revision satisfies it.

## Why this matters

`scripts/generate-operation-contract-inventory.py` produces
`docs/operation-contract-inventory.json`, a published artifact. To build it, the
script reads the classification policy out of C# source **by regex**. That parse
is the fragile part, and its fragility is invisible until it silently produces a
wrong inventory.

The current reader, `read_policy()` at `:39`, requires all of the following
about `OperationClassifier.cs`:

- the exact declaration text `private static readonly HashSet<string> <Field>`
  (`_set_block` at `:30`, and the `marker` string at `:47`)
- the field's closing brace indented with **exactly 8 spaces** —
  the regex ends `\n        \};`
- `ActionContracts` to be followed by a blank line and the literal comment
  `// Only actions` (`:49`), which is a formatting detail, not a contract
- `DryRunCapableActions` located by a separate bespoke search (`:77`)

Reformat the file, or rename a modifier, or let an editor's indentation
preference through, and the generator raises `ValueError` — or worse, finds a
shorter match and reads a *different* set. Either way the failure is at release
time, not at edit time.

## Why revision 1 was self-contradictory

Revision 1 asked for a tool capability registry and specified a test forbidding
duplicate literal sets in the classifier. Its executor proved the contradiction by
running `read_policy()` against mutated copies of the source: **removing any of
the six declarations raises `ValueError`**. So the generator hard-requires
exactly what the test forbade.

Revision 1's done criterion and its own test 3 could not both hold. That proof is
correct.

## What the investigation changed about the fix

The original plan proposed moving the policy into a registry in
`ToolIdentity.cs`. **That is wrong on the merits, and this revision does not do
it.** Two facts, both verified:

1. **`ToolIdentity.cs` already exists** (104 lines) and is a *different*
   concern. Its own doc comment: *"Single tool-identity registry for canonical
   names, action projections and legacy aliases."* Moving read/mutate
   classification there would conflate two unrelated responsibilities.
2. **`OperationClassifier.Describe()` already exists** at `:506` and already
   cross-checks the parse. `verify_classifier_policy()` at `:218` shells out to
   `dotnet test --filter FullyQualifiedName~OperationInventoryClassifierParityTests`
   and fails closed when it disagrees.

So the real duplication is narrower and more specific than revision 1 claimed:
**the regex parse duplicates an authority that `Describe` already provides
authoritatively.** The four sets are not redundant with anything — they *are* the
single source of truth, and `Describe` reads them.

## The fix

Two parts, both in Python. No C# moves.

1. **Make the parse tolerant of formatting.** Match the six declarations by
   *name* and brace structure, not by exact modifiers and indentation. Keep
   failing closed when a declaration is genuinely absent — that behaviour is
   correct and must not be weakened into a silent empty set.
2. **Pin the parse in the Python lane.** `scripts/tests/test_operation_contract_inventory.py`
   already exists with six tests. Add tests that run `read_policy()` against the
   real source and assert all six fields are found and non-empty. This is the
   guard that makes a future C# refactor **loud**: `AGENTS.md` is explicit that
   `python -m unittest discover -s scripts\tests` catches contract drift the .NET
   suite is blind to, and this is precisely that case. Today the first signal is
   the release gate failing.

## Commands you will need

| Purpose | Command | Expected |
|-----------|---------|----------|
| Generator self-check | `python scripts\generate-operation-contract-inventory.py --check` | exit 0 |
| Generator tests | `python -m unittest discover -s scripts\tests -p "test_operation_contract_inventory.py" -v` | all pass |
| Full Python suite | `python -m unittest discover -s scripts\tests` | all pass |
| Tool contract gate | `python scripts\validate-tool-contracts.py` | exit 0 |
| Solution build | `dotnet build Genexus18MCP.sln -v:minimal` | exit 0 |

Do **not** run `.\build.ps1`. You are not changing C#, so a .NET test run is only
needed if you find yourself editing C# at all — which is a STOP condition.

## Scope

**In scope**:

- `scripts/generate-operation-contract-inventory.py` — `read_policy`, `_set_block`,
  and the `DryRunCapableActions` search only
- `scripts/tests/test_operation_contract_inventory.py`
- `CHANGELOG.md`

**Out of scope** (do NOT touch):

- `src/GxMcp.Gateway/OperationClassifier.cs` — **the policy stays here.** It is the
  single source of truth and `Describe` consumes it. Read it; do not edit it. If
  you think it needs a change, that is a STOP.
- `src/GxMcp.Gateway/ToolIdentity.cs` — different concern, see above.
- `docs/operation-contract-inventory.json` — a generated artifact. Regenerate it
  only if your parse change alters the output, and if it does, **report that as a
  finding before committing it**: a tolerant parse that changes the inventory
  means the old inventory was wrong.
- `docs/mcp_capabilities_inventory.md`.
- `verify_classifier_policy()` at `:218` — the parity cross-check already works.

## Git workflow

- Prose commit subject, e.g.
  `"Stop the contract generator from depending on how the classifier is formatted"`.
- `CHANGELOG.md` under `## Unreleased` → `### Internal`. This changes no
  published output and no tool behaviour, so it is `Internal`, not `Fixed`.
- Do NOT push, open a PR, or merge.

## Steps

### Step 1: Read the whole script before changing it

`scripts/generate-operation-contract-inventory.py` in full, and
`scripts/tests/test_operation_contract_inventory.py` in full. Revision 1's author
(the advisor) read only the `_set_block` function and wrote a plan that could not
be satisfied — the fourth time in this session that a partial read produced a
wrong plan.

Note that `read_policy` has a second hard-coded marker: `"// Only actions"` at
`:49`, used to find the end of the `ActionContracts` block. Treat it the same way
as the indentation literal.

### Step 2: Make the parse tolerant

Rewrite the readers so they locate each declaration by name and balanced-brace
structure rather than by exact modifier text and indentation. Concretely, for
each of `PureReadOnlyTools`, `KnownMutatingTools`, `ModeDependentTools`,
`NameOnlyMutatingTools`, `DryRunCapableActions` and the `ActionContracts`
dictionary:

- match the field name at a declaration boundary
- allow any modifier combination that still declares a static collection
- find the body by brace matching, not by expecting `\n        \};`

Two invariants you must not lose:

- **Fail closed.** If a declaration is genuinely missing, raise `ValueError`
  naming the field and the file. A tolerant parse that returns an empty set on a
  typo would produce an inventory that silently drops every tool in that class —
  far worse than a build failure.
- **Only string literals inside the body are values.** `_strings()` already does
  this. A declaration like `... = BuildFrom("a", "b")` must not be read as
  including `BuildFrom` or the parentheses.

Write a short comment at each reader saying *what* it is now tolerant of, so the
next person does not tighten it back.

### Step 3: Pin it in the Python lane

Add to `scripts/tests/test_operation_contract_inventory.py`:

1. `test_read_policy_locates_every_required_field` — run `read_policy()` against
   the real `OperationClassifier.cs` and assert all four named sets,
   `ActionContracts` and `DryRunCapableActions` are found and non-empty.
2. `test_read_policy_is_insensitive_to_reformatting` — the load-bearing one.
   Take the real source, re-indent the six declarations (shift the whole file by
   four spaces, and/or strip the `private ` modifier), write it to a temp file,
   and assert `read_policy()` returns **the same result** as on the original.
   This is what fails when someone tightens the regex again.
3. `test_read_policy_still_fails_closed_on_a_missing_field` — delete one
   declaration from a temp copy and assert `ValueError` is raised, not an empty
   set. Without this, "tolerant" and "broken" are indistinguishable.

Import `read_policy` from the generator module. Check how
`test_operation_contract_inventory.py` already reaches the script — do not add a
second import mechanism.

### Step 4: Mutation-check every new test

| Mutation | Must turn red |
|---|---|
| restore the exact-8-space-closing-brace regex | `test_read_policy_is_insensitive_to_reformatting` |
| restore the exact `private static readonly HashSet<string>` prefix | same |
| make a missing declaration yield `set()` instead of raising | `test_read_policy_still_fails_closed_on_a_missing_field` |
| make `read_policy` return only the first N fields | `test_read_policy_locates_every_required_field` |

**Confirm each mutation applied to the source before running the tests** — print
the changed line. Several mutation attempts in this session silently no-opped and
produced convincing false greens.

### Step 5: Validation

```powershell
python scripts\validate-tool-contracts.py
python scripts\generate-operation-contract-inventory.py --check
python -m unittest discover -s scripts\tests
git diff --stat -- docs\operation-contract-inventory.json
```

**The inventory must be unchanged.** If `git diff` on
`docs/operation-contract-inventory.json` is non-empty, stop and report: it means
the previous output was produced by a parse that did not agree with the current
one, which is a finding about the published artifact, not something to fold
silently into this commit.

## Test plan

- Modified: `scripts/tests/test_operation_contract_inventory.py` — three new
  tests alongside the existing six.
- No C# test changes.
- Verification: the three gates in Step 5.

## Done criteria

- [ ] All six fields are located by name and brace structure, not by modifier text
      or indentation.
- [ ] A genuinely missing declaration still raises `ValueError` naming the field.
- [ ] Only string literals inside a declaration body are read as values.
- [ ] Each reader carries a comment saying what it tolerates.
- [ ] Three new Python tests exist and pass.
- [ ] `python scripts\generate-operation-contract-inventory.py --check` exits 0.
- [ ] `python -m unittest discover -s scripts\tests` exits 0.
- [ ] `python scripts\validate-tool-contracts.py` exits 0.
- [ ] `git diff -- docs\operation-contract-inventory.json` is **empty**.
- [ ] `OperationClassifier.cs` and `ToolIdentity.cs` are unmodified.
- [ ] All four mutations confirmed applied and red as specified.
- [ ] `CHANGELOG.md` has an entry under `## Unreleased` → `### Internal`.
- [ ] `git status --short` shows nothing outside the in-scope list.

## STOP conditions

Stop and report back if:

- `read_policy` does not return the six fields the plan names, or its signature
  differs from `(path: Path) -> tuple[...]`. Report the actual signature.
- Making the parse tolerant *changes* `docs/operation-contract-inventory.json`.
  Report the diff — do not commit a regenerated artifact.
- You conclude the policy should move out of `OperationClassifier.cs`. Report the
  case for it; this plan does not make that move.
- `ActionContracts` cannot be located by brace matching because its initialiser
  contains a brace inside a string literal. Report it — this is a real parsing
  hazard, not a hypothetical one.
- The fix appears to require modifying a file listed as out of scope.

## Maintenance notes

- **Why this is `Internal` and not `Fixed`**: no published artifact and no tool
  behaviour changes. If the inventory turns out to change, that reclassifies the
  work — see the STOP condition above.
- The three new tests are the durable part. The tolerant regex is the part that
  will get tightened again by someone who means well; the reformatting test is
  what makes that visible.
- This plan supersedes revision 1's premise. `ToolIdentity` is a name/action/
  alias registry, not a capability registry, and `Describe` already exists and
  already cross-checks the parse. Do not "complete" revision 1 by moving sets
  into `ToolIdentity.cs`; that would conflate two responsibilities to satisfy a
  plan written before either was read.
- A fuller fix — having C# emit the policy as JSON so there is no parse at all —
  is the obvious next step and would delete `_set_block` entirely. It was not
  authorised and is not this plan. It is worth writing up once this lands.
