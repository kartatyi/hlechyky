using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Толока (одинадцяте оновлення, docs/games/specs/clicker-v11.md §2; пакет A — clicker-v11-a.md): відкриття з гривні,
/// закладання етапу (гроші + вироби, найгірша придатна якість, відмови), годинник і офлайн, нагороди будов, велика
/// толока з червоного золотого, друзі на толоці через пошту цеху (−15 % за друга, до трьох; із реліквією не більше
/// −60 %), фестиваль, збереження, обпал і ачівки.
/// </summary>
public class ClickerTolokaTests
{
    const double Hryvnia = 1e15, Gold = 1e27;

    static RoomHarness Wheel(string nick = "Оля")
    {
        var h = new RoomHarness("clicker");
        h.Solo(nick);
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static JsonElement T(RoomHarness h) => View(h).GetProperty("toloka");
    static JsonElement Stage(RoomHarness h) => T(h).GetProperty("stage");
    static double Pots(RoomHarness h) => View(h).GetProperty("pots").GetDouble();
    static ActResult Toloka(RoomHarness h, string what) => h.Act(0, "toloka", new { @do = what });

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static void Give(RoomHarness h, double pots, double? total = null) => Patch(h, s =>
    {
        s["pots"] = pots;
        s["total"] = total ?? pots;
    });

    static void Items(RoomHarness h, params (string Key, int N)[] items) => Patch(h, s =>
    {
        var bag = new JsonObject();
        foreach (var (key, n) in items) bag[key] = n;
        s["craft"]!["items"] = bag;
    });

    static int Item(RoomHarness h, string key)
    {
        foreach (var it in View(h).GetProperty("craft").GetProperty("items").EnumerateArray())
            if (it.GetProperty("key").GetString() == key) return it.GetProperty("n").GetInt32();
        return 0;
    }

    /// <summary>Стан Толоки просто в збереження: готові будови, етап, помічники, піднесене друзями.</summary>
    static void Row(RoomHarness h, string[]? built = null, int stage = 0, DateTimeOffset laidAt = default, string[]? helpers = null,
        int helped = 0) => Patch(h, s =>
    {
        var row = new JsonObject
        {
            ["built"] = new JsonArray((built ?? []).Select(x => (JsonNode)x).ToArray()),
            ["stage"] = stage,
            ["helpers"] = new JsonArray((helpers ?? []).Select(x => (JsonNode)x).ToArray()),
            ["helped"] = helped,
        };
        if (laidAt != default) row["laidAt"] = laidAt.ToString("O");
        s["toloka"] = row;
    });

    static string[] Small => Clicker.Buildings.Where(b => !b.Big).Select(b => b.Key).ToArray();
    static string[] Upto(string key) => Clicker.Buildings.Select(b => b.Key).TakeWhile(k => k != key).Append(key).ToArray();

    /// <summary>Усе на етап криниці: гроші й вироби з запасом.</summary>
    static void ReadyWell(RoomHarness h)
    {
        Give(h, 1e17);
        Items(h, ("pot||1", 15), ("bowl||1", 15));
    }

    // ---------- відкриття ----------

    [Fact]
    public void The_toloka_opens_at_one_hryvnia_for_all_time()
    {
        var h = Wheel();
        Give(h, Hryvnia - 1);
        Assert.Equal(JsonValueKind.Null, T(h).ValueKind);
        var r = Toloka(h, "lay");
        Assert.False(r.Ok);
        Assert.Contains("відкриється", r.Message);

        // Гроші можна й витратити — важить лише «за весь час».
        Give(h, 0, Hryvnia);
        var st = Stage(h);
        Assert.Equal("well", st.GetProperty("building").GetString());
        Assert.Equal(0, st.GetProperty("index").GetInt32());
        Assert.Equal(JsonValueKind.Null, st.GetProperty("endsAt").ValueKind);
        Assert.Equal(2, st.GetProperty("needs").GetArrayLength());
        Assert.Empty(T(h).GetProperty("built").EnumerateArray());
        Assert.False(T(h).GetProperty("waitBig").GetBoolean());
    }

    [Fact]
    public void The_catalog_carries_the_buildings_texts()
    {
        var h = Wheel();
        var c = View(h).GetProperty("catalog").GetProperty("toloka");
        Assert.Equal(12, c.GetProperty("buildings").GetArrayLength());
        var well = c.GetProperty("buildings")[0];
        Assert.Equal("well", well.GetProperty("key").GetString());
        Assert.Equal(3, well.GetProperty("stages").GetArrayLength());
        Assert.Equal("Дубовий зруб", well.GetProperty("stages")[1].GetProperty("name").GetString());
    }

    // ---------- закладання ----------

    [Fact]
    public void Laying_takes_the_money_and_the_worst_fitting_wares()
    {
        var h = Wheel();
        Give(h, 3e15);
        // Горщиків просять звичайних: ідуть спершу звичайні, потім добрі. Мисок — теж, дзвінкі лишаються.
        Items(h, ("pot||1", 10), ("pot||2", 10), ("bowl||3", 10), ("bowl||1", 5), ("bowl|kosiv|1", 5), ("jug||1", 3));
        var r = Toloka(h, "lay");
        Assert.True(r.Ok, r.Message);
        Assert.Contains("Копаємо яму", r.Message);
        Assert.InRange(Pots(h), 2e15 - 1e3, 2e15 + 1e9);
        Assert.Equal(0, Item(h, "pot||1"));
        Assert.Equal(5, Item(h, "pot||2"));
        Assert.Equal(0, Item(h, "bowl||1"));
        Assert.Equal(0, Item(h, "bowl|kosiv|1"));
        Assert.Equal(5, Item(h, "bowl||3"));
        Assert.Equal(3, Item(h, "jug||1"));
        var st = Stage(h);
        var laid = st.GetProperty("laidAt").GetDateTimeOffset();
        Assert.Equal(h.Clock.UtcNow, laid);
        Assert.Equal(laid.AddHours(1), st.GetProperty("endsAt").GetDateTimeOffset());
    }

    [Fact]
    public void Laying_refuses_without_money_wares_or_twice()
    {
        var h = Wheel();
        Give(h, 5e14, Hryvnia);
        Items(h, ("pot||1", 15), ("bowl||1", 15));
        var r = Toloka(h, "lay");
        Assert.False(r.Ok);
        Assert.StartsWith("Бракує глеків", r.Message);

        Give(h, 1e17);
        Items(h, ("pot||1", 10), ("bowl||1", 15));
        r = Toloka(h, "lay");
        Assert.False(r.Ok);
        Assert.StartsWith("Бракує виробів", r.Message);
        Assert.Contains("ще 5", r.Message);
        // Відмова нічого не забрала.
        Assert.Equal(10, Item(h, "pot||1"));
        Assert.InRange(Pots(h), 1e17 - 1e3, 1e17 + 1e12);

        // Кращої якості, ніж просять, — теж годиться; гіршої — ні (добрі макітри для зрубу).
        Row(h, stage: 1);
        Items(h, ("jug||1", 20), ("makitra||1", 10));
        r = Toloka(h, "lay");
        Assert.False(r.Ok);
        Assert.Contains("макітра", r.Message);

        Items(h, ("jug||1", 20), ("makitra||2", 10));
        Assert.True(Toloka(h, "lay").Ok);
        r = Toloka(h, "lay");
        Assert.False(r.Ok);
        Assert.Contains("уже будується", r.Message);
        Assert.False(Toloka(h, "dance").Ok);
    }

    // ---------- годинник ----------

    [Fact]
    public void The_stage_is_ready_after_its_hours_and_the_building_after_the_last_stage()
    {
        var h = Wheel();
        ReadyWell(h);
        Assert.True(Toloka(h, "lay").Ok);
        h.Clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(0, Stage(h).GetProperty("index").GetInt32());
        Assert.Equal(0, T(h).GetProperty("done").GetInt32());
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        var st = Stage(h);
        Assert.Equal(1, st.GetProperty("index").GetInt32());
        Assert.Equal(JsonValueKind.Null, st.GetProperty("endsAt").ValueKind);
        Assert.Equal(1, T(h).GetProperty("done").GetInt32());

        // Наступний етап — лише рукою: годинник сам не йде.
        h.Clock.Advance(TimeSpan.FromHours(10));
        Assert.Equal(1, Stage(h).GetProperty("index").GetInt32());

        Items(h, ("jug||1", 20), ("makitra||2", 10));
        Assert.True(Toloka(h, "lay").Ok);
        h.Clock.Advance(TimeSpan.FromHours(2));
        Items(h, ("barrel||2", 10), ("jug||2", 20));
        Assert.True(Toloka(h, "lay").Ok);
        h.Clock.Advance(TimeSpan.FromHours(3));
        var t = T(h);
        Assert.Equal(["well"], t.GetProperty("built").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal("mill", t.GetProperty("stage").GetProperty("building").GetString());
        Assert.Equal(0, t.GetProperty("stage").GetProperty("index").GetInt32());
        Assert.Equal(3, t.GetProperty("done").GetInt32());
        Assert.Equal(1.05, t.GetProperty("allMult").GetDouble(), 9);
        // Ачівка — з першою ж дією (вид її лише ставить у чергу).
        Assert.True(h.Act(0, "look").Ok);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-toloka-first");
    }

    [Fact]
    public void The_clock_runs_offline_and_survives_save_and_load()
    {
        var h = Wheel();
        ReadyWell(h);
        Assert.True(Toloka(h, "lay").Ok);
        var save = h.Room.Game.Save()!;

        // Інша кімната (перезапуск сервера) через добу: етап уже готовий, хоч гончаря й не було.
        var k = Wheel("Оля");
        k.Clock.UtcNow = h.Clock.UtcNow.AddDays(1);
        lock (k.Room.Sync) k.Room.Game.Load(save);
        var st = k.View(0).GetProperty("toloka").GetProperty("stage");
        Assert.Equal(1, st.GetProperty("index").GetInt32());
        Assert.Equal(1, k.View(0).GetProperty("toloka").GetProperty("done").GetInt32());

        // А за пів години — ще будується, з тим самим кінцем.
        var m = Wheel("Оля");
        m.Clock.UtcNow = h.Clock.UtcNow.AddMinutes(30);
        lock (m.Room.Sync) m.Room.Game.Load(save);
        var ms = m.View(0).GetProperty("toloka").GetProperty("stage");
        Assert.Equal(0, ms.GetProperty("index").GetInt32());
        Assert.Equal(h.Clock.UtcNow.AddHours(1), ms.GetProperty("endsAt").GetDateTimeOffset());
    }

    [Fact]
    public void Save_and_load_keep_everything_and_an_old_save_has_no_toloka()
    {
        var h = Wheel();
        Give(h, 1e20);
        Row(h, built: ["well"], stage: 2, laidAt: h.Clock.UtcNow.AddMinutes(-5), helpers: ["Микола", "Назар"], helped: 7);
        var before = T(h).GetRawText();
        var save = h.Room.Game.Save()!;
        var k = Wheel("Оля");
        lock (k.Room.Sync) k.Room.Game.Load(save);
        Assert.Equal(before, k.View(0).GetProperty("toloka").GetRawText());
        Assert.Equal(7, T(h).GetProperty("helped").GetInt32());

        // Збереження з v10 — без рядка толоки: з гривнею — криниця з нуля, без — нічого.
        var old = JsonNode.Parse(save)!.AsObject();
        old.Remove("toloka");
        old["relics"] = null;
        lock (k.Room.Sync) k.Room.Game.Load(old.ToJsonString());
        var t = k.View(0).GetProperty("toloka");
        Assert.Empty(t.GetProperty("built").EnumerateArray());
        Assert.Equal("well", t.GetProperty("stage").GetProperty("building").GetString());
        Assert.Equal(0, t.GetProperty("done").GetInt32());
        // Порожня толока в збереження не пишеться зовсім.
        Assert.Null(JsonNode.Parse(Wheel("Нова").Room.Game.Save()!)!["toloka"]);
    }

    // ---------- нагороди ----------

    [Fact]
    public void Rewards_work_forge_store_and_kiln_pier_guest_slot_and_the_jingdezhen_rung()
    {
        var h = Wheel();
        Give(h, 1e30);
        var store = View(h).GetProperty("craft").GetProperty("storeCap").GetInt32();
        Row(h, built: Upto("mill"));
        Assert.Equal(store, View(h).GetProperty("craft").GetProperty("storeCap").GetInt32());
        Row(h, built: Upto("forge"));
        Assert.Equal(store + Clicker.ForgeStore, View(h).GetProperty("craft").GetProperty("storeCap").GetInt32());

        // Цзиндечжень зачинений, доки нема Пристані.
        Row(h, built: Small);
        var r = h.Act(0, "buy", new { key = "jingdezhen" });
        Assert.False(r.Ok);
        Assert.Contains("Пристань на Ворсклі", r.Message);
        Row(h, built: Upto("pier"));
        r = h.Act(0, "buy", new { key = "jingdezhen" });
        Assert.DoesNotContain("спершу збудуй", r.Message ?? "");

        // Гостинний двір: один гість — одне місце, із Пристанню — два.
        Patch(h, s =>
        {
            s["guests"] = new JsonObject
            {
                ["met"] = new JsonObject { [Clicker.Guests[0].Key] = h.Clock.UtcNow.AddHours(-1).ToString("O") },
                ["next"] = h.Clock.UtcNow.AddHours(1).ToString("O"),
            };
            s["toloka"] = null;
        });
        Assert.Equal(1, View(h).GetProperty("guests").GetProperty("slots").GetInt32());
        Row(h, built: Upto("pier"));
        Assert.Equal(2, View(h).GetProperty("guests").GetProperty("slots").GetInt32());
        // Кожна будова — +5 % до всього.
        Assert.Equal(1 + 0.05 * 6, T(h).GetProperty("allMult").GetDouble(), 9);
    }

    [Fact]
    public void The_big_toloka_waits_for_the_red_gold()
    {
        var h = Wheel();
        Give(h, 1e26);
        Row(h, built: Small);
        var t = T(h);
        Assert.Equal(JsonValueKind.Null, t.GetProperty("stage").ValueKind);
        Assert.True(t.GetProperty("waitBig").GetBoolean());
        var r = Toloka(h, "lay");
        Assert.False(r.Ok);
        Assert.Contains("червоний золотий", r.Message);

        Give(h, Gold);
        t = T(h);
        Assert.False(t.GetProperty("waitBig").GetBoolean());
        Assert.Equal("pier", t.GetProperty("stage").GetProperty("building").GetString());
        Assert.Equal(2, t.GetProperty("stage").GetProperty("needs").GetArrayLength());
    }

    // ---------- скорочення ----------

    [Fact]
    public void Friends_cut_fifteen_percent_each_up_to_three_and_with_the_relic_never_past_sixty()
    {
        var h = Wheel();
        Give(h, 1e20);
        var laid = h.Clock.UtcNow;
        double Hours() => (Stage(h).GetProperty("endsAt").GetDateTimeOffset() - laid).TotalHours;
        double Cut() => Stage(h).GetProperty("cut").GetDouble();

        Row(h, built: ["well"], stage: 2, laidAt: laid);              // вітряк, «Жорна»: 4 год
        Assert.Equal(0, Cut());
        Assert.Equal(4, Hours(), 6);
        Row(h, built: ["well"], stage: 2, laidAt: laid, helpers: ["А"]);
        Assert.Equal(0.15, Cut(), 9);
        Assert.Equal(3.4, Hours(), 6);
        Row(h, built: ["well"], stage: 2, laidAt: laid, helpers: ["А", "Б", "В", "Г", "Ґ"]);
        Assert.Equal(0.45, Cut(), 9);
        Assert.Equal(2.2, Hours(), 6);

        // Родова толока: 3 % за рівень, стеля −30 %; разом із друзями — не більше −60 %.
        Patch(h, s => s["relics"] = new JsonObject { ["toloka"] = 5 });
        Assert.Equal(0.60, Cut(), 9);
        Row(h, built: ["well"], stage: 2, laidAt: laid, helpers: ["А"]);
        Assert.Equal(0.30, Cut(), 9);
        Patch(h, s => s["relics"] = new JsonObject { ["toloka"] = 60 });
        Assert.Equal(0.45, Cut(), 9);
        Row(h, built: ["well"], stage: 2, laidAt: laid, helpers: ["А", "Б", "В"]);
        Assert.Equal(Clicker.TolokaCutMax, Cut(), 9);
        Assert.Equal(4 * 0.4, Hours(), 6);
    }

    // ---------- фестиваль ----------

    [Fact]
    public void The_festival_doubles_everything_for_an_hour_once_a_day()
    {
        var h = Wheel();
        Give(h, 1e35);
        Row(h, built: Upto("hall"));
        var r = Toloka(h, "festival");
        Assert.False(r.Ok);
        Assert.Contains("Гончарний фестиваль", r.Message);
        Assert.Equal(JsonValueKind.Null, T(h).GetProperty("festival").ValueKind);

        Row(h, built: Upto("festival"));
        var all = View(h).GetProperty("allMult").GetDouble();
        Assert.True(Toloka(h, "festival").Ok);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-festival");
        Assert.Equal(all * Clicker.FestivalMult, View(h).GetProperty("allMult").GetDouble(), all * 1e-9);
        Assert.Equal(h.Clock.UtcNow.AddHours(1), T(h).GetProperty("festival").GetProperty("until").GetDateTimeOffset());
        r = Toloka(h, "festival");
        Assert.False(r.Ok);
        Assert.Contains("уже гуляє", r.Message);

        h.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(all, View(h).GetProperty("allMult").GetDouble(), all * 1e-9);
        r = Toloka(h, "festival");
        Assert.False(r.Ok);
        Assert.Contains("Наступний фестиваль", r.Message);
        h.Clock.Advance(TimeSpan.FromHours(23));
        Assert.True(Toloka(h, "festival").Ok);
    }

    // ---------- обпал і ачівки ----------

    [Fact]
    public void Firing_does_not_touch_the_toloka()
    {
        var h = Wheel();
        Give(h, 1e20);
        Row(h, built: ["well", "mill"], stage: 1, laidAt: h.Clock.UtcNow.AddMinutes(-10), helpers: ["Микола"], helped: 3);
        var before = T(h).GetRawText();
        var r = h.Act(0, "fire");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(before, T(h).GetRawText());
    }

    [Fact]
    public void The_whole_small_toloka_and_all_twelve_give_their_achievements()
    {
        var h = Wheel();
        Give(h, 1e40);
        var last = Clicker.Buildings.Single(b => b.Key == "school");
        Row(h, built: Small.Take(4).ToArray(), stage: last.Stages.Length - 1, laidAt: h.Clock.UtcNow);
        h.Clock.Advance(TimeSpan.FromHours(last.Stages[^1].Hours));
        Assert.True(h.Act(0, "look").Ok);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-toloka-small");
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:potter-toloka-all");

        var jug = Clicker.Buildings[^1];
        Row(h, built: Clicker.Buildings.SkipLast(1).Select(b => b.Key).ToArray(), stage: jug.Stages.Length - 1, laidAt: h.Clock.UtcNow);
        h.Clock.Advance(TimeSpan.FromHours(jug.Stages[^1].Hours));
        Assert.True(h.Act(0, "look").Ok);
        Assert.Contains(h.Awards, a => a.Reason == "ach:potter-toloka-all");
        var t = T(h);
        Assert.Equal(12, t.GetProperty("built").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, t.GetProperty("stage").ValueKind);
        Assert.False(t.GetProperty("waitBig").GetBoolean());
    }

    // ---------- друзі на толоці (пошта цеху) ----------

    sealed class Tsekh
    {
        public FakeStore Store { get; } = new();
        public FakeClock Clock { get; } = new();
        public ClickerGuildService Svc { get; }
        readonly List<RoomHarness> _rooms = [];
        public Tsekh() => Svc = new ClickerGuildService(Store, Clock);

        public RoomHarness Potter(string nick)
        {
            var h = new RoomHarness("clicker", services: RoomHarness.WithService(Svc));
            h.Solo(nick);
            Assert.True(h.Act(0, "look").Ok);
            _rooms.Add(h);
            Publish(h, nick);
            return h;
        }

        /// <summary>Каркас пише збереження після кожної дії — тут це робимо руками.</summary>
        public void Publish(RoomHarness h, string nick)
        {
            lock (h.Room.Sync) Store.SaveState("clicker:" + ClickerGuildService.Key(nick), h.Room.Game.Save()!);
        }

        /// <summary>Усі годинники разом: кімнати й цех живуть однаковим часом.</summary>
        public void Advance(TimeSpan by)
        {
            Clock.Advance(by);
            foreach (var r in _rooms) r.Clock.Advance(by);
        }
    }

    static ActResult Bring(RoomHarness h, string to, string key, int n) => h.Act(0, "guild", new { op = "toloka", to, key, n });

    [Fact]
    public void A_friend_brings_wares_offline_and_they_count_when_the_owner_comes()
    {
        var g = new Tsekh();
        var owner = g.Potter("Оля");
        var friend = g.Potter("Микола");
        Give(owner, 2e15);
        g.Publish(owner, "Оля");
        Give(friend, 2e15);
        Items(friend, ("pot||1", 30), ("pot||2", 4));
        var pots = Pots(friend);

        var r = Bring(friend, "Оля", "pot||1", 10);
        Assert.True(r.Ok, r.Message);
        Assert.Contains("Копаємо яму", r.Message);
        Assert.Equal(20, Item(friend, "pot||1"));
        Assert.True(Pots(friend) > pots, "помічникові — гостинець толоки");
        Assert.Equal(10, T(friend).GetProperty("helped").GetInt32());

        // Друга посилка — лише те, що ще бракує (перша ще в дорозі, і цех її рахує).
        r = Bring(friend, "Оля", "pot||1", 10);
        Assert.True(r.Ok, r.Message);
        Assert.StartsWith("🤝 5 ×", r.Message);
        Assert.Equal(15, Item(friend, "pot||1"));
        r = Bring(friend, "Оля", "pot||2", 1);
        Assert.False(r.Ok);
        Assert.Contains("вже досить", r.Message);

        // Хата показує, скільки ще бракує, з посилками в дорозі.
        var house = Views.Json(g.Svc.House("Оля"));
        var need = house.GetProperty("toloka").GetProperty("needs")[0];
        Assert.Equal("pot", need.GetProperty("ware").GetString());
        Assert.Equal(0, need.GetProperty("left").GetInt32());
        Assert.Equal(15, house.GetProperty("toloka").GetProperty("needs")[1].GetProperty("left").GetInt32());
        Assert.Contains("Микола", house.GetProperty("toloka").GetProperty("helpers").EnumerateArray().Select(x => x.GetString()));

        // Господар лише дивиться — пошта чекає; перша дія — і вироби в рядку вимог, а Микола на толоці.
        Assert.Equal(0, Stage(owner).GetProperty("needs")[0].GetProperty("got").GetInt32());
        Assert.True(owner.Act(0, "look").Ok);
        var st = Stage(owner);
        Assert.Equal(15, st.GetProperty("needs")[0].GetProperty("got").GetInt32());
        Assert.Equal(["Микола"], st.GetProperty("helpers").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(0.15, st.GetProperty("cut").GetDouble(), 9);
        Assert.Null(g.Svc.TakeToloka("оля"));

        // Закладаючи, свої горщики вже не потрібні — лише миски.
        Items(owner, ("bowl||1", 15));
        Assert.True(Toloka(owner, "lay").Ok);
        Assert.Equal(owner.Clock.UtcNow.AddHours(0.85), Stage(owner).GetProperty("endsAt").GetDateTimeOffset());
    }

    [Fact]
    public void Wrong_wares_self_and_no_building_are_refused_before_sending()
    {
        var g = new Tsekh();
        var owner = g.Potter("Оля");
        var friend = g.Potter("Микола");
        Items(friend, ("pot||1", 10), ("jug||1", 10), ("bowl||1", 10));

        // У Олі ще нема гривні — будови нема.
        var r = Bring(friend, "Оля", "pot||1", 1);
        Assert.False(r.Ok);
        Assert.Contains("нема будови", r.Message);

        Give(owner, 2e15);
        g.Publish(owner, "Оля");
        r = Bring(friend, "Оля", "jug||1", 1);
        Assert.False(r.Ok);
        Assert.Contains("не йде", r.Message);
        Assert.Contains("горщик", r.Message);
        Assert.Equal(10, Item(friend, "jug||1"));

        Give(friend, 2e15);
        g.Publish(friend, "Микола");
        r = Bring(friend, "Микола", "pot||1", 1);
        Assert.False(r.Ok);
        r = Bring(friend, "Нікого", "pot||1", 1);
        Assert.False(r.Ok);
        Assert.Contains("не сідав", r.Message);
        r = Bring(friend, "Оля", "lion||4", 1);
        Assert.False(r.Ok);
        Assert.Equal(10, Item(friend, "pot||1"));
        Assert.Null(g.Svc.TakeToloka("оля"));
    }

    [Fact]
    public void While_a_stage_is_building_each_friend_helps_once_and_the_wares_go_to_the_store()
    {
        var g = new Tsekh();
        var owner = g.Potter("Оля");
        var a = g.Potter("Микола");
        var b = g.Potter("Назар");
        ReadyWell(owner);
        Assert.True(Toloka(owner, "lay").Ok);
        g.Publish(owner, "Оля");
        Items(a, ("pot||1", 10));
        Items(b, ("bowl||2", 3));

        Assert.True(Bring(a, "Оля", "pot||1", 4).Ok);
        var r = Bring(a, "Оля", "pot||1", 1);
        Assert.False(r.Ok);
        Assert.Contains("вже був", r.Message);
        Assert.True(Bring(b, "Оля", "bowl||2", 3).Ok);

        Assert.True(owner.Act(0, "look").Ok);
        var st = Stage(owner);
        Assert.Equal(2, st.GetProperty("helpers").GetArrayLength());
        Assert.Equal(0.30, st.GetProperty("cut").GetDouble(), 9);
        Assert.Equal(owner.Clock.UtcNow.AddHours(0.7), st.GetProperty("endsAt").GetDateTimeOffset());
        Assert.Equal(4, Item(owner, "pot||1"));
        Assert.Equal(3, Item(owner, "bowl||2"));
    }

    [Fact]
    public void A_stage_that_finished_offline_takes_help_for_the_next_one()
    {
        var g = new Tsekh();
        var owner = g.Potter("Оля");
        var friend = g.Potter("Микола");
        ReadyWell(owner);
        Assert.True(Toloka(owner, "lay").Ok);
        g.Publish(owner, "Оля");
        g.Advance(TimeSpan.FromHours(2));

        // Збереження ще каже «копаємо яму будується», але цех бачить, що вона вже готова, — несемо на зруб.
        Items(friend, ("jug||1", 8), ("pot||1", 5));
        var r = Bring(friend, "Оля", "pot||1", 1);
        Assert.False(r.Ok);
        Assert.Contains("Дубовий зруб", r.Message);
        r = Bring(friend, "Оля", "jug||1", 8);
        Assert.True(r.Ok, r.Message);
        Assert.Contains("Дубовий зруб", r.Message);

        Assert.True(owner.Act(0, "look").Ok);
        var st = Stage(owner);
        Assert.Equal(1, st.GetProperty("index").GetInt32());
        Assert.Equal(8, st.GetProperty("needs")[0].GetProperty("got").GetInt32());
        Assert.Equal(["Микола"], st.GetProperty("helpers").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(0, Item(owner, "jug||1"));
    }

    [Fact]
    public void Three_friends_cut_the_stage_and_the_hundredth_ware_gives_the_helper_achievement()
    {
        var g = new Tsekh();
        var owner = g.Potter("Оля");
        Give(owner, 1e20);
        Row(owner, built: ["well"]);                                  // вітряк, «Кам'яний підмурок»: макітри й тарілки
        g.Publish(owner, "Оля");
        var nicks = new[] { "Микола", "Назар", "Влад", "Smaug" };
        foreach (var nick in nicks)
        {
            var f = g.Potter(nick);
            Items(f, ("dish||1", 3));
            if (nick == "Smaug") Row(f, helped: Clicker.TolokaHelpForAchievement - 2);
            Assert.True(Bring(f, "Оля", "dish||1", 3).Ok);
            if (nick == "Smaug") Assert.Contains(f.Awards, a => a.Reason == "ach:potter-toloka-helper");
            else Assert.DoesNotContain(f.Awards, a => a.Reason == "ach:potter-toloka-helper");
        }
        Assert.True(owner.Act(0, "look").Ok);
        var st = Stage(owner);
        Assert.Equal(4, st.GetProperty("helpers").GetArrayLength());
        Assert.Equal(0.45, st.GetProperty("cut").GetDouble(), 9);
        Assert.Equal(12, st.GetProperty("needs")[1].GetProperty("got").GetInt32());
    }

    [Fact]
    public void Toloka_mail_waits_in_the_guild_state_across_a_restart()
    {
        var g = new Tsekh();
        var owner = g.Potter("Оля");
        var friend = g.Potter("Микола");
        Give(owner, 2e15);
        g.Publish(owner, "Оля");
        Items(friend, ("bowl||1", 6));
        Assert.True(Bring(friend, "Оля", "bowl||1", 6).Ok);

        // Новий сервіс на тому самому сховищі (перезапуск) — посилка лежить.
        var again = new ClickerGuildService(g.Store, g.Clock);
        var box = again.TakeToloka("оля");
        Assert.NotNull(box);
        var p = Assert.Single(box!);
        Assert.Equal(("Микола", "well", 0, "bowl", 6), (p.From, p.Building, p.Stage, p.Ware, p.N));
    }
}
