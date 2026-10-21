using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hlechyky.Games;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>«🚨 Тривога» (секція Tryvoga, docs/tryvoga.md). Підхоплюється наживо.</summary>
public sealed class TryvogaOptions
{
    /// <summary>false — нічого не опитуємо й не пишемо, API каже <c>off</c>, на сайті нема ні плашки, ні мапи.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>NEPTUN: <c>/api/v1/alerts</c> і <c>/api/v1/threats</c>. Ключів не треба; частіше ніж раз на 5 с не можна.</summary>
    public string BaseUrl { get; set; } = "https://neptun.in.ua";
    public int AlertsSeconds { get; set; } = 15;
    public int ThreatsSeconds { get; set; } = 10;
    /// <summary>Стільки без жодного вдалого опитування тривог — і ми вже не знаємо, чи тихо: плашка сіра.</summary>
    public int StaleSeconds { get; set; } = 180;
    /// <summary>Писати архів цілей і тривог (для аналітики потім).</summary>
    public bool Archive { get; set; } = true;
    public string ArchivePath { get; set; } = "data/tryvoga.db";
    /// <summary>Район, поки людина не обрала свій: ключ NEPTUN, його область і як його назвати на плашці.</summary>
    public string DefaultKey { get; set; } = "житомирський";
    public string DefaultOblast { get; set; } = "житомирська";
    public string DefaultName { get; set; } = "Житомир";
}

/// <summary>Тривога в районі (<see cref="Whole"/> = false) чи на всю одиницю з обласного списку (окуповані, м. Київ).</summary>
public sealed record AlertRow(string Key, string Name, string Oblast, string Level, DateTimeOffset Since, IReadOnlyList<string> Reasons, bool Whole);

public sealed record AlertSet(long Version, DateTimeOffset? UpdatedAt, IReadOnlyList<AlertRow> Rows);

public sealed record ThreatPoint(double Lat, double Lon, DateTimeOffset T);

public sealed record Threat(
    string Id, string Type, string? Title, double Lat, double Lon, double? Heading, double? SpeedKmh,
    string? Region, string? District, string? Locality, string? Confidence, int? Sources, int? Count,
    string? Quality, double? UncertaintyKm, string? Lifecycle, DateTimeOffset? UpdatedAt, string? Text,
    bool AreaOnly, bool Advisory, IReadOnlyList<ThreatPoint> Trail, string Raw)
{
    /// <summary>Курс — припущення з повідомлення («курсом на …», <c>presumptiveCourse</c>), а не з руху. Так у
    /// більшості цілей (22.10: 226 з 252 з курсом); на мапі такий курс — лише «промінь».</summary>
    public bool Presumptive { get; init; }

    /// <summary>NEPTUN виміряв рух (<c>velocity.bearingDeg</c>, ~1 % цілей) — курс підтверджений.</summary>
    public bool Measured { get; init; }
}

public sealed record ThreatSet(string Epoch, DateTimeOffset? ServerTime, IReadOnlyList<Threat> Items);

/// <summary>Що зараз у районі людини: рівень найвищий з усіх записів, що його стосуються, відколи — найраніше.</summary>
public sealed record AlertStatus(bool On, string? Level, DateTimeOffset? Since, IReadOnlyList<string> Reasons);

