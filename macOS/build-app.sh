#!/bin/sh
set -eu
cd "$(dirname "$0")"
swift build -c release --product CodexUsage
BIN_DIR=$(swift build -c release --show-bin-path)
APP="dist/Codex Usage.app"
mkdir -p "$APP/Contents/MacOS"
cp "$BIN_DIR/CodexUsage" "$APP/Contents/MacOS/CodexUsage"
cp Info.plist "$APP/Contents/Info.plist"
codesign --force --sign - "$APP"
printf 'Built %s/%s\n' "$PWD" "$APP"
