# Plan 125: Let the shared credential redactor match compound and short key shapes

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise.
>
> **Drift check (run first)**: `git status --short` and
> `git diff -- src/GxMcp.Gateway/Helpers/LogRedaction.cs src/GxMcp.Worker/Helpers/LogRedaction.cs`
> If the pattern change described in "Already done for you" is **not** present in
> your worktree, that is a STOP condition — report it rather than redoing it.
>
> **This is revision 2.** Revision 1 declared
> `LogRedactionSingleSourceTests.cs` out of scope and forbade editing it. That was
> wrong, and it is the only reason revision 1's executor stopped. The revision-2
> scope adds that one file and specifies exactly three changes to it.

## Status

- **Priority**: P1
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: security
- **Planned at**: commit `0f0d71a8`, 2026-10-02
- **Revision**: 2 (supersedes revision 1 of 2026-10-01)

## Why this matters

`LogRedaction.Pattern` is the single credential-masking rule for everything the
Gateway and Worker write to a log. Its key alternation is wrapped in `\b`, so it
requires a word boundary *before* the key. A compound key whose prefix is a
letter therefore does not match, and its value is logged intact:

```
/p:KBDbPassword=secret   -> NOT MATCHED (passes through unredacted)
KBDbPassword=secret      -> NOT MATCHED
DbPassword=secret        -> NOT MATCHED
Pwd=secret               -> NOT MATCHED
Server=db;Pwd=abc        -> NOT MATCHED
myToken=abc              -> NOT MATCHED
password=secret          -> REDACTED
```

Five call sites (`KbImportHelper`, `MacroSuggestionService`, `Program.Http`,
`PreviewService`, `SharedWorkerHost`), so five log paths are exposed.

`Pwd` is not a hypothetical key: it is the standard ADO.NET alias for
`Password`, so it is the shape most likely to appear inside a real connection
string.

## This gap was already known and deliberately deferred

The most important thing to understand before editing anything. The test file
`src/GxMcp.Gateway.Tests/LogRedactionSingleSourceTests.cs` was written by the
author who consolidated five divergent copies of this helper into one, and it
records the gap in the source rather than leaving it in a tracker:

```csharp
            // KNOWN GAP, unchanged by this consolidation and present in all five
            // copies before it. The unmasked value pattern stops at a delimiter,
            // so a semicolon-delimited connection string is masked up to its
            // first ";" and whatever follows - including "Pwd=", which is a
            // standard ADO.NET alias for "Password" and is not itself a key this
            // pattern recognises - is logged intact.
            //
            // Widening the pattern to cover this is a security change to the
            // logging rule, not a consolidation of it, so it is recorded here
            // rather than folded into this refactor.
```

**This plan is that deferred security change.** So the two tests that pin the
leak are not stale — they are the marker, and closing the gap means inverting
them deliberately and rewriting the rationale. Do not "fix" them by deleting the
assertions; that would leave the gap open and the guard silent.

Two of them state the deferral in their **names**:

- `APwdDelimitedConnectionStringStillLeaksItsTail`
- `ThePwdKeyIsStillNotRecognisedOnItsOwn`

A name is documentation. Both must be renamed as part of this change, or the
next reader will be told the opposite of what the code does.

## Already done for you

The previous executor applied the pattern change and **left it uncommitted** in
this worktree. Do not revert it; do not re-derive it. Confirm it is present, then
continue from there.

`src/GxMcp.Gateway/Helpers/LogRedaction.cs` and
`src/GxMcp.Worker/Helpers/LogRedaction.cs` now carry:

```csharp
        internal const string Pattern =
            @"(?is)(?<key>(?:[A-Za-z0-9_.\[\]-]*(?:password|passwd|pwd|token|secret|api[-_]?key|authorization|credential|connectionstring)\b|\bpass\b))\s*[""']?\s*(?<separator>\s*[:=]\s*)(?:\".*?\"|'.*?'|(?:Bearer\s+)?[^\s,;}&\]]+)";
```

