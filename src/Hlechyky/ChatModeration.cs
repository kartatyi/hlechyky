using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Hlechyky.Games;
using Microsoft.Data.Sqlite;

namespace Hlechyky;

/// <summary>Прибрана з Балачок репліка: чия, що там було й чи був файл (його, може, теж час стерти з диска).</summary>
public sealed record DeletedChat(long Id, string Nick, string Text, string Kind, ChatFile? File);

/// <summary>
/// Обмеження людини в балачках: <see cref="ChatModeration.Mute"/> — не пише ніде, <see cref="ChatModeration.Media"/> —
/// без файлів. <paramref name="Until"/> null — поки адмін не зніме. <paramref name="Ip"/> — звідки людина заходила,
/// коли її обмежили: гість міняє нік як хоче, тож гостя з тієї самої адреси це теж стосується.
/// </summary>
public sealed record ChatLimit(long Id, string Kind, string Nick, string? Ip, DateTimeOffset? Until, string By, DateTimeOffset At)
{
    public bool Active(DateTimeOffset now) => Until is not { } u || u > now;
}

/// <summary>Обмеження, як його бачать усі: хто, що й до коли (без адреси).</summary>
public sealed record ChatLimitView(string Kind, string Nick, DateTimeOffset? Until);

/// <summary>
/// Що зараз із Балачками — летить усім подією <c>chatMod</c>: 📌 закріплене, 🐢 повільний режим (<paramref name="SlowSec"/>
/// 0 — вимкнено), 🚫 файли всім і хто обмежений. <c>…Until</c> null при ввімкненому — поки адмін не зніме.
/// </summary>
public sealed record ChatModState(ChatMessage? Pinned, int SlowSec, DateTimeOffset? SlowUntil, bool MediaOff,
    DateTimeOffset? MediaUntil, IReadOnlyList<ChatLimitView> Limits);

/// <summary>Рядок журналу модерації: хто з адмінів що зробив.</summary>
public sealed record ChatModLogLine(long Id, DateTimeOffset At, string By, string Text);

/// <summary>Для вкладки «🛡 Модерація»: стан, усі обмеження з адресами й журнал.</summary>
public sealed record ChatModOverview(ChatModState State, IReadOnlyList<ChatLimit> Limits, IReadOnlyList<ChatModLogLine> Log);

/// <summary>
/// Що вийшло з дії адміна. <paramref name="Line"/> — рядок у Балачки для всіх («🔇 Олі заборонено писати на годину»),
/// <paramref name="Deleted"/> — які репліки прибрати з екранів, <paramref name="Changed"/> — стан змінився, розіслати
/// <c>chatMod</c>. <paramref name="Message"/> — що сказати самому адміну (помилку чи «прибрано 12»).
/// </summary>
public sealed record ModOutcome(bool Ok, string? Message = null, ChatMessage? Line = null, IReadOnlyList<long>? Deleted = null,
    bool Changed = false)
{
    public static ModOutcome Fail(string why) => new(false, why);
}

/// <summary>Відповідь браузеру адміна.</summary>
public sealed record ModReply(bool Ok, string? Message);

