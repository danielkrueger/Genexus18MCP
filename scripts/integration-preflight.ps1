[CmdletBinding()]
param(
    [string]$BaseRef = 'origin/main',
    [ValidateRange(30, 7200)]
    [int]$TimeoutSeconds = 1200,
    [switch]$SkipPowerShell,
    [switch]$SkipNode,
    [switch]$SkipDotnet,
    [switch]$ValidateOnly,
    [string]$SummaryPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "PowerShell 7+ is required for the integration preflight. Run with 'pwsh'."
}

$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'scripts\release-contract.ps1')
$runRoot = Join-Path $env:TEMP ('gxmcp-integration-preflight-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
if ([string]::IsNullOrWhiteSpace($SummaryPath)) {
    $SummaryPath = Join-Path $runRoot 'summary.json'
} else {
    $SummaryPath = [IO.Path]::GetFullPath($SummaryPath)
}

$summary = [ordered]@{
    schemaVersion = 'gxmcp-integration-preflight/1'
    startedAtUtc = [DateTime]::UtcNow.ToString('o')
    endedAtUtc = $null
    root = $root
    baseRef = $BaseRef
    timeoutSeconds = $TimeoutSeconds
    validateOnly = [bool]$ValidateOnly
    changedFiles = @()
    phases = New-Object System.Collections.Generic.List[object]
    status = 'running'
    failure = $null
}

function Write-IntegrationSummary {
    $summary.endedAtUtc = [DateTime]::UtcNow.ToString('o')
    $parent = Split-Path -Parent $SummaryPath
    if ($parent -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $temporary = "$SummaryPath.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText(
        $temporary,
        (($summary | ConvertTo-Json -Depth 12) + [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $SummaryPath -Force
}

function Get-ChangedPaths {
    $paths = New-Object System.Collections.Generic.List[string]
    $baseDiff = @(& git -C $root diff --name-only "$BaseRef...HEAD" 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not calculate the Git diff against '$BaseRef'."
    }
    foreach ($path in $baseDiff) {
        if (-not [string]::IsNullOrWhiteSpace([string]$path)) { [void]$paths.Add(([string]$path).Trim()) }
    }
    foreach ($arguments in @(
        @('diff', '--name-only'),
        @('diff', '--cached', '--name-only'),
        @('ls-files', '--others', '--exclude-standard')
    )) {
        $localDiff = @(& git -C $root @arguments 2>$null)
        if ($LASTEXITCODE -ne 0) {
            throw "Could not calculate the local Git diff for integration validation."
        }
        foreach ($path in $localDiff) {
            if (-not [string]::IsNullOrWhiteSpace([string]$path)) { [void]$paths.Add(([string]$path).Trim()) }
        }
    }
    return @($paths | ForEach-Object { $_ -replace '\\', '/' } | Select-Object -Unique | Sort-Object)
}

function Test-ChangedSet {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$Paths
    )

    foreach ($path in $Paths) {
        $fullPath = Join-Path $root $path
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }
        $extension = [IO.Path]::GetExtension($fullPath).ToLowerInvariant()
        if ($extension -in @('.dll', '.exe', '.pdb', '.zip', '.png', '.jpg', '.jpeg', '.gif', '.ico')) { continue }
        try { $text = [IO.File]::ReadAllText($fullPath) } catch { continue }
        if ($text -match '(?m)^(<<<<<<<|=======|>>>>>>>)( .*)?$') {
            throw "Conflict markers remain in changed file '$path'."
        }
    }

    $contractTouched = @($Paths | Where-Object {
        $_ -match '(^|/)(tool_definitions\.json|src/GxMcp\.Gateway/.*CommandDispatcher\.cs$|ToolHelpCatalog\.cs$)'
    })
    if ($contractTouched.Count -gt 0 -and $Paths -notcontains 'src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json') {
        throw 'A schema/router/dispatcher/help change must include the discovery golden fixture.'
    }

    $productionTouched = @($Paths | Where-Object {
        ($_ -match '^(src/|cli/|scripts/|install\.ps1$)') -and
        ($_ -notmatch '(^|/)tests?/') -and
        ($_ -notmatch '(\.Tests/|\.test\.js$|\.test\.ps1$)')
    })
    if ($productionTouched.Count -gt 0 -and $Paths -notcontains 'CHANGELOG.md') {
        throw 'Production changes must update CHANGELOG.md under ## Unreleased.'
    }
    if ($Paths -contains 'CHANGELOG.md') {
        $changelog = [IO.File]::ReadAllText((Join-Path $root 'CHANGELOG.md'))
        if ($changelog -notmatch '(?m)^## Unreleased\s*$') {
            throw 'CHANGELOG.md must contain an ## Unreleased section.'
        }
    }
}

function Resolve-LocalGeneXusSdkPath {
    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($env:GX_PATH)) {
        [void]$candidates.Add($env:GX_PATH.Trim().Trim('"'))
    }
    $catalogPath = Join-Path $root 'config\gx-versions.json'
    if (Test-Path -LiteralPath $catalogPath -PathType Leaf) {
        try {
            $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
            foreach ($entry in @($catalog.supportedMajors)) {
                if (-not [string]::IsNullOrWhiteSpace([string]$entry.defaultInstallPath)) {
                    [void]$candidates.Add([string]$entry.defaultInstallPath)
                }
            }
        } catch { }
    }
    foreach ($candidate in @($candidates | Select-Object -Unique)) {
        try {
            $resolved = [IO.Path]::GetFullPath($candidate)
            if (Test-Path -LiteralPath (Join-Path $resolved 'Artech.Architecture.Common.dll') -PathType Leaf) {
                return $resolved
            }
        } catch { }
    }
    return $null
}

function Add-SkippedPhase {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Reason
    )
    [void]$summary.phases.Add([ordered]@{
        name = $Name
        status = 'skipped'
        exitCode = 0
        durationSeconds = 0
        reason = $Reason
    })
    Write-Host "[SKIP] $Name — $Reason" -ForegroundColor Yellow
    Write-IntegrationSummary
}

