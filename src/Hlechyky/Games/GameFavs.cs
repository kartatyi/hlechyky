using System.Text.Json;
using System.Text.RegularExpressions;
using Hlechyky.Games.Economy;

namespace Hlechyky.Games;

/// <summary>
/// «⭐ Мої ігри» в лобі: які ігри акаунт позначив зірочкою. Лише акаунт — гостьовий нік може взяти будь-хто.
/// Гра в родині (хрестики, юрма) позначається id родини — він же id одного з її режимів, тож сервер про родини
/// не знає й тут. Зберігається в <see cref="IGameStore"/> під ключем <c>favs:&lt;нік&gt;</c> JSON-масивом у порядку
/// позначення. Разом із «🕘 Нещодавно» лобі бере одним <c>GET /api/games/mine</c>.
/// </summary>
public sealed partial class GameFavs(IGameStore store)
{
    public const int Max = 30;
    /// <summary>Скільки ігор у «🕘 Нещодавно» віддаємо: лобі покаже менше (родини зливаються в одну плитку).</summary>
    public const int RecentMax = 12;
    readonly Lock _gate = new();

    [GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial Regex GameId();

    static string Key(string nick) => "favs:" + Auth.NickKey(nick);

    public List<string> Of(string nick)
    {
        if (Auth.IsGuestNick(nick)) return [];
        lock (_gate) return Read(nick);
    }

    /// <summary>Поставити чи зняти зірочку. null — відмова (гість, криві дані, повно).</summary>
    public List<string>? Set(string nick, string? game, bool on)
    {
        if (Auth.IsGuestNick(nick) || game is null || !GameId().IsMatch(game)) return null;
        lock (_gate)
        {
            var favs = Read(nick);
            var has = favs.Contains(game);
            if (on == has) return favs;
            if (on && favs.Count >= Max) return null;
            if (on) favs.Add(game); else favs.Remove(game);
            store.SaveState(Key(nick), JsonSerializer.Serialize(favs));
            return favs;
        }
    }

    List<string> Read(string nick)
    {
        var json = store.LoadState(Key(nick));
        if (string.IsNullOrEmpty(json)) return [];
        try { return (JsonSerializer.Deserialize<List<string>>(json) ?? []).Where(g => g is not null && GameId().IsMatch(g)).Distinct().ToList(); }
        catch (JsonException) { return []; }
    }

    public sealed record FavBody(string? Game, bool On);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/games/mine", Mine);
        app.MapPost("/api/games/fav", Fav);
    }

    /// <summary>
    /// GET /api/games/mine: зірочки (лише акаунт, у гостя порожньо) і «🕘 Нещодавно» — ігри від найсвіжішої
    /// з днем (київським), коли грав. Нещодавні — й гостеві: вони з його ж партій, нічого не зберігаємо.
    /// </summary>
    public static IResult Mine(HttpContext c, GameFavs favs, EconomyStore store)
    {
        var nick = Auth.Nick(c);
        var user = Auth.IsUser(c);
        var recent = Auth.NickKey(nick) == Auth.Guest ? [] : store.RecentGames(EconomyStore.Key(nick), RecentMax);
        return Results.Ok(new { account = user, favs = user ? favs.Of(nick) : [], recent });
    }

    /// <summary>POST /api/games/fav { game, on }: ok і всі зірочки після зміни. Гість — 403.</summary>
    public static IResult Fav(HttpContext c, FavBody body, GameFavs favs)
    {
        if (!Auth.IsUser(c)) return Results.Json(new { ok = false, error = "Зірочки — для акаунтів: зареєструй нік у профілі." }, statusCode: 403);
        var list = favs.Set(Auth.Nick(c), body.Game, body.On);
        return list is null
            ? Results.Ok(new { ok = false, error = "Не вийшло — забагато зірочок (до " + Max + ") або дивна гра.", favs = favs.Of(Auth.Nick(c)) })
            : Results.Ok(new { ok = true, favs = list });
    }
}
