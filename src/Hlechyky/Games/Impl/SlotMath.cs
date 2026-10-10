using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Hlechyky.Games.Impl;

// =====================================================================================================================
// Математика автоматів (docs/games/specs/slots.md §3). Чисті класи без кімнат: симуляції в тестах женуть мільйони
// обертів напряму. Новий автомат = новий клас ISlotMath + маленький нащадок SlotGame (Slots.cs).
// =====================================================================================================================

/// <summary>
/// Випадковість автомата. У проді — <see cref="CryptoSlotRng"/> (<see cref="RandomNumberGenerator"/>), у тестах —
/// <see cref="SeededSlotRng"/> на сідованому <see cref="Random"/> кімнати (<see cref="IRoomContext.Seeded"/>), як у рулетці.
/// </summary>
public interface ISlotRng
{
    /// <summary>Рівномірно 0..max-1.</summary>
    int Next(int max);

    /// <summary>Рівномірно [0, 1).</summary>
    double NextDouble();
}

public sealed class CryptoSlotRng : ISlotRng
{
    public static readonly CryptoSlotRng Instance = new();

    public int Next(int max) => RandomNumberGenerator.GetInt32(max);

    public double NextDouble()
    {
        Span<byte> b = stackalloc byte[8];
        RandomNumberGenerator.Fill(b);
        return (BitConverter.ToUInt64(b) >> 11) * (1.0 / (1UL << 53));
    }
}

public sealed class SeededSlotRng(Random rng) : ISlotRng
{
    public int Next(int max) => rng.Next(max);
    public double NextDouble() => rng.NextDouble();
}

/// <summary>
/// Результат оберту: сценарій (той самий JSON, що генерує мок <c>web/games/slots/&lt;id&gt;.js</c>: <c>{ bet, steps, win,
/// state, … }</c>), виграш у черепках (уже зі стелею), позначки (<c>glek3</c>, <c>bonus</c> …) і стан між обертами.
/// </summary>
public sealed record SlotOutcome(JsonObject Script, int Win, IReadOnlyList<string> Flags, JsonObject State)
{
    /// <summary>Виграш у ставках.</summary>
    public double Mult(int bet) => bet > 0 ? (double)Win / bet : 0;
}

/// <summary>Математика одного автомата: стрічки, лінії, таблиця виплат, сценарій оберту.</summary>
public interface ISlotMath
{
    /// <summary>Стеля виграшу за оберт у ставках (slot-glek — 1000×).</summary>
    int Cap { get; }

    /// <summary>Чи після виграшу відкривається ризик-гра «Ворожка» (лише slot-glek).</summary>
    bool Gamble { get; }

    /// <summary>
    /// Оберт: усе, разом із бонусом, пораховано наперед. <paramref name="state"/> — стан між обертами (у slot-glek
    /// порожній), повертається новий у <see cref="SlotOutcome.State"/>. Чиста функція від rng.
    /// </summary>
    SlotOutcome Spin(int bet, ISlotRng rng, JsonObject state);

    /// <summary>Таблиця для клієнта (ⓘ і звірка з моком): стрічки, лінії, виплати.</summary>
    JsonObject Table();
}

/// <summary>
/// «Однорукий Глек»: 3 барабани × 3 рядки, 5 ліній, стрічки по 34, Глек — дикий. Платить однаковий ряд зліва направо
/// від першого барабана; на лінії — один виграш. Виплати — у ставках на лінію (ставка ÷ 5). Після зупинки з шансом
/// 1 з 12 Дядько Глек чхає 🤧 — 1–3 клітинки (не Глеки) стають дикими Глеками, і лінії рахуються на зміненому полі.
/// RTP бази 97,0 % (96,996 % — точно, перебором усіх 34³ зупинок × усіх варіантів чиху), виграш у 52,45 % обертів.
/// </summary>
public sealed class SlotGlekMath : ISlotMath
{
    public const string Wild = "glek";
    public const int Lines = 5;

