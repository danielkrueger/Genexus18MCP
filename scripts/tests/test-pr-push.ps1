$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $root 'scripts\pr-push.ps1'
$source = Get-Content -LiteralPath $scriptPath -Raw
$tokens = $null
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors) | Out-Null
if ($errors.Count -gt 0) { throw "pr-push has PowerShell parse errors: $($errors -join '; ')" }

foreach ($requiredText in @(
    'PreflightTimeoutSeconds',
    'git status --porcelain=v1 --untracked-files=all',
    'baseRefName',
    '$pr.url',
    '$prUri.Host -ne ''github.com''',
    'did not expose a valid GitHub pull request URL',
    'git fetch --no-tags',
    'refs/remotes/codex-pr-base/',
    'git merge-base --is-ancestor',
    'integration-preflight.ps1',
    '-BaseRef $preflightRef',
    'git ls-remote',
    'remote head verified',
    'No push was attempted.'
)) {
    if ($source -notmatch [regex]::Escape($requiredText)) {
        throw "pr-push lost required preflight guard: $requiredText"
    }
}

if ($source -match "'--json',\s*'[^']*baseRepository") {
    throw 'pr-push must not request the unsupported gh pr view baseRepository field.'
}

$preflightIndex = $source.IndexOf('integration-preflight.ps1', [StringComparison]::Ordinal)
$pushIndex = $source.IndexOf('& git @pushArguments', [StringComparison]::Ordinal)
if ($preflightIndex -lt 0 -or $pushIndex -lt 0 -or $preflightIndex -ge $pushIndex) {
    throw 'The local preflight must complete before the push command can run.'
}

Write-Host 'pr-push contract: clean tree, fresh base, CI-equivalent preflight and fail-closed push order passed' -ForegroundColor Green
