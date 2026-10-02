# Plan 126: Close the incremental-build dirty set for the remaining write surfaces

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise.
>
> **Drift check (run first)**: `git diff --stat 0f0d71a8..HEAD -- src/GxMcp.Worker`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1
- **Effort**: M
- **Risk**: LOW
- **Depends on**: none (plan 110 is a separate branch; if 110 is merged first,
  re-verify the marks it added are not duplicated here)
- **Category**: bug
- **Planned at**: commit `0f0d71a8`, 2026-10-01

## Why this matters

Plan 110 closed 7 of the 21 Worker files that commit SDK writes without
recording the target as dirty. This plan closes the rest. The defect produces a
**wrong artifact**: once an object has been built once by MCP in a session, a
later successful write that fails to mark the target dirty leaves the next
build on the compile-only fast path, shipping a stale `.dll` built from a `.cs`
that was never regenerated — silently, with no error surface.

Plan 110's executor reported the remaining 18 files; the count I measured
independently is 14 after its 7, and they are **not all the same kind**. Some
own their commit site and are real gaps; some delegate to a surface that already
marks and are already correct. This plan is therefore an **audit first, fix
second** exercise, and the audit's output is a deliverable in its own right.

## Current state

The classifier: `src/GxMcp.Worker/Services/EditDirtyTracker.cs` (94 lines).
`IsDirty(kbPath, objectName)` at `:84` returns `true` for any target with no
explicit clean record, so the safe default is already correct — the defect is
only ever a *missing* `MarkDirty`. `MarkClean` at `:55` also records the target
in `_everBuilt`, which is what makes a target eligible for the fast path at all.

The single mutation entry point is
`src/GxMcp.Worker/Services/WriteService.cs:445`:

```csharp
        internal static void NotePerTargetWrite(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return;
            StampPerTargetWrite(target);
            MarkTargetDirty(target);
        }
```

`MarkTargetDirty` (private, same file) resolves the KB path best-effort and
calls `EditDirtyTracker.MarkDirty`. Both failures are best-effort by design.

Consumers, all reading the tracker correctly and needing no change:

- `src/GxMcp.Worker/Services/InProcessBuildRunner.cs:413` —
  `targetMaySkipSpecify = !EditDirtyTracker.IsDirty(kbPath, t);`
- `src/GxMcp.Worker/Services/BuildService.cs:2004` — the `NoBuildNeeded`
  short-circuit.
- `src/GxMcp.Worker/Services/DefaultFastIncrementalDecision.cs:73` — the
  dirty/clean split feeding both.

Plan 110 already marked (do not duplicate): `PropertyService.cs` (3 sites),
`LayoutService.cs` (3), `LayoutService.SourcePersistence.cs` (1), and six files
under `Services/Structure/` (`AuthoringService` ×3, `AttributeWriteService`,
`GroupStructureService`, `IndexService`, `VisualStructureService`,
`DomainWriteService`). It also established the two rules this plan follows:
mark **after** a confirmed commit, never on a rollback path; and classify by
`WriteService.ShouldMarkTargetDirty(responseJson)` rather than inventing a
second judgement.

**The 14 files, with the classification I measured.** This is the starting
point for your audit, not a substitute for it:

| File | `EnsureSave(` | `.Commit()` | `.Save()` | Pre-existing signal |
|---|---|---|---|---|
| `ApiIntrospectService.cs` | 1 | 1 | 1 | **already calls `WritePipeline.NoteWrite`** (`ApiIntrospectService.cs:1098`) — a different entry point |
| `AtomicAuthoringService.cs` | 1 | 0 | 0 | delegates to the write service |
| `AtomicCreateService.cs` | 2 | 0 | 0 | delegates to the write service |
| `BatchService.cs` | 1 | 1 | 0 | delegates to the write service |
| `ForgeService.cs` | 1 | 0 | 0 | **no mark signal found** |
| `GxServerWriteService.cs` | 2 | 0 | 0 | **no mark signal found** |
| `MergeToolService.cs` | 3 | 0 | 0 | **no mark signal found** |
| `PatternApplyService.cs` | 2 | 0 | 2 | **no mark signal found** |
| `RefactorService.cs` | 8 | 2 | 0 | delegates to the write service |
| `StructureService.cs` | 2 | 8 | 6 | **no mark signal found** |
| `WwpProjectionHelper.cs` | 1 | 0 | 0 | **no mark signal found** |
| `WriteService.PatternWrite.cs` | 3 | 1 | 0 | **partial class of `WriteService`** |
| `WriteService.ThemeWrite.cs` | 1 | 1 | 0 | **partial class of `WriteService`**, already calls `MarkReadCacheDirty` (`:86`) |
| `WriteService.VisualWrite.cs` | 1 | 1 | 0 | **partial class of `WriteService`** |

Two structural facts about the three partials, verified: they are all
`public partial class WriteService` (`WriteService.PatternWrite.cs:12`), so they
share static state with `WriteService.cs` and can call `NotePerTargetWrite`
directly. `WriteService.ThemeWrite.cs` already has a `MarkReadCacheDirty` call at
`:86` and no `NotePerTargetWrite`, which makes it a clear marker of the pattern
to follow.

