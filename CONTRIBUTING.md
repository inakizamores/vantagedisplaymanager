# Contributing to Vantage

Thanks for your interest! Vantage aims to be the display manager that *doesn't* have quirks,
which makes contributions a bit different from a typical app: most bugs live at the boundary
between Windows, GPU drivers, and monitor firmware.

## Ground rules (the short version)

The architecture and its ten design principles are documented in
[docs/BLUEPRINT.md](docs/BLUEPRINT.md). The ones that matter most for any PR:

1. **Never `Thread.Sleep` to orchestrate** display changes — wait on events or poll with a
   deadline, then **verify by re-querying**. Setters lie.
2. **Never persist session-scoped IDs** (adapter LUIDs, CCD source/target ids, display
   numbers) in profiles. Monitors are identified by EDID vendor/product/serial.
3. **Profile identity is semantic** — the tolerant matcher in `ProfileMatcher`, never raw
   struct equality.
4. **Undocumented APIs** (DPI scale, SDR white level set) stay isolated in `Vantage.Interop`
   behind struct-size checks and fail soft.
5. **No admin rights**, no vendored closed-source binaries, no UI code in `Vantage.Core`.

## Building

```bash
dotnet build Vantage.sln          # .NET 8 SDK required
```

- `src/Vantage.Interop` — hand-written Win32/CCD/EDID/GDI P/Invoke (no dependencies)
- `src/Vantage.Core` — engine: capture, identity, matching, apply pipeline, store
- `src/Vantage.Cli` — `vantagectl`, headless twin of the app
- `src/Vantage.App` — WPF app (WPF-UI + H.NotifyIcon + CommunityToolkit.Mvvm)

## Testing display changes

CI can only compile — real display switching needs real hardware. When you change anything in
the capture/apply path, please test on your machine and note in the PR:

- `vantagectl capture test` → `vantagectl apply test` round-trips cleanly
- A real mode switch (different resolution or refresh) applies and **verifies**
- If you have HDR hardware: `vantagectl hdr on` / `off` verifies
- Sleep/wake and monitor hot-plug don't break detection (`vantagectl list`)

## Reporting bugs

Use the bug template and include `vantagectl list --json` output — display bugs are almost
always specific to a monitor/driver combination, and that output is what makes them
reproducible.

## Release process (maintainers)

1. Update `CHANGELOG.md` and the version in `Directory.Build.props`.
2. Commit, then tag: `git tag v0.x.y[-beta]` and `git push origin v0.x.y[-beta]`.
3. The [Release workflow](.github/workflows/release.yml) builds the installer, portable and
   lite packages, checksums, and publishes the GitHub Release automatically. Tags containing
   `-` are marked pre-release.
