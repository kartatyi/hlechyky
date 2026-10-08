using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hlechyky.Games;
using Microsoft.Extensions.Options;

namespace Hlechyky;

// =====================================================================================================================
// «Свій трек» — гімн переможця з власного уривка (docs/games/specs/anthem.md §3). Людина шле пісню чи відео з телефона
// (до 40 МБ) і каже, звідки й скільки грати (5–15 с); сервер одним викликом ffmpeg вирізає уривок, вирівнює гучність і
// кладе mp3 у data/anthems. Оригінал не зберігаємо — лише вирізане. Тип файла — за магічними байтами, далі ffprobe:
// чи є звук і скільки триває. Новий уривок — не частіше разу на 2 хвилини; адмін може зняти будь-який (черепки, як і
// з фото, не повертаються: оплачено вміння, а не файл).
//
// Друге джерело — пісня з пошуку радіо (той самий /api/search, що й для черги): сервер бере її тим самим yt-dlp у кеш
// радіо (TrackCache стежить за місцем), віддає для прослуховування цілою, і ріже той самий ffmpeg. Взяти й послухати
// може будь-який акаунт (до 10 скачувань на годину) — купувати «Свій трек» треба лише перед тим, як зберегти уривок.
// =====================================================================================================================

/// <summary>Тека з вирізаними гімнами — <c>data/anthems</c> (у тестах — тимчасова).</summary>
public sealed record AnthemDir(string Path);

/// <summary>Що ffprobe побачив у файлі: чи є звукова доріжка і скільки секунд він триває.</summary>
public sealed record AnthemProbe(bool HasAudio, double Seconds);

/// <summary>
/// ffprobe й ffmpeg для свого треку. Інтерфейс — щоб тести перевіряли правила без ffmpeg на машині.
/// </summary>
public interface IAnthemCutter
{
    /// <summary>Подивитись файл; null — ffprobe його не прочитав, не знає, скільки триває звук, або сам не запустився.</summary>
    Task<AnthemProbe?> ProbeAsync(string path, CancellationToken ct);

    /// <summary>
    /// Вирізати з <paramref name="src"/> уривок [<paramref name="start"/>; +<paramref name="len"/>] у mp3 <paramref name="dst"/>.
    /// Повертає null, коли вийшло, інакше — текст помилки для логів (людині його не показують).
    /// </summary>
    Task<string?> CutAsync(string src, string dst, double start, double len, CancellationToken ct);
}

