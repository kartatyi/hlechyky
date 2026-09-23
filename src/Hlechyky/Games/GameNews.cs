using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hlechyky.Games;

/// <summary>
/// «Що нового» в іграх: яку версію оновлення кожної гри гравець уже бачив. Сам текст живе в модулі гри
/// (<c>news: { v, title, items }</c> у <c>HGames.register</c>), тож сервер про зміст нічого не знає — лише
/// пам'ятає «бачив v» на нік, щоб вікно вискакувало один раз і з будь-якого пристрою. Гончарне коло має
/// своє «що нового» у збереженні й сюди не ходить.
/// Зберігається в <see cref="IGameStore"/> під ключем <c>news:&lt;нік&gt;</c> одним JSON-словником гра → версія.
/// </summary>
public sealed partial class GameNews(IGameStore store)
{
    const int MaxGames = 200;
    readonly Lock _gate = new();

    [GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial Regex GameId();
    [GeneratedRegex("^[A-Za-z0-9._-]{1,32}$")]
    private static partial Regex Version();

    static string Key(string nick) => "news:" + Auth.NickKey(nick);

    public Dictionary<string, string> Seen(string nick)
    {
        if (Auth.IsGuestNick(nick)) return [];
        lock (_gate) return Read(nick);
    }

    /// <summary>Позначити «бачив». false — ігнорували (гість, криві дані).</summary>
    public bool Mark(string nick, string? game, string? v)
    {
        if (Auth.IsGuestNick(nick) || game is null || v is null || !GameId().IsMatch(game) || !Version().IsMatch(v)) return false;
        lock (_gate)
        {
            var seen = Read(nick);
            if (seen.TryGetValue(game, out var was) && was == v) return true;
            if (!seen.ContainsKey(game) && seen.Count >= MaxGames) return false;
            seen[game] = v;
            store.SaveState(Key(nick), JsonSerializer.Serialize(seen));
            return true;
        }
    }

    Dictionary<string, string> Read(string nick)
    {
        var json = store.LoadState(Key(nick));
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public sealed record MarkBody(string? Game, string? V);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/games/news", (HttpContext c, GameNews news) => Results.Ok(new { seen = news.Seen(Auth.Nick(c)) }));
        app.MapPost("/api/games/news", (HttpContext c, MarkBody body, GameNews news) =>
            Results.Ok(new { ok = news.Mark(Auth.Nick(c), body.Game, body.V) }));
    }
}