with an updated XML doc comment on `Pattern` in both files explaining the
prefix allowance and the `pass` exclusion.

**Note on "identical"**: the two *files* are not byte-identical — their doc
comments differ. Only the **pattern literal** must match, and
`TheTwoAssemblyCopiesCarryTheSamePattern` extracts and compares just that. Do not
"fix" a file-hash difference between the two copies; there is none to fix.

Two changes, and the reason for each:

1. **The long keys get a prefix allowance**: `[A-Za-z0-9_.\[\]-]*` before the
   alternation, so a compound key like `KBDbPassword` matches. The character
   class is deliberately narrow — word characters plus `.`, `[`, `]`, `-` — so
   the prefix cannot run across arbitrary text into a key word.
2. **`pass` keeps its strict boundary, as a separate alternative** `\bpass\b`.
   This is the load-bearing subtlety. If `pass` joined the prefixed group then
   `bypass=1` matches as `by` + `pass`, redacting an ordinary diagnostic value.
   **Do not move `pass` into the prefixed group.**

Measured behaviour, current vs. this pattern:

| Input | current | this pattern |
|---|---|---|
| `/p:KBDbPassword=<v>` | no match | **redacted** |
| `KBDbPassword=<v>` | no match | **redacted** |
| `DbPassword=<v>` | no match | **redacted** |
| `Pwd=<v>` | no match | **redacted** |
| `Server=db;Pwd=<v>` | no match | **redacted** |
| `myToken=<v>` | no match | **redacted** |
| `password=`, `passwd=`, `token=`, `secret=`, `api_key=`, `authorization=Bearer`, `credential=`, `connectionstring=`, `my-credential:` | redacted | redacted (unchanged) |
| `bypass=1` | no match | no match |
| `compassion=<v>` | no match | no match |
| `aPass=<v>` | no match | no match |
| `the password is wrong` (prose, no separator) | no match | no match |
| `passwords must be long` | no match | no match |
| `the token is invalid` | no match | no match |

`BenignTextIsLeftAlone` (`user=alice`, `target=Foo`, `kb=C:\kb\MyKb`) keeps
passing — verified by the previous executor's full-suite run.

## Commands you will need

| Purpose | Command | Expected on success |
|-----------|---------|---------------------|
| Build Gateway | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | exit 0 |
| Build Worker | `$env:GX_PATH='C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Rebuild both test projects | `dotnet build src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj -t:Rebuild -v:q` then the same for `src\GxMcp.Worker.Tests` | exit 0 |
| Focused Gateway tests | `dotnet test src\GxMcp.Gateway.Tests --filter "FullyQualifiedName~LogRedaction" --no-build --logger "console;verbosity=minimal"` | all pass |
| Full Gateway suite | `dotnet test src\GxMcp.Gateway.Tests --no-build --logger "console;verbosity=minimal"` | all pass (2242 passed / 28 skipped / 2273 total at `0f0d71a8`) |
| Full Worker suite | `dotnet test src\GxMcp.Worker.Tests --no-build --logger "console;verbosity=minimal"` | all pass |
| Solution build | `dotnet build Genexus18MCP.sln -v:minimal` | exit 0 |

**Critical**: `dotnet test --no-build` runs the DLL in the *test project's*
output folder, which holds its own copy of the production assembly. Build the
**test project**, not just the production project, or you will measure the old
code. `dotnet test` also rejects `-t:Rebuild` (MSB1008) — put `-t:Rebuild` on
`dotnet build`. And `|` does not work in this SDK's `--filter`; run one
`FullyQualifiedName~Foo` per invocation.

Do **not** run `.\build.ps1` (`AGENTS.md`: it must run last).

## Scope

**In scope**:

- `src/GxMcp.Gateway/Helpers/LogRedaction.cs` (pattern already applied; adjust
  comments only if Step 1's read shows it is still unclear)
- `src/GxMcp.Worker/Helpers/LogRedaction.cs` (same)
- `src/GxMcp.Gateway.Tests/LogRedactionSingleSourceTests.cs` — **exactly the three
  changes in Step 2**, nothing else in this file
- `src/GxMcp.Gateway.Tests/LogRedactionKeyShapeTests.cs` (create — Step 3)
- `CHANGELOG.md`

**Out of scope** (do NOT touch):

- `src/GxMcp.Gateway.Tests/LogRedactionTests.cs` — passing, no reason to touch it.
- `EveryRecognisedKeyIsMasked`, `AConnectionStringIsMaskedWhereItWasNotBefore`,
  `TheServerAndPortOfAConnectionStringAreAlsoHidden`,
  `TheTwoAssemblyCopiesCarryTheSamePattern`, `EveryCallSiteGoesThroughTheHelper`,
  `BenignTextIsLeftAlone`, `NullAndEmptyAreHandledWithoutThrowing` inside
  `LogRedactionSingleSourceTests.cs` — all passing. Leave them.
- `src/GxMcp.Gateway/KbCreateHelper.cs` — plan 116 added `MaskDbPasswordProperty`
  there. The general fix makes it redundant; **leave it** as defence in depth
  and say so in NOTES.
- `Program.RedactBodyForLog` / `Program.LogValue` — the callers, correct.
- `Program.Http.cs:363` — masks `dbPassword` on the inbound body. A different
  (input-side) mechanism; stays.

## Git workflow

- Prose commit subject, e.g.
  `"Close the credential-redaction gap the consolidation deliberately left open"`.
- `CHANGELOG.md` under `## Unreleased` → `### Fixed`, same commit. State that
  compound and short credential key shapes were logged intact, that the gap was
  recorded in-source as deferred and is now closed, and that the two tests which
  pinned the leak were inverted rather than removed. Never write a credential
  value.
