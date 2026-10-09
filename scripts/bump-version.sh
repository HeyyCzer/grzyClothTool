#!/usr/bin/env bash
# Bumps grzyClothTool's version, commits and tags it. macOS/Linux counterpart of bump-version.ps1.
#
# The single source of truth is <FileVersion> in grzyClothTool/grzyClothTool.csproj: the in-app updater compares
# the running exe's FileVersion with that element on master, then downloads
# releases/download/v<version>/grzyClothTool.zip. Pushing the tag triggers .github/workflows/release.yml, which
# builds and publishes that release.
#
# Usage:
#   ./scripts/bump-version.sh patch           # 1.4.1 -> 1.4.2, commit + tag v1.4.2 (local only)
#   ./scripts/bump-version.sh minor --push    # 1.4.1 -> 1.5.0, commit + tag, push branch and tag
set -euo pipefail

usage() {
    echo "Usage: $0 patch|minor|major [--push]" >&2
    exit 2
}

fail() {
    echo "error: $*" >&2
    exit 1
}

part=''
push=false
for arg in "$@"; do
    case "$arg" in
        patch|minor|major) [[ -z "$part" ]] || usage; part="$arg" ;;
        --push|-Push|-p) push=true ;;
        -h|--help) usage ;;
        *) usage ;;
    esac
done
[[ -n "$part" ]] || usage

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
csproj="$repo_root/grzyClothTool/grzyClothTool.csproj"

if [[ -n "$(git -C "$repo_root" status --porcelain)" ]]; then
    fail 'Working tree is not clean. Commit or stash your changes first.'
fi

current="$(grep -Eo '<FileVersion>[0-9]+\.[0-9]+\.[0-9]+</FileVersion>' "$csproj" | head -n 1 | sed -E 's/<\/?FileVersion>//g')" || true
[[ -n "$current" ]] || fail "No <FileVersion>X.Y.Z</FileVersion> found in $csproj"

IFS=. read -r major minor patch <<< "$current"
case "$part" in
    major) major=$((major + 1)); minor=0; patch=0 ;;
    minor) minor=$((minor + 1)); patch=0 ;;
    patch) patch=$((patch + 1)) ;;
esac
next="$major.$minor.$patch"
tag="v$next"

if [[ -n "$(git -C "$repo_root" tag --list "$tag")" ]]; then
    fail "Tag $tag already exists."
fi

# Keep the file's BOM / line endings / missing final newline: perl slurps the raw bytes and rewrites only the first
# match (perl ships with macOS and practically every Linux; sed -i differs between BSD and GNU).
NEXT_VERSION="$next" perl -0777 -pi -e \
    's/<FileVersion>\d+\.\d+\.\d+<\/FileVersion>/<FileVersion>$ENV{NEXT_VERSION}<\/FileVersion>/' "$csproj"

git -C "$repo_root" add -- "$csproj"
git -C "$repo_root" commit -m ":bookmark: $tag"
git -C "$repo_root" tag -a "$tag" -m "$tag"

printf '\033[32mBumped %s -> %s (tag %s)\033[0m\n' "$current" "$next" "$tag"

branch="$(git -C "$repo_root" rev-parse --abbrev-ref HEAD)"
if [[ "$branch" != 'master' ]]; then
    printf "\033[33mWARNING: You are on '%s'. The in-app updater only sees versions that reach master.\033[0m\n" "$branch" >&2
fi

if $push; then
    git -C "$repo_root" push origin "$branch"
    git -C "$repo_root" push origin "$tag"
    printf '\033[32mPushed %s and %s. The Release workflow will build and publish the release.\033[0m\n' "$branch" "$tag"
else
    echo "Not pushed. When ready: git push origin $branch && git push origin $tag"
fi
