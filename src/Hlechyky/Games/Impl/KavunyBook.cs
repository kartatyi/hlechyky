using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>Секція <c>Kavuny</c> конфігу (наживо, без перезапуску).</summary>
public sealed class KavunyOptions
{
    /// <summary>Гра відкрита. false — нового раунду не почати, тайла в лобі нема; раунд, що йде, догравається.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Найменша ставка, черепків.</summary>
    public int MinBet { get; set; } = 10;
    /// <summary>Найбільша ставка; 0 — без стелі (лише гаманець).</summary>
    public int MaxBet { get; set; } = 2000;
    /// <summary>Стеля множника (×2 … ×1000): доріс до неї — «віз порожній», раунд платить сам. Береться на старті раунду.</summary>
    public int MaxX { get; set; } = KavunyCore.DefaultCap / 100;
}

/// <summary>
/// Раунд у касі від списання до розрахунку. <c>Table</c> — «{кімната}:{epoch}», <c>No</c> — номер раунду в стані гри.
/// <c>Return</c>: null — ще йде (сирота поверне ставку), 0 — програв, інакше — скільки повернути.
/// </summary>
public sealed record KavunyRec(string Table, int No, string Nick, int Stake, string Seed, DateTimeOffset At, int? Return);

/// <summary>
/// Каса «Кавунів на ярмарку» (docs/games/specs/kavuny.md §4) — за зразком <see cref="LelkaBook"/>: дім платить зі своєї
/// кишені. Раунд лягає в сховище (<see cref="StoreKey"/>) ДО списання й лежить там, доки не розраховано. Ключі леджера:
/// <c>kavuny-bet|win|back:{table}:{no}:{нік}</c> — гра, відновлення на старті й підмітання можуть розрахувати те саме, гроші
/// підуть раз.
/// <para>
/// Синхронно (під замком кімнати, як ставка рулетки сам на сам) — лише старт: запис і списання, від них залежить, чи
/// почнеться раунд. Розрахунок (забрав, гнилий, віз порожній) — у черзі каси поза замком: тік у базу не ходить.
/// Сирота (раунд, якого не довів стіл: процес упав, деплой) розраховується так: підсумок уже в записі — за ним; ні —
/// раунд анульовано, ставку повертаємо.
/// </para>
/// </summary>
public sealed class KavunyBook : BackgroundService
{
    public const string StoreKey = "kavuny:pending";
    public static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(60);
    /// <summary>Раунд за найвищої стелі живе кілька хвилин; запис, старший за це й не тримаваний живим столом, — сирота.</summary>
    public static readonly TimeSpan OrphanAge = TimeSpan.FromMinutes(30);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly IStakes _stakes;
    readonly IGameStore _store;
    readonly IClock _clock;
    readonly ILogger<KavunyBook>? _log;
    readonly Action<Action> _defer;
    readonly Func<string, int, bool>? _held;
    readonly IOptionsMonitor<KavunyOptions>? _opts;
    readonly DateTimeOffset _startedAt;
    readonly object _gate = new();
    readonly ConcurrentQueue<Action> _queue = new();
    int _draining;

    /// <param name="defer">Як виконати роботу каси поза замком: типово — своя черга на пулі потоків; тести — <c>a =&gt; a()</c>.</param>
    /// <param name="held">Чи раунд тримає живий стіл (тоді відновлення його не чіпає).</param>
    public KavunyBook(IStakes stakes, IGameStore store, IClock? clock = null, ILogger<KavunyBook>? log = null,
        Action<Action>? defer = null, Func<string, int, bool>? held = null, IOptionsMonitor<KavunyOptions>? options = null)
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

