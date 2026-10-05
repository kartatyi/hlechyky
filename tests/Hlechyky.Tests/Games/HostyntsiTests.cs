using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

[Collection(SerialPerf.Name)]
public class HostyntsiTests
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ігор", "Марта", "Тарас", "Ліна", "Остап", "Яна"];

    static RoomHarness Table(int n, int seed = 1, object? options = null)
    {
        var h = new RoomHarness("hostyntsi", options: options, seed: seed);
        for (var i = 0; i < n; i++) h.Join(Nicks[i]);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static Hostyntsi Game(RoomHarness h) => (Hostyntsi)h.Room.Game;

    static void ToGo(RoomHarness h)
    {
        h.Tick(Hostyntsi.ReadyTicks);
        Assert.Equal(Hostyntsi.PhGo, Game(h).Phase);
    }

    /// <summary>Гостинець рівно навпроти місця: центр його станції.</summary>
    static Hostyntsi.Gift Before(RoomHarness h, int seat, int kind, double dx = 0) =>
        Game(h).Put(kind, Hostyntsi.Center(Game(h).P(seat).Station) + dx);

    static void Unlock(RoomHarness h, int seat) => Game(h).P(seat).Lock = 0;

    static void Grab(RoomHarness h, int seat, int kind, int times)
    {
        for (var i = 0; i < times; i++)
        {
            var x = Before(h, seat, kind);
            Assert.True(h.Act(seat, "grab", new { id = x.Id }).Ok, h.Reply.Message);
            Unlock(h, seat);
        }
    }

    static string Snap(RoomHarness h) => h.View(null).GetRawText();

    static List<string> Said(RoomHarness h) => [.. h.Outbox.OfType<TableSaid>().Select(x => x.Line.Text)];

    // ---------- паспорт і лобі ----------

    [Fact]
    public void Passport_seat_names_and_alone_needs_a_bot()
    {
        var h = new RoomHarness("hostyntsi");
        h.Join("Оля");
        var info = h.Room.Info;
        Assert.Equal(("hostyntsi", GameGroup.Live, 1, 8, 50, StartMode.ByHost, false),
            (info.Id, info.Group, info.MinPlayers, info.MaxPlayers, info.TickMs, info.Start, info.Rated));
        Assert.Contains(info.Options!, o => o.Key == "botlvl");
        Assert.DoesNotContain(info.Options!, o => o.Key is "party" or "bots");
        Assert.Equal("рудий", h.Room.Game.SeatName(1));
        Assert.False(h.Start().Ok);
        Assert.Equal(LiveBots.AloneText, h.Reply.Message);
        Assert.Contains("Хапай", ((IPartyMinigame)h.Room.Game).Howto);
    }

    [Fact]
    public void Lobby_view_shows_stations_of_seated_and_no_gifts()
    {
        var h = new RoomHarness("hostyntsi");
        h.Join("Оля");
        h.Join("Петро");
        var v = h.View(0);
        Assert.Equal("lobby", v.GetProperty("phase").GetString());
        var f = v.GetProperty("frame");
        Assert.Equal(Hostyntsi.PhLobby, f.GetProperty("ph").GetInt32());
        Assert.Equal(0, f.GetProperty("st")[0].GetInt32());
        Assert.Equal(1, f.GetProperty("st")[1].GetInt32());
        Assert.Equal(JsonValueKind.Null, f.GetProperty("st")[2].ValueKind);
        Assert.Equal(0, f.GetProperty("g").GetArrayLength());
        Assert.Equal(3 * Hostyntsi.Spacing, f.GetProperty("len").GetInt32());
        Assert.Equal("Чекаємо на гравців", h.Act(0, "grab").Message);
    }

    // ---------- хапання ----------

    [Fact]
    public void Grab_before_the_chute_rolls_or_unknown_action_changes_nothing()
    {
        var h = Table(2);
        var before = Snap(h);
        Assert.False(h.Act(0, "grab").Ok);
        Assert.False(h.Act(0, "dance").Ok);
        Assert.False(Game(h).Act(5, "grab", default).Ok);       // місце, що не грає
        Assert.Equal(before, Snap(h));
    }

    [Fact]
    public void Grab_takes_the_gift_in_front_and_it_goes_to_the_basket()
    {
        var h = Table(2);
        ToGo(h);
        var g = Before(h, 0, Hostyntsi.Painted);
        Assert.True(h.Act(0, "grab", new { id = g.Id }).Ok);
        var p = Game(h).P(0);
        Assert.Equal([(Hostyntsi.Painted, 3)], p.Basket);
        Assert.Equal(3, p.BasketValue);
        Assert.DoesNotContain(g, Game(h).Gifts);
        Assert.Equal(Hostyntsi.GrabLock, p.Lock);
        // у кадрі — подія хапу, яку бачать усі
        var ev = h.View(null).GetProperty("frame").GetProperty("ev");
        Assert.Contains(ev.EnumerateArray(), e => e[1].GetInt32() == Hostyntsi.EvGrab && e[2].GetInt32() == 0 && e[5].GetInt32() == g.Id);
    }

    [Fact]
    public void Without_id_the_lowest_gift_in_reach_is_taken()
    {
        var h = Table(2);
        ToGo(h);
        var hi = Before(h, 0, Hostyntsi.Jug, -100);
        var lo = Before(h, 0, Hostyntsi.Gold, 100);
        Assert.True(h.Act(0, "grab").Ok);
        Assert.Contains(hi, Game(h).Gifts);
        Assert.DoesNotContain(lo, Game(h).Gifts);
    }

    [Fact]
    public void Someone_elses_gift_is_out_of_reach_and_a_miss_locks_the_hands()
    {
        var h = Table(2);
        ToGo(h);
        var other = Before(h, 1, Hostyntsi.Gold);
        Assert.True(h.Act(0, "grab", new { id = other.Id }).Ok);       // промах — теж хід: руки зайняті
        Assert.Contains(other, Game(h).Gifts);
        Assert.Equal(Hostyntsi.MissLock, Game(h).P(0).Lock);
        Assert.Empty(Game(h).P(0).Basket);
        var mine = Before(h, 0, Hostyntsi.Gold);
        var before = Snap(h);
        Assert.Equal("Руки зайняті", h.Act(0, "grab", new { id = mine.Id }).Message);
        Assert.Equal(before, Snap(h));
        Unlock(h, 0);
        Assert.True(h.Act(0, "grab", new { id = mine.Id }).Ok);
        Assert.Single(Game(h).P(0).Basket);
    }

    [Fact]
    public void Named_gift_that_already_slid_by_is_a_miss_not_the_ember_behind_it()
    {
        var h = Table(2);
        ToGo(h);
        var gone = Before(h, 0, Hostyntsi.Painted, 700);
        var ember = Before(h, 0, Hostyntsi.Ember);
        h.Act(0, "grab", new { id = gone.Id });
        Assert.Contains(ember, Game(h).Gifts);
        Assert.Equal(0, Game(h).P(0).Burn);
    }

    [Fact]
    public void Jitter_margin_counts_a_late_press_but_not_too_late()
    {
        var h = Table(2);
        ToGo(h);
        var v = Game(h).Speed;
        var late = Hostyntsi.Late(v);
        var ok = Before(h, 0, Hostyntsi.Jug, late - 1);
        Assert.True(h.Act(0, "grab", new { id = ok.Id }).Ok);
        Assert.Single(Game(h).P(0).Basket);
        Unlock(h, 0);
        var far = Before(h, 0, Hostyntsi.Jug, late + 1);
        h.Act(0, "grab", new { id = far.Id });
        Assert.Single(Game(h).P(0).Basket);
        Assert.Equal(Hostyntsi.MissLock, Game(h).P(0).Lock);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public void Windows_with_jitter_never_overlap_even_for_the_fastest_gold(int n)
    {
        var vmax = (900 + 150 * (Hostyntsi.Rounds - 1) + 500) * Hostyntsi.GoldMul;
        for (var d = 0.0; d <= Hostyntsi.Length(n); d += 5)
            Assert.True(Enumerable.Range(0, n).Count(st => Hostyntsi.InReach(st, d, vmax)) <= 1, $"d={d}");
    }

    [Fact]
    public void Ember_costs_three_burns_two_seconds_and_takes_no_slot()
    {
        var h = Table(2);
        ToGo(h);
        var e = Before(h, 0, Hostyntsi.Ember);
        h.Act(0, "grab", new { id = e.Id });
        var p = Game(h).P(0);
        Assert.Empty(p.Basket);
        Assert.Equal(-3, p.BasketValue);
        Assert.Equal(Hostyntsi.BurnTicks, p.Burn);
        var g = Before(h, 0, Hostyntsi.Gold);
        var before = Snap(h);
        Assert.Equal("Пече! Ще мить", h.Act(0, "grab", new { id = g.Id }).Message);
        Assert.Equal(before, Snap(h));
        h.Tick(Hostyntsi.BurnTicks);
        Assert.Equal(0, p.Burn);
        var g2 = Before(h, 0, Hostyntsi.Gold);
        Assert.True(h.Act(0, "grab", new { id = g2.Id }).Ok);
        Assert.Equal(2, p.BasketValue);
    }

    [Fact]
    public void Full_basket_grabs_no_more()
    {
        var h = Table(2);
        ToGo(h);
        for (var i = 0; i < Hostyntsi.BasketMax; i++)
        {
            var g = Before(h, 0, Hostyntsi.Pumpkin);
            Assert.True(h.Act(0, "grab", new { id = g.Id }).Ok);
            Unlock(h, 0);
        }
        Assert.Equal(-8, Game(h).P(0).BasketValue);
        var gold = Before(h, 0, Hostyntsi.Gold);
        var before = Snap(h);
        Assert.Contains("Кошик повний", h.Act(0, "grab", new { id = gold.Id }).Message);
        Assert.Equal(before, Snap(h));
    }

    [Fact]
    public void Cat_in_a_bag_is_random_from_minus_three_to_five_and_seeded()
    {
        List<int> Run(int seed)
        {
            var h = Table(2, seed);
            ToGo(h);
            var r = new List<int>();
            for (var i = 0; i < 40; i++)
            {
                var p = Game(h).P(0);
                p.Basket.Clear();
                var c = Before(h, 0, Hostyntsi.Cat);
                h.Act(0, "grab", new { id = c.Id });
                Unlock(h, 0);
                r.Add(p.Basket.Single().Val);
            }
            return r;
        }
        var a = Run(3);
        Assert.All(a, x => Assert.InRange(x, Hostyntsi.CatMin, Hostyntsi.CatMax));
        Assert.True(a.Distinct().Count() >= 5);
        Assert.Equal(a, Run(3));
    }

    // ---------- жолоб ----------

    [Fact]
    public void Gold_rolls_faster_and_the_chute_speeds_up_over_round_and_rounds()
    {
        var h = Table(2);
        ToGo(h);
        var g = Game(h);
        var jug = g.Put(Hostyntsi.Jug, 0);
        var gold = g.Put(Hostyntsi.Gold, 0);
        h.Tick(5);
        Assert.Equal(1.5, gold.D / jug.D, 2);
        var v0 = g.Speed;
        h.Tick(400);
        Assert.True(g.Speed > v0 + 200);
        h.Tick(Hostyntsi.RoundTicks - 405 + Hostyntsi.EndTicks + 1);
        Assert.Equal(2, g.RoundNo);
        Assert.Equal(900 + 150, g.Speed, 0);
    }

    [Fact]
    public void Gifts_spawn_from_the_top_and_fall_off_the_bottom()
    {
        var h = Table(3);
        ToGo(h);
        h.Tick(200);
        var g = Game(h);
        Assert.NotEmpty(g.Gifts);
        Assert.All(g.Gifts, x => Assert.InRange(x.D, 0, Hostyntsi.Length(3)));
        Assert.Contains(g.Gifts, x => x.D < Hostyntsi.Spacing);
        var start = g.Put(Hostyntsi.Jug, Hostyntsi.Length(3) - 10);
        h.Tick();
        Assert.DoesNotContain(start, g.Gifts);
    }

    [Fact]
    public void Stations_rotate_every_round_and_three_players_each_top_once()
    {
        var h = Table(3);
        var g = Game(h);
        var top = new HashSet<int>();
        var seen = new List<int[]>();
        for (var r = 1; r <= 3; r++)
        {
            Assert.Equal(r, g.RoundNo);
            var st = Enumerable.Range(0, 3).Select(s => g.P(s).Station).ToArray();
            Assert.Equal([0, 1, 2], st.Order());
            seen.Add(st);
            top.Add(Array.IndexOf(st, 0));
            if (r < 3) h.Tick(Hostyntsi.ReadyTicks + Hostyntsi.RoundTicks + Hostyntsi.EndTicks);
        }
        Assert.Equal(3, top.Count);
        Assert.NotEqual(seen[0], seen[1]);
    }

    [Fact]
    public void Party_of_eight_rotates_inside_the_round_and_everyone_visits_every_station()
    {
        var h = new PartyHarness("hostyntsi", humans: 0, bots: 8, seed: 4);
        h.Start();
        var g = (Hostyntsi)h.Game;
        Assert.Equal(8, g.Shifts);
        var seen = Enumerable.Range(0, 8).Select(_ => new HashSet<int>()).ToArray();
        h.RunToEnd(_ => { for (var s = 0; s < 8; s++) seen[s].Add(g.P(s).Station); });
        Assert.All(seen, x => Assert.Equal(8, x.Count));
    }

    [Fact]
    public void Ordinary_game_of_six_shifts_twice_a_round_and_a_press_in_flight_counts_for_the_old_station()
    {
        var h = Table(6);
        var g = Game(h);
        Assert.Equal(2, g.Shifts);
        ToGo(h);
        Assert.Equal(g.ShiftTicks, h.View(null).GetProperty("frame").GetProperty("sl").GetInt32());
        h.Tick(g.ShiftTicks - 1);
        var before = g.P(0).Station;
        h.Tick();
        Assert.Equal((before + 5) % 6, g.P(0).Station);          // крок угору, верхній — униз
        Assert.Equal(2, h.View(null).GetProperty("shifts").GetInt32());
        var old = g.Put(Hostyntsi.Painted, Hostyntsi.Center(before));
        Assert.True(h.Act(0, "grab", new { id = old.Id }).Ok);
        Assert.Single(g.P(0).Basket);                             // натиск летів, поки всі зсувались
        h.Tick(Hostyntsi.JitterTicks + 1);
        Unlock(h, 0);
        var late = g.Put(Hostyntsi.Painted, Hostyntsi.Center(before));
        h.Act(0, "grab", new { id = late.Id });
        Assert.Single(g.P(0).Basket);                             // а згодом стара станція — вже не твоя
    }

    [Fact]
    public void Two_players_swap_top_between_rounds()
    {
        var h = Table(2);
        var g = Game(h);
        var first = g.P(0).Station;
        h.Tick(Hostyntsi.ReadyTicks + Hostyntsi.RoundTicks + Hostyntsi.EndTicks);
        Assert.NotEqual(first, g.P(0).Station);
    }

    // ---------- раунди й кінець ----------

    [Fact]
    public void Basket_dumps_into_total_and_three_rounds_end_with_the_best()
    {
        var h = Table(2);
        ToGo(h);
        var g = Game(h);
        var gold = Before(h, 1, Hostyntsi.Gold);
        h.Act(1, "grab", new { id = gold.Id });
        h.Tick(Hostyntsi.RoundTicks);
        Assert.Equal(Hostyntsi.PhEnd, g.Phase);
        Assert.Equal(5, g.P(1).Total);
        Assert.Equal([5], g.P(1).RoundSums);
        Assert.Contains(h.View(null).GetProperty("frame").GetProperty("ev").EnumerateArray(),
            e => e[1].GetInt32() == Hostyntsi.EvDump && e[2].GetInt32() == 1 && e[4].GetInt32() == 5);
        h.Tick(Hostyntsi.EndTicks);
        Assert.Equal(2, g.RoundNo);
        Assert.Empty(g.P(1).Basket);
        h.Tick(2 * (Hostyntsi.ReadyTicks + Hostyntsi.RoundTicks + Hostyntsi.EndTicks));
        Assert.Equal(Hostyntsi.PhOver, g.Phase);
        var fin = h.Finished.Single().Result;
        Assert.Equal([1], fin.Winners);
        Assert.Equal(5, fin.Scores![1]);
        Assert.Equal(0, fin.Scores[0]);
        Assert.StartsWith("Гостинці: Петро 5", fin.Text);
        var v = h.View(0);
        Assert.Equal("over", v.GetProperty("phase").GetString());
        Assert.Equal(1, v.GetProperty("winner").GetInt32());
        Assert.Equal(3, v.GetProperty("sums")[1].GetArrayLength());
        // після кінця — ні хапів, ні тиків
        Assert.False(h.Act(0, "grab").Ok);
        h.Tick(100);
        Assert.Single(h.Finished);
    }

    [Fact]
    public void All_equal_is_a_draw()
    {
        var h = Table(2);
        h.Tick(3 * (Hostyntsi.ReadyTicks + Hostyntsi.RoundTicks + Hostyntsi.EndTicks));
        var fin = h.Finished.Single().Result;
        Assert.Empty(fin.Winners);
        Assert.Contains("нічия", fin.Text);
    }

    [Fact]
    public void Glek_speaks_at_start_and_end_but_not_every_tick()
    {
        var h = Table(2);
        Assert.Contains(Said(h), o => o.Contains("жадібність"));
        ToGo(h);
        var gold = Before(h, 0, Hostyntsi.Gold);
        h.Act(0, "grab", new { id = gold.Id });
        h.Tick(3 * (Hostyntsi.ReadyTicks + Hostyntsi.RoundTicks + Hostyntsi.EndTicks));
        Assert.Single(h.Finished);
        Assert.Contains(Said(h), s => s.Contains("найповніший кошик"));
        Assert.InRange(Said(h).Count, 2, 4);
    }

    [Fact]
    public void Achievements_gold_basket_lucky_cat_and_no_burns_in_a_human_game()
    {
        var h = Table(2);
        ToGo(h);
        var g = Game(h);
        Grab(h, 0, Hostyntsi.Gold, 3);
        Assert.Contains(Said(h), s => s.Contains("третій золотий"));
        // кіт: перебираємо, поки не дасть +5 (сід фіксований, генератор столу)
        for (var i = 0; i < 200 && !g.P(1).Cat5; i++)
        {
            g.P(1).Basket.Clear();
            var c = Before(h, 1, Hostyntsi.Cat);
            h.Act(1, "grab", new { id = c.Id });
            Unlock(h, 1);
        }
        Assert.True(g.P(1).Cat5);
        Grab(h, 0, Hostyntsi.Jug, 5);                          // 3 золоті + 5 глеків — кошик повний
        h.Tick(Hostyntsi.RoundTicks + Hostyntsi.EndTicks + Hostyntsi.ReadyTicks);
        Assert.Equal(2, g.RoundNo);
        Grab(h, 0, Hostyntsi.Jug, 2);                          // разом 10 хапів без жару
        var e = Before(h, 1, Hostyntsi.Ember);
        h.Act(1, "grab", new { id = e.Id });
        h.Tick(2 * (Hostyntsi.ReadyTicks + Hostyntsi.RoundTicks + Hostyntsi.EndTicks));
        Assert.Single(h.Finished);
        var aw = h.Awards.Select(a => (a.Nick, a.Reason)).ToList();
        Assert.Contains(("Оля", "ach:hostyntsi-gold3"), aw);
        Assert.Contains(("Оля", "ach:hostyntsi-noburn"), aw);
        Assert.Contains(("Петро", "ach:hostyntsi-cat5"), aw);
        Assert.DoesNotContain(("Петро", "ach:hostyntsi-noburn"), aw);
        Assert.All(new[] { "hostyntsi-noburn", "hostyntsi-cat5", "hostyntsi-gold3" }, k => Assert.NotNull(AchievementCatalog.Get(k)));
    }

    // ---------- вид, кадр, детермінізм ----------

    [Fact]
    public void View_and_frame_shape_is_the_same_for_everyone()
    {
        var h = Table(3);
        ToGo(h);
        h.Tick(100);
        var v0 = h.View(0);
        Assert.Equal(h.View(null).GetRawText(), v0.GetRawText());     // таємниць нема: кіт розкривається лише при хапі
        foreach (var k in new[] { "phase", "round", "rounds", "skin", "basketMax", "spacing", "halfWin", "jitterMs", "tickMs",
            "roundTicks", "values", "cat", "goldMul", "totals", "sums", "left", "winners", "botOffer", "bot", "frame" })
            Assert.True(v0.TryGetProperty(k, out _), k);
        var f = h.Room.Game.Frame()!;
        var fj = Views.Json(f);
        foreach (var k in new[] { "t", "ph", "r", "left", "v", "len", "st", "g", "p", "b", "ev" })
            Assert.True(fj.TryGetProperty(k, out _), k);
        Assert.Equal(8, fj.GetProperty("st").GetArrayLength());
        Assert.Equal(5, fj.GetProperty("p")[0].GetArrayLength());
        Assert.All(fj.GetProperty("g").EnumerateArray(), g => Assert.Equal(4, g.GetArrayLength()));
        Assert.Equal(Hostyntsi.RoundTicks - 100, fj.GetProperty("left").GetInt32());
    }

    [Fact]
    public void Same_seed_same_game()
    {
        string Run()
        {
            var h = new RoomHarness("hostyntsi", options: new { botlvl = "hard" }, seed: 9);
            h.Join("Оля");
            h.Act(0, LiveBots.Toggle, new { on = true });
            h.Start();
            h.Tick(700);
            return Snap(h);
        }
        Assert.Equal(Run(), Run());
    }

    [Fact, Trait("Category", "Perf")]
    public void Thousand_ticks_with_eight_on_the_chute_under_two_seconds()
    {
        // Звичайна партія на вісьмох (кадр щотику) і вечірка з вісьмома сильними ботами від початку до кінця.
        var h = Table(8);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            h.Room.Game.Tick();
            Views.Json(h.Room.Game.Frame()!);
        }
        var p = new PartyHarness("hostyntsi", humans: 0, bots: 8, level: LiveBots.Level.Hard, seed: 2);
        p.Start();
        for (var i = 0; i < 1500 && !p.Host.Over; i++) { p.TickSub(); p.Game.Frame(); }
        sw.Stop();
        Assert.True(p.Host.Over);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} мс");
    }

    [Fact]
    public void Rematch_starts_a_fresh_game()
    {
        var h = Table(2);
        ToGo(h);
        var gold = Before(h, 0, Hostyntsi.Gold);
        h.Act(0, "grab", new { id = gold.Id });
        h.Tick(3 * (Hostyntsi.ReadyTicks + Hostyntsi.RoundTicks + Hostyntsi.EndTicks));
        Assert.Single(h.Finished);
        Assert.True(h.Rematch().Ok, h.Reply.Message);
        var g = Game(h);
        Assert.Equal(Hostyntsi.PhReady, g.Phase);
        Assert.Equal(1, g.RoundNo);
        Assert.All(Enumerable.Range(0, 2), s => Assert.Equal(0, g.P(s).Total));
        Assert.Equal(JsonValueKind.Array, h.View(0).GetProperty("series").GetProperty("wins").ValueKind);
    }

    // ---------- вихід посеред партії ----------

    [Fact]
    public void Leaving_mid_game_three_play_on_two_end()
    {
        var h = Table(3);
        ToGo(h);
        h.Leave("Ігор");
        Assert.Empty(h.Finished);
        Assert.True(Game(h).P(2).Left);
        Assert.False(Game(h).Act(2, "grab", default).Ok);
        h.Leave("Петро");
        var fin = h.Finished.Single().Result;
        Assert.Equal([0], fin.Winners);
    }

    // ---------- Миколаїв скін ----------

    [Fact]
    public void Saint_Nicholas_skin_from_the_first_to_the_nineteenth_of_December_Kyiv()
    {
        var h = new RoomHarness("hostyntsi");
        h.Clock.UtcNow = new DateTimeOffset(2026, 12, 5, 10, 0, 0, TimeSpan.Zero);
        h.Join("Оля");
        h.Join("Петро");
        Assert.Equal("nik", h.View(0).GetProperty("skin").GetString());
        h.Start();
        Assert.True(Game(h).Nik);
        Assert.Equal("різочка", Game(h).KindName(Hostyntsi.Ember));
        Assert.Contains(Said(h), o => o.Contains("Миколай"));
        Assert.True(Hostyntsi.NikDay(new DateTimeOffset(2026, 11, 30, 22, 30, 0, TimeSpan.Zero)));   // у Києві вже 1 грудня
        Assert.False(Hostyntsi.NikDay(new DateTimeOffset(2026, 12, 19, 22, 30, 0, TimeSpan.Zero)));  // а тут уже 20-те
        Assert.False(Hostyntsi.NikDay(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero)));
        var plain = Table(2);
        Assert.Equal("", plain.View(0).GetProperty("skin").GetString());
        Assert.Equal("жар", Game(plain).KindName(Hostyntsi.Ember));
    }

    // ---------- соло з ботом ----------

    static RoomHarness Solo(string lvl, int seed)
    {
        var h = new RoomHarness("hostyntsi", options: new { botlvl = lvl }, seed: seed);
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    [Fact]
    public void Solo_with_bots_two_bots_play_and_win_without_rewards()
    {
        var h = Solo("normal", 4);
        var g = Game(h);
        Assert.Equal([1, 2], g.Bots);
        Assert.True(g.BotGame);
        Assert.StartsWith(LiveBots.Name, h.Room.Game.SeatBot(1));
        Assert.Equal(2, h.View(0).GetProperty("bot").GetArrayLength());
        h.Tick(3 * (Hostyntsi.ReadyTicks + Hostyntsi.RoundTicks + Hostyntsi.EndTicks));
        Assert.True(g.P(1).Total > 0 && g.P(2).Total > 0, $"{g.P(1).Total} {g.P(2).Total}");
        Assert.True(g.P(1).Grabs > 5);
        var fin = h.Finished.Single().Result;
        Assert.Empty(fin.Winners);                         // людина стояла — переміг бот
        Assert.StartsWith("🤖", fin.Verdict);
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void Bot_never_overfills_and_hard_bot_brings_a_good_basket()
    {
        var h = Solo("hard", 6);
        var g = Game(h);
        h.Tick(Hostyntsi.ReadyTicks + Hostyntsi.RoundTicks - 1);
        Assert.All(g.Bots, b => Assert.InRange(g.P(b).Basket.Count, 0, Hostyntsi.BasketMax));
        Assert.All(g.Bots, b => Assert.True(g.P(b).BasketValue >= 5, $"бот {b}: {g.P(b).BasketValue}"));
    }

    [Fact]
    public void Hard_bot_scores_clearly_more_than_easy_bot()
    {
        // Той самий жолоб: бот-сильний і бот-легкий у вечірці на двох, місця й сіди перебираємо.
        long hard = 0, easy = 0;
        for (var seed = 1; seed <= 12; seed++)
        {
            foreach (var lvl in new[] { LiveBots.Level.Hard, LiveBots.Level.Easy })
            {
                var h = new PartyHarness("hostyntsi", humans: 0, bots: 3, level: lvl, seed: seed);
                h.Start();
                var r = h.RunToEnd()!;
                var sum = r.Scores.Values.Sum();
                if (lvl == LiveBots.Level.Hard) hard += sum; else easy += sum;
            }
        }
        Assert.True(hard > easy * 1.3, $"сильні {hard}, легкі {easy}");
    }

    // ---------- режим вечірки ----------

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    public void Party_with_only_bots_finishes_before_cap_with_scores_for_all(int bots)
    {
        var h = new PartyHarness("hostyntsi", humans: 0, bots: bots, seed: 7);
        h.Start();
        var g = (Hostyntsi)h.Game;
        Assert.True(g.Party);
        Assert.Equal(1, g.RoundsTotal);
        var r = h.RunToEnd();
        Assert.NotNull(r);
        Assert.Equal(MinigameEnd.Finished, r!.How);
        Assert.Equal(bots, r.Scores.Count);
        Assert.Equal(bots, r.Places.Length);
        Assert.NotEmpty(r.Winners);
        Assert.Contains(r.Scores.Values, v => v > 0);
        Assert.True(h.Clock.UtcNow - h.StartedAt <= TimeSpan.FromMilliseconds(h.Host.CapMs));
        Assert.Equal(0, h.Ctx.Muted);
        Assert.Equal(0, h.Parent.Leaked);
        Assert.Equal(0, h.Parent.Finishes);
        Assert.True(h.Parent.Says.Count <= 1, string.Join(" | ", h.Parent.Says));
    }

    [Fact]
    public void Party_idle_humans_do_not_stall_and_score_zero()
    {
        var h = new PartyHarness("hostyntsi", humans: 2, bots: 2, level: LiveBots.Level.Normal, seed: 3);
        h.Start();
        Assert.Equal([2, 3], ((Hostyntsi)h.Game).Bots);
        var r = h.RunToEnd()!;
        Assert.Equal(MinigameEnd.Finished, r.How);
        Assert.Equal(0, r.Scores[0]);
        Assert.Equal(0, r.Scores[1]);
        Assert.All(r.Winners, w => Assert.True(w >= 2));
    }

    [Fact]
    public void Party_human_who_plays_well_is_on_top()
    {
        var wins = 0;
        var log = "";
        for (var seed = 1; seed <= 5; seed++)
        {
            var h = new PartyHarness("hostyntsi", humans: 1, bots: 3, level: LiveBots.Level.Easy, seed: seed);
            h.Start();
            var g = (Hostyntsi)h.Game;
            var r = h.RunToEnd(x =>
            {
                var me = g.P(0);
                if (g.Phase != Hostyntsi.PhGo || me.Lock > 0 || me.Burn > 0 || me.Basket.Count >= Hostyntsi.BasketMax) return;
                var c = Hostyntsi.Center(me.Station);
                var slots = Hostyntsi.BasketMax - me.Basket.Count;
                var timeLeft = (Hostyntsi.RoundTicks - g.Rt) * Hostyntsi.TickMs / 1000.0;
                foreach (var gift in g.Gifts.ToArray())
                {
                    if (gift.D < c - Hostyntsi.HalfWin || gift.D > c + Hostyntsi.HalfWin) continue;
                    var good = gift.Kind is Hostyntsi.Gold or Hostyntsi.Painted
                        || (gift.Kind == Hostyntsi.Jug && slots * 4 > timeLeft);
                    if (good) { x.Act(0, "grab", new { id = gift.Id }); break; }
                }
            })!;
            Assert.Equal(MinigameEnd.Finished, r.How);
            if (r.Places[0] == 1) wins++;
            log += $" [{g.P(0).Station}: {string.Join(",", r.Scores.OrderBy(k => k.Key).Select(k => k.Value))}]";
        }
        Assert.True(wins >= 4, $"людина вгорі {wins} з 5{log}");
    }

    [Fact]
    public void Party_scores_midway_cover_every_seat_and_include_the_basket()
    {
        var h = new PartyHarness("hostyntsi", humans: 3, bots: 1, seed: 5);
        h.Start();
        var g = (Hostyntsi)h.Game;
        h.TickSub(Hostyntsi.ReadyTicks + 1);
        var gift = g.Put(Hostyntsi.Gold, Hostyntsi.Center(g.P(0).Station));
        Assert.True(h.Act(0, "grab", new { id = gift.Id }).Ok);
        var s = ((IPartyMinigame)g).PartyScores();
        Assert.Equal(4, s.Count);
        Assert.Equal(5, s[0]);
        var r = h.RunToEnd()!;
        Assert.Equal(5, r.Scores[0]);       // кошик висипано рівно раз
    }

    [Fact]
    public void Party_is_deterministic_by_seed()
    {
        string Run()
        {
            var h = new PartyHarness("hostyntsi", humans: 0, bots: 6, level: LiveBots.Level.Hard, seed: 11);
            h.Start();
            var r = h.RunToEnd()!;
            return string.Join(",", r.Scores.OrderBy(kv => kv.Key).Select(kv => kv.Value)) + "|" + h.Clock.UtcNow.ToUnixTimeMilliseconds();
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Ordinary_table_ignores_party_keys()
    {
        var room = new RoomHarness("hostyntsi", options: new { party = "1", bots = "1,2" });
        room.Join("Оля");
        Assert.False(((Hostyntsi)room.Room.Game).Party);
        Assert.Equal(Hostyntsi.Rounds, ((Hostyntsi)room.Room.Game).RoundsTotal);
    }
}
