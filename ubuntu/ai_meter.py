#!/usr/bin/env python3
"""AI Meter: Claude and Codex plan usage in the Ubuntu top bar."""

from __future__ import annotations

import argparse
import json
import os
import shutil
import threading
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path

APP_ID = "ai-meter"
REFRESH_SECONDS = 300  # The only refresh trigger; both endpoints are informal, so poll gently.
HERE = Path(__file__).resolve().parent
AUTOSTART_FILE = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")) / "autostart" / f"{APP_ID}.desktop"


class UsageError(Exception):
    pass


@dataclass
class Window:
    """One rate-limit window, such as Claude's 5-hour session or Codex's weekly limit."""

    title: str
    percent: float  # Percent used, 0-100.
    resets_at: datetime | None


@dataclass
class Usage:
    windows: list[Window] = field(default_factory=list)
    plan: str | None = None

    @property
    def peak(self) -> float | None:
        """The tightest window, which is what the top bar shows."""
        return max((w.percent for w in self.windows), default=None)


@dataclass
class Provider:
    name: str
    initial: str
    dashboard: str
    usage: Usage | None = None
    error: str | None = None


# --- Fetching -------------------------------------------------------------------------------


def _get_json(url: str, headers: dict[str, str], expired_hint: str) -> dict:
    request = urllib.request.Request(url, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        if error.code in (401, 403):
            raise UsageError(f"Sign-in expired. {expired_hint}") from None
        raise UsageError(f"HTTP {error.code} from {urllib.parse.urlsplit(url).hostname}.") from None
    except (urllib.error.URLError, TimeoutError) as error:
        raise UsageError(f"Network error: {getattr(error, 'reason', error)}") from None


def _parse_date(value: str | None) -> datetime | None:
    if not value:
        return None
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None


def fetch_claude() -> Usage:
    """Uses the OAuth token that Claude Code stores in ~/.claude/.credentials.json."""
    config_dir = Path(os.environ.get("CLAUDE_CONFIG_DIR", Path.home() / ".claude"))
    try:
        oauth = json.loads((config_dir / ".credentials.json").read_text())["claudeAiOauth"]
        token = oauth["accessToken"]
    except FileNotFoundError:
        raise UsageError("Claude Code sign-in not found. Run `claude` and log in.") from None
    except (KeyError, ValueError):
        raise UsageError("Claude Code sign-in isn't a Claude subscription login.") from None

    body = _get_json(
        "https://api.anthropic.com/api/oauth/usage",
        {"Authorization": f"Bearer {token}", "anthropic-beta": "oauth-2025-04-20"},
        "Run `claude` once to refresh it.",
    )
    titles = {
        "five_hour": "Session (5 hr)",
        "seven_day": "Weekly",
        "seven_day_opus": "Weekly · Opus",
        "seven_day_sonnet": "Weekly · Sonnet",
    }
    windows = [
        Window(title, float(body[key]["utilization"]), _parse_date(body[key].get("resets_at")))
        for key, title in titles.items()
        if body.get(key)
    ]
    plan = oauth.get("subscriptionType")
    return Usage(windows, plan.capitalize() if plan else None)


def _codex_title(seconds: int | None) -> str:
    if seconds is None:
        return "Limit"
    if seconds == 604_800:
        return "Weekly"
    if seconds == 86_400:
        return "Daily"
    if seconds < 86_400:
        return f"Session ({seconds // 3600} hr)"
    return f"{seconds // 86_400}-day"


def fetch_codex() -> Usage:
    """Uses the ChatGPT token that the Codex CLI stores in ~/.codex/auth.json."""
    codex_home = Path(os.environ.get("CODEX_HOME", Path.home() / ".codex"))
    try:
        tokens = json.loads((codex_home / "auth.json").read_text()).get("tokens")
    except FileNotFoundError:
        raise UsageError("Codex sign-in not found. Run `codex login`.") from None
    if not tokens or "access_token" not in tokens:
        raise UsageError("Codex is using an API key, which has no plan usage limits.")

    headers = {"Authorization": f"Bearer {tokens['access_token']}", "User-Agent": "codex_cli_rs"}
    if tokens.get("account_id"):
        headers["ChatGPT-Account-Id"] = tokens["account_id"]
    body = _get_json("https://chatgpt.com/backend-api/wham/usage", headers, "Run `codex` once to refresh it.")

    limits = body.get("rate_limit") or {}
    windows = [
        Window(
            _codex_title(w.get("limit_window_seconds")),
            float(w["used_percent"]),
            datetime.fromtimestamp(w["reset_at"], timezone.utc) if w.get("reset_at") else None,
        )
        for w in (limits.get("primary_window"), limits.get("secondary_window"))
        if w
    ]
    plan = body.get("plan_type")
    return Usage(windows, plan.capitalize() if plan else None)


FETCHERS = {"Claude": fetch_claude, "Codex": fetch_codex}


def new_providers() -> list[Provider]:
    return [
        Provider("Claude", "C", "https://claude.ai/settings/usage"),
        Provider("Codex", "X", "https://chatgpt.com/codex/settings/usage"),
    ]


def refresh(providers: list[Provider]) -> None:
    """Fetches every provider. A failure keeps the last good numbers instead of blanking them."""
    for provider in providers:
        try:
            provider.usage, provider.error = FETCHERS[provider.name](), None
        except UsageError as error:
            provider.error = str(error)
        except Exception as error:  # Unexpected response shape; keep the indicator alive.
            provider.error = f"Unexpected response: {error}"


# --- Formatting -----------------------------------------------------------------------------


def percent_text(provider: Provider) -> str:
    peak = provider.usage.peak if provider.usage else None
    return "–" if peak is None else f"{round(peak)}%"


def top_bar_label(providers: list[Provider]) -> str:
    """Compact label, e.g. "C 8%  X 0%"."""
    return "  ".join(f"{p.initial} {percent_text(p)}" for p in providers)


def relative(when: datetime, now: datetime | None = None) -> str:
    seconds = (when - (now or datetime.now(timezone.utc))).total_seconds()
    if seconds <= 60:
        return "now"
    minutes = int(seconds // 60)
    if minutes < 60:
        return f"in {minutes} min"
    hours, minutes = divmod(minutes, 60)
    if hours < 24:
        return f"in {hours} hr {minutes} min" if minutes else f"in {hours} hr"
    days = round(hours / 24)
    return f"in {days} day" + ("s" if days != 1 else "")


def window_line(window: Window) -> str:
    line = f"{window.title}: {round(window.percent)}% used"
    if window.resets_at:
        line += f" · resets {relative(window.resets_at)}"
    return line


def bar(percent: float, width: int = 20) -> str:
    filled = round(min(max(percent, 0), 100) / 100 * width)
    return "▰" * filled + "▱" * (width - filled)


# --- Top bar indicator ----------------------------------------------------------------------


def run_indicator() -> None:
    import gi

    gi.require_version("Gtk", "3.0")
    gi.require_version("AyatanaAppIndicator3", "0.1")
    from gi.repository import AyatanaAppIndicator3 as AppIndicator
    from gi.repository import Gio, GLib, Gtk

    providers = new_providers()
    state = {"updated": None, "busy": False}

    indicator = AppIndicator.Indicator.new(APP_ID, "ai-meter-symbolic", AppIndicator.IndicatorCategory.APPLICATION_STATUS)
    indicator.set_icon_theme_path(str(HERE / "icons"))
    indicator.set_title("AI Meter")
    indicator.set_status(AppIndicator.IndicatorStatus.ACTIVE)

    def info_item(text: str) -> Gtk.MenuItem:
        item = Gtk.MenuItem(label=text)
        item.set_sensitive(False)
        return item

    def action_item(text: str, callback) -> Gtk.MenuItem:
        item = Gtk.MenuItem(label=text)
        item.connect("activate", lambda _: callback())
        return item

    def toggle_autostart(item: Gtk.CheckMenuItem) -> None:
        if item.get_active():
            AUTOSTART_FILE.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(HERE / f"{APP_ID}.desktop", AUTOSTART_FILE)
        else:
            AUTOSTART_FILE.unlink(missing_ok=True)

    def render() -> bool:
        indicator.set_label(top_bar_label(providers), "C 100%  X 100%")
        menu = Gtk.Menu()
        for provider in providers:
            header = provider.name + (f" · {provider.usage.plan}" if provider.usage and provider.usage.plan else "")
            menu.append(info_item(header))
            if provider.usage is None and provider.error is None:
                menu.append(info_item("Loading…"))
            if provider.usage:
                for window in provider.usage.windows:
                    menu.append(info_item(f"{bar(window.percent)}  {window_line(window)}"))
                if not provider.usage.windows:
                    menu.append(info_item("No usage limits reported."))
            if provider.error:
                menu.append(info_item(f"⚠ {provider.error}"))
            url = provider.dashboard
            menu.append(action_item(f"Open {provider.name} Usage Page…",
                                    lambda url=url: Gio.AppInfo.launch_default_for_uri(url, None)))
            menu.append(Gtk.SeparatorMenuItem())

        if state["updated"]:
            menu.append(info_item(f"Updated {state['updated']:%H:%M}"))
        autostart = Gtk.CheckMenuItem(label="Start at Login")
        autostart.set_active(AUTOSTART_FILE.exists())
        autostart.connect("toggled", toggle_autostart)
        menu.append(autostart)
        menu.append(action_item("Quit AI Meter", Gtk.main_quit))
        menu.show_all()
        indicator.set_menu(menu)
        return True  # Keep the per-minute re-render that keeps "resets in …" current.

    def finished() -> bool:
        state["busy"] = False
        state["updated"] = datetime.now()
        render()
        return False

    def start_refresh() -> bool:
        if not state["busy"]:
            state["busy"] = True
            threading.Thread(target=lambda: (refresh(providers), GLib.idle_add(finished)), daemon=True).start()
        return True

    render()
    start_refresh()
    GLib.timeout_add_seconds(REFRESH_SECONDS, start_refresh)
    GLib.timeout_add_seconds(60, render)  # Local only; no network.
    Gtk.main()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--once", action="store_true", help="print usage to the terminal and exit")
    args = parser.parse_args()

    if not args.once:
        run_indicator()
        return

    providers = new_providers()
    refresh(providers)
    print(top_bar_label(providers))
    for provider in providers:
        plan = f" ({provider.usage.plan})" if provider.usage and provider.usage.plan else ""
        print(f"\n{provider.name}{plan}")
        for window in provider.usage.windows if provider.usage else []:
            print(f"  {bar(window.percent)}  {window_line(window)}")
        if provider.error:
            print(f"  ⚠ {provider.error}")


if __name__ == "__main__":
    main()
