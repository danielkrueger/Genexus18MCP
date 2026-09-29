# GeneXus 18 MCP — diagnostics collector for bug reports
# =======================================================
#
# Produces ONE redacted text file you can paste into a GitHub issue so we can
# debug worker crashes / disconnects / timeouts without asking for your KB.
#
# What it collects (all local, nothing is uploaded):
#   - versions (OS, Node, npm, installed genexus-mcp, on-PATH copies)
#   - the durable worker crash ledger (%LOCALAPPDATA%\GenexusMCP\worker-crashes.jsonl)
#   - the update-check cache
#   - crash / cold-start / tool-latency lines from worker_debug.log (NOT the whole log)
#
# What it does NOT collect: your source code, object contents, or full logs.
# Absolute paths, your Windows user/host name, and KB paths are redacted to
# placeholders (<HOME>, <USER>, <HOST>, <KB>), and credential values (tokens,
# passwords, api keys, authorization headers, connection strings, user ids, and
# the ghp_/github_pat_/npm_ token prefixes) are replaced with [REDACTED].
# ALWAYS skim the output before pasting — if you spot anything sensitive, delete
# that line.
#
# Usage:
#   pwsh -File scripts\collect-diagnostics.ps1
#   pwsh -File scripts\collect-diagnostics.ps1 -WorkerLog "C:\path\to\worker_debug.log"
#   pwsh -File scripts\collect-diagnostics.ps1 -OutFile "C:\tmp\gxmcp-diag.txt"

[CmdletBinding()]
param(
    [string]$WorkerLog,
    [string]$OutFile,
    [int]$LogLines = 120,
    [int]$CrashEntries = 40
)

$ErrorActionPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot 'release-contract.ps1')
$sb = New-Object System.Text.StringBuilder
function Add-Line([string]$s = '') { [void]$sb.AppendLine($s) }
function Section([string]$t) { Add-Line ''; Add-Line "===== $t =====" }

# ── Redaction ────────────────────────────────────────────────────────────
# Two layers. This script's own layer is identity redaction: it knows the local
# home/user/host values and replaces them, which no shared helper can do. The
# canonical credential layer is delegated to release-contract.ps1 (issue #327):
# the crash ledger and the worker_debug.log excerpts below routinely carry
# connection strings and tool arguments, and the bug-report template tells users
# to paste this bundle into a public issue, so those lines needed the same
# credential redaction the release pipeline already applies.
$home_ = $env:USERPROFILE
$user  = $env:USERNAME
$host_ = $env:COMPUTERNAME
function Redact([string]$text) {
    if ([string]::IsNullOrEmpty($text)) { return $text }
    # KB paths first (most specific), then home, then bare user/host tokens.
    $text = [Regex]::Replace($text, '(?i)[A-Z]:\\KBs\\[^\\"\s]+', '<KB>')
    if ($home_) { $text = $text.Replace($home_, '<HOME>') }
    if ($user)  { $text = [Regex]::Replace($text, [Regex]::Escape($user), '<USER>') }
    if ($host_) { $text = [Regex]::Replace($text, [Regex]::Escape($host_), '<HOST>') }
    # Any remaining C:\Users\<name>\... → <HOME>\...
    $text = [Regex]::Replace($text, '(?i)[A-Z]:\\Users\\[^\\"\s]+', '<HOME>')
    # Credential redaction last: it is the canonical union (token/password/pwd/
    # secret/api_key/authorization/connection string/user id plus the ghp_/
    # github_pat_/npm_ literal prefixes), with the value read as a unit so a
    # phrase secret or a quoted value cannot leak its tail.
    $text = Protect-GxMcpReleaseText $text
    return $text
}

Add-Line "GeneXus 18 MCP diagnostics"
Add-Line "(paths / user / host / KB names and credential values redacted — skim before sharing)"

