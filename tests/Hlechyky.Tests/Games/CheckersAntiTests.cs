using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>Шашки-піддавки (прохід №3, пункт 206): ті самі ходи, мета навпаки.</summary>
public class CheckersAntiTests
{
    static RoomHarness Table(string variant = "anti")
    {
        var h = new RoomHarness("checkers", options: new { variant });
        h.Join("Оля");
        h.Join("Петро");
        return h;
    }

    static string Board(params (char P, string Sq)[] men)
    {
        var b = new char[64];
        for (var i = 0; i < 64; i++) b[i] = CheckersRules.Dark(i) ? '.' : ' ';
        foreach (var (p, sq) in men) b[CheckersRules.Parse(sq)!.Value] = p;
        return new string(b);
    }

    static RoomHarness Table(int turn, params (char P, string Sq)[] men)
    {
        var h = Table();
        ((Checkers)h.Room.Game).Load(JsonSerializer.Serialize(
            new Checkers.Snapshot(Board(men), turn, null, null, false, null, null, 0, [], Anti: true)));
        return h;
    }

    static ActResult Move(RoomHarness h, int seat, params string[] path) => h.Act(seat, "move", new { path });

    [Fact]
    public void Variant_is_classic_by_default_and_anti_on_request()
    {
        var classic = new RoomHarness("checkers");
        classic.Join("Оля");
        classic.Join("Петро");
        Assert.Equal("classic", classic.View(0).GetProperty("variant").GetString());
        Assert.Equal("anti", Table().View(0).GetProperty("variant").GetString());
    }

    [Fact]
    public void Whoever_lost_the_last_piece_wins()
    {
        var h = Table(0, ('w', "c3"), ('w', "a1"), ('b', "d4"));
        Assert.True(Move(h, 0, "c3", "e5").Ok);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.Equal("anti-nopieces", h.View(0).GetProperty("result").GetProperty("reason").GetString());
        Assert.Contains("піддавки", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Capture_is_still_compulsory_in_giveaway()
    {
        var h = Table(0, ('w', "c3"), ('w', "g3"), ('b', "d4"), ('b', "h8"));
        Assert.False(Move(h, 0, "g3", "h4").Ok);
        Assert.True(Move(h, 0, "c3", "e5").Ok);
    }

    [Fact]
    public void No_moves_left_means_victory_for_the_stuck_side()
    {
        // Біла на a1 затиснута: попереду b2, а бити її нема куди — за нею c3. Після ходу чорних білим нема ходу,
        // і в піддавках це перемога білих.
        var h = Table(1, ('w', "a1"), ('b', "b2"), ('b', "c3"), ('b', "h6"));
        Assert.True(Move(h, 1, "h6", "g5").Ok);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal("anti-nomoves", h.View(0).GetProperty("result").GetProperty("reason").GetString());
    }

    [Fact]
    public void Anti_survives_save_and_load()
    {
        var h = Table();
        var game = (Checkers)h.Room.Game;
        var copy = new Checkers();
        copy.Load(game.Save()!);
        Assert.Contains("\"Anti\":true", copy.Save());
    }
}
