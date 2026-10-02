# Plan 113: Bound the KB use-lease registry without changing an observable lease state

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise.
>
> **Drift check (run first)**:
> `git diff --stat 0f0d71a8..HEAD -- src/GxMcp.Gateway/KbUseLeaseRegistry.cs src/GxMcp.Gateway/SessionKbContextStore.cs src/GxMcp.Gateway/Program.KbContext.cs src/GxMcp.Gateway/Program.RequestLoop.cs src/GxMcp.Gateway/Program.cs`
> If any of those changed, compare the "Current state" excerpts against live code
> before proceeding; on a mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: MEDIUM — this plan touches a published string (`leaseState`) and an
  error code (`KB_LEASE_EXPIRED`). The whole design exists to change neither.
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `0f0d71a8`, 2026-10-02
- **Revision**: 2. Revision 1 specified an eviction guard that could not work;
  its executor proved it and stopped. That proof is correct and this revision
  keeps it.

## Why this matters

`KbUseLeaseRegistry._byToken` is a `Dictionary<string, Entry>` that entries are
added to and **never removed from**. A long-lived Gateway that serves many
sessions accumulates one `Entry` per lease forever. Each `Entry` also owns two
nested dictionaries (`Renewals`, `Closes`), so the per-lease cost is not one
small object.

This is a real leak, but it is **not** an urgent one: entries are small and the
count grows with lease count, not with request count. P2 is honest.

## Why revision 1 was wrong, and what it got right

Revision 1 said: on eviction, skip entries that are still in `_byOpenKey`. The
executor proved that guard is impossible, and the proof is verified:

`RefreshState` at `KbUseLeaseRegistry.cs:279-286`:

```csharp
        private void RefreshState(Entry entry)
        {
            if (entry.State == KbUseLeaseState.Active && _clock.Now >= entry.ExpiresAt)
            {
                entry.State = KbUseLeaseState.Expired;
                _byOpenKey.Remove(new OpenKey(entry.OwnerScopeId, entry.Identity, entry.ClientRequestId));
            }
        }
```

`_byOpenKey` is cleared at the *instant of expiry*, so "skip if in `_byOpenKey`"
would skip nothing at exactly the moment eviction wants to act. The guard cannot
work.

Revision 1 also would have broken two published behaviours:

1. **`leaseState` string.** `Program.KbContext.cs:35-42`:
   ```csharp
             var lease = _kbLeases.Get(snapshot.Lease.Token);
             if (lease == null)
                 return "invalid";
             ...
             if (lease.State == KbUseLeaseState.Expired)
                 return "expired";
   ```
   `Get` returns `null` for an absent token (`:215`). Evicting an expired entry
   turns `leaseState: "expired"` into `leaseState: "invalid"`. **Verified.**
2. **`KB_LEASE_EXPIRED` auto-recovery.** `Program.RequestLoop.cs:264-289`:
   ```csharp
                             var renewal = _kbLeases.Renew(
                                 sessionSnapshot.Lease.Token, ...);
                             if (renewal.Status == ... Success && ...) { ... }
                             else if (_currentExplicitKb.Value && renewal.Status == ... Expired)
                             {
                                 long generation = sessionSnapshot.ContextGeneration + 1;
                                 ...
                                 var freshLease = _kbLeases.Open(sessionId, canonicalAlias, generation, identity, "session-" + generation, TimeSpan.FromMinutes(10));
   ```
   The `Expired` branch is the documented recovery for a client that names a KB
   explicitly after its lease expired. Without the entry, `Renew` cannot return
   `Expired`, and `Validate` raises `KB_LEASE_INVALID` instead. **Verified.**

Revision 1 was right that eviction is needed and wrong about how to make it
safe. Both of its STOP findings stand; this plan satisfies them instead of
arguing with them.

## The design

**Every** read that can observe a terminal lease state takes its token from a
live session snapshot. Verified — the complete set of reads:

