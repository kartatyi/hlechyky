using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Налаштування реклами (секція <c>Ad</c> в appsettings.json). Числа тут, а не в коді, бо «раз на скільки
/// треків крутити рекламу» доводиться підкручувати на живому ефірі. Частоту, яку господар поставив у вкладці
/// «📣 Реклама», тримає база (<see cref="AdLibraryStore.Frequency"/>), а <see cref="EveryTracks"/> і
/// <see cref="MinMinutes"/> — лише типові значення, поки він її не чіпав. Ключі колишнього конкурсу реклами
/// (<c>Days</c>, <c>AutoOpen</c>, <c>WinnerReward</c>…) у чиїхось appsettings нічого не ламають: зв'язування
/// налаштувань незнайомі ключі мовчки пропускає.
/// </summary>
public sealed class AdOptions
{
    /// <summary>Реклама в ефірі взагалі. false — джингл спить: сам нічого не ставить, треків не рахує й за рекламу не платить.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Реклама з колоди сама заходить в ефір. false — лише руками: «Наступну в чергу» чи «саме цю».</summary>
    public bool Jingle { get; set; } = true;
    /// <summary>Не частіше ніж раз на стільки треків…</summary>
    public int EveryTracks { get; set; } = 6;
    /// <summary>…і не частіше ніж раз на стільки хвилин.</summary>
    public int MinMinutes { get; set; } = 25;

    /// <summary>Черепки кожному, хто прослухав рекламу з увімкненим плеєром. 0 — не нараховувати.</summary>
    public int ListenReward { get; set; } = 2;
    /// <summary>Скільки черепків на день можна набрати прослуханою рекламою.</summary>
    public int ListenDailyCap { get; set; } = 40;
}

/// <summary>
/// Єдина дірочка реклами в ефір: поставити готове голосове в кінець черги. Інтерфейс тут для тестів —
/// справжній <see cref="RadioEngine"/> тягне за собою пів сервера, а перевірити треба лише «коли саме».
/// </summary>
public interface IAdAir
{
    (bool Ok, string Message) AddVoice(TrackInfo track, string filePath, string nick);
}

/// <summary>
/// Справжній ефір: той самий публічний метод, яким у чергу лягають звичайні голосові, лише без рядка в Журналі —
/// реклама заходить щокілька треків, і «Дядько Глек записує голосове» засипало б Журнал.
/// </summary>
public sealed class RadioAir(RadioEngine engine) : IAdAir
{
    public (bool Ok, string Message) AddVoice(TrackInfo track, string filePath, string nick) =>
        engine.AddVoice(track, filePath, nick, journal: false);
}

