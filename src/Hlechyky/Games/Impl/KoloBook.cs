using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>Секція <c>Kolo</c> конфігу (наживо, без перезапуску).</summary>
public sealed class KoloOptions
{
    /// <summary>Гра відкрита. false — нових столів нема, ставок не приймає; раунд, що вже крутиться, докручується й платить.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Найменша ставка (одна фішка на множник), черепків.</summary>
    public int MinBet { get; set; } = 10;
    /// <summary>Найбільше на один множник за раунд; 0 — без стелі (лише гаманець і арифметика виграшу).</summary>
    public int MaxBet { get; set; } = 2000;
}

/// <summary>Один гравець раунду в касі: скільки списано і скільки повернути (вирішено ще при закритті ставок; 0 — програв).</summary>
public sealed record KoloPay(string Nick, int Stake, int Return);

/// <summary>
/// Раунд колеса від закриття ставок до розрахунку: сегмент уже вирішено, ставки списано (чи списуються).
/// <c>Table</c> — «{кімната}:{epoch}», <c>Round</c> — номер раунду в стані гри, <c>X</c> — множник сегмента (0 — «Тріснув!»).
/// </summary>
public sealed record KoloRound(string Table, int Round, string GameId, int Seg, int X, DateTimeOffset At, IReadOnlyList<KoloPay> Pays);

/// <summary>Чим скінчилось списання одного гравця: <c>Wallet</c> — гаманець до списання.</summary>
public sealed record KoloTake(string Nick, bool Ok, int Wallet);

/// <summary>
/// Каса «Гончарного колеса» (docs/games/specs/kolo.md §3) — за зразком <see cref="LelkaBook"/> і <see cref="RouletteBook"/>:
/// дім платить зі своєї кишені, тож гра ходить до <see cref="IStakes"/> сама, а кожен закритий раунд лежить у сховищі
/// (<see cref="StoreKey"/>), поки його не розраховано. Ключі леджера — <c>kolo-bet|win|back:{table}:{round}:{нік}</c>:
/// гра, відновлення на старті й підмітання можуть розрахувати той самий раунд, а гроші підуть раз.
/// <para>
/// Тік у базу не ходить: запис раунду, списання, виплати й гаманці — у черзі каси (<see cref="Launch"/>,
/// <see cref="Settle"/>, <see cref="Balances"/>), по одному завданню, у порядку надходження, поза замком кімнати. Порядок
/// важить: списання раунду завжди раніше за його виплати. Відповіді черга віддає колбеком, стіл приймає їх на тіку.
/// </para>
/// <para>
/// Сирота (стіл зник посеред обертання й не повернувся) розраховується за вже вирішеним сегментом, як у рулетці: хто
/// заплатив і виграв — отримує виграш, хто програв — нічого. Повернення ставки (<c>kolo-back</c>) — лише коли запис
/// розходиться з леджером.
/// </para>
/// </summary>
public sealed class KoloBook : BackgroundService
{
    public const string StoreKey = "kolo:pending";
    public static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(60);
    /// <summary>Раунд у касі живе ≤ 5 с обертання плюс заморозка деплою; старший — сирота.</summary>
    public static readonly TimeSpan OrphanAge = TimeSpan.FromMinutes(2);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly IStakes _stakes;
    readonly IGameStore _store;
    readonly IClock _clock;
    readonly ILogger<KoloBook>? _log;
    readonly Action<Action> _defer;
    readonly Func<string, int, bool>? _held;
    readonly IOptionsMonitor<KoloOptions>? _opts;
    readonly DateTimeOffset _startedAt;
    readonly object _gate = new();
    readonly ConcurrentQueue<Action> _queue = new();
    int _draining;

