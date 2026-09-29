$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $root 'release.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
foreach ($name in @('Get-ReleaseChangelogUnreleasedAnchor', 'Test-ReleaseChangelogSubstantiveBody', 'Update-ReleaseChangelogPromotion')) {
    $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $definition) { throw "Missing changelog promotion helper: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}

$temp = Join-Path $env:TEMP ('gxmcp-changelog-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
$fixture = Join-Path $temp 'CHANGELOG.md'
try {
    $initial = @(
        '# Changelog',
        '',
        '## Unreleased',
        '',
        '### Fixed',
        '',
        '- A fixture entry that must move to the version section.',
        '',
        '## v3.0.0 - 2026-01-01',
        '',
        '### Fixed',
        '',
        '- An older published entry.',
        ''
    ) -join "`r`n"
    [System.IO.File]::WriteAllText($fixture, $initial, [System.Text.UTF8Encoding]::new($false))

    $promotion = Update-ReleaseChangelogPromotion -Path $fixture -Version '3.1.0' -Date '2026-02-02'
    if ($promotion.status -ne 'promoted') { throw "Promotion did not run: $($promotion.status)" }
    if (-not $promotion.anchored) { throw 'Promotion left the new Unreleased section without an anchor.' }
    $text = [System.IO.File]::ReadAllText($fixture)

    if ($text -notmatch '(?m)^## v3\.1\.0 - 2026-02-02[ \t]*\r?$') { throw 'Promotion did not create the exact version heading.' }
    if ($text -notmatch '(?m)^## Unreleased[ \t]*\r?$') { throw 'Promotion removed the Unreleased heading.' }

    # The old body must now live under the version heading, not under Unreleased.
    $unreleasedSection = [regex]::Match($text, '(?ms)^##[ \t]+Unreleased[ \t]*\r?\n(?<body>.*?)(?=\r?\n##[ \t])')
    if (-not $unreleasedSection.Success) { throw 'Could not read the promoted Unreleased section.' }
    if ($unreleasedSection.Groups['body'].Value -match 'A fixture entry that must move') {
        throw 'The promoted entry stayed under Unreleased instead of moving to the version heading.'
    }
    $versionSection = [regex]::Match($text, '(?ms)^##[ \t]+v3\.1\.0[ \t]+- 2026-02-02[ \t]*\r?\n(?<body>.*?)(?=\r?\n##[ \t]|\z)')
    if (-not $versionSection.Success -or $versionSection.Groups['body'].Value -notmatch 'A fixture entry that must move') {
        throw 'The promoted entry did not land under the new version heading.'
    }
    $published = [regex]::Match($text, '(?ms)^##[ \t]+v3\.0\.0[ \t]+- 2026-01-01[ \t]*\r?\n(?<body>.*?)(?=\r?\n##[ \t]|\z)')
    if (-not $published.Success -or $published.Groups['body'].Value -notmatch 'An older published entry') {
        throw 'Promotion disturbed a previously published section.'
    }
    if ($versionSection.Groups['body'].Value -match 'An older published entry') {
        throw 'The promotion pulled a published entry into the new version section.'
    }

    # Issue #328: the whole point. The fresh Unreleased section must offer an
    # unambiguous anchor, and the anchor alone must not count as an entry.
    $body = $unreleasedSection.Groups['body'].Value
    if ($body -notmatch '### Added' -or $body -notmatch '### Fixed') {
        throw 'The promoted Unreleased section has no subsection skeleton to anchor the next entry.'
    }
    if ($body -notmatch '<!--') {
        throw 'The promoted Unreleased section has no anchor comment naming the contract.'
    }
    if (Test-ReleaseChangelogSubstantiveBody $body) {
        throw 'The anchor must not be treated as a substantive release entry.'
    }

    # A real entry under the anchored section IS substantive again.
    $withEntry = $body -replace '(?m)^### Fixed[ \t]*\r?$', "### Fixed`r`n`r`n- A genuinely new entry."
    if (-not (Test-ReleaseChangelogSubstantiveBody $withEntry)) {
        throw 'A real bullet under the anchored section must count as substantive.'
    }
    if (Test-ReleaseChangelogSubstantiveBody '') { throw 'An empty body must never be substantive.' }
    if (Test-ReleaseChangelogSubstantiveBody "`r`n   `r`n") { throw 'A whitespace-only body must never be substantive.' }

    # Re-promoting the same version must be a no-op, not a second heading.
    $repeat = Update-ReleaseChangelogPromotion -Path $fixture -Version '3.1.0' -Date '2026-02-02'
    if ($repeat.status -ne 'already-present') { throw "A repeated promotion must be a no-op, got $($repeat.status)." }
    $after = [System.IO.File]::ReadAllText($fixture)
    if (([regex]::Matches($after, '(?m)^##[ \t]+v3\.1\.0[ \t]')).Count -ne 1) {
        throw 'A repeated promotion duplicated the version heading.'
    }

    # A changelog with no Unreleased heading must report the failure instead of
    # silently producing a release with no notes.
    $noUnreleased = Join-Path $temp 'NO-UNRELEASED.md'
    [System.IO.File]::WriteAllText($noUnreleased, "# Changelog`r`n`r`n## v3.0.0 - 2026-01-01`r`n`r`n### Fixed`r`n`r`n- x`r`n", [System.Text.UTF8Encoding]::new($false))
    $missing = Update-ReleaseChangelogPromotion -Path $noUnreleased -Version '3.1.0' -Date '2026-02-02'
    if ($missing.status -ne 'no-unreleased') { throw "A changelog without Unreleased must fail loudly, got $($missing.status)." }

    # The published v3.1.0 section must survive a later promotion byte-for-byte,
    # and the anchor must stay under Unreleased where it belongs.
    $v310Before = [regex]::Match($text, '(?ms)^##[ \t]+v3\.1\.0[ \t]+- 2026-02-02.*?(?=\r?\n##[ \t]|\z)').Value
    [System.IO.File]::WriteAllText(
        $fixture,
        ($text -replace '(?m)^### Added[ \t]*\r?\n', "### Added`r`n`r`n- The next real entry.`r`n"),
        [System.Text.UTF8Encoding]::new($false))
    $second = Update-ReleaseChangelogPromotion -Path $fixture -Version '3.2.0' -Date '2026-03-03'
    if ($second.status -ne 'promoted') { throw "The second promotion failed: $($second.status)" }
    $secondText = [System.IO.File]::ReadAllText($fixture)
    $v310After = [regex]::Match($secondText, '(?ms)^##[ \t]+v3\.1\.0[ \t]+- 2026-02-02.*?(?=\r?\n##[ \t]|\z)').Value
    if ($v310After -cne $v310Before) { throw 'A later promotion rewrote an already published section.' }
    if ($secondText -notmatch 'The next real entry') { throw 'The new entry was lost by the second promotion.' }
    $unreleasedAgain = [regex]::Match($secondText, '(?ms)^##[ \t]+Unreleased[ \t]*\r?\n(?<body>.*?)(?=\r?\n##[ \t])')
    if (-not $unreleasedAgain.Success -or (Test-ReleaseChangelogSubstantiveBody $unreleasedAgain.Groups['body'].Value)) {
        throw 'The re-anchored Unreleased section must be empty of entries.'
    }

    Write-Host 'release-changelog: promotion moves the body, anchors the fresh Unreleased, and stays idempotent' -ForegroundColor Green
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