- Do NOT push, open a PR, or merge.

## Steps

### Step 1: Read the whole test file before editing it

`src/GxMcp.Gateway.Tests/LogRedactionSingleSourceTests.cs` in full. Revision 1's
author (me) grep-selected from this file instead of reading it, and that is
precisely why the plan shipped with a scope that could not be satisfied.

Confirm the pattern change from "Already done for you" is present in both copies,
and that the three tests named in Step 2 exist at the line numbers below.

### Step 2: Invert the three tests that pin the gap

**Change 2a — `ThePwdKeyIsStillNotRecognisedOnItsOwn` (`:112`).**
Rename to `ThePwdKeyIsRecognisedOnItsOwn` and assert the redaction:

```csharp
        [Fact]
        public void ThePwdKeyIsRecognisedOnItsOwn()
        {
            Assert.Equal("pwd=<redacted>", LogRedaction.Redact("pwd=<sentinel>"));
        }
```

Add a one-line comment naming the reason the name changed: `Pwd` is the
standard ADO.NET alias for `Password` and was previously unrecognised.

**Change 2b — `APwdDelimitedConnectionStringStillLeaksItsTail` (`:104`).**
Rename to `APwdDelimitedConnectionStringNoLongerLeaksItsTail`. Replace the
`KNOWN GAP` comment block with one that records the closure, and invert the
assertion:

```csharp
        // This used to be a KNOWN GAP recorded here rather than folded into the
        // consolidation that created this file: the unmasked value pattern stops
        // at a delimiter, so a semicolon-delimited connection string leaked
        // everything after its first ";", including "Pwd=" - the standard ADO.NET
        // alias for "Password". The pattern now recognises the short alias, so the
        // tail is masked with the head. Before this change the masked output ended
        // at "<redacted>;Pwd=<sentinel>".
        Assert.Equal(
            "connectionstring=<redacted>;Pwd=<redacted>",
            LogRedaction.Redact("connectionstring=Server=db;Pwd=<sentinel>"));
```

Assert on the full expected string rather than only `DoesNotContain`, so the
test pins the masking boundary and would catch a regression that leaks the host.

