# Finds private members in the Worker/Gateway that nothing references.
#
# C# does not warn on an unused private method, and a duplication scan does not
# see one either, so these survive every other check. This is a CANDIDATE
# GENERATOR, not an oracle: it over-reports by design, and every hit must be
# confirmed before anything is removed. See the two exclusions below.
#
# Two buckets, because "unreachable" is two different findings:
#
#   DEAD     - no production caller and nothing names it. Deletion candidate.
#   UNWIRED  - no production caller, but a test names it. An advertised
#              capability whose wiring never landed, or an internal kept alive
#              only by its own tests. Report it; deleting one deletes coverage.
#
# Known exclusions, and why each is a false positive rather than a miss:
#
#   * Constructors. `private Foo(...)` is called as `new Foo(...)`, and the
#     declared name is the class name, so the count is always 1. Constructors
#     are not reported.
#   * Fields initialised inline. `private readonly Dictionary<..> _x = new()` is
#     never referenced again - the initialiser IS the use - so the count is 1.
#     Fields are only reported when the declaration has no `= new` initialiser
#     and no `= <expr>` on the same line, which is the shape an orphan field
#     left by a refactor actually takes.
#
# Only the "no reference anywhere" signal is reported. An earlier version also
# flagged names that appeared only inside string literals, on the theory that
# this is the shape a dead reflection hook takes. That pass was removed: its
# quote-stripping mis-fired on ordinary call sites and reported six live methods
# as dead, which is worse than reporting nothing. A name with a single textual
# occurrence is a reliable candidate; "occurs only in a literal" is not
# detectable without a parser, and guessing is how this tool came to be wrong in
# the first place.
#
# Usage: pwsh -NoProfile -File scripts/find-unreachable.ps1
param(
  # Emit a self-test of the declaration parser before the scan.
  [switch]$SelfTest
)

$repo = (Get-Location).Path

# --- one pass: count every identifier occurrence ---------------------------
#
# Production and test references are counted SEPARATELY, and the reason is a
# demonstrated defect in the previous version. Declarations are read from the
# production projects only, but references were counted across all of `src`,
# tests included. So a private method was reported dead only if nothing at all
# named it - and a test that merely MENTIONED it silenced it completely.
#
# That is not hypothetical: the reachability guard in
# GxMcp.Worker.Tests/ModuleServiceInstallPathTests.cs names the six
# advertised-but-unwired members in its allowlist, and with that single file
# present this script reported 0 unreachable members while six existed. The
# silence was indistinguishable from a clean tree.
#
# A mention is not a call, so no textual count can fully close this. What the
# two counts do give is the distinction that matters: a member with no
# production caller is either dead (nothing names it) or unwired (something
# names it, but only a test). Those are opposite decisions - delete versus
# report - and collapsing them is what hid six findings.
#
# The two maps are Ordinal, not the PowerShell default. A case-insensitive
# hashtable merges members that differ only in case, and this repository has
# such a pair: PatternAnalysisService.FindWWPInstance and
# SaveAsService.FindWwpInstance are different methods on different classes. With
# the default comparer the second's four references were credited to the first,
# so the first was reported as live and this script missed a genuinely
# unreachable member. C# identifier matching is case-sensitive, and a tool that
# audits C# has to agree with the language.
$prodCounts = [hashtable]::new([System.StringComparer]::Ordinal)
$testCounts = [hashtable]::new([System.StringComparer]::Ordinal)
$idPattern = [regex]'[A-Za-z_][A-Za-z0-9_]*'

function Add-Counts([hashtable]$Into, [string]$Text) {
  foreach ($m in $idPattern.Matches($Text)) {
    $n = $m.Value
    if ($Into.ContainsKey($n)) { $Into[$n]++ } else { $Into[$n] = 1 }
  }
}

$prodFiles = Get-ChildItem -Path src\GxMcp.Worker, src\GxMcp.Gateway -Recurse -Include *.cs -File |
  Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$testFiles = Get-ChildItem -Path src -Recurse -Include *.cs -File |
  Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.DirectoryName -match 'Tests' }

foreach ($f in $prodFiles) { Add-Counts $prodCounts ([IO.File]::ReadAllText($f.FullName)) }
foreach ($f in $testFiles) { Add-Counts $testCounts ([IO.File]::ReadAllText($f.FullName)) }

