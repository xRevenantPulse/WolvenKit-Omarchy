# Fork content-cache regression harness

Standalone .NET 10 console tests link the production `TransactionalContentUpdater.cs` directly. No WPF, live HTTP, game/download assets, external test packages, Wine, or copied runtime. Every scenario uses an isolated real filesystem and a fake `HttpMessageHandler`.

From this directory, using the repository's declared .NET SDK:

The compatibility lab may keep an ignored local `global.json` to select its installed SDK. That local override is excluded from the source allowlist and is not part of the published harness.

```sh
python run-tests.py
python run-tests.py --baseline
# Optional compatibility check, with separately supplied ZIPs:
python run-tests.py --artifact /path/to/scripts.zip --artifact /path/to/templates.zip
```

`cases.json` contains 69 invocations, including all original 23 scenarios. Windows .NET 10 can run the same framework-dependent DLL:

```powershell
dotnet build -c Release
.\Run-ContentCacheTests.ps1
.\Run-ContentCacheTests.ps1 -Baseline
# Or supply an independently built/copied runner with -Dll <path>.
```

The baseline wrappers require each named `ASSERT baseline[‑scripts]: previous content lost; actual=` message and a nonzero exit, not just any nonzero host/build failure. `Baseline.cs` and `ScriptBaseline.cs` preserve the original destructive update-method snapshots, with only injectable HTTP/base-directory and inert logging/UI substitutions. They intentionally reproduce content loss on ZIP HTTP503 while the old marker survives. They are test fixtures, not alternate production implementations.

## Coverage and provenance

- Original 23 scenarios retained: HTTP/marker errors, timeout/cancellation, already-canceled lock wait, corrupt ZIP/CRC, unsafe paths/symlinks/duplicates/empty archives, unchanged marker, file-child conflict, content/marker rename failures, cleanup warnings/aggregation and success.
- New device-name checks exercise COM/LPT with superscript 1/2/3, bare and extension forms, uppercase and lowercase. A representative `COM¹.txt` assertion was observed failing before the predicate correction. The full 24-name matrix is regression coverage, not 24 independently observed first-RED cycles.
- Component-tree rejection includes implicit parents, reversed order, explicit/implicit spelling conflicts, nested parents and file/directory roles. Existing full-name collision rejection remains. Consistent explicit directories and matching children (both entry orders) are positive controls; paths are never renamed/canonicalized.
- The original `stage` file/child fixture now expects `InvalidDataException` during pre-extraction validation rather than a host-dependent extraction `IOException`. `stage-io` separately uses a 300-character filename to cause a genuine filesystem staging failure without a commit seam; old content/marker must survive.
- Marker-backup failure must occur before any content rename. A real Unix permission fault / Windows file-sharing fault blocks content rollback after injected marker-commit failure; both exceptions, backup content, backup marker and the recovery path must survive, without restoring an old marker over un-restored new content.
- A throwing post-commit warning observer must not fail an already committed update. An attached trace listener must capture both the cleanup warning and observer exception.
- `lock-wait-cancel` starts with an uncanceled token, observes the task actually pending with zero HTTP, then cancels. The waiter cannot release a gate it never acquired.

The device predicate, tree validation, marker-backup ordering, rollback-marker ordering and warning-observer corrections each have observed pre-fix failing assertions and subsequent passing runs in the private handoff evidence. Several older tests and the new waiting-cancellation/staging-IO controls passed immediately; they are not retroactively described as first-RED work.

This is an operational rollback contract, not a crash-recovery or multi-process protocol. Windows permission/sharing behavior and the PowerShell wrapper require actual Windows execution; Linux passes and cross-builds are not Windows runtime passes. Do not run the pre-correction superscript runner on Windows: it intentionally contains the unsafe validation defect.
