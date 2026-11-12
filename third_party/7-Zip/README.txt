7-Zip 26.05, by Igor Pavlov — https://www.7-zip.org
Built by tools/build-7zip.sh from the official source 7z2605-src.tar.xz
(https://github.com/ip7z/7zip/releases/tag/26.05), with llvm-mingw 20261020, Windows x64.

Changes to the official source (GNU LGPL, see License.txt):
  - CPP/7zip/Archive/Nsis/NsisIn.h: "#define NSIS_SCRIPT" enabled, so that the script of NSIS
    installers/uninstallers is decompiled to "[NSIS].nsi" (disabled in official builds).
  - Built with DISABLE_RAR=1: no RAR code, hence no unRAR license restriction.

Used by AppMover to read what NSIS uninstallers remove. The source and this script reproduce the binary.

SHA-256:
0b6080b8c3e7a579161d2d495224c0e51c467576cb4c7048b0ab485ef44cc22e  7z2605-src.tar.xz
b1e8d1a0154773be1c3c7a30383141a99f9c4083de51579116b3960f30833905  7zz.exe
