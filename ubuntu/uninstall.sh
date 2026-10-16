#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
# SPDX-License-Identifier: Apache-2.0
# Disable and remove the native extension.
set -euo pipefail
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
UUID="ai-meter@ai-meter.local"
gnome-extensions disable "$UUID" 2>/dev/null || true
rm -rf "$DATA/gnome-shell/extensions/$UUID"
echo "AI Meter removed."
