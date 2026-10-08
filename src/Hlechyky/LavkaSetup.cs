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
/// того, кому подарували, тим самим шляхом, що й гаманець (<see cref="ToastFor"/> через розсилку ігор). Рядок Журналу
/// (прокльон) — теж через розсилку ігор (<see cref="Journal"/>): так він ляже від імені сайту під фільтр «🎮 Ігри».
/// </summary>
public sealed class HubLavkaWire(IHubContext<RadioHub> hub, IOutbox outbox, ILogger<HubLavkaWire> log) : ILavkaWire
{
    public void Look(string nick, LavkaLook? look) => All("look", new { nick, look });
    public void Fireworks(string nick) => All("fireworks", new { nick });
    public void Chat(object line) => All("chat", line);
    public void Toast(string nick, string text) => outbox.Post(new ToastFor(nick, text, "ok"));
    public void Journal(string text) => outbox.Post(new Journal(text));

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
    public sealed record AnthemFetchRequest(string? TrackId);
    public sealed record AnthemTrackRequest(string? TrackId, double? Start, double? Len, string? Title);
    public sealed record CurseRequest(string? To, string? Item);
    public sealed record CurseIdRequest(long? Id);
    public sealed record RingRequest(string? Item);

    public static IServiceCollection AddHlechykyLavka(this IServiceCollection services)
    {
        services.AddSingleton<LavkaStore>();
        // Справжні ефір, голос і розсилка; тести (і будь-хто інший) можуть підкласти свої раніше
        services.TryAddSingleton<ILavkaAir, RadioLavkaAir>();
        services.TryAddSingleton<ILavkaVoice, TtsLavkaVoice>();
        services.TryAddSingleton<ILavkaWire, HubLavkaWire>();
        services.AddSingleton<Lavka>();
        // «Свій дзвінок» для особистих закликів (Calls) — з Лавки
        services.TryAddSingleton<IRings>(sp => sp.GetRequiredService<Lavka>());
        services.TryAddSingleton(_ => new LavkaPhotoDir(Paths.Resolve("data/avatars")));
        services.AddSingleton<LavkaPhotos>();
        // Свій трек і гімн переможця за столом (docs/games/specs/anthem.md)
        services.TryAddSingleton(_ => new AnthemDir(Paths.Resolve("data/anthems")));
        services.TryAddSingleton<IAnthemCutter, FfmpegAnthemCutter>();
        services.TryAddSingleton<IAnthemSource, YtAnthemSource>();
        services.AddSingleton<LavkaAnthems>();
        services.AddHostedService<AnthemPlayer>();
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
        api.MapPost("/curse", Curse);
        api.MapPost("/curse/ransom", Ransom);
        api.MapPost("/curse/reveal", Reveal);
        api.MapPost("/ring", Ring);
        api.MapPost("/photo", SetPhoto);
        api.MapDelete("/photo", RemovePhoto);
        api.MapGet("/photo/{file}", (string file, LavkaPhotos photos, HttpContext c) => PhotoFile(file, photos, c));
        api.MapGet("/photos", AdminPhotos);
        api.MapPost("/photos/remove", TakeDown);
        api.MapPost("/anthem", SetAnthem);
        api.MapPost("/anthem/fetch", FetchAnthemSource);
        api.MapPost("/anthem/track", CutAnthemTrack);
        api.MapGet("/anthem/src/{id}", (string id, LavkaAnthems anthems, HttpContext c) => AnthemSource(id, anthems, c));
        api.MapGet("/anthem/of/{nick}", (string nick, Lavka lavka) => AnthemOf(nick, lavka));
        api.MapGet("/anthem/{file}", (string file, LavkaAnthems anthems, HttpContext c) => AnthemFile(file, anthems, c));
        api.MapGet("/anthems", AdminAnthems);
        api.MapPost("/anthems/remove", TakeDownAnthem);
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

    // ---------- прокльон і дзвінок (docs/games/specs/flair.md) ----------

    /// <summary>POST /api/lavka/curse { to, item } → { ok, message, balance }: наслати прокльон на акаунт.</summary>
    public static IResult Curse(HttpContext c, CurseRequest b, Lavka lavka) =>
        Money(lavka.Curse(Auth.Nick(c), Auth.IsUser(c), b.To, b.Item));

    /// <summary>POST /api/lavka/curse/ransom { id } → { ok, message, balance }: відкупитись від прокльону на собі за 1000.</summary>
    public static IResult Ransom(HttpContext c, CurseIdRequest b, Lavka lavka) =>
        Money(lavka.Ransom(Auth.Nick(c), Auth.IsUser(c), b.Id));

    /// <summary>POST /api/lavka/curse/reveal { id } → { ok, message, balance }: дізнатися, від кого прокльон, за 300.</summary>
    public static IResult Reveal(HttpContext c, CurseIdRequest b, Lavka lavka) =>
        Money(lavka.Reveal(Auth.Nick(c), Auth.IsUser(c), b.Id));

    /// <summary>POST /api/lavka/ring { item } → { ok, message }: обрати свій гімн дзвінком; порожній item — без дзвінка.</summary>
    public static IResult Ring(HttpContext c, RingRequest b, Lavka lavka) =>
        Reply(lavka.SetRing(Auth.Nick(c), Auth.IsUser(c), b.Item));

    static IResult Money(LavkaReply r)
    {
        var body = new { ok = r.Ok, message = r.Message, balance = r.Balance ?? 0 };
        return r.Ok ? Results.Ok(body) : Results.BadRequest(body);
    }

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
    public static IResult TakeDown(HttpContext c, TakeDownRequest b, LavkaPhotos photos)
    {
        if (!Auth.IsAdmin(c)) return Results.BadRequest(new { ok = false, message = "Це вміє лише розробник" });
        var r = photos.TakeDown(b.Nick);
        return Reply(new LavkaReply(r.Ok, r.Message));
    }

    // ---------- гімн переможця ----------

    /// <summary>
    /// POST /api/lavka/anthem?start=&lt;с&gt;&amp;len=&lt;с&gt; — тіло запиту й є пісня чи відео (до 40 МБ), назва — у заголовку
    /// <c>X-Anthem-Title</c> (encodeURIComponent: заголовки лише ASCII). → { ok, message, url, title, readyAt }; відмова —
    /// { ok: false, message }. Числа — з крапкою, як їх пише браузер, незалежно від мови сервера.
    /// </summary>
    public static async Task<IResult> SetAnthem(HttpContext c, LavkaAnthems anthems)
    {
        if (c.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = LavkaAnthems.MaxBytes + 1;
        static double? Num(HttpContext c, string name) =>
            double.TryParse(c.Request.Query[name].ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        string? title = null;
        var raw = c.Request.Headers["X-Anthem-Title"].ToString();
        if (raw.Length > 0)
            try { title = Uri.UnescapeDataString(raw); } catch (UriFormatException) { }
        LavkaAnthemReply r;
        try
        {
            r = await anthems.SetAsync(Auth.Nick(c), Auth.IsUser(c), Num(c, "start"), Num(c, "len"), title,
                c.Request.ContentLength, c.Request.Body, c.RequestAborted);
        }
        catch (BadHttpRequestException) { r = new(false, LavkaAnthems.TooBig); }   // Kestrel обірвав тіло понад ліміт
        catch (OperationCanceledException) { return Results.Empty; }              // людина скасувала чи закрила вкладку
        return r.Ok
            ? Results.Ok(new { ok = true, message = r.Message, url = r.Url, title = r.Title, readyAt = r.ReadyAt })
            : Results.BadRequest(new { ok = false, message = r.Message });
    }

    /// <summary>
    /// POST /api/lavka/anthem/fetch { trackId } — взяти пісню з пошуку радіо (id з <c>/api/search</c>), щоб послухати й обрати
    /// уривок. Лише акаунт, «Свій трек» купувати ще не треба; до 10 скачувань на годину. → { ok, message, id, title, artist,
    /// duration, previewUrl }; відмова — { ok: false, message }.
    /// </summary>
    public static async Task<IResult> FetchAnthemSource(HttpContext c, AnthemFetchRequest b, LavkaAnthems anthems)
    {
        AnthemSourceReply r;
        try { r = await anthems.FetchAsync(Auth.Nick(c), Auth.IsUser(c), b.TrackId, c.RequestAborted); }
        catch (OperationCanceledException) { return Results.Empty; }              // людина пішла, не дочекавшись
        return r.Ok
            ? Results.Ok(new { ok = true, message = r.Message, id = r.Id, title = r.Title, artist = r.Artist, duration = r.Duration, previewUrl = r.PreviewUrl })
            : Results.BadRequest(new { ok = false, message = r.Message });
    }

    /// <summary>
    /// POST /api/lavka/anthem/track { trackId, start, len, title } — вирізати уривок із пісні, взятої через <c>/anthem/fetch</c>.
    /// Ті самі правила, що й для файла (куплений «Свій трек», раз на 2 хв, 5–15 с). → { ok, message, url, title, readyAt }.
    /// </summary>
    public static async Task<IResult> CutAnthemTrack(HttpContext c, AnthemTrackRequest b, LavkaAnthems anthems)
    {
        LavkaAnthemReply r;
        try { r = await anthems.CutTrackAsync(Auth.Nick(c), Auth.IsUser(c), b.TrackId, b.Start, b.Len, b.Title, c.RequestAborted); }
        catch (OperationCanceledException) { return Results.Empty; }
        return r.Ok
            ? Results.Ok(new { ok = true, message = r.Message, url = r.Url, title = r.Title, readyAt = r.ReadyAt })
            : Results.BadRequest(new { ok = false, message = r.Message });
    }

    /// <summary>
    /// GET /api/lavka/anthem/src/&lt;id&gt; — ціла пісня з кешу радіо, щоб послухати «звідки» й «скільки» перед нарізкою.
    /// Лише акаунтам; id — 11 знаків YouTube (регулярка), шляхів назовні нема; перемотка (range), кеш — 5 хв і лише свій.
    /// </summary>
    public static IResult AnthemSource(string id, LavkaAnthems anthems, HttpContext c)
    {
        if (!Auth.IsUser(c) || anthems.SourcePath(id) is not { } path) return Results.NotFound();
        c.Response.Headers.CacheControl = "private, max-age=300";
        c.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(path, AudioMime(path), enableRangeProcessing: true);
    }

    /// <summary>Тип звуку за розширенням файла в кеші (yt-dlp кладе m4a, opus, webm…).</summary>
    static string AudioMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".m4a" or ".aac" => "audio/mp4",
        ".opus" or ".ogg" or ".oga" => "audio/ogg",
        ".webm" => "audio/webm",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".mka" => "audio/x-matroska",
        _ => "application/octet-stream",
    };

    /// <summary>GET /api/lavka/anthem/of/{nick} — гімн у профілі: { title, emoji, url }; нема гімну, що зазвучить, — 404.</summary>
    public static IResult AnthemOf(string nick, Lavka lavka) => lavka.AnthemOf(nick) is { } a
        ? Results.Ok(new { title = a.Title, emoji = a.Emoji, url = a.Url })
        : Results.NotFound();

    /// <summary>
    /// GET /api/lavka/anthem/&lt;хеш ніка&gt;-&lt;версія&gt;-&lt;звідки&gt;-&lt;скільки&gt;.mp3 — вирізаний уривок. Лише наш mp3 (ім'я
    /// перевіряє регулярка), nosniff, кеш на рік (новий уривок — нова адреса), перемотка (range). Чуже ім'я чи нема файла — 404.
    /// </summary>
    public static IResult AnthemFile(string file, LavkaAnthems anthems, HttpContext c)
    {
        if (anthems.Resolve(file) is not { } path) return Results.NotFound();
        c.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        c.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(path, "audio/mpeg", enableRangeProcessing: true);
    }

    /// <summary>GET /api/lavka/anthems — адміну: усі свої треки [{ nick, title, url, at }], свіжі згори.</summary>
    public static IResult AdminAnthems(HttpContext c, LavkaAnthems anthems) => !Auth.IsAdmin(c)
        ? Results.BadRequest(new { ok = false, message = "Це бачить лише розробник" })
        : Results.Ok(anthems.All().Select(a => new { nick = a.Nick, title = a.Title, url = a.Url, at = a.At }).ToList());

    /// <summary>POST /api/lavka/anthems/remove { nick } — адмін знімає свій трек. → { ok, message }.</summary>
    public static IResult TakeDownAnthem(HttpContext c, TakeDownRequest b, LavkaAnthems anthems) => !Auth.IsAdmin(c)
        ? Results.BadRequest(new { ok = false, message = "Це вміє лише розробник" })
        : Reply(anthems.TakeDown(b.Nick));

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
