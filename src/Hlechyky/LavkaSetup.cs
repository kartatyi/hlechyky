using Hlechyky.Games;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>
/// Справжній ефір для присвяти: той самий публічний шлях, яким у чергу лягають голосові й реклама (без рядка в Журналі —
/// про присвяту балачки скажуть самі), і та сама кнопка «посунути», якою люди міняють порядок черги.
/// </summary>
public sealed class RadioLavkaAir(RadioEngine engine) : ILavkaAir
{
    public IReadOnlyList<LavkaQueued> Queue() =>
        engine.Snapshot().Queue.Select(q => new LavkaQueued(q.ItemId, q.Track, q.RequestedBy, q.Status)).ToList();

    public (bool Ok, string Message) AddVoiceBefore(TrackInfo track, string filePath, string nick, string beforeItemId)
    {
        var added = engine.AddVoice(track, filePath, nick, journal: false);
        if (!added.Ok) return added;
        // Голосове стало в кінець; тепер — просто перед тим треком. Між цими двома кроками черга живе далі, тож
        // місце шукаємо вже в свіжому знімку.
        var queue = engine.Snapshot().Queue;
        var mine = queue.FirstOrDefault(q => q.Track.Id == track.Id);
        var at = queue.FindIndex(q => q.ItemId == beforeItemId);
        if (mine is not null && at >= 0) engine.Move(mine.ItemId, at, nick, isAdmin: true);
        return (true, "Присвята в черзі");
    }
}

/// <summary>
/// Голос Глека для присвяти — той самий edge-tts, що веде «Свою гру» (Остап), з тим самим кешем. Готова репліка
/// копіюється в кеш ефіру як звичайне голосове (<c>voice-…</c>): так liquidsoap її бачить, сайт віддає за
/// <c>/api/voice/&lt;id&gt;.mp3</c>, а черга, бан-лист і кеш поводяться з нею, як з будь-яким голосовим.
/// </summary>
public sealed class TtsLavkaVoice(TtsService tts, IOptionsMonitor<YtDlpOptions> yt, ILogger<TtsLavkaVoice> log) : ILavkaVoice
{
    /// <summary>Скільки чекати на озвучку. edge-tts зазвичай вкладається в секунду-три; далі людина чекати не мусить.</summary>
    public TimeSpan Wait { get; set; } = TimeSpan.FromSeconds(12);

    public async Task<LavkaVoiceClip?> SpeakAsync(string text, string title, string artist, CancellationToken ct)
    {
        if (!tts.Enabled) return null;
        var voice = Games.Impl.SvoyaPacks.VoiceName;
        var clip = tts.TryGet(voice, text);
        if (clip is null)
        {
            tts.Enqueue(voice, [text], urgent: true);
            var until = DateTimeOffset.UtcNow + Wait;
            while (clip is null && DateTimeOffset.UtcNow < until)
            {
                await Task.Delay(100, ct);
                clip = tts.TryGet(voice, text);
            }
            if (clip is null)
            {
                log.LogInformation("присвята без голосу: озвучка не встигла за {Seconds} с", Wait.TotalSeconds);
                return null;
            }
        }
        try
        {
            var id = VoiceService.Prefix + Guid.NewGuid().ToString("N")[..12];
            var dir = Paths.Resolve(yt.CurrentValue.CacheDir);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, id + ".mp3");
            File.Copy(clip.FilePath, path, overwrite: true);
            var seconds = Math.Max(1, (int)Math.Ceiling(clip.Seconds));
            return new LavkaVoiceClip(new TrackInfo(id, title, artist, seconds, null, $"/api/voice/{id}.mp3", null), path);
        }
        catch (IOException ex)
        {
            log.LogWarning(ex, "озвучену присвяту не вдалось покласти в кеш ефіру");
            return null;
        }
    }
}

/// <summary>
/// Події Лавки на дроті: вигляд, феєрверк і рядки балачок — усім одразу (як ефір шле свої), тост — лише з'єднанням
/// того, кому подарували, тим самим шляхом, що й гаманець (<see cref="ToastFor"/> через розсилку ігор).
/// </summary>
public sealed class HubLavkaWire(IHubContext<RadioHub> hub, IOutbox outbox, ILogger<HubLavkaWire> log) : ILavkaWire
{
    public void Look(string nick, LavkaLook? look) => All("look", new { nick, look });
    public void Fireworks(string nick) => All("fireworks", new { nick });
    public void Chat(object line) => All("chat", line);
    public void Toast(string nick, string text) => outbox.Post(new ToastFor(nick, text, "ok"));

