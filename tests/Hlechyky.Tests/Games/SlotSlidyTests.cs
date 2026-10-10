using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;
using M = Hlechyky.Games.Impl.SlotSlidyMath;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Підставна випадковість «Слідів на полиці»: символи з черги (кожен ключ → число посередині його відрізка ваг), порожня
/// черга — стале число <see cref="Rest"/> (0 — завжди k1, 0,99999 — завжди горно).
/// </summary>
public sealed class SlidyRng(double rest = 0.5) : ISlotRng
{
    readonly Queue<double> _q = new();
    public double Rest { get; set; } = rest;

    static double Mid(double[] w, int i) => (w.Take(i).Sum() + w[i] / 2) / w.Sum();

    public SlidyRng Keys(bool free, params string[] keys)
    {
        var w = (free ? M.Free : M.Base).Sym;
        foreach (var k in keys) _q.Enqueue(Mid(w, M.Code(k)));
        return this;
    }

    public int Next(int max) => 0;
    public double NextDouble() => _q.Count > 0 ? _q.Dequeue() : Rest;
}

/// <summary>
/// «Сліди на полиці» (slot-slidy, docs/games/specs/slot-slidy.md): кластери, сліди (мокре коло, ×2…×128, сума під
/// кластером), каскади, горно й вільні з незгасними слідами, ретригер і межа 50, стеля, куплений бонус, сценарій крок за
/// кроком (як клієнт), автомат на платформі (гроші рівно раз, купівля бонусу), симуляції RTP.
/// </summary>
public class SlotSlidyTests(ITestOutputHelper output)
{
    const int Bet = 100;

    /// <summary>Поле без кластерів: сусіди по горизонталі різняться на 1, по вертикалі — на 2 (з 7 символів).</summary>
    static string[][] Plain() =>
        [.. Enumerable.Range(0, M.Cols).Select(c => Enumerable.Range(0, M.Rows).Select(r => M.Paying[(c + 2 * r) % 7]).ToArray())];

    static int[] Codes(string[][] g)
    {
        var a = new int[M.Cells];
        for (var c = 0; c < M.Cols; c++) for (var r = 0; r < M.Rows; r++) a[c * M.Rows + r] = M.Code(g[c][r]);
        return a;
    }

    static List<M.Cluster> Clusters(string[][] g)
    {
        var l = new List<M.Cluster>();
        M.Scan(Codes(g), l);
        return l;
    }