A seventh category exists that this table does not cover: a write path can
commit through a helper that this scan cannot see. **That is the first thing
your audit must establish per file.**

## Commands you will need

| Purpose | Command | Expected on success |
|-----------|---------|---------------------|
| Build Worker | `$env:GX_PATH='C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Rebuild test project | `dotnet build src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj -t:Rebuild -v:q` | exit 0 |
| Focused tests | `dotnet test src\GxMcp.Worker.Tests --filter "FullyQualifiedName~DirtyMark" --no-build --logger "console;verbosity=minimal"` | all pass |
| Full Worker suite | `dotnet test src\GxMcp.Worker.Tests --no-build --logger "console;verbosity=minimal"` | all pass (4417+ passed / 5 skipped at `0f0d71a8`) |
| Solution build | `dotnet build Genexus18MCP.sln -v:minimal` | exit 0 |

**Critical**: `dotnet test --no-build` runs the DLL in the *test project's*
output folder, which holds its own copy of the production assembly. Build the
**test project** or you measure old code. `dotnet test` rejects `-t:Rebuild`
(MSB1008) — put it on `dotnet build`. `|` does not work in this SDK's
`--filter`; one `FullyQualifiedName~Foo` per run.

Do **not** run `.\build.ps1`.

## Scope

**In scope**:

- `src/GxMcp.Worker/Services/ForgeService.cs`
- `src/GxMcp.Worker/Services/GxServerWriteService.cs`
- `src/GxMcp.Worker/Services/MergeToolService.cs`
- `src/GxMcp.Worker/Services/PatternApplyService.cs`
- `src/GxMcp.Worker/Services/StructureService.cs`
- `src/GxMcp.Worker/Services/WwpProjectionHelper.cs`
- `src/GxMcp.Worker/Services/WriteService.PatternWrite.cs`
- `src/GxMcp.Worker/Services/WriteService.ThemeWrite.cs`
- `src/GxMcp.Worker/Services/WriteService.VisualWrite.cs`
- `src/GxMcp.Worker.Tests/MutatingToolDirtyMarkTests.cs` (create — the name plan
  110 used; if that file already exists on your branch because 110 was merged,
  extend it instead of creating a duplicate, and say so in NOTES)

**Out of scope** (do NOT touch):

- `EditDirtyTracker.cs`, `WriteService.cs`, `InProcessBuildRunner.cs`,
  `BuildService.cs`, `DefaultFastIncrementalDecision.cs` — the mechanism and its
  consumers are correct.
- `ApiIntrospectService.cs` — it already calls `WritePipeline.NoteWrite` at
  `:1098`. **Verify that and leave it alone.** It is in the table only because a
  literal scan for `NotePerTargetWrite` cannot see it.
- `AtomicAuthoringService.cs`, `AtomicCreateService.cs`, `BatchService.cs`,
  `RefactorService.cs` — they delegate to the write service. Verify that per
  file; if one turns out to own a commit site, that is a STOP condition, not a
  licence to edit it.
- `ObjectService.MarkReadCacheDirty` call sites — read-cache invalidation, a
  different mechanism.
- The nine files plan 110 already marked.
- `src/GxMcp.Gateway/**`.

## Git workflow

- Prose commit subject, e.g.
  `"Mark the dirty set from the write surfaces that commit their own objects"`.
- `CHANGELOG.md` under `## Unreleased` → `### Fixed`, same commit.
- Do NOT push, open a PR, or merge.

## Steps

### Step 1: Audit all 14 files and publish the classification

For each of the 14, answer three questions **with a `file:line` citation**:

1. Does this file reach a `trans.Commit()` / `EnsureSave()` on its own, or does
   it hand the object to another service that does? Trace the actual call, do
   not infer from a field name.
2. If it owns the commit, what is the in-scope variable holding the KB object
   name at that point?
3. Is the commit on a path that rolls back on failure (so marking would make a
   reverted write dirty) or on a path that commits unconditionally?

Write the resulting table into your report. Classify each file as
**owns-a-commit-site**, **delegates-to-a-marking-surface**, or
**rollback-only**. This table is the deliverable even if you fix nothing.

If your audit finds a file outside the in-scope list that owns a commit site,
that is a STOP condition — report it, do not edit it.

### Step 2: Add the marks to the files that own a commit site

For each classified **owns-a-commit-site** file, call
`WriteService.NotePerTargetWrite(<objectNameVariable>);` immediately after the
save that follows a confirmed mutation — the same placement rule plan 110
established. Use the variable actually in scope; do not add a parameter to make
one available.

For the three `WriteService` partials, call `NotePerTargetWrite` directly (same
class). Follow the pattern `WriteService.ThemeWrite.cs:86` already demonstrates
for the read-cache mark.

For files classified **delegates-to-a-marking-surface**, add nothing. Record
which surface marks, so the classification is auditable.

### Step 3: Extend the regression guard

Create `src/GxMcp.Worker.Tests/MutatingToolDirtyMarkTests.cs` (or extend plan
110's if it exists). Two layers:

1. A **table-driven source-shape theory** over the files you marked, asserting a
   minimum `NotePerTargetWrite` count each — the shape plan 110 used, so the two
   plans' guards read the same way.
2. A **consumer pin** asserting
   `InProcessBuildRunner.cs` still contains
   `targetMaySkipSpecify = !EditDirtyTracker.IsDirty(kbPath, t);` and
   `DefaultFastIncrementalDecision.cs` still contains
   `EditDirtyTracker.IsDirty(kbPath, t)`.

   **Pin the full unique statement, not a substring.** Plan 110's first attempt
   pinned `!EditDirtyTracker.IsDirty(kbPath, t)`, which occurs **twice** in
   `InProcessBuildRunner.cs` (`:348` and `:413`) and therefore passed even with
   the fast path replaced by `true`. Do not repeat that.

Write the file-level comment explaining why the guard is source-shape: the dirty
mark and the build decision are separated by an SDK commit that unit tests
cannot perform, so the observable link is the source. Count marks through
`RepoSource.WithoutComments`, not `RepoSource.Read` — an explanatory comment
naming the call would otherwise satisfy an assertion about the call.

### Step 4: Mutation-check every new test

For each file you marked, revert that file's marks and confirm the guard goes
red. Also confirm the consumer pin goes red when `targetMaySkipSpecify` is
replaced with `true`.

**Confirm each mutation actually applied to the source before running the
test** — print the changed line. A mutation that does not apply is
indistinguishable from a guard that does not work, and three of this session's
own mutation attempts failed exactly that way.

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

- New file: `src/GxMcp.Worker.Tests/MutatingToolDirtyMarkTests.cs` — one theory
  over the marked files, plus the consumer pin.
- Structural pattern: plan 110's `MutatingToolDirtyMarkTests` if present on your
  branch; otherwise `src/GxMcp.Worker.Tests/WriteDirtyOutcomeTests.cs` for the
  theory style and `InspectSectionCompletionTests.cs` for the source-shape
  justification.
- Verification: focused filter → all pass; full Worker suite → no new failures.

## Done criteria

- [ ] The Step 1 classification table covers all 14 files, each with a citation.
- [ ] Every file classified **owns-a-commit-site** has at least one mark after a
      confirmed commit, and none on a rollback path.
- [ ] No file classified **delegates-to-a-marking-surface** was modified.
- [ ] `ApiIntrospectService.cs`, `EditDirtyTracker.cs`, `WriteService.cs`,
      `InProcessBuildRunner.cs`, `BuildService.cs`, `DefaultFastIncrementalDecision.cs`
      and the four delegating services are untouched.
- [ ] No new parameter was added to any method to obtain an object name.
- [ ] The guard pins `targetMaySkipSpecify = !EditDirtyTracker.IsDirty(kbPath, t);`
      as a full statement, not a substring.
- [ ] Mark counts are read through `RepoSource.WithoutComments`.
- [ ] Every mutation was confirmed applied before its test run, and each made
      the expected test red.
- [ ] `dotnet test src\GxMcp.Worker.Tests` exits 0, no new failures.
- [ ] `dotnet build Genexus18MCP.sln -v:minimal` exits 0.
- [ ] `CHANGELOG.md` has an entry under `## Unreleased` → `### Fixed`, stating
      how many files were closed and how many were already correct.
- [ ] `git status --short` shows nothing outside the in-scope list.

## STOP conditions

Stop and report back if:

- `WriteService.NotePerTargetWrite` or `EditDirtyTracker.MarkDirty` has a
  different signature than this plan states.
- A file classified **delegates-to-a-marking-surface** turns out to own a commit
  site after tracing. Report the file and the call path; do not edit it.
- A commit site has no in-scope variable holding the KB object name, so a mark
  would need a new parameter. Report the method; do not add the parameter.
- A commit site is on a path that is not reachable from any tool — report it;
  do not add a speculative mark.
- `MutatingToolDirtyMarkTests.cs` already exists because plan 110 was merged,
  and extending it would produce two overlapping tables. Merge the tables and
  say so rather than creating a second file.
- The fix appears to require modifying a file listed as out of scope.

## Maintenance notes

- **This closes a 2× scope error in the original audit.** The audit reported
  "four surfaces"; the measurement was 21 files. Plan 110 closed 7. If your
  classification finds that most of the remaining 14 were already correct, the
  honest total is smaller than 21 and the changelog must say so — do not claim
  coverage the marks do not provide.
- Interacts with: any new Worker write path. The convention after this plan is
  that a write which commits through the SDK calls `NotePerTargetWrite`.
  A delegating service is exempt, because the surface it delegates to marks.
- A reviewer should scrutinise the Step 1 table more than the diff. The value
  of this plan is the classification; the marks are mechanical once it is right.
- `PatternApplyService` deserves particular attention: it is the surface behind
  `genexus_apply_pattern`, whose reapply route is refused for non-WWP patterns
  (plan 124). A mark on a refused route is harmless but a mark on a route that
  does not commit would be wrong — check before marking.