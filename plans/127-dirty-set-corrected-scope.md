# Plan 127: Mark the dirty set on every remaining write surface, with the corrected scope

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise.
>
> **Drift check (run first)**:
> `git diff --stat 0f0d71a8..HEAD -- src/GxMcp.Worker/Services`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1
- **Effort**: M
- **Risk**: LOW
- **Supersedes**: plan 126 (same intent, wrong scope — see below)
- **Depends on**: plan 110 is on a separate branch. If 110 is merged first,
  do not duplicate its marks; the tables should simply be additive.
- **Category**: bug
- **Planned at**: commit `0f0d71a8`, 2026-10-02

## Why this matters

A write that commits through the SDK without recording the target as dirty
produces a **wrong artifact**, not an error. Once an object has been built once
in a session, a later successful write that omits the mark leaves the next
build on the compile-only fast path, shipping a stale `.dll` built from a `.cs`
that was never regenerated — silently.

The mechanism is one call: `WriteService.NotePerTargetWrite(target)`.

## This plan replaces plan 126, and why

Plan 126's audit step did its job — it found that plan 126's own premise was
wrong. Plan 126 listed four services as "delegates to the write service" and
therefore exempt. **All four own commit sites**, verified:

| File | Executor claim | Verified by the advisor |
|---|---|---|
| `BatchService.cs` | own SDK transaction | `BeginTransaction()` `:89` → `EnsureSave` `:116` → **`Commit()` `:117`**, `Rollback` `:122`. `_writeService.WriteObject` `:147` is only the fallback when the object lookup returned null. |
| `AtomicAuthoringService.cs` | commits at `:134` | `obj.EnsureSave(check: false)` `:134`, no mark in file. `PropertyService.ApplyPropertiesDirect` is in-memory only. |
| `AtomicCreateService.cs` | commits at `:570` | `obj.EnsureSave(check: false)` `:570`, no mark in file. |
| `RefactorService.cs` | two own transactions | own `sdkTrans` `:676` → **`:708 Commit()`**, own `sdkTrans` `:783` → **`:815 Commit()`**, plus `EnsureSave` at `:443`, `:462`, `:590`, `:695`, `:707`, `:802`, `:814`, `:886`. |

**The error in plan 126 was mine**: I classified these four as delegating
because `_writeService.WriteObject` call sites were present in each file. That
is inference from a field name, not tracing. The `_writeService` calls are
genuine but they are *alternate* paths — the own-transaction path is the one
that actually commits in the normal case. Lesson for the audit below: **trace
the commit, do not infer it from a collaborator's presence.**

Net correction: **13 files need marks, not 9**, and the audit found two commit
sites that must **never** be marked.

## Current state

The classifier: `src/GxMcp.Worker/Services/EditDirtyTracker.cs`.
`IsDirty(kbPath, objectName)` returns `true` for any target with no explicit
clean record, so the safe default is already correct — the defect is only ever a
*missing* mark. `MarkClean` records the target in `_everBuilt`, which is what
makes a target eligible for the fast path at all.

The single mutation entry point, `src/GxMcp.Worker/Services/WriteService.cs:445`:

```csharp
        internal static void NotePerTargetWrite(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return;
            StampPerTargetWrite(target);
            MarkTargetDirty(target);
        }
```

`MarkTargetDirty` (private, same file) resolves the KB path best-effort and
calls `EditDirtyTracker.MarkDirty`. Callers pass **only the object name** — do
not pass a KB path, do not add one.

Consumers, all correct, none to be changed:
`InProcessBuildRunner.cs:413` (`targetMaySkipSpecify = !EditDirtyTracker.IsDirty(kbPath, t);`),
`BuildService.cs:2004`, `DefaultFastIncrementalDecision.cs:73`.

`WriteService.ShouldMarkTargetDirty(responseJson)` at `WriteService.cs:500` is
the existing convention for classifying a tool's persistence outcome. Prefer it
over inventing a second judgement.

### Already correct — leave alone

`ApiIntrospectService.cs` calls `WritePipeline.NoteWrite(api.Name)` at `:1098`
after `transaction.Commit()` `:1083`, which reaches `NotePerTargetWrite` via
`WritePipeline.cs:209`. A literal scan for `NotePerTargetWrite` cannot see this
and would wrongly flag the file. **Do not edit it.**

