using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hlechyky.Games.Economy;

/// <summary>Чесне сальдо азарту: скільки поставив і скільки повернув — сьогодні (київська доба) і за весь час.</summary>
public readonly record struct Saldo(long DayBet, long DayWin, long AllBet, long AllWin)
{
    public long DayNet => DayWin - DayBet;
    public long AllNet => AllWin - AllBet;

    /// <summary>Для виду: <c>{day: {bet, win}, all: {bet, win}}</c>.</summary>
    public object Wire() => new { day = new { bet = DayBet, win = DayWin }, all = new { bet = AllBet, win = AllWin } };
}

/// <summary>Сальдо азарту з пам'яті — можна питати й під замком кімнати.</summary>
public interface IAzartSaldo
{
    /// <summary><paramref name="kind"/>: <c>slot</c> — усі автомати разом (з Ворожкою, купленим бонусом і Скарбничкою), <c>lelka</c>.</summary>
    Saldo Get(string kind, string? nick);
}

/// <summary>
/// Сальдо азарту (пакет 5 «Азарт чесно», 20.10): леджер — істина, тут лише суми в пам'яті. На старті сервера одним
/// запитом підсумовує леджер (весь час і від київської півночі), далі дописує кожен рух черепків, про який каже
/// <see cref="Economy.Moved"/>. Поставив — списання з причинами <see cref="Heads"/>, повернув — нарахування з ними ж.
/// Призи турнірів, подарунки й доплати сюди не входять: це не повернута ставка.
/// </summary>
public sealed class AzartSaldo(Db? db, Economy? economy, IClock clock, ILogger<AzartSaldo>? log = null) : IAzartSaldo, IHostedService
{
    /// <summary>Голова причини (до першої «:») → вид азарту.</summary>
    public static readonly IReadOnlyDictionary<string, string> Heads = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["slot-bet"] = "slot", ["slot-buy"] = "slot", ["slot-win"] = "slot", ["slot-jackpot"] = "slot",
        ["lelka-bet"] = "lelka", ["lelka-win"] = "lelka", ["lelka-back"] = "lelka",
    };

    sealed class Acc
    {
        public string Day = "";
        public long DayBet, DayWin, AllBet, AllWin;
    }

    readonly object _gate = new();
    readonly Dictionary<(string Kind, string Key), Acc> _map = [];

    /// <summary>Вид азарту за причиною руху (<c>slot-win:slot-hold</c> → <c>slot</c>) або null.</summary>
    public static string? KindOf(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return null;
        var i = reason.IndexOf(':');
        return Heads.TryGetValue(i < 0 ? reason : reason[..i], out var k) ? k : null;
    }

    public Saldo Get(string kind, string? nick)
    {
        var key = EconomyStore.Key(nick);
        if (key.Length == 0) return default;
        var today = Days.Today(clock);
        lock (_gate)
        {
            if (!_map.TryGetValue((kind, key), out var a)) return default;
            return a.Day == today ? new Saldo(a.DayBet, a.DayWin, a.AllBet, a.AllWin) : new Saldo(0, 0, a.AllBet, a.AllWin);
        }
    }

    /// <summary>Рух черепків (уже закомічений). Не наш вид — нічого.</summary>
    public void Note(string nick, int delta, string reason)
    {
        if (delta == 0 || KindOf(reason) is not { } kind) return;
        var key = EconomyStore.Key(nick);
        if (key.Length == 0) return;
        var today = Days.Today(clock);
        lock (_gate) Add(Of(kind, key), today, delta < 0 ? -delta : 0, delta > 0 ? delta : 0, delta < 0 ? -delta : 0, delta > 0 ? delta : 0);
    }

    Acc Of(string kind, string key)
    {
        if (!_map.TryGetValue((kind, key), out var a)) _map[(kind, key)] = a = new Acc();
        return a;
    }

    static void Add(Acc a, string today, long dayBet, long dayWin, long allBet, long allWin)
    {
        if (a.Day != today) { a.Day = today; a.DayBet = 0; a.DayWin = 0; }
        a.DayBet += dayBet; a.DayWin += dayWin; a.AllBet += allBet; a.AllWin += allWin;
    }

    /// <summary>
    /// Підсумувати леджер заново. Пам'ять чиститься перед читанням: рухи, що прийдуть під час запиту, ляжуть поверх
    /// (подвоїтись може лише рух, закомічений у ту саму мілісекунду, — на старті сервера їх нема).
    /// </summary>
    public void Load()
    {
        if (db is null) return;
        var now = clock.UtcNow;
        var today = Days.Of(now);
        var from = DayStartUtc(now).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss");
        lock (_gate) _map.Clear();
        var heads = string.Join(",", Heads.Keys.Select((_, i) => "$h" + i));
        var rows = db.With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"""
                SELECT nick_key, substr(reason, 1, instr(reason || ':', ':') - 1) AS head,
                       SUM(CASE WHEN delta < 0 THEN -delta ELSE 0 END),
                       SUM(CASE WHEN delta > 0 THEN delta ELSE 0 END),
                       SUM(CASE WHEN delta < 0 AND created_at >= $from THEN -delta ELSE 0 END),
                       SUM(CASE WHEN delta > 0 AND created_at >= $from THEN delta ELSE 0 END)
                FROM ledger
                WHERE reason LIKE 'slot-%' OR reason LIKE 'lelka-%'
                GROUP BY nick_key, head
                HAVING head IN ({heads})
                """;
            cmd.Parameters.AddWithValue("$from", from);
            var i = 0;
            foreach (var h in Heads.Keys) cmd.Parameters.AddWithValue("$h" + i++, h);
            using var r = cmd.ExecuteReader();
            var list = new List<(string Key, string Head, long AllBet, long AllWin, long DayBet, long DayWin)>();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)));
            return list;
        });
        lock (_gate)
            foreach (var x in rows)
                Add(Of(Heads[x.Head], x.Key), today, x.DayBet, x.DayWin, x.AllBet, x.AllWin);
    }

    /// <summary>Київська північ дня, у якому <paramref name="now"/>.</summary>
    public static DateTimeOffset DayStartUtc(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Days.Kyiv);
        var midnight = new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(midnight, Days.Kyiv.GetUtcOffset(midnight));
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (economy is not null) economy.Moved += Note;
        try { Load(); }
        catch (Exception ex) { log?.LogWarning(ex, "сальдо азарту не підсумував — покаже лише нове"); }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        if (economy is not null) economy.Moved -= Note;
        return Task.CompletedTask;
    }
}
