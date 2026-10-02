# Plan 114: Remove the victim's own entry in WorkerPool eviction, not a replacement's

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

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `0f0d71a8`, 2026-10-01

## Why this matters

`WorkerPool` removes entries from its concurrent dictionary at nine sites. Two
of them deliberately use the conditional `TryRemove(KeyValuePair<string,
Entry>)` overload so a concurrent removal-and-reinstall of the same alias cannot
be undone by a stale caller. `EvictEntry` — the one that actually stops a
running `GxMcp.Worker.exe` — uses the plain key overload.

The window is small but the consequence is not: `SelectVictim()` runs under
`_capacityLock`, but the worker's `OnWorkerExited` handler removes from
`_entries` **without** taking that lock, and `AcquireAsync`'s `GetOrAdd` does
not take it either. If the victim's worker exits and a concurrent
`AcquireAsync` installs a fresh entry for the same alias between selection and
eviction, `EvictEntry` removes the **replacement** and never stops its worker.
The result is an untracked `GxMcp.Worker.exe`: invisible to `ListOpen()`,
`Snapshot()`, `whoami`, `IsAtCapacity()`, and unreapable by `StopAll()` on
shutdown. It survives gateway exit holding KB file locks until its own idle
timer fires.

## Current state

`src/GxMcp.Gateway/WorkerPool.cs`:

- `:684-688` — the defect:

```csharp
        private void EvictEntry(Entry entry)
        {
            try { entry.Worker?.StopWithReason(WorkerStopReason.ExplicitClose); } catch { }
            _entries.TryRemove(entry.Handle.NormalizedAlias, out _);
        }
```

- The two siblings that get it right:
  - `:306` `_entries.TryRemove(new KeyValuePair<string, Entry>(capturedHandle.NormalizedAlias, entry));`
  - `:357` `_entries.TryRemove(new KeyValuePair<string, Entry>(handle.NormalizedAlias, entry));`
- The plain-overload sites: `:275` (capacity-full bail-out, before the spawn),
  `:333` (startup-failure cleanup), `:502`, `:503`, `:504`, `:520`, `:537`
  (these last are on `_known` / `_startupFailures` or are guarded by a
  preceding `TryRemove` probe — read each before deciding it is in scope).
- `:265-278` — the capacity path: `SelectVictim()` then `EvictEntry(victim)`
  inside `lock (_capacityLock)`; if the count is still at/over the cap it
  removes the key it is about to spawn and throws `WorkerPoolFullException`.
- `:201` `AcquireAsync`'s `GetOrAdd` does not take `_capacityLock`.
- Test seams that already exist and that you should reuse rather than add new
  ones: `SpawnFactoryForTest` (`:73`, used at `:283`), `SelectEvictableVictimForTest`
  (around `:660`), `SetInFlightForTest` (around `:668`).
- Tests: `src/GxMcp.Gateway.Tests/WorkerPoolTests.cs` covers
  `SelectVictimForTest` and `BeginCommand` (`:27-136`) but nothing about
  `EvictEntry` removing a replacement, and nothing that asserts `StopAll()`
  reaches a worker whose entry was removed underneath it.

Repo conventions:

- Worker-stop reasons are explicit (`WorkerStopReason.ExplicitClose`,
  `SdkCompatibilityRejected`, …) — do not add a new one without need.
- Every regression guard gets a mutation check (`AGENTS.md`).

## Commands you will need

| Purpose | Command | Expected on success |
|-----------|---------|---------------------|
| Build Gateway | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | exit 0 |
| Focused tests | `dotnet test src\GxMcp.Gateway.Tests --filter "FullyQualifiedName~WorkerPool" --logger "console;verbosity=minimal"` | all pass |
| Full Gateway suite | `dotnet test src\GxMcp.Gateway.Tests --logger "console;verbosity=minimal"` | all pass (880+ before) |
| Solution build | `dotnet build Genexus18MCP.sln -v:minimal` | exit 0 |

`MSB3027`/`MSB3021` naming the Gateway/Worker exe → documented scoped
permission: `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`, retry.
Force `-t:Rebuild` after a compile failure. Do **not** run `.\build.ps1`.

## Scope

**In scope**:

- `src/GxMcp.Gateway/WorkerPool.cs`
- `src/GxMcp.Gateway.Tests/WorkerPoolEvictionIdentityTests.cs` (create)

**Out of scope** (do NOT touch):

- `src/GxMcp.Gateway/WorkerProcess.cs` — the orphan's lifetime is a
  consequence, not the fix target.
- The capacity policy itself: which entry is chosen as a victim, the
  `MaxOpenKbs` cap, the busy-worker eligibility rules. Those were hardened
  deliberately and are correct.
- `WorkerPoolFullException` and the bail-out at `:275` — its plain
  `TryRemove` is a different situation (removing the entry it is about to
  occupy) and is not part of this defect. Leave it.
- `WorkerSupervisor.cs`, `CrashLedger.cs`.

## Git workflow

- Branch `advisor/114-workerpool-evict-identity`, commit in your worktree.
- Prose commit subject, e.g.
  `"Remove the evicted entry itself, not the one that replaced it"`.
- `CHANGELOG.md` under `## Unreleased` → `### Fixed`, same commit.
- Do NOT push, open a PR, or merge.

## Steps

### Step 1: Use the conditional overload in `EvictEntry`

Change `:687` to match `:306` and `:357`:

```csharp
            _entries.TryRemove(new KeyValuePair<string, Entry>(entry.Handle.NormalizedAlias, entry));
```

