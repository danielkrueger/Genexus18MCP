# Plan 111: Give the durable mutation-operation journal cross-process coordination

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
- **Effort**: M
- **Risk**: MED
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `0f0d71a8`, 2026-10-01

## Why this matters

The mutation-operation journal is the crash fence that stops a retried
idempotency key from replaying a mutation that may already have committed. It
is a single file on disk, but the Gateway deliberately runs as **multiple
processes**: `stdio-isolated` mode gives every AI client its own Gateway, and
legacy HTTP mode runs a master plus proxies. Today that file has only an
in-process `lock`. Two Gateways both persisting the same path is
last-writer-wins: process B writes a `started` fence, process A's next
`Begin` serialises its own stale in-memory entry set over the whole file, and B's
fence is gone. A crash after B's `Begin` then reads back as "never started" and
the same key replays.

The fix pattern already exists in this repository, ninety lines away:
`MutationRecoveryRegistry` takes a cross-process `.lock` lease with
`FileShare.None` and reloads inside it. This plan makes the operation journal
use the same discipline.

## Current state

Key files:

- `src/GxMcp.Gateway/MutationOperationJournal.cs` (the file to change) —
  `:26` `private readonly object _gate = new object();` is the only mutual
  exclusion. `:397-426` `PersistLocked()` serialises the **entire**
  `_entries` dictionary to a temp file, `Flush(true)`s it, and
  `File.Replace`s the whole thing. `:41` loads entries once, in the
  constructor. `Begin` is reached from
  `src/GxMcp.Gateway/IdempotencyCache.cs:194-198` → `_journal.Begin(...)`, and
  it consults only that in-memory snapshot (`:194-198` calls
  `MutationOperationJournal.Begin`, which reads `_entries`).
- `src/GxMcp.Gateway/MutationRecoveryRegistry.cs` — the **exemplar**. `:544`
  `var lease = new FileStream(_journalPath + ".lock", FileMode.OpenOrCreate,
  FileAccess.ReadW...` opens the sibling lock file; under contention it
  `Thread.Sleep(20)` in a bounded retry loop (up to ~2000 ms) while holding its
  own `_journalLock`. `ReloadTrustedJournal` (`:564-578`) then re-reads and
  re-parses under the lease. Read that method and match its shape.
- `src/GxMcp.Gateway/Program.cs:335` and `:831` — the production construction
  site, `Path.Combine(AppContext.BaseDirectory, "state",
  "mutation-operations.json")`, one fixed path per installed exe, no scope
  token. Two Gateways of the same install therefore target the same file. This
  is by design (the fence must be shared across processes); it is the
  *coordination* that is missing.

The consequence to preserve: `CHANGELOG.md` states "A completed or interrupted
entry cannot be replayed blindly after Gateway restart". That property is what
the cross-process lease restores — a `started` record written by another
process must be visible before `Begin` decides its result.

Repo conventions:

- Structured error envelopes; a refusal is a deliberate design feature.
- Tests: `src/GxMcp.Gateway.Tests/MutationOperationJournalTests.cs` and
  `ScopedReceiptPersistenceTests.cs` (the latter at `:18-26` covers
  single-instance restart read-back — the closest existing pattern). Read both
  before writing tests.
- Time is injected: look for an existing `IMonotonicClock` /
  clock-abstraction seam in the Gateway (used by `KbUseLeaseRegistry`) and reuse
  it for any new timeout rather than calling `DateTime.UtcNow` inline.
- Every regression guard gets a mutation check (`AGENTS.md`).

## Commands you will need

| Purpose | Command | Expected on success |
|-----------|---------|---------------------|
| Build Gateway | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | exit 0 |
| Focused tests | `dotnet test src\GxMcp.Gateway.Tests --filter "FullyQualifiedName~MutationOperationJournal\|FullyQualifiedName~Idempotency\|FullyQualifiedName~ScopedReceipt" --logger "console;verbosity=minimal"` | all pass |
| Full Gateway suite | `dotnet test src\GxMcp.Gateway.Tests --logger "console;verbosity=minimal"` | all pass (880+ before this change) |
| Solution build | `dotnet build Genexus18MCP.sln -v:minimal` | exit 0 |

If a build fails with `MSB3027`/`MSB3021` naming `GxMcp.Gateway.exe` or
`GxMcp.Worker.exe`, that is the documented scoped-permission case: run
`Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force` and retry. After any
compile failure force `-t:Rebuild` before trusting a test result.

Do **not** run `.\build.ps1` (`AGENTS.md`: it must run last).

## Scope

**In scope**:

- `src/GxMcp.Gateway/MutationOperationJournal.cs`
- `src/GxMcp.Gateway/MutationOperationJournalCrossProcessTests.cs` (create)

