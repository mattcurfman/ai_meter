#!/bin/zsh
# SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
# SPDX-License-Identifier: Apache-2.0
# Builds "AI Meter.app" into ./build. Pass --install to copy it to ~/Applications and launch it.
set -euo pipefail
cd "${0:A:h}"

swift build -c release
BIN="$(swift build -c release --show-bin-path)/AIMeter"

APP="build/AI Meter.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp "$BIN" "$APP/Contents/MacOS/AIMeter"
cp Resources/Info.plist "$APP/Contents/Info.plist"
mkdir -p "$APP/Contents/Resources"
cp ../LICENSE ../NOTICE "$APP/Contents/Resources/"
codesign --force --sign - "$APP"
echo "Built $APP"

if [[ "${1:-}" == "--install" ]]; then
  mkdir -p ~/Applications
  pkill -x AIMeter || true
  rm -rf ~/Applications/"AI Meter.app"
  cp -R "$APP" ~/Applications/
  open ~/Applications/"AI Meter.app"
  echo "Installed ~/Applications/AI Meter.app"
fi