Read the surrounding code to get the exact `KeyValuePair<string, Entry>`
construction used at `:306`/`:357` — the alias source differs
(`capturedHandle.NormalizedAlias` vs `entry.Handle.NormalizedAlias`), so match
what this method actually holds.

Add a one-line comment stating the reason, matching the tone of the existing
comments at those two sites: the conditional form is what prevents evicting a
concurrent replacement that took the alias after the victim's worker exited.

### Step 2: Make the orphan case unreachable as a backstop

The conditional removal is the fix. Additionally, verify that a worker whose
entry was removed cannot persist: check whether `EvictEntry` stopping
`entry.Worker` is the only reference to that `WorkerProcess`, and whether
`StopAll()` iterates `_entries` (it appears to, around `:502-537`). If a
`WorkerProcess` can be live with no entry referencing it, report it as a STOP
condition with the reference you found — do not add a registry to fix it in
this plan.

### Step 3: Write the regression test

Create `src/GxMcp.Gateway.Tests/WorkerPoolEvictionIdentityTests.cs`. Use
`SpawnFactoryForTest` (`:73`) so no real process is started. The test must
reproduce the interleaving deterministically rather than relying on a race.

The reliable way to do that is to drive the state directly: use the existing
seams to (a) get an entry into `_entries` for an alias, (b) replace it with a
different `Entry` instance for the same alias — which is exactly what a
`GetOrAdd` after an exit does — then (c) invoke eviction for the **first**
(stale) entry and assert that the **second** survives in the pool.

If `EvictEntry` is private and has no test seam, prefer adding a minimal
`internal` seam next to `SelectEvictableVictimForTest`/`SetInFlightForTest`
(they establish the precedent) rather than reaching in by reflection. Keep the
seam as narrow as those two are.

Cases:

1. `EvictingAStaleEntryDoesNotRemoveAReplacement` — the core regression. The
   replacement must still be reachable in `_entries` afterwards (assert via
   whatever read path exists, e.g. `ListOpen()` or `IsAtCapacity()`).
2. `EvictingTheCurrentEntryStillRemovesIt` — the ordinary case still works, so
   the fix is not "never remove".
3. `AWorkerWhoseEntryWasReplacedIsNotStoppedByTheStaleEviction` — assert the
   replacement's worker is not stopped. This is the half that matters: the
   bug is not just a lost dictionary entry, it is a lost process handle.
4. `StopAllReachesEveryStillRegisteredEntry` — if `StopAll()` iterates
   `_entries`, assert it stops each registered worker, so the orphan scenario
   is bounded by the real shutdown path.

### Step 4: Mutation-check every new test

Revert Step 1 to the plain overload and confirm case 1 (and case 3) go red.
Record the result. A guard that cannot fail is not coverage.

**Verify**: with the plain overload restored, `EvictingAStaleEntryDoesNotRemoveAReplacement` FAILS.

### Step 5: Full validation

```powershell
dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal
dotnet test src\GxMcp.Gateway.Tests --logger "console;verbosity=minimal"
dotnet build Genexus18MCP.sln -v:minimal
```

**Verify**: all exit 0, no new failures.

## Test plan

- New file: `src/GxMcp.Gateway.Tests/WorkerPoolEvictionIdentityTests.cs`
  - 4 cases as above.
- Structural pattern: `src/GxMcp.Gateway.Tests/WorkerPoolTests.cs`
  (`SpawnFactoryForTest` usage, `BeginCommand`/in-flight assertions).
- Verification: focused filter → all pass; full suite → no new failures.

## Done criteria

- [ ] `EvictEntry` uses `TryRemove(KeyValuePair<string, Entry>)`.
- [ ] The reason is stated in a comment at the call site.
- [ ] Cases 1–4 exist and pass.
- [ ] The mutation (reverting to the plain overload) makes case 1 red.
- [ ] No capacity-policy, victim-selection or busy-worker logic was changed.
- [ ] `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` exits 0.
- [ ] `dotnet test src\GxMcp.Gateway.Tests` exits 0, no new failures.
- [ ] `dotnet build Genexus18MCP.sln -v:minimal` exits 0.
- [ ] `CHANGELOG.md` has an entry under `## Unreleased` → `### Fixed`.
- [ ] `git status --short` shows nothing outside the in-scope list.

## STOP conditions

Stop and report back if:

- Reproducing the interleaving deterministically requires holding
  `_capacityLock` from a test, i.e. the seams cannot express the state. Report
  what you tried rather than adding a lock-order change.
- You find that `EvictEntry` cannot be given a test seam without changing its
  visibility in a way that widens the public surface. Report it.
- You find that a `WorkerProcess` genuinely can outlive its `_entries` entry
  with nothing tracking it. Report the reference path — that is a larger
  problem than this plan and needs its own decision.
- The fix appears to require modifying a file listed as out of scope.

## Maintenance notes

- Interacts with: any future change to `SelectVictim` or the capacity lock. The
  conditional removal is only correct because `EvictEntry` is the sole place
  that stops a worker for capacity reasons; if a second eviction path is added,
  it needs the same overload.
- A reviewer should scrutinise: whether the test actually reproduces the
  replacement (not just a stale-key removal), and whether it asserts on the
  worker's liveness, not only on dictionary contents.
- Note the neighbouring decision: the plain overload at `:275` is correct as
  written because it removes the alias the caller is about to occupy. Do not
  "fix" it in a follow-up without that reasoning.
