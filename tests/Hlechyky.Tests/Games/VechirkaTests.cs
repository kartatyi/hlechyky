using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>Вечірка в кімнаті (S1.3): лобі й боти, повний вечір, вид без таємного, повернення (К1), авто-пауза, фікстури.</summary>
public sealed class VechirkaTests
{
    internal static RoomHarness Table(int humans, int bots, string len = "30", int seed = 3)
    {
        var h = new RoomHarness("vechirka", new { len, voice = "none" }, seed);
        for (var k = 0; k < humans; k++) h.Join(new[] { "Оля", "Петро", "Іван", "Ганна" }[k]);
        for (var k = 0; k < bots; k++) Assert.True(h.Act(0, "bot", new { on = true }).Ok);
        return h;
    }

    static Vechirka Game(RoomHarness h) => (Vechirka)h.Room.Game;

    /// <summary>Тикати, доки умова не справдиться (ігровий час, кроки по 20 мс).</summary>
    static bool Until(RoomHarness h, Func<bool> done, int maxSeconds)
    {
        for (var k = 0; k < maxSeconds * 50; k++)
        {
            if (done()) return true;
            h.Tick();
        }
        return done();
    }

    [Fact]
    public void Lobby_bots_are_called_by_the_host_and_counted_in_rounds()
    {
        var h = Table(2, 0);
        Assert.False(h.Act(1, "bot", new { on = true }).Ok);
        Assert.True(h.Act(0, "bot", new { on = true }).Ok);
        Assert.True(h.Act(0, "bot", new { on = true }).Ok);
        var v = h.View(1);
        Assert.Equal("lobby", v.GetProperty("phase").GetString());
        Assert.Equal(["🤖 Галя", "🤖 Грицько"], v.GetProperty("lobby").GetProperty("bots").EnumerateArray().Select(b => b.GetProperty("name").GetString()));
        Assert.Equal(VechirkaRules.Rounds(30, 4), v.GetProperty("lobby").GetProperty("rounds").GetInt32());
        Assert.True(h.Act(0, "bot", new { on = false }).Ok);
        Assert.Single(h.View(0).GetProperty("lobby").GetProperty("bots").EnumerateArray());
        Assert.False(h.Act(0, "roll").Ok);
    }

    [Fact]
    public void Alone_without_bots_cannot_start_but_with_one_can()
    {
        var h = Table(1, 0);
        Assert.False(h.Start().Ok);
        h.Act(0, "bot", new { on = true });
        Assert.True(h.Start().Ok);
        Assert.Equal(2, Game(h).Core!.N);
    }

    [Fact]
    public void Bots_and_a_dozing_human_play_the_whole_evening_to_finish()
    {
        var h = Table(1, 3);
        Assert.True(h.Start().Ok);
        var c = Game(h).Core!;
        var limit = c.S.Rounds * 4 * 30 + c.S.Rounds * 20;
        Assert.True(Until(h, () => h.Room.Status == RoomStatus.Finished, limit), $"застрягли: {c.S.Phase} {c.S.Round}/{c.S.Rounds}");
        var fin = Assert.Single(h.Finished);
        Assert.Single(h.Room.Result!.Scores!);   // scores — лише за кріслами людей
        Assert.Empty(h.Awards);        // одна людина — без нагород
        Assert.StartsWith("🎉 Глечикова вечірка", h.Room.Result!.Text);
    }

