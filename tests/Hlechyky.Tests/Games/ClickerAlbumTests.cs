using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Альбом майстра (пакет C дев'ятого оновлення, ClickerAlbum.cs): сітка «виріб × розпис» із зірками, майстерність
/// і золоті руки, кахляна піч із заміною кахлі, виставка, трипільський музей, збереження й обпал. Правила сітки,
/// зірок, печі й рівнів — публічні статичні функції (їх кличемо напряму), решту — через справжній обпал у горні
/// або патч збереження. Ніде не зашито ні 12 виробів, ні 9 розписів: числа беруться з каталогів.
/// </summary>
public class ClickerAlbumTests
{
    static RoomHarness Wheel(int seed = 1)
    {
        var h = new RoomHarness("clicker", seed: seed);
        h.Solo("Оля");
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static JsonElement Album(RoomHarness h) => View(h).GetProperty("album");
    static JsonElement Craft(RoomHarness h) => View(h).GetProperty("craft");
    static ActResult Act(RoomHarness h, string action, object? payload = null) => h.Act(0, action, payload);
    static ActResult AlbumAct(RoomHarness h, object payload) => Act(h, "album", payload);

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static JsonObject AlbumNode(JsonObject s)
    {
        if (s["album"] is JsonObject a) return a;
        var fresh = new JsonObject();
        s["album"] = fresh;
        return fresh;
    }

    static void Items(RoomHarness h, params (string Key, int N)[] items) => Patch(h, s =>
    {
        var bag = new JsonObject();
        foreach (var (key, n) in items) bag[key] = n;
        s["craft"]!["items"] = bag;
    });

    static void Click(RoomHarness h, int n)
    {
        Assert.True(Act(h, "spin", PotterHands.Human(n)).Ok);
        h.Clock.Advance(1);
    }

    /// <summary>Виліпити n виробів: на колі вже майже готова робота, сушарня порожня — один клік її довершує.</summary>
    static void FormMany(RoomHarness h, int wares)
    {
        Patch(h, s => s["upgrades"]!["workshop"] = 500);         // сушарня на 20
        while (wares > 0)
        {
            var n = Math.Min(Clicker.RackMax, wares);
            Patch(h, s => { s["craft"]!["rack"] = new JsonArray(); s["craft"]!["work"] = 40.0 * n - 1; });
            Click(h, 1);
            wares -= n;
        }
    }

    // ---------- справжній обпал (щоб гачок AlbumOnFired працював, як у грі) ----------

    /// <summary>Сушарня: стільки сухих сирців одного виробу.</summary>
    static void Rack(RoomHarness h, int dry, string ware = "pot") => Patch(h, s =>
    {
        var rack = new JsonArray();
        var now = h.Clock.UtcNow;
        for (var i = 0; i < dry; i++) rack.Add(new JsonObject { ["ware"] = ware, ["clay"] = "", ["dryAt"] = now.AddMinutes(-1).ToString("O") });
        s["craft"]!["rack"] = rack;
    });

    static object Timeline(IEnumerable<(int Ms, int Act)> acts) => new { op = "open", t = acts.Select(a => new[] { a.Ms, a.Act }).ToArray() };

    /// <summary>Умілий палій (той самий, що в тестах горна): тримає жар у смузі, підкидаючи й прикриваючи заслінку.</summary>
    static List<(int Ms, int Act)> Stoker(int seed)
    {
        var acts = new List<(int Ms, int Act)>();
        var open = true;
        var last = -10_000;
        for (var i = 1; i < KilnHeat.Steps - 3 && acts.Count < KilnHeat.MaxActs; i++)
        {
            if (i * 100 - last < 300) continue;
            double fuel = 0, future = 0;
            KilnHeat.Run(seed, acts, (step, T, F, _) =>
            {
                if (step == i - 1) fuel = F;
                if (step == Math.Min(KilnHeat.Steps - 1, i + 20)) future = T;
            });
            var n = i + 21;
            var hi = n < KilnHeat.WarmSteps ? KilnHeat.Hi0 + (KilnHeat.Hi - KilnHeat.Hi0) * n / KilnHeat.WarmSteps : KilnHeat.Hi;
            var lo = n < KilnHeat.WarmSteps ? hi - 150 : KilnHeat.Lo;
            int? act = null;
            if (future > hi - 25 && open) act = KilnHeat.Close;
            else if (future < lo + 40 && !open) act = KilnHeat.Open;
            else if (future < lo + 50 && fuel < KilnHeat.FuelMax - 0.4) act = KilnHeat.Stoke;
            if (act is not { } a) continue;
            acts.Add((i * 100 + 50, a));
            if (a == KilnHeat.Close) open = false;
            if (a == KilnHeat.Open) open = true;
            last = i * 100;
        }
        return acts;
    }

    /// <summary>Розпалити вручну, дочекатись кінця й відкрити рукою вмілого палія — у партії трапляються дзвінкі.</summary>
    static ActResult Burn(RoomHarness h)
    {
        var lit = Act(h, "kiln", new { op = "light" });
        Assert.True(lit.Ok, lit.Message);
        var seed = View(h).GetProperty("kiln").GetProperty("seed").GetInt32();
        h.Clock.Advance(KilnHeat.Steps / 10.0 + 0.2);
        return Act(h, "kiln", Timeline(Stoker(seed)));
    }

    static void Cool(RoomHarness h) => h.Clock.Advance(Clicker.KilnCool + TimeSpan.FromSeconds(1));

    /// <summary>Обпалювати повні партії, доки не справдиться умова (або не скінчаться спроби).</summary>
    static bool BurnUntil(RoomHarness h, Func<bool> done, int batches = 10)
    {
        Patch(h, s => s["upgrades"]!["kiln"] = 180);
        for (var i = 0; i < batches; i++)
        {
            Rack(h, 24);
            Assert.True(Burn(h).Ok);
            if (done()) return true;
            Cool(h);
        }
        return done();
    }

    static int PopCount(int x) => System.Numerics.BitOperations.PopCount((uint)x);

    static int[] Full() => Enumerable.Repeat((1 << Clicker.AlbumColumns) - 1, Clicker.Wares.Length).ToArray();

    static int[] FullRows(int rows) => Enumerable.Range(0, Clicker.Wares.Length).Select(i => i < rows ? (1 << Clicker.AlbumColumns) - 1 : 0).ToArray();

    static List<StoveTile> Tiles(int n, int q, string style = "kosiv") => Enumerable.Repeat(new StoveTile(style, q), n).ToList();

    static double Bonus(IReadOnlyList<int> cells, IReadOnlyList<int>? stars = null, IReadOnlyList<StoveTile>? stove = null, int finds = 0) =>
        Clicker.AlbumBonusFor(cells, stars, stove, finds);

    // ---------- сітка: чисті правила ----------

    [Fact]
    public void The_grid_is_wares_by_styles_and_grows_with_the_catalogs()
    {
        // Ні 12, ні 9 ніде не зашито: додасться виріб чи розпис — сітка сама стане більшою.
        Assert.Equal(Clicker.Styles.Length + 1, Clicker.AlbumColumns);
        Assert.Equal(Clicker.Wares.Length * Clicker.AlbumColumns, Clicker.AlbumSize);
        Assert.Equal(0, Clicker.AlbumStyleIndex(""));
        Assert.Equal(0, Clicker.AlbumStyleIndex(null));
        Assert.Equal(1, Clicker.AlbumStyleIndex("gavarets"));
        Assert.Equal(8, Clicker.AlbumStyleIndex("trypillia"));
        Assert.Equal(-1, Clicker.AlbumStyleIndex("gzhel"));
        Assert.Equal(0, Clicker.AlbumWareIndex("pot"));
        Assert.Equal(11, Clicker.AlbumWareIndex("lion"));
        Assert.Equal(-1, Clicker.AlbumWareIndex("vase"));
    }

    [Fact]
    public void Each_cell_is_half_a_percent()
    {
        var cells = new int[Clicker.Wares.Length];
        cells[0] = 0b101;
        cells[5] = 0b1;
        Assert.Equal(3, Clicker.AlbumOpenCount(cells));
        Assert.Equal(0.015, Bonus(cells), 9);
    }

    [Fact]
    public void A_full_row_adds_five_percent_on_top_of_its_cells()
    {
        var cells = FullRows(1);
        Assert.Equal(1, Clicker.AlbumFullRows(cells));
        Assert.Equal(0, Clicker.AlbumFullColumns(cells));
        Assert.Equal(Clicker.AlbumColumns * 0.005 + 0.05, Bonus(cells), 9);
    }

    [Fact]
    public void A_full_column_adds_six_percent_on_top_of_its_cells()
    {
        var cells = Enumerable.Repeat(1 << 4, Clicker.Wares.Length).ToArray();   // косівський на всіх виробах
        cells[3] |= 1;                                                          // і одна проста макітра
        Assert.Equal(1, Clicker.AlbumFullColumns(cells));
        Assert.Equal(0, Clicker.AlbumFullRows(cells));
        Assert.Equal((Clicker.Wares.Length + 1) * 0.005 + 0.06, Bonus(cells), 9);
    }

    [Fact]
    public void The_whole_grid_without_stars_is_cells_rows_and_columns()
    {
        var cells = Full();
        Assert.Equal(Clicker.AlbumSize, Clicker.AlbumOpenCount(cells));
        Assert.Equal(Clicker.Wares.Length, Clicker.AlbumFullRows(cells));
        Assert.Equal(Clicker.AlbumColumns, Clicker.AlbumFullColumns(cells));
        Assert.Equal(Clicker.AlbumSize * 0.005 + Clicker.Wares.Length * 0.05 + Clicker.AlbumColumns * 0.06, Bonus(cells), 9);
    }

    [Fact]
    public void Stray_bits_beyond_the_last_style_count_for_nothing()
    {
        var cells = new int[Clicker.Wares.Length];
        var stars = new int[Clicker.Wares.Length];
        cells[0] = 1 << 12;
        stars[0] = 1 << 12;
        Assert.Equal(0, Clicker.AlbumOpenCount(cells));
        Assert.Equal(0, Clicker.AlbumStarCount(stars));
        Assert.Equal(0, Bonus(cells, stars, null, 1 << 9));
    }

    // ---------- зірки ----------

    [Fact]
    public void A_star_is_a_percent_on_top_of_its_cell()
    {
        var cells = new int[Clicker.Wares.Length];
        var stars = new int[Clicker.Wares.Length];
        cells[0] = 0b111;
        stars[0] = 0b101;
        Assert.Equal(2, Clicker.AlbumStarCount(stars));
        Assert.Equal(0, Clicker.AlbumStarRows(stars));
        Assert.Equal(3 * 0.005 + 2 * 0.01, Bonus(cells, stars), 9);
    }

    [Fact]
    public void A_row_all_in_stars_adds_five_percent_more()
    {
        var cells = FullRows(1);
        var stars = FullRows(1);
        Assert.Equal(Clicker.AlbumColumns, Clicker.AlbumStarCount(stars));
        Assert.Equal(1, Clicker.AlbumStarRows(stars));
        Assert.Equal(Clicker.AlbumColumns * 0.005 + 0.05 + Clicker.AlbumColumns * 0.01 + 0.05, Bonus(cells, stars), 9);
    }

    [Fact]
    public void The_whole_album_in_stars_adds_twenty_five_percent_on_top()
    {
        var cells = Full();
        var stars = Full();
        var n = Clicker.AlbumSize;
        var rows = Clicker.Wares.Length;
        var expect = n * 0.005 + rows * 0.05 + Clicker.AlbumColumns * 0.06 + n * 0.01 + rows * 0.05 + 0.25;
        Assert.Equal(expect, Bonus(cells, stars), 9);
        // Дванадцять виробів — +361 %; коли пакет «Ремесло» долучить три нові, та сама формула дасть +431,5 %.
        Assert.True(expect >= 3.6, expect.ToString());
        Assert.Equal(4.315, 135 * 0.005 + 15 * 0.05 + 9 * 0.06 + 135 * 0.01 + 15 * 0.05 + 0.25, 9);
    }

    [Fact]
    public void A_star_without_its_cell_still_counts_only_once_for_the_cell()
    {
        // Зірка без клітинки — зіпсоване збереження; Load її відкриває, а сама функція рахує зірку й клітинку окремо.
        var cells = new int[Clicker.Wares.Length];
        var stars = new int[Clicker.Wares.Length];
        stars[0] = 1;
        Assert.Equal(0.01, Bonus(cells, stars), 9);
        cells[0] = 1;
        Assert.Equal(0.015, Bonus(cells, stars), 9);
    }

    // ---------- піч: чисті правила ----------

    [Fact]
    public void The_stove_pays_by_quality_not_by_pattern()
    {
        Assert.Equal(0.015, Clicker.AlbumStoveBonus(Tiles(1, 2)), 9);
        Assert.Equal(0.03, Clicker.AlbumStoveBonus(Tiles(1, 3)), 9);
        // Розпис на бонус не впливає — лише якість.
        Assert.Equal(Clicker.AlbumStoveBonus(Tiles(3, 2, "kosiv")), Clicker.AlbumStoveBonus(Tiles(3, 2, "")), 9);
        Assert.Equal(0.015 * 5 + 0.03 * 3, Clicker.AlbumStoveBonus([.. Tiles(5, 2), .. Tiles(3, 3)]), 9);
        Assert.Equal(0, Clicker.AlbumStoveBonus(null));
        Assert.Equal(0, Clicker.AlbumStoveBonus([]));
    }

    [Fact]
    public void A_full_stove_adds_five_and_a_stove_of_ringing_tiles_ten_instead()
    {
        Assert.Equal(0.015 * 12 + 0.05, Clicker.AlbumStoveBonus(Tiles(12, 2)), 9);
        Assert.Equal(0.015 * 11 + 0.03 + 0.05, Clicker.AlbumStoveBonus([.. Tiles(11, 2), .. Tiles(1, 3)]), 9);
        // Уся піч із дзвінких — +10 % замість +5 %.
        Assert.Equal(0.03 * 12 + 0.10, Clicker.AlbumStoveBonus(Tiles(12, 3)), 9);
        // Понад дванадцять гнізд не буває — зайве не рахується.
        Assert.Equal(0.03 * 12 + 0.10, Clicker.AlbumStoveBonus(Tiles(40, 3)), 9);
    }

    [Fact]
    public void The_lavish_tile_is_the_best_one_and_the_star_rule_already_knows_it()
    {
        // Розкішна (q 4) прийде з горном пакета B1; альбом готовий заздалегідь — і зіркою, і ставкою кахлі.
        Assert.Equal(4, Clicker.StoveQualityBonus.Length - 1);
        Assert.Equal(0.04, Clicker.StoveQualityBonus[4], 9);
        Assert.True(Clicker.StoveQualityBonus[4] > Clicker.StoveQualityBonus[3]);
        Assert.Equal(0.04 * 12 + 0.10, Clicker.AlbumStoveBonus(Tiles(12, 4)), 9);
        Assert.Equal(0.03 * 11 + 0.04 + 0.10, Clicker.AlbumStoveBonus([.. Tiles(11, 3), .. Tiles(1, 4)]), 9);
        // Зірка — від «дзвінкої» і вище, тож розкішний виріб зірку теж ставить.
        Assert.Equal(3, Clicker.StarQuality);
        Assert.True(4 >= Clicker.StarQuality);
        Assert.Equal(3, Clicker.StoveRingQuality);
    }

    [Fact]
    public void The_museum_is_a_percent_a_find_and_five_more_when_full()
    {
        var empty = new int[Clicker.Wares.Length];
        Assert.Equal(0.03, Bonus(empty, null, null, 0b1011), 9);
        Assert.Equal(0.13, Bonus(empty, null, null, 0xFF), 9);
        Assert.Equal(0.13, Clicker.AlbumMuseumBonus(0xFF), 9);
    }

    // ---------- майстерність: чисті правила ----------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 0)]
    [InlineData(5, 1)]
    [InlineData(14, 1)]
    [InlineData(15, 2)]
    [InlineData(40, 3)]
    [InlineData(100, 4)]
    [InlineData(250, 5)]
    [InlineData(600, 6)]
    [InlineData(1_499, 6)]
    [InlineData(1_500, 7)]
    [InlineData(4_000, 8)]
    [InlineData(10_000, 9)]
    [InlineData(24_999, 9)]
    [InlineData(25_000, 10)]
    [InlineData(10_000_000, 10)]
    public void Mastery_levels_follow_the_thresholds(long fired, int level) => Assert.Equal(level, Clicker.MasteryLevel(fired));

    [Fact]
    public void Each_mastery_level_is_six_percent_less_work_and_ten_percent_more_value()
    {
        Assert.Equal(1, Clicker.MasteryWorkMult(0));
        Assert.Equal(0.94, Clicker.MasteryWorkMult(1), 9);
        Assert.Equal(0.7, Clicker.MasteryWorkMult(5), 9);
        // Десятий рівень — рівно підлога роботи (40 %), нижче основа й не пустила б.
        Assert.Equal(0.4, Clicker.MasteryWorkMult(10), 9);
        Assert.Equal(Clicker.MinWorkShare, Clicker.MasteryWorkMult(10), 9);
        Assert.Equal(0.4, Clicker.MasteryWorkMult(99), 9);
        Assert.Equal(1, Clicker.MasteryValueMult(0));
        Assert.Equal(1.1, Clicker.MasteryValueMult(1), 9);
        Assert.Equal(1.9, Clicker.MasteryValueMult(9), 9);
        // Золоті руки: ×2 за рівні і ще ×1,25 зверху.
        Assert.Equal(2.5, Clicker.MasteryValueMult(10), 9);
        Assert.Equal(2.5, Clicker.MasteryValueMult(77), 9);
    }

    // ---------- у грі ----------

    [Fact]
    public void A_new_game_has_an_empty_album()
    {
        var a = Album(Wheel());
        Assert.Equal(Clicker.Wares.Length, a.GetProperty("cells").GetArrayLength());
        Assert.All(a.GetProperty("cells").EnumerateArray(), c => Assert.Equal(0, c.GetInt32()));
        Assert.Equal(0, a.GetProperty("open").GetInt32());
        Assert.Equal(0, a.GetProperty("starOpen").GetInt32());
        Assert.Equal(0, a.GetProperty("starRows").GetInt32());
        Assert.Equal(Clicker.AlbumSize, a.GetProperty("size").GetInt32());
        Assert.Equal(0, a.GetProperty("stove").GetArrayLength());
        Assert.False(a.GetProperty("stoveRing").GetBoolean());
        Assert.Equal(0, a.GetProperty("stoveBonus").GetDouble());
        Assert.Equal(0, a.GetProperty("show").GetArrayLength());
        Assert.Equal(0, a.GetProperty("finds").GetInt32());
        Assert.Equal(0, a.GetProperty("shards").GetInt32());
        Assert.Equal(JsonValueKind.Null, a.GetProperty("find").ValueKind);
        Assert.Equal(0, a.GetProperty("bonus").GetDouble());
        Assert.All(a.GetProperty("mastery").EnumerateArray(), m => Assert.Equal(0, m.GetInt32()));
    }

    [Fact]
    public void Saved_cells_and_stars_show_in_the_view_and_boost_everything()
    {
        var h = Wheel();
        var before = View(h).GetProperty("allMult").GetDouble();
        Patch(h, s =>
        {
            var a = AlbumNode(s);
            a["cells"] = new JsonObject
            {
                ["pot"] = new JsonArray("", "gavarets", "vasylkiv", "bubnivka", "kosiv", "opishnia", "mezhyhirya", "petrykivka", "trypillia"),
                ["jug"] = new JsonArray("kosiv"),
            };
            a["stars"] = new JsonObject { ["jug"] = new JsonArray("kosiv") };
        });
        var al = Album(h);
        Assert.Equal(511, al.GetProperty("cells")[0].GetInt32());
        Assert.Equal(1 << 4, al.GetProperty("cells")[2].GetInt32());
        Assert.Equal(1 << 4, al.GetProperty("stars")[2].GetInt32());
        Assert.Equal(10, al.GetProperty("open").GetInt32());
        Assert.Equal(1, al.GetProperty("starOpen").GetInt32());
        Assert.Equal(1, al.GetProperty("rows").GetInt32());
        Assert.Equal(0, al.GetProperty("cols").GetInt32());
        var bonus = 10 * 0.005 + 0.05 + 0.01;
        Assert.Equal(bonus, al.GetProperty("bonus").GetDouble(), 9);
        Assert.Equal(before * (1 + bonus), View(h).GetProperty("allMult").GetDouble(), 9);
    }

    [Fact]
    public void A_star_in_a_save_opens_its_cell_too_and_junk_is_dropped()
    {
        var h = Wheel();
        Patch(h, s =>
        {
            var a = AlbumNode(s);
            a["cells"] = new JsonObject { ["vase"] = new JsonArray(""), ["pot"] = new JsonArray("gzhel", "") };
            a["stars"] = new JsonObject { ["bowl"] = new JsonArray("trypillia") };
        });
        var al = Album(h);
        Assert.Equal(1, al.GetProperty("cells")[0].GetInt32());
        Assert.Equal(1 << 8, al.GetProperty("cells")[1].GetInt32());
        Assert.Equal(1 << 8, al.GetProperty("stars")[1].GetInt32());
        Assert.Equal(2, al.GetProperty("open").GetInt32());
        Assert.Equal(1, al.GetProperty("starOpen").GetInt32());
    }

    [Fact]
    public void An_old_save_of_twelve_wares_lands_on_the_right_rows_whatever_the_catalog()
    {
        // Маски в збереженні — не числа, а ключі: припишуть виробів (пакет «Ремесло» додає три) — старе збереження
        // читається так само, а сітка просто стає більшою.
        var h = Wheel();
        Patch(h, s => AlbumNode(s)["cells"] = new JsonObject
        {
            ["pot"] = new JsonArray("", "kosiv"),
            ["lion"] = new JsonArray("trypillia"),
            ["kukhol"] = new JsonArray(""),          // виріб дев'ятого оновлення: поки його нема — просто пропускається
            ["vase"] = new JsonArray(""),            // такого нема й не буде
        });
        var al = Album(h);
        Assert.Equal(Clicker.Wares.Length, al.GetProperty("cells").GetArrayLength());
        Assert.Equal(1 | (1 << 4), al.GetProperty("cells")[Clicker.AlbumWareIndex("pot")].GetInt32());
        Assert.Equal(1 << 8, al.GetProperty("cells")[Clicker.AlbumWareIndex("lion")].GetInt32());
        Assert.Equal(3 + (Clicker.AlbumWareIndex("kukhol") >= 0 ? 1 : 0), al.GetProperty("open").GetInt32());
        // І назад: збереження пише ті самі ключі.
        Patch(h, _ => { });
        Assert.Equal(1 | (1 << 4), Album(h).GetProperty("cells")[Clicker.AlbumWareIndex("pot")].GetInt32());
    }

    [Fact]
    public void Firing_a_ringing_ware_opens_a_cell_and_lights_a_star()
    {
        var h = Wheel(seed: 3);
        Assert.True(BurnUntil(h, () => Album(h).GetProperty("starOpen").GetInt32() > 0),
            "за десять умілих партій жодного дзвінкого горщика — зламано зірки чи модель жару");
        var al = Album(h);
        var row = Clicker.AlbumWareIndex("pot");
        Assert.Equal(1, al.GetProperty("cells")[row].GetInt32() & 1);      // проста клітинка горщика відкрита
        Assert.Equal(1, al.GetProperty("stars")[row].GetInt32() & 1);      // і з зіркою
        Assert.Equal(1, al.GetProperty("open").GetInt32());
        Assert.Equal(1, al.GetProperty("starOpen").GetInt32());
        Assert.Equal(0.005 + 0.01, al.GetProperty("bonus").GetDouble(), 9);
        // Звичайні вироби зірки не дають: перевіряємо, що зірок не більше за клітинки.
        Assert.True(al.GetProperty("starOpen").GetInt32() <= al.GetProperty("open").GetInt32());
    }

    [Fact]
    public void The_last_star_in_the_album_earns_the_ringing_album()
    {
        // Увесь альбом у зірках, крім простого горщика: перший дзвінкий горщик закриває і рядок, і весь альбом.
        var h = Wheel(seed: 3);
        Patch(h, s =>
        {
            var cells = new JsonObject();
            var stars = new JsonObject();
            foreach (var w in Clicker.Wares)
            {
                var all = new JsonArray();
                var star = new JsonArray();
                all.Add("");
                if (w.Key != "pot") star.Add("");
                foreach (var st in Clicker.Styles) { all.Add(st.Key); star.Add(st.Key); }
                cells[w.Key] = all;
                stars[w.Key] = star;
            }
            var a = AlbumNode(s);
            a["cells"] = cells;
            a["stars"] = stars;
        });
        Assert.Equal(Clicker.AlbumSize - 1, Album(h).GetProperty("starOpen").GetInt32());
        Assert.DoesNotContain(h.Awards, x => x.Reason == "ach:potter-album-stars");
        Assert.True(BurnUntil(h, () => Album(h).GetProperty("starOpen").GetInt32() == Clicker.AlbumSize),
            "за десять умілих партій жодного дзвінкого горщика");
        var al = Album(h);
        Assert.Equal(Clicker.Wares.Length, al.GetProperty("starRows").GetInt32());
        Assert.Contains(h.Awards, x => x.Reason == "ach:potter-album-stars");
        var expect = Clicker.AlbumSize * 0.005 + Clicker.Wares.Length * 0.05 + Clicker.AlbumColumns * 0.06
            + Clicker.AlbumSize * 0.01 + Clicker.Wares.Length * 0.05 + 0.25;
        Assert.Equal(expect, al.GetProperty("bonus").GetDouble(), 9);
    }

    [Fact]
    public void Golden_hands_come_with_the_tenth_mastery_level()
    {
        var h = Wheel();
        Patch(h, s => s["craft"]!["firedBy"] = new JsonObject { ["pot"] = 24_995 });
        Assert.Equal(9, Album(h).GetProperty("mastery")[0].GetInt32());
        Assert.DoesNotContain(h.Awards, x => x.Reason == "ach:potter-mastery");
        Rack(h, 6);
        Assert.True(Act(h, "kiln", new { op = "light", helper = true }).Ok);
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(10, Album(h).GetProperty("mastery")[0].GetInt32());
        Assert.Contains(View(h).GetProperty("away").GetProperty("notes").EnumerateArray().Select(x => x.GetString()),
            n => n!.StartsWith("🖐 Золоті руки"));
        // Палій обпалив без гравця — ачівка чекала в черзі й виїхала з найближчою дією.
        Assert.True(Act(h, "wear", new { key = "" }).Ok);
        Assert.Contains(h.Awards, x => x.Reason == "ach:potter-mastery");
    }

    [Fact]
    public void Mastery_makes_the_ware_quicker_to_form()
    {
        var h = Wheel();
        int Need(string ware) => Craft(h).GetProperty("wares").EnumerateArray().First(w => w.GetProperty("key").GetString() == ware).GetProperty("need").GetInt32();
        Assert.Equal(40, Need("pot"));
        Patch(h, s => s["craft"]!["firedBy"] = new JsonObject { ["pot"] = 5, ["bowl"] = 25_000, ["lion"] = 600 });
        Assert.Equal(38, Need("pot"));                              // ceil(40 × 0,94)
        Assert.Equal(20, Need("bowl"));                             // 50 × 0,4 — рівно підлога
        Assert.Equal(320, Need("lion"));                            // 500 × 0,64
        var mastery = Album(h).GetProperty("mastery").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        Assert.Equal(Clicker.Wares.Length, mastery.Length);
        Assert.Equal(1, mastery[Clicker.AlbumWareIndex("pot")]);
        Assert.Equal(10, mastery[Clicker.AlbumWareIndex("bowl")]);
        Assert.Equal(6, mastery[Clicker.AlbumWareIndex("lion")]);

        // Виріб на колі теж просить менше: 38 кліків — і горщик на сушарні.
        for (var i = 0; i < 38; i += 12) Click(h, Math.Min(12, 38 - i));
        Assert.Equal(1, Craft(h).GetProperty("rack").GetArrayLength());
    }

    [Fact]
    public void Mastery_raises_the_price_of_the_ware()
    {
        var h = Wheel();
        Patch(h, s => s["upgrades"]!["kiln"] = 100);
        long Value(string ware) => Craft(h).GetProperty("wares").EnumerateArray().First(w => w.GetProperty("key").GetString() == ware).GetProperty("value").GetInt64();
        var jug = Value("jug");
        var pot = Value("pot");
        Patch(h, s => s["craft"]!["firedBy"] = new JsonObject { ["jug"] = 250 });      // рівень 5 → +50 %
        Assert.InRange(Value("jug"), jug * 1.5 - 2, jug * 1.5 + 2);
        Assert.Equal(pot, Value("pot"));
        Items(h, ("jug|kosiv|3", 1));
        var item = Craft(h).GetProperty("items")[0].GetProperty("value").GetInt64();
        var passive = View(h).GetProperty("baseSecond").GetDouble();
        var expect = passive * 6 * 2.6 * 1.5 * 1.5;                                   // секунди × якість × розпис × майстерність
        Assert.InRange(item, expect - 2, expect + 2);
        // Золоті руки додають ще чверть.
        Patch(h, s => s["craft"]!["firedBy"] = new JsonObject { ["jug"] = 25_000 });
        Assert.InRange(Value("jug"), jug * 2.5 - 3, jug * 2.5 + 3);
    }

    [Fact]
    public void Mastery_is_not_a_prestige_casualty()
    {
        var h = Wheel();
        Patch(h, s => { s["total"] = 2_000_000_000; s["craft"]!["firedBy"] = new JsonObject { ["pot"] = 100 }; });
        Assert.True(Act(h, "fire").Ok);
        Assert.Equal(4, Album(h).GetProperty("mastery")[0].GetInt32());
        Assert.Equal(31, Craft(h).GetProperty("wares")[0].GetProperty("need").GetInt32());   // ceil(40 × 0,76)
    }

    // ---------- кахляна піч ----------

    [Fact]
    public void A_good_tile_goes_from_the_store_into_the_stove()
    {
        var h = Wheel();
        Items(h, ("tile|kosiv|2", 2), ("pot||1", 1));
        Assert.Equal(2, Album(h).GetProperty("tiles").GetInt32());
        var r = AlbumAct(h, new { op = "tile", key = "tile|kosiv|2" });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("+1,5 %", r.Message);
        var al = Album(h);
        Assert.Equal(1, al.GetProperty("stove").GetArrayLength());
        Assert.Equal("kosiv", al.GetProperty("stove")[0].GetProperty("style").GetString());
        Assert.Equal(2, al.GetProperty("stove")[0].GetProperty("q").GetInt32());
        Assert.Equal(0.015, al.GetProperty("bonus").GetDouble(), 9);
        Assert.Equal(0.015, al.GetProperty("stoveBonus").GetDouble(), 9);
        Assert.Equal(1, al.GetProperty("tiles").GetInt32());
        var items = Craft(h).GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!, x => x.GetProperty("n").GetInt32());
        Assert.Equal(1, items["tile|kosiv|2"]);
        Assert.Equal(1, items["pot||1"]);
    }

    [Fact]
    public void A_ringing_tile_is_worth_twice_a_good_one()
    {
        var h = Wheel();
        Items(h, ("tile|opishnia|3", 1));
        Assert.True(AlbumAct(h, new { op = "tile" }).Ok);
        Assert.Equal(0.03, Album(h).GetProperty("stoveBonus").GetDouble(), 9);
    }

    [Fact]
    public void The_stove_refuses_plain_quality_other_wares_and_empty_hands()
    {
        var h = Wheel();
        Assert.StartsWith("У коморі нема доброї кахлі", AlbumAct(h, new { op = "tile" }).Message);
        Items(h, ("tile||1", 3), ("pot||3", 1));
        Assert.StartsWith("Звичайна кахля на піч не йде", AlbumAct(h, new { op = "tile", key = "tile||1" }).Message);
        Assert.Equal("У піч кладуть лише кахлі", AlbumAct(h, new { op = "tile", key = "pot||3" }).Message);
        Assert.Equal("У піч кладуть лише кахлі", AlbumAct(h, new { op = "tile", key = "nonsense" }).Message);
        Assert.StartsWith("У коморі нема доброї кахлі", AlbumAct(h, new { op = "tile" }).Message);
        Assert.StartsWith("У коморі нема доброї кахлі", AlbumAct(h, new { op = "tile", key = "tile|kosiv|3" }).Message);
        Assert.Equal("Такого в альбомі не роблять", AlbumAct(h, new { op = "burn" }).Message);
        Assert.Equal(0, Album(h).GetProperty("stove").GetArrayLength());
        Assert.Equal(4, Craft(h).GetProperty("items").EnumerateArray().Sum(x => x.GetProperty("n").GetInt32()));
    }

    [Fact]
    public void Without_a_key_an_empty_socket_takes_the_plainest_good_tile()
    {
        var h = Wheel();
        Items(h, ("tile|opishnia|3", 1), ("tile|bubnivka|2", 1), ("tile||1", 5));
        Assert.True(AlbumAct(h, new { op = "tile" }).Ok);
        Assert.Equal("bubnivka", Album(h).GetProperty("stove")[0].GetProperty("style").GetString());
        Assert.True(AlbumAct(h, new { op = "tile" }).Ok);
        Assert.Equal(3, Album(h).GetProperty("stove")[1].GetProperty("q").GetInt32());
        Assert.StartsWith("У коморі нема доброї кахлі", AlbumAct(h, new { op = "tile" }).Message);
    }

    [Fact]
    public void Twelve_tiles_make_a_stove_and_an_achievement_and_then_only_a_swap()
    {
        var h = Wheel();
        Items(h, ("tile|kosiv|2", 13));
        for (var i = 0; i < 11; i++) Assert.True(AlbumAct(h, new { op = "tile", key = "tile|kosiv|2" }).Ok);
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:potter-stove");
        var last = AlbumAct(h, new { op = "tile", key = "tile|kosiv|2" });
        Assert.True(last.Ok);
        Assert.Contains("+23 %", last.Message);                       // 12 × 1,5 % + 5 % повної печі
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-stove");
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:potter-stove-ring");
        Assert.Equal(0.23, Album(h).GetProperty("bonus").GetDouble(), 9);
        Assert.False(Album(h).GetProperty("stoveRing").GetBoolean());
        Assert.StartsWith("Піч уже вся в кахлях", AlbumAct(h, new { op = "tile", key = "tile|kosiv|2" }).Message);
        Assert.Equal(1, Craft(h).GetProperty("items")[0].GetProperty("n").GetInt32());
    }

    [Fact]
    public void Swapping_a_tile_puts_the_old_one_back_into_the_store()
    {
        var h = Wheel();
        Items(h, ("tile|kosiv|2", 1), ("tile|opishnia|3", 1));
        Assert.True(AlbumAct(h, new { op = "tile", key = "tile|kosiv|2" }).Ok);
        var r = AlbumAct(h, new { op = "tile", slot = 0, key = "tile|opishnia|3" });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("вернулась у комору", r.Message);
        var al = Album(h);
        Assert.Equal(1, al.GetProperty("stove").GetArrayLength());
        Assert.Equal("opishnia", al.GetProperty("stove")[0].GetProperty("style").GetString());
        Assert.Equal(3, al.GetProperty("stove")[0].GetProperty("q").GetInt32());
        Assert.Equal(0.03, al.GetProperty("stoveBonus").GetDouble(), 9);
        var items = Craft(h).GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!, x => x.GetProperty("n").GetInt32());
        Assert.Equal(1, items["tile|kosiv|2"]);
        Assert.False(items.ContainsKey("tile|opishnia|3"));
    }

    [Fact]
    public void Swapping_refuses_the_very_same_tile_and_a_socket_that_is_not_there()
    {
        var h = Wheel();
        Items(h, ("tile|kosiv|2", 3));
        Assert.True(AlbumAct(h, new { op = "tile", key = "tile|kosiv|2" }).Ok);
        Assert.Equal("Точнісінько така кахля в цьому гнізді вже стоїть", AlbumAct(h, new { op = "tile", slot = 0, key = "tile|kosiv|2" }).Message);
        // Гніздо за краєм печі — просто наступна кахля в порожнє гніздо.
        Assert.True(AlbumAct(h, new { op = "tile", slot = 9, key = "tile|kosiv|2" }).Ok);
        Assert.Equal(2, Album(h).GetProperty("stove").GetArrayLength());
        Assert.Equal(1, Craft(h).GetProperty("items")[0].GetProperty("n").GetInt32());
    }

    [Fact]
    public void Without_a_key_a_swap_takes_the_best_tile_in_the_store()
    {
        var h = Wheel();
        Items(h, ("tile|kosiv|2", 1), ("tile||2", 1), ("tile|opishnia|3", 1));
        Assert.True(AlbumAct(h, new { op = "tile", key = "tile|kosiv|2" }).Ok);
        Assert.True(AlbumAct(h, new { op = "tile", slot = 0 }).Ok);
        Assert.Equal(3, Album(h).GetProperty("stove")[0].GetProperty("q").GetInt32());
    }

    [Fact]
    public void A_stove_all_in_ringing_tiles_gives_ten_percent_and_the_ring_achievement()
    {
        var h = Wheel();
        Items(h, ("tile|kosiv|2", 12), ("tile|opishnia|3", 12));
        for (var i = 0; i < 12; i++) Assert.True(AlbumAct(h, new { op = "tile", key = "tile|kosiv|2" }).Ok);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-stove");
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:potter-stove-ring");
        for (var i = 0; i < 11; i++) Assert.True(AlbumAct(h, new { op = "tile", slot = i, key = "tile|opishnia|3" }).Ok);
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:potter-stove-ring");
        var last = AlbumAct(h, new { op = "tile", slot = 11, key = "tile|opishnia|3" });
        Assert.True(last.Ok, last.Message);
        Assert.Contains("Уся піч у дзвінких", last.Message);
        Assert.Contains("+46 %", last.Message);                       // 12 × 3 % + 10 % замість 5 %
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-stove-ring");
        var al = Album(h);
        Assert.True(al.GetProperty("stoveRing").GetBoolean());
        Assert.Equal(0.46, al.GetProperty("stoveBonus").GetDouble(), 9);
        // Дванадцять добрих кахлів повернулись у комору.
        var items = Craft(h).GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!, x => x.GetProperty("n").GetInt32());
        Assert.Equal(12, items["tile|kosiv|2"]);
    }

    [Fact]
    public void A_broken_stove_in_a_save_keeps_only_real_good_tiles_and_no_more_than_twelve()
    {
        var h = Wheel();
        Patch(h, s =>
        {
            var stove = new JsonArray();
            stove.Add(new JsonObject { ["style"] = "kosiv", ["quality"] = 1 });
            stove.Add(new JsonObject { ["style"] = "gzhel", ["quality"] = 3 });
            stove.Add(new JsonObject { ["style"] = null, ["quality"] = 2 });
            for (var i = 0; i < 20; i++) stove.Add(new JsonObject { ["style"] = "opishnia", ["quality"] = 3 });
            AlbumNode(s)["stove"] = stove;
        });
        var st = Album(h).GetProperty("stove");
        Assert.Equal(12, st.GetArrayLength());
        Assert.Equal("", st[0].GetProperty("style").GetString());
    }

    [Fact]
    public void The_stove_save_accepts_every_quality_the_game_knows_and_nothing_above()
    {
        // Поки найвища якість — дзвінка; коли горно навчиться розкішних, збереження підхопить їх без правок тут.
        var top = Clicker.QualityMult.Length - 1;
        var h = Wheel();
        Patch(h, s => AlbumNode(s)["stove"] = new JsonArray(
            new JsonObject { ["style"] = "kosiv", ["quality"] = top },
            new JsonObject { ["style"] = "kosiv", ["quality"] = top + 1 }));
        var st = Album(h).GetProperty("stove");
        Assert.Equal(1, st.GetArrayLength());
        Assert.Equal(top, st[0].GetProperty("q").GetInt32());
        Assert.Equal(Clicker.StoveQualityBonus[top], Album(h).GetProperty("stoveBonus").GetDouble(), 9);
    }

    // ---------- 📌 виставка ----------

    static void OpenCells(RoomHarness h) => Patch(h, s =>
    {
        var a = AlbumNode(s);
        a["cells"] = new JsonObject { ["pot"] = new JsonArray("", "kosiv"), ["jug"] = new JsonArray(""), ["makitra"] = new JsonArray("opishnia") };
        a["stars"] = new JsonObject { ["pot"] = new JsonArray("kosiv") };
    });

    [Fact]
    public void Up_to_three_open_cells_go_on_show()
    {
        var h = Wheel();
        OpenCells(h);
        var r = AlbumAct(h, new { op = "show", keys = new[] { "pot|kosiv", "jug|" } });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("На виставці", r.Message);
        var show = Album(h).GetProperty("show");
        Assert.Equal(2, show.GetArrayLength());
        Assert.Equal("pot|kosiv", show[0].GetProperty("key").GetString());
        Assert.Equal("pot", show[0].GetProperty("ware").GetString());
        Assert.Equal("kosiv", show[0].GetProperty("style").GetString());
        Assert.True(show[0].GetProperty("star").GetBoolean());
        Assert.Equal("jug", show[1].GetProperty("ware").GetString());
        Assert.Equal("", show[1].GetProperty("style").GetString());
        Assert.False(show[1].GetProperty("star").GetBoolean());
    }

    [Fact]
    public void The_show_refuses_a_fourth_a_closed_cell_and_nonsense()
    {
        var h = Wheel();
        OpenCells(h);
        Assert.True(AlbumAct(h, new { op = "show", keys = new[] { "pot|" } }).Ok);
        Assert.Equal("На виставку йде лише те, що вже є в альбомі", AlbumAct(h, new { op = "show", keys = new[] { "bowl|" } }).Message);
        Assert.Equal("На виставку йде лише те, що вже є в альбомі", AlbumAct(h, new { op = "show", keys = new[] { "vase|" } }).Message);
        Assert.Equal("На виставку йде лише те, що вже є в альбомі", AlbumAct(h, new { op = "show", keys = new[] { "pot|gzhel" } }).Message);
        Assert.Equal("На виставку йде лише те, що вже є в альбомі", AlbumAct(h, new { op = "show", keys = new[] { "дурня" } }).Message);
        Assert.Equal($"На видноті вміщається {Clicker.ShowMax} вироби — більше вже комора",
            AlbumAct(h, new { op = "show", keys = new[] { "pot|", "pot|kosiv", "jug|", "makitra|opishnia" } }).Message);
        // Відмова нічого не змінила.
        Assert.Equal(1, Album(h).GetProperty("show").GetArrayLength());
        // Однакові ключі не займають двох місць.
        Assert.True(AlbumAct(h, new { op = "show", keys = new[] { "pot|", "pot|", "jug|" } }).Ok);
        Assert.Equal(2, Album(h).GetProperty("show").GetArrayLength());
        // Порожній список прибирає виставку.
        var off = AlbumAct(h, new { op = "show", keys = Array.Empty<string>() });
        Assert.True(off.Ok);
        Assert.StartsWith("📌 Виставку прибрано", off.Message);
        Assert.Equal(0, Album(h).GetProperty("show").GetArrayLength());
    }

    [Fact]
    public void The_show_survives_a_reload_and_a_firing_but_drops_cells_that_are_not_in_the_album()
    {
        var h = Wheel();
        OpenCells(h);
        Assert.True(AlbumAct(h, new { op = "show", keys = new[] { "pot|kosiv", "jug|" } }).Ok);
        Patch(h, _ => { });
        Assert.Equal(2, Album(h).GetProperty("show").GetArrayLength());
        Patch(h, s => s["total"] = 2_000_000_000);
        Assert.True(Act(h, "fire").Ok);
        Assert.Equal(2, Album(h).GetProperty("show").GetArrayLength());
        // Збереження з рук: на видноті те, чого в альбомі нема, — і чотири замість трьох.
        Patch(h, s => AlbumNode(s)["show"] = new JsonArray("bowl|", "vase|", "pot|kosiv", "jug|", "makitra|opishnia", "pot|"));
        var show = Album(h).GetProperty("show");
        Assert.Equal(Clicker.ShowMax, show.GetArrayLength());
        Assert.Equal(new[] { "pot|kosiv", "jug|", "makitra|opishnia" }, show.EnumerateArray().Select(x => x.GetProperty("key").GetString()));
    }

    // ---------- трипільський музей ----------

    [Fact]
    public void Digging_clay_finds_trypillian_things_about_one_and_a_half_percent_of_the_time()
    {
        var h = Wheel(seed: 7);
        FormMany(h, 600);
        Assert.Equal(600, Craft(h).GetProperty("formed").GetInt64());
        var a = Album(h);
        var found = PopCount(a.GetProperty("finds").GetInt32()) + a.GetProperty("shards").GetInt32();
        // У середньому дев'ять; зерно стале, тож межі — лише запобіжник від зламаного шансу (нуль чи кожен другий).
        Assert.InRange(found, 2, 25);
        Assert.Equal(JsonValueKind.Object, a.GetProperty("find").ValueKind);
        Assert.True(a.GetProperty("bonus").GetDouble() >= 0.01);
    }

    [Fact]
    public void A_duplicate_find_becomes_a_shard()
    {
        var h = Wheel(seed: 3);
        Patch(h, s => AlbumNode(s)["finds"] = new JsonArray("spiral", "binocular", "figurine", "house", "grain", "krater", "ladle"));
        for (var i = 0; i < 60 && Album(h).GetProperty("shards").GetInt32() == 0; i++) FormMany(h, 20);
        var a = Album(h);
        Assert.True(a.GetProperty("shards").GetInt32() > 0 || PopCount(a.GetProperty("finds").GetInt32()) == 8);
        if (a.GetProperty("shards").GetInt32() > 0 && PopCount(a.GetProperty("finds").GetInt32()) == 7)
            Assert.True(a.GetProperty("find").GetProperty("dup").GetBoolean());
    }

    [Fact]
    public void The_full_museum_gives_thirteen_percent_an_achievement_and_stops_digging()
    {
        var h = Wheel(seed: 11);
        for (var i = 0; i < 200 && PopCount(Album(h).GetProperty("finds").GetInt32()) < 8; i++) FormMany(h, 20);
        var a = Album(h);
        Assert.Equal(255, a.GetProperty("finds").GetInt32());
        Assert.Contains(h.Awards, x => x.Reason == "ach:potter-museum-shards");
        Assert.Equal(0.13, a.GetProperty("bonus").GetDouble(), 9);
        var shards = a.GetProperty("shards").GetInt32();
        var last = Views.Text(a.GetProperty("find"));
        FormMany(h, 400);
        Assert.Equal(shards, Album(h).GetProperty("shards").GetInt32());
        Assert.Equal(last, Views.Text(Album(h).GetProperty("find")));
    }

    [Fact]
    public void Five_shards_glue_into_a_missing_find()
    {
        var h = Wheel();
        Patch(h, s => { var a = AlbumNode(s); a["finds"] = new JsonArray("spiral", "binocular", "figurine", "house", "grain", "krater", "ladle"); a["shards"] = 4; });
        Assert.Equal("Бракує уламків: треба 5, є 4", AlbumAct(h, new { op = "glue" }).Message);
        Patch(h, s => AlbumNode(s)["shards"] = 6);
        var r = AlbumAct(h, new { op = "glue" });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("пряслице", r.Message);
        var al = Album(h);
        Assert.Equal(255, al.GetProperty("finds").GetInt32());
        Assert.Equal(1, al.GetProperty("shards").GetInt32());
        Assert.Equal("whorl", al.GetProperty("find").GetProperty("key").GetString());
        Assert.False(al.GetProperty("find").GetProperty("dup").GetBoolean());
        Assert.Contains(h.Awards, x => x.Reason == "ach:potter-museum-shards");
        Patch(h, s => AlbumNode(s)["shards"] = 9);
        Assert.StartsWith("Музей уже повний", AlbumAct(h, new { op = "glue" }).Message);
    }

    [Fact]
    public void Glue_picks_among_missing_finds_only()
    {
        var h = Wheel(seed: 5);
        Patch(h, s => { var a = AlbumNode(s); a["finds"] = new JsonArray("spiral", "figurine"); a["shards"] = 30; });
        for (var i = 0; i < 6; i++) Assert.True(AlbumAct(h, new { op = "glue" }).Ok);
        Assert.Equal(255, Album(h).GetProperty("finds").GetInt32());
        Assert.Equal(0, Album(h).GetProperty("shards").GetInt32());
    }

    [Fact]
    public void A_find_while_away_is_noted()
    {
        // Підмайстри ліплять без гравця; зерна перебираємо, доки хоч одне не принесе знахідку за простій.
        for (var seed = 1; seed < 400; seed++)
        {
            var h = Wheel(seed);
            Patch(h, s => { s["upgrades"]!["apprentice"] = 25; s["upgrades"]!["workshop"] = 500; });
            h.Clock.Advance(TimeSpan.FromMinutes(30));
            var away = View(h).GetProperty("away");
            if (away.ValueKind != JsonValueKind.Object) continue;
            if (!away.GetProperty("notes").EnumerateArray().Any(n => n.GetString()!.StartsWith("🏺"))) continue;
            Assert.True(PopCount(Album(h).GetProperty("finds").GetInt32()) > 0);
            return;
        }
        Assert.Fail("за 400 зерен жодної знахідки за простій — шанс зламано");
    }

    // ---------- збереження, обпал, каталог ----------

    [Fact]
    public void The_album_survives_a_reload()
    {
        var h = Wheel();
        Items(h, ("tile|gavarets|3", 2));
        Assert.True(AlbumAct(h, new { op = "tile" }).Ok);
        Patch(h, s =>
        {
            var a = AlbumNode(s);
            a["cells"] = new JsonObject { ["dish"] = new JsonArray("", "petrykivka") };
            a["stars"] = new JsonObject { ["dish"] = new JsonArray("petrykivka") };
            a["finds"] = new JsonArray("house");
            a["shards"] = 3;
            a["last"] = new JsonObject { ["key"] = "house", ["at"] = "2026-09-15T10:00:00+00:00", ["dup"] = false };
            a["show"] = new JsonArray("dish|petrykivka");
        });
        var before = Views.Text(Album(h));
        Patch(h, _ => { });
        Assert.Equal(before, Views.Text(Album(h)));
        Assert.Equal(1, Album(h).GetProperty("stove").GetArrayLength());
        Assert.Equal("house", Album(h).GetProperty("find").GetProperty("key").GetString());
        Assert.Equal("dish|petrykivka", Album(h).GetProperty("show")[0].GetProperty("key").GetString());
    }

    [Fact]
    public void An_old_save_without_the_album_starts_it_empty()
    {
        var h = Wheel();
        Patch(h, s => AlbumNode(s)["cells"] = new JsonObject { ["pot"] = new JsonArray("") });
        Patch(h, s => s.Remove("album"));
        Assert.Equal(0, Album(h).GetProperty("open").GetInt32());
        Patch(h, s => s["album"] = null);
        Assert.Equal(0, Album(h).GetProperty("open").GetInt32());
        Patch(h, s => s["album"] = new JsonObject());
        Assert.Equal(0, Album(h).GetProperty("bonus").GetDouble());
        Assert.Equal(0, Album(h).GetProperty("show").GetArrayLength());
        Assert.Equal(0, Album(h).GetProperty("starOpen").GetInt32());
    }

    [Fact]
    public void Firing_the_workshop_keeps_the_album_the_stove_and_the_museum()
    {
        var h = Wheel();
        Items(h, ("tile|kosiv|2", 1));
        Assert.True(AlbumAct(h, new { op = "tile" }).Ok);
        Patch(h, s =>
        {
            s["total"] = 2_000_000_000;
            var a = AlbumNode(s);
            a["cells"] = new JsonObject { ["pot"] = new JsonArray("", "kosiv") };
            a["stars"] = new JsonObject { ["pot"] = new JsonArray("kosiv") };
            a["finds"] = new JsonArray("spiral", "krater");
            a["shards"] = 2;
        });
        var before = Views.Text(Album(h));
        Assert.True(Act(h, "fire").Ok);
        Assert.Equal(before, Views.Text(Album(h)));
        Assert.Equal(0.005 * 2 + 0.01 + 0.015 + 0.02, Album(h).GetProperty("bonus").GetDouble(), 9);
    }

    [Fact]
    public void The_catalog_carries_facts_and_every_number_the_panel_shows()
    {
        var c = View(Wheel()).GetProperty("catalog").GetProperty("album");
        Assert.Equal(10, c.GetProperty("masteryAt").GetArrayLength());
        Assert.Equal(0.005, c.GetProperty("cell").GetDouble(), 9);
        Assert.Equal(0.05, c.GetProperty("row").GetDouble(), 9);
        Assert.Equal(0.06, c.GetProperty("column").GetDouble(), 9);
        Assert.Equal(0.01, c.GetProperty("star").GetDouble(), 9);
        Assert.Equal(0.05, c.GetProperty("starRow").GetDouble(), 9);
        Assert.Equal(0.25, c.GetProperty("starAll").GetDouble(), 9);
        Assert.Equal(0.06, c.GetProperty("masteryWork").GetDouble(), 9);
        Assert.Equal(0.1, c.GetProperty("masteryValue").GetDouble(), 9);
        Assert.Equal(1.25, c.GetProperty("masteryTop").GetDouble(), 9);
        Assert.Equal(Clicker.StoveQualityBonus, c.GetProperty("stoveQuality").EnumerateArray().Select(x => x.GetDouble()));
        Assert.Equal(0.05, c.GetProperty("stoveFull").GetDouble(), 9);
        Assert.Equal(0.1, c.GetProperty("stoveRing").GetDouble(), 9);
        Assert.Equal(3, c.GetProperty("stoveRingQuality").GetInt32());
        Assert.Equal(Clicker.QualityMult.Length - 1, c.GetProperty("topQuality").GetInt32());
        Assert.Equal(3, c.GetProperty("showMax").GetInt32());
        Assert.Equal(8, c.GetProperty("finds").GetArrayLength());
        Assert.All(c.GetProperty("finds").EnumerateArray(), f => Assert.False(string.IsNullOrWhiteSpace(f.GetProperty("desc").GetString())));
        foreach (var w in Clicker.Wares) Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("wares").GetProperty(w.Key).GetString()));
        // Вироби, які додасть пакет «Ремесло», теж уже з фактами — рядків альбому стане п'ятнадцять і всі підписані.
        foreach (var key in new[] { "kukhol", "tykva", "pleskanets" })
            Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("wares").GetProperty(key).GetString()));
        Assert.True(c.GetProperty("styles").TryGetProperty("", out _));
        foreach (var s in Clicker.Styles) Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("styles").GetProperty(s.Key).GetProperty("text").GetString()));
        Assert.Contains("не на кераміці", c.GetProperty("styles").GetProperty("petrykivka").GetProperty("text").GetString());
        Assert.Contains("ЮНЕСКО з 2019", c.GetProperty("styles").GetProperty("kosiv").GetProperty("text").GetString());
    }

    [Fact]
    public void The_album_view_is_a_pure_function_of_state()
    {
        var h = Wheel(seed: 9);
        FormMany(h, 200);
        Assert.Equal(Views.Text(Album(h)), Views.Text(Album(h)));
    }

    [Fact]
    public void Styles_still_buy_and_wear_through_the_old_actions()
    {
        // Вкладка «Розписи» переїхала в альбом лише на клієнті: серверні paint і wear ті самі.
        var h = Wheel();
        Patch(h, s => { s["pots"] = 2_000_000; s["total"] = 2_000_000; });
        var r = Act(h, "paint", new { key = "gavarets" });
        Assert.True(r.Ok, r.Message);
        Assert.Equal("gavarets", View(h).GetProperty("wear").GetString());
        Assert.True(Act(h, "wear", new { key = "" }).Ok);
        Assert.Equal("", View(h).GetProperty("wear").GetString());
        Assert.True(Act(h, "wear", new { key = "gavarets" }).Ok);
        Assert.Equal("Цього розпису ще нема в колекції", Act(h, "wear", new { key = "kosiv" }).Message);
    }
}
