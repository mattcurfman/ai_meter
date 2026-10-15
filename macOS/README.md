# AI Meter for macOS

A menu bar app that shows Claude and Codex plan usage at a glance (`C 8%  X 0%`).
The label shows each provider's tightest window; click it to see every window,
when it resets, and links to each provider's usage page.

```sh
./build_app.sh            # builds "build/AI Meter.app"
./build_app.sh --install  # copies it to ~/Applications and launches it
```

Turn on **Open at Login** from the ••• menu in the panel.

## Where the numbers come from

- **Claude**: the OAuth token that Claude Code keeps in the login keychain
  (`Claude Code-credentials`), sent to `api.anthropic.com/api/oauth/usage`.
- **Codex**: the ChatGPT token in `~/.codex/auth.json` (or `$CODEX_HOME`),
  sent to `chatgpt.com/backend-api/wham/usage`.

AI Meter only reads these credentials; it never refreshes or writes them. If a
token expires, running `claude` or `codex` once refreshes it. Both endpoints are
undocumented, so AI Meter refreshes only every 5 minutes (opening the panel
shows the last fetched numbers).
