# WolvenKit - Omarchy

An unofficial, Omarchy-focused fork of [WolvenKit](https://github.com/WolvenKit/WolvenKit) for Cyberpunk 2077 modding.

I maintain this fork to keep a tested WolvenKit setup working on my Omarchy desktop and to review upstream changes before adopting them. Omarchy is based on Arch Linux, but this is not a claim that the fork works on every Arch installation or Linux distribution.

The GUI is still the Windows WPF application running through Steam and Proton. This is not a native Linux port.

## Original project

WolvenKit and its contributors built the original editor. This fork keeps their source history, credits and GPL-3.0 license.

- [Original source code](https://github.com/WolvenKit/WolvenKit)
- [REDModding project website](http://redmodding.org/)
- [WolvenKit wiki](https://wiki.redmodding.org/wolvenkit)
- [Upstream download and Linux setup guide](https://wiki.redmodding.org/wolvenkit/getting-started/download)
- [Original README and credits](README.upstream.md)

This fork is not affiliated with or endorsed by the WolvenKit or Omarchy teams. Fork-specific compatibility reports belong here rather than being presented to upstream as failures of an unmodified release.

## What changed

The Omarchy compatibility mode is enabled only when `WOLVENKIT_PROTON_COMPAT=1` is present in the Windows process environment. It leaves the normal rendering path unchanged without that opt-in.

The mode uses an opaque main window, software WPF composition, native swapchain presentation and a precomputed viewcube texture. It also hides native preview surfaces behind embedded dialogs and requests a fresh frame when those surfaces become visible again. WPF composition and the GPU scene renderer are separate parts of this setup.

Other changes correct archive-import command notifications, handle canceled or empty file selections before any copy/delete, and add contextual render-error logging. The logging does not suppress exceptions or claim that a failed renderer loaded successfully.

These source changes do not install fonts or Microsoft's shader compiler. The tested editor prefix also uses a native `d3dcompiler_47` override and Liberation font substitutes. Those prerequisites still need to be configured for a new installation.

## Tested scope

The current source is based on WolvenKit main at `c6065b22f70cb0a53913f5440d112ab1b7dd00a4`, which declares 10.0.0-beta.2.

Validation used one Omarchy/Hyprland setup with NVIDIA graphics, Steam Proton Experimental and a separate editor prefix. It covered:

- Real geometry in both the quick mesh panel and document Mesh Preview, including camera rotation and zoom.
- Dialog dismissal and tab switching with automatic preview repaint.
- Archive-command state transitions and clean picker cancellation.
- Regular and bold physical font resolution using substitutes.
- GLB export, import into native Blender, mesh re-import and reopening.
- Archive packing, byte-exact extraction and ZIP checks in a disposable project.

The focused command, picker and renderer tests ran under Proton. The complete test project compiled, but the full repository suite was not executed. This is local validation, not a cross-distro certification or a long-session stability guarantee.

## Setup and builds

This repository contains the fork's source changes. A fork-specific binary release or one-command installer has not been published yet. An upstream portable download will not automatically include these changes.

Use a matching Windows x64 build and an editor-only Steam/Proton prefix. Keep that prefix separate from Cyberpunk's prefix, and back up projects and the editor configuration before changing either.

The compatibility flag can be supplied in the WolvenKit Steam shortcut's launch options:

```text
WOLVENKIT_PROTON_COMPAT=1 %command%
```

It can also be stored in that editor prefix's `HKCU\Environment`. Use one explicit configuration method and verify that the Windows application receives it.

Enabling the flag alone is not the whole setup. The tested shader-compiler override, available fonts, graphics stack and runtime dependencies also matter. Do not install dependencies into the game's prefix to repair the editor.

The repository retains the upstream build structure and SDK pin. Linux cross-building is possible, but the validation build required a private SDK override, a compiler-property override and resource filename-case workarounds. Until those steps are packaged and tested here, use the original build instructions in [README.upstream.md](README.upstream.md) rather than treating this as a ready-made native Arch build.

Check third-party dependency licenses before building or redistributing binaries. The fork-specific changes do not add Microsoft shader-compiler DLLs, game DLLs, game assets, prefix backups or private audit logs.

## Known limitations

- Script and template update requests timed out in the tested environment.
- Native viewport right-click menus did not open in the tested swapchain path.
- Fully textured materials, every entity/sector workflow, floating/docking and image export are not certified.
- One earlier laboratory process exit remains unexplained. Later controlled runs closed normally, but they do not establish the cause of that exit.
- Other GPU stacks, desktop sessions, Proton versions and Arch/Linux distributions remain untested.

## Upstream updates

`origin` is this fork. `upstream` is the original WolvenKit repository. I fetch and review upstream changes on a separate branch, run the relevant tests, and merge only the changes I intend to maintain. There is no automatic upstream sync or merge policy.

Inherited GitHub Actions remain disabled initially while their build, release and synchronization behavior is reviewed. Existing application update links and services still originate upstream, so do not use an upstream app replacement as a fork upgrade. It can remove the local compatibility changes.

## License

[GNU General Public License v3.0](LICENSE), retained from WolvenKit. Existing copyright notices, upstream history and third-party license terms remain in place. The original project and contributor credits are preserved in [README.upstream.md](README.upstream.md).
