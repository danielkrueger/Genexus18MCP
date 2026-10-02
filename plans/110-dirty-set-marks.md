# Plan 110: Mark the incremental-build dirty set from every mutating write surface

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
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `0f0d71a8`, 2026-10-01

## Why this matters

The Worker's incremental build has a fast path: if a target is classified
"clean" it skips Specify+Generate and calls `Run.Compile` directly, and with
`fastIncremental=true` an all-clean target list short-circuits to
`NoBuildNeeded` without dispatching a build at all. Cleanliness is decided by
`EditDirtyTracker`, whose only mutation is `EditDirtyTracker.MarkDirty`, reached
solely through `WriteService.NotePerTargetWrite`.

Four write surfaces that mutate KB objects successfully never call it:
`PropertyService`, `LayoutService`, and everything under `Services/Structure/`.
`genexus_properties action=set`, `genexus_layout set_property`, and the
`genexus_structure` mutating actions all commit through the SDK and return
success, yet leave the target classified clean. The next build then ships a
stale `.dll` built from a `.cs` that was never regenerated — silently, with no
error surface. This is the one finding in the audit that produces a wrong
artifact rather than a wrong message.

## Current state

Key files:

- `src/GxMcp.Worker/Services/EditDirtyTracker.cs` — the tracker. `IsDirty`
  (line 84) returns `true` for any target with no explicit clean record, so the
  safe default is already correct; the defect is purely the missing
  `MarkDirty` on four write paths.
- `src/GxMcp.Worker/Services/WriteService.cs:445` —
  `internal static void NotePerTargetWrite(string target)`, which calls
  `StampPerTargetWrite(target)` then `MarkTargetDirty(target)`. `MarkTargetDirty`
  (private, same file) resolves the KB path best-effort through
  `_objectServiceRef?.GetKbService()?.GetKbPath()` and then calls
  `EditDirtyTracker.MarkDirty(kbPath, target)` inside a `try/catch` that is
  deliberately best-effort.
- `src/GxMcp.Worker/Services/InProcessBuildRunner.cs:413` —
  `targetMaySkipSpecify = !EditDirtyTracker.IsDirty(kbPath, t);` guarding the
  compile-only fast path at `:414`.
- `src/GxMcp.Worker/Services/BuildService.cs:2004` —
  `if (fiDecision.NothingDirty) { return ... "NoBuildNeeded" }`.
- `src/GxMcp.Worker/Services/DefaultFastIncrementalDecision.cs:73` — the only
  consumer that classifies targets into dirty/clean, feeding both of the above.

The four surfaces that miss the mark:

- `src/GxMcp.Worker/Services/PropertyService.cs` — **zero** occurrences of
  `NotePerTargetWrite`, `EditDirtyTracker`, `StampPerTargetWrite` or
  `MarkDirty`. Three commit sites: `:869`, `:963`, `:1390`, each preceded by
  `obj.EnsureSave()` (`:868`, `:962`, `:1389`) inside a
  `try { ... } finally { if (!committed) trans.Rollback(); }`. Reached from
  `src/GxMcp.Worker/Services/CommandDispatcher.cs:2815` (`SetProperties`) and
  `:2823` (`SetProperty`), both of which `return` directly.
- `src/GxMcp.Worker/Services/LayoutService.cs` and its partials
  (`LayoutService.SourcePersistence.cs`, and the write paths around
  `:966`, `:1105`, `:1232`) — zero occurrences of the same four symbols. They
  already call `_objectService.MarkReadCacheDirty(obj, "Layout")` (for example
  `:981`, `:1120`, `:1241`), which is the read-cache invalidation and is a
  **different** mechanism.
- `src/GxMcp.Worker/Services/Structure/AuthoringService.cs:94`,
  `Structure/AttributeWriteService.cs:103`,
  `Structure/GroupStructureService.cs:121`, `Structure/IndexService.cs:219`,
  `Structure/VisualStructureService.cs:72`, `Structure/DomainWriteService.cs:94`
  — each ends a successful mutation in `EnsureSave()` / `Save()` with no dirty
  mark. The whole `Services/Structure/` directory has **zero** occurrences of
  any of the four symbols.

