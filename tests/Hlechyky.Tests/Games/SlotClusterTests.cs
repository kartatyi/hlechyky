using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games.Impl;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Цвіт папороті» (slot-cluster): кластери й дикий, каскади, рівні шкали, бонус, стеля, детермінізм, форма сценарію
/// (програвач-перевірка повторює сценарій крок за кроком, як клієнт), симуляції RTP (specs/slots.md §2).
/// </summary>
public class SlotClusterTests(ITestOutputHelper output)
{
    const int Bet = 100;
    static readonly string[] K = SlotClusterMath.Keys;
    static byte Sym(string k) => (byte)Array.IndexOf(K, k);

    /// <summary>Поле без жодного кластера: квіти f1–f4 так, що сусіди завжди різні.</summary>
    static byte[] Plain()
    {
        var g = new byte[SlotClusterMath.N];
        for (var c = 0; c < 7; c++) for (var r = 0; r < 7; r++) g[c * 7 + r] = (byte)((c + 2 * r) % 4);
        return g;
    }

    static List<SlotClusterMath.Cluster> Clusters(byte[] g)
    {
        var l = new List<SlotClusterMath.Cluster>();
        SlotClusterMath.Scan(g, Bet, l);
        return l;
    }

    // ---------- кластери й дикий ----------

    [Fact]
    public void Plain_field_has_no_clusters()
    {
        Assert.Empty(Clusters(Plain()));
        Assert.Equal(0, SlotClusterMath.Scan(Plain(), Bet, null));
    }

    [Fact]
    public void Five_touching_by_sides_pay_four_do_not_and_diagonals_do_not_touch()
    {
        var g = Plain();
        for (var c = 0; c < 4; c++) g[c * 7 + 0] = Sym("comb");
        Assert.Empty(Clusters(g));                                 // четверо — мало
        g[4 * 7 + 0] = Sym("comb");
        var cl = Assert.Single(Clusters(g));
        Assert.Equal("comb", K[cl.Sym]);
        Assert.Equal(5, cl.Cells.Length);
        Assert.Equal(100, SlotClusterMath.PayOf(cl.Sym, 5, Bet));  // 1 ставка

        var d = Plain();                                           // драбинка навскіс — не кластер
        for (var i = 0; i < 6; i++) d[i * 7 + i] = Sym("comb");
        Assert.Empty(Clusters(d));
    }

    [Fact]
    public void Fern_is_wild_and_can_join_two_clusters_at_once()
    {
        var g = Plain();
        // рядок 3: candle ×4, листок, comb ×2, і ще comb ×2 під ними (рядок 4)
        for (var c = 0; c < 4; c++) g[c * 7 + 3] = Sym("candle");
        g[4 * 7 + 3] = SlotClusterMath.Fern;
        g[5 * 7 + 3] = g[6 * 7 + 3] = g[5 * 7 + 4] = g[6 * 7 + 4] = Sym("comb");
        var cl = Clusters(g).OrderBy(x => x.Sym).ToList();
        Assert.Equal(2, cl.Count);
        Assert.Equal("candle", K[cl[0].Sym]);
        Assert.Equal("comb", K[cl[1].Sym]);
        Assert.Contains(4 * 7 + 3, cl[0].Cells);                   // листок — в обох
        Assert.Contains(4 * 7 + 3, cl[1].Cells);
        Assert.Equal(5, cl[0].Cells.Length);
        Assert.Equal(5, cl[1].Cells.Length);

        var only = new byte[49];                                   // самі дикі не платять
        Array.Fill(only, SlotClusterMath.Fern);
        Assert.Empty(Clusters(only));
    }

    [Fact]
    public void Pay_ladder_follows_cluster_size()
    {
        var comb = Sym("comb");
        Assert.Equal(0, SlotClusterMath.PayOf(comb, 4, Bet));
        Assert.Equal(100, SlotClusterMath.PayOf(comb, 5, Bet));
        Assert.Equal(450, SlotClusterMath.PayOf(comb, 10, Bet));   // 9–10 — сходинка «9»
        Assert.Equal(6000, SlotClusterMath.PayOf(comb, 49, Bet));
        Assert.Equal(2, SlotClusterMath.PayOf(Sym("f1"), 6, 10));  // 0,2 × 10 — рівно, без округлення (було 0,25 → 2,5 → 3)
        Assert.Equal(1, SlotClusterMath.PayOf(Sym("f1"), 5, 1));   // не менше черепка
    }

