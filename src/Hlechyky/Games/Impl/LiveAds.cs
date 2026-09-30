using Hlechyky.Games.Economy;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Налаштування живої реклами (секція <c>LiveAds</c>). Увімкнення й частку господар ще й перемикає у вкладці
/// «📣 Реклама» (живе в базі); тут — типові значення й усе, що підкручується рідко.
/// </summary>
public sealed class LiveAdsOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Яка частка звичайних слотів реклами дістається живій прожарці (решта — бібліотека).</summary>
    public double Share { get; set; } = 0.4;
    /// <summary>Темп Глека й Поліни в рекламі — свій, не ведучого «Своєї гри».</summary>
    public string Rate { get; set; } = "-4%";
    /// <summary>Спортивний блок новин — голосом коментатора.</summary>
    public string SportRate { get; set; } = "+12%";
    public string Sign { get; set; } = "Глечики. Слухай, як гуде.";
    public int TargetCooldownMinutes { get; set; } = 45;
    /// <summary>Той самий факт тій самій людині — не частіше ніж раз на стільки годин.</summary>
    public int FactCooldownHours { get; set; } = 24;

    public int OrderPrice { get; set; } = 100;
    public int OrderAnonPrice { get; set; } = 150;
    public int OrderDailyPerBuyer { get; set; } = 3;
    public int OrderDailyPerTarget { get; set; } = 3;
    /// <summary>Ціль замовлення — акаунт, що заходив за стільки днів.</summary>
    public int ActiveDays { get; set; } = 14;
    /// <summary>Замовлення, що так і не прозвучало (ефір мовчав), за стільки годин повертає черепки.</summary>
    public int OrderExpireHours { get; set; } = 6;

    public int EventCooldownMinutes { get; set; } = 60;
    public int EventsPerHour { get; set; } = 3;
    public int EventFreshMinutes { get; set; } = 30;
    public int StreakEvent { get; set; } = 5;
    public int BigBuy { get; set; } = 500;

    public int NewsFrom { get; set; } = 20;
    public int NewsTo { get; set; } = 23;

    public int TickSeconds { get; set; } = 20;
    public int RenderTimeoutSeconds { get; set; } = 120;
    /// <summary>Скільки останніх файлів лишати для адміна і скільки хвилин тримати відіграні.</summary>
    public int KeepFiles { get; set; } = 20;
    public int KeepMinutes { get; set; } = 60;
    public string BedsDir { get; set; } = "data/liveads/beds";
    public string Lines { get; set; } = "data/liveads/lines.json";
}

/// <summary>Готовий живий ролик для ефіру.</summary>
public sealed record LiveClip(long Id, string TrackId, string Title, int Seconds, string FilePath);

/// <summary>Що джинглу треба від живої реклами: замовлене — першим, решта — коли настав слот.</summary>
public interface ILiveAdSource
{
    /// <summary>Готова замовлена прожарка (іде на найближчій межі треку).</summary>
    LiveClip? TakeOrdered();
    /// <summary>Реакція, новини або жива прожарка; null — хай грає бібліотека. <paramref name="libraryEmpty"/> — частка не діє.</summary>
    LiveClip? Take(bool libraryEmpty);
    /// <summary>Ефір прийняв ролик у чергу (ok) чи відмовив — тоді ролик знову чекає.</summary>
    void Sent(LiveClip clip, bool ok);
    /// <summary>Ролик заграв.</summary>
    void Started(string trackId);
}

/// <summary>Відповідь замовлення: чи вдалось, що сказати, баланс і номер ролика.</summary>
public sealed record LiveOrderReply(bool Ok, string Message, int? Balance = null, long? Id = null);