**Distinguish these two mechanisms carefully.** They are not the same and both
are required:

- `EditDirtyTracker.MarkDirty` — build-path bookkeeping. Decides
  Specify+Generate vs compile-only. This is what this plan adds.
- `ObjectService.MarkReadCacheDirty` (`src/GxMcp.Worker/Services/ObjectService.cs:4090`)
  — read-cache invalidation, so the next read does not serve pre-write bytes.
  `LayoutService` already does this; `PropertyService` does not, which is a
  separate concern this plan does **not** attempt to change.

For reference, the surfaces that already do it right:
`src/GxMcp.Worker/Services/WriteService.cs:1289` calls `MarkTargetDirty(target)`
gated on `!dryRun && ShouldMarkTargetDirty(wrapped)`;
`src/GxMcp.Worker/Services/PatchService.cs:952` and the `WwpActionService.*`
partials (`FormActions.cs:151`, `Grid.cs:185`, `Tables.cs:198`, `Tabs.cs:145`,
`WebComponentReplacement.cs:222`) call
`WriteService.NotePerTargetWrite(target)` after a confirmed save.

Repo conventions this change must match:

- Write outcome is classified by `WriteService.ShouldMarkTargetDirty(string
  responseJson)` (`WriteService.cs:478`), which parses the envelope and returns
  `true` for `WriteNoChange`, rollback, and unparseable responses. A confirmed
  `WriteNoChange` is deliberately **not** dirty. Reuse that judgement rather
  than inventing a second one.
- Only mark **after** a confirmed commit. Marking before the save would make a
  rolled-back write dirty.
- Every regression guard in this repo gets a mutation check (revert the fix,
  confirm the test goes red). See `AGENTS.md` § Required workflow.
- Tests read source shape through `src/TestSupport/SourceAssert.cs`
  (`SourceAssert.Count(haystack, needle)`) and `src/TestSupport/RepoSource.cs`
  (`RepoSource.Read(...)`, `RepoSource.CsFilesIn(...)`). Use these; do not
  hand-roll a new source-reading helper. Model on
  `src/GxMcp.Worker.Tests/WriteDirtyOutcomeTests.cs` and
  `src/GxMcp.Worker.Tests/InspectSectionCompletionTests.cs` (the latter shows
  the accepted split: a unit test for the classifier, a source-shape assertion
  where a live SDK model is unavailable).

## Commands you will need

