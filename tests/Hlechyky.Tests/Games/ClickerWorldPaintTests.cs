using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Одинадцяте оновлення, пакет C «Розписи світу» (docs/games/specs/clicker-v11.md §3, clicker-v11-c.md): ціни й ворота
/// шести розписів світу, альбом на 15 стовпчиків без утрати старого «повного альбому», бонус і ачівка альбому світу,
/// дві нові техніки розпису — «Кобальт» (тонкий контур на повільному колі) і «Раку» (вийняти в тирсу у вікно світіння).
/// </summary>
public class ClickerWorldPaintTests
{
    static readonly string[] WorldStyles = ["jingdezhen", "iznik", "delft", "meissen", "sevres", "raku"];
    static readonly string[] HomeStyleKeys = ["gavarets", "vasylkiv", "bubnivka", "kosiv", "opishnia", "mezhyhirya", "petrykivka", "trypillia"];

    static RoomHarness Wheel(int seed = 1)
    {
        var h = new RoomHarness("clicker", seed: seed);
        h.Solo("Оля");
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static JsonElement Kiln(RoomHarness h) => View(h).GetProperty("kiln");
    static JsonElement Album(RoomHarness h) => View(h).GetProperty("album");
    static JsonElement Pattern(RoomHarness h) => Kiln(h).GetProperty("pattern");
    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);
    static ActResult K(RoomHarness h, object payload) => Act(h, "kiln", payload);
    static List<string?> Techs(RoomHarness h) => Kiln(h).GetProperty("techs").EnumerateArray().Select(x => x.GetString()).ToList();

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static JsonNode SaveNode(RoomHarness h)
    {
        lock (h.Room.Sync) return JsonNode.Parse(h.Room.Game.Save()!)!;
    }

    static JsonObject AlbumNode(JsonObject s)
    {
        if (s["album"] is JsonObject a) return a;
        var fresh = new JsonObject();
        s["album"] = fresh;
        return fresh;
    }

    /// <summary>Клітинки альбому: на кожному виробі — ці розписи ("" — простий).</summary>
    static JsonObject Cells(IEnumerable<string> styles, Func<string, bool>? skip = null)
    {
        var cells = new JsonObject();
        foreach (var w in Clicker.Wares)
        {
            var list = new JsonArray();
            foreach (var st in styles)
                if (skip is null || !skip(w.Key + "|" + st)) list.Add(st);
            cells[w.Key] = list;
        }
        return cells;
    }

    static readonly string[] HomeColumns = ["", .. HomeStyleKeys];

    // ---------- розписи світу: ціни й ворота ----------

    [Fact]
    public void World_styles_cost_a_hundred_first_levels_of_their_tier_and_come_after_the_home_ones()
    {
        Assert.Equal(HomeStyleKeys, Clicker.Styles.Take(Clicker.HomeStyles).Select(s => s.Key));
        Assert.Equal(WorldStyles, Clicker.Styles.Skip(Clicker.HomeStyles).Select(s => s.Key));
        foreach (var s in Clicker.Styles.Skip(Clicker.HomeStyles))
        {
            var tier = Clicker.Shop.Single(u => u.Key == s.Tier);
            Assert.Equal(100 * tier.Base, s.Price, 1e-9 * s.Price);
        }
        // Кожен наступний розпис світу вдесятеро дорожчий — як і щаблі.
        var prices = Clicker.Styles.Skip(Clicker.HomeStyles).Select(s => s.Price).ToList();
        for (var i = 1; i < prices.Count; i++) Assert.Equal(prices[i - 1] * 10, prices[i], 1e-9 * prices[i]);
        Assert.All(Clicker.Styles.Take(Clicker.HomeStyles), s => Assert.Equal("", s.Tier));
    }

    [Fact]
    public void The_shop_catalog_names_the_tier_of_every_world_style()
    {
        var h = Wheel();
        var styles = View(h).GetProperty("shopCatalog").GetProperty("styles").EnumerateArray().ToList();
        Assert.Equal(Clicker.Styles.Length, styles.Count);
        foreach (var s in styles)
        {
            var key = s.GetProperty("key").GetString()!;
            Assert.Equal(WorldStyles.Contains(key) ? key : "", s.GetProperty("tier").GetString());
        }
    }