| Call site | Token source |
|---|---|
| `Program.KbContext.cs:35` | `snapshot.Lease.Token` |
| `Program.RequestLoop.cs:74` | `snapshot.Lease.Token` |
| `Program.RequestLoop.cs:77` | `snapshot.Lease.Token` |
| `Program.RequestLoop.cs:264` | `sessionSnapshot.Lease.Token` |

The only registry calls that do **not** come from a snapshot are `Open` at
`Program.KbContext.cs:77` and `Program.RequestLoop.cs:282`, and `Open` creates
leases rather than observing terminal ones.

**Therefore: an entry in a terminal state whose token no session references is
unobservable, and may be removed without changing any published behaviour.**

The guard is reachability, not `_byOpenKey`.

### What "terminal" must mean — the trap in this plan

`RefreshState` is called lazily, only for the entry being touched. An entry that
expired and was never accessed still has `State == Active` in memory. A sweep
that filters on `entry.State != Active` will therefore **miss most of the very
entries it exists to reclaim**, because nothing has touched them since they
expired.

So terminality must be:

```csharp
entry.State != KbUseLeaseState.Active || _clock.Now >= entry.ExpiresAt
```

not `entry.State != KbUseLeaseState.Active`. This is the single most likely way
to implement this plan wrong, and it fails *silently* — the tests in Step 4
exist to catch it.

### Pieces

1. **`SessionKbContextStore.IsLeaseTokenReferenced(string token)`** (new,
   `internal`). Scans `_entries` for any snapshot whose `Lease?.Token` equals
   `token`. `_entries` holds only live sessions (`CleanupExpired()` at `:132`
   removes idle ones), so this is the reachability question directly.
2. **`KbUseLeaseRegistry`** gains:
   - an optional ctor parameter `Func<string, bool>? isTokenReferenced`,
     defaulting to `null`. **`null` must mean "assume referenced"**, i.e. behave
     exactly as today. Only `Program` opts in. This keeps every other
     construction site fail-safe.
   - `TrimUnreferencedTerminalEntries(int targetCount)` (new, `internal`),
     returning how many entries it removed.
   - a capacity threshold check that triggers the trim when `_byToken.Count`
     exceeds a high-water mark.
3. **`Program.cs:36`** passes the predicate. It constructs the registry and owns
   `_sessionKbContexts`, so it can wire `token => _sessionKbContexts.IsLeaseTokenReferenced(token)`
   without either class knowing about the other.

### Lock ordering — read this before writing the trim

`TrimUnreferencedTerminalEntries` needs registry state (`_gate`) and, for each
candidate, the session store's view. Calling the predicate **while holding
`_gate`** nests two locks in a new order. Nothing today takes store-lock →
registry-lock, so it would not deadlock immediately, but it introduces an
undocumented ordering that a future change could invert silently.

