$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$doctor = Join-Path $root 'scripts/release-doctor.ps1'
. (Join-Path $root 'scripts/release-contract.ps1')
$temp = Join-Path $env:TEMP ('gxmcp-release-doctor-test-' + [guid]::NewGuid().ToString('N'))
$fixture = Join-Path $temp 'repo'
# Record the root the way the real preflight does (release-preflight.ps1:102 runs it
# through ConvertTo-GxMcpReleasePath). On a host whose %TEMP% is an 8.3 short path the
# raw $fixture is a different string than the normalized root the doctor derives from
# its own inputs, which made the matching-preflight case fail for a reason that has
# nothing to do with the repository state. See #322.
$fixtureRoot = ConvertTo-GxMcpReleasePath -Path $fixture
$publish = Join-Path $fixture 'publish'
New-Item -ItemType Directory -Path (Join-Path $publish 'worker') -Force | Out-Null
try {
    $oldGxPath = $env:GX_PATH
    $env:GX_PATH = 'C:\SDK\doctor-fixture'
    Set-Content -LiteralPath (Join-Path $fixture 'package.json') -Value '{"name":"fixture","version":"3.0.1"}' -Encoding utf8
    & git -C $fixture init -q
    & git -C $fixture config user.email 'doctor@example.invalid'
    & git -C $fixture config user.name 'doctor-test'
    & git -C $fixture add package.json
    & git -C $fixture commit -q -m 'fixture source'
    $fixtureCommit = (git -C $fixture rev-parse HEAD).Trim()
    Set-Content -LiteralPath (Join-Path $publish 'GxMcp.Gateway.exe') -Value 'gateway' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $publish 'worker/GxMcp.Worker.exe') -Value 'worker' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $publish 'tool_definitions.json') -Value '{}' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $publish 'nexus-ide.vsix') -Value 'vsix' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $fixture 'nexus-ide-3.0.1.vsix') -Value 'vsix' -Encoding ascii
    foreach ($relative in @('src/GxMcp.Gateway/bin/Release/net10.0-windows/GxMcp.Gateway.exe', 'src/GxMcp.Worker/bin/x86/Release/GxMcp.Worker.exe')) {
        $sourcePath = Join-Path $fixture ($relative -replace '/', '\')
        New-Item -ItemType Directory -Path (Split-Path -Parent $sourcePath) -Force | Out-Null
        $sourceValue = if ($relative -match 'Gateway') { 'gateway' } else { 'worker' }
        Set-Content -LiteralPath $sourcePath -Value $sourceValue -Encoding ascii
    }
    Set-Content -LiteralPath (Join-Path $fixture 'publish.zip') -Value 'publish-zip' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $fixture 'publish.zip.sha256') -Value 'checksum' -Encoding ascii
    $processTrxRoot = Join-Path $temp 'process-trx'
    New-Item -ItemType Directory -Path $processTrxRoot -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $processTrxRoot 'process.trx') -Value '<TestRun><ResultSummary><Counters total="1" /></ResultSummary></TestRun>' -Encoding ascii

    $noStatusPath = Join-Path $temp 'missing-status.json'
    $noPreflightPath = Join-Path $temp 'missing-preflight.json'
    $raw = @(& pwsh -NoProfile -File $doctor -Root $fixture -Version 3.0.1 -StatusFile $noStatusPath -PreflightSummaryPath $noPreflightPath -Json)
    if ($LASTEXITCODE -ne 0) { throw "Release doctor failed without status: $($raw -join "`n")" }
    $noStatus = ($raw -join "`n") | ConvertFrom-Json
    if ($noStatus.status -ne $null -or $noStatus.preflightMatchesInputs -or $noStatus.retryCommand -match '-SkipTests') {
        throw 'Doctor must fail closed when no status or preflight evidence exists.'
    }
    $malformedStatusPath = Join-Path $temp 'malformed-status.json'
    [ordered]@{ version = '3.0.1'; tag = 'v3.0.1'; state = 'succeeded'; phase = 'complete' } |
        ConvertTo-Json | Set-Content -LiteralPath $malformedStatusPath -Encoding utf8
    $raw = @(& pwsh -NoProfile -File $doctor -Root $fixture -Version 3.0.1 -StatusFile $malformedStatusPath -PreflightSummaryPath $noPreflightPath -Json)
    if ($LASTEXITCODE -ne 0) { throw "Release doctor failed for incomplete status: $($raw -join "`n")" }
    $incompleteStatus = ($raw -join "`n") | ConvertFrom-Json
    if ($incompleteStatus.publicationEvidenceValid -or $incompleteStatus.nextAction -eq 'complete') {
        throw 'Incomplete status evidence must not be reported as complete.'
    }

    $statusPath = Join-Path $temp 'status.json'
    $preflightPath = Join-Path $temp 'preflight.json'
    $publicationPath = Join-Path $temp 'publication.json'
    [ordered]@{
        version = '3.0.1'; tag = 'v3.0.1'; phase = 'starting'; state = 'running'
        publicationState = 'not-started'; publicationEvidencePath = $publicationPath; error = 'token=doctor-secret'
    } | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding utf8
    [ordered]@{
        schemaVersion = 'gxmcp-release-preflight/1'; status = 'failed'; artifactFingerprint = 'fixture'
        root = $fixtureRoot; version = '3.0.1'; sourceCommit = $fixtureCommit; gxPath = $env:GX_PATH
        liveMode = 'single'; liveKbPath = 'C:\KBs\Fixture'; liveKbSource = 'explicit'; liveFixtureManifest = $null
        liveFixtureSource = 'none'; liveMajors = @(); liveGxPathMap = @(); requireLive = $false; requireBuildAll = $false
        skipLive = $false; skipWarningBaseline = $false; processSmokeMode = 'serial-after-parallel'; processSmokeTestCount = 1
        processSmokeResultsPath = $processTrxRoot; processSmokeBinaryFingerprint = Get-GxMcpReleaseProcessSmokeFingerprint -RepositoryRoot $fixture
        phases = @([ordered]@{ name = 'solution process smoke tests'; status = 'failed'; reason = 'timeout fixture' })
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $preflightPath -Encoding utf8

    $raw = @(& pwsh -NoProfile -File $doctor -Root $fixture -Version 3.0.1 -StatusFile $statusPath -PreflightSummaryPath $preflightPath -Json)
    if ($LASTEXITCODE -ne 0) { throw "Release doctor failed for preflight fixture: $($raw -join "`n")" }
    $doctorJson = ($raw -join "`n") | ConvertFrom-Json
    if ($doctorJson.nextAction -ne 'fix-preflight-and-retry') { throw "Doctor selected the wrong preflight action: $($doctorJson.nextAction)" }
    if (-not $doctorJson.requiredArtifactsReady) { throw 'Doctor did not recognize the complete fixture artifact set.' }
    if (($raw -join "`n") -match 'doctor-secret') { throw 'Doctor JSON exposed a sensitive status error.' }

    $matchingFingerprint = [string]$doctorJson.artifactFingerprint
    [ordered]@{
        version = '3.0.1'; tag = 'v3.0.1'; phase = 'packaging'; state = 'failed'; error = $null
        publicationState = 'not-started'; publicationEvidencePath = $publicationPath
    } | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding utf8
    [ordered]@{
        schemaVersion = 'gxmcp-release-preflight/1'; status = 'passed'; artifactFingerprint = $matchingFingerprint
        root = $fixtureRoot; version = '3.0.1'; sourceCommit = $fixtureCommit; gxPath = $env:GX_PATH
        liveMode = 'single'; liveKbPath = 'C:\KBs\Fixture'; liveKbSource = 'explicit'; liveFixtureManifest = $null
        liveFixtureSource = 'none'; liveMajors = @(); liveGxPathMap = @(); requireLive = $false; requireBuildAll = $false
        skipLive = $false; skipWarningBaseline = $false; processSmokeMode = 'serial-after-parallel'; processSmokeTestCount = 1
        processSmokeResultsPath = $processTrxRoot; processSmokeBinaryFingerprint = Get-GxMcpReleaseProcessSmokeFingerprint -RepositoryRoot $fixture
        phases = @(
            foreach ($phaseName in @('release metadata parity', 'tool contract validation', 'operation contract inventory', 'v3 plan readiness', 'warning baseline documentation parity', 'Python script tests', 'PowerShell script tests', 'CLI tests', 'CLI lint', 'Nexus IDE checks', 'solution build and tests', 'solution process smoke tests', 'Release warning baseline', 'live KB gate')) {
                $command = switch ($phaseName) {
                    'release metadata parity' { 'python scripts/verify-release-metadata.py --version 3.0.1' }
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
                [ordered]@{ name = $phaseName; status = 'passed'; command = $command; exitCode = 0; reason = $null }
            }
        )
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $preflightPath -Encoding utf8
    $raw = @(& pwsh -NoProfile -File $doctor -Root $fixture -Version 3.0.1 -StatusFile $statusPath -PreflightSummaryPath $preflightPath -LiveKbPath 'C:\KBs\Fixture' -Json)
    if ($LASTEXITCODE -ne 0) { throw "Release doctor failed for matching artifact fixture: $($raw -join "`n")" }
    $doctorJson = ($raw -join "`n") | ConvertFrom-Json
    if (-not $doctorJson.preflightMatchesInputs -or $doctorJson.retryCommand -notmatch '-SkipBuild.*-SkipTests') {
        throw 'Doctor did not recommend the safe artifact reuse for a matching preflight.'
    }
    Set-Content -LiteralPath (Join-Path $publish 'GxMcp.Gateway.exe') -Value 'gateway-modified' -Encoding ascii
    $raw = @(& pwsh -NoProfile -File $doctor -Root $fixture -Version 3.0.1 -StatusFile $statusPath -PreflightSummaryPath $preflightPath -LiveKbPath 'C:\KBs\Fixture' -Json)
    if ($LASTEXITCODE -ne 0) { throw "Release doctor failed for modified artifact fixture: $($raw -join "`n")" }
    $doctorJson = ($raw -join "`n") | ConvertFrom-Json
    if ($doctorJson.preflightMatchesInputs -or $doctorJson.retryCommand -match '-SkipTests') {
        throw 'Doctor recommended reuse after a required artifact changed.'
    }
    Set-Content -LiteralPath (Join-Path $publish 'GxMcp.Gateway.exe') -Value 'gateway' -Encoding ascii

    [ordered]@{
        schemaVersion = 'gxmcp-release-preflight/1'; status = 'passed'; artifactFingerprint = $matchingFingerprint
        root = $fixtureRoot; version = '3.0.1'; sourceCommit = $fixtureCommit; gxPath = $env:GX_PATH
        liveMode = 'single'; liveKbPath = 'C:\KBs\Fixture'; liveKbSource = 'explicit'; liveFixtureManifest = $null
        liveFixtureSource = 'none'; liveMajors = @(); liveGxPathMap = @(); requireLive = $false; requireBuildAll = $false
        skipLive = $false; skipWarningBaseline = $false; processSmokeMode = 'serial-after-parallel'; processSmokeTestCount = 1
        processSmokeResultsPath = $processTrxRoot; processSmokeBinaryFingerprint = Get-GxMcpReleaseProcessSmokeFingerprint -RepositoryRoot $fixture
        phases = @(
            foreach ($phaseName in @('release metadata parity', 'tool contract validation', 'operation contract inventory', 'v3 plan readiness', 'warning baseline documentation parity', 'Python script tests', 'PowerShell script tests', 'CLI tests', 'CLI lint', 'Nexus IDE checks', 'solution build and tests', 'solution process smoke tests', 'Release warning baseline', 'live KB gate')) {
                $command = switch ($phaseName) {
                    'release metadata parity' { 'python scripts/verify-release-metadata.py --version 3.0.1' }
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
                [ordered]@{ name = $phaseName; status = 'passed'; command = $command; exitCode = 0; reason = $null }
            }
        )
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $preflightPath -Encoding utf8
    $publicationAssets = @(
        foreach ($assetName in @('publish.zip', 'publish.zip.sha256', 'nexus-ide-3.0.1.vsix')) {
            $assetPath = Join-Path $fixture $assetName
            [ordered]@{ name = $assetName; size = (Get-Item -LiteralPath $assetPath).Length
                digest = 'sha256:' + (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
                state = 'uploaded'; updatedAt = '2026-09-23T00:00:00Z' }
        }
    )
    [ordered]@{
        schemaVersion = 'gxmcp-release-publication/1'; state = 'verified'; tag = 'v3.0.1'; version = '3.0.1'
        expectedCommit = $fixtureCommit; sourceCommit = $fixtureCommit; tagCommit = $fixtureCommit
        assetsUpdatedAtUtc = '2026-09-23T00:00:00Z'; assets = $publicationAssets
        workflowRunId = '77'; workflowStatus = 'completed'; workflowConclusion = 'success'; workflowUrl = 'https://github.com/lennix1337/Genexus18MCP/actions/runs/77'
        releasePublishedAtUtc = '2026-09-22T23:00:00Z'; npmVersion = '3.0.1'; npmGitHead = $fixtureCommit; releaseUrl = 'https://github.com/lennix1337/Genexus18MCP/releases/tag/v3.0.1'; error = $null
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $publicationPath -Encoding utf8
    [ordered]@{
        version = '3.0.1'; tag = 'v3.0.1'; repository = 'lennix1337/Genexus18MCP'; tagCommit = $fixtureCommit; phase = 'complete'; state = 'succeeded'
        publicationState = 'verified'; publicationEvidencePath = $publicationPath; nextAction = 'complete'
    } | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding utf8
    $raw = @(& pwsh -NoProfile -File $doctor -Root $fixture -Version 3.0.1 -StatusFile $statusPath -PreflightSummaryPath $preflightPath -Json)
    if ($LASTEXITCODE -ne 0) { throw "Release doctor failed for verified fixture: $($raw -join "`n")" }
    $doctorJson = ($raw -join "`n") | ConvertFrom-Json
    if ($doctorJson.nextAction -ne 'complete' -or $doctorJson.publication.npmVersion -ne '3.0.1') { throw 'Doctor did not recognize verified publication state.' }

    $remoteBin = Join-Path $temp 'remote-bin'
    New-Item -ItemType Directory -Path $remoteBin -Force | Out-Null
    $remoteRelease = [ordered]@{
        tagName = 'v3.0.1'; url = 'https://github.com/lennix1337/Genexus18MCP/releases/tag/v3.0.1'
        isDraft = $false; publishedAt = '2026-09-23T00:00:00Z'; assets = $publicationAssets
    } | ConvertTo-Json -Compress -Depth 8
    $remoteRun = '[{"databaseId":77,"status":"completed","conclusion":"success","headSha":"' + $fixtureCommit + '","headBranch":"v3.0.1","event":"release","createdAt":"2026-09-23T00:00:01Z","url":"https://github.com/lennix1337/Genexus18MCP/actions/runs/77"}]'
    Set-Content -LiteralPath (Join-Path $remoteBin 'git.cmd') -Value "@echo off`r`necho $fixtureCommit refs/tags/v3.0.1^{}`r`nexit /b 0" -Encoding ascii
    Set-Content -LiteralPath (Join-Path $remoteBin 'gh.cmd') -Value "@echo off`r`nif /I `"%~1`"==`"release`" echo $remoteRelease`r`nif /I `"%~1`"==`"run`" echo $remoteRun`r`nexit /b 0" -Encoding ascii
    Set-Content -LiteralPath (Join-Path $remoteBin 'npm.cmd') -Value "@echo [{`"version`":`"3.0.1`",`"gitHead`":`"$fixtureCommit`"}]" -Encoding ascii
    $oldRemotePath = $env:PATH
    try {
        $env:PATH = "$remoteBin;$oldRemotePath"
        $remoteRaw = @(& pwsh -NoProfile -File $doctor -Root $fixture -Version 3.0.1 -StatusFile $statusPath -PreflightSummaryPath $preflightPath -LiveKbPath 'C:\KBs\Fixture' -Remote -Repository 'lennix1337/Genexus18MCP' -Json)
        if ($LASTEXITCODE -ne 0) { throw "Release doctor failed for remote fixture: $($remoteRaw -join "`n")" }
        $remoteJson = ($remoteRaw -join "`n") | ConvertFrom-Json
        if ($remoteJson.nextAction -ne 'complete' -or -not $remoteJson.remote.publicationValid) { throw 'Doctor did not require a healthy remote publication before completion.' }
        Set-Content -LiteralPath (Join-Path $remoteBin 'npm.cmd') -Value "@echo [{`"version`":`"3.0.1`",`"gitHead`":`"deadbeef`"}]" -Encoding ascii
        $remoteRaw = @(& pwsh -NoProfile -File $doctor -Root $fixture -Version 3.0.1 -StatusFile $statusPath -PreflightSummaryPath $preflightPath -LiveKbPath 'C:\KBs\Fixture' -Remote -Repository 'lennix1337/Genexus18MCP' -Json)
        if ($LASTEXITCODE -ne 0) { throw "Release doctor failed for mismatched remote npm fixture: $($remoteRaw -join "`n")" }
        $remoteJson = ($remoteRaw -join "`n") | ConvertFrom-Json
        if ($remoteJson.nextAction -eq 'complete') { throw 'Doctor reported completion despite a mismatched remote npm commit.' }
    } finally {
        $env:PATH = $oldRemotePath
        if (Test-Path -LiteralPath $remoteBin) { Remove-Item -LiteralPath $remoteBin -Recurse -Force -ErrorAction SilentlyContinue }
    }

    $source = Get-Content -LiteralPath $doctor -Raw
    foreach ($forbidden in @('gh release create', 'gh release upload', 'npm publish', 'git push', 'Set-Content', 'Remove-Item')) {
        if ($source -match [regex]::Escape($forbidden)) { throw "Release doctor is not read-only: $forbidden" }
    }
    Write-Host 'release-doctor: local state, artifact readiness, recovery action and read-only contracts passed' -ForegroundColor Green
} finally {
    if ($null -ne $oldGxPath) { $env:GX_PATH = $oldGxPath } else { Remove-Item Env:GX_PATH -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
