#!/usr/bin/env bash
# Installs AI Meter for the current user and starts it. Pass --autostart to also start it at login.
set -euo pipefail
cd "$(dirname "$0")"

PACKAGES=(python3-gi gir1.2-gtk-3.0 gir1.2-ayatanaappindicator3-0.1)
missing=()
for package in "${PACKAGES[@]}"; do
  dpkg -s "$package" >/dev/null 2>&1 || missing+=("$package")
done
if ((${#missing[@]})); then
  echo "Installing ${missing[*]} (needs sudo)…"
  sudo apt-get install -y "${missing[@]}"
fi

DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
DEST="$DATA/ai-meter"
BIN="$HOME/.local/bin/ai-meter"

mkdir -p "$DEST/icons" "$HOME/.local/bin" "$DATA/applications" "$DATA/icons/hicolor/scalable/apps"
install -m 755 ai_meter.py "$DEST/ai_meter.py"
install -m 644 icons/ai-meter-symbolic.svg "$DEST/icons/"
install -m 644 icons/ai-meter-symbolic.svg "$DATA/icons/hicolor/scalable/apps/"
ln -sf "$DEST/ai_meter.py" "$BIN"

cat > "$DEST/ai-meter.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=AI Meter
Comment=Claude and Codex plan usage in the top bar
Exec=$BIN
Icon=ai-meter-symbolic
Categories=Utility;
Terminal=false
X-GNOME-Autostart-enabled=true
DESKTOP
install -m 644 "$DEST/ai-meter.desktop" "$DATA/applications/ai-meter.desktop"

if [[ "${1:-}" == "--autostart" ]]; then
  mkdir -p "${XDG_CONFIG_HOME:-$HOME/.config}/autostart"
  install -m 644 "$DEST/ai-meter.desktop" "${XDG_CONFIG_HOME:-$HOME/.config}/autostart/"
fi

pkill -f "$DEST/ai_meter.py" 2>/dev/null || true
nohup "$BIN" >/dev/null 2>&1 &
echo "AI Meter installed to $DEST and started."
