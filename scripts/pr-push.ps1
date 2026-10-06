[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [int]$PullRequest,

    [ValidateRange(30, 7200)]
    [int]$PreflightTimeoutSeconds = 1200,

    [switch]$ForceWithLease
)

$ErrorActionPreference = 'Stop'

function Fail-Push([string]$Message) {
    Write-Error "PR push failed: $Message"
    exit 1
}

function Get-GhJson([string[]]$Arguments) {
    $output = @(& gh @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        Fail-Push "gh $($Arguments -join ' ') failed: $($output -join ' ')"
    }
    try {
        return ($output -join "`n") | ConvertFrom-Json
    } catch {
        Fail-Push "gh $($Arguments -join ' ') returned invalid JSON."
    }
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Fail-Push "GitHub CLI (gh) is required."
}

$branch = (& git branch --show-current 2>&1).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch)) {
    Fail-Push "The push helper requires a named local branch."
}
if ($branch -eq 'main') {
    Fail-Push "Refusing to push from main. Check out the PR branch first."
}

$dirty = @(& git status --porcelain=v1 --untracked-files=all 2>&1)
if ($LASTEXITCODE -ne 0) {
    Fail-Push "Could not inspect the working tree before preflight."
}
if ($dirty.Count -gt 0) {
    Fail-Push "The working tree is not clean. Commit or remove every local change before running the PR preflight."
}

$pr = Get-GhJson @(
    'pr', 'view', $PullRequest.ToString(),
    '--json', 'number,state,baseRefName,headRefName,headRefOid,headRepository,url'
)
if ($pr.state -ne 'OPEN') {
    Fail-Push "PR #$PullRequest must be OPEN; current state is '$($pr.state)'."
}

$headRepo = $pr.headRepository.nameWithOwner
$headRef = $pr.headRefName
$headOid = $pr.headRefOid
$prUri = $null
if (-not [Uri]::TryCreate([string]$pr.url, [UriKind]::Absolute, [ref]$prUri) -or
    $prUri.Scheme -ne 'https' -or $prUri.Host -ne 'github.com' -or
    $prUri.AbsolutePath -notmatch '^/([^/]+)/([^/]+)/pull/\d+/?$') {
    Fail-Push "PR #$PullRequest did not expose a valid GitHub pull request URL."
}
$baseRepo = "$($Matches[1])/$($Matches[2])"
$baseRef = $pr.baseRefName
if ([string]::IsNullOrWhiteSpace($headRepo) -or
    [string]::IsNullOrWhiteSpace($headRef) -or
    [string]::IsNullOrWhiteSpace($headOid) -or
    [string]::IsNullOrWhiteSpace($baseRepo) -or
    [string]::IsNullOrWhiteSpace($baseRef)) {
    Fail-Push "PR #$PullRequest did not expose complete base and head repository/ref/OID data."
}

$preflightRef = "refs/remotes/codex-pr-base/$baseRef"
$fetchSpec = "+refs/heads/${baseRef}:$preflightRef"
Write-Host "Updating the PR base before validation: $baseRepo/$baseRef" -ForegroundColor Cyan
& git fetch --no-tags "https://github.com/$baseRepo.git" $fetchSpec
if ($LASTEXITCODE -ne 0) {
    Fail-Push "Could not fetch the latest PR base '$baseRepo/$baseRef'. No push was attempted."
}
& git merge-base --is-ancestor $preflightRef HEAD
if ($LASTEXITCODE -ne 0) {
    Fail-Push "The PR branch is behind the latest '$baseRepo/$baseRef'. Rebase it, rerun the tests, and retry. No push was attempted."
}

$preflightPath = Join-Path $PSScriptRoot 'integration-preflight.ps1'
Write-Host "Running the CI-equivalent local preflight against $preflightRef..." -ForegroundColor Cyan
& pwsh -NoProfile -File $preflightPath -BaseRef $preflightRef -TimeoutSeconds $PreflightTimeoutSeconds
if ($LASTEXITCODE -ne 0) {
    Fail-Push "Preflight failed. No push was attempted."
}

$targetUrl = "https://github.com/$headRepo.git"
$targetRef = "refs/heads/$headRef"
$destination = "HEAD:$targetRef"
$pushArguments = @('push')
if ($ForceWithLease) {
    $lease = '--force-with-lease={0}:{1}' -f $targetRef, $headOid
    $pushArguments += $lease
}
$pushArguments += @($targetUrl, $destination)

Write-Host "PR #$PullRequest push target resolved:" -ForegroundColor Cyan
Write-Host "  repository: $headRepo"
Write-Host "  ref:        $headRef"
Write-Host "  expected:   $headOid"
Write-Host "  lease:      $ForceWithLease"

if ($PSCmdlet.ShouldProcess("$headRepo/$headRef", "push local HEAD")) {
    & git @pushArguments
    if ($LASTEXITCODE -ne 0) {
        Fail-Push "git push failed with exit code $LASTEXITCODE."
    }
    $localHead = (& git rev-parse HEAD 2>&1).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($localHead)) {
        Fail-Push "Could not read the local HEAD after push."
    }
    $remoteOutput = @(& git ls-remote $targetUrl $targetRef 2>&1)
    if ($LASTEXITCODE -ne 0 -or $remoteOutput.Count -eq 0) {
        Fail-Push "Could not read back the pushed remote head."
    }
    $remoteHead = (($remoteOutput[0].ToString().Trim() -split '\s+')[0]).Trim()
    if ($remoteHead -ne $localHead) {
        Fail-Push "Remote head verification failed: local=$localHead, remote=$remoteHead."
    }
    Write-Host "Push completed; remote head verified: $remoteHead" -ForegroundColor Green
}
