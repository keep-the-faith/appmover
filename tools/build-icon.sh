#!/usr/bin/env bash
# Regenerates src/AppMover.Gui/Assets/appmover.ico from appmover.svg (32 px and up) and appmover-small.svg (16-24 px).
# The renderer (@resvg/resvg-js) is installed in .build/icon-tools; PNG previews are written to .build/icon-preview.
set -euo pipefail
cd "$(dirname "$0")/.."

TOOLS="$PWD/.build/icon-tools"
if [ ! -d "$TOOLS/node_modules/@resvg/resvg-js" ]; then
    mkdir -p "$TOOLS"
    npm install --prefix "$TOOLS" --no-audit --no-fund --no-save @resvg/resvg-js@2.6.2
fi

ASSETS=src/AppMover.Gui/Assets
NODE_PATH="$TOOLS/node_modules" node tools/make-icon.cjs \
    "$ASSETS/appmover.svg" "$ASSETS/appmover-small.svg" "$ASSETS/appmover.ico" .build/icon-preview
