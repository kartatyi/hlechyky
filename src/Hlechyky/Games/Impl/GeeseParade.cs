namespace Hlechyky.Games.Impl;

/// <summary>Вид живності параду: ключ (у виді), однина, родовий множини (для питань), емодзі й темп бігу.</summary>
public sealed record GeeseKind(string Id, string One, string Many, string Emoji, double Pace);

/// <summary>Прикмета: коза в хустці, гусак із глечиком… <see cref="Many"/> — «кіз у хустці» для питання.</summary>
public sealed record GeeseTrait(string Id, string Kind, string One, string Many);

/// <summary>
/// Одна тварина параду. Одиниці: <see cref="T0"/> — мс від початку параду, коли тварина виходить з-за краю;
/// <see cref="V"/> — ширин двору за секунду; <see cref="L"/> — доріжка (0 — найдальша, згори); <see cref="D"/> — +1
/// праворуч, −1 ліворуч; <see cref="Y"/> — зсув у межах доріжки (−0,25…0,25 її висоти), щоб сусіди не злипались;
/// <see cref="F"/> — номер зграї (−1 — сама). Положення: <c>p = (t − T0)/1000 · V</c>, <c>x = D &gt; 0 ? −M + p : 1 + M − p</c>.
/// </summary>
public sealed record GeeseAnimal(string K, string? Tr, int L, int D, int T0, double V, double Y, int F);

/// <summary>Тин чи віз на доріжці <see cref="L"/> від <see cref="X0"/> до <see cref="X1"/> (частки ширини двору): за ним видно лише частину тварини.</summary>
public sealed record GeeseCover(string Kind, int L, double X0, double X1);

/// <summary>
/// Питання раунду. <see cref="Type"/>: <c>count</c> (один вид), <c>sum</c> (два види разом), <c>trait</c> (з прикметою),
/// <c>most</c> (кого найбільше — вибір з <see cref="Opts"/>), <c>dir</c> (скільки бігло ліворуч).
/// </summary>
public sealed record GeeseQuestion(string Type, string Text, string[] Kinds, string? Trait, string[]? Opts)
{
    public bool Choice => Opts is not null;
}

/// <summary>Раунд: парад, питання, правильна відповідь (число або номер варіанта) і тварини-відповідь (індекси).</summary>
public sealed record GeeseRound(int Level, bool Pre, int Ms, int Lanes, GeeseAnimal[] Animals, GeeseCover[] Covers,
    GeeseQuestion Q, int Answer, int[] Hits);

/// <summary>
/// Генератор параду «Порахуй гусей». Чистий і детермінований: той самий <see cref="Random"/> — той самий парад.
/// Сервер будує парад тут і шле клієнтам готовий список тварин (а не лише сід): клієнт лише малює рух за формулою,
/// тож у різних гравців парад однаковий до тварини без паритету C#↔JS. Рівень 0…6 — складність (раунд 1…7).
/// </summary>
public static class GeeseParade
{
    /// <summary>Поле за краєм двору (частка ширини): тварина виходить з-за краю й зникає за іншим.</summary>
    public const double Margin = 0.08;
    public const int MaxLevel = 6;
    public const int MinAnimals = 8, MaxAnimals = 30;
    /// <summary>Найбільше число, яке можна відповісти.</summary>
    public const int MaxAnswer = 60;
    /// <summary>Найдовша дорога тварини через двір — частка параду: решта йде на те, щоб тварини виходили весь парад.</summary>
    public const double MaxTravel = 0.33;
    const double SlowestPace = 0.85;   // найповільніший вид (качка)

    public static readonly GeeseKind[] Kinds =
    [
        new("goose", "гуска", "гусей", "🪿", 0.95),
        new("hen", "курка", "курей", "🐔", 1.0),
        new("duck", "качка", "качок", "🦆", 0.85),
        new("goat", "коза", "кіз", "🐐", 1.1),
        new("pig", "порося", "поросят", "🐖", 0.9),
        new("cat", "кіт", "котів", "🐈", 1.25),
    ];

    public static readonly GeeseTrait[] Traits =
    [
        new("hustka", "goat", "коза в хустці", "кіз у хустці"),
        new("hlechyk", "goose", "гусак із глечиком на голові", "гусаків із глечиком на голові"),
        new("chorna", "hen", "чорна курка", "чорних курей"),
        new("bant", "pig", "порося з бантиком", "поросят із бантиком"),
        new("rudyi", "cat", "рудий кіт", "рудих котів"),
    ];

    public static GeeseKind Kind(string id) => Kinds.First(k => k.Id == id);
    public static GeeseTrait Trait(string id) => Traits.First(t => t.Id == id);

    /// <summary>Прикмети на гру: 2–3 з п'яти.</summary>
    public static string[] PickTraits(Random rng) =>
        [.. Traits.Select(t => t.Id).OrderBy(_ => rng.Next()).Take(2 + rng.Next(2)).OrderBy(x => x, StringComparer.Ordinal)];

