using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Скарб Полуботка»: час на хід, Глек за того, хто проспав, пауза між раундами, вихід і повернення на СВОЄ місце з тією
/// ж роллю, рукою, мапами й злитками (контракт §8), ачівки §9.1 і кінець партії.
/// </summary>
public class SkarbRejoinTests
{
    static SkarbCore CoreOf(RoomHarness h) => SkarbRoom.CoreOf(h);

    /// <summary>Телефон заснув: останнє з'єднання закрилось, Grace (20 с) минув — каркас звільняє місце.</summary>
    static void Gone(RoomHarness h, string nick)
    {
        h.Rooms.NoteOffline(nick, h.Clock.UtcNow);
        h.Clock.Advance(TimeSpan.FromSeconds(21));
        h.Rooms.DropIfGone(h.Clock.UtcNow);
    }

    static string[] Log(JsonElement v) => [.. v.GetProperty("log").EnumerateArray().Select(l => l.GetString()!)];

    // =========================================================================================
    // Час на хід
    // =========================================================================================

    [Fact]
    public void Timeout_waits_for_the_deadline_then_passes_the_worst_card()
    {
        var h = SkarbRoom.Table(3);
        var c = CoreOf(h);
        var t = c.Turn;
        var v = h.View(0);
        Assert.Equal(60_000 + Skarb.RoleRevealMs, v.GetProperty("timerMs").GetInt32());
        Assert.Equal(h.Clock.UtcNow.AddSeconds(64), v.GetProperty("deadline").GetDateTimeOffset());
        Assert.Equal("Ще є час", h.Act(0, "timeout").Message);
        h.Clock.Advance(63);
        Assert.Equal("Ще є час", h.Act(1, "timeout").Message);
        int worst;
        lock (h.Room.Sync) worst = SkarbBot.Worst(c, t).Card;
        h.Clock.Advance(1.5);
        Assert.True(h.Act(1, "timeout").Ok);
        Assert.Equal(1, c.Passes[t]);
        Assert.DoesNotContain(worst, c.Hand[t]);
        Assert.Contains(worst, c.Discard);
        Assert.Equal(6, c.Hand[t].Count);
        Assert.NotEqual(t, c.Turn);
        // Другий хід раунду — без запасу на показ ролі.
        v = h.View(0);
        Assert.Equal(60_000, v.GetProperty("timerMs").GetInt32());
        Assert.False(v.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == t).GetProperty("away").GetBoolean());
    }

    [Fact]
    public void Sleeping_twice_hands_the_seat_to_glek_until_the_human_acts()
    {
        var h = SkarbRoom.Table(3);
        var c = CoreOf(h);
        var t = c.Turn;
        // Усі троє сплять два кола: після другого свого строку кожен — away.
        for (var i = 0; i < 6; i++)
        {
            h.Clock.Advance(65);
            Assert.True(h.Act(0, "timeout").Ok);
        }
        var v = h.View(0);
        Assert.All(v.GetProperty("players").EnumerateArray(), p => Assert.True(p.GetProperty("away").GetBoolean()));
        Assert.Contains(Log(v), l => l == $"{c.NameOf(t)} задрімав — Глек копає замість нього");
        // Тепер за t грає Глек, але спершу 5 с пільги — раптом повернеться.
        Assert.Equal(t, c.Turn);
        var botIn = v.GetProperty("botIn").GetInt32();
        Assert.InRange(botIn, Skarb.AwayGraceMs - 1, Skarb.AwayGraceMs);
        Assert.Equal("Глек ще думає", h.Act(0, BoardBots.Nudge).Message);
        h.Clock.AdvanceMs(botIn);
        var turns = c.Turns;
        Assert.True(h.Act(0, BoardBots.Nudge).Ok);
        Assert.Equal(turns + 1, c.Turns);   // один поштовх — один хід
        // Своя дія знімає away.
        var next = c.Turn;
        Assert.True(h.Act(next, "pass", new { card = c.Hand[next][0] }).Ok);
        v = h.View(0);
        Assert.False(v.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == next).GetProperty("away").GetBoolean());
        Assert.Contains(Log(v), l => l == $"{c.NameOf(next)} знову копає сам");
    }

    [Fact]
    public void No_timer_means_no_deadline()
    {
        var h = SkarbRoom.Table(3, timer: "none");
        var v = h.View(0);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("deadline").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("timerMs").ValueKind);
        h.Clock.Advance(600);
        Assert.Equal("Тут без часу", h.Act(0, "timeout").Message);
    }

    [Fact]
    public void Glek_waits_for_the_role_reveal_then_thinks_a_moment()
    {
        // Сід, де першим ходить Глек.
        for (var seed = 1; seed < 50; seed++)
        {
            var h = SkarbRoom.Table(1, bots: "4", seed: seed);
            var c = CoreOf(h);
            if (c.Turn == 0)
            {
                Assert.Equal("Глекам нема що робити", h.Act(0, BoardBots.Nudge).Message);
                continue;
            }
            Assert.Equal(Skarb.RoleRevealMs, h.View(0).GetProperty("botIn").GetInt32());
            h.Clock.AdvanceMs(Skarb.RoleRevealMs - 1);
            Assert.Equal("Глек ще думає", h.Act(0, BoardBots.Nudge).Message);
            h.Clock.AdvanceMs(1);
            Assert.True(h.Act(0, BoardBots.Nudge).Ok);
            if (c.Turn != 0)
            {
                Assert.Equal(BoardBots.ThinkMs, h.View(0).GetProperty("botIn").GetInt32());
                Assert.Equal("Глек ще думає", h.Act(0, BoardBots.Nudge).Message);
            }
            return;
        }
        Assert.Fail("за 50 сідів Глек ні разу не почав");
    }

    // =========================================================================================
    // Пауза між раундами
    // =========================================================================================

    static void ToRoundEnd(RoomHarness h)
    {
        var c = CoreOf(h);
        for (var i = 0; i < 2000 && c.Phase == SkPhase.Play; i++) SkarbRoom.Step(h, 0);
        Assert.Equal(SkPhase.Round, c.Phase);
    }

    [Fact]
    public void Pause_ends_when_every_human_is_ready_or_after_next_at()
    {
        var h = SkarbRoom.Table(2, bots: "3", seed: 4);
        var c = CoreOf(h);
        ToRoundEnd(h);
        var v = h.View(0);
        Assert.Equal(h.Clock.UtcNow.AddMilliseconds(Skarb.PauseMs), v.GetProperty("nextAt").GetDateTimeOffset());
        Assert.Equal("Раунд скінчився — чекаємо наступного", h.Act(0, "pass", new { card = 1 }).Message);
        Assert.Equal("Глекам нема що робити", h.Act(0, BoardBots.Nudge).Message);
        Assert.True(h.Act(0, "next").Ok);
        Assert.Equal(SkPhase.Round, c.Phase);
        Assert.Equal("[0]", h.View(1).GetProperty("ready").GetRawText());
        Assert.Equal("next", h.View(1).GetProperty("me").GetProperty("must").GetString());
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("me").GetProperty("must").ValueKind);
        Assert.True(h.Act(0, "next").Ok);   // повторний — нічого не міняє
        Assert.Equal(SkPhase.Round, c.Phase);
        Assert.True(h.Act(1, "next").Ok);
        Assert.Equal(SkPhase.Play, c.Phase);
        Assert.Equal(2, c.Round);
        Assert.Equal("[]", h.View(0).GetProperty("ready").GetRawText());

        ToRoundEnd(h);
        Assert.True(h.Act(0, "next").Ok);
        h.Clock.Advance(14);
        Assert.True(h.Act(0, "next").Ok);
        Assert.Equal(SkPhase.Round, c.Phase);
        h.Clock.Advance(1);
        Assert.True(h.Act(0, "next").Ok);   // після nextAt — перший же next
        Assert.Equal(3, c.Round);
    }

    // =========================================================================================
    // Вихід і повернення
    // =========================================================================================

    [Fact]
    public void Dropped_player_comes_back_to_own_seat_with_the_same_role_hand_peeks_and_gold()
    {
        var h = SkarbRoom.Table(3);
        var c = CoreOf(h);
        lock (h.Room.Sync)
        {
            c.Peek[1][0] = 1;
            c.Gold[1] = 5;
        }
        Gone(h, "Петро");
        Assert.Null(h.Room.Seats[1]);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        var away = h.View(0).GetProperty("players")[1];
        Assert.True(away.GetProperty("away").GetBoolean());
        Assert.Equal("Петро", away.GetProperty("nick").GetString());
        Assert.Contains(Log(h.View(0)), l => l == "Глек сідає замість Петро");

        // Партія йде без нього: Глек ходить за Петра (без пільги — Петра за столом нема).
        var turns = c.Paths[1] + c.Passes[1] + c.Breaks[1] + c.Fixes[1] + c.Maps[1] + c.Rockfalls[1];
        for (var i = 0; i < 40 && c.Paths[1] + c.Passes[1] + c.Breaks[1] + c.Fixes[1] + c.Maps[1] + c.Rockfalls[1] == turns; i++)
        {
            if (c.Turn == 1)
            {
                var botIn = h.View(0).GetProperty("botIn").GetInt32();
                Assert.True(botIn <= BoardBots.ThinkMs + Skarb.RoleRevealMs);
            }
            SkarbRoom.Step(h, 0);
        }
        Assert.True(c.Phase != SkPhase.Play || c.Paths[1] + c.Passes[1] + c.Breaks[1] + c.Fixes[1] + c.Maps[1] + c.Rockfalls[1] > turns);
        var role = c.Mole[1];
        var hand = c.Hand[1].ToArray();
        var said = h.Outbox.OfType<TableSaid>().Count();

        Assert.True(h.Join("Петро").Ok);
        Assert.Equal("Твоя лопата на місці — Глек копав замість тебе", h.Reply.Message);
        Assert.Equal("Петро", h.Room.Seats[1]);
        var me = h.View(1).GetProperty("me");
        Assert.Equal(1, me.GetProperty("seat").GetInt32());
        Assert.Equal(role ? "mole" : "digger", me.GetProperty("role").GetString());
        Assert.Equal(hand, me.GetProperty("hand").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));
        Assert.Equal("stone", me.GetProperty("peeks").GetProperty("0").GetString());
        var back = h.View(0);
        Assert.False(back.GetProperty("players")[1].GetProperty("away").GetBoolean());
        Assert.Equal(5, back.GetProperty("players")[1].GetProperty("gold").GetInt32());
        Assert.Contains(Log(back), l => l == "Петро повертається до своєї лопати");
        Assert.Contains(h.Outbox.OfType<TableSaid>().Skip(said), s => s.Line.Text.Contains("Петро"));
    }

    [Fact]
    public void Stranger_cannot_take_someone_elses_shovel()
    {
        var h = SkarbRoom.Table(3);
        Gone(h, "Петро");
        var stranger = h.Rooms.Join(h.RoomId, "Хома");
        Assert.False(stranger.Reply.Ok);
        Assert.Equal("От халепа — місць уже нема", stranger.Reply.Message);
        Assert.Null(h.Room.Seats[1]);
        Assert.False(h.Rooms.Join(h.RoomId, "Оля").Reply.Ok);
        var game = (Skarb)h.Room.Game;
        lock (h.Room.Sync)
        {
            Assert.Null(game.Rejoin("Оля"));
            Assert.Null(game.Rejoin("Хома"));
            Assert.Equal(1, game.Rejoin("ПЕТРО"));
        }
        Assert.True(h.Join("Петро").Ok);
        Assert.Equal("Петро", h.Room.Seats[1]);
    }

    [Fact]
    public void Everyone_leaving_ends_the_game_without_winners()
    {
        var h = SkarbRoom.Table(2, bots: "2");
        var game = (Skarb)h.Room.Game;
        h.Leave("Петро");
        Assert.Empty(h.Finished);
        Assert.True(Views.Json(game.View(0)).GetProperty("players")[1].GetProperty("away").GetBoolean());
        h.Leave("Оля");
        var fin = Assert.Single(h.Finished);
        Assert.Empty(fin.Result.Winners);
        Assert.Contains("розійшлись", fin.Result.Text);
        Assert.True(game.Core.Abandoned);
        var over = Views.Json(game.View(null)).GetProperty("over");
        Assert.Equal("left", over.GetProperty("reason").GetString());
        Assert.Equal("[]", over.GetProperty("winners").GetRawText());
    }

    // =========================================================================================
    // Ачівки й кінець партії
    // =========================================================================================

    [Fact]
    public void Finisher_gets_the_gold_achievement()
    {
        var h = SkarbRoom.Table(3);
        var c = CoreOf(h);
        var t = c.Turn;
        lock (h.Room.Sync)
        {
            c.GoldGoal = 1;
            for (var x = 1; x <= 6; x++) c.Put(x, 0, "x");
            c.Hand[t].Clear();
            c.Hand[t].Add(0);
        }
        Assert.True(h.Act(t, "play", new { card = 0, x = 7, y = 0 }).Ok);
        Assert.Equal("diggers", c.Ends[^1].Side);
        var aw = Assert.Single(h.Awards, a => a.Reason == "ach:skarb-gold");
        Assert.Equal(SkarbRoom.Crew[t], aw.Nick);
        Assert.Equal(0, aw.Shards);
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:skarb-mole");
    }

    [Fact]
    public void Lone_mole_at_six_gets_mole_lone_and_rich()
    {
        var h = SkarbRoom.Table(1, bots: "5");
        var c = CoreOf(h);
        lock (h.Room.Sync)
        {
            Array.Clear(c.Mole);
            c.Mole[0] = true;
            c.Gold[0] = 9;
            c.Deck.Clear();
            foreach (var o in c.Order) c.Hand[o].Clear();
            c.Hand[0].Add(58);
            c.Turn = 0;
        }
        Assert.True(h.Act(0, "pass", new { card = 58 }).Ok);
        Assert.Equal("moles", c.Ends[^1].Side);
        Assert.Equal(13, c.Gold[0]);
        Assert.Equal(["ach:skarb-mole", "ach:skarb-lone", "ach:skarb-rich"], h.Awards.Select(a => a.Reason));
        Assert.All(h.Awards, a => Assert.Equal("Оля", a.Nick));
        Assert.Contains(Hlechyky.Games.Economy.AchievementCatalog.All, a => a.Key == "skarb-lone" && a.Reward == 25);
    }

    [Fact]
    public void Whole_game_with_people_and_gleks_finishes_with_stats()
    {
        var h = SkarbRoom.Table(2, bots: "5", seed: 31);
        for (var i = 0; i < 3000 && h.Finished.Count == 0; i++) SkarbRoom.Step(h, i % 2);
        var fin = Assert.Single(h.Finished);
        var c = CoreOf(h);
        Assert.True(c.Over);
        Assert.Equal(c.Winners, fin.Result.Winners);
        Assert.StartsWith("Скарб Полуботка: найбагат", fin.Result.Text);
        Assert.Equal(c.Winners.Length > 1 ? "🤝 Скарб поділили" : null, fin.Result.Verdict);
        Assert.Equal(c.Gold[0], fin.Result.Scores![0]);
        Assert.False(fin.Result.Scores.ContainsKey(2));   // Глек — без рахунку
        var v = h.View(0);
        Assert.Equal("over", v.GetProperty("phase").GetString());
        var over = v.GetProperty("over");
        Assert.Equal("gold", over.GetProperty("reason").GetString());
        Assert.Equal(3, over.GetProperty("rounds").GetArrayLength());
        Assert.Equal(3, over.GetProperty("stats").GetProperty("timeline").GetArrayLength());
        Assert.Equal(c.Turns, over.GetProperty("stats").GetProperty("turns").GetInt32());
        var seat0 = over.GetProperty("stats").GetProperty("seats")[0];
        foreach (var key in new[] { "seat", "paths", "deadEnds", "breaks", "fixes", "maps", "rockfalls", "passes", "moleRounds", "won", "moleGold", "finished" })
            Assert.True(seat0.TryGetProperty(key, out _), key);
        Assert.Equal(c.Order.Select(o => c.Gold[o]), over.GetProperty("gold").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(JsonValueKind.Object, v.GetProperty("roundEnd").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("botIn").ValueKind);
        Assert.Equal("over", v.GetProperty("events").EnumerateArray().Last().GetProperty("k").GetString());

        // «Ще раз» — нова партія з нуля, seq тягнеться далі.
        var seq = c.Seq;
        Assert.True(h.Rematch().Ok);
        var c2 = CoreOf(h);
        Assert.NotSame(c, c2);
        Assert.Equal(1, c2.Round);
        Assert.All(c2.Order, o => Assert.Equal(0, c2.Gold[o]));
        Assert.True(h.View(0).GetProperty("events")[0].GetProperty("seq").GetInt64() > seq);
    }
}
