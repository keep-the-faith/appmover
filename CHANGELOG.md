# Changelog

All notable changes to AppMover are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - 2026-11-11

First version. Validated on Windows 10 (22H2): applications of every supported installer type were exported and
imported on real machines; the automated tests run on a simulated Windows.

### Added

#### Inventory and detection
- List of the installed applications from the Uninstall keys: machine (64-bit and 32-bit views) and every user profile;
  hives of users who are not logged on are mounted (`NTUSER.DAT`, and `UsrClass.dat` for their classes).
- Entries hidden by "Apps & features" (updates, system components…) filtered out, shown with `--hidden`.
- Installer detection with a confidence score and its evidence: MSI, WiX Burn, Inno Setup, NSIS, InstallShield,
  Squirrel, ClickOnce (reported as not transferable).

#### Collection
- Install directories from the Uninstall entry, the installer data, the icon and uninstaller folders, and the MSI
  components; too broad folders (drive root, Program Files, Windows, user profile…) are rejected.
- Windows Installer: installed components read from the registry and the `Registry` table of the cached `.msi`.
- WiX Burn bundles (e.g. the Python installer): the MSI packages chained by the bundle are found through Burn's
  dependency registration and collected like MSI applications; the cached setup is kept for uninstallation.
- Inno Setup: uninstall log (`unins000.dat`) reader.
- NSIS: uninstaller script decompiled by an embedded 7-Zip; both shell contexts are tried and only existing paths and
  keys are kept.
- InstallShield, Wise and custom installers: paths listed in their logs (`setup.ilg`, `install.log`…); a listed folder
  is kept only when it, or one of its parents, is named after the application or its publisher, and Start menu and
  desktop paths are left to the shortcut collector.
- System integrations: shortcuts (Start menu, desktop, taskbar, advertised MSI shortcuts), services and drivers,
  scheduled tasks (programs and COM handlers), Run keys, App Paths, environment variables (PATH entries only), firewall
  rules, event log sources, COM classes and type libraries, ProgIDs, URL protocols and file associations.
- Heuristic search of data and settings named after the application or its publisher (AppData, LocalAppData,
  LocalLow, ProgramData, Documents, `Software\Vendor\Product`, UAC VirtualStore).
- Dynamic analysis: kernel ETW trace of the application's processes while it is used (`trace` command,
  **Record usage…** button), adding the data folders and settings keys it actually wrote or read.
- Temporary folders (a user's `%TEMP%`, `Windows\Temp`) never collected.
- Confidence levels (Certain, Probable, Possible) with reasons and warnings; duplicates and nested items merged;
  editable selection file (`collect -o`, `export --items`).
- Prerequisites detected from the executables (imports, CLR header, `runtimeconfig.json`): Visual C++ 2005-2022,
  .NET Framework, .NET, DirectX, WebView2.

#### Package
- `.appmove` archive (ZIP64) with the files, `registry.json` and `manifest.json` (SHA-256, size, date and attributes of
  every file, users, folders, shortcuts, tasks, services, prerequisites, issues).
- Tokenized paths (`{InstallDir}`, `{ProgramFiles}`, `{U0.AppData}`…) in file names, registry values, task
  definitions and configuration files, including JSON-escaped (`C:\\…`) and forward-slash (`C:/…`) forms.
- Configuration files containing paths are flagged and adapted on deployment, keeping their encoding.
- Files locked by the running application read from a Volume Shadow Copy (`--no-vss` to skip them).
- File version of executables and DLLs recorded.
- Optional password encryption (`--encrypt`): AES-256-CBC + HMAC-SHA256, PBKDF2-SHA256 (600,000 iterations); password
  and integrity checked before decryption.
- `verify` command (SHA-256 of every file).

#### Deployment
- Plan computed before any change (`--dry-run`, preview in the interface), with checks: application already
  installed, disk space, missing prerequisites, existing services and tasks, drivers, firewall rules, service accounts.
- Install directory relocated to the target's known folders or chosen (`--install-dir`); source users mapped to target
  users (`--user-map`, same name by default).
- Files, folders, registry (32/64-bit views, target user hives), shortcuts recreated when their target moved,
  services through the Service Control Manager, scheduled tasks with accounts rewritten, PATH entries, UAC VirtualStore
  following the install directory.
- Newer executables and DLLs already on the target are kept (file version comparison); read-only, hidden and system
  files (`desktop.ini`…) can be replaced.
- Services that start with Windows are started at the end of the deployment; a service that cannot start is reported
  as a warning.
- Registry values already identical on the target are not rewritten; a value of an existing key protected on the
  target (owned by Windows or another application) is skipped with a warning.
- Every change journaled before it is made, with backups of replaced files and previous registry values; automatic
  rollback on failure (files and values left unchanged are not reported as failures).
- Uninstall entry pointing to AppMover; `uninstall` command (also from "Apps & features") replays the journal backwards;
  `deployments` command.

#### Interfaces
- Command line `appmover.exe`: `list`, `inspect`, `collect`, `trace`, `export`, `verify`, `deploy`, `deployments`,
  `uninstall`; JSON output; passwords typed without echo or read from `APPMOVER_PASSWORD`.
- Graphical interface `appmover-gui.exe` (WPF): Export, Import and Deployments tabs, review grouped by confidence,
  usage recording, encryption, preview, progress, deployment warnings; uninstallation without the main window when called from
  "Apps & features".
- Application icon (a window whose tile is moved out by an arrow) on both executables and the windows; SVG sources and
  `tools/build-icon.sh` to regenerate the `.ico`.
- Both interfaces in English; logs in `%LOCALAPPDATA%\AppMover\logs`.

#### Project
- MIT License (Copyright (c) 2026 Keep The Faith), also in the executables' properties and in `dist/`.

#### Build
- `build.sh`: build, tests and self-contained single-file publication for Windows x64, from Linux or Windows; .NET SDK
  and NuGet packages installed in `.build/`.
- `tools/build-7zip.sh`: 7-Zip 26.05 built from the official source with the NSIS script decompiler enabled and without
  RAR code (LGPL); embedded in AppMover, its notices shipped in `dist/third-party/7-Zip`.
- GitHub Actions workflow building, testing and publishing on Windows; a `v<version>` tag publishes the GitHub
  Release (executables, zip, notes from this changelog).
- Optional Authenticode signature of the executables and of the embedded 7-Zip (`SIGN_PFX`, `tools/sign.sh` with
  osslsigncode);
  `tools/make-test-cert.sh` creates a self-signed test certificate.

### Known limitations
See the *Known limitations* section of the [README](README.md).
