#!/usr/bin/env bash
# Build Kospet Map Maker: tests -> self-contained single-file executables -> (optional) GitHub release.
#
#   ./build.sh                       # tests + publish win-x64 and linux-x64
#   ./build.sh --skip-tests          # publish only
#   ./build.sh --runtimes linux-x64  # one platform (comma-separated list)
#   ./build.sh --release             # + tag v<version>, push it and create a GitHub release (needs gh)
#   ./build.sh --version 1.1.0 --release
#
# Output: publish/<runtime>/ and publish/release/KospetMapMaker-<version>-<runtime>[.exe]
set -euo pipefail
cd "$(dirname "$0")"

VERSION=""
RUNTIMES="win-x64,linux-x64"
CONFIG="Release"
SKIP_TESTS=0
RELEASE=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)    VERSION="$2"; shift 2 ;;
    --runtimes)   RUNTIMES="$2"; shift 2 ;;
    --config)     CONFIG="$2"; shift 2 ;;
    --skip-tests) SKIP_TESTS=1; shift ;;
    --release)    RELEASE=1; shift ;;
    -h|--help)    sed -n '2,10p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

step() { printf '\n\033[36m==> %s\033[0m\n' "$1"; }

if [[ -z "$VERSION" ]]; then
  VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' KospetMapMaker.csproj | head -n1)"
fi
[[ -n "$VERSION" ]] || { echo "Version not found: pass --version or set <Version> in KospetMapMaker.csproj" >&2; exit 1; }
TAG="v$VERSION"
echo "Kospet Map Maker $VERSION"

if [[ $SKIP_TESTS -eq 0 ]]; then
  step "Tests"
  dotnet test tests/KospetMapMaker.Tests -c "$CONFIG"
fi

mkdir -p publish/release
ASSETS=()
IFS=',' read -ra RIDS <<< "$RUNTIMES"
for rid in "${RIDS[@]}"; do
  step "Publish $rid"
  out="publish/$rid"
  rm -rf "$out"
  dotnet publish KospetMapMaker.csproj -c "$CONFIG" -r "$rid" --self-contained -p:Version="$VERSION" -o "$out"
  if [[ "$rid" == win* ]]; then
    src="$out/KospetMapMaker.exe"; dst="publish/release/KospetMapMaker-$VERSION-$rid.exe"
  else
    src="$out/KospetMapMaker"; dst="publish/release/KospetMapMaker-$VERSION-$rid"
  fi
  cp "$src" "$dst"
  [[ "$rid" == win* ]] || chmod +x "$dst"
  ASSETS+=("$dst")
  echo "  $dst  $(du -h "$dst" | cut -f1)"
done

if [[ $RELEASE -eq 1 ]]; then
  step "GitHub release $TAG"
  command -v gh >/dev/null || { echo "GitHub CLI 'gh' not found (https://cli.github.com), then: gh auth login" >&2; exit 1; }
  [[ -z "$(git status --porcelain)" ]] || { echo "Working tree has uncommitted changes: commit them first" >&2; exit 1; }
  git rev-parse -q --verify "refs/tags/$TAG" >/dev/null || git tag -a "$TAG" -m "Kospet Map Maker $VERSION"
  git push origin "$TAG"
  NOTES=$(printf 'Windows: KospetMapMaker-%s-win-x64.exe\nLinux: KospetMapMaker-%s-linux-x64 (chmod +x)\n\nSingle file, nothing to install.' "$VERSION" "$VERSION")
  gh release create "$TAG" "${ASSETS[@]}" --title "Kospet Map Maker $VERSION" --notes "$NOTES"
fi

step "Done"
printf '  %s\n' "${ASSETS[@]}"
