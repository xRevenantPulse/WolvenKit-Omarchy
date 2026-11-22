param(
    [switch]$Baseline,
    [string]$Dll = (Join-Path $PSScriptRoot 'bin/Release/net10.0/ForkContentCache.dll'),
    [string]$TempRoot = (Join-Path $PSScriptRoot 'runtime-temp')
)
$ErrorActionPreference = 'Stop'
# The same framework-dependent assembly runs on Windows .NET 10, without WPF.
$cases = Get-Content -Raw (Join-Path $PSScriptRoot 'cases.json') | ConvertFrom-Json
if ($Baseline) { $cases = @(@('baseline'), @('baseline-scripts')) }
New-Item -ItemType Directory -Force -Path $TempRoot | Out-Null
$previousTemp = $env:TMPDIR
try {
    $env:TMPDIR = (Resolve-Path $TempRoot).Path
    foreach ($case in $cases) {
        $caseArgs = @($case)
        $start = New-Object System.Diagnostics.ProcessStartInfo
        $start.FileName = 'dotnet'
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        # Case values contain no double quotes. Quote each argument, including spaces/semicolons.
        $start.Arguments = '"' + $Dll + '" ' + (($caseArgs | ForEach-Object { '"' + $_ + '"' }) -join ' ')
        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $start
        if (!$process.Start()) { throw 'Could not start dotnet' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $output = $stdout.Result + $stderr.Result
        Write-Output "CASE $($caseArgs -join ' | ')"
        Write-Output $output
        if ($Baseline) {
            $expected = 'ASSERT ' + $caseArgs[0] + ': previous content lost; actual='
            if ($process.ExitCode -eq 0 -or !$output.Contains($expected)) {
                throw "Baseline $($caseArgs[0]) did not reproduce expected content-loss assertion (exit $($process.ExitCode))"
            }
        } elseif ($process.ExitCode -ne 0) {
            throw "Cache scenario failed (exit $($process.ExitCode))"
        }
        $process.Dispose()
    }
    if ($Baseline) { Write-Output 'PASS two expected content-loss assertions (not arbitrary hosting failures)' }
    else { Write-Output "PASS $($cases.Count) real Windows filesystem / fake HTTP scenarios" }
} finally { $env:TMPDIR = $previousTemp }
