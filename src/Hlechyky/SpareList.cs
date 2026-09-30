using System.Text;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>
/// Запаска ефіру: список треків із кешу, який liquidsoap крутить сам, коли в черзі й у Глека порожньо, бо нове
/// не вантажиться (yt-dlp зламався, YouTube лежить, сервер упав). Раніше цю дірку закривав потік зі Спотіфаю.
/// Спершу те, що люди люблять (❤ чи грало ≥ <see cref="SparePlan.OftenPlays"/> разів), а мало такого — докидаємо
/// будь-що з кешу, що хоч раз грало. Реклама між треками запаски — справа джингла (<c>AdJingle</c>), не цього списку.
/// </summary>
public sealed class SpareList(Db db, YtDlpService ytdlp, IOptionsMonitor<LiquidsoapOptions> liq, IOptionsMonitor<AutoDjOptions> adj,
    ILogger<SpareList> log) : BackgroundService
{
    public string FilePath => Paths.Resolve(liq.CurrentValue.SparePlaylist);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Write(); }
            catch (Exception ex) { log.LogWarning(ex, "список запаски не записався"); }
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, liq.CurrentValue.SpareRefreshMinutes)), ct); }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>Переписати список. Повертає, скільки треків у ньому.</summary>
    public int Write()
    {
        var o = liq.CurrentValue;
        var banned = db.BannedIds();
        var cacheDir = Path.GetFullPath(ytdlp.CacheDir);
        var candidates = new List<SparePlan.Candidate>();
        foreach (var s in db.CacheStats())
        {
            if (s.FilePath is not { } file || banned.Contains(s.TrackId)) continue;
            // лише треки самого радіо: у підтеках кешу живуть ігри (мелодії, голоси), їм в ефірі не місце
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)), cacheDir, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(file)) continue;
            candidates.Add(new SparePlan.Candidate(s.TrackId, file, s.Plays, s.Likes));
        }
        var picked = SparePlan.Pick(candidates, o.SpareMax);

        var sb = new StringBuilder();
        var n = 0;
        foreach (var c in picked)
        {
            var t = db.GetTrack(c.TrackId);
            if (t is null || VoiceService.IsVoice(t.Id)) continue;
            if (adj.CurrentValue.MaxDurationSeconds > 0 && t.DurationSec > adj.CurrentValue.MaxDurationSeconds) continue;
            var uri = string.IsNullOrEmpty(o.CacheMount) ? Path.GetFullPath(c.File).Replace('\\', '/')
                : o.CacheMount.TrimEnd('/') + "/" + Path.GetFileName(c.File);
            sb.Append(LiquidsoapClient.Annotate([("rt_kind", "spare"), ("track_id", t.Id), ("title", t.Title), ("artist", t.Artist)], uri)).Append('\n');
            n++;
        }

        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = sb.ToString();
        // liquidsoap стежить за файлом і перечитує його на кожну зміну: той самий список не чіпаємо
        if (File.Exists(path) && File.ReadAllText(path) == text) return n;
        // спершу поруч, потім підміна — liquidsoap ніколи не прочитає напівзаписаний список
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
        log.LogInformation("запаска: {N} треків у {File}", n, path);
        return n;
    }
}

/// <summary>Що брати в запаску — окремо від файлів і бази, щоб перевіряти тестами.</summary>
public static class SparePlan
{
    public sealed record Candidate(string TrackId, string File, int Plays, int Likes);

    /// <summary>«Люблять» — як у «Ті, що ми слухаємо»: ❤ або грало стільки разів.</summary>
    public const int OftenPlays = 3;
    /// <summary>Менше улюблених — докидаємо просто те, що вже грало, щоб запаска не крутила десяток пісень по колу.</summary>
    public const int MinGood = 40;

    public static List<Candidate> Pick(IEnumerable<Candidate> all, int max)
    {
        var list = all.GroupBy(c => c.TrackId).Select(g => g.First()).ToList();
        var good = list.Where(c => c.Likes > 0 || c.Plays >= OftenPlays)
            .OrderByDescending(c => c.Likes).ThenByDescending(c => c.Plays).ThenBy(c => c.TrackId, StringComparer.Ordinal).ToList();
        if (good.Count < MinGood)
            good.AddRange(list.Where(c => c.Likes == 0 && c.Plays is > 0 and < OftenPlays)
                .OrderByDescending(c => c.Plays).ThenBy(c => c.TrackId, StringComparer.Ordinal).Take(MinGood - good.Count));
        return good.Take(Math.Max(0, max)).ToList();
    }
}
