using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Фон «Здам хату»: набирає світ з Inside Airbnb, коли розробник попросить (<see cref="Start"/>), і потроху перевіряє,
/// чи фото сторонніх оголошень ще живі. Самих фото ми не зберігаємо — гравець бачить їх із сайту-джерела, тож мертве фото
/// означає мертву загадку: таке оголошення тихо випадає з банку. Перевірка неспішна (коло по всьому банку — за тиждень),
/// а коли браузер гравця каже «фото не відкрилось» (<see cref="Suspect"/>) — те оголошення перевіряємо позачергово.
/// </summary>
public sealed class KhataImport : BackgroundService
{
    /// <summary>Стан імпорту — для рядка поступу в розробника.</summary>
    public sealed record Progress(bool Running, int Done, int Total, int Added, int Updated, string Message, DateTimeOffset? FinishedAt);

    /// <summary>За скільки обходимо весь банк перевіркою фото.</summary>
    public static readonly TimeSpan Sweep = TimeSpan.FromDays(7);
    static readonly TimeSpan MinGap = TimeSpan.FromSeconds(30), MaxGap = TimeSpan.FromHours(1);
    static readonly TimeSpan SuspectGap = TimeSpan.FromSeconds(3);
    /// <summary>Пауза між містами: файли великі, а сервер набору — не наш.</summary>
    public static TimeSpan CityGap { get; set; } = TimeSpan.FromSeconds(1);
    public const int MaxSuspects = 50;
    /// <summary>Скільки разів пробуємо файл одного міста.</summary>
    public const int Attempts = 2;

    readonly KhataStore _store;
    readonly HttpClient _http;
    readonly IClock _clock;
    readonly ILogger? _log;
    readonly SemaphoreSlim _wake = new(0);
    readonly ConcurrentQueue<long> _suspects = new();
    volatile Progress _state = new(false, 0, 0, 0, 0, "", null);
    int _wanted;

    /// <summary>Стан набору України з DIM.RIA — для рядка в розробника.</summary>
    public sealed record RiaProgress(bool HasKey, int Added, string Message, DateTimeOffset? At);

    readonly Func<KhataOptions> _options;
    readonly Queue<(KhataRia.City City, long Id)> _riaQueue = new();
    /// <summary>Оголошення, які вже питали й вони не згодились (чуже місто, без фото): вдруге запит на них не витрачаємо.</summary>
    readonly HashSet<long> _riaSkip = [];
    int _riaCity = -1;
    int _riaAdded;
    Dictionary<string, double>? _uah;
    DateTimeOffset _uahAt;
    volatile RiaProgress _ria = new(false, 0, "", null);

    public KhataImport(KhataStore store, HttpClient http, IClock clock, ILogger? log = null, Func<KhataOptions>? options = null)
    {
        _store = store;
        _http = http;
        _clock = clock;
        _log = log;
        _options = options ?? (() => new KhataOptions());
    }

    public Progress State => _state;
    public RiaProgress Ria => _ria;

    /// <summary>Розробник просить набрати (чи донабрати) світ. false — імпорт уже йде.</summary>
    public bool Start()
    {
        if (_state.Running || Interlocked.Exchange(ref _wanted, 1) == 1) return false;
        _state = new(true, 0, KhataAirbnb.Cities.Count, 0, 0, "Починаю…", null);
        _wake.Release();
        return true;
    }

    /// <summary>У когось не відкрилось фото з цією адресою — перевіримо те оголошення позачергово.</summary>
    public bool Suspect(string? photoUrl)
    {
        if (_store.ByPhoto(photoUrl) is not { } flat || _suspects.Count >= MaxSuspects || _suspects.Contains(flat.Id)) return false;
        _suspects.Enqueue(flat.Id);
        _wake.Release();
        return true;
    }

    protected override Task ExecuteAsync(CancellationToken ct) => Task.WhenAll(MainLoop(ct), RiaLoop(ct));

    // ---------------------------------------------------------------------------------------
    // Україна: DIM.RIA — потроху, у межах пакета запитів
    // ---------------------------------------------------------------------------------------

