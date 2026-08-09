using System.Diagnostics;
using Vantage.Core.Services;
using Vantage.Interop.Ccd;
using Xunit;

namespace Vantage.Core.Tests;

/// <summary>
/// The engine's wait loop (BLUEPRINT P2/P7: poll with a deadline, never a blind sleep). It
/// replaced fixed <c>Task.Delay</c> calls of 150/300/600 ms in the DPI, HDR and colour-depth
/// steps, so the properties that matter are: it returns as soon as the hardware agrees, it
/// still honours the full budget when the hardware is slow, and a transient driver error while
/// an output re-trains does not end the wait early.
/// </summary>
public class ApplyEngineWaitTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(600);

    /// <summary>Timing assertions get generous slack — CI machines are not real-time systems.</summary>
    private const int Slack = 250;

    [Fact]
    public async Task ReturnsAsSoonAsTheConditionHolds_NotAtTheEndOfTheBudget()
    {
        var clock = Stopwatch.StartNew();
        var flipsAt = TimeSpan.FromMilliseconds(80);

        var result = await ApplyEngine.PollUntilAsync(
            () => clock.Elapsed >= flipsAt, Budget, CancellationToken.None);

        Assert.True(result);
        // The old code slept the full 600 ms before looking even once.
        Assert.True(clock.ElapsedMilliseconds < 600,
            $"waited {clock.ElapsedMilliseconds} ms for a condition true at 80 ms");
    }

    [Fact]
    public async Task CheckFirst_CostsNothingWhenAlreadySatisfied()
    {
        var clock = Stopwatch.StartNew();

        var result = await ApplyEngine.PollUntilAsync(
            () => true, Budget, CancellationToken.None, checkFirst: true);

        Assert.True(result);
        Assert.True(clock.ElapsedMilliseconds < Slack,
            $"an already-satisfied check took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task WithoutCheckFirst_LooksOnlyAfterGivingTheDriverAMoment()
    {
        var checks = 0;

        await ApplyEngine.PollUntilAsync(
            () => { checks++; return true; }, Budget, CancellationToken.None);

        // A setter that has only just returned hasn't reached the driver; querying instantly
        // would read back the old value and report a false failure.
        Assert.Equal(1, checks);
    }

    [Fact]
    public async Task GivesUpAfterTheBudget_AndNotLongAfter()
    {
        var clock = Stopwatch.StartNew();

        var result = await ApplyEngine.PollUntilAsync(
            () => false, Budget, CancellationToken.None);

        Assert.False(result);
        Assert.True(clock.ElapsedMilliseconds >= Budget.TotalMilliseconds,
            $"gave up after {clock.ElapsedMilliseconds} ms, short of the {Budget.TotalMilliseconds} ms budget");
        Assert.True(clock.ElapsedMilliseconds < Budget.TotalMilliseconds + Slack,
            $"overshot the budget by {clock.ElapsedMilliseconds - Budget.TotalMilliseconds} ms");
    }

    [Fact]
    public async Task KeepsPollingThroughTransientDriverErrors()
    {
        var attempts = 0;

        var result = await ApplyEngine.PollUntilAsync(
            () =>
            {
                // An output mid-re-train throws until it comes back.
                if (++attempts < 3)
                    throw new CcdException("display is re-training", 0);
                return true;
            },
            Budget, CancellationToken.None);

        Assert.True(result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Cancellation_StopsTheWait()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ApplyEngine.PollUntilAsync(() => false, TimeSpan.FromSeconds(30), cts.Token));
    }
}
