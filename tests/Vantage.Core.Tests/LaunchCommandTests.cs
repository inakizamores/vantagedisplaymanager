using Vantage.App.Services;
using Xunit;

namespace Vantage.Core.Tests;

/// <summary>
/// The launch grammar every preset shortcut, sign-in entry and script depends on. Pure
/// parsing, so these are the cheapest tests in the suite — and the ones that catch a broken
/// shortcut for every user at once.
/// </summary>
public class LaunchCommandTests
{
    [Fact]
    public void Plain_launch_is_neither_tray_nor_apply()
    {
        var command = LaunchCommand.Parse([]);
        Assert.Null(command.ApplyTarget);
        Assert.False(command.TrayOnly);
        Assert.False(command.Update);
        Assert.False(command.Help);
    }

    [Theory]
    [InlineData("--apply", "Racing HDR")]
    [InlineData("--APPLY", "Racing HDR")]
    public void Apply_takes_everything_up_to_the_next_switch(string switchName, string expected)
    {
        // A .lnk hands a spaced name to us already split into words.
        var command = LaunchCommand.Parse([switchName, "Racing", "HDR"]);
        Assert.Equal(expected, command.ApplyTarget);
    }

    [Fact]
    public void Apply_equals_form_parses()
    {
        var command = LaunchCommand.Parse(["--apply=Desk"]);
        Assert.Equal("Desk", command.ApplyTarget);
    }

    [Fact]
    public void Apply_with_no_value_is_a_plain_launch()
    {
        var command = LaunchCommand.Parse(["--apply"]);
        Assert.Null(command.ApplyTarget);
    }

    [Fact]
    public void Tray_and_update_switches_parse()
    {
        Assert.True(LaunchCommand.Parse(["--tray"]).TrayOnly);
        Assert.True(LaunchCommand.Parse(["--update"]).Update);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    [InlineData("-?")]
    public void Help_switches_parse(string arg) => Assert.True(LaunchCommand.Parse([arg]).Help);

    [Fact]
    public void Ipc_message_round_trips()
    {
        var command = LaunchCommand.Parse(["--apply", "Racing", "HDR"]);
        var wire = command.ToMessage();
        Assert.Equal("Racing HDR", LaunchCommand.FromMessage(wire).ApplyTarget);

        Assert.Null(LaunchCommand.FromMessage(LaunchCommand.Parse([]).ToMessage()).ApplyTarget);
    }
}