    [Fact]
    public void Spectator_view_hides_secrets_and_shows_everything_public()
    {
        var h = Table(2, 2);
        h.Start();
        Until(h, () => Game(h).Core!.S.Phase == "turn", 30);
        var raw = h.View(null).GetRawText();
        Assert.DoesNotContain("BonusKeys", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rng", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"bonuses\"", raw);
        var v = h.View(null);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("you").ValueKind);
        Assert.Equal(4, v.GetProperty("players").GetArrayLength());
        var mine = h.View(0).GetProperty("you");
        Assert.Equal(0, mine.GetProperty("i").GetInt32());
        Assert.True(mine.GetProperty("toStand").GetInt32() > 0);
    }

    [Fact]
    public void Only_the_current_player_rolls_and_emo_is_rate_limited()
    {
        var h = Table(2, 0);
        h.Start();
        var c = Game(h).Core!;
        Until(h, () => c.S.Phase == "turn", 30);
        var cur = c.S.Cur!.Value;
        var seatCur = Game(h).SeatOfP(cur)!.Value;
        var other = 1 - seatCur;
        Assert.False(h.Act(other, "roll").Ok);
        Assert.True(h.Act(seatCur, "roll").Ok);
        Assert.True(h.Act(other, "emo", new { k = "laugh" }).Ok);
        Assert.True(h.Act(other, "emo", new { k = "clap" }).Ok);   // мовчки проігноровано
        Assert.Single(Game(h).Emo);
        Assert.False(h.Act(other, "emo", new { k = "dance" }).Ok);
        Assert.False(h.Act(1, "pause", new { on = true }).Ok);
        Assert.True(h.Act(0, "pause", new { on = true }).Ok);
        Assert.Equal("host", c.S.Paused);
        h.Tick(500);
        Assert.Equal("host", c.S.Paused);
        h.Act(0, "pause", new { on = false });
        Assert.Null(c.S.Paused);
    }

    [Fact]
    public void Gone_human_is_played_by_a_bot_and_comes_back_to_the_same_place()
    {
        var h = Table(3, 0);
        h.Start();
        var g = Game(h); var c = g.Core!;
        Until(h, () => c.S.Phase == "turn", 30);
        var petro = c.S.P.FindIndex(p => p.Nick == "Петро");
        h.Rooms.NoteOffline("Петро", h.Clock.UtcNow);
        h.Clock.Advance(TimeSpan.FromSeconds(21));
        h.Rooms.DropIfGone(h.Clock.UtcNow);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.True(c.S.P[petro].Away);
        Assert.Contains(h.View(null).GetProperty("players").EnumerateArray(), p => p.GetProperty("away").GetBoolean());
        // за Петра ходить машина: його хід минає швидко, а не за 20 с
        Assert.True(Until(h, () => c.S.Cur == petro, 300));
        var round = c.S.Round;
        Assert.True(Until(h, () => c.S.Cur != petro, 15));

        Assert.True(h.Rooms.Join(h.RoomId, "Петро").Reply.Ok);
        Assert.False(c.S.P[petro].Away);
        Assert.Equal(petro, g.POf(h.Room.SeatOf("Петро")!.Value));
        Assert.Equal(c.N, 3);
        _ = round;
    }

    [Fact]
    public void Lone_human_offline_pauses_the_table_and_the_room_waits()
    {
        var h = Table(1, 3);
        h.Start();
        var c = Game(h).Core!;
        Until(h, () => c.S.Phase == "turn", 30);
        h.Rooms.NoteOffline("Оля", h.Clock.UtcNow);
        h.Clock.Advance(TimeSpan.FromSeconds(21));
        h.Rooms.DropIfGone(h.Clock.UtcNow);
        var id = h.RoomId;
        Assert.NotNull(h.Rooms.Find(id));
        h.Tick(3000);   // хвилина
        Assert.Equal("empty", c.S.Paused);
        var frozen = (c.S.Round, c.S.TurnIdx, c.S.Phase);
        h.Tick(500);
        Assert.Equal(frozen, (c.S.Round, c.S.TurnIdx, c.S.Phase));
        h.Rooms.Housekeeping(h.Clock.UtcNow);
        Assert.NotNull(h.Rooms.Find(id));

        Assert.True(h.Rooms.Join(id, "Оля").Reply.Ok);
        h.Tick(5);
        Assert.Null(c.S.Paused);
        Assert.True(Until(h, () => (c.S.Round, c.S.TurnIdx, c.S.Phase) != frozen, 60));
    }

    /// <summary>Фікстури видів кожної фази для клієнта (K1): машинна копія §14.1.</summary>
    [Fact]
    public void Writes_view_fixtures_of_every_phase()
    {
        var h = Table(2, 2, seed: 11);
        var dir = Path.Combine(Paths.Root, "tests", "Hlechyky.Tests", "Fixtures", "vechirka");
        Directory.CreateDirectory(dir);
        var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        void Write(string name, JsonElement v) => File.WriteAllText(Path.Combine(dir, $"view-{name}.json"), JsonSerializer.Serialize(v, opts));
        Write("lobby", h.View(0));
        h.Start();
        var c = Game(h).Core!;
        var seen = new HashSet<string>();
        for (var k = 0; k < 400_000 && h.Room.Status == RoomStatus.Playing; k++)
        {
            h.Tick();
            var name = c.S.Phase == "prompt" ? "prompt-" + c.S.Pr!.Kind : c.S.Phase;
            if (!seen.Add(name)) continue;
            var who = c.S.Phase switch
            {
                "prompt" => c.S.Pr!.Who,
                "turn" or "aim" => c.S.Cur ?? 0,
                _ => 0,
            };
            Write(name, h.View(Game(h).SeatOfP(who) ?? 0));
            if (name is "turn" or "mg" or "results") Write(name + "-spectator", h.View(null));
        }
        Write("done", h.View(0));
        foreach (var ph in new[] { "intro", "order", "turn", "walk", "pick", "results", "late", "final" }) Assert.Contains(ph, seen);
    }
}

[Collection(SerialPerf.Name)]
public sealed class VechirkaPerfTests
{
    [Fact]
    [Trait("Category", "Perf")]
    public void Thousand_board_ticks_are_fast()
    {
        var h = VechirkaTests.Table(1, 7);
        h.Start();
        h.Tick(200);
        var sw = Stopwatch.StartNew();
        h.Tick(1000);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} мс");
    }
}
