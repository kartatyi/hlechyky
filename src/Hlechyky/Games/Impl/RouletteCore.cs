using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Правила європейської рулетки (docs/games/specs/roulette.md §2) — чиста статика без каркаса: колесо, кольори, ключі
/// полів, що покриває кожна ставка і скільки вона повертає. Ключ поля (<c>spot</c>) завжди канонічний: числа за
/// зростанням через «-» — «split:17-20», «corner:0-1-2-3», «dozen:2», «red».
/// </summary>
public static class RouletteCore
{
    /// <summary>Порядок кишеньок на колесі за годинниковою від зеро.</summary>
    public static readonly IReadOnlyList<int> Wheel =
    [
        0, 32, 15, 19, 4, 21, 2, 25, 17, 34, 6, 27, 13, 36, 11, 30, 8, 23, 10, 5, 24, 16, 33, 1, 20, 14, 31, 9, 22, 18, 29, 7,
        28, 12, 35, 3, 26,
    ];

    static readonly HashSet<int> Reds = [1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36];

    /// <summary>Зовнішні ставки: на зеро програють усі.</summary>
    public static readonly IReadOnlyList<string> Outside =
        ["column:1", "column:2", "column:3", "dozen:1", "dozen:2", "dozen:3", "red", "black", "even", "odd", "low", "high"];

    static readonly string[] Simple = ["red", "black", "even", "odd", "low", "high"];
    static readonly string[] Types =
        ["straight", "split", "street", "corner", "line", "column", "dozen", "red", "black", "even", "odd", "low", "high"];

    /// <summary>Усі 157 полів (§2.3) — ключ → які числа покриває.</summary>
    static readonly Dictionary<string, int[]> Spots = Build();

    /// <summary>Усі канонічні ключі в порядку: числа, спліти, вулиці, кути, лінії, колонки, дюжини, рівні гроші.</summary>
    public static readonly IReadOnlyList<string> All = [.. Spots.Keys];

    /// <summary>"r", "b" або "g" (зеро).</summary>
    public static string ColorOf(int n) => n == 0 ? "g" : Reds.Contains(n) ? "r" : "b";

    public static bool Valid(string? spot) => spot is not null && Spots.ContainsKey(spot);

    /// <summary>Числа, які покриває поле (для невідомого — порожньо).</summary>
    public static int[] Covers(string spot) => Spots.TryGetValue(spot, out var nums) ? nums : [];

    /// <summary>Виплата k:1 (число — 35, спліт — 17 … рівні гроші — 1).</summary>
    public static int Pays(string spot) => Type(spot) switch
    {
        "straight" => 35,
        "split" => 17,
        "street" => 11,
        "corner" => 8,
        "line" => 5,
        "column" or "dozen" => 2,
        _ => 1,
    };

    /// <summary>
    /// Найбільше черепків на одному полі: число платить ×36, і виграш мусить влізти в <c>int</c> гаманця (§12). Лімітів
    /// ставок нема — це лише межа арифметики.
    /// </summary>
    public const int MaxPerSpot = int.MaxValue / 36;

    /// <summary>Що повертається до гаманця за ставку <paramref name="amount"/> на поле, коли випало <paramref name="n"/>: ставка×(k+1) або 0. У <c>long</c> — без обрізання.</summary>
    public static long Return(string spot, int amount, int n) =>
        amount > 0 && Array.IndexOf(Covers(spot), n) >= 0 ? (long)amount * (Pays(spot) + 1) : 0;

    /// <summary>Найбільше, що поверне набір ставок за будь-якого числа 0..36 (у <c>long</c>).</summary>
    public static long MaxReturn(IEnumerable<(string Spot, int Amount)> bets)
    {
        var list = bets.ToList();
        long best = 0;
        for (var n = 0; n <= 36; n++) best = Math.Max(best, list.Sum(b => Return(b.Spot, b.Amount, n)));
        return best;
    }

