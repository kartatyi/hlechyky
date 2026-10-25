using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Мапа «Чумаків-далекобійників» (<c>data/fura/maps/&lt;id&gt;.json</c>, специфікація — docs/games/specs/fura.md, «Мапа»):
/// граф доріг, місця з дворами й боксами, вантажі. Одна мапа на обидва боки: браузер будує з неї світ, сервер — граф
/// для кілометрів рейсу й зони для судді. Читається з диска один раз на процес і далі живе в кеші; сирий текст файлу
/// лежить поруч — його як є віддає <c>GET /api/games/fura/map/{id}</c>.
/// </summary>
public sealed class FuraMap
{
    /// <summary>Тека мап відносно кореня репо.</summary>
    public const string Dir = "data/fura/maps";

    /// <summary>Мапа, на якій їздять зараз (етап 1 — одна).</summary>
    public const string DefaultId = "kray";

    /// <summary>Які мапи є взагалі. Шлях будується лише з цього списку — з клієнта в нього не потрапить нічого.</summary>
    public static readonly IReadOnlyList<string> Known = [DefaultId];

    static readonly ConcurrentDictionary<string, Lazy<FuraMap>> Cache = new(StringComparer.Ordinal);

    /// <summary>Мапа з кешу (перший раз — з диска). Невідомий id чи биті дані — виняток: без мапи гри нема.</summary>
    public static FuraMap Get(string id = DefaultId)
    {
        if (!Known.Contains(id)) throw new ArgumentException($"нема мапи «{id}»", nameof(id));
        return Cache.GetOrAdd(id, k => new Lazy<FuraMap>(() => Parse(k, File.ReadAllText(Paths.Resolve($"{Dir}/{k}.json"))))).Value;
    }

    /// <summary>Сирий JSON мапи для HTTP; null — такої мапи нема або файл не читається.</summary>
    public static string? Raw(string id)
    {
        if (!Known.Contains(id)) return null;
        try { return Get(id).Json; }
        catch (Exception) { return null; }
    }

    // ---------- що в мапі ----------

    public string Id { get; }
    public string Title { get; }
    /// <summary>Файл як є — для віддачі браузеру.</summary>
    public string Json { get; }
    public IReadOnlyDictionary<string, (double X, double Z)> Nodes { get; }
    public IReadOnlyList<FuraRoad> Roads { get; }
    public IReadOnlyList<FuraPlace> Places { get; }
    public IReadOnlyList<FuraCargo> Cargo { get; }

    readonly Dictionary<string, FuraPlace> _places;
    readonly Dictionary<string, FuraCargo> _cargo;
    /// <summary>Найкоротший шлях графом між вузлами місць, метри: [від][до].</summary>
    readonly Dictionary<string, Dictionary<string, double>> _between;

    FuraMap(string id, string title, string json, Dictionary<string, (double, double)> nodes, List<FuraRoad> roads,
        List<FuraPlace> places, List<FuraCargo> cargo)
    {
        Id = id;
        Title = title;
        Json = json;
        Nodes = nodes;
        Roads = roads;
        Places = places;
        Cargo = cargo;
        _places = places.ToDictionary(p => p.Id, StringComparer.Ordinal);
        _cargo = cargo.ToDictionary(c => c.Id, StringComparer.Ordinal);
        _between = new(StringComparer.Ordinal);
        foreach (var from in places)
        {
            var dist = Dijkstra(from.Node);
            _between[from.Id] = places.ToDictionary(p => p.Id, p => dist.GetValueOrDefault(p.Node, double.PositiveInfinity), StringComparer.Ordinal);
        }
    }

    public FuraPlace? Place(string id) => _places.GetValueOrDefault(id);
    public FuraCargo? CargoOf(string id) => _cargo.GetValueOrDefault(id);

    /// <summary>Найкоротший шлях дорогами між місцями, метри (нескінченність — дороги нема).</summary>
    public double Meters(string fromPlace, string toPlace) =>
        _between.TryGetValue(fromPlace, out var row) && row.TryGetValue(toPlace, out var m) ? m : double.PositiveInfinity;

    /// <summary>Те саме в кілометрах з одним знаком — так його бачить людина і так рахують плату й час.</summary>
    public double Km(string fromPlace, string toPlace) => Math.Round(Meters(fromPlace, toPlace) / 1000, 1, MidpointRounding.AwayFromZero);

    /// <summary>Дейкстра від вузла: довжина ребра — сума відрізків полілінії a → via… → b (дороги двобічні).</summary>
    Dictionary<string, double> Dijkstra(string start)
    {
        var dist = new Dictionary<string, double>(StringComparer.Ordinal) { [start] = 0 };
        var queue = new PriorityQueue<string, double>();
        queue.Enqueue(start, 0);
        var done = new HashSet<string>(StringComparer.Ordinal);
        while (queue.TryDequeue(out var node, out var d))
        {
            if (!done.Add(node)) continue;
            foreach (var road in Roads)
            {
                var other = road.A == node ? road.B : road.B == node ? road.A : null;
                if (other is null || done.Contains(other)) continue;
                var nd = d + road.Length;
                if (nd < dist.GetValueOrDefault(other, double.PositiveInfinity))
                {
                    dist[other] = nd;
                    queue.Enqueue(other, nd);
                }
            }
        }
        return dist;
    }

