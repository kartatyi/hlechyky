using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>Вимикачі й числа слотів (appsettings.json → «Slots», наживо). docs/games/specs/slots.md §6.</summary>
public sealed class SlotsOptions
{
    /// <summary>false — «автомати на перерві»: оберт і Ворожка відмовляють, у /api/me slots=false, лобі ховає тему.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Дозволені ставки. Порожньо — типові <see cref="DefaultBets"/>.</summary>
    public int[]? Bets { get; set; }
    /// <summary>Стеля ставки (0 — без стелі).</summary>
    public int MaxBet { get; set; }
    /// <summary>Скільки відсотків кожної ставки йде в Скарбничку.</summary>
    public double JackpotPct { get; set; } = 1;
    /// <summary>З чого Скарбничка починає (і після виграшу).</summary>
    public int JackpotSeed { get; set; } = 10000;
    /// <summary>
    /// «Має впасти до»: оберт, на якому сума досягла б цієї межі, зриває Скарбничку гарантовано (0 — межі нема).
    /// Частина кожного внеску тоді відкладається на наступний старт (<see cref="SeedShare"/>), щоб у середньому
    /// виплачувалось рівно внесене (specs/slots.md §4).
    /// </summary>
    public int JackpotMustHit { get; set; } = 100_000;

    /// <summary>Межа діє: задана й більша за початкову суму.</summary>
    public bool MustHitOn => JackpotMustHit > 0 && JackpotMustHit > JackpotSeed && JackpotSeed > 0;

    /// <summary>
    /// Яка частка внеску йде не в суму, а в запас на наступний старт: f = 1 / (1 + ln(межа ÷ старт)). Чому так: при шансі
    /// p = внесок ÷ сума сума на мить падіння розподілена як P(не впала до x) = старт ÷ x, тож у середньому Скарбничка
    /// падає на старт × (1 + ln(межа ÷ старт)) (з межею — 33 026 при 10 000 / 100 000); запас за цикл (f × усі внески)
    /// мусить покрити старт: f × E[виплата] = старт. Без межі — 0 (старт дає дім, як і було).
    /// </summary>
    public double SeedShare => MustHitOn ? 1 / (1 + Math.Log((double)JackpotMustHit / JackpotSeed)) : 0;
    /// <summary>Виграш Скарбнички — ще й в ефір голосом Глека (подією живої реклами).</summary>
    public bool OnAir { get; set; } = true;

    public static readonly int[] DefaultBets = [10, 20, 50, 100, 200, 500];

    /// <summary>
    /// Найменша ставка: на дрібніших округлення виплат (не менше 1 черепка, «,5 — угору») задирає RTP понад 100 %
    /// (рецензія 09.10: ставка 1 — cascade 149 %, cluster 158 %; ставка 5 — cluster 99,6 %).
    /// </summary>
    public const int MinBet = 10;

    /// <summary>Ставки, які справді можна поставити: з конфігу (чи типові), не менше <see cref="MinBet"/>, не вище <see cref="MaxBet"/>.</summary>
    public int[] AllowedBets()
    {
        var src = Bets is { Length: > 0 } b ? b : DefaultBets;
        var ok = src.Where(x => x >= MinBet && (MaxBet <= 0 || x <= MaxBet)).Distinct().Order().ToArray();
        return ok.Length > 0 ? ok : [.. DefaultBets.Where(x => MaxBet <= 0 || x <= MaxBet)];
    }
}

/// <summary>Незакритий оберт (чи хід Ворожки): ставку от-от спишуть, виграш от-от нарахують. Ключі леджера — <c>{Ref}:bet|win|jp</c>.</summary>
public sealed record SlotPending(string Ref, string Nick, string GameId, int Bet, int Win, int Jackpot, DateTimeOffset At);

