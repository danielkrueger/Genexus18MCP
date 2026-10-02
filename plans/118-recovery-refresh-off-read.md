# Plan 118: Move the mutation-recovery journal refresh off the read path

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
- **Effort**: M
- **Risk**: MED
- **Depends on**: none
- **Category**: perf
- **Planned at**: commit `0f0d71a8`, 2026-10-01

## Why this matters

Every `genexus_read` call unconditionally calls `_mutationRecovery.Refresh()`.
`Refresh` takes an exclusive cross-process file lock with `FileShare.None`,
retries with a sleep for up to ~2 seconds **while holding the registry's own
`_journalLock`**, then re-reads and re-parses the whole journal and enumerates
a directory. The product's hottest read pays a cross-process lock, a full file
parse and a directory glob on every call — and when two Gateways or any writer
is contending, up to a 2-second stall per read with every other registry
operation blocked behind it.

The registry's own comment says the refresh belongs at the write gate, which
is where the unconditional refresh also happens. The read-path call was added
so a fence written by *another* process is visible; that need is real and must
be preserved.

## Current state

- `src/GxMcp.Gateway/Program.ToolDispatch.cs:229` — inside
  `if (string.Equals(tName, "genexus_read", ...))`:
  `_mutationRecovery.Refresh();` before the recovery-fence scan at `:240`.
- `src/GxMcp.Gateway/Program.ToolDispatch.cs:158` — the *other* call site, on
  the mutating path: `if (isMutating && !IsMutationPreview(tArgs)) _mutationRecovery.Refresh();`
  followed by the `IsHealthy` gate at `:159-168` and the per-target
  `TryGet` scan at `:172-179`. **This one is the write gate and must stay
  unconditional.**
- `src/GxMcp.Gateway/MutationRecoveryRegistry.cs:444-455` — `Refresh` takes
  `lock (_journalLock)` and then calls `AcquireJournalLock()`.
- `:534-554` — `AcquireJournalLock` opens `_journalPath + ".lock"` with
  `FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None`; under
  contention it `Thread.Sleep(20)` in a bounded loop up to ~2000 ms, while
  `_journalLock` is held.
- `:564-578` — `ReloadTrustedJournal` does `File.ReadAllText` +
  `JToken.Parse` of the whole journal, plus a `Directory.GetFiles` glob, then
  rebuilds `_pending`.
- `:442-443` — the comment stating the refresh exists because several gateways
  share the path.
- The consumers of the refreshed state: `Program.ToolDispatch.cs:240-247`
  (`FindForRead` → `requireAuthoritativeRead`, and `isLiveTool |= !IsHealthy ||
  Count > 0`), plus `:174` `TryGet`.

Repo conventions:

- Keep the registry's own `IMonotonicClock`-style injection if it exists for
  the lock retry; do not introduce `DateTime.UtcNow` inline.
- Response shapes are frozen; this plan changes only *when* state is read, not
  what is returned.
- Every regression guard gets a mutation check (`AGENTS.md`).

## Commands you will need

| Purpose | Command | Expected on success |
|-----------|---------|---------------------|
| Build Gateway | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | exit 0 |
| Focused tests | `dotnet test src\GxMcp.Gateway.Tests --filter "FullyQualifiedName~MutationRecovery\|FullyQualifiedName~ToolDispatch\|FullyQualifiedName~SemanticCache" --logger "console;verbosity=minimal"` | all pass |
| Full Gateway suite | `dotnet test src\GxMcp.Gateway.Tests --logger "console;verbosity=minimal"` | all pass (880+ before) |
| Solution build | `dotnet build Genexus18MCP.sln -v:minimal` | exit 0 |

`MSB3027`/`MSB3021` naming the Gateway/Worker exe → documented scoped
permission: `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`, retry.
Force `-t:Rebuild` after a compile failure. Do **not** run `.\build.ps1`.

## Scope

**In scope**:

- `src/GxMcp.Gateway/MutationRecoveryRegistry.cs`
- `src/GxMcp.Gateway/Program.ToolDispatch.cs`
- `src/GxMcp.Gateway.Tests/MutationRecoveryRefreshPolicyTests.cs` (create)

