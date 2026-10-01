using Hlechyky.Games;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Padel;

/// <summary>Відповідь сервісу Падельні: тіло успіху або людська відмова з кодом (400 правила, 403 права, 404 нема).</summary>
public sealed record PadelReply(object? Body, string? Error = null, int Status = 200)
{
    public static PadelReply No(string message, int status = 400) => new(null, message, status);
    public IResult Http() => Error is null ? Results.Json(Body) : PadelSetup.Fail(Error, Status);
}

/// <summary>
/// Гості падела й імена акаунтів. Гість — просто ім'я (<c>g:&lt;число&gt;</c>); прив'язаний до акаунта гість
/// рахується акаунту (<see cref="Canon"/>), але в старих матчах і далі видно його ім'я — так чесніше до історії.
/// Акаунти читаємо з наявної таблиці <c>accounts</c> (лише читання) і тримаємо в пам'яті: імена потрібні на кожен
/// вид табло, а нові акаунти з'являються рідко — промах просто перечитує таблицю (не частіше ніж раз на 2 с).
/// </summary>
public sealed class PadelPlayers : IPadelPlayers
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS padel_guests(
            id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, name_key TEXT NOT NULL, linked_to TEXT,
            created_at TEXT NOT NULL, created_by TEXT);
        """;

    sealed record Guest(long Id, string Name, string? LinkedTo);

    readonly Db _db;
    readonly IClock _clock;
    readonly object _lock = new();
    readonly Dictionary<long, Guest> _guests = [];
    Dictionary<string, string> _accounts = new(StringComparer.Ordinal);
    DateTimeOffset _accountsAt = DateTimeOffset.MinValue;

    public PadelPlayers(Db db, IClock clock)
    {
        _db = db;
        _clock = clock;
        _db.With(c =>
        {
            using (var cmd = c.CreateCommand()) { cmd.CommandText = Schema; cmd.ExecuteNonQuery(); }
            using var q = c.CreateCommand();
            q.CommandText = "SELECT id, name, linked_to FROM padel_guests";
            using var r = q.ExecuteReader();
            while (r.Read()) _guests[r.GetInt64(0)] = new(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
            return 0;
        });
        LoadAccounts();
    }

    void LoadAccounts()
    {
        var map = _db.With(c =>
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            using var q = c.CreateCommand();
            q.CommandText = "SELECT nick_key, nick FROM accounts";
            try
            {
                using var r = q.ExecuteReader();
                while (r.Read()) d[r.GetString(0)] = r.GetString(1);
            }
            catch (SqliteException) { /* таблиці ще нема — порожньо */ }
            return d;
        });
        lock (_lock) { _accounts = map; _accountsAt = _clock.UtcNow; }
    }

    string? AccountNick(string key)
    {
        lock (_lock) if (_accounts.TryGetValue(key, out var n)) return n;
        if (_clock.UtcNow - _accountsAt < TimeSpan.FromSeconds(2)) return null;
        LoadAccounts();
        lock (_lock) return _accounts.TryGetValue(key, out var n) ? n : null;
    }

    Guest? FindGuest(string pid)
    {
        if (!Pid.IsGuest(pid)) return null;
        lock (_lock) return _guests.TryGetValue(long.Parse(pid[2..], CultureInfo.InvariantCulture), out var g) ? g : null;
    }

    public string Name(string pid) =>
        Pid.IsUser(pid) ? AccountNick(pid[2..]) ?? pid[2..] : FindGuest(pid)?.Name ?? "?";

    public string Canon(string pid) => FindGuest(pid)?.LinkedTo ?? pid;

    public bool Exists(string pid) => Pid.IsUser(pid) ? AccountNick(pid[2..]) is not null : FindGuest(pid) is not null;

    public PadelPlayer Player(string pid) => new(pid, Name(pid), Pid.IsGuest(pid), FindGuest(pid)?.LinkedTo);

    /// <summary>P-об'єкт на дроті.</summary>
    public object P(string pid) => new { pid, name = Name(pid), guest = Pid.IsGuest(pid) };

    /// <summary>Усі акаунти сайту за ім'ям — для вибору гравців.</summary>
    public IReadOnlyList<PadelPlayer> Accounts()
    {
        LoadAccounts();
        lock (_lock)
            return [.. _accounts.Select(a => new PadelPlayer("u:" + a.Key, a.Value, false))
                .OrderBy(p => p.Name, StringComparer.Create(CultureInfo.GetCultureInfo("uk-UA"), true))];
    }

    public IReadOnlyList<PadelPlayer> Guests()
    {
        lock (_lock) return [.. _guests.Values.Select(g => new PadelPlayer(Pid.Guest(g.Id), g.Name, true, g.LinkedTo))];
    }

    /// <summary>Вписати гостя за ім'ям. Такий самий неприв'язаний уже є — він і є.</summary>
    public PadelReply AddGuest(string? raw, string by)
    {
        var name = Auth.CleanNick(raw);
        if (name.Length < Auth.NickMin || name.Length > Auth.NickMax) return PadelReply.No("Ім'я — від 2 до 24 символів");
        if (AccountNick(Auth.NickKey(name)) is not null) return PadelReply.No("Це акаунт — обери його зі списку");
        var key = Auth.NickKey(name);
        lock (_lock)
        {
            var same = _guests.Values.FirstOrDefault(g => g.LinkedTo is null && Auth.NickKey(g.Name) == key);
            if (same is not null) return new(new { ok = true, player = Player(Pid.Guest(same.Id)) });
            var id = _db.With(c =>
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "INSERT INTO padel_guests(name, name_key, created_at, created_by) VALUES($n, $k, $at, $by); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$at", _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$by", by);
                return (long)cmd.ExecuteScalar()!;
            });
            _guests[id] = new(id, name, null);
            return new(new { ok = true, player = new PadelPlayer(Pid.Guest(id), name, true) });
        }
    }

    /// <summary>Прив'язати гостя до акаунта (null — відв'язати). Права перевіряє виклик.</summary>
    public PadelReply Link(string? guestPid, string? accountPid)
    {
        var g = guestPid is null ? null : FindGuest(guestPid);
        if (g is null) return PadelReply.No("Нема такого гостя", 404);
        if (accountPid is not null && !Exists(accountPid)) return PadelReply.No("Нема такого акаунта", 404);
        _db.With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE padel_guests SET linked_to = $l WHERE id = $id";
            cmd.Parameters.AddWithValue("$l", (object?)accountPid ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", g.Id);
            return cmd.ExecuteNonQuery();
        });
        lock (_lock) _guests[g.Id] = g with { LinkedTo = accountPid };
        return new(new { ok = true, player = Player(guestPid!) });
    }
}