**Change 2c — `ThePatternExistsOncePerAssembly` (`:27`).**
The needle is
`"password|passwd|pass|token|secret|api[-_]?key|authorization|credential|connectionstring"`.
Inserting `pwd` breaks that run, so the literal count drops to 0 and the test
fails for a mechanical reason, not a real one. `CountOccurrences` is an ordinal
`IndexOf` substring count, so it is sensitive to contiguity by construction.

Replace the needle with `internal const string Pattern`, which is stable against
any future change to the key list and states what the test name claims — one
`Pattern` declaration per assembly:

```csharp
            Assert.Equal(1, CountOccurrences(AllSource("GxMcp.Gateway"), "internal const string Pattern"));
            Assert.Equal(1, CountOccurrences(AllSource("GxMcp.Worker"), "internal const string Pattern"));
```

Comment why the needle is the declaration rather than the key list: the key list
is expected to change when a key is added, and pinning it made the test fail for
a correct change.

**Do not touch anything else in that file.**

### Step 3: Write the new regression tests

Create `src/GxMcp.Gateway.Tests/LogRedactionKeyShapeTests.cs`.

1. `ACompoundKeyIsMasked` — a theory over the six shapes this plan fixes:
   `/p:KBDbPassword=<sentinel>`, `KBDbPassword=<sentinel>`, `DbPassword=<sentinel>`,
   `Pwd=<sentinel>`, `Server=db;Pwd=<sentinel>`, `myToken=<sentinel>`. Assert the
   sentinel is absent and `<redacted>` is present.
2. `ANearMissIsNotMasked` — the load-bearing negative theory, over `bypass=<v>`,
   `compassion=<v>`, `aPass=<v>`, `the password is wrong`, `passwords must be
   long`, `the token is invalid`, `<v>`. Assert each comes back **unchanged**.
   This is the guard that stops a future edit from moving `pass` into the
   prefixed group and silently corrupting diagnostics.
3. `EveryPreviouslyMaskedKeyIsStillMasked` — replay the 9 cases of the existing
   `EveryRecognisedKeyIsMasked` theory so the old surface is visibly preserved.
   If you judge this redundant with the existing theory, say so in NOTES and omit
   it; do not delete the existing one.

Use a sentinel (`SENTINEL-CREDENTIAL`) — never a plausible password. Reuse the
existing `CountOccurrences` helper if you need it; do not write a second copy.

### Step 4: Mutation-check every changed and added test

Five mutations, each of which must produce a specific red:

| Mutation | Must turn red |
|---|---|
| restore the **original** `\b(...)\b` pattern | `ACompoundKeyIsMasked` — proves the tests detect the original defect |
| move `pass` from `\bpass\b` into the prefixed alternation | `ANearMissIsNotMasked` on `bypass=<sentinel>` — proves the negative guard has teeth |
| change only the Worker copy | `TheTwoAssemblyCopiesCarryTheSamePattern` — proves parity |
| add a second `internal const string Pattern` to the Gateway file | `ThePatternExistsOncePerAssembly` — proves the new needle still guards |
| revert 2b's expected string to the old leaking output | `APwdDelimitedConnectionStringNoLongerLeaksItsTail` — proves the inversion is real and not a deleted assert |

The fifth matters most: it distinguishes a deliberate inversion from a test that
was simply weakened.

**Confirm each mutation actually applied to the source before running the
test** — print the changed line *and* the file's byte length before and after.
The previous executor hit `edit` calls that reported success without matching,
and its warning is well taken: a mutation that silently no-ops is
indistinguishable from a guard that does not work.

### Step 5: Full validation

```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'
dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal
dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal
dotnet build src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj -t:Rebuild -v:q
dotnet build src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj -t:Rebuild -v:q
dotnet test src\GxMcp.Gateway.Tests --no-build --logger "console;verbosity=minimal"
dotnet test src\GxMcp.Worker.Tests --no-build --logger "console;verbosity=minimal"
dotnet build Genexus18MCP.sln -v:minimal
```

The Worker suite has not been run since the pattern changed. Run it.

## Test plan

- Modified: `src/GxMcp.Gateway.Tests/LogRedactionSingleSourceTests.cs` — three
  named changes (two inversions with renames, one needle change).