/// <summary>Таблиці модерації. DDL і SQL живуть тут, від <see cref="Db"/> — лише з'єднання на одну коротку операцію.</summary>
public sealed class ChatModStore
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS chat_limits(
            id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, nick TEXT NOT NULL, nick_key TEXT NOT NULL, ip TEXT,
            until TEXT, by_nick TEXT NOT NULL, created_at TEXT NOT NULL, lifted_at TEXT, lifted_by TEXT);
        CREATE INDEX IF NOT EXISTS ix_chat_limits_live ON chat_limits(lifted_at, until);
        CREATE TABLE IF NOT EXISTS chat_mod(key TEXT PRIMARY KEY, value TEXT NOT NULL, until TEXT);
        CREATE TABLE IF NOT EXISTS chat_modlog(
            id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, by_nick TEXT NOT NULL, text TEXT NOT NULL);
        """;
    const string LimitCols = "id, kind, nick, ip, until, by_nick, created_at";

    readonly Db _db;

    public ChatModStore(Db db)
    {
        _db = db;
        _db.With(c => { Exec(c, Schema); return 0; });
    }

    /// <summary>Чинні обмеження: не зняті й не прострочені.</summary>
    public List<ChatLimit> Live(DateTimeOffset now) => _db.With(c =>
    {
        using var cmd = Cmd(c, $"SELECT {LimitCols} FROM chat_limits WHERE lifted_at IS NULL AND (until IS NULL OR until > $now) ORDER BY id",
            ("$now", Iso(now)));
        using var r = cmd.ExecuteReader();
        var list = new List<ChatLimit>();
        while (r.Read())
            list.Add(new ChatLimit(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : Ts(r.GetString(4)), r.GetString(5), Ts(r.GetString(6))));
        return list;
    });

    /// <summary>Нове обмеження замість чинного того самого виду в того самого ніка.</summary>
    public long Add(string kind, string nick, string? ip, DateTimeOffset? until, string by, DateTimeOffset now) => _db.With(c =>
    {
        using var tx = c.BeginTransaction();
        Lift(c, tx, kind, Auth.NickKey(nick), by, now);
        using var cmd = Cmd(c, """
            INSERT INTO chat_limits(kind, nick, nick_key, ip, until, by_nick, created_at) VALUES($k, $n, $key, $ip, $u, $by, $now);
            SELECT last_insert_rowid();
            """, ("$k", kind), ("$n", nick), ("$key", Auth.NickKey(nick)), ("$ip", ip), ("$u", until is { } u ? Iso(u) : null),
            ("$by", by), ("$now", Iso(now)));
        cmd.Transaction = tx;
        var id = (long)cmd.ExecuteScalar()!;
        tx.Commit();
        return id;
    });

    /// <summary>Зняти обмеження виду <paramref name="kind"/> з ніка. Скільки зняли (0 — нічого й не було).</summary>
    public int Lift(string kind, string nick, string by, DateTimeOffset now) => _db.With(c => Lift(c, null, kind, Auth.NickKey(nick), by, now));

    static int Lift(SqliteConnection c, SqliteTransaction? tx, string kind, string key, string by, DateTimeOffset now)
    {
        using var cmd = Cmd(c, "UPDATE chat_limits SET lifted_at = $now, lifted_by = $by WHERE kind = $k AND nick_key = $key AND lifted_at IS NULL",
            ("$now", Iso(now)), ("$by", by), ("$k", kind), ("$key", key));
        cmd.Transaction = tx;
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Адреса, яку взнали вже після обмеження (людина зайшла пізніше).</summary>
    public void SetIp(long id, string ip) => _db.Exec("UPDATE chat_limits SET ip = $ip WHERE id = $id", ("$ip", ip), ("$id", id));

    /// <summary>Перемикач на всі Балачки (<c>slow</c>, <c>media</c>, <c>pin</c>): значення й до коли; null — вимкнено.</summary>
    public (string Value, DateTimeOffset? Until)? Get(string key) => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT value, until FROM chat_mod WHERE key = $k", ("$k", key));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.IsDBNull(1) ? null : Ts(r.GetString(1))) : ((string, DateTimeOffset?)?)null;
    });

    public void Set(string key, string value, DateTimeOffset? until) =>
        _db.Exec("INSERT INTO chat_mod(key, value, until) VALUES($k, $v, $u) ON CONFLICT(key) DO UPDATE SET value = $v, until = $u",
            ("$k", key), ("$v", value), ("$u", until is { } u ? Iso(u) : null));

    public void Clear(string key) => _db.Exec("DELETE FROM chat_mod WHERE key = $k", ("$k", key));

    public void Log(string by, string text, DateTimeOffset now) =>
        _db.Exec("INSERT INTO chat_modlog(at, by_nick, text) VALUES($at, $by, $t)", ("$at", Iso(now)), ("$by", by), ("$t", text));

    public List<ChatModLogLine> RecentLog(int n) => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT id, at, by_nick, text FROM chat_modlog ORDER BY id DESC LIMIT $n", ("$n", n));
        using var r = cmd.ExecuteReader();
        var list = new List<ChatModLogLine>();
        while (r.Read()) list.Add(new ChatModLogLine(r.GetInt64(0), Ts(r.GetString(1)), r.GetString(2), r.GetString(3)));
        return list;
    });

    static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    static DateTimeOffset Ts(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    static SqliteCommand Cmd(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = Cmd(c, sql);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// Адмін і Балачки: прибрати репліку чи все, що людина написала (🧹), заборонити людині писати (🔇) чи кидати файли
/// (🚫) на строк, вимкнути файли всім, 🐢 повільний режим і 📌 закріплене повідомлення. Обмеження й перемикачі лежать
/// у базі (переживають перезапуск), а перевірка на кожну репліку йде з пам'яті.
/// <para>
/// Заборона писати діє скрізь, де людина говорить: Балачки, файли, балачка столу, присвята з Лавки, агенти через /mcp.
/// Повільний режим і файли — лише загальні Балачки. Адміна це все не стосується. Гостя впізнаємо ще й за адресою:
/// нік гість міняє одним кліком. Акаунт — лише за ніком: за одною адресою можуть сидіти двоє друзів.
/// </para>
/// </summary>
public sealed class ChatModeration
{
    public const string Mute = "mute";
    public const string Media = "media";
    public const string NotAdmin = "Це вміє лише адмін";
    /// <summary>Скільки секунд між репліками в повільному режимі можна поставити.</summary>
    public const int SlowMin = 5, SlowMax = 3600;
    const int LogLines = 150;

    readonly ChatModStore _store;
    readonly Db _db;
    readonly IClock _clock;
    readonly ChatFiles? _files;
    readonly ILogger<ChatModeration>? _log;

    readonly object _gate = new();
    List<ChatLimit> _limits;
    int _slowSec;
    DateTimeOffset? _slowUntil;
    bool _mediaOff;
    DateTimeOffset? _mediaUntil;
    long _pin;
    /// <summary>Остання адреса, з якої заходив нік (ключ — <see cref="Auth.NickKey"/>).</summary>
    readonly ConcurrentDictionary<string, string> _ips = new();
    /// <summary>Коли нік востаннє говорив у Балачках — для повільного режиму.</summary>
    readonly Dictionary<string, DateTimeOffset> _said = new();

    public ChatModeration(ChatModStore store, Db db, IClock clock, ChatFiles? files = null, ILogger<ChatModeration>? log = null)
    {
        (_store, _db, _clock, _files, _log) = (store, db, clock, files, log);
        var now = clock.UtcNow;
        _limits = store.Live(now);
        if (store.Get("slow") is { } slow && int.TryParse(slow.Value, CultureInfo.InvariantCulture, out var sec) && Alive(slow.Until, now))
            (_slowSec, _slowUntil) = (sec, slow.Until);
        if (store.Get("media") is { } media && Alive(media.Until, now)) (_mediaOff, _mediaUntil) = (true, media.Until);
        if (store.Get("pin") is { } pin && long.TryParse(pin.Value, CultureInfo.InvariantCulture, out var id)) _pin = id;
    }

    static bool Alive(DateTimeOffset? until, DateTimeOffset now) => until is not { } u || u > now;

    // =========================================================================================
    // Перевірки — на кожну репліку
    // =========================================================================================

    /// <summary>
    /// Запам'ятати, звідки заходить нік: якщо його обмежили, коли адреса була ще невідома (після перезапуску), — вона
    /// тепер лягає й до обмеження. Петля (сам сервер, агенти) нічого не каже — її не пишемо.
    /// </summary>
    public void NoteIp(string nick, string? ip)
    {
        if (string.IsNullOrEmpty(nick) || !Remote(ip)) return;
        var key = Auth.NickKey(nick);
        _ips[key] = ip!;
        List<ChatLimit> fill;
        lock (_gate)
        {
            fill = _limits.Where(l => l.Ip is null && Auth.NickKey(l.Nick) == key).ToList();
            if (fill.Count == 0) return;
            _limits = [.. _limits.Select(l => fill.Contains(l) ? l with { Ip = ip } : l)];
        }
        foreach (var l in fill)
            try { _store.SetIp(l.Id, ip!); }
            catch (SqliteException ex) { _log?.LogWarning(ex, "модерація: не записалась адреса для {Nick}", nick); }
    }

    static bool Remote(string? ip) => !string.IsNullOrEmpty(ip) && ip != "?" && IPAddress.TryParse(ip, out var a) && !IPAddress.IsLoopback(a);

    /// <summary>Чи можна цьому ніку говорити (null — так). <paramref name="account"/> false — гість: його ловимо й за адресою.</summary>
    public string? WriteRefusal(string nick, bool account, string? ip)
    {
        var l = Find(Mute, nick, account, ip);
        return l is null ? null : "🔇 Адмін заборонив тобі писати " + UntilText(l.Until, _clock.UtcNow, "поки не зніме");
    }

    /// <summary>Чи можна цьому ніку кинути файл (null — так): своя заборона чи файли вимкнено всім.</summary>
    public string? MediaRefusal(string nick, bool account, string? ip)
    {
        var now = _clock.UtcNow;
        if (Find(Media, nick, account, ip) is { } l) return "🚫 Адмін заборонив тобі кидати файли " + UntilText(l.Until, now, "поки не зніме");
        lock (_gate)
        {
            Expire(now);
            if (_mediaOff) return "🚫 Файли в Балачках вимкнено " + UntilText(_mediaUntil, now, "поки адмін не ввімкне");
        }
        return null;
    }

    /// <summary>🐢 Повільний режим: null — можна (і репліку врахуй через <see cref="NoteSaid"/>), інакше скільки ще чекати.</summary>
    public string? SlowRefusal(string nick)
    {
        var now = _clock.UtcNow;
        lock (_gate)
        {
            Expire(now);
            if (_slowSec <= 0 || !_said.TryGetValue(Auth.NickKey(nick), out var last)) return null;
            var wait = TimeSpan.FromSeconds(_slowSec) - (now - last);
            return wait <= TimeSpan.Zero ? null : $"🐢 Повільний режим: наступне — через {Math.Ceiling(wait.TotalSeconds):0} с";
        }
    }

    /// <summary>Репліка в Балачках пішла — для повільного режиму.</summary>
    public void NoteSaid(string nick)
    {
        var now = _clock.UtcNow;
        lock (_gate)
        {
            if (_said.Count > 500)
                foreach (var k in _said.Where(p => now - p.Value > TimeSpan.FromSeconds(SlowMax)).Select(p => p.Key).ToList()) _said.Remove(k);
            _said[Auth.NickKey(nick)] = now;
        }
    }

    ChatLimit? Find(string kind, string nick, bool account, string? ip)
    {
        var now = _clock.UtcNow;
        var key = Auth.NickKey(nick);
        var byIp = !account && Remote(ip);
        lock (_gate)
        {
            Expire(now);
            return _limits.FirstOrDefault(l => l.Kind == kind && (Auth.NickKey(l.Nick) == key || byIp && l.Ip == ip));
        }
    }

    /// <summary>Прострочене — геть із пам'яті (у базі воно й так уже не чинне). Під замком.</summary>
    void Expire(DateTimeOffset now)
    {
        if (_limits.Any(l => !l.Active(now))) _limits = [.. _limits.Where(l => l.Active(now))];
        if (_slowSec > 0 && !Alive(_slowUntil, now)) (_slowSec, _slowUntil) = (0, null);
        if (_mediaOff && !Alive(_mediaUntil, now)) (_mediaOff, _mediaUntil) = (false, null);
    }

    // =========================================================================================
    // Стан — усім
    // =========================================================================================

    public ChatModState State()
    {
        var now = _clock.UtcNow;
        long pin;
        int slow;
        DateTimeOffset? slowUntil, mediaUntil;
        bool media;
        List<ChatLimitView> views;
        lock (_gate)
        {
            Expire(now);
            (pin, slow, slowUntil, media, mediaUntil) = (_pin, _slowSec, _slowUntil, _mediaOff, _mediaUntil);
            views = [.. _limits.Select(l => new ChatLimitView(l.Kind, l.Nick, l.Until))];
        }
        var pinned = pin > 0 ? _db.ChatById(pin) : null;
        return new ChatModState(pinned, slow, slowUntil, media, mediaUntil, views);
    }

    public ChatModOverview Overview()
    {
        var state = State();
        List<ChatLimit> limits;
        lock (_gate) limits = [.. _limits];
        return new ChatModOverview(state, limits, _store.RecentLog(LogLines));
    }

    // =========================================================================================
    // Дії адміна
    // =========================================================================================

    /// <summary>🔇 / 🚫 людині на <paramref name="minutes"/> хвилин (0 і менше — поки не зняти).</summary>
    public ModOutcome Limit(string by, string? nick, string? kind, int minutes)
    {
        nick = (nick ?? "").Trim();
        if (kind is not (Mute or Media)) return ModOutcome.Fail("Не знаю такого обмеження");
        if (nick.Length == 0 || nick.Length > 64) return ModOutcome.Fail("Кого?");
        if (Auth.NickKey(nick) == Auth.NickKey(by)) return ModOutcome.Fail("Себе не обмежиш");
        var now = _clock.UtcNow;
        DateTimeOffset? until = minutes > 0 ? now.AddMinutes(Math.Min(minutes, 366 * 24 * 60)) : null;
        _ips.TryGetValue(Auth.NickKey(nick), out var ip);
        var id = _store.Add(kind, nick, ip, until, by, now);
        lock (_gate)
        {
            var key = Auth.NickKey(nick);
            _limits = [.. _limits.Where(l => !(l.Kind == kind && Auth.NickKey(l.Nick) == key)), new ChatLimit(id, kind, nick, ip, until, by, now)];
        }
        var what = kind == Mute ? "писати" : "кидати файли";
        var span = minutes > 0 ? "на " + Span(minutes) : "— поки адмін не зніме";
        var icon = kind == Mute ? "🔇" : "🚫";
        _store.Log(by, $"{icon} заборонив {NickCases.Dative(nick)} {what} {span}", now);
        return new(true, Line: Say(by, $"{icon} {NickCases.Dative(nick)} заборонено {what} {span}"), Changed: true);
    }

    /// <summary>Зняти з людини 🔇 чи 🚫 раніше строку.</summary>
    public ModOutcome Lift(string by, string? nick, string? kind)
    {
        nick = (nick ?? "").Trim();
        if (kind is not (Mute or Media)) return ModOutcome.Fail("Не знаю такого обмеження");
        var now = _clock.UtcNow;
        var key = Auth.NickKey(nick);
        ChatLimit? was;
        lock (_gate)
        {
            Expire(now);
            was = _limits.FirstOrDefault(l => l.Kind == kind && Auth.NickKey(l.Nick) == key);
            if (was is not null) _limits = [.. _limits.Where(l => l != was)];
        }
        _store.Lift(kind, nick, by, now);
        if (was is null) return ModOutcome.Fail("Цю людину й так нічого не стримує");
        var name = NickCases.Dative(was.Nick);
        var (icon, what) = kind == Mute ? ("🔊", "писати") : ("📎", "кидати файли");
        _store.Log(by, $"{icon} дозволив {name} знову {what}", now);
        return new(true, Line: Say(by, $"{icon} {name} знову можна {what}"), Changed: true);
    }

    /// <summary>🚫 Файли всім: <paramref name="minutes"/> &gt; 0 — на строк, 0 — поки не ввімкнути, &lt; 0 — увімкнути.</summary>
    public ModOutcome MediaAll(string by, int minutes)
    {
        var now = _clock.UtcNow;
        if (minutes < 0)
        {
            bool was;
            lock (_gate) { Expire(now); was = _mediaOff; (_mediaOff, _mediaUntil) = (false, null); }
            _store.Clear("media");
            if (!was) return ModOutcome.Fail("Файли й так можна");
            _store.Log(by, "📎 увімкнув файли в Балачках", now);
            return new(true, Line: Say(by, "📎 Файли в Балачках знову можна"), Changed: true);
        }
        DateTimeOffset? until = minutes > 0 ? now.AddMinutes(Math.Min(minutes, 366 * 24 * 60)) : null;
        _store.Set("media", "off", until);
        lock (_gate) (_mediaOff, _mediaUntil) = (true, until);
        var span = minutes > 0 ? "на " + Span(minutes) : "— поки адмін не ввімкне";
        _store.Log(by, $"🚫 вимкнув файли всім {span}", now);
        return new(true, Line: Say(by, $"🚫 Файли в Балачках вимкнено {span}"), Changed: true);
    }

    /// <summary>🐢 Повільний режим: <paramref name="seconds"/> між репліками на <paramref name="minutes"/> (0 — поки не вимкнути); seconds ≤ 0 — вимкнути.</summary>
    public ModOutcome Slow(string by, int seconds, int minutes)
    {
        var now = _clock.UtcNow;
        if (seconds <= 0)
        {
            bool was;
            lock (_gate) { Expire(now); was = _slowSec > 0; (_slowSec, _slowUntil) = (0, null); }
            _store.Clear("slow");
            if (!was) return ModOutcome.Fail("Повільний режим і так вимкнено");
            _store.Log(by, "🐇 вимкнув повільний режим", now);
            return new(true, Line: Say(by, "🐇 Повільний режим вимкнено — пишіть скільки влізе"), Changed: true);
        }
        seconds = Math.Clamp(seconds, SlowMin, SlowMax);
        DateTimeOffset? until = minutes > 0 ? now.AddMinutes(Math.Min(minutes, 366 * 24 * 60)) : null;
        _store.Set("slow", seconds.ToString(CultureInfo.InvariantCulture), until);
        lock (_gate) (_slowSec, _slowUntil) = (seconds, until);
        var span = minutes > 0 ? "на " + Span(minutes) : "поки адмін не вимкне";
        _store.Log(by, $"🐢 повільний режим: раз на {Gap(seconds)}, {span}", now);
        return new(true, Line: Say(by, $"🐢 Повільний режим: одне повідомлення на {Gap(seconds)} — {span}"), Changed: true);
    }

    /// <summary>📌 Закріпити репліку (<paramref name="id"/> ≤ 0 — відкріпити).</summary>
    public ModOutcome Pin(string by, long id)
    {
        var now = _clock.UtcNow;
        if (id <= 0)
        {
            long was;
            lock (_gate) { was = _pin; _pin = 0; }
            _store.Clear("pin");
            if (was <= 0) return ModOutcome.Fail("Нічого й не закріплено");
            _store.Log(by, "📌 відкріпив повідомлення", now);
            return new(true, Changed: true);
        }
        if (_db.ChatById(id) is not { } m) return ModOutcome.Fail("Цього повідомлення вже нема");
        if (m.Kind == "mod") return ModOutcome.Fail("Рядок про обмеження не закріплюють");
        _store.Set("pin", id.ToString(CultureInfo.InvariantCulture), null);
        lock (_gate) _pin = id;
        _store.Log(by, $"📌 закріпив повідомлення {NickCases.Genitive(m.Nick)}: «{Short(m)}»", now);
        return new(true, Changed: true);
    }

    /// <summary>🗑 Прибрати одну репліку з Балачок.</summary>
    public ModOutcome Delete(string by, long id)
    {
        if (_db.DeleteChat(id, by) is not { } gone) return ModOutcome.Fail("Цього повідомлення вже нема");
        _store.Log(by, $"🗑 прибрав повідомлення {NickCases.Genitive(gone.Nick)}: «{Short(gone.Text, gone.File)}»", _clock.UtcNow);
        return Gone([gone]);
    }

    /// <summary>🧹 Прибрати все, що людина написала за останні <paramref name="hours"/> годин (0 і менше — за весь час).</summary>
    public ModOutcome Clean(string by, string? nick, int hours)
    {
        nick = (nick ?? "").Trim();
        if (nick.Length == 0) return ModOutcome.Fail("Кого?");
        var now = _clock.UtcNow;
        var gone = _db.DeleteChatBy(nick, hours > 0 ? now.AddHours(-hours) : null, by);
        if (gone.Count == 0) return ModOutcome.Fail("Прибирати нема чого");
        var span = hours <= 0 ? "за весь час" : hours <= 1 ? "за годину" : hours == 24 ? "за добу" : $"за {hours} год";
        _store.Log(by, $"🧹 прибрав {NickCases.Genitive(nick)} {Lines(gone.Count)} {span}", now);
        return Gone(gone) with { Message = $"Прибрано {Lines(gone.Count)}" };
    }

    /// <summary>Прибране — з екранів, файли — з диска (якщо ніде більше не лежать), закріплене — відкріпити.</summary>
    ModOutcome Gone(IReadOnlyList<DeletedChat> gone)
    {
        var changed = false;
        lock (_gate)
            if (_pin > 0 && gone.Any(g => g.Id == _pin)) { _pin = 0; changed = true; }
        if (changed) _store.Clear("pin");
        foreach (var hash in gone.Select(g => g.File?.Hash).OfType<string>().Distinct())
            if (!_db.ChatFileInUse(hash)) _files?.Drop(hash);
        return new(true, Deleted: [.. gone.Select(g => g.Id)], Changed: changed);
    }

    ChatMessage Say(string by, string text) => _db.AddChat(by, text, "mod");

    // =========================================================================================
    // Слова
    // =========================================================================================

    /// <summary>«10 хв», «годину», «3 год», «добу», «тиждень», «2 дн.» — після «на».</summary>
    public static string Span(int minutes) => minutes switch
    {
        60 => "годину",
        1440 => "добу",
        10080 => "тиждень",
        < 60 => $"{minutes} хв",
        _ when minutes % 1440 == 0 => $"{minutes / 1440} дн.",
        _ when minutes % 60 == 0 => $"{minutes / 60} год",
        _ => $"{minutes} хв",
    };

    static string Gap(int seconds) => seconds % 60 == 0 ? $"{seconds / 60} хв" : $"{seconds} с";

    /// <summary>«до 21:30», «до 12.10 21:30» (київський час) або «— <paramref name="forever"/>».</summary>
    public static string UntilText(DateTimeOffset? until, DateTimeOffset now, string forever)
    {
        if (until is not { } u) return "— " + forever;
        var local = TimeZoneInfo.ConvertTime(u, Days.Kyiv);
        var today = TimeZoneInfo.ConvertTime(now, Days.Kyiv).Date;
        return local.Date == today ? $"до {local:HH:mm}" : $"до {local:dd.MM HH:mm}";
    }

    /// <summary>«1 повідомлення», «3 повідомлення», «12 повідомлень».</summary>
    static string Lines(int n) => n % 10 is >= 1 and <= 4 && n % 100 is < 11 or > 14 ? $"{n} повідомлення" : $"{n} повідомлень";

    static string Short(ChatMessage m) => Short(m.Text, m.File);

    static string Short(string text, ChatFile? file)
    {
        var t = text.Length == 0 && file is { } f ? "📎 " + f.Name : text;
        return t.Length <= 80 ? t : t[..(char.IsHighSurrogate(t[79]) ? 79 : 80)] + "…";
    }
}