    [Fact]
    public void A_world_style_is_not_sold_before_its_tier_even_with_the_money()
    {
        var h = Wheel();
        Patch(h, s => { s["pots"] = 1e40; s["total"] = 1e40; });
        var r = Act(h, "paint", new { key = "jingdezhen" });
        Assert.False(r.Ok);
        Assert.Equal("Цзиндечженська синь привезуть, коли матимеш «Порцеляна Цзиндечженя»", r.Message);
        Patch(h, s => s["upgrades"]!["jingdezhen"] = 1);
        Assert.True(Act(h, "paint", new { key = "jingdezhen" }).Ok);
        // Сусідній щабель не відмикає чужий розпис.
        Assert.False(Act(h, "paint", new { key = "iznik" }).Ok);
        var owned = View(h).GetProperty("styles").EnumerateArray().Where(x => x.GetProperty("owned").GetBoolean()).Select(x => x.GetProperty("key").GetString());
        Assert.Equal(["jingdezhen"], owned);
    }

    [Fact]
    public void A_world_style_costs_its_price_and_gives_five_percent_like_the_old_ones()
    {
        var h = Wheel();
        Patch(h, s => { s["pots"] = 7e34; s["total"] = 7e34; s["upgrades"]!["jingdezhen"] = 1; });
        var before = View(h).GetProperty("perSecond").GetDouble();
        Assert.True(Act(h, "paint", new { key = "jingdezhen" }).Ok);
        var v = View(h);
        Assert.Equal(1e34, v.GetProperty("pots").GetDouble(), 1e24);
        Assert.Equal(before * 1.05, v.GetProperty("perSecond").GetDouble(), before * 1e-9);
        Assert.Equal("jingdezhen", v.GetProperty("wear").GetString());
        Patch(h, s => { s["pots"] = 5e35; s["upgrades"]!["iznik"] = 1; });
        var poor = Act(h, "paint", new { key = "iznik" });
        Assert.False(poor.Ok);
        Assert.StartsWith("Бракує глеків: треба ще", poor.Message);
    }

    [Fact]
    public void The_museum_achievement_stays_with_the_eight_home_styles()
    {
        var h = Wheel();
        Patch(h, s => { s["pots"] = 1e19; s["total"] = 1e19; });
        foreach (var k in HomeStyleKeys) Assert.True(Act(h, "paint", new { key = k }).Ok, k);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-museum");
    }

    // ---------- альбом на 15 стовпчиків ----------

    [Fact]
    public void An_old_full_album_stays_full_and_the_world_starts_empty()
    {
        var h = Wheel();
        Patch(h, s => AlbumNode(s)["cells"] = Cells(HomeColumns));
        var a = Album(h);
        Assert.Equal(Clicker.AlbumSize, a.GetProperty("open").GetInt32());
        Assert.Equal(Clicker.AlbumSize, a.GetProperty("size").GetInt32());
        Assert.Equal(Clicker.Wares.Length, a.GetProperty("rows").GetInt32());
        Assert.Equal(Clicker.AlbumHomeColumns, a.GetProperty("cols").GetInt32());
        Assert.Equal(Clicker.AlbumHomeColumns, a.GetProperty("homeColumns").GetInt32());
        Assert.Equal(0, a.GetProperty("worldOpen").GetInt32());
        Assert.Equal(Clicker.Wares.Length * WorldStyles.Length, a.GetProperty("worldSize").GetInt32());
        Assert.False(a.GetProperty("worldFull").GetBoolean());
        // Бонус — рівно такий, як до одинадцятого оновлення: клітинки, повні рядки й повні стовпчики.
        var old = Clicker.AlbumCellBonus * Clicker.AlbumSize + Clicker.AlbumRowBonus * Clicker.Wares.Length
            + Clicker.AlbumColumnBonus * Clicker.AlbumHomeColumns;
        Assert.Equal(old, a.GetProperty("bonus").GetDouble(), 1e-12);
    }

