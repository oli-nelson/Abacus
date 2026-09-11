#!/usr/bin/env bash
# Run natively on the target OS/architecture; also smoke-tests the archived binary.
set -Eeuo pipefail
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
source "$script_dir/release-version.sh"
if [[ $# != 2 ]]; then
  echo 'Usage: scripts/package-release.sh <version> <linux-x64|linux-arm64|osx-x64|osx-arm64>' >&2
  exit 1
fi
version=$(release_version "$1")
rid=$2
case "$(uname -s)/$(uname -m)" in
  Linux/x86_64) host=linux-x64 ;;
  Linux/aarch64|Linux/arm64) host=linux-arm64 ;;
  Darwin/x86_64) host=osx-x64 ;;
  Darwin/arm64) host=osx-arm64 ;;
  *) echo 'error: unsupported host' >&2; exit 1 ;;
esac
[[ $rid == "$host" ]] || { echo "error: $rid must be built/tested on a native $rid host (this is $host)" >&2; exit 1; }
root=$(cd "$script_dir/.." && pwd)
out="$root/artifacts/release"
mkdir -p "$out"
temp=$(mktemp -d)
trap 'rm -rf "$temp"' EXIT
dotnet publish "$root/src/Abacus/Abacus.csproj" -c Release -r "$rid" \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false \
  -p:Version="$version" -o "$temp/publish"
archive="abacus-$version-$rid.tar.gz"
tar -czf "$temp/$archive" -C "$temp/publish" abacus
mkdir "$temp/smoke"
tar -xzf "$temp/$archive" -C "$temp/smoke"
# Run outside Git, without installed harnesses or dotnet on PATH.
actual=$(cd "$temp/smoke" && PATH=/usr/bin:/bin ./abacus version)
[[ $actual == "$version" ]] || { echo "error: expected $version; binary reported $actual" >&2; exit 1; }
(cd "$temp/smoke" && PATH=/usr/bin:/bin ./abacus --help >/dev/null)
mv "$temp/$archive" "$out/$archive"
echo "Packaged and verified $out/$archive"
