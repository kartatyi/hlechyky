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
/// Автомат, у якому можна купити бонус одразу (вимикач <c>Slots:BuyBonus</c>, ціна — <c>Slots:BuyPrice</c> ставок).
/// Гроші, Скарбничку й вид веде база <see cref="SlotGame"/> (дія <c>buy</c>).
/// </summary>
public interface ISlotBuyBonus
{
    /// <summary>Найменша ціна в ставках, з якою RTP купленого бонусу не вищий за RTP бази (з симуляції).</summary>
    int BuyMinPrice { get; }

    /// <summary>Сценарій купленого бонусу (уже зі стелею), без базового оберту. Ціну списує гра.</summary>
    SlotOutcome Buy(int bet, ISlotRng rng);
}

/// <summary>
/// «Однорукий Глек»: 3 барабани × 3 рядки, 5 ліній, стрічки по 32 (ті самі, що в моку slot-glek.js), Глек — дикий.
/// Платить однаковий ряд зліва направо від першого барабана; на лінії — один виграш. Виплати — у ставках на лінію
/// (ставка ÷ 5). Таблицю підібрано під RTP бази 95,50 % (точно, перебором усіх 32³ зупинок), виграш у 38,4 % обертів.
/// </summary>
public sealed class SlotGlekMath : ISlotMath
{
    public const string Wild = "glek";
    public const int Lines = 5;

    // Стрічки барабанів (по 32): вишні 7, груша 6, слива 6, кавун 4, дзвоник 3, підкова 3, сімка 2 (поруч), Глек 1.
    // Дослівно з моку — клієнт крутить ті самі стрічки, тож символи над і під лінією чесні.
    public static readonly string[][] Reels =
    [
        "cherry pear seven seven plum cherry melon bell pear cherry plum horseshoe cherry pear melon plum glek cherry bell pear plum horseshoe melon cherry pear plum bell cherry melon horseshoe pear plum".Split(' '),
        "plum cherry bell pear seven seven cherry melon plum horseshoe pear cherry glek plum melon cherry pear bell plum cherry horseshoe pear melon plum cherry bell pear melon cherry plum horseshoe pear".Split(' '),
        "pear melon cherry plum horseshoe cherry pear glek bell plum cherry seven seven pear melon cherry plum bell horseshoe pear cherry plum melon pear cherry bell plum horseshoe cherry melon pear plum".Split(' '),
    ];

    /// <summary>Лінії: рядок кожного барабана. 0 — середня, 1 — верхня, 2 — нижня, 3 і 4 — діагоналі.</summary>
    public static readonly int[][] PayLines = [[1, 1, 1], [0, 0, 0], [2, 2, 2], [0, 1, 2], [2, 1, 0]];

    /// <summary>Виплати в ставках на лінію: символ → (скільки в ряд → множник). Мок мав 600/120/60/40/25/12/10/8+1 (RTP 80 %).</summary>
    public static readonly Dictionary<string, Dictionary<int, int>> Pay = new(StringComparer.Ordinal)
    {
        ["glek"] = new() { [3] = 600 },
        ["seven"] = new() { [3] = 150 },
        ["horseshoe"] = new() { [3] = 75 },
        ["bell"] = new() { [3] = 45 },
        ["melon"] = new() { [3] = 30 },
        ["plum"] = new() { [3] = 14 },
        ["pear"] = new() { [3] = 12 },
        ["cherry"] = new() { [3] = 10, [2] = 1 },
    };

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

    /// <summary>Сума виплат по всіх лініях у ставках на лінію (для точного RTP перебором).</summary>
    public static int Units(int[] stops)
    {
        var u = 0;
        foreach (var ln in PayLines)
            if (Line(At(stops, 0, ln[0]), At(stops, 1, ln[1]), At(stops, 2, ln[2])) is { } w) u += w.Pay;
        return u;
    }

    public SlotOutcome Spin(int bet, ISlotRng rng, JsonObject state)
    {
        int[] stops = [rng.Next(Reels[0].Length), rng.Next(Reels[1].Length), rng.Next(Reels[2].Length)];
        return ScriptFor(stops, bet, state);
    }

    /// <summary>Сценарій для заданих зупинок — поле за полем як scriptFor у моку.</summary>
    public SlotOutcome ScriptFor(int[] stops, int bet, JsonObject state)
    {
        var items = new JsonArray();
        long win = 0;
        var glek3 = false;
        for (var i = 0; i < PayLines.Length; i++)
        {
            var ln = PayLines[i];
            if (Line(At(stops, 0, ln[0]), At(stops, 1, ln[1]), At(stops, 2, ln[2])) is not { } w) continue;
            // як Math.round(u × ставка ÷ 5) у моку; для ставок, кратних 5, — рівно
            var amount = (long)Math.Round(w.Pay * (double)bet / Lines, MidpointRounding.AwayFromZero);
            var cells = new JsonArray();
            for (var c = 0; c < w.N; c++) cells.Add(new JsonArray(c, ln[c]));
            items.Add(new JsonObject { ["line"] = i, ["cells"] = cells, ["sym"] = w.Sym, ["amount"] = amount });
            win += amount;
            if (w.Sym == Wild && w.N == 3) glek3 = true;
        }
        // Стеля — недосяжна для цих стрічок (найбільше 122,8×), але чесно обрізаємо, якщо таблицю колись піднімуть.
        var capped = Math.Min(win, (long)Cap * bet);

        // Очікування — лише чесно: на якійсь лінії перші два вже «сімка/Глек», і третій барабан справді вирішує.
        static bool Big(string k) => k is Wild or "seven";
        var tease = new JsonArray();
        if (PayLines.Any(ln => Big(At(stops, 0, ln[0])) && Big(At(stops, 1, ln[1])))) tease.Add(2);

        var steps = new JsonArray { new JsonObject { ["t"] = "spin", ["stops"] = new JsonArray(stops[0], stops[1], stops[2]), ["tease"] = tease } };
        if (items.Count > 0) steps.Add(new JsonObject { ["t"] = "win", ["items"] = items, ["amount"] = capped });
        var script = new JsonObject
        {
            ["bet"] = bet,
            ["steps"] = steps,
            ["win"] = capped,
            ["state"] = new JsonObject(),
            ["glek3"] = glek3,
        };
        return new SlotOutcome(script, (int)capped, glek3 ? ["glek3"] : [], new JsonObject());
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
        return new JsonObject { ["reels"] = reels, ["lines"] = lines, ["pay"] = pay, ["payUnit"] = 1.0 / Lines, ["wild"] = Wild, ["cap"] = Cap };
    }
}
