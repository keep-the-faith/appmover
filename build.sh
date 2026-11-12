#!/usr/bin/env bash
# Builds, tests and publishes appmover.exe (CLI) and appmover-gui.exe (WPF), Windows x64 self-contained single files, into dist/.
# The .NET 8 SDK is installed locally in .build/dotnet when missing; NuGet packages go to .build/nuget (nuget.config).
# third_party/7-Zip/7zz.exe (embedded) is produced by tools/build-7zip.sh.
set -euo pipefail
cd "$(dirname "$0")"

DOTNET_DIR="$PWD/.build/dotnet"
if [ ! -x "$DOTNET_DIR/dotnet" ]; then
    mkdir -p .build
    curl -sSL https://dot.net/v1/dotnet-install.sh -o .build/dotnet-install.sh
    bash .build/dotnet-install.sh --channel 8.0 --install-dir "$DOTNET_DIR"
fi

export DOTNET_ROOT="$DOTNET_DIR" PATH="$DOTNET_DIR:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# Not configurable in nuget.config.
export NUGET_HTTP_CACHE_PATH="$PWD/.build/nuget-http-cache"

# Authenticode signature, when a certificate is given (tools/make-test-cert.sh creates a test one): 7zz.exe is signed
# before being embedded (a copy: third_party keeps the reproducible build), the executables after publication.
SEVENZIP=()
if [ -n "${SIGN_PFX:-}" ]; then
    mkdir -p .build/signed
    cp third_party/7-Zip/7zz.exe .build/signed/7zz.exe
    tools/sign.sh .build/signed/7zz.exe
    SEVENZIP=("-p:SevenZipExe=$PWD/.build/signed/7zz.exe")
fi

dotnet build -c Release "${SEVENZIP[@]}"
dotnet test -c Release --no-build
dotnet publish src/AppMover.Cli -c Release -o dist "${SEVENZIP[@]}"
dotnet publish src/AppMover.Gui -c Release -o dist "${SEVENZIP[@]}"
# License of AppMover, and notices of the embedded third-party binaries (7-Zip, GNU LGPL).
cp LICENSE dist/LICENSE.txt
mkdir -p dist/third-party/7-Zip
cp third_party/7-Zip/License.txt third_party/7-Zip/README.txt dist/third-party/7-Zip/

if [ -n "${SIGN_PFX:-}" ]; then
    tools/sign.sh dist/appmover.exe dist/appmover-gui.exe
fi

ls -lh dist/appmover.exe dist/appmover-gui.exe
