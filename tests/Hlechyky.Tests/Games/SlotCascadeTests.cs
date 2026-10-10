using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;
using M = Hlechyky.Games.Impl.SlotCascadeMath;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Підставна випадковість «Розбитих глеків»: символи з черги (кожен ключ → число в середині його відрізка ваг), порожня
/// черга — по колу 9 символів, що платять (нових виграшів так не з'являється: кожного +1 на 9 нових клітинок).
/// </summary>
public sealed class CascadeRng : ISlotRng
{
    readonly Queue<double> _q = new();
    int _cycle;

    static readonly string[] Cycle = ["k1", "k2", "k3", "k4", "bowl", "pot", "makitra", "kumanets", "glek"];

    static double Mid(double[] w, int i) => (w.Take(i).Sum() + w[i] / 2) / w.Sum();

    /// <summary>Ключі за вагами бази (<paramref name="free"/> — вільних обертів).</summary>
    public CascadeRng Keys(bool free, params string[] keys)
    {
        var w = free ? M.Free : M.Base;
        foreach (var k in keys)
        {
            var code = M.Code(k);
            if (code < M.MultCode) { _q.Enqueue(Mid(w.Sym, code)); continue; }
            _q.Enqueue(Mid(w.Sym, M.MultCode));
            _q.Enqueue(Mid(w.Mult, code - M.MultCode));
        }
        return this;
    }

    /// <summary>Сире число для NextDouble (напр. вибір символу гончаря за його вагами).</summary>
    public CascadeRng Raw(double x) { _q.Enqueue(x); return this; }

    /// <summary>Ціле поле (30 клітинок по колонках) — для вільного оберту.</summary>
    public CascadeRng Field(bool free, string[][] g) => Keys(free, [.. g.SelectMany(c => c)]);

    public int Queued => _q.Count;
    public int Next(int max) => 0;
    public double NextDouble() => _q.Count > 0 ? _q.Dequeue() : Mid(M.Base.Sym, M.Code(Cycle[_cycle++ % Cycle.Length]));
}

public class SlotCascadeTests(ITestOutputHelper output)
{
    static readonly string[] Filler = ["k1", "k2", "k3", "k4", "bowl", "pot", "makitra", "kumanets", "glek"];

    /// <summary>Поле: спершу <paramref name="put"/> (по колонках, з клітинки 0), решта — наповнювач без 8 однакових.</summary>
    static string[][] Grid(params (string Key, int N)[] put)
    {
        var flat = new List<string>();
        foreach (var (k, n) in put) flat.AddRange(Enumerable.Repeat(k, n));
        var used = put.Select(p => p.Key).ToHashSet();
        var fill = Filler.Where(f => !used.Contains(f)).ToArray();
        for (var i = 0; flat.Count < M.Cells; i++) flat.Add(fill[i % fill.Length]);
        return [.. Enumerable.Range(0, M.Cols).Select(c => flat.Skip(c * M.Rows).Take(M.Rows).ToArray())];
    }

    static List<JsonObject> Steps(SlotOutcome o) => [.. o.Script["steps"]!.AsArray().Select(s => s!.AsObject())];
    static string T(JsonObject s) => (string)s["t"]!;
    static long L(JsonNode? n) => long.Parse(n!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("glek", 8, 100)]
    [InlineData("glek", 9, 100)]
    [InlineData("glek", 10, 250)]
    [InlineData("glek", 11, 250)]
    [InlineData("glek", 12, 500)]
    [InlineData("glek", 20, 500)]
    [InlineData("k1", 8, 3)]
    [InlineData("k4", 10, 12)]
    [InlineData("k3", 9, 5)]
    [InlineData("kumanets", 12, 250)]
    public void Pays_eight_or_more_anywhere_by_tier(string sym, int n, long expected)
    {
        var o = new M().ScriptFor(Grid((sym, n)), 10, new CascadeRng());
        var s = Steps(o);
        Assert.Equal(["spin", "win", "cascade"], s.Select(T));
        var item = s[1]["items"]!.AsArray().Single()!.AsObject();
        Assert.Equal(sym, (string)item["sym"]!);
        Assert.Equal(n, (int)item["n"]!);
        Assert.Equal(n, item["cells"]!.AsArray().Count);
        Assert.Equal(expected, L(item["amount"]));
        Assert.Equal(expected, L(s[1]["amount"]));
        Assert.Equal(260, (int)s[1]["hold"]!);
        Assert.Equal(expected, o.Win);
        Assert.Equal(expected, L(o.Script["win"]));
    }

    [Fact]
    public void Seven_of_a_kind_pays_nothing()
    {
        var o = new M().ScriptFor(Grid(("glek", 7)), 10, new CascadeRng());
        Assert.Equal(["spin"], Steps(o).Select(T));
        Assert.Equal(0, o.Win);
    }