/// <summary>Справжні ffprobe й ffmpeg — з тієї ж теки, що й для ефіру (<see cref="YtDlpOptions.FfmpegDir"/>), або з PATH.</summary>
public sealed class FfmpegAnthemCutter(IOptionsMonitor<YtDlpOptions> yt) : IAnthemCutter
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan CutTimeout = TimeSpan.FromSeconds(30);
    /// <summary>Скільки чекати, поки вбитий ffmpeg справді вийде (і відпустить файли).</summary>
    static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    string Tool(string name)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var path = Path.Combine(Paths.Resolve(yt.CurrentValue.FfmpegDir), exe);
        return File.Exists(path) ? path : exe;   // нема в теці — хай шукає в PATH (машина розробника)
    }

    /// <summary>
    /// Звідки ffprobe і ffmpeg можуть читати й якими демуксерами — лише ці два списки перед <c>-i</c>. Файл від людини
    /// (і пісня з кешу радіо) — чужі байти: без них плейлист HLS/concat у «пісні» змусив би ffmpeg ходити по інших файлах
    /// сервера чи в мережу, а нерідний демуксер — розбирати те, чого ми ніколи не чекали. Імена — як у <c>ffmpeg -demuxers</c>:
    /// збіг з будь-яким словом назви (<c>mov,mp4,m4a,…</c>, <c>matroska,webm</c>) уже пускає. Це те, що пропускає
    /// <see cref="ChatFileKind.Sniff"/> як звук чи відео, плюс сирий AAC (ADTS) і все, що качає yt-dlp (m4a, opus, webm).
    /// </summary>
    public const string Formats = "mp3,mov,mp4,m4a,3gp,3g2,mj2,matroska,webm,ogg,flac,wav,aac";
    static readonly string[] InputGuard = ["-protocol_whitelist", "file", "-format_whitelist", Formats];

    public async Task<AnthemProbe?> ProbeAsync(string path, CancellationToken ct)
    {
        // Перша звукова доріжка (є — є звук) і дві тривалості: самої доріжки й усього файла (JSON — бо обидві звуться duration)
        var (code, output, _) = await Run(Tool("ffprobe"), [
            "-v", "error", .. InputGuard, "-select_streams", "a:0", "-show_entries", "stream=codec_type,duration:format=duration",
            "-of", "json", path,
        ], ProbeTimeout, ct);
        return code == 0 ? ParseProbe(output) : null;
    }

    /// <summary>
    /// Відповідь ffprobe (<c>-of json</c>) → що в файлі. Тривалість — звукової доріжки, а коли її нема (WebM не пише) — усього
    /// файла. Контейнер без жодної тривалості (обидві N/A, ffprobe їх просто не пише) — null, «не прочитали»: «закороткий»
    /// збрехав би людині про файл, який може бути й на годину. Без звуку тривалість не важить — <c>(false, 0)</c>.
    /// </summary>
    public static AnthemProbe? ParseProbe(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            double? stream = null, format = null;
            var audio = false;
            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
                foreach (var st in streams.EnumerateArray())
                {
                    if (!st.TryGetProperty("codec_type", out var type) || type.GetString() != "audio") continue;
                    audio = true;
                    stream = Seconds(st);
                    break;
                }
            if (root.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.Object) format = Seconds(f);
            if (!audio) return new AnthemProbe(false, 0);
            return (stream ?? format) is { } s ? new AnthemProbe(true, s) : null;
        }
        catch (JsonException) { return null; }

        static double? Seconds(JsonElement e) =>
            e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.String
            && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) && v > 0
                ? v : null;
    }

    public async Task<string?> CutAsync(string src, string dst, double start, double len, CancellationToken ct)
    {
        // -ss перед -i — швидкий пошук, для звуку досить точний. Затухання — після loudnorm: однопрохідний loudnorm
        // інакше підтягнув би тихий початок назад. loudnorm усередині піднімає частоту до 192 кГц — повертаємо 44,1.
        var (code, _, err) = await Run(Tool("ffmpeg"), [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            .. InputGuard, "-ss", F(start), "-t", F(len), "-i", src,
            "-vn", "-sn", "-dn", "-map_metadata", "-1", "-ac", "2", "-ar", "44100", "-c:a", "libmp3lame", "-b:a", "128k",
            "-af", $"loudnorm=I=-16:TP=-1.5:LRA=11,afade=t=in:d=0.25,afade=t=out:st={F(Math.Max(0, len - 0.8))}:d=0.8,aresample=44100",
            "-f", "mp3", dst,
        ], CutTimeout, ct);
        return code == 0 ? null : $"ffmpeg {code}: {err.Trim()}";
    }

    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Процес із тайм-аутом: не вклався — убиваємо все дерево й кажемо «час вийшов» (код −1).</summary>
    static async Task<(int Code, string Out, string Err)> Run(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process p;
        try { p = Process.Start(psi) ?? throw new InvalidOperationException(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return (-2, "", $"{Path.GetFileName(exe)} не запустився"); }
        using (p)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var output = p.StandardOutput.ReadToEndAsync(cts.Token);
            var err = p.StandardError.ReadToEndAsync(cts.Token);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                // Kill лише просить. Поки процес не вийшов, Windows тримає його файли (.part, .tmp) — і finally нагорі не
                // зміг би їх стерти. Чекаємо коротко й уже без токена людини: вона пішла, а прибрати за собою треба.
                try { await p.WaitForExitAsync(CancellationToken.None).WaitAsync(KillWait, CancellationToken.None); }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { /* не вийшов — підбере Sweep */ }
                ct.ThrowIfCancellationRequested();   // людина пішла — це не «час вийшов»
                return (-1, "", "час вийшов");
            }
            return (p.ExitCode, await output, await err);
        }
    }
}

