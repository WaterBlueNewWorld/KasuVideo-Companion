#!/usr/bin/env bash
# Checks that every place naming the KasuVideo Companion's version agrees:
#   Jellyfin.Plugin.KasuVideoCompanion.csproj <Version>   0.1.0.0   (the assembly version)
#   build.yaml version                                    "0.1.0.0" (the catalog and the zip name)
#   README.md install path                                KasuVideo Companion_0.1.0.0
#   CHANGELOG.md                                          ## [0.1.0]
# and, when a tag is given (the release workflow passes it), that the tag is v0.1.0.
#
#   bash scripts/check-version.sh [v0.1.0]
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

# four_part 0.1 → 0.1.0.0, 0.1.0 → 0.1.0.0, 0.1.0.0 stays.
four_part() {
  local IFS=. parts
  read -ra parts <<<"$1"
  while ((${#parts[@]} < 4)); do parts+=(0); done
  printf '%s.%s.%s.%s' "${parts[@]:0:4}"
}

project="$(sed -n 's:.*<Version>\([0-9.]*\)</Version>.*:\1:p' Jellyfin.Plugin.KasuVideoCompanion.csproj | head -1)"
manifest="$(sed -n 's/^version: *"\{0,1\}\([0-9.]*\)"\{0,1\} *$/\1/p' build.yaml | head -1)"
readme="$(sed -n 's:.*KasuVideo Companion_\([0-9.]*\)[/"].*:\1:p' README.md | head -1)"

[[ -n "$project" ]] || { echo "No <Version> in the .csproj" >&2; exit 1; }
expected="$(four_part "$project")"
# The changelog and the tag use three parts: 0.1.0.
short="${expected%.0}"
status=0
check() { # NAME VALUE
  if [[ "$2" == "$expected" ]]; then
    printf '  ok   %-40s %s\n' "$1" "$2"
  else
    printf '  FAIL %-40s %s (expected %s)\n' "$1" "${2:-missing}" "$expected" >&2
    status=1
  fi
}

echo "KasuVideo Companion $expected (.csproj: $project)"
check "build.yaml version" "$manifest"
check "README.md install path" "$readme"
if grep -q "^## \[$short\]" CHANGELOG.md; then
  printf '  ok   %-40s %s\n' "CHANGELOG.md section" "[$short]"
else
  printf '  FAIL %-40s %s\n' "CHANGELOG.md section" "no \"## [$short]\"" >&2
  status=1
fi
if [[ $# -gt 0 ]]; then
  tag="$1"
  if [[ "$tag" != v* ]]; then
    printf '  FAIL %-40s %s (tags look like v%s)\n' "release tag" "$tag" "$short" >&2
    status=1
  else
    check "release tag $tag" "$(four_part "${tag#v}")"
  fi
fi

exit "$status"
