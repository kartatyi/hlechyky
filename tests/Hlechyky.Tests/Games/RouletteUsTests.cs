using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Американське колесо «🇺🇸 з 00» (specs/roulette.md §13): 38 кишеньок, 00 = 37 усередині, ставки біля нулів,
/// «п'ятірка» 6:1, RTP з кишенькової математики, опція столу й перемикач соло. Європейське — без змін (RouletteCoreTests,
/// RouletteTests).
/// </summary>
public class RouletteUsTests
{
    const int ZZ = RouletteCore.DoubleZero;
    const string Spin = RouletteGame.Spinning, Bets = RouletteGame.Bets;

    // ---------- колесо ----------

    [Fact]
    public void Us_wheel_has_38_pockets_in_american_order()
    {
        var w = RouletteCore.WheelUs;
        Assert.Equal(38, w.Count);
        Assert.Equal(Enumerable.Range(0, 38), w.Order());
        Assert.Equal([0, 28, 9, 26, 30, 11, 7, 20, 32, 17, 5, 22, 34, 15, 3, 24, 36, 13, 1], w.Take(19));
        Assert.Equal([ZZ, 27, 10, 25, 29, 12, 8, 19, 31, 18, 6, 21, 33, 16, 4, 23, 35, 14, 2], w.Skip(19));
        // американське колесо: 0 навпроти 00, і кожна пара n, n+1 (1–2, 3–4 …) — навпроти одна одної
        int At(int n) => w.ToList().IndexOf(n);
        Assert.Equal(19, Math.Abs(At(0) - At(ZZ)));
        for (var n = 1; n <= 35; n += 2) Assert.Equal(19, Math.Abs(At(n) - At(n + 1)));
        // кольори чергуються в кожній половині (через 0 і 00 — той самий колір поруч, так і є)
        for (var i = 1; i < 37; i++)
        {
            if (i == 18 || i == 19) continue;   // 1 → 00 → 27
            Assert.NotEqual(RouletteCore.ColorOf(w[i]), RouletteCore.ColorOf(w[i + 1]));
        }
        Assert.Same(RouletteCore.Wheel, RouletteCore.WheelOf(false));
        Assert.Same(RouletteCore.WheelUs, RouletteCore.WheelOf(true));
        Assert.Equal(37, RouletteCore.Pockets(false));
        Assert.Equal(38, RouletteCore.Pockets(true));
    }

    [Fact]
    public void Double_zero_is_green_and_named_00()
    {
        Assert.Equal("g", RouletteCore.ColorOf(ZZ));
        Assert.Equal("00", RouletteCore.Name(ZZ));
        Assert.Equal("0", RouletteCore.Name(0));
        Assert.Equal("17", RouletteCore.Name(17));
        Assert.Equal(18, Enumerable.Range(0, 38).Count(n => RouletteCore.ColorOf(n) == "r"));
        Assert.Equal(18, Enumerable.Range(0, 38).Count(n => RouletteCore.ColorOf(n) == "b"));
        Assert.Equal("Подвійне зеро!", RouletteLines.Number(ZZ));
        Assert.Equal("Зеро!", RouletteLines.Number(0));
    }

    // ---------- поле ----------

    [Fact]
    public void Every_us_spot_parses_and_round_trips()
    {
        Assert.Equal(161, RouletteCore.AllUs.Count);
        var byType = RouletteCore.AllUs.GroupBy(RouletteCore.Type).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(38, byType["straight"]);
        Assert.Equal(62, byType["split"]);
        Assert.Equal(15, byType["street"]);
        Assert.Equal(22, byType["corner"]);
        Assert.Equal(11, byType["line"]);
        Assert.Equal(3, byType["column"]);
        Assert.Equal(3, byType["dozen"]);
        Assert.Equal(1, byType["five"]);
        foreach (var spot in RouletteCore.AllUs)
        {
            Assert.Equal(spot, RouletteCore.Canon(spot, us: true));
            Assert.True(RouletteCore.Valid(spot, us: true));
            var nums = RouletteCore.Covers(spot);
            Assert.NotEmpty(nums);
            var type = RouletteCore.Type(spot);
            if (type is "straight" or "split" or "street" or "corner" or "line")
            {
                var back = RouletteCore.Canon($"{type}:{string.Join('-', nums.Reverse().Select(RouletteCore.Name))}", us: true);
                Assert.Equal(spot, back);
                Assert.Equal(spot, RouletteCore.FromParts(type, [.. nums.Reverse()], null, us: true));
            }
            Assert.False(string.IsNullOrEmpty(RouletteCore.Label(spot)));
        }
        // зеро-зона американського поля — рівно ці
        string[] zeros = ["straight:0", "straight:00", "split:0-00", "split:0-1", "split:0-2", "split:00-2", "split:00-3",
            "street:0-1-2", "street:0-00-2", "street:00-2-3", "five"];
        Assert.Equal(zeros.Order(), RouletteCore.AllUs.Where(s => RouletteCore.Covers(s).Any(n => n is 0 or ZZ) && !RouletteCore.IsOutside(s)).Order());
        Assert.Equal([0, ZZ], RouletteCore.Covers("split:0-00"));
        Assert.Equal([0, ZZ, 1, 2, 3], RouletteCore.Covers("five"));
    }

