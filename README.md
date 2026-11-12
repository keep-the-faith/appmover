# AppMover

Move an installed Windows application to another PC **without its original installer**: AppMover finds everything that
belongs to the application (files, registry, services, scheduled tasks, shortcuts, file associations, user data…),
packs it into a `.appmove` archive, and deploys it on another machine — with a full preview, automatic rollback on
failure and a clean uninstallation.

> **Status: validated on Windows 10 (22H2).** Applications of every supported installer type (MSI, WiX Burn,
> Inno Setup, NSIS, InstallShield, Squirrel) were exported and imported on real machines; the automated tests run on
> a simulated Windows (fake registry and file system).
> Drivers are not transferred (see *Known limitations*). Keep a backup or a restore point before deploying.

> **Licenses.** Moving software to another computer may be forbidden by its license agreement (EULA), and some
> applications are bound to the machine they were activated on. You are responsible for respecting the licenses of the
> applications you transfer.

## Requirements

- Windows 10 or 11, x64, on both machines.
- Administrator rights (both executables request elevation).
- Nothing to install: `appmover.exe` (command line) and `appmover-gui.exe` (graphical interface) are self-contained.

## Graphical interface

`appmover-gui.exe` has three tabs:

- **Export** — pick an application, review what was found, grouped by confidence (Certain / Probable / Possible, each
  with the reason and warnings), optionally **Record usage…** (see *Dynamic analysis*), then save the package
  (optionally encrypted with a password).
- **Import** — open a package (its integrity is checked), choose the install directory and which user of this PC
  receives the data of each source user, look at the **preview** of every operation and of the points of attention,
  then deploy.
- **Deployments** — applications installed with AppMover on this PC, and their uninstallation.

## Command line

```
appmover list [--all-users] [--hidden] [--json]
appmover inspect <app> [--json]
appmover collect <app> [-o selection.json] [--trace trace.json] [--json]
appmover trace <app> [--launch] [-o trace.json]
appmover export <app> [-o package.appmove] [--items selection.json] [--include-possible]
                      [--encrypt] [--no-vss] [--trace trace.json] [--force]
appmover verify <package>
appmover deploy <package> [--install-dir <dir>] [--user-map 0=<user|SID>,…] [--dry-run] [--force] [--yes]
appmover deployments
appmover uninstall <deployment id|app> [--yes]
```

`<app>` is an id, an Uninstall key name or part of the application name. `appmover help` describes every option.

Typical transfer:

```
# Source PC
appmover collect "Notepad++" -o npp.json      # review; edit "Selected" in npp.json if needed
appmover export "Notepad++" --items npp.json -o npp.appmove

# Target PC
appmover deploy npp.appmove --dry-run         # shows everything that would be done
appmover deploy npp.appmove
appmover uninstall "Notepad++"                # or from "Apps & features"
```

Environment variables: `APPMOVER_PASSWORD` (password of encrypted packages, for automation — passwords are never
accepted on the command line), `APPMOVER_7ZIP` (another 7-Zip built with the NSIS decompiler, see below).

Logs: `%LOCALAPPDATA%\AppMover\logs`.

## How it works

### 1. Inventory and installer detection

Applications are read from the Uninstall keys (machine 64/32-bit, every user profile — hives of users who are not
logged on are mounted). The installer technology is detected with a score and its evidence: MSI, WiX Burn,
Inno Setup, NSIS, InstallShield, Squirrel, ClickOnce (not transferable).

### 2. Collection

| Source | What it finds |
|---|---|
| Uninstall entry, install folders | install directories (also derived from MSI components), the Uninstall key |
| Windows Installer | installed components (registry), the `Registry` table of the cached `.msi`; for a WiX Burn bundle, those of every MSI package it chains |
| Inno Setup | the uninstall log `unins000.dat` |
| NSIS | the script of the uninstaller, decompiled by the embedded 7-Zip |
| InstallShield, Wise, custom installers | paths listed in their logs (`setup.ilg`, `install.log`…) |
| System | shortcuts, services and drivers, scheduled tasks, Run keys, App Paths, environment variables (PATH entries only), firewall rules, event log sources, COM classes, ProgIDs, URL protocols and file associations |
| Heuristic | data and settings named after the application or its publisher (AppData, ProgramData, Documents, `Software\Vendor\Product`, UAC VirtualStore) |
| Dynamic analysis (optional) | what the application actually reads and writes while you use it |

Every item has a confidence: **Certain** and **Probable** items are selected by default, **Possible** ones (guesses,
shared or system files) are not. Prerequisites (Visual C++, .NET, WebView2, DirectX) are detected from the
executables and checked on the target.

### 3. Package (`.appmove`)

A ZIP archive with the files, `registry.json` and `manifest.json` (SHA-256 of every file, users, shortcuts, tasks,
services, prerequisites, issues). Paths are **tokenized** — `{InstallDir}`, `{ProgramFiles}`, `{U0.AppData}`… — in file
names, registry values, task definitions and configuration files (`C:\…`, JSON-escaped `C:\\…` and `C:/…` forms), so the
application can be deployed in another folder, for another user, or on a PC where Program Files is elsewhere.

- Files locked by the running application are read from a **Volume Shadow Copy** (`--no-vss` to skip them).
- `--encrypt`: AES-256 + HMAC-SHA256 of the whole archive, key derived from the password (PBKDF2, 600,000 iterations).

### 4. Deployment

The plan is computed first (`--dry-run` shows it): folders, files, shortcuts (recreated when their target moved),
services (through the Service Control Manager), registry, PATH entries, scheduled tasks (accounts rewritten for the
target user), and an Uninstall entry pointing to AppMover. Checks: application already installed, disk space,
missing prerequisites, driver and firewall warnings.