# dead    = no production caller and nothing names it. Delete candidate.
# unwired = no production caller, but a test names it. An advertised capability
#          whose wiring is missing, or an internal kept alive only by its own
#          tests. Report, do not delete.
# live    = a production caller exists.
function Get-Reachability([string]$Name) {
  $prod = if ($prodCounts.ContainsKey($Name)) { $prodCounts[$Name] } else { 0 }
  $test = if ($testCounts.ContainsKey($Name)) { $testCounts[$Name] } else { 0 }
  if ($prod -gt 1) { return 'live' }
  if ($test -gt 0) { return 'unwired' }
  return 'dead'
}


# --- declaration parsing ---------------------------------------------------
# Token-based, not a regex over the type: an earlier version used a greedy
# character class for the type, which swallowed the member name and reported
# every private member in the repo as unused. Get-DeclaredMember returns a
# hashtable: Name, Kind (ctor|field|method), Initialised.
$keywords = @('if','for','foreach','while','switch','return','new','get','set',
              'using','lock','do','else','case','break','continue','throw','yield')

function Get-DeclaredMember([string]$line, [string]$typeName) {
  if ($line -notmatch '^\s*private\s') { return $null }
  $body = $line -replace '^\s*private\s+', ''
  $body = $body -replace '^(static\s+|readonly\s+|const\s+|virtual\s+|override\s+|sealed\s+|async\s+|extern\s+|unsafe\s+|volatile\s+)+', ''
  if ($body -eq $line -or $body.Trim() -eq '') { return $null }

  $opener = [regex]::Match($body, '[({=;]')
  if (-not $opener.Success) { return $null }
  $head = $body.Substring(0, $opener.Index)
  $tokens = @([regex]::Matches($head, '[A-Za-z_][A-Za-z0-9_]*') | ForEach-Object { $_.Value })
  if ($tokens.Count -eq 0) { return $null }
  $name = $tokens[$tokens.Count - 1]
  if ($keywords -contains $name) { return $null }

  # A `(` as the first opener with the name equal to the enclosing type is a
  # constructor; it is reached through `new Type(...)`, not by name.
  if ($opener.Value -eq '(' -and $name -eq $typeName) {
    return [PSCustomObject]@{ Name = $name; Kind = 'ctor'; Initialised = $true }
  }

  $initialised = $opener.Value -eq '='
  $kind = if ($opener.Value -eq '(') { 'method' } else { 'field' }
  [PSCustomObject]@{ Name = $name; Kind = $kind; Initialised = $initialised }
}