/// <summary>
/// Жива реклама: факти → сценарій з банку фраз → рендер фоном → готовий ролик чекає слоту в <see cref="AdJingle"/>.
/// Порядок у слоті: замовлена прожарка, свіжа реакція на подію, новини дня, з імовірністю <c>Share</c> — прожарка
/// когось присутнього, інакше бібліотека. Прожарка присутніх готується заздалегідь (одна наперед), щоб слот не
/// чекав edge-tts. Усе, що не вийшло (нема мережі, ffmpeg, TTS), просто не робить ролика — ефір не помічає.
/// </summary>
public sealed class LiveAds(LiveAdsStore store, LiveFacts facts, ILiveRenderer renderer, Economy.Economy economy, Presence presence,
    IOutbox outbox, IClock clock, IOptionsMonitor<LiveAdsOptions> options, IOptionsMonitor<CurfewOptions> curfew,
    IOptionsMonitor<YtDlpOptions> yt, ILogger<LiveAds> log) : BackgroundService, ILiveAdSource
{
    public const string Reason = "liveads:order";
    public const string Anonymous = "таємного шанувальника";

    readonly object _lock = new();
    readonly SemaphoreSlim _signal = new(0);
    readonly SemaphoreSlim _tick = new(1, 1);
    long? _resultsMark, _lavkaMark, _accountsMark;
    string? _curfewDay;
    LiveLines? _lines;
    DateTime _linesAt;

    LiveAdsOptions O => options.CurrentValue;

    /// <summary>Випадковість — для тестів підміняється.</summary>
    public Random Rng { get; set; } = new();
    public Func<double> Roll { get; set; } = Random.Shared.NextDouble;
    /// <summary>Каталог готових роликів (той самий кеш, що в голосових); тести дають свій.</summary>
    public string CacheDir { get; set; } = "";

    string Cache => CacheDir.Length > 0 ? CacheDir : Paths.Resolve(yt.CurrentValue.CacheDir);

    /// <summary>Банк фраз; перечитується, коли файл змінився, — правити жарти можна без перезапуску.</summary>
    public LiveLines Lines
    {
        get
        {
            var path = Paths.Resolve(O.Lines);
            lock (_lock)
            {
                try
                {
                    var at = File.GetLastWriteTimeUtc(path);
                    if (_lines is null || at != _linesAt) { _lines = LiveLines.Load(path); _linesAt = at; facts.Say = _lines.Say; }
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
                {
                    if (_lines is null) log.LogWarning(ex, "банк фраз живої реклами не читається ({Path})", path);
                    _lines ??= new LiveLines();
                }
                return _lines;
            }
        }
        set { lock (_lock) { _lines = value; _linesAt = DateTime.MaxValue; facts.Say = value.Say; } }
    }

    // =================================================================================================================
    // Увімкнення й частка (господар у вкладці «📣 Реклама»)
    // =================================================================================================================

    public bool Enabled => O.Enabled && store.State("enabled") != "0";

    public double Share => double.TryParse(store.State("share"), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : O.Share;

    public (bool Ok, string Message) SetEnabled(bool on)
    {
        store.SetState("enabled", on ? "1" : "0");
        return (true, on ? "Жива реклама ввімкнена" : "Жива реклама вимкнена — крутиться лише бібліотека");
    }

    public (bool Ok, string Message) SetShare(double share)
    {
        if (share is < 0 or > 1 || double.IsNaN(share)) return (false, "Частка — від 0 до 1");
        store.SetState("share", share.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        return (true, $"Живої реклами — {Math.Round(share * 100)} % слотів");
    }

    // =================================================================================================================
    // Слот у джинглі
    // =================================================================================================================

    public LiveClip? TakeOrdered()
    {
        if (!Enabled) return null;
        lock (_lock)
            return store.WithStatus("ready").Where(r => r.Kind == "order").Select(Claim).FirstOrDefault(c => c is not null);
    }

    public LiveClip? Take(bool libraryEmpty)
    {
        if (!Enabled) return null;
        var now = clock.UtcNow;
        lock (_lock)
        {
            var ready = store.WithStatus("ready");
            foreach (var r in ready.Where(r => r.Kind == "event"))
                if (r.CreatedAt > now.AddMinutes(-O.EventFreshMinutes) && Claim(r) is { } clip) return clip;
            foreach (var r in ready.Where(r => r.Kind == "news"))
                if (r.Day == Days.Of(now) && Claim(r) is { } clip) return clip;
            if (!libraryEmpty && Roll() >= Share) return null;
            foreach (var r in ready.Where(r => r.Kind == "roast"))
                if (r.Target is { } t && presence.IsOnline(t) && !store.OptedOut(t) && !Cooling(t, now) && Claim(r) is { } clip) return clip;
            return null;
        }
    }

    LiveClip? Claim(LiveAdRow r)
    {
        if (r.File is null) return null;
        var path = FileOf(r.File);
        if (!File.Exists(path)) { store.Move(r.Id, "ready", "failed", clock.UtcNow); return null; }
        if (!store.Move(r.Id, "ready", "sent", clock.UtcNow)) return null;
        return new LiveClip(r.Id, r.File, Title(r), Math.Max(1, r.Seconds), path);
    }

    static string Title(LiveAdRow r) => r.Kind switch
    {
        "order" => $"🔥 Прожарка: {r.Target}",
        "roast" => $"🔥 Прожарка: {r.Target}",
        "event" => "📣 Глек реагує",
        "news" => "📰 Новини Глечиків",
        _ => "📣 Жива реклама",
    };

    public void Sent(LiveClip clip, bool ok)
    {
        if (!ok) store.Move(clip.Id, "sent", "ready", clock.UtcNow);
        else log.LogInformation("жива реклама {Id} ({Title}) стала в чергу", clip.Id, clip.Title);
    }

    public void Started(string trackId)
    {
        if (store.ByFile(trackId) is { } r) store.Move(r.Id, "sent", "aired", clock.UtcNow);
    }

    string FileOf(string trackId) => Path.Combine(Cache, trackId + ".mp3");

    bool Cooling(string target, DateTimeOffset now) =>
        store.AboutSince(target, now.AddMinutes(-Math.Max(O.TargetCooldownMinutes, O.EventCooldownMinutes) - 1))
            .Any(r => r.SentAt is { } s && s > now.AddMinutes(-O.TargetCooldownMinutes));

    // =================================================================================================================
    // Замовлена прожарка
    // =================================================================================================================

    public LiveOrderReply Order(string buyer, bool account, string? target, bool anon)
    {
        LiveOrderReply No(string m) => new(false, m, account ? economy.Balance(buyer) : null);
        if (!account || Auth.IsGuestNick(buyer)) return No("Закріпи нік — тоді зможеш замовити прожарку");
        if (!Enabled) return No("Глек зараз мовчить — прожарки не приймаються");
        var name = Auth.CleanNick(target);
        if (name.Length == 0) return No("Обери, кого прожарити");
        if (store.Account(name) is not { } acc) return No($"«{name}» — не акаунт");
        var now = clock.UtcNow;
        if (acc.Seen < now.AddDays(-Math.Max(1, O.ActiveDays))) return No($"{acc.Nick} давно не заходить — прожарювати нема для кого");
        if (store.OptedOut(acc.Nick)) return No($"{acc.Nick} відмовляється від прожарок");
        var price = anon ? O.OrderAnonPrice : O.OrderPrice;
        long id;
        lock (_lock)
        {
            var (byBuyer, onTarget) = store.OrdersToday(buyer, acc.Nick, now);
            if (byBuyer >= O.OrderDailyPerBuyer) return No($"Сьогодні вже {byBuyer} {LiveLines.Plural(byBuyer, "прожарка", "прожарки", "прожарок")} — Глекові теж треба охолонути. Завтра ще");
            if (onTarget >= O.OrderDailyPerTarget) return No($"{acc.Nick} сьогодні вже смажать {onTarget} {LiveLines.Plural(onTarget, "раз", "рази", "разів")} — досить");
            // Рядок з'являється «неоплаченим»: фонова кухня бере лише queued, тож не спече прожарку, за яку ще не заплатили
            id = store.Add("order", acc.Nick, buyer, anon, price, "paying", now);
            if (economy.TrySpend(buyer, price, Reason, $"liveads:{id}")) store.Move(id, "paying", "queued", now);
            else
            {
                store.Move(id, "paying", "failed", now);
                return No($"Бракує {Math.Max(1, price - economy.Balance(buyer))} 🏺");
            }
        }
        try { outbox.Post(new Journal($"🔥 Прожарка для {acc.Nick} від {(anon ? Anonymous : buyer)} — скоро в ефірі")); }
        catch (Exception ex) { log.LogWarning(ex, "журнал не прийняв рядок про прожарку"); }
        Kick();
        return new(true, $"Глек уже гріє сковорідку: прожарка для {acc.Nick} скоро в ефірі", economy.Balance(buyer), id);
    }

    /// <summary>Повернути черепки за замовлення, що не вийшло (та сама причина в гаманці, свій ref).</summary>
    void Refund(LiveAdRow r, string from)
    {
        if (!store.Move(r.Id, from, "refunded", clock.UtcNow)) return;
        if (r.Buyer is null || r.Price <= 0) return;
        economy.Grant(r.Buyer, r.Price, Reason, $"liveads-refund:{r.Id}",
            $"+{r.Price} {Economy.Economy.Shards(r.Price)}: прожарка не вийшла — повертаю");
        log.LogInformation("замовлення прожарки {Id} не вийшло — {Buyer} отримує {Price} назад", r.Id, r.Buyer, r.Price);
    }

    /// <summary>Відмова «Мене не прожарювати»: не ціль живих, подій, замовлень і не згадується в новинах.</summary>
    public (bool Ok, string Message) OptOut(string nick, bool account, bool off)
    {
        if (!account || Auth.IsGuestNick(nick)) return (false, "Закріпи нік — тоді це твоє рішення");
        store.SetOptOut(nick, off);
        return (true, off ? "Домовились: Глек тебе не прожарює" : "Сковорідка знову відкрита — Глек може тебе прожарити");
    }

    // =================================================================================================================
    // Фонова кухня
    // =================================================================================================================

    public void Kick() { try { _signal.Release(); } catch (SemaphoreFullException) { } }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "жива реклама спіткнулась"); }
            try { await _signal.WaitAsync(TimeSpan.FromSeconds(Math.Max(3, O.TickSeconds)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Один обхід: прибрати протухле, спекти замовлене, помітити події, новини у своє вікно, одна прожарка
    /// наперед, почистити старі файли. Тести кличуть його самі.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (!await _tick.WaitAsync(0, ct)) return;
        try
        {
            var now = clock.UtcNow;
            Expire(now);
            if (!Enabled) return;
            foreach (var r in store.WithStatus("queued").Where(r => r.Kind == "order"))
                await BakeOrder(r, ct);
            await Events(now, ct);
            await News(now, ct);
            await Prepare(ct);
            Clean(now);
        }
        finally { _tick.Release(); }
    }

    void Expire(DateTimeOffset now)
    {
        foreach (var status in new[] { "queued", "ready" })
            foreach (var r in store.WithStatus(status))
            {
                var old = r.Kind switch
                {
                    "order" => r.CreatedAt < now.AddHours(-Math.Max(1, O.OrderExpireHours)),
                    "event" => r.CreatedAt < now.AddMinutes(-O.EventFreshMinutes),
                    "news" => r.Day != Days.Of(now),
                    // Прожарка на присутніх: пів години — факти несвіжі, а людина могла й піти
                    _ => r.CreatedAt < now.AddMinutes(-30) || (r.Target is { } t && !presence.IsOnline(t)),
                };
                if (!old) continue;
                if (r.Kind == "order") Refund(r, status);
                else store.Move(r.Id, status, "expired", now);
            }
    }

    async Task BakeOrder(LiveAdRow r, CancellationToken ct)
    {
        var lines = Lines;
        var values = new Dictionary<string, string>
        {
            ["target"] = lines.Say(r.Target!),
            ["buyer"] = r.Anon ? Anonymous : lines.Say(r.Buyer ?? ""),
        };
        var script = Roast(r.Target!, r.Anon ? "order_anon" : "order", values, 3, ignoreFactCooldown: true);
        if (script is null || !await Bake(r.Id, "queued", script, ct)) Refund(store.Get(r.Id)!, store.Get(r.Id)!.Status);
    }

    /// <summary>Спекти ролик у файл і позначити готовим; false — не вийшло (рядок стає failed).</summary>
    async Task<bool> Bake(long id, string from, LiveScript script, CancellationToken ct)
    {
        store.SetText(id, script.Text, string.Join(',', script.Facts));
        var file = $"voice-live-{id}";
        Directory.CreateDirectory(Cache);
        double? sec = null;
        try { sec = await renderer.RenderAsync(script, FileOf(file), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "рендер живої реклами {Id} впав", id);
        }
        if (sec is not { } s || s <= 0)
        {
            store.Move(id, from, "failed", clock.UtcNow);
            return false;
        }
        lock (_lock)
        {
            store.Ready(id, script.Text, string.Join(',', script.Facts), file, (int)Math.Ceiling(s), clock.UtcNow);
        }
        return true;
    }

    // ---------- прожарка присутніх ----------

    /// <summary>Тримати одну прожарку наперед на когось із присутніх.</summary>
    async Task Prepare(CancellationToken ct)
    {
        if (store.WithStatus("ready").Any(r => r.Kind == "roast")) return;
        if (PickTarget() is not { } pick) return;
        var id = store.Add("roast", pick.Nick, null, false, 0, "queued", clock.UtcNow);
        await Bake(id, "queued", pick.Script, ct);
    }

    /// <summary>
    /// Ціль живої прожарки: спершу ті, у кого грає плеєр, потім решта онлайн; лише акаунти без відмови й поза
    /// кулдауном; першим — хто найдовше не смажився. Береться перший, про кого є що сказати (два свіжі факти).
    /// </summary>
    public (string Nick, LiveScript Script)? PickTarget()
    {
        var now = clock.UtcNow;
        var last = store.LastSent();
        var listening = presence.Listening;
        var online = presence.Online;
        var order = listening.Concat(online.Except(listening, StringComparer.OrdinalIgnoreCase))
            .Where(n => !Auth.IsGuestNick(n))
            .Select((n, i) => (Nick: n, Listening: i < listening.Count))
            .OrderBy(x => x.Listening ? 0 : 1)
            .ThenBy(x => last.TryGetValue(Auth.NickKey(x.Nick), out var t) ? t : DateTimeOffset.MinValue)
            .ToList();
        foreach (var (nick, _) in order)
        {
            if (store.Account(nick) is not { } acc || store.OptedOut(nick) || Cooling(nick, now)) continue;
            if (Roast(acc.Nick, "roast", new Dictionary<string, string> { ["target"] = Lines.Say(acc.Nick) }, 2, false) is { } s)
                return (acc.Nick, s);
        }
        return null;
    }

    /// <summary>
    /// Сценарій прожарки: вступ + найсоковитіші факти (по одному з родини, не ті, що вже звучали цій людині за добу) +
    /// кінцівка. <paramref name="count"/> — скільки фактів; менше двох свіжих — живої прожарки нема (замовлена
    /// бере, що є, аж до «нема чого сказати»).
    /// </summary>
    public LiveScript? Roast(string target, string introKind, Dictionary<string, string> introValues, int count, bool ignoreFactCooldown)
    {
        var lines = Lines;
        var now = clock.UtcNow;
        var used = ignoreFactCooldown ? [] : store.AboutSince(target, now.AddHours(-Math.Max(1, O.FactCooldownHours)))
            .Where(r => r.Status is "sent" or "aired").SelectMany(r => r.FactKinds).ToHashSet();
        var present = presence.Online;
        var all = facts.For(target, present);
        var picked = new List<(LiveFact Fact, LiveLine Line)>();
        var families = new HashSet<string>();
        foreach (var f in all.Where(f => !used.Contains(f.Kind)).OrderByDescending(f => f.Juice + Rng.NextDouble() * 0.15))
        {
            if (picked.Count >= count) break;
            if (!families.Add(LiveFacts.Family(f.Kind))) continue;
            if (lines.Pick(lines.Facts.GetValueOrDefault(f.Kind), f.Values, LiveLines.Glek, O.Rate, Rng) is { } line)
                picked.Add((f, line));
        }
        var juicy = picked.Count(p => p.Fact.Juice >= 0.2);
        if (introKind == "roast" && juicy < 2) return null;
        if (picked.Count == 0)
        {
            var nothing = LiveFact.Of("nothing", 0, ("nick", lines.Say(target)));
            if (lines.Pick(lines.Facts.GetValueOrDefault("nothing"), nothing.Values, LiveLines.Glek, O.Rate, Rng) is { } line)
                picked.Add((nothing, line));
        }
        var intro = lines.Pick(lines.Intro.GetValueOrDefault(introKind), introValues, LiveLines.Polina, O.Rate, Rng);
        if (intro is null || picked.Count == 0) return null;
        var script = new List<LiveLine> { intro };
        for (var i = 0; i < picked.Count; i++)
        {
            if (i > 0 && Rng.NextDouble() < 0.5 && lines.Pick(lines.Link, picked[i].Fact.Values, LiveLines.Glek, O.Rate, Rng) is { } link)
                script.Add(link);
            script.Add(picked[i].Line);
        }
        if (lines.Pick(lines.Outro, new Dictionary<string, string> { ["nick"] = lines.Say(target) }, LiveLines.Glek, O.Rate, Rng) is { } outro)
            script.Add(outro);
        return new LiveScript(introKind == "roast" ? "roast" : "order", lines.Style(introKind == "roast" ? "roast" : "order", Rng),
            script, picked.Select(p => p.Fact.Kind).ToList());
    }

    /// <summary>Господар натиснув «Прожарити зараз»: спекти одразу (без кулдаунів, але з відмовою) і віддати в ефір.</summary>
    public async Task<(LiveClip? Clip, string Message)> RoastNowAsync(string nick, CancellationToken ct)
    {
        if (store.Account(Auth.CleanNick(nick)) is not { } acc) return (null, "Такого акаунта нема");
        if (store.OptedOut(acc.Nick)) return (null, $"{acc.Nick} відмовляється від прожарок");
        var script = Roast(acc.Nick, "roast", new Dictionary<string, string> { ["target"] = Lines.Say(acc.Nick) }, 3, true)
            ?? Roast(acc.Nick, "order_anon", new Dictionary<string, string> { ["target"] = Lines.Say(acc.Nick), ["buyer"] = Anonymous }, 3, true);
        if (script is null) return (null, "Банк фраз порожній");
        var id = store.Add("roast", acc.Nick, "господар", false, 0, "queued", clock.UtcNow);
        return await Serve(id, script, ct);
    }

    public async Task<(LiveClip? Clip, string Message)> NewsNowAsync(CancellationToken ct)
    {
        if (NewsScript() is not { } script) return (null, "Банк фраз порожній");
        var id = store.Add("news", null, "господар", false, 0, "queued", clock.UtcNow);
        return await Serve(id, script, ct);
    }

    async Task<(LiveClip? Clip, string Message)> Serve(long id, LiveScript script, CancellationToken ct)
    {
        if (!await Bake(id, "queued", script, ct)) return (null, "Не спеклось: edge-tts чи ffmpeg недоступні");
        lock (_lock)
            return store.Get(id) is { } r && Claim(r) is { } clip ? (clip, "Готово") : (null, "Файл зник");
    }

    // ---------- новини ----------

    async Task News(DateTimeOffset now, CancellationToken ct)
    {
        var h = TimeZoneInfo.ConvertTime(now, Days.Kyiv).Hour;
        if (h < O.NewsFrom || h >= O.NewsTo || presence.Listening.Count == 0) return;
        var today = Days.Of(now);
        if (store.Since(now.AddDays(-1)).Any(r => r.Kind == "news" && r.Buyer is null && r.Day == today && r.Status is not ("failed" or "expired"))) return;
        if (NewsScript() is not { } script) return;
        var id = store.Add("news", null, null, false, 0, "queued", now);
        await Bake(id, "queued", script, ct);
    }

    /// <summary>Дайджест дня: Поліна веде, спортивний блок — Глек темпом коментатора. Відмовники поіменно не звучать.</summary>
    public LiveScript? NewsScript()
    {
        var lines = Lines;
        var items = facts.News(store.OptedOutKeys());
        var none = new Dictionary<string, string>();
        var intro = lines.Pick(lines.News.GetValueOrDefault("intro"), none, LiveLines.Polina, O.Rate, Rng);
        if (intro is null) return null;
        var script = new List<LiveLine> { intro };
        foreach (var (f, _) in items.Where(i => !i.Sport))
            if (lines.Pick(lines.News.GetValueOrDefault(f.Kind), f.Values, LiveLines.Polina, O.Rate, Rng) is { } l) script.Add(l);
        var sport = items.Where(i => i.Sport).ToList();
        if (sport.Count > 0 && lines.Pick(lines.News.GetValueOrDefault("sport_intro"), none, LiveLines.Polina, O.Rate, Rng) is { } si)
        {
            script.Add(si);
            foreach (var (f, _) in sport)
                if (lines.Pick(lines.News.GetValueOrDefault(f.Kind), f.Values, LiveLines.Glek, O.SportRate, Rng) is { } l) script.Add(l);
        }
        if (script.Count < 3 && lines.Pick(lines.News.GetValueOrDefault("quiet"), none, LiveLines.Glek, O.Rate, Rng) is { } q) script.Add(q);
        if (lines.Pick(lines.News.GetValueOrDefault("outro"), none, LiveLines.Polina, O.Rate, Rng) is { } o) script.Add(o);
        return new LiveScript("news", lines.Style("news", Rng, "sport"), script, items.Select(i => i.Fact.Kind).ToList());
    }

    // ---------- реакції на події ----------

    /// <summary>
    /// Події — з бази, а не підписками: нові рядки результатів, покупок і акаунтів помічаються за водяними знаками
    /// (на старті — поточний кінець, щоб не реагувати на історію). Так не довелось чіпати ні ігри, ні Лавку, ні
    /// реєстрацію. Відбій кола — о 00:00 за Києвом тим, кого він стосується і хто онлайн.
    /// </summary>
    async Task Events(DateTimeOffset now, CancellationToken ct)
    {
        var found = new List<(string Kind, string Nick, LiveFact Fact)>();
        var lines = Lines;

        var maxResult = store.Scalar("SELECT COALESCE(MAX(id), 0) FROM game_results");
        if (_resultsMark is { } rm && maxResult > rm)
        {
            var fresh = store.Query("SELECT DISTINCT nick_key, nick, game FROM game_results WHERE id > $m AND outcome IN ('win', 'loss')",
                r => (Key: r.GetString(0), Nick: r.GetString(1), Game: r.GetString(2)), ("$m", rm));
            foreach (var (key, nick, game) in fresh)
            {
                var (outcome, n, _) = facts.CurrentStreak(key, game);
                if (n < O.StreakEvent) continue;
                var kind = outcome == "loss" ? "lose_streak" : "win_streak";
                found.Add((kind, nick, LiveFact.Of(kind, 1, ("nick", lines.Say(nick)), ("n", n), ("game", GameTitle(game)))));
            }
        }
        _resultsMark = maxResult;

        var maxLavka = store.Scalar("SELECT COALESCE(MAX(rowid), 0) FROM lavka_owned");
        if (_lavkaMark is { } lm && maxLavka > lm)
        {
            var buys = store.Query("SELECT o.nick_key, COALESCE(a.nick, o.nick_key), o.item, o.source, o.from_nick, o.price FROM lavka_owned o LEFT JOIN accounts a ON a.nick_key = o.nick_key WHERE o.rowid > $m AND o.price >= $p",
                r => (Owner: r.GetString(1), Item: r.GetString(2), Source: r.GetString(3), From: r.IsDBNull(4) ? null : r.GetString(4), Price: r.GetInt32(5)),
                ("$m", lm), ("$p", O.BigBuy));
            foreach (var b in buys)
            {
                var who = b.Source == "buy" ? b.Owner : b.From ?? b.Owner;
                found.Add(("big_buy", who, LiveFact.Of("big_buy", 1, ("nick", lines.Say(who)), ("n", b.Price), ("item", LavkaCatalog.Label(b.Item)))));
            }
        }
        _lavkaMark = maxLavka;

        var maxAcc = store.Scalar("SELECT COALESCE(MAX(rowid), 0) FROM accounts");
        if (_accountsMark is { } am && maxAcc > am)
            foreach (var nick in store.Query("SELECT nick FROM accounts WHERE rowid > $m", r => r.GetString(0), ("$m", am)))
                found.Add(("new_account", nick, LiveFact.Of("new_account", 1, ("nick", lines.Say(nick)))));
        _accountsMark = maxAcc;

        var local = TimeZoneInfo.ConvertTime(now, Days.Kyiv);
        var c = curfew.CurrentValue;
        if (local.Hour == c.From && local.Minute < 15 && _curfewDay != Days.Of(now) && c.Nicks.Count > 0)
        {
            _curfewDay = Days.Of(now);
            var night = c.Nicks.Where(n => presence.IsOnline(n) && !store.OptedOut(n)).ToList();
            if (night.Count > 0)
                found.Add(("curfew", night[0], LiveFact.Of("curfew", 1, ("nick", lines.Say(night[0])),
                    ("nicks", string.Join(", ", night.Select(lines.Say))), ("n", night.Count))));
        }

        foreach (var (kind, nick, fact) in found)
        {
            if (store.OptedOut(nick)) continue;
            var recent = store.Since(now.AddHours(-1)).Where(r => r.Kind == "event").ToList();
            if (recent.Count >= O.EventsPerHour) break;
            if (store.AboutSince(nick, now.AddMinutes(-O.EventCooldownMinutes)).Any(r => r.Kind == "event")) continue;
            if (EventScript(kind, fact) is not { } script) continue;
            var id = store.Add("event", nick, null, false, 0, "queued", now);
            await Bake(id, "queued", script, ct);
        }
    }

    string GameTitle(string id) => facts.GameTitle(id);

    public LiveScript? EventScript(string kind, LiveFact fact)
    {
        var lines = Lines;
        var body = lines.Pick(lines.Events.GetValueOrDefault(kind), fact.Values, LiveLines.Glek, O.Rate, Rng);
        var intro = lines.Pick(lines.Intro.GetValueOrDefault("event"), fact.Values, LiveLines.Polina, O.Rate, Rng);
        if (body is null || intro is null) return null;
        return new LiveScript("event", lines.Style("event", Rng, "polka"), [intro, body], [kind]);
    }

    // ---------- прибирання ----------

    void Clean(DateTimeOffset now)
    {
        foreach (var (id, file) in store.Stale(Math.Max(0, O.KeepFiles), now.AddMinutes(-Math.Max(1, O.KeepMinutes))))
        {
            try { File.Delete(FileOf(file)); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            store.DropFile(id);
        }
    }

    // =================================================================================================================
    // Що бачать люди
    // =================================================================================================================

    /// <summary>Картка в Лавці (<c>GET /api/liveads</c>).</summary>
    public object View(string nick, bool account)
    {
        var now = clock.UtcNow;
        var me = account && !Auth.IsGuestNick(nick);
        var mine = me ? store.OrdersOf(nick, 10) : [];
        var today = mine.Count(r => r.Day == Days.Of(now) && r.Status is not ("failed" or "refunded"));
        return new
        {
            enabled = Enabled,
            price = O.OrderPrice,
            anonPrice = O.OrderAnonPrice,
            dailyPerBuyer = O.OrderDailyPerBuyer,
            dailyPerTarget = O.OrderDailyPerTarget,
            left = me ? Math.Max(0, O.OrderDailyPerBuyer - today) : 0,
            account = me,
            optOut = me && store.OptedOut(nick),
            balance = me ? economy.Balance(nick) : 0,
            targets = store.Targets(now.AddDays(-Math.Max(1, O.ActiveDays))),
            orders = mine.Select(r => new { id = r.Id, target = r.Target, anon = r.Anon, price = r.Price, status = Stage(r, now), createdAt = r.CreatedAt }),
        };
    }

    /// <summary>Стан замовлення для людини: у черзі / в ефірі / зіграно / повернуто.</summary>
    public static string Stage(LiveAdRow r, DateTimeOffset now) => r.Status switch
    {
        "queued" or "ready" => "queued",
        "sent" => "soon",
        "aired" => r.PlayedAt is { } p && p.AddSeconds(r.Seconds + 5) > now ? "air" : "done",
        "refunded" or "failed" => "refunded",
        _ => r.Status,
    };

    /// <summary>Блок «Жива реклама» у вкладці «📣 Реклама» (<c>GET /api/liveads/admin</c>).</summary>
    public object AdminView()
    {
        var now = clock.UtcNow;
        return new
        {
            enabled = Enabled,
            configEnabled = O.Enabled,
            share = Share,
            targetCooldownMinutes = O.TargetCooldownMinutes,
            news = new { from = O.NewsFrom, to = O.NewsTo },
            ready = store.WithStatus("ready").Select(r => new { id = r.Id, kind = r.Kind, target = r.Target }),
            recent = store.Recent(20).Select(r => new
            {
                id = r.Id, kind = r.Kind, target = r.Target, buyer = r.Buyer, anon = r.Anon, price = r.Price,
                text = r.Text, facts = r.FactKinds, status = r.Status, stage = Stage(r, now), seconds = r.Seconds,
                createdAt = r.CreatedAt, sentAt = r.SentAt, playedAt = r.PlayedAt,
                url = r.File is { } f && File.Exists(FileOf(f)) ? $"/api/voice/{f}.mp3" : null,
            }),
        };
    }
}
