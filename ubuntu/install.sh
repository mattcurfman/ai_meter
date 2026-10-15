#!/usr/bin/env bash
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

# Stop only the old indicator owned by this user.
for proc in /proc/[0-9]*; do
  [[ -O "$proc" && -r "$proc/cmdline" ]] || continue
  mapfile -d '' -t args < "$proc/cmdline" || continue
  if [[ "${args[1]:-}" == "$DATA/ai-meter/ai_meter.py" || "${args[1]:-}" == "$HOME/.local/bin/ai-meter" ]]; then
    kill "${proc##*/}" 2>/dev/null || true
  fi
done
rm -rf "$DATA/ai-meter"
rm -f "$HOME/.local/bin/ai-meter" "$DATA/applications/ai-meter.desktop" \
  "$DATA/icons/hicolor/scalable/apps/ai-meter-symbolic.svg" \
  "${XDG_CONFIG_HOME:-$HOME/.config}/autostart/ai-meter.desktop"

gnome-extensions disable "$UUID" 2>/dev/null || true
mkdir -p "$DEST"
install -m 644 metadata.json extension.js usage.js stylesheet.css "$DEST/"

if gnome-extensions enable "$UUID"; then
  echo "AI Meter enabled. If this is an update, log out and back in to load the new code."
else
  echo "AI Meter installed. Log out and back in, then run:"
  echo "  gnome-extensions enable $UUID"
fi
echo "Enabled extensions start at login. No Python runtime or background service is needed."