**Out of scope** (do NOT touch):

- `src/GxMcp.Gateway/MutationOperationJournal.cs` — the operation journal is a
  different file with different locking; plan 111 changes it.
- The write-gate call at `Program.ToolDispatch.cs:158` and everything from
  `:159-179`. It must remain unconditional.
- `SemanticCacheStore.cs`, `IdempotencyCache.cs`, `HttpSessionRegistry.cs` —
  other per-request scans, tracked separately, out of scope here.
- Any change to the envelope or to `requireAuthoritativeRead` semantics.

## Git workflow

- Branch `advisor/118-recovery-refresh-off-read`, commit in your worktree.
- Prose commit subject, e.g.
  `"Stop charging every read a cross-process journal lock"`.
- `CHANGELOG.md` under `## Unreleased` → `### Changed` (a latency change, not
  a bug fix — it must not read as a fix for a staleness bug it does not fix).
- Do NOT push, open a PR, or merge.

## Steps

### Step 1: Add a change signal the read path can consult cheaply

The read path needs to know "has the journal changed since I last looked?"
without taking the lock. Implement a cheap staleness check:

- Subscribe to or poll the journal file's `LastWriteTimeUtc` (and its length),
  and compare against the values captured at the last successful load. A
  `File.GetLastWriteTimeUtc` is a metadata read that does **not** take the
  `FileShare.None` lease, so it does not block a concurrent writer.
- Cache the captured values on the registry alongside the loaded state, and
  expose them as part of whatever internal "loaded" bookkeeping already
  exists.
- **Do not** treat the timestamp alone as sufficient if the file can be
  replaced (a `File.Replace` changes the inode on some systems). Combine
  timestamp **and** length, and state that in a comment. If you believe
  timestamp+length is insufficient, report it as a STOP condition with the
  reason rather than shipping a weaker check.

### Step 2: Add a conditional refresh and use it on the read path

- Add a method (e.g. `RefreshIfChanged()`) that performs the existing
  `Refresh()` body **only** when the change signal says the journal moved, and
  returns without taking the lock otherwise.
- `Refresh()` itself keeps its current unconditional behaviour; the write gate
  at `Program.ToolDispatch.cs:158` keeps calling it.
- Change `:229` to call the conditional variant.
- Add a comment at both call sites saying which is which, so a future reader
  does not "simplify" the read path back to unconditional.

### Step 3: Bound the lock wait so it cannot stall a read

While the lease is held, the retry loop sleeps up to ~2 s. On the read path
that is a 2-second stall for a *cache-freshness check*. Reduce the bound for
the read path specifically, or make `RefreshIfChanged` fail-open (skip the
reload this call and let the next call pick it up) when the lease is not
obtained within a much smaller budget. Failing open is safe **only** because
the conservative behaviour — treating the fence as unresolved and forcing an
authoritative read — is the fail-safe direction. Verify that reading
`requireAuthoritativeRead` and `isLiveTool` confirms this before choosing it,
and report which direction you verified.

Use a named constant for the bound in the style of
`WorkerPool.WarmSpareAwaitCap`.

### Step 4: Write the regression tests

Create `src/GxMcp.Gateway.Tests/MutationRecoveryRefreshPolicyTests.cs`.

1. `AReadWithAnUnchangedJournalTakesNoCrossProcessLease` — the core
   performance regression. Construct the registry over a temp journal, do an
   initial load, then assert a second conditional refresh does **not** take the
   lease. Assert on the lease, not on elapsed time — a timing assertion is
   flaky. The way to observe it: hold the `.lock` file yourself with
   `FileShare.None` and assert the conditional refresh returns quickly and does
   not block, while an unconditional `Refresh()` in the same state does block.
   Use the reflection pattern `Issue192LeaseRecoveryTests.cs:257` uses to reach
   privates if needed.
2. `AJournalChangedByAnotherProcessIsStillPickedUpOnTheReadPath` — the safety
   property, and the one that matters more than test 1. Write to the journal
   from outside the registry, then assert a conditional refresh on the read
   path **does** observe the new fence. Without this, the optimization silently
   serves a stale answer and the test suite stays green.
