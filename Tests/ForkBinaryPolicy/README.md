# Downstream binary-update policy tests

This fork intentionally disables **in-app binary checks and installation on every platform**. This is product policy, not a persisted setting and not conditional on `WOLVENKIT_PROTON_COMPAT`. It does not certify Windows runtime compatibility or disable the separate script/template updater.

## Linux-executable substituted-boundary tests

```sh
dotnet run --project Tests/ForkBinaryPolicy/Portable/ForkBinaryPolicy.Portable.csproj -c Release -- checks ui constructor matrix
```

Requires the repository SDK (or a private, byte-restored SDK override) and NuGet dependencies. The portable runner links the real production `UpdateService`, `IUpdateService`, `MinimalGithubRelease`, `ForkBinaryUpdatePolicy`, and `UpdateDialogViewModel`. Only unavailable platform/application boundaries are substituted by `Portable/Stubs.cs`: settings/app-data, DesktopBridge, assembly version, logger, AppViewModel and the dialog base. Semver, Newtonsoft.Json and ObservableObject are real dependencies. This is **not Windows/WPF execution**.

The existing private HttpClient is replaced by reflection before any updater method is invoked. Its handler throws at the first HTTP boundary; it never issues requests or returns a release that could reach extraction/process/exit. The original updater failed at that tripwire. Keeping this injection test-only avoids changing production DI constructor selection. A renamed private transport makes the test fail rather than silently use real HTTP.

The full command verifies direct installation denial, startup availability, supplied malformed release metadata, explicit tag-check denial, disabled remote changelog, every public dialog state entry (including Retry's execution entry), no false latest-version claim, no Update/Retry buttons, and constructor/app-data laziness. A matrix covers stable/nightly/unknown channel, both saved startup/ask values, compatibility flag unset/0/1, and both skip-permission values. No test downloads, extracts, launches an updater or exits the application.

## Parent-controlled Windows tests

```powershell
.\Tests\ForkBinaryPolicy\Run-WindowsTests.ps1
# Optional: select the SDK installed in the isolated test checkout, restoring global.json afterward.
.\Tests\ForkBinaryPolicy\Run-WindowsTests.ps1 -SdkVersion 10.0.113
```

The wrapper runs only `WolvenKit.UnitTests.ForkBinaryUpdatePolicyTests` against the real Windows-targeted App assembly and interfaces. It rejects non-Windows environments and verifies a nonempty all-passing TRX. It does not launch the GUI. `-NoBuild` is available only when the exact test checkout has already been built. Use a disposable Windows test checkout; the wrapper restores its SDK override in `finally`.

Actual interactive Home/Settings/Retry/startup behavior remains a parent-owned WPF acceptance check. A successful Linux cross-build is not a Windows execution pass.
