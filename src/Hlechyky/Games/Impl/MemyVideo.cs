using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Кадр із відео для «Мемології» (мем із <c>image.video</c> + <c>image.at</c>, specs/memy.md §4). Пише сирий кадр
/// (PNG) у файл — далі той самий конвеєр, що й для картинки за адресою (<see cref="MemyImages.NormalizeAsync"/>).
/// За інтерфейсом — щоб тести обходились без YouTube.
/// </summary>
public interface IMemyFrames
{
    /// <summary>(вдалось, безнадійно — повтор нічого не змінить, чому ні).</summary>
    Task<(bool Ok, bool Final, string Why)> GrabAsync(string video, double at, string to, CancellationToken ct);
}

/// <summary>
/// Справжній грабер: <c>yt-dlp -g</c> дає пряму адресу потоку (≤ 720p, спершу avc1 — ffmpeg розкодує будь-де), потім
/// <c>ffmpeg -ss &lt;at&gt; -i &lt;потік&gt; -frames:v 1</c> — один кадр, без завантаження всього відео (2–4 с на мем).
/// Адресу потоку тримаємо годину: кілька мемів з одного відео — один запуск yt-dlp.
/// </summary>
public sealed class MemyVideoFrames(YtDlpService ytdlp, string ffmpeg, ILogger? log = null) : IMemyFrames
{
    public const string Format = "bv*[height<=720][vcodec^=avc1]/bv*[height<=720]/b[height<=720]/b";
    static readonly TimeSpan StreamLife = TimeSpan.FromHours(1);
    static readonly TimeSpan FfmpegTimeout = TimeSpan.FromSeconds(45);

    readonly ConcurrentDictionary<string, (string Url, DateTime Until)> _streams = new(StringComparer.Ordinal);

    public async Task<(bool Ok, bool Final, string Why)> GrabAsync(string video, double at, string to, CancellationToken ct)
    {
        if (!File.Exists(ffmpeg)) return (false, true, $"нема ffmpeg ({ffmpeg})");
        string? stream;
        try
        {
            if (_streams.TryGetValue(video, out var s) && s.Until > DateTime.UtcNow) stream = s.Url;
            else
            {
                (stream, var err, var raw) = await ytdlp.StreamUrlAsync(video, Format, ct);
                if (stream is null) return (false, Hopeless(raw), "yt-dlp: " + err);
                _streams[video] = (stream, DateTime.UtcNow + StreamLife);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (System.ComponentModel.Win32Exception ex) { return (false, true, "yt-dlp не запустився: " + ex.Message); }
        catch (Exception ex) { return (false, false, "yt-dlp: " + ex.Message); }

        var (ok, code, text) = await RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-ss", at.ToString("0.###", CultureInfo.InvariantCulture),
             "-i", stream, "-frames:v", "1", "-an", "-f", "image2", "-c:v", "png", to], ct);
        if (ok && File.Exists(to) && new FileInfo(to).Length > 0) return (true, false, "");
        // адреса потоку могла протухнути (403) — наступна спроба візьме свіжу
        _streams.TryRemove(video, out _);
        if (ok) return (false, true, $"кадру на {at.ToString("0.##", CultureInfo.InvariantCulture)} с нема — відео коротше?");
        log?.LogInformation("Мемологія: кадр із {Video} на {At} с не взявся — {Error}", video, at, text);
        return (false, false, code is null ? "кадр: " + text : "кадр: " + (text.Length > 0 ? text : $"ffmpeg вийшов з кодом {code}"));
    }

    /// <summary>
    /// Безнадійне від YouTube (за сирим stderr yt-dlp): 18+ (просить підтвердити вік — куки), відео видалене, приватне,
    /// недоступне, акаунт закрито. Решта — мережа, «підтверди, що не бот», тимчасові відмови — звичайний збій, ще дві
    /// спроби (а там сон до рестарту, без нескінченних повторів).
    /// </summary>
    public static bool Hopeless(string err) =>
        err.Contains("confirm your age", StringComparison.OrdinalIgnoreCase)
        || err.Contains("age-restricted", StringComparison.OrdinalIgnoreCase)
        || err.Contains("inappropriate for some users", StringComparison.OrdinalIgnoreCase)
        || err.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase)
        || err.Contains("This video is not available", StringComparison.OrdinalIgnoreCase)
        || err.Contains("Private video", StringComparison.OrdinalIgnoreCase)
        || err.Contains("has been removed", StringComparison.OrdinalIgnoreCase)
        || err.Contains("account associated with this video has been terminated", StringComparison.OrdinalIgnoreCase)
        || err.Contains("Incomplete YouTube ID", StringComparison.OrdinalIgnoreCase);

    /// <summary>(вийшов з 0, код виходу чи null — не вклався, останні слова stderr).</summary>
    static async Task<(bool Ok, int? Code, string Error)> RunAsync(string exe, List<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process? p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception ex) { return (false, -1, "ffmpeg не запустився: " + ex.Message); }
        if (p is null) return (false, -1, "ffmpeg не запустився");
        using (p)
        {
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { /* уже вийшов — не біда */ }
            var err = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(FfmpegTimeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                if (ct.IsCancellationRequested) throw;
                return (false, null, $"не вклався в {FfmpegTimeout.TotalSeconds:0} с");
            }
            var text = (await err).Trim();
            return (p.ExitCode == 0, p.ExitCode, text.Length > 200 ? text[^200..] : text);
        }
    }
}