    [Fact]
    public void World_cells_add_their_own_bonus_and_the_whole_world_grid_a_quarter_more()
    {
        var h = Wheel();
        Patch(h, s => AlbumNode(s)["cells"] = Cells(HomeColumns));
        var home = Album(h).GetProperty("bonus").GetDouble();
        Patch(h, s => AlbumNode(s)["cells"] = Cells([.. HomeColumns, "jingdezhen"]));
        var one = Album(h);
        Assert.Equal(Clicker.Wares.Length, one.GetProperty("worldOpen").GetInt32());
        // Повний стовпчик світу не дає +6 %: лише клітинки (стовпчики — частина «альбому світу»).
        Assert.Equal(home + Clicker.AlbumCellBonus * Clicker.Wares.Length, one.GetProperty("bonus").GetDouble(), 1e-12);
        Assert.Equal(Clicker.AlbumHomeColumns, one.GetProperty("cols").GetInt32());
        Patch(h, s => AlbumNode(s)["cells"] = Cells([.. HomeColumns, .. WorldStyles]));
        var all = Album(h);
        var world = Clicker.Wares.Length * WorldStyles.Length;
        Assert.True(all.GetProperty("worldFull").GetBoolean());
        Assert.Equal(world, all.GetProperty("worldOpen").GetInt32());
        Assert.Equal(home + Clicker.AlbumCellBonus * world + Clicker.AlbumWorldAllBonus, all.GetProperty("bonus").GetDouble(), 1e-12);
        // «Домашній» підсумок не зрушився.
        Assert.Equal(Clicker.AlbumSize, all.GetProperty("open").GetInt32());
    }

    [Fact]
    public void World_stars_count_as_stars_but_not_toward_the_old_all_stars()
    {
        var h = Wheel();
        Patch(h, s =>
        {
            AlbumNode(s)["cells"] = Cells(HomeColumns);
            AlbumNode(s)["stars"] = Cells(HomeColumns);
        });
        var home = Album(h);
        Assert.Equal(Clicker.AlbumSize, home.GetProperty("starOpen").GetInt32());
        Patch(h, s => AlbumNode(s)["stars"] = Cells([.. HomeColumns, "delft"]));
        var a = Album(h);
        Assert.Equal(Clicker.AlbumSize, a.GetProperty("starOpen").GetInt32());
        Assert.Equal(Clicker.Wares.Length, a.GetProperty("worldOpen").GetInt32());   // зірка відкриває й клітинку
        Assert.Equal(home.GetProperty("bonus").GetDouble() + (Clicker.AlbumCellBonus + Clicker.AlbumStarBonus) * Clicker.Wares.Length,
            a.GetProperty("bonus").GetDouble(), 1e-12);
    }

    [Fact]
    public void The_last_world_cell_fired_is_the_album_of_the_world_achievement()
    {
        var h = Wheel();
        Patch(h, s =>
        {
            AlbumNode(s)["cells"] = Cells([.. HomeColumns, .. WorldStyles], key => key == "pot|raku");
            s["styles"] = new JsonArray([.. HomeStyleKeys.Concat(WorldStyles).Select(x => (JsonNode)x!)]);
        });
        Assert.False(Album(h).GetProperty("worldFull").GetBoolean());
        Assert.True(K(h, new { op = "paint", style = "raku" }).Ok);
        Patch(h, s =>
        {
            var rack = new JsonArray();
            for (var i = 0; i < 3; i++) rack.Add(new JsonObject { ["ware"] = "pot", ["clay"] = "", ["dryAt"] = h.Clock.UtcNow.AddMinutes(-1).ToString("O") });
            s["craft"]!["rack"] = rack;
        });
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:potter-album-world");
        Assert.True(K(h, new { op = "light", helper = true }).Ok);
        h.Clock.Advance(31);
        Assert.True(Act(h, "look").Ok);                                        // ачівки доходять із дією гравця
        Assert.True(Album(h).GetProperty("worldFull").GetBoolean());
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-album-world");
    }

