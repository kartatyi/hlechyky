using System.Diagnostics;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Перетискання фото «Де це?» до ~180 КБ: ширина не більше 1152, JPEG q 7 (а коли й так важко — q 10). Кеш
/// Вікісховища — мініатюри 1280 з медіаною 358 КБ; на 3G це 1–3 секунди порожньої рамки. Після ffmpeg файл
/// ще раз іде через <see cref="GeoImage.Strip"/> (ffmpeg дописує свій коментар), і лише тоді атомарно
/// підміняє старий. Лише у фоні завантажувача, ніколи під замком кімнати.
/// <para>
/// Файли з власним тегом повороту (APP1) не чіпаємо: ffmpeg різних версій по-різному шанує EXIF-поворот, і
/// «боком» тут гірше за «важко».
/// </para>
/// </summary>
public static class GeoShrink
{
    public const long Target = 230 * 1024;
    public const int MaxWidth = 1152;

    /// <summary>Пройтись по теці й перетиснути все важче за <see cref="Target"/>. Скільки перетиснуто.</summary>
    public static async Task<int> AllAsync(string ffmpeg, string dir, ILogger? log, CancellationToken ct)
    {
        if (!File.Exists(ffmpeg) || !Directory.Exists(dir)) return 0;
        var n = 0;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.jpg").ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (f.Length <= Target) continue;
            if (await OneAsync(ffmpeg, f.FullName, log, ct)) n++;
        }
        if (n > 0) log?.LogInformation("«Де це?»: перетиснуто фото {N}", n);
        return n;
    }

    /// <summary>Одне фото: true — підмінили легшим.</summary>
    public static async Task<bool> OneAsync(string ffmpeg, string path, ILogger? log, CancellationToken ct)
    {
        byte[] src;
        try { src = await File.ReadAllBytesAsync(path, ct); }
        catch (IOException) { return false; }
        if (GeoImage.HasSegment(src, 0xE1)) return false;
        var tmp = path + ".s.tmp.jpg";
        try
        {
            foreach (var q in new[] { "7", "10" })
            {
                if (!await RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-i", path,
                        "-vf", $"scale='min({MaxWidth},iw)':-2", "-q:v", q, tmp], ct)) return false;
                var bytes = await File.ReadAllBytesAsync(tmp, ct);
                if (bytes.Length > Target && q == "7") continue;
                if (bytes.Length >= src.Length || GeoImage.Strip(bytes) is not { } clean) return false;
                await File.WriteAllBytesAsync(tmp, clean, ct);
                File.Move(tmp, path, overwrite: true);
                return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.LogWarning("«Де це?»: не перетиснув {File} — {Error}", Path.GetFileName(path), ex.Message);
            return false;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }

    static async Task<bool> RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return false;
        var err = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return false;
        }
        await err;
        return p.ExitCode == 0;
    }
}
