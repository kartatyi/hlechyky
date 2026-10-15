using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Economy;

/// <summary>
/// Разовий подарунок-перерахунок після перебудови економіки (15.10.2026: <see cref="EconomyOptions.Boost"/> ×10, донат і
/// вивід вимикаються). Кожному акаунту (не гостю й не боту) — дві виплати:
/// <list type="bullet">
/// <item>доплата за всі вже здобуті ачівки до ціни × Boost: «скільки коштують зараз» мінус «скільки за них уже прийшло»
///   (самі <c>ach:*</c> і минулі перерахунки) — тож новий ярлик доплачує лише різницю, а знижений Boost нічого не забирає;</item>
/// <item>рівний подарунок <see cref="EconomyOptions.RegiftShards"/>.</item>
/// </list>
/// Ярлик <see cref="EconomyOptions.RegiftId"/> — у ключах леджера (<c>regift-ach:{ярлик}:{нік}</c>, <c>regift:{ярлик}:{нік}</c>),
/// тож кожен старт сервера проходить список наново, а платить лише тим, кому за цим ярликом ще не платив (і акаунтам,
/// що з'явились після минулого старту, — теж). Порожній ярлик — нічого.
/// </summary>
public sealed class Regift(EconomyStore store, Economy economy, IOptionsMonitor<EconomyOptions> opts, IOutbox outbox,
    ILogger<Regift> log) : BackgroundService
{
    /// <summary>Кому що прийшло цим проходом.</summary>
    public sealed record Line(string Nick, int Achievements, int Gift);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // не в саму мить старту: хай сервер спершу підніметься й почне віддавати сторінки
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); }
        catch (OperationCanceledException) { return; }
        try { Run(); }
        catch (Exception ex) { log.LogWarning(ex, "подарунок-перерахунок спіткнувся — спробую на наступному старті"); }
    }

    /// <summary>Пройти всіх людей і доплатити, що ще не доплачено за цим ярликом.</summary>
    public IReadOnlyList<Line> Run()
    {
        var o = opts.CurrentValue;
        var id = (o.RegiftId ?? "").Trim();
        if (id.Length == 0) return [];
        var lines = new List<Line>();
        foreach (var (key, nick) in store.People())
        {
            // виняток на одному ніку не лишає решту без подарунка
            try
            {
                var ach = 0;
                var worth = store.AchievementsOf(key)
                    .Select(u => AchievementCatalog.Get(u.Key))
                    .Sum(a => a is null ? 0L : economy.Earn(a.Reward));
                var due = (int)Math.Clamp(worth - store.PaidForAchievements(key), 0, int.MaxValue);
                if (due > 0 && economy.Grant(nick, due, $"regift-ach:{id}", $"regift-ach:{id}:{key}",
                        $"+{due} {Economy.Shards(due)}: Дядько Глек перерахував твої ачівки за новим курсом") == GrantResult.Applied)
                    ach = due;

                var gift = 0;
                if (o.RegiftShards > 0 && economy.Grant(nick, o.RegiftShards, $"regift:{id}", $"regift:{id}:{key}",
                        $"+{o.RegiftShards} {Economy.Shards(o.RegiftShards)}: подарунок від Дядька Глека") == GrantResult.Applied)
                    gift = o.RegiftShards;

                if (ach > 0 || gift > 0) lines.Add(new Line(nick, ach, gift));
            }
            catch (Exception ex) { log.LogWarning(ex, "подарунок-перерахунок для {Nick} не пройшов", nick); }
        }
        if (lines.Count > 0)
        {
            log.LogInformation("подарунок-перерахунок «{Id}»: {N} людям, ачівки +{Ach}, подарунки +{Gift}",
                id, lines.Count, lines.Sum(l => (long)l.Achievements), lines.Sum(l => (long)l.Gift));
            try
            {
                outbox.Post(new Journal($"🎁 Дядько Глек перебудував економіку: за все тепер ×{Math.Max(1, o.Boost)} черепків, " +
                    $"ачівки перераховано за новим курсом, а кожному ще й подарунок ({lines.Count} {People(lines.Count)})"));
            }
            catch (Exception ex) { log.LogWarning(ex, "не розповів про подарунок-перерахунок"); }
        }
        return lines;
    }

    static string People(int n)
    {
        var abs = Math.Abs(n);
        if (abs % 100 is >= 11 and <= 14) return "людей";
        return (abs % 10) switch { 1 => "людина", 2 or 3 or 4 => "людини", _ => "людей" };
    }
}
