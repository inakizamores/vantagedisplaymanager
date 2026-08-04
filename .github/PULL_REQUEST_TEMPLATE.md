## What does this PR do?

<!-- One or two sentences. Link the issue if there is one: Fixes #123 -->

## How was it tested?

<!-- Vantage changes are hardware-sensitive. Describe your display setup and what you
     verified: which profiles/modes/HDR states you applied, hot-plug, sleep/wake, etc. -->

- Windows version:
- GPU(s):
- Monitors:

## Checklist

- [ ] `dotnet build Vantage.sln -c Release` passes with no new warnings
- [ ] Follows the design principles in [docs/BLUEPRINT.md](../docs/BLUEPRINT.md) (P1–P10) — especially: no `Thread.Sleep` orchestration, verify setters by re-query, never persist session-scoped display IDs
- [ ] CHANGELOG.md updated (user-visible changes only)
