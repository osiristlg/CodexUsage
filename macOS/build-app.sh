#!/bin/sh
set -eu
cd "$(dirname "$0")"
SWIFT_ADHOC_FLAGS="-Xswiftc -D -Xswiftc CODEX_USAGE_ADHOC"
swift build --disable-sandbox -c release --product CodexUsage $SWIFT_ADHOC_FLAGS
BIN_DIR=$(swift build --disable-sandbox -c release --show-bin-path $SWIFT_ADHOC_FLAGS)
APP="dist/Codex Usage.app"
mkdir -p "$APP/Contents/MacOS"
cp "$BIN_DIR/CodexUsage" "$APP/Contents/MacOS/CodexUsage"
cp Info.plist "$APP/Contents/Info.plist"

# The receiver remains the existing Kestrel application. Publish it framework-dependent
# when a .NET SDK is present, or consume a release payload prepared on another machine.
RECEIVER_DEST="$APP/Contents/Resources/Receiver"
rm -rf "$RECEIVER_DEST"
RECEIVER_SOURCE=${CODEX_USAGE_RECEIVER_PAYLOAD:-}
if [ -z "$RECEIVER_SOURCE" ] && command -v dotnet >/dev/null 2>&1; then
  RECEIVER_SOURCE=".build/receiver-payload"
  rm -rf "$RECEIVER_SOURCE"
  dotnet publish ../src/CodexUsage.Receiver/CodexUsage.Receiver.csproj -c Release --self-contained false -o "$RECEIVER_SOURCE"
fi
if [ -n "$RECEIVER_SOURCE" ] && [ -f "$RECEIVER_SOURCE/Codex Usage Receiver.dll" ]; then
  mkdir -p "$RECEIVER_DEST"
  cp -R "$RECEIVER_SOURCE/". "$RECEIVER_DEST/"
  printf 'Bundled framework-dependent receiver payload (requires .NET 10 ASP.NET Core runtime).\n'
else
  printf 'Receiver payload not bundled: install a .NET SDK or set CODEX_USAGE_RECEIVER_PAYLOAD to a published payload.\n'
fi
codesign --force --sign - "$APP"
printf 'Built %s/%s\n' "$PWD" "$APP"
