# Security policy

## Scope and support

This is an experimental project. Security fixes target the current `main`
branch. There is no guaranteed response time or long-term support for older
revisions. Keep the operating system, GJS, libsoup, and provider CLIs updated.

## Report a vulnerability

Do not disclose an unpatched vulnerability or credentials in a public issue.
Use GitHub's **Report a vulnerability** action on the repository's Security tab
if it is available. If private reporting is not enabled, ask for a private
contact through a public issue with no technical details or secrets. Wait for
that contact before sending the report.

Include the affected revision, platform, impact, and a reproduction that uses
fake credentials. If a token was exposed, revoke or rotate it with the provider.
Removing a file or comment does not revoke its token.

## Data access

- macOS reads Claude credentials through the login keychain and Codex
  credentials from `CODEX_HOME` or `~/.codex/auth.json`.
- Ubuntu reads the Claude credential file and starts the user's Codex
  `app-server`. Codex can update its own credentials, logs, and local state.
- Direct usage requests go to `api.anthropic.com` or `chatgpt.com` over HTTPS.
  The installed Codex CLI controls its own network requests and settings.
- AI Meter has no telemetry implementation. Provider services and the Codex
  CLI are separate software with separate privacy policies.

Only install code you trust. A GNOME Shell extension runs inside your desktop
session and is not a security sandbox. The macOS app is also not sandboxed.
Review `PATH`, CLI configuration, and credential directory overrides. Do not
run the installers with `sudo`.

See [RELEASING.md](RELEASING.md) for known limits and release checks. A code
review is not a security certification or proof that no vulnerability exists.