    [Fact]
    public void Cascade_drops_the_rest_and_tumbles_until_calm()
    {
        // 8 k4 б'ються; перший новий згори — k1, і k1 стає 8 → другий виграш; далі наповнювач по колу — спокій.
        var first = Grid(("k4", 8), ("k1", 7));
        var rng = new CascadeRng().Keys(false, "k1");
        var o = new M().ScriptFor(first, 10, rng);
        var s = Steps(o);
        Assert.Equal(["spin", "win", "cascade", "win", "cascade"], s.Select(T));
        Assert.Equal(1, (int)s[2]["n"]!);
        Assert.Equal(2, (int)s[4]["n"]!);
        Assert.Equal("k1", (string)s[3]["items"]![0]!["sym"]!);
        Assert.Equal(8 + 3, o.Win);

        // падіння: у кожній колонці ті, що лишились (у тому ж порядку), — знизу, нові — згори
        var removed = s[2]["remove"]!.AsArray().Select(p => (c: (int)p![0]!, r: (int)p![1]!)).ToHashSet();
        Assert.Equal(8, removed.Count);
        var after = s[2]["grid"]!.AsArray();
        for (var c = 0; c < M.Cols; c++)
        {
            var keep = Enumerable.Range(0, M.Rows).Where(r => !removed.Contains((c, r))).Select(r => first[c][r]).ToList();
            var col = after[c]!.AsArray().Select(x => (string)x!).ToList();
            Assert.Equal(keep, col.Skip(M.Rows - keep.Count));
        }
        Assert.Equal("k1", (string)after[0]![0]!);

        // кінцеве поле спокійне: нема 8 однакових
        var last = s[4]["grid"]!.AsArray().SelectMany(c => c!.AsArray().Select(x => (string)x!)).ToList();
        Assert.All(M.Paying, k => Assert.True(last.Count(x => x == k) < M.MinCount));
    }

    [Fact]
    public void Eggs_multiply_the_whole_cascade_win_in_the_base()
    {
        var o = new M().ScriptFor(Grid(("x10", 1), ("x5", 1), ("glek", 8)), 10, new CascadeRng());
        var s = Steps(o);
        Assert.Equal(["spin", "win", "cascade", "mult"], s.Select(T));
        var m = s[3];
        Assert.Equal(0, (int)m["from"]!);
        Assert.Equal(15, (int)m["to"]!);
        Assert.Equal(100, L(m["seq"]));
        Assert.Equal(1400, L(m["add"]));
        Assert.False((bool)m["fs"]!);
        Assert.Equal(2, m["cells"]!.AsArray().Count);
        Assert.Equal([10, 5], m["cells"]!.AsArray().Select(c => (int)c![2]!).Order().Reverse());
        Assert.Equal(1500, o.Win);
        Assert.Equal([15], o.Script["meta"]!["mults"]!.AsArray().Select(x => (int)x!));
    }

    [Fact]
    public void Eggs_without_a_win_do_nothing()
    {
        var o = new M().ScriptFor(Grid(("x100", 2), ("glek", 7)), 10, new CascadeRng());
        Assert.Equal(["spin"], Steps(o).Select(T));
        Assert.Equal(0, o.Win);
    }

