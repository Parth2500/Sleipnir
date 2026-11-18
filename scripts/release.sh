#!/bin/sh
# Builds a release of Sleipnir: the plain Linux tarball, and the signed package Bifröst installs.
#   scripts/release.sh <version> <private-key-file>
# Needs the .NET 10 SDK and `bifrost` on the PATH (or BIFROST="dotnet run --project <Bifrost>/src/Bifrost.App --").
# The private key is the owner's, made by `bifrost release keygen`; keep it offline. Attach everything in artifacts/release/.
set -eu
# This project needs the .NET 10 SDK. When the one on the PATH is older and a user-level install exists, use that.
if [ "$(dotnet --version 2>/dev/null | cut -d. -f1)" -lt 10 ] 2>/dev/null && [ -x "$HOME/.dotnet/dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
fi
version=${1:?usage: scripts/release.sh <version> <private-key-file>}
key=${2:?usage: scripts/release.sh <version> <private-key-file>}
bifrost=${BIFROST:-bifrost}
out=artifacts/release
mkdir -p "$out"

dotnet publish src/Sleipnir.App -p:PublishProfile=linux-x64

# The package Bifröst installs: a manifest and the program, signed.
$bifrost release pack sleipnir "$version" \
  --program linux-x64=artifacts/linux-x64/sleipnir \
  --emits download.finished --folder downloads:write \
  --out "$out/sleipnir-$version.bifrost"
$bifrost release sign "$out/sleipnir-$version.bifrost" --key "$key"

# The plain download for people who run Sleipnir alone.
tar -czf "$out/sleipnir-$version-linux-x64.tar.gz" -C artifacts/linux-x64 sleipnir
(cd "$out" && sha256sum "sleipnir-$version-linux-x64.tar.gz" > "sleipnir-$version-linux-x64.tar.gz.sha256")

ls -la "$out"