    [Fact]
    public void World_cells_live_in_the_save_under_their_style_keys()
    {
        var h = Wheel();
        Patch(h, s => AlbumNode(s)["cells"] = new JsonObject { ["jug"] = new JsonArray("", "sevres") });
        var saved = SaveNode(h)["album"]!["cells"]!["jug"]!.AsArray().Select(x => x!.GetValue<string>());
        Assert.Equal(["", "sevres"], saved);
        Assert.Equal(1, Album(h).GetProperty("worldOpen").GetInt32());
        Assert.Equal(1, Album(h).GetProperty("open").GetInt32());
    }

    // ---------- техніки: відкриття ----------

    [Fact]
    public void Kobalt_and_raku_open_with_their_styles_or_tens_of_thousands_fired()
    {
        var h = Wheel();
        Assert.Equal("Кобальт відкриється з цзиндечженською синню в колекції або після 10 000 обпалених",
            K(h, new { op = "paint", style = "", tech = "kobalt" }).Message);
        Assert.Equal("Раку відкриється з розписом раку в колекції або після 25 000 обпалених",
            K(h, new { op = "paint", style = "", tech = "raku" }).Message);
        // Усі вісім старих технік — а нових ще нема: smaug із 4 335 обпаленими їх не побачить до Толоки.
        Patch(h, s => { s["craft"]!["firedBy"] = new JsonObject { ["pot"] = 9_999 }; s["styles"] = new JsonArray(HomeStyleKeys.Select(x => (JsonNode)x!).ToArray()); });
        Assert.DoesNotContain("kobalt", Techs(h));
        Assert.DoesNotContain("raku", Techs(h));
        Assert.Equal(8, Techs(h).Count);
        Patch(h, s => s["craft"]!["firedBy"] = new JsonObject { ["pot"] = 10_000 });
        Assert.Contains("kobalt", Techs(h));
        Assert.DoesNotContain("raku", Techs(h));
        Patch(h, s => s["craft"]!["firedBy"] = new JsonObject { ["pot"] = 25_000 });
        Assert.Contains("raku", Techs(h));
        // З розписом у колекції — одразу, без обпалених.
        var g = Wheel();
        Patch(g, s => s["styles"] = new JsonArray("jingdezhen"));
        Assert.Contains("kobalt", Techs(g));
        Assert.DoesNotContain("raku", Techs(g));
        Patch(g, s => s["styles"] = new JsonArray("raku"));
        Assert.Contains("raku", Techs(g));
    }

    [Fact]
    public void All_techniques_is_still_all_eight_old_ones()
    {
        var h = Wheel();
        Patch(h, s => { s["craft"]!["firedBy"] = new JsonObject { ["pot"] = 1500 }; s["styles"] = new JsonArray("gavarets", "kosiv"); });
        Assert.True(Act(h, "look").Ok);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-tech-all");
        Assert.Equal(Clicker.HomeTechniques, Clicker.Techniques.Length - 2);
    }

    [Fact]
    public void The_catalog_carries_the_new_techniques_with_their_home_styles()
    {
        var h = Wheel();
        var techs = View(h).GetProperty("catalog").GetProperty("kiln").GetProperty("techs").EnumerateArray().ToList();
        Assert.Equal("jingdezhen", techs.Single(t => t.GetProperty("key").GetString() == "kobalt").GetProperty("home").GetString());
        Assert.Equal("raku", techs.Single(t => t.GetProperty("key").GetString() == "raku").GetProperty("home").GetString());
    }

    // ---------- спільне для мінігор ----------

    static object PathOf(IEnumerable<double[]> pts) =>
        new { op = "decor", path = KilnPaint.Encode(pts.Select(p => new KilnPaint.Pt(p[0], p[1], p[2], p.Length > 3 && p[3] >= 1))) };

    static void OpenNew(RoomHarness h) => Patch(h, s => s["styles"] = new JsonArray("jingdezhen", "raku"));

    static ActResult Paint(RoomHarness h, string tech, Func<JsonElement, List<double[]>> draw, string style = "")
    {
        var start = K(h, new { op = "paint", style, tech });
        Assert.True(start.Ok, start.Message);
        var pts = draw(Pattern(h));
        h.Clock.AdvanceMs((int)(pts[^1][0] - pts[0][0]) + 50);
        return K(h, PathOf(pts));
    }