# Issue #326: this site had its own narrower pattern set (no api_key, no
# authorization, no literal token prefixes) and the same space-terminated value
# class. It now delegates to the canonical implementation in release-contract.ps1
# and keeps no local copy.
function Redact-DiagnosticText {
    param([AllowNull()][string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return '' }
    return (Protect-GxMcpReleaseText $Text)
}

function Invoke-BoundedPhase {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Executable,
        [string[]]$Arguments = @()
    )

    $safeName = $Name -replace '[^A-Za-z0-9_.-]', '-'
    $stdoutPath = Join-Path $runRoot "$safeName.stdout.log"
    $stderrPath = Join-Path $runRoot "$safeName.stderr.log"
    $resolvedExecutable = $Executable
    if (-not [IO.Path]::IsPathRooted($resolvedExecutable)) {
        $command = Get-Command $resolvedExecutable -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $command) { throw "Executable '$Executable' was not found on PATH." }
        $resolvedExecutable = $command.Source
    }
    $phase = [ordered]@{
        name = $Name
        command = ((@($resolvedExecutable) + @($Arguments)) -join ' ')
        status = 'running'
        exitCode = $null
        durationSeconds = 0
        stdoutPath = $stdoutPath
        stderrPath = $stderrPath
        reason = $null
    }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::new()
    $stdoutTask = $null
    $stderrTask = $null
    $stdout = ''
    $stderr = ''
    try {
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $resolvedExecutable
        $startInfo.WorkingDirectory = $root
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        foreach ($argument in $Arguments) { [void]$startInfo.ArgumentList.Add([string]$argument) }
        $process.StartInfo = $startInfo
        if (-not $process.Start()) { throw "Could not start '$Executable'." }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill($true) } catch { }
            try { $process.WaitForExit(5000) } catch { }
            $phase.status = 'timeout'
            $phase.exitCode = 124
            $phase.reason = "Phase exceeded ${TimeoutSeconds}s and was terminated."
        } else {
            $phase.exitCode = $process.ExitCode
            $phase.status = if ($phase.exitCode -eq 0) { 'passed' } else { 'failed' }
            if ($phase.status -eq 'failed') { $phase.reason = "Command exited with code $($phase.exitCode)." }
        }
    } catch {
        $phase.status = 'failed'
        $phase.exitCode = 1
        $phase.reason = $_.Exception.Message
    } finally {
        if ($null -ne $stdoutTask) {
            try { $stdout = Redact-DiagnosticText $stdoutTask.GetAwaiter().GetResult() } catch { $stdout = '' }
        }
        if ($null -ne $stderrTask) {
            try { $stderr = Redact-DiagnosticText $stderrTask.GetAwaiter().GetResult() } catch { $stderr = '' }
        }
        [IO.File]::WriteAllText($stdoutPath, $stdout, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($stderrPath, $stderr, [Text.UTF8Encoding]::new($false))
        $watch.Stop()
        $phase.durationSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
        [void]$summary.phases.Add($phase)
        Write-Host "`n>>> Integration preflight: $Name" -ForegroundColor Cyan
        Write-Host "    $($phase.command)" -ForegroundColor DarkGray
        foreach ($line in @($stdout -split "`r?`n" | Where-Object { $_ } | Select-Object -Last 40)) {
            Write-Host "    $line"
        }
        foreach ($line in @($stderr -split "`r?`n" | Where-Object { $_ } | Select-Object -Last 20)) {
            Write-Host "    stderr: $line" -ForegroundColor DarkYellow
        }
        Write-IntegrationSummary
        $process.Dispose()
    }
    if ($phase.status -ne 'passed') {
        throw "Integration phase '$Name' failed: $($phase.reason). Output: $stdoutPath; errors: $stderrPath"
    }
}

