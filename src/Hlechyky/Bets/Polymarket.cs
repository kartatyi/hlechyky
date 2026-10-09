using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Hlechyky.Games;
using Microsoft.Extensions.Options;

namespace Hlechyky.Bets;

/// <summary>Варіант із Polymarket: назва і ціна (= імовірність, «світ думає»).</summary>
public sealed record PmOption(string Title, double P);

/// <summary>Ринок події з кількома такими, як «A vs B — More Markets»: адмін обирає, який із них брати.</summary>
public sealed record PmMarket(string Id, string Question, IReadOnlyList<PmOption> Options);

/// <summary>Картка огляду: що за подія, до коли, обіг, перші варіанти.</summary>
public sealed record PmCard(string Slug, string Title, string Url, DateTimeOffset? EndDate, double Volume, double Volume24h, string? Image,
    int Markets, IReadOnlyList<PmOption> Top);

/// <summary>Чернетка події для редактора: питання, варіанти з цінами, кінець. <see cref="Markets"/> — коли ринків кілька.</summary>
public sealed record PmDraft(string Slug, string Title, string Description, string Url, DateTimeOffset? EndDate,
    IReadOnlyList<PmOption> Options, IReadOnlyList<PmMarket> Markets, string? Market);

public sealed record PmResult<T>(T? Value, string? Error = null, int Status = 200) where T : class
{
    public bool Ok => Error is null;
}

/// <summary>
/// Огляд Polymarket (gamma-api, без ключа) — сервер ходить сам і кешує на <c>Bets:Polymarket:CacheSeconds</c>, браузер туди
/// не ходить. Таймаут чи помилка — зрозуміла відмова «Polymarket не відповідає», не падіння. Тести підсовують свій
/// HttpMessageHandler замість мережі.
/// </summary>
public sealed class Polymarket
{
    public const string Down = "Polymarket не відповідає — спробуй трохи згодом";
    public const string NotFound = "Такої події на Polymarket нема";
    public const int PageSize = 20;
    const int CacheMax = 300;

    /// <summary>
    /// Вкладки огляду. Теги перевірено curl-ом 09.10: soccer, sports, ukraine, politics, crypto, pop-culture існують.
    /// Для України тег дає більше живого, ніж пошук «ukraine» (50+ відкритих проти ~20 серед 50 знайдених).
    /// </summary>
    public static readonly IReadOnlyList<(string Id, string Title, string? Tag)> Cats =
    [
        ("top", "🔥 Топ за добу", null),
        ("soccer", "⚽ Футбол", "soccer"),
        ("sports", "🏀 Спорт", "sports"),
        ("ukraine", "🇺🇦 Україна", "ukraine"),
        ("politics", "🏛 Політика", "politics"),
        ("crypto", "₿ Крипта", "crypto"),
        ("pop-culture", "🎬 Поп-культура", "pop-culture"),
        ("soon", "⏳ Скоро закінчуються", null),
    ];

    readonly HttpClient _http;
    readonly IOptionsMonitor<BetsOptions> _opts;
    readonly IClock _clock;
    readonly ILogger<Polymarket> _log;
    readonly ConcurrentDictionary<string, (DateTimeOffset At, HttpStatusCode Code, string Body)> _cache = new(StringComparer.Ordinal);

