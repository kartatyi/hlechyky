using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Шаткуй!» v2 (контракт _tools/shatkuy-v2.md): гарбуз на два удари, часник із зубчиками, перець, що пече, швидка
/// картопля, точний удар, множник серії, гарбуз-велетень Класики й «Шаткуй разом» на кількох (той самий сід, свій мороз).
/// </summary>
public class ShatkuyV2Tests
{
    static int _w = 500_000;

    static List<ShatkuyFruit> Air(ShatkuyGame g) => Air(g.Field, g.GameNow);

    static List<ShatkuyFruit> Air(ShatkuyField f, int now) =>
        [.. f.Fruits.Where(x => !f.IsCut(x.Id) && x.At <= now && now <= x.At + x.D).OrderBy(x => x.Id)];

    /// <summary>Бот, що ріже кожен плід своїм змахом, крім тих, що не пройшли фільтр (гарбуз — двома).</summary>
    static void Bot(RoomHarness h, ShatkuyGame g, int ticks, Func<ShatkuyFruit, bool>? cut = null)
    {
        for (var i = 0; i < ticks && g.Phase == ShatkuyGame.PhGo; i++)
        {
            var c = Air(g).Where(f => !f.Glek && (cut?.Invoke(f) ?? true)).Select(f => new[] { f.Id, ++_w }).ToArray();
            if (c.Length > 0) h.Input(0, "cut", new { c });
            h.Tick();
        }
    }

    static (RoomHarness H, ShatkuyGame G) Go(string id, int seed = 5)
    {
        var h = new RoomHarness(id, seed: seed);
        Assert.True(h.Solo("Мар'яна").Ok);
        var g = (ShatkuyGame)h.Room.Game;
        Assert.True(h.Act(0, "go").Ok);
        h.Clock.AdvanceMs(ShatkuyCore.LeadMs);
        return (h, g);
    }

    static ShatkuyFruit WaitFor(RoomHarness h, ShatkuyGame g, Func<ShatkuyFruit, bool> what, int fresh = 600)
    {
        for (var i = 0; i < 6_000; i++)
        {
            var f = Air(g).FirstOrDefault(x => what(x) && g.GameNow - x.At < fresh);
            if (f is not null) return f;
            Bot(h, g, 1, x => !what(x));
        }
        throw new InvalidOperationException("не дочекались");
    }

    static JsonElement FruitInView(RoomHarness h, int id) =>
        h.View(0).GetProperty("fruits").EnumerateArray().Single(f => f.GetProperty("id").GetInt32() == id);

    // ---------- розклад ----------

    [Fact]
    public void New_veggies_come_from_the_third_wave_potato_is_fast_and_zen_has_no_pepper()
    {
        var fair = new ShatkuyPlan(ShatkuyCore.Fair, 11);
        fair.Until(60_000);
        var plain = fair.Fruits.Where(f => ShatkuyCore.IsPlain(f.K) || ShatkuyCore.Veggies.Contains(f.K)).ToList();
        var veg = plain.Where(f => ShatkuyCore.Veggies.Contains(f.K)).ToList();
        Assert.DoesNotContain(veg, f => f.At < 3_000);                       // перші дві хвилі — лише старі
        Assert.InRange(veg.Count / (double)plain.Count, 0.12, 0.38);           // ~25 %
        Assert.Contains(veg, f => f.K == ShatkuyCore.Pepper);
        Assert.True(veg.Count(f => f.K == ShatkuyCore.Pepper) < veg.Count(f => f.K == ShatkuyCore.Potato));
        // картопля летить ×1,35 швидше: 2600–3200 × 0,75
        Assert.All(veg.Where(f => f.K == ShatkuyCore.Potato), f => Assert.InRange(f.D, 1_950, 2_400));
        Assert.All(plain.Where(f => f.K != ShatkuyCore.Potato), f => Assert.True(f.D >= 2_600));

        var zen = new ShatkuyPlan(ShatkuyCore.Zen, 11);
        zen.Until(90_000);
        Assert.DoesNotContain(zen.Fruits, f => f.K == ShatkuyCore.Pepper);
        Assert.Contains(zen.Fruits, f => f.K == ShatkuyCore.Garlic);
    }

