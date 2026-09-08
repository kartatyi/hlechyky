using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>
/// Голосові в ефір. Браузер шле шматок webm/ogg/mp4 з мікрофона, ffmpeg переганяє його в mp3
/// просто в кеш — там же, де лежать скачані треки, тож liquidsoap бачить файл без окремого
/// монтування, а далі запис іде чергою як звичайний трек. Заразом ffmpeg підтягує гучність до
/// музики: без цього голос в ефірі ледь чути. Id виду "voice-xxxxxxxxxxxx", по ньому і сервер,
/// і фронт упізнають запис.
/// </summary>
public sealed class VoiceService(IOptionsMonitor<YtDlpOptions> yt, IOptionsMonitor<VoiceOptions> options, ILogger<VoiceService> log)
{
    public const string Prefix = "voice-";

    VoiceOptions O => options.CurrentValue;
    string CacheDir => Paths.Resolve(yt.CurrentValue.CacheDir);
    string Tool(string name) => Path.Combine(Paths.Resolve(yt.CurrentValue.FfmpegDir), OperatingSystem.IsWindows() ? name + ".exe" : name);

    public bool Enabled => O.Enabled;
    public int MaxSeconds => Math.Clamp(O.MaxSeconds, 5, 900);
    public long MaxUploadBytes => Math.Max(64 * 1024, O.MaxUploadBytes);

    public static bool IsVoice(string? trackId) => trackId is not null && trackId.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Файл готового голосового; null, коли id не наш або запис не дожив.</summary>
    public string? FilePath(string id)
    {
        if (!IsVoice(id) || id.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-')) return null;
        var path = Path.Combine(CacheDir, id + ".mp3");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Приймає сирий запис із браузера і повертає готовий до черги трек із файлом у кеші.</summary>
    public async Task<(TrackInfo Track, string FilePath)> SaveAsync(Stream body, string nick, CancellationToken ct)
    {
        Directory.CreateDirectory(CacheDir);
        var id = Prefix + Guid.NewGuid().ToString("N")[..12];
        var raw = Path.Combine(CacheDir, id + ".part");   // .part кеш пропускає, тож недописаний файл ніколи не зійде за трек
        var mp3 = Path.Combine(CacheDir, id + ".mp3");
        try
        {
            var size = await ReceiveAsync(body, raw, ct);
            if (size < 1024) throw new InvalidOperationException("запис порожній");

            // -t на секунду більше ліміту: якщо браузер прислав довше, ріжемо тут, а не сваримось
            var lufs = O.LoudnessLufs.ToString(CultureInfo.InvariantCulture);
            var (code, err) = await RunAsync(Tool("ffmpeg"), [
                "-hide_banner", "-loglevel", "error", "-y",
                "-i", raw,
                "-vn", "-ac", "1", "-ar", "44100", "-b:a", "96k",
                "-af", $"loudnorm=I={lufs}:TP=-1.5:LRA=11",
                "-t", (MaxSeconds + 1).ToString(),
                mp3,
            ], TimeSpan.FromSeconds(60), ct);
            if (code != 0 || !File.Exists(mp3))
            {
                log.LogWarning("ffmpeg failed ({Code}) on voice from {Nick}: {Err}", code, nick, err.Trim());
                throw new InvalidOperationException(Short(err));
            }

            var sec = (int)Math.Round(await DurationAsync(mp3, ct));
            if (sec < 1) throw new InvalidOperationException("зовсім коротко, спробуй ще раз");

            log.LogInformation("voice {Id} from {Nick}: {Bytes} B -> {Sec} s", id, nick, size, sec);
            return (new TrackInfo(id, "Голосове", nick, sec, null, $"/api/voice/{id}.mp3", null), mp3);
        }
        catch
        {
            Delete(mp3);
            throw;
        }
        finally
        {
            Delete(raw);
        }
    }

    /// <summary>Пише тіло запиту у файл, обриваючись на ліміті: розмір із заголовка нам ніхто не обіцяв.</summary>
    async Task<long> ReceiveAsync(Stream body, string path, CancellationToken ct)
    {
        await using var file = File.Create(path);
        var buf = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var n = await body.ReadAsync(buf, ct);
            if (n == 0) break;
            total += n;
            if (total > MaxUploadBytes) throw new InvalidOperationException($"задовгий запис, ліміт {MaxUploadBytes / (1024 * 1024)} МБ");
            await file.WriteAsync(buf.AsMemory(0, n), ct);
        }
        return total;
    }

    async Task<double> DurationAsync(string path, CancellationToken ct)
    {
        var (code, o) = await RunAsync(Tool("ffprobe"), [
            "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path,
        ], TimeSpan.FromSeconds(15), ct, wantStdout: true);
        return code == 0 && double.TryParse(o.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { /* хай полежить */ }
    }

    static async Task<(int Code, string Text)> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct, bool wantStdout = false)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Paths.Root,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{Path.GetFileName(exe)} не запустився");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* уже вийшов */ }
            throw new TimeoutException($"{Path.GetFileName(exe)} не вклався в час");
        }
        return (p.ExitCode, wantStdout ? await outTask : await errTask);
    }

    /// <summary>Повний текст помилки лишається в логах; людині — один зрозумілий рядок.</summary>
    static string Short(string err) =>
        err.Contains("Invalid data", StringComparison.OrdinalIgnoreCase) || err.Contains("Error opening input", StringComparison.OrdinalIgnoreCase)
            ? "браузер прислав щось нечитабельне, спробуй записати ще раз"
            : "не вийшло перегнати запис у mp3";
}
