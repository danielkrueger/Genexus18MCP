# Release build warning baseline

## Measurement

The machine-readable source of truth is
[`build_warning_baseline.json`](build_warning_baseline.json). Regenerate the
JSON and generated Markdown together only after reviewing a real Release
rebuild:

```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'
.\scripts\check-build-warning-baseline.ps1 -UpdateBaseline -GxPath $env:GX_PATH
```

<!-- BEGIN GENERATED WARNING BASELINE -->
Captured on 2026-10-02 from the machine-readable baseline.

The actionable baseline is **274** distinct (code, file, line) locations. Line-only moves remain visible and do not count as new diagnostics.

| Project | CS8600 | CS8602 | CS8603 | CS8604 | CS8605 | CS8618 | CS8620 | CS8625 | Other | Total |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| GxMcp.Gateway | 74 | 6 | 9 | 17 | 0 | 6 | 1 | 2 | 2 | 117 |
| GxMcp.Gateway.Tests | 69 | 15 | 3 | 23 | 6 | 0 | 21 | 10 | 3 | 150 |
| GxMcp.Worker.Tests | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 7 | 7 |
| **Total** | 143 | 21 | 12 | 40 | 6 | 6 | 22 | 12 | 12 | **274** |
<!-- END GENERATED WARNING BASELINE -->

## Policy

- A Release rebuild must exit successfully.
- `MSB3277` must remain at zero; its suppression is scoped to
  `GxMcp.Worker.Tests` and does not hide compiler/analyzer warnings.
- Future warning work should compare distinct `(code, file, line)` locations
  against the JSON manifest and must not add a new `(code, file)` diagnostic
  without an explicit baseline update reviewed with a Release rebuild. Line
  shifts are reported as `moved` and remain visible in the command output.
- The release script runs
  `.\scripts\check-build-warning-baseline.ps1` without `-UpdateBaseline` and
  fails on `MSB3277` or any new warning location.
