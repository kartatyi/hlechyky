using System.Text;
using System.Text.Json;
using Anthropic.Models.Messages;

namespace Hlechyky;

/// <summary>
/// Руки Дядька Глека: тонкі обгортки над тим, що вже вміє <see cref="RadioEngine"/>. Кожна дія
/// виконується від імені того, хто попросив, і завжди з isAdmin: false — адмінських інструментів
/// (бан, налаштування) тут нема принципово, бо чат відкритий і замовити дію може будь-хто.
/// </summary>
public static class DjToolbox
{
    static JsonElement Str(string description) =>
        JsonSerializer.SerializeToElement(new { type = "string", description });

    static JsonElement Int(string description) =>
        JsonSerializer.SerializeToElement(new { type = "integer", description });

    public static readonly List<ToolUnion> Definitions =
    [
        new Tool
        {
            Name = "queue_track",
            Description = "Знайти трек і поставити його в чергу від імені того, хто попросив. " +
                          "Приймає вільний текст («Скрябін Спи собі сама»), посилання на YouTube/YouTube Music або Spotify. " +
                          "Це основний інструмент: коли просять щось поставити — саме він.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["query"] = Str("Виконавець і назва, або посилання"),
                },
                Required = ["query"],
            },
        },
        new Tool
        {
            Name = "search_tracks",
            Description = "Пошук без постановки в чергу: коли треба показати варіанти або перевірити, " +
                          "чи взагалі щось знайдеться. Нічого не змінює.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["query"] = Str("Що шукати"),
                },
                Required = ["query"],
            },
        },
        new Tool
        {
            Name = "skip_track",
            Description = "Пропустити те, що зараз грає. Роби тільки коли про це прямо попросили.",
            InputSchema = new() { Properties = new Dictionary<string, JsonElement>() },
        },
        new Tool
        {
            Name = "remove_from_queue",
            Description = "Прибрати трек із черги за його item_id (він є у списку черги в стані радіо).",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["item_id"] = Str("item_id із черги"),
                },
                Required = ["item_id"],
            },
        },
        new Tool
        {
            Name = "move_in_queue",
            Description = "Пересунути трек у черзі: to_index 0 означає «піде наступним».",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["item_id"] = Str("item_id із черги"),
                    ["to_index"] = Int("Нова позиція, рахуючи з 0"),
                },
                Required = ["item_id", "to_index"],
            },
        },
        new Tool
        {
            Name = "queue_playlist",
            Description = "Закинути в чергу цілий плейлист за його id (id бери з radio_stats what=playlists).",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["playlist_id"] = Int("id плейлиста"),
                    ["shuffle"] = JsonSerializer.SerializeToElement(
                        new { type = "boolean", description = "Перемішати перед додаванням (за замовчуванням так)" }),
                },
                Required = ["playlist_id"],
            },
        },
        new Tool
        {
            Name = "radio_stats",
            Description = "Заглянути в архів радіо: що грало (history), хто найактивніше замовляє (top), " +
                          "що залайкали (likes), які є плейлисти (playlists). Нічого не змінює.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["what"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "string",
                        @enum = new[] { "history", "top", "likes", "playlists" },
                        description = "Що саме подивитись",
                    }),
                    ["limit"] = Int("Скільки рядків (для top — за скільки днів), за замовчуванням 10"),
                },
                Required = ["what"],
            },
        },
    ];

    public static async Task<string> RunAsync(string name, IReadOnlyDictionary<string, JsonElement> input,
        string nick, RadioEngine engine, Db db, YtMusicClient ytm, CancellationToken ct)
    {
        string? Text(string key) => input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Num(string key) => input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        bool Flag(string key, bool fallback) => input.TryGetValue(key, out var v)
            ? v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback
            : fallback;

        try
        {
            switch (name)
            {
                case "queue_track":
                {
                    var q = Text("query");
                    if (string.IsNullOrWhiteSpace(q)) return "Порожній запит — нічого шукати.";
                    var (ok, message) = await engine.AddAsync(nick, isAdmin: false, q, null, ct);
                    return (ok ? "Готово: " : "Не вийшло: ") + message;
                }

                case "search_tracks":
                {
                    var q = Text("query");
                    if (string.IsNullOrWhiteSpace(q)) return "Порожній запит.";
                    var found = await ytm.SearchSongsAsync(q.Trim(), 6, ct);
                    if (found.Count == 0) return "Нічого не знайшлося.";
                    var sb = new StringBuilder("Знайшлося:\n");
                    foreach (var r in found)
                        sb.AppendLine($"  {r.Artist} — {r.Title} ({r.DurationSec / 60}:{r.DurationSec % 60:00})");
                    return sb.ToString();
                }

                case "skip_track":
                {
                    var (ok, message) = await engine.SkipAsync(nick);
                    return (ok ? "Пропустив: " : "Не вийшло: ") + message;
                }

                case "remove_from_queue":
                {
                    var id = Text("item_id");
                    if (string.IsNullOrWhiteSpace(id)) return "Треба item_id із черги.";
                    var (ok, message) = engine.Remove(id, nick, isAdmin: false);
                    return (ok ? "Прибрав: " : "Не вийшло: ") + message;
                }

                case "move_in_queue":
                {
                    var id = Text("item_id");
                    var to = Num("to_index");
                    if (string.IsNullOrWhiteSpace(id) || to is null) return "Треба item_id і to_index.";
                    var (ok, message) = engine.Move(id, to.Value, nick, isAdmin: false);
                    return (ok ? "Пересунув: " : "Не вийшло: ") + message;
                }

                case "queue_playlist":
                {
                    var id = Num("playlist_id");
                    if (id is null) return "Треба playlist_id.";
                    var playlist = db.GetPlaylist(id.Value);
                    if (playlist is null) return "Нема такого плейлиста.";
                    var ids = db.PlaylistTracks(id.Value).Select(x => x.Track.Id).ToList();
                    if (ids.Count == 0) return $"Плейлист «{playlist.Name}» порожній.";
                    var n = await engine.AddManyAsync(ids, nick, isAdmin: false, Flag("shuffle", true), ct);
                    return n == 0
                        ? $"Усе з «{playlist.Name}» уже в черзі або щойно грало."
                        : $"Закинув {n} треків з «{playlist.Name}».";
                }

                case "radio_stats":
                    return Stats(Text("what"), Num("limit"), db);

                default:
                    return $"Нема такого інструмента: {name}";
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"Інструмент {name} впав: {ex.Message}";
        }
    }

    static string Stats(string? what, int? limit, Db db)
    {
        var n = Math.Clamp(limit ?? 10, 1, 30);
        var sb = new StringBuilder();
        switch (what)
        {
            case "history":
                var history = db.History(n);
                if (history.Count == 0) return "Історія порожня.";
                sb.AppendLine("Що грало (найновіше згори):");
                foreach (var h in history)
                    sb.AppendLine($"  {h.Track.Label} — {(h.RequestedBy is null ? "твій вибір" : h.RequestedBy)}" +
                                  $"{(h.Likes > 0 ? $", лайків {h.Likes}" : "")}{(h.Skipped ? ", пропустили" : "")}");
                return sb.ToString();

            case "top":
                var days = Math.Clamp(limit ?? 7, 1, 365);
                var top = db.TopRequesters(days);
                if (top.Count == 0) return $"За {days} дн. ніхто нічого не замовляв.";
                sb.AppendLine($"Хто замовляв за {days} дн.:");
                foreach (var t in top) sb.AppendLine($"  {t.Nick}: {t.Count}");
                return sb.ToString();

            case "likes":
                var liked = db.LikedTracksDetailed(n);
                if (liked.Count == 0) return "Поки нічого не лайкали.";
                sb.AppendLine("Улюблене:");
                foreach (var l in liked) sb.AppendLine($"  {l.Track.Label} — {string.Join(", ", l.Likers)}");
                return sb.ToString();

            case "playlists":
                var lists = db.Playlists();
                if (lists.Count == 0) return "Плейлистів ще нема.";
                sb.AppendLine("Плейлисти:");
                foreach (var p in lists) sb.AppendLine($"  [{p.Id}] {p.Name} — {p.Count} треків, створив {p.CreatedBy}");
                return sb.ToString();

            default:
                return "what має бути history, top, likes або playlists.";
        }
    }
}
