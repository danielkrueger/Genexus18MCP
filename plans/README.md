# Implementation Plans

## Current audit — 2026-10-01, commit `0f0d71a8`

Fifteen findings survived vetting and became plans 110–124. Every finding was
independently re-verified against the code at `0f0d71a8` before a plan was
written; one reported finding was rejected on measurement (see "Rejected"
below). These plans are handoffs. Executors work in isolated git worktrees on
`advisor/<plan>-<slug>` branches; merging is the maintainer's decision.

| Plan | Title | Priority | Effort | Risk | Depends on | Status |
|------|-------|----------|--------|------|------------|--------|
| [110](./110-dirty-set-marks.md) | Mark the incremental-build dirty set from every mutating write surface | P1 | M | LOW | — | DONE (approved on review) — **partially complete, see scope correction below** |
| [111](./111-journal-cross-process.md) | Give the durable mutation-operation journal cross-process coordination | P1 | M | MED | — | DONE (approved on review) |
| [112](./112-idempotency-pre-dispatch-fail.md) | Fail the idempotency journal fence on pre-dispatch factory failures | P1 | S | LOW | — | DONE (approved on review) |
| [113](./113-lease-registry-evict.md) | Bound the KB use-lease registry without changing an observable lease state | P2 | M | MEDIUM | — | DONE — `3e0003d1` |
| [123](./123-tool-capability-registry.md) | Make the contract-inventory generator's policy read tolerant | P2 | S | LOW | — | DONE — `8c9c7b2e` |
| [125](./125-logredaction-key-shapes.md) | Let the shared credential redactor match compound and short key shapes | P1 | S | LOW | — | DONE — `724f024c` (revision 2) |
| [126](./126-dirty-set-remaining-files.md) | Close the incremental-build dirty set for the remaining write surfaces | P1 | M | LOW | — | **SUPERSEDED by 127** — four "delegators" own commit sites |
| [127](./127-dirty-set-corrected-scope.md) | Mark the dirty set on every remaining write surface, corrected scope | P1 | M | LOW | — | DONE — `2e8cd273` |
| [114](./114-workerpool-evict-identity.md) | Remove the victim's own entry in WorkerPool eviction, not a replacement's | P2 | S | LOW | — | DONE (approved on review) |
| [115](./115-visual-verify-runner.md) | Route the visual-verify CLI runner through the hardened shim builder | P1 | S | LOW | — | DONE (approved on review) |
| [116](./116-kbcreate-password.md) | Keep the KB-create database password off the child command line | P1 | S | LOW | — | DONE (approved on review) — but see the LogRedaction gap below |
| [117](./117-kb-xml-dtd.md) | Prohibit DTD processing at the four remaining KB-sourced XML parse sites | P2 | S | LOW | — | DONE (approved on review) |
| [118](./118-recovery-refresh-off-read.md) | Move the mutation-recovery journal refresh off the read path | P2 | M | MED | — | DONE (approved on review) |
| [119](./119-kb-selection-one-impl.md) | Make the KB-selection policy one implementation instead of two | P1 | S | MED | — | DONE (approved on review) |
| [120](./120-env-var-docs.md) | Document every environment variable the code actually reads | P2 | S | LOW | — | DONE (approved on review) |
| [121](./121-lane-list.md) | Make the documented validation lane list match what CI runs | P3 | S | LOW | — | DONE (approved on review) |
| [122](./122-readme-release.md) | Correct the README release section to match the workflow that ships | P3 | S | LOW | — | DONE (approved on review) |
| [123](./123-tool-capability-registry.md) | Put every tool capability fact in one registry | P2 | M | MED | — | BLOCKED — the plan's own proof gate contradicts its own test. See below. |
| [124](./124-apply-pattern-schema.md) | Fix the apply_pattern schema prose that promises a refusal route | P2 | S | LOW | — | DONE (approved on review) |
| [111](./111-journal-cross-process.md) | Give the durable mutation-operation journal cross-process coordination | P1 | M | MED | — | IN PROGRESS |

**No inter-plan dependencies.** Every plan's in-scope file list is disjoint from
every other plan's, so all fifteen can execute in parallel worktrees. Two pairs
touch the same *subsystem* but different files, and are ordered only by
judgment, not necessity: 111 and 112 both concern the mutation journal
(111 its locking, 112 its exception handling); 118 also touches the recovery
registry that 111 uses as its pattern source.

**Why 110 leads.** It is the only finding that produces a wrong *artifact*
rather than a wrong message: a `genexus_properties set` or `genexus_layout
set_property` succeeds, the target stays classified clean, and the next
incremental build ships a stale DLL with no error surface. Everything else is a
wrong answer, a wrong promise, or a resource leak.

### Rejected in this audit

- **`Argv.Quote` splits arguments containing a double quote** (reported as an
  argument-injection bug across five `git`/`gh` call sites, pinned as a
  "KNOWN DEFECT" by `src/GxMcp.Worker.Tests/ArgvQuotingTests.cs:59`).
  **Measured and rejected.** The test passes the quoted value as the *entire*
  command line, so it lands in `argv[0]`, where `CommandLineToArgvW` has
  documented special handling. Measured against a real child process, the
  primitive round-trips every case: `a"b`, `a\"b`, `a b\"c d`,
  `fix the "parser" now`, `feat/"quoted"` and `a b\` all arrive as exactly one
  argument. The 24 tests pass. The defect that remains is in the **test**: it
  documents a bug that does not exist and pins bytes that are only wrong
  because of the `argv[0]` slot. An executor acting on it would break every
  command line the server builds. Reported as a test-accuracy finding, not a
  code change; not planned.
- **Evicting a busy Worker** — already fixed in `0f0d71a8`'s history by #333
  (`CHANGELOG.md:30`), with the in-flight reservation and idle-only victim
  policy. Not re-planned.
- **Splitting `OperationsRouter` and the Gateway request loop** — plans 103 and
  104, both DONE. Plan 123 targets the next tier (`McpRouter.cs`, 2311 lines),
  not those.
- **`net48` on the Worker** — pinned by the GeneXus SDK. By design.
- **Migrating the eight already-hardened XML parse sites to the new helper** —
  plan 117 adds the helper and converts the four unhardened sites only. A
  follow-up sweep is tracked in that plan's maintenance notes.

### Not planned from this audit

- A `genexus_read` conditional-read path, multi-KB federation beyond
  `genexus_query`, and the capability-state machine — direction options
  surfaced to the maintainer, not defects. The conditional read and the
  federation already shipped in `0f0d71a8` (#357, #356).
- Two undiagnosed defects recorded in `CHANGELOG.md:19`: `genexus_edit_form
  action=add_textblock` persisting nothing, and a `genexus_layout
  set_property` whose read-back disagrees with the object read path. Both were
  observed on a hand-authored scratch panel the SDK's own validator rejects, and
  both need a live KB gate this checkout cannot run. Reported, not planned.
- `SearchIndex.FindByEntityKey` doing an O(objects) scan per lookup, and the
  per-frame `FindPendingForOperation` scan — real and measured, but lower
  leverage than the fifteen above and not yet planned.

### Plan 124 — the refusal is scoped, and the schema now says so

The executor caught a precision point the plan had flattened: the reapply
refusal is **scoped, not blanket**. `PatternApplyService` returns early for
`pattern.IsWorkWithPlus`, so WorkWithPlus reapply is a live route; only the
generic pattern-engine path reaches
`TryBuildRouteUnsupportedRejection(…, PatternRoute.Reapply)` behind
`if (!pattern.IsWorkWithPlus)`. I verified both branches. So the schema says
"refused for **non-WorkWithPlus** patterns" rather than "unsupported" — the
shorter blanket phrasing would have been wrong for WWP.

Test 2 asserts that scope by reading `IsSupported` from the Worker source and
requiring the schema to name `WorkWithPlus`, so if someone later flips
`GenericReapplySupported` the guard fails in the direction that forces the
schema to be revisited.

Two scope extensions the executor made and flagged, both correct:

- **`ToolHelpCatalog.cs` was changed.** It carried the byte-identical false
  sentence "Omit `pattern` only with `reapply=true`…" at `:248`, plus an
  unscoped "`reapply: true` to regenerate over an existing instance" at `:287`.
  Leaving either would have left the schema and the tool help contradicting each
  other — the exact defect the plan exists to remove.
- **The `examples` array was fixed**, which the plan did not list.
  `{"name":"Customer","reapply":true}` demonstrated the refused combination on a
  plain Transaction; it is now the one non-refused case,
  `{"name":"WorkWithPlusCustomer","pattern":"WorkWithPlus","reapply":true}`.

The `reapply` description grew 29 → 207 bytes, which the plan asked to justify.
It carries the refusal, its scope, the reason (no derived-object regeneration)
and the alternative route. The compensation is real and measured: `pattern`
grew only +52, and `ToolProfileFilter.Compact` truncates descriptions to ~40
chars per profile, so `pattern`'s profile bytes are unchanged and `reapply`
costs ~11 against the 82500 `all` budget. Total `all` profile 34135 → 34201
tokens against a 34500 budget. **No budget constant was touched.**

The golden fixture diff is one line — only `reapply` is visible there, because
the discovery fixture stores the *compacted* schema, so `pattern` and the
example are stripped before it is built. Expected, not a partial regeneration.

Two incidental notes: `docs/agent_playbook.md` was checked and does **not**
repeat the promise (no STOP). And the golden-regeneration run touched two
sibling fixtures with line-ending churn only (zero content diff under
`--ignore-cr-at-eol`); both were reverted to keep the tree in scope, and
`McpDiscoveryContractTests` re-run green afterwards.

### Plan 123 — BLOCKED, the plan's own proof gate contradicts its own test

Plan 123 requires `OperationClassifier.Describe()` to stay byte-identical,
and named `python scripts/generate-operation-contract-inventory.py --check` as
the proof. But that script does **not** call `Describe()` — it regex-parses the
*source text* of `OperationClassifier.cs`:

```
_set_block(): raises if the field is absent
requires PureReadOnlyTools, KnownMutatingTools, ModeDependentTools,
         NameOnlyMutatingTools, the ActionContracts dict, DryRunCapableActions
