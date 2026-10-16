# Release checks

Use this list before each release. It is a checklist, not a record of passed
tests. Do not label a release stable until its platform checks pass.

## Current limits

- The macOS keychain subprocess has no timeout or cancellation handler. If it
  stalls, usage refresh for both providers can stop. Add bounded subprocess
  handling and test a stalled child and large output before a stable release.
- The current release-preparation changes have not been built or rendered on
  macOS. A Mac with Swift 6 is required for those checks.
- Ubuntu's Codex tests use a local protocol fixture. They do not prove live
  compatibility with every Codex version. Record a tested CLI version
  in the release notes after a live check.
- The review did not establish provider permission or the ownership of all
  source and image content. The maintainer must confirm those points.

## Legal and source checks

- Confirm the right to license all included code and assets under Apache-2.0.
  Check employer and third-party rights where applicable.
- Retain LICENSE, NOTICE, SPDX headers, and required third-party notices.
- Check the full Git history and release artifacts for secrets. Inspect all
  screenshots for tokens, account identifiers, and private desktop content.
- Check that README links and installation commands work from a fresh clone.
- Enable GitHub private vulnerability reporting, or publish a verified private
  reporting channel in SECURITY.md. Do not assume the feature is enabled.

## Platform checks

- Run the offline tests and syntax checks in CONTRIBUTING.md.
- On macOS, build the release app with Swift 6. Test successful and failed
  requests, repeated failures, extreme percentages, login items, and quit.
  Inspect light and dark panels. Confirm LICENSE and NOTICE are in the bundle.
- On Ubuntu 24.04 / GNOME Shell 46, test install, update, uninstall, and
  disable/re-enable during a request. Confirm no child process remains after
  cleanup. Confirm unrelated processes and legacy user files are untouched.
- Run live usage checks with a test account. Record provider CLI versions.
  Test sign-out, missing credentials, HTTP errors, and cancellation. Do not
  publish credentials or raw authentication responses.

## Publish

- Record the tested revision, platforms, CLI versions, checks, and known limits
  in release notes. Label incomplete platform support as experimental.
- Include LICENSE and NOTICE in source and binary distributions.
- If distributing macOS binaries, document signing and notarization status.
- Publish only after the maintainer approves the release. A local build or
  passing test does not mean that a release was published.
