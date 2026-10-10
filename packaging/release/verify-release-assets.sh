#!/usr/bin/env bash
set -euo pipefail

assets_dir="${1:?Pass the directory containing release assets.}"
expected_tag="${2:?Pass the stable release tag.}"
test -d "$assets_dir"
[[ "$expected_tag" =~ ^v[1-9][0-9]*\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]

version="${expected_tag#v}"
legacy_setup=false
if [[ "$version" != '1.0.12' &&
      "$(printf '%s\n' "$version" '1.0.12' | sort -V | head -n 1)" == "$version" ]]; then
  legacy_setup=true
fi
expected="$({
  for arch in x64 arm64; do
    rid="win-$arch"
    pack_id="ErikZettersten.Shnapp.$arch"
    setup="Shnapp-$expected_tag-$arch.exe"
    if $legacy_setup; then setup="$pack_id-$rid-Setup.exe"; fi
    printf '%s\n' \
      "Shnapp-$rid.zip" "Shnapp-$rid.zip.sha256" \
      "assets.$rid.json" "$pack_id-$version-$rid-full.nupkg" \
      "$setup" "$setup.sha256" \
      "RELEASES-$rid" "releases.$rid.json"
  done
} | LC_ALL=C sort)"
actual="$(find "$assets_dir" -mindepth 1 -maxdepth 1 -type f -printf '%f\n' | LC_ALL=C sort)"
if [[ "$actual" != "$expected" ]]; then
  echo 'Release must contain both portable ZIPs, Velopack installers and feeds, and checksums.' >&2
  diff -u <(printf '%s\n' "$expected") <(printf '%s\n' "$actual") >&2 || true
  exit 1
fi

(
  cd "$assets_dir"
  for rid in win-x64 win-arm64; do
    arch="${rid#win-}"
    pack_id="ErikZettersten.Shnapp.$arch"
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
    grep -Fx 'current-version.txt' <<<"$listing" >/dev/null || {
      echo "$archive does not contain the portable launcher layout." >&2
      exit 1
    }
    archive_version="$(unzip -p "$archive" current-version.txt | sed -n '1p' | tr -d '\r')"
    if [[ ! "$archive_version" =~ ^v[1-9][0-9]*\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
      echo "Invalid current version in $archive." >&2
      exit 1
    fi
    if [[ -n "$expected_tag" && "$archive_version" != "$expected_tag" ]]; then
      echo "$archive points at $archive_version instead of $expected_tag." >&2
      exit 1
    fi
    if ! cmp -s <(printf '%s\n\n' "$archive_version") <(unzip -p "$archive" current-version.txt); then
      echo "$archive has an invalid current-version.txt." >&2
      exit 1
    fi
    grep -Fx "versions/$archive_version/Shnapp.exe" <<<"$listing" >/dev/null
    grep -Fx "versions/$archive_version/Shnapp.pri" <<<"$listing" >/dev/null
    grep -Fx "versions/$archive_version/LICENSE" <<<"$listing" >/dev/null
    grep -Fx "versions/$archive_version/THIRD_PARTY_NOTICES.txt" <<<"$listing" >/dev/null
    grep -Eq "^versions/$archive_version/THIRD_PARTY_NOTICES/.+" <<<"$listing" || {
      echo "$archive is missing third-party license files." >&2
      exit 1
    }
    while IFS= read -r entry; do
      if [[ "$entry" == /* || "$entry" == *\\* || "$entry" == *:* ||
            "$entry" == *../* || "$entry" == */.. || "$entry" == *./* ]]; then
        echo "Unsafe ZIP entry in $archive: $entry" >&2
        exit 1
      fi
      case "$entry" in
        Shnapp.exe|current-version.txt|LICENSE|THIRD_PARTY_NOTICES.txt|versions/|"versions/$archive_version/"|"versions/$archive_version/"*) ;;
        *) echo "Unexpected ZIP entry in $archive: $entry" >&2; exit 1 ;;
      esac
      if [[ "$entry" == "versions/$archive_version/"* ]]; then
        relative="${entry#versions/$archive_version/}"
        case "$relative" in
          ''|Shnapp.exe|Shnapp.pri|LICENSE|THIRD_PARTY_NOTICES.txt|THIRD_PARTY_NOTICES/*) ;;
          *) echo "Unexpected file in single-file payload: $entry" >&2; exit 1 ;;
        esac
      fi
    done <<<"$listing"

    setup="Shnapp-$expected_tag-$arch.exe"
    if $legacy_setup; then setup="$pack_id-$rid-Setup.exe"; fi
    package="$pack_id-$version-$rid-full.nupkg"
    test -s "$setup"
    test -s "$package"
    sha256sum --check --strict "$setup.sha256"
    [[ "$(head -c 2 "$setup")" == 'MZ' ]] || {
      echo "$setup is not a Windows executable." >&2
      exit 1
    }
    unzip -tqq "$package"
    package_listing="$(unzip -Z1 "$package")"
    for required in Shnapp.exe Shnapp.pri LICENSE THIRD_PARTY_NOTICES.txt; do
      grep -Fx "lib/app/$required" <<<"$package_listing" >/dev/null || {
        echo "$package is missing $required." >&2
        exit 1
      }
    done
    grep -Eq '^lib/app/THIRD_PARTY_NOTICES/.+' <<<"$package_listing" || {
      echo "$package is missing third-party license files." >&2
      exit 1
    }

    jq -e --arg id "$pack_id" --arg version "$version" --arg package "$package" '
      (.Assets | length) == 1 and
      .Assets[0].PackageId == $id and .Assets[0].Version == $version and
      .Assets[0].Type == "Full" and .Assets[0].FileName == $package and
      (.Assets[0].SHA1 | test("^[0-9A-F]{40}$")) and
      (.Assets[0].SHA256 | test("^[0-9A-F]{64}$")) and
      (.Assets[0].Size > 0)
    ' "releases.$rid.json" >/dev/null || {
      echo "Invalid Velopack update feed for $rid." >&2
      exit 1
    }
    feed_sha256="$(jq -r '.Assets[0].SHA256 | ascii_downcase' "releases.$rid.json")"
    feed_sha1="$(jq -r '.Assets[0].SHA1' "releases.$rid.json")"
    feed_size="$(jq -r '.Assets[0].Size' "releases.$rid.json")"
    [[ "$(sha256sum "$package" | cut -d' ' -f1)" == "$feed_sha256" &&
       "$(sha1sum "$package" | cut -d' ' -f1 | tr '[:lower:]' '[:upper:]')" == "$feed_sha1" &&
       "$(stat -c %s "$package")" == "$feed_size" ]] || {
      echo "Velopack update feed hashes or size do not match $package." >&2
      exit 1
    }
    jq -e --arg setup "$setup" --arg package "$package" '
      length == 2 and
      any(.[]; .RelativeFileName == $setup and .Type == "Installer") and
      any(.[]; .RelativeFileName == $package and .Type == "Full")
    ' "assets.$rid.json" >/dev/null || {
      echo "Invalid Velopack asset list for $rid." >&2
      exit 1
    }
    [[ "$(sed '1s/^\xEF\xBB\xBF//' "RELEASES-$rid" | tr -d '\r\n')" == "$feed_sha1 $package $feed_size" ]] || {
      echo "Invalid Velopack legacy feed for $rid." >&2
      exit 1
    }
  done
)
