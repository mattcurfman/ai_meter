#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
# SPDX-License-Identifier: Apache-2.0
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TEMP="$(mktemp -d)"
TAIL_PID=""
cleanup() {
  if [[ -n "$TAIL_PID" ]]; then
    kill "$TAIL_PID" 2>/dev/null || true
    wait "$TAIL_PID" 2>/dev/null || true
  fi
  rm -rf "$TEMP"
}
trap cleanup EXIT
export HOME="$TEMP/home"
export XDG_DATA_HOME="$HOME/.local/share"
export XDG_CONFIG_HOME="$HOME/.config"
mkdir -p "$TEMP/bin" "$XDG_DATA_HOME/ai-meter"
printf 'keep this file\n' > "$XDG_DATA_HOME/ai-meter/ai_meter.py"
tail "$XDG_DATA_HOME/ai-meter/ai_meter.py" -f >/dev/null &
TAIL_PID=$!
printf '#!/bin/sh\necho "GNOME Shell 46.0"\n' > "$TEMP/bin/gnome-shell"
printf '#!/bin/sh\nexit 0\n' > "$TEMP/bin/gnome-extensions"
chmod +x "$TEMP/bin/gnome-shell" "$TEMP/bin/gnome-extensions"
export PATH="$TEMP/bin:$PATH"
bash "$ROOT/install.sh"
DEST="$XDG_DATA_HOME/gnome-shell/extensions/ai-meter@ai-meter.local"
for file in metadata.json metadata.json.license extension.js usage.js stylesheet.css LICENSE NOTICE; do
  test -s "$DEST/$file"
done
cmp "$ROOT/../LICENSE" "$DEST/LICENSE"
cmp "$ROOT/../NOTICE" "$DEST/NOTICE"
kill -0 "$TAIL_PID"
grep -q 'keep this file' "$XDG_DATA_HOME/ai-meter/ai_meter.py"
bash "$ROOT/uninstall.sh"
test ! -e "$DEST"
kill -0 "$TAIL_PID"
test -f "$XDG_DATA_HOME/ai-meter/ai_meter.py"
echo 'PASS: install and uninstall preserve legacy files and unrelated processes'