    [Fact]
    public void Each_wheel_refuses_the_other_wheels_zero_spots()
    {
        foreach (var usOnly in new[] { "straight:00", "split:0-00", "split:00-2", "split:00-3", "street:0-00-2", "street:00-2-3", "five" })
        {
            Assert.Null(RouletteCore.Canon(usOnly));
            Assert.False(RouletteCore.Valid(usOnly));
            Assert.NotNull(RouletteCore.Canon(usOnly, us: true));
        }
        foreach (var euOnly in new[] { "split:0-3", "street:0-2-3", "corner:0-1-2-3" })
        {
            Assert.NotNull(RouletteCore.Canon(euOnly));
            Assert.Null(RouletteCore.Canon(euOnly, us: true));
        }
        // 37 — лише внутрішній запис 00: на дроті його нема ні в ключі, ні в числах агента
        foreach (var bad in new[] { "straight:37", "split:0-37", "split:2-37", "five:0-00-1-2-3", "straight:000-0", "split:00-1", "split:00-0-1" })
            Assert.Null(RouletteCore.Canon(bad, us: true));
        Assert.Equal(RouletteCore.Say.NoSuchSpot, RouletteCore.Read(Views.Payload(new { type = "straight", numbers = new[] { 37 } }), out _, us: true));
        Assert.Equal(RouletteCore.Say.NoSuchSpot, RouletteCore.Read(Views.Payload(new { type = "five" }), out _));
        Assert.Equal(RouletteCore.Say.NotUnderstood, RouletteCore.Read(Views.Payload(new { type = "split", numbers = new object[] { "0", "нуль" } }), out _, us: true));
    }

    [Fact]
    public void Agents_write_00_as_a_string_and_keys_order_it_after_0()
    {
        Assert.Null(RouletteCore.Read(Views.Payload(new { type = "split", numbers = new object[] { "00", 0 } }), out var a, us: true));
        Assert.Equal("split:0-00", a);
        Assert.Null(RouletteCore.Read(Views.Payload(new { type = "street", numbers = new object[] { 3, 2, "00" } }), out var b, us: true));
        Assert.Equal("street:00-2-3", b);
        Assert.Null(RouletteCore.Read(Views.Payload(new { type = "five", amount = 5 }), out var f, us: true));
        Assert.Equal("five", f);
        Assert.Null(RouletteCore.Read(Views.Payload(new { spot = "split:00-0" }), out var c, us: true));
        Assert.Equal("split:0-00", c);
        Assert.Equal("split:00-2", RouletteCore.FromParts("split", [2, ZZ], null, us: true));

        Assert.Equal("Подвійне зеро", RouletteCore.Label("straight:00"));
        Assert.Equal("Спліт 0·00", RouletteCore.Label("split:0-00"));
        Assert.Equal("Вулиця 00·2·3", RouletteCore.Label("street:00-2-3"));
        Assert.Equal("П'ятірка 0·00·1·2·3", RouletteCore.Label("five"));
    }

    // ---------- виплати й RTP ----------

    static void Pays(string spot, int k, int[] hits, int[] misses)
    {
        Assert.True(RouletteCore.Valid(spot, us: true), spot);
        Assert.Equal(k, RouletteCore.Pays(spot));
        foreach (var n in hits) Assert.Equal(10L * (k + 1), RouletteCore.Return(spot, 10, n));
        foreach (var n in misses) Assert.Equal(0, RouletteCore.Return(spot, 10, n));
    }