# ── Versions ─────────────────────────────────────────────────────────────
Section 'Versions'
try { Add-Line ("OS: " + [System.Environment]::OSVersion.VersionString + " (" + $env:PROCESSOR_ARCHITECTURE + ")") } catch {}
try { Add-Line ("Node: " + (& node --version 2>&1)) } catch { Add-Line 'Node: <not found>' }
try { Add-Line ("npm: " + (& npm --version 2>&1)) } catch { Add-Line 'npm: <not found>' }
try {
    $gvers = & npm ls -g genexus-mcp --depth=0 2>&1 | Where-Object { $_ -match 'genexus-mcp@' }
    Add-Line ("npm -g genexus-mcp: " + ($gvers -join '; ').Trim())
} catch {}
try {
    $where = (& where.exe genexus-mcp 2>&1)
    Add-Line "on-PATH genexus-mcp:"
    foreach ($w in $where) { Add-Line ("  " + (Redact $w)) }
} catch {}

# ── GeneXus SDK Installations ────────────────────────────────────────────
Section 'GeneXus SDK Installations'
$gxRoots = @()
if ($env:GENEXUS_HOME) { $gxRoots += $env:GENEXUS_HOME }
foreach ($drive in @('C', 'D', 'E')) {
    $gxRoots += "${drive}:\Program Files (x86)\GeneXus"
    $gxRoots += "${drive}:\Program Files\GeneXus"
}
$foundGx = @()
foreach ($root in $gxRoots) {
    if (Test-Path $root) {
        if (Test-Path (Join-Path $root 'genexus.exe')) {
            $foundGx += $root
        } else {
            Get-ChildItem -Path $root -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                if (Test-Path (Join-Path $_.FullName 'genexus.exe')) {
                    $foundGx += $_.FullName
                }
            }
        }
    }
}
$foundGx = $foundGx | Select-Object -Unique
if ($foundGx.Count -eq 0) {
    Add-Line "<no GeneXus installations found in standard directories>"
} else {
    foreach ($g in $foundGx) {
        $exe = Join-Path $g 'genexus.exe'
        $commonDll = Join-Path $g 'Artech.Architecture.Common.dll'
        $pv = (Get-Item $exe -ErrorAction SilentlyContinue).VersionInfo.ProductVersion
        $fv = (Get-Item $exe -ErrorAction SilentlyContinue).VersionInfo.FileVersion
        $commonFv = (Get-Item $commonDll -ErrorAction SilentlyContinue).VersionInfo.FileVersion
        Add-Line "- Path: $(Redact $g)"
        Add-Line "  genexus.exe: ProductVersion=$pv FileVersion=$fv"
        if ($commonFv) { Add-Line "  Artech.Architecture.Common.dll: FileVersion=$commonFv" }
    }
}

# ── Active Configuration & KB ────────────────────────────────────────────
Section 'Active Configuration & KB'
$cfgCandidates = @()
if ($env:GX_CONFIG_PATH) { $cfgCandidates += $env:GX_CONFIG_PATH }
$cfgCandidates += @(
    (Join-Path $env:APPDATA 'GenexusMCP\config.json'),
    (Join-Path (Get-Location) 'config.json')
)
$cfgFound = $null
foreach ($c in $cfgCandidates) {
    if ($c -and (Test-Path $c)) { $cfgFound = $c; break }
}
if ($cfgFound) {
    Add-Line "Config file: $(Redact $cfgFound)"
    try {
        $raw = Get-Content $cfgFound -Raw | ConvertFrom-Json
        $gxPath = $raw.GeneXus.InstallationPath
        Add-Line "  Configured GeneXusPath: $(Redact $gxPath)"
        if ($gxPath -and (Test-Path $gxPath)) {
            $cExe = Join-Path $gxPath 'genexus.exe'
            if (Test-Path $cExe) {
                $cVer = (Get-Item $cExe).VersionInfo.ProductVersion
                Add-Line "  Configured SDK Version: $cVer"
            }
        }
        if ($raw.Environment.KBPath) {
            Add-Line "  Configured KBPath: $(Redact $raw.Environment.KBPath)"
        }
        if ($raw.Environment.DefaultKb) {
            Add-Line "  Configured DefaultKb: $(Redact $raw.Environment.DefaultKb)"
        }
        if ($raw.Server.TransportMode) {
            Add-Line "  TransportMode: $($raw.Server.TransportMode)"
        }
        if ($raw.Server.ResolutionPolicy) {
            Add-Line "  ResolutionPolicy: $($raw.Server.ResolutionPolicy)"
        }
    } catch {
        Add-Line "  <error reading JSON configuration: $($_.Exception.Message)>"
    }
} else {
    Add-Line "<no config.json found>"
}

