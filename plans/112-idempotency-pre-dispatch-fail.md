# Plan 112: Fail the idempotency journal fence on pre-dispatch factory failures

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
- **Risk**: LOW
- **Depends on**: none (independent of plan 111; both may land in either order)
- **Category**: bug
- **Planned at**: commit `0f0d71a8`, 2026-10-01

## Why this matters

`IdempotencyCache.GetOrCompute` writes a `started` fence to the durable journal
before running the mutation, and only clears it on `Complete` or on one
specific exception. Any other exception escapes with the record still
`started`. On the next attempt with the same key, `BeginJournal` maps a
`started` record to `BeginResult.UnknownAfterRestart`, which becomes
`UsageException("operation_unknown")` — "A previous process may have committed
this mutation".

For failures where the mutation provably never reached the SDK, that message is
false. The realistic cases are a full Worker pool, an ambiguous KB, an unknown
action, or a client disconnect mid-dispatch. The caller is told a previous
process may have committed a write that never ran, and the only way out is a
manual journal repair. The fence is doing its job for genuine unknown outcomes
and lying for known ones.

## Current state

- `src/GxMcp.Gateway/IdempotencyCache.cs:158-169`:

```csharp
                try
                {
                    var result = await factory().ConfigureAwait(false);
                    Put(kbPath, tool, key, payloadHash, result);
                    CompleteJournal(kbPath, tool, key, payloadHash);
                    return result;
                }
                catch (ErrorNotCacheable ex)
                {
                    FailJournal(kbPath, tool, key, payloadHash);
                    return ex.Result;
                }
```

  `ErrorNotCacheable` is the only handled type. Everything else propagates.
- `src/GxMcp.Gateway/MutationOperationJournal.cs:91-94` — an existing record
  whose status is `started` maps to `BeginResult.UnknownAfterRestart`.
- `IdempotencyCache.cs:146-150` — that result becomes
  `UsageException("operation_unknown", ...)`.
- Exceptions that are known to reach this boundary and are **not**
  `ErrorNotCacheable`:
  - `UsageException` and `IdempotencyConflictException` are caught *outside*
    `idempotencyMiddleware.Invoke` (`src/GxMcp.Gateway/Program.RequestLoop.cs:637`
    and `:651`) — direct proof that exceptions are expected to escape the
    factory.
  - `WorkerPoolFullException` (`src/GxMcp.Gateway/WorkerPool.cs:276`).
  - `KbResolutionException` (thrown by `src/GxMcp.Gateway/KbResolver.cs:148`
    and `:168`).
  - `OperationCanceledException` from `RequestAborted`.

The distinction that makes this safe: a failure raised **before** the worker
command was written to the pipe means the mutation did not run, so the record
must be failed. A failure raised **after** dispatch means the outcome is
genuinely unknown and the fence must stay `started`. Both behaviours are
correct; today only one of them is implemented.

Repo conventions:

- Read the `UsageException` shape before adding a catch — do not invent a new
  exception type.
- Structured envelopes; do not change any response shape in this plan.
- Every regression guard gets a mutation check (`AGENTS.md`).

## Commands you will need

| Purpose | Command | Expected on success |
|-----------|---------|---------------------|
| Build Gateway | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | exit 0 |
| Focused tests | `dotnet test src\GxMcp.Gateway.Tests --filter "FullyQualifiedName~Idempotency\|FullyQualifiedName~MutationOperationJournal" --logger "console;verbosity=minimal"` | all pass |
| Full Gateway suite | `dotnet test src\GxMcp.Gateway.Tests --logger "console;verbosity=minimal"` | all pass (880+ before) |
| Solution build | `dotnet build Genexus18MCP.sln -v:minimal` | exit 0 |

`MSB3027`/`MSB3021` naming `GxMcp.Gateway.exe`/`GxMcp.Worker.exe` → the
documented scoped permission applies: `Stop-Process -Name
GxMcp.Gateway,GxMcp.Worker -Force`, then retry. Force `-t:Rebuild` after any
compile failure before trusting a test result. Do **not** run `.\build.ps1`.

## Scope

**In scope**:

- `src/GxMcp.Gateway/IdempotencyCache.cs`
- `src/GxMcp.Gateway/IdempotencyFailureJournalTests.cs` (create)

**Out of scope** (do NOT touch):

- `src/GxMcp.Gateway/MutationOperationJournal.cs` — its `Begin`/`Complete`/`Fail`
  contract is correct. Plan 111 changes its locking, not its semantics.
- `src/GxMcp.Gateway/Program.RequestLoop.cs` — where the escaping exceptions are
  caught is not this plan's business.
- Any change to the *content* of the `operation_unknown` message, or to the
  existing `ErrorNotCacheable` behaviour. This plan only adds the missing case.
- `src/GxMcp.Worker/**`.

## Git workflow

- Branch `advisor/112-idempotency-fail-journal`, commit in your worktree.
- Prose commit subject, e.g.
  `"Stop a pre-dispatch failure from poisoning an idempotency key for the process"`.
- `CHANGELOG.md` under `## Unreleased` → `### Fixed`, same commit. State that
  the fence is unchanged for post-dispatch failures and now clears for
  pre-dispatch ones.
- Do NOT push, open a PR, or merge.

## Steps

### Step 1: Add a pre-dispatch failure classification

Add a helper that decides whether an exception means "the mutation provably
did not run". It must be a named, documented predicate — not an inline type
check scattered in a catch block. In the style of
`WorkerLivenessClassifier` / `SdkDiagnosticClassifier` (both exist in
`src/GxMcp.Gateway/` for exactly this shape), or as a `private static bool`
on `IdempotencyCache` if the set is small.

The set to start with, all of which are raised before a worker command is
written:

