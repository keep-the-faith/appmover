#!/usr/bin/env bash
# Signs Windows executables (Authenticode, SHA-256) with osslsigncode.
#   SIGN_PFX            certificate and private key (.pfx)
#   SIGN_PASSWORD_FILE  file holding the .pfx password (or SIGN_PASSWORD)
#   SIGN_TIMESTAMP_URL  RFC 3161 timestamp server (default: DigiCert); empty to skip the timestamp
# On Windows, use signtool instead (see README, "Signing").
# osslsigncode is used from the PATH, or fetched once from the Debian archive into .build/osslsigncode.
set -euo pipefail
cd "$(dirname "$0")/.."

: "${SIGN_PFX:?SIGN_PFX is not set}"
TIMESTAMP_URL="${SIGN_TIMESTAMP_URL-http://timestamp.digicert.com}"

OSSLSIGNCODE=$(command -v osslsigncode || true)
if [ -z "$OSSLSIGNCODE" ]; then
    LOCAL=.build/osslsigncode/root/usr/bin/osslsigncode
    if [ ! -x "$LOCAL" ]; then
        ARCH=$(dpkg --print-architecture)
        DEB="osslsigncode_2.9-2_${ARCH}.deb"
        mkdir -p .build/osslsigncode
        curl -sSfL "https://deb.debian.org/debian/pool/main/o/osslsigncode/$DEB" -o ".build/osslsigncode/$DEB"
        dpkg -x ".build/osslsigncode/$DEB" .build/osslsigncode/root
    fi
    OSSLSIGNCODE=$LOCAL
fi

PASSWORD_ARGS=()
if [ -n "${SIGN_PASSWORD_FILE:-}" ]; then
    PASSWORD_ARGS=(-readpass "$SIGN_PASSWORD_FILE")
elif [ -n "${SIGN_PASSWORD:-}" ]; then
    PASSWORD_ARGS=(-pass "$SIGN_PASSWORD")
fi
TIMESTAMP_ARGS=()
[ -n "$TIMESTAMP_URL" ] && TIMESTAMP_ARGS=(-ts "$TIMESTAMP_URL")

for file in "$@"; do
    "$OSSLSIGNCODE" sign -pkcs12 "$SIGN_PFX" "${PASSWORD_ARGS[@]}" -h sha256 -n "AppMover" \
        "${TIMESTAMP_ARGS[@]}" -in "$file" -out "$file.signed" >/dev/null
    chmod --reference="$file" "$file.signed"
    mv "$file.signed" "$file"
    echo "Signed $file"
done