    [Fact]
    public void Every_bet_type_pays_on_the_us_wheel()
    {
        Pays("straight:00", 35, [ZZ], [0, 1]);
        Pays("straight:0", 35, [0], [ZZ]);
        Pays("straight:17", 35, [17], [ZZ, 0]);
        Pays("split:0-00", 17, [0, ZZ], [1, 2]);
        Pays("split:0-1", 17, [0, 1], [ZZ, 2]);
        Pays("split:0-2", 17, [0, 2], [ZZ, 1]);
        Pays("split:00-2", 17, [ZZ, 2], [0, 3]);
        Pays("split:00-3", 17, [ZZ, 3], [0, 2]);
        Pays("split:17-20", 17, [17, 20], [ZZ, 18]);
        Pays("street:0-1-2", 11, [0, 1, 2], [ZZ, 3]);
        Pays("street:0-00-2", 11, [0, ZZ, 2], [1, 3]);
        Pays("street:00-2-3", 11, [ZZ, 2, 3], [0, 1]);
        Pays("street:13-14-15", 11, [13, 14, 15], [ZZ, 16]);
        Pays("corner:17-18-20-21", 8, [17, 18, 20, 21], [ZZ, 19]);
        Pays("five", 6, [0, ZZ, 1, 2, 3], [4, 36]);
        Pays("line:13-14-15-16-17-18", 5, [13, 18], [ZZ, 19]);
        Pays("column:1", 2, [1, 34], [ZZ, 0, 35]);
        Pays("dozen:3", 2, [25, 36], [ZZ, 0, 24]);
        Pays("red", 1, [1, 36], [ZZ, 0, 2]);
        Pays("black", 1, [2, 35], [ZZ, 0, 1]);
        Pays("even", 1, [2, 36], [ZZ, 0, 1]);
        Pays("odd", 1, [1, 35], [ZZ, 0, 2]);
        Pays("low", 1, [1, 18], [ZZ, 0, 19]);
        Pays("high", 1, [19, 36], [ZZ, 0, 18]);
        Assert.Equal(70, RouletteCore.Return("five", 10, ZZ));
        Assert.Equal(36L * RouletteCore.MaxPerSpot, RouletteCore.MaxReturn([("straight:00", RouletteCore.MaxPerSpot)]));
    }

    [Fact]
    public void Double_zero_loses_every_outside_bet()
    {
        foreach (var spot in RouletteCore.Outside) Assert.Equal(0, RouletteCore.Return(spot, 100, ZZ));
    }

    /// <summary>
    /// RTP кожного поля — точно, з кишенькової математики: Σ повернень ставки 1 по всіх кишеньках / кількість кишеньок.
    /// Європейське: 36/37 = 97,30 % на кожне поле. Американське: 36/38 = 94,74 %, «п'ятірка» — 35/38 = 92,11 %.
    /// </summary>
    [Fact]
    public void Rtp_of_every_bet_comes_from_pocket_math()
    {
        foreach (var us in new[] { false, true })
        {
            var pockets = RouletteCore.Pockets(us);
            foreach (var spot in RouletteCore.AllOf(us))
            {
                long back = 0;
                for (var n = 0; n < pockets; n++) back += RouletteCore.Return(spot, 1, n);
                var expected = spot == RouletteCore.Five ? 35 : 36;
                Assert.True(back == expected, $"{(us ? "us" : "eu")} {spot}: повертає {back} з {pockets}");
            }
        }
        Assert.Equal(97.30, Math.Round(36.0 / 37 * 100, 2));
        Assert.Equal(94.74, Math.Round(36.0 / 38 * 100, 2));
        Assert.Equal(92.11, Math.Round(35.0 / 38 * 100, 2));
        // перевага дому вдвічі більша: 2/38 проти 1/37 (≈ 1,95×) — Глек не бреше
        Assert.InRange((2.0 / 38) / (1.0 / 37), 1.9, 2.0);
    }

    // ---------- стіл «🇺🇸 з 00» ----------

