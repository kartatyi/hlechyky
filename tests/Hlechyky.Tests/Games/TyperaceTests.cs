using System.Text.Json;
using System.Text.RegularExpressions;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Клавоперегони за столом (2–10) і тренування самому: фази, друк (pos), фініш із суддею, місця, хвіст, стеля,
/// хвилина тиші, вихід, «Ще раз», підсумок і Журнал, види й кадри на дроті, соло зі збереженням (spec §2–§4).
/// Банк — маленький тестовий (<see cref="TyperaceTestBank"/>), щоб тести не залежали від правок у data/.
/// </summary>
public class TyperaceTests
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Тарас", "Ірина", "Сашко", "Марко", "Леся", "Остап"];

    internal static RoomHarness Table(int players = 2, object? options = null, int seed = 1, bool start = true)
    {
        var h = new RoomHarness("typerace", options ?? new { length = "short", source = "classic" }, seed, RoomHarness.WithService(TyperaceTestBank.Create()));
        for (var i = 0; i < players; i++) Assert.True(h.Join(Nicks[i]).Ok, h.Reply.Message);
        if (start) Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    internal static void ToGo(RoomHarness h)
    {
        h.Tick(TyperaceRace.ReadyTicks);
        Assert.Equal("go", Phase(h));
    }

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;
    internal static int Len(RoomHarness h) => h.View(null).GetProperty("len").GetInt32();
    static DateTimeOffset GoAt(RoomHarness h) => DateTimeOffset.Parse(h.View(null).GetProperty("goAt").GetString()!);

    static JsonElement Racer(RoomHarness h, int seat) =>
        h.View(null).GetProperty("racers").EnumerateArray().Single(r => r.GetProperty("seat").GetInt32() == seat);

    static ActResult Finish(RoomHarness h, int seat, TyperaceLogs.Log log) => h.Act(seat, "finish", new { k = log.K, d = log.D });

    /// <summary>Поставити годинник на мить «старт + atMs» і фінішувати чесним журналом, що бачив трохи менше часу.</summary>
    static ActResult FinishAt(RoomHarness h, int seat, long atMs, int seed = 1)
    {
        h.Clock.UtcNow = GoAt(h).AddMilliseconds(atMs);
        return Finish(h, seat, TyperaceLogs.HumanIn(Len(h), atMs - 300, seed));
    }

    static void Pos(RoomHarness h, int seat, int c, int e = 0) => h.Input(seat, "pos", new { c, e });

    static int CpmFor(int len, long ms) => (int)Math.Round(len * 60_000.0 / ms, MidpointRounding.AwayFromZero);

    // ------------------------------------------------------------------------------------ паспорт

    [Fact]
    public void Catalog_lists_both_games_sharing_one_module_and_css()
    {
        var reg = RoomHarness.NewRegistry();
        var table = reg.Catalog.Single(g => g.Id == "typerace");
        var solo = reg.Catalog.Single(g => g.Id == "typerace-solo");
        Assert.Equal("typerace", table.Module);
        Assert.Equal("typerace", solo.Module);
        Assert.True(table.HasCss);
        Assert.True(solo.HasCss);
        var ti = reg.Info("typerace")!;
        var si = reg.Info("typerace-solo")!;
        Assert.Equal((GameGroup.Live, 2, 10, 200, StartMode.ByHost, false), (ti.Group, ti.MinPlayers, ti.MaxPlayers, ti.TickMs, ti.Start, ti.Hidden));
        Assert.Equal((GameGroup.Solo, 1, 1, StartMode.Immediate, true, true), (si.Group, si.MinPlayers, si.MaxPlayers, si.Start, si.Private, si.Persistent));
        Assert.Equal(ScoreOrder.HigherIsBetter, si.Score);
        Assert.Equal(["length", "source"], ti.Options!.Select(o => o.Key));
        Assert.Equal("medium", ti.Options![0].Default);
        Assert.Equal("all", ti.Options![1].Default);
        Assert.Contains("Steam Deck", ti.Hint);
        var json = JsonSerializer.Serialize(table, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"module\":\"typerace\"", json);
        Assert.Contains("\"hasCss\":true", json);
        Assert.Equal("7", new Typerace().SeatName(6));
    }

    // ------------------------------------------------------------------------------------ лобі й старт

    [Fact]
    public void Table_waits_for_the_host_and_hides_the_text_in_the_lobby()
    {
        var h = Table(2, start: false);
        var v = h.View(1);
        Assert.Equal("lobby", v.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("text").ValueKind);
        Assert.Equal(0, v.GetProperty("len").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("src").ValueKind);
        Assert.Equal(2, v.GetProperty("racers").GetArrayLength());
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
        // не господар — не почне
        Assert.False(h.Rooms.StartByHost(h.RoomId, "Петро").Reply.Ok);
        // сам-один — теж ні
        var alone = Table(1, start: false);
        Assert.False(alone.Start().Ok);
        Assert.Contains("щонайменше 2", alone.Reply.Message);
    }

    [Fact]
    public void Start_picks_a_text_and_counts_down_fifteen_ticks_before_go()
    {
        var h = Table(2);
        var v = h.View(0);
        Assert.Equal("ready", v.GetProperty("phase").GetString());
        var text = v.GetProperty("text").GetString()!;
        Assert.Equal(text.Length, v.GetProperty("len").GetInt32());
        Assert.InRange(text.Length, 120, 203);          // коротко: ~150
        Assert.Equal("classic", v.GetProperty("src").GetProperty("kind").GetString());
        Assert.Equal(3000, v.GetProperty("goIn").GetInt64());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("goAt").ValueKind);
        var readyAt = DateTimeOffset.Parse(v.GetProperty("readyAt").GetString()!);

        h.Tick(TyperaceRace.ReadyTicks - 1);
        Assert.Equal("ready", Phase(h));
        var views = h.Outbox.OfType<RoomViews>().Count();
        h.Tick(1);
        Assert.Equal("go", Phase(h));
        Assert.Equal(views + 1, h.Outbox.OfType<RoomViews>().Count());
        Assert.Equal(readyAt.AddMilliseconds(3000), GoAt(h));
        var endsAt = DateTimeOffset.Parse(h.View(0).GetProperty("endsAt").GetString()!);
        Assert.Equal(GoAt(h).AddMilliseconds(60_000 + 500L * text.Length), endsAt);
        Assert.Equal(60_000 + 500L * text.Length, h.View(0).GetProperty("endsIn").GetInt64());
    }

    // ------------------------------------------------------------------------------------ pos

    [Fact]
    public void Pos_is_ignored_before_go_after_finish_and_from_a_leaver()
    {
        var h = Table(3);
        Pos(h, 0, 5);
        Assert.Equal(0, Racer(h, 0).GetProperty("c").GetInt32());
        var early = h.Act(0, "pos", new { c = 5, e = 0 });
        Assert.False(early.Ok);
        Assert.Equal("Ще не старт", early.Message);

        ToGo(h);
        Pos(h, 0, 5);
        Assert.Equal(5, Racer(h, 0).GetProperty("c").GetInt32());
        Assert.True(FinishAt(h, 0, 30_000).Ok);
        var after = h.Act(0, "pos", new { c = 3, e = 0 });
        Assert.Equal("Ти вже на фініші", after.Message);
        Assert.Equal(Len(h), Racer(h, 0).GetProperty("c").GetInt32());

        // хто встав — уже не їде: гра відмовляє навіть напряму
        var game = (Typerace)h.Room.Game;
        h.Leave("Ганна");
        lock (h.Room.Sync)
        {
            var r = game.Act(2, "pos", Views.Payload(new { c = 9, e = 0 }));
            Assert.Equal("Ти в цих перегонах не їдеш", r.Message);
        }
        Assert.Equal(0, Racer(h, 2).GetProperty("c").GetInt32());
        Assert.Equal(3, Racer(h, 2).GetProperty("s").GetInt32());
        Assert.True(Racer(h, 2).GetProperty("gone").GetBoolean());
    }

    [Fact]
    public void Pos_clamps_to_the_text_and_rejects_garbage()
    {
        var h = Table(2);
        ToGo(h);
        var len = Len(h);
        Pos(h, 0, 7, 1);
        var before = Views.Text(h.View(null));
        foreach (var bad in new object?[]
                 {
                     new { c = len + 1, e = 0 }, new { c = -1, e = 0 }, new { c = 3, e = 2 }, new { c = "5", e = 0 },
                     new { c = 3 }, new { e = 1 }, new { c = 2.5, e = 0 }, new[] { 3, 0 }, 5, null,
                 })
        {
            var r = h.Act(0, "pos", bad);
            Assert.False(r.Ok);
            Assert.Equal("Не зрозумів, де ти", r.Message);
        }
        Assert.Equal(before, Views.Text(h.View(null)));
        Assert.True(h.Act(0, "pos", new { c = len, e = 0 }).Ok);        // рівно len — ще можна (фініш прийде слідом)
        Assert.Equal("Тут так не ходять", h.Act(0, "jump", new { }).Message);
    }

    [Fact]
    public void Pos_marks_the_frame_dirty_only_when_something_changed()
    {
        var h = Table(2);
        ToGo(h);
        int Frames() => h.Outbox.OfType<RoomFrame>().Count();
        h.Tick(3);
        Assert.Equal(0, Frames());                 // ніхто не друкує — кадрів нема
        Pos(h, 0, 3);
        h.Tick(1);
        Assert.Equal(1, Frames());
        Pos(h, 0, 3);
        h.Tick(1);
        Assert.Equal(1, Frames());                 // той самий c і e — нічого
        Pos(h, 0, 3, 1);
        Pos(h, 1, 1);
        h.Tick(1);
        Assert.Equal(2, Frames());                 // дві зміни за тик — один кадр
        var f = Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);
        var p = f.GetProperty("p").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        Assert.Equal(20, p.Length);
        Assert.Equal([3, 1, 1, 0], p[..4]);
        Assert.All(p[4..], x => Assert.Equal(-1, x));
        Assert.Equal(1, Racer(h, 0).GetProperty("wrong").GetInt32());   // червоний з pos рахується, поки нема журналу
    }

    // ------------------------------------------------------------------------------------ фініш

    [Fact]
    public void Finish_records_server_time_place_speed_and_accuracy()
    {
        var h = Table(2);
        ToGo(h);
        var len = Len(h);
        var r = FinishAt(h, 0, 30_000);
        Assert.True(r.Ok);
        var cpm = CpmFor(len, 30_000);
        Assert.Equal($"Фініш! {cpm} зн/хв, точність 100 %", r.Message);
        var me = Racer(h, 0);
        Assert.Equal(30_000, me.GetProperty("fin").GetInt64());
        Assert.Equal(1, me.GetProperty("place").GetInt32());
        Assert.Equal(cpm, me.GetProperty("cpm").GetInt32());
        Assert.Equal(100, me.GetProperty("acc").GetInt32());
        Assert.Equal(0, me.GetProperty("wrong").GetInt32());
        Assert.Equal(2, me.GetProperty("s").GetInt32());
        Assert.Equal(len, me.GetProperty("c").GetInt32());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("flag").ValueKind);
    }

    [Fact]
    public void Finish_before_go_twice_or_after_done_is_refused_with_the_right_text()
    {
        var h = Table(2);
        var log = TyperaceLogs.Human(Len(h));
        Assert.Equal("Перегони ще не почались", Finish(h, 0, log).Message);
        ToGo(h);
        Assert.True(FinishAt(h, 0, 30_000).Ok);
        Assert.Equal("Ти вже на фініші", Finish(h, 0, log).Message);
        Assert.True(FinishAt(h, 1, 40_000, seed: 2).Ok);
        h.Tick(1);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        // каркас дограну партію відбиває сам; гра, якщо її таки спитати, каже своє
        Assert.False(Finish(h, 1, log).Ok);
        lock (h.Room.Sync)
            Assert.Equal("Перегони вже скінчились", h.Room.Game.Act(1, "finish", Views.Payload(new { k = log.K, d = log.D })).Message);
        // журнал без k/d — не відмова, а «журнал не читається»
        var h2 = Table(2);
        ToGo(h2);
        h2.Clock.AdvanceMs(20_000);
        var bare = h2.Act(0, "finish", new { });
        Assert.True(bare.Ok);
        Assert.Contains("журнал не читається", bare.Message);
        Assert.Equal("bad-log", Racer(h2, 0).GetProperty("flag").GetString());
    }

    [Fact]
    public void Places_follow_arrival_order_and_skip_flagged_runs()
    {
        var h = Table(3);
        ToGo(h);
        var len = Len(h);
        // Петро — бот із рівним ритмом, приїхав першим
        h.Clock.UtcNow = GoAt(h).AddMilliseconds(len * 150 + 200);
        var bot = Finish(h, 1, TyperaceLogs.Robot(len, 150));
        Assert.True(bot.Ok);
        Assert.Equal("Фініш, але заїзд не зараховано: ритм метронома", bot.Message);
        Assert.True(FinishAt(h, 2, len * 150 + 5_000, seed: 3).Ok);
        Assert.True(FinishAt(h, 0, len * 150 + 9_000, seed: 4).Ok);
        Assert.Equal(JsonValueKind.Null, Racer(h, 1).GetProperty("place").ValueKind);
        Assert.Equal("metronome", Racer(h, 1).GetProperty("flag").GetString());
        Assert.Equal(1, Racer(h, 2).GetProperty("place").GetInt32());
        Assert.Equal(2, Racer(h, 0).GetProperty("place").GetInt32());
        h.Tick(1);
        var result = h.View(null).GetProperty("result");
        Assert.Equal([2, 0, 1], result.GetProperty("order").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal([2], result.GetProperty("winners").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal([2], h.Finished.Single().Result.Winners);
    }

    [Theory]
    [InlineData("short", 20_000, 30_000)]      // 0,75 × 20 с = 15 с — мало, підтягуємо до 30 с
    [InlineData("short", 60_000, 45_000)]      // 0,75 × 60 с = 45 с
    [InlineData("medium", 170_000, -1)]        // 0,75 × 170 с = 127 с → не більше 90 с, і не далі за стелю партії
    public void First_verified_finish_sets_the_tail_within_thirty_and_ninety_seconds(string length, long winnerMs, long tailMs)
    {
        var h = Table(2, new { length });
        ToGo(h);
        var len = Len(h);
        var cap = 60_000 + 500L * len;
        Assert.True(winnerMs < cap);
        Pos(h, 1, 3);
        Assert.True(FinishAt(h, 0, winnerMs).Ok, h.Reply.Message);
        var v = h.View(null);
        Assert.True(v.GetProperty("tail").GetBoolean());
        var endsAt = DateTimeOffset.Parse(v.GetProperty("endsAt").GetString()!);
        var expected = tailMs >= 0 ? GoAt(h).AddMilliseconds(winnerMs + tailMs) : GoAt(h).AddMilliseconds(Math.Min(cap, winnerMs + 90_000));
        Assert.Equal(expected, endsAt);
    }

    [Fact]
    public void The_tail_ends_the_race_and_ranks_the_rest_by_progress_then_errors()
    {
        var h = Table(4);
        ToGo(h);
        Pos(h, 1, 50);
        Pos(h, 2, 80);
        Pos(h, 3, 49, 1); h.Tick(1);          // Іван помилився двічі
        Pos(h, 3, 49, 0); h.Tick(1);
        Pos(h, 3, 50, 1); h.Tick(1);
        Pos(h, 3, 50, 0); h.Tick(1);
        Assert.True(FinishAt(h, 0, 20_000).Ok);
        h.Tick(1);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        h.Tick(30_000 / 200);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var result = h.View(null).GetProperty("result");
        Assert.Equal([0, 2, 1, 3], result.GetProperty("order").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal([0], result.GetProperty("winners").EnumerateArray().Select(x => x.GetInt32()));
        Assert.False(result.GetProperty("allDone").GetBoolean());
        Assert.Contains("Оля", result.GetProperty("say").GetString());
        // хто не дописав, лишається зі своїми знаками й зн/хв за час заїзду, без місця
        Assert.Equal(80, Racer(h, 2).GetProperty("c").GetInt32());
        Assert.True(Racer(h, 2).GetProperty("cpm").GetInt32() > 0);
        Assert.Equal(JsonValueKind.Null, Racer(h, 2).GetProperty("place").ValueKind);
        Assert.Equal(2, Racer(h, 3).GetProperty("wrong").GetInt32());
        var line = h.Outbox.OfType<Journal>().Last().Text;
        Assert.StartsWith("Клавоперегони: Оля ", line);
        Assert.Matches(@"Ганна \d+ \(лише \d+ %\), Петро \d+ \(лише \d+ %\), Іван \d+ \(лише \d+ %\)$", line);
    }

    [Fact]
    public void The_hard_cap_ends_a_race_nobody_finished_as_a_draw_with_standings()
    {
        var h = Table(2);
        ToGo(h);
        var len = Len(h);
        Pos(h, 0, 30);
        Pos(h, 1, 60);
        var capTicks = (int)((60_000 + 500L * len) / 200);
        h.Tick(capTicks - 2);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        h.Tick(3);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var fin = h.Finished.Single();
        Assert.True(fin.Result.Draw);
        Assert.Null(fin.Result.Scores);
        Assert.StartsWith("Клавоперегони: ніхто не дописав — Петро ", fin.Result.Text);
        var result = h.View(null).GetProperty("result");
        Assert.Equal([1, 0], result.GetProperty("order").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Empty(result.GetProperty("winners").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("say").GetString()));
    }

    [Fact]
    public void All_finished_ends_the_race_at_the_next_tick()
    {
        var h = Table(2);
        ToGo(h);
        Assert.True(FinishAt(h, 0, 25_000).Ok);
        Assert.True(FinishAt(h, 1, 26_000, seed: 2).Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        h.Tick(1);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var result = h.View(null).GetProperty("result");
        Assert.True(result.GetProperty("allDone").GetBoolean());
        Assert.Contains("Усі дописали", result.GetProperty("say").GetString());
    }

    [Fact]
    public void An_idle_minute_without_a_single_key_closes_the_table_as_a_draw()
    {
        var h = Table(2);
        ToGo(h);
        h.Tick(60_000 / 200 - 1);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        h.Tick(1);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Finished.Single().Result.Draw);
        Assert.Equal("Клавоперегони: ніхто й пальцем не ворухнув", h.Finished.Single().Result.Text);
        Assert.Contains("пальцем не ворухнув", h.View(null).GetProperty("result").GetProperty("say").GetString());

        // одна літера за хвилину — і стіл живе далі
        var alive = Table(2);
        ToGo(alive);
        alive.Tick(250);
        Pos(alive, 1, 1);
        alive.Tick(100);
        Assert.Equal(RoomStatus.Playing, alive.Room.Status);
    }

    [Fact]
    public void A_leaver_keeps_a_finished_result_but_cannot_win()
    {
        var h = Table(3);
        ToGo(h);
        Assert.True(FinishAt(h, 0, 20_000).Ok);
        h.Leave("Оля");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.True(FinishAt(h, 1, 22_000, seed: 2).Ok);
        Pos(h, 2, 10);
        h.Tick(31_000 / 200);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Finished.Single().Result.Winners);
        var olya = Racer(h, 0);
        Assert.True(olya.GetProperty("gone").GetBoolean());
        Assert.Equal(1, olya.GetProperty("place").GetInt32());
        Assert.Equal(3, olya.GetProperty("s").GetInt32());
        Assert.Equal("Оля", olya.GetProperty("nick").GetString());
        Assert.Contains("Оля ", h.Finished.Single().Result.Text);
        Assert.Contains("🚪", h.Finished.Single().Result.Text);
        // очки — лише тим, хто дограв за столом
        Assert.Equal([1], h.Finished.Single().Result.Scores!.Keys.Order());
    }

    [Fact]
    public void The_last_racer_alone_may_still_finish_and_wins()
    {
        var h = Table(2);
        ToGo(h);
        Pos(h, 1, 20);
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.True(FinishAt(h, 0, 40_000).Ok);
        h.Tick(1);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Finished.Single().Result.Winners);
        Assert.Contains("їхав сам", h.View(null).GetProperty("result").GetProperty("say").GetString());
    }

    [Fact]
    public void Everyone_leaving_closes_the_table_with_a_journal_line()
    {
        var h = Table(2);
        ToGo(h);
        h.Leave("Петро");
        h.Leave("Оля");
        var fin = h.Finished.Single();
        Assert.True(fin.Result.Draw);
        Assert.Equal("Клавоперегони: усі встали з-за столу, заїзд не дограли", fin.Result.Text);
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text == fin.Result.Text);
        Assert.Null(h.Rooms.Find(h.RoomId));          // порожній стіл каркас прибрав
    }

    [Fact]
    public void Rematch_gives_a_new_text_a_clean_state_and_rotated_seats()
    {
        var h = Table(2);
        ToGo(h);
        var first = h.View(null).GetProperty("text").GetString();
        Assert.True(FinishAt(h, 0, 25_000).Ok);
        Assert.True(FinishAt(h, 1, 27_000, seed: 2).Ok);
        h.Tick(1);
        var seats = h.Room.Seats.ToArray();
        Assert.True(h.Rematch().Ok);
        Assert.Equal(2, h.Room.Round);
        Assert.Equal(seats[1], h.Room.Seats[0]);
        Assert.Equal(seats[0], h.Room.Seats[1]);
        var v = h.View(null);
        Assert.Equal("ready", v.GetProperty("phase").GetString());
        Assert.NotEqual(first, v.GetProperty("text").GetString());
        Assert.False(v.GetProperty("tail").GetBoolean());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("result").ValueKind);
        foreach (var r in v.GetProperty("racers").EnumerateArray())
        {
            Assert.Equal(0, r.GetProperty("c").GetInt32());
            Assert.Equal(JsonValueKind.Null, r.GetProperty("fin").ValueKind);
            Assert.Equal(JsonValueKind.Null, r.GetProperty("place").ValueKind);
        }
        Assert.Equal(new[] { "Петро", "Оля" }, v.GetProperty("racers").EnumerateArray().Select(r => r.GetProperty("nick").GetString()));
    }

    [Fact]
    public void A_finished_table_reopened_by_a_newcomer_shows_the_lobby_again()
    {
        var h = Table(2);
        ToGo(h);
        h.Leave("Петро");
        Assert.True(FinishAt(h, 0, 25_000).Ok);
        h.Tick(1);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Join("Ганна").Ok);
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
        var v = h.View(null);
        Assert.Equal("lobby", v.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("text").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("result").ValueKind);
        Assert.Equal(new[] { "Оля", "Ганна" }, v.GetProperty("racers").EnumerateArray().Select(r => r.GetProperty("nick").GetString()));
        Assert.True(h.Start().Ok);
        Assert.Equal("ready", Phase(h));
    }

    [Fact]
    public void The_same_seed_and_actions_give_the_same_view()
    {
        string Play(int seed)
        {
            var h = Table(3, new { length = "medium", source = "all" }, seed);
            ToGo(h);
            Pos(h, 1, 40);
            Pos(h, 2, 10, 1);
            h.Tick(5);
            Assert.True(FinishAt(h, 0, 60_000).Ok);
            h.Tick(100 * 5);
            return Views.Text(h.View(null)) + h.Finished.Single().Result.Text;
        }
        Assert.Equal(Play(5), Play(5));
        Assert.NotEqual(Play(5), Play(6));
    }

    // ------------------------------------------------------------------------------------ очки, ачівки, Журнал

    [Fact]
    public void Scores_go_to_every_verified_finisher_and_the_journal_lists_speeds()
    {
        var h = Table(3);
        ToGo(h);
        var len = Len(h);
        h.Clock.UtcNow = GoAt(h).AddMilliseconds(len * 150 + 200);
        Assert.True(Finish(h, 2, TyperaceLogs.Robot(len, 150)).Ok);     // бот приїхав першим — і без місця
        Assert.True(FinishAt(h, 0, 40_000).Ok);
        Assert.True(FinishAt(h, 1, 46_000, seed: 2).Ok);
        h.Tick(1);
        var fin = h.Finished.Single();
        Assert.Equal(new Dictionary<int, long> { [0] = CpmFor(len, 40_000), [1] = CpmFor(len, 46_000) }, fin.Result.Scores);
        Assert.Equal($"Клавоперегони: Оля {CpmFor(len, 40_000)} зн/хв, Петро {CpmFor(len, 46_000)}, Ганна 🤖 (не зараховано)", fin.Result.Text);
        Assert.Equal([0], fin.Result.Winners);
        // рядок таблиці столу — з каркаса (перемоги), соло-рядків за стіл нема
        Assert.Empty(h.Scores);
    }

    [Fact]
    public void Achievements_are_requested_for_300_cpm_and_for_a_clean_long_run_only()
    {
        // середній текст (240+ знаків), чисто й швидко — обидві
        var h = Table(2, new { length = "medium" });
        ToGo(h);
        var len = Len(h);
        Assert.True(len >= 240);
        var fast = (long)(len * 60_000.0 / 320);
        Assert.True(FinishAt(h, 0, fast).Ok);
        Assert.Equal(["ach:typerace-300", "ach:typerace-clean"], h.Awards.Where(a => a.Nick == "Оля").Select(a => a.Reason).Order());
        Assert.All(h.Awards, a => Assert.Equal(0, a.Shards));

        // з помилками — лише «швидкі пальці», якщо точність ≥ 95 %
        h.Clock.UtcNow = GoAt(h).AddMilliseconds(fast + 1000);
        var sloppy = TyperaceLogs.Sloppy(len, every: 30, meanMs: (int)(fast / len));
        h.Clock.UtcNow = GoAt(h).AddMilliseconds(Math.Max(fast + 1000, sloppy.Ms + 200));
        Assert.True(Finish(h, 1, sloppy).Ok);
        var petro = h.Awards.Where(a => a.Nick == "Петро").Select(a => a.Reason).ToList();
        Assert.DoesNotContain("ach:typerace-clean", petro);

        // бот не дістає нічого
        var b = Table(2, new { length = "medium" });
        ToGo(b);
        var blen = Len(b);
        b.Clock.UtcNow = GoAt(b).AddMilliseconds(blen * 150 + 200);
        Assert.True(Finish(b, 0, TyperaceLogs.Robot(blen, 150)).Ok);
        Assert.Empty(b.Awards);

        // короткий текст чисто — «Без жодної помилки» не дається (треба 240+)
        var s = Table(2, new { length = "short" });
        ToGo(s);
        var slen = Len(s);
        Assert.True(FinishAt(s, 0, (long)(slen * 60_000.0 / 320)).Ok);
        Assert.Equal(["ach:typerace-300"], s.Awards.Select(a => a.Reason));
    }

    [Fact]
    public void Uncle_Hlek_says_one_line_in_the_result_and_never_in_the_table_chat()
    {
        var h = Table(2);
        ToGo(h);
        Assert.True(FinishAt(h, 0, 25_000).Ok);
        Assert.True(FinishAt(h, 1, 29_000, seed: 2).Ok);
        h.Tick(1);
        var say = h.View(null).GetProperty("result").GetProperty("say").GetString();
        Assert.False(string.IsNullOrWhiteSpace(say));
        Assert.DoesNotContain("{", say);
        Assert.Empty(h.Outbox.OfType<TableSaid>());
        Assert.Empty(h.Outbox.OfType<DjSays>());
    }

    [Theory]
    [InlineData(0, "Равлик")]
    [InlineData(119, "Равлик")]
    [InlineData(120, "Пішохід")]
    [InlineData(200, "Велосипед")]
    [InlineData(280, "Трактор")]
    [InlineData(359, "Трактор")]
    [InlineData(360, "Мотоцикл")]
    [InlineData(450, "Ракета")]
    public void Titles_go_from_snail_to_rocket(int cpm, string title) => Assert.Equal(title, TyperaceLines.Title(cpm));

    // ------------------------------------------------------------------------------------ види й кадри

    [Fact]
    public void View_shape_matches_the_spec_and_the_seat_view_equals_the_watcher_view()
    {
        var h = Table(3);
        ToGo(h);
        Pos(h, 1, 12);
        Assert.True(FinishAt(h, 0, 30_000).Ok);
        var v = h.View(null);
        Assert.Equal(
            ["phase", "turn", "len", "text", "src", "opts", "readyAt", "goAt", "endsAt", "goIn", "endsIn", "tail", "racers", "result"],
            v.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
        Assert.Equal(["kind", "author", "title", "year"], v.GetProperty("src").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["seat", "nick", "gone", "c", "s", "wrong", "fin", "place", "cpm", "acc", "flag"],
            v.GetProperty("racers")[0].EnumerateObject().Select(p => p.Name));
        var watcher = Views.Text(h.View(null));
        for (var s = 0; s < 3; s++) Assert.Equal(watcher, Views.Text(h.View(s)));
        Assert.False(Views.Has(v, "me"));
        Assert.False(Views.Has(v, "noTexts"));
        Assert.True(Views.Text(v).Length < 3 * 1024);
    }

    [Fact]
    public void Frame_is_a_flat_int_array_under_120_bytes_for_ten_racers()
    {
        var h = Table(10, new { length = "long" });
        ToGo(h);
        var len = Len(h);
        Assert.True(len >= 480);
        for (var s = 0; s < 10; s++) Pos(h, s, len - 1 - s, s % 2);
        h.Tick(1000);
        var game = h.Room.Game;
        string json;
        lock (h.Room.Sync) json = Views.Text(game.Frame());
        Assert.True(json.Length < 120, json);
        Assert.Matches(@"^\{""t"":\d+,""p"":\[(-?\d+,){19}-?\d+\]\}$", json);
    }

    [Fact]
    public void Tick_sends_frames_only_when_dirty_and_views_only_on_events()
    {
        var h = Table(2);
        var views0 = h.Outbox.OfType<RoomViews>().Count();
        var changes = 0;
        h.Tick(TyperaceRace.ReadyTicks);
        for (var i = 1; i <= 20; i++)
        {
            Pos(h, 0, i);
            changes++;
            if (i % 3 == 0) { Pos(h, 1, i / 3); changes++; }
            h.Tick(1);
            h.Tick(1);
        }
        Assert.True(FinishAt(h, 0, 30_000).Ok);
        h.Tick(1);
        Assert.True(FinishAt(h, 1, 31_000, seed: 2).Ok);
        h.Tick(1);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Outbox.OfType<RoomFrame>().Count() <= changes + 2);
        // старт (каркас) + відлік → go + перший фініш + другий фініш разом із кінцем
        Assert.Equal(4, h.Outbox.OfType<RoomViews>().Count() - views0 + 1);
    }

    [Fact]
    public void Finish_act_marks_the_view_dirty_and_the_next_tick_sends_it()
    {
        var h = Table(3);
        ToGo(h);
        var before = h.Outbox.OfType<RoomViews>().Count();
        Assert.True(FinishAt(h, 0, 30_000).Ok);
        Assert.Equal(before, h.Outbox.OfType<RoomViews>().Count());
        h.Tick(1);
        Assert.Equal(before + 1, h.Outbox.OfType<RoomViews>().Count());
        h.Tick(1);
        Assert.Equal(before + 1, h.Outbox.OfType<RoomViews>().Count());
    }

    [Fact]
    public void The_server_accepts_exactly_what_the_module_sends()
    {
        // імена полів модуль бере з одного рядка WIRE поруч із register — тут читаємо саме його
        var js = File.ReadAllText(Paths.Resolve("web/games/typerace.js"));
        var m = Regex.Match(js, @"const WIRE = \{ pos: \['c', 'e'\], finish: \['k', 'd'\], go: \['length', 'source'\], stop: \[\] \};");
        Assert.True(m.Success, "у typerace.js нема рядка WIRE з полями pos/finish/go/stop");
        Assert.Contains("ctx.input(WIRE_POS", js);

        var h = Table(2);
        ToGo(h);
        h.Input(0, "pos", new { c = 4, e = 1 });
        Assert.Equal(4, Racer(h, 0).GetProperty("c").GetInt32());
        Assert.Equal(1, Racer(h, 0).GetProperty("s").GetInt32());
        h.Input(0, "pos", new { pos = 9 });               // чужі імена не пройдуть
        Assert.Equal(4, Racer(h, 0).GetProperty("c").GetInt32());
        var log = TyperaceLogs.HumanIn(Len(h), 29_000);
        h.Clock.UtcNow = GoAt(h).AddMilliseconds(30_000);
        var alien = h.Act(0, "finish", new { log = log.K, deltas = log.D });   // фініш є, але журналу під чужими іменами суддя не бачить
        Assert.True(alien.Ok);
        Assert.Contains("журнал не читається", alien.Message);
        Assert.Equal("bad-log", Racer(h, 0).GetProperty("flag").GetString());

        var s = Solo();
        Assert.True(s.Act(0, "go", new { length = "short", source = "proverbs" }).Ok);
        s.Tick(1);
        Assert.Equal("short", s.View(0).GetProperty("opts").GetProperty("length").GetString());
        Assert.Equal("proverbs", s.View(0).GetProperty("opts").GetProperty("source").GetString());
        Assert.True(s.Act(0, "stop", new { }).Ok);
        s.Tick(1);
        Assert.Equal("pick", s.View(0).GetProperty("phase").GetString());
        Assert.True(s.Act(0, "go", new { length = (string?)null, source = (string?)null }).Ok);   // відсутнє — лишається збережене
        s.Tick(1);
        Assert.Equal("proverbs", s.View(0).GetProperty("opts").GetProperty("source").GetString());
    }

    // ------------------------------------------------------------------------------------ соло

    internal static RoomHarness Solo(string nick = "Оля", TyperaceBank? bank = null, FakeStore? store = null, int seed = 1)
    {
        var h = new RoomHarness("typerace-solo", seed: seed, services: RoomHarness.WithService(bank ?? TyperaceTestBank.Create()));
        if (store is not null) foreach (var (k, v) in store.States) h.Store.States[k] = v;
        Assert.True(h.Solo(nick).Ok, h.Reply.Message);
        return h;
    }

    static JsonElement Me(RoomHarness h) => h.View(0).GetProperty("me");

    static void SoloGo(RoomHarness h, object? payload = null)
    {
        Assert.True(h.Act(0, "go", payload ?? new { }).Ok, h.Reply.Message);
        h.Tick(1);
        Assert.Equal("ready", Phase(h));
        h.Tick(TyperaceRace.ReadyTicks);
        Assert.Equal("go", Phase(h));
    }

    [Fact]
    public void Solo_opens_in_pick_with_defaults_and_remembers_saved_settings()
    {
        var h = Solo();
        var v = h.View(0);
        Assert.Equal("pick", v.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("text").ValueKind);
        Assert.Equal("medium", v.GetProperty("opts").GetProperty("length").GetString());
        Assert.Equal("all", v.GetProperty("opts").GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, Me(h).GetProperty("best").ValueKind);
        Assert.Equal(0, Me(h).GetProperty("runs").GetInt32());
        Assert.False(Me(h).GetProperty("isRecord").GetBoolean());
        Assert.False(Views.Has(v, "noTexts"));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);

        var store = new FakeStore();
        store.SaveState("typerace-solo:оля", """{"v":1,"length":"short","source":"classic","best":312,"runs":14,"last":{"cpm":298,"acc":96,"wrong":7,"ms":60403,"len":300}}""");
        var back = Solo(store: store);
        Assert.Equal("pick", Phase(back));
        Assert.Equal("short", back.View(0).GetProperty("opts").GetProperty("length").GetString());
        Assert.Equal("classic", back.View(0).GetProperty("opts").GetProperty("source").GetString());
        Assert.Equal(312, Me(back).GetProperty("best").GetInt32());
        Assert.Equal(14, Me(back).GetProperty("runs").GetInt32());

        // чужа версія збереження — чистий стан, а не виняток
        var odd = new FakeStore();
        odd.SaveState("typerace-solo:оля", """{"v":2,"length":"long","best":999,"runs":5}""");
        var clean = Solo(store: odd);
        Assert.Equal("medium", clean.View(0).GetProperty("opts").GetProperty("length").GetString());
        Assert.Equal(JsonValueKind.Null, Me(clean).GetProperty("best").ValueKind);
        var junk = new FakeStore();
        junk.SaveState("typerace-solo:оля", "{не json");
        Assert.Equal(0, Me(Solo(store: junk)).GetProperty("runs").GetInt32());
    }

    [Fact]
    public void Solo_go_validates_options_and_starts_the_countdown()
    {
        var h = Solo();
        Assert.Equal("Такої довжини нема", h.Act(0, "go", new { length = "xxl" }).Message);
        Assert.Equal("Таких текстів нема", h.Act(0, "go", new { source = "газети" }).Message);
        Assert.Equal("Таких текстів нема", h.Act(0, "go", new { source = 5 }).Message);
        Assert.Equal("pick", Phase(h));
        var views = h.Outbox.OfType<RoomViews>().Count();
        Assert.True(h.Act(0, "go", new { length = "long", source = "classic" }).Ok);
        Assert.Equal(views, h.Outbox.OfType<RoomViews>().Count());     // вид піде з тика
        h.Tick(1);
        Assert.Equal(views + 1, h.Outbox.OfType<RoomViews>().Count());
        var v = h.View(0);
        Assert.Equal("ready", v.GetProperty("phase").GetString());
        Assert.Equal("long", v.GetProperty("opts").GetProperty("length").GetString());
        Assert.True(v.GetProperty("len").GetInt32() >= 480);
        Assert.Equal("Спершу дограй або натисни «Стоп»", h.Act(0, "go", new { }).Message);
        h.Tick(TyperaceRace.ReadyTicks);
        Assert.Equal("go", Phase(h));
        Assert.Equal("Спершу дограй або натисни «Стоп»", h.Act(0, "go", new { }).Message);
        // збереження лягає після кожного вдалого ходу
        Assert.Contains("\"length\":\"long\"", h.Store.States["typerace-solo:оля"]);
    }

    [Fact]
    public void Solo_finish_bumps_runs_updates_best_and_scores_the_leaderboard()
    {
        var h = Solo();
        SoloGo(h, new { length = "short" });
        var len = Len(h);
        var r = FinishAt(h, 0, 40_000);
        Assert.True(r.Ok);
        Assert.StartsWith("Рекорд!", r.Message);
        h.Tick(1);
        Assert.Equal("done", Phase(h));
        var cpm = CpmFor(len, 40_000);
        Assert.Equal(cpm, Me(h).GetProperty("best").GetInt32());
        Assert.Equal(1, Me(h).GetProperty("runs").GetInt32());
        Assert.True(Me(h).GetProperty("isRecord").GetBoolean());
        Assert.Equal(TyperaceLines.Title(cpm), Me(h).GetProperty("title").GetString());
        Assert.Equal(cpm, h.Scores.Single().Score);
        Assert.Equal("typerace-solo", h.Scores.Single().GameId);
        Assert.Contains("Рекорд!", h.View(0).GetProperty("result").GetProperty("say").GetString());

        SoloGo(h);
        var len2 = Len(h);
        Assert.True(FinishAt(h, 0, 120_000, seed: 5).Ok);
        h.Tick(1);
        Assert.Equal(cpm, Me(h).GetProperty("best").GetInt32());       // повільніше — рекорд той самий
        Assert.Equal(2, Me(h).GetProperty("runs").GetInt32());
        Assert.False(Me(h).GetProperty("isRecord").GetBoolean());
        Assert.Equal(2, h.Scores.Count);
        Assert.Equal(CpmFor(len2, 120_000), h.Scores[1].Score);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void Solo_flagged_run_counts_a_run_but_neither_scores_nor_sets_a_record()
    {
        var h = Solo();
        SoloGo(h);
        var len = Len(h);
        h.Clock.UtcNow = GoAt(h).AddMilliseconds(len * 150 + 200);
        var r = Finish(h, 0, TyperaceLogs.Robot(len, 150));
        Assert.Contains("не зараховано", r.Message);
        h.Tick(1);
        Assert.Equal("done", Phase(h));
        Assert.Equal(1, Me(h).GetProperty("runs").GetInt32());
        Assert.Equal(JsonValueKind.Null, Me(h).GetProperty("best").ValueKind);
        Assert.False(Me(h).GetProperty("isRecord").GetBoolean());
        Assert.Empty(h.Scores);
        Assert.Empty(h.Awards);
        Assert.Equal("metronome", Racer(h, 0).GetProperty("flag").GetString());
    }

    [Fact]
    public void Solo_stop_returns_to_pick_without_counting_a_run()
    {
        var h = Solo();
        Assert.Equal("Зараз нема чого спиняти", h.Act(0, "stop", new { }).Message);
        Assert.True(h.Act(0, "go", new { }).Ok);
        Assert.True(h.Act(0, "stop", new { }).Ok);          // ще у відліку
        h.Tick(1);
        Assert.Equal("pick", Phase(h));
        SoloGo(h);
        Pos(h, 0, 10);
        Assert.True(h.Act(0, "stop", new { }).Ok);           // посеред заїзду
        h.Tick(1);
        Assert.Equal("pick", Phase(h));
        Assert.Equal(0, Me(h).GetProperty("runs").GetInt32());
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("text").ValueKind);
        Assert.Empty(h.Scores);
    }

    [Fact]
    public void Solo_never_finishes_the_room_and_save_round_trips()
    {
        var h = Solo();
        SoloGo(h, new { length = "short", source = "classic" });
        Assert.True(FinishAt(h, 0, 35_000).Ok);
        h.Tick(1);
        SoloGo(h);
        Assert.True(FinishAt(h, 0, 30_000, seed: 3).Ok);
        h.Tick(1);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Empty(h.Finished);
        var saved = h.Store.States["typerace-solo:оля"];
        using var doc = JsonDocument.Parse(saved);
        Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("runs").GetInt32());
        Assert.Equal(Len(h), doc.RootElement.GetProperty("last").GetProperty("len").GetInt32());

        var again = Solo(store: h.Store);
        Assert.Equal(Views.Text(Me(h).GetProperty("best")), Views.Text(Me(again).GetProperty("best")));
        Assert.Equal(2, Me(again).GetProperty("runs").GetInt32());
        Assert.Equal("classic", again.View(0).GetProperty("opts").GetProperty("source").GetString());
        Assert.Equal("pick", Phase(again));
        var game = (TyperaceSolo)again.Room.Game;
        lock (again.Room.Sync) Assert.Equal(saved, game.Save());
    }

    [Fact]
    public void Solo_idle_minute_returns_to_pick_without_a_run()
    {
        var h = Solo();
        SoloGo(h);
        h.Tick(60_000 / 200);
        Assert.Equal("pick", Phase(h));
        Assert.Equal(0, Me(h).GetProperty("runs").GetInt32());
        Assert.Empty(h.Scores);
    }

    [Fact]
    public void Solo_hard_cap_ends_an_unfinished_run_without_counting_it()
    {
        var h = Solo();
        SoloGo(h, new { length = "short" });
        var len = Len(h);
        Pos(h, 0, len / 2);
        h.Tick((int)((60_000 + 500L * len) / 200) + 1);
        Assert.Equal("done", Phase(h));
        Assert.Equal(0, Me(h).GetProperty("runs").GetInt32());
        Assert.False(h.View(0).GetProperty("result").GetProperty("allDone").GetBoolean());
        Assert.Contains("Не доїхав", h.View(0).GetProperty("result").GetProperty("say").GetString());
        Assert.Empty(h.Scores);
        // і знову можна
        SoloGo(h);
    }

    [Fact]
    public void Solo_with_an_empty_bank_says_so_and_does_not_start()
    {
        var h = Solo(bank: TyperaceBank.From([]));
        Assert.True(h.View(0).GetProperty("noTexts").GetBoolean());
        var r = h.Act(0, "go", new { });
        Assert.False(r.Ok);
        Assert.Equal(Typerace.NoTexts, r.Message);
        Assert.Equal("pick", Phase(h));
    }

    [Fact]
    public void Solo_pos_is_quietly_dropped_outside_the_run()
    {
        var h = Solo();
        h.Input(0, "pos", new { c = 3, e = 0 });
        Assert.Equal("pick", Phase(h));
        SoloGo(h);
        h.Input(0, "pos", new { c = 3, e = 0 });
        Assert.Equal(3, Racer(h, 0).GetProperty("c").GetInt32());
        h.Tick(5);
        // у соло кадрів нема: дивитись нікому, свій трактор клієнт і так знає
        Assert.Empty(h.Outbox.OfType<RoomFrame>());
    }
}
