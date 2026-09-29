$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$sourcePath = Join-Path $root 'scripts\integration-preflight.ps1'
$source = Get-Content -LiteralPath $sourcePath -Raw
$contractPredicate = [regex]::Match($source, '\$contractTouched\s*=\s*@\(\s*\$Paths\s*\|\s*Where-Object\s*\{(?<predicate>[\s\S]*?)\}\s*\)')
if (-not $contractPredicate.Success -or $contractPredicate.Groups['predicate'].Value -match 'Routers/') {
    throw 'The tools/list golden guard must track discovery schema surfaces, not every router implementation.'
}

foreach ($requiredText in @(
    'gxmcp-integration-preflight/1',
    'Get-ChangedPaths',
    "'diff', '--cached', '--name-only'",
    "'ls-files', '--others', '--exclude-standard'",
    'Conflict markers remain',
    'tool_definitions',
    'tools-list.response.json',
    'CHANGELOG.md',
    'AllowEmptyCollection',
    'ReadToEndAsync',
    'Kill($true)',
    'Get-Command $resolvedExecutable',
    'resolvedExecutable',
    'TimeoutSeconds',
    '--no-restore',
    "'-m:1'",
    'validate-tool-contracts.py',
    'generate-operation-contract-inventory.py',
    'operation-contract-inventory',
    'Python script tests',
    "'test_*.py'",
    'run-release-script-tests.ps1',
    'Resolve-LocalGeneXusSdkPath',
    'GxMcp.Gateway.Tests',
    'Worker tests',
    'GeneXus SDK is not installed locally',
    'npm.cmd',
    '$ValidateOnly'
)) {
    if ($source -notmatch [regex]::Escape($requiredText)) {
        throw "Integration preflight lost required guard: $requiredText"
    }
}

$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    $sourcePath,
    [ref]$null,
    [ref]$parseErrors) | Out-Null
if ($parseErrors.Count -gt 0) {
    throw "Integration preflight has PowerShell parse errors: $($parseErrors -join '; ')"
}

# Issue #326: Redact-DiagnosticText had a local pattern set that missed api_key,
# authorization and the literal token prefixes, and used the same
# space-terminated value class, so `password=my secret` kept `secret`. It now
# delegates to the canonical contract; prove the delegated coverage, since a
# text-only source guard cannot tell a narrower copy from a real delegation.
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($sourcePath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
$redactDefinition = $ast.Find({
    param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Redact-DiagnosticText'
}, $true)
if (-not $redactDefinition) { throw 'Missing integration preflight redactor: Redact-DiagnosticText' }
. (Join-Path $root 'scripts\release-contract.ps1')
. ([scriptblock]::Create($redactDefinition.Extent.Text))
foreach ($leak in @(
        'api_key=sk-abc def',
        'authorization: Bearer eyJhbGciOi payload',
        'ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ012345',
        'npm_abcdefghijkl',
        'password=my secret passphrase',
        'connection string=Server=tcp:srv;Password=hunter2',
        'user id=sa')) {
    $masked = [string](Redact-DiagnosticText $leak)
    if ($masked -match 'sk-abc|eyJhbGciOi|ghp_ABCDEF|npm_abcdefghijkl|passphrase|tcp:srv|hunter2|(^|\W)sa(\W|$)') {
        throw "Integration preflight diagnostics leaked a credential value: '$leak' => '$masked'."
    }
}
if ([string](Redact-DiagnosticText 'plain line with no credential') -ne 'plain line with no credential') {
    throw 'Text without a credential must pass through the integration redactor unchanged.'
}
if ([string](Redact-DiagnosticText '') -ne '') { throw 'An empty diagnostic must stay empty.' }

Write-Host 'integration-preflight contract: PASS'
