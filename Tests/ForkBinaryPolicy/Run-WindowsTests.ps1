param(
    [string]$DotNet = 'dotnet',
    [string]$SdkVersion = '',
    [switch]$NoBuild
)
# Focused REAL Windows/WPF-linked tests, not the portable substituted-boundary runner.
# Never invokes the GUI, a downloader or the unpacker. HTTP has an injected tripwire.
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Run this wrapper in the parent-controlled Windows test environment.' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$globalJson = Join-Path $root 'global.json'
$original = [IO.File]::ReadAllBytes($globalJson)
$results = Join-Path $root '.audit-evidence\windows-binary-policy'
New-Item -ItemType Directory -Force -Path $results | Out-Null
$trx = Join-Path $results 'binary-policy.trx'
if (Test-Path $trx) { Remove-Item $trx }
Push-Location $root
try {
    if ($SdkVersion) {
        $config = Get-Content -Raw $globalJson | ConvertFrom-Json
        $config.sdk.version = $SdkVersion
        [IO.File]::WriteAllText($globalJson, ($config | ConvertTo-Json -Depth 20))
    }
    $arguments = @('test', 'Tests\WolvenKit.UnitTests\WolvenKit.UnitTests.csproj', '-c', 'Release',
        '-p:Platform=x64', '-p:EnableWindowsTargeting=true',
        '--filter', 'FullyQualifiedName~WolvenKit.UnitTests.ForkBinaryUpdatePolicyTests',
        '--logger', 'trx;LogFileName=binary-policy.trx', '--results-directory', $results)
    if ($NoBuild) { $arguments += '--no-build' }
    & $DotNet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Focused dotnet test failed: $LASTEXITCODE" }
    if (!(Test-Path $trx)) { throw 'No TRX was produced; not a verified test run.' }
    [xml]$report = Get-Content -Raw $trx
    $counters = $report.TestRun.ResultSummary.Counters
    if ([int]$counters.executed -lt 1 -or [int]$counters.failed -ne 0 -or [int]$counters.passed -ne [int]$counters.executed) {
        throw 'Expected a nonempty passing test run; inspect the TRX.'
    }
    Write-Host "Verified $($counters.passed) focused Windows tests; evidence: $trx"
} finally {
    [IO.File]::WriteAllBytes($globalJson, $original)
    Pop-Location
}
