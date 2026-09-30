using System.Globalization;
using System.Text.RegularExpressions;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Підтягнути рекламу до гучності музики в ефірі. Музика з ютуба приходить на −7…−9 LUFS, а мова, зведена
/// loudnorm до −14, на її тлі ледь чутна. Однопрохідний loudnorm вище −13 мову не піднімає — упирається в піки,
/// тож рецепт інший: компресор і dynaudnorm вирівнюють голос, лімітер зрізає піки з запасом, а тоді точний підсил
/// за двома замірами (лімітер «з'їдає» частину, тож другий замір поправляє перший) і лімітер-запобіжник.
/// Запобіжник працює на вчетверо вищій частоті: звичайний пропускає міжвідлікові піки (+2…3 dBTP після mp3), і
/// «ба-дум-тсс» хрипів би. Ціна — стеля: мова з паузами виходить на −11…−12 LUFS, гучніше вже тільки хрипом.
/// </summary>
public static class AdLoudness
{
    const string Pre = "acompressor=threshold=-30dB:ratio=6:attack=3:release=120:knee=6,dynaudnorm=f=150:g=15:p=0.9:m=20,"
                     + "alimiter=limit=0.5:attack=2:release=60:level=false";
    const string Post = "aresample=176400,alimiter=limit=0.79:attack=5:release=60:level=false,aresample=44100";

    static readonly Regex Integrated = new(@"I:\s+(-?\d+(?:\.\d+)?) LUFS", RegexOptions.Compiled);

    /// <summary>Інтегральна гучність файлу (LUFS) після фільтра <paramref name="filter"/>; null — ffmpeg не зміг.</summary>
    public static async Task<double?> MeasureAsync(string ffmpeg, string path, string? filter, CancellationToken ct)
    {
        var af = string.IsNullOrEmpty(filter) ? "ebur128=framelog=quiet" : filter + ",ebur128=framelog=quiet";
        var (code, err) = await FfmpegLiveRenderer.Run(ffmpeg, ["-hide_banner", "-nostats", "-i", path, "-af", af, "-f", "null", "-"],
            TimeSpan.FromSeconds(60), ct);
        if (code != 0) return null;
        var ms = Integrated.Matches(err);
        return ms.Count > 0 && double.TryParse(ms[^1].Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var i) ? i : null;
    }

    /// <summary>
    /// Переписати <paramref name="path"/> гучнішим (моно 44,1 кГц 96k). Файл, що вже не тихіший за ціль більш ніж на
    /// <paramref name="tolerance"/> LU (рецепт сам дає −11…−12 при цілі −10), не чіпається — тож прогнати бібліотеку вдруге безпечно.
    /// false — не вийшло, файл лишився як був.
    /// </summary>
    public static async Task<bool> BoostAsync(string ffmpeg, string path, double lufs, CancellationToken ct, double tolerance = 2)
    {
        if (!File.Exists(path) || !File.Exists(ffmpeg)) return false;
        if (await MeasureAsync(ffmpeg, path, null, ct) is not { } now) return false;
        if (now >= lufs - tolerance) return true;
        if (await MeasureAsync(ffmpeg, path, Pre, ct) is not { } pre || double.IsInfinity(pre) || pre < -70) return false;
        var gain = Math.Clamp(lufs - pre + 1.5, -20, 30);
        // Поправка — не більше +4 dB: під стелею лімітера більший підсил лише пережимає голос, гучнішим його не робить
        if (await MeasureAsync(ffmpeg, path, Chain(gain), ct) is { } got && got > -70)
            gain = Math.Clamp(gain + Math.Min(4, (lufs - got) * 1.3), -20, 30);

        var tmp = path + ".loud.mp3";
        var (code, _) = await FfmpegLiveRenderer.Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-i", path,
            "-af", Chain(gain) + ",aresample=44100", "-ac", "1", "-c:a", "libmp3lame", "-b:a", "96k", tmp], TimeSpan.FromSeconds(60), ct);
        if (code != 0 || !File.Exists(tmp)) { TryDelete(tmp); return false; }
        // Уже вичавлений файл гучнішим не стане — лишаємо як був, щоб повторний прогін не пережимав голос удруге
        if (await MeasureAsync(ffmpeg, tmp, null, ct) is not { } result || result < now + 1) { TryDelete(tmp); return true; }
        try { File.Move(tmp, path, overwrite: true); }
        catch (IOException) { TryDelete(tmp); return false; }
        catch (UnauthorizedAccessException) { TryDelete(tmp); return false; }
        return true;
    }

    static string Chain(double gain) => $"{Pre},volume={gain.ToString("0.##", CultureInfo.InvariantCulture)}dB,{Post}";

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