    void All(string name, object payload) => _ = SendAsync(name, payload);

    async Task SendAsync(string name, object payload)
    {
        try { await hub.Clients.All.SendAsync(name, payload); }
        catch (Exception ex) { log.LogWarning(ex, "Лавка не розіслала {Event}", name); }
    }
}

/// <summary>
/// Підключення Лавки й HTTP (<c>/api/lavka…</c>). Кожен маршрут — окремий статичний метод, щоб тести кликали його рівно
/// так, як це робить сервер. Хаб-метод <c>Fireworks</c> живе в <see cref="RadioHub"/>.
/// </summary>
public static class LavkaSetup
{
    public sealed record BuyRequest(string? Item, string? For);
    public sealed record WearRequest(string? Slot, string? Item);
    public sealed record DedicateRequest(string? To, string? Phrase);
    public sealed record TakeDownRequest(string? Nick);

    public static IServiceCollection AddHlechykyLavka(this IServiceCollection services)
    {
        services.AddSingleton<LavkaStore>();
        // Справжні ефір, голос і розсилка; тести (і будь-хто інший) можуть підкласти свої раніше
        services.TryAddSingleton<ILavkaAir, RadioLavkaAir>();
        services.TryAddSingleton<ILavkaVoice, TtsLavkaVoice>();
        services.TryAddSingleton<ILavkaWire, HubLavkaWire>();
        services.AddSingleton<Lavka>();
        services.TryAddSingleton(_ => new LavkaPhotoDir(Paths.Resolve("data/avatars")));
        services.AddSingleton<LavkaPhotos>();
        return services;
    }

    public static WebApplication MapHlechykyLavka(this WebApplication app)
    {
        var api = app.MapGroup("/api/lavka");
        api.MapGet("", Shop);
        api.MapGet("/looks", Looks);
        api.MapPost("/buy", Buy);
        api.MapPost("/wear", Wear);
        api.MapPost("/dedicate", Dedicate);
        api.MapPost("/photo", SetPhoto);
        api.MapDelete("/photo", RemovePhoto);
        api.MapGet("/photo/{file}", (string file, LavkaPhotos photos, HttpContext c) => PhotoFile(file, photos, c));
        api.MapGet("/photos", AdminPhotos);
        api.MapPost("/photos/remove", TakeDown);
        return app;
    }

    /// <summary>GET /api/lavka — вітрина: каталог зі станом «моє / вдягнуто», вміння, фрази присвяти. Гостю — те саме, але порожнє.</summary>
    public static object Shop(HttpContext c, Lavka lavka) => lavka.View(Auth.Nick(c), Auth.IsUser(c));

    /// <summary>GET /api/lavka/looks — вигляд усіх, у кого щось вдягнуто. Публічно: бачити вигляд інших може кожен.</summary>
    public static object Looks(Lavka lavka) => new { looks = lavka.Looks() };

    /// <summary>POST /api/lavka/buy { item, for? } → { ok, message, balance }. for — нік акаунта, якому дарують.</summary>
    public static IResult Buy(HttpContext c, BuyRequest b, Lavka lavka)
    {
        var r = lavka.Buy(Auth.Nick(c), Auth.IsUser(c), b.Item, b.For);
        var body = new { ok = r.Ok, message = r.Message, balance = r.Balance ?? 0 };
        return r.Ok ? Results.Ok(body) : Results.BadRequest(body);
    }

    /// <summary>POST /api/lavka/wear { slot, item } → { ok, message }; item null — зняти.</summary>
    public static IResult Wear(HttpContext c, WearRequest b, Lavka lavka) =>
        Reply(lavka.Wear(Auth.Nick(c), Auth.IsUser(c), b.Slot, b.Item));