    [Fact]
    public void Giant_takes_the_next_wave_and_nothing_else_flies_meanwhile()
    {
        var a = new ShatkuyPlan(ShatkuyCore.Classic, 3);
        a.Until(10_000);
        a.QueueGiant();
        a.Until(40_000);
        var giant = Assert.Single(a.Fruits, f => f.K == ShatkuyCore.Giant);
        Assert.True(giant.Big && giant.D == ShatkuyCore.BigFlyMs && giant.At > 10_000);
        Assert.DoesNotContain(a.Fruits, f => f != giant && f.At > giant.At - 50 && f.At < giant.At + giant.D);
        // таймові режими велетня не беруть
        var fair = new ShatkuyPlan(ShatkuyCore.Fair, 3);
        fair.QueueGiant();
        fair.Until(60_000);
        Assert.DoesNotContain(fair.Fruits, f => f.K == ShatkuyCore.Giant);
    }

    [Fact]
    public void Cloves_fly_up_from_the_cut_point_and_are_the_same_for_the_same_cut()
    {
        var garlic = new ShatkuyFruit(7, ShatkuyCore.Garlic, 1_000, 2_800, 0.3, 0.6, 0.8, 1);
        foreach (var g in new[] { 1_400, 2_400, 3_500 })
        {
            var cloves = ShatkuyCore.ClovesOf(42, garlic, g);
            Assert.Equal(ShatkuyCore.Cloves, cloves.Count);
            Assert.Equal(cloves, ShatkuyCore.ClovesOf(42, garlic, g));
            var s = (g - garlic.At) / (double)garlic.D;
            var x = garlic.X0 + (garlic.X1 - garlic.X0) * s;
            var y = garlic.H * 4 * s * (1 - s);
            foreach (var c in cloves)
            {
                Assert.Equal(ShatkuyCore.Clove, c.K);
                var cs = (g - c.At) / (double)c.D;
                Assert.InRange(cs, 0, 0.5);                                     // ще піднімається
                Assert.Equal(x, c.X0 + (c.X1 - c.X0) * cs, 2);
                Assert.Equal(y, c.H * 4 * cs * (1 - cs), 2);
            }
            Assert.Equal(3, cloves.Select(c => c.X1).Distinct().Count());     // віялом
        }
        Assert.NotEqual(ShatkuyCore.ClovesOf(42, garlic, 2_000), ShatkuyCore.ClovesOf(43, garlic, 2_000));
    }

    [Fact]
    public void Series_multiplier_steps()
    {
        Assert.Equal([1, 1, 2, 2, 3, 3, 4, 4], new[] { 0, 9, 10, 24, 25, 49, 50, 500 }.Select(ShatkuyCore.Mult));
    }

    // ---------- партія ----------

    [Fact]
    public void Pumpkin_cracks_first_and_needs_another_swipe_after_120_ms()
    {
        var (h, g) = Go("shatkuy-zen");
        var p = WaitFor(h, g, f => f.K == ShatkuyCore.Pumpkin, 300);
        var before = g.Score;
        var w = ++_w;
        Assert.True(h.Act(0, "cut", new { c = new[] { new[] { p.Id, w } } }).Ok);   // тріщина
        Assert.Equal(before, g.Score);
        Assert.False(g.IsCut(p.Id));
        Assert.True(FruitInView(h, p.Id).GetProperty("cr").GetBoolean());
        Assert.False(h.Act(0, "cut", new { c = new[] { new[] { p.Id, w } } }).Ok);        // той самий змах — ні
        Assert.False(h.Act(0, "cut", new { c = new[] { new[] { p.Id, ++_w } } }).Ok);     // зарано
        h.Clock.AdvanceMs(ShatkuyCore.CrackGapMs);
        Assert.True(h.Act(0, "cut", new { c = new[] { new[] { p.Id, ++_w } } }).Ok);
        Assert.Equal(before + 3, g.Score);
        Assert.True(g.IsCut(p.Id));
        Assert.False(FruitInView(h, p.Id).TryGetProperty("cr", out _));
    }