    // ---------- розбір ----------

    /// <summary>Розібрати JSON мапи. Публічне для тестів (мапа з рядка без диска).</summary>
    public static FuraMap Parse(string id, string json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new FormatException("мапа — не об'єкт");
        var nodes = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        foreach (var (name, pt) in root["nodes"]!.AsObject()) nodes[name] = Point(pt!);

        var roads = new List<FuraRoad>();
        foreach (var r in root["roads"]!.AsArray())
        {
            var a = (string)r!["a"]!;
            var b = (string)r["b"]!;
            if (!nodes.TryGetValue(a, out var pa) || !nodes.TryGetValue(b, out var pb))
                throw new FormatException($"дорога {(string?)r["id"]} веде в невідомий вузол");
            var pts = new List<(double X, double Z)> { pa };
            if (r["via"] is JsonArray via) pts.AddRange(via.Select(v => Point(v!)));
            pts.Add(pb);
            var length = 0.0;
            for (var i = 1; i < pts.Count; i++) length += Math.Sqrt(Sq(pts[i].X - pts[i - 1].X) + Sq(pts[i].Z - pts[i - 1].Z));
            roads.Add(new FuraRoad((string?)r["id"] ?? $"{a}-{b}", (string?)r["kind"] ?? "", a, b, length));
        }

        var places = new List<FuraPlace>();
        foreach (var p in root["places"]!.AsArray())
        {
            var y = p!["yard"]!;
            var docks = p["docks"]!.AsArray()
                .Select(d => new FuraDock((string)d!["id"]!, (double)d["x"]!, (double)d["z"]!, (double?)d["yaw"] ?? 0))
                .ToArray();
            var node = (string)p["node"]!;
            if (!nodes.ContainsKey(node)) throw new FormatException($"місце {(string?)p["id"]} стоїть на невідомому вузлі");
            if (docks.Length == 0) throw new FormatException($"місце {(string?)p["id"]} без боксів");
            places.Add(new FuraPlace((string)p["id"]!, (string?)p["title"] ?? "", (string?)p["short"] ?? (string?)p["title"] ?? "",
                node, new FuraYard((double)y["x"]!, (double)y["z"]!, (double)y["w"]!, (double)y["d"]!), docks,
                (bool?)p["depot"] ?? false));
        }
        var known = places.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

        var cargo = new List<FuraCargo>();
        foreach (var c in root["cargo"]!.AsArray())
        {
            static string[] Ids(JsonNode? n) => n is JsonArray a ? [.. a.Select(x => (string)x!)] : [];
            cargo.Add(new FuraCargo((string)c!["id"]!, (string?)c["title"] ?? "", (string?)c["emoji"] ?? "",
                (double?)c["mult"] ?? 1, [.. Ids(c["from"]).Where(known.Contains)], [.. Ids(c["to"]).Where(known.Contains)],
                (string?)c["trailer"] is { Length: > 0 } tr ? tr : FuraCargo.Tent, (double?)c["term"] is double term and > 0 ? term : 1));
        }

        return new FuraMap(id, (string?)root["title"] ?? id, json, nodes, roads, places, cargo);
    }

    static (double, double) Point(JsonNode n)
    {
        var a = n.AsArray();
        return ((double)a[0]!, (double)a[1]!);
    }

    static double Sq(double v) => v * v;
}

/// <summary>Дорога графа: <see cref="Length"/> — сума відрізків полілінії, метри.</summary>
public sealed record FuraRoad(string Id, string Kind, string A, string B, double Length);

/// <summary>Двір — прямокутник по осях: центр (X, Z), W уздовж X, D уздовж Z.</summary>
public sealed record FuraYard(double X, double Z, double W, double D)
{
    /// <summary>Чи точка у дворі, розширеному на <paramref name="margin"/> метрів з кожного боку.</summary>
    public bool Contains(double x, double z, double margin = 0) =>
        Math.Abs(x - X) <= W / 2 + margin && Math.Abs(z - Z) <= D / 2 + margin;
}

/// <summary>Бокс (місце для причепа): центр і курс передка причепа (0 = +Z, π/2 = +X).</summary>
public sealed record FuraDock(string Id, double X, double Z, double Yaw);

public sealed record FuraPlace(string Id, string Title, string Short, string Node, FuraYard Yard, FuraDock[] Docks, bool Depot)
{
    public FuraDock? Dock(string id) => Docks.FirstOrDefault(d => d.Id == id);
}

/// <summary>
/// Вантаж: <see cref="Mult"/> — множник плати; звідки й куди його возять (лише відомі місця). <see cref="Trailer"/> — тип
/// причепа, під який він їде (гараж 0.6: дошка показує лише вантажі під свої причепи), <see cref="Term"/> — множник терміну.
/// </summary>
public sealed record FuraCargo(string Id, string Title, string Emoji, double Mult, string[] From, string[] To,
    string Trailer = FuraCargo.Tent, double Term = 1)
{
    /// <summary>Звичайний тентований причіп: усі вантажі, що були до гаража.</summary>
    public const string Tent = "tent";
}
