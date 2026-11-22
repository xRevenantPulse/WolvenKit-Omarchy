# BINARY-001 standalone installer policy regression

`Installer.Portable.csproj` links the **actual Unpacker Program.cs**, policy and real System.CommandLine package. Only unqualified Process/Directory/File/Environment resolve to narrow throwing tripwires. It is safe to run the original source in this project: even enumeration/Exists are intercepted. No installer is run against a filesystem installation.

```sh
dotnet run --project Tests/ForkBinaryPolicy/Installer/Installer.Portable.csproj -c Release
```

Tests cover public Main with normal/missing/malformed/help/null arguments, exact branded stderr and exit1, direct actual async handler with normal/empty/null paths, and both private side-effect helpers. Every substituted boundary count must remain zero. Main returns normally; it does not call Environment.Exit. Direct helpers throw the policy's exact InvalidOperationException before path inspection/effects.

`Windows/Installer.Windows.csproj` references the real Unpacker project, not linked/substituted installer code or the App/GUI assembly. The shared policy is Compile-linked into Unpacker. The Windows runner calls real Main and uses reflection for the real private helpers; it has **no boundary counters**. Compile success is not execution evidence.

```sh
dotnet build Tests/ForkBinaryPolicy/Installer/Windows/Installer.Windows.csproj -c Release -p:EnableWindowsTargeting=true
```

## Parent-only guarded Windows acceptance

Never run the preserved baseline as an installer. Verify candidate artifact hashes from the private review manifest first. Parent owns Windows/Proton execution; no such execution was performed by the correction worker. Use only a disposable sentinel directory, never an installed application or production prefix. Keep the complete self-contained output directories together, not just their exe files.

Executables, relative to checkout:

- `WolvenKit.Unpacker/bin/Release/net10.0-windows10.0.26100.0/win-x64/WolvenKit.Unpacker.exe`
- `Tests/ForkBinaryPolicy/Installer/Windows/bin/Release/net10.0-windows10.0.26100.0/win-x64/Installer.Windows.exe`

PowerShell example, **not executed by worker** (replace `$sandbox` with a new disposable location):

```powershell
$sandbox = 'C:\disposable-binary-policy-sentinels'
if (Test-Path $sandbox) { throw 'Choose a NEW disposable directory' }
New-Item -ItemType Directory "$sandbox\install", "$sandbox\payload" | Out-Null
Set-Content "$sandbox\install\WolvenKit.exe" 'target sentinel, not an executable'
Set-Content "$sandbox\payload\WolvenKit.exe" 'payload sentinel, not an executable'
$before = @(Get-ChildItem $sandbox -Recurse -File | Get-FileHash | Sort-Object Path)
& '.\WolvenKit.Unpacker\bin\Release\net10.0-windows10.0.26100.0\win-x64\WolvenKit.Unpacker.exe' --wolvenkit-exe-path "$sandbox\install\WolvenKit.exe" --unzipped-path "$sandbox\payload"
if ($LASTEXITCODE -ne 1) { throw 'Expected standalone policy denial exit1' }
& '.\Tests\ForkBinaryPolicy\Installer\Windows\bin\Release\net10.0-windows10.0.26100.0\win-x64\Installer.Windows.exe' "$sandbox\install\WolvenKit.exe" "$sandbox\payload"
if ($LASTEXITCODE -ne 0) { throw 'Focused candidate runner failed' }
$after = @(Get-ChildItem $sandbox -Recurse -File | Get-FileHash | Sort-Object Path)
if (Compare-Object $before $after -Property Path,Hash) { throw 'Sentinels changed' }
```

Expected standalone stderr is exactly `ForkBinaryUpdatePolicy.Message` followed by newline, exit1. Expected focused runner exit0 and eight PASS lines: five Main cases, direct handler normal/empty/null, direct IsProcessRunningByPath and direct MoveDirectoryWithOverwrite. Windows lines end with `real installer; no substituted-boundary counters`; Linux lines end with `zero boundary calls`. These Windows outcomes are expectations for parent verification, not worker runtime results.

This is a fixed product-update policy, not anti-tamper: manual external extraction/replacement of reviewed portable builds remains possible. No release provenance or automatic download fallback is provided.
