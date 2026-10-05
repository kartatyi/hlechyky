using System.Collections.Concurrent;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Клітинка дошки вечірки (specs/vechirka.md §3.1).</summary>
public sealed record VechirkaNode(string Id, int X, int Y, string Type, int V, bool Bump, string? Name, string? Zone);

/// <summary>Варіант розвилки: куди веде й підпис кнопки (перший — типовий).</summary>
public sealed record VechirkaFork(string To, string Label);

/// <summary>Платне ребро (пором): плата — у скарбничку, <see cref="Key"/> — можна ключем.</summary>
public sealed record VechirkaGate(string From, string To, int Cost, bool Key, string Name);

/// <summary>
/// Карта вечірки — дані, не код (§3): сервер і клієнт читають той самий JSON. Тут модель, перевірки при
/// завантаженні й BFS (уперед за ребрами; неорієнтована відстань — для пательні, шлагбаума й переїзду лавки).
/// </summary>
public sealed class VechirkaMap
{
    public string Id { get; }
    public string Title { get; }
    public int V { get; }
    public int W { get; }
    public int H { get; }
    public string Start { get; }
    public IReadOnlyList<VechirkaNode> Nodes { get; }
    public IReadOnlyDictionary<string, VechirkaNode> ById { get; }
    public IReadOnlyDictionary<string, string[]> Next { get; }
    public IReadOnlyDictionary<string, string[]> Prev { get; }
    public IReadOnlyDictionary<string, VechirkaFork[]> Forks { get; }
    public IReadOnlyList<VechirkaGate> Gates { get; }
    public IReadOnlyList<string> Stands { get; }
    /// <summary>Сирий JSON — клієнтові як є (декор, зони), щоб не губити полів, яких сервер не знає.</summary>
    public string Json { get; }

    readonly Dictionary<string, Dictionary<string, int>> _und = new();

    VechirkaMap(JsonElement r, string json)
    {
        Json = json;
        Id = r.GetProperty("id").GetString()!;
        Title = r.TryGetProperty("title", out var t) ? t.GetString() ?? Id : Id;
        V = r.TryGetProperty("v", out var v) ? v.GetInt32() : 1;
        W = r.GetProperty("w").GetInt32();
        H = r.GetProperty("h").GetInt32();
        Start = r.GetProperty("start").GetString()!;
        var nodes = new List<VechirkaNode>();
        foreach (var n in r.GetProperty("nodes").EnumerateArray())
            nodes.Add(new VechirkaNode(
                n.GetProperty("id").GetString()!, n.GetProperty("x").GetInt32(), n.GetProperty("y").GetInt32(),
                n.GetProperty("type").GetString()!,
                n.TryGetProperty("v", out var nv) ? nv.GetInt32() : 0,
                n.TryGetProperty("bump", out var b) && b.ValueKind == JsonValueKind.True,
                n.TryGetProperty("name", out var nm) ? nm.GetString() : null,
                n.TryGetProperty("zone", out var z) ? z.GetString() : null));
        Nodes = nodes;
        var byId = new Dictionary<string, VechirkaNode>();
        foreach (var n in nodes)
            if (!byId.TryAdd(n.Id, n)) throw new InvalidDataException($"карта {Id}: вузол {n.Id} двічі");
        ById = byId;

        var next = nodes.ToDictionary(n => n.Id, _ => new List<string>());
        var prev = nodes.ToDictionary(n => n.Id, _ => new List<string>());
        foreach (var e in r.GetProperty("edges").EnumerateArray())
        {
            var a = e[0].GetString()!; var bb = e[1].GetString()!;
            if (!byId.ContainsKey(a) || !byId.ContainsKey(bb)) throw new InvalidDataException($"карта {Id}: ребро {a}→{bb} у нікуди");
            next[a].Add(bb); prev[bb].Add(a);
        }
        Next = next.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        Prev = prev.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());

