#!/usr/bin/env bash
set -euo pipefail

assets_dir="${1:?Pass the directory containing release assets.}"
expected_tag="${2:-}"
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
    duplicates="$(LC_ALL=C sort <<<"$listing" | uniq -d)"
    if [[ -n "$duplicates" ]]; then
      echo "$archive contains duplicate ZIP entries." >&2
      exit 1
    fi
    grep -Fx 'Shnapp.exe' <<<"$listing" >/dev/null
    grep -Fx 'LICENSE' <<<"$listing" >/dev/null
    grep -Fx 'THIRD_PARTY_NOTICES.txt' <<<"$listing" >/dev/null
    if ! grep -Fx 'current-version.txt' <<<"$listing" >/dev/null; then
      if [[ "$expected_tag" != 'v1.0.0' && "$expected_tag" != 'v1.0.2' ]]; then
        echo "$archive does not contain the portable launcher layout." >&2
        exit 1
      fi
      grep -Fx 'Shnapp.pri' <<<"$listing" >/dev/null
      continue
    fi
    version="$(unzip -p "$archive" current-version.txt | sed -n '1p' | tr -d '\r')"
    if [[ ! "$version" =~ ^v[1-9][0-9]*\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
      echo "Invalid current version in $archive." >&2
      exit 1
    fi
    if [[ -n "$expected_tag" && "$version" != "$expected_tag" ]]; then
      echo "$archive points at $version instead of $expected_tag." >&2
      exit 1
    fi
    if ! cmp -s <(printf '%s\n\n' "$version") <(unzip -p "$archive" current-version.txt); then
      echo "$archive has an invalid current-version.txt." >&2
      exit 1
    fi
    grep -Fx "versions/$version/Shnapp.exe" <<<"$listing" >/dev/null
    grep -Fx "versions/$version/Shnapp.pri" <<<"$listing" >/dev/null
    grep -Fx "versions/$version/LICENSE" <<<"$listing" >/dev/null
    grep -Fx "versions/$version/THIRD_PARTY_NOTICES.txt" <<<"$listing" >/dev/null
    while IFS= read -r entry; do
      if [[ "$entry" == /* || "$entry" == *\\* || "$entry" == *:* ||
            "$entry" == *../* || "$entry" == */.. || "$entry" == *./* ]]; then
        echo "Unsafe ZIP entry in $archive: $entry" >&2
        exit 1
      fi
      case "$entry" in
        Shnapp.exe|current-version.txt|LICENSE|THIRD_PARTY_NOTICES.txt|versions/|"versions/$version/"|"versions/$version/"*) ;;
        *) echo "Unexpected ZIP entry in $archive: $entry" >&2; exit 1 ;;
      esac
    done <<<"$listing"
  done
)