/// <summary>Пісня з кешу радіо: id, що про неї знає база (може бути порожньо), тривалість за базою і файл на диску.</summary>
public sealed record AnthemSourceTrack(string Id, string Title, string Artist, int DurationSec, string Path);

/// <summary>
/// Звідки «Свій трек» бере пісню з пошуку радіо. Інтерфейс — щоб тести не ходили в мережу й не кликали yt-dlp.
/// </summary>
public interface IAnthemSource
{
    /// <summary>Пісня, що вже лежить у кеші (під цим id чи та сама під іншим); null — треба качати.</summary>
    AnthemSourceTrack? Find(string id);

    /// <summary>Що це за пісня: база → YouTube Music → yt-dlp. null — такої нема.</summary>
    Task<TrackInfo?> InfoAsync(string id, CancellationToken ct);

    /// <summary>Скачати в кеш радіо (як для черги: куки на 18+, та сама пісня під іншим id не качається вдруге). Шлях до файла.</summary>
    Task<string> DownloadAsync(TrackInfo track, CancellationToken ct);
}

/// <summary>
/// Справжнє джерело — та сама машинерія, що й у черзі (<see cref="YtDlpService"/>): файл ляже в кеш радіо, і TrackCache
/// прибере його колись разом з іншими. Свіжий файл (дві години від останнього дотику) TrackCache не чіпає — тож
/// кожне <see cref="Find"/> оновлює позначку часу: поки людина слухає й ріже, пісня з-під неї не зникне.
/// </summary>
public sealed class YtAnthemSource(YtDlpService ytdlp, YtMusicClient ytm, Db db, ILogger<YtAnthemSource> log) : IAnthemSource
{
    public AnthemSourceTrack? Find(string id)
    {
        if (ytdlp.FindCached(id) is not { } path) return null;
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* не біда */ }
        var t = db.GetTrack(id);
        return new AnthemSourceTrack(id, t?.Title ?? "", t?.Artist ?? "", t?.DurationSec ?? 0, path);
    }

    public async Task<TrackInfo?> InfoAsync(string id, CancellationToken ct)
    {
        var t = db.GetTrack(id);
        if (t is null)
        {
            try
            {
                if (await ytm.LookupAsync(id, ct) is { DurationSec: > 0 } r) t = AutoDj.ToTrack(r);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug(ex, "YTM lookup failed for {Id}", id); }
            t ??= await ytdlp.FetchInfoAsync("https://music.youtube.com/watch?v=" + id, ct);
            db.UpsertTrack(t);   // до скачування, щоб файл було куди записати й щоб та сама пісня знайшлась під іншим id
        }
        return t;
    }

    public async Task<string> DownloadAsync(TrackInfo track, CancellationToken ct)
    {
        var path = await ytdlp.DownloadAsync(track, ct);
        db.SetTrackFile(track.Id, path);
        return path;
    }
}

/// <summary>Пісня з пошуку взята: id, назва й виконавець, точна тривалість (ffprobe) і звідки її слухати цілою.</summary>
public sealed record AnthemSourceReply(bool Ok, string Message, string? Id = null, string? Title = null, string? Artist = null,
    double? Duration = null, string? PreviewUrl = null);

/// <summary>Відповідь на дію зі своїм треком: чи вдалось, що сказати, уривок тепер, його назва й коли можна поставити новий.</summary>
public sealed record LavkaAnthemReply(bool Ok, string Message, string? Url = null, string? Title = null, DateTimeOffset? ReadyAt = null);