### The 13 files to mark

Each line was traced to its commit. All paths are relative to `src/GxMcp.Worker/Services/`.

| # | File | Commit site(s) | Mark argument | Notes |
|---|---|---|---|---|
| 1 | `AtomicAuthoringService.cs` | `EnsureSave` `:134` | `name` (in scope `:30`) | failure path rolls back at `:195`; mark after the save returns |
| 2 | `AtomicCreateService.cs` | `EnsureSave` `:570` | `spec.Name` | compensation paths `:528`/`:551`/`:619`; mark after the save returns |
| 3 | `BatchService.cs` | `Commit()` `:117` (own tx from `:89`) | `target` (in scope `:86`) | `finally` rollback `:122`; mark after `:117`, guarded by `ok` |
| 4 | `RefactorService.cs` | `:708`, `:815` (own tx) + `EnsureSave` `:443`, `:462`, `:590`, `:695`, `:707`, `:802`, `:814`, `:886` | the name variable in scope at each site | rollbacks at `:712`, `:819` — do **not** mark those |
| 5 | `ForgeService.cs` | `EnsureSave` `:69` | `name` (param of `Scaffold`) | |
| 6 | `GxServerWriteService.cs` | `EnsureSave` `:445`, `:461` (loop `:424`) | `name` (conflict object name `:393`) | per-object catch `:467`; mark both |
| 7 | `MergeToolService.cs` | `EnsureSave` `:153` → `saved = true` `:154` | `merged.Name` | see the partial-save note below |
| 8 | `PatternApplyService.cs` | `TryOfficialAttach` return `:2344`; `ApplyPatternToObject` engine-apply `:951` | `result.HostName` `:2259`; `targetName` `:1014` | **excludes dead code** — see below |
| 9 | `StructureService.cs` | `Commit()` `:188`, `:499`+`:519`, `:728`+`:745`, `:2159` | `targetName` (method param) | **excludes rollback helpers** — see below |
| 10 | `WwpProjectionHelper.cs` | `parent.Save(prefs)` `:187`, fallback `parent.EnsureSave(true)` `:196` | `parent.Name` | **no caller marks this** — see below |
| 11 | `WriteService.PatternWrite.cs` | `Commit()` `:229` | `target` (param `:26`) | same partial class as #12/#13 |
| 12 | `WriteService.ThemeWrite.cs` | `Commit()` `:84` | `target` (param `:14`) | already has `_objectService.MarkReadCacheDirty` `:86` — copy this placement |
| 13 | `WriteService.VisualWrite.cs` | `Commit()` `:469` (`sdkCommitCompleted = true` `:470`) | `target` (param `:134`) | post-commit returns `:513`/`:543` carry `committed: true` |

The three partials are all `public partial class WriteService`
(`WriteService.PatternWrite.cs:12`), so they share static state and can call
`NotePerTargetWrite` unqualified.

### Three things that must NOT be marked

Marking any of these is a new defect, not a fix.

1. **`PatternApplyService.cs:2792`** — `TryDirectAttachPatternInstance`'s
   `saveMethod.Invoke(...)`. The method is **defined** at `:2674` and the only
   other occurrence in `src` is a *comment* at `WriteService.PatternWrite.cs:525`
   saying "Public so PatternApplyService.TryDirectAttachPatternInstance can
   reuse the". **Zero call sites.** It is dead code kept alive by a comment
   that promises a caller which never arrived.
2. **`PatternApplyService.cs:2571`** — `TryInvokeBuildProcessUpdateParent_Legacy`'s
   `parent.EnsureSave(true)`. Definition at `:2512`; the only other occurrence
   is a *string* in `ModuleServiceInstallPathTests.cs:136`, not a call. Also dead.
3. **`StructureService.cs:1318` and `:1617`** — `RestoreAuthoredTransactionParts`
   and `RestoreTransactionSnapshot`. These run on **rollback** paths
   (`:1331`, `:1338`, `:1668`, `:1685`) and restore pre-mutation state. Marking
   them would mark a reverted write as dirty — the exact failure the mechanism
   is meant to avoid. They also have no name variable in scope, only `trn`.

