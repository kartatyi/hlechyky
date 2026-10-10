using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>Налаштування «Двадцять одно» (секція <c>Blackjack</c> в appsettings.json), наживо.</summary>
public sealed class BlackjackOptions
{
    /// <summary>Гра відкрита. false — нових столів нема, ставок не приймає; роздача, що йде, догравається й розраховується.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Найменша ставка на бокс, черепків.</summary>
    public int MinBet { get; set; } = 10;
    /// <summary>Найбільша ставка на бокс; 0 — без стелі (лише гаманець і арифметика виграшу).</summary>
    public int MaxBet { get; set; } = 2000;
}

/// <summary>
/// Один гравець роздачі в касі: скільки мало бути списано за роздачу загалом (ставка + подвоєння + спліти) і скільки
/// повернути (null — роздача ще йде).
/// </summary>
public sealed record BjPay(string Nick, int Stake, int? Return);

/// <summary>
/// Роздача від списання ставок до виплати. <c>Table</c> — «{кімната}:{epoch}», <c>Round</c> — номер роздачі в стані гри.
/// <c>Final</c> — Глек дограв і підсумки (<see cref="BjPay.Return"/>) записано: сирота платить за ними. Не <c>Final</c> —
/// роздачу обірвано посеред ходів: сирота повертає все списане (§3.5).
/// </summary>
public sealed record BjRound(string Table, int Round, string GameId, DateTimeOffset At, bool Final, IReadOnlyList<BjPay> Pays);

/// <summary>
/// Каса «Двадцять одно» (docs/games/specs/blackjack.md §3) — за зразком <see cref="RouletteBook"/>: дім платить зі своєї
/// кишені, тож гра ходить до <see cref="IStakes"/> сама, а кожна роздача лежить у сховищі (<see cref="StoreKey"/>) від
/// першого списання до виплати. Списань за роздачу може бути кілька (ставка, подвоєння, спліти) — усі під одним префіксом
/// <c>blackjack-bet:{table}:{round}:</c>, крок — перед ніком (<c>b</c>, <c>d</c>, <c>s1</c>, <c>s2</c>), нік — останнім
/// сегментом (у ніку може бути «:»). Виплата — <c>blackjack-pay:…:{нік}</c>, повернення — <c>blackjack-back:…:{нік}</c>:
/// гра, відновлення на старті й підмітання можуть розрахувати ту саму роздачу — гроші підуть раз.
/// </summary>
public sealed class BlackjackBook : BackgroundService
{
    public const string StoreKey = "blackjack:pending";
    public static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(60);
    /// <summary>
    /// Роздача за столом може тягтись хвилинами (5 боксів × 3 руки × 15 с на рішення), а соло — скільки людина думає.
    /// Живий стіл свою роздачу тримає сам (<see cref="HeldBy"/>); запис, старший за це, чий стіл зник, — сирота.
    /// </summary>
    public static readonly TimeSpan OrphanAge = TimeSpan.FromMinutes(20);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly IStakes _stakes;
    readonly IGameStore _store;
    readonly IClock _clock;
    readonly ILogger<BlackjackBook>? _log;
    readonly Action<Action> _defer;
    readonly Func<string, int, bool>? _held;
    readonly IOptionsMonitor<BlackjackOptions>? _opts;
    readonly DateTimeOffset _startedAt;
    readonly object _gate = new();

    /// <param name="defer">Як виконати виплату поза замком кімнати: типово — пул потоків; тести — <c>a =&gt; a()</c>.</param>
    /// <param name="held">Чи роздачу («{table}», номер) зараз тримає живий стіл — тоді її не чіпають ні відновлення, ні підмітання.</param>
    public BlackjackBook(IStakes stakes, IGameStore store, IClock? clock = null, ILogger<BlackjackBook>? log = null,
        Action<Action>? defer = null, Func<string, int, bool>? held = null, IOptionsMonitor<BlackjackOptions>? options = null)
    {
        _stakes = stakes;
        _store = store;
        _clock = clock ?? new SystemClock();
        _log = log;
        _defer = defer ?? (a => ThreadPool.QueueUserWorkItem(_ => a()));
        _held = held;
        _opts = options;
        _startedAt = _clock.UtcNow;
    }