    public static int Lanes(int level) => level < 2 ? 1 : level < 4 ? 2 : 3;
    public static int Count(int level) => MinAnimals + (int)Math.Round((MaxAnimals - MinAnimals) * Math.Clamp(level, 0, MaxLevel) / (double)MaxLevel);
    public static int ParadeMs(int level) => 10_000 + 833 * Math.Clamp(level, 0, MaxLevel);
    /// <summary>Базова швидкість рівня, ширин двору за секунду: від ~5,8 с через двір до ~2,8 с.</summary>
    public static double Speed(int level) => 0.20 + 0.035 * Math.Clamp(level, 0, MaxLevel);

    /// <summary>
    /// Раунд рівня <paramref name="level"/>. <paramref name="ms"/> — довжина параду (null — за рівнем),
    /// <paramref name="lastType"/> — тип питання минулого раунду (не повторюємо двічі поспіль).
    /// </summary>
    public static GeeseRound Make(Random rng, int level, bool pre, string[] traits, string? lastType = null, int? ms = null)
    {
        level = Math.Clamp(level, 0, MaxLevel);
        var lanes = Lanes(level);
        var both = level >= 2;
        var paradeMs = ms ?? ParadeMs(level);
        var n = Math.Clamp(Count(level) + rng.Next(3) - 1, MinAnimals, MaxAnimals);

        // Тип питання — до параду: під нього й складаємо парад, щоб питання завжди мало сенс.
        var types = new List<string> { "count" };
        if (level >= 1) { types.Add("sum"); types.Add("most"); }
        if (level >= 2 && traits.Length > 0) types.Add("trait");
        if (both) types.Add("dir");
        if (types.Count > 1 && lastType is not null) types.Remove(lastType);
        var type = types[rng.Next(types.Count)];
        var trait = type == "trait" ? Trait(traits[rng.Next(traits.Length)]) : null;

        // Які види біжать: 3 на початку, 6 наприкінці. Вид прикмети з питання — обов'язково.
        var kc = Math.Min(Kinds.Length, 3 + level / 2);
        var kinds = Kinds.Select(k => k.Id).OrderBy(_ => rng.Next()).Take(kc).ToList();
        if (trait is not null && !kinds.Contains(trait.Kind)) kinds[^1] = trait.Kind;

        // Скільки кого: кожен вид — хоч одна тварина, решта — за випадковою вагою.
        var counts = kinds.ToDictionary(k => k, _ => 1);
        var weights = kinds.Select(_ => 1 + rng.Next(4)).ToArray();
        for (var i = kinds.Count; i < n; i++)
        {
            var roll = rng.Next(weights.Sum());
            var j = 0;
            while (roll >= weights[j]) roll -= weights[j++];
            counts[kinds[j]]++;
        }
        if (type == "most")
        {
            // «Кого найбільше» мусить мати одну відповідь: при рівних перекладаємо одну тварину до першого з рівних.
            var max = counts.Values.Max();
            var tied = kinds.Where(k => counts[k] == max).ToList();
            if (tied.Count > 1) { counts[tied[1]]--; counts[tied[0]]++; }
        }
        if (trait is not null && counts[trait.Kind] < 2)
        {
            var donor = kinds.Where(k => k != trait.Kind).OrderByDescending(k => counts[k]).First();
            if (counts[donor] > 1) { counts[donor]--; counts[trait.Kind]++; }
        }

        // Тварини групами: зграя (від рівня 3) — 3–5 одного виду разом, однією доріжкою й швидкістю.
        var units = new List<(string K, int Size)>();
        foreach (var k in kinds)
        {
            var left = counts[k];
            while (left > 0)
            {
                var size = level >= 3 && left >= 3 && rng.NextDouble() < 0.45 ? Math.Min(left, 3 + rng.Next(3)) : 1;
                units.Add((k, size));
                left -= size;
            }
        }
        units = [.. units.OrderBy(_ => rng.Next())];

        // Напрямки: з рівня 2 частина біжить ліворуч (на рівні 2 — менше).
        var leftShare = level >= 4 ? 0.5 : 0.3;
        var dirs = units.Select(_ => both && rng.NextDouble() < leftShare ? -1 : 1).ToArray();
        if (type == "dir")
        {
            if (!dirs.Contains(-1)) dirs[rng.Next(dirs.Length)] = -1;
            if (!dirs.Contains(1) && dirs.Length > 1) dirs[(Array.IndexOf(dirs, -1) + 1) % dirs.Length] = 1;
        }

        // Час виходу: рівні проміжки з тремтінням, щоб двір не пустував і тварини не виходили купою.
        // Повільний парад підганяємо: дорога через двір — не довше MaxTravel параду, інакше на виходи лишається
        // лише перша половина, і далі рахуєш застиглий натовп, а не парад. Множник спільний для всіх — різниця
        // темпу між видами лишається.
        var vMin = (1 + 2 * Margin) * 1000 / (MaxTravel * paradeMs);
        var speed = Speed(level) * Math.Max(1, vMin / (Speed(level) * SlowestPace * 0.9));
        var animals = new List<GeeseAnimal>(n);
        var gap = (int)(70 / speed);     // мс між тваринами зграї: ~0,07 ширини двору
        for (var u = 0; u < units.Count; u++)
        {
            var (k, size) = units[u];
            var v = Math.Round(speed * Kind(k).Pace * (0.9 + 0.2 * rng.NextDouble()), 4);
            var travel = (1 + 2 * Margin) / v * 1000;
            var span = (size - 1) * gap;
            var latest = Math.Max(0, paradeMs - 300 - travel - span);
            var slot = (u + 0.15 + 0.7 * rng.NextDouble()) / units.Count * latest;
            var t0 = (int)Math.Min(latest, slot);
            var lane = rng.Next(lanes);
            for (var i = 0; i < size; i++)
                animals.Add(new GeeseAnimal(k, null, lane, dirs[u], t0 + i * gap, v, Math.Round((rng.NextDouble() - 0.5) * 0.5, 3), size > 1 ? u : -1));
        }

        // Прикмети гри: кожна тварина свого виду — з імовірністю ~35 %; у питанні про прикмету — хоч одна.
        for (var i = 0; i < animals.Count; i++)
            foreach (var tr in traits)
                if (Trait(tr).Kind == animals[i].K && rng.NextDouble() < 0.35) animals[i] = animals[i] with { Tr = tr };
        if (trait is not null && !animals.Any(a => a.Tr == trait.Id))
        {
            var idx = animals.FindIndex(a => a.K == trait.Kind);
            animals[idx] = animals[idx] with { Tr = trait.Id };
        }

        // Тин і віз (з рівня 3): за ними видно частину тварини.
        var covers = new List<GeeseCover>();
        if (level >= 3)
        {
            var nc = level >= 5 ? 2 : 1;
            for (var c = 0; c < nc; c++)
            {
                var w = 0.14 + 0.06 * rng.NextDouble();
                var x0 = Math.Round(0.15 + (0.6 - w) * rng.NextDouble() + c * 0.05, 3);
                var lane = c % lanes == 0 ? lanes - 1 : rng.Next(lanes);
                if (covers.Any(o => o.L == lane && o.X0 < x0 + w && x0 < o.X1)) x0 = Math.Round(Math.Min(0.86 - w, x0 + 0.3), 3);
                covers.Add(new GeeseCover(rng.Next(2) == 0 ? "tyn" : "viz", lane, x0, Math.Round(x0 + w, 3)));
            }
        }

        var list = animals.OrderBy(a => a.T0).ThenBy(a => a.L).ToArray();
        var (q, answer, hits) = Ask(rng, type, trait, kinds, list);
        return new GeeseRound(level, pre, paradeMs, lanes, list, [.. covers], q, answer, hits);
    }

