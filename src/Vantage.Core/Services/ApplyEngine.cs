using Vantage.Core.Models;
using Vantage.Interop;
using Vantage.Interop.Ccd;
using Vantage.Interop.Gdi;

namespace Vantage.Core.Services;

public enum ApplyStepKind
{
    ResolveAdapters,
    Validate,
    ApplyTopology,
    ApplyModes,
    WaitForSettle,
    ApplyDpi,
    ApplyHdr,
    ApplySdrWhiteLevel,
    Verify,
}

public sealed record ApplyProgress(ApplyStepKind Step, string Message);

public sealed record ApplyReport
{
    public required bool Succeeded { get; init; }
    public required List<string> Log { get; init; }
    public ProfileMatchResult? FinalMatch { get; init; }
    public string? FailureReason { get; init; }
}

/// <summary>
/// Applies a profile using the verified pipeline from BLUEPRINT §5:
/// resolve LUIDs → validate → apply → settle (poll-with-deadline, no fixed sleeps) →
/// DPI → HDR → SDR white level → final semantic verify. Every setter is re-checked.
/// </summary>
public sealed class ApplyEngine(DisplayService displayService)
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SettlePollInterval = TimeSpan.FromMilliseconds(250);
    private const int SetterRetries = 3;

    public async Task<ApplyReport> ApplyAsync(
        VantageProfile profile,
        IProgress<ApplyProgress>? progress = null,
        CancellationToken ct = default)
    {
        var log = new List<string>();
        void Report(ApplyStepKind step, string message)
        {
            log.Add($"[{step}] {message}");
            progress?.Report(new ApplyProgress(step, message));
        }

        try
        {
            // 1. Resolve adapter LUIDs: stored LUID → adapter device path → current LUID (P3).
            Report(ApplyStepKind.ResolveAdapters, "Re-mapping adapter identifiers for this session");
            var current = displayService.Capture();
            var luidMap = BuildLuidMap(profile.Replay, current, log);

            var missing = ProfileMatcher.Match(profile, current).Displays
                .Where(d => d.Kind == DisplayMatchKind.DisplayMissing)
                .Select(d => d.ProfileIdentity.FriendlyName ?? d.ProfileIdentity.StableId)
                .ToList();
            if (missing.Count > 0)
                return Fail(log, $"Displays required by this profile are not connected: {string.Join(", ", missing)}");

            // 2. Skip the CCD replay entirely when the same set of displays is already active —
            //    mode/position differences are reconciled per-display below with a single
            //    desktop transition (fewer flashes than replay-then-reconcile).
            var wantedIds = profile.Displays.Where(d => d.Enabled).Select(d => d.Identity.StableId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var liveIds = current.Displays.Select(d => d.Identity.StableId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sameTopology = wantedIds.SetEquals(liveIds);

            if (sameTopology)
            {
                Report(ApplyStepKind.ApplyTopology, "Display set unchanged — skipping topology replay");
            }
            else
            {
                var (paths, modes) = DisplayService.ToNative(profile.Replay, luidMap);

                // Validate before touching anything.
                Report(ApplyStepKind.Validate, "Validating configuration with Windows (SDC_VALIDATE)");
                var err = CcdApi.Validate(paths, modes);
                if (err != 0)
                {
                    Report(ApplyStepKind.Validate, $"Full-config validation failed ({err}); trying topology-only fallback");
                    err = CcdApi.ApplyTopologyOnly(paths);
                    if (err != 0)
                        return Fail(log, $"Windows rejected the configuration (error {err}).");
                    Report(ApplyStepKind.ApplyTopology, "Applied topology-only fallback");
                }
                else
                {
                    ct.ThrowIfCancellationRequested();
                    Report(ApplyStepKind.ApplyTopology, "Applying display topology and modes");
                    err = CcdApi.Apply(paths, modes);
                    if (err != 0)
                        return Fail(log, $"SetDisplayConfig failed (error {err}).");
                }
            }

            // 3. Reconcile per-display modes/positions/primary in one staged desktop transition.
            var staged = ReconcileModes(profile, Report);

            // 4. Wait for the OS to settle — poll with deadline, never a blind sleep (P2/P7).
            if (!sameTopology || staged)
            {
                Report(ApplyStepKind.WaitForSettle, "Waiting for Windows to settle the new configuration");
                var settled = await WaitForSettleAsync(profile, ct).ConfigureAwait(false);
                Report(ApplyStepKind.WaitForSettle, settled ? "Configuration settled" : "Settle timeout — continuing with per-display settings");
            }

            // 5. Per-display settings, each verified by re-query.
            var snapshot = displayService.Capture();
            var byStableId = snapshot.Displays.ToDictionary(d => d.Identity.StableId, StringComparer.OrdinalIgnoreCase);

            foreach (var wanted in profile.Displays.Where(d => d.Enabled))
            {
                ct.ThrowIfCancellationRequested();
                if (!byStableId.TryGetValue(wanted.Identity.StableId, out var live))
                    continue;

                var luid = new LUID
                {
                    LowPart = (uint)(live.Address.AdapterLuid & 0xFFFFFFFF),
                    HighPart = (int)(live.Address.AdapterLuid >> 32),
                };

                await ApplyDpiAsync(wanted, live, luid, Report, ct).ConfigureAwait(false);
                await ApplyHdrAsync(wanted, live, luid, Report, ct).ConfigureAwait(false);
                ApplySdrWhiteLevel(wanted, live, luid, Report);
            }

            // 6. Final semantic verification (P1) — the truth comes from re-capture, not from setters.
            Report(ApplyStepKind.Verify, "Verifying final state");
            var final = displayService.Capture();
            var match = ProfileMatcher.Match(profile, final);
            var ok = match.IsActive;
            Report(ApplyStepKind.Verify, ok ? "Profile is now active" : "Applied with differences — see match report");

            return new ApplyReport { Succeeded = ok, Log = log, FinalMatch = match, FailureReason = ok ? null : "Final state differs from profile." };
        }
        catch (OperationCanceledException)
        {
            return Fail(log, "Cancelled.");
        }
    }

    private static ApplyReport Fail(List<string> log, string reason)
    {
        log.Add($"[Failed] {reason}");
        return new ApplyReport { Succeeded = false, Log = log, FailureReason = reason };
    }

    private static Dictionary<ulong, ulong> BuildLuidMap(ReplayPayload replay, SystemSnapshot current, List<string> log)
    {
        // Current adapter device path → current LUID.
        var currentByPath = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in current.Displays)
        {
            if (d.AdapterDevicePath is { Length: > 0 } path)
                currentByPath[path] = d.Address.AdapterLuid;
        }

        var map = new Dictionary<ulong, ulong>();
        foreach (var (storedLuidHex, adapterPath) in replay.AdapterPaths)
        {
            if (!ulong.TryParse(storedLuidHex, System.Globalization.NumberStyles.HexNumber, null, out var storedLuid))
                continue;
            if (currentByPath.TryGetValue(adapterPath, out var currentLuid))
            {
                if (storedLuid != currentLuid)
                    log.Add($"[ResolveAdapters] {adapterPath}: {storedLuidHex} -> {currentLuid:X}");
                map[storedLuid] = currentLuid;
            }
            else
            {
                log.Add($"[ResolveAdapters] Adapter not present this session: {adapterPath}");
            }
        }
        return map;
    }

    /// <summary>
    /// Stages resolution/refresh/position/primary changes for every display that differs,
    /// then commits them as one desktop transition. Returns true if anything was staged.
    /// </summary>
    private bool ReconcileModes(VantageProfile profile, Action<ApplyStepKind, string> report)
    {
        var snapshot = displayService.Capture();
        var liveById = snapshot.Displays.ToDictionary(d => d.Identity.StableId, StringComparer.OrdinalIgnoreCase);
        var staged = false;

        foreach (var wanted in profile.Displays.Where(d => d.Enabled))
        {
            if (!liveById.TryGetValue(wanted.Identity.StableId, out var live) || live.GdiDeviceName is not { Length: > 0 } gdiName)
                continue;

            var wantedHz = (uint)Math.Round(wanted.RefreshMillihertz / 1000.0);
            var liveHz = (uint)Math.Round(live.RefreshMillihertz / 1000.0);
            var modeDiffers = wanted.Width != live.Width || wanted.Height != live.Height || wantedHz != liveHz;
            var placementDiffers = wanted.PositionX != live.PositionX || wanted.PositionY != live.PositionY
                                   || wanted.Primary != live.IsPrimary;
            if (!modeDiffers && !placementDiffers)
                continue;

            var result = GdiApi.StageModeChange(
                gdiName, wanted.Width, wanted.Height, wantedHz,
                wanted.PositionX, wanted.PositionY,
                setPrimary: wanted.Primary && !live.IsPrimary);

            if (result is DispChangeResult.Successful or DispChangeResult.Restart)
            {
                staged = true;
                report(ApplyStepKind.ApplyModes,
                    $"{live.Identity.FriendlyName}: staging {wanted.Width}x{wanted.Height} @ {wantedHz} Hz at ({wanted.PositionX},{wanted.PositionY})");
            }
            else
            {
                report(ApplyStepKind.ApplyModes, $"{live.Identity.FriendlyName}: stage failed ({result})");
            }
        }

        if (!staged)
            return false;

        var commit = GdiApi.CommitStaged();
        report(ApplyStepKind.ApplyModes, commit == DispChangeResult.Successful
            ? "Mode changes committed"
            : $"Mode commit returned {commit}");
        return true;
    }

    private async Task<bool> WaitForSettleAsync(VantageProfile profile, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + SettleTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var snapshot = displayService.Capture();
                var match = ProfileMatcher.Match(profile, snapshot);
                // Topology-level settle: geometry fields only; HDR/DPI come later in the pipeline.
                var topologyOk = match.Displays.All(d =>
                    d.Kind is DisplayMatchKind.Match or DisplayMatchKind.MatchWithTolerance ||
                    d.Diffs.All(diff => diff.Field is "hdr" or "dpiScale"));
                if (topologyOk)
                    return true;
            }
            catch (CcdException)
            {
                // Transient while modes switch — keep polling until deadline.
            }
            await Task.Delay(SettlePollInterval, ct).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task ApplyDpiAsync(
        ProfileDisplay wanted, DisplayState live, LUID luid,
        Action<ApplyStepKind, string> report, CancellationToken ct)
    {
        if (wanted.DpiScalePercent is not { } targetPercent || live.Dpi is not { } dpi)
            return;
        if (dpi.CurrentPercent == targetPercent)
            return;

        // Undocumented API (P4): compute relative steps against the recommended scale.
        var steps = CcdApi.DpiScaleSteps;
        var recIdx = Array.IndexOf(steps, dpi.RecommendedPercent);
        var targetIdx = Array.IndexOf(steps, targetPercent);
        if (recIdx < 0 || targetIdx < 0)
        {
            report(ApplyStepKind.ApplyDpi, $"{live.Identity.FriendlyName}: unsupported scale {targetPercent}% — skipped");
            return;
        }

        for (var attempt = 1; attempt <= SetterRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            CcdApi.SetSourceDpiScale(luid, live.Address.SourceId, targetIdx - recIdx);
            var check = CcdApi.GetSourceDpiScale(luid, live.Address.SourceId);
            if (check.Succeeded && check.Value.CurScaleRel == targetIdx - recIdx)
            {
                report(ApplyStepKind.ApplyDpi, $"{live.Identity.FriendlyName}: scale set to {targetPercent}%");
                return;
            }
            await Task.Delay(150 * attempt, ct).ConfigureAwait(false);
        }
        report(ApplyStepKind.ApplyDpi, $"{live.Identity.FriendlyName}: could not verify scale change to {targetPercent}%");
    }

    private static async Task ApplyHdrAsync(
        ProfileDisplay wanted, DisplayState live, LUID luid,
        Action<ApplyStepKind, string> report, CancellationToken ct)
    {
        if (wanted.HdrEnabled is not { } enable || !live.Hdr.Supported)
            return;
        if (live.Hdr.Enabled == enable)
            return;

        for (var attempt = 1; attempt <= SetterRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // Dual-path HDR (P4): 24H2 SET_HDR_STATE first, legacy advanced-color otherwise.
            var err = WindowsVersion.IsWindows11_24H2OrGreater
                ? CcdApi.SetHdrState(luid, live.Address.TargetId, enable)
                : CcdApi.SetAdvancedColorState(luid, live.Address.TargetId, enable);
            if (err != 0)
                report(ApplyStepKind.ApplyHdr, $"{live.Identity.FriendlyName}: HDR setter returned {err} (attempt {attempt})");

            // Setters lie — verify by re-query, with a short settle delay (HDRTray pattern).
            await Task.Delay(300 * attempt, ct).ConfigureAwait(false);
            if (VerifyHdr(luid, live.Address.TargetId, enable))
            {
                report(ApplyStepKind.ApplyHdr, $"{live.Identity.FriendlyName}: HDR {(enable ? "enabled" : "disabled")}");
                return;
            }
        }
        report(ApplyStepKind.ApplyHdr, $"{live.Identity.FriendlyName}: could not verify HDR change");
    }

    private static bool VerifyHdr(LUID luid, uint targetId, bool expected)
    {
        if (WindowsVersion.IsWindows11_24H2OrGreater)
        {
            var info2 = CcdApi.GetAdvancedColorInfo2(luid, targetId);
            if (info2.Succeeded)
                return (info2.Value.ActiveColorMode == DISPLAYCONFIG_ADVANCED_COLOR_MODE.Hdr) == expected;
        }
        var info = CcdApi.GetAdvancedColorInfo(luid, targetId);
        return info.Succeeded && info.Value.AdvancedColorEnabled == expected;
    }

    private static void ApplySdrWhiteLevel(
        ProfileDisplay wanted, DisplayState live, LUID luid, Action<ApplyStepKind, string> report)
    {
        if (wanted.SdrWhiteLevelNits is not { } nits)
            return;
        if (live.Hdr.SdrWhiteLevelNits is { } currentNits && Math.Abs(currentNits - nits) < 0.5)
            return;

        // Undocumented API — fail soft (P4).
        var err = CcdApi.SetSdrWhiteLevel(luid, live.Address.TargetId, nits);
        var check = CcdApi.GetSdrWhiteLevel(luid, live.Address.TargetId);
        if (err == 0 && check.Succeeded && Math.Abs(CcdApi.SdrWhiteLevelToNits(check.Value.SDRWhiteLevel) - nits) < 0.5)
            report(ApplyStepKind.ApplySdrWhiteLevel, $"{live.Identity.FriendlyName}: SDR white level set to {nits:0} nits");
        else
            report(ApplyStepKind.ApplySdrWhiteLevel, $"{live.Identity.FriendlyName}: SDR white level change not verified (err {err})");
    }
}