- `UsageException` — includes `KbResolutionException` if that derives from it;
  **verify the actual hierarchy and do not assume**.
- `WorkerPoolFullException`.
- Argument/validation failures surfaced as `UsageException` or a sibling — read
  the existing exception types in `src/GxMcp.Gateway/UsageException.cs` and
  `GatewayArgsValidator.cs` to enumerate them accurately.

Explicitly **NOT** in the set, because the outcome is genuinely unknown:
`OperationCanceledException` raised after dispatch began, and any exception
thrown once the command has been handed to a worker.

### Step 2: Fail the record on those exceptions

Change the `catch` so it handles both cases explicitly and documents the
distinction in a comment. Shape:

```csharp
                catch (Exception ex) when (IsPreDispatchFailure(ex))
                {
                    // The mutation provably never reached the SDK, so the
                    // 'started' fence is describing a write that did not
                    // happen. Clear it; leaving it poisons this key for the
                    // life of the process with a false operation_unknown.
                    FailJournal(kbPath, tool, key, payloadHash);
                    throw;
                }
```

Re-throw: the caller must still see the original failure. This plan changes
the journal state, not the response.

Add a comment at the `FailJournal` call naming the contrast: a failure after
dispatch deliberately leaves the record `started`, because "may have
committed" is then the truth.

### Step 3: Write the regression tests

Create `src/GxMcp.Gateway.Tests/IdempotencyFailureJournalTests.cs`. The defect
is invisible unless you can inspect the journal state after a thrown factory,
so the test needs a way to observe it. Use whatever seam
`src/GxMcp.Gateway.Tests/MutationOperationJournalTests.cs` already uses to read
a journal file from disk after the fact; if there is none, construct an
`IdempotencyCache` over a temp journal path and read the file directly.

Cases:

1. `APoolFullFailureLeavesTheKeyReusable` — a factory that throws
   `WorkerPoolFullException`. Assert: the exception propagates **and** the
   journal file does not contain a `started` record for that key. The
   second half is the actual regression guard.
2. `AKbResolutionFailureLeavesTheKeyReusable` — same with the KB-resolution
   exception type as it actually exists in the hierarchy.
3. `AFailureAfterDispatchKeepsTheFenceAndStillReportsOperationUnknown` — a
   factory that throws an exception **not** in the pre-dispatch set. Assert the
   record stays `started` and that a second call with the same key gets
   `operation_unknown`. This is the test that stops the fix from over-reaching
   and deleting the fence.
4. `ErrorNotCacheableStillReturnsItsResult` — the pre-existing behaviour is
   unchanged: the result is returned, not thrown, and the record is failed.

For cases 1 and 3, assert on the journal's persisted state (parse the JSON),
not only on the returned exception.

### Step 4: Mutation-check every new test

Revert the `when (IsPreDispatchFailure(ex))` filter (making it catch nothing)
and confirm test 1 fails. Then make the predicate catch **everything** and
confirm test 3 fails. Record both mutations.

**Verify**: filter-removal → test 1 red. Over-broad predicate → test 3 red.

### Step 5: Full validation

```powershell
dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal
dotnet test src\GxMcp.Gateway.Tests --logger "console;verbosity=minimal"
dotnet build Genexus18MCP.sln -v:minimal
```

**Verify**: all exit 0, no new failures.

## Test plan

- New file: `src/GxMcp.Gateway.Tests/IdempotencyFailureJournalTests.cs`
  - 4 cases as above.
- Structural pattern: `src/GxMcp.Gateway.Tests/MutationOperationJournalTests.cs`
  (journal status vocabulary) and `ScopedReceiptPersistenceTests.cs` (temp
  journal path + teardown).
- Verification: focused filter → all pass; full suite → no new failures.

## Done criteria

- [ ] A named, documented predicate distinguishes pre-dispatch from post-dispatch failures.
- [ ] A pre-dispatch failure calls `FailJournal` **and** re-throws the original exception.
- [ ] A post-dispatch failure still leaves the record `started`.
- [ ] `ErrorNotCacheable` behaviour is byte-for-byte unchanged.
- [ ] The predicate's membership is derived from the real exception hierarchy, not assumed.
- [ ] `src/GxMcp.Gateway/IdempotencyFailureJournalTests.cs` exists with 4 cases, all passing.
- [ ] Both mutations performed and recorded.
- [ ] `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` exits 0.
- [ ] `dotnet test src\GxMcp.Gateway.Tests` exits 0, no new failures.
- [ ] `dotnet build Genexus18MCP.sln -v:minimal` exits 0.
- [ ] `CHANGELOG.md` has an entry under `## Unreleased` → `### Fixed`.
- [ ] `git status --short` shows nothing outside the in-scope list.

## STOP conditions

Stop and report back if:

- `KbResolutionException` does **not** derive from `UsageException` and adding
  it to the set would require changing an exception hierarchy. Report the real
  hierarchy.
- There is no seam to observe persisted journal state from a test without
  changing production code outside the in-scope list. Report what you tried.
- You conclude that `OperationCanceledException` from `RequestAborted` can
  arrive both before and after dispatch indistinguishably, so classifying it
  either way is a coin flip. Report it — this is a genuine ambiguity worth a
  decision, not a guess.
- The fix appears to require modifying a file listed as out of scope.

## Maintenance notes

- Interacts with: plan 111 (the same journal's locking). Independent files,
  independent concerns; either can land first. If both land, re-run
  `IdempotencyFailureJournalTests` — it reads the same file.
- A reviewer should scrutinise the **narrowness** of the predicate. A
  too-broad predicate deletes the fence for writes that may have committed,
  which is strictly worse than the current behaviour.
- Any new exception type that can be thrown before dispatch should be added to
  the predicate. Leave a comment at the predicate saying so, so the next
  author knows to look.
