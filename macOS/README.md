# AI Meter for macOS

A menu bar app that shows the Claude and Codex allowance left with beer glasses.
Each glass empties as usage increases. The menu bar shows each provider's
tightest window. Click it to see a glass for every window, its remaining
percentage, its reset time, and a link to the provider's usage page.
When any window is fully used, a bartender says "You've been cut off".
Unknown usage shows a dash or an error, not an empty glass.

| Light | Dark |
| --- | --- |
| ![AI Meter panel in light mode](docs/panel-light.png) | ![AI Meter panel in dark mode](docs/panel-dark.png) |

```sh
./build_app.sh            # builds "build/AI Meter.app"
./build_app.sh --install  # copies it to ~/Applications and launches it
```

Turn on **Open at Login** from the ••• menu in the panel.

Builds require a Swift 6 toolchain and macOS 14 or later. The script creates
an ad-hoc-signed app, not a notarized release. LICENSE and NOTICE are included
in the app bundle. See the [release checks](../RELEASING.md) before distribution.

## Where the numbers come from

- **Claude**: the OAuth token that Claude Code keeps in the login keychain
  (`Claude Code-credentials`), sent to `api.anthropic.com/api/oauth/usage`.
- **Codex**: the ChatGPT token in `~/.codex/auth.json` (or `$CODEX_HOME`),
  sent to `chatgpt.com/backend-api/wham/usage`.

AI Meter only reads these credentials; it never refreshes or writes them. If a
token expires, use the provider CLI to sign in or refresh it. Both endpoints are
undocumented, so AI Meter refreshes only every 5 minutes (opening the panel
shows the last fetched numbers).

A failed check replaces the previous result with an error. The footer shows
the last check time, not the time of the last successful response.

Read the [security policy](../SECURITY.md) and [disclaimers](../DISCLAIMER.md).
This project is licensed under [Apache-2.0](../LICENSE).