3. `TheWriteGateStillRefreshesUnconditionally` — a source-shape assertion over
   `Program.ToolDispatch.cs` that the mutating branch still calls the
   unconditional `Refresh()` and the `genexus_read` branch calls the
   conditional one. Use `RepoSource.Read` + `SourceAssert.Count` from
   `src/TestSupport/`. Model on `ArgvQuotingTests.cs`
   (`TheQuotingPrimitiveLivesInOnePlaceAndEveryCallerUsesIt`).
4. `AContendedReadDoesNotStallForSeconds` — hold the lease and assert the
   read-path variant returns within a documented bound. Use a generous bound
   (e.g. 1 second, not 100 ms) so the test is not flaky on a loaded machine,
   and compare against the unconditional path's behaviour in the same test.

### Step 5: Mutation-check every new test

Make `RefreshIfChanged` always take the lease (i.e. revert it to
`Refresh`) and confirm test 1 fails. Make it **never** reload (return
immediately, ignoring the change signal) and confirm test 2 fails. Record
both.

**Verify**: with the change signal ignored, test 2 FAILS.

### Step 6: Full validation

```powershell
dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal
dotnet test src\GxMcp.Gateway.Tests --logger "console;verbosity=minimal"
dotnet build Genexus18MCP.sln -v:minimal
```

**Verify**: all exit 0, no new failures.

## Test plan

- New file: `src/GxMcp.Gateway.Tests/MutationRecoveryRefreshPolicyTests.cs`
  - 4 cases as above.
- Structural pattern: the existing recovery-registry tests for the temp
  journal fixture; `ArgvQuotingTests.cs` for the source-shape idiom.
- Verification: focused filter → all pass; full suite → no new failures.

## Done criteria

- [ ] A cheap change signal exists that does not take the `FileShare.None` lease.
- [ ] The read path uses a conditional refresh; the write gate keeps the unconditional one.
- [ ] The read path's lock wait is bounded well below the ~2 s current cap, or fails open in the fail-safe direction (state which, and why that direction is safe).
- [ ] A journal changed by another process is still observed by the next read.
- [ ] No envelope or `requireAuthoritativeRead` semantics changed.
- [ ] 4 tests exist and pass; both mutations make them red.
- [ ] `MutationOperationJournal.cs` and `IdempotencyCache.cs` are untouched.
- [ ] `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` exits 0.
- [ ] `dotnet test src\GxMcp.Gateway.Tests` exits 0, no new failures.
- [ ] `dotnet build Genexus18MCP.sln -v:minimal` exits 0.
- [ ] `CHANGELOG.md` has an entry under `## Unreleased` → `### Changed`.
- [ ] `git status --short` shows nothing outside the in-scope list.

## STOP conditions

Stop and report back if:

- Timestamp+length is insufficient to detect a change reliably given how the
  journal is written (`File.Replace` at `MutationOperationJournal.cs:423` is a
  whole-file replace). Report the concern rather than shipping a check that can
  miss a write.
- The fail-open direction is **not** the safe one — i.e. if skipping the reload
  would let a read serve a cached answer while an unresolved write fence exists.
  Verify by reading `requireAuthoritativeRead` and `isLiveTool` at
  `Program.ToolDispatch.cs:240-247` and report what you found before choosing.
- Detecting a change requires a per-write notification that only the *other*
  file (`MutationOperationJournal.cs`) could provide. Report that; do not edit
  it — plan 111 owns it.
- The fix appears to require modifying a file listed as out of scope.

## Maintenance notes

- Interacts with: plan 111 (same subsystem, different file) and with any future
  multi-process topology. The two call sites are deliberately different, and
  that asymmetry needs a comment or the next reader will unify them.
- A reviewer should scrutinise test 2 far more than test 1. Test 1 is a
  performance claim; test 2 is the correctness claim. A change that keeps test 1
  green while breaking test 2 has made the product wrong and fast.
- Deferred, tracked separately: `HttpSessionRegistry.ActiveSessions` and
  `SessionKbContextStore.CleanupExpired` also do O(n) scans per request/frame,
  and `Program.WorkerLifecycle.FindPendingForOperation` scans all pending
  requests per progress frame. Those are real and independent; do not fold them
  into this plan.