    [Fact]
    public void Table_option_picks_the_wheel_and_glek_warns()
    {
        var info = new Roulette().Info;
        var opt = Assert.Single(info.Options!);
        Assert.Equal("wheel", opt.Key);
        Assert.Equal("eu", opt.Default);
        Assert.Equal(["eu", "us"], opt.Values.Select(v => v.Value));
        Assert.Contains(opt.Values, v => v.Label == "🇺🇸 з 00");
        Assert.Null(new RouletteSolo().Info.Options);

        var us = RouletteKit.TableUs(("Оля", 1000));
        Assert.Equal("us", us.View(0).GetProperty("wheel").GetString());
        Assert.True(us.G.Us);
        Assert.Equal(RouletteLines.UsWarning, us.View(0).GetProperty("glek").GetProperty("say").GetString());
        Assert.Equal("На американському колесі я забираю вдвічі більше. Любиш ризик — прошу", RouletteLines.UsWarning);

        var eu = RouletteKit.Table(("Оля", 1000));
        Assert.Equal("eu", eu.View(0).GetProperty("wheel").GetString());
        Assert.False(eu.G.Us);
        Assert.NotEqual(RouletteLines.UsWarning, eu.View(0).GetProperty("glek").GetProperty("say").GetString());
    }

    [Fact]
    public void Us_table_settles_00_through_the_book()
    {
        var k = RouletteKit.TableUs(("Оля", 1000), ("Петро", 1000));
        Assert.True(k.Bet(0, "straight:00", 20).Ok);   // ×36 = 720
        Assert.True(k.Bet(0, "split:0-00", 10).Ok);    // ×18 = 180
        Assert.True(k.Bet(0, "five", 10).Ok);          // ×7 = 70
        Assert.True(k.Bet(0, "street:00-2-3", 5).Ok);  // ×12 = 60
        Assert.True(k.Bet(0, "red", 50).Ok);           // 0
        Assert.True(k.Bet(1, "straight:0", 10).Ok);    // 0
        Assert.True(k.Bet(1, "black", 40).Ok);         // 0
        k.Rig(ZZ);
        k.To(Spin);
        var spin = k.View(0).GetProperty("spin");
        Assert.Equal(ZZ, spin.GetProperty("n").GetInt32());
        Assert.Equal("g", spin.GetProperty("c").GetString());
        Assert.Empty(k.Grants);   // гроші — коли кулька лягла
        k.To(RouletteGame.Result);

        Assert.Equal(1000 - 95 + 1030, k.Stakes.Balance("Оля"));
        Assert.Equal(950, k.Stakes.Balance("Петро"));
        Assert.Equal([$"grant:Оля:1030:{k.Ref("roulette-win", "Оля")}"], k.Grants);
        var v = k.View(0);
        Assert.Equal(ZZ, v.GetProperty("history")[0].GetProperty("n").GetInt32());
        Assert.Equal("g", v.GetProperty("history")[0].GetProperty("c").GetString());
        var last = v.GetProperty("last");
        Assert.Equal(ZZ, last.GetProperty("n").GetInt32());
        var hits = last.GetProperty("results")[0].GetProperty("hits").EnumerateArray().Select(h => h.GetString()).ToList();
        Assert.Equal(["straight:00", "split:0-00", "five", "street:00-2-3"], hits);
        Assert.StartsWith("Подвійне зеро!", v.GetProperty("glek").GetProperty("say").GetString());
        Assert.Contains("ach:roulette-straight", k.Achievements("Оля"));
        Assert.Contains("ach:roulette-zero", k.Achievements("Оля"));
    }

    [Fact]
    public void Five_number_pays_6_to_1_on_each_of_its_pockets()
    {
        var k = RouletteKit.TableUs(("Оля", 10_000));
        foreach (var n in new[] { 0, ZZ, 1, 2, 3, 4, 36 })
        {
            var before = k.Stakes.Balance("Оля");
            Assert.True(k.Bet(0, "five", 10).Ok);
            k.Round(n);
            Assert.Equal(before - 10 + (n is 4 or 36 ? 0 : 70), k.Stakes.Balance("Оля"));
        }
    }

    [Fact]
    public void Zero_zero_with_losers_makes_glek_laugh()
    {
        var k = RouletteKit.TableUs(("Оля", 1000));
        k.Bet(0, "red", 100);
        k.Rig(ZZ);
        k.To(RouletteGame.Result);
        Assert.Equal("laugh", k.S.Glek.Mood);
        Assert.Contains(k.S.Glek.Say, RouletteLines.ZeroZero);
    }