    // Стрічки барабанів (по 34): вишні 9, груша 8, слива 7, кавун 3, дзвоник 2, підкова 2, сімка 2 (поруч), Глек 1.
    // Фрукти розкидані рівно (однакові не стоять поруч, крім сімок), щоб частіше складались ряди. Клієнт крутить ті
    // самі стрічки (REELS у slot-glek.js чи view.table.reels) — символи над і під лінією чесні.
    public static readonly string[][] Reels =
    [
        "plum cherry seven seven pear cherry plum pear cherry melon plum pear cherry bell plum pear cherry melon plum pear cherry horseshoe glek pear cherry plum melon cherry pear plum bell cherry pear horseshoe".Split(' '),
        "plum melon pear cherry plum bell pear cherry melon plum pear cherry pear seven seven cherry plum horseshoe cherry pear plum melon cherry pear plum glek cherry pear bell plum cherry pear horseshoe cherry".Split(' '),
        "horseshoe pear cherry bell plum pear cherry melon pear cherry plum horseshoe pear cherry plum bell melon cherry pear plum cherry plum pear cherry seven seven pear plum cherry melon pear glek cherry plum".Split(' '),
    ];

    /// <summary>Лінії: рядок кожного барабана. 0 — середня, 1 — верхня, 2 — нижня, 3 і 4 — діагоналі.</summary>
    public static readonly int[][] PayLines = [[1, 1, 1], [0, 0, 0], [2, 2, 2], [0, 1, 2], [2, 1, 0]];

    /// <summary>
    /// Виплати в ставках на лінію: символ → (скільки в ряд → множник). 09.10 (98 %): частіші дрібні фрукти на стрічках
    /// і чих Глека, тож верх зрізано на 17–20 % (було 600/150/75/45/30/14/12/10+1, стрічки по 32 — RTP 95,5 %).
    /// </summary>
    public static readonly Dictionary<string, Dictionary<int, int>> Pay = new(StringComparer.Ordinal)
    {
        ["glek"] = new() { [3] = 500 },
        ["seven"] = new() { [3] = 120 },
        ["horseshoe"] = new() { [3] = 60 },
        ["bell"] = new() { [3] = 40 },
        ["melon"] = new() { [3] = 26 },
        ["plum"] = new() { [3] = 10 },
        ["pear"] = new() { [3] = 8 },
        ["cherry"] = new() { [3] = 7, [2] = 1 },
    };

    /// <summary>Чих: 1 з <see cref="SneezeOdds"/> обертів (rng.Next(12) == 11 — одне з 12 рівноймовірних).</summary>
    public const int SneezeOdds = 12;

    /// <summary>Скільки клітинок чхне: ваги для 1, 2, 3 (з 20: 70 % / 25 % / 5 %).</summary>
    public static readonly int[] SneezeWeights = [14, 5, 1];

    public int Cap => 1000;
    public bool Gamble => true;

    /// <summary>Виграш на лінії: (символ, скільки в ряд, множник на лінію) або null. Як evalLine у моку.</summary>
    public static (string Sym, int N, int Pay)? Line(string a, string b, string c)
    {
        Span<string> keys = [a, b, c];
        var sym = Wild;
        foreach (var k in keys) if (k != Wild) { sym = k; break; }
        var n = 0;
        foreach (var k in keys) { if (k == sym || k == Wild) n++; else break; }
        return Pay.TryGetValue(sym, out var p) && p.TryGetValue(n, out var pay) ? (sym, n, pay) : null;
    }

    /// <summary>Символ клітинки: стовпчик c, рядок r (0 — верх) при зупинці барабана на позиції stops[c].</summary>
    public static string At(int[] stops, int c, int r) => Reels[c][(stops[c] + r) % Reels[c].Length];

