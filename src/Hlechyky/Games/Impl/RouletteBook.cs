using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Один гравець закритого кола: скільки списано й скільки повернути. <c>Return</c> — Σ ставка×(k+1) по ставках, що зіграли; 0 — програв.</summary>
public sealed record SpinPay(string Nick, int Stake, int Return);

/// <summary>
/// Закрите коло, яке ще не виплачено: число вже вирішене, ставки списано (чи списуються). <c>Table</c> — «{кімната}:{epoch}»,
/// <c>Spin</c> — номер кола в стані гри. Лежить у сховищі від закриття до виплати — щоб падіння процесу посеред кола
/// розрахувалось за вже вирішеним числом.
/// </summary>
public sealed record PendingSpin(string Table, int Spin, string GameId, int Number, DateTimeOffset At, IReadOnlyList<SpinPay> Pays);

/// <summary>
/// Каса рулетки (docs/games/specs/roulette.md §3.3, §3.5): списання за коло, виплати, відновлення після перезапуску.
/// Банк столу каркаса тут не годиться — він нуль-сумовий, а дім платить 35:1 зі своєї кишені. Тому каса ходить до
/// <see cref="IStakes"/> сама, а кожне закрите коло пише в сховище (<see cref="StoreKey"/>), поки його не виплачено.
/// Усе ідемпотентно за ключами леджера: гра, відновлення на старті й нічне підмітання можуть розрахувати те саме коло —
/// гроші підуть раз.
/// </summary>
public sealed class RouletteBook : BackgroundService
{
    /// <summary>JSON-словник «{table}:{spin}» → <see cref="PendingSpin"/> у <see cref="IGameStore"/>.</summary>
    public const string StoreKey = "roulette:pending";
    /// <summary>Як часто підмітати сиріт — кола, чий стіл зник, не дочекавшись виплати.</summary>
    public static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(60);
    /// <summary>Коло живе ≤ 6 с плюс ≤ 60 с заморозки; запис, старший за це, — сирота.</summary>
    public static readonly TimeSpan OrphanAge = TimeSpan.FromMinutes(2);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly IStakes _stakes;
    readonly IGameStore _store;
    readonly IClock _clock;
    readonly ILogger<RouletteBook>? _log;
    readonly Action<Action> _defer;
    readonly Func<string, int, bool>? _held;
    readonly DateTimeOffset _startedAt;
    readonly object _gate = new();

    /// <param name="defer">Як виконати виплату поза замком кімнати: типово — пул потоків; тести — <c>a =&gt; a()</c>.</param>
    /// <param name="held">
    /// Чи коло («{table}», номер) зараз тримає живий стіл — тоді кульку доведе й виплатить сама гра, а відновлення й
    /// підмітання його не чіпають (інакше після перезапуску чи довгої паузи гроші приходили б раніше, ніж лягла кулька).
    /// null — таких столів не буває (тести, каса без кімнат).
    /// </param>
    public RouletteBook(IStakes stakes, IGameStore store, IClock? clock = null, ILogger<RouletteBook>? log = null,
        Action<Action>? defer = null, Func<string, int, bool>? held = null)
    {
        _stakes = stakes;
        _store = store;
        _clock = clock ?? new SystemClock();
        _log = log;
        _defer = defer ?? (a => ThreadPool.QueueUserWorkItem(_ => a()));
        _held = held;
        // Мить народження каси в цьому процесі: усе, що старше, лишилось від старого процесу.
        _startedAt = _clock.UtcNow;
    }

