#!/usr/bin/env bash
set -euo pipefail

assets_dir="${1:?Pass the directory containing release assets.}"
test -d "$assets_dir"

expected=$'Shnapp-win-arm64.zip\nShnapp-win-arm64.zip.sha256\nShnapp-win-x64.zip\nShnapp-win-x64.zip.sha256'
actual="$(find "$assets_dir" -mindepth 1 -maxdepth 1 -type f -printf '%f\n' | LC_ALL=C sort)"
if [[ "$actual" != "$expected" ]]; then
  echo 'Release must contain exactly the two portable ZIPs and their SHA-256 files.' >&2
  diff -u <(printf '%s\n' "$expected") <(printf '%s\n' "$actual") >&2 || true
  exit 1
fi

(
  cd "$assets_dir"
  for rid in win-x64 win-arm64; do
    archive="Shnapp-$rid.zip"
    test -s "$archive"
    sha256sum --check --strict "$archive.sha256"
    unzip -tqq "$archive"
    listing="$(unzip -Z1 "$archive")"
    grep -Fx 'Shnapp.exe' <<<"$listing" >/dev/null
    grep -Fx 'Shnapp.pri' <<<"$listing" >/dev/null
    grep -Fx 'LICENSE' <<<"$listing" >/dev/null
    grep -Fx 'THIRD_PARTY_NOTICES.txt' <<<"$listing" >/dev/null
  done
)
