# Coding Standards

Review-time rules for this repository. The implementer reads
[`AGENTS.md`](AGENTS.md) for navigation and workflow; this file holds the rules a
reviewer **enforces** on a diff. Each rule names the failure it prevents, so a
reviewer can recognise a violation without reconstructing the history that
motivated it.

## Text edits

A tracked text file is edited with the file-editing tool, never by round-tripping
it through a shell. The shell is for reading and for running commands.

Three distinct corruptions came from doing this the other way, all on files that
carry history or contracts:

- Addressing a long file by **fixed line index** truncated a 4,418-line
  `CHANGELOG.md` to 115 lines, deleting every released version.
- Piping a UTF-8 file through the **PowerShell pipeline** re-encoded it through
  the console codepage: em-dashes became `ΓÇö` and accented characters were
  destroyed, while the file still parsed as valid text.
- Mutating a list by **index arithmetic with shifting bounds** deleted closing
  braces, so the file stopped compiling.

**Review signals.** A diff that rewrites a whole file's line endings, or a
`CHANGELOG.md` that lost `## v` sections, is this bug wearing a different hat.
Check `git diff --stat`: when the changed-line count dwarfs the semantic change,
the edit mechanism is suspect, not the intent.

`git checkout --ours` / `--theirs` is the correct way to resolve a conflict
wholesale. Resolving a single hunk means hand-editing the markers out.

## Build state

A build that fails leaves stale outputs behind, and the next test run then
reports results from the **previous** binary. A test failure that contradicts the
code you just wrote is a stale output until proven otherwise.

After any compile failure, force the rebuild before trusting a test result:

```powershell
dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -c Release -t:Rebuild
```

**Review signal.** If a change is described as fixing a failure and the test that
caught it still fails, suspect the binary before the code.

## Validation lanes

A change is validated when **every** lane is green, not the lanes you remember.
The PowerShell and Python suites catch contract drift that the .NET, CLI and Nexus
suites are blind to: a live-test class missing its `ProcessSmoke` trait passes
every .NET lane and is rejected by the release preflight guard.

```powershell
dotnet test Genexus18MCP.sln -c Release --filter "Category!=ProcessSmoke"
dotnet test Genexus18MCP.sln -c Release --no-build --filter "Category=ProcessSmoke"
pwsh -NoProfile -File scripts\tests\run-release-script-tests.ps1
python -m unittest discover -s scripts/tests
python scripts/validate-tool-contracts.py
python scripts/generate-operation-contract-inventory.py --check
python scripts/verify-release-metadata.py --root . --version <package.json version>
npm test
npm run lint
Push-Location src/nexus-ide
npm ci
npm run compile
npm run lint
npm test
Pop-Location
pwsh -NoProfile -File scripts/check-build-warning-baseline.ps1 -ValidateOnly
pwsh -NoProfile -File scripts/mcp_llm_contract_smoke.ps1
pwsh -NoProfile -File scripts/coverage/collect.ps1
pwsh -NoProfile -File scripts/coverage/assert-threshold.ps1 -CoverageRoot artifacts/coverage -MinLineRatePercent 60
.\build.ps1                                                     # last; see ordering below
```

**Superset of CI.** This block is the reviewer's full set.
`.github/workflows/ci.yml` enforces a *subset* of it: an entry is here because CI
runs it, because a release depends on it, or because CI cannot run it locally —
not because every line is a CI step. A reviewer who expects CI and this block to
be line-for-line identical will flag a legitimate superset as drift. CI
additionally runs the protected SDK/live lane (`scripts/ci/sdk-validation.ps1`),
which needs the self-hosted `gx-sdk-18` runner and is therefore not listed.

The Nexus-IDE lane is **not** optional. On a 3.x release the `.vsix` it builds is
a required release asset, and `.github/workflows/release.yml` byte-compares it
against the copy inside `publish.zip`.

**Ordering.** `build.ps1` runs **last**. A solution-level `dotnet test -c Release`
rebuilds the Worker outside the `x86` platform the solution maps it to, so
running it after `build.ps1` breaks the publish↔source byte identity and
`Get-GxMcpReleaseProcessSmokeFingerprint` returns empty — which fails
`test-release-preflight.ps1` for a reason unrelated to the change under test.

**Review signal.** A commit whose message cites counts for some lanes but not
others is unverified. Ask for the missing lane's output, not for a claim about it.

## Guards must be able to fail

A regression test that cannot fail is not a guard. Every new guard gets a
**mutation check**: revert the fix it protects and confirm the test goes red.
This is what distinguishes coverage from the appearance of coverage.

The same rule applies to source-level guards. A regex asserted against a string
built in a double-quoted PowerShell string will silently interpolate `\$var`
into nothing and match for the wrong reason — the guard passes while testing
nothing. Use single-quoted patterns so the regex reaches the matcher intact.

**Review signal.** A new test with no recorded mutation check is unproven. Ask
which mutation turns it red.

## Honest failure modes

A gate that cannot run is `unavailable`, never `pass`. This covers a KB that will
not open under the selected SDK, a native assembly absent from every install, and
a live test skipped for want of a fixture.

A label may describe a cause; it may never change an outcome. The preflight
aggregate accepts `unavailable` as an approved terminal status, so downgrading a
failed phase would certify a release that ran nothing.

Absence of evidence is reported as absence. When a contract is unverifiable on
this host, say so and name what would unblock it, rather than merging on the
strength of the build being green.

## Scope

Smallest scoped change; preserve unrelated working-tree changes. A file the task
never names is not an invitation to reformat it.

For review-enforceable contract rules (tool schema, dispatch path, cache
invalidation, generated artifacts), see
[`AGENTS.md` § Source of truth and tool changes](AGENTS.md).