    /// <summary>Поле після зупинки: [стовпчик, рядок].</summary>
    public static string[,] Field(int[] stops)
    {
        var f = new string[3, 3];
        for (var c = 0; c < 3; c++) for (var r = 0; r < 3; r++) f[c, r] = At(stops, c, r);
        return f;
    }

    /// <summary>Сума виплат по всіх лініях у ставках на лінію для поля (для точного RTP перебором).</summary>
    public static int Units(string[,] f)
    {
        var u = 0;
        foreach (var ln in PayLines)
            if (Line(f[0, ln[0]], f[1, ln[1]], f[2, ln[2]]) is { } w) u += w.Pay;
        return u;
    }

    /// <summary>Сума виплат без чиху — лише зупинки.</summary>
    public static int Units(int[] stops) => Units(Field(stops));

    /// <summary>Клітинки, що можуть чхнути: усі, де не Глек, у порядку стовпчик → рядок.</summary>
    public static List<(int C, int R)> SneezeTargets(string[,] f)
    {
        var list = new List<(int, int)>(9);
        for (var c = 0; c < 3; c++) for (var r = 0; r < 3; r++) if (f[c, r] != Wild) list.Add((c, r));
        return list;
    }

    /// <summary>
    /// Чи чхне Глек і які клітинки: шанс 1 з 12, кількість — за <see cref="SneezeWeights"/> (не більше, ніж є не-Глеків),
    /// клітинки — рівномірно серед не-Глеків (частковий Фішер — Єйтс). Не залежить ні від чого, крім rng.
    /// </summary>
    public static List<(int C, int R)> Sneeze(string[,] f, ISlotRng rng)
    {
        if (rng.Next(SneezeOdds) != SneezeOdds - 1) return [];
        var x = rng.Next(SneezeWeights.Sum());
        var m = 1;
        for (var i = 0; i < SneezeWeights.Length; i++) { if (x < SneezeWeights[i]) { m = i + 1; break; } x -= SneezeWeights[i]; }
        var pool = SneezeTargets(f);
        m = Math.Min(m, pool.Count);
        var picked = new List<(int, int)>(m);
        for (var i = 0; i < m; i++)
        {
            var j = i + rng.Next(pool.Count - i);
            (pool[i], pool[j]) = (pool[j], pool[i]);
            picked.Add(pool[i]);
        }
        return picked;
    }

    public SlotOutcome Spin(int bet, ISlotRng rng, JsonObject state)
    {
        int[] stops = [rng.Next(Reels[0].Length), rng.Next(Reels[1].Length), rng.Next(Reels[2].Length)];
        return ScriptFor(stops, bet, state, Sneeze(Field(stops), rng));
    }