/// <summary>
/// Джингл: реклама з колоди бібліотеки час від часу сама заходить в ефір. Умов три, і всі три мусять збігтись —
/// минуло досить треків, минуло досить хвилин (обидва числа — <see cref="Frequency"/>) і на сайті хтось є. Без
/// останньої умови реклама крутилась би о четвертій ранку сама собі.
/// </summary>
public sealed class AdJingle(AdLibrary library, AdLibraryStore store, AdListenRewards rewards, IAdAir air, IVoiceSaver voice,
    Presence presence, IClock clock, IOptionsMonitor<AdOptions> opts, ILogger<AdJingle> log, ILiveAdSource? live = null)
{
    public const string AdTitle = "Реклама глека";
    public const int MaxEveryTracks = 100;
    public const int MaxMinMinutes = 600;

    readonly object _lock = new();
    int _since;
    DateTimeOffset? _lastAt;
    /// <summary>
    /// До якої миті в черзі ефіру ще стоїть реклама, що не заграла. Замовлена прожарка не чекає частоти, але й не
    /// стає впритул до іншої реклами; стеля — бо господар міг зняти рекламу з черги, і «чекати» вічно не можна.
    /// </summary>
    DateTimeOffset? _waitingUntil;
    public static readonly TimeSpan WaitingCap = TimeSpan.FromMinutes(20);

    /// <summary>Скільки треків минуло від останньої реклами — видно у вкладці «📣 Реклама», у тестах і в лозі.</summary>
    public int Since { get { lock (_lock) return _since; } }

    /// <summary>Заграв новий трек. Кличе <see cref="AdJingleHook"/>, підписаний на RadioEngine.TrackStarted.</summary>
    public void OnTrackStarted(TrackInfo track)
    {
        try { Step(track); }
        catch (Exception ex) { log.LogWarning(ex, "джингл реклами спіткнувся"); }
    }

    /// <summary>
    /// Реклама в ефірі — трек із бібліотеки. За назвою теж: господар міг видалити рекламу, поки вона стояла в
    /// черзі, і дограти вона має як реклама, а не як пісня, що зсуває лічильник.
    /// </summary>
    bool IsAd(TrackInfo track) =>
        library.IsAd(track.Id) || (track.Title == AdTitle && track.Id.StartsWith("voice-", StringComparison.Ordinal));

    void Step(TrackInfo track)
    {
        var o = opts.CurrentValue;
        if (!o.Enabled) return;
        if (IsAd(track))
        {
            // Це грає сама реклама: не рахуємо її за трек, починаємо відлік від цієї миті і платимо слухачам
            if (library.IsAd(track.Id)) library.Played(track.Id);
            else live?.Started(track.Id);
            rewards.Start(track);
            lock (_lock)
            {
                _since = 0;
                _lastAt = clock.UtcNow;
                _waitingUntil = null;
            }
            return;
        }
        if (!o.Jingle) return;
        // Ротація кожен трек може бути вже інша: господар вмикає й вимикає рекламу просто посеред ефіру.
        var hasLibrary = library.HasLive();
        if (!hasLibrary && live is null) return;
        var (every, minutes) = Frequency();

        lock (_lock)
        {
            _since++;
            // Замовлена прожарка — за неї заплатили: іде на найближчій межі треку, частоти не чекає. Але між двома
            // рекламами хоч один трек: _since ≥ 1 тут завжди (щойно заграв не-рекламний трек), а реклама, що ще
            // стоїть у черзі, тримає _waitingUntil.
            if (live is not null && presence.Count > 0 && !Waiting() && live.TakeOrdered() is { } ordered && QueueLive(ordered).Ok) return;
            if (_since < every) return;
            // Лічильник далі не росте, але й не скидається: щойно з'явиться слухач — реклама піде.
            if (presence.Count == 0) return;
            if (_lastAt is { } last && clock.UtcNow - last < TimeSpan.FromMinutes(minutes)) return;
            // Жива (реакція, новини, прожарка присутніх) — першою; не дала нічого — бібліотека, як завжди
            if (live?.Take(!hasLibrary) is { } fresh && QueueLive(fresh).Ok) return;
            if (hasLibrary && library.Take() is { } clip && Queue(clip).Ok) _since = 0;     // відмова — уже в черзі або в ефірі, спробуємо наступного разу
        }
    }

    bool Waiting() => _waitingUntil is { } w && clock.UtcNow < w;

    /// <summary>Живий ролик — у чергу ефіру; відмова ефіру повертає його живій рекламі чекати наступного слоту.</summary>
    (bool Ok, string Message) QueueLive(LiveClip clip)
    {
        (bool Ok, string Message) r = File.Exists(clip.FilePath)
            ? air.AddVoice(new TrackInfo(clip.TrackId, AdTitle, clip.Title, clip.Seconds, null, $"/api/voice/{clip.TrackId}.mp3", null), clip.FilePath, "Дядько Глек")
            : (false, "Файлу живої реклами вже нема");
        live?.Sent(clip, r.Ok);
        if (r.Ok)
        {
            _since = 0;
            _lastAt = clock.UtcNow;
            _waitingUntil = clock.UtcNow + WaitingCap;
            log.LogInformation("жива реклама {Track} («{Title}») стала в чергу", clip.TrackId, clip.Title);
        }
        return r;
    }

    /// <summary>Господар натиснув «Прожарити зараз» / «Новини зараз»: готовий живий ролик — у чергу без лічильника й хвилин.</summary>
    public (bool Ok, string Message) PlayLive(LiveClip clip)
    {
        lock (_lock)
        {
            var r = QueueLive(clip);
            return r.Ok ? (true, $"«{clip.Title}» стала в чергу") : r;
        }
    }

    /// <summary>Господар натиснув «Наступну в чергу»: наступна реклама з колоди стає в чергу одразу, без лічильника й хвилин.</summary>
    public (bool Ok, string Message) PlayNow()
    {
        lock (_lock)
        {
            if (library.Take() is not { } clip) return (false, "Нема що крутити: у ротації порожньо");
            var r = Queue(clip);
            if (r.Ok) _since = 0;
            return r.Ok ? (true, $"«{clip.Title}» стала в чергу") : r;
        }
    }

    /// <summary>Саме цю рекламу з бібліотеки — в чергу, хай навіть вона вимкнена в ротації.</summary>
    public (bool Ok, string Message) PlayClip(long id)
    {
        if (library.Get(id) is not { } clip) return (false, "Такої реклами нема");
        lock (_lock)
        {
            var r = Queue(clip);
            if (r.Ok) _since = 0;
            return r.Ok ? (true, $"«{clip.Title}» стала в чергу") : r;
        }
    }

    (bool Ok, string Message) Queue(AdClip clip)
    {
        if (voice.FilePath(clip.TrackId) is not { } path) return (false, "Файлу реклами вже нема");   // кеш почистили — не біда
        var track = new TrackInfo(clip.TrackId, AdTitle, clip.Title, clip.Seconds, null, $"/api/voice/{clip.TrackId}.mp3", null);
        var r = air.AddVoice(track, path, "Дядько Глек");
        if (r.Ok)
        {
            _lastAt = clock.UtcNow;
            _waitingUntil = clock.UtcNow + WaitingCap;
            log.LogInformation("реклама {Track} («{Title}») стала в чергу", clip.TrackId, clip.Title);
        }
        return r;
    }

    // ---------- частота ----------

    /// <summary>
    /// Раз на скільки треків і не частіше ніж раз на скільки хвилин: те, що поставив господар (база), а поки не
    /// ставив — типове з <c>Ad:EveryTracks</c>/<c>Ad:MinMinutes</c>. Питаємо на кожен трек: один рядок із бази
    /// раз на кілька хвилин нічого не коштує, зате нова частота діє вже з наступного треку.
    /// </summary>
    public (int EveryTracks, int MinMinutes) Frequency()
    {
        var (every, minutes) = store.Frequency();
        var o = opts.CurrentValue;
        return (Math.Max(1, every ?? o.EveryTracks), Math.Max(0, minutes ?? o.MinMinutes));
    }

    public (bool Ok, string Message) SetFrequency(int everyTracks, int minMinutes)
    {
        if (everyTracks is < 1 or > MaxEveryTracks) return (false, $"Треків — від 1 до {MaxEveryTracks}");
        if (minMinutes is < 0 or > MaxMinMinutes) return (false, $"Хвилин — від 0 до {MaxMinMinutes}");
        store.SetFrequency(everyTracks, minMinutes, clock.UtcNow);
        return (true, minMinutes == 0
            ? $"Реклама — раз на {everyTracks} тр."
            : $"Реклама — раз на {everyTracks} тр., але не частіше ніж раз на {minMinutes} хв");
    }
}

