using System.Globalization;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Economy;

/// <summary>Місце людини в головоломці дня (місця з однаковим результатом — спільні).</summary>
public sealed record DailyPlace(string Nick, string NickKey, int Place, int Of);

/// <summary>
/// Нічний підсумок «Щоденного глека» (25.10.2026): по кожній головоломці вчорашнього дня — черепки «за кращий результат»
/// усім людям, що її розв'язали (за місцем: перший — <see cref="EconomyOptions.DailyBestMax"/>, останній — нуль, сам-один —
/// половина), і призи першим місцям (<see cref="EconomyOptions.DailyTopPrizes"/>), коли розв'язало щонайменше
/// <see cref="EconomyOptions.DailyTopMinSolvers"/>. Лише люди: акаунти без ботів.
///
/// Місце — за тим самим порядком, що й топ дня (спроби, тоді час), а головоломки з очками (<see cref="IDailyPoints"/>) —
/// за очками. Ключі леджера <c>daily-best:{гра}:{день}:{нік}</c> і <c>daily-top:{гра}:{день}:{нік}</c>: перезапуск чи
/// повторний прохід не платить двічі. Підбиває лише вчорашній день — старші дні не доплачує. Дошки очок дочитують базу
/// фоном: поки когось на них бракує, день не підбиваємо (до <see cref="PointsTries"/> спроб раз на хвилину, далі — як є).
/// </summary>
public sealed class DailyPrizes(Daily daily, EconomyStore store, Economy economy, GameNames names, IClock clock,
    IOptionsMonitor<EconomyOptions> opts, IOutbox outbox, ILogger<DailyPrizes> log) : BackgroundService
{
    static readonly TimeSpan Every = TimeSpan.FromMinutes(1);
    /// <summary>Скільки разів чекати на очки дня (дошки очок дочитують базу фоном), перш ніж підбити без них.</summary>
    public const int PointsTries = 5;
    string _settled = "";
    int _tries;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
        catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            var day = Yesterday();
            if (day != _settled)
            {
                try
                {
                    if (Settle(day, final: ++_tries >= PointsTries) >= 0) { _settled = day; _tries = 0; }
                }
                catch (Exception ex) { log.LogWarning(ex, "щоденний глек: не підбив {Day} — спробую за хвилину", day); }
            }
            try { await Task.Delay(Every, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    string Yesterday() =>
        DateOnly.ParseExact(Days.Today(clock), "yyyy-MM-dd").AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Місця людей у головоломці дня, найкращі спершу.</summary>
    public List<DailyPlace> Places(string day, string game) => Places(day, game, out _);

    /// <param name="missing">Чи бракує когось на дошці очок (дошка ще дочитує базу — тоді місця несправжні).</param>
    public List<DailyPlace> Places(string day, string game, out bool missing)
    {
        missing = false;
        var rows = store.DailySolvers(day, game);
        if (rows.Count == 0) return [];
        List<(DailyRow Row, long Key1, long Key2)> keyed;
        if (daily.HasPoints(game))
        {
            // більше очок — краще; без очок — у хвіст
            var pts = rows.Select(r => (r, daily.PointsOf(game, day, r.Nick))).ToList();
            missing = pts.Any(x => x.Item2 is null);
            keyed = [.. pts.Select(x => (x.r, -(x.Item2 ?? long.MinValue / 2), 0L)).OrderBy(x => x.Item2)];
        }
        else
            keyed = [.. rows.Select(r => (r, (long)r.Attempts, (long)r.Ms))];   // уже в порядку топу дня
        var list = new List<DailyPlace>(keyed.Count);
        for (var i = 0; i < keyed.Count; i++)
        {
            var place = i > 0 && keyed[i].Key1 == keyed[i - 1].Key1 && keyed[i].Key2 == keyed[i - 1].Key2 ? list[i - 1].Place : i + 1;
            list.Add(new DailyPlace(keyed[i].Row.Nick, keyed[i].Row.NickKey, place, keyed.Count));
        }
        return list;
    }

    /// <summary>Скільки «за кращий результат» за місце <paramref name="place"/> з <paramref name="of"/> (уже × Boost).</summary>
    public static int Best(int max, int place, int of) =>
        max <= 0 || of <= 0 ? 0
        : of == 1 ? (int)Math.Round(max / 2.0, MidpointRounding.AwayFromZero)
        : (int)Math.Round(max * (of - place) / (double)(of - 1), MidpointRounding.AwayFromZero);

    /// <summary>
    /// Підбити день: виплати й рядок у Журнал. Повертає, скільки черепків роздано цим проходом, або −1 — нічого не
    /// платив, бо на дошці очок ще когось бракує (не <paramref name="final"/>: спробуй згодом).
    /// </summary>
    public int Settle(string day, bool final = true)
    {
        var o = opts.CurrentValue;
        var bestMax = o.Earn(Math.Max(0, o.DailyBestMax));
        var top = (o.DailyTopPrizes ?? []).Select(p => o.Earn(Math.Max(0, p))).ToArray();
        if (bestMax <= 0 && top.All(p => p <= 0)) return 0;
        var date = DateOnly.ParseExact(day, "yyyy-MM-dd").ToString("dd.MM", CultureInfo.InvariantCulture);
        var all = new List<(string Game, List<DailyPlace> Places)>();
        foreach (var game in daily.Puzzles)
        {
            var places = Places(day, game, out var missing);
            if (missing && !final) return -1;
            if (places.Count > 0) all.Add((game, places));
        }
        var given = 0;
        var firsts = new List<string>();
        foreach (var (game, places) in all)
        {
            var title = names.Title(game);
            var withTop = places.Count >= Math.Max(1, o.DailyTopMinSolvers);
            var before = given;
            foreach (var p in places)
            {
                var best = Best(bestMax, p.Place, p.Of);
                if (best > 0 && economy.Grant(p.Nick, best, $"daily-best:{game}", $"daily-best:{game}:{day}:{p.NickKey}",
                        $"+{best} {Economy.Shards(best)}: «{title}» за {date} — {p.Place} місце з {p.Of}") == GrantResult.Applied)
                    given += best;
                var prize = withTop && p.Place <= top.Length ? top[p.Place - 1] : 0;
                if (prize > 0 && economy.Grant(p.Nick, prize, $"daily-top:{game}", $"daily-top:{game}:{day}:{p.NickKey}",
                        $"+{prize} {Economy.Shards(prize)}: «{title}» за {date} — приз за {p.Place} місце") == GrantResult.Applied)
                    given += prize;
            }
            if (withTop && given > before)
                firsts.Add($"{title} — {string.Join(", ", places.Where(p => p.Place == 1).Select(p => p.Nick))}");
        }
        if (firsts.Count > 0)
            outbox.Post(new Journal($"☀ Щоденний глек за {date}: перші — {string.Join(" · ", firsts)}"));
        if (given > 0) log.LogInformation("щоденний глек: {Day} підбито, роздано {Shards}", day, given);
        return given;
    }
}