    static List<JsonObject> Steps(JsonNode? steps) => [.. steps!.AsArray().Select(s => s!.AsObject())];
    static string T(JsonObject s) => (string)s["t"]!;
    static long L(JsonNode? n) => long.Parse(n!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- кластери й виплати

    [Fact]
    public void Plain_field_is_calm()
    {
        Assert.Empty(Clusters(Plain()));
    }

    [Fact]
    public void Five_touching_by_sides_pay_four_do_not_and_diagonals_do_not_touch()
    {
        // нижній ряд: над ним (рядок 4) глеків нема — кластер рівно з тих, що поставили
        var g = Plain();
        for (var c = 0; c < 4; c++) g[c][5] = "glek";
        Assert.DoesNotContain(Clusters(g), k => k.Cells.Contains(5));   // четверо — мало
        g[4][5] = "glek";
        var cl = Assert.Single(Clusters(g), k => k.Cells.Contains(5));
        Assert.Equal("glek", M.Paying[cl.Sym]);
        Assert.Equal(5, cl.Cells.Length);

        var d = Plain();                                           // драбинка навскіс — не кластер
        for (var i = 0; i < 6; i++) d[i][i] = "glek";
        Assert.DoesNotContain(Clusters(d), k => M.Paying[k.Sym] == "glek");
    }

    [Theory]
    [InlineData(5, 0)]
    [InlineData(8, 3)]
    [InlineData(9, 4)]
    [InlineData(10, 4)]
    [InlineData(11, 5)]
    [InlineData(15, 6)]
    [InlineData(16, 7)]
    [InlineData(36, 7)]
    public void Cluster_size_tiers(int n, int tier) => Assert.Equal(tier, M.Tier(n));

    [Fact]
    public void Pays_are_whole_on_every_bet_and_scale_with_it()
    {
        foreach (var bet in SlotsOptions.DefaultBets)
            for (var s = 0; s < M.Paying.Length; s++)
                for (var t = 0; t < M.Tiers; t++)
                    Assert.Equal(M.Pay10[s][t] * bet / 10, M.PayOf(s, M.TierFrom[t], bet));
        Assert.Equal(250, M.PayOf(M.Code("glek"), 5, 100));       // 2,5 ставки
        Assert.Equal(20000, M.PayOf(M.Code("glek"), 16, 100));    // 200 ставок
        Assert.Equal(5, M.PayOf(M.Code("k1"), 5, 10));            // 0,5 ставки
    }

    // ---------------------------------------------------------------- сліди

    [Fact]
    public void Trace_grows_wet_then_doubles_up_to_128()
    {
        int[] want = [1, 2, 4, 8, 16, 32, 64, 128, 128];
        var t = 0;
        foreach (var w in want) Assert.Equal(w, t = M.NextTrace(t));
    }

    [Fact]
    public void Cluster_is_multiplied_by_the_sum_of_fired_traces_wet_ones_do_not_count()
    {
        var tr = new int[M.Cells];
        int[] cells = [0, 1, 2, 3, 4];
        Assert.Equal(1, M.MultOf(tr, cells));                     // слідів нема — ×1
        tr[0] = 1; tr[1] = 1;
        Assert.Equal(1, M.MultOf(tr, cells));                     // мокрі не множать
        tr[2] = 2;
        Assert.Equal(2, M.MultOf(tr, cells));
        tr[3] = 8; tr[4] = 128;
        Assert.Equal(138, M.MultOf(tr, cells));                   // сума, не добуток
        tr[5] = 64;                                               // слід поза кластером — не його
        Assert.Equal(138, M.MultOf(tr, cells));
    }

    /// <summary>Глеки (0,0)…(0,4): у колонці 0 нові символи кладемо так, щоб поле знову стало спокійним.</summary>
    static (string[][] G, SlidyRng Rng) GlekColumn()
    {
        var g = Plain();
        for (var r = 0; r < 5; r++) g[0][r] = "glek";
        var rng = new SlidyRng().Keys(true, "k1", "k3", "makitra", "glek", "k2");   // як Plain у колонці 0, рядки 0–4
        return (g, rng);
    }

    [Fact]
    public void Free_spin_pays_on_traces_and_grows_them_under_the_broken_pots()
    {
        var (g, rng) = GlekColumn();
        var tr = new int[M.Cells];
        tr[0] = 1; tr[1] = 2; tr[2] = 4; tr[5 * M.Rows] = 64;    // (0,0) мокрий, (0,1) ×2, (0,2) ×4, (5,0) — осторонь
        var (win, steps) = new M().FreeSpinFor(g, tr, Bet, rng);
        var s = Steps(steps);
        Assert.Equal(["win", "cascade"], s.Select(T));
        var it = Assert.Single(s[0]["items"]!.AsArray())!.AsObject();
        Assert.Equal("glek", (string)it["sym"]!);
        Assert.Equal(5, (int)it["n"]!);
        Assert.Equal(250, L(it["pay"]));
        Assert.Equal(6, (int)it["mult"]!);                        // 2 + 4; мокрий не рахується
        Assert.Equal(1500, L(it["amount"]));
        Assert.Equal(1500, win);
        // сліди під розбитими: 1→2, 2→4, 4→8, нові — мокрі; осторонь — без змін
        Assert.Equal([2, 4, 8, 1, 1], tr.Take(5));
        Assert.Equal(64, tr[5 * M.Rows]);
        var up = s[1]["up"]!.AsArray().Select(u => ((int)u![0]!, (int)u[1]!, (int)u[2]!)).ToList();
        Assert.Equal([(0, 0, 2), (0, 1, 4), (0, 2, 8), (0, 3, 1), (0, 4, 1)], up);
        Assert.Equal(5, s[1]["remove"]!.AsArray().Count);
    }

    [Fact]
    public void Base_spin_starts_with_a_clean_shelf()
    {
        // перший виграш оберту завжди ×1: сліди в базі — лише з цього ж каскаду
        var (g, rng) = GlekColumn();
        rng.Keys(true);
        var o = new M().ScriptFor(g, Bet, new SlidyRng().Keys(false, "k1", "k3", "makitra", "glek", "k2"));
        var s = Steps(o.Script["steps"]);
        Assert.Equal(["spin", "win", "cascade"], s.Select(T));
        Assert.Equal(1, (int)s[1]["items"]![0]!["mult"]!);
        Assert.Equal(250, o.Win);
        Assert.All(s[2]["up"]!.AsArray(), u => Assert.Equal(1, (int)u![2]!));
    }

    [Fact]
    public void Endless_tumble_multiplies_by_the_fired_traces_and_stops_at_the_cap()
    {
        // завжди k1: усе поле — один кластер 36; щоразу знову все k1. Множники: 1, 1 (мокрі), 72, 144, 288 → стеля.
        var o = new M().Spin(Bet, new SlidyRng(0), new JsonObject());
        var s = Steps(o.Script["steps"]);
        var wins = s.Where(x => T(x) == "win").ToList();
        Assert.Equal([1, 1, 72, 144, 288], wins.Select(w => (int)w["items"]![0]!["mult"]!));
        var pay = M.PayOf(M.Code("k1"), 36, Bet);                 // 12 ставок
        Assert.Equal(1200, pay);
        Assert.Equal(5000L * Bet, o.Win);
        Assert.Equal(5000L * Bet - (1200 + 1200 + 1200 * 72 + 1200 * 144), L(wins[^1]["amount"]));
        Assert.True((bool)o.Script["capped"]!);
        Assert.Equal("banner", T(s[^1]));
        Assert.Contains("cap", o.Flags);
        Assert.DoesNotContain(s, x => T(x) == "bonusIn");
    }

    // ---------------------------------------------------------------- горно й вільні

    [Fact]
    public void Three_furnaces_after_the_cascade_open_ten_free_spins()
    {
        var g = Plain();
        g[0][1] = "furnace"; g[2][3] = "furnace";
        var two = new M().ScriptFor(g, Bet, new SlidyRng());
        Assert.False((bool)two.Script["bonus"]!);
        Assert.DoesNotContain(Steps(two.Script["steps"]), x => T(x) == "bonusIn");

        g[4][5] = "furnace";
        // вільні: усе поле — горна (ні кластерів, ні виграшу), а ретригер — щоразу, поки разом менше 50
        var o = new M().ScriptFor(g, Bet, new SlidyRng(0.99999));
        var s = Steps(o.Script["steps"]);
        Assert.True((bool)o.Script["bonus"]!);
        var i = s.FindIndex(x => T(x) == "bonusIn");
        Assert.Equal("furn", T(s[i - 1]));
        Assert.Equal(3, s[i - 1]["cells"]!.AsArray().Count);
        Assert.Equal(10, (int)s[i]["count"]!);
        Assert.Equal(50, s.Count(x => T(x) == "spin") - 1);                       // 10 + 4 × 10 = 50, не більше
        Assert.Equal([10, 10, 10, 10], s.Where(x => T(x) == "fs" && x.ContainsKey("add")).Select(x => (int)x["add"]!));
        Assert.Equal("bonusOut", T(s[^1]));
        Assert.Equal(50, (int)s[^1]["spins"]!);
        Assert.Equal(0, o.Win);
    }

    [Fact]
    public void Free_spins_keep_the_traces_from_spin_to_spin()
    {
        // у вільних кожне поле — глеки в колонці 0 (рядки 0–4) на спокійному тлі: щоразу ті самі клітинки б'ються
        var m = new M();
        var tr = new int[M.Cells];
        long[] wins = new long[4];
        for (var k = 0; k < 4; k++)
        {
            var (g, rng) = GlekColumn();
            wins[k] = m.FreeSpinFor(g, tr, Bet, rng).Win;
        }
        // 1-й: слідів нема (×1), лишились мокрі; 2-й: мокрі не множать (×1), стали ×2; 3-й: 5 × 2 = ×10; 4-й: 5 × 4 = ×20
        Assert.Equal([250, 250, 250 * 10, 250 * 20], wins);
        Assert.All(tr.Take(5), v => Assert.Equal(8, v));
    }

    [Fact]
    public void Bought_bonus_is_just_the_free_spins()
    {
        var o = new M().Buy(Bet, new SeededSlotRng(new Random(5)));
        var s = Steps(o.Script["steps"]);
        Assert.Equal("bonusIn", T(s[0]));
        Assert.Equal("bonusOut", T(s[^1]));
        Assert.True((bool)o.Script["bought"]!);
        Assert.True((bool)o.Script["bonus"]!);
        Assert.Contains("bought", o.Flags);
        Assert.Equal(L(s[^1]["total"]), o.Win);
        Assert.True(new M().BuyMinPrice >= 90);
    }

    // ---------------------------------------------------------------- сценарій крок за кроком (як клієнт)

    static readonly HashSet<string> StepTypes = ["spin", "win", "cascade", "furn", "bonusIn", "fs", "bonusOut", "banner"];

    /// <summary>
    /// Програти сценарій, як клієнт slot-slidy.js, і перевірити кожен крок проти поля й правил: кластери — рівно ті, що на
    /// полі; множник — сума слідів ×2+ під ними; розбиті — рівно клітинки кластерів, сліди під ними ростуть; решта падає
    /// вниз у тому ж порядку; у базі полиця чиста на кожен оберт, у вільних — сліди тримаються; горно 3+ — вхід/ретригер.
    /// </summary>
    static (long Win, int Free, bool Bonus) Replay(JsonObject script, int bet)
    {
        var s = Steps(script["steps"]);
        Assert.All(s, x => Assert.Contains(T(x), StepTypes));
        var capped = script["capped"]?.GetValue<bool>() == true;
        string[][]? grid = null;
        var tr = new int[M.Cells];
        bool fs = false, bonus = false;
        int total = 0, left = 0, spins = 0, casc = 0;
        long win = 0, fsWin = 0;
        for (var i = 0; i < s.Count; i++)
        {
            var x = s[i];
            switch (T(x))
            {
                case "spin":
                    grid = [.. x["grid"]!.AsArray().Select(c => c!.AsArray().Select(k => (string)k!).ToArray())];
                    Assert.Equal(M.Cols, grid.Length);
                    Assert.All(grid, c => Assert.Equal(M.Rows, c.Length));
                    if (!fs) Array.Clear(tr);
                    else spins++;
                    var tease = M.Tease(Codes(grid), M.Scatters);
                    Assert.Equal(!fs || total < M.MaxFreeSpins ? tease : [], x["tease"]!.AsArray().Select(v => (int)v!));
                    casc = 0;
                    break;
                case "win":
                {
                    Assert.NotNull(grid);
                    var cl = Clusters(grid!);
                    var items = x["items"]!.AsArray().Select(it => it!.AsObject()).ToList();
                    Assert.Equal(cl.Count, items.Count);
                    long sum = 0;
                    for (var j = 0; j < cl.Count; j++)
                    {
                        var it = items[j];
                        Assert.Equal(M.Paying[cl[j].Sym], (string)it["sym"]!);
                        Assert.Equal(cl[j].Cells.Length, (int)it["n"]!);
                        Assert.Equal(cl[j].Cells.Select(p => (p / M.Rows, p % M.Rows)),
                            it["cells"]!.AsArray().Select(p => ((int)p![0]!, (int)p[1]!)));
                        var mult = cl[j].Cells.Sum(p => tr[p] >= 2 ? tr[p] : 0);
                        if (mult == 0) mult = 1;
                        Assert.Equal(mult, (int)it["mult"]!);
                        Assert.Equal(M.PayOf(cl[j].Sym, cl[j].Cells.Length, bet), L(it["pay"]));
                        Assert.Equal(L(it["pay"]) * mult, L(it["amount"]));
                        sum += L(it["amount"]);
                    }
                    var amount = L(x["amount"]);
                    var last = i == s.Count - 1 || T(s[i + 1]) != "cascade";
                    if (capped && last) Assert.True(amount <= sum);
                    else Assert.Equal(sum, amount);
                    win += amount;
                    if (fs) fsWin += amount;
                    break;
                }
                case "cascade":
                {
                    var prev = s[i - 1];
                    Assert.Equal("win", T(prev));
                    var rem = prev["items"]!.AsArray().SelectMany(it => it!["cells"]!.AsArray().Select(p => (int)p![0]! * M.Rows + (int)p[1]!)).Distinct().Order().ToList();
                    Assert.Equal(rem, x["remove"]!.AsArray().Select(p => (int)p![0]! * M.Rows + (int)p[1]!));
                    var up = x["up"]!.AsArray().Select(u => ((int)u![0]! * M.Rows + (int)u[1]!, (int)u[2]!)).ToList();
                    Assert.Equal(rem, up.Select(u => u.Item1));
                    foreach (var (p, v) in up)
                    {
                        Assert.Equal(M.NextTrace(tr[p]), v);
                        tr[p] = v;
                    }
                    var after = x["grid"]!.AsArray().Select(c => c!.AsArray().Select(k => (string)k!).ToArray()).ToArray();
                    var set = rem.ToHashSet();
                    for (var c = 0; c < M.Cols; c++)
                    {
                        var keep = Enumerable.Range(0, M.Rows).Where(r => !set.Contains(c * M.Rows + r)).Select(r => grid![c][r]).ToList();
                        Assert.Equal(keep, after[c].Skip(M.Rows - keep.Count));
                        Assert.All(after[c], k => M.Code(k));
                    }
                    grid = after;
                    Assert.Equal(++casc, (int)x["n"]!);
                    break;
                }
                case "furn":
                {
                    Assert.Empty(Clusters(grid!));                                 // горно рахується на спокійній полиці
                    var fc = Enumerable.Range(0, M.Cells).Where(p => grid![p / M.Rows][p % M.Rows] == M.Furnace).ToList();
                    Assert.True(fc.Count >= M.Scatters);
                    Assert.Equal(fc, x["cells"]!.AsArray().Select(p => (int)p![0]! * M.Rows + (int)p[1]!));
                    Assert.Equal(fs, x["fs"]?.GetValue<bool>() == true);
                    if (!fs) Assert.Equal("bonusIn", T(s[i + 1]));
                    else Assert.Equal("fs", T(s[i + 1]));
                    break;
                }
                case "bonusIn":
                    Assert.False(fs);
                    fs = bonus = true;
                    Array.Clear(tr);
                    total = left = (int)x["count"]!;
                    Assert.Equal(M.FreeSpins, total);
                    break;
                case "fs":
                    Assert.True(fs);
                    if (x["add"] is { } add)
                    {
                        Assert.Equal(Math.Min(M.Retrigger, M.MaxFreeSpins - total), (int)add);
                        total += (int)add;
                        left += (int)add;
                        Assert.True(total <= M.MaxFreeSpins);
                    }
                    else
                    {
                        Assert.Equal(--left, (int)x["left"]!);
                        Assert.Equal("spin", T(s[i + 1]));
                    }
                    break;
                case "banner":
                    Assert.True(capped);
                    break;
                case "bonusOut":
                    Assert.True(fs);
                    Assert.Equal(fsWin, L(x["total"]));
                    Assert.Equal(spins, (int)x["spins"]!);
                    Assert.Equal(tr.Max(), (int)x["top"]!);
                    if (!capped) Assert.Equal(0, left);
                    Assert.Equal(i, s.Count - 1);
                    fs = false;
                    break;
            }
        }
        if (!bonus && !capped) Assert.Empty(Clusters(grid!));                   // база закінчилась спокоєм
        if (!bonus && !capped)
        {
            var fc = Enumerable.Range(0, M.Cells).Count(p => grid![p / M.Rows][p % M.Rows] == M.Furnace);
            Assert.True(fc < M.Scatters);
        }
        Assert.Equal(win, L(script["win"]));
        return (win, spins, bonus);
    }

    [Fact]
    public void Scripts_replay_step_by_step_like_the_client()
    {
        var m = new M();
        var rng = new SeededSlotRng(new Random(17));
        int bonuses = 0, multiplied = 0, retriggers = 0;
        long won = 0;
        for (var i = 0; i < 4000; i++)
        {
            var o = m.Spin(Bet, rng, new JsonObject());
            var (w, _, b) = Replay(o.Script, Bet);
            Assert.Equal(o.Win, w);
            won += w;
            if (b) bonuses++;
            var st = Steps(o.Script["steps"]);
            if (st.Any(x => T(x) == "win" && x["items"]!.AsArray().Any(it => (int)it!["mult"]! > 1))) multiplied++;
            retriggers += st.Count(x => T(x) == "fs" && x.ContainsKey("add"));
            Assert.Equal(Bet, (int)o.Script["bet"]!);
            Assert.IsType<JsonObject>(o.Script["state"]);
        }
        for (var i = 0; i < 300; i++)
        {
            var o = m.Buy(Bet, rng);
            var (w, n, b) = Replay(o.Script, Bet);
            Assert.True(b);
            Assert.True(n >= M.FreeSpins);
            Assert.Equal(o.Win, w);
        }
        output.WriteLine($"4000 обертів: бонусів {bonuses}, з множником {multiplied}, ретригерів {retriggers}, RTP ≈ {won / 4000.0 / Bet:P1}");
        Assert.True(bonuses >= 10, $"бонусів {bonuses}");
        Assert.True(multiplied >= 30, $"з множником {multiplied}");
    }

    [Fact]
    public void Fast_spins_the_same_numbers_as_the_script()
    {
        var m = new M();
        var a = new SeededSlotRng(new Random(3));
        var b = new SeededSlotRng(new Random(3));
        for (var i = 0; i < 3000; i++) Assert.Equal(m.Spin(Bet, a, new JsonObject()).Win, m.Fast(Bet, b));
    }

    [Fact]
    public void Stratified_pieces_add_up_to_the_whole_spin()
    {
        // розшарована оцінка чесна: база без бонусу (SkipBonus) + окремо зіграний бонус на тих самих числах = цілий оберт
        var m = new M();
        var a = new SeededSlotRng(new Random(21));
        var b = new SeededSlotRng(new Random(21));
        var skip = new M.Probe { SkipBonus = true };
        var bonuses = 0;
        for (var i = 0; i < 20_000; i++)
        {
            var whole = m.Fast(Bet, a);
            var before = skip.Bonuses;
            var part = m.Fast(Bet, b, skip);
            if (skip.Bonuses > before) { part += m.FastBonus(Bet, b); bonuses++; }
            Assert.Equal(Math.Min(whole, 5000L * Bet), Math.Min(part, 5000L * Bet));
        }
        Assert.True(bonuses > 50);
    }

    [Fact]
    public void Every_bet_from_the_set_pays_the_same_in_bets()
    {
        // виплати — цілі десяті ставки, множники цілі → виграш на ставці b рівно b/10 виграшу на ставці 10:
        // RTP однаковий на всіх ставках з SlotsOptions.DefaultBets (таблиця RTP у spec — одна цифра на всі)
        var m = new M();
        foreach (var bet in SlotsOptions.DefaultBets)
        {
            var a = new SeededSlotRng(new Random(9));
            var b = new SeededSlotRng(new Random(9));
            for (var i = 0; i < 4000; i++)
            {
                var w10 = m.Fast(10, a);
                Assert.Equal(w10 * bet / 10, m.Fast(bet, b));
            }
        }
    }

    // ---------------------------------------------------------------- автомат на платформі

    static int Sum(FakeStakes s, string suffix) => s.Ledger.Where(x => x.Ref.EndsWith(suffix, StringComparison.Ordinal)).Sum(x => x.Delta);

    [Fact]
    public void Machine_takes_the_bet_and_pays_the_whole_bonus_exactly_once()
    {
        var k = new SlotsKit(wallet: 100_000, game: "slot-slidy");
        Assert.IsType<SlotSlidy>(k.H.Room.Game);
        Assert.Equal("Сліди на полиці", k.H.Room.Game.Info.Title);
        long won = 0;
        for (var i = 0; i < 300; i++)
        {
            var r = k.Spin(20);
            Assert.True(r.Ok, r.Message);
            var sc = k.View.GetProperty("last").GetProperty("script");
            won += sc.GetProperty("win").GetInt32() + (sc.TryGetProperty("jackpot", out var jp) ? jp.GetInt32() : 0);
        }
        Assert.Equal(300, k.Stakes.Ledger.Count(x => x.Ref.EndsWith(":bet", StringComparison.Ordinal)));
        Assert.Equal(-300 * 20, Sum(k.Stakes, ":bet"));
        Assert.Equal(won, Sum(k.Stakes, ":win") + Sum(k.Stakes, ":jp"));
        Assert.Equal(100_000 - 6000 + won, k.Stakes.Balance("Оля"));
        Assert.Equal(k.Stakes.Balance("Оля"), k.View.GetProperty("balance").GetInt32());
        Assert.All(k.Stakes.Reasons.Where(x => x.Key.EndsWith(":bet")), x => Assert.Equal("slot-bet:slot-slidy", x.Value));
        Assert.Empty(k.Bank.Pending());
        Assert.Equal(JsonValueKind.Null, k.View.GetProperty("gamble").ValueKind);
        Assert.True(k.H.Act(0, "gamble", new { pick = "r" }) is { Ok: false });
        var table = k.View.GetProperty("table");
        Assert.Equal(5000, table.GetProperty("cap").GetInt32());
        Assert.Equal(6, table.GetProperty("cols").GetInt32());
        Assert.Equal(200, table.GetProperty("pay").GetProperty("glek").GetProperty("16").GetDouble());
        Assert.Equal(JsonValueKind.Null, k.View.GetProperty("buy").ValueKind);   // типово бонус не продається
    }

    [Fact]
    public void Buy_bonus_is_off_by_default_and_refused()
    {
        var k = new SlotsKit(wallet: 100_000, game: "slot-slidy");
        var r = k.H.Act(0, "buy", new { bet = 10 });
        Assert.False(r.Ok);
        Assert.Contains("не продається", r.Message);
        Assert.Empty(k.Stakes.Ledger);
        Assert.Equal(100_000, k.Stakes.Balance("Оля"));

        var glek = new SlotsKit(wallet: 100_000, opts: o => o.BuyBonus = true);   // автомат без купівлі
        Assert.False(glek.H.Act(0, "buy", new { bet = 10 }).Ok);
        Assert.Equal(JsonValueKind.Null, glek.View.GetProperty("buy").ValueKind);
    }

    [Fact]
    public void Bought_bonus_charges_the_price_once_and_pays_the_win()
    {
        var k = new SlotsKit(wallet: 100_000, game: "slot-slidy", opts: o => o.BuyBonus = true);
        Assert.Equal(100, k.View.GetProperty("buy").GetProperty("price").GetInt32());
        var r = k.H.Act(0, "buy", new { bet = 20 });
        Assert.True(r.Ok, r.Message);
        var sc = k.View.GetProperty("last").GetProperty("script");
        Assert.True(sc.GetProperty("bought").GetBoolean());
        Assert.Equal(2000, sc.GetProperty("price").GetInt32());
        Assert.Equal("bonusIn", sc.GetProperty("steps")[0].GetProperty("t").GetString());
        var win = sc.GetProperty("win").GetInt32() + (sc.TryGetProperty("jackpot", out var jp) ? jp.GetInt32() : 0);
        Assert.Equal(-2000, Sum(k.Stakes, ":bet"));
        Assert.Equal(win, Sum(k.Stakes, ":win") + Sum(k.Stakes, ":jp"));
        Assert.Equal(100_000 - 2000 + win, k.Stakes.Balance("Оля"));
        Assert.Equal("slot-buy:slot-slidy", Assert.Single(k.Stakes.Reasons, x => x.Key.EndsWith(":bet")).Value);

        // той самий seq — повтор запиту, нічого не робить
        var seq = k.View.GetProperty("last").GetProperty("seq").GetInt32();
        Assert.True(k.H.Act(0, "buy", new { bet = 20, seq = seq - 1 }).Ok);
        Assert.Equal(seq, k.View.GetProperty("last").GetProperty("seq").GetInt32());
        Assert.Single(k.Stakes.Ledger, x => x.Ref.EndsWith(":bet", StringComparison.Ordinal));

        // бракує на ціну — відмова без списання
        var poor = new SlotsKit(wallet: 1500, game: "slot-slidy", opts: o => o.BuyBonus = true);
        var p = poor.H.Act(0, "buy", new { bet = 20 });
        Assert.False(p.Ok);
        Assert.Contains("бонус коштує 2000", p.Message);
        Assert.Empty(poor.Stakes.Ledger);
    }

    [Fact]
    public void Buy_price_never_goes_below_the_safe_one()
    {
        var k = new SlotsKit(wallet: 100_000, game: "slot-slidy", opts: o => { o.BuyBonus = true; o.BuyPrice = 10; });
        Assert.Equal(M.MinBuyPrice, k.View.GetProperty("buy").GetProperty("price").GetInt32());
        Assert.True(k.H.Act(0, "buy", new { bet = 10 }).Ok);
        Assert.Equal(-10 * M.MinBuyPrice, Sum(k.Stakes, ":bet"));
        Assert.Equal(1000, new SlotsOptions { BuyPrice = 100_000 }.BuyPriceFor(M.MinBuyPrice));
    }

    [Fact]
    public void Machine_with_the_same_seed_spins_the_same_and_survives_save_load()
    {
        static string Run()
        {
            var k = new SlotsKit(wallet: 100_000, seed: 7, game: "slot-slidy");
            var all = new List<string>();
            for (var i = 0; i < 40; i++) { k.Spin(10); all.Add(k.View.GetProperty("last").GetProperty("script").ToString()); }
            return string.Join("\n", all);
        }
        Assert.Equal(Run(), Run());

        var k = new SlotsKit(wallet: 100_000, game: "slot-slidy");
        for (var i = 0; i < 5; i++) Assert.True(k.Spin(50).Ok);
        var saved = k.G.Save()!;
        var g2 = new SlotSlidy();
        g2.Load(saved);
        Assert.Equal(5, g2.State.Spins);
        Assert.Equal(50, g2.State.Bet);
        Assert.Equal("spin", g2.State.Last!["steps"]![0]!["t"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- симуляції (цифри — у specs/slot-slidy.md)

    sealed record Sim(double Rtp, double Sd, double Hit, double BonusEvery, double Max, long Capped);

    static Sim Simulate(int spins, int seed)
    {
        var m = new M();
        var rng = new SeededSlotRng(new Random(seed));
        var p = new M.Probe();
        double won = 0, sq = 0, max = 0;
        long hits = 0;
        for (var i = 0; i < spins; i++)
        {
            var w = m.Fast(Bet, rng, p) / (double)Bet;
            won += w; sq += w * w;
            if (w > 0) hits++;
            max = Math.Max(max, w);
        }
        var mean = won / spins;
        return new Sim(mean, Math.Sqrt(sq / spins - mean * mean), (double)hits / spins, (double)spins / Math.Max(1, p.Bonuses), max, p.Capped);
    }

    /// <summary>
    /// Розшарована оцінка: база (вхід у бонус рахується, сам бонус — ні) + частота входу × середній бонус з окремого
    /// прогону бонусів. Пряма симуляція тут безпорадна: σ оберту ≈ 21 ставка (бонус дає 54 % RTP і буває до 5000×).
    /// </summary>
    static (double Rtp, double Se, double BonusEvery, double AvgFree, double SeFree) Stratified(int spins, int bonuses, int seed)
    {
        var m = new M();
        var rng = new SeededSlotRng(new Random(seed));
        var p = new M.Probe { SkipBonus = true };
        double a = 0, a2 = 0;
        for (var i = 0; i < spins; i++) { var w = m.Fast(Bet, rng, p) / (double)Bet; a += w; a2 += w * w; }
        double b = 0, b2 = 0;
        for (var i = 0; i < bonuses; i++) { var w = m.FastBonus(Bet, rng) / (double)Bet; b += w; b2 += w * w; }
        double ma = a / spins, mb = b / bonuses, q = (double)p.Bonuses / spins;
        double va = a2 / spins - ma * ma, vb = b2 / bonuses - mb * mb;
        var se = Math.Sqrt(va / spins + q * q * vb / bonuses + mb * mb * q * (1 - q) / spins);
        return (ma + q * mb, se, 1 / q, mb, Math.Sqrt(vb / bonuses));
    }

    const double Target = 0.97;

    [Fact]
    public void Two_hundred_thousand_spins_and_a_stratified_million()
    {
        var s = Simulate(200_000, 1);
        output.WriteLine($"slot-slidy 200 тис.: RTP {s.Rtp:P2} (σ оберту {s.Sd:F1}), виграш {s.Hit:P1}, бонус 1 з {s.BonusEvery:F0}, найбільше {s.Max:F0}×, стеля {s.Capped}");
        // σ середнього на 200 тис. ≈ 21 / √200 000 ≈ 4,7 % — пряма симуляція лише грубо (±15 % ≈ 3σ)
        Assert.InRange(s.Rtp, Target - 0.15, Target + 0.15);
        Assert.InRange(s.Hit, 0.33, 0.38);
        Assert.InRange(s.BonusEvery, 140, 200);
        Assert.True(s.Max <= 5000);

        var st = Stratified(1_000_000, 50_000, 2);
        output.WriteLine($"розшаровано (1 млн + 50 тис. бонусів): RTP {st.Rtp:P2} ± {st.Se:P2}, бонус 1 з {st.BonusEvery:F0}, вільні в середньому {st.AvgFree:F1}× ± {st.SeFree:F1}");
        Assert.InRange(st.Rtp, Target - 0.025, Target + 0.02);    // σ ≈ 0,7 %
        Assert.InRange(st.BonusEvery, 150, 190);
        // куплений бонус за безпечну ціну не щедріший за базу (σ середнього бонусу на 50 тис. ≈ 1,1×)
        Assert.True((st.AvgFree - 2 * st.SeFree) / M.MinBuyPrice <= Target, $"купівля {st.AvgFree / M.MinBuyPrice:P1}");
    }

    [Fact, Trait("Category", "Perf")]
    public void Stratified_ten_million_spins_and_a_million_bonuses()
    {
        // 10 млн обертів бази + 1 млн бонусів окремо: σ ≈ 0,26 % (хвіст бонусу важкий: σ бонусу ≈ 250 ставок). Найточніша оцінка —
        // 96,60 % (1,2 млрд обертів бази + 44 млн бонусів, spec §2); тут — у межах 3σ від неї й не вище за 97 % + 2σ. Пряма —
        // 5 млн для частоти виграшу й бонусу (RTP прямо: σ ≈ 0,95 %).
        var s = Simulate(5_000_000, 3);
        output.WriteLine($"slot-slidy 5 млн: RTP {s.Rtp:P3} (σ оберту {s.Sd:F2}), виграш {s.Hit:P2}, бонус 1 з {s.BonusEvery:F1}, найбільше {s.Max:F0}×, стеля {s.Capped}");
        Assert.InRange(s.Rtp, Target - 0.03, Target + 0.03);
        Assert.InRange(s.BonusEvery, 160, 180);

        var st = Stratified(10_000_000, 1_000_000, 4);
        output.WriteLine($"розшаровано (10 млн + 1 млн бонусів): RTP {st.Rtp:P3} ± {st.Se:P3}, бонус 1 з {st.BonusEvery:F1}, вільні в середньому {st.AvgFree:F2}× ± {st.SeFree:F2} → купівля за {M.MinBuyPrice}× — {st.AvgFree / M.MinBuyPrice:P2}, за 100× — {st.AvgFree / 100:P2}");
        Assert.InRange(st.Rtp, 0.958, Target + 0.005);
        Assert.True(st.AvgFree / M.MinBuyPrice <= st.Rtp, "куплений бонус щедріший за базу");
    }
}
