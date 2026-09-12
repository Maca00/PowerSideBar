#!/usr/bin/env bash
set -euo pipefail

VERSION="${1:-}"
if [[ -z "$VERSION" ]]; then
  echo "Usage: ./scripts/pack.sh <version>   e.g. ./scripts/pack.sh 1.0.1"
  exit 1
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PUBLISH_DIR="$ROOT/artifacts/publish"
RELEASES_DIR="$ROOT/artifacts/releases"
ICON="$ROOT/src/PowerSideBar/Assets/powersidebar.ico"

echo "==> Publishing PowerSideBar $VERSION (win-x64 self-contained)"
rm -rf "$PUBLISH_DIR"
dotnet publish "$ROOT/src/PowerSideBar/PowerSideBar.csproj" \
  -c Release \
  --self-contained \
  -r win-x64 \
  -o "$PUBLISH_DIR" \
  -p:Version="$VERSION" \
  -p:AssemblyVersion="${VERSION}.0" \
  -p:FileVersion="${VERSION}.0"

if ! command -v vpk >/dev/null 2>&1; then
  # Common location for `dotnet tool install -g`
  export PATH="$PATH:$HOME/.dotnet/tools:/c/Users/$USER/.dotnet/tools"
fi

if ! command -v vpk >/dev/null 2>&1; then
  echo "==> Installing vpk global tool"
  dotnet tool install -g vpk --version 1.2.0
  export PATH="$PATH:$HOME/.dotnet/tools"
fi

if ! command -v vpk >/dev/null 2>&1; then
  echo "vpk not found. Add ~/.dotnet/tools to PATH and retry."
  exit 1
fi

echo "==> Packaging with Velopack"
rm -rf "$RELEASES_DIR"
vpk pack \
  --packId PowerSideBar \
  --packVersion "$VERSION" \
  --packDir "$PUBLISH_DIR" \
  --mainExe PowerSideBar.exe \
  --outputDir "$RELEASES_DIR" \
  --icon "$ICON" \
  --packTitle "PowerSideBar"

echo ""
echo "Done. Artifacts in: $RELEASES_DIR"
echo "Upload to GitHub Releases:"
echo "  vpk upload github --outputDir \"$RELEASES_DIR\" --repoUrl https://github.com/Maca00/PowerSideBar --publish --releaseName \"v$VERSION\" --tag \"v$VERSION\""
echo ""
echo "Or manually: attach Setup.exe + *.nupkg + releases.*.json from artifacts/releases to a GitHub Release tagged v$VERSION"
