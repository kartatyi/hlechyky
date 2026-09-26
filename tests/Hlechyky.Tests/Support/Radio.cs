using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Hlechyky.Tests.Support;

/// <summary>
/// Радіо в базі без ефіру: треки, програвання, лайки й репліки з точним часом (StartPlay і ToggleLike ставлять
/// «зараз», а тестам треба перевіряти межі періодів), і запит від імені ніка — як його бачить маршрут.
/// </summary>
public static class Radio
{
    public static TrackInfo Track(Db db, string id, string artist = "Гурт", string? title = null)
    {
        var t = new TrackInfo(id, title ?? "Пісня " + id, artist, 180, null, "https://music.youtube.com/watch?v=" + id, null);
        db.UpsertTrack(t);
        return t;
    }

    /// <summary>Програвання, що почалось о <paramref name="at"/> і дограло за три хвилини; повертає його id.</summary>
    public static long Play(Db db, string trackId, string source, string? by, DateTimeOffset at)
    {
        db.Exec("INSERT INTO plays(track_id, source, requested_by, started_at, ended_at) VALUES($t, $s, $b, $at, $end)",
            ("$t", trackId), ("$s", source), ("$b", by), ("$at", Iso(at)), ("$end", Iso(at.AddMinutes(3))));
        return db.With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT MAX(id) FROM plays";
            return (long)cmd.ExecuteScalar()!;
        });
    }

    public static void Like(Db db, string trackId, string nick, DateTimeOffset at) =>
        db.Exec("INSERT INTO likes(track_id, nick, created_at) VALUES($t, $n, $at)", ("$t", trackId), ("$n", nick), ("$at", Iso(at)));

    public static void Chat(Db db, string nick, string text, DateTimeOffset at) =>
        db.Exec("INSERT INTO chat(nick, text, kind, created_at) VALUES($n, $t, 'chat', $at)", ("$n", nick), ("$t", text), ("$at", Iso(at)));

    /// <summary>Запит від імені ніка, як його лишає UseHlechykyAuth: нік і роль — у c.Items.</summary>
    public static HttpContext As(string nick, bool admin = false)
    {
        var c = new DefaultHttpContext();
        c.Items["nick"] = nick;
        c.Items["role"] = admin ? "admin" : "member";
        return c;
    }

    /// <summary>Код відповіді й тіло так, як їх побачить браузер (camelCase).</summary>
    public static (int? Status, JsonElement Body) Reply(IResult result) =>
        (((IStatusCodeHttpResult)result).StatusCode, Views.Json(((IValueHttpResult)result).Value));

    static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("o");
}
