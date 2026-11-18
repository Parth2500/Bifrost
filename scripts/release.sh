#!/bin/sh
# Builds a release of Bifröst: the Android app signed with the owner's key, and the Linux program for the service.
#   scripts/release.sh <version>
# Needs: .NET 10 SDK, JDK 21, the Android SDK. The Android key is the owner's, made once with keytool (see docs/DEVELOPMENT.md);
# give it as BIFROST_ANDROID_KEYSTORE=<file>. The passwords are asked for here, or read from BIFROST_ANDROID_STORE_PASS and
# BIFROST_ANDROID_KEY_PASS; they are never written anywhere. Everything to attach to the release lands in artifacts/release/.
set -eu
version=${1:?usage: scripts/release.sh <version>}
: "${BIFROST_ANDROID_KEYSTORE:?set BIFROST_ANDROID_KEYSTORE to the keystore file}"
[ -f "$BIFROST_ANDROID_KEYSTORE" ] || { echo "No such keystore: $BIFROST_ANDROID_KEYSTORE" >&2; exit 1; }
if [ "$(dotnet --version 2>/dev/null | cut -d. -f1)" -lt 10 ] 2>/dev/null && [ -x "$HOME/.dotnet/dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
fi
export JAVA_HOME=${JAVA_HOME:-$HOME/.jdks/temurin-21}
sdk=${ANDROID_SDK:-$HOME/Android/Sdk}
if [ -z "${BIFROST_ANDROID_STORE_PASS:-}" ]; then printf 'Keystore password: ' >&2; stty -echo; read -r BIFROST_ANDROID_STORE_PASS; stty echo; echo >&2; fi
if [ -z "${BIFROST_ANDROID_KEY_PASS:-}" ]; then printf 'Key password (Enter if the same): ' >&2; stty -echo; read -r BIFROST_ANDROID_KEY_PASS; stty echo; echo >&2; fi
: "${BIFROST_ANDROID_KEY_PASS:=$BIFROST_ANDROID_STORE_PASS}"
export BIFROST_ANDROID_STORE_PASS BIFROST_ANDROID_KEY_PASS

out=artifacts/release
mkdir -p "$out"

dotnet build src/Bifrost.Android -c Release -p:AndroidSdkDirectory="$sdk" -p:JavaSdkDirectory="$JAVA_HOME"
apk=$(find src/Bifrost.Android/bin/Release -name '*-Signed.apk' | head -1)
cp "$apk" "$out/bifrost-$version.apk"
# Prove the key that signed it is the owner's and not the debug key.
apksigner=$(ls "$sdk"/build-tools/*/apksigner | tail -1)
"$apksigner" verify --print-certs "$out/bifrost-$version.apk" | grep -E 'Signer #1 certificate (DN|SHA-256)'

dotnet publish src/Bifrost.App -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o artifacts/bifrost-linux-x64
tar -czf "$out/bifrost-$version-linux-x64.tar.gz" -C artifacts/bifrost-linux-x64 bifrost
(cd "$out" && sha256sum "bifrost-$version.apk" "bifrost-$version-linux-x64.tar.gz" > "bifrost-$version.sha256")
ls -la "$out"
