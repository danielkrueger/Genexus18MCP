# Plan 115: Route the visual-verify CLI runner through the hardened shim builder

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
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: security
- **Planned at**: commit `0f0d71a8`, 2026-10-01

## Why this matters

Two services spawn the same class of browser-driver CLI shim (`.cmd`/`.bat`)
and therefore both need `cmd.exe` plus a hardened argument builder. One of them
calls the hardened builder. The other hand-rolls the same job, and its copy
does not reject `%` or escape cmd metacharacters — so a path or URL derived
from an LLM-supplied object name reaches a `cmd.exe` command line with
environment-variable expansion and metacharacter interpretation still live.

The whole reason `src/GxMcp.Worker/Helpers/Argv.cs` and
`Helpers/LogRedaction.cs` exist is documented in their own file comments: a
rule that lives in five places is only as good as the copy whose author
remembered to update it. This is that failure, already realised.

The exposure is on the post-write verification hook, so a single malformed
object name degrades into command execution rather than a failed screenshot.

## Current state

- `src/GxMcp.Worker/Services/VisualVerifyService.cs:44-62` — the duplicated
  runner, `public class DefaultCliRunner : ICliRunner`:

```csharp
                ProcessStartInfo psi;
                var ext = Path.GetExtension(fileName);
                bool isNativeExe = string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(ext, ".com", StringComparison.OrdinalIgnoreCase);
                if (!isNativeExe)
                {
                    // .cmd/.bat/.ps1 shims need cmd.exe + PATHEXT lookup.
                    psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + fileName + "\" " + arguments + "\"");
                }
```

- `:504-505` — the weak quoter:

```csharp
        private static string Quote(string s) =>
            "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
```

  It escapes `\` and `"` and nothing else. It does not reject `%`, and it does
  not escape `& | < > ^ ( )`.
- Call sites, all reaching `cmd.exe` with a caller-derived value:
  - `:326` `_runner.Run(driver.CliPath, "open " + Quote(url), DefaultCliTimeoutMs)`
  - `:332` `_runner.Run(driver.CliPath, "screenshot " + Quote(outPath), DefaultCliTimeoutMs)`
  - `:344-345` `string args = "playwright screenshot " + Quote(url) + " " + Quote(outPath);`
  where `url` is built at `:226` from `ResolveBaseUrl()` +
  `_objectAspxResolver(target)` and `target` is the object name the caller
  asked to verify.
- **The hardened sibling**, which is what to reuse:
  `src/GxMcp.Worker/Services/BrowserDriverInvoker.cs` —
  `BuildShimArguments(driverPath, arguments)` (~`:44`) rejects an argument
  containing `%` or any control character, and `EscapeCmdMeta` (~`:51-60`)
  prefixes `& | < > ^ ( )` with `^`. It returns the exact
  `/d /s /c ""<driver>" <escaped>""` form.
- `src/GxMcp.Worker/Services/PreviewService.cs:82-89` — the other
  `ICliRunner` implementation, which **already** delegates to
  `BrowserDriverProcess.BuildShimArguments` and uses
  `DefaultBrowserDriverInvoker.ParseLegacyArguments`. Read this as the
  structural template for the fix.
- Reachability of the same shim case from the dispatcher:
  `src/GxMcp.Worker/Services/CommandDispatcher.cs:306`, `:2192`, `:2283`.

Repo conventions:

- Error paths return structured envelopes; do not introduce a new one here.
- Refusals are a deliberate design feature — refusing an unsafe driver argument
  is a success case, not an error case to be avoided.
- Tests: `src/GxMcp.Worker.Tests/` — find the existing tests for
  `BrowserDriverInvoker.BuildShimArguments` (they assert DTD/shape for the
  sibling) and model on those.
- Every regression guard gets a mutation check (`AGENTS.md`).

## Commands you will need