    static int Beauty(RoomHarness h) => Kiln(h).GetProperty("beauty").GetInt32();

    // ---------- кобальт ----------

    /// <summary>Рука на кобальті: тримає пензлик угорі й водить його вздовж радіуса, поки коло крутиться (як ріжок).</summary>
    static List<double[]> Cobalt(JsonElement pattern, double offset = 0, int seed = 42)
    {
        var rnd = new Random(seed);
        var s = pattern.GetProperty("shape");
        int r0 = s.GetProperty("r0").GetInt32(), amp = s.GetProperty("amp").GetInt32(), k = s.GetProperty("k").GetInt32();
        double ph = s.GetProperty("phase").GetInt32() * Math.PI / 180, period = s.GetProperty("period").GetInt32(), dir = s.GetProperty("dir").GetInt32();
        var pts = new List<double[]>();
        var beta = -Math.PI / 2;
        double ms = 0;
        while (ms < period + 400)
        {
            var theta = beta - dir * 2 * Math.PI * ms / period;
            var r = r0 + amp * Math.Sin(k * theta + ph) + offset;
            pts.Add([ms, 500 + r * Math.Cos(beta) + rnd.NextDouble() * 3 - 1.5, 500 + r * Math.Sin(beta) + rnd.NextDouble() * 3 - 1.5, pts.Count == 0 ? 1 : 0]);
            ms += 20 + rnd.Next(5) + rnd.NextDouble();
        }
        return pts;
    }

    [Fact]
    public void The_kobalt_pattern_is_a_slow_wheel_with_a_thin_tolerance()
    {
        var h = Wheel();
        OpenNew(h);
        Assert.True(K(h, new { op = "paint", style = "", tech = "kobalt" }).Ok);
        var p = Pattern(h);
        Assert.Equal("kobalt", p.GetProperty("tech").GetString());
        var s = p.GetProperty("shape");
        Assert.InRange(s.GetProperty("period").GetInt32(), 7600, 9400);
        Assert.Equal(KilnPaint.KobaltTol, s.GetProperty("tol").GetDouble());
        Assert.DoesNotContain("seed", p.GetRawText(), StringComparison.OrdinalIgnoreCase);
        // Коло кобальту повільніше за ріжок на будь-якому зерні.
        for (var seed = 1; seed < 200; seed++)
        {
            var kob = (KilnPaint.Kobalt)KilnPaint.Pattern("kobalt", seed);
            var riz = (KilnPaint.Rizh)KilnPaint.Pattern("rizh", seed);
            Assert.True(kob.Period > riz.Period, $"{seed}");
            Assert.InRange(kob.K, 5, 8);
        }
    }

    [Fact]
    public void A_careful_hand_paints_kobalt_beautifully_and_a_scribble_does_not()
    {
        var h = Wheel();
        OpenNew(h);
        var r = Paint(h, "kobalt", p => Cobalt(p));
        Assert.True(r.Ok, r.Message);
        Assert.True(Beauty(h) >= 85, $"{Beauty(h)}");
        Assert.Equal(JsonValueKind.Null, Pattern(h).ValueKind);
        var rnd = new Random(9);
        Assert.True(Paint(h, "kobalt", _ => Enumerable.Range(0, 200).Select(i => new double[] { i * 17 + rnd.NextDouble() * 4, 60 + 40 * Math.Cos(i / 5.0) + rnd.NextDouble(), 60 + 40 * Math.Sin(i / 5.0), i == 0 ? 1 : 0 }).ToList()).Ok);
        Assert.True(Beauty(h) <= 10, $"{Beauty(h)}");
    }