    /// <summary>Скільки в гаманці. Економіка впала — 0 (тоді ставка просто не пройде).</summary>
    public int Balance(string nick)
    {
        try { return _stakes.Balance(nick); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "рулетка: не дізнався баланс {Nick}", nick);
            return 0;
        }
    }

    /// <summary>Записати закрите коло в сховище — синхронно, ДО першого списання. false — не записалось: коло не крутимо.</summary>
    public bool Open(PendingSpin spin)
    {
        try
        {
            lock (_gate)
            {
                var all = Read();
                all[Key(spin.Table, spin.Spin)] = spin;
                Write(all);
            }
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "рулетка: коло {Table}:{Spin} не записалось у касу", spin.Table, spin.Spin);
            return false;
        }
    }

    /// <summary>Списати ставки кола з гаманця — синхронно. Виняток — false (нічого не списано, або повтор ключа нічого не з'їсть).</summary>
    public bool Take(string nick, int amount, string reason, string refKey)
    {
        try { return _stakes.TrySpend(nick, amount, reason, refKey); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "рулетка: ставка {Nick} ({Ref}) не списалась", nick, refKey);
            return false;
        }
    }

    /// <summary>Прибрати запис кола, за яке ніхто не заплатив.</summary>
    public void Drop(string table, int spin) => Remove(table, spin);

    /// <summary>Виплатити коло й прибрати запис — відкладено (поза замком кімнати).</summary>
    public void Settle(PendingSpin spin) => _defer(() =>
    {
        try { SettleNow(spin); }
        catch (Exception ex) { _log?.LogWarning(ex, "рулетка: коло {Table}:{Spin} не розрахувалось", spin.Table, spin.Spin); }
    });

    /// <summary>
    /// Розрахувати всі записи, закриті раніше за <paramref name="olderThan"/>, крім тих, що тримає живий стіл
    /// (<c>held</c>). Повертає, скільки розраховано.
    /// </summary>
    public int Recover(DateTimeOffset olderThan)
    {
        List<PendingSpin> due;
        try
        {
            lock (_gate) due = [.. Read().Values.Where(p => p.At < olderThan)];
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "рулетка: не прочитав касу");
            return 0;
        }
        var done = 0;
        foreach (var spin in due)
        {
            // Поза _gate: перевірка бере замок кімнати, а гра під ним ходить у касу (Open) — навпаки був би глухий кут.
            if (Held(spin)) continue;
            try { if (SettleNow(spin)) done++; }
            catch (Exception ex) { _log?.LogWarning(ex, "рулетка: коло {Table}:{Spin} не відновилось", spin.Table, spin.Spin); }
        }
        if (done > 0) _log?.LogInformation("рулетка: каса розрахувала кіл після перерви: {Count}", done);
        return done;
    }

    /// <summary>Підмітання сиріт: записи, старші за <see cref="OrphanAge"/>.</summary>
    public int Sweep() => Recover(_clock.UtcNow - OrphanAge);

    /// <summary>
    /// Старт: розрахувати все, що лишилось від старого процесу, — лише записи, старші за народження каси. Коло, яке
    /// відновлений стіл відкрив уже в цьому процесі, — його, не наше.
    /// </summary>
    public int RecoverAtStart() => Recover(_startedAt);

    /// <summary>Записи, що зараз лежать у касі (для тестів і діагностики).</summary>
    public IReadOnlyList<PendingSpin> Pending()
    {
        lock (_gate) return [.. Read().Values];
    }

    /// <summary>Чи лежить у касі запис кола (тобто його ще не виплачено). null — касу не прочитати.</summary>
    public bool? Holds(string table, int spin)
    {
        try
        {
            lock (_gate) return Read().ContainsKey(Key(table, spin));
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "рулетка: не прочитав касу");
            return null;
        }
    }

    /// <summary>
    /// Чи коло тримає живий стіл у <paramref name="rooms"/>: кімната з id до першої «:» у <paramref name="table"/> грає, і
    /// її рулетка чекає саме на це коло. Бере замок кімнати.
    /// </summary>
    public static bool HeldBy(Rooms rooms, string table, int spin)
    {
        var colon = table.IndexOf(':');
        if (colon <= 0 || rooms.Find(table[..colon]) is not { } room) return false;
        lock (room.Sync) return room.Status == RoomStatus.Playing && room.Game is RouletteGame game && game.Holds(table, spin);
    }

    bool Held(PendingSpin spin)
    {
        if (_held is null) return false;
        try { return _held(spin.Table, spin.Spin); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "рулетка: не з'ясував, чи стіл {Table} живий", spin.Table);
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Старт: усе, що лишилось від старого процесу, — розрахувати за вже вирішеними числами. Столи, які повернув
        // TablesKeeper.RestoreAtStart (він у Program.cs іде раніше за хостовані сервіси), доводять свої кола самі.
        RecoverAtStart();
        using var timer = new PeriodicTimer(SweepEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) Sweep();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Виплата за леджером: платимо лише тим, у кого справді є списання цього кола (впало між записом і списанням — нікому
    /// нічого). Сума списання не та, що в записі, — запис зіпсовано: повертаємо списане. true — запис прибрано.
    /// </summary>
    bool SettleNow(PendingSpin spin)
    {
        var prefix = $"roulette-bet:{spin.Table}:{spin.Spin}:";
        IReadOnlyList<LedgerMove>? moves;
        try { moves = _stakes.Moves(prefix); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "рулетка: не прочитав леджер кола {Table}:{Spin} — спробую згодом", spin.Table, spin.Spin);
            return false;
        }
        if (moves is null)
        {
            _log?.LogWarning("рулетка: леджера нема — коло {Table}:{Spin} нікому не плачу", spin.Table, spin.Spin);
            Remove(spin.Table, spin.Spin);
            return true;
        }
        var clean = true;
        foreach (var pay in spin.Pays)
        {
            var key = Rooms.NickKey(pay.Nick);
            var row = moves.FirstOrDefault(m => string.Equals(m.Ref, prefix + key, StringComparison.Ordinal));
            if (row is null) continue;   // не заплатив — і не отримає
            try
            {
                if (-row.Delta == pay.Stake)
                {
                    if (pay.Return > 0)
                        _stakes.Grant(pay.Nick, pay.Return, $"roulette-win:{spin.GameId}", $"roulette-win:{spin.Table}:{spin.Spin}:{key}");
                }
                else
                {
                    _log?.LogWarning("рулетка: коло {Table}:{Spin}, {Nick}: списано {Delta}, а в записі {Stake} — повертаю списане",
                        spin.Table, spin.Spin, pay.Nick, row.Delta, pay.Stake);
                    _stakes.Grant(pay.Nick, Math.Abs(row.Delta), $"roulette-back:{spin.GameId}", $"roulette-back:{spin.Table}:{spin.Spin}:{key}");
                }
            }
            catch (Exception ex)
            {
                clean = false;   // запис лишається: підмітання спробує ще (ключі не дадуть заплатити двічі)
                _log?.LogWarning(ex, "рулетка: виплата {Nick} за коло {Table}:{Spin} не пройшла", pay.Nick, spin.Table, spin.Spin);
            }
        }
        if (clean) Remove(spin.Table, spin.Spin);
        return clean;
    }

    void Remove(string table, int spin)
    {
        try
        {
            lock (_gate)
            {
                var all = Read();
                if (all.Remove(Key(table, spin))) Write(all);
            }
        }
        catch (Exception ex) { _log?.LogWarning(ex, "рулетка: запис кола {Table}:{Spin} не прибрався", table, spin); }
    }

    static string Key(string table, int spin) => $"{table}:{spin}";

    /// <summary>Під <see cref="_gate"/>. Зіпсований запис — лог і порожній словник (інакше каса не відкрилась би ніколи).</summary>
    Dictionary<string, PendingSpin> Read()
    {
        var json = _store.LoadState(StoreKey);
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.Ordinal);
        try
        {
            var all = JsonSerializer.Deserialize<Dictionary<string, PendingSpin>>(json, Json);
            return all is null ? new(StringComparer.Ordinal) : new(all, StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            _log?.LogWarning(ex, "рулетка: запис каси зіпсовано — починаю з порожньої");
            return new(StringComparer.Ordinal);
        }
    }

    void Write(Dictionary<string, PendingSpin> all) => _store.SaveState(StoreKey, JsonSerializer.Serialize(all, Json));
}

/// <summary>Підключення рулетки в <see cref="GamesSetup"/>: каса (виплати, відновлення після перезапуску).</summary>
public static class RouletteSetup
{
    public static IServiceCollection AddRoulette(IServiceCollection services)
    {
        services.AddSingleton(sp => new RouletteBook(sp.GetRequiredService<IStakes>(), sp.GetRequiredService<IGameStore>(),
            sp.GetService<IClock>(), sp.GetService<ILogger<RouletteBook>>(),
            // Кімнати — ліниво, у мить перевірки: касу першою просить гра (Configure), тобто вже зсередини Rooms.
            held: (table, spin) => sp.GetService<Rooms>() is { } rooms && RouletteBook.HeldBy(rooms, table, spin)));
        services.AddHostedService(sp => sp.GetRequiredService<RouletteBook>());
        return services;
    }
}