    // ---------- програвач сценарію ----------

    sealed class Seen
    {
        public int Cascades, MaxChain, Lv1, Lv2, Lv3, Boom, Bonus, FsAdd, FsAddSum, Spins, Pere, PereFs, PereMade;
        public readonly int[] PereLeaves = new int[5];
    }

    static string[][] Grid(JsonNode n) => [.. n.AsArray().Select(col => col!.AsArray().Select(x => x!.GetValue<string>()).ToArray())];
    static byte[] Bytes(string[][] g)
    {
        var b = new byte[49];
        for (var c = 0; c < 7; c++) for (var r = 0; r < 7; r++) b[c * 7 + r] = Sym(g[c][r]);
        return b;
    }
    static (int C, int R) Cell(JsonNode? n) => (n![0]!.GetValue<int>(), n[1]!.GetValue<int>());
    static bool Wild(string k) => k is "fern" or "bloom";

    /// <summary>
    /// Програти сценарій так, як клієнт slot-cluster.js: перевірити кожен крок проти поля й правил і повернути, що бачили.
    /// </summary>
    static Seen Replay(JsonObject script, int bet, int cap = 5000)
    {
        var seen = new Seen();
        var steps = script["steps"]!.AsArray();
        var kinds = steps.Select(x => x!["t"]!.GetValue<string>()).ToList();
        Assert.Equal(bet, script["bet"]!.GetValue<int>());
        Assert.NotNull(script["state"]);
        Assert.NotNull(script["info"]);
        string[][]? g = null;
        var meter = 0;
        var done = new bool[3];
        bool inBonus = false, bonusOver = false, capped = script["cap"]?.GetValue<bool>() == true;
        long total = 0, fsWon = 0;
        int fsLeft = 0, chain = 0;
        HashSet<(int, int)>? lastWin = null;
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i]!.AsObject();
            switch (kinds[i])
            {
                case "spin":
                    g = Grid(s["grid"]!);
                    Assert.Equal(7, g.Length);
                    Assert.All(g, col => Assert.Equal(7, col.Length));
                    Assert.All(g.SelectMany(x => x), k => Assert.Contains(k, K.Take(9)));   // цвіт сам не падає
                    seen.Spins++;
                    chain = 0;
                    lastWin = null;
                    break;
                case "win":
                {
                    var want = Clusters(Bytes(g!));
                    var items = s["items"]!.AsArray();
                    Assert.Equal(want.Count, items.Count);                 // жодного пропущеного кластера
                    long sum = 0;
                    lastWin = [];
                    for (var j = 0; j < items.Count; j++)
                    {
                        var it = items[j]!;
                        var sym = it["sym"]!.GetValue<string>();
                        var cells = it["cells"]!.AsArray().Select(Cell).ToList();
                        Assert.Equal(K[want[j].Sym], sym);
                        Assert.Equal(want[j].Cells.Select(p => (p / 7, p % 7)).OrderBy(x => x), cells.OrderBy(x => x));
                        Assert.Equal(cells.Count, it["n"]!.GetValue<int>());
                        Assert.All(cells, x => Assert.True(g![x.C][x.R] == sym || Wild(g[x.C][x.R])));
                        var amount = it["amount"]!.GetValue<long>();
                        Assert.Equal(SlotClusterMath.PayOf(want[j].Sym, cells.Count, bet), amount);
                        sum += amount;
                        foreach (var x in cells) lastWin.Add(x);
                    }
                    var stepAmount = s["amount"]!.GetValue<long>();
                    if (capped && !kinds.Skip(i + 1).Contains("win")) Assert.InRange(stepAmount, 0, sum);   // урізаний стелею
                    else Assert.Equal(sum, stepAmount);
                    Assert.Equal(250, s["hold"]!.GetValue<int>());
                    total += stepAmount;
                    if (inBonus) fsWon += stepAmount;
                    chain++;
                    seen.MaxChain = Math.Max(seen.MaxChain, chain);
                    break;
                }
                case "cascade":
                {
                    var boom = s["boom"]?.GetValue<bool>() == true;
                    var rem = s["remove"]!.AsArray().Select(Cell).ToList();
                    Assert.Equal(rem.Count, rem.Distinct().Count());
                    var set = rem.ToHashSet();
                    if (boom)
                    {
                        seen.Boom++;
                        for (var c = 1; c <= 5; c++) for (var r = 1; r <= 5; r++) Assert.Contains((c, r), set);
                        if (lastWin is not null) Assert.True(lastWin.IsSubsetOf(set));
                    }
                    else
                    {
                        Assert.NotNull(lastWin);
                        Assert.Equal(lastWin!.OrderBy(x => x), set.OrderBy(x => x));
                    }
                    Assert.Equal(chain, s["n"]!.GetValue<int>());
                    Assert.Equal(meter, s["from"]!.GetValue<int>());
                    var fern = s["fern"]!.GetValue<int>();
                    Assert.Equal(boom ? meter : Math.Min(SlotClusterMath.MeterMax, meter + rem.Count), fern);
                    meter = fern;
                    // поле після падіння: у кожній колонці незгаслі зберегли порядок і лежать унизу
                    var after = Grid(s["grid"]!);
                    for (var c = 0; c < 7; c++)
                    {
                        var keep = Enumerable.Range(0, 7).Where(r => !set.Contains((c, r))).Select(r => g![c][r]).ToList();
                        Assert.Equal(keep, after[c].Skip(7 - keep.Count));
                    }
                    g = after;
                    lastWin = null;
                    seen.Cascades++;
                    break;
                }
                case "morph":
                {
                    // перелесник: одразу після spin, до пошуку кластерів; 2–4 листки на недикі клітинки свого шляху
                    Assert.Equal("perelesnyk", s["why"]!.GetValue<string>());
                    Assert.Equal("spin", kinds[i - 1]);
                    var path = s["path"]!.AsArray().Select(Cell).ToList();
                    Assert.Equal(SlotClusterMath.PereFlight, path.Count);
                    Assert.All(path, x => { Assert.InRange(x.C, 0, 6); Assert.InRange(x.R, 0, 6); });
                    // ламана від краю до краю: щокроку на клітинку вперед (по колонках чи рядках) і на 0/±1 убік
                    bool Flight(bool byCol)
                    {
                        var dir = byCol ? path[1].C - path[0].C : path[1].R - path[0].R;
                        if (Math.Abs(dir) != 1 || (byCol ? path[0].C : path[0].R) != (dir > 0 ? 0 : 6)) return false;
                        for (var j = 1; j < path.Count; j++)
                        {
                            var (a, b) = (path[j - 1], path[j]);
                            if ((byCol ? b.C - a.C : b.R - a.R) != dir || Math.Abs(byCol ? b.R - a.R : b.C - a.C) > 1) return false;
                        }
                        return true;
                    }
                    Assert.True(Flight(true) || Flight(false));
                    var cells = s["cells"]!.AsArray().Select(x => (C: x![0]!.GetValue<int>(), R: x[1]!.GetValue<int>(), Key: x[2]!.GetValue<string>())).ToList();
                    Assert.InRange(cells.Count, SlotClusterMath.PereMin, SlotClusterMath.PereMax);
                    Assert.All(cells, x => Assert.Equal("fern", x.Key));
                    Assert.All(cells, x => Assert.False(Wild(g![x.C][x.R])));
                    var at = cells.Select(x => path.IndexOf((x.C, x.R))).ToList();
                    Assert.All(at, j => Assert.True(j >= 0));                  // лише на шляху
                    Assert.Equal(at.OrderBy(x => x).Distinct(), at);           // у порядку польоту, без повторів
                    var had = Clusters(Bytes(g!)).Count > 0;
                    foreach (var x in cells) g![x.C][x.R] = x.Key;
                    if (!had && Clusters(Bytes(g!)).Count > 0) seen.PereMade++;
                    seen.Pere++;
                    if (inBonus) seen.PereFs++;
                    seen.PereLeaves[cells.Count]++;
                    break;
                }
                case "lvl":
                {
                    var n = s["n"]!.GetValue<int>();
                    Assert.False(done[n - 1]);
                    for (var j = 0; j < n - 1; j++) Assert.True(done[j]);   // рівні — по черзі
                    Assert.True(meter >= SlotClusterMath.Levels[n - 1]);
                    Assert.Empty(Clusters(Bytes(g!)));                     // рівень — лише коли виграшів нема
                    done[n - 1] = true;
                    var cells = s["cells"]!.AsArray().Select(x => (C: x![0]!.GetValue<int>(), R: x[1]!.GetValue<int>(), Key: x[2]!.GetValue<string>())).ToList();
                    if (n == 1)
                    {
                        seen.Lv1++;
                        Assert.InRange(cells.Count, 3, 6);
                        Assert.All(cells, x => Assert.Equal("fern", x.Key));
                        Assert.All(cells, x => Assert.False(Wild(g![x.C][x.R])));
                        Assert.Equal(cells.Count, cells.Select(x => (x.C, x.R)).Distinct().Count());
                    }
                    else if (n == 2)
                    {
                        seen.Lv2++;
                        var from = s["from"]!.GetValue<string>();
                        var to = s["to"]!.GetValue<string>();
                        Assert.Contains(from, new[] { "f1", "f2", "f3", "f4" });
                        Assert.Contains(to, new[] { "f1", "f2", "f3", "f4" });
                        Assert.NotEqual(from, to);
                        var all = Enumerable.Range(0, 49).Where(p => g![p / 7][p % 7] == from).Select(p => (p / 7, p % 7));
                        Assert.Equal(all.OrderBy(x => x), cells.Select(x => (x.C, x.R)).OrderBy(x => x));
                        Assert.All(cells, x => Assert.Equal(to, x.Key));
                    }
                    else
                    {
                        seen.Lv3++;
                        Assert.Equal(9, cells.Count);
                        Assert.All(cells, x => { Assert.Equal("bloom", x.Key); Assert.InRange(x.C, 2, 4); Assert.InRange(x.R, 2, 4); });
                        Assert.Contains(kinds[i + 1], new[] { "win", "cascade" });   // далі — вибух
                    }
                    foreach (var x in cells) g![x.C][x.R] = x.Key;
                    break;
                }
                case "fern":
                    meter = s["to"]!.GetValue<int>();
                    Assert.Equal(0, meter);
                    if (s["end"]?.GetValue<bool>() == true) Assert.Equal(steps.Count - 1, i);
                    else done = new bool[3];
                    break;
                case "bonusIn":
                    Assert.False(inBonus);
                    Assert.Equal(1, seen.Lv3);
                    Assert.Equal(5, s["count"]!.GetValue<int>());
                    Assert.Equal("Цвіт папороті", s["title"]!.GetValue<string>());
                    inBonus = true;
                    fsLeft = 5;
                    seen.Bonus++;
                    break;
                case "fs":
                    Assert.True(inBonus);
                    if (s["add"] is { } add)
                    {
                        var a = add.GetValue<int>();
                        Assert.InRange(a, 1, 3);
                        fsLeft += a;
                        seen.FsAdd++;
                        seen.FsAddSum += a;
                        Assert.True(5 + seen.FsAddSum <= 15);
                    }
                    else
                    {
                        Assert.Equal(--fsLeft, s["left"]!.GetValue<int>());
                        Assert.Equal("spin", kinds[i + 1]);
                    }
                    break;
                case "bonusOut":
                    Assert.True(inBonus);
                    Assert.Equal(fsWon, s["total"]!.GetValue<long>());
                    if (!capped) Assert.Equal(0, fsLeft);
                    bonusOver = true;
                    break;
                case "jackpot":
                    break;
                default:
                    Assert.Fail("невідомий крок " + kinds[i]);
                    break;
            }
        }
        Assert.Equal("fern", kinds[^1]);
        Assert.Equal(inBonus, bonusOver);
        Assert.Equal(total, script["win"]!.GetValue<long>());
        Assert.True(total <= (long)cap * bet);
        if (capped) Assert.Equal((long)cap * bet, total);
        var info = script["info"]!;
        Assert.Equal(inBonus, info["bonus"]!.GetValue<bool>());
        Assert.Equal(seen.MaxChain, info["chain"]!.GetValue<int>());
        Assert.Equal(seen.Pere, info["pere"]?.GetValue<int>() ?? 0);
        return seen;
    }

    static JsonObject Script(SlotClusterMath m, int bet, int seed) =>
        m.Spin(bet, new SeededSlotRng(new Random(seed)), new JsonObject()).Script;

    [Fact]
    public void Scripts_replay_step_by_step_like_the_client()
    {
        var m = new SlotClusterMath();
        var all = new Seen();
        for (var seed = 0; seed < 1500; seed++)
        {
            var s = Replay(Script(m, Bet, seed), Bet);
            all.Cascades += s.Cascades; all.Lv1 += s.Lv1; all.Lv2 += s.Lv2; all.Lv3 += s.Lv3; all.Boom += s.Boom;
            all.Bonus += s.Bonus; all.FsAdd += s.FsAdd; all.MaxChain = Math.Max(all.MaxChain, s.MaxChain);
            all.Pere += s.Pere; all.PereFs += s.PereFs; all.PereMade += s.PereMade;
            for (var j = 0; j < 5; j++) all.PereLeaves[j] += s.PereLeaves[j];
        }
        output.WriteLine($"1500 обертів: каскадів {all.Cascades}, найдовший {all.MaxChain}, світлячки {all.Lv1}, русалка {all.Lv2}, цвіт {all.Lv3}, бонусів {all.Bonus}, +3 {all.FsAdd}, перелесників {all.Pere} (у вільних {all.PereFs}, листків 2/3/4: {all.PereLeaves[2]}/{all.PereLeaves[3]}/{all.PereLeaves[4]}, склали виграш з нічого {all.PereMade})");
        Assert.True(all.Cascades > 500);
        Assert.True(all.MaxChain >= 3);
        Assert.True(all.Lv1 > 50 && all.Lv2 > 10 && all.Lv3 > 3);
        Assert.True(all.Bonus > 3);
        Assert.True(all.Pere > 60 && all.PereFs > 0 && all.PereMade > 10);
        Assert.All(all.PereLeaves[2..], x => Assert.True(x > 15));
    }

    [Fact]
    public void Bonus_runs_free_spins_with_meter_carried_and_plus_three_on_a_second_bloom()
    {
        var m = new SlotClusterMath();
        int bonuses = 0, adds = 0;
        for (var seed = 0; seed < 30_000 && (bonuses < 30 || adds < 1); seed++)
        {
            var r = m.Play(Bet, new SeededSlotRng(new Random(seed)), false);
            if (!r.Bonus) continue;
            var sc = Script(m, Bet, seed);
            var s = Replay(sc, Bet);
            bonuses++;
            adds += s.FsAdd;
            Assert.Equal(1 + 5 + s.FsAddSum, s.Spins);                    // основний оберт + вільні
            Assert.Equal(r.FreeSpins, s.Spins - 1);
            Assert.Equal(r.FsWon, sc["info"]!["fsWon"]!.GetValue<long>());
        }
        output.WriteLine($"бонусів {bonuses}, повторних цвітів (+3) {adds}");
        Assert.True(bonuses >= 30);
        Assert.True(adds >= 1);
    }

    [Fact]
    public void Same_seed_gives_the_same_script_and_play_matches_spin()
    {
        var m = new SlotClusterMath();
        var pere = 0;
        for (var seed = 0; seed < 300; seed++)
        {
            var a = Script(m, 50, seed).ToJsonString();
            Assert.Equal(a, Script(m, 50, seed).ToJsonString());
            var p = m.Play(50, new SeededSlotRng(new Random(seed)), false);
            var j = JsonNode.Parse(a)!;
            Assert.Equal(j["win"]!.GetValue<long>(), p.Win);   // без сценарію — той самий оберт (і той самий перелесник)
            Assert.Equal(j["info"]!["pere"]?.GetValue<int>() ?? 0, p.Pere);
            pere += p.Pere;
        }
        Assert.True(pere > 10);
        Assert.NotEqual(Script(m, 50, 1).ToJsonString(), Script(m, 50, 2).ToJsonString());
    }

    [Fact]
    public void Cap_cuts_the_win_and_ends_the_spin()
    {
        var m = new SlotClusterMath(cap: 3);
        var hits = 0;
        for (var seed = 0; seed < 5000 && hits < 20; seed++)
        {
            var o = m.Spin(Bet, new SeededSlotRng(new Random(seed)), new JsonObject());
            Assert.True(o.Win <= 300);
            if (!o.Flags.Contains("cap")) continue;
            hits++;
            Assert.Equal(300, o.Win);
            Assert.True(o.Script["cap"]!.GetValue<bool>());
            Replay(o.Script, Bet, cap: 3);
            // після урізаного виграшу — нічого, крім закриття бонусу й шкали
            var kinds = o.Script["steps"]!.AsArray().Select(x => x!["t"]!.GetValue<string>()).ToList();
            Assert.All(kinds.Skip(kinds.LastIndexOf("win") + 1), t => Assert.Contains(t, new[] { "bonusOut", "fern" }));
        }
        Assert.True(hits >= 20);
        Assert.Equal(5000, new SlotClusterMath().Cap);
    }

    [Fact]
    public void Table_has_the_paytable_levels_and_perelesnyk()
    {
        var t = new SlotClusterMath().Table();
        Assert.Equal(60, t["pay"]!["comb"]!["16"]!.GetValue<double>());
        Assert.Equal(0.2, t["pay"]!["f1"]!["5"]!.GetValue<double>());
        Assert.Equal(22, t["weights"]!["f1"]!.GetValue<double>());
        Assert.Equal("fern", t["wild"]!.GetValue<string>());
        Assert.Equal(5000, t["cap"]!.GetValue<int>());
        Assert.Equal(new[] { 11, 30, 52 }, t["levels"]!.AsArray().Select(x => x!.GetValue<int>()));
        var pe = t["pere"]!;
        Assert.Equal(SlotClusterMath.PereChance, pe["chance"]!.GetValue<double>());
        Assert.Equal(12, pe["oneIn"]!.GetValue<int>());
        Assert.Equal(2, pe["min"]!.GetValue<int>());
        Assert.Equal(4, pe["max"]!.GetValue<int>());
        Assert.True(pe["fs"]!.GetValue<bool>());
        Assert.Contains("Перелесник", t["rules"]!.GetValue<string>());
    }

    // ---------- перелесник і ставки ----------

    [Fact]
    public void Perelesnyk_flies_over_about_one_field_in_twelve()
    {
        var m = new SlotClusterMath();
        var rng = new SeededSlotRng(new Random(5));
        long fields = 0, pere = 0;
        for (var i = 0; i < 100_000; i++)
        {
            var r = m.Play(Bet, rng, false);
            fields += 1 + r.FreeSpins;          // і у вільних — той самий шанс
            pere += r.Pere;
        }
        var p = (double)pere / fields;
        output.WriteLine($"перелесник: {pere} з {fields} полів = 1 з {1 / p:F2}");
        // σ частки на ~100 тис. полів ≈ 0,09 п. п.; допуск ±0,35 п. п. — ≈ 4σ
        Assert.InRange(p, SlotClusterMath.PereChance - 0.0035, SlotClusterMath.PereChance + 0.0035);
    }

    [Fact]
    public void Every_bet_from_10_to_500_pays_the_same_multiple_no_rounding()
    {
        // усі виплати кратні 0,1 ставки, а ставки — 10…500 (кратні 10): × ставку — ціле, округлення нема
        foreach (var row in SlotClusterMath.Pay)
            foreach (var v in row) Assert.Equal(Math.Round(v * 10), v * 10, 9);
        var m = new SlotClusterMath();
        for (var seed = 0; seed < 1500; seed++)
        {
            var w10 = m.Play(10, new SeededSlotRng(new Random(seed)), false).Win;
            foreach (var bet in new[] { 20, 50, 100, 200, 500 })
                Assert.Equal(w10 * bet / 10, m.Play(bet, new SeededSlotRng(new Random(seed)), false).Win);
        }
    }

    // ---------- на сервері ----------

    [Fact]
    public void Cluster_machine_plays_through_the_shared_base()
    {
        var k = new SlotsKit(wallet: 100_000, game: "slot-cluster");
        Assert.Equal("Цвіт папороті", k.G.Info.Title);
        long won = 0;
        for (var i = 1; i <= 60; i++)
        {
            var r = k.Spin(50);
            Assert.True(r.Ok, r.Message);
            var last = k.View.GetProperty("last");
            Assert.Equal(i, last.GetProperty("seq").GetInt32());
            var sc = JsonNode.Parse(last.GetProperty("script").GetRawText())!.AsObject();
            Replay(sc, 50);
            won += sc["win"]!.GetValue<long>() + (sc["jackpot"]?.GetValue<long>() ?? 0);
        }
        Assert.Equal(100_000 - 60 * 50 + won, k.Stakes.Balance("Оля"));
        Assert.Equal(JsonValueKind.Null, k.View.GetProperty("gamble").ValueKind);   // Ворожки тут нема
        Assert.False(k.H.Act(0, "gamble", new { pick = "r" }).Ok);
        Assert.Equal(5000, k.View.GetProperty("table").GetProperty("cap").GetInt32());
        Assert.Equal(100_000, k.View.GetProperty("mustHit").GetInt32());
    }

    // ---------- математика ----------

    /// <summary>Симуляція кусками з окремими сідами (паралельно, але детерміновано).</summary>
    static (double Rtp, double Hit, double Lv1, double Lv2, double Lv3, double Big, double Max, double Sd) Simulate(int spins, int seed, int bet = Bet)
    {
        const int parts = 8;
        var m = new SlotClusterMath();
        var res = new (double Won, double Sq, long Hit, long L1, long L2, long L3, long Big, long Max)[parts];
        Parallel.For(0, parts, new ParallelOptions { MaxDegreeOfParallelism = 4 }, p =>
        {
            var rng = new SeededSlotRng(new Random(seed * 1000 + p));
            double won = 0, sq = 0;
            long hit = 0, l1 = 0, l2 = 0, l3 = 0, big = 0, max = 0;
            for (var i = 0; i < spins / parts; i++)
            {
                var r = m.Play(bet, rng, false);
                var x = (double)r.Win / bet;
                won += x; sq += x * x;
                if (r.Win > 0) hit++;
                if (r.Lv[0] > 0) l1++;
                if (r.Lv[1] > 0) l2++;
                if (r.Lv[2] > 0) l3++;
                if (x >= 10) big++;
                max = Math.Max(max, r.Win);
            }
            res[p] = (won, sq, hit, l1, l2, l3, big, max);
        });
        double n = spins / parts * parts, mean = res.Sum(x => x.Won) / n;
        return (mean, res.Sum(x => x.Hit) / n, res.Sum(x => x.L1) / n, res.Sum(x => x.L2) / n, res.Sum(x => x.L3) / n,
            res.Sum(x => x.Big) / n, res.Max(x => x.Max) / (double)bet, Math.Sqrt(res.Sum(x => x.Sq) / n - mean * mean));
    }

    /// <summary>
    /// RTP бази — оцінка з 68 млн обертів по 100 (specs/slots.md §2): 97,0 % ± 0,07 %. σ оберту ≈ 6,1 ставки, тож похибка
    /// на 200 тис. — 1,4 % (тест: ±3 %, 2,2σ), на 5 млн — 0,27 % (Perf: ±0,8 %, 3σ; сід 2 дає 96,33 %, −2,5σ — тому
    /// не ±0,5 %, що було б лотереєю).
    /// </summary>
    const double Rtp = 0.970;

    void Print(string what, (double Rtp, double Hit, double Lv1, double Lv2, double Lv3, double Big, double Max, double Sd) r) =>
        output.WriteLine($"slot-cluster {what}: RTP {r.Rtp:P2}, виграш {r.Hit:P2}, світлячки {r.Lv1:P2} (1 з {1 / r.Lv1:F1}), русалка {r.Lv2:P2} (1 з {1 / r.Lv2:F0}), цвіт {r.Lv3:P3} (1 з {1 / r.Lv3:F0}), ≥10× {r.Big:P2}, найбільше {r.Max:F1}×, σ {r.Sd:F2}");

    [Fact]
    public void Two_hundred_thousand_spins_near_target()
    {
        var r = Simulate(200_000, 1);
        Print("200 тис.", r);
        Assert.InRange(r.Rtp, Rtp - 0.03, Rtp + 0.03);
        Assert.InRange(r.Hit, 0.465, 0.48);         // ≥ 45 % (було 40,7 %)
        Assert.InRange(r.Lv1, 0.137, 0.157);        // 1 з 6,8 (було 1 з 9,8)
        Assert.InRange(r.Lv2, 0.046, 0.055);        // 1 з 19,8 (було 1 з 32)
        Assert.InRange(r.Lv3, 0.0125, 0.0153);      // 1 з 72 (було 1 з 99)
        Assert.True(r.Max <= 5000);
    }

    [Fact, Trait("Category", "Perf")]
    public void Five_million_spins_within_three_sigma()
    {
        var r = Simulate(5_000_000, 2);
        Print("5 млн", r);
        Assert.InRange(r.Rtp, Rtp - 0.008, Rtp + 0.008);
        Assert.InRange(r.Hit, 0.468, 0.476);
        Assert.InRange(r.Lv3, 0.0133, 0.0145);
    }
}
