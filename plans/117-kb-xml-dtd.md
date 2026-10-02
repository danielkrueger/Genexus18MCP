# Plan 117: Prohibit DTD processing at the four remaining KB-sourced XML parse sites

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

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: security
- **Planned at**: commit `0f0d71a8`, 2026-10-01

## Why this matters

Eight XML parse sites in the Worker already set
`DtdProcessing.Prohibit, XmlResolver = null` and have tests asserting a
`DOCTYPE` is rejected. Four sites that parse KB-sourced XML do not, and they
are the ones whose input comes from a KB the user merely opened, or from an
imported archive. The hardened copies make the pattern look exhaustive when it
is not, which is exactly the cost of the rule living in eight places instead of
one.

All four documents are machine-generated GeneXus metadata with no legitimate
DTD, so prohibiting DTDs rejects only content that should never be there.

## Current state

The four unhardened sites:

1. `src/GxMcp.Worker/Helpers/KbConnectionString.cs:39-40` —
   `var doc = new XmlDocument(); doc.Load(connFile);` on
   `<kbPath>/knowledgebase.connection`. Reached from the SDT-propagation and
   webform-repair paths (the file builds a `Server=…;Database=…` string and
   hands it to `SdtModelPropagation.cs:53` and `WebFormCompositionRepair.cs:35`).
2. `src/GxMcp.Worker/Helpers/SdtModelPropagation.cs:184-185` —
   `var doc = new XmlDocument(); doc.LoadXml(xml);` on a decompressed SDT
   structure blob read out of the KB.
3. `src/GxMcp.Worker/Helpers/WebFormSaveDiagnostics.cs:235-236` — inside
   `ExtractProbeAttrFromXml`, `var d = new XmlDocument(); d.LoadXml(xml);` on
   WebFormPart XML read from the KB.
4. `src/GxMcp.Worker/Services/ModuleService.cs:294-297` —
   `document.LoadXml(xml)` on `ModuleManifest.mf` read out of a `.opc` archive.
   Note this one wraps the parse in a `try/catch` that converts any failure
   into `InvalidDataException("Module package manifest in '…' is not valid
   XML: …")`; a DTD rejection must keep that shape rather than escaping as a
   raw `XmlException`.

The hardened exemplars to copy:

- `src/GxMcp.Worker/Services/ModuleInstallPackage.cs:209` — the best template:

```csharp
            using (Stream stream = package.GetPart(uri).GetStream(FileMode.Open, FileAccess.Read))
            using (XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlCharacters }))
                return XElement.Load(reader);
```

  Note it also bounds `MaxCharactersInDocument` via a named constant.
- Also hardened: `src/GxMcp.Worker/Services/NavigationService.cs:49`,
  `TransferService.cs:466` and `:504`, `TextTreeFileService.cs:289`,
  `WwpTemplateXml.cs:23`.
- The corresponding tests, which are the pattern for this plan's new tests:
  `src/GxMcp.Worker.Tests/ModuleInstallPackageTests.cs:181` and
  `src/GxMcp.Worker.Tests/WwpTemplateXmlTests.cs:138` both assert a `DOCTYPE`
  is rejected. No equivalent exists for the four sites above.
- The layout write path is already safe and must stay that way:
  `src/GxMcp.Worker/Helpers/WebFormXmlHelper.cs:107` parses via
  `XDocument.Parse` with DTDs prohibited before line `:289` re-parses the
  round-tripped output. Do not touch that file.

Repo conventions:

- These helpers log parse failures at the level the existing `catch` uses
  (`Logger.Info` for `KbConnectionString`, `Logger.Debug` for
  `SdtModelPropagation`) and return a degraded value. Preserve that.
- Every regression guard gets a mutation check (`AGENTS.md`).

## Commands you will need