| Purpose | Command | Expected on success |
|-----------|---------|---------------------|
| Build Worker | `$env:GX_PATH='C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0, 0 errors |
| Focused tests | `dotnet test src\GxMcp.Worker.Tests --filter "FullyQualifiedName~DirtyTracker\|FullyQualifiedName~WriteDirtyOutcome\|FullyQualifiedName~FastIncrementalDecision" --logger "console;verbosity=minimal"` | all pass |
| Full Worker suite | `dotnet test src\GxMcp.Worker.Tests --logger "console;verbosity=minimal"` | all pass (was 1811+ passed / 4 skipped before this change) |
| Solution build | `dotnet build Genexus18MCP.sln -v:minimal` | exit 0 |

If a build fails with `MSB3027`/`MSB3021` naming `GxMcp.Gateway.exe` or
`GxMcp.Worker.exe`, that is the documented scoped-permission case: run
`Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force` and retry. After **any**
compile failure, force `-t:Rebuild` before trusting a test result
(`CODING_STANDARDS.md` § Build state).

## Scope

**In scope** (the only files you may modify):

- `src/GxMcp.Worker/Services/PropertyService.cs`
- `src/GxMcp.Worker/Services/LayoutService.cs` and
  `src/GxMcp.Worker/Services/LayoutService.SourcePersistence.cs`
- `src/GxMcp.Worker/Services/Structure/*.cs` (any file in that directory)
- `src/GxMcp.Worker.Tests/MutatingToolDirtyMarkTests.cs` (create)

**Out of scope** (do NOT touch, even though they look related):

- `src/GxMcp.Worker/Services/EditDirtyTracker.cs` — its `IsDirty` default is
  already the safe one. Changing it would weaken the guarantee this plan
  restores.
- `src/GxMcp.Worker/Services/WriteService.cs` — `NotePerTargetWrite` already
  exists and is correct. You are adding call sites, not changing the primitive.
- `src/GxMcp.Worker/Services/InProcessBuildRunner.cs`,
  `src/GxMcp.Worker/Services/BuildService.cs`,
  `src/GxMcp.Worker/Services/DefaultFastIncrementalDecision.cs` — consumers.
  They read the tracker correctly.
- `ObjectService.MarkReadCacheDirty` call sites — a different mechanism, see
  above.
- `src/GxMcp.Gateway/**` — no Gateway change is needed for this defect.

## Git workflow

- Branch: `advisor/110-dirty-set-marks` (created for you in an isolated
  worktree; commit there).
- Commit per step or per logical unit. This repo's recent subjects are prose
  sentences stating the outcome, not conventional-commit prefixes, e.g.
  `"Reclaim trigram postings on replacement, eviction and reload"` and
  `"Reduce duplication and complexity across Worker and Gateway"`. Older
  commits use `fix(scope): ...`. Match the prose style.
- Add a `CHANGELOG.md` entry under `## Unreleased` → `### Fixed` in the same
  commit as the code change. This is mandatory per `AGENTS.md`; a change
  without one is rejected. Write it in the repo's established voice: state the
  concrete defect, the mechanism, and what changes for the caller, and say
  plainly what was **not** fixed if anything was left out.
- Do NOT push, open a PR, or merge.

## Steps

### Step 1: Add the dirty marks to `PropertyService`

Three commit sites (`:869`, `:963`, `:1390`), each in the shape:

```csharp
                        obj.EnsureSave();
                        trans.Commit();
                        committed = true;
                        InvalidatePropertyCache(obj);
```

Call `WriteService.NotePerTargetWrite(target);` immediately after
`committed = true;`. `target` is the method parameter and is in scope at all
three sites. Do not add it on the failure paths — `committed` is the guard.

Verify: `Select-String -Path src\GxMcp.Worker\Services\PropertyService.cs -Pattern 'NotePerTargetWrite'` returns exactly 3 occurrences (one per commit site).

### Step 2: Add the dirty marks to `LayoutService`

Find every successful `EnsureSave()` / `Save()` in
`LayoutService.cs` and `LayoutService.SourcePersistence.cs` that commits a
layout or form write. The ones near the existing `MarkReadCacheDirty` calls
(`LayoutService.cs:981`, `:1120`, `:1241` and
`LayoutService.SourcePersistence.cs:212`, `:398`, `:444`) are the confirmed
sites; place `WriteService.NotePerTargetWrite(target);` next to each, after
the commit succeeds. Use the local variable holding the object name in each
method — inspect the enclosing method signature rather than assuming `target`
is the parameter name.

Verify: `Select-String -Path src\GxMcp.Worker\Services\LayoutService*.cs -Pattern 'NotePerTargetWrite'` returns at least 6 occurrences, and each is within a committed (not rolled-back) region.

### Step 3: Add the dirty marks to `Services/Structure/`

Same treatment for `AuthoringService.cs:94`, `AttributeWriteService.cs:103`,
`GroupStructureService.cs:121`, `IndexService.cs:219`,
`VisualStructureService.cs:72`, `DomainWriteService.cs:94`. Place the call
after the `EnsureSave()`/`Save()` that follows a successful mutation. Each of
these methods takes the object name as a parameter (e.g. `objName` in
`AuthoringService.AddExternalMember`) — read the signature.

Verify: `Get-ChildItem src\GxMcp.Worker\Services\Structure -Filter *.cs | Select-String 'NotePerTargetWrite'` returns at least 6 occurrences across at least 6 distinct files.

### Step 4: Write the regression guard

Create `src/GxMcp.Worker.Tests/MutatingToolDirtyMarkTests.cs`. It needs two
kinds of assertion, because a live SDK model is unavailable in unit tests —
this split is the pattern `InspectSectionCompletionTests.cs` uses, and its file
comment explains why:

1. A **source-shape assertion** over the in-scope files: for each of
   `PropertyService.cs`, `LayoutService.cs`, `LayoutService.SourcePersistence.cs`
   and the six `Services/Structure/*.cs` files, assert that
   `SourceAssert.Count(source, "WriteService.NotePerTargetWrite(") >= 1`.
   Use `RepoSource.Read("src", "GxMcp.Worker", "Services", file)`.
2. A **table-driven assertion** naming the mutating tool surfaces and the
   minimum number of marks each must have, so a later refactor that adds a new
   commit site without a mark fails loudly:

   ```csharp
   [Theory]
   [InlineData("PropertyService.cs", 3)]
   [InlineData("LayoutService.cs", 3)]
   [InlineData("LayoutService.SourcePersistence.cs", 3)]
   [InlineData("Structure/AuthoringService.cs", 1)]
   [InlineData("Structure/AttributeWriteService.cs", 1)]
   [InlineData("Structure/GroupStructureService.cs", 1)]
   [InlineData("Structure/IndexService.cs", 1)]
   [InlineData("Structure/VisualStructureService.cs", 1)]
   [InlineData("Structure/DomainWriteService.cs", 1)]
   public void EveryMutatingWriteSurfaceMarksItsTargetDirty(string file, int minimum)
   {
       string source = RepoSource.Read("src", "GxMcp.Worker", "Services", file.Replace('/', Path.DirectorySeparatorChar));
       Assert.True(SourceAssert.Count(source, "WriteService.NotePerTargetWrite(") >= minimum,
           file + " commits SDK writes without marking its target dirty; the next incremental build would ship a stale artifact.");
   }
   ```

3. One more test that pins the *consumer* contract, so the guard is not
   satisfied by marks that are never read: assert that
   `src/GxMcp.Worker/Services/InProcessBuildRunner.cs` still contains
   `"!EditDirtyTracker.IsDirty(kbPath, t)"` and that
   `DefaultFastIncrementalDecision.cs` still contains
   `"EditDirtyTracker.IsDirty(kbPath, t)"`. If either is gone, the fast path
   was removed and this plan's marks are pointless.

Write a file-level comment explaining *why* the guard is source-shape: the
dirty mark and the build decision are separated by an SDK commit that unit
tests cannot perform, so the observable link is the source.

**Verify**: `dotnet test src\GxMcp.Worker.Tests --filter "FullyQualifiedName~MutatingToolDirtyMark" --logger "console;verbosity=minimal"` → all pass, 11+ new tests.

### Step 5: Mutation-check the guard

Revert each of the Step 1–3 edits one at a time (or all at once) and confirm
`MutatingToolDirtyMarkTests` goes red. Then restore. A guard that cannot fail
is not coverage.

**Verify**: with the Step 1–3 edits reverted, the focused test run above must FAIL with your new test's message. Restore and re-run → pass.

### Step 6: Full validation

Run, in order, from the repo root:

```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'
dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal
dotnet test src\GxMcp.Worker.Tests --logger "console;verbosity=minimal"
dotnet build Genexus18MCP.sln -v:minimal
dotnet test src\GxMcp.Gateway.Tests --logger "console;verbosity=minimal"
```

Do **not** run `.\build.ps1` — per `AGENTS.md` it must run last, after the
test lanes, because a solution-level `dotnet test -c Release` rebuilds the
Worker outside the x86 platform the solution maps it to and breaks the
publish↔source byte identity the release fingerprint depends on.

**Verify**: every command exits 0 and the Worker suite has no new failures.

## Test plan

- New file: `src/GxMcp.Worker.Tests/MutatingToolDirtyMarkTests.cs`
  - `EveryMutatingWriteSurfaceMarksItsTargetDirty` — 9 cases, one per write
    surface, each asserting a minimum mark count.
  - `TheIncrementalBuildStillConsultsTheDirtyTracker` — 2 assertions pinning
    the two consumer sites.
- Structural pattern: `src/GxMcp.Worker.Tests/InspectSectionCompletionTests.cs`
  (source-shape guard with a documented reason) and
  `src/GxMcp.Worker.Tests/WriteDirtyOutcomeTests.cs` (table-driven theory over
  write outcomes).
- Helper conventions: `SourceAssert.Count`, `RepoSource.Read`,
  `RepoSource.CsFilesIn` from `src/TestSupport/`.
- Verification: the focused filter above → all pass. Then the full Worker
  suite → no new failures.

## Done criteria

ALL must hold:

- [ ] `Select-String -Path src\GxMcp.Worker\Services\PropertyService.cs -Pattern 'NotePerTargetWrite'` returns 3 matches.
- [ ] `Get-ChildItem src\GxMcp.Worker\Services\Structure -Filter *.cs | Select-String 'NotePerTargetWrite'` spans ≥ 6 distinct files.
- [ ] `Select-String -Path src\GxMcp.Worker\Services\LayoutService*.cs -Pattern 'NotePerTargetWrite'` returns ≥ 6 matches.
- [ ] `src/GxMcp.Worker.Tests/MutatingToolDirtyMarkTests.cs` exists, with ≥ 11 test cases, and all pass.
- [ ] Mutation check performed and recorded: reverting the production edits makes the new tests red.
- [ ] `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` exits 0.
- [ ] `dotnet test src\GxMcp.Worker.Tests` exits 0 with no new failures.
- [ ] `dotnet build Genexus18MCP.sln -v:minimal` exits 0.
- [ ] `CHANGELOG.md` has an entry under `## Unreleased` → `### Fixed`.
- [ ] `git status --short` shows no modified file outside the in-scope list.

## STOP conditions

Stop and report back (do not improvise) if:

- `WriteService.NotePerTargetWrite` no longer has the signature
  `(string target)` or is no longer `internal static`, or
  `EditDirtyTracker.MarkDirty` no longer takes `(kbPath, objectName)`.
- A commit site in scope turns out to already mark dirty through some other
  path you did not find — report which path rather than adding a second mark.
- Any in-scope write method turns out to have no in-scope variable holding the
  KB object name, such that the mark would need a new plumbing parameter. Report
  the method; do not add parameters.
- `dotnet test src\GxMcp.Worker.Tests` fails on a test unrelated to your change
  and the failure is not explained by your diff.
- The fix appears to require modifying a file listed as out of scope.

## Maintenance notes

- What interacts with this: **any new mutating write surface in the Worker**.
  The convention after this plan is that a write which commits through the SDK
  must call `WriteService.NotePerTargetWrite`. A future write path that omits it
  silently reintroduces the stale-artifact defect, which is why
  `MutatingToolDirtyMarkTests` is table-driven rather than a fixed list — add
  a row when you add a surface.
- A reviewer should scrutinise: that every added call sits on the committed
  path, not the rollback path; and that no call was added to a read-only method
  that happens to sit near a `Save()`.
- Explicitly deferred: `PropertyService` does not call
  `ObjectService.MarkReadCacheDirty` after a property write, which is a
  read-cache-consistency question distinct from the build one. It is not fixed
  here and is not a regression.
- The four Worker surfaces are not the only mutating surfaces — the
  `WwpActionService.*` partials and `PatchService` already mark correctly. This
  plan closes the four that did not; it does not audit for a fifth.
