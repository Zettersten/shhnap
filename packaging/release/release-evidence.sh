#!/usr/bin/env bash
set -euo pipefail

mode="${1:?Pass write or verify.}"
assets_dir="${2:?Pass the directory containing release assets.}"
release_tag="${3:?Pass the stable release tag.}"
head_sha="${4:?Pass the tag commit SHA.}"
run_id="${5:?Pass the successful tag run ID.}"
evidence_dir="${6:?Pass the evidence directory.}"

[[ "$mode" == write || "$mode" == verify ]]
[[ "$release_tag" =~ ^v[1-9][0-9]*\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]
[[ "$head_sha" =~ ^[0-9a-f]{40}$ ]]
[[ "$run_id" =~ ^[1-9][0-9]*$ ]]
test -d "$assets_dir"

if [[ "$mode" == write ]]; then
  mkdir -p "$evidence_dir"
  jq -n --arg tag "$release_tag" --arg sha "$head_sha" --argjson run "$run_id" \
    '{schemaVersion: 1, tag: $tag, headSha: $sha, runId: $run}' > "$evidence_dir/source.json"
  (
    cd "$assets_dir"
    sha256sum -- * | LC_ALL=C sort -k2
  ) > "$evidence_dir/assets.sha256"
fi

test -s "$evidence_dir/source.json"
test -s "$evidence_dir/assets.sha256"
evidence_hashes="$(realpath "$evidence_dir/assets.sha256")"
jq -e --arg tag "$release_tag" --arg sha "$head_sha" --argjson run "$run_id" '
  (keys | sort) == ["headSha", "runId", "schemaVersion", "tag"] and
  .schemaVersion == 1 and .tag == $tag and .headSha == $sha and .runId == $run
' "$evidence_dir/source.json" >/dev/null

actual_names="$(find "$assets_dir" -mindepth 1 -maxdepth 1 -type f -printf '%f\n' | LC_ALL=C sort)"
recorded_names="$(awk '{name = $2; sub(/^\*/, "", name); print name}' \
  "$evidence_dir/assets.sha256" | LC_ALL=C sort)"
if [[ "$actual_names" != "$recorded_names" ||
      "$(wc -l < "$evidence_dir/assets.sha256")" -ne 16 ]]; then
  echo 'Release assets do not match the successful tag build evidence.' >&2
  exit 1
fi
(
  cd "$assets_dir"
  sha256sum --check --strict "$evidence_hashes"
)