    [Fact]
    public void Garlic_drops_three_cloves_and_a_lost_clove_is_no_miss()
    {
        var (h, g) = Go("shatkuy");
        var garlic = WaitFor(h, g, f => f.K == ShatkuyCore.Garlic);
        var before = g.Score;
        Assert.True(h.Act(0, "cut", new { c = new[] { new[] { garlic.Id, ++_w } } }).Ok);
        Assert.Equal(before + 1 * g.Field.Mult, g.Score);
        var cloves = g.Fruits.Where(f => f.K == ShatkuyCore.Clove).ToList();
        Assert.Equal([ShatkuyCore.CloveId(garlic.Id, 0), ShatkuyCore.CloveId(garlic.Id, 1), ShatkuyCore.CloveId(garlic.Id, 2)], cloves.Select(c => c.Id).Order());
        h.Tick();
        var v = FruitInView(h, cloves[0].Id);
        Assert.Equal("v", v.GetProperty("k").GetString());
        Assert.Equal(garlic.Id, v.GetProperty("from").GetInt32());
        // один зубчик ріжемо (+1), два пропускаємо — ні життя, ні серії
        var mid = g.Score;
        var mult = g.Field.Mult;
        Assert.True(h.Act(0, "cut", new { c = new[] { new[] { cloves[0].Id, ++_w } } }).Ok);
        Assert.Equal(mid + ShatkuyCore.Mult(g.Streak), g.Score);
        Assert.True(g.Streak > 0);
        var streak = g.Streak;
        Bot(h, g, 3_000 / ShatkuyGame.TickMs, f => f.K != ShatkuyCore.Clove);
        Assert.Equal(0, g.Misses);
        Assert.True(g.Streak >= streak, $"серія {g.Streak} < {streak}");
        Assert.True(mult >= 1);
    }

    [Fact]
    public void A_missed_pepper_burns_and_breaks_the_streak_but_keeps_the_lives()
    {
        var (h, g) = Go("shatkuy", seed: 9);
        WaitFor(h, g, f => f.K == ShatkuyCore.Pepper);
        Assert.True(g.Streak > 0);
        Assert.Empty(g.Field.Burn);
        for (var i = 0; i < 200 && g.Field.Burn.Count == 0; i++) Bot(h, g, 1, f => f.K != ShatkuyCore.Pepper);
        Assert.Single(g.Field.Burn);
        Assert.Equal(ShatkuyCore.BurnMs, g.Field.Burn[0].E - g.Field.Burn[0].S);
        Assert.Equal(0, g.Misses);
        Assert.Equal(ShatkuyGame.PhGo, g.Phase);
        var burn = h.View(0).GetProperty("burn");
        Assert.Equal(1, burn.GetArrayLength());
        Assert.True(g.BestStreak > 0);
        // одразу після опіку серія з нуля (бот міг устигнути розрізати пару плодів того ж тику)
        Assert.True(g.Streak <= 3, $"серія {g.Streak}");
    }

    [Fact]
    public void Precise_flag_adds_one_only_to_a_fresh_plain_cut()
    {
        var (h, g) = Go("shatkuy-zen");
        var a = WaitFor(h, g, f => ShatkuyCore.Price(f.K) == 1 && f.K != ShatkuyCore.Pumpkin && f.K != ShatkuyCore.Garlic);
        var before = g.Score;
        Assert.True(h.Act(0, "cut", new { c = new[] { new[] { a.Id, ++_w, 1 } } }).Ok);
        Assert.Equal(before + 2, g.Score);
        Assert.Equal(1, g.Precise);
        // уже розрізаний з прапорцем — нічого
        Assert.False(h.Act(0, "cut", new { c = new[] { new[] { a.Id, ++_w, 1 } } }).Ok);
        Assert.Equal(1, g.Precise);
        Assert.Equal(1, h.View(0).GetProperty("precise").GetInt32());
    }

    [Fact]
    public void Streak_multiplies_only_the_base_price_and_a_miss_breaks_it()
    {
        var (h, g) = Go("shatkuy");
        Bot(h, g, 40_000 / ShatkuyGame.TickMs);
        Assert.Equal(0, g.Misses);
        Assert.True(g.BestStreak >= 25, $"серія {g.BestStreak}");
        Assert.True(g.Score > g.Field.Cuts * 2, $"очок {g.Score} за {g.Field.Cuts} розрізів — множник не множить");
        var v = h.View(0);
        Assert.Equal(g.Streak, v.GetProperty("streak").GetInt32());
        Assert.Equal(ShatkuyCore.Mult(g.Streak), v.GetProperty("mult").GetInt32());
        Assert.Equal(g.BestStreak, v.GetProperty("bestStreak").GetInt32());
        // не ріжемо — перший промах рве серію
        for (var i = 0; i < 400 && g.Misses == 0; i++) h.Tick();
        Assert.Equal(1, g.Misses);
        Assert.Equal(0, g.Streak);
        Assert.Equal(1, h.View(0).GetProperty("mult").GetInt32());
    }

