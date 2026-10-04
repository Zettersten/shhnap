#!/usr/bin/env bash
set -euo pipefail

repository="$(cd "$(dirname "$0")/../../.." && pwd)"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
mkdir -p "$scratch/assets"
for number in {01..16}; do
  printf 'asset %s\n' "$number" > "$scratch/assets/asset-$number"
done

evidence="$repository/packaging/release/release-evidence.sh"
tag='v1.2.3'
sha='0123456789abcdef0123456789abcdef01234567'
bash "$evidence" write "$scratch/assets" "$tag" "$sha" 123 "$scratch/evidence" >/dev/null
bash "$evidence" verify "$scratch/assets" "$tag" "$sha" 123 "$scratch/evidence" >/dev/null

if bash "$evidence" verify "$scratch/assets" "$tag" "$sha" 124 "$scratch/evidence" >/dev/null 2>&1; then
  echo 'Evidence accepted a different workflow run.' >&2
  exit 1
fi
printf 'changed\n' >> "$scratch/assets/asset-01"
if bash "$evidence" verify "$scratch/assets" "$tag" "$sha" 123 "$scratch/evidence" >/dev/null 2>&1; then
  echo 'Evidence accepted modified release bytes.' >&2
  exit 1
fi
echo 'Release evidence rejects a different run and modified bytes.'
