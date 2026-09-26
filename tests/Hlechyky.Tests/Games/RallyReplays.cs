using System.Text.Json;
using System.Text.Json.Serialization;
using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Журнал вводу для паритету C# ↔ JS (spec §5.7): траса (мапа й ворота всередині — щоб браузерові не було
/// звідки більше їх брати), машини, скільки тиків, вводи [тик, місце, маска] і FNV-хеш стану після всіх тиків.
/// Хеші записує C#; <c>docs/games/dev/rally-parity.js</c> рахує їх у браузері тією самою <c>RallySim</c>.
/// </summary>
public sealed class RallyJournal
{
    [JsonPropertyName("track")] public string Track { get; set; } = "";
    [JsonPropertyName("laps")] public int Laps { get; set; }
    [JsonPropertyName("cars")] public string?[] Cars { get; set; } = [];
    [JsonPropertyName("ticks")] public int Ticks { get; set; }
    [JsonPropertyName("inputs")] public List<int[]> Inputs { get; set; } = [];
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("map")] public string[] Map { get; set; } = [];
    [JsonPropertyName("gates")] public int[][][] Gates { get; set; } = [];
    [JsonPropertyName("slots")] public int[][] Slots { get; set; } = [];
    [JsonPropertyName("heading")] public int Heading { get; set; }
}

public static class RallyReplays
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = false };

    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "liquidsoap", "radio.liq"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("корінь репозиторію не знайдено");
    }

    public static string Dir() => Path.Combine(Root(), "tests", "Hlechyky.Tests", "Games", "RallyReplays");

    public static RallyJournal Load(string file) =>
        JsonSerializer.Deserialize<RallyJournal>(File.ReadAllText(file)) ?? throw new InvalidOperationException(file);

    static RallyTrack TrackOf(RallyJournal j) =>
        new(j.Track, j.Track, j.Map, j.Gates, [.. j.Slots.Select(s => (s[0], s[1]))], j.Heading);

    /// <summary>
    /// Прокрутити журнал голим ядром: перед тиком t — ввід із міткою t (як сервер, що отримав його вчасно),
    /// після кожного тика — хеш X, Y, A, VF, VL присутніх машин.
    /// </summary>
    public static string Play(RallyJournal j)
    {
        var core = new RallyCore(TrackOf(j), j.Laps);
        for (var s = 0; s < j.Cars.Length; s++)
            if (j.Cars[s] is { } car) core.Grid(s, car);
        var h = RallyCore.FnvStart;
        var next = 0;
        for (var i = 0; i < j.Ticks; i++)
        {
            while (next < j.Inputs.Count && j.Inputs[next][0] <= core.T + 1)
            {
                var inp = j.Inputs[next++];
                core.Schedule(inp[1], inp[0], inp[2]);
            }
            core.Tick();
            h = core.Hash(h);
        }
        return h.ToString("x8");
    }

    /// <summary>Записати журнал автопілотом: маска кожного місця на кожен тик, у журнал — лише зміни.</summary>
    public static RallyJournal Record(string trackId, int laps, int drivers, int ticks, int seed, int drift)
    {
        var t = RallyTracks.Get(trackId);
        var rng = new Random(seed);
        var cars = new string?[RallyCore.Seats];
        for (var s = 0; s < drivers; s++) cars[s] = Rally.Cars[(s + seed) % Rally.Cars.Length].Id;
        var core = new RallyCore(t, laps);
        for (var s = 0; s < drivers; s++) core.Grid(s, cars[s]!);
        var pilot = new RallyPilot(t, rng) { Drift = drift };
        var j = new RallyJournal
        {
            Track = t.Id, Laps = laps, Cars = cars, Ticks = ticks,
            Map = [.. t.Map], Gates = t.Gates, Slots = [.. t.Slots.Select(s => new[] { s.X, s.Y })], Heading = t.Heading,
        };
        var last = new int[RallyCore.Seats];
        Array.Fill(last, -1);
        for (var i = 0; i < ticks; i++)
        {
            for (var s = 0; s < drivers; s++)
            {
                var m = pilot.Mask(core, s);
                // трохи людської неточності: зрідка відпускає газ чи смикає кермо
                if (core.T > RallyCore.CountTicks && rng.Next(100) < 3) m ^= 1 << rng.Next(3);
                if (m == last[s]) continue;
                last[s] = m;
                j.Inputs.Add([core.T + 1, s, m]);
                core.Schedule(s, core.T + 1, m);
            }
            core.Tick();
        }
        j.Hash = Play(j);
        return j;
    }

    /// <summary>Три журнали зі spec §5.7. Переписуються лише з RALLY_WRITE_REPLAYS=1 (коли свідомо міняли фізику).</summary>
    public static void WriteAll(string dir)
    {
        Directory.CreateDirectory(dir);
        Save(Path.Combine(dir, "1-selo-drift.json"), Record("selo", 3, 1, 1500, seed: 1, drift: 250));
        Save(Path.Combine(dir, "2-yarmarok-six.json"), Record("yarmarok", 3, 6, 2500, seed: 2, drift: 60));
        Save(Path.Combine(dir, "3-ozero-two.json"), Record("ozero", 3, 2, 1500, seed: 3, drift: 120));
    }

    static void Save(string file, RallyJournal j)
    {
        // один рядок на ввід — щоб diff журналу читався
        var json = JsonSerializer.Serialize(j, Pretty);
        File.WriteAllText(file, json.Replace("],[", "],\n[") + "\n");
    }
}
