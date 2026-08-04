# Security Policy

## Supported versions

Only the [latest release](https://github.com/inakizamores/vantagedisplaymanager/releases/latest)
receives security fixes.

## Reporting a vulnerability

Please **do not** open a public issue for security problems. Instead, use GitHub's private
vulnerability reporting: go to the repository's **Security** tab → **Report a vulnerability**.
You'll get a response within a few days.

## Scope notes

- Vantage runs unelevated (`asInvoker`) and never requires administrator rights.
- It reads monitor EDIDs from `HKLM` (read-only) and writes only to
  `%LOCALAPPDATA%\Vantage` and HKCU.
- Profiles are plain JSON with no polymorphic type handling — the store is not an execution
  vector by design. If you find a way to make it one, that's exactly the kind of report we
  want.