    [Fact]
    public void Zen_has_no_multiplier()
    {
        var (h, g) = Go("shatkuy-zen");
        Bot(h, g, 30_000 / ShatkuyGame.TickMs);
        Assert.True(g.BestStreak >= 10);
        Assert.Equal(1, g.Field.Mult);
        Assert.Equal(1, h.View(0).GetProperty("mult").GetInt32());
        Assert.Empty(h.View(0).GetProperty("k").GetProperty("series").EnumerateArray());
    }

    [Fact]
    public void Classic_sends_a_giant_pumpkin_every_hundred_and_its_fall_is_no_miss()
    {
        var (h, g) = Go("shatkuy");
        var w = 0;
        for (var i = 0; i < 20_000 && g.Phase == ShatkuyGame.PhGo && !g.Fruits.Any(f => f.K == ShatkuyCore.Giant && f.At + f.D + 1_000 < g.GameNow); i++)
        {
            var air = Air(g);
            var c = air.Where(f => !f.Glek && !f.Big).Select(f => new[] { f.Id, ++_w }).ToList();
            if (air.FirstOrDefault(f => f.K == ShatkuyCore.Giant) is { } giant && ++w % 4 == 0) c.Add([giant.Id, ++_w]);
            if (c.Count > 0) h.Input(0, "cut", new { c });
            h.Tick();
        }
        Assert.Equal(ShatkuyGame.PhGo, g.Phase);
        var first = g.Fruits.Where(f => f.K == ShatkuyCore.Giant).OrderBy(f => f.At).First();
        Assert.True(g.Score >= ShatkuyCore.GiantEvery);
        Assert.True(g.BigHits >= 5, $"ударів {g.BigHits}");
        Assert.Equal(0, g.Misses);
        Assert.DoesNotContain(g.Fruits, f => f.Id < ShatkuyCore.CloveBase && f != first && f.At > first.At - 50 && f.At < first.At + first.D);
        // кавун Ярмарку й велетень — одна механіка: удар раз на змах, стеля BigMaxHits
        Assert.True(g.Field.KavunHits == 0);
    }

    [Fact]
    public void New_achievements_and_the_old_view_fields_stay()
    {
        Assert.NotNull(Hlechyky.Games.Economy.AchievementCatalog.Get(ShatkuyGame.AchStreak));
        Assert.NotNull(Hlechyky.Games.Economy.AchievementCatalog.Get(ShatkuyGame.AchFire));
        var (h, g) = Go("shatkuy-fair");
        Bot(h, g, 200);
        var v = h.View(0);
        foreach (var key in new[] { "mode", "phase", "now", "t0", "len", "lives", "misses", "score", "best", "games", "combo", "gleks", "big", "cuts", "frz", "dbl", "fruits", "k" })
            Assert.True(v.TryGetProperty(key, out _), key);
        foreach (var key in new[] { "lag", "look", "frost", "bigMax", "glek", "crackGap", "burn", "series", "r", "pts" })
            Assert.True(v.GetProperty("k").TryGetProperty(key, out _), key);
        var f = v.GetProperty("fruits").EnumerateArray().First();
        foreach (var key in new[] { "id", "k", "at", "d", "x0", "x1", "h", "sp", "c", "m" }) Assert.True(f.TryGetProperty(key, out _), key);
        for (var i = 0; i < 4_000 && g.Phase == ShatkuyGame.PhGo; i++) Bot(h, g, 1);
        var end = h.View(0).GetProperty("end");
        Assert.True(end.GetProperty("streak").GetInt32() > 0);
        Assert.True(end.TryGetProperty("precise", out _));
        Assert.True(end.TryGetProperty("rec", out _));
    }

    [Fact]
    public void Streak_of_fifty_gives_the_silver_sickle_achievement()
    {
        var (h, g) = Go("shatkuy-zen");
        for (var i = 0; i < 4_000 && g.Phase == ShatkuyGame.PhGo; i++) Bot(h, g, 1);
        Assert.True(g.BestStreak >= ShatkuyGame.StreakAch, $"серія {g.BestStreak}");
        Assert.Contains(h.Awards, a => a.Reason == "ach:" + ShatkuyGame.AchStreak);
    }

    // ---------- Шаткуй разом ----------

    static RoomHarness Party(int people, int seed = 3)
    {
        var h = new RoomHarness("shatkuy-party", seed: seed);
        for (var i = 0; i < people; i++) Assert.True(h.Join($"шаткар{i}").Ok);
        return h;
    }

    static ShatkuyParty P(RoomHarness h) => (ShatkuyParty)h.Room.Game;