| Purpose | Command | Expected on success |
|-----------|---------|---------------------|
| Build Worker | `$env:GX_PATH='C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Focused tests | `dotnet test src\GxMcp.Worker.Tests --filter "FullyQualifiedName~BrowserDriver\|FullyQualifiedName~VisualVerify\|FullyQualifiedName~Preview" --logger "console;verbosity=minimal"` | all pass |
| Full Worker suite | `dotnet test src\GxMcp.Worker.Tests --logger "console;verbosity=minimal"` | all pass (1811+ / 4 skipped before) |
| Solution build | `dotnet build Genexus18MCP.sln -v:minimal` | exit 0 |

`MSB3027`/`MSB3021` naming the Gateway/Worker exe → documented scoped
permission: `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`, retry.
Force `-t:Rebuild` after a compile failure. Do **not** run `.\build.ps1`.

## Scope

**In scope**:

- `src/GxMcp.Worker/Services/VisualVerifyService.cs`
- `src/GxMcp.Worker.Tests/VisualVerifyCliRunnerHardeningTests.cs` (create)

**Out of scope** (do NOT touch):

- `src/GxMcp.Worker/Services/BrowserDriverInvoker.cs` and
  `BrowserDriverProcess.cs` — these are correct. Reuse them; do not add
  parameters or relax the `%` rejection to accommodate the new caller.
- `src/GxMcp.Worker/Services/PreviewService.cs` — already correct.
- `src/GxMcp.Worker/Helpers/Argv.cs` — that is the *CRT* quoting primitive
  for native exes. It is a different layer from cmd metacharacter escaping and
  is not what this plan needs. (It has a known, separately-documented
  measurement caveat; do not touch it.)
- The `Which`/driver-detection logic at `:93-108`.

## Git workflow

- Branch `advisor/115-visual-verify-runner`, commit in your worktree.
- Prose commit subject, e.g.
  `"Stop the visual-verify runner from re-implementing the driver shim"`.
- `CHANGELOG.md` under `## Unreleased` → `### Fixed`, same commit. State the
  mechanism (cmd metacharacter / `%` expansion in a caller-derived path) and
  that the hardened builder is now shared.
- Do NOT push, open a PR, or merge.

## Steps

### Step 1: Replace the hand-built command line with the shared builder

Delete `DefaultCliRunner`'s private `Quote` and its `cmd.exe` string
construction. Build the argument list as discrete logical arguments and hand it
to `BrowserDriverProcess.BuildShimArguments`, exactly as `PreviewService`
does. The existing call sites concatenate a verb and a path into one string;
split them so the builder receives arguments, not a pre-joined line. For
example `CaptureScreenshot` becomes two calls — `open` with the url, then
`screenshot` with the out path — rather than `"open " + Quote(url)`.

Keep the `.exe`/`.com` native path (`psi = new ProcessStartInfo(fileName,
arguments)`) behaving as it does; only the shim branch changes. Read
`PreviewService.cs:82-89` and mirror its structure, including how it handles
the argument array and what it does when the builder throws.

### Step 2: Handle the builder's rejection as a structured refusal

`BuildShimArguments` throws `ArgumentException` on an unsafe character. That
must not surface as an unhandled crash from a verify call. Convert it to the
service's structured error envelope in the shape the surrounding code already
uses (`err` out-parameter / `McpResponse.Err`). The message should name the
driver and say the argument was refused, without echoing the rejected value
back in full.

### Step 3: Write the regression tests

Create `src/GxMcp.Worker.Tests/VisualVerifyCliRunnerHardeningTests.cs`.

1. A **source-shape** guard proving the duplication is gone and the shared
   builder is used, using `RepoSource.Read` / `SourceAssert.Count` from
   `src/TestSupport/`:
   - `SourceAssert.Count(source, "new ProcessStartInfo(\"cmd.exe\"") == 0`
   - `SourceAssert.Count(source, "BuildShimArguments(") >= 1`
   - the private `Quote` no longer exists:
     `Assert.DoesNotContain("private static string Quote", source)`
   Model the style on `src/GxMcp.Worker.Tests/ArgvQuotingTests.cs`
   (`TheQuotingPrimitiveLivesInOnePlaceAndEveryCallerUsesIt`).
