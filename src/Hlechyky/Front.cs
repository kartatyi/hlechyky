using System.Security.Cryptography;

namespace Hlechyky;

/// <summary>
/// Відбиток фронту. web/ віддається наживо (Program.cs, no-cache), тож сторінка, відкрита до деплою, живе зі старим
/// кодом — а F5 рве музику. <c>GET /api/front</c> → <c>{ files: { "app.js": "3f9a0c…", "games/runner.js": "…" } }</c>:
/// короткий хеш вмісту кожного .js/.css/.html. Сторінка звіряє його з тим, з яким стартувала (app.js, checkFront):
/// змінений модуль гри core.js перевантажує на льоту, решта — плашка «Сайт оновився».
/// </summary>
public sealed class FrontPrint(string webRoot, TimeSpan? fresh = null)
{
    static readonly string[] Kinds = [".js", ".css", ".html"];
    /// <summary>Після рестарту сторінки питають разом — стільки відповідаємо тим самим, не обходячи теку.</summary>
    readonly TimeSpan _fresh = fresh ?? TimeSpan.FromSeconds(2);

    readonly object _gate = new();
    readonly Dictionary<string, (DateTime At, long Len, string Hash)> _seen = new(StringComparer.Ordinal);
    (DateTime At, Dictionary<string, string> Files)? _last;

    public IReadOnlyDictionary<string, string> Files()
    {
        lock (_gate)
        {
            if (_last is { } l && DateTime.UtcNow - l.At < _fresh) return l.Files;
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Directory.Exists(webRoot))
                foreach (var path in Directory.EnumerateFiles(webRoot, "*", SearchOption.AllDirectories))
                {
                    if (!Kinds.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
                    var rel = Path.GetRelativePath(webRoot, path).Replace('\\', '/');
                    if (Hash(path, rel) is { } h) files[rel] = h;
                }
            _last = (DateTime.UtcNow, files);
            return files;
        }
    }

    /// <summary>Хеш рахуємо лише тоді, коли в файла змінився час запису чи довжина.</summary>
    string? Hash(string path, string rel)
    {
        try
        {
            var fi = new FileInfo(path);
            if (_seen.TryGetValue(rel, out var s) && s.At == fi.LastWriteTimeUtc && s.Len == fi.Length) return s.Hash;
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))[..12].ToLowerInvariant();
            _seen[rel] = (fi.LastWriteTimeUtc, fi.Length, hash);
            return hash;
        }
        // git саме переписує файл — віддаємо попередній відбиток, наступний запит прочитає новий
        catch (IOException) { return _seen.TryGetValue(rel, out var s) ? s.Hash : null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

public static class FrontSetup
{
    public static WebApplication MapFront(this WebApplication app)
    {
        var print = new FrontPrint(app.Environment.WebRootPath);
        app.MapGet("/api/front", (HttpContext c) =>
        {
            c.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { files = print.Files() });
        });
        return app;
    }
}
