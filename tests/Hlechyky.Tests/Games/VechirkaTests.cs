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
    public void The_evening_is_thirty_minutes_by_default_and_fifteen_is_an_option()
    {
        // Пакет 2 (20.10): типово ≈ 30 хв (було 45), додано ≈ 15 хв.
        var opt = RoomHarness.NewRegistry().Info("vechirka")!.Options!.Single(o => o.Key == "len");
        Assert.Equal("30", opt.Default);
        Assert.Equal(["15", "30", "45", "60"], opt.Values.Select(v => v.Value).ToArray());
        var h = new RoomHarness("vechirka", new { voice = "none" }, 3);
        h.Join("Оля");
        h.Join("Петро");
        Assert.Equal(30, h.View(0).GetProperty("lobby").GetProperty("len").GetInt32());
        var fast = Table(2, 0, len: "15");
        Assert.Equal(15, fast.View(0).GetProperty("lobby").GetProperty("len").GetInt32());
        Assert.Equal(VechirkaRules.Rounds(15, 2), fast.View(0).GetProperty("lobby").GetProperty("rounds").GetInt32());
        Assert.Equal(VechirkaRules.DefaultLen, VechirkaRules.Len("17"));
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
        // стеля — чесна: на коло 4 ходи по 30 с + міні-гра до її стелі 120 с, плюс підсумок; у зведенні в пулі довші ігри (Брід, Гостинці)
        var limit = c.S.Rounds * (4 * 30 + 120) + 60;
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
        // Відстежувані фікстури переписуємо лише на прохання (VECHIRKA_FIXTURES=1): інакше кожен dotnet test
        // бруднив робочу копію, а брудна копія блокує деплой (R1 M5). Без змінної — пишемо в темп і лише перевіряємо фази.
        var dir = Environment.GetEnvironmentVariable("VECHIRKA_FIXTURES") == "1"
            ? Path.Combine(Paths.Root, "tests", "Hlechyky.Tests", "Fixtures", "vechirka")
            : Path.Combine(Path.GetTempPath(), "hlechyky-vechirka-fixtures");
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

    // ---------- виправлення за рецензіями R1/R2 ----------

    [Fact]
    public void Bot_on_first_place_is_not_a_draw_the_best_human_wins_and_the_verdict_names_the_bot()
    {
        var h = Table(2, 2, seed: 5);
        Assert.True(h.Start().Ok);
        var c = Game(h).Core!;
        var limit = c.S.Rounds * 4 * 30 + c.S.Rounds * 20 + c.S.Rounds * 120;
        Assert.True(Until(h, () => c.S.Phase is "final" or "done", limit), $"застрягли: {c.S.Phase} {c.S.Round}/{c.S.Rounds}");
        var bot = c.S.P.FindIndex(p => p.Bot);
        c.S.P[bot].Gleks = 99;
        Assert.True(Until(h, () => h.Room.Status == RoomStatus.Finished, 120));
        var res = h.Room.Result!;
        Assert.False(res.Draw);
        var best = Enumerable.Range(0, c.N).Where(i => !c[i].Bot).MinBy(c.PlaceOf);
        Assert.Contains(Game(h).SeatOfP(best)!.Value, res.Winners);
        Assert.Contains(c[bot].Name, res.Verdict);
    }

    // ---------- пакет 1: хто пішов до фіналу ----------

    /// <summary>Четверо людей до фіналу; <paramref name="leaver"/> (якщо є) стає лідером і встає перед кінцем.</summary>
    static (RoomHarness H, VechirkaCore C) FourToFinal(string? leaver)
    {
        var h = Table(4, 0, seed: 5);
        Assert.True(h.Start().Ok);
        var c = Game(h).Core!;
        var limit = c.S.Rounds * 4 * 30 + c.S.Rounds * 20 + c.S.Rounds * 120;
        Assert.True(Until(h, () => c.S.Phase is "final" or "done", limit), $"застрягли: {c.S.Phase} {c.S.Round}/{c.S.Rounds}");
        if (leaver is not null)
        {
            c.S.P[c.S.P.FindIndex(p => p.Nick == leaver)].Gleks = 99;
            h.Leave(leaver);
        }
        Assert.True(Until(h, () => h.Room.Status == RoomStatus.Finished, 120));
        return (h, c);
    }

    /// <summary>Скільки 🏺 за місце дали ніку (0 — нічого).</summary>
    static int PlaceBonus(RoomHarness h, string nick) =>
        h.Awards.Where(a => a.Nick == nick && a.Reason.StartsWith("vechirka:place:")).Sum(a => a.Shards);

    [Fact]
    public void Leader_who_left_before_the_final_is_named_head_and_nobody_moves_up_into_the_bonus()
    {
        var (h, c) = FourToFinal("Іван");
        var g = Game(h);
        var ivan = c.S.P.FindIndex(p => p.Nick == "Іван");
        Assert.Equal(1, c.PlaceOf(ivan));
        var res = h.Room.Result!;
        Assert.False(res.Draw);
        // переможець кімнати — найкраща з присутніх людей
        var others = Enumerable.Range(0, c.N).Where(i => i != ivan).OrderBy(c.PlaceOf).ToList();
        Assert.Contains(g.SeatOfP(others[0])!.Value, res.Winners);
        Assert.Contains("Голова вечірки — Іван (пішли до фіналу)", res.Verdict);
        Assert.Contains($"з тих, хто дограв, найкраще — {c[others[0]].Name}", res.Verdict);
        Assert.StartsWith("🎉 Глечикова вечірка: Іван (пішли до фіналу) — ", res.Text);
        // хто пішов — без бонусу; решта — бонус свого справжнього місця серед усіх людей (2-ге → 10, 3-тє → 5, 4-те → 0)
        Assert.Equal(0, PlaceBonus(h, "Іван"));
        Assert.DoesNotContain(h.Awards, a => a.Nick == "Іван");
        int[] bonus = [20, 10, 5, 0];
        foreach (var i in others)
        {
            var place = 1 + Enumerable.Range(0, c.N).Count(j => c.Rank(j) > c.Rank(i));
            Assert.Equal(bonus[place - 1], PlaceBonus(h, c[i].Nick!));
        }
        Assert.Equal(10, PlaceBonus(h, c[others[0]].Nick!));
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:vechirka-win");
    }

    [Fact]
    public void Nobody_left_the_bonuses_and_the_head_are_as_before()
    {
        var (h, c) = FourToFinal(null);
        var res = h.Room.Result!;
        Assert.Null(res.Verdict);
        Assert.DoesNotContain("пішли до фіналу", res.Text);
        int[] bonus = [20, 10, 5, 0];
        for (var i = 0; i < c.N; i++)
        {
            var place = 1 + Enumerable.Range(0, c.N).Count(j => c.Rank(j) > c.Rank(i));
            Assert.Equal(bonus[place - 1], PlaceBonus(h, c[i].Nick!));
            Assert.Equal(c.PlaceOf(i) == 1, h.Awards.Any(a => a.Nick == c[i].Nick && a.Reason == "ach:vechirka-win"));
            if (c.PlaceOf(i) == 1) Assert.Contains(Game(h).SeatOfP(i)!.Value, res.Winners);
        }
    }

    [Fact]
    public void Host_who_paused_and_left_does_not_keep_the_table_paused()
    {
        var h = Table(2, 1);
        h.Start();
        var c = Game(h).Core!;
        Until(h, () => c.S.Phase == "turn", 30);
        Assert.True(h.Act(0, "pause", new { on = true }).Ok);
        h.Tick();
        Assert.Equal("host", c.S.Paused);
        h.Leave("Оля");
        h.Tick(5);
        Assert.Null(c.S.Paused);
    }

    sealed class BrokenBrain : IVechirkaBrain
    {
        public void Think(VechirkaCore core, int i) => throw new NullReferenceException("мізки");
    }

    sealed class BrokenMg : IMgRunner
    {
        public bool Begin(string id, int[] pSeats, bool[] bot, LiveBots.Level level) => true;
        public string Title => "Зламана";
        public string Howto => "";
        public int CapMs => 60_000;
        public ActResult Act(int p, string action, JsonElement payload) => throw new InvalidOperationException("act");
        public TickResult Tick() => throw new InvalidOperationException("tick");
        public object? View(int? p) => null;
        public object? Frame() => null;
        public string[] Names() => [];
        public string[] SeatNames() => [];
        public MinigameResult? Result => null;
    }

    [Fact]
    public void Exceptions_in_bot_brains_and_the_minigame_do_not_end_the_evening()
    {
        var h = Table(1, 3, seed: 9);
        Game(h).BrainFactory = () => new BrokenBrain();
        Game(h).RunnerFactory = (_, _) => new BrokenMg();
        Assert.True(h.Start().Ok);
        var c = Game(h).Core!;
        var limit = c.S.Rounds * 4 * 30 + c.S.Rounds * 20 + c.S.Rounds * 60;
        Assert.True(Until(h, () => h.Room.Status == RoomStatus.Finished, limit), $"застрягли: {c.S.Phase} {c.S.Round}/{c.S.Rounds}");
        Assert.StartsWith("🎉 Глечикова вечірка", h.Room.Result!.Text);
        Assert.True(c.S.Round >= c.S.Rounds);
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

    /// <summary>§19: тисяча тиків саме у фазі mg — вечірка + хост + Крижина на вісьмох (R1 m9).</summary>
    [Fact]
    [Trait("Category", "Perf")]
    public void Thousand_ticks_of_an_eight_seat_icefloe_minigame_are_fast()
    {
        var h = VechirkaTests.Table(1, 7, seed: 21);
        var g = (Vechirka)h.Room.Game;
        g.PoolFactory = () => [.. VechirkaPool.Available.Where(e => e.Id == "icefloe")];
        h.Start();
        var c = g.Core!;
        for (var k = 0; k < 50 * 900 && c.S.Phase != "mg"; k++) h.Tick();
        Assert.Equal("mg", c.S.Phase);
        h.Tick(50);
        var sw = Stopwatch.StartNew();
        h.Tick(1000);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} мс");
    }
}
