using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Hlechyky.Games;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>«🌦 Погода» (секція Weather). Підхоплюється наживо.</summary>
public sealed class WeatherOptions
{
    /// <summary>false — нікого не питаємо, <c>/api/weather</c> каже <c>off</c>, небо на сайті просто ясне.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Open-Meteo: відкритий безплатний сервіс без ключа. Питаємо лише ми, з сервера, — браузери людей туди не ходять.</summary>
    public string BaseUrl { get; set; } = "https://api.open-meteo.com";
    /// <summary>Скільки тримаємо відповідь для області. Open-Meteo оновлює поточну погоду раз на 15 хвилин.</summary>
    public int CacheMinutes { get; set; } = 15;
    /// <summary>Глек раз на день каже в Балачках про погоду — лише коли є про що: злива, сніг, ожеледь, мороз, спека, вітрюган.</summary>
    public bool GlekTalks { get; set; } = true;
    /// <summary>О котрій за Києвом (і ще три години після — якщо сервер саме тоді спав).</summary>
    public int GlekHour { get; set; } = 8;
    /// <summary>Область, про погоду якої каже Глек (Балачки одні на всіх). Порожньо — типова область «Тривоги».</summary>
    public string Home { get; set; } = "";
}

/// <summary>Погода зараз в обласному центрі. <see cref="Code"/> — код WMO, як його віддає Open-Meteo.</summary>
public sealed record WeatherNow(string Oblast, string Name, double Lat, double Lon, double Temp, int Code, int Cover, double Wind, DateTimeOffset At);

/// <summary>Що обіцяють на сьогодні — з цього Глек складає своє слово.</summary>
public sealed record WeatherDay(int Code, double TempMax, double TempMin, double PrecipMm, int PrecipChance, double GustKmh);