    [Fact]
    public void Kobalt_punishes_a_shaky_line_harder_than_the_horn()
    {
        // Та сама рука, що веде на 14 одиниць повз пунктир: ріжок це пробачає, кобальт — ні.
        double Score(string tech, int seed)
        {
            var pattern = JsonSerializer.SerializeToElement(new { tech, shape = KilnPaint.Pattern(tech, seed) switch
            {
                KilnPaint.Kobalt z => (object)new { r0 = z.R0, amp = z.Amp, k = z.K, phase = z.Phase, period = z.Period, dir = z.Dir },
                KilnPaint.Rizh z => new { r0 = z.R0, amp = z.Amp, k = z.K, phase = z.Phase, period = z.Period, dir = z.Dir },
                _ => throw new InvalidOperationException(),
            } });
            var pts = Cobalt(pattern, offset: 14).Select(p => new KilnPaint.Pt(p[0], p[1], p[2], p[3] >= 1)).ToList();
            return KilnPaint.Beauty(tech, seed, pts);
        }
        for (var seed = 3; seed < 10; seed++)
        {
            var riz = Score("rizh", seed);
            var kob = Score("kobalt", seed);
            Assert.True(kob + 25 < riz, $"{seed}: кобальт {kob}, ріжок {riz}");
        }
    }

    [Fact]
    public void Kobalt_is_the_home_technique_of_the_jingdezhen_blue()
    {
        var h = Wheel();
        OpenNew(h);
        var r = Paint(h, "kobalt", _ => Enumerable.Range(0, 100).Select(i => new double[] { i * 20 + i % 3, 20 + i % 7, 20, i == 0 ? 1 : 0 }).ToList(), style: "jingdezhen");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(Clicker.HomeBonus, Beauty(h));
        Assert.EndsWith("(рідний осередок +10)", r.Message);
    }

    // ---------- раку ----------

    /// <summary>
    /// Рука на раку: тик по печі (кришка), а тоді по черзі — узяти виріб щипцями біля печі в мить <paramref name="when"/>
    /// (мс від кришки чи кінця попереднього виймання для вікна [open, len]) і за <paramref name="carryMs"/> донести в
    /// <paramref name="to"/> (типово — у яму).
    /// </summary>
    static List<double[]> Quench(JsonElement pattern, Func<int, int, int, double> when, int carryMs = 500, Func<int, (double X, double Y)>? to = null, int seed = 7)
    {
        var rnd = new Random(seed);
        var s = pattern.GetProperty("shape");
        var kiln = s.GetProperty("kiln").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var pit = s.GetProperty("pit").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var pieces = s.GetProperty("pieces").EnumerateArray().Select(x => x.EnumerateArray().Select(y => y.GetInt32()).ToArray()).ToList();
        var pts = new List<double[]>
        {
            new[] { 0.0, kiln[0] + 3, kiln[1] - 5, 1 },
            new[] { 70.0, kiln[0] + 4, kiln[1] - 4, 0 },
        };
        double from = 0;
        for (var i = 0; i < pieces.Count; i++)
        {
            var start = from + when(i, pieces[i][0], pieces[i][1]);
            var (tx, ty) = to?.Invoke(i) ?? (pit[0], pit[1]);
            var n = Math.Max(6, carryMs / 26);
            for (var j = 0; j <= n; j++)
            {
                var t = j / (double)n;
                var ms = start + carryMs * t + (j > 0 && j < n ? rnd.NextDouble() * 6 - 3 : 0);
                pts.Add([ms, kiln[0] + (tx - kiln[0]) * t + rnd.NextDouble() * 4 - 2, kiln[1] + (ty - kiln[1]) * t - 60 * Math.Sin(Math.PI * t) + rnd.NextDouble() * 4 - 2, j == 0 ? 1 : 0]);
            }
            from = pts[^1][0];
        }
        return pts;
    }