/// <summary>
/// Повітряні тривоги й те, що в небі, — з NEPTUN, одним опитуванням на всіх. Тривоги раз на 15 с: змінились чи дані
/// стали застарілими — шлемо в хаб <c>tryvoga</c>, і кожна вкладка перепитує свій район. Цілі раз на 10 с — для мапи
/// і для архіву (<see cref="TryvogaArchive"/>), який пишеться, навіть коли сайт ніхто не дивиться.
/// <para>
/// Чесність понад усе: коли NEPTUN мовчить довше за <see cref="TryvogaOptions.StaleSeconds"/>, ми кажемо «не знаю», а
/// не «тихо». Правило «чи стосується тривога мого району» — тут (<see cref="Status"/>), клієнт лише малює.
/// </para>
/// </summary>
public sealed partial class Tryvoga(
    IOptionsMonitor<TryvogaOptions> options, IHubContext<RadioHub> hub, TryvogaArchive archive, IGameStore store,
    IClock clock, ILogger<Tryvoga> log) : BackgroundService
{
    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var h = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Hlechyky/1.0 (+https://hlechyky.pp.ua)");
        return h;
    }

    TryvogaOptions O => options.CurrentValue;

    /// <summary>Знімок разом із часом опитування — одним незмінним об'єктом: пише фоновий цикл, читають запити.</summary>
    sealed record Snap<T>(T Set, DateTimeOffset At);

    volatile Snap<AlertSet>? _alerts;   // останнє вдале опитування тривог
    volatile Snap<ThreatSet>? _threats;
    bool _pushedStale;

    public bool Enabled => O.Enabled;

    public bool Stale(DateTimeOffset now) => Old(_alerts?.At, now);

    bool Old(DateTimeOffset? at, DateTimeOffset now) => at is not { } ok || (now - ok).TotalSeconds > Math.Max(30, O.StaleSeconds);

    /// <summary>Чим знімки тривог відрізняються: версія NEPTUN, а без неї — сам перелік (інакше хаб смикав би всіх щоразу).</summary>
    static string Sig(AlertSet s) => s.Version != 0
        ? s.Version.ToString(CultureInfo.InvariantCulture)
        : string.Join(';', s.Rows.Select(r => r.Key + "|" + r.Level + "|" + r.Since.ToUnixTimeSeconds()));

    // ---------- розбір відповідей NEPTUN ----------

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static DateTimeOffset? Time(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;

    /// <summary>Рівень NEPTUN: <c>yellow</c> — дронова загроза, <c>red</c> — ракетна. Без рівня — це просто тривога, тобто червона.</summary>
    static string Level(string? l) => l is "yellow" or "red" ? l : "red";

    /// <summary>Відповідь <c>/api/v1/alerts</c>. null — це не вона (сторінка помилки, порожньо).</summary>
    public static AlertSet? ParseAlerts(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("raions", out var raions) || raions.ValueKind != JsonValueKind.Array)
                return null;
            var rows = new List<AlertRow>();
            void Read(JsonElement list, bool whole)
            {
                foreach (var a in list.EnumerateArray())
                {
                    var key = Str(a, "key");
                    var since = Time(Str(a, "since"));
                    if (string.IsNullOrWhiteSpace(key) || since is null) continue;
                    var reasons = a.TryGetProperty("reasons", out var r) && r.ValueKind == JsonValueKind.Array
                        ? r.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                        : [];
                    rows.Add(new AlertRow(key.Trim(), Str(a, "name") ?? key, Str(a, "oblast") ?? "", Level(Str(a, "level")), since.Value, reasons, whole));
                }
            }
            Read(raions, false);
            if (root.TryGetProperty("oblasts", out var oblasts) && oblasts.ValueKind == JsonValueKind.Array) Read(oblasts, true);
            var version = root.TryGetProperty("version", out var v) && v.TryGetInt64(out var n) ? n : 0;
            return new AlertSet(version, Time(Str(root, "updatedAt")), rows);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Відповідь <c>/api/v1/threats</c>. Ціль без id, типу чи координат пропускаємо.</summary>
    public static ThreatSet? ParseThreats(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("threats", out var list) || list.ValueKind != JsonValueKind.Array)
                return null;
            var items = new List<Threat>();
            foreach (var t in list.EnumerateArray())
            {
                var id = Str(t, "id");
                var type = Str(t, "type");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(type) || Num(t, "lat") is not { } lat || Num(t, "lon") is not { } lon) continue;
                var trail = new List<ThreatPoint>();
                if (t.TryGetProperty("trail", out var tr) && tr.ValueKind == JsonValueKind.Array)
                    foreach (var p in tr.EnumerateArray())
                        if (Num(p, "lat") is { } pl && Num(p, "lon") is { } po && Time(Str(p, "t")) is { } pt)
                            trail.Add(new ThreatPoint(pl, po, pt));
                var hasVel = t.TryGetProperty("velocity", out var vel) && vel.ValueKind == JsonValueKind.Object;
                double? speed = hasVel ? Num(vel, "speedKmh") : null;
                double? bearing = hasVel ? Num(vel, "bearingDeg") : null;
                // курсу нема, а рух виміряно — курс і є напрям руху
                items.Add(new Threat(
                    id, type, Str(t, "title"), lat, lon, Num(t, "heading") ?? bearing, speed,
                    Str(t, "region"), Str(t, "district"), Str(t, "locality"), Str(t, "confidenceLevel"),
                    (int?)Num(t, "sourceCount"), (int?)Num(t, "count"), Str(t, "positionQuality"), Num(t, "uncertaintyKm"),
                    Str(t, "lifecycle"), Time(Str(t, "updatedAt")), Str(t, "explanationShort"),
                    Flag(t, "areaOnly"), Flag(t, "advisory"), trail, WithoutTrail(t))
                {
                    Presumptive = Flag(t, "presumptiveCourse") && bearing is null,
                    Measured = bearing is not null,
                });
            }
            return new ThreatSet(Str(root, "epoch") ?? "", Time(Str(root, "serverTime")), items);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Сирий запис цілі для архіву — без сліду (точки сліду й так лягають окремими рядками), кирилицею.</summary>
    static string WithoutTrail(JsonElement t)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            foreach (var p in t.EnumerateObject())
                if (p.Name != "trail") p.WriteTo(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Чи тривога в районі <paramref name="key"/> з області <paramref name="oblast"/>: запис самого району (м. Київ і
    /// Севастополь — теж «район», ключ той самий) або запис на всю його область.
    /// </summary>
    public static AlertStatus Status(AlertSet? set, string? key, string? oblast)
    {
        if (set is null || string.IsNullOrEmpty(key)) return new AlertStatus(false, null, null, []);
        var hits = set.Rows.Where(r => r.Key == key || (r.Whole && !string.IsNullOrEmpty(oblast) && r.Key == oblast)).ToList();
        if (hits.Count == 0) return new AlertStatus(false, null, null, []);
        var level = hits.Any(r => r.Level == "red") ? "red" : "yellow";
        return new AlertStatus(true, level, hits.Min(r => r.Since), hits.SelectMany(r => r.Reasons).Distinct().ToList());
    }

    // ---------- що бачить сайт ----------

    [GeneratedRegex(@"^[\p{L}\p{M}0-9 .'’ʼ-]{2,60}$")]
    private static partial Regex KeyRx();

    public sealed record Region(string Key, string Oblast, string Name);

    static string StoreKey(string nick) => "tryvoga:" + Auth.NickKey(nick);

    /// <summary>Район акаунта (обраний з будь-якого пристрою). Гість — null: його район живе в браузері.</summary>
    public Region? Mine(string nick)
    {
        if (Auth.IsGuestNick(nick)) return null;
        var json = store.LoadState(StoreKey(nick));
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<Region>(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Запам'ятати район акаунта. false — гість або криві дані.</summary>
    public bool SetMine(string nick, string? key, string? oblast, string? name)
    {
        if (Auth.IsGuestNick(nick) || !Valid(key) || !Valid(oblast) || string.IsNullOrWhiteSpace(name) || name.Length > 80) return false;
        store.SaveState(StoreKey(nick), JsonSerializer.Serialize(new Region(key!.Trim(), oblast!.Trim(), name.Trim())));
        return true;
    }

    public static bool Valid(string? key) => key is not null && KeyRx().IsMatch(key.Trim());

    static string Iso(DateTimeOffset? t) => t?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) ?? "";

    /// <summary>GET /api/tryvoga: усі тривоги (для мапи) і стан району, про який питають (raion, oblast), або обраного акаунтом.</summary>
    public object View(string nick, string? k, string? o)
    {
        var opt = O;
        if (!opt.Enabled) return new { off = true };
        var now = clock.UtcNow;
        var mine = Mine(nick);
        var def = new Region(opt.DefaultKey, opt.DefaultOblast, opt.DefaultName);
        Region ask = Valid(k) && Valid(o) ? new Region(k!.Trim(), o!.Trim(), "") : mine ?? def;
        var snap = _alerts;
        var set = snap?.Set;
        var stale = Stale(now);
        var st = Status(set, ask.Key, ask.Oblast);
        return new
        {
            off = false,
            at = Iso(snap?.At),
            stale,
            version = set?.Version ?? 0,
            status = new { on = st.On, level = st.Level, since = Iso(st.Since), reasons = st.Reasons },
            my = mine,
            def,
            alerts = (set?.Rows ?? []).Select(r => new { key = r.Key, name = r.Name, oblast = r.Oblast, level = r.Level, since = Iso(r.Since), reasons = r.Reasons, whole = r.Whole }),
        };
    }

    /// <summary>GET /api/tryvoga/threats: останній знімок цілей, стиснутий для мапи (слід — до 40 останніх точок).</summary>
    public object ThreatsView()
    {
        if (!O.Enabled) return new { off = true };
        var now = clock.UtcNow;
        var snap = _threats;
        var set = snap?.Set;
        static double R(double d) => Math.Round(d, 4);
        return new
        {
            off = false,
            at = Iso(snap?.At),
            stale = Old(snap?.At, now),
            threats = (set?.Items ?? []).Select(t => new
            {
                id = t.Id, type = t.Type, title = t.Title, lat = R(t.Lat), lon = R(t.Lon), heading = t.Heading, speed = t.SpeedKmh,
                region = t.Region, locality = t.Locality, confidence = t.Confidence, sources = t.Sources, count = t.Count,
                quality = t.Quality, unc = t.UncertaintyKm, lifecycle = t.Lifecycle, updated = Iso(t.UpdatedAt), text = t.Text,
                areaOnly = t.AreaOnly, advisory = t.Advisory, presumptive = t.Presumptive, measured = t.Measured,
                trail = t.Trail.OrderBy(p => p.T).TakeLast(40).Select(p => new[] { R(p.Lat), R(p.Lon) }),
            }),
        };
    }

    // ---------- опитування ----------

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var nextAlerts = DateTimeOffset.MinValue;
        var nextThreats = DateTimeOffset.MinValue;
        int alertFails = 0, threatFails = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { break; }
            var opt = O;
            if (!opt.Enabled) continue;
            var now = clock.UtcNow;
            if (now >= nextAlerts)
            {
                var ok = await PollAlerts(opt, ct);
                alertFails = ok ? 0 : alertFails + 1;
                nextAlerts = clock.UtcNow + Backoff(Math.Max(5, opt.AlertsSeconds), alertFails);
            }
            if (now >= nextThreats)
            {
                var ok = await PollThreats(opt, ct);
                threatFails = ok ? 0 : threatFails + 1;
                nextThreats = clock.UtcNow + Backoff(Math.Max(5, opt.ThreatsSeconds), threatFails);
            }
            // дані застаріли (чи ожили) без нової версії — теж новина для плашки
            var stale = Stale(clock.UtcNow);
            if (stale != _pushedStale) await Push(stale);
        }
    }

    /// <summary>Збій — пауза вдвічі довша за кожен поспіль, але не довше 2 хв: не довбемо NEPTUN, коли він лежить.</summary>
    static TimeSpan Backoff(int seconds, int fails) =>
        TimeSpan.FromSeconds(fails == 0 ? seconds : Math.Min(120, seconds * Math.Pow(2, Math.Min(fails, 5))));

    async Task<string?> Get(TryvogaOptions opt, string path, CancellationToken ct)
    {
        try
        {
            using var res = await Http.GetAsync(opt.BaseUrl.TrimEnd('/') + path, ct);
            if (!res.IsSuccessStatusCode) { log.LogDebug("NEPTUN {Path}: {Code}", path, (int)res.StatusCode); return null; }
            return await res.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { log.LogDebug(ex, "NEPTUN {Path} не відповів", path); return null; }
    }

    async Task<bool> PollAlerts(TryvogaOptions opt, CancellationToken ct)
    {
        var json = await Get(opt, "/api/v1/alerts", ct);
        var set = json is null ? null : ParseAlerts(json);
        if (set is null) return false;
        var was = _alerts?.Set;
        _alerts = new Snap<AlertSet>(set, clock.UtcNow);
        if (opt.Archive) archive.SaveAlerts(set, clock.UtcNow);
        if (was is null || Sig(was) != Sig(set)) await Push(Stale(clock.UtcNow));
        return true;
    }

    async Task<bool> PollThreats(TryvogaOptions opt, CancellationToken ct)
    {
        var json = await Get(opt, "/api/v1/threats", ct);
        var set = json is null ? null : ParseThreats(json);
        if (set is null) return false;
        _threats = new Snap<ThreatSet>(set, clock.UtcNow);
        if (opt.Archive) archive.SaveThreats(set, clock.UtcNow);
        return true;
    }

    /// <summary>Хабом летить лише «щось змінилось»: свій район кожна вкладка перепитує сама (GET /api/tryvoga).</summary>
    async Task Push(bool stale)
    {
        _pushedStale = stale;
        try { await hub.Clients.All.SendAsync("tryvoga", new { version = _alerts?.Set.Version ?? 0, stale }); }
        catch (Exception ex) { log.LogDebug(ex, "хаб: tryvoga"); }
    }

    public sealed record RegionBody(string? Key, string? Oblast, string? Name);

    public static void Map(WebApplication app)
    {
        // raion/oblast, а не k: ?k= на цьому сайті — вхід адміна за ключем, його перехоплює Auth і веде на /
        app.MapGet("/api/tryvoga", (HttpContext c, Tryvoga t, string? raion, string? oblast) => Results.Ok(t.View(Auth.Nick(c), raion, oblast)));
        app.MapGet("/api/tryvoga/threats", (Tryvoga t) => Results.Ok(t.ThreatsView()));
        app.MapPost("/api/tryvoga/region", (HttpContext c, RegionBody body, Tryvoga t) =>
            Results.Ok(new { ok = t.SetMine(Auth.Nick(c), body.Key, body.Oblast, body.Name) }));
    }

    /// <summary>Для тестів: підкласти знімки, ніби їх щойно опитали.</summary>
    public void Feed(AlertSet? alerts, ThreatSet? threats)
    {
        if (alerts is not null) _alerts = new Snap<AlertSet>(alerts, clock.UtcNow);
        if (threats is not null) _threats = new Snap<ThreatSet>(threats, clock.UtcNow);
    }
}