$exitCode = 0
try {
    $summary.changedFiles = @(Get-ChangedPaths)
    Test-ChangedSet -Paths $summary.changedFiles
    Write-Host "Integration preflight summary: $SummaryPath" -ForegroundColor DarkGray
    Write-Host "Changed paths: $($summary.changedFiles.Count)" -ForegroundColor DarkGray

    if ($ValidateOnly) {
        Add-SkippedPhase -Name 'test execution' -Reason 'validation-only mode'
    } else {
        Invoke-BoundedPhase -Name 'tool-contracts' -Executable 'python' -Arguments @(
            (Join-Path $root 'scripts\validate-tool-contracts.py')
        )
        Invoke-BoundedPhase -Name 'operation-contract-inventory' -Executable 'python' -Arguments @(
            (Join-Path $root 'scripts\generate-operation-contract-inventory.py'), '--check'
        )
        Invoke-BoundedPhase -Name 'Python script tests' -Executable 'python' -Arguments @(
            '-m', 'unittest', 'discover', '-s', (Join-Path $root 'scripts\tests'), '-p', 'test_*.py', '-v'
        )
        if ($SkipPowerShell) {
            Add-SkippedPhase -Name 'PowerShell script tests' -Reason 'disabled by -SkipPowerShell'
        } else {
            Invoke-BoundedPhase -Name 'PowerShell script tests' -Executable 'pwsh' -Arguments @(
                '-NoProfile', '-File', (Join-Path $root 'scripts\tests\run-release-script-tests.ps1')
            )
        }
        if ($SkipNode) {
            Add-SkippedPhase -Name 'Node tests and lint' -Reason 'disabled by -SkipNode'
        } else {
            Invoke-BoundedPhase -Name 'Node tests' -Executable 'npm.cmd' -Arguments @('test')
            Invoke-BoundedPhase -Name 'Node lint' -Executable 'npm.cmd' -Arguments @('run', 'lint')
        }
        if ($SkipDotnet) {
            Add-SkippedPhase -Name 'solution tests' -Reason 'disabled by -SkipDotnet'
        } elseif ($null -eq (Resolve-LocalGeneXusSdkPath)) {
            Invoke-BoundedPhase -Name 'Gateway tests' -Executable 'dotnet' -Arguments @(
                'test', (Join-Path $root 'src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj'),
                '--no-restore', '--nologo', '-v:minimal', '-m:1'
            )
            Add-SkippedPhase -Name 'Worker tests' -Reason 'GeneXus SDK is not installed locally; CI SDK-independent gates still ran and the protected SDK lane remains authoritative.'
        } else {
            Invoke-BoundedPhase -Name 'solution tests' -Executable 'dotnet' -Arguments @(
                'test', (Join-Path $root 'Genexus18MCP.sln'),
                '--no-restore', '--nologo', '-v:minimal', '-m:1'
            )
        }
    }
    $summary.status = 'passed'
}
catch {
    $summary.status = 'failed'
    $summary.failure = $_.Exception.Message
    $exitCode = 1
    Write-Error "Integration preflight failed: $($summary.failure)"
}
finally {
    Write-IntegrationSummary
    Write-Host "Integration preflight result: $($summary.status); summary=$SummaryPath" -ForegroundColor $(if ($summary.status -eq 'passed') { 'Green' } else { 'Red' })
}

exit $exitCode
