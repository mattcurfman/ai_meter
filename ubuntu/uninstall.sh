#!/usr/bin/env bash
# Disable and remove the native extension.
set -euo pipefail
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
UUID="ai-meter@ai-meter.local"
gnome-extensions disable "$UUID" 2>/dev/null || true
rm -rf "$DATA/gnome-shell/extensions/$UUID"
echo "AI Meter removed."