**Out of scope** (do NOT touch):

- `src/GxMcp.Gateway/MutationRecoveryRegistry.cs` — it is the exemplar to
  copy, not a file to change. Do not "share" the helper by editing it; if you
  believe it must change, that is a STOP condition.
- `src/GxMcp.Gateway/IdempotencyCache.cs` — its call sites are correct as-is.
  Plan 112 changes its exception handling; do not do that here.
- `Program.cs` — the fixed path is intentional. Do not add a per-process suffix
  to the journal path: that would make the fence per-process and destroy the
  property this plan restores.
- The on-disk format. Entries, statuses and the `MaxEntries = 4096` /
  `MaxBytes = 2 MB` ceilings stay exactly as they are. `Load()`'s fail-closed
  semantics are load-bearing.

## Git workflow

- Branch `advisor/111-journal-cross-process`, commit in your worktree.
- Prose commit subjects matching the repo, e.g.
  `"Give the mutation journal the cross-process lock its sibling already has"`.
- Add a `CHANGELOG.md` entry under `## Unreleased` → `### Fixed`, same commit.
  State the concrete failure (a second Gateway's `started` fence erased by a
  first Gateway's `Begin`) and what changed.
- Do NOT push, open a PR, or merge.

## Steps

### Step 1: Add a cross-process lease to `MutationOperationJournal`

Mirror `MutationRecoveryRegistry`'s lease. Requirements:

- The lock path is `this journal path + ".lock"`.
- Open with `FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None` so a
  second process cannot hold it at the same time.
- Retry with a bounded loop and a short sleep between attempts, and bound the
  total wait. Use a named constant for the cap, in the style of
  `WorkerPool.WarmSpareAwaitCap` or `AnalyzeService.InspectSectionBudget`
  (both are `internal static readonly TimeSpan` with a comment explaining the
  bound). Do not inline a magic number.
- On timeout, fail **closed**: return the existing `BeginResult
  .JournalUnavailable` from `Begin` rather than proceeding unlocked. That
  result already exists and `IdempotencyCache.cs:151-155` turns it into the
  `operation_journal_unavailable` envelope telling the caller the write was not
  executed. This is the same fail-closed choice `MutationRecoveryRegistry`
  makes.
- Take the lease **around** the read-modify-write, and the in-process `_gate`
  **inside** the lease. That order (process lock outermost) is what prevents a
  deadlock and must match `MutationRecoveryRegistry`.

### Step 2: Reload on-disk entries inside the lease before deciding `Begin`

Currently `Begin` decides `Completed` / `Conflict` / `UnknownAfterRestart`
from the in-memory `_entries` loaded once at construction. Under the lease:

1. Reload the file into `_entries` (there is already a load path — reuse it
   rather than writing a second parser).
2. Then perform the existing `Begin` logic unchanged.

Preserve `Load()`'s behaviour on a corrupt file exactly: whatever it does today
(take the fail-closed path), keep doing that. Do not swallow a load error and
continue with an empty set — that would convert "unavailable" into "no
conflict", which is the opposite of safe.

Add a comment naming the reason in one sentence: the in-memory snapshot can be
stale because another process persists the same file.

### Step 3: Hold the lease in `PersistLocked`'s callers

`PersistLocked` is called from `Begin`, `Complete` and `Fail` (or equivalent).
Ensure every path that persists also holds the lease for the read-modify-write,
so the sequence "reload → decide → persist" is atomic as a whole. A lease
acquired only inside `Load` would leave the window open.

### Step 4: Write the cross-process regression test

Create `src/GxMcp.Gateway.Tests/MutationOperationJournalCrossProcessTests.cs`.
The defect is invisible to a single-instance test, so the test must use **two**
`MutationOperationJournal` instances over the same path, which is exactly the
shape the existing suite is missing.

Cases:

1. `AStartedFenceWrittenByOneInstanceIsVisibleToAnother` — construct two
   journals on the same temp path. Instance A calls `Begin` (leaving the record
   `started`). Instance B then calls `Begin` for the **same** key and must NOT
   report `JournalUnavailable` or treat the key as absent; assert the result is
   the one the existing state implies (`Conflict` or
   `UnknownAfterRestart` — read the code and assert the actual correct
   classification, do not guess).
2. `AConcurrentWriterCannotEraseAnotherInstancesStartedRecord` — interleave
   A.Begin, B.Begin (different key), A.Complete, and assert the file on disk
   still contains both records. Parse the file and assert on the actual JSON,
   not on a return value.
3. `AnUnheldLeaseFailsClosedRatherThanProceeding` — hold the `.lock` file open
   yourself (`FileShare.None`) in the test, then call `Begin` and assert it
   returns `JournalUnavailable` and that **no** entry was written. This is the
   test that proves the fence fails closed rather than silently unlocking.
4. `AnUnreadableOrCorruptJournalStillFailsClosed` — confirm the pre-existing
   fail-closed path still holds after your reload change.

Use a unique temp directory per test (see how
`ScopedReceiptPersistenceTests.cs` creates its fixtures) and delete it in a
`finally`. Tests must not share a path.

### Step 5: Mutation-check every new test

Revert each production edit individually and confirm the corresponding test
goes red. Specifically: undo the reload-in-`Begin` step and test 1 must fail;
undo the lease and test 2 and 3 must fail. Record which mutation killed which
test in your report.

**Verify**: with the Step 2 reload removed, `AStartedFenceWrittenByOneInstanceIsVisibleToAnother` fails. With the Step 1 lease removed, `AnUnheldLeaseFailsClosedRatherThanProceeding` fails.

### Step 6: Full validation

```powershell
dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal
dotnet test src\GxMcp.Gateway.Tests --logger "console;verbosity=minimal"
dotnet build Genexus18MCP.sln -v:minimal
dotnet test src\GxMcp.Worker.Tests --logger "console;verbosity=minimal"
```

**Verify**: all exit 0, no new failures in either suite.

## Test plan

- New file: `src/GxMcp.Gateway.Tests/MutationOperationJournalCrossProcessTests.cs`
  - 4 cases as specified above.
- Structural pattern: `src/GxMcp.Gateway.Tests/ScopedReceiptPersistenceTests.cs`
  (temp-path fixture, single-instance persistence assertions) and
  `MutationOperationJournalTests.cs` (status/result vocabulary — reuse its
  result enum names exactly).
- Verification: focused filter → all pass; full Gateway suite → no new failures.

## Done criteria

- [ ] `MutationOperationJournal` opens `<journalPath>.lock` with `FileShare.None`.
- [ ] `Begin` reloads on-disk entries while holding that lease before deciding its result.
- [ ] The lease is held across reload → decide → persist, not only around the read.
- [ ] Lease-acquisition failure returns `BeginResult.JournalUnavailable` and writes nothing.
- [ ] A corrupt journal still takes the pre-existing fail-closed path.
- [ ] No change to the on-disk format, the entry model, or the `MaxEntries`/`MaxBytes` ceilings.
- [ ] `src/GxMcp.Gateway/MutationOperationJournalCrossProcessTests.cs` exists with 4 cases, all passing.
- [ ] Mutation check done and recorded for every production edit.
- [ ] `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` exits 0.
- [ ] `dotnet test src\GxMcp.Gateway.Tests` exits 0, no new failures.
- [ ] `dotnet build Genexus18MCP.sln -v:minimal` exits 0.
- [ ] `CHANGELOG.md` has an entry under `## Unreleased` → `### Fixed`.
- [ ] `git status --short` shows nothing outside the in-scope list.

## STOP conditions

Stop and report back if:

- `MutationRecoveryRegistry`'s lease is not reusable as the pattern described
  (e.g. it is a nested private type with Gateway-wide state that would require
  editing that file to share). Report what sharing would actually cost instead
  of editing it.
- The `BeginResult` enum no longer has a `JournalUnavailable` member, or
  `Load()`'s current corrupt-file behaviour is not fail-closed. Report the
  actual behaviour; do not redefine it.
- Acquiring the lease requires a retry/backoff longer than a tool call's
  timeout budget, such that a normal two-Gateway setup would start rejecting
  writes. Report the numbers.
- You find that `PersistLocked` is called from somewhere that cannot hold the
  lease (e.g. from a finaliser or a static initialiser). Report the call site.
- The fix appears to require modifying a file listed as out of scope.

## Maintenance notes

- Interacts with: any future change to the Gateway's process topology. If a
  third process shape is added, the lease is what keeps the fence correct —
  the path staying fixed is deliberate and must not be "fixed" to be
  per-process.
- A reviewer should scrutinise: lock ordering (process lease outermost,
  `_gate` inside) and the fail-closed path. A lock acquired in the reverse order
  is a deadlock, and a timeout that proceeds unlocked silently removes the
  fence.
- The lease makes `Begin` do file I/O it did not before. If that shows up as
  latency in a later profile, the correct follow-up is a version counter
  written by the lock holder (the same shape as plan 116's read-path change),
  not removing the lease.
- Deferred: `PersistLocked` rewrites the whole journal on every keyed write
  (two rewrites and two fsyncs per mutation, O(journal size)). That is a real
  cost but a separate change, and it is HIGH risk because the file *is* the
  fence. Not in scope.
