using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>Ядро вечірки без кімнати (S1.2): карта, клітинки, предмети, події, дуель, Пізній вечір, фінал, Save/Load.</summary>
public sealed class VechirkaCoreTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 6, 20, 0, 0, TimeSpan.Zero);

    public static readonly VechirkaPoolEntry[] TestPool =
    [
        new("tyr", "Ярмарковий тир", "стріляй", "tap", 2, 1, 2, 8, 60_000),
        new("skyrta", "Скирта", "клади", "tap", 2, 1, 2, 8, 60_000),
        new("hostyntsi", "Гостинці", "лови", "tap", 2, 1, 2, 8, 55_000),
        new("geese", "Порахуй гусей", "лічи", "brain", 1, 1, 2, 8, 120_000),
        new("icefloe", "Крижина", "штовхай", "move", 1, 1, 2, 8, 90_000),
        new("pong", "Понг", "відбивай", "move", 3, 0, 2, 2, 60_000),
    ];

    sealed class Table
    {
        public VechirkaCore C = null!;
        public VechirkaStubMg Mg = null!;
        public DateTimeOffset Now = T0;

        public void Run(int ms, int step = 50)
        {
            for (var t = 0; t < ms; t += step) { Now = Now.AddMilliseconds(step); C.Advance(Now); }
        }

        /// <summary>Крутити, доки людина не мусить вирішувати (busy минув).</summary>
        public void Settle(int maxMs = 30_000)
        {
            for (var t = 0; t < maxMs; t += 50)
            {
                if (C.S.Busy is null && C.S.Phase is "turn" or "aim" or "prompt" or "pick" or "card" or "late" or "done") return;
                Now = Now.AddMilliseconds(50); C.Advance(Now);
            }
        }

        public void Act(int i, string a, object? p = null) =>
            C.Act(i, a, JsonSerializer.SerializeToElement(p ?? new { }), Now);

        public int Cur => C.S.Cur!.Value;
        public VechirkaPlayer P(int i) => C.S.P[i];

        /// <summary>Поставити поточного на from і піти рівно на n кроків вибирайком.</summary>
        public void Jump(string from, int n)
        {
            var p = P(Cur);
            p.Pos = from;
            p.Items.Remove("pick");
            p.Items.Insert(0, "pick");
            Act(Cur, "item", new { k = "pick" });
            Act(Cur, "aim", new { n });
            Settle();
        }

        public void Answer(string k)
        {
            var pr = C.S.Pr!;
            Act(pr.Who, "pick", new { o = pr.Options.FindIndex(o => o.K == k) });
            Settle();
        }
    }

    static Table New(int n = 3, bool bots = false, int len = 45, ulong seed = 7, IReadOnlyList<VechirkaPoolEntry>? pool = null)
    {
        var map = VechirkaMap.Load("selo");
        var st = new VechirkaState { Len = len };
        for (var i = 0; i < n; i++)
            st.P.Add(new VechirkaPlayer { Nick = bots ? null : $"Гравець{i}", Bot = bots, Name = bots ? VechirkaRules.BotNames[i] : $"Гравець{i}" });
        var t = new Table { Mg = new VechirkaStubMg(seed + 1) };
        t.C = new VechirkaCore(map, st, t.Mg, pool ?? TestPool);
        t.C.Start(seed, t.Now);
        return t;
    }

    /// <summary>Стіл людей у фазі turn першого гравця.</summary>
    static Table Turn(int n = 3, ulong seed = 7)
    {
        var t = New(n, seed: seed);
        t.Settle();
        Assert.Equal("turn", t.C.S.Phase);
        return t;
    }

    static int Other(Table t, int k = 1) => t.C.S.Order[k];

    // ---------- карта ----------

    [Fact]
    public void Selo_map_loads_and_passes_its_checks()
    {
        var m = VechirkaMap.Load("selo");
        Assert.Equal(48, m.Nodes.Count);
        Assert.Equal(8, m.Stands.Count);
        Assert.All(m.Stands, s => Assert.Single(m.Prev[s]));
        Assert.Equal(3, m.Forks.Count);
        Assert.Equal(4, m.Forward("m6", "m10", false));
        Assert.Equal(5, m.Forward("m27", "m13", true));
        Assert.Null(m.ForwardPath("m27", "p0", false));
        Assert.Equal(1, m.Dist("m1", "m0"));
    }

    [Fact]
    public void Map_with_a_stand_that_has_two_ways_in_is_rejected()
    {
        var json = File.ReadAllText(Path.Combine(Paths.Root, "data", "vechirka", "maps", "selo.json"));
        var two = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        two["stands"]!.AsArray().Add("m13");   // у m13 входять і m12, і p3
        Assert.Throws<InvalidDataException>(() => VechirkaMap.Parse(two.ToJsonString()));
        var dead = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var edges = dead["edges"]!.AsArray();
        edges.RemoveAt(edges.Count - 1);       // p3 → m13: з p3 нема виходу
        Assert.Throws<InvalidDataException>(() => VechirkaMap.Parse(dead.ToJsonString()));
    }

    // ---------- числа ----------

    [Theory]
    [InlineData(30, 2, 13)] [InlineData(30, 8, 7)] [InlineData(45, 2, 19)] [InlineData(45, 5, 13)]
    [InlineData(45, 8, 10)] [InlineData(60, 2, 20)] [InlineData(60, 5, 18)] [InlineData(60, 8, 14)]
    public void Rounds_follow_the_target_minutes(int min, int n, int rounds) =>
        Assert.Equal(rounds, VechirkaRules.Rounds(min, n));

    [Fact]
    public void Shared_places_split_their_sum_rounding_up()
    {
        Assert.Equal([8, 8, 2], VechirkaRules.Pay([1, 1, 3]));
        Assert.Equal([10, 6, 2, 2], VechirkaRules.Pay([1, 2, 3, 3]));
        Assert.Equal([10, 3], VechirkaRules.Pay([1, 2]));
        Assert.Equal([7, 7], VechirkaRules.Pay([1, 1]));
    }

    [Fact]
    public void Score_orders_gleks_then_coins_then_wins()
    {
        Assert.True(VechirkaRules.Score(0, 49, 15) < VechirkaRules.Score(0, 50, 0));
        Assert.True(VechirkaRules.Score(1, 0, 0) > VechirkaRules.Score(0, 9999, 99));
        Assert.True(VechirkaRules.Score(2, 10, 3) > VechirkaRules.Score(2, 10, 2));
    }

    // ---------- цілий вечір ----------

    [Theory]
    [InlineData(2, 30)] [InlineData(3, 30)] [InlineData(5, 30)] [InlineData(8, 30)] [InlineData(4, 45)]
    public void Bots_alone_play_the_whole_evening_to_the_end(int n, int len)
    {
        var t = New(n, bots: true, len: len);
        var phases = new HashSet<string>();
        for (var k = 0; k < 400_000 && !t.C.S.Done; k++) { t.Run(100, 100); phases.Add(t.C.S.Phase); }
        Assert.True(t.C.S.Done, $"застрягли у {t.C.S.Phase}, коло {t.C.S.Round}/{t.C.S.Rounds}");
        Assert.Contains(t.C.Out, o => o.Kind == "finish");
        Assert.Equal(VechirkaRules.Rounds(len, n), t.C.S.Rounds);
        Assert.True(t.Mg.Played.Count >= t.C.S.Rounds);
        Assert.True(t.C.S.P.Sum(p => p.Gleks) > 0, "за вечір ніхто не купив глека");
        Assert.True(t.C.S.Late);
        foreach (var ph in new[] { "intro", "order", "turn", "walk", "pick", "results", "late", "final" }) Assert.Contains(ph, phases);
        // ігровий час у межах R·N·30 + R·20 с
        Assert.True(t.Now - T0 < TimeSpan.FromSeconds(t.C.S.Rounds * n * 30 + t.C.S.Rounds * 20), $"{(t.Now - T0).TotalMinutes:0} хв");
    }

    [Fact]
    public void Humans_who_never_act_doze_off_and_the_evening_still_ends()
    {
        var t = New(3, len: 30);
        for (var k = 0; k < 400_000 && !t.C.S.Done; k++) t.Run(200, 100);
        Assert.True(t.C.S.Done);
        Assert.All(t.C.S.P, p => Assert.True(p.Auto));
    }

    [Fact]
    public void Same_seed_same_evening()
    {
        string Play()
        {
            var t = New(4, bots: true, len: 30, seed: 99);
            for (var k = 0; k < 400_000 && !t.C.S.Done; k++) t.Run(100, 100);
            return t.C.Save();
        }
        Assert.Equal(Play(), Play());
        var other = New(4, bots: true, len: 30, seed: 100);
        for (var k = 0; k < 400_000 && !other.C.S.Done; k++) other.Run(100, 100);
        Assert.NotEqual(Play(), other.C.Save());
    }

    [Fact]
    public void Load_of_save_in_every_phase_keeps_the_evening_going()
    {
        var t = New(3, bots: true, len: 30, seed: 5);
        var seen = new HashSet<string>();
        for (var k = 0; k < 400_000 && !t.C.S.Done; k++)
        {
            t.Run(100, 100);
            var ph = t.C.S.Phase + (t.C.S.Pr?.Kind ?? "");
            if (!seen.Add(ph)) continue;
            var json = t.C.Save();
            var back = VechirkaCore.Load(json, t.C.Map, new VechirkaStubMg(3), TestPool, t.Now);
            Assert.Equal(t.C.S.Round, back.S.Round);
            Assert.Equal(t.C.S.P.Select(p => (p.Pos, p.Coins, p.Gleks)), back.S.P.Select(p => (p.Pos, p.Coins, p.Gleks)));
            Assert.Equal(t.C.S.Phase == "mg" ? "card" : t.C.S.Phase, back.S.Phase);
            var now = t.Now;
            for (var j = 0; j < 400_000 && !back.S.Done; j++) { now = now.AddMilliseconds(100); back.Advance(now); }
            Assert.True(back.S.Done, $"після Load у фазі {ph} вечір застряг у {back.S.Phase}");
        }
        Assert.Contains("results", seen);
        Assert.Contains(seen, x => x.StartsWith("prompt"));
    }

    // ---------- клітинки ----------

    [Fact]
    public void Coin_trap_church_and_well_do_what_they_say()
    {
        var t = Turn();
        var i = t.Cur;
        var c0 = t.P(i).Coins;
        t.Jump("m0", 1);   // m1 +3
        Assert.Equal(c0 + 3, t.P(i).Coins);
        t.Settle();

        var j = t.Cur;
        t.P(j).Coins = 10;
        t.Jump("m3", 1);   // m4 −3 у скарбничку
        Assert.Equal(7, t.P(j).Coins);
        Assert.Equal(3, t.C.S.Bank);
        t.Settle();

        var k = t.Cur;
        t.P(k).Coins = 0; t.P(k).Bump = true;
        t.Jump("m1", 1);   // церква +5, гуля знята (вибирайко з гулею — 1..6)
        Assert.Equal(5, t.P(k).Coins);
        Assert.False(t.P(k).Bump);
    }

    [Fact]
    public void Late_evening_doubles_coins_and_traps()
    {
        var t = Turn();
        t.C.S.Late = true;
        var i = t.Cur;
        t.P(i).Coins = 10;
        t.Jump("m0", 1);
        Assert.Equal(16, t.P(i).Coins);
        t.Settle();
        var j = t.Cur;
        t.P(j).Coins = 10;
        t.Jump("m3", 1);
        Assert.Equal(4, t.P(j).Coins);
    }

    [Fact]
    public void Wolf_gives_a_bump_unless_a_charm_saves()
    {
        var t = Turn();
        var i = t.Cur;
        t.C.S.Stand = "m29";
        t.Jump("l0", 1);
        Assert.True(t.P(i).Bump);
        Assert.Equal(1, t.P(i).S.Traps);
        t.Settle();
        var j = t.Cur;
        t.P(j).Items.Add("charm");
        t.Jump("l0", 1);
        Assert.False(t.P(j).Bump);
        Assert.DoesNotContain("charm", t.P(j).Items);
    }

    [Fact]
    public void Bank_pays_at_most_fifteen_and_passing_costs_two()
    {
        var t = Turn();
        var i = t.Cur;
        t.C.S.Bank = 20;
        t.P(i).Coins = 0;
        t.Jump("m15", 1);
        Assert.Equal(15, t.P(i).Coins);
        Assert.Equal(5, t.C.S.Bank);
        Assert.Equal(1, t.P(i).S.Banks);
        t.Settle();
        var j = t.Cur;
        t.C.S.Stand = "m3";
        t.P(j).Coins = 10;
        t.Jump("m15", 2);   // повз 🐷 на m17 (+3)
        Assert.Equal(7, t.C.S.Bank);
        Assert.Equal(11, t.P(j).Coins);
    }

    [Fact]
    public void Chest_gives_an_item_or_three_coins()
    {
        var items = 0; var empty = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var t = Turn(seed: seed);
            var i = t.Cur;
            t.C.S.Stand = "m29";
            t.P(i).Coins = 0;
            t.Jump("m8", 1);   // m9 — скриня (стенд відсунули)
            if (t.P(i).Items.Count == 1) items++;
            else { Assert.Equal(3, t.P(i).Coins); empty++; }
            Assert.Equal(1, t.P(i).S.Chests);
        }
        Assert.True(items > 25 && empty > 0, $"{items}/{empty}");
    }

    [Fact]
    public void Full_hand_asks_what_to_drop_and_the_default_drops_the_new_one()
    {
        var t = Turn();
        var i = t.Cur;
        t.C.S.Stand = "m29";
        t.C.NextEvent = "gift";
        t.P(i).Items.AddRange(["horse", "key", "charm"]);
        t.Jump("m4", 1);   // m5 — подія «гостинець»
        Assert.Equal("discard", t.C.S.Pr!.Kind);
        Assert.Equal(4, t.C.S.Pr.Options.Count);
        var fresh = t.C.S.Pr.NewItem!;
        t.Act(i, "pick", new { o = 0 });   // викинути підкову
        Assert.Equal(["key", "charm", fresh], t.P(i).Items);
    }

    // ---------- лавка, крамниця, пором, розвилка ----------

    [Fact]
    public void Stand_sells_one_glek_per_turn_and_moves_away()
    {
        var t = Turn();
        var i = t.Cur;
        t.C.S.Stand = "m3";
        t.P(i).Coins = 45;
        t.Jump("m2", 3);
        Assert.Equal("stand", t.C.S.Pr!.Kind);
        t.Act(i, "pick", new { o = 0 });
        Assert.Equal(1, t.P(i).Gleks);
        Assert.Equal(25, t.P(i).Coins);
        Assert.NotEqual("m3", t.C.S.Stand);
        Assert.True(t.C.Map.Dist("m3", t.C.S.Stand) >= 8 || t.C.Map.Stands.Count(s => t.C.Map.Dist("m3", s) >= 8) == 0);
        t.C.S.Stand = "m4";   // переїхала прямо під ноги — цього ходу вже не продає
        t.Settle();
        Assert.NotEqual("prompt", t.C.S.Phase);
        Assert.Equal("m5", t.P(i).Pos);
        Assert.Equal(1, t.P(i).Gleks);
    }

    [Fact]
    public void Stand_default_is_to_buy_and_no_coins_means_no_question()
    {
        var t = Turn();
        var i = t.Cur;
        t.C.S.Stand = "m3";
        t.P(i).Coins = 20;
        t.Jump("m2", 1);
        Assert.Equal("stand", t.C.S.Pr!.Kind);
        t.Run(10_500);
        Assert.Equal(1, t.P(i).Gleks);
        t.Settle();
        var j = t.Cur;
        t.C.S.Stand = "m3";
        t.P(j).Coins = 5;
        t.Jump("m2", 1);
        Assert.Null(t.C.S.Pr);
        Assert.Contains(t.C.Out, o => o.Key == "broke");
    }

    [Fact]
    public void Sale_makes_the_glek_cheaper_until_the_end_of_next_round()
    {
        var t = Turn();
        var i = t.Cur;
        t.C.S.Stand = "m29";
        t.C.NextEvent = "sale";
        t.Jump("m4", 1);
        Assert.Equal(VechirkaRules.SalePrice, t.C.Price);
        t.C.S.Round += 2;
        Assert.Equal(VechirkaRules.GlekPrice, t.C.Price);
    }

    [Fact]
    public void Shop_sells_one_item_and_skip_stops_asking()
    {
        var t = Turn();
        var i = t.Cur;
        t.C.S.Stand = "m29";
        t.C.S.Shops["m8"] = ["horse", "pan", "key"];
        t.P(i).Coins = 20;
        t.Jump("m7", 1);
        Assert.Equal("shop", t.C.S.Pr!.Kind);
        Assert.Equal(5, t.C.S.Pr.Options.Count);
        t.Act(i, "pick", new { o = 0 });
        Assert.Contains("horse", t.P(i).Items);
        Assert.Equal(15, t.P(i).Coins);
        Assert.Equal(5, t.P(i).S.Shop);
        t.Settle();
        var j = t.Cur;
        t.P(j).Coins = 0;
        t.Jump("m7", 1);
        Assert.Null(t.C.S.Pr);   // нічого не по кишені — не питаємо
    }

    [Fact]
    public void Fork_asks_the_way_and_ferry_costs_ten_or_a_key()
    {
        var t = Turn();
        var i = t.Cur;
        t.C.S.Stand = "m3";
        t.Jump("m5", 3);
        Assert.Equal("fork", t.C.S.Pr!.Kind);
        Assert.Equal(["go:m7", "go:l0"], t.C.S.Pr.Options.Select(o => o.K));
        t.Answer("go:l0");
        Assert.Equal("l1", t.P(i).Pos);
        t.Settle();

        var j = t.Cur;
        t.P(j).Coins = 12;
        t.Jump("m27", 2);
        Assert.Equal("ferry", t.C.S.Pr!.Kind);
        Assert.Equal("go:m28", t.C.S.Pr.Options[t.C.S.Pr.Default].K);
        Assert.False(t.C.S.Pr.Options.First(o => o.K == "key:p0").Ok);
        t.Answer("pay:p0");
        Assert.Equal("p1", t.P(j).Pos);
        Assert.Equal(1, t.P(j).S.Ferries);
        Assert.True(t.C.S.Bank >= 10);
        t.Settle();
        if (t.C.S.Phase == "prompt") t.Answer(t.C.S.Pr!.Options[0].K);   // Водяник
        t.Settle();

        var k = t.Cur;
        t.P(k).Coins = 0;
        t.P(k).Items.Add("key");
        t.Jump("m27", 1);
        Assert.Throws<GameError>(() => t.Act(k, "pick", new { o = t.C.S.Pr!.Options.FindIndex(o => o.K == "pay:p0") }));
        t.Answer("key:p0");
        Assert.Equal("p0", t.P(k).Pos);
        Assert.DoesNotContain("key", t.P(k).Items);
    }

    // ---------- предмети ----------

    [Fact]
    public void Pan_bumps_a_neighbour_and_takes_three_coins_but_not_through_a_charm()
    {
        var t = Turn();
        var i = t.Cur; var o = Other(t);
        t.P(i).Pos = "m5"; t.P(o).Pos = "m7"; t.P(o).Coins = 10;
        t.P(i).Items.Add("pan");
        t.Act(i, "item", new { k = "pan" });
        Assert.Equal("aim", t.C.S.Phase);
        Assert.Contains(o, t.C.S.Am!.Targets!);
        t.Act(i, "aim", new { target = o });
        Assert.True(t.P(o).Bump);
        Assert.Equal(7, t.P(o).Coins);
        Assert.Equal(1, t.P(i).S.Pans);
        Assert.Throws<GameError>(() => t.Act(i, "item", new { k = "horse" }));   // один предмет за хід

        var t2 = Turn();
        i = t2.Cur; o = Other(t2);
        t2.P(i).Pos = "m5"; t2.P(o).Pos = "m9"; t2.P(o).Coins = 10;
        t2.P(i).Items.Add("pan");
        Assert.Throws<GameError>(() => t2.Act(i, "item", new { k = "pan" }));   // 4 клітинки — далеко
        t2.P(o).Pos = "m6"; t2.P(o).Items.Add("charm");
        t2.Act(i, "item", new { k = "pan" });
        t2.Act(i, "aim", new { target = o });
        Assert.False(t2.P(o).Bump);
        Assert.Equal(10, t2.P(o).Coins);
        Assert.Empty(t2.P(o).Items);
        Assert.Empty(t2.P(i).Items);
    }

    [Fact]
    public void Fork_rope_pumpkin_and_feather()
    {
        var t = Turn();
        var i = t.Cur; var o = Other(t);
        t.P(o).Coins = 5; t.P(i).Coins = 0;
        t.P(i).Items.AddRange(["fork"]);
        t.Act(i, "item", new { k = "fork" });
        t.Act(i, "aim", new { target = o });
        Assert.Equal(0, t.P(o).Coins);
        Assert.Equal(5, t.P(i).Coins);

        foreach (var (item, check) in new (string, Action<Table, int, int>)[]
        {
            ("rope", (x, a, b) => Assert.Equal(x.P(a).Pos, x.P(b).Pos)),
            ("pumpkin", (x, a, b) => { Assert.Equal("m20", x.P(a).Pos); Assert.Equal("m3", x.P(b).Pos); }),
        })
        {
            var u = Turn();
            var a = u.Cur; var b = Other(u);
            u.P(a).Pos = "m3"; u.P(b).Pos = "m20";
            u.P(a).Items.Add(item);
            u.Act(a, "item", new { k = item });
            u.Act(a, "aim", new { target = b });
            check(u, a, b);
            Assert.Equal(1, u.P(a).S.Bully);
        }

        var f = Turn();
        var w = f.Cur;
        f.C.S.Stand = "m19";
        f.P(w).Items.Add("feather");
        f.Act(w, "item", new { k = "feather" });
        Assert.Equal("m18", f.P(w).Pos);
        Assert.Equal("turn", f.C.S.Phase);
        Assert.Equal(0, f.P(w).S.Chests);   // телепорт — клітинка (скриня) не діє
    }

    [Fact]
    public void Horseshoe_rolls_three_dice_and_a_bump_one()
    {
        var t = Turn();
        var i = t.Cur;
        t.P(i).Items.Add("horse");
        t.Act(i, "item", new { k = "horse" });
        t.Act(i, "roll");
        Assert.Equal(3, t.C.S.T.Dice.Length);
        var u = Turn();
        var j = u.Cur;
        u.P(j).Bump = true;
        u.Act(j, "roll");
        Assert.Single(u.C.S.T.Dice);
        Assert.False(u.P(j).Bump);
    }

    [Fact]
    public void Gate_only_on_allowed_cells_and_it_stops_and_charges_the_walker()
    {
        var t = Turn();
        var i = t.Cur; var o = Other(t);
        t.C.S.Stand = "m29";
        t.P(i).Pos = "m3"; t.P(o).Pos = "m0"; t.P(o).Coins = 10;
        t.P(i).Items.Add("gate");
        t.Act(i, "item", new { k = "gate" });
        var nodes = t.C.S.Am!.Nodes!;
        Assert.DoesNotContain("m8", nodes);    // крамниця
        Assert.DoesNotContain("m0", nodes);    // Криниця й фішка
        Assert.DoesNotContain("m3", nodes);    // сама стоїть
        Assert.DoesNotContain("m16", nodes);   // скарбничка (і далеко)
        Assert.Throws<GameError>(() => t.Act(i, "aim", new { node = "m8" }));
        t.Act(i, "aim", new { node = "m2" });
        Assert.Single(t.C.S.Gates);
        // далі ходить o (хід i не грається — щоб не влізла дуель чи подія)
        t.C.S.Cur = o; t.C.S.TurnIdx = 1; t.C.S.T = new VechirkaTurn();
        var c0 = t.P(i).Coins;
        t.Jump("m0", 5);
        Assert.Equal("m2", t.P(o).Pos);   // уперся
        Assert.Equal(5 + 5, t.P(o).Coins);   // −5 власникові, +5 церква
        Assert.Equal(c0 + 5, t.P(i).Coins);
        Assert.Empty(t.C.S.Gates);
    }

    // ---------- події ----------

    [Theory]
    [InlineData("fair")] [InlineData("rain")] [InlineData("dog")] [InlineData("wedding")] [InlineData("poor")]
    [InlineData("move")] [InlineData("tax")] [InlineData("swap")] [InlineData("wind")] [InlineData("wheel")] [InlineData("gift")]
    public void Every_event_does_its_thing(string key)
    {
        var t = Turn(4);
        var i = t.Cur;
        t.C.S.Round = 3;
        t.C.S.Stand = "m29";
        for (var k = 0; k < 4; k++) { t.P(k).Coins = 8 + 4 * k; t.P(k).Pos = "m" + (20 + k); }
        var before = t.C.S.P.Select(p => (p.Pos, p.Coins)).ToArray();
        var stand = t.C.S.Stand;
        t.C.NextEvent = key;
        t.Jump(key == "wind" ? "m29" : "m4", 1);   // з m30 вітер несе без розвилок
        var after = t.C.S.P;
        switch (key)
        {
            case "fair": Assert.All(Enumerable.Range(0, 4), k => Assert.Equal(before[k].Coins + 3, after[k].Coins)); break;
            case "rain": Assert.Equal(8, t.C.S.Bank); break;
            case "dog": Assert.True(after[i].Bump); break;
            case "wedding": Assert.Equal(before[i].Coins + 6, after[i].Coins); break;
            case "poor": Assert.Equal(16, after[0].Coins); break;
            case "move": Assert.NotEqual(stand, t.C.S.Stand); break;
            case "tax": Assert.Equal(5, t.C.S.Bank); Assert.Equal(15, after[3].Coins); break;
            case "swap":
                var was = Enumerable.Range(0, 4).Select(k => k == i ? "m5" : before[k].Pos).ToArray();
                Assert.All(Enumerable.Range(0, 4), k => Assert.NotEqual(was[k], after[k].Pos));
                Assert.Equal(was.Order(), after.Select(p => p.Pos).Order());
                break;
            case "wind": Assert.True(t.C.Map.Forward("m30", after[i].Pos, false) is >= 3 and <= 6, after[i].Pos); break;
            case "wheel": Assert.True(after[i].Coins != before[i].Coins || after[i].Items.Count == 1 || t.C.S.Bank > 0); break;
            case "gift": Assert.Single(after[i].Items); break;
        }
    }

    [Fact]
    public void Vodyanyk_asks_coins_or_a_bump()
    {
        var t = Turn();
        var i = t.Cur;
        t.P(i).Coins = 10;
        t.Jump("p0", 1);
        Assert.Equal("event", t.C.S.Pr!.Kind);
        t.Answer("pay");
        Assert.Equal(7, t.P(i).Coins);
        Assert.Equal(3, t.C.S.Bank);
    }

    // ---------- дуель ----------

    [Fact]
    public void Duel_takes_stakes_plays_and_pays_the_winner_double_and_only_once_a_round()
    {
        var t = Turn();
        var i = t.Cur; var o = Other(t);
        t.P(i).Coins = 30; t.P(o).Coins = 30;
        t.Mg.Scorer = s => [.. s.Select(p => p == i ? 10L : 1L)];
        t.Jump("m10", 1);
        Assert.Equal("duelWho", t.C.S.Pr!.Kind);
        t.Answer(o.ToString());
        Assert.Equal("duelStake", t.C.S.Pr!.Kind);
        Assert.All(t.C.S.Pr.Options, x => Assert.True(x.Ok));
        t.Answer("20");
        Assert.Equal(10, t.P(i).Coins);
        Assert.Equal("card", t.C.S.Phase);
        Assert.Equal(new[] { i, o }, t.C.S.M!.Duel);
        var third = Other(t, 2);
        t.Act(third, "bet", new { i });
        Assert.Throws<GameError>(() => t.Act(i, "bet", new { i }));
        t.Act(i, "ready"); t.Act(o, "ready");
        t.Run(200);
        Assert.Equal("results", t.C.S.Phase);
        Assert.Equal(50, t.P(i).Coins);
        Assert.Equal(10, t.P(o).Coins);
        Assert.Equal(1, t.P(i).MgWins);
        Assert.Equal(10 + 2, t.P(third).Coins);
        Assert.True(t.C.S.DuelDone);
        t.Settle();
        Assert.Equal(o, t.Cur);
        var c0 = t.P(o).Coins;
        t.Jump("m20", 1);   // друга ⚔ за коло — Глек розняв
        Assert.Equal(c0 + 5, t.P(o).Coins);
        Assert.Contains(t.C.Out, x => x.Key == "duelSplit");
    }

    [Fact]
    public void Duel_with_a_broke_rival_is_for_honour()
    {
        var t = Turn();
        var i = t.Cur; var o = Other(t);
        t.P(i).Coins = 30; t.P(o).Coins = 0;
        t.Mg.Scorer = s => [.. s.Select(p => p == o ? 10L : 1L)];
        t.Jump("m10", 1);
        t.Answer(o.ToString());
        Assert.Equal("card", t.C.S.Phase);
        Assert.True(t.C.S.M!.Honor);
        t.Run(10_500);
        t.Run(200);
        Assert.Equal(3, t.P(o).Coins);
        Assert.Equal(30, t.P(i).Coins);
    }

    // ---------- міні-гра, Пізній вечір, фінал ----------

    [Fact]
    public void Last_player_picks_the_game_and_results_pay_by_place()
    {
        var t = Turn();
        t.C.S.TurnIdx = 2;
        for (var k = 0; k < 3; k++) t.P(k).Coins = 10 + k;
        t.Act(t.Cur, "roll");
        t.Settle();
        while (t.C.S.Phase == "prompt") t.Answer(t.C.S.Pr!.Options[t.C.S.Pr.Default].K);
        t.Settle();
        Assert.Equal("pick", t.C.S.Phase);
        var chooser = t.C.S.Pk!.Chooser!.Value;
        Assert.Equal(t.C.Lasts(1)[0], chooser);
        Assert.Equal(3, t.C.S.Pk.Options.Distinct().Count());
        Assert.DoesNotContain("pong", t.C.S.Pk.Options);   // лише дуелі
        Assert.Throws<GameError>(() => t.Act((chooser + 1) % 3, "pick", new { o = 0 }));
        var best = Other(t, 0);
        t.Mg.Scorer = s => [.. s.Select(p => p == best ? 100L : p)];
        var before = t.C.S.P.Select(p => p.Coins).ToArray();
        t.Act(chooser, "pick", new { o = 1 });
        t.Settle();
        Assert.Equal("card", t.C.S.Phase);
        foreach (var k in t.C.S.M!.Seats) t.Act(k, "ready");
        t.Run(100);
        Assert.Equal("results", t.C.S.Phase);
        Assert.Equal(before[best] + 10, t.P(best).Coins);
        Assert.Equal(1, t.P(best).MgWins);
        Assert.Equal(new[] { 10, 5, 2 }, t.C.S.M.Results!.Select(r => r.Coins));
    }

    [Fact]
    public void Crashed_minigame_pays_everyone_the_average()
    {
        var t = Turn();
        t.Mg.Fail = true;
        t.C.S.TurnIdx = 3;
        for (var k = 0; k < 3; k++) t.P(k).Coins = 0;
        t.C.S.TurnIdx = 2;
        t.Act(t.Cur, "roll");
        t.Settle();
        while (t.C.S.Phase == "prompt") t.Answer(t.C.S.Pr!.Options[t.C.S.Pr.Default].K);
        t.Settle();
        var before = t.C.S.P.Select(p => p.Coins).ToArray();
        t.Run(8_200);   // pick тайм-аут → рулетка → картка
        t.Run(12_000);
        Assert.Equal("results", t.C.S.Phase);
        Assert.Equal("Crash", t.C.S.M!.How);
        Assert.All(Enumerable.Range(0, 3), k => Assert.Equal(before[k] + 6, t.P(k).Coins));
    }

    [Fact]
    public void Late_evening_splits_the_bank_and_the_last_gets_a_gift()
    {
        var t = New(6, len: 30);
        t.Settle();
        t.C.S.Round = t.C.S.Rounds - 3;
        t.C.S.TurnIdx = 5;
        t.C.S.Bank = 31;
        for (var k = 0; k < 6; k++) { t.P(k).Coins = 0; t.P(k).Gleks = k; }
        t.C.S.Cur = t.C.S.Order[5];
        t.C.S.T = new VechirkaTurn();
        t.Mg.Scorer = s => [.. s.Select(_ => 0L)];
        // кінець ходу останнього → міні-гра → нове коло = Пізній вечір
        for (var k = 0; k < 2000 && t.C.S.Phase != "late"; k++) t.Run(100);
        Assert.Equal("late", t.C.S.Phase);
        Assert.True(t.C.S.Late);
        Assert.Equal([0, 1], t.C.S.L!.Choosers.Order());
        Assert.Equal(1, t.C.S.Bank);   // 31 / 3 = 10 кожному з трьох нижчих, остача 1
        t.Act(0, "pick", new { o = 1 });   // перо
        t.Run(12_500);
        Assert.Contains("feather", t.P(0).Items);
        Assert.Contains(t.C.Out, o => o.Key == "late");
    }

    [Fact]
    public void Final_hands_out_bonus_gleks_and_finishes()
    {
        var t = New(3, bots: true, len: 30);
        for (var k = 0; k < 400_000 && t.C.S.Phase != "final"; k++) t.Run(100, 100);
        Assert.Equal(3, t.C.S.F!.Bonuses.Count);
        var gl = t.C.S.P.Sum(p => p.Gleks);
        for (var k = 0; k < 400 && !t.C.S.Done; k++) t.Run(100, 100);
        Assert.True(t.C.S.Done);
        Assert.Equal(gl + t.C.S.F.Bonuses.Sum(b => b.Winners.Length), t.C.S.P.Sum(p => p.Gleks));
        Assert.Equal(1, t.C.S.F.Ranking[0][1]);
        Assert.All(t.C.S.F.Bonuses, b => Assert.True(b.Winners.Length < 3));
    }

    [Fact]
    public void Big_table_always_has_the_unlucky_nomination()
    {
        for (ulong s = 1; s < 10; s++) Assert.Contains("traps", New(6, seed: s).C.S.BonusKeys);
        Assert.Equal(3, New(3).C.S.BonusKeys.Distinct().Count());
    }

    // ---------- AFK ----------

    [Fact]
    public void Two_missed_turns_doze_off_and_any_action_wakes_up()
    {
        var t = Turn();
        var i = t.Cur;
        t.Run(20_100);
        Assert.Equal(1, t.P(i).Misses);
        Assert.False(t.P(i).Auto);
        t.Settle();
        while (t.C.S.Phase == "prompt" && t.C.S.Pr!.Who == i) t.Run(12_100);
        t.Settle();
        // наступний хід того ж — лише через коло; тут перевіряємо другий пропуск у prompt/turn іншого
        var j = t.Cur;
        t.Run(20_100);
        t.Settle();
        t.P(j).Misses = 1;
        for (var k = 0; k < 600 && !t.P(j).Auto; k++) t.Run(100);
        Assert.True(t.P(j).Auto || t.P(i).Auto);
        var dozed = t.P(j).Auto ? j : i;
        t.Act(dozed, "here");
        Assert.False(t.P(dozed).Auto);
    }

    [Fact]
    public void Illegal_actions_do_not_change_the_state()
    {
        var t = Turn();
        var i = t.Cur; var o = Other(t);
        var before = t.C.Save();
        Assert.Throws<GameError>(() => t.Act(o, "roll"));
        Assert.Throws<GameError>(() => t.Act(i, "item", new { k = "pan" }));
        Assert.Throws<GameError>(() => t.Act(i, "aim", new { n = 3 }));
        Assert.Throws<GameError>(() => t.Act(i, "pick", new { o = 0 }));
        Assert.Throws<GameError>(() => t.Act(i, "ready"));
        Assert.Throws<GameError>(() => t.Act(i, "bet", new { i = o }));
        Assert.Throws<GameError>(() => t.Act(i, "dance"));
        t.P(i).Items.Add("pick");
        t.Act(i, "item", new { k = "pick" });
        Assert.Throws<GameError>(() => t.Act(i, "aim", new { n = 13 }));
        Assert.Throws<GameError>(() => t.Act(i, "aim", new { n = 0 }));
        t.Act(i, "aim");   // скасувати
        Assert.Equal("turn", t.C.S.Phase);
        Assert.Contains("pick", t.P(i).Items);
        _ = before;
    }

    // ---------- виправлення за рецензіями R1/R2 ----------

    [Fact]
    public void Cancelling_aim_does_not_refill_the_turn_timer()
    {
        var t = Turn();
        var i = t.Cur;
        t.Run(15_000);
        Assert.Equal("turn", t.C.S.Phase);
        for (var k = 0; k < 3; k++)
        {
            t.P(i).Items.Remove("pick"); t.P(i).Items.Insert(0, "pick");
            t.Act(i, "item", new { k = "pick" });
            Assert.Equal("aim", t.C.S.Phase);
            t.Act(i, "aim");   // скасувати
            Assert.Equal("turn", t.C.S.Phase);
            Assert.True((t.C.S.Until!.Value - t.Now).TotalMilliseconds <= 5_000, "скасування дало новий повний хід");
        }
        t.Run(5_100);
        Assert.True(t.C.S.Phase != "turn" || t.C.S.Cur != i, "хід не скінчився за залишком таймера");
    }

    [Fact]
    public void Item_wait_cancel_loop_cannot_stretch_the_turn()
    {
        var t = Turn();
        var i = t.Cur;
        var start = t.Now;
        for (var k = 0; k < 20 && t.C.S.Phase == "turn" && t.C.S.Cur == i; k++)
        {
            t.P(i).Items.Remove("pick"); t.P(i).Items.Insert(0, "pick");
            t.Act(i, "item", new { k = "pick" });
            t.Run(VechirkaRules.AimMs - 100);   // тягнемо приціл майже до кінця
            if (t.C.S.Phase != "aim") break;
            t.Act(i, "aim");   // скасувати
        }
        Assert.True(t.C.S.Phase != "turn" || t.C.S.Cur != i, "хід досі триває після циклу «предмет → скасувати»");
        Assert.True((t.Now - start).TotalMilliseconds <= VechirkaRules.TurnMs + 2 * VechirkaRules.AimMs,
            "цикл скасувань розтягнув хід");
    }

    [Fact]
    public void Duel_of_two_bots_waits_for_a_human_bettor_or_the_bet_window()
    {
        foreach (var bet in new[] { true, false })
        {
            var t = Turn();
            var i = t.Cur; var o = Other(t); var third = Other(t, 2);
            t.P(i).Coins = 30; t.P(o).Coins = 30;
            t.Jump("m10", 1);
            t.Answer(o.ToString());
            t.Answer("10");
            Assert.Equal("card", t.C.S.Phase);
            t.P(i).Bot = true; t.P(o).Bot = true;   // дуелянти — машини: «Готовий» нікому тиснути
            t.Run(1_000);
            Assert.Equal("card", t.C.S.Phase);       // глядач ще не поставив — картка тримається
            if (bet)
            {
                t.Act(third, "bet", new { i });
                t.Run(100);
                Assert.NotEqual("card", t.C.S.Phase);
            }
            else
            {
                t.Run(VechirkaRules.BetMs);
                Assert.NotEqual("card", t.C.S.Phase);
            }
        }
    }

    [Fact]
    public void A_bot_starting_a_duel_calls_a_human_by_default()
    {
        var t = Turn();
        var i = t.Cur; var o = Other(t); var third = Other(t, 2);
        t.P(o).Bot = true; t.P(o).Coins = 90; t.P(third).Coins = 5;
        t.P(i).Bot = true;
        t.Jump("m10", 1);
        var pr = t.C.S.Pr!;
        Assert.Equal("duelWho", pr.Kind);
        Assert.Equal(third.ToString(), pr.Options[pr.Default].K);
        new VechirkaBot().Think(t.C, i);   // і мізки бота кличуть людину, хоч бот-сусід багатший
        Assert.Equal("duelStake", t.C.S.Pr!.Kind);
        Assert.Equal(third.ToString(), t.C.S.Pr.NewItem);
    }

    [Fact]
    public void Host_pause_blocks_ready_bet_and_pick()
    {
        var t = Turn();
        var i = t.Cur; var o = Other(t); var third = Other(t, 2);
        t.P(i).Coins = 30; t.P(o).Coins = 30;
        t.Jump("m10", 1);
        t.Answer(o.ToString());
        t.Answer("10");
        Assert.Equal("card", t.C.S.Phase);
        t.C.SetPause("host");
        Assert.Throws<GameError>(() => t.Act(i, "ready"));
        Assert.Throws<GameError>(() => t.Act(third, "bet", new { i }));
        t.C.SetPause(null);
        t.Act(i, "ready");
    }

    [Fact]
    public void Card_starts_as_soon_as_the_last_unready_human_leaves()
    {
        var t = Turn();
        t.C.S.TurnIdx = 2;
        t.Act(t.Cur, "roll");
        t.Settle();
        while (t.C.S.Phase == "prompt") t.Answer(t.C.S.Pr!.Options[t.C.S.Pr.Default].K);
        t.Settle();
        Assert.Equal("pick", t.C.S.Phase);
        t.Act(t.C.S.Pk!.Chooser!.Value, "pick", new { o = 0 });
        t.Settle();
        Assert.Equal("card", t.C.S.Phase);
        var seats = t.C.S.M!.Seats;
        t.Act(seats[0], "ready"); t.Act(seats[1], "ready");
        Assert.Equal("card", t.C.S.Phase);
        t.P(seats[2]).Away = true;   // відпав, не натиснувши «Готовий»
        t.Run(100);
        Assert.NotEqual("card", t.C.S.Phase);
    }

    sealed class ThrowingBrain : IVechirkaBrain
    {
        public int Calls;
        public void Think(VechirkaCore core, int i) { Calls++; throw new InvalidOperationException("мізки зламались"); }
    }

    [Fact]
    public void Broken_bot_brain_falls_back_to_defaults_and_the_evening_goes_on()
    {
        var t = New(3, bots: true);
        var brain = new ThrowingBrain();
        var errors = 0;
        t.C.Brain = brain; t.C.OnError = _ => errors++;
        var r0 = t.C.S.Round;
        t.Run(300_000, 100);
        Assert.True(brain.Calls > 0);
        Assert.True(errors > 0);
        Assert.True(t.C.S.Round > r0 + 1 || t.C.S.Done, $"застрягли: {t.C.S.Phase} {t.C.S.Round}");
    }

    sealed class PulseMg : IMgRunner
    {
        int _n;
        public bool Begin(string id, int[] pSeats, bool[] bot, LiveBots.Level level) => true;
        public string Title => "x";
        public string Howto => "";
        public int CapMs => 60_000;
        public ActResult Act(int p, string action, JsonElement payload) => ActResult.Done;
        /// <summary>Кадр — лише на непарних кроках (як підгра з кроком, довшим за тик кімнати).</summary>
        public TickResult Tick() => ++_n % 2 == 1 ? TickResult.FrameOnly : TickResult.None;
        public object? View(int? p) => null;
        public object? Frame() => null;
        public string[] Names() => [];
        public string[] SeatNames() => [];
        public MinigameResult? Result => null;
    }

    [Fact]
    public void Minigame_frame_made_inside_an_action_is_not_lost()
    {
        var map = VechirkaMap.Load("selo");
        var st = new VechirkaState { Len = 45 };
        for (var k = 0; k < 3; k++) st.P.Add(new VechirkaPlayer { Nick = $"Г{k}", Name = $"Г{k}" });
        var c = new VechirkaCore(map, st, new PulseMg(), TestPool);
        c.Start(7, T0);
        c.S.Phase = "mg"; c.S.Busy = null; c.S.Until = null;
        c.S.M = new VechirkaMgState { Id = "tyr", Title = "Тир", Seats = [0, 1, 2], Running = true };
        c.LastMgTick = TickResult.None;
        c.Act(0, "mg", JsonSerializer.SerializeToElement(new { a = "fire" }), T0.AddSeconds(1));   // крок 1 — кадр
        c.Advance(T0.AddSeconds(1.02));                                                             // крок 2 — без кадру
        Assert.True(c.LastMgTick.Frame, "кадр кроку, зробленого в дії, затерто наступним кроком");
    }
}
