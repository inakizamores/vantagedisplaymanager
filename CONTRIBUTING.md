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
   numbers) in profiles. Monitors are identified by EDID vendor/product/serial — but never
   assume that is *unique*: identical panels of one model commonly share a serial. Take
   identity from `MonitorIdentityResolver`, which appends a connector discriminator to the
   ids that collide, and index displays with `SafeIndex`, never `ToDictionary`.
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

> **A clean local build does not mean CI is green.** CI installs the .NET 8 SDK, but the runner
> also carries newer ones and `dotnet` picks the highest — so overload resolution can differ
> from your machine. A bare `array.Reverse()` binds to LINQ locally but to the `void`
> `MemoryExtensions.Reverse(Span<T>)` on CI, where it stops compiling. Spell out
> `Enumerable.Reverse(...)`; the same applies to other span-vs-LINQ names on arrays.

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

1. Update `CHANGELOG.md` — a `## [x.y.z]` heading is required, the workflow refuses a tag
   without one — and the version in `Directory.Build.props`, which the tag must match.
2. Commit, then tag: `git tag v0.x.y[-beta]` and `git push origin v0.x.y[-beta]`.
3. The [Release workflow](.github/workflows/release.yml) runs the tests, builds the installer,
   portable and lite packages, generates a delta package, writes checksums, and publishes the
   GitHub Release. Tags containing `-` are marked pre-release.

Three things in that workflow are deliberate and easy to break by accident:

- **Deltas need the previous release present.** `--delta BestSpeed` is the packer's default,
  but it only produces a delta when the previous release's `.nupkg` is already in its
  `--outputDir` — so a `vpk download github` step runs first. Without it the packer silently
  has nothing to diff and every update is a full ~75 MB download, which is what every release
  up to 1.0.2 shipped. The feed it generates still lists that previous package, which is why
  the asset step copies *every* `.nupkg`: a feed entry pointing at an asset the release does
  not carry cannot be resolved. **Don't size a delta locally** — a local toolchain makes
  nearly every assembly differ from the CI-built previous release (471 of 476 files "patched"
  locally vs 17 of 477 in CI). Only CI-to-CI numbers are real.
- **Publishing is draft-first, and verified.** GitHub's asset upload endpoint intermittently
  answers `HTTP 500: Error creating asset temp dir` on the large files. Uploading them in one
  batch is what triggers it; on 1.0.2 that published a release carrying 2 of its 6 assets,
  with an update feed referencing packages that were not there. The workflow now creates the
  release as a draft, uploads one file at a time with five retries, verifies every local asset
  is present on the release, and only then undrafts. A partial upload fails the job instead of
  shipping something broken.
- **`vpk` is pinned** to the Velopack version referenced by `Vantage.App.csproj`. Bump both
  together, deliberately: an unpinned packer could one day emit a feed format the
  `UpdateManager` in every shipped client cannot parse, and those clients have no recovery
  path.
