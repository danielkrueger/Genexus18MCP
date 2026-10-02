# Plan 120: Document every environment variable the code actually reads

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise.
>
> **Drift check (run first)**: `git diff --stat 0f0d71a8..HEAD -- docs/environment_variables.md src`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: dx
- **Planned at**: commit `0f0d71a8`, 2026-10-01

## Why this matters

`docs/environment_variables.md` is the only place an operator can discover that
a variable exists, and it currently omits 48 of the 108 environment variables
the code reads. The omissions are not random — they include the five
TeamDev credential variables and the three that disable a watchdog or force a
failure. An operator whose build wedges cannot find `GXMCP_INDEX_NO_PROGRESS_SEC`
or `GXMCP_ASYNC_JOB_WATCHDOG_S`; an operator cannot find that a TeamDev
password can be supplied without putting it in the tool arguments.

The doc carries its own unenforced rule at `:206-208` — "when you add a new
`GXMCP_*` variable, add a row here" — and the only test that reads it asserts
three phrases about one variable. This plan adds the missing rows and makes the
rule enforceable.

## Current state

Measured at commit `0f0d71a8`: 108 distinct names are read via
`GetEnvironmentVariable("<NAME>")` under `src/`; 48 of them do not appear
anywhere in `docs/environment_variables.md`. Confirmed missing:

- **Credentials / auth** (read by
  `src/GxMcp.Worker/Services/GxServerWriteService.cs:516,520,522,524,555`):
  `GXMCP_TEAMDEV_URL`, `GXMCP_TEAMDEV_USER`, `GXMCP_TEAMDEV_PASSWORD`,
  `GXMCP_TEAMDEV_TOKEN`, `GXMCP_TEAMDEV_AUTHTYPE`. The doc's only TeamDev
  entry is `GXMCP_TEAMDEV_PENDING_NAME` (`:125`).
- **Watchdogs / fail-forcing**:
  `GXMCP_INDEX_NO_PROGRESS_SEC` (`Services/IndexBuildWatchdog.cs:35`),
  `GXMCP_ASYNC_JOB_WATCHDOG_S` (`src/GxMcp.Gateway/Program.WorkerLifecycle.cs:1286`),
  `GXMCP_IDLE_GC` (`src/GxMcp.Worker/Program.cs:1074`).
- **Diagnostics**: `GXMCP_CRASH_LEDGER_PATH` (`src/GxMcp.Gateway/CrashLedger.cs:44`),
  `GXMCP_SOURCE_ENCODING` (`src/GxMcp.Worker/Compatibility/DynamicSdkBridge.cs:135`),
  `GXMCP_WEBFORM_SAVE_DIAGNOSTICS` (`Services/WriteService.VisualWrite.cs:447`),
  `GXMCP_VERBOSE_LOGS`, `GXMCP_SCREENSHOT_DIR`, `GXMCP_DSO_NAME`.
- **Write safety / test hooks**: `GXMCP_WRITE_FORCE` (`Services/WriteService.cs:1160`),
  `GXMCP_WRITE_OWNER_ID`, `GXMCP_ALLOW_CONCURRENT_BUILDS`, `GXMCP_STATE_SCOPE_ID`.
- **Transport / harness**: `GX_MCP_STDIO`, `GX_MCP_PORT`, `GX_MCP_PIPE`,
  `GX_MCP_SHARED_GATEWAY`, `GXMCP_SHARED_GATEWAY`, `GXMCP_LIVE_GATEWAY_EXE`,
  `GXMCP_LIVE_RPC_TIMEOUT_MS`, `GXMCP_LIVE_SUMMARY_PATH`, `GX_MCP_REPO_ROOT`,
  `MCP_PERF_PROFILE`.
- **SDK probe / experiments**: `GX_MCP_SDK_PROBE`, `GX_MCP_SDK_PROBE_DIR`,
  `GX_KB_ALIAS`, `GX_KB_PATH`, `GX_PROGRAM_DIR`,
  `GX_MCP_PATTERN_DEBUG`, `GX_MCP_PATTERN_DEBUG_DIR`,
  `GX_MCP_PATTERN_DELTA_EXPERIMENT`, `GX_MCP_PATTERN_DIRECT_SAVE_EXPERIMENT`,
  `GX_MCP_PATTERN_NATIVE_EXPERIMENT`, `GX_MCP_PATTERN_PRESAVE_EXPERIMENT`,
  `GX_MCP_PATTERN_SEMANTIC_EXPERIMENT`, `GXMCP_SDK_PROBE`,
  `GXMCP_REQUIRE_WWP`, `GXMCP_PARITY_IDE_NAME`, `GXMCP_PARITY_MCP_NAME`,
  `GXMCP_KB_GENERATION`, `GXMCP_KB_ID`, `GXMCP_UPDATE_GOLDEN`.
