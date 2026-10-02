# Plan 116: Keep the KB-create database password off the child command line

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
- **Depends on**: none
- **Category**: security
- **Planned at**: commit `0f0d71a8`, 2026-10-01

## Why this matters

`genexus_kb action=create` spawns MSBuild to instantiate a Knowledge Base. The
database password the caller supplied is passed as a `/p:` property on that
child's command line. On Windows the command line of a running process is
readable by any process that can query it, and MSBuild's own error output
echoes the invocation back. The failure path then returns that raw
concatenated stdout+stderr to the client as the response's `output` field,
unredacted.

The request log is already clean — `Program.Http.cs:363` masks `dbPassword` in
the inbound body — so the leak is specifically the child command line and the
response body. The credential is request-supplied and rotates rarely, which
makes exposure a burn-and-rotate event rather than a transient.

This repository already models exactly the right alternative: `Configuration.cs`
represents DB credentials as `PasswordEnvironmentVariable` /
`ConnectionStringEnvironmentVariable` aliases, and `KbCreateHelper` already
uses a process environment variable for `GX_PATH` and `GX_PROGRAM_DIR` on the
very same `ProcessStartInfo`.

## Current state

`src/GxMcp.Gateway/KbCreateHelper.cs`:

- `:216` writes a temp MSBuild project file with
  `Password=""$(KBDbPassword)""` at `:212`.
- `:219-223` builds the argument string:

```csharp
                bool integrated = string.IsNullOrWhiteSpace(options.DbUser);
                string arguments = $"/nologo /v:minimal \"{tempProj}\" \"/p:KBDirectory={fullPath}\" \"/p:KBTemplate={templatePath}\" \"/p:KBDbServer={dbServer}\" \"/p:KBDbName={dbName}\" \"/p:KBIntegratedSecurity={integrated}\"";
                if (!integrated)
                {
                    arguments += $" \"/p:KBDbUser={options.DbUser}\" \"/p:KBDbPassword={options.DbPassword}\"";
                }
```

- `:225-237` the `ProcessStartInfo`; note `:236-237` already sets
  `psi.EnvironmentVariables["GX_PATH"]` and `["GX_PROGRAM_DIR"]`.
- `:259` `string allOutput = (stdout + "\n" + stderr).Trim();`
- `:261-272` the failure envelope returns `["output"] = allOutput` verbatim.
- `options.DbPassword` originates from the tool arguments at
  `src/GxMcp.Gateway/Program.GatewayTools.cs:576`.
- The credential model to imitate: `src/GxMcp.Gateway/Configuration.cs:405-406`
  (`PasswordEnvironmentVariable`, `ConnectionStringEnvironmentVariable`).
- The redaction helper that exists and is used elsewhere on the Gateway:
  `src/GxMcp.Gateway/Helpers/LogRedaction.cs:41` (`LogRedaction.Redact`),
  used by `Program.Http.LogValue` at `Program.Http.cs:588`.

Repo conventions:

- The temp project file is deleted in an existing `finally` around `:335-342` —
  keep that behaviour; a response file (see Step 1) must be cleaned up the same
  way.
- Structured envelopes; do not change `status`, `code` or `message`.
- Never reproduce a secret value in a log, an error, a test fixture or a
  changelog. Tests must assert on the *absence* of the value, using a synthetic
  marker, never a real credential.
- Every regression guard gets a mutation check (`AGENTS.md`).

## Commands you will need

| Purpose | Command | Expected on success |
|-----------|---------|---------------------|
| Build Gateway | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | exit 0 |
| Focused tests | `dotnet test src\GxMcp.Gateway.Tests --filter "FullyQualifiedName~KbCreate\|FullyQualifiedName~LogRedaction\|FullyQualifiedName~Configuration" --logger "console;verbosity=minimal"` | all pass |
| Full Gateway suite | `dotnet test src\GxMcp.Gateway.Tests --logger "console;verbosity=minimal"` | all pass (880+ before) |
| Solution build | `dotnet build Genexus18MCP.sln -v:minimal` | exit 0 |
| CLI suite | `npm test` | exit 0 |

