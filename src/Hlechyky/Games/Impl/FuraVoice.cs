using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Голос Глека-диспетчера «Чумаків-далекобійників» (specs/fura.md, «1e»). Книга фраз — <c>data/fura/voice.json</c>:
/// подія → кілька варіантів без чисел. Сервер лише озвучує (той самий <see cref="TtsService"/> і кеш, що в «Своїй грі»
/// й «Додепах») і віддає, що вже готово: <c>GET /api/games/fura/voice</c>. Коли сказати — вирішує браузер: події рейсу
/// (зчепився, дзенькнуло, пів дороги) він бачить сам, без кругу через стіл.
/// </summary>
public sealed class FuraVoice(TtsService tts)
{
    public const string File = "data/fura/voice.json";

    public sealed record Book(string Voice, IReadOnlyDictionary<string, IReadOnlyList<string>> Lines);

    static readonly Lazy<Book> Default = new(() => Parse(System.IO.File.ReadAllText(Paths.Resolve(File))));

    /// <summary>Книга з диска (раз за життя процесу).</summary>
    public static Book Load() => Default.Value;

    public static Book Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var voice = root.TryGetProperty("voice", out var v) && v.GetString() is { Length: > 0 } s ? s : LiveLines.Glek;
        var lines = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var p in root.GetProperty("lines").EnumerateObject())
            lines[p.Name] = [.. p.Value.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Trim().Length > 0)];
        return new Book(voice, lines);
    }

    /// <summary>Усі репліки — у чергу озвучки (уже готові TtsService пропустить сам). Кличе прогрів на старті.</summary>
    public void Warm(Book? book = null)
    {
        var b = book ?? Load();
        tts.Enqueue(b.Voice, b.Lines.Values.SelectMany(x => x).Select(DotepyVoice.Clean));
    }

    /// <summary>
    /// Що віддати браузеру: подія → варіанти з адресою mp3, якщо вже озвучено (<c>url: null</c> — ще ні; браузер тоді
    /// покаже фразу текстом). <c>enabled: false</c> — голосу на сервері нема зовсім.
    /// </summary>
    public object Snapshot(Book? book = null)
    {
        var b = book ?? Load();
        return new
        {
            enabled = tts.Enabled,
            lines = b.Lines.ToDictionary(kv => kv.Key, kv => kv.Value.Select(text =>
            {
                var clip = tts.Enabled ? tts.TryGet(b.Voice, DotepyVoice.Clean(text)) : null;
                return new { text, url = clip is null ? null : SvoyaVoice.UrlPrefix + clip.Hash + ".mp3", sec = clip?.Seconds ?? 0 };
            }).ToList()),
        };
    }
}