    /// <summary>
    /// Сценарій для заданих зупинок (і чиху) — поле за полем як scriptFor у моку: spin → morph (чих) → win.
    /// Виграш рахується на полі ПІСЛЯ чиху — рівно тому, що клієнт бачить після кроку morph.
    /// </summary>
    public SlotOutcome ScriptFor(int[] stops, int bet, JsonObject state, IReadOnlyList<(int C, int R)>? sneeze = null)
    {
        var f = Field(stops);
        // Очікування — лише чесно: на якійсь лінії перші два вже «сімка/Глек», і третій барабан справді вирішує.
        // Рахується на полі зупинки (барабани крутяться до чиху).
        static bool Big(string k) => k is Wild or "seven";
        var tease = new JsonArray();
        if (PayLines.Any(ln => Big(f[0, ln[0]]) && Big(f[1, ln[1]]))) tease.Add(2);

        var morph = new JsonArray();
        foreach (var (c, r) in sneeze ?? [])
        {
            if (f[c, r] == Wild) continue;   // Глек Глеком не чхає (Sneeze таких не дає — захист для тестових сценаріїв)
            f[c, r] = Wild;
            morph.Add(new JsonArray(c, r, Wild));
        }

        var items = new JsonArray();
        long win = 0;
        var glek3 = false;
        for (var i = 0; i < PayLines.Length; i++)
        {
            var ln = PayLines[i];
            if (Line(f[0, ln[0]], f[1, ln[1]], f[2, ln[2]]) is not { } w) continue;
            // як Math.round(u × ставка ÷ 5) у моку; для ставок, кратних 5, — рівно
            var amount = (long)Math.Round(w.Pay * (double)bet / Lines, MidpointRounding.AwayFromZero);
            var cells = new JsonArray();
            for (var c = 0; c < w.N; c++) cells.Add(new JsonArray(c, ln[c]));
            items.Add(new JsonObject { ["line"] = i, ["cells"] = cells, ["sym"] = w.Sym, ["amount"] = amount });
            win += amount;
            if (w.Sym == Wild && w.N == 3) glek3 = true;
        }
        // Стеля — недосяжна (найбільше 303,6× — чих на поле з двома лініями сімок і Глеків), але чесно обрізаємо.
        var capped = Math.Min(win, (long)Cap * bet);

        var steps = new JsonArray { new JsonObject { ["t"] = "spin", ["stops"] = new JsonArray(stops[0], stops[1], stops[2]), ["tease"] = tease } };
        if (morph.Count > 0) steps.Add(new JsonObject { ["t"] = "morph", ["cells"] = morph, ["why"] = "sneeze" });
        if (items.Count > 0) steps.Add(new JsonObject { ["t"] = "win", ["items"] = items, ["amount"] = capped });
        var script = new JsonObject
        {
            ["bet"] = bet,
            ["steps"] = steps,
            ["win"] = capped,
            ["state"] = new JsonObject(),
            ["glek3"] = glek3,
        };
        if (morph.Count > 0) script["sneeze"] = morph.Count;
        var flags = new List<string>();
        if (glek3) flags.Add("glek3");
        if (morph.Count > 0) flags.Add("sneeze");
        return new SlotOutcome(script, (int)capped, flags, new JsonObject());
    }

    public JsonObject Table()
    {
        var pay = new JsonObject();
        foreach (var (sym, p) in Pay)
        {
            var o = new JsonObject();
            foreach (var (n, k) in p.OrderByDescending(x => x.Key)) o[n.ToString()] = k;
            pay[sym] = o;
        }
        var reels = new JsonArray();
        foreach (var r in Reels) reels.Add(new JsonArray([.. r.Select(s => (JsonNode)JsonValue.Create(s)!)]));
        var lines = new JsonArray();
        foreach (var l in PayLines) lines.Add(new JsonArray(l[0], l[1], l[2]));
        var tot = SneezeWeights.Sum();
        var cells = new JsonObject();
        for (var i = 0; i < SneezeWeights.Length; i++) cells[(i + 1).ToString()] = Math.Round(100.0 * SneezeWeights[i] / tot, 1);
        return new JsonObject
        {
            ["reels"] = reels, ["lines"] = lines, ["pay"] = pay, ["payUnit"] = 1.0 / Lines, ["wild"] = Wild, ["cap"] = Cap,
            // ⓘ «Глек чхнув»: шанс на оберт (1 з oneIn), скільки клітинок (відсотки серед чихів), що робить
            ["sneeze"] = new JsonObject
            {
                ["oneIn"] = SneezeOdds,
                ["p"] = Math.Round(1.0 / SneezeOdds, 4),
                ["cells"] = cells,
                ["min"] = 1,
                ["max"] = SneezeWeights.Length,
                ["text"] = $"Після зупинки з шансом 1 з {SneezeOdds} Дядько Глек чхає 🤧 — 1–{SneezeWeights.Length} клітинки (не Глеки) стають дикими Глеками, і лінії рахуються вже на зміненому полі. Шанс завжди однаковий — не залежить ні від програшів, ні від гаманця.",
            },
        };
    }
}