    /// <param name="defer">Як виконати роботу каси поза тіком: типово — своя черга на пулі потоків; тести — <c>a =&gt; a()</c>.</param>
    /// <param name="held">Чи раунд тримає живий стіл (тоді відновлення й підмітання його не чіпають).</param>
    public KoloBook(IStakes stakes, IGameStore store, IClock? clock = null, ILogger<KoloBook>? log = null,
        Action<Action>? defer = null, Func<string, int, bool>? held = null, IOptionsMonitor<KoloOptions>? options = null)
    {
        _stakes = stakes;
        _store = store;
        _clock = clock ?? new SystemClock();
        _log = log;
        _defer = defer ?? Serial;
        _held = held;
        _opts = options;
        _startedAt = _clock.UtcNow;
    }

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
                catch (Exception ex) { _log?.LogWarning(ex, "колесо: завдання каси впало"); }
            }
            Volatile.Write(ref _draining, 0);
            // Хтось устиг покласти завдання між останнім TryDequeue і скиданням прапорця — добираємо самі.
            if (_queue.IsEmpty || Interlocked.CompareExchange(ref _draining, 1, 0) != 0) return;
        }
    }

    /// <summary>Поточні налаштування (наживо).</summary>
    public KoloOptions Options => _opts?.CurrentValue ?? Defaults;
    static readonly KoloOptions Defaults = new();

    public int Balance(string nick)
    {
        try { return _stakes.Balance(nick); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "колесо: не дізнався баланс {Nick}", nick);
            return 0;
        }
    }

    /// <summary>Записати раунд у сховище — ДО першого списання. false — не записалось: ставок не беремо.</summary>
    public bool Open(KoloRound round)
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
            _log?.LogWarning(ex, "колесо: раунд {Table}:{Round} не записався в касу", round.Table, round.Round);
            return false;
        }
    }

    public bool Take(string nick, int amount, string refKey)
    {
        try { return _stakes.TrySpend(nick, amount, "kolo-bet:kolo", refKey); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "колесо: ставка {Nick} ({Ref}) не списалась", nick, refKey);
            return false;
        }
    }

    public void Drop(string table, int round) => Remove(table, round);

    /// <summary>
    /// «Ставки зроблено!» — у черзі каси: запис раунду в сховище (до першого списання), тоді списання кожному.
    /// <paramref name="done"/> кличеться з черги: null — запис не ліг (нічого не списано), інакше — чим скінчилось у
    /// кожного. Ніхто не заплатив — запис прибрано.
    /// </summary>
    public void Launch(KoloRound round, Action<IReadOnlyList<KoloTake>?> done) => _defer(() =>
    {
        if (!Open(round))
        {
            done(null);
            return;
        }
        var res = new List<KoloTake>(round.Pays.Count);
        foreach (var pay in round.Pays)
        {
            var wallet = Balance(pay.Nick);
            res.Add(new KoloTake(pay.Nick, Take(pay.Nick, pay.Stake, BetRef(round.Table, round.Round, pay.Nick)), wallet));
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

    /// <summary>Колесо стало: виплати й прибрати запис — у черзі каси (поза замком кімнати).</summary>
    public void Settle(KoloRound round) => _defer(() =>
    {
        try { SettleNow(round); }
        catch (Exception ex) { _log?.LogWarning(ex, "колесо: раунд {Table}:{Round} не розрахувався", round.Table, round.Round); }
    });

    public int Recover(DateTimeOffset olderThan)
    {
        List<KoloRound> due;
        try
        {
            lock (_gate) due = [.. Read().Values.Where(p => p.At < olderThan)];
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "колесо: не прочитав касу");
            return 0;
        }
        var done = 0;
        foreach (var round in due)
        {
            if (Held(round)) continue;
            try { if (SettleNow(round)) done++; }
            catch (Exception ex) { _log?.LogWarning(ex, "колесо: раунд {Table}:{Round} не відновився", round.Table, round.Round); }
        }
        if (done > 0) _log?.LogInformation("колесо: каса розрахувала раундів після перерви: {Count}", done);
        return done;
    }

    /// <summary>Підмітання: сироти, старші за <see cref="OrphanAge"/>.</summary>
    public int Sweep() => Recover(_clock.UtcNow - OrphanAge);

    /// <summary>На старті: лише записи старого процесу (раунд, який відновлений стіл уже відкрив у цьому, — не чіпаємо).</summary>
    public int RecoverAtStart() => Recover(_startedAt);

    public IReadOnlyList<KoloRound> Pending()
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
            _log?.LogWarning(ex, "колесо: не прочитав касу");
            return null;
        }
    }

    /// <summary>Чи раунд тримає живий стіл у <paramref name="rooms"/> (бере замок кімнати).</summary>
    public static bool HeldBy(Rooms rooms, string table, int round)
    {
        var colon = table.IndexOf(':');
        if (colon <= 0 || rooms.Find(table[..colon]) is not { } room) return false;
        lock (room.Sync) return room.Status == RoomStatus.Playing && room.Game is Kolo game && game.Holds(table, round);
    }

    bool Held(KoloRound round)
    {
        if (_held is null) return false;
        try { return _held(round.Table, round.Round); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "колесо: не з'ясував, чи стіл {Table} живий", round.Table);
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

    /// <summary>Зупинка сервера: дочекатись черги каси (списання й виплати), щоб не лишити їх сиротами.</summary>
    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        var until = DateTime.UtcNow.AddSeconds(5);
        while ((!_queue.IsEmpty || Volatile.Read(ref _draining) == 1) && DateTime.UtcNow < until && !ct.IsCancellationRequested)
            await Task.Delay(20, CancellationToken.None);
    }

    public static string BetRef(string table, int round, string nick) => $"kolo-bet:{table}:{round}:{Rooms.NickKey(nick)}";
    public static string WinRef(string table, int round, string nick) => $"kolo-win:{table}:{round}:{Rooms.NickKey(nick)}";
    static string BackRef(string table, int round, string nick) => $"kolo-back:{table}:{round}:{Rooms.NickKey(nick)}";

    /// <summary>Розрахунок за леджером: платимо лише тим, у кого справді є списання цього раунду. true — запис прибрано.</summary>
    bool SettleNow(KoloRound round)
    {
        var prefix = $"kolo-bet:{round.Table}:{round.Round}:";
        IReadOnlyList<LedgerMove>? moves;
        try { moves = _stakes.Moves(prefix); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "колесо: не прочитав леджер раунду {Table}:{Round} — спробую згодом", round.Table, round.Round);
            return false;
        }
        if (moves is null)
        {
            _log?.LogWarning("колесо: леджера нема — раунд {Table}:{Round} нікому не плачу", round.Table, round.Round);
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
                    if (pay.Return > 0) _stakes.Grant(pay.Nick, pay.Return, "kolo-win:kolo", WinRef(round.Table, round.Round, pay.Nick));
                }
                else
                {
                    _log?.LogWarning("колесо: раунд {Table}:{Round}, {Nick}: списано {Delta}, а в записі {Stake} — повертаю списане",
                        round.Table, round.Round, pay.Nick, row.Delta, pay.Stake);
                    _stakes.Grant(pay.Nick, Math.Abs(row.Delta), "kolo-back:kolo", BackRef(round.Table, round.Round, pay.Nick));
                }
            }
            catch (Exception ex)
            {
                clean = false;
                _log?.LogWarning(ex, "колесо: виплата {Nick} за раунд {Table}:{Round} не пройшла", pay.Nick, round.Table, round.Round);
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
        catch (Exception ex) { _log?.LogWarning(ex, "колесо: запис раунду {Table}:{Round} не прибрався", table, round); }
    }

    static string Key(string table, int round) => $"{table}:{round}";

    Dictionary<string, KoloRound> Read()
    {
        var json = _store.LoadState(StoreKey);
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.Ordinal);
        try
        {
            var all = JsonSerializer.Deserialize<Dictionary<string, KoloRound>>(json, Json);
            return all is null ? new(StringComparer.Ordinal) : new(all, StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            _log?.LogWarning(ex, "колесо: запис каси зіпсовано — починаю з порожньої");
            return new(StringComparer.Ordinal);
        }
    }

    void Write(Dictionary<string, KoloRound> all) => _store.SaveState(StoreKey, JsonSerializer.Serialize(all, Json));
}

/// <summary>Підключення «Гончарного колеса» в <see cref="GamesSetup"/>: налаштування <c>Kolo:*</c> і каса.</summary>
public static class KoloSetup
{
    public static IServiceCollection AddKolo(IServiceCollection services)
    {
        services.AddOptions<KoloOptions>().BindConfiguration("Kolo");
        services.AddSingleton(sp => new KoloBook(sp.GetRequiredService<IStakes>(), sp.GetRequiredService<IGameStore>(),
            sp.GetService<IClock>(), sp.GetService<ILogger<KoloBook>>(),
            held: (table, round) => sp.GetService<Rooms>() is { } rooms && KoloBook.HeldBy(rooms, table, round),
            options: sp.GetService<IOptionsMonitor<KoloOptions>>()));
        services.AddHostedService(sp => sp.GetRequiredService<KoloBook>());
        return services;
    }
}