/// <summary>Рядок «Заносів тижня»: нік, автомат, множник, сума (і Скарбничка, якщо була), коли.</summary>
public sealed record SlotBigWin(string Nick, string Game, double Mult, int Win, int Jackpot, DateTimeOffset At);

/// <summary>Куди Скарбничка розповідає про свята: рядок у Балачки від Глека й подія в ефір.</summary>
public interface ISlotsWire
{
    void Chat(string text);
    /// <summary>Подія живої реклами (фраза Глека в чергу ефіру). Нема живої реклами — мовчки нічого.</summary>
    void Air(string kind, string nick, IReadOnlyList<(string Key, object Value)> values) { }
}

/// <summary>
/// Каса й Скарбничка автоматів (specs/slots.md §4–§5). Одинак на весь сайт:
/// <list type="bullet">
/// <item>гроші оберту — через <see cref="IStakes"/> з ключами леджера, з журналом наперед (<see cref="Open"/>/<see cref="Close"/>)
///   і відновленням після падіння процесу — як <see cref="RouletteBook"/>;</item>
/// <item>Скарбничка: кожна ставка додає <see cref="SlotsOptions.JackpotPct"/> %, таємничий шанс оберту p = внесок ÷ сума —
///   тоді в середньому виплачується рівно те, що внесли (крім початкового <see cref="SlotsOptions.JackpotSeed"/> — його дає дім);</item>
/// <item>«Заноси тижня» (≥ 20×, ≤ 500 записів) для <c>GET /api/slots/feed</c>.</item>
/// </list>
/// Сховище (<see cref="IGameStore"/>): сума Скарбнички й журнал — одним ключем <see cref="StoreKey"/>, заноси — <see cref="FeedKey"/>.
/// Суму не пишемо на кожен оберт (SQLite-запис на кожну пачку вже раз клав коло): пишемо разом із журналом
/// (а він — лише на виграшний оберт), на виграш Скарбнички одразу і раз на <see cref="FlushEvery"/>, якщо щось змінилось.
/// Падіння губить щонайбільше внески за ці секунди — гроші дому, не гравців.
/// </summary>
public sealed class SlotsBank : BackgroundService
{
    public const string StoreKey = "slots:bank";
    public const string FeedKey = "slots:feed";
    public const double FeedMinMult = 20;
    public const int FeedMax = 500;
    public const double ChatMinMult = 100;
    public static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(10);
    /// <summary>Оберт живе мілісекунди; запис журналу, старший за це, — оберт, який не дорахувався (виняток, падіння).</summary>
    public static readonly TimeSpan OrphanAge = TimeSpan.FromMinutes(2);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    sealed class Saved
    {
        public double Pot { get; set; }
        /// <summary>Відкладене з внесків на наступні старти мінус уже взяте на них (може бути й від'ємним — тоді доклав дім).</summary>
        public double Reserve { get; set; }
        public Dictionary<string, SlotPending> Pending { get; set; } = new(StringComparer.Ordinal);
    }

    readonly IStakes _stakes;
    readonly IGameStore _store;
    readonly IClock _clock;
    readonly Func<SlotsOptions> _opts;
    readonly ISlotsWire? _wire;
    readonly ILogger<SlotsBank>? _log;
    readonly Action<Action> _defer;
    readonly DateTimeOffset _startedAt;
    readonly object _gate = new();
    readonly Saved _s;
    readonly List<SlotBigWin> _feed;
    bool _dirty;

    /// <param name="defer">Як виконати розмову з Балачками поза замком кімнати: типово — пул потоків; тести — <c>a =&gt; a()</c>.</param>
    public SlotsBank(IStakes stakes, IGameStore store, Func<SlotsOptions> options, IClock? clock = null, ISlotsWire? wire = null,
        ILogger<SlotsBank>? log = null, Action<Action>? defer = null)
    {
        _stakes = stakes;
        _store = store;
        _opts = options;
        _clock = clock ?? new SystemClock();
        _wire = wire;
        _log = log;
        _defer = defer ?? (a => ThreadPool.QueueUserWorkItem(_ => a()));
        _startedAt = _clock.UtcNow;
        _s = Load<Saved>(StoreKey) ?? new Saved();
        if (_s.Pot <= 0 || double.IsNaN(_s.Pot)) _s.Pot = Math.Max(0, O.JackpotSeed);
        _feed = Load<List<SlotBigWin>>(FeedKey) ?? [];
    }

