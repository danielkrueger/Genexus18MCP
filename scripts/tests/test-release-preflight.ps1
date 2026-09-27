$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$drySummary = Join-Path $env:TEMP ('gxmcp-preflight-test-' + [guid]::NewGuid().ToString('N') + '.json')
$matrixDrySummary = Join-Path $env:TEMP ('gxmcp-preflight-matrix-test-' + [guid]::NewGuid().ToString('N') + '.json')
$requiredSummary = Join-Path $env:TEMP ('gxmcp-preflight-required-' + [guid]::NewGuid().ToString('N') + '.json')
$localSummary = Join-Path $env:TEMP ('gxmcp-preflight-local-' + [guid]::NewGuid().ToString('N') + '.json')
$fixtureRoot = Join-Path $env:TEMP ('gxmcp-preflight-fixtures-' + [guid]::NewGuid().ToString('N'))
$SummaryPath = $null
# Every summary path this test assigns, so the finally can also remove the phase
# logs derived from it (Issue #323). Without this, each run leaks one log pair
# per phase into %TEMP%.
$allSummaryPaths = New-Object System.Collections.Generic.List[string]
function New-TestSummaryPath([string]$Prefix) {
    $path = Join-Path $env:TEMP ('gxmcp-preflight-' + $Prefix + '-' + [guid]::NewGuid().ToString('N') + '.json')
    $allSummaryPaths.Add($path)
    return $path
}
$requiredNames = @(
    'release metadata parity', 'tool contract validation', 'operation contract inventory',
    'v3 plan readiness', 'warning baseline documentation parity', 'Python script tests', 'PowerShell script tests',
    'CLI tests', 'CLI lint', 'Nexus IDE checks', 'solution build and tests',
    'solution process smoke tests', 'Release warning baseline',
    'live KB gate'
)
try {
    $preflightPath = Join-Path $root 'scripts/release-preflight.ps1'
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($preflightPath, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw $errors[0] }
    . (Join-Path $root 'scripts/release-contract.ps1')
    $catalog = [pscustomobject]@{
        primaryMajor = '18'
        supportedMajors = @(
            [pscustomobject]@{ major = '17'; defaultInstallPath = 'C:\Program Files (x86)\GeneXus\GeneXus17Trial' }
            [pscustomobject]@{ major = '18'; defaultInstallPath = 'C:\Program Files (x86)\GeneXus\GeneXus18' }
        )
    }
    New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'KBTeste'), (Join-Path $fixtureRoot 'KBTeste17') -Force | Out-Null
    $auto18 = @(Get-GxMcpLocalLiveKbPath -Catalog $catalog -GxPath 'C:\Program Files (x86)\GeneXus\GeneXus18' -KbRoot $fixtureRoot)
    $auto17 = @(Get-GxMcpLocalLiveKbPath -Catalog $catalog -GxPath 'C:\Program Files (x86)\GeneXus\GeneXus17Trial' -KbRoot $fixtureRoot)
    $auto17Custom = @(Get-GxMcpLocalLiveKbPath -Catalog $catalog -GxPath 'D:\SDK\GeneXus17Trial' -KbRoot $fixtureRoot)
    $none = Get-GxMcpLocalLiveKbPath -Catalog $catalog -GxPath 'C:\SDK\Unknown' -KbRoot (Join-Path $env:TEMP 'gxmcp-no-fixtures')
    if ($auto18.Count -ne 1 -or $auto18[0].major -ne '18' -or $auto18[0].path -ne (Join-Path $fixtureRoot 'KBTeste')) { throw 'GeneXus 18 fixture autodetection selected the wrong KB.' }
    if ($auto17.Count -ne 1 -or $auto17[0].major -ne '17' -or $auto17[0].path -ne (Join-Path $fixtureRoot 'KBTeste17')) { throw 'GeneXus 17 fixture autodetection selected the wrong KB.' }
    if ($auto17Custom.Count -ne 1 -or $auto17Custom[0].major -ne '17' -or $auto17Custom[0].path -ne (Join-Path $fixtureRoot 'KBTeste17')) { throw 'Custom GeneXus 17 fixture autodetection selected the wrong KB.' }
    if ($null -ne $none) { throw 'Fixture autodetection returned a KB that does not exist.' }

    & pwsh -NoProfile -File (Join-Path $root 'scripts/release-preflight.ps1') -DryRun -SkipLive -SummaryPath $drySummary
    if ($LASTEXITCODE -ne 0) { throw "Dry-run preflight failed with exit code $LASTEXITCODE." }
    $summary = Get-Content -LiteralPath $drySummary -Raw | ConvertFrom-Json
    if ($summary.schemaVersion -ne 'gxmcp-release-preflight/1') { throw 'Unexpected preflight summary schema.' }
    if ($summary.executionMode -ne 'dry-run' -or $summary.processSmokeMode -ne 'serial-after-parallel' -or $summary.wallDurationSeconds -lt 0 -or $summary.phaseDurationTotalSeconds -lt 0) {
        throw 'Preflight summary must expose execution mode, process lane and nonnegative timing metrics.'
    }
    $actualNames = @($summary.phases | ForEach-Object name)
    if (($actualNames -join '|') -ne ($requiredNames -join '|')) { throw "Preflight phase order changed: $($actualNames -join ', ')" }
    $buildIndex = [array]::IndexOf($actualNames, 'solution build and tests')
    $processIndex = [array]::IndexOf($actualNames, 'solution process smoke tests')
    $liveIndex = [array]::IndexOf($actualNames, 'live KB gate')
    foreach ($staticName in @('tool contract validation', 'operation contract inventory', 'v3 plan readiness', 'warning baseline documentation parity', 'Python script tests', 'PowerShell script tests')) {
        if ([array]::IndexOf($actualNames, $staticName) -ge $buildIndex) {
            throw "Cheap static phase '$staticName' must run before the solution build and tests."
        }
    }
    if ($processIndex -le $buildIndex -or $liveIndex -le $processIndex) {
        throw 'Process smoke tests must run after the solution build and before the live gate.'
    }
    if (@($summary.phases | Where-Object status -eq 'dry-run').Count -ne 13) { throw 'All non-live phases must be marked dry-run.' }
    if (@($summary.phases | Where-Object status -eq 'skipped').Count -ne 1) { throw 'Live skip must be explicit in the summary.' }

    & pwsh -NoProfile -File (Join-Path $root 'scripts\release-preflight.ps1') -DryRun -SkipLive -LiveMajors '17,18' -LiveGxPathMap '17=C:\SDK\GX17' -SummaryPath $matrixDrySummary *> $null
    if ($LASTEXITCODE -ne 0) { throw "Matrix dry-run preflight failed with exit code $LASTEXITCODE." }
    $matrixSummary = Get-Content -LiteralPath $matrixDrySummary -Raw | ConvertFrom-Json
    if ($matrixSummary.liveMode -ne 'matrix') { throw 'Matrix options were not reflected in the release preflight summary.' }

    & pwsh -NoProfile -File (Join-Path $root 'scripts/release-preflight.ps1') -DryRun -RequireBuildAll -LiveKbPath (Join-Path $fixtureRoot 'missing') -SummaryPath $requiredSummary *> $null
    if ($LASTEXITCODE -eq 0) { throw 'Required live Build All gate must fail closed when no fixture is configured.' }
    $required = Get-Content -LiteralPath $requiredSummary -Raw | ConvertFrom-Json
    $requiredLive = @($required.phases | Where-Object name -eq 'live KB gate' | Select-Object -Last 1)
    if ($required.status -ne 'failed' -or $requiredLive.status -ne 'skipped') { throw 'Required live gate failure was not recorded in the summary.' }

    & pwsh -NoProfile -File (Join-Path $root 'scripts\release-preflight.ps1') -DryRun -RequireBuildAll -LiveKbPath (Join-Path $fixtureRoot 'KBTeste') -SummaryPath $localSummary *> $null
    if ($LASTEXITCODE -ne 0) { throw 'A local KB must be sufficient for the dry-run live gate without a fixture manifest.' }
    $local = Get-Content -LiteralPath $localSummary -Raw | ConvertFrom-Json
    $localLive = @($local.phases | Where-Object name -eq 'live KB gate' | Select-Object -Last 1)
    # Normalize both sides: the preflight resolves the recorded path, while
    # $fixtureRoot still carries whatever form %TEMP% had.  On hosts where TEMP
    # is an 8.3 short path the raw comparison would fail even though the live
    # gate passed and the same directory was selected.
    $expectedLocalKb = ConvertTo-GxMcpReleasePath -Path (Join-Path $fixtureRoot 'KBTeste') -BasePath $root
    $actualLocalKb = ConvertTo-GxMcpReleasePath -Path ([string]$local.liveKbPath) -BasePath $root
    if ($localLive.status -ne 'dry-run' -or $actualLocalKb -ne $expectedLocalKb) { throw 'Local KB live source was not recorded in the preflight summary.' }

    # Regression guard: a path that differs only by normalization (dot segments)
    # must still be recognized as the same recorded KB, which is what fails on
    # an 8.3 %TEMP%.
    $dotSegmentKb = ConvertTo-GxMcpReleasePath -Path (Join-Path (Join-Path $fixtureRoot '.') 'KBTeste') -BasePath $root
    if ($dotSegmentKb -ne $expectedLocalKb) { throw 'Release path normalization must collapse equivalent path forms.' }

    $releaseSource = Get-Content -LiteralPath (Join-Path $root 'release.ps1') -Raw
    if ($releaseSource -match "'-SkipLive'") { throw 'Canonical release entrypoint must allow configured live preflight values to participate.' }

    $preflightSource = Get-Content -LiteralPath (Join-Path $root 'scripts/release-preflight.ps1') -Raw
    foreach ($marker in @(
        'LiveMajors', 'LiveGxPathMap', 'test-live-matrix.ps1', "liveMode =",
        'Start-PreflightPhase', 'Complete-PreflightPhase', 'Invoke-PreflightParallel',
        'ResumeSummaryPath', 'artifactFingerprint', 'sourceCommit', 'executionMode',
        'warning baseline documentation parity', 'check-build-warning-baseline.ps1'
    )) {
        if ($preflightSource -notmatch [regex]::Escape($marker)) { throw "Release preflight is missing multi-version live marker: $marker" }
    }
    if ($preflightSource.Contains(', $liveDefinition')) { throw 'Live KB gate must not run in parallel with the solution MSBuild phase.' }
    foreach ($marker in @(
        'Category!=ProcessSmoke', 'Category=ProcessSmoke', 'solution process smoke tests', '$processSmokeDefinition',
        'GXMCP_LIVE_GATEWAY_EXE', '--no-build', '--no-restore', '--logger', 'Get-PreflightTrxTestCount',
        'processSmokeTestCount', 'Process smoke filter selected zero tests'
    )) {
        if ($preflightSource -notmatch [regex]::Escape($marker)) { throw "Process-sensitive preflight marker is missing: $marker" }
    }
    $parallelAssignment = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left.Extent.Text -eq '$parallelDefinitions'
    }, $true)
    if (-not $parallelAssignment -or $parallelAssignment.Extent.Text -match 'solution process smoke tests|Category=ProcessSmoke') {
        throw 'Process smoke tests must never be present in the parallel definition set.'
    }
    $processAssignment = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left.Extent.Text -eq '$processSmokeDefinition'
    }, $true)
    if (-not $processAssignment -or
        $processAssignment.Extent.Text -notmatch '--no-build' -or
        $processAssignment.Extent.Text -notmatch '--no-restore') {
        throw 'The process lane must execute the current Release binaries with no rebuild or restore.'
    }
    $parallelCall = $preflightSource.IndexOf('Invoke-PreflightParallel -Definitions', [StringComparison]::Ordinal)
    $processCall = $preflightSource.IndexOf('Invoke-PreflightPhase @processSmokeDefinition', [StringComparison]::Ordinal)
    $liveCall = $preflightSource.IndexOf('Invoke-PreflightPhase @liveDefinition', [StringComparison]::Ordinal)
    if ($parallelCall -lt 0 -or $processCall -le $parallelCall -or $liveCall -le $processCall) {
        throw 'Process smoke tests must run after the parallel wave and before the live gate.'
    }
    $processTestFiles = @(
        'src/GxMcp.Gateway.Tests/Issue146AcceptanceMatrixContractTests.cs',
        'src/GxMcp.Gateway.Tests/McpSmokeScriptContractTests.cs',
        'src/GxMcp.Gateway.Tests/GatewayProcessLeaseTests.cs',
        'src/GxMcp.Gateway.Tests/LiveGatewayHarnessCleanupTests.cs',
        'src/GxMcp.Worker.Tests/BuildServiceTests.cs',
        'src/GxMcp.Worker.Tests/BuildReapByPidTests.cs',
        'src/GxMcp.Worker.Tests/GithubServiceTests.cs',
        'src/GxMcp.Worker.Tests/TimeTravelServiceTests.cs'
    ) | ForEach-Object { Join-Path $root $_ }
    $gatewayTestRoot = Join-Path $root 'src\GxMcp.Gateway.Tests'
    $processTestFiles += @(Get-ChildItem -LiteralPath $gatewayTestRoot -Filter '*.cs' -File | Where-Object {
        (Get-Content -LiteralPath $_.FullName -Raw) -match 'class\s+\w+\s*:\s*[^\r\n]*IClassFixture<LiveGatewayHarness>'
    } | ForEach-Object { $_.FullName })
    foreach ($testFile in @($processTestFiles | Sort-Object -Unique)) {
        $testSource = Get-Content -LiteralPath $testFile -Raw
        if ($testSource -notmatch '\[\s*Trait\("Category",\s*"ProcessSmoke"\)\s*\]') {
            throw "ProcessSmoke trait is missing from $testFile."
        }
    }
    $issue146Source = Get-Content -LiteralPath (Join-Path $root 'src/GxMcp.Gateway.Tests/Issue146AcceptanceMatrixContractTests.cs') -Raw
    if ($issue146Source -notmatch '(?s)\[\s*Trait\("Category",\s*"ProcessSmoke"\)\s*\]\s*public\s+sealed\s+class\s+Issue146StdioSmokeContractTests') {
        throw 'The stdio process test class itself must carry the ProcessSmoke trait.'
    }

    # Exercise the new parallel coordinator in dry-run mode. This keeps the
    # test independent of SDK availability while proving deterministic result
    # ordering and one result per launched phase.
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'scripts/release-preflight.ps1'), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw $errors[0] }
    $functionNames = @(
        'Format-PreflightCommand', 'Write-PreflightSummary', 'Test-PreflightPhaseStatus', 'Get-PreflightTrxTestCount',
        'Get-ReusablePreflightPhase', 'New-PreflightPhaseState', 'Start-PreflightPhase', 'Complete-PreflightPhase',
        'Add-PreflightPhaseResult', 'Invoke-PreflightParallel', 'Write-PreflightPhaseLog',
        'Get-PreflightPhaseLogPaths', 'Format-PreflightPhaseLogHint'
    )
    foreach ($name in $functionNames) {
        $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
        if (-not $definition) { throw "Missing parallel preflight function: $name" }
        . ([scriptblock]::Create($definition.Extent.Text))
    }
    if ((Test-PreflightPhaseStatus -Status 'timeout') -or
        (Test-PreflightPhaseStatus -Status 'failed') -or
        (Test-PreflightPhaseStatus -Status 'unavailable') -or
        -not (Test-PreflightPhaseStatus -Status 'unavailable' -AllowUnavailable)) {
        throw 'Timeout, failure, and disallowed unavailable statuses must fail closed.'
    }
    $summary = [ordered]@{
        startedAtUtc = [DateTime]::UtcNow.ToString('o')
        phases = New-Object System.Collections.Generic.List[object]
        status = 'running'
    }
    $SummaryPath = New-TestSummaryPath 'parallel'
    $DryRun = $true
    $PhaseTimeoutSeconds = 30
    $resumeEnabled = $false
    $resumePhases = @{}
    $resumeReason = $null
    # Starting a phase must persist its running state before completion so an
    # interrupted host leaves a resumable incremental summary.
    $DryRun = $false
    $SummaryPath = New-TestSummaryPath 'running'
    $runningState = Start-PreflightPhase -Name 'incremental phase' -Executable 'cmd.exe' -Arguments @('/c', 'exit', '0') -WorkingDirectory $root
    $runningSummary = Get-Content -LiteralPath $SummaryPath -Raw | ConvertFrom-Json
    $runningPhases = @($runningSummary.phases | ForEach-Object { $_ })
    if ($runningPhases.Count -ne 1 -or $runningPhases[0].name -ne 'incremental phase' -or $runningPhases[0].status -ne 'running') {
        throw 'Preflight must persist a running phase before waiting for completion.'
    }
    $runningResult = Complete-PreflightPhase -State $runningState
    if ($runningResult.status -ne 'passed') { throw 'Incremental phase did not complete successfully.' }
    Add-PreflightPhaseResult -Phase $runningResult
    $completedSummary = Get-Content -LiteralPath $SummaryPath -Raw | ConvertFrom-Json
    $completedPhases = @($completedSummary.phases | ForEach-Object { $_ })
    if ($completedPhases.Count -ne 1 -or $completedPhases[0].status -ne 'passed') {
        throw 'Completing a phase must update its existing summary entry without duplication.'
    }
    $DryRun = $true
    $SummaryPath = New-TestSummaryPath 'parallel'
    $summary.phases.Clear()
    $artifactRoot = Join-Path $fixtureRoot 'artifact'
    New-Item -ItemType Directory -Path (Join-Path $artifactRoot 'publish/worker') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $artifactRoot 'publish/GxMcp.Gateway.exe') -Value 'gateway' -NoNewline
    Set-Content -LiteralPath (Join-Path $artifactRoot 'publish/worker/GxMcp.Worker.exe') -Value 'worker' -NoNewline
    Set-Content -LiteralPath (Join-Path $artifactRoot 'publish/tool_definitions.json') -Value '{}' -NoNewline
    Set-Content -LiteralPath (Join-Path $artifactRoot 'publish/nexus-ide.vsix') -Value 'vsix' -NoNewline
    $fingerprint = Get-GxMcpReleaseArtifactFingerprint -RepositoryRoot $artifactRoot
    if ([string]::IsNullOrWhiteSpace([string]$fingerprint)) {
        throw 'Artifact fingerprint must be one scalar value when all release artifacts exist.'
    }
    # Mirror the published bytes into the source build paths the process-smoke
    # fingerprint compares against. Test-GxMcpReleasePreflightCertificate
    # recomputes that fingerprint from Expected.root, so pointing the resume case
    # at this fixture keeps the assertion hermetic. Reading the live repository
    # here instead makes the test a false negative whenever publish/ was not
    # produced by the immediately-preceding build: the preflight runs
    # 'PowerShell script tests' in parallel with 'dotnet test -c Release', and that
    # lane rewrites src\GxMcp.Worker\bin\Release underneath the read.
    foreach ($mirror in @(
            @{ Source = 'src/GxMcp.Gateway/bin/Release/net10.0-windows/GxMcp.Gateway.exe'; From = 'publish/GxMcp.Gateway.exe' },
            @{ Source = 'src/GxMcp.Worker/bin/x86/Release/GxMcp.Worker.exe'; From = 'publish/worker/GxMcp.Worker.exe' }
        )) {
        $target = Join-Path $artifactRoot ($mirror.Source -replace '/', '\')
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $artifactRoot ($mirror.From -replace '/', '\')) -Destination $target -Force
    }
    $processSmokeFingerprint = Get-GxMcpReleaseProcessSmokeFingerprint -RepositoryRoot $artifactRoot
    if ([string]$processSmokeFingerprint -notmatch '^[0-9a-f]{64}$') {
        throw 'The process-smoke fingerprint must resolve from the fixture artifact pair.'
    }
    $trxRoot = Join-Path $fixtureRoot 'process-trx'
    New-Item -ItemType Directory -Path $trxRoot -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $trxRoot 'process.trx'), '<TestRun><ResultSummary><Counters total="3" executed="3" /></ResultSummary></TestRun>')
    if ((Get-PreflightTrxTestCount -ResultsDirectory $trxRoot) -ne 3) {
        throw 'Process smoke TRX test count was not preserved.'
    }
    $resumeCatalog = [pscustomobject]@{
        primaryMajor = '18'
        supportedMajors = @([pscustomobject]@{ major = '18'; defaultInstallPath = 'C:\SDK\GeneXus18' })
    }
    $resumeExpected = Resolve-GxMcpReleasePreflightInputs `
        -Root $artifactRoot -Catalog $resumeCatalog -GxPath 'C:\SDK\GeneXus18' -Version '3.5.0' `
        -SourceCommit 'source-commit' -LiveKbPath 'C:\KBs\Fixture' `
        -LiveFixtureManifest 'C:\Fixtures\release.json' -RequireBuildAll -ArtifactFingerprint $fingerprint
    $matchingResume = [pscustomobject]@{
        schemaVersion = 'gxmcp-release-preflight/1'; status = 'passed'
        root = $resumeExpected.root; version = $resumeExpected.version; sourceCommit = $resumeExpected.sourceCommit
        gxPath = $resumeExpected.gxPath; liveKbPath = $resumeExpected.liveKbPath; liveMode = $resumeExpected.liveMode
        liveMajors = @($resumeExpected.liveMajors); liveGxPathMap = @($resumeExpected.liveGxPathMap)
        liveFixtureManifest = $resumeExpected.liveFixtureManifest; liveKbSource = $resumeExpected.liveKbSource
        liveFixtureSource = $resumeExpected.liveFixtureSource; requireLive = $resumeExpected.requireLive
        requireBuildAll = $resumeExpected.requireBuildAll; skipLive = $resumeExpected.skipLive
        skipWarningBaseline = $resumeExpected.skipWarningBaseline; artifactFingerprint = $resumeExpected.artifactFingerprint
        processSmokeMode = 'serial-after-parallel'; processSmokeTestCount = 3
        processSmokeResultsPath = $trxRoot; processSmokeBinaryFingerprint = $processSmokeFingerprint
        phases = @(
            foreach ($phaseName in (Get-GxMcpMandatoryPreflightPhaseNames)) {
                $command = switch ($phaseName) {
                    'release metadata parity' { 'python scripts/verify-release-metadata.py --version 3.5.0' }
                    'tool contract validation' { 'python scripts/validate-tool-contracts.py' }
                    'operation contract inventory' { 'python scripts/generate-operation-contract-inventory.py --check' }
                    'v3 plan readiness' { 'python scripts/validate-v3-plan.py --require-ready' }
                    'warning baseline documentation parity' { 'pwsh -File scripts/check-build-warning-baseline.ps1 -ValidateOnly' }
                    'Python script tests' { 'python -m unittest discover -s scripts/tests -v' }
                    'PowerShell script tests' { 'pwsh -File scripts/tests/run-release-script-tests.ps1' }
                    'CLI tests' { 'npm test' }
                    'CLI lint' { 'npm run lint' }
                    'Nexus IDE checks' { 'npm --prefix src/nexus-ide run check' }
                    'solution build and tests' { 'dotnet test Genexus18MCP.sln -c Release --filter Category!=ProcessSmoke -v:minimal' }
                    'solution process smoke tests' { 'dotnet test Genexus18MCP.sln -c Release --no-build --no-restore --filter Category=ProcessSmoke --logger trx;LogFilePrefix=process-smoke --results-directory C:/temp/process-smoke -v:minimal' }
                    'Release warning baseline' { 'pwsh -File scripts/check-build-warning-baseline.ps1 -BaselineFile docs/build_warning_baseline.json' }
                    'live KB gate' { 'pwsh -File scripts/test-live.ps1 -KbPath C:/KBs/Fixture -SkipBuild' }
                }
                [pscustomobject]@{ name = $phaseName; status = 'passed'; command = $command; exitCode = 0 }
            }
        )
    }
    if (-not (Test-GxMcpReleasePreflightCompatibility -Summary $matchingResume -Expected $resumeExpected) -or
        -not (Test-GxMcpReleasePreflightCertificate -Summary $matchingResume -Expected $resumeExpected)) {
        throw 'A matching complete preflight summary must be resumable.'
    }
    $matchingResume.sourceCommit = 'different-commit'
    if (Test-GxMcpReleasePreflightCompatibility -Summary $matchingResume -Expected $resumeExpected) {
        throw 'A changed source commit must invalidate preflight resume.'
    }
    $matchingResume.sourceCommit = $resumeExpected.sourceCommit
    $matchingResume.liveFixtureManifest = 'C:\Fixtures\different.json'
    if (Test-GxMcpReleasePreflightCompatibility -Summary $matchingResume -Expected $resumeExpected) {
        throw 'A changed live fixture must invalidate preflight resume.'
    }
    $matchingResume.liveFixtureManifest = $resumeExpected.liveFixtureManifest
    $matchingResume.artifactFingerprint = 'different-artifact'
    if (Test-GxMcpReleasePreflightCompatibility -Summary $matchingResume -Expected $resumeExpected) {
        throw 'A changed artifact fingerprint must invalidate preflight resume.'
    }
    $matchingResume.artifactFingerprint = $resumeExpected.artifactFingerprint
    $matchingResume.status = 'failed'
    if (-not (Test-GxMcpReleasePreflightCompatibility -Summary $matchingResume -Expected $resumeExpected)) {
        throw 'Build reuse should be allowed after rerunning a matching failed preflight.'
    }
    if (Test-GxMcpReleasePreflightCertificate -Summary $matchingResume -Expected $resumeExpected) {
        throw 'A failed preflight must never certify SkipBuild and SkipTests.'
    }
    $matchingResume.status = 'passed'
    $definitions = @(
        [ordered]@{ Name = 'parallel first'; Executable = 'cmd.exe'; Arguments = @('/c', 'exit', '0'); WorkingDirectory = $root },
        [ordered]@{ Name = 'parallel second'; Executable = 'cmd.exe'; Arguments = @('/c', 'exit', '0'); WorkingDirectory = $root }
    )
    $parallelResults = @(Invoke-PreflightParallel -Definitions $definitions)
    if ($parallelResults.Count -ne 2 -or $summary.phases.Count -ne 2) {
        throw 'Parallel preflight must return and record every phase.'
    }
    if (($parallelResults.name -join '|') -ne 'parallel first|parallel second' -or
        @($summary.phases | ForEach-Object status) -contains 'failed' -or
        @($summary.phases | ForEach-Object status) -contains 'running') {
        throw 'Parallel preflight must preserve definition order and terminal dry-run statuses.'
    }
    if (-not (@($summary.phases | ForEach-Object status) -contains 'dry-run')) {
        throw 'Parallel dry-run phases must be marked dry-run.'
    }

    # A passed phase from a matching prior summary must be reusable without
    # starting a child process; the record remains observable as reused.
    $resumeEnabled = $true
    $resumeSummaryPath = 'fixture-summary.json'
    $resumePhases = @{
        'reusable phase' = [pscustomobject]@{ status = 'passed'; command = 'cmd.exe /c exit 0'; exitCode = 0; durationSeconds = 1.25 }
    }
    $reusableState = Start-PreflightPhase -Name 'reusable phase' -Executable 'cmd.exe' -Arguments @('/c', 'exit', '0') -WorkingDirectory $root
    $reusableResult = Complete-PreflightPhase -State $reusableState
    if ($reusableResult.status -ne 'passed' -or -not $reusableResult.reused) {
        throw 'Matching passed preflight phases must be marked reusable.'
    }
    $resumePhases['reusable phase'].command = 'cmd.exe /c exit 7'
    $commandMismatch = Start-PreflightPhase -Name 'reusable phase' -Executable 'cmd.exe' -Arguments @('/c', 'exit', '0') -WorkingDirectory $root
    if ($null -ne $commandMismatch.Phase.PSObject.Properties['reused']) { throw 'A phase with a different exact command must not be reused.' }
    $resumePhases['solution process smoke tests'] = [pscustomobject]@{ status = 'passed'; command = 'old' }
    $processReuse = Get-ReusablePreflightPhase -Name 'solution process smoke tests' -Command 'new'
    if ($null -ne $processReuse) { throw 'The process lane must execute again instead of reusing stale process evidence.' }

    # Load the production runner and exercise a nonzero command without
    # starting the real release matrix. AllowFailure keeps the phase object so
    # the test can inspect exit-code propagation.
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'scripts\release-preflight.ps1'), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw $errors[0] }
    foreach ($name in @('Format-PreflightCommand', 'Write-PreflightSummary', 'Invoke-PreflightPhase', 'Format-PreflightPhaseLogHint')) {
        $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
        if (-not $definition) { throw "Missing $name production function." }
        . ([scriptblock]::Create($definition.Extent.Text))
    }
    $summary = [ordered]@{
        startedAtUtc = [DateTime]::UtcNow.ToString('o')
        phases = New-Object System.Collections.Generic.List[object]
        endedAtUtc = $null
    }
    $SummaryPath = New-TestSummaryPath 'phase'
    $DryRun = $false
    $phase = Invoke-PreflightPhase -Name 'mock failure' -Executable 'cmd.exe' -Arguments @(
        '/c', 'echo MOCK-EVIDENCE & echo MOCK-STDERR 1>&2 & exit 7'
    ) -AllowFailure
    if ($phase.exitCode -ne 7 -or $phase.status -ne 'failed') { throw 'Nonzero phase exit code was not preserved.' }
    if (-not (Test-Path -LiteralPath $SummaryPath)) { throw 'Phase summary was not written atomically.' }

    # Issue #323: the summary is what survives the terminal, and until now it
    # carried no output at all, so a red phase left no evidence beyond an
    # unlabelled console tail.
    if ([string]::IsNullOrWhiteSpace([string]$phase.stdoutPath)) { throw 'A phase that ran must record where its output went.' }
    if ([string]::IsNullOrWhiteSpace([string]$phase.stderrPath)) { throw 'A phase that ran must record where its errors went.' }
    $evidenceSummary = Get-Content -LiteralPath $SummaryPath -Raw | ConvertFrom-Json
    $evidencePhase = @($evidenceSummary.phases | Where-Object name -eq 'mock failure') | Select-Object -First 1
    if ($null -eq $evidencePhase) { throw 'The failed phase must be present in the persisted summary.' }
    $capturedOut = (Get-Content -LiteralPath ([string]$evidencePhase.stdoutPath) -Raw)
    $capturedErr = (Get-Content -LiteralPath ([string]$evidencePhase.stderrPath) -Raw)
    if ($capturedOut -notmatch 'MOCK-EVIDENCE') { throw 'Phase output must be persisted where the summary points at.' }
    if ($capturedErr -notmatch 'MOCK-STDERR') { throw 'Phase error output must be persisted where the summary points at.' }
    $loggedSummaryJson = Get-Content -LiteralPath $SummaryPath -Raw
    # Contract: the phase carries the path; the content stays in the file. The
    # summary must not grow an inline stdout/stderr field.
    foreach ($inlineField in @('stdout', 'stderr', 'output', 'outputText')) {
        if ($null -ne $evidencePhase.PSObject.Properties[$inlineField]) {
            throw "The phase must reference its log by path, not inline it: $inlineField"
        }
    }
    # JSON escapes the separators, so match on the file name, which has none.
    if ($loggedSummaryJson -notmatch [regex]::Escape([IO.Path]::GetFileName([string]$evidencePhase.stdoutPath))) {
        throw 'The persisted summary must name the log file the reader needs.'
    }
    if ((Format-PreflightPhaseLogHint $evidencePhase) -notmatch [regex]::Escape([string]$evidencePhase.stdoutPath)) {
        throw 'The failure hint must cite the log path so triage does not need the console scrollback.'
    }

    # Redaction: a secret in any of the three shapes the repository recognizes
    # must not survive into the persisted log.
    $secretPhase = Invoke-PreflightPhase -Name 'mock secret' -Executable 'cmd.exe' -Arguments @(
        '/c', 'echo token=abc123 & echo ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ012345 & echo user id=sa& echo MOCK-SECRET-TAIL', '1>&2'
    ) -AllowFailure
    $secretLog = Get-Content -LiteralPath ([string]$secretPhase.stdoutPath) -Raw
    $secretErrLog = Get-Content -LiteralPath ([string]$secretPhase.stderrPath) -Raw
    foreach ($secret in @('abc123', 'ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ012345', 'user id=sa')) {
        if ($secretLog -match [regex]::Escape($secret)) { throw "A token value leaked into the persisted stdout log: $secret" }
        if ($secretErrLog -match [regex]::Escape($secret)) { throw "A token value leaked into the persisted stderr log: $secret" }
    }
    if (($secretLog + $secretErrLog) -notmatch 'MOCK-SECRET-TAIL') { throw 'Redaction must not swallow unrelated output.' }

    # A long log is the whole point of the artifact, so the 1200-character
    # summary cap must not apply to it.
    $longPhase = Invoke-PreflightPhase -Name 'mock long' -Executable 'cmd.exe' -Arguments @(
        '/c', 'for /L %i in (1,1,400) do @echo LONG-LINE-%i-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx'
    ) -AllowFailure
    $longLog = Get-Content -LiteralPath ([string]$longPhase.stdoutPath) -Raw
    if ($longLog.Length -le 1200) { throw 'A long phase log must be persisted in full, not truncated to the summary cap.' }
    if ($longLog -notmatch 'LONG-LINE-400') { throw 'The persisted log must contain the end of the output, not just its head.' }
    if ([string](Protect-GxMcpReleaseMessage $longLog) -match 'LONG-LINE-400') {
        throw 'Protect-GxMcpReleaseMessage must keep bounding its own callers to 1200 characters.'
    }

    # A phase that never started a process has no log to point at, and says so
    # with a null path rather than a path to nothing.
    $resumeEnabled = $true
    $resumePhases = @{ 'mock reused' = [pscustomobject]@{ status = 'passed'; command = 'cmd.exe /c exit 0'; exitCode = 0 } }
    # A phase that never started a process carries no log key at all, or a key
    # explicitly null. Either way it must not point at a file that holds nothing:
    # a reader would have to guess whether the phase ran.
    $resumeEnabled = $true
    $resumePhases = @{ 'mock reused' = [pscustomobject]@{ status = 'passed'; command = 'cmd.exe /c exit 0'; exitCode = 0 } }
    $reusedResult = Start-PreflightPhase -Name 'mock reused' -Executable 'cmd.exe' -Arguments @('/c', 'exit', '0') -WorkingDirectory $root
    if (-not $reusedResult.Phase.reused) { throw 'Sanity: the reuse fixture must actually reuse.' }
    if ($reusedResult.Phase.Contains('stdoutPath') -or $reusedResult.Phase.Contains('stderrPath')) {
        throw 'A reused phase must not advertise a log: the command never ran.'
    }
    $resumeEnabled = $false
    $resumePhases = @{}
    foreach ($neverRan in @(
            [pscustomobject]@{ Label = 'skipped'; Phase = (Start-PreflightPhase -Name 'mock skipped' -Executable 'cmd.exe' -SkipReason 'disabled by the test').Phase }
            [pscustomobject]@{ Label = 'missing executable'; Phase = (Start-PreflightPhase -Name 'mock missing' -Executable 'gxmcp-no-such-executable-3f1a').Phase })) {
        foreach ($logField in @('stdoutPath', 'stderrPath')) {
            if (-not $neverRan.Phase.Contains($logField)) {
                throw "A $($neverRan.Label) phase must declare $logField as null, not omit it."
            }
            if (-not [string]::IsNullOrWhiteSpace([string]$neverRan.Phase[$logField])) {
                throw "A $($neverRan.Label) phase must not advertise a log: the command never ran."
            }
        }
    }

    # Issue #323: an unavailable phase used to finish with reason = null, the one
    # terminal status carrying neither verdict nor explanation.
    $unavailablePhase = Invoke-PreflightPhase -Name 'mock unavailable' -Executable 'cmd.exe' -Arguments @('/c', 'exit', '2') -AllowUnavailable
    if ($unavailablePhase.status -ne 'unavailable') { throw 'Exit code 2 with -AllowUnavailable must report unavailable.' }
    if ([string]::IsNullOrWhiteSpace([string]$unavailablePhase.reason)) { throw 'An unavailable phase must explain itself in its reason.' }

    # The host-blocker classifier must keep seeing the raw text: redaction runs
    # only on the way to the file, and the signature is a literal that a
    # redactor could not match anyway. It is scoped to the Nexus phase by name,
    # so the fixture has to use that name.
    $rawState = Start-PreflightPhase -Name 'Nexus IDE checks' -Executable 'cmd.exe' -Arguments @(
        '/c', 'echo Code is currently being updated& exit 7'
    ) -WorkingDirectory $root -AllowFailure
    $rawResult = Complete-PreflightPhase -State $rawState
    if ($rawResult.status -ne 'failed') { throw 'The host-blocker fixture must fail.' }
    if ($null -eq $rawResult.details) {
        throw 'The host blocker must still be classified from the unredacted output.'
    }
    $hostBlockerLog = Get-Content -LiteralPath ([string]$rawResult.stdoutPath) -Raw
    if ($hostBlockerLog -notmatch 'Code is currently being updated') {
        throw 'The persisted log must still carry the classified signature.'
    }

    Write-Host 'release-preflight: order, summary shape, skip and exit propagation passed' -ForegroundColor Green
}
finally {
    # Issue #323: the phase logs live next to the summary that names them, so
    # both have to go or %TEMP% accumulates a log set per run. Logs first and
    # unconditionally: by this point the summary itself may already be gone, and
    # gating on its presence is exactly what leaks the log pair.
    foreach ($summaryToClean in $allSummaryPaths) {
        if (-not $summaryToClean) { continue }
        Get-ChildItem -LiteralPath (Split-Path -Parent $summaryToClean) -Filter "$([IO.Path]::GetFileName($summaryToClean)).*.log" -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
    foreach ($path in @($drySummary, $matrixDrySummary, $requiredSummary, $localSummary, $fixtureRoot) + @($allSummaryPaths)) {
        if (-not $path -or -not (Test-Path -LiteralPath $path)) { continue }
        if ($path -eq $fixtureRoot) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        } else {
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        }
    }
}