    [Fact]
    public void Eu_table_refuses_00_bets_and_us_table_refuses_eu_zero_bets()
    {
        var eu = RouletteKit.Table(("Оля", 1000));
        foreach (var spot in new[] { "straight:00", "split:0-00", "five", "street:00-2-3" })
            Assert.Equal(RouletteCore.Say.NoSuchSpot, eu.Bet(0, spot, 5).Message);
        var us = RouletteKit.TableUs(("Оля", 1000));
        foreach (var spot in new[] { "corner:0-1-2-3", "split:0-3", "street:0-2-3" })
            Assert.Equal(RouletteCore.Say.NoSuchSpot, us.Bet(0, spot, 5).Message);
        Assert.Equal(0, us.View(0).GetProperty("onTable").GetInt32());
    }

    [Fact]
    public void Shared_table_cannot_switch_the_wheel()
    {
        var k = RouletteKit.TableUs(("Оля", 1000));
        var r = k.H.Act(0, "wheel", new { wheel = "eu" });
        Assert.False(r.Ok);
        Assert.Equal(RouletteGame.TableWheelText, r.Message);
        Assert.True(k.G.Us);
    }

    [Fact]
    public void Draw_is_uniform_over_38_pockets_on_the_us_wheel()
    {
        var k = RouletteKit.TableUs(("Оля", 1000));
        var counts = new int[38];
        lock (k.H.Room.Sync)
            for (var i = 0; i < 38_000; i++) counts[k.G.Draw()]++;
        Assert.All(counts, c => Assert.InRange(c, 800, 1200));
        k.G.Rig = () => ZZ;
        Assert.Equal(ZZ, k.G.Draw());
        k.G.Rig = () => 38;
        Assert.Throws<GameError>(() => k.G.Draw());
        k.G.Rig = () => -1;
        Assert.Throws<GameError>(() => k.G.Draw());
        // європейське колесо 00 не знає
        var eu = RouletteKit.Table(("Оля", 1000));
        eu.G.Rig = () => ZZ;
        Assert.Throws<GameError>(() => eu.G.Draw());
    }

    [Fact]
    public void Table_resume_keeps_the_us_wheel_and_crash_recovery_pays_00()
    {
        var k = RouletteKit.TableUs(("Оля", 1000));
        k.Bet(0, "straight:00", 10);
        k.Bet(0, "five", 10);
        var saved = k.G.Save()!;
        Assert.Contains("\"wheel\":\"us\"", saved);
        lock (k.H.Room.Sync)
        {
            k.G.Start();
            k.G.Load(saved);
            k.G.Resumed(TimeSpan.FromSeconds(5));
        }
        Assert.True(k.G.Us);
        Assert.Equal(20, k.View(0).GetProperty("onTable").GetInt32());

        // закрили коло з 00 — і процес упав: каса розраховує за записаним числом 37
        k.Rig(ZZ);
        k.To(Spin);
        Assert.Equal(ZZ, Assert.Single(k.Book.Pending()).Number);
        var book2 = new RouletteBook(k.Stakes, k.Store, defer: a => a());
        Assert.Equal(1, book2.Recover(DateTimeOffset.MaxValue));
        Assert.Equal(1000 - 20 + 360 + 70, k.Stakes.Balance("Оля"));
    }

    [Fact]
    public void Eu_table_state_never_turns_american_on_load()
    {
        var k = RouletteKit.Table(("Оля", 1000));
        var saved = k.G.Save()!.Replace("\"wheel\":\"eu\"", "\"wheel\":\"us\"");
        lock (k.H.Room.Sync)
        {
            k.G.Start();
            k.G.Load(saved);
        }
        Assert.False(k.G.Us);   // стіл — завжди з опції
    }

    // ---------- соло: перемикач ----------