if ($SelfTest) {
  $cases = @(
    @{ Line = '        private static string ParseConfig(string path)'; Type = 'Configuration'; Want = 'method' }
    @{ Line = '        private readonly object _lock = new object();'; Type = 'CrashLedger'; Want = 'field' }
    @{ Line = '        private const int MaxSamples = 24;'; Type = 'Registry'; Want = 'field' }
    @{ Line = '        private static bool IsOk(string? s)'; Type = 'X'; Want = 'method' }
    @{ Line = '        private Dictionary<string, JObject> Groups { get; private set; }'; Type = 'Result'; Want = 'field' }
    @{ Line = '        private CommandDispatcher()'; Type = 'CommandDispatcher'; Want = 'ctor' }
    @{ Line = '        private KbHandle(string key) : base(key)'; Type = 'KbHandle'; Want = 'ctor' }
    @{ Line = '        private static string[] Candidates()'; Type = 'Catalog'; Want = 'method' }
  )
  $bad = 0
  foreach ($c in $cases) {
    $got = Get-DeclaredMember $c.Line $c.Type
    $kind = if ($got) { $got.Kind } else { '<none>' }
    $ok = $kind -eq $c.Want
    if (-not $ok) { $bad++ }
    '{0}  [{1}]  want={2} got={3}' -f $(if ($ok) { 'ok  ' } else { 'FAIL' }), $c.Line.Trim(), $c.Want, $kind
  }

  # The classifier, not just the parser. These pin the distinction that was
  # collapsed before: a name a test merely mentions is 'unwired', not 'dead',
  # and the two lead to opposite decisions. A classifier that cannot tell them
  # apart silently reports a clean tree.
  $classify = @(
    @{ Prod = 2; Test = 0; Want = 'live' }
    @{ Prod = 7; Test = 3; Want = 'live' }
    @{ Prod = 1; Test = 4; Want = 'unwired' }
    @{ Prod = 1; Test = 1; Want = 'unwired' }
    @{ Prod = 1; Test = 0; Want = 'dead' }
    @{ Prod = 0; Test = 0; Want = 'dead' }
  )
  foreach ($c in $classify) {
    $prodCounts['Sentinel'] = $c.Prod
    $testCounts['Sentinel'] = $c.Test
    $got = Get-Reachability 'Sentinel'
    $ok = $got -eq $c.Want
    if (-not $ok) { $bad++ }
    '{0}  [prod={1} test={2}]  want={3} got={4}' -f $(if ($ok) { 'ok  ' } else { 'FAIL' }), $c.Prod, $c.Test, $c.Want, $got
  }
  Remove-Item prodCounts['Sentinel'] -ErrorAction SilentlyContinue
  Remove-Item testCounts['Sentinel'] -ErrorAction SilentlyContinue

  # Case sensitivity, as a classifier case rather than a comment. C# identifiers
  # are case-sensitive, so two members differing only in case are two members.
  # The two values must differ: a case-insensitive map is last-writer-wins, so
  # equal values would give the same answer under either comparer and the case
  # would prove nothing. With a large value stored under the lower-cased key, a
  # case-insensitive lookup of 'Sentinel' returns that value and reports the
  # member as live - which is exactly how PatternAnalysisService.FindWWPInstance
  # stayed hidden behind SaveAsService.FindWwpInstance.
  $prodCounts['Sentinel'] = 1
  $prodCounts['sentinel'] = 99
  $caseGot = Get-Reachability 'Sentinel'
  $caseOk = $caseGot -eq 'dead'
  if (-not $caseOk) { $bad++ }
  '{0}  [Sentinel=1, sentinel=99]  want=dead got={1}' -f $(if ($caseOk) { 'ok  ' } else { 'FAIL' }), $caseGot
  Remove-Item prodCounts['Sentinel'] -ErrorAction SilentlyContinue
  Remove-Item prodCounts['sentinel'] -ErrorAction SilentlyContinue

  "self-test: $($cases.Count + $classify.Count + 1 - $bad)/$($cases.Count + $classify.Count + 1) passed"
  if ($bad -gt 0) { exit 1 }
  return
}

# --- scan ------------------------------------------------------------------
$targets = Get-ChildItem -Path src\GxMcp.Worker, src\GxMcp.Gateway -Recurse -Include *.cs -File |
  Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

$dead = @()
$unwired = @()
foreach ($f in $targets) {
  $lines = Get-Content $f.FullName
  $typeName = [IO.Path]::GetFileNameWithoutExtension($f.Name)
  for ($i = 0; $i -lt $lines.Count; $i++) {
    $member = Get-DeclaredMember $lines[$i] $typeName
    if (-not $member) { continue }
    if ($member.Kind -eq 'ctor') { continue }
    # An inline initialiser is the use; only an uninitialised field is a candidate.
    if ($member.Kind -eq 'field' -and $member.Initialised) { continue }

    $bucket = Get-Reachability $member.Name
    if ($bucket -eq 'live') { continue }

    $row = [PSCustomObject]@{
      File = $f.FullName.Replace("$repo\", ''); Line = $i + 1; Kind = $member.Kind
      Name = $member.Name; Text = $lines[$i].Trim()
    }
    if ($bucket -eq 'dead') { $dead += $row } else { $unwired += $row }
  }
}

"unreachable private members: dead=$($dead.Count) unwired=$($unwired.Count)"
''
'DEAD - no production caller and nothing names it. Deletion candidates;'
'       confirm each before removing, as this tool over-reports by design.'
$dead | Sort-Object File, Line | ForEach-Object { "  {0}:{1}  [{2}] {3}" -f $_.File, $_.Line, $_.Kind, $_.Text }
''
'UNWIRED - no production caller, but a test names it. These are advertised'
'          capabilities whose wiring never landed, or internals kept alive only'
'          by their own tests. Report them; deleting one deletes its coverage.'
$unwired | Sort-Object File, Line | ForEach-Object { "  {0}:{1}  [{2}] {3}" -f $_.File, $_.Line, $_.Kind, $_.Text }