/// <summary>Підписка джингла на ефір: живе рівно стільки, скільки сервер, і знімається на зупинці.</summary>
public sealed class AdJingleHook(AdJingle jingle, RadioEngine engine) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        engine.TrackStarted += jingle.OnTrackStarted;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        engine.TrackStarted -= jingle.OnTrackStarted;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Підключення реклами. Кличеться одним рядком із <c>EconomySetup.AddHlechykyEconomy</c> і одним —
/// із <c>MapHlechykyEconomy</c>. Керує рекламою лише господар, із вкладки «📣 Реклама».
/// </summary>
public static class AdSetup
{
    public static IServiceCollection AddAds(this IServiceCollection services)
    {
        services.AddOptions<AdOptions>().BindConfiguration("Ad");

        // Годинник реєструє каркас, і робить це раніше; TryAdd — щоб реклама піднімалась і без нього (тести).
        services.TryAddSingleton<IClock, SystemClock>();

        services.TryAddSingleton<IVoiceSaver, VoiceSaver>();
        services.TryAddSingleton<IAdAir, RadioAir>();
        services.TryAddSingleton<IAdOnAir, RadioOnAir>();

        services.AddSingleton<AdLibraryStore>();
        services.AddSingleton<AdLibrary>();
        services.AddSingleton<AdListenRewards>();
        services.AddSingleton<AdJingle>();
        services.AddHostedService<AdJingleHook>();
        services.AddLiveAds();     // жива реклама: факти гравців, прожарки, новини — джингл бере її через ILiveAdSource
        return services;
    }

    public sealed record AdEveryRequest(int EveryTracks, int MinMinutes);
    public sealed record AdClipPatch(string? Title, bool? Enabled);
    public sealed record AdAllRequest(bool Enabled);

    /// <summary>
    /// Вкладка «📣 Реклама» (<c>GET /api/ads/library</c>). <c>fallback</c> колись казав, що крутиться, коли в
    /// ротації порожньо, — реклама господаря чи переможець конкурсу. Обох прибрано разом із конкурсом, тож тепер
    /// там завжди null: поле лишилось, щоб клієнти, які його читають, не спіткнулись.
    /// </summary>
    public static object LibraryView(AdLibrary library, AdJingle jingle, IVoiceSaver voice, AdOptions o)
    {
        var (every, minutes) = jingle.Frequency();
        return new
        {
            items = library.All().Select(a => new
            {
                id = a.Id, trackId = a.TrackId, title = a.Title, seconds = a.Seconds, enabled = a.Enabled,
                plays = a.Plays, lastPlayedAt = a.LastPlayedAt, createdAt = a.CreatedAt,
                missing = voice.FilePath(a.TrackId) is null,
            }),
            everyTracks = every, minMinutes = minutes, since = jingle.Since,
            jingle = o.Enabled && o.Jingle,
            reward = new { amount = Math.Max(0, o.ListenReward), dailyCap = o.ListenDailyCap },
            fallback = (object?)null,
            maxMb = voice.MaxUploadBytes / (1024 * 1024),
        };
    }