**Use a two-phase trim instead:** under `_gate`, collect the tokens that look
trimmable; release `_gate`; ask the store about those tokens; re-acquire `_gate`
and re-validate each candidate (`still in `_byToken`, still terminal by the
two-part rule) before removing. No nested locks, and the re-validation closes
the window where a session acquired a reference between phases.

On the TOCTOU: a session can only reference a token it was already handed, and
terminal leases are not handed to new sessions. A terminal entry that becomes
referenced between the two phases would be a pre-existing session re-reading a
lease it already held, which does not happen — `RefreshLease` only stores what
`Renew` returns. If you find a counter-example in the source, report it rather
than adding a lock.

### Choosing the trigger

Do **not** sweep on every request — that is O(n) work per request. Sweep when
`_byToken.Count` crosses a high-water mark, trimming down to a low-water mark.
A hysteresis pair (trim to 75% of the high-water mark, say) avoids thrashing
when the working set sits near the threshold. Pick one pair of constants, name
them, and comment why they are what they are.

## Commands you will need

| Purpose | Command | Expected |
|-----------|---------|----------|
| Build Gateway | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | exit 0 |
| Rebuild test project | `dotnet build src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj -t:Rebuild -v:q` | exit 0 |
| Focused tests | `dotnet test src\GxMcp.Gateway.Tests --filter "FullyQualifiedName~KbUseLease" --no-build --logger "console;verbosity=minimal"` | all pass |
| Full Gateway suite | `dotnet test src\GxMcp.Gateway.Tests --no-build --logger "console;verbosity=minimal"` | all pass (2245 passed / 28 skipped / 2273 total at `0f0d71a8`) |
| Solution build | `dotnet build Genexus18MCP.sln -v:minimal` | exit 0 |

**Critical**: `dotnet test --no-build` runs the DLL in the *test project's*
output folder. Build the **test project** or you measure old code. `dotnet test`
rejects `-t:Rebuild` (MSB1008). `|` does not work in `--filter`.

Do **not** run `.\build.ps1`.

## Scope

**In scope**:

- `src/GxMcp.Gateway/KbUseLeaseRegistry.cs`
- `src/GxMcp.Gateway/SessionKbContextStore.cs`
- `src/GxMcp.Gateway/Program.cs` — only the registry construction at `:36`
- `src/GxMcp.Gateway.Tests/` — the lease-registry and session-store test files,
  plus one new test file
- `CHANGELOG.md`

**Out of scope** (do NOT touch):

- `Program.KbContext.cs` and `Program.RequestLoop.cs` — **these define the
  behaviour this plan must not change.** Read them to confirm your change is
  invisible to them; do not edit them. If you believe you must, that is a STOP.
- The tool schemas in `tool_definitions.json` — no published schema changes.
- `docs/mcp_capabilities_inventory.md` and
  `docs/operation-contract-inventory.json` — no published action changes.
- Any cache or retry policy in `OperationClassifier`.

## Git workflow

- Prose commit subject, e.g.
  `"Bound the lease registry without changing what a client can observe"`.
- `CHANGELOG.md` under `## Unreleased` → `### Fixed`, same commit. State that
  the registry was previously unbounded, that eviction is now reachability-gated
  so `leaseState: "expired"` and `KB_LEASE_EXPIRED` recovery are unchanged, and
  quote a test that proves each.
- Do NOT push, open a PR, or merge.

## Steps

### Step 1: Re-trace every read before writing anything

Enumerate every `_kbLeases.<read>` call site and confirm each one that can
observe a terminal state gets its token from a live session snapshot. The table
above is the advisor's measurement, not a specification — verify it. If you find
a read that observes terminal state **without** a snapshot reference, that is a
STOP condition and the whole design needs rethinking.

### Step 2: Add the reachability query

`SessionKbContextStore.IsLeaseTokenReferenced(string token)`. Read `CleanupExpired`
(`:132`) and the `_entries` type first. Note the existing comment at `:137` about
stdio having no HTTP idle timeout — your method inherits whatever that means for
session liveness, and should not second-guess it.

### Step 3: Add the trim and the trigger

Per "Pieces" above. The optional-predicate-defaults-to-referenced rule is
load-bearing: it means `new KbUseLeaseRegistry(clock)` with no predicate behaves
exactly as before, so every existing test and any other construction site is
unaffected. Assert that in a test.

Two-phase, no nested locks. Re-validate under the lock before removing.

### Step 4: Write the regression tests

Four tests, each aimed at a specific way this can silently break:

1. `AnExpiredLeaseIsStillReportedWhileItsSessionReferencesIt` — the decisive
   one. Create a lease, let it expire, keep the session referencing it, trim, then
   assert through `Program`'s own mapping that the state is still `"expired"`, not
   `"invalid"`. If you cannot drive `Program.KbContext`'s private mapping from a
   test, assert the registry-level equivalent (`Get(token)?.State ==
   Expired` after the trim) **and** say in NOTES which published string you did
   not exercise end to end.
2. `ATrimmedRegistryDoesNotBreakExplicitKbRecovery` — an expired lease that is
   referenced, `Renew` returns `Expired` after a trim. This is the
   `KB_LEASE_EXPIRED` path; without the entry it would return `Invalid`.
3. `AnUnreferencedTerminalLeaseIsReclaimed` — create a lease, release or expire
   it, drop the session, trim, assert the count fell and `Get(token)` is `null`.
4. `AnExpiredButNeverTouchedLeaseIsAlsoReclaimed` — the `RefreshState` trap.
   Create a lease with a short TTL, advance the fake clock **without calling any
   registry method**, then trim. Assert it was reclaimed. With a
   `State != Active` filter this test goes red, which is the point.

Plus: `AregistryWithoutThePredicateBehavesAsBefore` — construct with no predicate,
trim, assert nothing is reclaimed. This pins the fail-safe default.

### Step 5: Mutation-check every new test

| Mutation | Must turn red |
|---|---|
| filter terminality on `State != Active` only (drop the `_clock.Now >= ExpiresAt` clause) | test 4 — **the decisive one** |
| make the predicate always return `true` | test 3 |
| make the predicate always return `false` | tests 1 and 2 |
| default the predicate to "unreferenced" instead of "referenced" | `AregistryWithoutThePredicateBehavesAsBefore` |
| remove the re-validation in phase two | report what you observe; if nothing goes red, say so — that is itself a finding about how narrow the window is |

**Confirm each mutation applied to the source before running the test** — print
the changed line. Several mutation attempts in this session silently no-opped and
produced convincing false greens.

### Step 6: Full validation

```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'
dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal
dotnet build src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj -t:Rebuild -v:q
dotnet test src\GxMcp.Gateway.Tests --no-build --logger "console;verbosity=minimal"
dotnet build Genexus18MCP.sln -v:minimal
```

## Done criteria

- [ ] Every read that can observe a terminal state re-verified as snapshot-sourced.
- [ ] `Program.KbContext.cs` and `Program.RequestLoop.cs` are **unmodified**.
- [ ] `leaseState: "expired"` is still produced for a referenced expired lease.
- [ ] `Renew` still returns `Expired` for a referenced expired lease after a trim.
- [ ] The trim is two-phase with no nested locks, and re-validates before removal.
- [ ] Terminality is `State != Active || _clock.Now >= ExpiresAt`.
- [ ] The predicate defaults to "referenced"; an unconfigured registry is
      byte-for-byte unchanged in behaviour.
- [ ] No new lock-order dependency.
- [ ] All five mutations confirmed applied and red as specified.
- [ ] `dotnet test src\GxMcp.Gateway.Tests` exits 0, no new failures.
- [ ] `dotnet build Genexus18MCP.sln -v:minimal` exits 0.
- [ ] `CHANGELOG.md` has an entry under `## Unreleased` → `### Fixed`.
- [ ] `git status --short` shows nothing outside the in-scope list.

## STOP conditions

Stop and report back if:

- You find a registry read that observes a terminal state **without** going
  through a live session snapshot. The design does not hold; report the call path.
- `KbUseLeaseRegistry`'s existing public surface differs from what this plan
  quotes (`Get` `:210`, `Renew`, `Validate` `:221`, `RefreshState` `:279`).
- `SessionKbContextStore._entries` is not a type you can enumerate safely, or
  `CleanupExpired` at `:132` behaves differently than described.
- Wiring the predicate at `Program.cs:36` requires `_sessionKbContexts` to be
  constructed before the registry in a way the current static initializer order
  cannot satisfy. Report the ordering problem — do not restructure `Program`.
- You conclude a read path can hold a terminal lease token without a session.
- The fix appears to require modifying a file listed as out of scope.

## Maintenance notes

- **The invariant to preserve, stated once: no client-visible string or error
  code changes.** If a future edit to this area alters `leaseState` or the
  `KB_LEASE_*` codes, it is wrong even if it looks like a cleanup. Two tests
  exist solely to hold that line.
- The reachability guard replaced the `_byOpenKey` guard because `_byOpenKey` is
  cleared at the instant of expiry. If someone later proposes using `_byOpenKey`
  for this, point them here.
- `RefreshState`'s laziness is the reason terminality needs two clauses. A future
  change that makes `RefreshState` eager (a sweep that touches every entry)
  would let the `State != Active` filter work — but would cost O(n) per call.
  Prefer keeping the lazy design and the two-clause filter.
- This was revision 1's second death, and for the same class of reason: a guard
  specified against a field whose lifecycle I had not traced. The general lesson
  is in `plans/README.md` and applies to every plan in this set.