/// <summary>
/// Справжня погода для неба на сайті (web/sky.js) і для Глека. Погода — обласного центру тієї області, яку людина
/// вибрала для «Тривоги»; питаємо Open-Meteo на вимогу й тримаємо відповідь <see cref="WeatherOptions.CacheMinutes"/>
/// хвилин, тож скільки б людей не сиділо на сайті, запитів — щонайбільше по одному на область за чверть години.
/// Сервіс мовчить — віддаємо останнє, що знали (до двох годин), далі чесно <c>off</c>: небо лишається ясним, сайт цілий.
/// </summary>
public sealed class Weather(
    IOptionsMonitor<WeatherOptions> options, IOptionsMonitor<TryvogaOptions> tryvoga, IGameStore store, RadioEngine radio,
    IClock clock, ILogger<Weather> log) : BackgroundService
{
    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var h = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(12) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Hlechyky/1.0 (+https://hlechyky.pp.ua)");
        return h;
    }

    WeatherOptions O => options.CurrentValue;

    /// <summary>Обласні центри: ключ — область, як її зве NEPTUN і мапа «Тривоги» (web/static/tryvoga-map.json).</summary>
    public static readonly IReadOnlyDictionary<string, (string Name, double Lat, double Lon)> Centers =
        new Dictionary<string, (string, double, double)>(StringComparer.OrdinalIgnoreCase)
        {
            ["вінницька"] = ("Вінниця", 49.23, 28.47), ["волинська"] = ("Луцьк", 50.75, 25.33), ["дніпропетровська"] = ("Дніпро", 48.46, 35.05),
            ["донецька"] = ("Краматорськ", 48.74, 37.58), ["житомирська"] = ("Житомир", 50.25, 28.66), ["закарпатська"] = ("Ужгород", 48.62, 22.29),
            ["запорізька"] = ("Запоріжжя", 47.84, 35.14), ["івано-франківська"] = ("Івано-Франківськ", 48.92, 24.71), ["київська"] = ("Київ", 50.45, 30.52),
            ["м. київ"] = ("Київ", 50.45, 30.52), ["кіровоградська"] = ("Кропивницький", 48.51, 32.26), ["луганська"] = ("Сіверськодонецьк", 48.95, 38.49),
            ["львівська"] = ("Львів", 49.84, 24.03), ["миколаївська"] = ("Миколаїв", 46.97, 32.00), ["одеська"] = ("Одеса", 46.48, 30.73),
            ["полтавська"] = ("Полтава", 49.59, 34.55), ["рівненська"] = ("Рівне", 50.62, 26.25), ["сумська"] = ("Суми", 50.91, 34.80),
            ["тернопільська"] = ("Тернопіль", 49.55, 25.59), ["харківська"] = ("Харків", 49.99, 36.23), ["херсонська"] = ("Херсон", 46.64, 32.61),
            ["хмельницька"] = ("Хмельницький", 49.42, 26.98), ["черкаська"] = ("Черкаси", 49.44, 32.06), ["чернівецька"] = ("Чернівці", 48.29, 25.94),
            ["чернігівська"] = ("Чернігів", 51.49, 31.29), ["автономна республіка крим"] = ("Сімферополь", 44.95, 34.10), ["севастополь"] = ("Севастополь", 44.62, 33.52),
        };

    sealed record Cached(WeatherNow Now, WeatherDay? Day, DateTimeOffset At);

    readonly ConcurrentDictionary<string, Cached> _cache = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Область за замовчуванням: та сама, що й у «Тривоги», поки людина не обрала свою.</summary>
    string HomeOblast => string.IsNullOrWhiteSpace(O.Home) ? tryvoga.CurrentValue.DefaultOblast : O.Home.Trim();

    // ---------- розбір відповіді Open-Meteo ----------

    static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    static double? First(JsonElement daily, string name) =>
        daily.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0
        && a[0].ValueKind == JsonValueKind.Number && a[0].TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    /// <summary>Відповідь <c>/v1/forecast</c>. null — це не вона (сторінка помилки, порожньо, нема температури чи коду).</summary>
    public static (WeatherNow Now, WeatherDay? Day)? Parse(string json, string oblast, DateTimeOffset at)
    {
        if (!Centers.TryGetValue(oblast.Trim(), out var c)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("current", out var cur) || cur.ValueKind != JsonValueKind.Object) return null;
            if (Num(cur, "temperature_2m") is not { } temp || Num(cur, "weather_code") is not { } code) return null;
            var now = new WeatherNow(oblast.Trim().ToLowerInvariant(), c.Name, c.Lat, c.Lon, Math.Round(temp, 1), (int)code,
                (int)Math.Clamp(Num(cur, "cloud_cover") ?? (code >= 3 ? 90 : code >= 1 ? 40 : 0), 0, 100), Math.Round(Num(cur, "wind_speed_10m") ?? 0, 1), at);
            WeatherDay? day = null;
            if (root.TryGetProperty("daily", out var d) && d.ValueKind == JsonValueKind.Object && First(d, "weather_code") is { } dc)
                day = new WeatherDay((int)dc, First(d, "temperature_2m_max") ?? temp, First(d, "temperature_2m_min") ?? temp,
                    First(d, "precipitation_sum") ?? 0, (int)(First(d, "precipitation_probability_max") ?? 0), First(d, "wind_gusts_10m_max") ?? 0);
            return (now, day);
        }
        catch (JsonException) { return null; }
    }

    // ---------- слово Глека ----------

    static string Deg(double t) => (t > 0 ? "+" : t < 0 ? "−" : "") + Math.Abs(Math.Round(t)).ToString(CultureInfo.InvariantCulture) + "°";

    /// <summary>
    /// Що Глек каже про сьогоднішню погоду — або null, коли день звичайний: зайвий раз у Балачки він не лізе.
    /// Найнебезпечніше — першим: гроза, ожеледь, злива, сніг, мороз, спека, вітер, просто дощ. <paramref name="pick"/> —
    /// яку з двох-трьох фраз узяти (день року), щоб не повторювався слово в слово.
    /// </summary>
    public static string? GlekLine(WeatherDay d, string city, int pick)
    {
        string One(params string[] lines) => lines[((pick % lines.Length) + lines.Length) % lines.Length];
        if (d.Code >= 95) return One(
            $"⛈ {city}: сьогодні гримітиме. Сидіть у хаті, слухайте радіо — і зарядку з розетки висмикніть, коли блискатиме.",
            $"⛈ {city}: обіцяють грозу. Хто надворі — під дерево не ставати, під парасолю можна.");
        if (d.Code is 56 or 57 or 66 or 67) return One(
            $"🧊 {city}: дощ із морозом, буде ожеледь. Ходіть дрібненько, як гуси, і нікуди не поспішайте.",
            $"🧊 {city}: слизько буде, як на вимитій підлозі. Кому за кермо — виїжджайте раніше.");
        if (d.PrecipMm >= 10 && d.Code is not (>= 71 and <= 77) and not (85 or 86)) return One(
            $"☔ {city}: сьогодні литиме як з відра — до {Math.Round(d.PrecipMm)} мм. Парасольку не забудьте, а краще дві.",
            $"☔ {city}: злива на підході. Беріть парасольку — мені в дощ добре, я полив'яний, а ви ні.");
        if (d.Code is (>= 71 and <= 77) or 85 or 86) return One(
            $"❄ {city}: сьогодні сніжитиме. Шапку на вуха, а ввечері гарячого чаю й за стіл до гурту.",
            $"❄ {city}: сніг іде. Взувайтесь тепліше — простуджених я в ефірі чую одразу.");
        if (d.TempMin <= -10) return One(
            $"🥶 {city}: мороз, уночі до {Deg(d.TempMin)}. Вдягайтесь як капуста — у сто одежин.",
            $"🥶 {city}: надворі {Deg(d.TempMin)}. Навіть я б тріснув. Рукавиці не забудьте.");
        if (d.TempMax >= 32) return One(
            $"🥵 {city}: спека, до {Deg(d.TempMax)}. Пийте воду — в мене її повно, я ж глек.",
            $"🥵 {city}: сьогодні {Deg(d.TempMax)}. У тінь, під вентилятор і з холодним узваром.");
        if (d.GustKmh >= 60) return One(
            $"💨 {city}: вітрюган, пориви до {Math.Round(d.GustKmh)} км/год. Шапки тримайте, а з балкона все зайве приберіть.",
            $"💨 {city}: дме так, що глечики з тину злітають. Обережно надворі.");
        if (d.PrecipMm >= 2 && d.PrecipChance >= 60) return One(
            $"🌧 {city}: сьогодні дощитиме. Парасолька в сумці зайвою не буде.",
            $"🌧 {city}: дощик на сьогодні. Беріть парасольку — і до нас, тут сухо.");
        return null;
    }

    // ---------- запит ----------

    string Url((string Name, double Lat, double Lon) c) => O.BaseUrl.TrimEnd('/') + "/v1/forecast"
        + "?latitude=" + c.Lat.ToString("0.##", CultureInfo.InvariantCulture) + "&longitude=" + c.Lon.ToString("0.##", CultureInfo.InvariantCulture)
        + "&current=temperature_2m,weather_code,cloud_cover,wind_speed_10m"
        + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_sum,precipitation_probability_max,wind_gusts_10m_max"
        + "&timezone=Europe%2FKyiv&forecast_days=1";

    /// <summary>Погода області: з пам'яті, якщо свіжа, інакше питаємо Open-Meteo. null — вимкнено, невідома область або сервіс довго мовчить.</summary>
    async Task<Cached?> Get(string? oblast, CancellationToken ct)
    {
        if (!O.Enabled) return null;
        var key = string.IsNullOrWhiteSpace(oblast) || !Centers.ContainsKey(oblast.Trim()) ? HomeOblast : oblast.Trim();
        if (!Centers.TryGetValue(key, out var center)) return null;
        var now = clock.UtcNow;
        var fresh = TimeSpan.FromMinutes(Math.Clamp(O.CacheMinutes, 5, 180));
        if (_cache.TryGetValue(key, out var have) && now - have.At < fresh) return have;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out have) && clock.UtcNow - have.At < fresh) return have;
            try
            {
                var json = await Http.GetStringAsync(Url(center), ct);
                if (Parse(json, key, clock.UtcNow) is { } p)
                {
                    var c = new Cached(p.Now, p.Day, clock.UtcNow);
                    _cache[key] = c;
                    return c;
                }
                log.LogWarning("Погода: Open-Meteo відповів не тим ({Oblast})", key);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                log.LogWarning("Погода: Open-Meteo мовчить ({Oblast}): {Why}", key, e.Message);
            }
            // Сервіс мовчить: дві години живемо зі старим, далі чесно кажемо «не знаю».
            return have is not null && clock.UtcNow - have.At < TimeSpan.FromHours(2) ? have : null;
        }
        finally { _gate.Release(); }
    }

    public async Task<object> View(string? oblast, CancellationToken ct)
    {
        var c = await Get(oblast, ct);
        if (c is null) return new { off = true };
        var n = c.Now;
        return new { oblast = n.Oblast, name = n.Name, lat = n.Lat, lon = n.Lon, t = n.Temp, code = n.Code, cover = n.Cover, wind = n.Wind,
            at = n.At.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) };
    }

    // ---------- Глек у Балачках: раз на день і лише коли є про що ----------

    const string SaidKey = "weather:said";

    /// <summary>Чи час Глекові сказати про погоду: київська година у вікні й сьогодні ще не казав.</summary>
    public static bool Due(DateTimeOffset now, int hour, string? saidDay, out string today)
    {
        var k = TimeZoneInfo.ConvertTime(now, Days.Kyiv);
        today = k.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var from = Math.Clamp(hour, 0, 20);
        return saidDay != today && k.Hour >= from && k.Hour < from + 3;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(5), ct); } catch (OperationCanceledException) { return; }
            try
            {
                if (!O.Enabled || !O.GlekTalks || !Due(clock.UtcNow, O.GlekHour, store.LoadState(SaidKey), out var today)) continue;
                var c = await Get(HomeOblast, ct);
                if (c?.Day is not { } day) continue;                       // сервіс мовчить — спробуємо за п'ять хвилин
                store.SaveState(SaidKey, today);                           // сказав чи ні — сьогодні більше не питаємо
                var k = TimeZoneInfo.ConvertTime(clock.UtcNow, Days.Kyiv);
                if (GlekLine(day, c.Now.Name, k.DayOfYear) is { } line) await radio.SayAsync(line);
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Погода: слово Глека не вийшло"); }
        }
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/weather", async (Weather w, string? oblast, CancellationToken ct) => Results.Ok(await w.View(oblast, ct)));
    }
}