    [Fact]
    public void The_raku_pattern_is_a_kiln_a_pit_and_three_glowing_windows()
    {
        var h = Wheel();
        OpenNew(h);
        Assert.True(K(h, new { op = "paint", style = "", tech = "raku" }).Ok);
        var s = Pattern(h).GetProperty("shape");
        Assert.Equal(KilnPaint.RakuPieces, s.GetProperty("pieces").GetArrayLength());
        foreach (var w in s.GetProperty("pieces").EnumerateArray())
        {
            Assert.InRange(w[0].GetInt32(), 1400, 2799);
            Assert.InRange(w[1].GetInt32(), 650, 999);
        }
        var kiln = s.GetProperty("kiln");
        var pit = s.GetProperty("pit");
        Assert.True(Math.Abs(kiln[0].GetInt32() - pit[0].GetInt32()) >= 500, "піч і яма — по різні боки");
        Assert.Equal(KilnPaint.RakuReach, s.GetProperty("reach").GetDouble());
        Assert.Equal(KilnPaint.RakuEarly, s.GetProperty("early").GetDouble());
        Assert.Equal(KilnPaint.RakuLate, s.GetProperty("late").GetDouble());
        Assert.DoesNotContain("seed", Pattern(h).GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pulling_every_piece_while_it_glows_is_beautiful()
    {
        var h = Wheel();
        OpenNew(h);
        var r = Paint(h, "raku", p => Quench(p, (_, open, len) => open + len / 2.0));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(100, Beauty(h));
    }

    [Fact]
    public void Too_early_cracks_and_too_late_is_dull()
    {
        var h = Wheel();
        OpenNew(h);
        // На 450 мс раніше вікна — лишається чверть; на 450 мс пізніше — половина.
        Assert.True(Paint(h, "raku", p => Quench(p, (_, open, _) => open - 450)).Ok);
        Assert.InRange(Beauty(h), 24, 26);
        Assert.True(Paint(h, "raku", p => Quench(p, (_, open, len) => open + len + 450)).Ok);
        Assert.InRange(Beauty(h), 49, 51);
        // Геть зарано чи геть запізно — нуль.
        Assert.True(Paint(h, "raku", p => Quench(p, (_, open, _) => Math.Max(80, open - 1300))).Ok);
        Assert.Equal(0, Beauty(h));
    }

    [Fact]
    public void A_piece_dropped_before_the_pit_is_lost()
    {
        var h = Wheel();
        OpenNew(h);
        // Другий виріб упустили посеред дороги (далеко від ями) — дві третини краси.
        Assert.True(Paint(h, "raku", p => Quench(p, (_, open, len) => open + len / 2.0,
            to: i => i == 1 ? (500, 200) : (p.GetProperty("shape").GetProperty("pit")[0].GetDouble(), p.GetProperty("shape").GetProperty("pit")[1].GetDouble()))).Ok);
        Assert.InRange(Beauty(h), 66, 67);
    }

    [Fact]
    public void A_slow_carry_cools_the_piece_but_never_below_half()
    {
        Assert.Equal(1, KilnPaint.RakuPieceScore(1500, 1200, true, [1000, 800]));
        Assert.Equal(0.9, KilnPaint.RakuPieceScore(1500, 1500, true, [1000, 800]), 1e-12);
        Assert.Equal(0.5, KilnPaint.RakuPieceScore(1500, 9000, true, [1000, 800]), 1e-12);
        Assert.Equal(0, KilnPaint.RakuPieceScore(1500, 500, false, [1000, 800]));
        // Межі вікна — ще вчасно.
        Assert.Equal(1, KilnPaint.RakuPieceScore(1000, 500, true, [1000, 800]));
        Assert.Equal(1, KilnPaint.RakuPieceScore(1800, 500, true, [1000, 800]));
        Assert.Equal(0.5, KilnPaint.RakuPieceScore(700, 500, true, [1000, 800]), 1e-12);
        Assert.Equal(0, KilnPaint.RakuPieceScore(200, 500, true, [1000, 800]));
        Assert.Equal(0, KilnPaint.RakuPieceScore(2800, 500, true, [1000, 800]));
    }

    [Fact]
    public void Strokes_away_from_the_kiln_and_twitches_are_not_pulls_and_cost_a_little()
    {
        var h = Wheel();
        OpenNew(h);
        Assert.True(Paint(h, "raku", p =>
        {
            var pts = Quench(p, (_, open, len) => open + len / 2.0);
            // Два зайві штрихи вже після всіх виробів: мазок по небу й ще одне «виймання» з порожньої печі.
            var t = pts[^1][0] + 300;
            var kiln = p.GetProperty("shape").GetProperty("kiln");
            for (var j = 0; j < 8; j++) pts.Add([t + j * 27 + j % 3, 100 + j * 30, 100, j == 0 ? 1 : 0]);
            t = pts[^1][0] + 300;
            for (var j = 0; j < 8; j++) pts.Add([t + j * 29 + j % 2, kiln[0].GetDouble() + j * 40, kiln[1].GetDouble(), j == 0 ? 1 : 0]);
            return pts;
        }).Ok);
        Assert.Equal(100 - 2 * (int)KilnPaint.RakuStray, Beauty(h));
    }

    [Fact]
    public void Raku_is_the_home_technique_of_the_raku_style()
    {
        var h = Wheel();
        OpenNew(h);
        var r = Paint(h, "raku", p => Quench(p, (_, open, len) => open + len + 450), style: "raku");
        Assert.True(r.Ok, r.Message);
        Assert.InRange(Beauty(h), 59, 61);
        Assert.Contains("(рідний осередок +10)", r.Message);
    }

    [Fact]
    public void A_robot_hand_or_a_rushed_path_is_refused_for_raku_too()
    {
        var h = Wheel();
        OpenNew(h);
        Assert.True(K(h, new { op = "paint", style = "", tech = "raku" }).Ok);
        var s = Pattern(h).GetProperty("shape");
        double kx = s.GetProperty("kiln")[0].GetDouble(), ky = s.GetProperty("kiln")[1].GetDouble();
        double px = s.GetProperty("pit")[0].GetDouble(), py = s.GetProperty("pit")[1].GetDouble();
        // Робот: кришка, а тоді три ідеально рівні перенесення — однаковий крок часу й однакова довжина кроку.
        var robot = new List<double[]> { new[] { 0.0, kx, ky, 1 } };
        double ms = 0;
        foreach (var w in s.GetProperty("pieces").EnumerateArray())
        {
            ms += w[0].GetInt32() + 100;
            for (var j = 0; j <= 20; j++) robot.Add([ms + j * 16, kx + (px - kx) * j / 20, ky + (py - ky) * j / 20, j == 0 ? 1 : 0]);
            ms += 320;
        }
        h.Clock.AdvanceMs((int)ms + 100);
        Assert.Equal("Рука так рівно не ходить — спробуй ще раз", K(h, PathOf(robot)).Message);
        // Жива рука, але прийшла одразу після візерунка — «так швидко не малюють».
        var g = Wheel();
        OpenNew(g);
        Assert.True(K(g, new { op = "paint", style = "", tech = "raku" }).Ok);
        var pts = Quench(Pattern(g), (_, open, len) => open + len / 2.0);
        g.Clock.Advance(1);
        Assert.Equal("Так швидко не малюють — спробуй ще раз", K(g, PathOf(pts)).Message);
        // Самий тик по кришці — закоротко.
        Assert.Equal("Закоротко: проведи довше", K(g, PathOf(pts.Take(2))).Message);
    }

    [Fact]
    public void Raku_beauty_goes_onto_the_batch_like_any_painting()
    {
        var h = Wheel();
        OpenNew(h);
        Assert.True(Paint(h, "raku", p => Quench(p, (_, open, len) => open + len / 2.0), style: "raku").Ok);
        Patch(h, s =>
        {
            var rack = new JsonArray();
            for (var i = 0; i < 4; i++) rack.Add(new JsonObject { ["ware"] = "bowl", ["clay"] = "", ["dryAt"] = h.Clock.UtcNow.AddMinutes(-1).ToString("O") });
            s["craft"]!["rack"] = rack;
        });
        // Руками, без жодної дії біля заслінки: недогріте, зате без тріщин — і з красою розпису.
        Assert.True(K(h, new { op = "light" }).Ok);
        h.Clock.Advance(KilnHeat.Steps / 10.0 + 0.2);
        Assert.True(K(h, new { op = "open", t = Array.Empty<int[]>() }).Ok);
        var last = Kiln(h).GetProperty("last");
        Assert.Equal("raku", last.GetProperty("style").GetString());
        Assert.Equal(100, last.GetProperty("beauty").GetInt32());
    }
}
