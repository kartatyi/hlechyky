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

    public KhataImport(KhataStore store, HttpClient http, IClock clock, ILogger? log = null)
    {
        _store = store;
        _http = http;
        _clock = clock;
        _log = log;
    }

    public Progress State => _state;

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

    protected override async Task ExecuteAsync(CancellationToken ct)
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