| Purpose | Command | Expected on success |
|-----------|---------|---------------------|
| Build Worker | `$env:GX_PATH='C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Focused tests | `dotnet test src\GxMcp.Worker.Tests --filter "FullyQualifiedName~Xml\|FullyQualifiedName~Module\|FullyQualifiedName~Sdt\|FullyQualifiedName~WebForm" --logger "console;verbosity=minimal"` | all pass |
| Full Worker suite | `dotnet test src\GxMcp.Worker.Tests --logger "console;verbosity=minimal"` | all pass (1811+ / 4 skipped before) |
| Solution build | `dotnet build Genexus18MCP.sln -v:minimal` | exit 0 |

`MSB3027`/`MSB3021` naming the Gateway/Worker exe → documented scoped
permission: `Stop-Process -Name GxMcp.Gateway,GxMcp.Worker -Force`, retry.
Force `-t:Rebuild` after a compile failure. Do **not** run `.\build.ps1`.

## Scope

**In scope**:

- `src/GxMcp.Worker/Helpers/KbConnectionString.cs`
- `src/GxMcp.Worker/Helpers/SdtModelPropagation.cs`
- `src/GxMcp.Worker/Helpers/WebFormSaveDiagnostics.cs`
- `src/GxMcp.Worker/Services/ModuleService.cs`
- `src/GxMcp.Worker/Helpers/SafeXml.cs` (create — the single hardened reader)
- `src/GxMcp.Worker.Tests/KbSourcedXmlDtdTests.cs` (create)

**Out of scope** (do NOT touch):

- The eight already-hardened sites, including
  `ModuleInstallPackage.cs`, `NavigationService.cs`, `TransferService.cs`,
  `TextTreeFileService.cs`, `WwpTemplateXml.cs` — they are correct. Do not
  "migrate" them to the new helper; that is a separate, larger refactor.
- `src/GxMcp.Worker/Helpers/WebFormXmlHelper.cs` — already safe, and load-bearing.
- Any file under `src/GxMcp.Gateway/`.
- Changing what these helpers return on success or on a malformed document.

## Git workflow

- Branch `advisor/117-kb-xml-dtd`, commit in your worktree.
- Prose commit subject, e.g.
  `"Prohibit DTDs at the last four KB-sourced XML parse sites"`.
- `CHANGELOG.md` under `## Unreleased` → `### Fixed`, same commit.
- Do NOT push, open a PR, or merge.

## Steps

### Step 1: Add one hardened reader

Create `src/GxMcp.Worker/Helpers/SafeXml.cs` with a single documented entry
point, modelled on `ModuleInstallPackage.ReadXml` (`:209`):

- `XmlReaderSettings` with `DtdProcessing = DtdProcessing.Prohibit`,
  `XmlResolver = null`, and a named `MaxCharactersInDocument` constant whose
  value is at least as large as `ModuleInstallPackage.MaxXmlCharacters`
  (read that value and do not lower it — a lower bound would reject legitimate
  large forms).
- Overloads for a file path and for a string, so the four call sites do not
  each build their own reader.
- A class comment naming why: the rule must exist once, because a rule copied
  into several call sites is only as good as the copy whose author remembered
  to update it. Quote that reasoning — `LogRedaction.cs:6-19` already makes
  exactly this argument in this repo.

### Step 2: Convert the four call sites

Each site keeps its existing return-on-failure behaviour and log level. Only
the load changes.

1. `KbConnectionString.cs:39-40` — load `connFile` through the path overload.
   The existing `catch` already returns `null` and logs at `Info`; a DTD
   rejection now lands there, which is the desired degraded behaviour.
2. `SdtModelPropagation.cs:184-185` — load the string. Existing `catch`
   returns the partially-collected `ids` and logs at `Debug`.
3. `WebFormSaveDiagnostics.cs:235-236` — inside `ExtractProbeAttrFromXml`.
   Existing `catch` returns `"(parse threw: …)"`. A DTD rejection now produces
   that string, which is a truthful report that the probe could not be read.
4. `ModuleService.cs:294-297` — load the string. The existing `catch` converts
   to `InvalidDataException`; confirm the DTD rejection still surfaces as that
   and not as a raw `XmlException`, adjusting only if the exception type
   differs (an `XmlException` is a base of what `LoadXml` already throws, so
   the existing `catch (Exception)` should already cover it — verify rather
   than assume).

### Step 3: Write the regression tests

Create `src/GxMcp.Worker.Tests/KbSourcedXmlDtdTests.cs`, following
`ModuleInstallPackageTests.cs:181` and `WwpTemplateXmlTests.cs:138`.