```

The executor proved it by running the script's own `read_policy()` against a
mutated copy in the temp dir (the repository was not modified): removing all
six declarations raises `ValueError: substring not found`; removing only the
four named ones raises `ValueError: could not locate PureReadOnlyTools`. So
the plan's test 3 — which forbids those declarations *anywhere outside*
`ToolIdentity.cs` — forbids exactly what its own done criterion requires. No C#
arrangement satisfies both, and the generator is not in the in-scope list.

Two resolutions, both a maintainer decision:

- **(a)** Keep the four named literal sets in `OperationClassifier.cs` as the
  generator's parse target and exclude those four names from test 3's forbidden
  list. `--check` stays green and the other seven registries still consolidate;
  the cost is the "single source" claim for the four classification sets only.
- **(b)** Authorise the generator change: `CLASSIFIER` points at
  `ToolIdentity.cs` and `read_policy()` parses the consolidated record table
  instead of five hardcoded field shapes — roughly 30 lines in one function.
  `validate-tool-contracts.py` is unaffected (it reads the routers, not the
  classifier).

Also worth keeping: the executor cleared three STOP conditions I expected to
fire. `ToolProfileFilter`'s allowlists encode membership only, not ordering —
`ToolProfileFilter.cs:122-125` filters the incoming array, so `tools/list`
order stays schema order. No classification in the matrix is *wrong*, only
missing. And no per-request cost is introduced, since the registry would be a
static table.

### The Step 1 gap table is a live finding, independent of the plan

The inventory the executor produced (its required deliverable) is worth more
than the plan that asked for it. **Five tool names are reachable through a
router but carry no classification at all**, so `ClassifyCanonicalTool` returns
`Unknown` and `Describe()` reports `cache=never, retry=never, effects=unknown`:

`genexus_explain` (`IndexGatePolicy.cs:28`, dispatched `OperationsRouter.cs:216`),
`genexus_orient` (four sites including `AutoTypeInjector.cs:27`),
`genexus_what_if` (`OperationsRouter.cs:284`),
`genexus_kb_explorer` (`SystemRouter.cs:236`),
`genexus_multi_agent_lock` (`AutoTypeInjector.cs:362`).

None is in `tool_definitions.json`, so no client discovers them via
`tools/list` — but a caller using the legacy name reaches them. Separately,
`_toolsWithTypeArg` contains one **dead entry**: `genexus_multi_agent_lock` is
not a published tool, so `AutoTypeInjector.cs:341` can only ever be asked about
a name the router already rejected.

And the plan **understated the surface**. Beyond the seven registries it names,
these also hold hardcoded tool names: `AutoTypeInjector._skipTools`
(`:22`), `OperationClassifier.IsKbScopedReadMetaTool` (`:453`), the inline name
tests in `RequiresSessionLease` (`:467-479`), `EffectsFor`/`ExecutionFor`/
`InvalidationFor` (`:565-602`), `RemovedToolsRegistry.Map` (`:20`), and the
`McpRouter` alias cases. `_skipTools` lists `genexus_github` and
`genexus_ai_complete`, which appear in no schema and in no other registry — both
verified absent. So "adding a tool means editing one registry" will not be true
after 123 lands unless those are consolidated too.

### Plan 113 — BLOCKED, plan was wrong

The executor stopped at Step 1/2 and I verified every claim against the code.
The finding is real; the fix as specified is not implementable inside its scope,
and the plan's own Step 4 test case 3 would have enshrined the regression.

The plan's premise was half wrong. A superseded-generation token does become
unreachable — but the **current** generation's expired token does not, and two
published behaviours depend on reading its terminal status:

1. `Program.GetSessionLeaseState` (`Program.KbContext.cs:35`) calls
   `_kbLeases.Get(snapshot.Lease.Token)` and returns `"expired"` at `:41`.
   `Get` returns `null` for an absent token (`KbUseLeaseRegistry.cs:213`), so a
   swept entry turns the published `"expired"` into `"invalid"` at `:37`. This
   string is published in `genexus_whoami` (`kb.leaseState`) and in
   `genexus_kb action=list` / `action=open`.
2. `Program.RequestLoop.cs:264-267` calls `_kbLeases.Renew(snapshot.Lease.Token, …)`.
   The `Expired` branch at `:277-289` is the explicit-`kbAlias` auto-recovery
   path. `Renew` returns the `FindOwned` status without a lease when the token
   is absent (`KbUseLeaseRegistry.cs:145-147`), so that branch never fires and
   `Validate` raises `KB_LEASE_INVALID` instead of `KB_LEASE_EXPIRED` — a
   different code and a different hint.

Worse, the guard the plan specified cannot protect either one. The plan said
"never remove an entry still referenced by `_byOpenKey`", but
`RefreshState` (`KbUseLeaseRegistry.cs:279-286`) removes that mapping in the
same instant it flips the state to `Expired` — so the guard is false for
exactly the entries that must be retained.

The existing `Issue192LeaseRecoveryTests` suite did not catch this: each of its
tests performs exactly one `Renew`/`Get` on the expired token, so the first
read survives. The damage is on the *second* read, which is the real-world
shape. That is worth remembering independently of this plan — the recovery
contract has a coverage gap.

Two ways forward, both requiring files the plan listed as out of scope:

- **(a) Give the registry a reachability signal.** A `Retain`/`MarkReferenced`
  hook called from `SessionKbContextStore.Set`/`RefreshLease`, sweeping only
  entries no live snapshot references. This preserves both published
  behaviours *and* actually removes the superseded-generation entries the plan
  identified — i.e. it fixes the real leak rather than bounding it. Crosses the
  session-store boundary, so it is a scope expansion.
- **(b) Ship the capacity ceiling alone.** Bounds the growth but does not fix
  the leak, and it still changes behaviour: with the ceiling reached by live
  active leases, `Open` starts failing closed. It would not satisfy the plan's
  own cases 1 and 3.

**Not yet rewritten.** (a) is the correct fix and the executor has mapped the
call sites; it needs a maintainer decision because it expands scope across the
KB-context subsystem. Case 3 of the original plan must be dropped or inverted
either way.

### Scope correction from plan 110 — my audit under-counted this defect by 5×

Plan 110 landed and is correct, but it closes **7 of 21** affected files, not
"four surfaces". I measured the real number after the executor reported it, and
confirmed it independently:

```
files under src/GxMcp.Worker/Services with EnsureSave( and no NotePerTargetWrite:
  at 0f0d71a8 (before): 21
  after plan 110:      14
```

The 14 still open, all unverified rather than all proven broken —
`ApiIntrospectService`, `AtomicAuthoringService`, `AtomicCreateService`,
`BatchService`, `ForgeService`, `GxServerWriteService`, `MergeToolService`,
`PatternApplyService`, `RefactorService`, `StructureService`,
`WriteService.PatternWrite`, `WriteService.ThemeWrite`, `WriteService.VisualWrite`,
`WwpProjectionHelper`.

Some delegate to a surface that now marks, so each needs a commit-site audit
before it can be called a defect. `ApiIntrospectService` is a known example of
the other kind: it calls `WritePipeline.NoteWrite` instead, a different entry
point, and needs reading before it is counted. **I do not know the true
remaining count** and neither does the executor; what is certain is that my
audit's "four surfaces" framing was wrong by roughly a factor of five, and that
plan 110 is a partial fix rather than the fix.

Two further corrections the executor made to my plan, both verified by me:

- **My consumer guard was a no-op.** I specified asserting
  `"!EditDirtyTracker.IsDirty(kbPath, t)"` is present in
  `InProcessBuildRunner.cs`. That substring occurs **twice** — `:348` (the
  all-clean short-circuit) and `:413` (the fast-path guard) — so the assertion
  would have passed with `:413` replaced by `true`, and shipped green forever.
  The mutation check caught it; the test now pins the full unique statement
  `targetMaySkipSpecify = !EditDirtyTracker.IsDirty(kbPath, t);`. I reproduced
  the mutation independently and confirmed the strengthened guard goes red.
- **My LayoutService count was wrong and the executor did not pad to match.** I
  predicted ≥6 marks across two files. Two of my six candidate sites are not
  commit sites: `TryRestoreProcedureSource` is the *compensating restore* on
  all ten of its callers' failure paths, so marking it would make a rolled-back
  write dirty — the exact error my own reviewer note warned against. And
  `TryFlushSourceForLayoutMutation(KBObject obj, out string error)` has no
  object-name variable, so a mark would need new plumbing, which is a STOP
  condition. The real topology is three commit sites in `LayoutService.cs` plus
  one choke point in `SourcePersistence.cs` that `SetProperty`, `SetProperties`
  and all of `ReportControls.cs` funnel through. Four is correct; the test
  asserts the real minimums (3/1) rather than my padded (3/3).

Also worth carrying forward: the executor counts marks through
`RepoSource.WithoutComments`, not `RepoSource.Read`, because it had added
explanatory comments to the same files and a comment naming the call would
otherwise satisfy an assertion about the call. `SourceAssert`'s own docs record
a stripper that once deleted a third of a service file while every test using
it stayed green — the inverse trap is equally live.

**Follow-up needed:** a plan for the remaining 14 files, which is an audit-then-
fix rather than a mechanical sweep, because the "does this file own its commit
site or delegate to one that now marks" question has to be answered per file.

### Plan 118 — one deviation worth carrying forward

Plan 118's own STOP condition asked whether timestamp+length is a sufficient
change signal. The executor found a genuine hole and fixed it, flagging the
deviation itself: `PersistJournal` commits with
`File.Move(temporary, _journalPath, overwrite: true)`
(`MutationRecoveryRegistry.cs:745`) — a whole-file atomic replace, the best
case for metadata detection, which I verified. But **two commits inside one
filesystem timestamp tick** (≈15.6 ms on Windows) at the same byte length would
compare equal, and the baseline would then equal the file *forever* — the fence
stays invisible until some unrelated write moves it. A permanent miss, not a
bounded one.

The fix converts it to a bounded miss: `ReadPathRecheckIntervalMs = 250`
re-reads at least that often, so the cost becomes one lease+parse per 250 ms of
read traffic instead of one per read. Test 2 pins each half of the signal
separately — a same-length/later-stamp commit only the timestamp can see, and a
same-stamp/longer commit only the length can see.

Also worth knowing:

- **The fail-open direction was verified before it was chosen**, which is what
  the STOP condition demanded. Losing the lease raises `JournalBusyException` →
  `_journalBusy` → `IsHealthy == false` → `isLiveTool = true` → the read goes to
  the Worker instead of the cache. That is fail-safe. A silent skip would have
  left `IsHealthy == true` and `Count == 0` and served a *cached* answer. The
  implemented shape is fail-open-into-busy, and that is asserted.
- `ConfirmReadCore` (`:355`) is a remaining read-path lease wait still at 2000
  ms, correctly, because a confirmation rewrites the journal. Candidate for a
  follow-up.
- A stale `.tmp-*` candidate next to an *unchanged* journal is no longer noticed
  by the read path, because noticing it needs the directory glob this plan
  removed. The safety-relevant direction is preserved — the write gate's
  unconditional `Refresh()` still catches it and blocks writes — so only a
  read's cache-bypass decision is affected, in an already-degraded state.
- **Not verified live.** The multi-Gateway shared-journal path this targets is
  exercised only by unit tests; the 28 skipped tests are the pre-existing
  live-KB classes. A live two-gateway smoke would gate this per `AGENTS.md`.
- Test 1 has a residual timing sensitivity (its "unchanged" precondition assumes
  <250 ms between baseline capture and the call). The executor shrank the
  window and documented it; it is not provably non-flaky on a pathological
  machine.

### New finding from plan 116 — the shared credential redactor has a real gap

Plan 116 assumed `LogRedaction.Redact` would cover the KB-create password in
the failure envelope. **It does not.** The executor found this by writing the
test and watching it fail, then confirmed the regex directly. I verified it
independently against the real pattern at
`src/GxMcp.Gateway/Helpers/LogRedaction.cs:29`:

```
/p:KBDbPassword=secret   -> NOT MATCHED (passes through unredacted)
KBDbPassword=secret      -> NOT MATCHED
password=secret          -> REDACTED
Server=db;Pwd=abc        -> NOT MATCHED
```

The pattern is `\b(?:password|passwd|…)\b`, and it requires a word boundary
*before* `password`. In `KBDbPassword` the preceding character is a letter, so
there is no boundary and the whole key/value passes through. Without a
call-site mask, plan 116 would have **looked** done and still put the
credential in the response body. The landed fix adds a
`MaskDbPasswordProperty` mask for that exact property shape and pins both
halves in a test; I reproduced the mutation by removing the call-site mask and
confirmed 2 of 4 tests go red.

`LogRedaction.Pattern` was **not** modified — out of scope for 116, correctly.
But this is a general defect, not a KB-create one: any `XxPassword=`-shaped key
passes the shared redactor untouched. Worth its own plan, and the obvious shape
is to relax the boundary so `KBDbPassword`/`DbPassword`/`Pwd` match, with the
existing redaction tests extended rather than replaced. Not yet planned.

Two more things 116 surfaced that are worth carrying:

- **The response-file mechanism had two measurable traps**, both found by
  running against the real MSBuild 4.8 binary rather than reasoning: the
  `"@ref"` argument must be *quoted* in `ProcessStartInfo.Arguments` or the
  child's CRT splits it at a space (MSB1022), and the file must be written
  UTF-8 **with BOM** or MSBuild decodes as ANSI and mangles a non-ASCII
  password (3 of 19 shapes wrong without the BOM, 0 of 19 with it). The new
  quoting also fixes two pre-existing bugs in the old inline `/p:` form: a
  password containing `;` failed with MSB1006, and non-ASCII was mangled.
- **Residual risk, stated not fixed:** the password is briefly on disk in the
  temp response file between write and the `finally` delete. Inherent to the
  mechanism. Deletion is in the existing `finally`, so a failed spawn cannot
  leave it behind, but the window is not zero.

**Not verified by 116:** no live KB create with a non-integrated DB login was
run. The mechanism was measured against the real MSBuild binary with the real
`ProcessStartInfo.Arguments` shape, but the GeneXus task consuming
`$(KBDbPassword)` was not. Per `AGENTS.md` a live smoke would normally gate
this; it touches no Worker path, but the gap is real and should gate release.

### New finding from plan 115 — one done criterion was unsatisfiable as written

Plan 115's criterion was "`new ProcessStartInfo("cmd.exe"` no longer appears in
`VisualVerifyService.cs`". That is **impossible to satisfy honestly**:
`BuildShimArguments` returns the whole `/d /s /c "…"` line, so `cmd.exe` must
remain the interpreter. I verified the reference implementation carries the
identical literal — `PreviewService.cs:87` has
`new ProcessStartInfo("cmd.exe", BrowserDriverProcess.BuildShimArguments(…)`,
and that file is out of scope and untouched.

The executor did **not** hide the string behind a variable to satisfy a text
match. It rewrote the guard to pin the actual defect instead — the hand-built
`"/c \"\""` fragment absent, `BuildShimArguments` present, `private static
string Quote` gone, and the native `new ProcessStartInfo(fileName, arguments)`
branch still present. That is the right call: a criterion that can only be met
by gaming the text is a bad criterion, and the substitution preserves the
guard's teeth.

**Behaviour change worth stating:** a driver that is neither `.exe`/`.com` nor
`.cmd`/`.bat` (e.g. a `.ps1` reached via `PATHEXT`) is now *refused* by the
shared rule rather than failing inside `cmd.exe`. The old comment claiming
`.cmd/.bat/.ps1` support was false — `cmd.exe` does not run `.ps1` without
`-File`.

### New finding from plan 114's review — not yet planned

The 114 executor found a second orphan path in the same class while verifying
its own backstop, flagged it rather than fixing it, and was right to: it is
outside that plan's scope. I read the code and it is real.

`WorkerPool.SpawnWorkerAsync` (`:331-357`):

- `:331-334` — when `IsProcessAliveForPool` is false, the plain
  `_entries.TryRemove(alias, out _)` can still remove a *replacement* entry
  whose worker is live. Same exposure 114 just closed in the eviction path.
- `:336-343` — when the alias no longer maps to this entry and there is no
  replacement with a worker, it throws at `:343`; the catch at `:349-358` then
  sets `entry.Worker = null` and removes the entry **without ever stopping
  `createdWorker`**, which `:331` just established is alive.

Reachable when a worker's `OnWorkerExited` removes the alias — or a planned
reload reuses the entry — while a spawn for the same alias is in flight. The
result is the same untracked `GxMcp.Worker.exe` 114 fixed on the eviction side.
Pre-existing, not introduced by 114. This means **114 is a path fix, not a
class fix**: the class still has one open path.

Not written up as a plan yet — it needs the same treatment as 114 (conditional
removal plus a stop-before-drop on the failure path) and should be sequenced
with a decision about whether the two paths are fixed together.

### Not yet dispatched

The executor for 112 corrected its own report after filing it: its claim that
`KbResolutionException` cannot reach the factory boundary was read from source
and never observed at runtime. The *classification* is unit-verified; the
*reachability* is not. Treated as `not verified` here too. The executor also
corrected the suite size used in several plans (2252 passing / 2280 total, not
the 880+ the plans state).

### Two follow-ups written after review — plans 125 and 126

The maintainer chose to close both gaps before publishing. Plans 125 and 126
exist for that; neither was in the original 110–124 set.

**125** fixes the general form of the gap plan 116 found. Its pattern was
**measured against the current one before the plan was written**, and that
measurement is what shaped it: the obvious fix — a prefix allowance in front of
the whole key alternation — makes `bypass=1` match as `by` + `pass`, redacting
an ordinary diagnostic value. So `pass` stays a strict-boundary alternative
outside the prefixed group, and the plan ships a negative theory that fails if
anyone later "simplifies" it. Also load-bearing and easy to miss: **the two
assembly copies must change together**, because
`TheTwoAssemblyCopiesCarryTheSamePattern` compares the literals for equality —
changing one and not the other fails a test unrelated to the change.

**126** was superseded by **127**; see below. Both were audit-first rather
than sweep-first. Measuring the 14 files showed they were not one kind:
`ApiIntrospectService` already marks through `WritePipeline.NoteWrite`
(invisible to a `NotePerTargetWrite` scan), four delegate to the write service,
three are partials of `WriteService` itself, and only some own a commit site.
Step 1 was therefore a classification with citations. It carried
forward both rules plan 110 established after its executor corrected the plan —
mark only after a confirmed commit, and pin the consumer as a **full unique
statement** (plan 110's first guard pinned a substring occurring twice and would
have shipped green forever).

**Process note carried forward for every future executor:** three mutation
attempts in this session failed because the edit pattern did not match the
source, each producing a green result indistinguishable from "the guard works".
Every plan written from here on requires printing the changed line before the
test run. One of those three was mine, during review.

### Both 125 and 126 were wrong on first write — and mine was the error in both

Both revision-1 executors stopped at a STOP condition. Neither stop was a
discovered defect in the repository. Both were **scope errors in plans I
wrote**, and both had the same shape: I asserted a fact about existing code
from a partial read, and the executor's full read contradicted me.

**126 → 127: I classified four services as "delegates to the write service"
without tracing their commits.** The evidence I used was that
`_writeService.WriteObject` appeared in each file. All four in fact own SDK
transactions and call `Commit()` themselves (`BatchService.cs:117`,
`RefactorService.cs:708` and `:815`, plus `EnsureSave` at
`AtomicAuthoringService.cs:134` and `AtomicCreateService.cs:570`). The
`_writeService` calls are real but they are *alternate* paths. Verified
independently before rewriting the plan. Net correction: **13 files need marks,
not 9** — and the audit also found four commit sites that must *never* be marked
(two dead methods whose only other reference is a comment, and two rollback
helpers that would mark a reverted write as dirty).

**125 → 125 revision 2: I declared `LogRedactionSingleSourceTests.cs` out of
scope without reading it.** I grep-selected from that file. Reading it in full
shows the redaction gap was **known, documented in the source, and
deliberately deferred**:

> Widening the pattern to cover this is a security change to the logging rule,
> not a consolidation of it, so it is recorded here rather than folded into this
> refactor.

So two tests were not stale guards — they were the deferral marker, and two of
them state it in their **names**: `ThePwdKeyIsStillNotRecognisedOnItsOwn` and
`APwdDelimitedConnectionStringStillLeaksItsTail`. Revision 1's scope was
unsatisfiable by construction. Revision 2 brings the file into scope with three
precisely specified changes: invert both tests *and rename them* (a name is
documentation), and replace `ThePatternExistsOncePerAssembly`'s needle with
`internal const string Pattern`, because `CountOccurrences` is an ordinal
`IndexOf` substring count and inserting `pwd` breaks contiguity — measured 0/1
against the modified file. Adding a credential key is a legitimate change and
must not be able to break that guard.

Two corrections of detail found while verifying:

- The two `LogRedaction.cs` files are **not** byte-identical, and never were —
  their doc comments differ. Only the *pattern literal* must match, and
  `TheTwoAssemblyCopiesCarryTheSamePattern` extracts just that. Revision 1 said
  "byte-identical"; it meant the literal.
- `bypass=1` matching as `by` + `pass` is not hypothetical — it is the reason
  `pass` cannot join the prefixed group, and it is why 125 ships a negative
  theory as a load-bearing test rather than a nicety.

**Process rule adopted after this:** a plan may not declare a file out of scope
on the strength of a grep-selected read. If a plan asserts a fact about existing
behaviour, the executor verifies it by reading, and a contradiction is a STOP —
which is exactly what both executors did, correctly.

### 125 approved — `724f024c`

Reviewed: both `LogRedaction.cs` copies, the three inversions, the new
`LogRedactionKeyShapeTests.cs` (22 cases), and the CHANGELOG entry. Gateway
2267 passed / 0 failed, Worker 4417 passed / 0 failed, solution exit 0. All
five mutations confirmed applied and red as specified.

The inversions are genuine rather than weakened — both tests renamed, full-string
assertions, rationale comments rewritten to record the closure instead of the
deferral. The executor also replaced the pre-existing `hunter2` literals in the
two tests it touched with a sentinel; the new file contains no plausible
credential at all.

Three corrections its report forced, all recorded so the reasoning is not
re-derived wrongly later:

- **My plan's rationale table was wrong about `compassion`.** I listed it as
  protected by `pass`'s strict *leading* boundary. It is not. Probed directly:
  removing the *trailing* `\b` does not make `compassion` match either — what
  actually protects it is the value-separator requirement, since `pass` in
  `compassion` is followed by `ion`, not `[:=]`. So `compassion` is **orthogonal
  to `pass`** and is not evidence about it at all. Only `bypass=` and `aPass=`
  discriminate the M2 mutation, which matches the executor's observed "exactly 2
  reds". The near-miss theory is still worth keeping, but two of its seven cases
  are documentation rather than load-bearing, and the last (a bare sentinel with
  no separator) passes trivially. Do not read that theory as 7-way coverage.
- **The baseline figure in my plan was internally inconsistent** (2242 + 28 ≠
  2273). Measured true baseline at `0f0d71a8`: 2245 passed / 2273 total / 28
  skipped. Total and skipped were right; `passed` was off by three. Not caused by
  any of these changes.
- `EveryPreviouslyMaskedKeyIsStillMasked` is a deliberate duplicate of the
  existing `EveryRecognisedKeyIsMasked` theory, kept so the new file states the
  whole key surface in one place. It is not new coverage and should not be
  counted as such.

### New finding from 125's mutation run — the redaction rule fails open

Surfaced accidentally: the executor's first M2 attempt dropped a closing
parenthesis, making the regex invalid. `LogRedaction.Redact`'s blanket `catch`
returned the input **unchanged**, so 30 unrelated tests went red while the
negative guard stayed green. The check reported failure, which is how it was
caught — but the underlying property is real and pre-existing:

**a malformed `Pattern` degrades to "log everything unmasked", silently.** There
is no test, no counter, and no log line distinguishing "nothing matched" from
"the rule is broken". For a security control whose entire purpose is to not leak,
failing open is the wrong direction, and the only reason it was caught here is
that a mutation happened to be malformed rather than merely different.

Not written up as a plan yet. The obvious shape is a compile-time-validated
pattern (or a startup self-check that the rule matches a known-positive sentinel
and a known-negative one, logging loudly if it does not). It needs the same
treatment as any credential change, and it must not be bundled into a fix that
also changes the key list, or the two become indistinguishable. Sequence after
the current set ships.

### 127 approved — `2e8cd273`

Reviewed independently, including a probe of my own that was **wrong** and had
to be redone: counting `NotePerTargetWrite` in a 1400-character window from
`:1318` reported two hits, which looked like a rollback helper had been marked.
Re-measured by method boundary instead — both rollback helpers span `:1323` to
`:1622` and contain **zero** marks; the two hits belonged to `MoveAttribute` and
`RemoveAttribute`. The 6 marks in `StructureService.cs` land in exactly the four
commit-owning methods the plan listed.

Verified in the diff:

- **Pure additions, 0 deletions in all 13 service files** (`--numstat`), so no
  method signature could have changed. Stronger evidence than the plan asked for.
- The consumer pin uses the full statement —
  `"targetMaySkipSpecify = !EditDirtyTracker.IsDirty(kbPath, t);"` — and counts
  go through `RepoSource.WithoutComments` (3 uses, zero `RepoSource.Read(`), so a
  comment cannot satisfy an assertion about a call.
- The negative theory anchors on each method's **declaration signature** rather
  than a raw count. Better than specified.
- `WwpProjectionHelper` marks `parent.Name` on **both** the primary and the
  fallback save, each with the reason inline. This is the user-visible bug: every
  caller marks only the WWP instance, yet the projection unconditionally
  regenerates the `WebForm`, which is itself a build target.
- The decisive mutation is the one worth keeping: planting a mark inside
  `RestoreTransactionSnapshot` left the per-file table **green** (6→7 against a
  minimum of 5) and only the negative layer went red. That is the empirical
  proof that a sweep without the negative theory would have shipped the defect
  this plan exists to prevent.

### The executor corrected my plan twice, the same way the earlier ones did

Both deviations are the executor fixing an imprecision in a table I wrote from a
partial read — the third and fourth time that has happened in this session:

1. **RefactorService.** My table listed nine mark sites, eight of them
   `EnsureSave` calls *inside* the two SDK transactions. Marking there records
   writes that the `catch` at `:712`/`:819` then rolls back — precisely the
   reverted-write defect the plan forbids. The executor marked after each
   `sdkTrans.Commit()` instead, covering the renamed object plus the patched
   callers. My table was wrong; its version is right.
2. **PatternApplyService.** `targetName` is declared at `:1014`, *after* the
   engine apply at `:951`, so it is not in scope at the mark site. The executor
   used the equivalent in-scope expression and noted that marking later would
   also have marked the "existing host detected" branch, which performs no apply.

Judgment calls A and B were resolved as recommended and both are argued in the
commit: `MergeToolService`'s partial is `saved: false` and no caller treats it as
committed, so only the success path is marked; the three partials mark `target`
with the `resolvedObject` divergence recorded in a comment.

Minor correction to my plan: `WritePipeline.cs` lives in `Helpers/`, not
`Services/`.

**Pattern across four executors now:** every time I write a table of facts about
existing code from a grep-selected read, an executor finds at least one row that
is wrong. The plans are still worth writing — three of four deviations were
improvements — but the tables are hypotheses to be re-traced, not specifications
to be trusted. 127's Step 1 exists for exactly that, and it reported **zero
drift** on the rows that survived.

### 113 and 123 unblocked by redesign, not by argument

Both plans had been blocked because their executors proved them wrong. Neither
proof was wrong. Both were resolved by finding out **why** the plan could not
work and rebuilding it on the real mechanism.

**113 — the guard was specified against a field with the wrong lifecycle.**
Revision 1 said "skip entries still in `_byOpenKey`". `RefreshState`
(`KbUseLeaseRegistry.cs:279-286`) removes the `_byOpenKey` mapping at the instant
of expiry, so the guard could never fire. Traced the actual invariant instead:
**every** registry read that can observe a terminal state takes its token from a
live session snapshot (`Program.KbContext.cs:35`, `Program.RequestLoop.cs:74`,
`:77`, `:264`); the only non-snapshot calls are `Open`, which creates rather than
observes. So an unreferenced terminal entry is genuinely unobservable, and
**reachability** is the correct guard. Revision 2 keeps both of revision 1's
published behaviours intact and proves each with a test.

The trap revision 2 had to name explicitly: `RefreshState` is **lazy**, so an
entry that expired and was never touched still reads `State == Active`. A sweep
filtering on `State != Active` silently reclaims almost nothing. Terminality must
be `State != Active || _clock.Now >= ExpiresAt`, and that specific mutation is the
plan's decisive one.

**123 — the fix I had authorised was wrong on the merits.** I had approved moving
the policy parse target to `ToolIdentity.cs`. Reading both files before writing
showed that is not what either is:

- `ToolIdentity.cs` already exists (104 lines) and is *"Single tool-identity
  registry for canonical names, action projections and legacy aliases"* — a
  different concern. Moving read/mutate classification there conflates two
  responsibilities.
- `OperationClassifier.Describe()` already exists at `:506`, and
  `verify_classifier_policy()` (`:218`) already cross-checks the parse against it
  by shelling out to a filtered `dotnet test`.

So the duplication revision 1 described was not the four sets — **they are the
single source of truth, and `Describe` reads them.** The real fragility is that
`_set_block()` requires the exact text `private static readonly HashSet<string>`
*and* the closing brace indented with exactly 8 spaces, so an editor preference
breaks a published generator at release time. Revision 2 makes the parse tolerant
of formatting while keeping it fail-closed, and pins it with three Python tests —
the lane `AGENTS.md` says exists precisely to catch drift the .NET suite cannot
see. **No C# moves.**

**Recorded as a lesson about my own process:** I authorised a change based on a
file's *name* before reading the file. Four plans in this set failed on a variant
of that — a field read without tracing its lifecycle, a set read without reading
its container, a test file declared out of scope after a grep, a function read
without its callers. The rule generalising all five: **a plan may not assert
anything about a file, field or function whose content the plan's author has not
read in full.**

### 123 approved — `8c9c7b2e`

Python-only, as designed. `validate-tool-contracts.py` exit 0,
`generate-operation-contract-inventory.py --check` exit 0,
`python -m unittest discover -s scripts\tests` 95 tests OK, and
`git diff -- docs/operation-contract-inventory.json` **empty** — which is the
check that matters most here: a tolerant parse that changed the inventory would
mean the published artifact had been produced by a parse that disagreed with the
source. It did not. `OperationClassifier.cs` and `ToolIdentity.cs` untouched.

All four mutations confirmed applied and red. Three judgment calls beyond the
plan, all improvements:

- It verified the `ActionContracts` brace-in-literal hazard **does not exist**
  (the one hit is a code brace adjacent to a literal, not inside it), and then
  hardened `_skip_trivia` to consume comments and literals as opaque units so a
  future one cannot shift the boundary. It kept ~20 lines of currently-dead
  verbatim/raw-string branches on the grounds that a matcher which silently
  mis-counts on a literal form is exactly the failure class this plan removes.
- `DryRunCapableActions` joined the uniform path and the duplicated private
  regex was deleted; the byte-identical inventory is what proves this changed no
  classification.
- Test 3 loops over **all six** declarations with `subTest` and asserts the
  raised message names the field, rather than the single declaration the plan
  specified.

**One item honestly reported as not verified, and it is worth carrying:** the
executor could not confirm the C# half of `--check` actually ran in its
unrestored worktree. Its report that `dotnet test` returned 0 with zero output is
odd — `--no-restore` without `project.assets.json` normally fails loudly — so I
checked the property that actually matters instead of the anecdote.

### Second fail-open found — the contract gate's parity half asserts only an exit code

`verify_classifier_policy()` at
`scripts/generate-operation-contract-inventory.py:280-296`:

```python
    result = subprocess.run(
        command, cwd=ROOT, capture_output=True, text=True, check=False, timeout=60
    )
    ...
    if result.returncode != 0:
        raise ValueError("OperationClassifier parity test failed:\n" + output[-4000:])
```

It asserts the exit code and nothing else. It never checks that a test actually
executed. `dotnet test` with a `--filter` that matches **zero** tests exits 0
and prints "No test matches the given testcase filter".

So: if `OperationInventoryClassifierParityTests` were renamed, or its two test
methods deleted, or its namespace changed, this gate would pass green forever
while cross-checking nothing. I confirmed the class exists today
(`OperationInventoryClassifierParityTests.cs:9`, 2 test methods), so **the gate
is not vacuous right now** — this is a latent hole, not a live one.

This is the second fail-open in this set, and the same shape as the `LogRedaction`
one: a control that degrades to "silently do nothing" instead of failing. Here
the control is a release gate whose whole purpose is to catch drift between the
published inventory and the classifier, and `AGENTS.md` is explicit that the
Python suite exists to catch what the .NET lanes are blind to — so a vacuous
green there removes the last line of defence for a published artifact.

The fix is small: parse the console output for a test-run banner and a non-zero
passed count, and raise when either is absent. Same discipline the mutation
checks in this session kept demanding — *a guard that cannot fail is not
coverage* — applied here to the guard itself. Write it up as a plan after the
release; do not fold it into 123, whose change is already validated and whose
inventory is proven byte-identical.

### 113 approved — `3e0003d1`

The core invariant is verifiable in one command and it holds: `git diff` over
`Program.KbContext.cs` and `Program.RequestLoop.cs` is **empty**. Those two files
define `leaseState` and the `KB_LEASE_EXPIRED` recovery, and neither was touched.

Verified in the diff:

- Terminality is the two-clause form the plan demanded:
  `entry.State != KbUseLeaseState.Active || now >= entry.ExpiresAt`. The
  mutation that drops the clock clause turns exactly one test red —
  `AnExpiredButNeverTouchedLeaseIsAlsoReclaimed` — which is the trap the plan
  named, caught.
- Phase one collects terminal candidates under the lock; phase three re-validates
  with the same predicate before removal. No nested locks.
- The ctor stores the predicate as given, so `null` means "assume referenced" and
  an unconfigured registry behaves exactly as before — pinned by its own test.
- **Test 1 exercises the published string end to end**, through
  `Program.GetSessionLeaseState(session) == "expired"` rather than a
  registry-level proxy. Stronger than the plan asked for, and it closes the gap I
  had left open with "say in NOTES which published string you did not exercise".
- Gateway 2251 passed / 0 failed — exactly baseline 2245 plus the 6 new tests.

### A fifth incomplete table, and an honest unverified result

`WorkerPool.cs:194` calls `Validate(snapshot.Lease.Token, ...)` and can raise
`KB_LEASE_EXPIRED`. It is snapshot-sourced, so **the design holds and the fix is
sound** — but my plan presented a four-row table as "the complete set of reads",
and it was not complete. That is the fifth plan in this set where a table I wrote
from a partial read was wrong about existing code, and the count is now: a field
read without tracing its lifecycle (113 rev 1), a set read without its container
(123 rev 1), a test file declared out of scope after a grep (125 rev 1), a
function read without its callers (126 rev 1), and now a call site missed in an
enumerated list (113 rev 2). The rule already recorded below is the whole
mitigation.

Mutation 5 is worth keeping as a precedent. Removing the phase-three
re-validation turns **nothing** red — not in the 6 trim tests, not in the full
2279-test suite — because closing that window needs a thread to renew an entry
between the store query and the removal, which the source does not permit. The
executor **kept the two lines** and reported it as unverified by test, on the
grounds that deleting a documented invariant because no test can fail it is how
the next change drops it quietly. That is the correct call and the correct way to
report it: `unverified`, not `verified`, and not deleted.

---

## Previous audit — 2026-09-09, commit `d77c20f`

The improve audit produced plans 087–109 below. Execute in order where dependencies apply; plans 087–101 are correctness/security/test/DX foundations, 102–105 are performance/architecture/migration, and 106–109 are design spikes. These plans are handoffs only and do not authorize source edits, commits, pushes, releases, or deployments by themselves.

| Plan | Title | Priority | Effort | Depends on | Status |
|------|-------|----------|--------|------------|--------|
| 087 | Restrict preview artifact paths | P1 | S | — | DONE |
| 088 | Remove unsafe cmd.exe browser-driver boundary | P1 | M | 087 | DONE (lint blocked outside scope: cli/lib/config.js:384) |
| 089 | Sanitize client-visible infrastructure errors | P1 | M | — | DONE |
| 090 | Atomically persist KB defaults | P1 | S | — | DONE |
| 091 | Fence multi-target async mutation retries | P1 | M | 090 | DONE |
| 092 | Single-flight cold index loading | P1 | S | — | DONE |
| 093 | Bound Worker MTA concurrency | P1 | M | — | DONE |
| 094 | Add GeneXus SDK CI validation lane | P1 | L | 105 | DONE |
| 095 | Worker crash/respawn/pending RPC tests | P1 | M | — | DONE |
| 096 | End-to-end KB selection route tests | P1 | M | 090 | DONE |
| 097 | CLI multi-client failure-path tests | P1 | M | — | DONE |
| 098 | Authoritative onboarding docs | P1 | S | — | DONE |
| 099 | Reconcile limitations tracking | P1 | M | — | DONE |
| 100 | Document explain compatibility mode | P1 | S | — | DONE |
| 101 | Reuse metadata resolution during search | P2 | M | 092 | DONE |
| 102 | Replace per-start WMI scans | P2 | M | 095 | DONE |
| 103 | Split OperationsRouter into typed modules | P2 | L | 096 | DONE |
| 104 | Decompose Gateway request loop | P2 | L | 103 | DONE |
| 105 | Make SDK compatibility reproducible | P2 | L | — | DONE |
| 106 | Resource navigation and object-aware completion spike | P2 | M | 099 | DONE |
| 107 | Deterministic conversion pipeline spike | P2 | L | 105 | DONE |
| 108 | Safe typed visual authoring spike | P2 | L | 087, 103 | DONE |
| 109 | Capability states and release gates spike | P2 | M | 094, 105 | DONE |

Recommended execution order: 087, 089, 090, 092, 093, 095, 096, 097, 098, 099, 100; then 088, 091, 101, 102; then 103, 104, 105; finally 106–109. Plans 103 and 104 must remain sequential because both alter high-fan-in Gateway routing. Do not mark an SDK/live-KB plan complete when its required environment is unavailable.

## Findings considered and rejected in this audit

- Dependency audit: no high/critical reachable runtime advisory was reported by `npm audit --omit=dev` or the .NET vulnerable-package check; no dependency plan was created.
- Localhost-only unauthenticated development HTTP behavior: documented by-design in `docs/technical_architecture.md`; only implementation-specific risks were planned.
- Existing dual-process .NET 10 Gateway/.NET Framework 4.8 Worker split: required by the GeneXus SDK; the plan targets reproducibility, not architectural replacement.


## Programa 3.0 — 2026-09-05

Nova análise sobre **b3d20f7 / v2.57.0**, preservando o histórico abaixo.
Comece por [073 — Programa 3.0](./073-v3-programa.md): achados com evidência,
arquitetura proposta, prioridades, dependências, critérios de GA e limitações.
Os **13 pacotes 074–086** estão `VERIFIED_INTEGRATED` no manifest para o núcleo
implementado e testado. O relatório [v3-integration-evidence-2026-09-06.md](../docs/v3-integration-evidence-2026-09-06.md)
consolida os comandos e artefatos. Business Components, WWP, multi-KB, replay
de modelo, runner VS Code interativo e soak prolongado continuam explicitamente
fora das capabilities suportadas e são gates pré-GA; ausência de fixture nunca
vira passagem. Esta execução não autoriza publicação.

Plan 094 is implemented in the isolated `agent/plan-094` worktree: hosted CI keeps
its fast Gateway-only path and writes a visible SDK-lane skip, while the protected
`gx-sdk-18` lane validates the locked SDK, runs live Worker checks, and publishes
pass/skip/fail evidence with cleanup. It remains opt-in via
`GXMCP_SDK_CI_ENABLED=true`; no licensed SDK or credentials are checked in.

- [Manifest de execução e status](./v3-execution.json)
- [Corpus de 15 cenários e oráculos](./v3-evaluation-corpus.json)
- 047 é consolidado em 074; 048–049 em 075; tradução/DSO de 050 em 081.
  A consolidação não marca os planos históricos como executados.
- O integrador atualiza o manifest e a tabela do programa após validação do diff.
  Estados: PLANNED, IN_PROGRESS, READY_FOR_REVIEW, VERIFIED_INTEGRATED, BLOCKED.

### Plan 099 — limitations reconciliation

The current limitations register is [`docs/mcp_limitations_tracking.md`](../docs/mcp_limitations_tracking.md).
It separates the 2026-03-25 historical baseline from the evidence-backed current
register, links each capability to provenance/artifacts, and leaves SDK/live gates
explicitly unverified until a qualifying fixture run is recorded. This documentation
reconciliation is complete; it does not authorize release or convert a live gate to
`DONE`.

---

Generated by the `improve` skill audit on 2026-07-10 against commit `b326cd4` (v2.16.1).

This backlog holds the findings that were **deferred** from the audit — the ones
too large, too high-blast-radius, or too research-dependent to implement safely in
the same pass. The audit's low-risk, verifiable findings (SEC-01/02/04/05, all six
BUG-*, TEST-01/DOCS-02, DEP-01, TOOL-02, DOCS-01) were implemented directly on the
`improve/audit-fixes` branch and are **not** in this backlog.

Each executor: read the plan fully before starting, honor its STOP conditions, and
update your row when done.

## Ninth-pass audit (2026-08-10, against `e756dd2` / v2.39.4) — new-code surface since the last cold audit

First pass to audit the code shipped **after** the v2.33.1 cold audit (the C# core's
passes 1–7 ledger stands, nexus-ide was cold-audited in pass 8): ~15,354 lines across
137 files in v2.34→v2.39.4 — write-verification integrity (#59/#70), `ObjectMover`
folder/module placement (#50), SDT/Domain persistence (#51–#57, #64),
`SaveSpecifyOrchestrator` (#60), `ReorgImpactService`/`ReorgSqlPreview` (#61),
`WwpActionService`, `AtomicCreateService`/`AtomicAuthoringService` (#58–#62), async-job
stall watchdog (#79), semantic-cache invalidation, `scripts/mcp_recover.ps1`. Every
finding vetted against live code by the advisor (three subagent line leads corrected).
Verification for the C# plans: `dotnet build Genexus18MCP.sln -v:minimal` + the
Worker/Gateway test suites from the repo root (Worker needs `$env:GX_PATH =
'C:\Program Files (x86)\GeneXus\GeneXus18'` first).

**All five selected by the maintainer (2026-08-10); plans 068–072 written and executed 2026-08-10/11.** Executor implemented all five directly on `main` (no worktrees): full solution builds 0 errors; Worker 1811 passed / 4 skipped; Gateway 880 passed (pre-existing skips only). Each change was also **live-validated against a real KB** (`C:\KBs\KBTeste`) over a scratch Streamable-HTTP gateway on port 5001: 068 returned `PatternTimeout` in 2.014s on the pathological `(a|aa)+$` pattern with the STA thread still responsive; 069 forced a genuine SDK stall past the 1s watchdog bound and observed the wedged worker **recycled** (PID change + `recycledWorker: true`) with the KB answering reads afterwards; 070 returned `deepAnalysis` + `runtimeNote`; 071 returned `GroupUpdated` + `persistedVerified: true`; 072 moved the object via `EntityManager.SaveWithParent` with the hardened resolver binding the 3-arg overload. Not yet released (unreleased in CHANGELOG).

| Plan | Title | Priority | Effort | Risk | Depends on | Status |
|------|-------|----------|--------|------|------------|--------|
| 068 | Bound regex match time on LLM-supplied patterns (search_source + read_logs grep — one pathological pattern wedges the STA worker ~15 min) | P1 | S | LOW | — | DONE |
| 069 | Recycle the wedged worker when the async-job stall watchdog fires (stalled jobs currently leave the KB blocked ~15 min with unusable recovery steps) | P1 | S-M | MED | — | DONE |
| 070 | Give `genexus_db deep=true` (reorg_impact/reorg_preview) a 10-min sync ceiling + expected-runtime note (60s default → spurious timeout while spec keeps running) | P2 | S-M | LOW-MED | — | DONE |
| 071 | Add issue-#59 post-save verification to `GroupStructureService` (membership writes can report false `GroupUpdated`; every other new write path verifies) | P3 | S | LOW | — | DONE |
| 072 | Harden `ObjectMover`'s EntityManager fallback (bare simple-name scan can bind a non-Artech type; constrain to `Artech.*` + log the binding) | P3 | S | LOW-MED | — | DONE |

Recommended order: **068, 069** (P1 availability — regex hang and stalled-worker
recycle are the two ways a single call takes down the whole KB) → **070** (P2 UX) →
**071, 072** (P3 quick wins). File overlap: 069 and 070 both touch
`Program.WorkerLifecycle.cs` (disjoint locations — the skip list / envelope vs
`GetToolTimeoutMs`); if run in parallel worktrees, merge 069 first and re-check 070's
drift excerpt. 068 and 072 both touch `ObjectService.cs` (disjoint: `ReadLogs` grep
vs `MoveObject`); same rule. 068 and 069 are independent (worker vs gateway).

Grounding evidence (each verified against live code at `e756dd2`):
- 068: `SourceSearchService.cs:70` builds `new Regex(pattern, opts)` with no match
  timeout and `App.config` sets no `REGEX_DEFAULT_MATCH_TIMEOUT` (net48 default =
  infinite); `ObjectService.cs:528` `ReadLogs` grep same. Search runs on the single
  STA thread (`CommandDispatcher.cs:379-385` `IsThreadSafe` only for control/index),
  so a catastrophic-backtracking pattern blocks every call to that KB; the 30s
  budget checks between entries, never inside `IsMatch`, and the gateway 60s timeout
  can't interrupt the worker — recovery is only the 15-min wedged kill
  (`WorkerProcess.cs:176-195`).
- 069: `Program.RequestLoop.cs:1784-1802` watchdog fires → `JobRegistry.Stall` →
  `return`, with no worker recycle; the stalled envelope tells the user to re-run the
  edit synchronously, which queues behind the same stuck STA thread. `OnWorkerExited`
  eager-respawn (`Program.WorkerLifecycle.cs:31-150`) skips only
  Idle/GatewayShutdown/BusyReject/ExplicitClose/PlannedReload — `Wedged` is not
  skipped, so a deliberate `StopWithReason(Wedged)` triggers the existing respawn
  loop. `BackgroundJobRegistry.Complete/Cancel` no-op on a `stalled` job, so the
  late "crashed/exited" response can't rewrite the terminal verdict.
- 070: `ReorgImpactService.cs:222,727-746` runs `ISpecifierService.ImpactDatabase`
  ("build-heavy" per its own doc) with no cancellation; `GetToolTimeoutMs`
  (`Program.WorkerLifecycle.cs:624`) has no `genexus_db` case → 60s default.
- 071: `GroupStructureService.cs:131-190` returns `GroupUpdated` after
  `EnsureSave()`+Commit with no re-read of `GroupStructurePart.Members`; contrast
  `DomainWriteService.VerifyEnumValuesPersisted` / `WwpActionService` re-read.
- 072: `ObjectMover.cs:44-46,214-254` `FindFirstType("EntityManager")` scans all
  loaded assemblies by simple name with no namespace filter.

Considered and rejected / downgraded this pass (so nobody re-audits):
- **mcp_recover.ps1**: reviewed clean — proper initialize/session handshake,
  `readOnlyHint` gate before any write (write requires explicit `-AllowWrite`), no
  injection surface (args flow as JSON body, never shell-interpolated). Not a finding.
- **SaveSpecifyOrchestrator `Thread.Sleep(250)` poll loop**: bounded (≤120s, clamped)
  and opt-in (`validationMode=specify`); not worth a plan.
- **Semantic-cache invalidation**: complete with a guard test
  (`SemanticCacheInvalidationTests`); the v2.39.4 fix holds — not re-planned.
- **`PersistenceVerifier` boolean-alias normalization**: correctly gated by
  `allowBooleanAliases`; enum/string values are not collapsed globally. Not a finding.

## Eighth-pass audit (2026-07-23, against `98b9a7d` / v2.33.0) — first independent cold audit of `src/nexus-ide`

The C# MCP core is exhaustively covered by passes 1–7 (its "considered/rejected"
ledger stands and was NOT re-audited). The only un-independently-audited surface is
the `src/nexus-ide` VS Code extension (~8.6k TS lines from plans 051–061 — those were
executor-written and reviewed only by the *executing* advisor, never given a fresh
cold audit). Two parallel read-only Explore subagents (security; correctness) swept it;
every finding was vetted against live code by the advisor before planning. Verification
for all: run from `src/nexus-ide/` — `npm run check` (compile + eslint + `@vscode/test-electron`).

**All six applied & released in v2.33.1 (2026-07-23).** Executed one executor subagent per
plan in isolated worktrees (two waves — 062/063/064/065, then 066/067 rebased on the landed
wave), advisor-reviewed (scope + full diff + tests), and cherry-picked to `main` (commits
`ca7d679`+`43c5fbc` 062, `bfed370` 063, `2dd85c0` 064, `052d756` 065, `5a748a0` 066,
`9a1bb40`+`89f80e2` 067). Consolidated gate on merged `main`: `npm run check` green —
compile 0 errors, eslint 0 errors (63 pre-existing warnings), **100 tests passing**
(suite 76 → 100). 062 took one REVISE round (also escape the no-CSP HistoryView loading
placeholder the plan missed).

| Plan | Title | Priority | Effort | Risk | Depends on | Status |
|------|-------|----------|--------|------|------------|--------|
| 062 | Escape KB-derived strings in Structure/Index/History webviews (close stored-XSS under `unsafe-inline` CSP) | P1 | S-M | LOW | — | DONE |
| 063 | Give the `&var.` member cache a TTL (completions frozen for the whole session today) | P2 | S | LOW | — | DONE |
| 064 | Guard rename against unsaved edits before the KB-side rename (stale rename shown as success) | P2 | S | LOW | — | DONE |
| 065 | Contain gxkb18-URI-derived paths inside the shadow root (path-traversal; `file:` branch already guards, gxkb18 doesn't) | P2 | M | MED | — | DONE |
| 066 | Abort in-flight AI inline-completion HTTP requests on cancel/timeout (leak + pinned status bar) | P2 | M | MED | — | DONE |
| 067 | Two papercuts — guard `variable.type.endsWith` + honor `ReferenceProvider` `includeDeclaration` | P3 | S | LOW | — | DONE |

Recommended order: **062** (security, P1, HIGH-confidence, clean verification) →
**063, 064** (P2 LOW-risk user-visible correctness, clean tests) → **065, 066** (P2
MED-risk — 065 must not reject legitimate nested-module paths; 066 touches the shared
HTTP client) → **067** (P3 quick wins). All six touch disjoint files and can run in
parallel worktrees, **except** 063 and 067 both edit `gxMemberResolver.ts` (disjoint
lines — caching vs the `variable.type` guard); if run in parallel, merge 063 first and
re-check 067's drift excerpt. Grounding evidence:
- 062: `StructureView.ts:266-299`, `IndexView.ts:124-141`, `HistoryView.ts:60-73` interpolate KB fields into `innerHTML`/`webview.html` unescaped under `script-src 'unsafe-inline'`; `PropertiesView.ts` is the safe in-repo reference.
- 063: `gxMemberResolver.ts:41` caches with no expiry; `hoverProvider.ts:8-9,25-29` is the TTL pattern to reuse.
- 064: `renameProvider.ts:14-56` issues `refactor` with no `document.isDirty` guard (the l.107 check only guards post-rename refresh).
- 065: `GxUriParser.ts:168-193` (gxkb18 parse) has no `..`/absolute rejection vs `:122` (file branch) which does; sinks `gxFileSystem.ts:297`, `gxShadowService.ts:126,258,563`.
- 066: `inlineCompletionProvider.ts:91` races a timeout but never aborts the request in `GxGatewayClient.postRawJsonRpc:249-320`.
- 067: `gxMemberResolver.ts:74-75` unguarded `variable.type.endsWith`; `referenceProvider.ts:22` `context.includeDeclaration` never read.

Considered and rejected / downgraded this pass (so nobody re-audits):
- **SEC-03 — DiagramView mermaid `securityLevel` unset** (`DiagramView.ts:36`): INVESTIGATE-ONLY, not planned. Its CSP is nonce-only (no `'unsafe-inline'`), so inline-handler injection is already blocked, and vendored `mermaid@11.16.0` defaults to `securityLevel: 'strict'`. At most a one-line defense-in-depth (`securityLevel:'strict'` + escape `mermaidSource`); revisit only if the mermaid default changes or a click/link-directive vector is confirmed against the vendored bundle.
- **SEC-04 / BUG-06 — unauthenticated localhost gateway + `static activeRequests` fragility**: NOT WORTH A STANDALONE PLAN. Localhost-only unauthenticated dev tooling is by-design (matches the MCP core's documented `Server.HttpPort` loopback contract); the finding is only an *amplifier* to 062, which the escaping fix neutralizes. The `finished`-flag bookkeeping duplication is a smell noted in plan 066's maintenance notes, not a demonstrated bug.
- **BackendManager spawn**: reviewed clean — argument-array `cp.spawn` (no `shell:true`), command/args from fixed extension-relative paths, not KB/request data. Not command-injection.

## Nexus IDE elevation (2026-07-23, against `52e66f1`) — awaiting approval

Bring the `src/nexus-ide` VS Code extension (it has real users) up to the MCP server's
quality bar. Phased program in `docs/nexus-ide-roadmap.md`; recon map in
`docs/nexus-ide-recon.md`. Release is tied to the MCP (versions in lockstep). **Not
executed** — read-only handoffs awaiting go-ahead. Phase 0–1 written now; Phases 2–4
described in the roadmap, to become plans after 0–1 land.

| Plan | Phase | Title | Priority | Effort | Risk | Depends on | Status |
|------|-------|-------|----------|--------|------|------------|--------|
| 051 | 0 | Nexus IDE test + lint/typecheck baseline & gate | P1 | M | LOW | — | DONE |
| 052 | 1 | Honest rename + real reference/definition locations | P1 | M | MED | 051 | DONE |
| 053 | 1 | Resolve SyncManager (wire/delete) + fix mis-wired command | P2 | S-M | MED | 051 | DONE |

Order: 051 (safety net) → 052 (most user-visible "looks-broken") → 053. All build/test
local/self-hosted (`@vscode/test-electron` + GeneXus SDK can't run on GitHub-hosted CI).

**Phase 0–1 applied (2026-07-23).** Executed one plan per executor in isolated worktrees,
advisor-reviewed, cherry-picked to `main` (`4b6b115` 051, `ce10e5c`+`414c005` 052,
`75839a4` 053). **Not released** (tied to the MCP cycle). Validation (real —
`@vscode/test-electron` runs in this environment):
- 051: 34 new unit tests (`GxGatewayClient`, `BackendManager`, providers) + `check` gate; suite 11→45.
- 052: rename now refreshes the editor on success (dirty-doc-guarded); references emit real
  `Range`s (source-scanned, 50-cap + fallback) + within-doc variable refs; suite →54. **Fixed a
  real found-along-the-way bug**: VS Code's word pattern excludes `&`, so variable rename was
  mis-routing to `RenameAttribute` — now detected via a shared `GxVariableToken` helper + routing tests.
- 053: `SyncManager` **wired** (gateway confirmed to emit `notifications/resources/updated` —
  `Program.Notifications.cs:193`), not deleted; dirty-doc-guard test added; mis-wired
  "Explain Code with AI" → relabeled to match `copyMcpConfig` (no real explain command exists yet); suite →56.
- Integrated on `main`: `tsc` 0 errors, `eslint` 0 errors (63 pre-existing warnings). Full
  56-test suite proven green on the combined branch (053 branched off `main`+051+052).
- Follow-up noted: a genuine "Explain Code with AI" command (Phase 3, `genexus_ai_complete`)
  would let 053's relabel become a real binding.

### Phase 2 — robustness / hygiene (written 2026-07-23, awaiting execution)

| Plan | Title | Priority | Effort | Risk | Depends on | Status |
|------|-------|----------|--------|------|------------|--------|
| 054 | Structured, level-gated logging (Logger + migrate ~110 `console.*` + consolidate channels) | P2 | M-L | LOW | 051 | DONE |
| 055 | Audit ~31 bare `catch {}` — stop swallowing real failures (log via 054) | P2 | M | LOW-MED | 054 | DONE |
| 056 | Harden webviews — CSP + local mermaid (DiagramView loads CDN, no CSP) | P2 | S-M | LOW-MED | — | DONE |
| 057 | Fix dev-tree path assumptions in packaged resolution (BackendManager/gxShadowService) | P3 | M | MED | 051 | DONE |

**Phases 2–4 (054–061) applied & released in v2.33.0** (reconciled 2026-07-23 from git:
commits `5b28899` 054, `d90e6ab` 055, `f9dd16c` 056, `30d808f` 057, `d90871e` 058,
`d512694` 059, `d6c2bd4` 060, `e4e66a0` 061; `a92502a` "phases 0-4 complete"). Rows below
kept for provenance.

**Corrected scope** (current-code grep, larger than the recon estimate): ~110 `console.*`
calls across 17 files (recon said 49); ~31 bare `catch {}` (recon said 11); OutputChannel
sprawl (Bootstrap/MCP/Build/SQL/Test/References). **Execute SEQUENTIALLY** (054→055→056→057,
merge between each) — `console.*`/`catch{}` are pervasive so the plans share files; parallel
worktrees would conflict.

### Phase 3 — feature completeness (written 2026-07-23, awaiting execution)

| Plan | Title | Priority | Effort | Risk | Depends on | Status |
|------|-------|----------|--------|------|------------|--------|
| 058 | Real, context-aware inline completion (real `&var.` members + optional AI, drop hardcoded ghost text) | P2 | M | LOW-MED | 051 | DONE |
| 059 | Diagnostics-driven code actions (fix from real diagnostics, not any `&word`) | P2 | M | LOW | 051 | DONE |
| 060 | LayoutView — honest read-only label + sandboxed rendering under a strict CSP | P3 | S-M | LOW-MED | 051 | DONE |

### Phase 4 — release discipline (written 2026-07-23, awaiting execution)

| Plan | Title | Priority | Effort | Risk | Depends on | Status |
|------|-------|----------|--------|------|------------|--------|
| 061 | Fold the Nexus IDE VSIX into `release.ps1` (version lockstep + build + attach to GH release) | P3 | M | MED-HIGH | 051 | DONE |

Phase 3 plans are largely independent (different provider files) but should run after Phase 2
lands (they touch files Phase 2 modified). 061 (Phase 4) touches `release.ps1` — verify via
`-DryRun`, never cut a real release from the plan itself. Everything build/test local/self-hosted.

Grounding: `renameProvider.ts:74` (empty edit), `referenceProvider.ts:29` ((0,0) locations),
`managers/SyncManager.ts` imported at `extension.ts:16` but never registered (dead code),
`gxActionsProvider.ts:56` (label/command mismatch). Extension test/lint/compile infra
already exists (`package.json` scripts + `@vscode/test-electron` + ESLint 9).

## Direction plans (2026-07-23, against `cf736ec` / v2.32.0) — awaiting approval

Forward-looking design/spike plans from a `next`-style direction pass (grounded in
repo evidence, not generic ideation). **These are NOT executed** — read-only handoffs
the maintainer picked to write up; they await an explicit go-ahead before any executor
runs. (Nexus IDE (#6) was scoped as a read-only recon, not a plan — see
`docs/nexus-ide-recon.md`.)

| Plan | Title | Priority | Effort | Risk | Depends on | Status |
|------|-------|----------|--------|------|------------|--------|
| 047 | Live-KB test harness (fixture KB + `GXMCP_TEST_KB` gate) — end the recurring "build-only" coverage hole | P2 | L | LOW | — | TODO |
| 048 | Wire the tool-identity registry (046) into the catalogs + guard test | P2 | M | LOW-MED | 046 (DONE) | TODO |
| 049 | Expand help + `next_legal_actions` coverage across the tool surface | P3 | M | LOW | 048 | TODO |
| 050 | Wire the deferred write paths — translations `CaptionExpression` + DSO (spike) | P3 | M | MED | 047 | TODO |

Dependency notes:
- **048 → 049**: 049 adds catalog coverage using `ToolIdentity` as the authoritative
  tool list + the 048 guard test to keep new entries honest. Land 048 first.
- **047 → 050**: 050 wires SDK writes that can only be *verified* against an opened KB;
  047 provides that harness. Without 047, 050 can only ship build-only (like 023/025/030/043).
- **047** is the highest-leverage of the set: it unblocks real verification for 050 and
  for converting the existing build-only plans to live coverage.

Grounding evidence (why each is real, not speculative):
- 047: plans 023/025/030/043 shipped build-only; `PatternApplyServiceTests`/`PatternParityHarnessTests` TODOs blocked on a fixture KB; the `LiveKbFact`/`GXMCP_TEST_KB` gate already exists.
- 048: 046's `ToolIdentity` prototype + doc already exist; ERGO-01/03 (plans 041/044) were the same drift class fixed twice.
- 049: `ToolHelpCatalog` covers ~10/40 tools; `NextLegalActionsBuilder` covers 7.
- 050: `TranslationsService.cs:58` "SDK write path not wired yet"; AGENTS.md "DSO write ops exist in the SDK but aren't wired."

## Seventh-pass audit (2026-07-23, against `4082fd3` / v2.31.1) — agent-ergonomics + new-code correctness

First `improve` pass to audit the **agent-ergonomics / token-efficiency** surface —
every prior pass (1–6) was scoped to performance + bug-fixing and explicitly skipped
this category, so the perf/bug well is nearly dry and the fresh leverage is here. Two
tracks: (a) advisor-audited the agent-ergo layer directly (output shaping, curated
catalogs, tool-name consolidation drift); (b) one read-only subagent swept the
correctness of code ADDED since v2.29.3 (`f63f204..HEAD`: issue #45/#46 variable-type
work, gxserver "ignored objects", BuildService additions). Every finding was vetted
against live code by the advisor — three subagent line-attributions were wrong and
corrected before planning.

**All seven applied (2026-07-23).** The maintainer authorized apply-all; each plan was
executed by a separate executor subagent in an isolated worktree, advisor-reviewed
(scope + diff + tests), and cherry-picked to `main` (commits `3b7ce81`, `c5eba73`,
`43d5a5c`, `d48e1aa`, `280412d`, `cef140f`, `f33e7ab`). Consolidated gate on merged
`main` green: solution builds 0 errors; Gateway 697 passed / 7 skipped; Worker 1578
passed / 4 skipped. **Not released** (unreleased in CHANGELOG `[Unreleased]`).
(Two earlier agent-ergo fixes shipped this session on `main` at `4082fd3`: fail-loud
typo'd-arg validation + content-first `genexus_api` default — done, not in this backlog.)

| Plan | Title | Priority | Effort | Risk | Depends on | Status |
|------|-------|----------|--------|------|------------|--------|
| 040 | Close the build "already running" TOCTOU race (`_inFlightBuilds` registered async in `RunBuild`) | P1 | S | LOW | — | DONE |
| 041 | Restore `next_legal_actions` for the consolidated create family (builder keyed on legacy tool names) | P1 | S-M | LOW | — | DONE |
| 042 | Make aggregates + empty-state universal (enrichment only fires for 8 fixed top-level keys) | P2 | M | LOW-MED | — | DONE |
| 043 | Restore SDT/BC/built-in bindings in modify-variable rollback (only primitives restored today) | P2 | M | MED | — | DONE |
| 044 | Key tool-help by canonical names + resolve legacy aliases (help unreachable by canonical name) | P3 | S | LOW | — | DONE |
| 045 | De-quadratic the `referencedButNotBuilt` evidence scan (`checkList.Any` in nested loops) | P3 | S | LOW | — | DONE |
| 046 | Design a single tool-identity registry (SPIKE — durable fix for 041 + 044's shared root cause) | P3 | M | LOW | informed by 041, 044 | DONE |

Recommended order: **040, 041** (P1 — build-correctness + a silently-dead ergo
feature) → **042, 043** (P2) → **044, 045** (P3 quick wins) → **046** (design spike,
do after 041/044 so the doc reflects the tactical fixes it subsumes). All seven touch
disjoint files and can run in parallel worktrees; 046 is a doc/spike and changes no
runtime behavior.

Dependency notes:
- **041 and 044 share a root cause** (curated catalogs keyed on pre-consolidation tool
  names). They are independent tactical fixes; **046** is the design spike for the
  durable single-source-of-truth registry that would subsume both and add a guard test
  so the drift can't recur. Land 041/044 first, then let 046's doc reflect them.

Considered and rejected this pass (so nobody re-audits):
- **ERGO-04 — extend minimal-schema field projection beyond `genexus_query`/`genexus_list_objects`**:
  NOT WORTH DOING. Every other list-returning tool (modules, versions, endpoints,
  controls, …) returns small collections (<~100 items); the compact-field savings are
  marginal and the risk of stripping a field an agent needs is real. Revisit only if a
  new tool starts returning large per-item shapes. (Aggregates/empty for those tools IS
  worth it — that's plan 042.)
- **SaveAsService clone-loop "success with partsSkipped"** (`SaveAsService.cs:147-170`):
  BY-DESIGN, not a bug. The v2.31.0 changelog documents that a per-part clone failure
  is now non-fatal and surfaced under `created.partsSkipped`; the subagent flagged it
  only as a tradeoff to confirm. `partsSkipped` is surfaced in the envelope. No change.
- **The entire historical perf/bug backlog** (plans 001–039, 6 passes): DONE or
  already-rejected. This pass did not re-audit index build, dispatch, decomposition,
  concurrency, or the SDK-endpoint services — they are covered.
- **TOON output on the MCP path**: deferred by the maintainer (2026-07-23) — the one
  remaining big token lever, invasive (breaks the documented JSON-in-JSON contract +
  ~900 tests). Do it later as a dedicated opt-in design (per-call `format=toon`,
  default json). Not planned here.

## Sixth-pass audit (2026-07-21, against `f63f204`) — performance + bug-fixing only

Third focused `improve` pass of the day, scoped to **performance and correctness bugs
only**, against tip `f63f204`. Three parallel read-only audits over the surface NOT
deeply reviewed in passes 1–5: perf in un-audited worker services; worker correctness;
gateway helpers/routers + the Node CLI. Nine findings vetted against live code; one
rejected, one deferred (below). Maintainer authorized auto-apply + commit to `main`;
**released in v2.29.3**.

| Plan | Title | Priority | Effort | Risk | Depends on | Status |
|------|-------|----------|--------|------|------------|--------|
| 032 | `CallerGraphService.GetCallees` drops per-candidate regex compile (mirror of 022) | P1 | S-M | LOW | — | DONE |
| 033 | `KbValidationService` passes known type to `FindObject` (O(n²) → O(n)) | P2 | S | LOW | — | DONE |
| 034 | `SourceParser.SkipString` uses GeneXus doubled-quote grammar, not backslash | P1 | S | LOW | — | DONE |
| 035 | `BrowserDriverInvoker.ResolveDriverPath` drains stderr (pipe-deadlock fix) | P2 | S | LOW | — | DONE |
| 036 | Wire `BackgroundJobRegistry.SweepExpired()` + seen-set prune into cleanup loop | P1 | S | LOW | — | DONE |
| 037 | `KbWatcherService` must not touch the SDK from a second STA thread | P2 | M | MED | — | DONE |
| 038 | `AutoTypeInjector` name→type cache scoped per KB (cross-KB contamination) | P2 | M | MED | — | DONE |
| 039 | `GeneratedDiffService.FindGeneratedFiles` walks each root once (not per-ext) | P3 | S | LOW | — | DONE |

All eight merged to `main` (commits `944cfd0`,`24a914b`,`5232a5c`,`5a6b7d2`,`abc362b`,`72653ac`,`d63feba`,`df5e8a6`); **released in v2.29.3**. Full suites green: Worker 1529 passed / 4 skipped; Gateway 660 passed / 7 skipped; solution builds 0 errors. Advisor-reviewed each diff; notable review actions:
- **037** (MED): executor found no pre-existing "post a job onto the command STA thread"
  API, so it added a minimal additive `Program.SdkActionQueue` drained by the existing
  sdkWorker poll tick **only when the command queue is idle** (non-starving, strictly
  lower priority), and routed `KbWatcher` onto it with a bounded 15s wait on its own
  thread. Serialization verified (same STA thread as `ProcessCommand`); no deadlock;
  timeout→dispose→`Set()` is try/catch-guarded. Accepted tradeoff: `CheckForChanges`
  now runs on the command thread when idle (correctness over the COM-corruption it
  replaces). In-scope (`Program.cs` + `KbWatcherService.cs`).
- **038** (MED): grep found a third caller (`McpRouter` completion), so the change also
  touched `McpRouter.cs` and added a 1-line `Program.GetCurrentKb()` accessor (mirroring
  the existing `GetWorkerPool`/`GetKbResolver`) — covered by the plan's "update any
  caller found via grep" clause. Only the name→type cache is KB-keyed; the schema-shape
  cache stays global. `whoami`'s feeder resolves current-KB → single-open-KB → skip
  (never mis-attributes across KBs). Single-KB behavior byte-identical; 4 new + 15
  adapted tests pass.
- **Test caveats (honest)**: 033, 035, 037 shipped **build-only** for their new behavior
  (all need a live opened KB/SDK to exercise; each plan's Step allowed this fallback).
  037 additionally relies on the full-suite run + the mechanism's structural guarantee
  (single-consumer STA thread). 032, 034, 039 got new passing unit tests; 036, 038 got
  new gateway tests.

Order: LOW-risk first (033, 034, 032, 035, 036, 039) then MED-risk (037, 038). All
independent (disjoint files). Tier-A LOW-risk ships on green tests; 037/038 ship only on
a clean diff + green full suite (037 STOPs and defers to human design if the dispatcher
STA-queue seam isn't cleanly reusable).

Considered and rejected/deferred this pass (so nobody re-audits):
- **`HistoryService.SaveSnapshot` `"error"` substring check** — REJECTED. `ReadObjectSource`
  returns a `JObject`-serialized payload, so any `"` in the source is escaped to `\"`;
  the bare substring `"error"` can only false-positive if an object's entire Source is
  literally the 5 chars `error`. Not reachable in practice.
- **CLI `kb add/remove/switch` config read-modify-write race** (`cli/lib/config.js`) —
  DEFERRED. Real lost-update across concurrent CLI processes (`writeFileAtomic` prevents
  torn writes but not the RMW race), but the commands are rare and user-invoked, and a
  robust fix needs cross-process advisory locking with stale-lock/TTL handling that
  fights the package's zero-runtime-dependency constraint — cure risks worse than the
  disease. Revisit if it actually bites (e.g. scripted multi-KB registration).
- Areas reviewed clean: `GatewayProcessLease` (named Mutex + atomic lease), `CrashLedger`
  (single-writer, lock-guarded), `OperationTracker` (per-record locks; sweep already
  wired), most of `ObjectService`/`AnalyzeService`/`PropertyService` (index-keyed or
  single-object-scoped). Next-pass candidates if needed: `Program.Http.cs` SSE loop,
  `KbHandle.cs`/`Configuration.cs`.

## Fifth-pass audit (2026-07-21, against `00573c3` / v2.29.2) — performance + bug-fixing only

Second focused `improve` pass of the day, again scoped to **performance and
correctness bugs only**, against tip `00573c3`. Three parallel read-only audits
(perf hot-paths in not-yet-reviewed worker services; correctness/concurrency in the
gateway; correctness in the v2.27–2.29 SDK-endpoint services + high-churn core).
Ten findings vetted against live code by the advisor. Maintainer authorized
auto-apply + commit to `main`; **released in v2.29.3**. Executed one executor per plan in
isolated worktrees, advisor-reviewed, merged the passing ones to `main`.

| Plan | Title | Priority | Effort | Risk | Depends on | Status |
|------|-------|----------|--------|------|------------|--------|
| 022 | `CallerGraphService.GetCallers` single index pass (impact-analysis hot path) | P1 | S-M | LOW | — | DONE |
| 023 | `ResolveWWPInstance` resolves WWP host by name, not full-KB scan (every WebForm/Layout edit) | P1 | S | LOW | — | DONE |
| 024 | `genexus_gam` define_api/deploy require `confirm=true` | P1 | S | LOW | — | DONE |
| 025 | `CiPipelineService` surfaces run/abort failures as errors, not "not connected" | P1 | S | LOW | — | DONE |
| 026 | `BackgroundJobRegistry` guards status transitions with a per-job lock | P1 | S | LOW | — | DONE |
| 027 | `MultiAgentLockService` writes the lock file atomically | P2 | S | LOW | — | DONE |
| 028 | `IdempotencyCache` evicts per-key gates (unbounded-growth fix) | P2 | S | MED | — | DONE |
| 029 | `CrossPlatformImpactAnalyzer` name→entry map instead of linear scan | P3 | S-M | LOW | — | DONE |
| 030 | `RefactorService` rename atomic (transaction around patch+rename) | P2 | M | MED | — | DONE |
| 031 | `worker_reload mode=hard` keeps drain window closed to concurrent spawns | P2 | M | MED | — | DONE |

All ten merged to `main` (commits `28e38fc`..`16646a5`); **released in v2.29.3**. Full
suites green: Worker 1524 passed / 4 skipped; Gateway 654 passed / 7 skipped; solution
builds 0 errors. Advisor-reviewed each diff; notable review actions:
- **028**: first cut disposed the evicted `SemaphoreSlim` — sent back (REVISE); a
  concurrent holder mid-`WaitAsync` could hit `ObjectDisposedException` on `Release()`.
  Final version removes the entry without disposing (no OS wait handle is ever
  allocated), commit `8e67972`.
- **031**: executor correctly found the real entry-removal path was `OnWorkerExited`
  firing synchronously from `StopProcess` (deeper than the plan's cited line); fix
  makes that handler skip removal while `Draining`, keeps the entry across the swap,
  and installs a fresh `DrainComplete` TCS per drain cycle. Documented, in-scope
  (`WorkerPool.cs` only), approved on merit.
- **Test caveats (honest)**: 023, 025, and 030 shipped **build-only** for their new
  behavior — `ResolveWWPInstance`/`RunPipeline`/rename all require a live opened KB +
  SDK to exercise, so a unit failure-injection test would need an SDK harness the
  suite doesn't have (each plan's Step 3 explicitly allowed this fallback). Their code
  was advisor-read against the plan intent and the existing per-file suites stayed
  green. 022/024/027/029 and 026/028/031 got new passing tests.
- **029 out-of-scope leftover**: `PatternAnalysisService.GetWWPStructure` still has a
  `model.Objects.GetAll()` scan (line ~215) and a now-slightly-stale comment (~line
  39); both are out of 023's scope and were deliberately left untouched (surgical).

Recommended execution order: 024, 025, 026 (P1 correctness) → 022, 023 (P1 perf) →
027, 028, 031, 030 (P2) → 029 (P3). All ten are independent (different files, no
shared edits) and can run in parallel worktrees. Tier A (022–027) is LOW-risk and
ships on green tests; the MED-risk set (028, 030, 031) ships only if the executor
diff is clean and the full suite stays green — otherwise left TODO for manual review.

Considered and rejected this pass (so nobody re-audits):
- No new god-object races: `WorkerPool` (beyond 031), `McpRouter`, `Program.Http`,
  `HttpSessionRegistry`, `CrashLedger`, `GatewayProcessLease`, and `OperationTracker`
  locking all reviewed clean.
- The ~14 other v2.27–2.29 SDK-endpoint services (`SecurityScanService`,
  `KbStatsService`, `TableRelationsService`, `ReorgImpactService`,
  `DesignSystemService`, `CurlProcService`, `UserControlsListService`, `BlameService`,
  `TimeTravelService`, `JsonPatchService`, `ConversionService`, `ExportObjectService`,
  `AutoTestService`, `InjectionService`) are defensively written — no verified new bug.

## Fourth-pass audit (2026-07-21, against `4885c1c` / v2.29.1) — performance + bug-fixing only

Focused `improve` pass scoped to **performance and correctness bugs only** (no
security/tech-debt/docs), against the current tip `4885c1c`. Three parallel
read-only audits: perf hot-paths, bugs in the v2.27–2.29 SDK-endpoint services,
and bugs/concurrency in the high-churn core. Every finding was vetted against the
live code by the advisor before planning. Prior passes had already fixed the big
index/concurrency items, so fresh findings concentrate in code added after the
last audit cutoff. All five were implemented (one executor per plan in isolated
worktrees, advisor-reviewed) and released in **v2.29.2**.

| Plan | Title | Priority | Effort | Depends on | Status |
|------|-------|----------|--------|------------|--------|
| 017 | Worker reaps cancel `_cts` (idle/heap/wedged task leak) | P1 | S | — | DONE |
| 018 | `search_source` metadata-field branch O(n²) → O(n) + honor `objectName` scope | P1 | S | — | DONE |
| 019 | `BuildService.Cancel()` mutates task status under `status._lock` | P2 | S | — | DONE |
| 020 | `design_system` (no name) uses TypeIndex instead of full-KB COM scan | P3 | S | — | DONE |
| 021 | Input-validation hardening for the 4 new-service papercuts | P2 | S | — | DONE |

No dependencies between 017–021; execute in priority order (017, 018, 019, 021, 020).

Recommended order rationale: 017 (recurring leak) and 018 (quadratic scan) are
the real-impact fixes; 019 (cancel race) is a real but lower-frequency bug; 021
bundles four low-severity papercuts; 020 is a rare-path micro-optimization.

Considered and rejected this pass (so nobody re-audits):
- **Idempotency-cache TOCTOU** (`IdempotencyCache.TryServe`/`BeginInflight`):
  inert — every mutating/SDK command is funneled onto the single STA
  `SdkCommandQueue` thread (`Worker/Program.cs`), so only harmless read-only
  commands can reach the parallel `Task.Run` path. Not a bug.
- **`OperationTracker.BuildMetricsSummary` dirty read** of `LastError`/`UpdatedAtUtc`
  outside the record lock: reporting-only rollup, not correctness-critical. Skip.
- **`KbStats`/`ReorgImpact` duplicated `reorgLikelyNeeded` heuristic**: tech-debt,
  not a bug, and this pass excludes tech-debt. Logic is correct for all cases.
- No new full-KB-scan-where-index-exists, redundant-reserialization, or O(n²)
  patterns beyond 018/020. WorkerPool, McpRouter, Program.Http, PatchService, and
  all of WriteService (7.6k lines) reviewed clean for concurrency.

## Third-pass audit (2026-07-20, against `9fe6817` / v2.29.0)

A follow-up `improve` audit ran after v2.29.0, focused on the recently-added
SDK-endpoint expansion (v2.27–2.29) and the v2.29.0 reliability batch. Five findings
became plans 012–016. All five are LOW-risk / HIGH-confidence and were applied +
released in v2.29.1 in the same pass.

| Plan | Title | Priority | Effort | Depends on | Status |
|------|-------|----------|--------|------------|--------|
| 012 | MSBuild reap operates on a disposed Process (v2.29.0 regression) | P1 | S | — | DONE |
| 013 | Confine `genexus_screenshot_publish` to image files under an allowed root | P1 | S | — | DONE |
| 014 | Redact JSON-RPC request bodies in the gateway HTTP log | P2 | S | — | DONE |
| 015 | Guard tests for the 10 new SDK-endpoint services + fail-fast confirm gates | P1 | M | — | DONE |
| 016 | Extract the duplicated "resolve design model or NoKbOpen" helper | P2 | S | 015 | DONE |

Dependency: **016 requires 015** — 015's `NoKbOpen` guard tests are the safety net
that proves 016's extraction preserves behavior. Land 015 first.

Rejected this pass (so nobody re-audits):
- **TECHDEBT-02 — hand-rolled `if(action==)` dispatch chains** in `DeployService` /
  `CiPipelineService`: NOT WORTH DOING NOW. The files are small; a shared
  `ActionDispatcher` only pays off once a third destructive-action service exists.
  Revisit then.
- **Performance**: no new hotspot found this pass. The prior PERF-01/02, plans
  002/003/006, and the rejected 004 (no batched SDK accessor) still stand.

## Execution order & status

| Plan | Title | Priority | Effort | Depends on | Status |
|------|-------|----------|--------|------------|--------|
| 001 | Index flush-count instrumentation + regression test | P1 | S | — | DONE |
| 002 | Secondary type/domain indexes for search & list | P1 | M | — | DONE |
| 003 | Incremental / sharded index flush | P2 | L | 001 | DONE |
| 004 | Batch COM property reads in the lite index walk | P2 | L | 001 | REJECTED |
| 005 | Replace CommandDispatcher switch with a dispatch table | P2 | M | — | DONE |
| 006 | Shared filter-predicate builder for SearchService/ListService | P2 | S-M | 002 | DONE |
| 007 | Decompose WriteService (6982 lines) | P3 | L | 009-style char tests | DONE |
| 008 | Decompose Gateway Program.cs (5657 lines) | P3 | L | — | DONE |
| 009 | BuildService characterization test suite | P2 | M | — | DONE |
| 010 | Repo-wide JS/TS lint + editorconfig, ESLint 9, central pkg versions | P3 | S-M | — | DONE |
| 011 | Trim ToolSchemaSizeTests comment history | P3 | S | — | DONE |

Status values: TODO | IN PROGRESS | DONE | BLOCKED (reason) | REJECTED (rationale)

## Dependency notes

- **003 requires 001** — the incremental-flush rewrite is the exact kind of change
  that can silently reintroduce the "full re-serialize per tick" cost. Land the
  flush-count regression test (001) first so 003 has a safety net.
- **004 requires 001** — same safety-net argument; it also changes the warm-start
  timing the flush test observes.
- **006 pairs with 002** — both touch the Search/List filter chain; do 002's index
  first, then factor the shared builder so it can consult the new indexes.
- **007 wants characterization tests first** — do not decompose WriteService without
  a test net around its ~34 public entry points. 009 is the model for how to build
  one; apply the same approach to WriteService before starting 007.

## Second-pass audit (2026-07-10, against `ee09820` / v2.17.0)

A follow-up audit ran after v2.17.0. Its safe, verifiable findings were fixed
directly (see the `Unreleased` CHANGELOG entry): the worker-crash-retry operation
status leak, the warm-spare concurrency bug, the stale `GENEXUS_MCP_CACHE_DIR`
troubleshooting entry, the missing env-var reference doc, and the untested `/mcp`
auth path. The findings below were **deferred** — MED-risk or needing per-site
verification — and should become numbered plans (012+) before execution:

- **PERF-01 — O(n²) parent-children index insert. DONE (Unreleased).** Fixed by adding a
  companion `ChildKeysByParent` key-set to `SearchIndex` for O(1) dedup in
  `AddOrUpdateEntryInParentIndex`, maintained alongside the list in `BuildParentIndex` and
  `RemoveEntryFromParentIndex` under the same per-list lock. Readers untouched (the set is
  `[JsonIgnore]`, rebuilt from `Objects`). Regression coverage in `ParentIndexDedupTests`.
- **PERF-02 — full index scan per non-quick search. DONE (Unreleased).** Rather than the
  fragile hand-maintained counter (a missed mutation site would make the flag lie),
  `HasPendingEnrichment()` now caches its result against the existing `_dirtyGeneration`
  mutation counter: a cache hit means nothing changed since the last scan, so the answer is
  reused; any mutation forces a rescan (which early-exits at the first un-enriched entry).
  A missed generation bump can only cost an extra scan, never a wrong cached answer.
  Regression coverage in `EnrichmentPendingCacheTests`.
- **BUG-03 — a hung (not crashed) worker is never idle-reaped. DONE (Unreleased).** Fixed
  by tracking per-in-flight-command start timestamps on `WorkerProcess` and force-stopping
  (`WorkerStopReason.Wedged`) from the health check when the oldest exceeds
  `Server.WedgedCommandTimeoutMinutes` (default 15 min). Idle-reap behavior unchanged;
  workers with no in-flight command never trip it. Tests: `WorkerWedgedDetectionTests`.
  Original finding for reference: `WorkerProcess._inFlightCommands`
  only decrements on a real worker response or a write failure; a gateway-side operation
  timeout updates the tracker but not the counter, and `ShouldStopForIdle` refuses to reap
  while in-flight > 0. A worker wedged on an SDK call (never exits) holds its slot forever.
  Needs a per-in-flight-command deadline map + a hard ceiling in the health check that
  force-stops past it — with a threshold well above legitimate long builds. Confirm real
  frequency from logs before investing.
- **TECHDEBT-01 — consolidate path-containment / make-relative helpers.** "Is this path
  inside the KB root?" is reimplemented in `AssetService`, `BlameService`, `TimeTravelService`
  (three variants) and *absent* at `IndexCacheService.cs:460`, `ObjectService.cs:1496,1528`.
  Extract one `PathSafety` helper + tests (trailing-slash/case/UNC), then replace call sites.
  Verify each currently-unchecked site actually takes untrusted input before adding a
  containment check that could reject a legitimate path.
- **TECHDEBT-02 — normalize the error envelope across ~16 services. PARTIALLY DONE.**
  Normalized (now emit via `McpResponse.Ok`/`Err`, regression tests added, full suite green):
  `ForgeService.Scaffold` (success + catch); `VersionControlService.GetPendingChanges/Update/Commit`
  (all `NoKb`/catch sites); `ObjectService.CreateObject` (`NoKb`, `UnsupportedObjectType`,
  `AlreadyExists`, catch) and `ObjectService.WorkerReload` (`Accepted`→canonical `accepted`,
  catch); `KbExplorerService.Locate` (missing-name, catch); `BlameService.Blame` (`KbNotInGit`,
  `PartNotTracked`, `GitFailed` ×2, `PathOutsideRepo`, `FileNotFound`).
  SKIPPED — observable, needs a product decision before touching:
  - `BlameService.Blame`'s `NoKb`/`missingArgs` sites (still the old private `Error()` helper):
    `BlameServiceTests` reads the top-level `code` field directly; canonical `Err` only nests
    it under `error.code`, so normalizing would drop a field the test depends on.
  - `KbExplorerService.Locate`'s `NotFound` site: `KbExplorerServiceTests` reads top-level
    `code` *and* top-level `name`; canonical shape nests `code` under `error` and has no
    top-level `name` slot.
  - `BatchService`'s three per-item `{error: rawString}` fallbacks (used only when a sub-call's
    result string fails to parse as JSON) — flagged as possibly-intentional in the original
    audit; left untouched.
  - `CommandDispatcher.TryCaptureWarmSnapshot`'s `{saved:false, error:...}` sub-object — not a
    top-level response envelope, it's a documented micro-schema stitched into another envelope
    under a `warmSnapshot` key; converting it to nested `McpResponse.Err` would be a worse fit,
    not a normalization.
  `Program.cs` and `IndexCacheService.cs` untouched per assignment guardrails (other work in
  flight there) — their hand-rolled shapes (soft-reload ack, etc.) remain for a future pass.
- **TECHDEBT-03 — additional god objects. PARTIAL (Unreleased).** Applied the same verbatim
  `partial class` split as 007/008. Done so far: `LayoutService` split into `.MutatorScan.cs`,
  `.VisualContext.cs`, `.SourcePersistence.cs`; `PatternApplyService` pattern-engine adapter
  types extracted into `PatternEngineAdapter.cs`. Full Worker suite green after each step.
  REMAINING (not yet split, safe to do later with the same method): the cores of
  `PatternApplyService.cs`, `ObjectService.cs`, `AnalyzeService.cs`, `PatchService.cs`.
- **TEST-02 — replace source-text guard tests with behavioral tests.** ~19 test files
  `File.ReadAllText` a `.cs` and `Assert.Contains` a string literal instead of exercising the
  code path (this is the root of the flagged flaky `Dispatcher_PatchApply_ValidateOnly`).
  Triage per-file; for the validate-only/dry-run mapping specifically, add a real
  `CommandDispatcher.Dispatch(validate=only)` test asserting no persistence.
- **DIR-01 — finish or shelve the v2.8.0 canonical-envelope cleanup. DONE.** Traced each dual-shape
  fallback to its producer: `BatchService.MultiEdit` and `ObjectService.ImportObjectFromText`'s
  create/write legs all call producers (`BatchEdit`, `WriteService.WriteObject`, `CreateObject`'s
  success path) that emit exclusively via `McpResponse.Ok`/`Err` — legacy branches removed, TODOs
  deleted, tests added (`BatchServiceTests`, `ObjectServiceImportTests`). `FeatureScaffoldService.IsOk`
  fallback stays: `Dispatcher.Invoke` can reach any tool, and `ForgeService`/`VersionControlService`/
  `ObjectService.WorkerReload`/`Program.cs` soft-reload still hand-roll legacy PascalCase statuses
  (`Success`/`Accepted`/`Error`) today — comment now names them instead of a bare `TODO(v2.8.0)`.
- **BUG-04 — `genexus_worker_pool action=warm_spares` reports empty results. DONE (Unreleased).**
  Fixed by making `ConfigureWarmSpares` async and awaiting the pre-spawns (bounded by a 10s
  `WarmSpareAwaitCap`) before building the result; spawns still running past the cap are
  reported as `Skipped` for that call but keep running in the background. Tests:
  `WarmSpareConfigurationTests`. Original finding for reference:
  `WorkerPool.ConfigureWarmSpares` (`src/GxMcp.Gateway/WorkerPool.cs:334-384`) fires the
  pre-spawns fire-and-forget, then returns `Prespawned`/`Skipped` synchronously — before
  any spawn's continuation runs — so the tool almost always reports nothing pre-spawned even
  though workers are coming up in the background. (The v2.17.0-followup `ConcurrentBag` change
  fixed the concurrent-write hazard on those lists but not this reporting gap.) Fix needs a
  bounded await of the spawn tasks before building the result, or a contract change to report
  "scheduled" synchronously and actual outcomes via `whoami`/`lifecycle`. MED risk: awaiting
  inline changes the tool's fast/fire-and-forget response-time characteristic — cap it.

## Findings considered and rejected

- **004 (batch COM property reads)**: REJECTED — superseded / not worth doing. A spike found
  the GeneXus SDK exposes no batched property accessor for the lite walk, and the existing
  delta-index system already avoids the redundant reads the plan targeted. Prior measurement
  also put the lite-walk COM reads at ~0.72ms/obj (not the cold-start bottleneck); the real
  flush cost was addressed by plan 003 (sharded flush). Revisit only if a batched SDK accessor
  appears or profiling re-implicates per-object COM reads.
- **SEC (kb_import/kb_diff arbitrary-directory resolve)**: NOT A FIX HERE — by-design.
  `ResolveKbPath` (`Program.cs:4930`) intentionally falls back to any existing directory
  because `genexus_kb_diff kbA/kbB` and `genexus_kb_import to` are **documented** as
  `<alias-or-path>` (AGENTS.md; v2.17.0 changelog). v2.17.0 already hardened the `name`/`type`
  segments + containment within the resolved root. Narrowing the resolver to declared
  aliases only would change a documented public-API contract, so it needs an explicit
  product decision, not a silent audit fix. Revisit if the alias-or-path contract is ever
  dropped.
- **DEP-01 (Node lockfile + `npm audit` CI step)**: NOT WORTH DOING NOW — `package.json`
  has zero runtime and zero dev dependencies, so a lockfile captures nothing and `npm audit`
  audits nothing. Revisit the moment any dependency is added (then generate the lockfile and
  add an `--audit-level=high` CI step in the same change).
- **SEC-03 (multi_agent_lock arbitrary path)**: REJECTED — not reachable. The audit
  flagged `kbPathOverride` flowing from tool args into `Path.Combine`, but
  `CommandDispatcher.cs:1506` hardcodes `kbPathOverride: null` on the only dispatch
  path, so `MultiAgentLockService.ResolveKbPath` always falls back to the open KB's
  path. The override parameter is an internal/test seam, never LLM-controlled. No fix
  needed; recorded so it isn't re-audited. (If a future change ever wires an
  override to tool args, revisit — validate it against the open-KB set then.)
- **PERF-03 / TECHDEBT-04 "confirm the divergence"**: SearchService and ListService
  filter chains differ slightly (type-match helper vs raw Contains). Before 006
  commonizes them, an executor MUST diff the two behaviours and preserve or
  deliberately reconcile them — do not assume they're identical.
