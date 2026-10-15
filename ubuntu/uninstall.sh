#!/usr/bin/env bash
# Stops AI Meter and removes everything install.sh created.
set -euo pipefail
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
pkill -f "$DATA/ai-meter/ai_meter.py" 2>/dev/null || true
rm -rf "$DATA/ai-meter"
rm -f "$HOME/.local/bin/ai-meter" "$DATA/applications/ai-meter.desktop" \
  "$DATA/icons/hicolor/scalable/apps/ai-meter-symbolic.svg" \
  "${XDG_CONFIG_HOME:-$HOME/.config}/autostart/ai-meter.desktop"
echo "AI Meter removed."
