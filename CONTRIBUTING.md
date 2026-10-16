# Contributing

Thank you for helping with AI Meter. Keep changes small and state the problem
they solve. Discuss large changes in an issue before implementation. Be
respectful. Discuss the code, not the person.

## Report a problem

For non-security bugs, open a GitHub issue with your OS, GNOME Shell or Swift
version, provider CLI version, steps to reproduce, and expected result.
Remove personal data from logs and screenshots. Never include credentials.
Follow [SECURITY.md](SECURITY.md) for security reports.

## Develop and test

- macOS: use macOS 14 or later and a Swift 6 toolchain. Run `swift build` in
  `macOS/`, then run `./build_app.sh` to build the app bundle. Check both light
  and dark modes, failed requests, and login-item behavior on a Mac.
- Ubuntu: use Ubuntu 24.04 with GNOME Shell 46, GJS, and libsoup 3. From
  `ubuntu/`, run `gjs -m tests/usage.test.js`, `bash tests/install.test.sh`, and
  `bash -n install.sh uninstall.sh tests/install.test.sh`.
- Run `git diff --check` before submitting a patch.
- Live checks use your credentials and contact provider services. They are
  optional for contributors. Never put real credentials in tests.

Keep network requests asynchronous and bounded. Do not log tokens, add
telemetry, force credential rotation, or delete unrelated user files.
Use local fixtures for regression tests. Test extension disable and re-enable
when you change request or process cleanup.

## Submit a pull request

Explain the change, its risks, and the checks you ran. State any checks you
could not run. Include screenshots for UI changes, without account details.
Do not commit build output, local credentials, or private logs.

## License and attribution

Unless you explicitly state otherwise, contributions intentionally submitted
for inclusion are licensed under Apache-2.0, as described in section 5 of
[LICENSE](LICENSE). Submit only work that you have the right to contribute.
Identify any third-party source or assets and include their required notices.
Do not remove existing copyright notices.

Add these lines at the start of new source files, with the file's comment syntax:

```text
SPDX-FileCopyrightText: YEAR COPYRIGHT HOLDER
SPDX-License-Identifier: Apache-2.0
```

Keep required shebangs and the Swift tools-version directive first. For formats
without comments, use a `.license` sidecar or an entry in `REUSE.toml`.
