using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Сцена одинадцятого оновлення (docs/games/specs/clicker-v11.md §6, пакет B): майдан Толоки за хатою малює
/// web/games/clicker-scene.js з виду <c>toloka</c>. Шов між сервером і малюнком — ключі будов, кількість етапів
/// (кожен етап — свій шар будови), порядок гончарів світу й поля виду, які читає сцена. Ці тести тримають шов:
/// додали будову чи етап на сервері — сцена мусить знати, як його малювати.
/// </summary>
public class ClickerSceneTolokaTests
{
    static readonly Lazy<string> Js = new(() => File.ReadAllText(Paths.Resolve("web/games/clicker-scene.js")));

    /// <summary>Тіло об'єкта-таблиці <c>const NAME = { … };</c> зі сцени (до першого «};» на початку рядка з двома пробілами).</summary>
    static string Table(string name)
    {
        var js = Js.Value;
        var at = js.IndexOf($"const {name} = {{", StringComparison.Ordinal);
        Assert.True(at >= 0, $"у clicker-scene.js нема {name}");
        var end = js.IndexOf("\n  };", at, StringComparison.Ordinal);
        Assert.True(end > at);
        return js[at..end];
    }

    /// <summary>
    /// Скільки шарів у будови в таблиці TB: кожен шар починається рядком із рівно шістьма пробілами й лапкою чи «[»
    /// (продовження шару — з вісьмома й «+»). Так записано всю таблицю — див. коментар над нею.
    /// </summary>
    static Dictionary<string, int> Layers()
    {
        var tb = Table("TB").Replace("\r", "");
        var res = new Dictionary<string, int>(StringComparer.Ordinal);
        string? cur = null;
        foreach (var line in tb.Split('\n'))
        {
            var head = Regex.Match(line, @"^    (\w+): \{ w: [\d.]+, h: [\d.]+, layers: \[$");
            if (head.Success) { cur = head.Groups[1].Value; res[cur] = 0; continue; }
            if (cur is null) continue;
            if (line.StartsWith("    ] },", StringComparison.Ordinal)) { cur = null; continue; }
            if (Regex.IsMatch(line, @"^      ['\[]")) res[cur]++;
        }
        return res;
    }

    static List<string> Keys(string table) =>
        Regex.Matches(Table(table), @"^\s{4}(\w+): \{", RegexOptions.Multiline).Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(Table(table), @"[{,] (\w+): \{ x:", RegexOptions.Multiline).Select(m => m.Groups[1].Value))
            .Distinct().ToList();

    [Fact]
    public void Scene_draws_every_building_with_a_layer_per_stage()
    {
        var layers = Layers();
        Assert.Equal(Clicker.Buildings.Select(b => b.Key).Order(), layers.Keys.Order());
        foreach (var b in Clicker.Buildings)
            Assert.True(b.Stages.Length == layers[b.Key], $"{b.Key}: етапів {b.Stages.Length}, шарів на сцені {layers[b.Key]}");
    }

    [Fact]
    public void Scene_has_a_place_for_every_building()
    {
        var slots = Keys("TSLOT");
        Assert.Equal(Clicker.Buildings.Select(b => b.Key).Order(), slots.Order());
    }

    [Fact]
    public void Scene_knows_the_six_world_potters_in_server_order()
    {
        var m = Regex.Match(Js.Value, @"const MASTERS = \[([^\]]+)\];");
        Assert.True(m.Success);
        var list = Regex.Matches(m.Groups[1].Value, "'(\\w+)'").Select(x => x.Groups[1].Value).ToArray();
        Assert.Equal(Clicker.MasterTiers, list);
        var art = Table("MASTER_ART");
        foreach (var k in Clicker.MasterTiers) Assert.Matches(new Regex(@"^\s{4}" + k + ": '", RegexOptions.Multiline), art);
    }

    [Fact]
    public void Scene_hook_for_the_toloka_package_is_published()
    {
        // Пакет «Толока» кличе api.sceneToloka?.(building, stage), коли етап готовий.
        Assert.Contains("api.sceneToloka = (a, b) =>", Js.Value);
    }

    // ---------- поля виду, які читає сцена ----------

    static RoomHarness Wheel()
    {
        var h = new RoomHarness("clicker", seed: 3);
        h.Solo("Оля");
        return h;
    }

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static JsonElement Toloka(RoomHarness h) => h.View(0).GetProperty("toloka");

    [Fact]
    public void Before_the_first_hryvnia_the_view_has_no_toloka_so_the_house_stays_as_it_was()
    {
        var h = Wheel();
        Assert.Equal(JsonValueKind.Null, Toloka(h).ValueKind);
    }

    [Fact]
    public void A_laid_stage_gives_the_scene_its_building_index_clock_and_helpers()
    {
        var h = Wheel();
        var now = h.Clock.UtcNow;
        Patch(h, s =>
        {
            s["pots"] = 1e16;
            s["total"] = 1e16;
            s["toloka"] = new JsonObject
            {
                ["built"] = new JsonArray("well"),
                ["stage"] = 1,
                ["laidAt"] = now.AddMinutes(-5).ToString("O"),
                ["helpers"] = new JsonArray("Назар", "Микола"),
            };
        });
        var t = Toloka(h);
        Assert.Equal(["well"], t.GetProperty("built").EnumerateArray().Select(x => x.GetString()!).ToArray());
        var st = t.GetProperty("stage");
        Assert.Equal("mill", st.GetProperty("building").GetString());
        Assert.Equal(1, st.GetProperty("index").GetInt32());
        Assert.Equal(JsonValueKind.String, st.GetProperty("laidAt").ValueKind);
        Assert.Equal(["Назар", "Микола"], st.GetProperty("helpers").EnumerateArray().Select(x => x.GetString()!).ToArray());
    }

    [Fact]
    public void A_stage_not_yet_laid_has_no_clock_so_the_scene_shows_stakes_not_scaffolding()
    {
        var h = Wheel();
        Patch(h, s =>
        {
            s["total"] = 1e15;
            s["toloka"] = new JsonObject { ["built"] = new JsonArray(), ["stage"] = 0 };
        });
        var st = Toloka(h).GetProperty("stage");
        Assert.Equal("well", st.GetProperty("building").GetString());
        Assert.Equal(0, st.GetProperty("index").GetInt32());
        Assert.Equal(JsonValueKind.Null, st.GetProperty("laidAt").ValueKind);
    }

    [Fact]
    public void All_twelve_built_and_a_festival_on_the_scene_reads_until_from_the_view()
    {
        var h = Wheel();
        var now = h.Clock.UtcNow;
        Patch(h, s =>
        {
            s["total"] = 1e30;
            var built = new JsonArray();
            foreach (var b in Clicker.Buildings) built.Add(b.Key);
            s["toloka"] = new JsonObject
            {
                ["built"] = built,
                ["festivalUntil"] = now.AddMinutes(30).ToString("O"),
                ["festivalNext"] = now.AddHours(23).ToString("O"),
            };
        });
        var t = Toloka(h);
        Assert.Equal(Clicker.Buildings.Length, t.GetProperty("built").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, t.GetProperty("stage").ValueKind);
        var until = t.GetProperty("festival").GetProperty("until").GetDateTimeOffset();
        Assert.True(until > now);
    }
}
