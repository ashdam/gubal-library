#!/usr/bin/env bash
set -euo pipefail

fail() { printf '%s\n' "$*" >&2; return 1; }

valid_version() {
  [[ $1 =~ ^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$ ]] || return 1
  local part
  local -a parts
  IFS=. read -r -a parts <<< "$1"
  for part in "${parts[@]}"; do (( part <= 65534 )) || return 1; done
}

version_gt() {
  valid_version "$1" && valid_version "$2" || return 1
  local index
  local -a left right
  IFS=. read -r -a left <<< "$1"
  IFS=. read -r -a right <<< "$2"
  for index in 0 1 2 3; do
    (( left[index] > right[index] )) && return 0
    (( left[index] < right[index] )) && return 1
  done
  return 1
}

project_version() {
  local xml=$1
  local -a values
  mapfile -t values < <(grep -oE '<Version>[^<]+</Version>' <<< "$xml" | sed -E 's@</?Version>@@g;s/^[[:space:]]+|[[:space:]]+$//g')
  [[ ${#values[@]} == 1 ]] && valid_version "${values[0]}" || { fail 'The project must define one four-part Version.'; return 1; }
  printf '%s\n' "${values[0]}"
}

revision_version() {
  local path xml
  for path in Directory.Build.props GubalLibrary.csproj; do
    if xml=$(git show "$1:$path" 2>/dev/null) && [[ $xml == *'<Version>'* ]]; then
      project_version "$xml"
      return
    fi
  done
  fail 'The revision does not define a release version.'
}

validate_release() {
  local candidate=$1 previous=$2 advertised=$3 releases=$4 tags=$5 published
  jq -e 'type == "array" and all(.[]; (.tag_name | type == "string") and (.draft | type == "boolean"))' <<< "$releases" >/dev/null || return 1
  jq -e 'type == "array" and all(.[]; type == "string")' <<< "$tags" >/dev/null || return 1
  version_gt "$candidate" "$previous" || { fail 'The new version must exceed the previous project version.'; return 1; }
  version_gt "$candidate" "$advertised" || { fail 'The new version must exceed the version in repo.json.'; return 1; }
  while IFS= read -r published; do
    published=${published#v}
    [[ $published =~ ^[0-9]+(\.[0-9]+){1,3}$ ]] || { fail "Cannot compare published tag $published."; return 1; }
    while [[ $published != *.*.*.* ]]; do published+=.0; done
    version_gt "$candidate" "$published" || { fail "The new version must exceed published release $published."; return 1; }
  done < <(jq -r '.[] | select(.draft == false) | .tag_name' <<< "$releases")
  if jq -e --arg version "$candidate" 'any(.[]; . == $version or . == ("v" + $version))' <<< "$tags" >/dev/null; then
    fail 'A tag already exists for this version.'
    return 1
  fi
}

check_release() {
  [[ ${BEFORE_SHA:-} =~ ^[0-9a-f]{40}$ && $BEFORE_SHA != 0000000000000000000000000000000000000000 ]] || { fail 'A previous main commit is required.'; return 1; }
  local candidate previous index releases tags advertised
  candidate=$(revision_version HEAD)
  previous=$(revision_version "$BEFORE_SHA")
  if [[ $candidate == "$previous" ]]; then
    printf 'changed=false\n' >> "$GITHUB_OUTPUT"
    return
  fi
  index=$(gh api "repos/$GITHUB_REPOSITORY/contents/repo.json?ref=main" -H 'Accept: application/vnd.github.raw+json')
  jq -e 'type == "array" and length == 1 and .[0].InternalName == "GubalLibrary"' <<< "$index" >/dev/null || { fail 'repo.json must contain one GubalLibrary entry.'; return 1; }
  advertised=$(jq -r '.[0].AssemblyVersion' <<< "$index")
  releases=$(gh api --paginate --slurp "repos/$GITHUB_REPOSITORY/releases?per_page=100" | jq 'add')
  tags=$(gh api --paginate --slurp "repos/$GITHUB_REPOSITORY/tags?per_page=100" | jq '[.[][] | .name]')
  validate_release "$candidate" "$previous" "$advertised" "$releases" "$tags"
  printf 'changed=true\nversion=%s\ntag=v%s\n' "$candidate" "$candidate" >> "$GITHUB_OUTPUT"
}

if [[ ${BASH_SOURCE[0]} == "$0" ]]; then check_release; fi
