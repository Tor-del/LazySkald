# Reusable Inno Setup installer

The installer is per-user, targets Windows 11 x64-compatible systems, and includes
the .NET runtime from the self-contained publish folder. It does not request admin
rights. Setup and Uninstall support English and Russian.

## Build a release

1. Change `Version` in `Directory.Build.props`.
2. Run `./build-installer.ps1`. It publishes the application, then invokes ISCC.
3. The EXE is written to `artifacts/installers`.

If needed, `tools/bootstrap-inno.ps1` installs Inno Setup 6.7.4 into the ignored
`tools/innosetup` folder without shortcuts or file associations. It verifies the
official download's pinned SHA-256 and Authenticode publisher before running it.
An existing ISCC can instead be passed with `-CompilerPath`.

```powershell
./build-installer.ps1
./build-installer.ps1 -SkipPublish -SourceDirectory artifacts/LazySkald-win-x64-0.5.0
./build-installer.ps1 -SourceDirectory artifacts/next-release -Version 0.6.0
```

## Upgrade contract

- **Never change `ProductAppId` for another version of the same product.**
  A compact GUID without braces is used so the registry key is deterministic.
- `PrepareToInstall` first requests a graceful shutdown through the named event.
  The application listens to `ProductExitEvent`; `ProductMutex` lets Setup wait
  until it exits. There is no broad process termination by executable name.
- Setup finds the registered previous installation in the current user's 64-bit
  or 32-bit uninstall registry view. The uninstaller must be an existing
  `unins*.exe` inside that installation's registered folder.
- The old uninstaller is run silently and synchronously **before any new product
  files are installed**. Setup verifies its exit code and removal of the uninstall
  registry key. If removal fails, installation stops instead of mixing versions.
- Previous install directory and tasks are recovered by Inno Setup before this
  removal. New files and a new uninstall record are then installed.
- User settings remain in `%LOCALAPPDATA%/ScreenReader`, outside the application
  directory. Neither setup nor uninstall deletes that directory. DPAPI-protected
  API keys remain usable by the same Windows user after an upgrade.
- Portable folders from versions 0.1–0.4 have no uninstall registration and are
  not deleted automatically. Exit an old portable copy from its tray menu before
  the first installation. Installed releases starting with 0.5 use this upgrade
  contract for future versions.

Uninstalling first means the previous binaries are already removed if copying the
new version later fails. User data is preserved, and the package can be rerun.

## Reuse for another application

Copy `AppConfig.iss` and change its product name, executable, publisher, directory,
icon, **AppId**, mutex and event names. Keep those new identifiers stable for that
application's future releases. Pass the config and published source directory:

```powershell
./build-installer.ps1 -SkipPublish `
  -ConfigFile installer/MyOtherApp.iss `
  -SourceDirectory artifacts/MyOtherApp `
  -Version 1.0.0
```

The other app can implement the named shutdown event for automatic graceful exit.
Otherwise an active mutex results in a localized request to close it manually.
Review the Windows minimum version and architecture in `setup.iss` for a different
application. `Upgrade.iss` is shared and requires no product-specific edits.

## Isolated installation tests

```powershell
./tests/installer/Test-Upgrade.ps1
```

The test builds three small fixture packages with a dedicated AppId, installs and
upgrades them twice under `artifacts/installer-qa/installed`, verifies removal of
old owned files and preservation of external settings, then uninstalls the QA
product. It does not install, uninstall, launch or stop the user's LazySkald.

The compiler and upgrade APIs follow the
[Inno Setup event documentation](https://jrsoftware.org/ishelp/topic_scriptevents.htm)
and [AppId documentation](https://jrsoftware.org/ishelp/topic_setup_appid.htm).