    [Fact]
    public void Solo_toggle_switches_the_wheel_warns_and_persists()
    {
        var k = RouletteKit.Solo(wallet: 1000);
        var v = k.View(0);
        Assert.Equal("eu", v.GetProperty("wheel").GetString());
        Assert.True(v.GetProperty("me").GetProperty("canWheel").GetBoolean());

        Assert.True(k.H.Act(0, "wheel", new { wheel = "us" }).Ok);
        v = k.View(0);
        Assert.Equal("us", v.GetProperty("wheel").GetString());
        Assert.Equal(RouletteLines.UsWarning, v.GetProperty("glek").GetProperty("say").GetString());
        var seq = v.GetProperty("glek").GetProperty("seq").GetInt64();
        Assert.True(k.H.Act(0, "wheel", new { wheel = "us" }).Ok);   // те саме колесо — нічого
        Assert.Equal(seq, k.View(0).GetProperty("glek").GetProperty("seq").GetInt64());

        Assert.True(k.Bet(0, "straight:00", 10).Ok);
        Assert.False(k.Me(0).GetProperty("canWheel").GetBoolean());
        var r = k.H.Act(0, "wheel", new { wheel = "eu" });
        Assert.Equal(RouletteGame.WheelBetsText, r.Message);
        Assert.Equal(RouletteGame.WheelWhichText, k.H.Act(0, "wheel", new { wheel = "fr" }).Message);
        Assert.Equal(RouletteGame.WheelWhichText, k.H.Act(0, "wheel").Message);

        k.SoloSpin(ZZ);
        Assert.Equal(1000 - 10 + 360, k.Stakes.Balance("Оля"));
        Assert.Equal(ZZ, k.View(0).GetProperty("last").GetProperty("n").GetInt32());

        // каркас зберіг стан після дії — колесо в ньому; Load повертає американське
        k.Bet(0, "red", 5);
        var saved = k.H.Store.States["roulette-solo:оля"];
        Assert.Contains("\"wheel\":\"us\"", saved);
        lock (k.H.Room.Sync)
        {
            k.G.Start();
            k.S.Wheel = "eu";
            k.G.Load(saved);
        }
        Assert.True(k.G.Us);
        Assert.Equal("us", k.View(0).GetProperty("wheel").GetString());
    }

    [Fact]
    public void Solo_cannot_switch_mid_spin()
    {
        var k = RouletteKit.Solo();
        k.Bet(0, "red", 10);
        k.Rig(5);
        Assert.True(k.H.Act(0, "spin", new { }).Ok);
        Assert.False(k.Me(0).GetProperty("canWheel").GetBoolean());
        Assert.Equal("Колесо крутиться — дочекайся", k.H.Act(0, "wheel", new { wheel = "us" }).Message);
        Assert.False(k.G.Us);
        k.To(Bets);
        Assert.True(k.H.Act(0, "wheel", new { wheel = "us" }).Ok);
    }

    [Fact]
    public void Switching_keeps_only_last_bets_the_new_wheel_has()
    {
        var k = RouletteKit.Solo(wallet: 1000);
        Assert.True(k.H.Act(0, "wheel", new { wheel = "us" }).Ok);
        k.Bet(0, "split:0-00", 10);
        k.Bet(0, "five", 5);
        k.Bet(0, "red", 20);
        k.SoloSpin(4);
        Assert.Equal(35, k.Me(0).GetProperty("repeatCost").GetInt32());
        Assert.True(k.H.Act(0, "wheel", new { wheel = "eu" }).Ok);
        Assert.Equal(RouletteLines.EuBack, k.S.Glek.Say);
        Assert.Equal(20, k.Me(0).GetProperty("repeatCost").GetInt32());
        Assert.True(k.H.Act(0, "repeat").Ok);
        Assert.Equal(["red"], k.View(0).GetProperty("players")[0].GetProperty("bets").EnumerateArray().Select(b => b.GetProperty("spot").GetString()));
    }

    [Fact]
    public void Rtp_simulation_through_draw_on_the_us_wheel_stays_near_94_74()
    {
        // Чесний генератор через Draw() (сід кімнати): 380 000 кіл «червоне» по 1 — RTP ≈ 36/38.
        var k = RouletteKit.TableUs(("Оля", 1000));
        long back = 0, five = 0;
        const int N = 380_000;
        lock (k.H.Room.Sync)
            for (var i = 0; i < N; i++)
            {
                var n = k.G.Draw();
                back += RouletteCore.Return("red", 1, n);
                five += RouletteCore.Return("five", 1, n);
            }
        Assert.InRange(back / (double)N, 0.9474 - 0.006, 0.9474 + 0.006);
        Assert.InRange(five / (double)N, 0.9211 - 0.02, 0.9211 + 0.02);
    }
}