- New file: `src/GxMcp.Gateway.Tests/LogRedactionKeyShapeTests.cs` — one positive
  theory (6 cases), one negative theory (7 cases).
- Verification: focused filter → all pass; both full suites → no new failures.

## Done criteria

- [ ] Both `LogRedaction.cs` copies carry the new pattern; the extracted literals
      match (`TheTwoAssemblyCopiesCarryTheSamePattern` passes).
- [ ] `ThePwdKeyIsStillNotRecognisedOnItsOwn` **renamed** to
      `ThePwdKeyIsRecognisedOnItsOwn` and asserting redaction.
- [ ] `APwdDelimitedConnectionStringStillLeaksItsTail` **renamed** to
      `APwdDelimitedConnectionStringNoLongerLeaksItsTail`, asserting the masked
      tail, with the `KNOWN GAP` comment replaced by one recording the closure.
- [ ] `ThePatternExistsOncePerAssembly` counts `internal const string Pattern`,
      with a comment saying why the key list is no longer the needle.
- [ ] No other test in `LogRedactionSingleSourceTests.cs` was modified, and
      `LogRedactionTests.cs` is untouched.
- [ ] `pass` is still a strict-boundary alternative; `bypass=` is NOT redacted.
- [ ] All 9 pre-existing key cases, both connection-string tests,
      `EveryCallSiteGoesThroughTheHelper`, `BenignTextIsLeftAlone`,
      `TheTwoAssemblyCopiesCarryTheSamePattern` still pass.
- [ ] `KbCreateHelper.cs` untouched; `MaskDbPasswordProperty` left in place and
      its redundancy noted.
- [ ] All five mutations made the expected test red, each confirmed applied by
      printing the changed line and the file byte length.
- [ ] No credential value anywhere — sentinel only.
- [ ] `dotnet test src\GxMcp.Gateway.Tests` exits 0, no new failures.
- [ ] `dotnet test src\GxMcp.Worker.Tests` exits 0, no new failures.
- [ ] `dotnet build Genexus18MCP.sln -v:minimal` exits 0.
- [ ] `CHANGELOG.md` has an entry under `## Unreleased` → `### Fixed`.
- [ ] `git status --short` shows nothing outside the in-scope list.

## STOP conditions

Stop and report back if:

- The pattern change is **not** already present in your worktree.
- Any test in `LogRedactionSingleSourceTests.cs` **other than the three named**
  must change to accommodate the fix.
- `EveryCallSiteGoesThroughTheHelper` fails. It counts a key-list needle across
  source text — report the count it saw versus the 1 it expected.
- The prefix class `[A-Za-z0-9_.\[\]-]*` causes a false positive you did not
  anticipate on real log text in the repo. Report the string.
- You conclude a short key must join the prefixed group. Report which one and the
  false positive it introduces; do not move it.
- You believe a test should be deleted rather than inverted. Report your
  reasoning — deleting is the one option that leaves the gap unguarded.
- The fix appears to require modifying a file listed as out of scope.

## Maintenance notes

- **The gap was known, recorded, and deferred — on purpose.** If a future reader
  wonders why the pattern has a prefix allowance and a lone `\bpass\b`, the
  answer is in this file's history and in the test names. Keep the names honest.
- `ThePatternExistsOncePerAssembly` now guards the *declaration*, not the key
  list. That is the correct granularity: adding a key is a legitimate change and
  must not break the guard. Do not "restore" the key-list needle.
- Interacts with: any new credential-shaped key. `ANearMissIsNotMasked` is what
  stops someone breaking diagnostics while chasing a leak.
- `KbCreateHelper.MaskDbPasswordProperty` is now redundant. The previous
  executor leaned toward removing it in a follow-up on the grounds that two
  independent mechanisms masking the same value is its own hazard. Agree or
  disagree in NOTES; do not act on it here.
- Both assembly copies must change together, forever. That constraint is
  load-bearing and is why the parity test exists.