    /// <summary>Канон із рядка: «split:20-17» → «split:17-20». Такого поля нема — null.</summary>
    public static string? Canon(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        key = key.Trim().ToLowerInvariant();
        if (Spots.ContainsKey(key)) return key;
        var colon = key.IndexOf(':');
        if (colon <= 0) return null;
        var type = key[..colon];
        if (Array.IndexOf(Simple, type) >= 0) return null;   // «red:1» — такого поля нема
        var parts = key[(colon + 1)..].Split('-');
        var nums = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, null, out nums[i])) return null;
        return FromParts(type, nums, null);
    }

    /// <summary>
    /// Канон із частин (форма агентів MCP): <paramref name="type"/> + числа для внутрішніх ставок або
    /// <paramref name="target"/> 1..3 для колонки й дюжини. Такого поля нема — null.
    /// </summary>
    public static string? FromParts(string type, IReadOnlyList<int>? numbers, int? target)
    {
        string key;
        switch (type)
        {
            case "column" or "dozen":
                var t = target ?? (numbers is { Count: 1 } ? numbers[0] : 0);
                key = $"{type}:{t}";
                break;
            case "straight" or "split" or "street" or "corner" or "line":
                if (numbers is null || numbers.Count == 0) return null;
                key = $"{type}:{string.Join('-', numbers.Order())}";
                break;
            default:
                if (Array.IndexOf(Simple, type) < 0) return null;
                key = type;
                break;
        }
        return Spots.ContainsKey(key) ? key : null;
    }

    /// <summary>
    /// Поле зі ставки гравця: <c>{ spot }</c> (так шле модуль) або <c>{ type, numbers | target }</c> (агенти). Обидва
    /// разом — береться <c>spot</c>. Повертає текст відмови або null, і тоді <paramref name="spot"/> — канонічний ключ.
    /// </summary>
    public static string? Read(JsonElement payload, out string spot)
    {
        spot = "";
        if (payload.ValueKind != JsonValueKind.Object) return Say.NotUnderstood;
        if (payload.TryGetProperty("spot", out var s))
        {
            if (s.ValueKind != JsonValueKind.String) return Say.NotUnderstood;
            if (Canon(s.GetString()) is not { } canon) return Say.NoSuchSpot;
            spot = canon;
            return null;
        }
        if (!payload.TryGetProperty("type", out var tp) || tp.ValueKind != JsonValueKind.String) return Say.NotUnderstood;
        var type = (tp.GetString() ?? "").Trim().ToLowerInvariant();
        if (Array.IndexOf(Types, type) < 0) return Say.NotUnderstood;
        List<int>? numbers = null;
        int? target = null;
        if (type is "straight" or "split" or "street" or "corner" or "line")
        {
            if (!payload.TryGetProperty("numbers", out var arr) || arr.ValueKind != JsonValueKind.Array) return Say.NotUnderstood;
            numbers = [];
            foreach (var x in arr.EnumerateArray())
            {
                if (x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out var v)) return Say.NotUnderstood;
                numbers.Add(v);
            }
        }
        else if (type is "column" or "dozen")
        {
            if (!payload.TryGetProperty("target", out var tg) || tg.ValueKind != JsonValueKind.Number || !tg.TryGetInt32(out var v))
                return Say.NotUnderstood;
            target = v;
        }
        if (FromParts(type, numbers, target) is not { } key) return Say.NoSuchSpot;
        spot = key;
        return null;
    }

    /// <summary>Людська назва поля: «Спліт 17·20», «Перші чотири», «Дюжина 13–24».</summary>
    public static string Label(string spot)
    {
        var nums = Covers(spot);
        return Type(spot) switch
        {
            "straight" => nums[0] == 0 ? "Зеро" : $"Число {nums[0]}",
            "split" => $"Спліт {Dots(nums)}",
            "street" => $"Вулиця {Dots(nums)}",
            "corner" => spot == "corner:0-1-2-3" ? "Перші чотири" : $"Кут {Dots(nums)}",
            "line" => $"Лінія {nums[0]}–{nums[^1]}",
            "column" => $"Колонка {spot[^1]}",
            "dozen" => $"Дюжина {nums[0]}–{nums[^1]}",
            "red" => "Червоне",
            "black" => "Чорне",
            "even" => "Парне",
            "odd" => "Непарне",
            "low" => "1–18",
            "high" => "19–36",
            _ => spot,
        };
    }

    /// <summary>Тип поля — те, що до двокрапки («split:17-20» → «split»).</summary>
    public static string Type(string spot)
    {
        var colon = spot.IndexOf(':');
        return colon < 0 ? spot : spot[..colon];
    }

    public static bool IsOutside(string spot) => Outside.Contains(spot);

    static string Dots(int[] nums) => string.Join('·', nums);

    static Dictionary<string, int[]> Build()
    {
        var d = new Dictionary<string, int[]>(StringComparer.Ordinal);
        void Add(string type, params int[] nums) => d[$"{type}:{string.Join('-', nums.Order())}"] = [.. nums.Order()];

        for (var n = 0; n <= 36; n++) Add("straight", n);
        for (var a = 1; a <= 35; a++) if (a % 3 != 0) Add("split", a, a + 1);
        for (var a = 1; a <= 33; a++) Add("split", a, a + 3);
        Add("split", 0, 1); Add("split", 0, 2); Add("split", 0, 3);
        for (var a = 1; a <= 34; a += 3) Add("street", a, a + 1, a + 2);
        Add("street", 0, 1, 2); Add("street", 0, 2, 3);
        for (var a = 1; a <= 32; a++) if (a % 3 != 0) Add("corner", a, a + 1, a + 3, a + 4);
        Add("corner", 0, 1, 2, 3);
        for (var a = 1; a <= 31; a += 3) Add("line", a, a + 1, a + 2, a + 3, a + 4, a + 5);
        var one36 = Enumerable.Range(1, 36).ToArray();
        for (var c = 1; c <= 3; c++) d[$"column:{c}"] = [.. one36.Where(n => (n - 1) % 3 == c - 1)];
        for (var z = 1; z <= 3; z++) d[$"dozen:{z}"] = [.. one36.Where(n => (n - 1) / 12 == z - 1)];
        d["red"] = [.. one36.Where(Reds.Contains)];
        d["black"] = [.. one36.Where(n => !Reds.Contains(n))];
        d["even"] = [.. one36.Where(n => n % 2 == 0)];
        d["odd"] = [.. one36.Where(n => n % 2 == 1)];
        d["low"] = [.. one36.Where(n => n <= 18)];
        d["high"] = [.. one36.Where(n => n >= 19)];
        return d;
    }

    /// <summary>Тексти відмов, які стосуються поля (§5 п. 4).</summary>
    public static class Say
    {
        public const string NotUnderstood = "Не зрозумів ставки";
        public const string NoSuchSpot = "Такої ставки на полі нема";
        public const string SpotTooBig = "Завелика ставка на одне поле";
        public const string WinTooBig = "Завеликий можливий виграш — каса стільки за раз не виплатить";
    }
}
