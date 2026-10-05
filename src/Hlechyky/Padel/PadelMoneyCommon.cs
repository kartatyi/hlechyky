using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Padel;

// Спільне половини грошей (збори, витрати, банки, рейтинг): хто прийшов із запитом, відповіді й SQL-дрібниці.
// Окремими іменами з префіксом PadelMoney…, щоб не перетнутись із половиною гри.

/// <summary>
/// Хто діє: pid акаунта (гість сайту — null, йому лише дивитись) і чи це адмін. Сервіси приймають його замість
/// HttpContext — тести збирають його одним рядком.
/// </summary>
public sealed record PadelMoneyActor(string? Pid, bool Admin)
{
    public static PadelMoneyActor Of(HttpContext c) => new(Hlechyky.Padel.Pid.Of(c), Auth.IsAdmin(c));
    public bool User => Pid is not null;
}

static class PadelMoneyHttp
{
    public const string AccountsOnly = "Це можуть лише акаунти — зайди на головній";

    public static IResult Ok(object body) => Results.Ok(body);
    public static IResult Fail(string message, int status = 400) => PadelSetup.Fail(message, status);
}

static class PadelMoneySql
{
    public static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    public static DateTimeOffset Ts(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static SqliteCommand Cmd(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public static int Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        return cmd.ExecuteNonQuery();
    }

    public static List<T> Rows<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> row, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(row(r));
        return list;
    }

    /// <summary>"g5" / "e7" / "p3" → 5 / 7 / 3; чуже — null.</summary>
    public static long? IdOf(string? id, char prefix) =>
        id is { Length: > 1 } && id[0] == prefix && long.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
}

/// <summary>Київський час для людей: «сб 4.10 о 18:00».</summary>
static class PadelMoneyTime
{
    static readonly string[] Days = ["нд", "пн", "вт", "ср", "чт", "пт", "сб"];

    public static DateTime Kyiv(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, Hlechyky.Games.Days.Kyiv).DateTime;
    public static string Hm(DateTimeOffset t) => Kyiv(t).ToString("HH:mm", CultureInfo.InvariantCulture);
    public static string When(DateTimeOffset t)
    {
        var k = Kyiv(t);
        return $"{Days[(int)k.DayOfWeek]} {k.Day}.{k.Month:00} о {Hm(t)}";
    }
}