    public Polymarket(IOptionsMonitor<BetsOptions> opts, IClock clock, ILogger<Polymarket> log, HttpMessageHandler? handler = null)
    {
        _opts = opts;
        _clock = clock;
        _log = log;
        // Таймаут — свій на кожен запит (з конфігу наживо), тож у самого клієнта його нема
        _http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Hlechyky/1.0 (+bets)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    PolymarketOptions O => _opts.CurrentValue.Polymarket ?? new PolymarketOptions();

    public static string EventUrl(string slug) => "https://polymarket.com/event/" + slug;

    /// <summary>slug — лише латиниця, цифри й дефіс: він іде в шлях запиту.</summary>
    public static bool SlugOk(string? slug) =>
        !string.IsNullOrEmpty(slug) && slug.Length <= 200 && slug.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    // ---------------------------------------------------------------- огляд

    /// <summary>Картки за вкладкою (<paramref name="cat"/>) або пошуком (<paramref name="q"/>, тоді вкладка не важить).</summary>
    public async Task<PmResult<PmFeed>> Feed(string? cat, string? q, int offset, CancellationToken ct = default)
    {
        offset = Math.Clamp(offset, 0, 2000);
        var query = (q ?? "").Trim();
        if (query.Length > 100) query = query[..100];
        string path;
        if (query.Length > 0)
            path = $"/public-search?q={Uri.EscapeDataString(query)}&limit_per_type={PageSize}&page={offset / PageSize + 1}";
        else
        {
            var c = Cats.FirstOrDefault(x => x.Id == (cat ?? "top"));
            if (c.Id is null) return new(null, "Нема такої вкладки", 400);
            path = c.Id == "soon"
                // хвіст «скоро» — від цієї хвилини: без end_date_min перші сторінки забиті торішнім, що досі не закрите
                ? $"/events?limit={PageSize}&offset={offset}&active=true&closed=false&order=endDate&ascending=true&end_date_min={Uri.EscapeDataString(_clock.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}"
                : $"/events?limit={PageSize}&offset={offset}&active=true&closed=false&order=volume24hr&ascending=false{(c.Tag is null ? "" : "&tag_slug=" + c.Tag)}";
        }
        var (code, body, error) = await Get(path, ct);
        if (error is not null) return new(null, error, 502);
        try
        {
            using var doc = JsonDocument.Parse(body!);
            JsonElement list;
            var more = false;
            if (doc.RootElement.ValueKind == JsonValueKind.Array) { list = doc.RootElement; more = list.GetArrayLength() >= PageSize; }
            else if (doc.RootElement.TryGetProperty("events", out var ev) && ev.ValueKind == JsonValueKind.Array)
            {
                list = ev;
                more = doc.RootElement.TryGetProperty("pagination", out var pg) && pg.TryGetProperty("hasMore", out var hm) && hm.ValueKind == JsonValueKind.True;
            }
            else return new(new PmFeed([], false));
            var cards = new List<PmCard>();
            foreach (var e in list.EnumerateArray())
            {
                // пошук віддає й закрите — у вітрину лише живе
                if (Bool(e, "closed") == true || Bool(e, "active") == false) continue;
                if (Card(e) is { } card) cards.Add(card);
            }
            return new(new PmFeed(cards, more));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            _log.LogWarning(ex, "Polymarket: не розібрав огляд {Path}", path);
            return new(null, Down, 502);
        }
    }

    /// <summary>Чернетка події за slug; <paramref name="market"/> — id ринку, коли їх кілька (інакше перший відкритий).</summary>
    public async Task<PmResult<PmDraft>> Event(string slug, string? market = null, CancellationToken ct = default)
    {
        if (!SlugOk(slug)) return new(null, NotFound, 404);
        var (code, body, error) = await Get("/events/slug/" + slug, ct, allowNotFound: true);
        if (error is not null) return new(null, error, 502);
        if (code == HttpStatusCode.NotFound) return new(null, NotFound, 404);
        try
        {
            using var doc = JsonDocument.Parse(body!);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object || Str(e, "title") is not { Length: > 0 } title) return new(null, NotFound, 404);
            var (options, markets, chosen) = Options(e, market);
            return new(new PmDraft(Str(e, "slug") ?? slug, title, Trim(Str(e, "description") ?? "", 2000), EventUrl(Str(e, "slug") ?? slug),
                Date(e, "endDate"), options, markets, chosen));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            _log.LogWarning(ex, "Polymarket: не розібрав подію {Slug}", slug);
            return new(null, Down, 502);
        }
    }