1. `EveryKbSourcedXmlLoadProhibitsDtds` — a **source-shape** guard over the
   four production files: assert each contains **no** `new XmlDocument()`
   followed by `Load`/`LoadXml`. Use `RepoSource.Read` and `SourceAssert.Count`
   from `src/TestSupport/`. The precise check: `SourceAssert.Count(source,
   "new XmlDocument()") == 0` in each of the four files. This is the guard
   that makes the hardening exhaustive rather than aspirational, and it is the
   one that fails if someone adds a fifth `XmlDocument` site.
2. `ASafeXmlReaderRejectsADoctype` — behavioural, on the new helper: build a
   document with a `DOCTYPE` and an internal entity, assert it throws.
3. `ASafeXmlReaderStillReadsOrdinaryKbMetadata` — assert a
   `knowledgebase.connection`-shaped document with no DTD parses correctly and
   `KbConnectionString.Build` returns the expected connection string. This is
   the guard that the hardening did not break the happy path.
4. `TheModuleManifestRejectionShapeIsPreserved` — assert a DTD-bearing manifest
   still surfaces as `InvalidDataException` (or whatever the existing `catch`
   produces), not a raw `XmlException`. Read the code and assert the actual
   shape.

### Step 4: Mutation-check every new test

Revert `KbConnectionString.cs` to `new XmlDocument()` + `Load` and confirm test
1 fails. Then, separately, remove the `DtdProcessing.Prohibit` from
`SafeXml.cs` and confirm test 2 fails. Record both.

**Verify**: with site 1 reverted, test 1 FAILS.

### Step 5: Full validation

```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'
dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal
dotnet test src\GxMcp.Worker.Tests --logger "console;verbosity=minimal"
dotnet build Genexus18MCP.sln -v:minimal
```

**Verify**: all exit 0, no new failures.

## Test plan

- New file: `src/GxMcp.Worker.Tests/KbSourcedXmlDtdTests.cs`
  - 4 cases as above.
- Structural pattern: `src/GxMcp.Worker.Tests/ModuleInstallPackageTests.cs:181`
  and `WwpTemplateXmlTests.cs:138` (DOCTYPE-rejection assertions);
  `ArgvQuotingTests.cs` (the source-shape idiom).
- Verification: focused filter → all pass; full Worker suite → no new failures.

## Done criteria

- [ ] `new XmlDocument()` no longer appears in the four in-scope files.
- [ ] All four loads go through the new hardened helper.
- [ ] `DtdProcessing.Prohibit` and `XmlResolver = null` are set, plus a `MaxCharactersInDocument` bound no lower than the existing `ModuleInstallPackage.MaxXmlCharacters`.
- [ ] Each site keeps its existing failure behaviour and log level.
- [ ] The eight already-hardened sites are untouched.
- [ ] 4 tests exist and pass; both mutations make them red.
- [ ] `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` exits 0.
- [ ] `dotnet test src\GxMcp.Worker.Tests` exits 0, no new failures.
- [ ] `dotnet build Genexus18MCP.sln -v:minimal` exits 0.
- [ ] `CHANGELOG.md` has an entry under `## Unreleased` → `### Fixed`.
- [ ] `git status --short` shows nothing outside the in-scope list.

## STOP conditions

Stop and report back if:

- Any of the four documents legitimately contains a `DOCTYPE` in a real KB.
  This was not measured. Report the document and the DTD; do not add a
  `DtdProcessing.Ignore` escape hatch to make a test pass.
- Bounding `MaxCharactersInDocument` at a value that does not break a
  legitimate large form requires knowing a real size, which cannot be measured
  without a KB. Report the value you chose and why, and do not lower it below
  `ModuleInstallPackage.MaxXmlCharacters`.
- Converting `ModuleService`'s parse changes the exception type its caller
  matches on. Report the caller's expectation.
- The fix appears to require modifying a file listed as out of scope.

## Maintenance notes

- Interacts with: any future XML parse added to the Worker. Test 1 is written
  to fail on a new `new XmlDocument()` in the four files, but a **new file** is
  not covered. When adding a fifth KB-sourced parse, route it through
  `SafeXml` and extend test 1's file list.
- A reviewer should scrutinise: that the happy path still parses (test 3), and
  that the log level and degraded return at each site are unchanged — a
  hardening that turns a degraded read into an exception is a regression.
- Honest limitation to state in the report: the input is KB-sourced rather
  than directly request-sourced, so this is a defence-in-depth closure of an
  inconsistency, not a demonstrated exploit path. The drift between hardened
  and unhardened sites is the certain part.