    /// <summary>Кожне місце ріже все своє (крім глеків і тих, кого <paramref name="skip"/> не пускає).</summary>
    static void PlayParty(RoomHarness h, int ticks, Func<int, ShatkuyFruit, bool>? skip = null)
    {
        var p = P(h);
        for (var i = 0; i < ticks && !p.Over; i++)
        {
            for (var s = 0; s < ShatkuyParty.Seats; s++)
            {
                if (p.FieldOf(s) is not { } f || f.Over || p.Bots.Contains(s)) continue;
                var c = Air(f, f.G(p.T)).Where(x => !x.Glek && !(skip?.Invoke(s, x) ?? false)).Select(x => new[] { x.Id, ++_w }).ToArray();
                if (c.Length > 0) h.Input(s, "cut", new { c });
            }
            h.Tick();
        }
    }

    [Fact]
    public void Party_everyone_gets_the_same_cart_and_frost_slows_only_its_cutter()
    {
        var h = Party(3);
        Assert.Equal("lobby", h.View(0).GetProperty("phase").GetString());
        Assert.False(h.Act(0, "cut", new { c = new[] { new[] { 1, 1 } } }).Ok);
        Assert.True(h.Start().Ok);
        var p = P(h);
        var f = Enumerable.Range(0, 3).Select(s => p.FieldOf(s)!).ToArray();
        Assert.All(f, x => Assert.Equal(p.Seed, x.Seed));
        Assert.Null(p.FieldOf(3));
        h.Clock.AdvanceMs(ShatkuyParty.LeadMs);
        PlayParty(h, 400, (s, x) => s != 0 && x.K == ShatkuyCore.Frost);   // мороз ріже лише місце 0
        var main = f.Select(x => x.Fruits.Where(y => y.Id < 10_000).OrderBy(y => y.Id).Take(25).ToList()).ToArray();
        Assert.Equal(main[0], main[1]);
        Assert.Equal(main[1], main[2]);
        if (f[0].Frost.Count > 0) Assert.True(f[0].G(p.T) < f[1].G(p.T));
        Assert.Empty(f[1].Frost);

        // вид місця: своє поле й суперники
        var v = h.View(1);
        Assert.Equal("party", v.GetProperty("mode").GetString());
        Assert.Equal("go", v.GetProperty("phase").GetString());
        Assert.NotEmpty(v.GetProperty("fruits").EnumerateArray());
        var party = v.GetProperty("party");
        Assert.Equal(1, party.GetProperty("me").GetInt32());
        var rivals = party.GetProperty("rivals").EnumerateArray().ToList();
        Assert.Equal(3, rivals.Count);
        Assert.Equal(f[2].Score, rivals[2].GetProperty("score").GetInt32());
        Assert.Equal("шаткар2", rivals[2].GetProperty("nick").GetString());
        foreach (var key in new[] { "streak", "mult", "combo", "done", "left" }) Assert.True(rivals[0].TryGetProperty(key, out _), key);
        // глядач — без плодів
        Assert.Empty(h.View(null).GetProperty("fruits").EnumerateArray());
    }

    [Fact]
    public void Party_ends_when_every_minute_is_over_with_places_and_scores()
    {
        var h = Party(3);
        Assert.True(h.Start().Ok);
        var p = P(h);
        h.Clock.AdvanceMs(ShatkuyParty.LeadMs);
        // місце 2 ріже лише половину — програє
        PlayParty(h, 3_000, (s, x) => s == 2 && x.Id % 2 == 0);
        Assert.True(p.Over);
        var done = Assert.Single(h.Finished);
        var scores = Enumerable.Range(0, 3).Select(s => p.FieldOf(s)!.Score).ToArray();
        Assert.Equal(scores.Max(), scores[done.Result.Winners[0]]);
        Assert.DoesNotContain(2, done.Result.Winners);
        Assert.Equal(3, done.Result.Scores!.Count);
        var party = h.View(0).GetProperty("party");
        Assert.True(party.GetProperty("over").GetBoolean());
        var places = party.GetProperty("places").EnumerateArray().ToList();
        Assert.Equal(3, places.Count);
        Assert.Equal(1, places[0].GetProperty("place").GetInt32());
        Assert.Equal(2, places[^1].GetProperty("seat").GetInt32());
        Assert.Contains("Шаткуй разом:", done.Result.Text);

        // ще раз — новий віз
        var seed = p.Seed;
        Assert.True(h.Rematch().Ok);
        if (!P(h).Started || P(h).Over) Assert.True(h.Start().Ok);
        Assert.True(P(h).Started && !P(h).Over);
        Assert.NotEqual(seed, P(h).Seed);
    }