    async Task<(HttpStatusCode Code, string? Body, string? Error)> Get(string path, CancellationToken ct, bool allowNotFound = false)
    {
        var o = O;
        var url = (string.IsNullOrWhiteSpace(o.BaseUrl) ? "https://gamma-api.polymarket.com" : o.BaseUrl.TrimEnd('/')) + path;
        var now = _clock.UtcNow;
        if (_cache.TryGetValue(url, out var hit) && now - hit.At < TimeSpan.FromSeconds(Math.Max(0, o.CacheSeconds)))
            return (hit.Code, hit.Body, null);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 1, 60)));
            using var resp = await _http.GetAsync(url, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode && !(allowNotFound && resp.StatusCode == HttpStatusCode.NotFound))
            {
                _log.LogWarning("Polymarket: {Code} на {Url}", (int)resp.StatusCode, url);
                return (resp.StatusCode, null, Down);
            }
            if (_cache.Count > CacheMax) _cache.Clear();
            _cache[url] = (now, resp.StatusCode, body);
            return (resp.StatusCode, body, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            _log.LogWarning("Polymarket не відповів на {Url}: {Error}", url, ex.Message);
            return (0, null, Down);
        }
    }

    // ---------------------------------------------------------------- розбір

    PmCard? Card(JsonElement e)
    {
        if (Str(e, "slug") is not { Length: > 0 } slug || Str(e, "title") is not { Length: > 0 } title) return null;
        var (options, markets, _) = Options(e, null);
        // Подія «відкрита», а всі її ринки вже закриті (чи лишився один кандидат) — чернетка вийшла б порожня, ставити нема на що
        if (options.Count < 2) return null;
        var count = e.TryGetProperty("markets", out var ms) && ms.ValueKind == JsonValueKind.Array ? ms.GetArrayLength() : 0;
        return new PmCard(slug, title, EventUrl(slug), Date(e, "endDate"), Num(e, "volume") ?? 0, Num(e, "volume24hr") ?? 0, Str(e, "image"),
            count, options.Take(3).ToList());
    }

    /// <summary>
    /// Варіанти події:
    /// — один ринок (матч «A vs B», «так/ні») → варіанти = його outcomes;
    /// — багато ринків «Yes/No» (negRisk, «Золотий м'яч» — 89 кандидатів) → варіант = groupItemTitle з ціною «Yes»,
    ///   закриті й нульові пропускаються, від найімовірнішого;
    /// — кілька ринків зі своїми outcomes («… — More Markets») → обраний ринок (або перший відкритий), а список ринків
    ///   іде адміну, щоб він сам обрав.
    /// </summary>
    static (List<PmOption> Options, List<PmMarket> Markets, string? Chosen) Options(JsonElement e, string? market)
    {
        var all = new List<(string Id, string Question, string Group, List<PmOption> Outcomes, bool YesNo)>();
        if (e.TryGetProperty("markets", out var ms) && ms.ValueKind == JsonValueKind.Array)
            foreach (var m in ms.EnumerateArray())
            {
                if (Bool(m, "closed") == true || Bool(m, "active") == false) continue;
                var outcomes = Strings(m, "outcomes");
                var prices = Strings(m, "outcomePrices");
                if (outcomes.Count == 0 || outcomes.Count != prices.Count) continue;
                var list = outcomes.Select((t, i) => new PmOption(Ua(t), Price(prices[i]))).ToList();
                var yesNo = outcomes.Count == 2 && outcomes[0].Equals("Yes", StringComparison.OrdinalIgnoreCase) && outcomes[1].Equals("No", StringComparison.OrdinalIgnoreCase);
                all.Add((Str(m, "id") ?? all.Count.ToString(CultureInfo.InvariantCulture), Str(m, "question") ?? "", Str(m, "groupItemTitle") ?? "", list, yesNo));
            }
        if (all.Count == 0) return ([], [], null);
        if (all.Count == 1) return (all[0].Outcomes, [], null);
        if (all.All(m => m.YesNo))
            return (all.Select(m => new PmOption(m.Group.Length > 0 ? m.Group : m.Question, m.Outcomes[0].P))
                .Where(o => o.P > 0).OrderByDescending(o => o.P).ToList(), [], null);
        var markets = all.Select(m => new PmMarket(m.Id, m.Group.Length > 0 && m.Question.Length == 0 ? m.Group : m.Question, m.Outcomes)).ToList();
        var pick = all.FirstOrDefault(m => m.Id == market);
        if (pick.Id is null) pick = all[0];
        return (pick.Outcomes, markets, pick.Id);
    }

    /// <summary>«Так/Ні» замість «Yes/No» — решту (імена команд) лишаємо, як є.</summary>
    static string Ua(string outcome) => outcome.Trim() switch
    {
        var s when s.Equals("Yes", StringComparison.OrdinalIgnoreCase) => "Так",
        var s when s.Equals("No", StringComparison.OrdinalIgnoreCase) => "Ні",
        var s => s,
    };

    static double Price(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? Math.Clamp(Math.Round(p, 4), 0, 1) : 0;

    static string Trim(string s, int n) => s.Length <= n ? s : s[..n];

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null } : null;

    static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    static double? Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null;
    }

    static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.ToUniversalTime() : null;

    /// <summary>outcomes / outcomePrices — JSON-рядок масиву («["Yes","No"]»), а буває й справжній масив.</summary>
    static List<string> Strings(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return [];
        try
        {
            if (v.ValueKind == JsonValueKind.String)
            {
                using var inner = JsonDocument.Parse(v.GetString() ?? "[]");
                return Items(inner.RootElement);
            }
            return Items(v);
        }
        catch (JsonException) { return []; }

        static List<string> Items(JsonElement a) => a.ValueKind != JsonValueKind.Array ? [] :
            [.. a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.GetRawText())];
    }
}

public sealed record PmFeed(IReadOnlyList<PmCard> Items, bool More);
