using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>Секція <c>Lelka</c> конфігу (наживо, без перезапуску).</summary>
public sealed class LelkaOptions
{
    /// <summary>Гра відкрита. false — нових столів нема, ставок не приймає; політ, що вже йде, долітає й розраховується.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Найменша ставка, черепків.</summary>
    public int MinBet { get; set; } = 10;
    /// <summary>Найбільша ставка; 0 — без стелі (лише гаманець).</summary>
    public int MaxBet { get; set; } = 2000;
}

/// <summary>
/// Один гравець раунду в касі: скільки списано, автозабір (соті, null — нема) і скільки повернути (null — ще летить,
/// 0 — пролетів, інакше ставка × множник). Повернення відомі, щойно людина забрала, тож пишуться в запис одразу.
/// </summary>
public sealed record LelkaPay(string Nick, int Stake, int? Auto, int? Return);

/// <summary>
/// Раунд Лелеки від закриття прийому до розрахунку: точка падіння вже вирішена, ставки списано (чи списуються).
/// <c>Table</c> — «{кімната}:{epoch}», <c>Round</c> — номер раунду в стані гри, <c>Crash</c> — соті.
/// </summary>
public sealed record LelkaRound(string Table, int Round, string GameId, int Crash, DateTimeOffset At, IReadOnlyList<LelkaPay> Pays);

/// <summary>Чим скінчилось списання одного гравця на зльоті: <c>Wallet</c> — гаманець до списання.</summary>
public sealed record LelkaTake(string Nick, bool Ok, int Wallet);

/// <summary>
/// Каса Лелеки (docs/games/specs/lelka.md §4) — за зразком <see cref="RouletteBook"/>: дім платить зі своєї кишені, тож
/// гра ходить до <see cref="IStakes"/> сама, а кожен закритий раунд лежить у сховищі (<see cref="StoreKey"/>), поки його
/// не розраховано. Ключі леджера: <c>lelka-bet|win|back:{table}:{round}:{нік}</c> — гра, відновлення на старті й
/// підмітання можуть розрахувати те саме, гроші підуть раз.
/// <para>
/// Сирота (стіл зник посеред польоту й не повернувся) розраховується чесно за вже вирішеною точкою: хто забрав — свій
/// виграш; автозабір ≤ точки падіння — ставка × автозабір (він би спрацював); раунд «одразу ×1,00» — програш; решті
/// ставку повертаємо (польоту ніхто не бачив до кінця).
/// </para>
/// <para>
/// Тік спільний для всіх реалтайм-ігор (<see cref="TickEngine"/>), тож гра в базу з тіку не ходить: запис раунду, списання,
/// запис забраного, виплати й гаманці — у черзі каси (<see cref="Launch"/>, <see cref="Cash"/>, <see cref="Settle"/>,
/// <see cref="Balances"/>), по одному завданню, у порядку надходження, поза замком кімнати. Порядок важить: списання
/// раунду завжди раніше за його виплати. Відповіді черга віддає гравцеві колбеком, а стіл приймає їх на наступному тіку.
/// </para>
/// </summary>
public sealed class LelkaBook : BackgroundService
{
    public const string StoreKey = "lelka:pending";
    public static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(60);
    /// <summary>Раунд живе ≤ 8 + 92 с, плюс заморозка; запис, старший за це, — сирота.</summary>
    public static readonly TimeSpan OrphanAge = TimeSpan.FromMinutes(4);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly IStakes _stakes;
    readonly IGameStore _store;
    readonly IClock _clock;
    readonly ILogger<LelkaBook>? _log;
    readonly Action<Action> _defer;
    readonly Func<string, int, bool>? _held;
    readonly IOptionsMonitor<LelkaOptions>? _opts;
    readonly IOutbox? _outbox;
    readonly DateTimeOffset _startedAt;
    readonly object _gate = new();
    readonly ConcurrentQueue<Action> _queue = new();
    int _draining;

    /// <param name="defer">
    /// Як виконати роботу каси поза тіком: типово — своя черга на пулі потоків (по одному завданню, у порядку надходження);
    /// тести — <c>a =&gt; a()</c> (одразу) чи збирач у список (щоб довести, що тік касу не чекає).
    /// </param>
    public LelkaBook(IStakes stakes, IGameStore store, IClock? clock = null, ILogger<LelkaBook>? log = null,
        Action<Action>? defer = null, Func<string, int, bool>? held = null, IOptionsMonitor<LelkaOptions>? options = null,
        IOutbox? outbox = null)
    {
        _stakes = stakes;
        _store = store;
        _clock = clock ?? new SystemClock();
        _log = log;
        _defer = defer ?? Serial;
        _held = held;
        _opts = options;
        _outbox = outbox;
        _startedAt = _clock.UtcNow;
    }