2. A **behavioural** test that the runner refuses an unsafe argument. Use the
   existing `ICliRunner` seam: substitute a recording `ICliRunner` (the type
   exists and `VisualVerifyService` accepts one — `:153`) and assert the
   structured refusal rather than a spawned process. If substituting requires
   reaching private members, prefer an `internal` test seam adjacent to the
   existing constructor overloads at `:145-164`, in the style of
   `SetInFlightForTest` in the Gateway.
3. A test that a **well-formed** url and out path still produce the same
   command shape `PreviewService` produces — i.e. the fix did not change
   behaviour for valid input. Assert on the built argument string.

### Step 4: Mutation-check every new test

Revert Step 1 (restore the hand-built `cmd.exe` line and `Quote`) and confirm
tests 1 and 3 go red. Restore the old `Quote` while keeping the new builder
call and confirm test 3 goes red. Record both.

**Verify**: with the duplication restored, the source-shape test FAILS.

### Step 5: Full validation

```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'
dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal
dotnet test src\GxMcp.Worker.Tests --logger "console;verbosity=minimal"
dotnet build Genexus18MCP.sln -v:minimal
```

**Verify**: all exit 0, no new failures.

## Test plan

- New file: `src/GxMcp.Worker.Tests/VisualVerifyCliRunnerHardeningTests.cs`
  - 3 cases as above.
- Structural pattern: `src/GxMcp.Worker.Tests/ArgvQuotingTests.cs` for the
  source-shape idiom; the existing browser-driver tests for the builder's own
  contract.
- Verification: focused filter → all pass; full Worker suite → no new failures.

## Done criteria

- [ ] `new ProcessStartInfo("cmd.exe"` no longer appears in `VisualVerifyService.cs`.
- [ ] `BuildShimArguments` is called from `VisualVerifyService`'s runner.
- [ ] The private `Quote` helper is deleted, not merely unused.
- [ ] A rejected unsafe argument produces a structured envelope, not an unhandled throw.
- [ ] Valid input produces the same command shape as `PreviewService` does today.
- [ ] The native `.exe`/`.com` branch is unchanged.
- [ ] `BrowserDriverInvoker.cs`, `BrowserDriverProcess.cs`, `PreviewService.cs` and `Argv.cs` are untouched.
- [ ] 3 tests exist and pass; the revert mutation makes them red.
- [ ] `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` exits 0.
- [ ] `dotnet test src\GxMcp.Worker.Tests` exits 0, no new failures.
- [ ] `dotnet build Genexus18MCP.sln -v:minimal` exits 0.
- [ ] `CHANGELOG.md` has an entry under `## Unreleased` → `### Fixed`.
- [ ] `git status --short` shows nothing outside the in-scope list.

## STOP conditions

Stop and report back if:

- `BuildShimArguments` rejects an argument shape that `VisualVerifyService`
  legitimately needs (e.g. a URL `PreviewService` never passes, or an
  `outPath` under a directory whose name contains `(` or `^`). Report the
  exact argument and the rejection; do not relax the shared builder.
- Splitting the concatenated verb+path into discrete arguments changes the
  command's meaning for a real driver. Report the before/after command lines.
- Converting the `ArgumentException` to a structured envelope would require
  changing a method signature used outside this file. Report it.
- The fix appears to require modifying a file listed as out of scope.

## Maintenance notes

- Interacts with: any change to the driver-detection logic or to
  `BrowserDriverInvoker.BuildShimArguments`. If a new driver is added whose CLI
  legitimately needs `%` in an argument, the shared builder's rejection is the
  thing to revisit — and deliberately, in one place, which is the point of this
  plan.
- A reviewer should scrutinise: that the refactor preserved the exact command
  for valid input, and that the structured refusal does not echo the rejected
  value.
- Known limitation to state in the report, not to fix here: whether a
  GeneXus object name can in practice contain `%`, `&` or `^` was not
  measured. The duplication and the drift are certain; exploitability is not.
  This is the same epistemic split the repo uses elsewhere.
