namespace Hlechyky.Bets;

// =====================================================================================================================
// «🎲 Ставки» (09.10.2026, контракт D:/or-wt/_tools/bets-contract.md). Банкує Дядько Глек: кеф фіксується в момент
// ставки, зіграло — Глек платить «ставка × кеф» (черепки беруться нізвідки), ні — ставка згорає. Два джерела: події
// (додає й розраховує адмін, найчастіше з Polymarket) і ставки на столах (розраховуються самі після партії). Сайт
// опенсорсний, тож усе вмикається в конфігу (секція Bets, наживо) — і відповідальність за «казино» на тому, хто вмикає.
// =====================================================================================================================

/// <summary>Секція <c>Bets</c> конфігу. Читається наживо (IOptionsMonitor), як ShardShop.</summary>
public sealed class BetsOptions
{
    /// <summary>Увесь розділ: вимкнено — ні подій, ні ставок на столах (розрахувати й скасувати вже зроблене адмін може).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Події — сторінка «🎲 Ставки».</summary>
    public bool Events { get; set; } = true;
    /// <summary>Ставки на столах перед партією.</summary>
    public bool Tables { get; set; } = true;
    /// <summary>Маржа Глека: кеф = (1 − Margin) / p.</summary>
    public double Margin { get; set; } = 0.08;
    /// <summary>Стеля на одну людину в одній події (сума всіх її ставок); 0 — без стелі.</summary>
    public int MaxEventBet { get; set; } = 2000;
    /// <summary>Стеля на одну людину на одну партію столу; 0 — без стелі.</summary>
    public int MaxTableBet { get; set; } = 500;
    public int MinBet { get; set; } = 1;
    public double MinOdds { get; set; } = 1.01;
    public double MaxOdds { get; set; } = 100;
    public PolymarketOptions Polymarket { get; set; } = new();
    /// <summary>Скільки пропозицій однієї людини водночас чекають адміна.</summary>
    public int SuggestPending { get; set; } = 5;

    public bool EventsOn => Enabled && Events;
    public bool TablesOn => Enabled && Tables;

    /// <summary>Межі кефа без дурниць у конфігу: нижня — не менше 1,01 (інакше «виграш» нічого не дає), верхня — не нижче нижньої.</summary>
    public double MinOddsOk => Math.Max(1.01, Math.Round(MinOdds, 2));
    public double MaxOddsOk => Math.Max(MinOddsOk, Math.Round(MaxOdds, 2));
    public int MinBetOk => Math.Max(1, MinBet);
    public double MarginOk => Math.Clamp(Margin, 0, 0.9);

    /// <summary>Стеля на людину для джерела; 0 — без стелі.</summary>
    public int MaxFor(string source) => Math.Max(0, source == BetSources.Table ? MaxTableBet : MaxEventBet);
}

public sealed class PolymarketOptions
{
    public string BaseUrl { get; set; } = "https://gamma-api.polymarket.com";
    public int CacheSeconds { get; set; } = 90;
    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>Джерела ставок — одна таблиця на обидва, щоб сальдо Глека рахувалось одним запитом.</summary>
public static class BetSources
{
    public const string Event = "event";
    public const string Table = "table";

    public static bool Known(string? s) => s is Event or Table;
}

/// <summary>Кефи: одна функція на події й столи, щоб маржа й межі ніде не розійшлись.</summary>
public static class BetMath
{
    /// <summary>
    /// Кеф з імовірності: (1 − Margin) / p, 2 знаки, у межах [MinOdds, MaxOdds]. Нульова чи дурна p — верхня межа: «світ
    /// думає 0%» не мусить давати нескінченний кеф.
    /// </summary>
    public static double Odds(double p, BetsOptions o)
    {
        if (double.IsNaN(p) || p <= 0) return o.MaxOddsOk;
        return Clamp((1 - o.MarginOk) / p, o);
    }

    /// <summary>Кеф, який вписав адмін чи порахував стіл, — до 2 знаків і в межі.</summary>
    public static double Clamp(double odds, BetsOptions o) =>
        double.IsNaN(odds) ? o.MinOddsOk : Math.Clamp(Math.Round(odds, 2, MidpointRounding.AwayFromZero), o.MinOddsOk, o.MaxOddsOk);

    /// <summary>Чи кеф уже в межах і з 2 знаками (саме такий зберігається на ставці).</summary>
    public static bool Fits(double odds, BetsOptions o) =>
        !double.IsNaN(odds) && odds >= o.MinOddsOk - 1e-9 && odds <= o.MaxOddsOk + 1e-9 && Math.Abs(Math.Round(odds, 2) - odds) < 1e-9;

    /// <summary>
    /// Виплата за виграну ставку: ставка × кеф униз до цілого черепка. Униз — на користь Глека, але не менше самої ставки
    /// (кеф ≥ 1,01, тож так і виходить).
    /// </summary>
    public static int Payout(int stake, double odds) => (int)Math.Min(int.MaxValue, Math.Max(stake, Math.Floor(stake * odds + 1e-9)));

    /// <summary>Однаковий вигляд кефа для текстів: «×2,4», «×1,05».</summary>
    public static string Show(double odds) => "×" + odds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
}