### `WwpProjectionHelper` is the highest-value mark in this plan

Every caller of the projection helper marks **only the WWP instance** and
reports the parent separately:

- `WwpActionService.WebComponentReplacement.cs:222` marks `target`, `:226`
  returns `["parent"] = parent.Name`
- `WwpActionService.Grid.cs:185` marks `target`, `:189` returns the parent name
- same shape in `.Tables.cs:198`, `.FormActions.cs:151`, `.Tabs.cs:145`
- `WriteService.PatternWrite.cs:434` and `PatternApplyService.cs:2502` also
  mark only the host

Yet the helper saves the parent `WebForm` unconditionally (`:187`, or `:196` on
the fallback path) and sets `ParentSaved = true`. A `WebForm` is itself a build
target, so a projection that regenerates it and does not mark it ships a stale
generated class. **This is the one mark in the plan that establishes a
convention rather than following one** — it is also the clearest user-visible
bug in the set. Mark it at both `:187`/`:188` and `:196`/`:197`.

## Commands you will need

| Purpose | Command | Expected |
|-----------|---------|----------|
| Build Worker | `$env:GX_PATH='C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Rebuild test project | `dotnet build src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj -t:Rebuild -v:q` | exit 0 |
| Focused tests | `dotnet test src\GxMcp.Worker.Tests --filter "FullyQualifiedName~DirtyMark" --no-build --logger "console;verbosity=minimal"` | all pass |
| Full Worker suite | `dotnet test src\GxMcp.Worker.Tests --no-build --logger "console;verbosity=minimal"` | all pass (4417+ passed / 5 skipped at `0f0d71a8`) |
| Full Gateway suite | `dotnet test src\GxMcp.Gateway.Tests --no-build --logger "console;verbosity=minimal"` | all pass |
| Solution build | `dotnet build Genexus18MCP.sln -v:minimal` | exit 0 |

**Critical**: `dotnet test --no-build` runs the DLL in the *test project's*
output folder, which holds its own copy of the production assembly. Build the
**test project** or you measure old code. `dotnet test` rejects `-t:Rebuild`
(MSB1008) — put it on `dotnet build`. `|` does not work in this SDK's
`--filter`; one `FullyQualifiedName~Foo` per run.

Do **not** run `.\build.ps1`.

## Scope

**In scope** (the only files you may modify):

- The 13 files in the table above
- `src/GxMcp.Worker.Tests/MutatingToolDirtyMarkTests.cs` (create — the name plan
  110 used; if it already exists on your branch because 110 was merged, extend
  it instead of creating a duplicate, and say so in NOTES)
- `CHANGELOG.md`

**Out of scope** (do NOT touch):

- `ApiIntrospectService.cs` — already marks through `WritePipeline.NoteWrite`
  `:1098`. Verify that and leave it.
- `EditDirtyTracker.cs`, `WriteService.cs`, `InProcessBuildRunner.cs`,
  `BuildService.cs`, `DefaultFastIncrementalDecision.cs` — the mechanism and
  its consumers are correct.
- `ObjectService.MarkReadCacheDirty` call sites — read-cache invalidation, a
  different mechanism.
- The dead code at `PatternApplyService.cs:2571` and `:2792`, and the rollback
  helpers at `StructureService.cs:1318` and `:1617`.
- The nine files plan 110 already marked.
- `src/GxMcp.Gateway/**`.

## Two judgment calls the plan makes for you

State both in NOTES with your reasoning; do not silently pick one.

**A. `MergeToolService.cs:153`.** `EnsureSave` sets `saved = true` `:154`, and
the failure path returns a **partial** envelope (`MergeCompletedSaveFailed`
`:177`) with no rollback. The plan's rule is "mark after a confirmed commit",
which would mark only inside the `try`. But `WriteService.ShouldMarkTargetDirty`
(`WriteService.cs:500`) takes the conservative view on uncertain persistence.
Marking the partial is the safer choice: a needless rebuild costs time, a
skipped one ships a stale artifact. **Recommendation: mark on the success path,
and add a note if you find a caller that treats the partial as committed.**

**B. The three partials: `target` or the resolved object.** In
`WriteService.PatternWrite.cs` the SDK save acts on `resolvedObject` `:214`/
`:226`, while the mark convention is `target` (the object the tool addressed).
They coincide in the normal `PatternInstance` case. The other partials in the
family (`WriteService.EventsIsolation.cs:46`, `WriteService.Variables.cs:275`)
already mark `target`. **Recommendation: mark `target`, and record the
divergence as a comment where `resolvedObject` can differ.**

## Git workflow

- Prose commit subject, e.g.
  `"Mark the dirty set on every remaining surface that commits its own objects"`.
- `CHANGELOG.md` under `## Unreleased` → `### Fixed`, same commit. State that
  13 files are covered, that two dead-code commit sites and two rollback
  helpers were deliberately left unmarked, and that plan 110's earlier
  "partial scope" declaration can now be read as complete.
- Do NOT push, open a PR, or merge.

## Steps

### Step 1: Re-trace each commit site before marking

For each of the 13, confirm **in the live source** that the line number in the
table is still a commit site and that the mark argument is in scope there. If a
line number has drifted, find the real one and say so. Do not trust the table
blind — it was produced by a different executor, and its own plan was wrong
once already.

### Step 2: Add the marks

Call `WriteService.NotePerTargetWrite(<name>);` immediately after the save that
follows a confirmed mutation. Unqualified inside the three partials. Use the
variable actually in scope — **do not add a parameter** to obtain a name.

Never mark a rollback path, and never mark any of the three exclusions above.

### Step 3: Extend the regression guard

Create `src/GxMcp.Worker.Tests/MutatingToolDirtyMarkTests.cs` (or extend plan
110's). Three layers:

1. A **table-driven source-shape theory** over the 13 files, asserting a
   minimum `NotePerTargetWrite` count each.
2. A **consumer pin** asserting `InProcessBuildRunner.cs` still contains
   `targetMaySkipSpecify = !EditDirtyTracker.IsDirty(kbPath, t);` and
   `DefaultFastIncrementalDecision.cs` still contains
   `EditDirtyTracker.IsDirty(kbPath, t)`.

   **Pin the full unique statement, not a substring.** Plan 110's first attempt
   pinned `!EditDirtyTracker.IsDirty(kbPath, t)`, which occurs **twice** in
   `InProcessBuildRunner.cs` (`:348` and `:413`) and therefore passed even with
   the fast path replaced by `true`.
3. A **negative theory** — the one layer that is new here and the reason this
   plan is not a pure sweep: assert `StructureService.cs` contains **zero**
   `NotePerTargetWrite` inside `RestoreAuthoredTransactionParts` and
   `RestoreTransactionSnapshot`, and that `PatternApplyService.cs` contains none
   inside `TryDirectAttachPatternInstance` or
   `TryInvokeBuildProcessUpdateParent_Legacy`. Without this, a future "be
   thorough" edit marks a rollback helper and every test stays green.

Explain in the file comment why the guard is source-shape: the dirty mark and
the build decision are separated by an SDK commit that unit tests cannot
perform, so the observable link is the source. Count marks through
`RepoSource.WithoutComments`, not `RepoSource.Read` — an explanatory comment
naming the call would otherwise satisfy an assertion about the call. The same
applies to the negative theory: a comment saying "deliberately not marked"
would defeat a naive count.

### Step 4: Mutation-check every new test

- For each of the 13 files: revert that file's marks, confirm the guard goes red.
- Confirm the consumer pin goes red when `targetMaySkipSpecify` is replaced
  with `true`.
- Confirm the negative theory goes red when you add a mark inside
  `RestoreTransactionSnapshot`.

**Confirm each mutation actually applied to the source before running the
test** — print the changed line. A mutation that does not apply is
indistinguishable from a guard that does not work, and three mutation attempts
in this session failed exactly that way.

### Step 5: Full validation

```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'
dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal
dotnet build src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj -t:Rebuild -v:q
dotnet test src\GxMcp.Worker.Tests --no-build --logger "console;verbosity=minimal"
dotnet build Genexus18MCP.sln -v:minimal
dotnet test src\GxMcp.Gateway.Tests --no-build --logger "console;verbosity=minimal"
```

## Test plan

- New file: `src/GxMcp.Worker.Tests/MutatingToolDirtyMarkTests.cs` — one
  positive theory over 13 files, one consumer pin, one negative theory over the
  4 exclusion sites.
- Structural pattern: plan 110's `MutatingToolDirtyMarkTests` if present on your
  branch; otherwise `WriteDirtyOutcomeTests.cs` for the theory style and
  `InspectSectionCompletionTests.cs` for the source-shape justification.
- Verification: focused filter → all pass; full Worker suite → no new failures.

## Done criteria

- [ ] Each of the 13 files re-traced in live source before marking; any line
      drift reported.
- [ ] Every file has at least one mark after a confirmed commit, none on a
      rollback path, none inside a method that added a parameter.
- [ ] `WwpProjectionHelper.cs` marks `parent.Name` at **both** `:187`/`:188` and
      `:196`/`:197` — the fallback path is not optional.
- [ ] `ApiIntrospectService.cs`, `EditDirtyTracker.cs`, `WriteService.cs`,
      `InProcessBuildRunner.cs`, `BuildService.cs`,
      `DefaultFastIncrementalDecision.cs` untouched.
- [ ] No mark inside `RestoreAuthoredTransactionParts`, `RestoreTransactionSnapshot`,
      `TryDirectAttachPatternInstance`, or `TryInvokeBuildProcessUpdateParent_Legacy`.
- [ ] No method signature changed anywhere.
- [ ] The consumer pin uses the full unique statement.
- [ ] The negative theory exists and passes; counts read through
      `RepoSource.WithoutComments`.
- [ ] Judgment calls A and B are decided and explained in NOTES.
- [ ] Every mutation confirmed applied before its test run, each red as expected.
- [ ] `dotnet test src\GxMcp.Worker.Tests` exits 0, no new failures.
- [ ] `dotnet build Genexus18MCP.sln -v:minimal` exits 0.
- [ ] `CHANGELOG.md` has an entry under `## Unreleased` → `### Fixed`.
- [ ] `git status --short` shows nothing outside the in-scope list.

## STOP conditions

Stop and report back if:

- `WriteService.NotePerTargetWrite` or `EditDirtyTracker.MarkDirty` has a
  different signature than this plan states.
- A commit site has no in-scope variable holding the KB object name, so a mark
  would need a new parameter. Report the method; do not add the parameter.
- A commit site is not reachable from any tool — report it; do not add a
  speculative mark.
- The two "dead code" methods at `PatternApplyService.cs:2571`/`:2792` turn out
  to have a caller this scan cannot see (reflection, a generated file, a script).
  **Report it and leave the code alone** — reviving dead code is not this plan.
- `MutatingToolDirtyMarkTests.cs` already exists because plan 110 was merged,
  and extending it would produce two overlapping tables. Merge the tables.
- The fix appears to require modifying a file listed as out of scope.

## Maintenance notes

- **The 21-file figure from the original audit was right after all.** Plan 110
  closed 7 and declared partial scope; this plan closes 13, plus
  `ApiIntrospectService` was already correct = 21. Once both land, plan 110's
  CHANGELOG declaration of partial scope should be read as complete.
- **The real lesson from plan 126**: four services were classified as
  delegating because a collaborator call was present, without tracing the
  commit. `_writeService.WriteObject` appearing in a file proves nothing about
  who commits. Trace `BeginTransaction` → `Commit` → the mark, every time.
- Interacts with: any new Worker write path. The convention is that a write
  which commits through the SDK calls `NotePerTargetWrite`, and a service that
  *saves an object other than the one it was asked about* must mark both.
  `WwpProjectionHelper` is the first case of the second kind.
- Interacts with: any rollback helper. A rollback helper that marks dirty
  defeats the whole mechanism. The negative theory is what keeps that honest —
  do not let it be deleted as "redundant" by a future cleanup.
- A reviewer should scrutinise the negative theory more than the diff. The 13
  marks are mechanical; the four exclusions are the part that needs judgement.
- The dead code at `PatternApplyService.cs:2571`/`:2792` and the comment at
  `WriteService.PatternWrite.cs:525` that promises a caller are worth a
  separate deletion PR. Out of scope here.