    public KavunyOptions Options => _opts?.CurrentValue ?? Defaults;
    static readonly KavunyOptions Defaults = new();

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
                catch (Exception ex) { _log?.LogWarning(ex, "кавуни: завдання каси впало"); }
            }
            Volatile.Write(ref _draining, 0);
            if (_queue.IsEmpty || Interlocked.CompareExchange(ref _draining, 1, 0) != 0) return;
        }
    }

    public int Balance(string nick)
    {
        try { return _stakes.Balance(nick); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "кавуни: не дізнався баланс {Nick}", nick);
            return 0;
        }
    }

    /// <summary>
    /// Старт раунду, синхронно: запис у сховище, тоді списання. null — пішло; інакше текст відмови (нічого не списано,
    /// запис прибрано).
    /// </summary>
    public string? Start(KavunyRec rec)
    {
        try
        {
            lock (_gate)
            {
                var all = Read();
                all[Key(rec.Table, rec.No)] = rec;
                Write(all);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "кавуни: раунд {Table}:{No} не записався в касу", rec.Table, rec.No);
            return Kavuny.ClosedText;
        }
        bool ok;
        try { ok = _stakes.TrySpend(rec.Nick, rec.Stake, "kavuny-bet:kavuny", BetRef(rec.Table, rec.No, rec.Nick)); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "кавуни: ставка {Nick} не списалась", rec.Nick);
            ok = false;
        }
        if (ok) return null;
        Remove(rec.Table, rec.No);
        return $"Бракує черепків: у гаманці {Math.Max(0, Balance(rec.Nick))}";
    }

    /// <summary>
    /// Розрахувати раунд — у черзі каси: підсумок (<c>Return</c>, якщо не null) спершу в запис, щоб сирота знав, тоді
    /// виплата за тим, що лежить у записі, і запис геть. Запису вже нема — раунд розраховано раніше (відновлення,
    /// підмітання): нічого не робимо, інакше «ставку назад» могли б заплатити поверх виграшу.
    /// </summary>
    public void Settle(KavunyRec rec) => _defer(() =>
    {
        var stored = Record(rec);
        if (stored is null) return;
        try { SettleNow(stored); }
        catch (Exception ex) { _log?.LogWarning(ex, "кавуни: раунд {Table}:{No} не розрахувався", rec.Table, rec.No); }
    });

    /// <summary>Стан гри — у сховище з черги каси (тік у базу не ходить): раунд, що скінчився на тіку, не лишиться «живим».</summary>
    public void Keep(string key, string json) => _defer(() =>
    {
        try { _store.SaveState(key, json); }
        catch (Exception ex) { _log?.LogWarning(ex, "кавуни: стан {Key} не записався", key); }
    });

    public int Recover(DateTimeOffset olderThan)
    {
        List<KavunyRec> due;
        try
        {
            lock (_gate) due = [.. Read().Values.Where(p => p.At < olderThan)];
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "кавуни: не прочитав касу");
            return 0;
        }
        var done = 0;
        foreach (var rec in due)
        {
            if (Held(rec)) continue;
            try { if (SettleNow(rec)) done++; }
            catch (Exception ex) { _log?.LogWarning(ex, "кавуни: раунд {Table}:{No} не відновився", rec.Table, rec.No); }
        }
        if (done > 0) _log?.LogInformation("кавуни: каса розрахувала раундів після перерви: {Count}", done);
        return done;
    }

    public int Sweep() => Recover(_clock.UtcNow - OrphanAge);
    public int RecoverAtStart() => Recover(_startedAt);

    public IReadOnlyList<KavunyRec> Pending()
    {
        lock (_gate) return [.. Read().Values];
    }

    /// <summary>Чи лежить у касі запис раунду (ще не розраховано). null — касу не прочитати.</summary>
    public bool? Holds(string table, int no)
    {
        try
        {
            lock (_gate) return Read().ContainsKey(Key(table, no));
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "кавуни: не прочитав касу");
            return null;
        }
    }

    /// <summary>Чи раунд тримає жива кімната в <paramref name="rooms"/> (бере замок кімнати).</summary>
    public static bool HeldBy(Rooms rooms, string table, int no)
    {
        var colon = table.IndexOf(':');
        if (colon <= 0 || rooms.Find(table[..colon]) is not { } room) return false;
        lock (room.Sync) return room.Status == RoomStatus.Playing && room.Game is Kavuny game && game.Holds(table, no);
    }

    bool Held(KavunyRec rec)
    {
        if (_held is null) return false;
        try { return _held(rec.Table, rec.No); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "кавуни: не з'ясував, чи стіл {Table} живий", rec.Table);
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

    /// <summary>Зупинка сервера: дочекатись черги каси, щоб не лишити розрахунки сиротами.</summary>
    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        var until = DateTime.UtcNow.AddSeconds(5);
        while ((!_queue.IsEmpty || Volatile.Read(ref _draining) == 1) && DateTime.UtcNow < until && !ct.IsCancellationRequested)
            await Task.Delay(20, CancellationToken.None);
    }

    public static string BetRef(string table, int no, string nick) => $"kavuny-bet:{table}:{no}:{Rooms.NickKey(nick)}";
    public static string WinRef(string table, int no, string nick) => $"kavuny-win:{table}:{no}:{Rooms.NickKey(nick)}";
    public static string BackRef(string table, int no, string nick) => $"kavuny-back:{table}:{no}:{Rooms.NickKey(nick)}";

    /// <summary>Розрахунок за леджером: платимо, лише якщо списання цього раунду справді є. true — запис прибрано.</summary>
    bool SettleNow(KavunyRec rec)
    {
        var bet = BetRef(rec.Table, rec.No, rec.Nick);
        IReadOnlyList<LedgerMove>? moves;
        try { moves = _stakes.Moves(bet); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "кавуни: не прочитав леджер раунду {Table}:{No} — спробую згодом", rec.Table, rec.No);
            return false;
        }
        if (moves is null)
        {
            _log?.LogWarning("кавуни: леджера нема — раунд {Table}:{No} нікому не плачу", rec.Table, rec.No);
            Remove(rec.Table, rec.No);
            return true;
        }
        var row = moves.FirstOrDefault(m => string.Equals(m.Ref, bet, StringComparison.Ordinal));
        if (row is not null)
        {
            if (-row.Delta != rec.Stake)
            {
                _log?.LogWarning("кавуни: раунд {Table}:{No}: списано {Delta}, а в записі {Stake} — повертаю списане", rec.Table, rec.No, row.Delta, rec.Stake);
                _stakes.Grant(rec.Nick, Math.Abs(row.Delta), "kavuny-back:kavuny", BackRef(rec.Table, rec.No, rec.Nick));
            }
            else if (rec.Return is null) _stakes.Grant(rec.Nick, rec.Stake, "kavuny-back:kavuny", BackRef(rec.Table, rec.No, rec.Nick));
            else if (rec.Return > 0) _stakes.Grant(rec.Nick, rec.Return.Value, "kavuny-win:kavuny", WinRef(rec.Table, rec.No, rec.Nick));
        }
        Remove(rec.Table, rec.No);
        return true;
    }

    /// <summary>
    /// Підсумок раунду — у запис каси. Повертає запис, як він тепер лежить (null — запису нема: розраховано раніше).
    /// Підсумок, що вже є в записі, не переписуємо (Load «анулює» раунд, якого каса могла вже дочекатись).
    /// Касу не прочитати — null: краще підмітання згодом поверне ставку, ніж платити наосліп (і «назад», і виграш).
    /// </summary>
    KavunyRec? Record(KavunyRec rec)
    {
        try
        {
            lock (_gate)
            {
                var all = Read();
                var key = Key(rec.Table, rec.No);
                if (!all.TryGetValue(key, out var old)) return null;
                if (old.Return is not null || rec.Return is null) return old;
                var now = old with { Return = rec.Return };
                all[key] = now;
                Write(all);
                return now;
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "кавуни: підсумок раунду {Table}:{No} не записався", rec.Table, rec.No);
            return null;
        }
    }

    void Remove(string table, int no)
    {
        try
        {
            lock (_gate)
            {
                var all = Read();
                if (all.Remove(Key(table, no))) Write(all);
            }
        }
        catch (Exception ex) { _log?.LogWarning(ex, "кавуни: запис раунду {Table}:{No} не прибрався", table, no); }
    }

    static string Key(string table, int no) => $"{table}:{no}";

    Dictionary<string, KavunyRec> Read()
    {
        var json = _store.LoadState(StoreKey);
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.Ordinal);
        try
        {
            var all = JsonSerializer.Deserialize<Dictionary<string, KavunyRec>>(json, Json);
            return all is null ? new(StringComparer.Ordinal) : new(all, StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            _log?.LogWarning(ex, "кавуни: запис каси зіпсовано — починаю з порожньої");
            return new(StringComparer.Ordinal);
        }
    }

    void Write(Dictionary<string, KavunyRec> all) => _store.SaveState(StoreKey, JsonSerializer.Serialize(all, Json));
}

/// <summary>Підключення «Кавунів» у <see cref="GamesSetup"/>: налаштування й каса.</summary>
public static class KavunySetup
{
    public static IServiceCollection AddKavuny(IServiceCollection services)
    {
        services.AddOptions<KavunyOptions>().BindConfiguration("Kavuny");
        services.AddSingleton(sp => new KavunyBook(sp.GetRequiredService<IStakes>(), sp.GetRequiredService<IGameStore>(),
            sp.GetService<IClock>(), sp.GetService<ILogger<KavunyBook>>(),
            held: (table, no) => sp.GetService<Rooms>() is { } rooms && KavunyBook.HeldBy(rooms, table, no),
            options: sp.GetService<IOptionsMonitor<KavunyOptions>>()));
        services.AddHostedService(sp => sp.GetRequiredService<KavunyBook>());
        return services;
    }
}
