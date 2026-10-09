using Hlechyky.Games;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Вимикач ігор (GamesSwitch.cs, секція Games): увесь розділ і окремі ігри. Вимкнене не відкривається — ні стіл, ні
/// соло, ні «сісти», «почати», «ще раз»; партія, що вже йде, дограється.
/// </summary>
public sealed class GamesSwitchTests
{
    readonly GamesOptions _opts = new();

    IServiceProvider Services() =>
        new ServiceCollection().AddSingleton<IOptionsMonitor<GamesOptions>>(new FixedOptions<GamesOptions>(_opts)).BuildServiceProvider();

    [Fact]
    public void Off_list_is_ids_by_comma_any_case_and_the_whole_switch_beats_it()
    {
        Assert.True(new GamesOptions().Plays("clicker"));
        var o = new GamesOptions { Off = " Clicker ,poker" };
        Assert.False(o.Plays("clicker"));
        Assert.False(o.Plays("poker"));
        Assert.True(o.Plays("ttt"));
        Assert.Equal("Цю гру на сайті вимкнено", o.Refusal("poker"));
        Assert.Null(o.Refusal("ttt"));

        o.Enabled = false;
        Assert.False(o.Plays("ttt"));
        Assert.Equal("Ігри на цьому сайті вимкнено", o.Refusal("ttt"));
    }

    [Fact]
    public void Switched_off_game_does_not_open_but_the_running_one_is_played_out()
    {
        var h = new RoomHarness("ttt", services: Services());
        Assert.True(h.Join("Оля").Ok);

        _opts.Off = "ttt";
        Assert.Equal("Цю гру на сайті вимкнено", h.Rooms.Create("Петро", "ttt", null).Reply.Message);
        Assert.Equal("Цю гру на сайті вимкнено", h.Join("Петро").Message);   // за стіл, що ще збирається, — теж ні

        _opts.Off = "";
        Assert.True(h.Join("Петро").Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        _opts.Off = "ttt";
        Assert.True(h.Act(0, "move", new { cell = 4 }).Ok);   // партію дограють
    }

    [Fact]
    public void Without_games_neither_solo_nor_rematch()
    {
        var solo = new RoomHarness("t-solo", services: Services());
        _opts.Enabled = false;
        Assert.Equal("Ігри на цьому сайті вимкнено", solo.Solo("Оля").Message);

        _opts.Enabled = true;
        var party = new RoomHarness("t-party", services: Services());
        party.Join("Оля");
        party.Join("Петро");
        _opts.Enabled = false;
        Assert.Equal("Ігри на цьому сайті вимкнено", party.Start().Message);
        Assert.Equal("Ігри на цьому сайті вимкнено", party.Rematch("Оля").Message);
    }
}