    static (GeeseQuestion Q, int Answer, int[] Hits) Ask(Random rng, string type, GeeseTrait? trait, List<string> kinds, GeeseAnimal[] list)
    {
        int[] Where(Func<GeeseAnimal, bool> f) => [.. Enumerable.Range(0, list.Length).Where(i => f(list[i]))];
        int Of(string k) => list.Count(a => a.K == k);
        switch (type)
        {
            case "sum":
            {
                var pair = kinds.OrderBy(_ => rng.Next()).Take(2).OrderBy(k => Array.FindIndex(Kinds, x => x.Id == k)).ToArray();
                var hits = Where(a => pair.Contains(a.K));
                return (new GeeseQuestion(type, $"{Cap(Kind(pair[0]).Many)} і {Kind(pair[1]).Many} разом — скільки?", pair, null, null), hits.Length, hits);
            }
            case "trait":
            {
                var hits = Where(a => a.Tr == trait!.Id);
                return (new GeeseQuestion(type, $"Скільки було {trait!.Many}?", [trait.Kind], trait.Id, null), hits.Length, hits);
            }
            case "most":
            {
                var best = kinds.OrderByDescending(Of).First();
                var opts = kinds.Where(k => k != best).OrderBy(_ => rng.Next()).Take(3).Append(best).OrderBy(_ => rng.Next()).ToArray();
                return (new GeeseQuestion(type, "Кого було найбільше?", [best], null, opts), Array.IndexOf(opts, best), Where(a => a.K == best));
            }
            case "dir":
            {
                var hits = Where(a => a.D < 0);
                return (new GeeseQuestion(type, "Скільки тварин бігло ліворуч?", [], null, null), hits.Length, hits);
            }
            default:
            {
                // Вид із двома й більше — питати «скільки котів», коли кіт один, нецікаво.
                var pool = kinds.Where(k => Of(k) >= 2).ToList();
                var k = (pool.Count > 0 ? pool : kinds)[rng.Next(pool.Count > 0 ? pool.Count : kinds.Count)];
                var hits = Where(a => a.K == k);
                return (new GeeseQuestion("count", $"Скільки {Kind(k).Many} пробігло двором?", [k], null, null), hits.Length, hits);
            }
        }
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
