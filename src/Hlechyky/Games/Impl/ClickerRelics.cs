using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Скарбниця роду (одинадцяте оновлення, docs/games/specs/clicker-v11.md §4). Клеймам лідерів більше нема куди йти:
/// усі секрети куплені, а обпал дає відсотки. Реліквії — рівні за клейма без стелі, кожен утричі дорожчий, і кожна
/// посилює одну річ (полицю, горно, гостей, толоку), а не «все». Петля «клейма → глеки → клейма» згасає сама:
/// рівень коштує ×3 клейм, тобто ×9 глеків за весь час, а дає лінійну надбавку.
/// </summary>
public sealed partial class Clicker
{
    /// <summary>У скільки разів дорожчий кожен наступний рівень реліквії.</summary>
    public const double RelicGrowth = 3;
    /// <summary>Стеля рівня в збереженні — лише щоб правлена база не дала «мільйон рівнів»: ціна туди однаково не дотягне.</summary>
    public const int RelicMaxLevel = 60;
    /// <summary>Скарбницю видно, коли куплено всі секрети або стільки вільних клейм уже лежить.</summary>
    public const long RelicShowFrom = 1_000_000;
    /// <summary>Стеля ціни — щоб сума з рештою витрат лишалась у <c>long</c>.</summary>
    const long RelicPriceCap = long.MaxValue / 8;

    /// <summary>Вісім реліквій. Step — частка за рівень, Cap — межа суми (0 — без межі).</summary>
    public static readonly ClickerRelic[] Relics =
    [
        new("basket3", "Батьків кошик", "Глек з полиці +10 % за рівень", 100_000, 0.10),
        new("cat3", "Родинний кіт", "Глеки з полиці падають частіше: −3 % чекання за рівень (до −40 %)", 300_000, 0.03, 0.40),
        new("fiddle", "Дідова скрипка", "Бонуси розписних глеків тривають +4 % за рівень (до ×2)", 200_000, 0.04, 1.0),
        new("towel", "Бабусин рушник", "Сирець сохне на 5 % швидше за рівень (не швидше 20 с)", 50_000, 0.05, 0.78),
        new("ember3", "Родовий жар", "Блиск партії +3 % за рівень (до +60 %)", 150_000, 0.03, 0.60),
        new("seal2", "Прадідова печатка", "Заморські гості платять +10 % і дають +5 % шани за рівень", 500_000, 0.10),
        new("hands", "Родинні руки", "Підмайстри ліплять +5 % за рівень (до ×2)", 100_000, 0.05, 1.0),
        new("toloka", "Родова толока", "Будова Толоки на 3 % швидша за рівень (до −30 %)", 1_000_000, 0.03, 0.30),
    ];

    readonly Dictionary<string, int> _relics = new(StringComparer.Ordinal);

    int RelicLevel(string key) => _relics.TryGetValue(key, out var n) ? n : 0;

    /// <summary>Сума важеля реліквії: рівень × крок, не більше межі.</summary>
    double Relic(string key)
    {
        var n = RelicLevel(key);
        if (n <= 0) return 0;
        var r = Relics.First(x => x.Key == key);
        var sum = r.Step * n;
        return r.Cap > 0 ? Math.Min(r.Cap, sum) : sum;
    }

    /// <summary>Множник чекання від «кота»-реліквії: 1 − сума, але не менше 0,6.</summary>
    double RelicWaitMult(string key) => 1 - Relic(key);

    /// <summary>Ціна наступного рівня реліквії: <c>Base·3^L</c>, зі стелею в <c>long</c>.</summary>
    public static long RelicPrice(ClickerRelic r, int level)
    {
        var p = r.Base * Math.Pow(RelicGrowth, Math.Max(0, level));
        return !double.IsFinite(p) || p >= RelicPriceCap ? RelicPriceCap : (long)Math.Ceiling(p);
    }

    bool RelicsOpen => Secrets.All(s => _secrets.Contains(s.Key)) || FreeStamps >= RelicShowFrom || _relics.Count > 0;

    ActResult BuyRelic(JsonElement payload)
    {
        if (Relics.FirstOrDefault(r => r.Key == Str(payload, "key")) is not { } relic)
            return ActResult.Fail("Такої реліквії в роду нема");
        if (!RelicsOpen) return ActResult.Fail($"Скарбниця відкриється, коли знатимеш усі секрети роду або матимеш {Count(RelicShowFrom)} вільних клейм");
        var level = RelicLevel(relic.Key);
        if (level >= RelicMaxLevel) return ActResult.Fail($"«{relic.Name}»: вище вже не буває");
        if (SpendStamps(RelicPrice(relic, level)) is { } fail) return fail;
        _relics[relic.Key] = level + 1;
        if (_relics.Count == 1 && level == 0) Achieve("potter-relic");
        if (_relics.Values.Sum() >= RelicsForAchievement) Achieve("potter-relic-50");
        return ActResult.Accept($"🗝 {relic.Name} — рівень {level + 1}");
    }

    /// <summary>Скільки рівнів реліквій разом — на ачівку «Скарбник роду».</summary>
    public const int RelicsForAchievement = 50;

    /// <summary>Вид скарбниці: null, поки не відкрилась; інакше рівень, ціна наступного й чи вистачає клейм.</summary>
    object? RelicsView()
    {
        if (!RelicsOpen) return null;
        var free = FreeStamps;
        return Relics.Select(r =>
        {
            var level = RelicLevel(r.Key);
            var price = RelicPrice(r, level);
            return new { key = r.Key, level, price, sum = Relic(r.Key), can = free >= price && level < RelicMaxLevel };
        }).ToList();
    }
}