- **Newer files are kept**: an executable or DLL already on the target with a higher file version is not replaced.
- Every change is **journaled before it is made** (with a backup of replaced files and previous registry values) in
  `%ProgramData%\AppMover\Deployments\<id>`: a failure rolls everything back, and the uninstallation replays the journal
  backwards. AppMover copies itself to `%ProgramFiles%\AppMover` so that the Uninstall entry keeps working.

### Dynamic analysis

`appmover trace <app> [--launch]` (or **Record usage…** in the interface) follows the application's processes with a
kernel ETW session while you use it, then `collect`/`export --trace` adds the data folders and settings keys it used.

## Known limitations

- Firewall rules are written to the registry and only apply after the firewall service restarts.
- Prerequisites are detected but not installed; drivers are not deployed; service account passwords must be set again.
- A target file in use makes the deployment fail (and roll back).
- Child MSI packages of WiX Burn bundles are not followed; Inno Setup `.ini` changes are not collected; NSIS paths
  computed in variables are ignored.
- Microsoft Store (MSIX) applications are not supported.

## Building

```
./build.sh
```

Builds, runs the tests and publishes `dist/appmover.exe`, `dist/appmover-gui.exe`, the license and the third-party notices. It runs on
Linux or Windows; the .NET 8 SDK and the NuGet packages are installed in `.build/` when missing.

`third_party/7-Zip/7zz.exe` is produced by `tools/build-7zip.sh` (see below); the executables embed it.

The application icon `src/AppMover.Gui/Assets/appmover.ico` is generated by `tools/build-icon.sh` from
`appmover.svg` (32 px and larger) and `appmover-small.svg` (16 to 24 px, drawn on a 16-pixel grid); run it after
changing either drawing. It needs Node.js.

### Releases

The GitHub Actions workflow (`.github/workflows/build.yml`) builds and tests every push and pull request, and uploads
the executables as a workflow artifact. Pushing a tag `v<version>` also publishes the GitHub Release:

```
git tag v1.0.0
git push origin v1.0.0
```

The tag must match `<Version>` in `Directory.Build.props`, and `CHANGELOG.md` must have a section for that version: it
becomes the release notes. The release holds `appmover.exe`, `appmover-gui.exe` and a zip with both, the license and the
third-party notices, attached a few minutes after the tag is pushed (when the workflow ends; a release drafted on the
GitHub page with a new tag gets them too). A version with a suffix (`1.1.0-beta.1`) is published as a pre-release. Releases are not signed.

### Signing

The executables and the embedded 7-Zip are signed (Authenticode, SHA-256, timestamped) when `SIGN_PFX` is set:

```
tools/make-test-cert.sh   # once: self-signed test certificate in .build/signing
SIGN_PFX=.build/signing/appmover-test.pfx SIGN_PASSWORD_FILE=.build/signing/password.txt ./build.sh
```

7-Zip is signed as a copy in `.build/signed/7zz.exe`, embedded instead of `third_party/7-Zip/7zz.exe` (MSBuild property
`SevenZipExe`): the committed binary stays the unsigned reproducible build whose SHA-256 is listed in its `README.txt`.

`tools/sign.sh` uses `osslsigncode` (fetched from the Debian archive into `.build/` when it is not installed);
`SIGN_TIMESTAMP_URL` changes the timestamp server, and an empty value skips the timestamp. On Windows, `signtool`
does the same:

```
signtool sign /fd SHA256 /f appmover.pfx /p <password> /tr http://timestamp.digicert.com /td SHA256 dist\appmover.exe dist\appmover-gui.exe
```

A self-signed certificate is trusted only on the PCs where it is installed. On each test PC, in an administrator
PowerShell:

```
Import-Certificate -FilePath appmover-test.cer -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath appmover-test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
```

The UAC prompt then shows "Keep The Faith (test)" as the verified publisher. Remove the certificate from both stores
(`certlm.msc`) after the tests. For a public release, sign with a certificate from a certification authority.

### Projects

| Project | Role |
|---|---|
| `AppMover.Core` | models, registry and file system abstractions, path tokens, parsers (shell links, PE, Inno log, NSIS script, MSI) |
| `AppMover.Inventory` | installed applications |
| `AppMover.Detection` | installer detection |
| `AppMover.Collectors` | collection session and collectors |
| `AppMover.Packaging` | package writer, manifest, encryption |
| `AppMover.Deployment` | package reader, deployment planner and executor, Windows services, tasks and shortcuts |
| `AppMover.Host` | Windows composition shared by both interfaces (VSS, ETW, embedded 7-Zip) |
| `AppMover.Cli`, `AppMover.Gui` | command line and WPF interface |
| `tests/AppMover.Tests` | xUnit tests on a simulated Windows |

## License

AppMover is released under the [MIT License](LICENSE) — Copyright (c) 2026 Keep The Faith.

## Third-party software

- **7-Zip** 26.05 by Igor Pavlov (GNU LGPL), built from the official source with the NSIS script decompiler enabled
  (`NSIS_SCRIPT`, disabled in official builds) and without RAR code — see `third_party/7-Zip/README.txt` and
  `tools/build-7zip.sh`, which reproduce the binary.
- NuGet packages: CommunityToolkit.Mvvm (MIT), Microsoft.Diagnostics.Tracing.TraceEvent (MIT), Microsoft.Extensions.Logging
  (MIT), Serilog (Apache-2.0), System.Management (MIT), xUnit (Apache-2.0).