`MSB3027`/`MSB3021` naming the Gateway/Worker exe → documented scoped
permission: `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`, retry.
Force `-t:Rebuild` after a compile failure. Do **not** run `.\build.ps1`.

## Scope

**In scope**:

- `src/GxMcp.Gateway/KbCreateHelper.cs`
- `src/GxMcp.Gateway.Tests/KbCreateCredentialHandlingTests.cs` (create)

**Out of scope** (do NOT touch):

- `src/GxMcp.Gateway/Program.GatewayTools.cs` — the argument name and its
  plumbing are correct; only the hand-off to the child process changes.
- `src/GxMcp.Gateway/Helpers/LogRedaction.cs` — reuse it, do not modify it.
- `src/GxMcp.Gateway/Configuration.cs` — the alias model is already right.
- The MSBuild project template's use of `$(KBDbPassword)` — see Step 1; the
  project file must still receive the value, just not through the command line.
- `src/GxMcp.Worker/**`.

## Git workflow

- Branch `advisor/116-kbcreate-password`, commit in your worktree.
- Prose commit subject, e.g.
  `"Stop handing the KB-create password to MSBuild on the command line"`.
- `CHANGELOG.md` under `## Unreleased` → `### Fixed`, same commit. Reference
  the credential **type** and location only; never the value.
- Do NOT push, open a PR, or merge.

## Steps

### Step 1: Pass the password to the child without the command line

Use a process environment variable, matching the `GX_PATH` idiom already
present on the same `ProcessStartInfo` at `:236-237`. Set the variable only on
the child's `EnvironmentVariables`, then pass the property **by name** on the
command line.

Because the temp project at `:212` references `$(KBDbPassword)`, MSBuild will
still need to resolve it. Two viable mechanisms — pick the one that works with
this MSBuild version and say which you chose and why:

- an MSBuild **response file** (`@path` in the argument string) written and
  deleted inside the existing `finally` at `:335-342`; or
- a property that the project template reads from an environment variable
  MSBuild surfaces (`$([System.Environment]::GetEnvironmentVariable(...))`).

If neither resolves without changing the project template in a way that would
break a caller who relies on the current shape, report that as a STOP condition
with both attempts documented — do not fall back to the command line.

Whichever you choose, the property **name** on the command line must not embed
the value. The `/p:KBDbUser=` value is not a secret and may stay.

### Step 2: Redact and bound the failure output

At `:261-272`, run `allOutput` through `LogRedaction.Redact(...)` and cap its
length before putting it in `["output"]`. Reuse whatever truncation helper the
Gateway already uses for response payloads (look for the one behind
`ResponseSizeGuard`) rather than writing a new one; if that is not
appropriate here, use a documented constant in the style of
`MutationOperationJournal.MaxBytes`.

Add a comment saying why: the child's output can echo the invocation, and an
absolute SDK path is not something the caller needs echoed verbatim.

### Step 3: Do not log the argument string

Verify that nothing between `:219` and `:240` writes `arguments` or
`psi.Arguments` to a log, an error envelope or a `Logger.*` call. If it does,
remove it; if removing it would lose the only diagnostic about a failed create,
replace it with the redacted form. Report what you found either way.

### Step 4: Write the regression tests

Create `src/GxMcp.Gateway.Tests/KbCreateCredentialHandlingTests.cs`. Use a
synthetic marker value (e.g. `"SENTINEL-PW-DO-NOT-LEAK"`) — never a real
credential, never a plausible one.

1. `ThePasswordIsNotOnTheChildCommandLine` — a **source-shape** guard:
   `SourceAssert.Count(source, "/p:KBDbPassword={") == 0` and
   `SourceAssert.Count(source, "options.DbPassword")` appears only in the
   environment-variable assignment, not in an argument concatenation. Use
   `RepoSource.Read("src", "GxMcp.Gateway", "KbCreateHelper.cs")` and
   `SourceAssert.Count`. Model on `src/GxMcp.Worker.Tests/ArgvQuotingTests.cs`
   (`TheQuotingPrimitiveLivesInOnePlaceAndEveryCallerUsesIt`).