- **Third-party passthrough**: `CHROME_DEVTOOLS_AXI_MCP_PATH`, `PATHEXT`.

Not all of these are operator-facing. The important distinction, and the point
of Step 2, is which ones a human might need to reach for.

The existing doc structure and its conventions:

- `docs/environment_variables.md:206-208` carries the unenforced
  add-a-row rule.
- The only test reading this file is
  `src/GxMcp.Worker.Tests/StaSchedulerTests.cs:216-227`, which asserts three
  phrases about one variable. Read it for the assertion style.
- Sections are grouped by concern (there is a GAM section, for example), and
  secrets are called out.

Repo conventions:

- Never write a secret **value** into the doc, a test, a comment or a changelog.
  Reference the variable name and the credential type only.
- Every regression guard gets a mutation check (`AGENTS.md`).

## Commands you will need

| Purpose | Command | Expected on success |
|-----------|---------|---------------------|
| List read vars | `Get-ChildItem src -Recurse -Filter *.cs \| Where-Object { $_.FullName -notmatch '\\(bin\|obj)\\' } \| Select-String -Pattern 'GetEnvironmentVariable\("([A-Z_0-9]+)"' -AllMatches \| ForEach-Object { $_.Matches } \| ForEach-Object { $_.Groups[1].Value } \| Sort-Object -Unique` | the full list |
| Worker build | `$env:GX_PATH='C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Focused tests | `dotnet test src\GxMcp.Worker.Tests --filter "FullyQualifiedName~StaScheduler\|FullyQualifiedName~Environment" --logger "console;verbosity=minimal"` | all pass |
| Full Worker suite | `dotnet test src\GxMcp.Worker.Tests --logger "console;verbosity=minimal"` | all pass (1811+ / 4 skipped before) |
| CLI suite | `npm test` | exit 0 |

`MSB3027`/`MSB3021` naming the Gateway/Worker exe → documented scoped
permission: `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`, retry.
Force `-t:Rebuild` after a compile failure. Do **not** run `.\build.ps1`.

## Scope

**In scope**:

- `docs/environment_variables.md`
- `src/GxMcp.Worker.Tests/EnvironmentVariableDocCoverageTests.cs` (create)

**Out of scope** (do NOT touch):

- Any production `.cs` file. This plan documents variables; it does not rename,
  remove or re-purpose any of them. If a variable genuinely should not exist,
  report it rather than deleting it.
- `docs/agent_playbook.md`, `AGENTS.md`, `TROUBLESHOOTING.md`.
- `cli/` — the Node side reads some of these too; Step 2 must decide whether
  they belong in the same doc, but do not edit the CLI.

## Git workflow

- Branch `advisor/120-env-var-docs`, commit in your worktree.
- Prose commit subject, e.g.
  `"Document the environment variables the code reads and the doc did not"`.
- `CHANGELOG.md` under `## Unreleased` → `### Internal` (documentation, no
  behaviour change). If you decide any row changes a supported configuration
  surface, use `### Added` and say so.
- Do NOT push, open a PR, or merge.

## Steps

### Step 1: Classify each missing variable and write its row

Work from the authoritative list produced by the command above, not from this
plan's summary — the list may have moved. For each missing name, read its
`GetEnvironmentVariable` call site to determine what it actually does, then
add a row.

Classify each into one of three kinds, and say which in the row:

1. **Operator-facing** — a human may need to set it (the watchdogs, the log
   directory, the TeamDev credentials, the crash ledger path). Document the
   purpose, the default, when to change it, and — for the credential
   variables — that the value is a secret and should be supplied through the
   environment rather than tool arguments. Never write a value.
2. **Harness/test-only** — set by the repo's own scripts
   (`GXMCP_KB_ID`, `GXMCP_KB_GENERATION`, `GXMCP_UPDATE_GOLDEN`,
   `GXMCP_PARITY_*`, `GX_MCP_SDK_PROBE*`, the `GX_MCP_PATTERN_*` experiment
   switches). Group these under a clearly-labelled subsection rather than
   mixing them with the operator-facing ones, so the operator-facing list stays
   short enough to read.
3. **Third-party passthrough** (`CHROME_DEVTOOLS_AXI_MCP_PATH`, `PATHEXT`,
   `GX_PROGRAM_DIR`) — one row each, saying plainly that the value is consumed
   by an external tool and is not interpreted by this server.

Match the existing table format exactly. Read the file first and follow its
column set and ordering; do not invent a new format.

### Step 2: Make the rule enforceable

Add to `src/GxMcp.Worker.Tests/EnvironmentVariableDocCoverageTests.cs` a test
that:

- Enumerates every `GetEnvironmentVariable("<NAME>")` literal under `src/`
  (both `GxMcp.Worker` and `GxMcp.Gateway`; restrict to the prefixes
  `GXMCP_`, `GENEXUS_MCP_`, `GX_MCP_`, `GX_` so third-party names like
  `PATHEXT` and OS names are excluded).
- Asserts each name appears in `docs/environment_variables.md`.
- Carries an **explicit allowlist** for names that are deliberately internal,
  each with a one-word reason, so the test is not a moving target. Whatever is
  on the allowlist must be listed in the doc too, under the harness/test-only
  heading from Step 1 — the allowlist is a record of intent, not an escape
  hatch.

Use `RepoSource.CsFilesIn` and `RepoSource.Read` from `src/TestSupport/` to
walk the sources rather than shelling out. Model the doc-reading assertion on
`src/GxMcp.Worker.Tests/StaSchedulerTests.cs:216-227`, which already reads this
doc from a test — match that.

Also delete the now-redundant unenforced note at `:206-208` (or rewrite it to
point at the test that now enforces it).

### Step 3: State what the doc is for, at the top

If the doc has no statement of scope, add one line: this is the complete set
of environment variables the Gateway and Worker read, and the test that keeps
it honest. That is what turns 48 omissions into a non-recurring problem.

### Step 4: Mutation-check the new test

Delete one documented row (pick a harness-only one) and confirm the test goes
red. Restore it.

**Verify**: with a documented row removed, the coverage test FAILS.

### Step 5: Full validation

```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'
dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal
dotnet test src\GxMcp.Worker.Tests --logger "console;verbosity=minimal"
dotnet build Genexus18MCP.sln -v:minimal
npm test
```

**Verify**: all exit 0, no new failures.

## Test plan

- New file: `src/GxMcp.Worker.Tests/EnvironmentVariableDocCoverageTests.cs`
  - 1 main test: every `GXMCP_`/`GENEXUS_MCP_`/`GX_MCP_`/`GX_` name read by
    `GetEnvironmentVariable` under `src/` appears in the doc.
  - 1 test asserting the allowlist entries are themselves documented, so the
    allowlist cannot become a silent dumping ground.
- Structural pattern: `src/GxMcp.Worker.Tests/StaSchedulerTests.cs:216-227`
  (existing doc-reading assertion in a test).
- Verification: focused filter → all pass; full suite → no new failures.

## Done criteria

- [ ] Every `GXMCP_`/`GENEXUS_MCP_`/`GX_MCP_`/`GX_` variable read under `src/` appears in `docs/environment_variables.md`.
- [ ] The five TeamDev credential variables are documented, with no value written anywhere.
- [ ] `GXMCP_INDEX_NO_PROGRESS_SEC`, `GXMCP_ASYNC_JOB_WATCHDOG_S` and `GXMCP_IDLE_GC` are documented.
- [ ] Every row is classified as operator-facing, harness/test-only, or third-party passthrough, and the doc says which.
- [ ] A test enforces the coverage, with an allowlist whose entries are themselves documented.
- [ ] The unenforced note at `:206-208` is removed or rewritten to point at the test.
- [ ] No production `.cs` file is modified.
- [ ] No secret value appears in the doc, the test, or the changelog.
- [ ] `dotnet test src\GxMcp.Worker.Tests` exits 0, no new failures.
- [ ] `dotnet build Genexus18MCP.sln -v:minimal` exits 0.
- [ ] `npm test` exits 0.
- [ ] `CHANGELOG.md` has an entry under `## Unreleased` → `### Internal`.
- [ ] `git status --short` shows nothing outside the in-scope list.

## STOP conditions

Stop and report back if:

- A variable read under `src/` has a value that is sensitive in a way that
  documenting its *name* would still be inappropriate. Report the name and the
  concern; do not document it.
- The `GetEnvironmentVariable` literals are constructed dynamically at some call
  sites, so a literal-scan test cannot see them. Report the count and the
  shape; the test must be honest about its coverage rather than claiming
  exhaustiveness it does not have.
- Enumerating the variables pulls in a large number of names that are
  unambiguously internal build plumbing, making the operator-facing section
  unreadable. Report the count and propose the split; do not solve it by
  silently allowlisting dozens of names.
- The fix appears to require modifying a file listed as out of scope.

## Maintenance notes

- Interacts with: any new `GetEnvironmentVariable` call. The Step 2 test turns
  that into a red test rather than a silent omission — which is the point.
- A reviewer should scrutinise the operator-facing/harness split. A doc where
  the operator has to wade through 30 experiment switches will not be read.
- The credential rows are the highest-value part of this plan and the easiest to
  get wrong. Check that no row's example implies a real value.
