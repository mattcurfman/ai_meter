#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
# SPDX-License-Identifier: Apache-2.0
# Install the native extension for the current user.
set -euo pipefail
cd "$(dirname "$0")"

if [[ "$(gnome-shell --version)" != "GNOME Shell 46."* ]]; then
  echo "This extension supports GNOME Shell 46 (Ubuntu 24.04)." >&2
  exit 1
fi

DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
UUID="ai-meter@ai-meter.local"
DEST="$DATA/gnome-shell/extensions/$UUID"

gnome-extensions disable "$UUID" 2>/dev/null || true
mkdir -p "$DEST"
install -m 644 metadata.json metadata.json.license extension.js usage.js stylesheet.css "$DEST/"
install -m 644 ../LICENSE ../NOTICE "$DEST/"

if gnome-extensions enable "$UUID"; then
  echo "AI Meter enabled. If this is an update, log out and back in to load the new code."
else
  echo "AI Meter installed. Log out and back in, then run:"
  echo "  gnome-extensions enable $UUID"
fi
echo "Enabled extensions start at login. No Python runtime or background service is needed."