/// <summary>Правила свого треку: хто може поставити уривок, з чого, як часто; файли на диску; адмінський перегляд і «Зняти».</summary>
public sealed partial class LavkaAnthems(LavkaStore store, Lavka lavka, ILavkaWire wire, IClock clock, AnthemDir dir,
    IAnthemCutter cutter, IAnthemSource source, ILogger<LavkaAnthems> log)
{
    public const string UrlPrefix = "/api/lavka/anthem/";
    /// <summary>Ціла пісня з кешу радіо — для прослуховування перед нарізкою.</summary>
    public const string SourcePrefix = "/api/lavka/anthem/src/";
    /// <summary>Скільки пісень одна людина може взяти з пошуку за годину (рахуються лише ті, яких ще не було в кеші).</summary>
    public const int FetchesPerHour = 10;
    /// <summary>Скільки чекаємо на YouTube і yt-dlp за одну пісню.</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(3);
    /// <summary>Пісня чи коротке відео з телефона; довше — хай людина сама обріже, ніж ми тягнутимемо сотні мегабайт.</summary>
    public const long MaxBytes = 40L * 1024 * 1024;
    public const double MinLen = 5, MaxLen = 15, DefaultLen = 10;
    public const double MinSource = 3, MaxSource = 20 * 60;
    public const int MaxTitle = 40;
    const int HeadBytes = 64;

    public const string NotOwned = "Спершу купи «Свій трек»";
    public const string Empty = "Файл не дійшов — обери ще раз";
    public const string TooBig = "Завеликий файл — до 40 МБ";
    public const string NotSong = "Це не схоже на пісню чи відео";
    public const string Unreadable = "Не вдалось прочитати файл — спробуй інший";
    public const string NoSound = "У файлі нема звуку";
    public const string TooShort = "Закороткий файл — треба хоч 3 секунди";
    public const string TooLong = "Задовгий файл — до 20 хвилин";
    public const string CutFailed = "Не вийшло вирізати уривок — спробуй інше місце чи інший файл";
    public const string Busy = "Уривок уже ріжеться — ще мить";
    public const string Saved = "Гімн стоїть — зазвучить за столом, коли виграєш";
    public const string BadId = "Такої пісні не знаю — обери її з пошуку";
    public const string NoSuchSong = "Такої пісні не знайшлось — обери іншу";
    public const string TooLongSong = "Задовга пісня — до 20 хвилин";
    public const string SourceFailed = "Не вдалося взяти пісню — спробуй ще";
    public const string Fetching = "Пісня вже качається — ще мить";
    public const string Fetched = "Пісня є — обери уривок і послухай";

    /// <summary>Ім'я файла: хеш ніка + версія вмісту + звідки й скільки (мс). Нове на кожен уривок — кеш браузера не заважає.</summary>
    [GeneratedRegex("^[0-9a-f]{16}-[0-9a-f]{12}-[0-9]{1,9}-[0-9]{1,6}\\.mp3$")]
    private static partial Regex FileName();

    /// <summary>Id пісні з пошуку — відео YouTube: рівно 11 знаків [A-Za-z0-9_-]. Ні крапок, ні скісних — жодних шляхів.</summary>
    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex TrackId();

    public static bool IsTrackId(string? id) => id is not null && TrackId().IsMatch(id);

    readonly object _gate = new();
    /// <summary>Хто зараз ріже: другий запит тієї самої людини не має запускати другий ffmpeg поруч.</summary>
    readonly ConcurrentDictionary<string, byte> _cutting = new(StringComparer.Ordinal);
    /// <summary>Хто зараз качає пісню з пошуку: одна за раз на людину.</summary>
    readonly ConcurrentDictionary<string, byte> _fetching = new(StringComparer.Ordinal);
    /// <summary>Скачування з пошуку за останню годину, ключ — ключ ніка (як <c>_hour</c> у ChatFiles).</summary>
    readonly Dictionary<string, List<DateTimeOffset>> _hour = new(StringComparer.Ordinal);

    static string Hex(ReadOnlySpan<byte> data, int chars) => Convert.ToHexString(SHA256.HashData(data))[..chars].ToLowerInvariant();

    /// <summary>
    /// Поставити уривок: <paramref name="body"/> — сирий файл (не довше за <see cref="MaxBytes"/>), <paramref name="start"/> і
    /// <paramref name="len"/> — секунди, як їх обрала людина (обрізаються до можливого), <paramref name="title"/> — назва
    /// за столом (порожня — «Свій трек»). Перший після купівлі й після адмінського «Зняти» — одразу, далі — раз на 2 хв.
    /// </summary>
    public async Task<LavkaAnthemReply> SetAsync(string nick, bool account, double? start, double? len, string? title,
        long? declaredLength, Stream body, CancellationToken ct)
    {
        if (!account) return new(false, Lavka.NotAccount);
        if (Refused(nick) is { } no) return no;
        if (declaredLength > MaxBytes) return new(false, TooBig);
        if (declaredLength == 0) return new(false, Empty);

        var key = Auth.NickKey(nick);
        if (!_cutting.TryAdd(key, 0)) return new(false, Busy);
        string? part = null;
        try
        {
            Sweep();
            Directory.CreateDirectory(dir.Path);
            part = Path.Combine(dir.Path, $".up-{Guid.NewGuid():N}.part");
            long size = 0;
            var head = new byte[HeadBytes];
            var headLen = 0;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var f = File.Create(part))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await body.ReadAsync(buf, ct)) > 0)
                {
                    size += n;
                    if (size > MaxBytes) return new(false, TooBig);
                    if (headLen < HeadBytes)
                    {
                        var take = Math.Min(n, HeadBytes - headLen);
                        Array.Copy(buf, 0, head, headLen, take);
                        headLen += take;
                    }
                    sha.AppendData(buf, 0, n);
                    await f.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
            if (size == 0) return new(false, Empty);
            if (ChatFileKind.Sniff(head.AsSpan(0, headLen)).Type is not (ChatFileKind.Audio or ChatFileKind.Video)) return new(false, NotSong);

            if (await cutter.ProbeAsync(part, ct) is not { } probe) return new(false, Unreadable);
            if (!probe.HasAudio) return new(false, NoSound);
            if (probe.Seconds < MinSource) return new(false, TooShort);
            if (probe.Seconds > MaxSource) return new(false, TooLong);

            return await SaveCutAsync(nick, key, part, Convert.ToHexString(sha.GetHashAndReset())[..12].ToLowerInvariant(),
                probe.Seconds, start, len, CleanTitle(title), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "свій трек {Nick} не ліг на диск", nick);
            return new(false, "Уривок не зберігся — спробуй ще раз трохи згодом");
        }
        finally
        {
            _cutting.TryRemove(key, out _);
            if (part is not null) TryDelete(part);
        }
    }

    /// <summary>
    /// Спільний хвіст файла й пісні з пошуку: обрізати start/len, вирізати ffmpeg-ом у тимчасовий файл, перейменувати,
    /// записати рядок і перерву. <paramref name="version"/> — 12 hex-знаків, що розрізняють джерело в імені файла.
    /// IO-помилки летять до того, хто кликав (у кожного своя відповідь).
    /// </summary>
    async Task<LavkaAnthemReply> SaveCutAsync(string nick, string key, string src, string version, double seconds,
        double? start, double? len, string? name, CancellationToken ct)
    {
        var (from, length) = Clamp(start, len, seconds);
        var startMs = (int)Math.Round(from * 1000);
        var lenMs = (int)Math.Round(length * 1000);
        var file = $"{Hex(Encoding.UTF8.GetBytes(key), 16)}-{version}-{startMs}-{lenMs}.mp3";
        var path = Path.Combine(dir.Path, file);
        string? tmp = null;
        try
        {
            // Той самий уривок того самого файла вдруге — той самий mp3: не ріжемо й не переписуємо, його саме можуть грати.
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(dir.Path);
                tmp = path + ".tmp";
                if (await cutter.CutAsync(src, tmp, startMs / 1000.0, lenMs / 1000.0, ct) is { } err)
                {
                    log.LogWarning("свій трек {Nick} не вирізався: {Err}", nick, err);
                    // пісню з кешу радіо могли саме прибрати — тоді винна не вона, а ми: хай людина просто спробує ще
                    return new(false, File.Exists(src) ? CutFailed : SourceFailed);
                }
                if (!File.Exists(tmp) || new FileInfo(tmp).Length == 0)
                {
                    log.LogWarning("свій трек {Nick}: ffmpeg нічого не записав", nick);
                    return new(false, File.Exists(src) ? CutFailed : SourceFailed);
                }
                File.Move(tmp, path, overwrite: true);
                tmp = null;
            }

            var now = clock.UtcNow;
            string? old;
            lock (_gate)
            {
                // ffmpeg працює без замка, тож правила — ще раз тут, поруч із записом
                if (Refused(nick) is { } late) return late;
                old = store.SetAnthem(nick, file, name, startMs, lenMs, (int)new FileInfo(path).Length, now);
                store.UsePerk(nick, LavkaCatalog.OwnAnthem, now);
            }
            if (old is not null && old != file) Delete(old);
            return new(true, Saved, UrlPrefix + file, name, lavka.ReadyAt(nick, LavkaCatalog.OwnAnthem));
        }
        finally
        {
            if (tmp is not null) TryDelete(tmp);
        }
    }

    // ---------- пісня з пошуку радіо ----------

    /// <summary>
    /// Взяти пісню з пошуку радіо, щоб послухати й обрати уривок. Лише акаунт; купувати «Свій трек» для цього не треба.
    /// Уже є в кеші — одразу; інакше — через yt-dlp у кеш радіо, не більше <see cref="FetchesPerHour"/> на годину й не
    /// довше <see cref="FetchTimeout"/>. Тривалість — від ffprobe, щоб повзунки збігались із тим, що ріже ffmpeg.
    /// </summary>
    public async Task<AnthemSourceReply> FetchAsync(string nick, bool account, string? trackId, CancellationToken ct)
    {
        if (!account) return new(false, Lavka.NotAccount);
        if (!IsTrackId(trackId)) return new(false, BadId);
        var id = trackId!;
        var key = Auth.NickKey(nick);
        if (!_fetching.TryAdd(key, 0)) return new(false, Fetching);
        try
        {
            var found = source.Find(id);
            if (found is null)
            {
                if (Quota(key) is { } wait) return new(false, wait);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(FetchTimeout);
                try
                {
                    if (await source.InfoAsync(id, cts.Token) is not { } info) return new(false, NoSuchSong);
                    if (info.DurationSec > MaxSource) return new(false, TooLongSong);
                    var path = await source.DownloadAsync(info, cts.Token);
                    found = new AnthemSourceTrack(id, info.Title, info.Artist, info.DurationSec, path);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }   // людина пішла
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    log.LogWarning("свій трек {Nick}: пісня {Id} не взялась: {Err}", nick, id, ex.Message);
                    return new(false, SourceFailed);
                }
            }
            if (!File.Exists(found.Path) || await cutter.ProbeAsync(found.Path, ct) is not { } probe) return new(false, SourceFailed);
            if (!probe.HasAudio) return new(false, NoSound);
            if (probe.Seconds < MinSource) return new(false, TooShort);
            if (probe.Seconds > MaxSource) return new(false, TooLongSong);
            return new(true, Fetched, id, found.Title, found.Artist, Math.Round(probe.Seconds, 3), SourcePrefix + id);
        }
        finally { _fetching.TryRemove(key, out _); }
    }

    /// <summary>Чи можна ще качати цієї години; null — можна (і спроба вже записана), інакше — коли знову.</summary>
    string? Quota(string key)
    {
        var now = clock.UtcNow;
        lock (_hour)
        {
            if (!_hour.TryGetValue(key, out var list)) _hour[key] = list = [];
            list.RemoveAll(at => now - at >= TimeSpan.FromHours(1));
            if (list.Count >= FetchesPerHour)
            {
                var mins = Math.Max(1, (int)Math.Ceiling((list.Min() + TimeSpan.FromHours(1) - now).TotalMinutes));
                return $"За годину можна взяти {FetchesPerHour} пісень — наступну за {mins} хв (або обери файл з телефона)";
            }
            list.Add(now);
            return null;
        }
    }

    /// <summary>Ціла пісня для прослуховування: шлях у кеші за id з адреси; null — id не наш чи файла нема.</summary>
    public string? SourcePath(string? trackId) =>
        IsTrackId(trackId) && source.Find(trackId!) is { } t && File.Exists(t.Path) ? t.Path : null;

    /// <summary>
    /// Вирізати уривок із пісні, взятої з пошуку (<see cref="FetchAsync"/>): ті самі правила, що й для файла — лише
    /// акаунт із купленим «Своїм треком», раз на 2 хвилини, 5–15 с. Назва порожня — «Виконавець — Назва».
    /// </summary>
    public async Task<LavkaAnthemReply> CutTrackAsync(string nick, bool account, string? trackId, double? start, double? len,
        string? title, CancellationToken ct)
    {
        if (!account) return new(false, Lavka.NotAccount);
        if (Refused(nick) is { } no) return no;
        if (!IsTrackId(trackId)) return new(false, BadId);
        var id = trackId!;
        var key = Auth.NickKey(nick);
        if (!_cutting.TryAdd(key, 0)) return new(false, Busy);
        try
        {
            Sweep();
            if (source.Find(id) is not { } song || !File.Exists(song.Path)) return new(false, SourceFailed);
            if (await cutter.ProbeAsync(song.Path, ct) is not { } probe) return new(false, SourceFailed);
            if (!probe.HasAudio) return new(false, NoSound);
            if (probe.Seconds < MinSource) return new(false, TooShort);
            if (probe.Seconds > MaxSource) return new(false, TooLongSong);
            var version = Hex(Encoding.UTF8.GetBytes("yt:" + id), 12);
            return await SaveCutAsync(nick, key, song.Path, version, probe.Seconds, start, len,
                CleanTitle(title) ?? DefaultTitle(song.Artist, song.Title), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // файл пісні зник посеред нарізки (TrackCache) чи диск заартачився
            log.LogWarning(ex, "свій трек {Nick} з пісні {Id} не вирізався", nick, id);
            return new(false, SourceFailed);
        }
        finally { _cutting.TryRemove(key, out _); }
    }

    /// <summary>Назва за столом для пісні з пошуку: «Виконавець — Назва» (без виконавця — лише назва), за правилами <see cref="CleanTitle"/>.</summary>
    public static string? DefaultTitle(string? artist, string? title)
    {
        var a = (artist ?? "").Trim();
        var t = (title ?? "").Trim();
        return CleanTitle(a.Length == 0 ? t : t.Length == 0 ? a : $"{a} — {t}");
    }

    /// <summary>Чому зараз не можна поставити уривок (не куплено, ще перерва); null — можна.</summary>
    LavkaAnthemReply? Refused(string nick)
    {
        lock (_gate)
        {
            if (!store.Owns(nick, LavkaCatalog.OwnAnthem)) return new(false, NotOwned);
            if (lavka.ReadyAt(nick, LavkaCatalog.OwnAnthem) is { } ready)
            {
                var row = store.Anthem(nick);
                return new(false, $"Новий уривок можна буде за {Math.Max(1, (int)Math.Ceiling((ready - clock.UtcNow).TotalMinutes))} хв",
                    row?.Url, row?.Title, ready);
            }
            return null;
        }
    }

    /// <summary>
    /// Звідки й скільки: довжина — 5…15 с (типово 10), але не довша за сам файл (файл на 3–5 с грає весь); початок — 0…кінець−довжина.
    /// Неможливі числа (NaN, нескінченність) — як не надіслані.
    /// </summary>
    public static (double Start, double Len) Clamp(double? start, double? len, double duration)
    {
        var l = len is { } x && double.IsFinite(x) ? Math.Clamp(x, MinLen, MaxLen) : DefaultLen;
        l = Math.Min(l, duration);
        var s = start is { } y && double.IsFinite(y) ? y : 0;
        s = Math.Clamp(s, 0, Math.Max(0, duration - l));
        return (s, l);
    }

    /// <summary>Назва за столом: без керівних символів і країв, до <see cref="MaxTitle"/> знаків; порожня — null.</summary>
    public static string? CleanTitle(string? raw)
    {
        var t = new string((raw ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (t.Length > MaxTitle) t = t[..(char.IsHighSurrogate(t[MaxTitle - 1]) ? MaxTitle - 1 : MaxTitle)].TrimEnd();
        return t.Length == 0 ? null : t;
    }

    /// <summary>Усі поставлені уривки — для адміна, свіжі згори.</summary>
    public List<LavkaAnthemRow> All() => store.AllAnthems();

    /// <summary>
    /// Адмін знімає уривок: файл і рядок — геть, «Свій трек» лишається за людиною, і новий уривок можна ставити одразу
    /// (перерва скинута). Поки нового нема — перемога без гімну.
    /// </summary>
    public LavkaReply TakeDown(string? nick)
    {
        var name = Auth.CleanNick((nick ?? "").Trim().TrimStart('@'));
        if (name.Length == 0) return new(false, "Чий гімн зняти?");
        LavkaAnthemRow? row;
        lock (_gate)
        {
            row = store.Anthem(name);
            if (row is null)
            {
                var of = NickCases.Genitive(name);
                return new(false, $"{NickCases.AtStart(of)} {of} свого гімну нема");
            }
            store.DropAnthem(row.Nick);
            store.ForgetPerk(row.Nick, LavkaCatalog.OwnAnthem);
        }
        Delete(row.File);
        try { wire.Toast(row.Nick, "🎤 Адмін зняв твій гімн — постав інший уривок"); }
        catch (Exception ex) { log.LogWarning(ex, "не сказали {Nick}, що гімн знято", row.Nick); }
        return new(true, $"Гімн {NickCases.Genitive(row.Nick)} знято");
    }

    /// <summary>Шлях до вирізаного mp3 за іменем з адреси; null — ім'я не наше (жодного «..», жодних чужих файлів) або файла нема.</summary>
    public string? Resolve(string? file)
    {
        if (string.IsNullOrEmpty(file) || !FileName().IsMatch(file)) return null;
        var path = Path.Combine(dir.Path, file);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Скільки лежить недописане (<c>.up-*.part</c>, <c>*.tmp</c>), поки його не вважають покинутим.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// Прибрати покинуте: прийом файла чи нарізку обірвав перезапуск сервера, вимкнене світло або ffmpeg, якого Windows
    /// не відпустила вчасно, — і finally свого не стер. Як <see cref="ChatFiles.Evict"/>: перед кожним новим уривком, лише
    /// старше за годину (живий прийом і нарізка тривають хвилини, тож чужого недописаного не зачепимо). Вирізані mp3 не чіпає.
    /// </summary>
    public void Sweep()
    {
        try
        {
            if (!Directory.Exists(dir.Path)) return;
            var before = (clock.UtcNow - StaleAfter).UtcDateTime;
            foreach (var f in new DirectoryInfo(dir.Path).EnumerateFiles())
            {
                var stale = (f.Name.StartsWith(".up-", StringComparison.Ordinal) && f.Name.EndsWith(".part", StringComparison.Ordinal))
                            || f.Name.EndsWith(".tmp", StringComparison.Ordinal);
                if (stale && f.LastWriteTimeUtc < before) TryDelete(f.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "тека гімнів не прибралась");
        }
    }

    void Delete(string file)
    {
        if (FileName().IsMatch(file)) TryDelete(Path.Combine(dir.Path, file));
    }

    /// <summary>Файл, який саме хтось тягне, Windows не дасть стерти — хай полежить, головне — не впасти.</summary>
    void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "файл гімну {File} не прибрався з диска", Path.GetFileName(path));
        }
    }
}