    /// <summary>Поточні налаштування (наживо).</summary>
    public BlackjackOptions Options => _opts?.CurrentValue ?? Defaults;
    static readonly BlackjackOptions Defaults = new();

    // ---------- ключі леджера ----------

    public static string BetPrefix(string table, int round) => $"blackjack-bet:{table}:{round}:";
    /// <summary>Списання: крок (<c>b</c> ставка, <c>d</c> подвоєння, <c>s1</c>/<c>s2</c> спліти) перед ніком.</summary>
    public static string BetRef(string table, int round, string step, string nick) => $"{BetPrefix(table, round)}{step}:{Rooms.NickKey(nick)}";
    public static string PayRef(string table, int round, string nick) => $"blackjack-pay:{table}:{round}:{Rooms.NickKey(nick)}";
    public static string BackRef(string table, int round, string nick) => $"blackjack-back:{table}:{round}:{Rooms.NickKey(nick)}";

    // ---------- гроші ----------

    /// <summary>Скільки в гаманці. Економіка впала — 0 (тоді ставка просто не пройде).</summary>
    public int Balance(string nick)
    {
        try { return _stakes.Balance(nick); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "двадцять одно: не дізнався баланс {Nick}", nick);
            return 0;
        }
    }

    /// <summary>Записати роздачу в сховище — синхронно, ДО першого списання. false — не записалось: не роздаємо.</summary>
    public bool Open(BjRound round) => Write(round, "не записалась у касу");

    /// <summary>Записати підсумки роздачі (<see cref="BjRound.Final"/>) — синхронно, щойно Глек дограв. false — лише лог.</summary>
    public bool Finalize(BjRound round) => Write(round, "не записала підсумків");

    bool Write(BjRound round, string what)
    {
        try
        {
            lock (_gate)
            {
                var all = Read();
                all[Key(round.Table, round.Round)] = round;
                Save(all);
            }
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "двадцять одно: роздача {Table}:{Round} {What}", round.Table, round.Round, what);
            return false;
        }
    }

    /// <summary>Списати з гаманця — синхронно. Виняток — false (нічого не списано, або повтор ключа нічого не з'їсть).</summary>
    public bool Take(string nick, int amount, string reason, string refKey)
    {
        try { return _stakes.TrySpend(nick, amount, reason, refKey); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "двадцять одно: {Nick} ({Ref}) не списалось", nick, refKey);
            return false;
        }
    }

    /// <summary>Прибрати запис роздачі, за яку ніхто не заплатив.</summary>
    public void Drop(string table, int round) => Remove(table, round);

    /// <summary>Виплатити роздачу (підсумки — спершу в запис) і прибрати запис — відкладено (поза замком кімнати).</summary>
    public void Settle(BjRound round) => _defer(() =>
    {
        try
        {
            Finalize(round);
            SettleNow(round);
        }
        catch (Exception ex) { _log?.LogWarning(ex, "двадцять одно: роздача {Table}:{Round} не розрахувалась", round.Table, round.Round); }
    });

    /// <summary>Розрахувати всі записи, відкриті раніше за <paramref name="olderThan"/>, крім тих, що тримає живий стіл.</summary>
    public int Recover(DateTimeOffset olderThan)
    {
        List<BjRound> due;
        try
        {
            lock (_gate) due = [.. Read().Values.Where(p => p.At < olderThan)];
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "двадцять одно: не прочитав касу");
            return 0;
        }
        var done = 0;
        foreach (var round in due)
        {
            // Поза _gate: перевірка бере замок кімнати, а гра під ним ходить у касу — навпаки був би глухий кут.
            if (Held(round)) continue;
            try { if (SettleNow(round)) done++; }
            catch (Exception ex) { _log?.LogWarning(ex, "двадцять одно: роздача {Table}:{Round} не відновилась", round.Table, round.Round); }
        }
        if (done > 0) _log?.LogInformation("двадцять одно: каса розрахувала роздач після перерви: {Count}", done);
        return done;
    }

    /// <summary>Підмітання сиріт: записи, старші за <see cref="OrphanAge"/>.</summary>
    public int Sweep() => Recover(_clock.UtcNow - OrphanAge);

    /// <summary>Старт: усе, що лишилось від старого процесу (записи, старші за народження каси).</summary>
    public int RecoverAtStart() => Recover(_startedAt);

    /// <summary>Записи, що зараз лежать у касі (для тестів і діагностики).</summary>
    public IReadOnlyList<BjRound> Pending()
    {
        lock (_gate) return [.. Read().Values];
    }

    /// <summary>Чи лежить у касі запис роздачі (тобто її ще не розраховано). null — касу не прочитати.</summary>
    public bool? Holds(string table, int round)
    {
        try
        {
            lock (_gate) return Read().ContainsKey(Key(table, round));
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "двадцять одно: не прочитав касу");
            return null;
        }
    }

    /// <summary>
    /// Чим кінчилась роздача для ніка за леджером: скільки виплачено й скільки повернуто (соло після перезапуску каже людині,
    /// що сталося з її ставкою). null — леджера нема чи не прочитати.
    /// </summary>
    public (int Paid, int Back)? Outcome(string table, int round, string nick)
    {
        try
        {
            var pay = _stakes.Moves(PayRef(table, round, nick));
            var back = _stakes.Moves(BackRef(table, round, nick));
            if (pay is null || back is null) return null;
            var p = PayRef(table, round, nick);
            var b = BackRef(table, round, nick);
            return (pay.Where(m => m.Ref == p).Sum(m => m.Delta), back.Where(m => m.Ref == b).Sum(m => m.Delta));
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "двадцять одно: не прочитав леджер роздачі {Table}:{Round}", table, round);
            return null;
        }
    }

    /// <summary>Чи роздачу тримає живий стіл у <paramref name="rooms"/> (кімната з id до першої «:» грає й чекає саме її).</summary>
    public static bool HeldBy(Rooms rooms, string table, int round)
    {
        var colon = table.IndexOf(':');
        if (colon <= 0 || rooms.Find(table[..colon]) is not { } room) return false;
        lock (room.Sync) return room.Status == RoomStatus.Playing && room.Game is BlackjackGame game && game.Holds(table, round);
    }

    bool Held(BjRound round)
    {
        if (_held is null) return false;
        try { return _held(round.Table, round.Round); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "двадцять одно: не з'ясував, чи стіл {Table} живий", round.Table);
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Столи, які повернув TablesKeeper.RestoreAtStart (у Program.cs раніше за хостовані сервіси), доводять роздачі самі.
        RecoverAtStart();
        using var timer = new PeriodicTimer(SweepEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) Sweep();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Розрахунок за леджером. Кожному — сума всіх його списань за роздачу (ставка, подвоєння, спліти). Підсумки є і сума
    /// збігається із записом — виплата <c>Return</c>; підсумків нема (роздачу обірвано) або сума не та (запис зіпсовано) —
    /// повертаємо все списане. Не платив — нічого. true — запис прибрано.
    /// </summary>
    bool SettleNow(BjRound round)
    {
        var prefix = BetPrefix(round.Table, round.Round);
        IReadOnlyList<LedgerMove>? moves;
        try { moves = _stakes.Moves(prefix); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "двадцять одно: не прочитав леджер роздачі {Table}:{Round} — спробую згодом", round.Table, round.Round);
            return false;
        }
        if (moves is null)
        {
            _log?.LogWarning("двадцять одно: леджера нема — роздачу {Table}:{Round} нікому не плачу", round.Table, round.Round);
            Remove(round.Table, round.Round);
            return true;
        }
        var clean = true;
        foreach (var pay in round.Pays)
        {
            var key = Rooms.NickKey(pay.Nick);
            long spent = 0;
            foreach (var m in moves)
            {
                if (!m.Ref.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var rest = m.Ref.AsSpan(prefix.Length);
                var colon = rest.IndexOf(':');
                if (colon > 0 && rest[(colon + 1)..].SequenceEqual(key)) spent -= m.Delta;
            }
            if (spent <= 0) continue;   // не заплатив — і не отримає
            try
            {
                if (round.Final && pay.Return is { } ret && spent == pay.Stake)
                {
                    if (ret > 0) _stakes.Grant(pay.Nick, ret, $"blackjack-pay:{round.GameId}", PayRef(round.Table, round.Round, pay.Nick));
                }
                else
                {
                    if (round.Final)
                        _log?.LogWarning("двадцять одно: роздача {Table}:{Round}, {Nick}: списано {Spent}, а в записі {Stake} — повертаю списане",
                            round.Table, round.Round, pay.Nick, spent, pay.Stake);
                    _stakes.Grant(pay.Nick, (int)Math.Min(int.MaxValue, spent), $"blackjack-back:{round.GameId}", BackRef(round.Table, round.Round, pay.Nick));
                }
            }
            catch (Exception ex)
            {
                clean = false;   // запис лишається: підмітання спробує ще (ключі не дадуть заплатити двічі)
                _log?.LogWarning(ex, "двадцять одно: виплата {Nick} за роздачу {Table}:{Round} не пройшла", pay.Nick, round.Table, round.Round);
            }
        }
        if (clean) Remove(round.Table, round.Round);
        return clean;
    }

    void Remove(string table, int round)
    {
        try
        {
            lock (_gate)
            {
                var all = Read();
                if (all.Remove(Key(table, round))) Save(all);
            }
        }
        catch (Exception ex) { _log?.LogWarning(ex, "двадцять одно: запис роздачі {Table}:{Round} не прибрався", table, round); }
    }

    static string Key(string table, int round) => $"{table}:{round}";

    /// <summary>Під <see cref="_gate"/>. Зіпсований запис — лог і порожній словник (інакше каса не відкрилась би ніколи).</summary>
    Dictionary<string, BjRound> Read()
    {
        var json = _store.LoadState(StoreKey);
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.Ordinal);
        try
        {
            var all = JsonSerializer.Deserialize<Dictionary<string, BjRound>>(json, Json);
            return all is null ? new(StringComparer.Ordinal) : new(all, StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            _log?.LogWarning(ex, "двадцять одно: запис каси зіпсовано — починаю з порожньої");
            return new(StringComparer.Ordinal);
        }
    }

    void Save(Dictionary<string, BjRound> all) => _store.SaveState(StoreKey, JsonSerializer.Serialize(all, Json));
}

/// <summary>Підключення «Двадцять одно» в <see cref="GamesSetup"/>: налаштування Blackjack:* і каса.</summary>
public static class BlackjackSetup
{
    public static IServiceCollection AddBlackjack(IServiceCollection services)
    {
        services.AddOptions<BlackjackOptions>().BindConfiguration("Blackjack");
        services.AddSingleton(sp => new BlackjackBook(sp.GetRequiredService<IStakes>(), sp.GetRequiredService<IGameStore>(),
            sp.GetService<IClock>(), sp.GetService<ILogger<BlackjackBook>>(),
            // Кімнати — ліниво, у мить перевірки: касу першою просить гра (Configure), тобто вже зсередини Rooms.
            held: (table, round) => sp.GetService<Rooms>() is { } rooms && BlackjackBook.HeldBy(rooms, table, round),
            options: sp.GetService<IOptionsMonitor<BlackjackOptions>>()));
        services.AddHostedService(sp => sp.GetRequiredService<BlackjackBook>());
        return services;
    }
}