    /// <summary>POST /api/lavka/dedicate { to, phrase } → { ok, message }; to — нік або «*» (усім).</summary>
    public static async Task<IResult> Dedicate(HttpContext c, DedicateRequest b, Lavka lavka) =>
        Reply(await lavka.DedicateAsync(Auth.Nick(c), Auth.IsUser(c), b.To, b.Phrase));

    // ---------- своя фотка ----------

    /// <summary>
    /// POST /api/lavka/photo — тіло запиту й є фото (JPEG/PNG/WebP, до <see cref="LavkaPhotos.MaxBytes"/>), обрізане
    /// браузером. → { ok, message, url, readyAt }. Тип визначають магічні байти, а не Content-Type запиту.
    /// </summary>
    public static async Task<IResult> SetPhoto(HttpContext c, LavkaPhotos photos)
    {
        var bytes = await ReadCapped(c.Request.Body, LavkaPhotos.MaxBytes + 1, c.RequestAborted);
        return PhotoReply(photos.Set(Auth.Nick(c), Auth.IsUser(c), bytes));
    }

    /// <summary>DELETE /api/lavka/photo — прибрати своє фото. → { ok, message, url: null, readyAt }.</summary>
    public static IResult RemovePhoto(HttpContext c, LavkaPhotos photos) => PhotoReply(photos.Remove(Auth.Nick(c), Auth.IsUser(c)));

    /// <summary>
    /// GET /api/lavka/photo/&lt;хеш ніка&gt;-&lt;версія&gt;.&lt;jpg|png|webp&gt; — саме фото. Тип — за магічними байтами файла,
    /// nosniff, кеш на рік: нове фото — нова адреса, тож старе з кешу ніде не вилізе. Чуже ім'я чи нема файла — 404.
    /// </summary>
    public static IResult PhotoFile(string file, LavkaPhotos photos, HttpContext c)
    {
        if (photos.Resolve(file) is not { } path) return Results.NotFound();
        Span<byte> head = stackalloc byte[16];
        int n;
        try
        {
            using var f = File.OpenRead(path);
            n = f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        catch (IOException) { return Results.NotFound(); }     // саме прибрали
        if (LavkaImage.MimeOf(head[..n]) is not { } mime) return Results.NotFound();
        c.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        c.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(path, mime);
    }

    /// <summary>GET /api/lavka/photos — адміну: усі поставлені фото (нік, адреса, коли, розмір), свіжі згори.</summary>
    public static IResult AdminPhotos(HttpContext c, LavkaPhotos photos) => !Auth.IsAdmin(c)
        ? Results.BadRequest(new { ok = false, message = "Це бачить лише розробник" })
        : Results.Ok(new { items = photos.All().Select(p => new { nick = p.Nick, url = p.Url, at = p.At, bytes = p.Bytes }) });

    /// <summary>POST /api/lavka/photos/remove { nick } — адмін знімає фото. → { ok, message }.</summary>
    public static IResult TakeDown(HttpContext c, TakeDownRequest b, LavkaPhotos photos) => !Auth.IsAdmin(c)
        ? Results.BadRequest(new { ok = false, message = "Це вміє лише розробник" })
        : photos.TakeDown(b.Nick) is var r && r.Ok ? Results.Ok(new { ok = true, message = r.Message }) : Results.BadRequest(new { ok = false, message = r.Message });

    static IResult PhotoReply(LavkaPhotoReply r)
    {
        var body = new { ok = r.Ok, message = r.Message, url = r.Url, readyAt = r.ReadyAt };
        return r.Ok ? Results.Ok(body) : Results.BadRequest(body);
    }

    /// <summary>Тіло запиту, але не більше <paramref name="max"/> байтів: решту не читаємо — на завелике досить знати, що воно завелике.</summary>
    static async Task<byte[]> ReadCapped(Stream body, int max, CancellationToken ct)
    {
        var buf = new byte[max];
        var n = 0;
        while (n < max)
        {
            var k = await body.ReadAsync(buf.AsMemory(n), ct);
            if (k == 0) break;
            n += k;
        }
        return buf[..n];
    }

    static IResult Reply(LavkaReply r) =>
        r.Ok ? Results.Ok(new { ok = true, message = r.Message }) : Results.BadRequest(new { ok = false, message = r.Message });
}
