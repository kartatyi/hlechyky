using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Звести сценарій у готовий mp3. Інтерфейс — для тестів: правила «хто, коли й що» не мусять чекати ні edge-tts, ні
/// ffmpeg. Невдача — null, не виняток: ролик просто не вийде, і ефір візьме бібліотеку.
/// </summary>
public interface ILiveRenderer
{
    Task<double?> RenderAsync(LiveScript script, string outPath, CancellationToken ct);
}

/// <summary>
/// Справжній рендер: кожна репліка через <see cref="ITtsEngine"/> (паузи стиснуті, як у Tts), склейка з ~0,3 с тиші,
/// «ба-дум-тсс» після позначених реплік, підпис наприкінці. Під голос — підкладка по колу: ~1,5 с музики наперед,
/// під голосом притишена sidechain-компресором, після — ~2 с хвоста з затуханням. Гучність — loudnorm до −14 LUFS,
/// як решта ефіру, моно 44,1 кГц 96k.
/// </summary>
public sealed class FfmpegLiveRenderer(ITtsEngine tts, IOptionsMonitor<LiveAdsOptions> options, IOptionsMonitor<TtsOptions> ttsOptions,
    IOptionsMonitor<YtDlpOptions> yt, ILogger<FfmpegLiveRenderer> log) : ILiveRenderer
{
    public const double Intro = 1.5, Gap = 0.3, RimGap = 0.1, Tail = 2.2;

    LiveAdsOptions O => options.CurrentValue;
    string Ffmpeg => Path.Combine(Paths.Resolve(yt.CurrentValue.FfmpegDir), OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
    string CacheDir => Paths.Resolve(yt.CurrentValue.CacheDir);

    public async Task<double?> RenderAsync(LiveScript script, string outPath, CancellationToken ct)
    {
        if (!File.Exists(Ffmpeg)) { log.LogInformation("жива реклама без ffmpeg ({Path}) — лише бібліотека", Ffmpeg); return null; }
        var bed = Path.Combine(Paths.Resolve(O.BedsDir), script.Style + ".mp3");
        if (!File.Exists(bed)) bed = Path.Combine(Paths.Resolve(O.BedsDir), "hold.mp3");
        if (!File.Exists(bed)) { log.LogWarning("нема підкладки {Bed}", bed); return null; }
        var rim = Path.Combine(Paths.Resolve(O.BedsDir), "rimshot.mp3");
        var hasRim = File.Exists(rim);

        // Репліки — у сталому каталозі, а не в тимчасовому на ролик: живий процес edge-tts стартує в каталозі першої
        // репліки, і Windows не дає видалити каталог, у якому сидить процес. Прибираються самі файли.
        var dir = Path.Combine(CacheDir, "liveads", "parts");
        Directory.CreateDirectory(dir);
        var tag = Guid.NewGuid().ToString("N")[..10];
        var mine = new List<string>();
        try
        {
            // Репліки — по черзі: живий edge-tts один, а черга з десяти одночасних лише наздоганяє сама себе
            var parts = new List<(string Path, double Seconds, double Gap)>();
            var i = 0;
            foreach (var line in script.Lines)
            {
                var path = await Speak(line, Path.Combine(dir, $"{tag}-{i++:00}.mp3"), ct);
                if (path is null) return null;
                mine.Add(path);
                var sec = await tts.DurationAsync(path, ct);
                parts.Add((path, sec, line.Rim && hasRim ? RimGap : Gap));
                if (line.Rim && hasRim) parts.Add((rim, 1.2, Gap));
            }
            var sign = await Sign(ct);
            if (sign is not null) parts.Add((sign, await tts.DurationAsync(sign, ct), 0));

            var voice = parts.Sum(p => p.Seconds + p.Gap);
            var total = Intro + voice + Tail;
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-stream_loop", "-1", "-i", bed };
            foreach (var p in parts) args.AddRange(["-i", p.Path]);
            args.AddRange(["-filter_complex", Graph(parts.Select(p => p.Gap).ToList(), total), "-map", "[out]",
                "-ac", "1", "-ar", "44100", "-c:a", "libmp3lame", "-b:a", "96k", "-t", F(total), outPath]);
            var (code, err) = await Run(Ffmpeg, args, TimeSpan.FromSeconds(Math.Max(20, O.RenderTimeoutSeconds)), ct);
            if (code != 0 || !File.Exists(outPath))
            {
                log.LogWarning("ffmpeg не зібрав живу рекламу ({Code}): {Err}", code, err.Trim().Split('\n').LastOrDefault());
                return null;
            }
            return total;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        finally
        {
            foreach (var f in mine)
                try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Граф: репліки з паузами — в один голос; голос зсунутий на інтро і доповнений тишею на хвіст; підкладка
    /// притишується голосом (sidechain) і змішується з ним; обрізка, вхід/вихід, loudnorm.
    /// </summary>
    public static string Graph(IReadOnlyList<double> gaps, double total)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < gaps.Count; i++)
            sb.Append($"[{i + 1}:a]aresample=44100,aformat=sample_fmts=fltp:channel_layouts=mono,apad=pad_dur={F(gaps[i])}[p{i}];");
        for (var i = 0; i < gaps.Count; i++) sb.Append($"[p{i}]");
        sb.Append($"concat=n={gaps.Count}:v=0:a=1[v];");
        sb.Append($"[v]adelay={(int)(Intro * 1000)}:all=1,apad=pad_dur={F(Tail + 1)}[vd];[vd]asplit=2[vm][sc];");
        sb.Append("[0:a]aresample=44100,aformat=sample_fmts=fltp:channel_layouts=mono,volume=0.5[bd];");
        sb.Append("[bd][sc]sidechaincompress=threshold=0.02:ratio=8:attack=20:release=450[duck];");
        sb.Append($"[duck][vm]amix=inputs=2:duration=shortest:normalize=0,atrim=0:{F(total)},afade=t=in:d=0.3,");
        sb.Append($"afade=t=out:st={F(total - 2)}:d=2,loudnorm=I=-14:TP=-1.5:LRA=11,aresample=44100[out]");
        return sb.ToString();
    }

    static string F(double x) => x.ToString("0.###", CultureInfo.InvariantCulture);

    async Task<string?> Speak(LiveLine line, string path, CancellationToken ct)
    {
        var ok = await tts.SynthesizeAsync(TtsService.Voice(line.Voice), line.Text, line.Rate, ttsOptions.CurrentValue.PauseMs, path, ct);
        if (ok && File.Exists(path)) return path;
        log.LogInformation("жива реклама без голосу: edge-tts не озвучив «{Text}»", line.Text.Length > 40 ? line.Text[..40] + "…" : line.Text);
        return null;
    }

    /// <summary>Підпис «Глечики. Слухай, як гуде.» — однаковий у кожному ролику, тож озвучується раз і лежить у кеші.</summary>
    async Task<string?> Sign(CancellationToken ct)
    {
        var text = O.Sign;
        if (string.IsNullOrWhiteSpace(text)) return null;
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{text}|{O.Rate}|{ttsOptions.CurrentValue.PauseMs}")))[..12].ToLowerInvariant();
        var path = Path.Combine(CacheDir, "liveads", $"sign-{hash}.mp3");
        if (File.Exists(path)) return path;
        var part = path + ".part.mp3";
        if (!await tts.SynthesizeAsync(TtsService.Voice(LiveLines.Glek), text, O.Rate, ttsOptions.CurrentValue.PauseMs, part, ct)) return null;
        try { File.Move(part, path, overwrite: true); } catch (IOException) { return File.Exists(path) ? path : null; }
        return path;
    }

    static async Task<(int Code, string Err)> Run(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var err = p.StandardError.ReadToEndAsync(cts.Token);
        _ = p.StandardOutput.ReadToEndAsync(cts.Token);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (-1, "час вийшов");
        }
        return (p.ExitCode, await err);
    }
}
