#!/usr/bin/env bash
# Builds the 7-Zip command line (7zz, standalone with every format) from the official source, with the NSIS script
# decompiler enabled (NSIS_SCRIPT, disabled in official builds) and without RAR code (DISABLE_RAR=1: pure LGPL).
#   - third_party/7-Zip/7zz.exe : Windows x64, embedded in AppMover to read the script of NSIS uninstallers
#   - .build/7zip-build/linux/7zz : build machine version, to check the decompiled scripts during development
# The Windows build uses llvm-mingw, installed in .build (no root needed).
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"

VERSION="${SEVENZIP_VERSION:-26.05}"
TAG="${VERSION//./}"
MINGW_VERSION="${LLVM_MINGW_VERSION:-20261020}"
WORK="$ROOT/.build/7zip-build"
TARGET="$ROOT/third_party/7-Zip"
JOBS="$(nproc)"

case "$(uname -m)" in
  x86_64) HOST=x86_64 ;;
  aarch64|arm64) HOST=aarch64 ;;
  *) echo "Unsupported build machine: $(uname -m)" >&2; exit 1 ;;
esac

# --- llvm-mingw (cross compiler for Windows). Its per-target "include" folders are symlinks in the archive:
#     they are replaced by a copy of the generic headers (symlinks may not be allowed where .build lives).
MINGW="$ROOT/.build/llvm-mingw"
if [ ! -x "$MINGW/bin/x86_64-w64-mingw32-clang++" ]; then
  archive="llvm-mingw-$MINGW_VERSION-ucrt-ubuntu-22.04-$HOST.tar.xz"
  curl -sSfL "https://github.com/mstorsjo/llvm-mingw/releases/download/$MINGW_VERSION/$archive" -o "$ROOT/.build/$archive"
  rm -rf "$MINGW" && mkdir -p "$MINGW"
  tar -xJf "$ROOT/.build/$archive" -C "$MINGW" --strip-components=1 \
    --exclude='*/aarch64-w64-mingw32/include' --exclude='*/armv7-w64-mingw32/include' \
    --exclude='*/i686-w64-mingw32/include' --exclude='*/x86_64-w64-mingw32/include'
  cp -r "$MINGW/generic-w64-mingw32/include" "$MINGW/x86_64-w64-mingw32/include"
fi

# --- 7-Zip source, NSIS_SCRIPT enabled
SRC_ARCHIVE="7z$TAG-src.tar.xz"
rm -rf "$WORK" && mkdir -p "$WORK/src"
curl -sSfL "https://github.com/ip7z/7zip/releases/download/$VERSION/$SRC_ARCHIVE" -o "$WORK/$SRC_ARCHIVE"
tar -xJf "$WORK/$SRC_ARCHIVE" -C "$WORK/src"
NSIS_H="$WORK/src/CPP/7zip/Archive/Nsis/NsisIn.h"
grep -q '^// #define NSIS_SCRIPT' "$NSIS_H"
sed -i 's|^// #define NSIS_SCRIPT|#define NSIS_SCRIPT|' "$NSIS_H"

BUNDLE="$WORK/src/CPP/7zip/Bundles/Alone2"
# Linux (build machine)
make -C "$BUNDLE" -j"$JOBS" -f makefile.gcc DISABLE_RAR=1 O="$WORK/linux" >"$WORK/linux.log" 2>&1 \
  || { tail -30 "$WORK/linux.log"; exit 1; }
# Windows x64
PATH="$MINGW/bin:$PATH" make -C "$BUNDLE" -j"$JOBS" -f makefile.gcc DISABLE_RAR=1 IS_MINGW=1 \
  CC=x86_64-w64-mingw32-clang CXX=x86_64-w64-mingw32-clang++ RC=x86_64-w64-mingw32-windres \
  O="$WORK/windows" >"$WORK/windows.log" 2>&1 \
  || { tail -30 "$WORK/windows.log"; exit 1; }

rm -rf "$TARGET" && mkdir -p "$TARGET"
cp "$WORK/windows/7zz.exe" "$TARGET/7zz.exe"
cp "$WORK/src/DOC/License.txt" "$TARGET/License.txt"
cat > "$TARGET/README.txt" <<README
7-Zip $VERSION, by Igor Pavlov — https://www.7-zip.org
Built by tools/build-7zip.sh from the official source $SRC_ARCHIVE
(https://github.com/ip7z/7zip/releases/tag/$VERSION), with llvm-mingw $MINGW_VERSION, Windows x64.

Changes to the official source (GNU LGPL, see License.txt):
  - CPP/7zip/Archive/Nsis/NsisIn.h: "#define NSIS_SCRIPT" enabled, so that the script of NSIS
    installers/uninstallers is decompiled to "[NSIS].nsi" (disabled in official builds).
  - Built with DISABLE_RAR=1: no RAR code, hence no unRAR license restriction.

Used by AppMover to read what NSIS uninstallers remove. The source and this script reproduce the binary.

SHA-256:
$(cd "$WORK" && sha256sum "$SRC_ARCHIVE")
$(cd "$TARGET" && sha256sum 7zz.exe)
README
cat "$TARGET/README.txt"