2. `TheFailureEnvelopeDoesNotEchoThePassword` — exercise the failure branch
   with an `output` string containing the sentinel and assert the returned
   `JObject["output"]` does not contain it. If the branch is not reachable
   without a real MSBuild, extract the envelope construction into a testable
   internal method **within this file** and test that.
3. `TheOutputFieldIsBounded` — assert a very large `allOutput` is truncated to
   the documented cap.
4. `LogRedactionStillCoversThePasswordShape` — assert the redaction path is
   actually applied, so a future refactor cannot drop the call while keeping
   tests 2 and 3 green (test 2 could pass because the sentinel never reaches
   that code).

### Step 5: Mutation-check every new test

Restore the `"/p:KBDbPassword=..."` concatenation and confirm test 1 fails.
Remove the `LogRedaction.Redact` call and confirm test 2 fails. Remove the cap
and confirm test 3 fails. Record all three.

**Verify**: with the command-line concatenation restored, test 1 FAILS.

### Step 6: Full validation

```powershell
dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal
dotnet test src\GxMcp.Gateway.Tests --logger "console;verbosity=minimal"
dotnet build Genexus18MCP.sln -v:minimal
npm test
npm run lint
```

**Verify**: all exit 0, no new failures.

## Test plan

- New file: `src/GxMcp.Gateway.Tests/KbCreateCredentialHandlingTests.cs`
  - 4 cases as above.
- Structural pattern: `src/GxMcp.Worker.Tests/ArgvQuotingTests.cs` for the
  source-shape idiom (it is the repo's clearest example of "the primitive lives
  in one place" as an assertion).
- Verification: focused filter → all pass; full Gateway suite → no new failures.

## Done criteria

- [ ] No `/p:` property on the MSBuild command line carries the password value.
- [ ] The password reaches MSBuild by an environment variable or response file only.
- [ ] The temp file (project or response) is deleted in the existing `finally`.
- [ ] `["output"]` in the failure envelope is redacted and length-capped.
- [ ] `arguments` / `psi.Arguments` is not logged anywhere.
- [ ] 4 tests exist and pass; all three mutations make them red.
- [ ] `Program.GatewayTools.cs`, `LogRedaction.cs` and `Configuration.cs` are untouched.
- [ ] `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` exits 0.
- [ ] `dotnet test src\GxMcp.Gateway.Tests` exits 0, no new failures.
- [ ] `dotnet build Genexus18MCP.sln -v:minimal` exits 0.
- [ ] `npm test` and `npm run lint` exit 0.
- [ ] `CHANGELOG.md` has an entry under `## Unreleased` → `### Fixed`, naming the credential type and location only.
- [ ] `git status --short` shows nothing outside the in-scope list.

## STOP conditions

Stop and report back if:

- Neither an MSBuild response file nor an environment-variable-backed property
  resolves `$(KBDbPassword)` in the generated project **without** changing the
  project template in a way that breaks existing callers. Document both
  attempts; do not fall back to the command line.
- The MSBuild task echoes the property value into stdout on success as well as
  on failure, such that redaction of the failure branch is insufficient.
  Report the echo.
- `KbCreateHelper` is called from somewhere other than
  `Program.GatewayTools.cs:576` that passes the password differently. Report
  the call site.
- The fix appears to require modifying a file listed as out of scope.

## Maintenance notes

- Interacts with: the temp-project template in this same file, and the
  `Configuration` credential-alias model. If KB create ever moves off MSBuild
  to `CreateKnowledgeBase` directly, the password stops travelling at all and
  this becomes moot.
- A reviewer should scrutinise: the redaction call must be present, not just
  the cap; and the temp file cleanup must be in the `finally` so a failed
  spawn does not leave a response file with a credential shape on disk.
- Do not add the password to any log, fixture or changelog. The tests use a
  sentinel precisely so a leaked value in a test artifact is obvious.