    /// <summary>
    /// Стан ефіру реклами (<c>GET /api/ads/air</c>) — тієї самої форми, що й раніше. <c>on</c> і
    /// <c>houseMissing</c> описували рекламу господаря й переможця конкурсу, яких більше нема: тепер там
    /// завжди null і false, а що крутиться — видно в бібліотеці.
    /// </summary>
    public static object AirView(AdJingle jingle, AdOptions o)
    {
        var (every, minutes) = jingle.Frequency();
        return new
        {
            on = (object?)null,
            houseMissing = false,
            everyTracks = every,
            minMinutes = minutes,
            since = jingle.Since,
            jingle = o.Enabled && o.Jingle,
        };
    }

    public static WebApplication MapAds(this WebApplication app)
    {
        // Відповіді тієї самої форми, що й у решти сайту: { ok, message } — фронт уже вміє її читати.
        static IResult Reply((bool Ok, string Message) r) =>
            r.Ok ? Results.Ok(new { ok = true, message = r.Message }) : Results.BadRequest(new { ok = false, message = r.Message });
        static IResult Deny() => Results.BadRequest(new { ok = false, message = "Це вміє тільки господар" });

        // ---- ефір реклами: тільки господар ----
        app.MapGet("/api/ads/air", (HttpContext c, AdJingle jingle, IOptionsMonitor<AdOptions> opts) =>
            Auth.IsAdmin(c) ? Results.Ok(AirView(jingle, opts.CurrentValue)) : Deny());

        app.MapPost("/api/ads/air/every", (HttpContext c, AdEveryRequest req, AdJingle jingle) =>
            Auth.IsAdmin(c) ? Reply(jingle.SetFrequency(req.EveryTracks, req.MinMinutes)) : Deny());

        app.MapPost("/api/ads/air/now", (HttpContext c, AdJingle jingle) =>
            Auth.IsAdmin(c) ? Reply(jingle.PlayNow()) : Deny());

        // ---- бібліотека реклам і ротація: тільки господар ----
        app.MapGet("/api/ads/library", (HttpContext c, AdLibrary library, AdJingle jingle, IVoiceSaver voice, IOptionsMonitor<AdOptions> opts) =>
            Auth.IsAdmin(c) ? Results.Ok(LibraryView(library, jingle, voice, opts.CurrentValue)) : Deny());

        // Тіло — сам аудіофайл (будь-який формат, який з'їсть ffmpeg); назва — у ?title=
        app.MapPost("/api/ads/library", async (HttpContext c, string? title, AdLibrary library, IVoiceSaver voice, CancellationToken ct) =>
        {
            if (!Auth.IsAdmin(c)) return Deny();
            if (c.Request.ContentLength > voice.MaxUploadBytes)
                return Reply((false, $"Завеликий файл, ліміт {voice.MaxUploadBytes / (1024 * 1024)} МБ"));
            return Reply(await library.AddAsync(c.Request.Body, title, Auth.Nick(c), ct));
        });

        app.MapPatch("/api/ads/library/{id:long}", (HttpContext c, long id, AdClipPatch req, AdLibrary library) =>
        {
            if (!Auth.IsAdmin(c)) return Deny();
            (bool Ok, string Message) r = (false, "Нема що міняти");
            if (req.Title is not null) r = library.Rename(id, req.Title);
            if (req.Enabled is { } on && (req.Title is null || r.Ok)) r = library.SetEnabled(id, on);
            return Reply(r);
        });

        app.MapPost("/api/ads/library/all", (HttpContext c, AdAllRequest req, AdLibrary library) =>
            Auth.IsAdmin(c) ? Reply(library.SetAll(req.Enabled)) : Deny());

        app.MapDelete("/api/ads/library/{id:long}", (HttpContext c, long id, AdLibrary library) =>
            Auth.IsAdmin(c) ? Reply(library.Delete(id)) : Deny());

        app.MapPost("/api/ads/library/{id:long}/now", (HttpContext c, long id, AdJingle jingle) =>
            Auth.IsAdmin(c) ? Reply(jingle.PlayClip(id)) : Deny());

        app.MapLiveAds();          // /api/liveads* — картка прожарки в Лавці й блок «Жива реклама» для господаря
        return app;
    }
}