    public SlotsOptions O => _opts();

    /// <summary>Сума Скарбнички зараз (ціла — те, що виплатиться).</summary>
    public int Pot { get { lock (_gate) return (int)Math.Floor(_s.Pot); } }

    /// <summary>Скільки в гаманці. Економіка впала — 0 (тоді ставка просто не пройде).</summary>
    public int Balance(string nick)
    {
        try { return _stakes.Balance(nick); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "слоти: не дізнався баланс {Nick}", nick);
            return 0;
        }
    }

    // ---------- Скарбничка ----------

    /// <summary>Відкладено на наступні старти (див. <see cref="SlotsOptions.SeedShare"/>).</summary>
    public double Reserve { get { lock (_gate) return _s.Reserve; } }

    /// <summary>
    /// Внесок ставки й таємничий шанс: p = внесок у суму ÷ сума (після внеску). З межею (<see cref="SlotsOptions.JackpotMustHit"/>)
    /// частка <see cref="SlotsOptions.SeedShare"/> внеску йде в запас на наступний старт, а оберт, на якому сума дійшла б
    /// до межі, зриває Скарбничку гарантовано (той, хто доклав останній внесок). Виграв — сума обнуляється до початкової
    /// (надлишок понад межу переходить у нову суму) й одразу пишеться в сховище (разом із журналом це робить
    /// <see cref="Open"/>), повертається виграш; ні — 0.
    /// </summary>
    public int Feed(int bet, ISlotRng rng)
    {
        lock (_gate)
        {
            var o = O;
            var put = bet * Math.Max(0, o.JackpotPct) / 100.0;
            if (put <= 0) return 0;
            var share = o.SeedShare;
            var add = put * (1 - share);
            _s.Reserve += put * share;
            _s.Pot += add;
            _dirty = true;
            var must = o.MustHitOn && _s.Pot >= o.JackpotMustHit;
            if (!must && rng.NextDouble() >= add / _s.Pot) return 0;
            var seed = Math.Max(0, o.JackpotSeed);
            var won = (int)Math.Floor(must ? Math.Min(_s.Pot, o.JackpotMustHit) : _s.Pot);
            var over = _s.Pot - won;   // копійки й надлишок понад межу — у нову суму, не губимо
            _s.Pot = seed + over;
            _s.Reserve -= seed;
            return won;
        }
    }

    /// <summary>Ставку не списано — внесок назад, а виграну Скарбничку повертаємо в суму (дім своє не губить).</summary>
    public void Unfeed(int bet, int jackpot)
    {
        lock (_gate)
        {
            var o = O;
            var put = bet * Math.Max(0, o.JackpotPct) / 100.0;
            var share = o.SeedShare;
            if (jackpot > 0)
            {
                var seed = Math.Max(0, o.JackpotSeed);
                _s.Pot += jackpot - seed;
                _s.Reserve += seed;
            }
            _s.Pot = Math.Max(0, _s.Pot - put * (1 - share));
            _s.Reserve -= put * share;
            _dirty = true;
        }
    }

    // ---------- журнал наперед ----------

    /// <summary>
    /// Записати оберт у журнал — синхронно, ДО списання (і разом — суму Скарбнички). false — не записалось: не крутимо.
    /// Чому так: якщо процес впаде між списанням і нарахуванням, відновлення на старті побачить запис, знайде списання
    /// в леджері за ключем <c>{Ref}:bet</c> і нарахує <c>{Ref}:win</c>/<c>{Ref}:jp</c>; якщо впаде до списання — списання
    /// нема, запис просто прибирається. Ключі ідемпотентні, тож гра й відновлення разом не заплатять двічі.
    /// </summary>
    public bool Open(SlotPending p)
    {
        try
        {
            lock (_gate)
            {
                _s.Pending[p.Ref] = p;
                WriteBank();
            }
            return true;
        }
        catch (Exception ex)
        {
            lock (_gate) _s.Pending.Remove(p.Ref);
            _log?.LogWarning(ex, "слоти: оберт {Ref} не записався в журнал", p.Ref);
            return false;
        }
    }

    /// <summary>Списати ставку: true — списано, false — бракує або не списалось (див. <see cref="Charge"/>).</summary>
    public bool Take(string nick, int amount, string reason, string refKey) => Charge(nick, amount, reason, refKey) == true;

    /// <summary>
    /// Списати ставку: true — списано, false — бракує. Виняток (таймаут, збій після коміту) — звіряємось із леджером за
    /// ключем, як відновлення: списання є — true, нема — false; леджер теж мовчить — null (не знаємо: запис журналу
    /// не чіпати, підмітання за 2 хв саме гляне в леджер і доплатить або прибере).
    /// </summary>
    public bool? Charge(string nick, int amount, string reason, string refKey)
    {
        try { return _stakes.TrySpend(nick, amount, reason, refKey); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "слоти: ставка {Nick} ({Ref}) — виняток, звіряю з леджером", nick, refKey);
            try
            {
                var moves = _stakes.Moves(refKey);
                if (moves is null) return false;   // без економіки — і ставки не було
                return moves.Any(m => m.Ref == refKey && m.Delta < 0);
            }
            catch (Exception ex2)
            {
                _log?.LogWarning(ex2, "слоти: леджер {Ref} теж не відповів", refKey);
                return null;
            }
        }
    }

    /// <summary>Нарахувати виграш і Скарбничку запису й прибрати його з журналу (на диск — з найближчим записом). false — лишився.</summary>
    public bool Close(SlotPending p)
    {
        if (!Pay(p)) return false;
        Forget(p.Ref);
        return true;
    }

    /// <summary>Прибрати запис, за який нічого не списано.</summary>
    public void Forget(string reference)
    {
        lock (_gate) if (_s.Pending.Remove(reference)) _dirty = true;
    }

    bool Pay(SlotPending p)
    {
        try
        {
            if (p.Win > 0) _stakes.Grant(p.Nick, p.Win, $"slot-win:{p.GameId}", p.Ref + ":win");
            if (p.Jackpot > 0) _stakes.Grant(p.Nick, p.Jackpot, $"slot-jackpot:{p.GameId}", p.Ref + ":jp");
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "слоти: виплата {Ref} не пройшла — спробую згодом", p.Ref);
            return false;
        }
    }

    /// <summary>
    /// Розрахувати записи журналу, старші за <paramref name="olderThan"/>: є списання <c>{Ref}:bet</c> у леджері — платимо
    /// (ідемпотентно), нема — прибираємо. Повертає, скільки розраховано.
    /// </summary>
    public int Recover(DateTimeOffset olderThan)
    {
        List<SlotPending> due;
        lock (_gate) due = [.. _s.Pending.Values.Where(p => p.At < olderThan)];
        var done = 0;
        foreach (var p in due)
        {
            IReadOnlyList<LedgerMove>? moves;
            try { moves = _stakes.Moves(p.Ref + ":bet"); }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "слоти: не прочитав леджер {Ref}", p.Ref);
                continue;
            }
            if (moves is null || !moves.Any(m => m.Ref == p.Ref + ":bet" && m.Delta < 0))
            {
                // Без економіки (moves null) не знаємо, — але тоді й ставки не було. Без списання — нічого не винні.
                Forget(p.Ref);
                done++;
                continue;
            }
            if (Close(p)) done++;
        }
        if (done > 0)
        {
            lock (_gate) Flush();
            _log?.LogInformation("слоти: журнал розрахував обертів після перерви: {Count}", done);
        }
        return done;
    }

    public int RecoverAtStart() => Recover(_startedAt);

    public IReadOnlyList<SlotPending> Pending()
    {
        lock (_gate) return [.. _s.Pending.Values];
    }

    // ---------- заноси й свята ----------

    /// <summary>
    /// Після оберту: заноси ≥ 20× — у журнал тижня, ≥ 100× і Скарбничка — рядок у Балачки (і Скарбничка — в ефір).
    /// Кличеться з гри під замком кімнати: сховище — лише на рідкісні заноси, Балачки — відкладено.
    /// </summary>
    public void Brag(string nick, string gameId, string title, int bet, int win, int jackpot)
    {
        var mult = bet > 0 ? (double)win / bet : 0;
        if (mult >= FeedMinMult || jackpot > 0)
        {
            try
            {
                lock (_gate)
                {
                    _feed.Add(new SlotBigWin(nick, gameId, Math.Round(mult, 2), win, jackpot, _clock.UtcNow));
                    if (_feed.Count > FeedMax) _feed.RemoveRange(0, _feed.Count - FeedMax);
                    _store.SaveState(FeedKey, JsonSerializer.Serialize(_feed, Json));
                }
            }
            catch (Exception ex) { _log?.LogWarning(ex, "слоти: занос {Nick} не записався", nick); }
        }
        if (_wire is null) return;
        string? text = null;
        if (jackpot > 0) text = SlotLines.Jackpot(nick, title, jackpot);
        else if (mult >= ChatMinMult) text = SlotLines.Big(nick, title, mult, win);
        if (text is null) return;
        var onAir = jackpot > 0 && O.OnAir;
        _defer(() =>
        {
            try { _wire.Chat(text); }
            catch (Exception ex) { _log?.LogWarning(ex, "слоти: рядок у Балачки не пішов"); }
            if (!onAir) return;
            try { _wire.Air("slot_jackpot", nick, [("nick", nick), ("n", jackpot), ("game", title)]); }
            catch (Exception ex) { _log?.LogWarning(ex, "слоти: подія в ефір не пішла"); }
        });
    }

    /// <summary>«Заноси тижня»: найбільші за 7 днів (спершу Скарбничка, далі множник), до <paramref name="take"/>.</summary>
    public IReadOnlyList<SlotBigWin> Week(int take = 20)
    {
        var from = _clock.UtcNow.AddDays(-7);
        lock (_gate)
            return [.. _feed.Where(w => w.At >= from).OrderByDescending(w => w.Jackpot > 0).ThenByDescending(w => w.Mult)
                .ThenByDescending(w => w.At).Take(take)];
    }

    /// <summary>Відповідь <c>GET /api/slots/feed</c>.</summary>
    public object FeedView()
    {
        var o = O;
        return new
        {
            on = o.Enabled,
            jackpot = Pot,
            mustHit = o.MustHitOn ? o.JackpotMustHit : (int?)null,   // null — поля нема (JsonIgnore у SlotsSetup)
            bets = o.AllowedBets(),
            wins = Week().Select(w => new { nick = w.Nick, game = w.Game, mult = w.Mult, win = w.Win, jackpot = w.Jackpot, at = w.At.UtcDateTime }),
        };
    }

    // ---------- сховище ----------

    /// <summary>Записати суму й журнал, якщо щось змінилось. Під <see cref="_gate"/>.</summary>
    void Flush()
    {
        if (!_dirty) return;
        try { WriteBank(); }
        catch (Exception ex) { _log?.LogWarning(ex, "слоти: Скарбничка не записалась"); }
    }

    public void FlushNow()
    {
        lock (_gate) Flush();
    }

    void WriteBank()
    {
        _store.SaveState(StoreKey, JsonSerializer.Serialize(_s, Json));
        _dirty = false;
    }

    T? Load<T>(string key) where T : class
    {
        try
        {
            var json = _store.LoadState(key);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "слоти: запис {Key} зіпсовано — починаю з чистого", key);
            return null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        RecoverAtStart();
        using var timer = new PeriodicTimer(FlushEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                Recover(_clock.UtcNow - OrphanAge);
                FlushNow();
            }
        }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        FlushNow();
    }
}