    [Fact]
    public void Party_left_player_keeps_the_score_and_others_go_on()
    {
        var h = Party(2);
        Assert.True(h.Start().Ok);
        var p = P(h);
        h.Clock.AdvanceMs(ShatkuyParty.LeadMs);
        PlayParty(h, 300);
        var gone = p.FieldOf(1)!.Score;
        Assert.True(gone > 0);
        h.Leave("шаткар1");
        Assert.False(p.Over);
        Assert.True(p.FieldOf(1)!.Over);
        PlayParty(h, 3_000);
        Assert.True(p.Over);
        Assert.Equal(gone, p.FieldOf(1)!.Score);
        var place = h.View(0).GetProperty("party").GetProperty("places").EnumerateArray().Single(x => x.GetProperty("seat").GetInt32() == 1);
        Assert.True(place.GetProperty("left").GetBoolean());
        Assert.Contains("(пішов)", Assert.Single(h.Finished).Result.Text);
    }

    [Fact]
    public void Party_alone_needs_bots_and_bots_play_their_own_fields()
    {
        var h = Party(1);
        Assert.False(h.Start().Ok);
        Assert.True(h.View(0).GetProperty("botOffer").GetBoolean());
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok);
        var p = P(h);
        Assert.Equal(ShatkuyParty.SoloBots, p.Bots.Count);
        h.Clock.AdvanceMs(ShatkuyParty.LeadMs);
        PlayParty(h, 3_000);
        Assert.True(p.Over);
        Assert.All(p.Bots, b => Assert.True(p.FieldOf(b)!.Score > 20, $"бот {b}: {p.FieldOf(b)!.Score}"));
        var done = Assert.Single(h.Finished);
        Assert.NotNull(done.Result.Verdict);
        Assert.DoesNotContain(h.Awards, a => a.Reason.StartsWith("ach:"));
        Assert.Contains(h.View(0).GetProperty("party").GetProperty("rivals").EnumerateArray(), r => r.GetProperty("bot").GetBoolean());
    }

    [Fact]
    public void Party_with_the_same_table_seed_is_the_same_cart()
    {
        var a = Party(2, seed: 77);
        var b = Party(2, seed: 77);
        Assert.True(a.Start().Ok);
        Assert.True(b.Start().Ok);
        Assert.Equal(P(a).Seed, P(b).Seed);
        a.Clock.AdvanceMs(ShatkuyParty.LeadMs);
        b.Clock.AdvanceMs(ShatkuyParty.LeadMs);
        a.Tick(200);
        b.Tick(200);
        Assert.Equal(P(a).FieldOf(0)!.Fruits.OrderBy(f => f.Id), P(b).FieldOf(1)!.Fruits.OrderBy(f => f.Id));
    }
}

/// <summary>Швидкодія «Шаткуй разом» — окремою колекцією (SerialPerf).</summary>
[Collection(SerialPerf.Name)]
public class ShatkuyPartyPerfTests
{
    [Fact, Trait("Category", "Perf")]
    public void Thousand_ticks_of_eight_fields_are_fast()
    {
        var h = new RoomHarness("shatkuy-party", seed: 4);
        for (var i = 0; i < ShatkuyParty.Seats; i++) Assert.True(h.Join($"ш{i}").Ok);
        Assert.True(h.Start().Ok);
        var p = (ShatkuyParty)h.Room.Game;
        h.Clock.AdvanceMs(ShatkuyParty.LeadMs);
        var w = 0;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1_000 && !p.Over; i++)
        {
            for (var s = 0; s < ShatkuyParty.Seats; s++)
            {
                var f = p.FieldOf(s)!;
                var g = f.G(p.T);
                var c = f.Fruits.Where(x => !f.IsCut(x.Id) && !x.Glek && x.At <= g && g <= x.At + x.D).Select(x => new[] { x.Id, ++w }).ToArray();
                if (c.Length > 0) h.Input(s, "cut", new { c });
            }
            h.Tick();
            if (i % 20 == 0) for (var s = 0; s < ShatkuyParty.Seats; s++) h.View(s);
        }
        Assert.True(sw.ElapsedMilliseconds < 2_000, $"{sw.ElapsedMilliseconds} мс");
    }
}