    async Task RiaLoop(CancellationToken ct)
    {
        var pause = TimeSpan.FromSeconds(30);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(pause, ct); }
            catch (OperationCanceledException) { break; }
            var o = _options();
            if (string.IsNullOrWhiteSpace(o.RiaKey))
            {
                _ria = new(false, _riaAdded, "Ключа DIM.RIA нема — українська оренда не набирається", null);
                pause = TimeSpan.FromMinutes(2);
                continue;
            }
            try { pause = await RiaStep(o, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "khata: крок DIM.RIA спіткнувся");
                RiaReport("Збій зв'язку з DIM.RIA — спробую пізніше");
                pause = TimeSpan.FromMinutes(10);
            }
        }
    }

    void RiaReport(string message) => _ria = new(true, _riaAdded, message, _clock.UtcNow);

    /// <summary>Курс НБУ (гривень за долар і євро) — на випадок оголошення з ціною у валюті. Перечитуємо раз на пів доби.</summary>
    async Task<Dictionary<string, double>> Rates(CancellationToken ct)
    {
        if (_uah is not null && _clock.UtcNow - _uahAt < TimeSpan.FromHours(12)) return _uah;
        try
        {
            _uah = KhataAirbnb.UahPer(await _http.GetStringAsync(KhataAirbnb.RatesUrl, ct));
            _uahAt = _clock.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _uah ??= [];
        }
        return _uah;
    }

    /// <summary>
    /// Один запит до DIM.RIA: коли черга порожня — пошук по наступному місту (сотня номерів, беремо незнайомі), інакше —
    /// одне оголошення з черги. Повертає, скільки чекати до наступного запиту: пакет дає обмаль запитів на годину, тож
    /// база росте повільно, але сама. Ліміт вичерпано чи ключ не приймають — довга пауза, а не стукіт у зачинені двері.
    /// </summary>
    public async Task<TimeSpan> RiaStep(KhataOptions o, CancellationToken ct)
    {
        var gap = TimeSpan.FromSeconds(3600.0 / Math.Clamp(o.RiaPerHour, 1, 3000));
        var key = o.RiaKey.Trim();
        if (_riaQueue.Count == 0)
        {
            var at = (_riaCity + 1) % KhataRia.Cities.Count;
            var city = KhataRia.Cities[at];
            using var resp = await _http.GetAsync(KhataRia.SearchUrl(city, key), ct);
            if (RiaRefused(resp) is { } wait) return wait;
            _riaCity = at;
            if (!resp.IsSuccessStatusCode)
            {
                RiaReport($"{city.Name}: пошук відповів {(int)resp.StatusCode}");
                return gap;
            }
            var ids = KhataRia.Ids(await resp.Content.ReadAsStringAsync(ct));
            var have = _store.Live.Count(f => f.Source == KhataSources.Ria && f.City == city.Name);
            var want = have >= KhataRia.PerCity ? KhataRia.Refresh : KhataRia.PerCity - have;
            var fresh = ids.Where(id => !_riaSkip.Contains(id) && !_store.Knows(KhataSources.Ria, id.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();
            for (var i = fresh.Count - 1; i > 0; i--)
            {
                var j = Random.Shared.Next(i + 1);
                (fresh[i], fresh[j]) = (fresh[j], fresh[i]);
            }
            foreach (var id in fresh.Take(want)) _riaQueue.Enqueue((city, id));
            RiaReport($"{city.Name}: оголошень знайдено {ids.Count}, нових беру {Math.Min(want, fresh.Count)}");
            return gap;
        }

        var (c, flatId) = _riaQueue.Peek();
        using var info = await _http.GetAsync(KhataRia.InfoUrl(flatId, key), ct);
        if (RiaRefused(info) is { } hold) return hold;
        _riaQueue.Dequeue();
        var flat = info.IsSuccessStatusCode ? KhataRia.Flat(await info.Content.ReadAsStringAsync(ct), c, await Rates(ct)) : null;
        if (flat is null) _riaSkip.Add(flatId);
        else
        {
            var (added, _, _) = _store.Import(KhataSources.Ria, [flat]);
            _riaAdded += added;
        }
        RiaReport($"{c.Name}: {(flat is null ? "оголошення не згодилось" : "додано квартиру")}; у черзі ще {_riaQueue.Count}");
        return gap;
    }

    /// <summary>DIM.RIA відмовила (ліміт запитів, пакет скінчився, ключ не той) — скільки чекати; null — не відмова.</summary>
    TimeSpan? RiaRefused(HttpResponseMessage resp)
    {
        switch (resp.StatusCode)
        {
            case HttpStatusCode.TooManyRequests:
                RiaReport("Ліміт запитів DIM.RIA на цю годину вичерпано — продовжу за годину");
                return TimeSpan.FromHours(1);
            case HttpStatusCode.Unauthorized:
                RiaReport("DIM.RIA не приймає ключ — перевір Khata:RiaKey");
                return TimeSpan.FromHours(1);
            case HttpStatusCode.PaymentRequired or HttpStatusCode.Forbidden:
                RiaReport("Пакет запитів DIM.RIA скінчився або ключ вимкнено — спробую за шість годин");
                return TimeSpan.FromHours(6);
            default:
                return null;
        }
    }

    // ---------------------------------------------------------------------------------------
    // світ: Inside Airbnb на прохання розробника; перевірка «чи фото ще живе»
    // ---------------------------------------------------------------------------------------

    async Task MainLoop(CancellationToken ct)
    {
        // Перше коло перевірки — не на старті сервера: там і без нас є що робити.
        var pause = TimeSpan.FromMinutes(2);
        while (!ct.IsCancellationRequested)
        {
            try { await _wake.WaitAsync(pause, ct); }
            catch (OperationCanceledException) { break; }
            try
            {
                if (Interlocked.Exchange(ref _wanted, 0) == 1) await RunAirbnb(ct);
                else if (_suspects.TryDequeue(out var id))
                {
                    if (_store.Live.FirstOrDefault(f => f.Id == id) is { } flat) await Check(flat, ct);
                }
                else if (_store.NextToCheck() is { } next) await Check(next, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log?.LogWarning(ex, "khata: фон спіткнувся"); }

            var external = _store.Live.Count(f => f.External);
            pause = !_suspects.IsEmpty ? SuspectGap
                : external == 0 ? MaxGap
                : TimeSpan.FromTicks(Math.Clamp(Sweep.Ticks / external, MinGap.Ticks, MaxGap.Ticks));
        }
    }

    /// <summary>Чи фото оголошення ще на місці. 404/410 — оголошення знімаємо; будь-що інше (мережа, 5xx) — лишаємо до наступного кола.</summary>
    public async Task<bool> Check(KhataFlat flat, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var resp = await _http.GetAsync(flat.Thumb, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (resp.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Gone)) return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return true;
        }
        if (_store.Drop(flat.Id)) _log?.LogInformation("khata: фото оголошення {Source}:{Ext} зникло — знято з гри", flat.Source, flat.ExtId);
        return false;
    }

    void Report(bool running, int done, int added, int updated, string message) =>
        _state = new(running, done, KhataAirbnb.Cities.Count, added, updated, message, running ? null : _clock.UtcNow);

    /// <summary>
    /// Набрати світ: курс НБУ, сторінка з адресами файлів, далі місто за містом — файл читаємо потоком (на диск не кладемо),
    /// відбираємо <see cref="KhataAirbnb.PerCity"/> оголошень і віддаємо банку. Кожен запуск бере іншу випадкову вибірку,
    /// тож повторний запуск базу не міняє місцями, а нарощує. Місто, яке не далося, пропускаємо й ідемо далі.
    /// </summary>
    public async Task RunAirbnb(CancellationToken ct)
    {
        int done = 0, added = 0, updated = 0, failed = 0;
        Report(true, 0, 0, 0, "Беру курс валют і список файлів…");
        Dictionary<string, double> usd;
        Dictionary<string, string> files;
        try
        {
            usd = KhataAirbnb.UsdPer(await _http.GetStringAsync(KhataAirbnb.RatesUrl, ct));
            files = KhataAirbnb.Latest(await _http.GetStringAsync(KhataAirbnb.IndexUrl, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log?.LogWarning(ex, "khata: імпорт світу не почався");
            Report(false, 0, 0, 0, "Не вийшло: курс валют або список файлів не відкрились. Спробуй пізніше");
            return;
        }
        if (usd.Count == 0 || files.Count == 0)
        {
            Report(false, 0, 0, 0, "Не вийшло: у курсі валют або списку файлів порожньо");
            return;
        }

        var rng = new Random();
        foreach (var city in KhataAirbnb.Cities)
        {
            ct.ThrowIfCancellationRequested();
            Report(true, done, added, updated, city.Name + "…");
            if (!files.TryGetValue(city.Slug, out var url) || !usd.TryGetValue(city.Currency, out var rate)) failed++;
            else
            {
                // Зв'язок із сервером набору часом рветься посеред файлу — одна повторна спроба рятує місто.
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                        resp.EnsureSuccessStatusCode();
                        await using var raw = await resp.Content.ReadAsStreamAsync(ct);
                        await using var gz = new GZipStream(raw, CompressionMode.Decompress);
                        using var reader = new StreamReader(gz, Encoding.UTF8);
                        // розбір сотень мегабайтів тексту — не на потоці, що обслуговує запити
                        var picked = await Task.Run(() => KhataAirbnb.Pick(KhataAirbnb.Rows(reader), city, rate, KhataAirbnb.PerCity, rng), ct);
                        var (a, u, _) = _store.Import(KhataSources.Airbnb, picked);
                        added += a;
                        updated += u;
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        if (attempt < Attempts)
                        {
                            if (CityGap > TimeSpan.Zero) await Task.Delay(CityGap * 3, ct);
                            continue;
                        }
                        failed++;
                        _log?.LogWarning(ex, "khata: місто {City} не далося", city.Slug);
                        break;
                    }
                }
                if (CityGap > TimeSpan.Zero) await Task.Delay(CityGap, ct);
            }
            done++;
        }
        var tail = failed > 0 ? $"; міст не далося: {failed}" : "";
        Report(false, done, added, updated, $"Готово: нових оголошень — {added}, оновлено — {updated}{tail}");
        _log?.LogInformation("khata: імпорт світу — додано {Added}, оновлено {Updated}, не далося міст {Failed}", added, updated, failed);
    }
}