/// <summary>Репліки Глека для Балачок — коротко й смішно.</summary>
public static class SlotLines
{
    public static string Jackpot(string nick, string title, int amount) =>
        $"🎰 Скарбничка Глека в «{title}» — до {nick}: +{amount} {Shards(amount)}! Глек пішов по нову. Й по валер'янку";

    public static string Big(string nick, string title, double mult, int win) =>
        $"🎰 {nick} — ×{Math.Floor(mult)} у «{title}»: +{win} {Shards(win)}. Глек перевіряє, чи ручка не відкручена";

    static string Shards(int n)
    {
        var d = n % 100;
        if (d is >= 11 and <= 14) return "черепків";
        return (n % 10) switch { 1 => "черепок", 2 or 3 or 4 => "черепки", _ => "черепків" };
    }
}

/// <summary>Балачки (рядок «dj» від Глека, як Падельня) і подія живої реклами.</summary>
public sealed class HubSlotsWire(Db db, IHubContext<RadioHub> hub, IOptionsMonitor<SiteOptions> site, IServiceProvider sp,
    ILogger<HubSlotsWire> log) : ISlotsWire
{
    public void Chat(string text)
    {
        var line = db.AddChat(site.CurrentValue.DjName is { Length: > 0 } dj ? dj : "Дядько Глек", text, "dj");
        _ = hub.Clients.All.SendAsync("chat", line).ContinueWith(t => log.LogWarning(t.Exception, "слоти: рядок у Балачки не розіслався"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Air(string kind, string nick, IReadOnlyList<(string Key, object Value)> values)
    {
        if (sp.GetService<LiveAds>() is not { } ads) return;
        ads.Tell(kind, nick, LiveFact.Of(kind, 1, [.. values]));
    }
}

/// <summary>Підключення слотів у <see cref="GamesSetup"/>: опції, каса-Скарбничка, Балачки, <c>/api/slots/feed</c>.</summary>
public static class SlotsSetup
{
    public static IServiceCollection AddSlots(IServiceCollection services)
    {
        services.AddOptions<SlotsOptions>().BindConfiguration("Slots");
        services.AddSingleton<ISlotsWire, HubSlotsWire>();
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptionsMonitor<SlotsOptions>>();
            return new SlotsBank(sp.GetRequiredService<IStakes>(), sp.GetRequiredService<IGameStore>(), () => opts.CurrentValue,
                sp.GetService<IClock>(), sp.GetService<ISlotsWire>(), sp.GetService<ILogger<SlotsBank>>());
        });
        services.AddHostedService(sp => sp.GetRequiredService<SlotsBank>());
        return services;
    }

    /// <summary>
    /// <c>GET /api/slots/feed</c> → <c>{ on, jackpot, bets, wins }</c>. Живе оновлення суми — опитуванням раз на 5–10 с
    /// (лише ті, хто дивиться лобі «Азарт» чи автомат; відповідь з пам'яті), а не подією хабу на кожен оберт усім.
    /// </summary>
    /// <summary>Як сайт (camelCase), але без null-полів: <c>mustHit</c> нема, коли межу вимкнено.</summary>
    public static readonly JsonSerializerOptions FeedJson = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static void MapSlots(WebApplication app) =>
        app.MapGet("/api/slots/feed", (SlotsBank bank) => Results.Json(bank.FeedView(), FeedJson));
}