        var forks = new Dictionary<string, VechirkaFork[]>();
        if (r.TryGetProperty("forks", out var fk))
            foreach (var f in fk.EnumerateObject())
                forks[f.Name] = f.Value.EnumerateArray()
                    .Select(o => new VechirkaFork(o.GetProperty("to").GetString()!, o.GetProperty("label").GetString() ?? "")).ToArray();
        Forks = forks;
        var gates = new List<VechirkaGate>();
        if (r.TryGetProperty("gates", out var gs))
            foreach (var g in gs.EnumerateArray())
                gates.Add(new VechirkaGate(g.GetProperty("from").GetString()!, g.GetProperty("to").GetString()!,
                    g.GetProperty("cost").GetInt32(), g.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.True,
                    g.TryGetProperty("name", out var gn) ? gn.GetString() ?? "" : ""));
        Gates = gates;
        Stands = r.TryGetProperty("stands", out var st) ? st.EnumerateArray().Select(x => x.GetString()!).ToArray() : [];
        Check();
    }

    /// <summary>Перевірки §3.1: кривої карти краще не мати зовсім, ніж вечір, що застряг посеред села.</summary>
    void Check()
    {
        if (!ById.ContainsKey(Start)) throw new InvalidDataException($"карта {Id}: старту {Start} нема");
        foreach (var n in Nodes)
        {
            if (Next[n.Id].Length == 0) throw new InvalidDataException($"карта {Id}: з {n.Id} нема виходу");
            if (n.X < 0 || n.Y < 0 || n.X > W || n.Y > H) throw new InvalidDataException($"карта {Id}: {n.Id} поза полем");
            var isFork = Next[n.Id].Length >= 2;
            if (isFork != Forks.ContainsKey(n.Id)) throw new InvalidDataException($"карта {Id}: розвилка {n.Id} без опису (або навпаки)");
            if (isFork && !Forks[n.Id].Select(f => f.To).Order().SequenceEqual(Next[n.Id].Order()))
                throw new InvalidDataException($"карта {Id}: варіанти розвилки {n.Id} не ті, що ребра");
        }
        foreach (var g in Gates)
            if (!Next.TryGetValue(g.From, out var nx) || !nx.Contains(g.To)) throw new InvalidDataException($"карта {Id}: ворота {g.From}→{g.To} без ребра");
        foreach (var s in Stands)
        {
            if (!ById.ContainsKey(s)) throw new InvalidDataException($"карта {Id}: стенда {s} нема");
            if (Prev[s].Length != 1) throw new InvalidDataException($"карта {Id}: у стенда {s} не один попередник");
        }
        var seen = new HashSet<string> { Start };
        var q = new Queue<string>([Start]);
        while (q.Count > 0) foreach (var nx in Next[q.Dequeue()]) if (seen.Add(nx)) q.Enqueue(nx);
        if (seen.Count != Nodes.Count) throw new InvalidDataException($"карта {Id}: не все досяжне зі старту");
    }

    public VechirkaNode this[string id] => ById[id];

    public VechirkaGate? GateOf(string from, string to) => Gates.FirstOrDefault(g => g.From == from && g.To == to);

    /// <summary>Неорієнтована відстань (по всіх ребрах, поромні теж) від <paramref name="from"/> до кожного вузла.</summary>
    public IReadOnlyDictionary<string, int> Undirected(string from)
    {
        lock (_und)
        {
            if (_und.TryGetValue(from, out var d)) return d;
            d = new Dictionary<string, int> { [from] = 0 };
            var q = new Queue<string>([from]);
            while (q.Count > 0)
            {
                var c = q.Dequeue();
                foreach (var nx in Next[c].Concat(Prev[c]))
                    if (d.TryAdd(nx, d[c] + 1)) q.Enqueue(nx);
            }
            return _und[from] = d;
        }
    }

    public int Dist(string a, string b) => Undirected(a).TryGetValue(b, out var d) ? d : int.MaxValue;

    /// <summary>
    /// Кроки вперед від <paramref name="from"/> до <paramref name="to"/> (BFS за ребрами; ворота — лише якщо
    /// <paramref name="ferry"/>). null — недосяжно. Стартовий вузол не рахується: from == to → 0.
    /// </summary>
    public int? Forward(string from, string to, bool ferry) => ForwardPath(from, to, ferry)?.Count;

    /// <summary>Найкоротший шлях уперед (без стартового вузла). null — недосяжно.</summary>
    public List<string>? ForwardPath(string from, string to, bool ferry)
    {
        if (from == to) return [];
        var back = new Dictionary<string, string> { [from] = "" };
        var q = new Queue<string>([from]);
        while (q.Count > 0)
        {
            var c = q.Dequeue();
            foreach (var nx in Next[c])
            {
                if (!ferry && GateOf(c, nx) is not null) continue;
                if (!back.TryAdd(nx, c)) continue;
                if (nx == to)
                {
                    var path = new List<string>();
                    for (var x = to; x != from; x = back[x]) path.Add(x);
                    path.Reverse();
                    return path;
                }
                q.Enqueue(nx);
            }
        }
        return null;
    }

    // ---------- завантаження ----------

    static readonly ConcurrentDictionary<string, VechirkaMap> Cache = new();

    public static VechirkaMap Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new VechirkaMap(doc.RootElement, json);
    }

    /// <summary>Карта з <c>data/vechirka/maps/&lt;id&gt;.json</c> (кеш на процес). Нема файла — виняток: вечірки без села нема.</summary>
    public static VechirkaMap Load(string id = "selo") => Cache.GetOrAdd(id, k =>
    {
        if (k.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new InvalidDataException("кривий id карти");
        var path = Path.Combine(Paths.Root, "data", "vechirka", "maps", k + ".json");
        return Parse(File.ReadAllText(path));
    });
}
