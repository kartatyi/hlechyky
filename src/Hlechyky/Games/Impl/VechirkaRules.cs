namespace Hlechyky.Games.Impl;

/// <summary>Предмет вечірки (§6.1): ключ, іконка, назва, ціна (0 — не продається), чи треба ціль.</summary>
public sealed record VechirkaItem(string Key, string Icon, string Name, int Price, string Aim);

/// <summary>Подія Глека (§5): ключ і вага в колоді.</summary>
public sealed record VechirkaEvent(string Key, int Weight, string Title);

/// <summary>Бонусна номінація (§9).</summary>
public sealed record VechirkaBonus(string Key, string Title);

/// <summary>
/// Константи й чисті таблиці вечірки (§4–§9). Числа — з <c>sim.py</c> (§7.1): змінив тут — зміни й там.
/// </summary>
public static class VechirkaRules
{
    public const int StartCoins = 10;
    public const int GlekPrice = 20, SalePrice = 15;
    public const int Double = 2;
    public const int BankCap = 15, BankPass = 2;
    public const int Church = 5, StartLand = 3;
    public const int ChestEmptyPct = 15, ChestEmptyCoins = 3;
    public const int GateToll = 5;
    public const int PanCoins = 3, PanRange = 3, ForkCoins = 7, GateRange = 8;
    public const int DuelHonor = 3, DuelSplit = 5, BetWin = 2;
    public const int LastMgMult = 2, LateMult = 2;
    public const int LateGiftCoins = 15;
    public const int Hand = 3;
    public const int StandMinDist = 8;
    public const int NoGames = 5;
    public const int Bonuses = 3;
    public const int MinRounds = 6, MaxRounds = 20;
    public static readonly int[] Stakes = [0, 5, 10, 20];

    // ---------- таймери (мс, §1.1, §2.3, §15.3) ----------
    public const int IntroMs = 6000, OrderMs = 3000, LateMs = 12000, TurnMs = 20000, AimMs = 12000;
    public const int PickMs = 8000, RouletteMs = 1500, CardMs = 10000, CardRepeatMs = 5000, ResultsMs = 6000;
    public const int BonusMs = 6000, SummaryMs = 8000;
    public const int DiceMs = 1400, StepMs = 260, WalkTailMs = 300, LandMs = 1200, EventMs = 2500, EndTurnMs = 600;
    public const int BuyMs = 2000, ItemMs = 1200, WheelMs = 2000;
    public const int BotMinMs = 800, BotMaxMs = 1600, AwayMs = 1500;
    public const int PauseMaxMs = 10 * 60_000;

    public static int PromptMs(string kind) => kind switch
    {
        "shop" => 8000,
        "duelWho" => 12000,
        "late" => LateMs,
        _ => 10000,
    };

    public static readonly VechirkaItem[] Items =
    [
        new("pan", "🍳", "Пательня", 5, "player"),
        new("horse", "🐴", "Підкова", 5, ""),
        new("pick", "🎯", "Вибирайко", 7, "number"),
        new("fork", "🔱", "Вила", 8, "player"),
        new("gate", "🚧", "Шлагбаум", 4, "node"),
        new("key", "🗝", "Ключ", 3, ""),
        new("rope", "🪢", "Аркан", 6, "player"),
        new("charm", "🧿", "Оберіг", 6, ""),
        new("pumpkin", "🎃", "Гарбуз", 0, "player"),
        new("feather", "🪶", "Перо лелеки", 0, ""),
    ];

    public static readonly Dictionary<string, VechirkaItem> Item = Items.ToDictionary(i => i.Key);

    /// <summary>Що продають крамниці (§6.3): усе, крім гарбуза й пера.</summary>
    public static readonly string[] Buyable = [.. Items.Where(i => i.Price > 0).Select(i => i.Key)];

    /// <summary>Ваги скрині (§6.2).</summary>
    public static readonly (string Key, int W)[] ChestWeights =
    [
        ("pan", 14), ("horse", 16), ("pick", 10), ("fork", 10), ("gate", 10),
        ("key", 10), ("rope", 8), ("charm", 10), ("pumpkin", 6), ("feather", 3),
    ];

    /// <summary>Шлагбаум можна лише на ці клітинки (§6.1).</summary>
    public static readonly HashSet<string> GateTypes = ["coin", "trap", "chest", "event", "duel", "church"];

    /// <summary>Пакості, від яких рятує оберіг (§6.1) — для реплік і статистики.</summary>
    public static readonly HashSet<string> Nasty = ["pan", "fork", "rope", "pumpkin"];

    public static readonly VechirkaEvent[] Events =
    [
        new("fair", 10, "Ярмарок у селі"),
        new("swap", 3, "Глек напився"),
        new("rain", 8, "Злива"),
        new("gift", 10, "Баба передала гостинця"),
        new("wind", 8, "Вітер у спину"),
        new("dog", 7, "Бровко"),
        new("wedding", 7, "Весілля"),
        new("wheel", 10, "Колесо фортуни"),
        new("poor", 8, "Глек жаліє"),
        new("move", 6, "Лавка переїжджає"),
        new("sale", 5, "Розпродаж"),
        new("tax", 6, "Податок пана"),
    ];

    public static readonly VechirkaBonus[] BonusList =
    [
        new("mgwins", "Ярмарковий король"),
        new("earned", "Багатій"),
        new("steps", "Мандрівник"),
        new("bully", "Задирака"),
        new("traps", "Невдаха вечора"),
        new("shop", "Покупець"),
        new("chests", "Скарбошукач"),
    ];

    public static readonly string[] BotNames = ["🤖 Галя", "🤖 Грицько", "🤖 Мирон", "🤖 Одарка", "🤖 Панас", "🤖 Христя", "🤖 Тарас", "🤖 Явдоха"];

    public static readonly string[] Emo = ["clap", "laugh", "wow", "angry", "party"];

    /// <summary>Виплати міні-гри за місце (§7.1) для N гравців: [1-ше, 2-ге, …].</summary>
    public static int[] Payouts(int n) => n switch
    {
        <= 1 => [10],
        2 => [10, 3],
        3 => [10, 5, 2],
        4 => [10, 6, 3, 1],
        5 => [10, 7, 5, 3, 1],
        6 => [10, 8, 6, 4, 2, 1],
        7 => [10, 8, 6, 5, 3, 2, 1],
        _ => [10, 8, 6, 5, 4, 3, 2, 1],
    };

    /// <summary>
    /// Шеляги за місця з поділом (§7.1): рівні ділять суму своїх місць порівну, угору. places — 1 = найкраще,
    /// рівні мають однакове число (як <see cref="MinigameResult.Places"/>).
    /// </summary>
    public static int[] Pay(int[] places)
    {
        var n = places.Length;
        var table = Payouts(n);
        var res = new int[n];
        for (var i = 0; i < n; i++)
        {
            var p = places[i];
            var same = places.Count(x => x == p);
            var sum = 0;
            for (var k = p - 1; k < p - 1 + same && k < table.Length; k++) sum += table[k];
            res[i] = (sum + same - 1) / same;
        }
        return res;
    }

    /// <summary>Кола з цільових хвилин (§7.3).</summary>
    public static int Rounds(int minutes, int n)
    {
        var r = Math.Round((minutes - 2) / (1.55 + 0.33 * n), MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(r, MinRounds, MaxRounds);
    }

    /// <summary>Підсумкові scores вечірки (§9): глеки → шеляги → перемоги.</summary>
    public static long Score(int gleks, int coins, int mgWins) => gleks * 1_000_000L + Math.Min(coins, 9_999) * 100L + Math.Min(mgWins, 99);
}