    /// <summary>Поставити роботу в чергу каси: один виконавець на пулі потоків, завдання — строго по черзі.</summary>
    void Serial(Action work)
    {
        _queue.Enqueue(work);
        if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0) ThreadPool.QueueUserWorkItem(_ => DrainQueue());
    }

    void DrainQueue()
    {
        while (true)
        {
            while (_queue.TryDequeue(out var work))
            {
                try { work(); }
                catch (Exception ex) { _log?.LogWarning(ex, "лелека: завдання каси впало"); }
            }
            Volatile.Write(ref _draining, 0);
            // Хтось устиг покласти завдання між останнім TryDequeue і скиданням прапорця — добираємо самі.
            if (_queue.IsEmpty || Interlocked.CompareExchange(ref _draining, 1, 0) != 0) return;
        }
    }

    /// <summary>Поточні налаштування (наживо).</summary>
    public LelkaOptions Options => _opts?.CurrentValue ?? Defaults;
    static readonly LelkaOptions Defaults = new();

    public int Balance(string nick)
    {
        try { return _stakes.Balance(nick); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "лелека: не дізнався баланс {Nick}", nick);
            return 0;
        }
    }

    /// <summary>Записати раунд у сховище — ДО першого списання (гра кличе через <see cref="Launch"/>). false — не записалось: ставки не беремо.</summary>
    public bool Open(LelkaRound round)
    {
        try
        {
            lock (_gate)
            {
                var all = Read();
                all[Key(round.Table, round.Round)] = round;
                Write(all);
            }
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "лелека: раунд {Table}:{Round} не записався в касу", round.Table, round.Round);
            return false;
        }
    }

    public bool Take(string nick, int amount, string refKey)
    {
        try { return _stakes.TrySpend(nick, amount, "lelka-bet:lelka", refKey); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "лелека: ставка {Nick} ({Ref}) не списалась", nick, refKey);
            return false;
        }
    }

    public void Drop(string table, int round) => Remove(table, round);

    /// <summary>
    /// «Злітаємо!» — у черзі каси: запис раунду в сховище (до першого списання), тоді списання кожному. <paramref name="done"/>
    /// кличеться з черги: null — запис не ліг (нічого не списано), інакше — чим скінчилось у кожного. Ніхто не заплатив —
    /// запис прибрано. Ключі леджера ті самі, що й раніше: повтор нічого не з'їсть.
    /// </summary>
    public void Launch(LelkaRound round, Action<IReadOnlyList<LelkaTake>?> done) => _defer(() =>
    {
        if (!Open(round))
        {
            done(null);
            return;
        }
        var res = new List<LelkaTake>(round.Pays.Count);
        foreach (var pay in round.Pays)
        {
            var wallet = Balance(pay.Nick);
            res.Add(new LelkaTake(pay.Nick, Take(pay.Nick, pay.Stake, BetRef(round.Table, round.Round, pay.Nick)), wallet));
        }
        if (res.TrueForAll(r => !r.Ok)) Drop(round.Table, round.Round);
        done(res);
    });

    /// <summary>Гаманці людей за столом — у черзі каси; <paramref name="done"/> кличеться з черги (нік → черепків).</summary>
    public void Balances(IReadOnlyList<string> nicks, Action<IReadOnlyDictionary<string, int>> done)
    {
        if (nicks.Count == 0) return;
        _defer(() =>
        {
            var all = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var nick in nicks) all[nick] = Balance(nick);
            done(all);
        });
    }

    /// <summary>
    /// Людина забрала — у черзі каси: виграш спершу в запис раунду (щоб і сирота знав), тоді в гаманець. Ключ той самий,
    /// що й у розрахунку, — двічі не заплатить; черга та сама, що й у списань, — раніше за списання не заплатить.
    /// </summary>
    public void Cash(string table, int round, string nick, int ret) => _defer(() => CashNow(table, round, nick, ret));

    void CashNow(string table, int round, string nick, int ret)
    {
        try
        {
            lock (_gate)
            {
                var all = Read();
                var key = Key(table, round);
                if (all.TryGetValue(key, out var r))
                {
                    var nk = Rooms.NickKey(nick);
                    all[key] = r with { Pays = [.. r.Pays.Select(p => Rooms.NickKey(p.Nick) == nk ? p with { Return = ret } : p)] };
                    Write(all);
                }
            }
        }
        catch (Exception ex) { _log?.LogWarning(ex, "лелека: забране {Nick} не записалось у раунд {Table}:{Round}", nick, table, round); }
        if (ret <= 0) return;
        try { _stakes.Grant(nick, ret, "lelka-win:lelka", WinRef(table, round, nick)); }
        catch (Exception ex) { _log?.LogWarning(ex, "лелека: виграш {Nick} не пройшов — доплатить розрахунок", nick); }
    }

    /// <summary>Розрахувати раунд і прибрати запис — відкладено (поза замком кімнати).</summary>
    public void Settle(LelkaRound round) => _defer(() =>
    {
        try { SettleNow(round); }
        catch (Exception ex) { _log?.LogWarning(ex, "лелека: раунд {Table}:{Round} не розрахувався", round.Table, round.Round); }
    });

    /// <summary>Рядок у загальні Балачки від Глека — відкладено, поза замком кімнати.</summary>
    public void Announce(string text)
    {
        if (_outbox is not { } outbox || string.IsNullOrWhiteSpace(text)) return;
        _defer(() =>
        {
            try { outbox.Post(new DjSays(text)); }
            catch (Exception ex) { _log?.LogWarning(ex, "лелека: рядок у Балачки не пішов"); }
        });
    }

    public int Recover(DateTimeOffset olderThan)
    {
        List<LelkaRound> due;
        try
        {
            lock (_gate) due = [.. Read().Values.Where(p => p.At < olderThan)];
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "лелека: не прочитав касу");
            return 0;
        }
        var done = 0;
        foreach (var round in due)
        {
            if (Held(round)) continue;
            try { if (SettleNow(round)) done++; }
            catch (Exception ex) { _log?.LogWarning(ex, "лелека: раунд {Table}:{Round} не відновився", round.Table, round.Round); }
        }
        if (done > 0) _log?.LogInformation("лелека: каса розрахувала раундів після перерви: {Count}", done);
        return done;
    }

    public int Sweep() => Recover(_clock.UtcNow - OrphanAge);

    public int RecoverAtStart() => Recover(_startedAt);

    public IReadOnlyList<LelkaRound> Pending()
    {
        lock (_gate) return [.. Read().Values];
    }

    /// <summary>Чи лежить у касі запис раунду (тобто його ще не розраховано). null — касу не прочитати.</summary>
    public bool? Holds(string table, int round)
    {
        try
        {
            lock (_gate) return Read().ContainsKey(Key(table, round));
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "лелека: не прочитав касу");
            return null;
        }
    }

    /// <summary>Чи раунд тримає живий стіл у <paramref name="rooms"/> (бере замок кімнати).</summary>
    public static bool HeldBy(Rooms rooms, string table, int round)
    {
        var colon = table.IndexOf(':');
        if (colon <= 0 || rooms.Find(table[..colon]) is not { } room) return false;
        lock (room.Sync) return room.Status == RoomStatus.Playing && room.Game is Lelka game && game.Holds(table, round);
    }

    bool Held(LelkaRound round)
    {
        if (_held is null) return false;
        try { return _held(round.Table, round.Round); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "лелека: не з'ясував, чи стіл {Table} живий", round.Table);
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        RecoverAtStart();
        using var timer = new PeriodicTimer(SweepEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) Sweep();
        }
        catch (OperationCanceledException) { }
    }

    public static string BetRef(string table, int round, string nick) => $"lelka-bet:{table}:{round}:{Rooms.NickKey(nick)}";
    public static string WinRef(string table, int round, string nick) => $"lelka-win:{table}:{round}:{Rooms.NickKey(nick)}";
    static string BackRef(string table, int round, string nick) => $"lelka-back:{table}:{round}:{Rooms.NickKey(nick)}";

    /// <summary>
    /// Скільки повернути людині: забрала — її виграш; ще «летить» (сирота) — автозабір ≤ точки падіння платить,
    /// падіння одразу — програш, інакше ставку назад (<c>Back</c>).
    /// </summary>
    public static (int Win, bool Back) Due(LelkaPay pay, int crash)
    {
        if (pay.Return is { } ret) return (ret, false);
        if (pay.Auto is { } auto && auto <= crash) return (LelkaCore.Win(pay.Stake, auto), false);
        if (crash <= 100) return (0, false);
        return (pay.Stake, true);
    }

    /// <summary>Розрахунок за леджером: платимо лише тим, у кого справді є списання цього раунду. true — запис прибрано.</summary>
    bool SettleNow(LelkaRound round)
    {
        var prefix = $"lelka-bet:{round.Table}:{round.Round}:";
        IReadOnlyList<LedgerMove>? moves;
        try { moves = _stakes.Moves(prefix); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "лелека: не прочитав леджер раунду {Table}:{Round} — спробую згодом", round.Table, round.Round);
            return false;
        }
        if (moves is null)
        {
            _log?.LogWarning("лелека: леджера нема — раунд {Table}:{Round} нікому не плачу", round.Table, round.Round);
            Remove(round.Table, round.Round);
            return true;
        }
        var clean = true;
        foreach (var pay in round.Pays)
        {
            var key = Rooms.NickKey(pay.Nick);
            var row = moves.FirstOrDefault(m => string.Equals(m.Ref, prefix + key, StringComparison.Ordinal));
            if (row is null) continue;   // не заплатив — і не отримає
            try
            {
                if (-row.Delta == pay.Stake)
                {
                    var (win, back) = Due(pay, round.Crash);
                    if (win > 0)
                    {
                        if (back) _stakes.Grant(pay.Nick, win, "lelka-back:lelka", BackRef(round.Table, round.Round, pay.Nick));
                        else _stakes.Grant(pay.Nick, win, "lelka-win:lelka", WinRef(round.Table, round.Round, pay.Nick));
                    }
                }
                else
                {
                    _log?.LogWarning("лелека: раунд {Table}:{Round}, {Nick}: списано {Delta}, а в записі {Stake} — повертаю списане",
                        round.Table, round.Round, pay.Nick, row.Delta, pay.Stake);
                    _stakes.Grant(pay.Nick, Math.Abs(row.Delta), "lelka-back:lelka", BackRef(round.Table, round.Round, pay.Nick));
                }
            }
            catch (Exception ex)
            {
                clean = false;
                _log?.LogWarning(ex, "лелека: виплата {Nick} за раунд {Table}:{Round} не пройшла", pay.Nick, round.Table, round.Round);
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
                if (all.Remove(Key(table, round))) Write(all);
            }
        }
        catch (Exception ex) { _log?.LogWarning(ex, "лелека: запис раунду {Table}:{Round} не прибрався", table, round); }
    }

    static string Key(string table, int round) => $"{table}:{round}";

    Dictionary<string, LelkaRound> Read()
    {
        var json = _store.LoadState(StoreKey);
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.Ordinal);
        try
        {
            var all = JsonSerializer.Deserialize<Dictionary<string, LelkaRound>>(json, Json);
            return all is null ? new(StringComparer.Ordinal) : new(all, StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            _log?.LogWarning(ex, "лелека: запис каси зіпсовано — починаю з порожньої");
            return new(StringComparer.Ordinal);
        }
    }

    void Write(Dictionary<string, LelkaRound> all) => _store.SaveState(StoreKey, JsonSerializer.Serialize(all, Json));
}

/// <summary>Підключення Лелеки в <see cref="GamesSetup"/>: налаштування й каса.</summary>
public static class LelkaSetup
{
    public static IServiceCollection AddLelka(IServiceCollection services)
    {
        services.AddOptions<LelkaOptions>().BindConfiguration("Lelka");
        services.AddSingleton(sp => new LelkaBook(sp.GetRequiredService<IStakes>(), sp.GetRequiredService<IGameStore>(),
            sp.GetService<IClock>(), sp.GetService<ILogger<LelkaBook>>(),
            held: (table, round) => sp.GetService<Rooms>() is { } rooms && LelkaBook.HeldBy(rooms, table, round),
            options: sp.GetService<IOptionsMonitor<LelkaOptions>>(), outbox: sp.GetService<IOutbox>()));
        services.AddHostedService(sp => sp.GetRequiredService<LelkaBook>());
        return services;
    }
}