    [Fact]
    public void Furnaces_open_the_bonus_where_eggs_add_up_for_the_whole_round_and_three_retrigger()
    {
        var rng = new CascadeRng();
        // вільний 1: 8 k1 і ×5 → acc 5; вільний 2: 8 k2 і ×3 → acc 8, виграш × 8; вільний 3: 8 k3 (0,5) без писанок → без множника,
        // і 3 горна → ще 5 обертів. Після кожного виграшу 8 нових клітинок — з черги (наповнювач без виграшу).
        string[] refill = ["bowl", "pot", "makitra", "kumanets", "glek", "bowl", "pot", "makitra"];
        rng.Field(true, Grid(("x5", 1), ("k1", 8))).Keys(true, refill);
        rng.Field(true, Grid(("x3", 1), ("k2", 8))).Keys(true, refill);
        rng.Field(true, Grid(("furnace", 3), ("k3", 8))).Keys(true, refill);
        var o = new M().ScriptFor(Grid(("furnace", 4)), 10, rng);
        Assert.Equal(0, rng.Queued);
        var s = Steps(o);

        var furn = s.First(x => T(x) == "furn");
        Assert.Equal(30, L(furn["amount"]));          // 4 горна — 3× ставки
        Assert.Equal(4, furn["cells"]!.AsArray().Count);
        Assert.Equal(10, (int)s.First(x => T(x) == "bonusIn")["count"]!);

        var mults = s.Where(x => T(x) == "mult").ToList();
        Assert.Equal(2, mults.Count);
        Assert.Equal((0, 5, true), ((int)mults[0]["from"]!, (int)mults[0]["to"]!, (bool)mults[0]["fs"]!));
        Assert.Equal((5, 8), ((int)mults[1]["from"]!, (int)mults[1]["to"]!));
        Assert.Equal(4 * 8 - 4, L(mults[1]["add"]));    // k2 8 = 0,4 × 10 = 4, × 8

        var retr = s.Where(x => T(x) == "furn" && (bool?)x["fs"] == true).ToList();
        Assert.Single(retr);
        Assert.Equal(0, L(retr[0]["amount"]));
        Assert.Contains(s, x => T(x) == "fs" && (int?)x["add"] == 5);
        Assert.Equal(15, s.Count(x => T(x) == "fs" && x["left"] is not null));
        Assert.Equal(15, s.Count(x => T(x) == "spin") - 1);

        var outStep = s.Last();
        Assert.Equal("bonusOut", T(outStep));
        long fsWin = 3 * 5 + 4 * 8 + 5;              // k1 3×5, k2 4×8, k3 5 без множника
        Assert.Equal(fsWin, L(outStep["total"]));
        Assert.Equal(15, (int)outStep["spins"]!);
        Assert.Equal(8, (int)outStep["acc"]!);
        Assert.Equal(30 + fsWin, o.Win);
        Assert.Contains("bonus", o.Flags);
        var meta = o.Script["meta"]!;
        Assert.True((bool)meta["bonus"]!);
        Assert.Equal(2, (int)meta["fsMults"]!);
        Assert.Equal(8, (int)meta["acc"]!);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    public void Furnace_pay_and_bonus_need_four(int n, bool bonus)
    {
        var o = new M().ScriptFor(Grid(("furnace", n)), 10, new CascadeRng());
        Assert.Equal(bonus, Steps(o).Any(x => T(x) == "bonusIn"));
        Assert.Equal(bonus ? M.FurnacePay(n) * 10 : 0, o.Win);
        Assert.Equal(n switch { 4 => 3, 5 => 5, 6 => 100, _ => 0 }, M.FurnacePay(n));
    }

    [Fact]
    public void Retrigger_stops_at_fifty_free_spins()
    {
        // кожен вільний — 3 горна: 10 + 8 × 5 = 50, далі вже не додається
        var rng = new CascadeRng();
        for (var i = 0; i < 60; i++) rng.Field(true, Grid(("furnace", 3)));
        var o = new M().ScriptFor(Grid(("furnace", 4)), 10, rng);
        var s = Steps(o);
        Assert.Equal(50, (int)s.Last()["spins"]!);
        Assert.Equal(8, s.Count(x => T(x) == "fs" && (int?)x["add"] == 5));
    }

    [Fact]
    public void Tease_only_when_exactly_one_furnace_is_missing()
    {
        static int[] G(params int[] furnCols)
        {
            var g = new int[M.Cells];
            for (var i = 0; i < M.Cells; i++) g[i] = i % 9;
            var row = new int[M.Cols];
            foreach (var c in furnCols) g[c * M.Rows + row[c]++] = M.FurnaceCode;
            return g;
        }
        Assert.Equal([3, 4, 5], M.Tease(G(0, 1, 2), 4));
        Assert.Equal([3], M.Tease(G(0, 1, 2, 3), 4));
        Assert.Equal([1, 2, 3, 4, 5], M.Tease(G(0, 0, 0), 4));
        Assert.Empty(M.Tease(G(0, 1), 4));
        Assert.Empty(M.Tease(G(0, 1, 5), 4));
        Assert.Empty(M.Tease(G(0, 0, 1, 1), 4));     // з 2 одразу на 4 — не бракувало «рівно одного»
        Assert.Equal([2, 3, 4, 5], M.Tease(G(0, 1), 3));

        var o = new M().ScriptFor(Grid(("furnace", 3)), 10, new CascadeRng());
        Assert.Equal([1, 2, 3, 4, 5], Steps(o)[0]["tease"]!.AsArray().Select(x => (int)x!));
        Assert.True((bool)o.Script["meta"]!["tease"]!);
    }

    [Fact]
    public void Win_is_capped_at_five_thousand_bets()
    {
        // 26 глеків × (4 писанки ×100) = 500 × 400 — далеко за стелю
        var o = new M().ScriptFor(Grid(("x100", 4), ("glek", 26)), 10, new CascadeRng());
        Assert.Equal(5000 * 10, o.Win);
        Assert.Equal(50_000, L(o.Script["win"]));
        Assert.True((bool)o.Script["capped"]!);
        Assert.Contains("cap", o.Flags);
        Assert.Equal("banner", T(Steps(o).Last()));
    }

    [Fact]
    public void Same_seed_same_scripts_and_fast_path_agrees()
    {
        var m = new M();
        string Run(int seed)
        {
            var rng = new SeededSlotRng(new Random(seed));
            return string.Join("\n", Enumerable.Range(0, 300).Select(_ => m.Spin(20, rng, new JsonObject()).Script.ToJsonString()));
        }
        Assert.Equal(Run(5), Run(5));
        Assert.NotEqual(Run(5), Run(6));

        // запис сценарію й швидкий прохід тягнуть ті самі числа — і з гончарем (він бере rng так само)
        var a = new SeededSlotRng(new Random(9));
        var b = new SeededSlotRng(new Random(9));
        var potter = 0;
        for (var i = 0; i < 3000; i++)
        {
            var o = m.Spin(10, a, new JsonObject());
            if (Steps(o).Any(x => T(x) == "morph")) potter++;
            Assert.Equal(o.Win, m.Fast(10, b));
        }
        Assert.True(potter > 50, $"гончар {potter}");
    }

    static readonly HashSet<string> StepTypes = ["spin", "morph", "win", "cascade", "mult", "furn", "bonusIn", "fs", "bonusOut", "banner"];

    [Fact]
    public void Script_has_the_mock_shape_and_its_money_adds_up()
    {
        var m = new M();
        var rng = new SeededSlotRng(new Random(11));
        int bonuses = 0, mults = 0;
        for (var i = 0; i < 6000; i++)
        {
            var o = m.Spin(10, rng, new JsonObject());
            var sc = o.Script;
            Assert.Equal(10, (int)sc["bet"]!);
            Assert.IsType<JsonObject>(sc["state"]);
            foreach (var k in new[] { "casc", "mults", "bonus", "tease", "fsMults", "acc" }) Assert.True(sc["meta"]!.AsObject().ContainsKey(k), k);
            var s = Steps(o);
            Assert.Equal("spin", T(s[0]));
            Assert.All(s, x => Assert.Contains(T(x), StepTypes));
            Assert.All(s.Where(x => T(x) == "spin"), x =>
            {
                Assert.Equal(M.Cols, x["grid"]!.AsArray().Count);
                Assert.All(x["grid"]!.AsArray(), c => Assert.Equal(M.Rows, c!.AsArray().Count));
                Assert.IsType<JsonArray>(x["tease"]);
            });
            Assert.All(s.Where(x => T(x) == "win"), x =>
                Assert.Equal(L(x["amount"]), x["items"]!.AsArray().Sum(it => L(it!["amount"]))));
            // гроші сценарію: виграші каскадів + доплати писанок + горно = win (без стелі)
            var sum = s.Sum(x => T(x) switch { "win" => L(x["amount"]), "mult" => L(x["add"]), "furn" => L(x["amount"]), _ => 0 });
            Assert.Equal(sum, L(sc["win"]));
            Assert.Equal(sum, o.Win);
            var inBonus = s.FindIndex(x => T(x) == "bonusIn");
            if (inBonus >= 0)
            {
                bonuses++;
                Assert.Equal("furn", T(s[inBonus - 1]));
                Assert.Equal("bonusOut", T(s[^1]));
                var fs = s.Skip(inBonus).Sum(x => T(x) switch { "win" => L(x["amount"]), "mult" => L(x["add"]), _ => 0 });
                Assert.Equal(fs, L(s[^1]["total"]));
            }
            mults += s.Count(x => T(x) == "mult");
            Replay(s);
        }
        Assert.True(bonuses > 10, $"бонусів {bonuses}");
        Assert.True(mults > 10, $"писанок {mults}");
    }

    // ---------------------------------------------------------------- «Гончар доліпив»

    static string[][] G(JsonNode? grid) => [.. grid!.AsArray().Select(c => c!.AsArray().Select(x => (string)x!).ToArray())];
    static int CountOf(string[][] g, string k) => g.Sum(col => col.Count(x => x == k));
    static HashSet<(int, int)> Where(string[][] g, Func<string, bool> f) =>
        [.. Enumerable.Range(0, M.Cols).SelectMany(c => Enumerable.Range(0, M.Rows).Where(r => f(g[c][r])).Select(r => (c, r)))];
    static HashSet<(int, int)> Pairs(JsonNode? cells) => [.. cells!.AsArray().Select(p => ((int)p![0]!, (int)p[1]!))];

    static void Calm(string[][] g) => Assert.All(M.Paying, k => Assert.True(CountOf(g, k) < M.MinCount, $"{k} {CountOf(g, k)}"));

    /// <summary>
    /// Програє базову частину сценарію, як клієнт: поле spin → morph → win/cascade… → mult → furn, і звіряє кожен крок із
    /// полем на руках (виграш — рівно групи 8+ на ньому, падіння — по колонках, писанки й горна — ті, що лежать).
    /// </summary>
    static void Replay(List<JsonObject> s)
    {
        var g = G(s[0]["grid"]);
        HashSet<(int, int)>? won = null;
        var calm = false;
        void SettleOnce() { if (!calm) { Calm(g); calm = true; } }
        for (var i = 1; i < s.Count; i++)
        {
            var x = s[i];
            switch (T(x))
            {
                case "morph":
                    Assert.Equal(1, i);
                    Assert.Equal("potter", (string)x["why"]!);
                    var key = (string)x["cells"]![0]![2]!;
                    Assert.True(CountOf(g, key) < M.MinCount);
                    foreach (var c in x["cells"]!.AsArray())
                    {
                        int cc = (int)c![0]!, r = (int)c[1]!;
                        Assert.Equal(key, (string)c[2]!);
                        Assert.Contains(g[cc][r], M.Paying);   // горна й писанки гончар не чіпає
                        Assert.NotEqual(key, g[cc][r]);
                        g[cc][r] = key;
                    }
                    Assert.Equal(M.MinCount, CountOf(g, key));
                    break;
                case "win":
                    won = [];
                    var paid = new HashSet<string>();
                    foreach (var it in x["items"]!.AsArray())
                    {
                        var sym = (string)it!["sym"]!;
                        var on = Where(g, k => k == sym);
                        Assert.True(on.SetEquals(Pairs(it["cells"])), $"клітинки {sym}");
                        Assert.Equal(on.Count, (int)it["n"]!);
                        Assert.True(on.Count >= M.MinCount);
                        won.UnionWith(on);
                        paid.Add(sym);
                    }
                    Assert.All(M.Paying.Where(k => !paid.Contains(k)), k => Assert.True(CountOf(g, k) < M.MinCount));
                    break;
                case "cascade":
                    var rem = Pairs(x["remove"]);
                    Assert.True(won!.SetEquals(rem));
                    var next = G(x["grid"]);
                    for (var c = 0; c < M.Cols; c++)
                    {
                        var keep = Enumerable.Range(0, M.Rows).Where(r => !rem.Contains((c, r))).Select(r => g[c][r]).ToList();
                        Assert.Equal(keep, next[c].Skip(M.Rows - keep.Count));
                    }
                    g = next;
                    won = null;
                    break;
                case "mult":
                    SettleOnce();
                    var eggs = Where(g, k => k[0] == 'x');
                    Assert.True(eggs.SetEquals(Pairs(x["cells"])));
                    Assert.All(x["cells"]!.AsArray(), c => Assert.Equal("x" + (int)c![2]!, g[(int)c[0]!][(int)c[1]!]));
                    break;
                case "furn":
                    SettleOnce();
                    Assert.True(Where(g, k => k == M.Furnace).SetEquals(Pairs(x["cells"])));
                    return;   // далі — бонус (свої поля)
                default:
                    SettleOnce();
                    return;
            }
        }
        SettleOnce();
    }

    [Fact]
    public void Potter_shapes_exactly_eight_from_crockery_on_a_dead_first_drop()
    {
        // поле без виграшу: ×10, 2 горна, 5 k1, решта — наповнювач; гончар (напевно), символ за вагами: 0,1 × 120 = 12 → k1
        var first = Grid(("x10", 1), ("furnace", 2), ("k1", 5));
        var o = new M().ScriptFor(first, 10, new CascadeRng().Raw(0.1), potter: true);
        var s = Steps(o);
        Assert.Equal(["spin", "morph", "win", "cascade", "mult"], s.Select(T));
        // Next(n) підставного rng = 0 → перші три клітинки посуду, що не k1 (писанку й горна оминає): (1,3), (1,4), (2,0)
        Assert.Equal(["1,3,k1", "1,4,k1", "2,0,k1"], s[1]["cells"]!.AsArray().Select(c => $"{(int)c![0]!},{(int)c[1]!},{(string)c[2]!}"));
        var item = s[2]["items"]!.AsArray().Single()!;
        Assert.Equal(("k1", 8), ((string)item["sym"]!, (int)item["n"]!));
        Assert.Equal(3, L(item["amount"]));
        Assert.Equal(10, (int)s[4]["to"]!);       // писанка лишилась і множить виграш гончаря
        Assert.Equal(30, o.Win);
        Assert.Equal("k1", (string)o.Script["meta"]!["potter"]!);
        Assert.Equal("x10", G(s[0]["grid"])[0][0]);
        Assert.Equal(["furnace", "furnace"], G(s[0]["grid"])[0][1..3]);
        Replay(s);

        // поле з виграшем — гончар не потрібен навіть «напевно»
        var won = new M().ScriptFor(Grid(("glek", 8)), 10, new CascadeRng(), potter: true);
        Assert.DoesNotContain(Steps(won), x => T(x) == "morph");
        Assert.Null(won.Script["meta"]!["potter"]);
    }

    [Fact]
    public void Potter_comes_only_on_the_first_base_drop_without_a_win_with_its_chance()
    {
        var m = new M();
        var rng = new SeededSlotRng(new Random(21));
        int dead = 0, potter = 0, small = 0, bonusPotter = 0;
        for (var i = 0; i < 20_000; i++)
        {
            var o = m.Spin(10, rng, new JsonObject());
            var s = Steps(o);
            var first = G(s[0]["grid"]);
            var deadFirst = M.Paying.All(k => CountOf(first, k) < M.MinCount);
            if (deadFirst) dead++;
            var at = s.Select((x, j) => (x, j)).Where(p => T(p.x) == "morph").Select(p => p.j).ToList();
            Replay(s);
            if (at.Count == 0) { Assert.Null(o.Script["meta"]!["potter"]); continue; }
            Assert.Equal([1], at);                    // один раз, одразу після першого падіння, ніколи у вільних
            Assert.True(deadFirst);
            Assert.Equal("win", T(s[2]));
            var key = (string)o.Script["meta"]!["potter"]!;
            var g = G(s[0]["grid"]);
            var eggsAndFurn = Where(g, k => !M.Paying.Contains(k));
            foreach (var c in s[1]["cells"]!.AsArray()) g[(int)c![0]!][(int)c[1]!] = (string)c[2]!;
            Assert.True(eggsAndFurn.SetEquals(Where(g, k => !M.Paying.Contains(k))));   // писанки й горна — ті самі
            var item = s[2]["items"]!.AsArray().Single()!;     // платить саме доліплена вісімка, і лише вона
            Assert.Equal((key, 8), ((string)item["sym"]!, (int)item["n"]!));
            potter++;
            if (key is "k1" or "k2" or "k3" or "k4") small++;
            if ((bool)o.Script["meta"]!["bonus"]!) bonusPotter++;
        }
        var rate = (double)potter / dead;
        output.WriteLine($"мертве перше падіння {dead} з 20 тис., гончар {potter} ({rate:P2} від мертвих, {potter / 200.0:F2} % обертів), черепки {small}, з бонусом {bonusPotter}");
        // σ частки ≈ √(0,09 × 0,91 / 12 800) ≈ 0,25 % — ±1 % ≈ 4σ
        Assert.InRange(rate, M.PotterChance - 0.01, M.PotterChance + 0.01);
        Assert.InRange((double)small / potter, 0.7, 0.9);  // черепки — 80 % ваг гончаря
    }

    [Fact]
    public void Every_default_bet_pays_in_exact_proportion_so_bet_ten_is_not_richer()
    {
        // Виплати — у десятих ставки, а всі типові ставки кратні 10: max(1, round(u × ставка)) нічого не округлює
        foreach (var bet in SlotsOptions.DefaultBets)
            for (var sym = 0; sym < M.Paying.Length; sym++)
                foreach (var n in new[] { 8, 10, 12 })
                {
                    var tier = n >= 12 ? 2 : n >= 10 ? 1 : 0;
                    Assert.Equal(0, M.Pay10[sym][tier] * bet % 10);
                    Assert.Equal(M.Pay10[sym][tier] * bet / 10, M.Amount(sym, n, bet));
                }
        // той самий сід на будь-якій ставці — ті самі оберти, виграш рівно пропорційний (і стеля теж) → RTP однаковий
        var m = new M();
        foreach (var bet in SlotsOptions.DefaultBets)
        {
            var a = new SeededSlotRng(new Random(17));
            var b = new SeededSlotRng(new Random(17));
            for (var i = 0; i < 10_000; i++) Assert.Equal(m.Fast(10, a) * bet, m.Fast(bet, b) * 10);
        }
    }

    // ---------------------------------------------------------------- автомат на платформі

    sealed class Kit
    {
        public FakeStakes Stakes { get; } = new();
        public SlotsBank Bank { get; }
        public RoomHarness H { get; }

        public Kit(int wallet = 100_000, int seed = 42)
        {
            Stakes.Set("Оля", wallet);
            Bank = new SlotsBank(Stakes, new FakeStore(), () => new SlotsOptions(), new FakeClock(), new FakeSlotsWire(), defer: a => a());
            H = new RoomHarness("slot-cascade", services: RoomHarness.WithService(Bank), seed: seed);
            Assert.True(H.Solo("Оля").Ok);
        }

        public ActResult Spin(int bet) => H.Act(0, "spin", new { bet });
        public JsonElement View => H.View(0);
    }

    static int Sum(FakeStakes s, string suffix) => s.Ledger.Where(m => m.Ref.EndsWith(suffix, StringComparison.Ordinal)).Sum(m => m.Delta);

    [Fact]
    public void Machine_takes_the_bet_and_pays_the_whole_bonus_exactly_once()
    {
        var k = new Kit();
        Assert.IsType<SlotCascade>(k.H.Room.Game);
        Assert.Equal("Розбиті глеки", k.H.Room.Game.Info.Title);
        long won = 0;
        var bonus = 0;
        for (var i = 1; i <= 400; i++)
        {
            var r = k.Spin(20);
            Assert.True(r.Ok, r.Message);
            var sc = k.View.GetProperty("last").GetProperty("script");
            won += sc.GetProperty("win").GetInt32() + (sc.TryGetProperty("jackpot", out var jp) ? jp.GetInt32() : 0);
            if (sc.GetProperty("meta").GetProperty("bonus").GetBoolean()) bonus++;
        }
        Assert.Equal(400, k.Stakes.Ledger.Count(m => m.Ref.EndsWith(":bet", StringComparison.Ordinal)));
        Assert.Equal(-400 * 20, Sum(k.Stakes, ":bet"));
        Assert.Equal(won, Sum(k.Stakes, ":win") + Sum(k.Stakes, ":jp"));
        Assert.Equal(100_000 - 8000 + won, k.Stakes.Balance("Оля"));
        Assert.Equal(k.Stakes.Balance("Оля"), k.View.GetProperty("balance").GetInt32());
        Assert.All(k.Stakes.Reasons.Where(x => x.Key.EndsWith(":bet")), x => Assert.Equal("slot-bet:slot-cascade", x.Value));
        Assert.Empty(k.Bank.Pending());
        Assert.Equal(JsonValueKind.Null, k.View.GetProperty("gamble").ValueKind);
        Assert.True(k.H.Act(0, "gamble", new { pick = "r" }) is { Ok: false });
        var table = k.View.GetProperty("table");
        Assert.Equal(5000, table.GetProperty("cap").GetInt32());
        Assert.Equal(M.PotterChance, table.GetProperty("potter").GetProperty("chance").GetDouble());
        Assert.Equal(8, table.GetProperty("potter").GetProperty("size").GetInt32());
        Assert.InRange(table.GetProperty("potter").GetProperty("syms").EnumerateObject().Sum(x => x.Value.GetDouble()), 99.5, 100.5);
        Assert.Equal(M.FreeSpins, table.GetProperty("free").GetInt32());
        Assert.Equal(0.5, table.GetProperty("pay").GetProperty("k3").GetProperty("8").GetDouble());
        output.WriteLine($"бонусів за 400: {bonus}");
    }

    [Fact]
    public void Machine_with_the_same_seed_spins_the_same()
    {
        static string Run()
        {
            var k = new Kit(seed: 7);
            var all = new List<string>();
            for (var i = 0; i < 60; i++) { k.Spin(10); all.Add(k.View.GetProperty("last").GetProperty("script").ToString()); }
            return string.Join("\n", all);
        }
        Assert.Equal(Run(), Run());
    }

    // ---------------------------------------------------------------- симуляції (цифри — у specs/slots.md)

    sealed record Sim(double Rtp, double Sd, double Hit, double BonusEvery, double AvgBonus, double Max, long Capped, double Potter, double Big10, double Big50, double Big100);

    static Sim Simulate(int spins, int seed)
    {
        const int bet = 100;
        var m = new M();
        var rng = new SeededSlotRng(new Random(seed));
        var p = new M.Probe();
        double won = 0, sq = 0, max = 0, bonusWon = 0;
        long hits = 0, b10 = 0, b50 = 0, b100 = 0;
        for (var i = 0; i < spins; i++)
        {
            var before = p.Bonuses;
            var w = m.Fast(bet, rng, p) / (double)bet;
            won += w; sq += w * w;
            if (w > 0) hits++;
            if (w >= 10) b10++;
            if (w >= 50) b50++;
            if (w >= 100) b100++;
            if (p.Bonuses > before) bonusWon += w;
            max = Math.Max(max, w);
        }
        var mean = won / spins;
        return new Sim(mean, Math.Sqrt(sq / spins - mean * mean), (double)hits / spins, (double)spins / Math.Max(1, p.Bonuses),
            bonusWon / Math.Max(1, p.Bonuses), max, p.Capped, (double)p.Potter / spins, (double)b10 / spins, (double)b50 / spins, (double)b100 / spins);
    }

    /// <summary>
    /// Розшарована оцінка: база (вхід у бонус і горно рахуються, сам бонус — ні) + частота входу × середній бонус з
    /// окремого прогону бонусів. Розкид утричі менший за прямий: бонус (≈ 22 % RTP) не залежить від рідкісного входу.
    /// </summary>
    static (double Rtp, double Se, double BonusEvery, double AvgFree) Stratified(int spins, int bonuses, int seed)
    {
        const int bet = 100;
        var m = new M();
        var rng = new SeededSlotRng(new Random(seed));
        var p = new M.Probe { SkipBonus = true };
        double a = 0, a2 = 0;
        for (var i = 0; i < spins; i++) { var w = m.Fast(bet, rng, p) / (double)bet; a += w; a2 += w * w; }
        double b = 0, b2 = 0;
        for (var i = 0; i < bonuses; i++) { var w = m.FastBonus(bet, rng) / (double)bet; b += w; b2 += w * w; }
        double ma = a / spins, mb = b / bonuses, q = (double)p.Bonuses / spins;
        double va = a2 / spins - ma * ma, vb = b2 / bonuses - mb * mb;
        var se = Math.Sqrt(va / spins + q * q * vb / bonuses + mb * mb * q * (1 - q) / spins);
        return (ma + q * mb, se, 1 / q, mb);
    }

    const double Target = 0.97;

    static string Line(Sim s) => $"RTP {s.Rtp:P3} (σ оберту {s.Sd:F2}), виграш {s.Hit:P2}, бонус 1 з {s.BonusEvery:F1}, " +
        $"середній бонус {s.AvgBonus:F1}×, гончар {s.Potter:P2}, ≥10× {s.Big10:P2}, ≥50× {s.Big50:P3}, ≥100× {s.Big100:P3}, " +
        $"найбільше {s.Max:F1}×, стеля {s.Capped}";

    [Fact]
    public void Two_hundred_thousand_spins()
    {
        // Висока волатильність: розкид виграшу за оберт ≈ 6,5 ставки → похибка середнього на 200 тис. ≈ 1,5 %, тож ±2 %
        // — це лише ~1,4σ. Прямий прогін перевіряємо з ±5 % (> 3σ), а точність ±2 % — розшарованою оцінкою на 1 млн обертів
        // бази й 50 тис. бонусів (σ ≈ 0,5 %, ±2 % ≈ 4σ; ≈ 5 с). Виграш ≈ 41,2 % (σ на 200 тис. ≈ 0,11 %), бонус ≈ 1 з 125
        // (≈ 1600 входів, σ ≈ 2,5 %), гончар ≈ 5,7 % обертів.
        var s = Simulate(200_000, 1);
        output.WriteLine("slot-cascade 200 тис.: " + Line(s));
        Assert.InRange(s.Rtp, Target - 0.05, Target + 0.05);
        Assert.InRange(s.Hit, 0.405, 0.42);
        Assert.InRange(s.BonusEvery, 110, 142);
        Assert.InRange(s.Potter, 0.053, 0.062);
        Assert.True(s.Max <= 5000);

        var st = Stratified(1_000_000, 50_000, 2);
        output.WriteLine($"розшаровано (1 млн + 50 тис. бонусів): RTP {st.Rtp:P2} ± {st.Se:P2}, бонус 1 з {st.BonusEvery:F0}, вільні в середньому {st.AvgFree:F1}×");
        Assert.InRange(st.Rtp, Target - 0.02, Target + 0.02);
    }

    [Fact, Trait("Category", "Perf")]
    public void Five_million_spins_within_half_a_percent()
    {
        // Прямий прогін 5 млн: σ середнього ≈ 0,3 % — ±1 % (> 3σ). ±0,5 % — розшарованою оцінкою: 10 млн обертів бази
        // (без самих бонусів) і 500 тис. бонусів окремо, σ ≈ 0,15 % (±0,5 % > 3σ).
        var s = Simulate(5_000_000, 3);
        output.WriteLine("slot-cascade 5 млн: " + Line(s));
        Assert.InRange(s.Rtp, Target - 0.01, Target + 0.01);
        Assert.InRange(s.Hit, 0.409, 0.415);
        Assert.InRange(s.BonusEvery, 119, 131);

        var st = Stratified(10_000_000, 500_000, 4);
        output.WriteLine($"розшаровано (10 млн + 500 тис. бонусів): RTP {st.Rtp:P3} ± {st.Se:P3}, бонус 1 з {st.BonusEvery:F1}, вільні в середньому {st.AvgFree:F2}×");
        Assert.InRange(st.Rtp, Target - 0.005, Target + 0.005);
    }
}