# ── genexus-mcp doctor (best-effort; short) ──────────────────────────────
Section 'genexus-mcp doctor'
try {
    $doc = & genexus-mcp doctor 2>&1 | Select-Object -First 60
    foreach ($d in $doc) { Add-Line (Redact ([string]$d)) }
} catch { Add-Line '<doctor unavailable>' }

# ── Worker crash ledger (the key artifact) ───────────────────────────────
Section "Worker crash ledger (last $CrashEntries)"
$ledger = Join-Path $env:LOCALAPPDATA 'GenexusMCP\worker-crashes.jsonl'
if (Test-Path $ledger) {
    Get-Content $ledger -Tail $CrashEntries | ForEach-Object { Add-Line (Redact $_) }
} else {
    Add-Line "<no crash ledger at %LOCALAPPDATA%\GenexusMCP\worker-crashes.jsonl>"
}

# ── Update-check cache ───────────────────────────────────────────────────
Section 'Update-check cache'
$upd = Join-Path $env:LOCALAPPDATA 'GenexusMCP\update-check.json'
if (Test-Path $upd) { Get-Content $upd -Raw | ForEach-Object { Add-Line (Redact $_) } }
else { Add-Line '<none>' }

# ── worker_debug.log: crash / cold-start / latency lines only ────────────
Section "worker_debug.log markers (last $LogLines matching)"
$logCandidates = @()
if ($WorkerLog) { $logCandidates += $WorkerLog }
$logCandidates += @(
    (Join-Path $env:LOCALAPPDATA 'npm-cache\_npx'),
    (Join-Path $env:APPDATA 'npm\node_modules\genexus-mcp\publish\worker')
)
$logFile = $null
if ($WorkerLog -and (Test-Path $WorkerLog)) { $logFile = $WorkerLog }
if (-not $logFile) {
    $logFile = Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'npm-cache\_npx') -Recurse -Filter 'worker_debug.log' -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $logFile) {
    $alt = Join-Path $env:APPDATA 'npm\node_modules\genexus-mcp\publish\worker\worker_debug.log'
    if (Test-Path $alt) { $logFile = $alt }
}
if ($logFile -and (Test-Path $logFile)) {
    Add-Line ("(source: " + (Redact $logFile) + ")")
    Select-String -Path $logFile -Pattern '\[WORKER-CRASH\]|\[COLD-START\]|\[TOOL-LATENCY\]|WorkerNativeCrashRecovered' -ErrorAction SilentlyContinue |
        Select-Object -Last $LogLines | ForEach-Object { Add-Line (Redact $_.Line) }
} else {
    Add-Line '<worker_debug.log not found — pass -WorkerLog "C:\...\worker_debug.log">'
}

# ── Write ────────────────────────────────────────────────────────────────
if (-not $OutFile) { $OutFile = Join-Path (Get-Location) 'genexus-mcp-diagnostics.txt' }
[System.IO.File]::WriteAllText($OutFile, $sb.ToString(), [System.Text.UTF8Encoding]::new($false))
Write-Host "Diagnostics written to: $OutFile" -ForegroundColor Green
Write-Host "Skim it for anything sensitive, then paste it into your GitHub issue." -ForegroundColor Yellow
