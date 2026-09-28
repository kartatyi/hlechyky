using System.Globalization;
using Hlechyky.Games;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hlechyky;

/// <summary>
/// «💡 Розробнику»: що додати, що змінити, що зламалось — від тих, хто сайтом користується, тому, хто його робить.
/// Кожна записка — це переписка: людина й розробник пишуть по черзі, скільки треба (<see cref="FeedbackMsg"/>).
/// Кожен бачить свої записки, адмін — усі разом, з фільтром за станом. Раніше пропозиції губились у Балачках між
/// кубиками й «добраніч», а потім — в одному рядку відповіді, на який людина не могла відписати.
/// <para>
/// <paramref name="Reply"/> — стара одна відповідь розробника. Лишилась у схемі заради старих баз: при старті вона
/// один раз стає першим повідомленням переписки, а нове туди вже не пишеться. <paramref name="AuthorRead"/> і
/// <paramref name="DevRead"/> — id останнього повідомлення, яке бачили автор і розробник: усе новіше — непрочитане.
/// </para>
/// </summary>
public sealed record FeedbackItem(long Id, string Nick, string Kind, string Text, string? Place, string? Screen, string? Ua,
    string Status, string? Reply, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long AuthorRead = 0, long DevRead = 0);

/// <summary>
/// Одне повідомлення переписки в записці. <paramref name="Dev"/> — від розробника (адміна), інакше — від автора.
/// <paramref name="Kind"/>: <c>text</c> — те, що написали; <c>status</c> — розробник переставив стан, і
/// <paramref name="Text"/> тоді — ключ стану (<c>done</c>…): людина бачить це в переписці й на 💡, як відповідь.
/// </summary>
public sealed record FeedbackMsg(long Id, long FeedbackId, string Nick, bool Dev, string Kind, string Text, DateTimeOffset At);

/// <summary>
/// Що чекає розробника: <paramref name="New"/> — нових записок, <paramref name="Replies"/> — записок, де людина
/// відписала, а розробник ще не бачив. <paramref name="Count"/> — скільки записок разом (одна може бути і тим, і тим).
/// </summary>
public sealed record FeedbackCount(int Count, int New, int Replies);

/// <summary>Таблиці записок і переписки. DDL і SQL живуть тут, від <see cref="Db"/> — лише з'єднання на одну коротку операцію.</summary>
public sealed class FeedbackStore
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS feedback(
            id INTEGER PRIMARY KEY AUTOINCREMENT, nick TEXT NOT NULL, nick_key TEXT NOT NULL, kind TEXT NOT NULL,
            text TEXT NOT NULL, place TEXT, screen TEXT, ua TEXT, status TEXT NOT NULL DEFAULT 'new', reply TEXT,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_feedback_nick ON feedback(nick_key, id);
        CREATE INDEX IF NOT EXISTS ix_feedback_status ON feedback(status, id);
        CREATE TABLE IF NOT EXISTS feedback_msg(
            id INTEGER PRIMARY KEY AUTOINCREMENT, feedback_id INTEGER NOT NULL, nick TEXT NOT NULL, nick_key TEXT NOT NULL,
            dev INTEGER NOT NULL DEFAULT 0, kind TEXT NOT NULL DEFAULT 'text', text TEXT NOT NULL, created_at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_feedback_msg_note ON feedback_msg(feedback_id, id);
        CREATE INDEX IF NOT EXISTS ix_feedback_msg_nick ON feedback_msg(nick_key, created_at);
        """;
    const string Cols = "id, nick, kind, text, place, screen, ua, status, reply, created_at, updated_at, author_read, dev_read";
    const string MsgCols = "id, feedback_id, nick, dev, kind, text, created_at";

    /// <summary>Непрочитане автором: є повідомлення розробника, новіше за те, що автор бачив.</summary>
    const string AuthorUnreadSql = "EXISTS(SELECT 1 FROM feedback_msg m WHERE m.feedback_id = f.id AND m.dev = 1 AND m.id > f.author_read)";
    /// <summary>Непрочитане розробником: автор відписав, а розробник того ще не бачив.</summary>
    const string DevUnreadSql = "EXISTS(SELECT 1 FROM feedback_msg m WHERE m.feedback_id = f.id AND m.dev = 0 AND m.id > f.dev_read)";

    /// <summary>
    /// Вересень 2026: одна відповідь розробника (<c>feedback.reply</c>) стає першим повідомленням переписки — з тим
    /// часом, коли її написали (<c>updated_at</c>). Рядок за рядком «якщо такого повідомлення ще нема», а не разовою
    /// позначкою: повторний старт нічого не задвоїть, а відповідь, яку дописав старий сервер (відкат деплою), теж
    /// перейде, коли повернеться новий. Сюди нове вже не пишеться, тож рахувати нема чого. Непрочитаною для автора
    /// вона стає навмисно — 💡 покаже, що тепер на неї можна відписати.
    /// </summary>
    const string ReplyToMsgSql = """
        INSERT INTO feedback_msg(feedback_id, nick, nick_key, dev, kind, text, created_at)
        SELECT f.id, 'Розробник', 'розробник', 1, 'text', f.reply, f.updated_at FROM feedback f
        WHERE f.reply IS NOT NULL AND trim(f.reply) <> ''
          AND NOT EXISTS(SELECT 1 FROM feedback_msg m WHERE m.feedback_id = f.id AND m.dev = 1 AND m.text = f.reply)
        ORDER BY f.id;
        """;

    readonly Db _db;

    public FeedbackStore(Db db)
    {
        _db = db;
        _db.With(c =>
        {
            Exec(c, Schema);
            // Бази, створені до переписки: хто що бачив — з нуля, тобто «ще нічого».
            AddColumn(c, "author_read", "INTEGER NOT NULL DEFAULT 0");
            AddColumn(c, "dev_read", "INTEGER NOT NULL DEFAULT 0");
            Exec(c, ReplyToMsgSql);
            return 0;
        });
    }

    static void AddColumn(SqliteConnection c, string name, string type)
    {
        using (var info = Cmd(c, "SELECT 1 FROM pragma_table_info('feedback') WHERE name = $n", ("$n", name)))
            if (info.ExecuteScalar() is not null) return;
        Exec(c, $"ALTER TABLE feedback ADD COLUMN {name} {type}");
    }

    public long Add(string nick, string kind, string text, string? place, string? screen, string? ua, DateTimeOffset now) => _db.With(c =>
    {
        using var cmd = Cmd(c, """
            INSERT INTO feedback(nick, nick_key, kind, text, place, screen, ua, status, created_at, updated_at)
            VALUES($n, $k, $kind, $t, $p, $s, $ua, 'new', $now, $now);
            SELECT last_insert_rowid();
            """, ("$n", nick), ("$k", Auth.NickKey(nick)), ("$kind", kind), ("$t", text), ("$p", place), ("$s", screen), ("$ua", ua), ("$now", Iso(now)));
        return (long)cmd.ExecuteScalar()!;
    });

    /// <summary>Скільки записок нік залишив, починаючи з <paramref name="since"/> — для обмеження частоти.</summary>
    public int CountSince(string nick, DateTimeOffset since) => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT COUNT(*) FROM feedback WHERE nick_key = $k AND created_at >= $since",
            ("$k", Auth.NickKey(nick)), ("$since", Iso(since)));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>Та сама записка того самого ніка, залишена недавно (подвійний клік, «а чи дійшло?»).</summary>
    public bool Recent(string nick, string text, DateTimeOffset since) => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT 1 FROM feedback WHERE nick_key = $k AND text = $t AND created_at >= $since LIMIT 1",
            ("$k", Auth.NickKey(nick)), ("$t", text), ("$since", Iso(since)));
        return cmd.ExecuteScalar() is not null;
    });

    /// <summary>Свої записки — свіжі розмови згори: відповідь розробника піднімає записку, як у месенджері.</summary>
    public List<FeedbackItem> Of(string nick, int limit) => _db.With(c =>
        Read(c, $"SELECT {Cols} FROM feedback WHERE nick_key = $k ORDER BY updated_at DESC, id DESC LIMIT $n", ("$k", Auth.NickKey(nick)), ("$n", limit)));

    /// <summary>
    /// Усі записки для розробника, свіжі згори. Записка, де людина відписала, а розробник ще не бачив, потрапляє сюди
    /// за будь-якого фільтра й ліміту: інакше 💡 рахувала б те, чого у вкладці не знайти.
    /// </summary>
    public List<FeedbackItem> All(string? status, int limit) => _db.With(c => status is null
        ? Read(c, $"SELECT {Cols} FROM feedback f WHERE f.id IN (SELECT id FROM feedback ORDER BY id DESC LIMIT $n) OR {DevUnreadSql} ORDER BY id DESC", ("$n", limit))
        : Read(c, $"SELECT {Cols} FROM feedback f WHERE f.id IN (SELECT id FROM feedback WHERE status = $s ORDER BY id DESC LIMIT $n) OR {DevUnreadSql} ORDER BY id DESC",
            ("$s", status), ("$n", limit)));

    public FeedbackItem? Get(long id) => _db.With(c => Read(c, $"SELECT {Cols} FROM feedback WHERE id = $id", ("$id", id)).FirstOrDefault());

    public Dictionary<string, int> Counts() => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT status, COUNT(*) FROM feedback GROUP BY status");
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        while (r.Read()) map[r.GetString(0)] = r.GetInt32(1);
        return map;
    });

    public bool SetStatus(long id, string status, DateTimeOffset now) => _db.With(c =>
        Exec(c, "UPDATE feedback SET status = $s, updated_at = $now WHERE id = $id", ("$s", status), ("$now", Iso(now)), ("$id", id)) > 0);

    // ---------- переписка ----------

    /// <summary>
    /// Нове повідомлення в записку. Той, хто пише, бачить усю розмову до свого повідомлення включно — тож його бік
    /// одразу «прочитав»; записка піднімається (updated_at), як розмова в месенджері.
    /// </summary>
    public long AddMsg(long feedbackId, string nick, bool dev, string kind, string text, DateTimeOffset now) => _db.With(c =>
    {
        using var tx = c.BeginTransaction();
        long id;
        using (var cmd = Cmd(c, """
            INSERT INTO feedback_msg(feedback_id, nick, nick_key, dev, kind, text, created_at) VALUES($f, $n, $k, $d, $kind, $t, $now);
            SELECT last_insert_rowid();
            """, ("$f", feedbackId), ("$n", nick), ("$k", Auth.NickKey(nick)), ("$d", dev ? 1 : 0), ("$kind", kind), ("$t", text), ("$now", Iso(now))))
            id = (long)cmd.ExecuteScalar()!;
        Exec(c, $"UPDATE feedback SET updated_at = $now, {(dev ? "dev_read" : "author_read")} = $m WHERE id = $f",
            ("$now", Iso(now)), ("$m", id), ("$f", feedbackId));
        tx.Commit();
        return id;
    });

    /// <summary>Переписка кількох записок за раз: адміну їх до трьохсот, і по запиту на кожну — забагато.</summary>
    public Dictionary<long, List<FeedbackMsg>> Msgs(IReadOnlyCollection<long> ids) => _db.With(c =>
    {
        var map = new Dictionary<long, List<FeedbackMsg>>();
        if (ids.Count == 0) return map;
        var names = ids.Select((_, i) => "$i" + i).ToArray();
        using var cmd = Cmd(c, $"SELECT {MsgCols} FROM feedback_msg WHERE feedback_id IN ({string.Join(',', names)}) ORDER BY id",
            ids.Select((id, i) => (names[i], (object?)id)).ToArray());
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var m = new FeedbackMsg(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3) != 0, r.GetString(4), r.GetString(5), Ts(r.GetString(6)));
            if (!map.TryGetValue(m.FeedbackId, out var list)) map[m.FeedbackId] = list = [];
            list.Add(m);
        }
        return map;
    });

    /// <summary>Скільки повідомлень автор (не розробник) написав з <paramref name="since"/> — для обмеження частоти.</summary>
    public int MsgsSince(string nick, DateTimeOffset since) => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT COUNT(*) FROM feedback_msg WHERE nick_key = $k AND dev = 0 AND created_at >= $since",
            ("$k", Auth.NickKey(nick)), ("$since", Iso(since)));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>Те саме повідомлення в ту саму записку від того самого ніка недавно — подвійний Enter.</summary>
    public bool RecentMsg(long feedbackId, string nick, string text, DateTimeOffset since) => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT 1 FROM feedback_msg WHERE feedback_id = $f AND nick_key = $k AND text = $t AND created_at >= $since LIMIT 1",
            ("$f", feedbackId), ("$k", Auth.NickKey(nick)), ("$t", text), ("$since", Iso(since)));
        return cmd.ExecuteScalar() is not null;
    });

    /// <summary>Автор побачив свої записки (усі чи одну): усе, що в них є, — прочитане. Повертає, чи щось змінилось.</summary>
    public bool MarkAuthorRead(string nick, long? id) => _db.With(c =>
        Exec(c, $"""
            UPDATE feedback SET author_read = (SELECT MAX(m.id) FROM feedback_msg m WHERE m.feedback_id = feedback.id)
            WHERE nick_key = $k {(id is null ? "" : "AND id = $id")}
              AND author_read < (SELECT IFNULL(MAX(m.id), 0) FROM feedback_msg m WHERE m.feedback_id = feedback.id)
            """, ("$k", Auth.NickKey(nick)), ("$id", id)) > 0);

    /// <summary>Розробник побачив ці записки: відповіді людей у них — прочитані. Повертає, чи щось змінилось.</summary>
    public bool MarkDevRead(IReadOnlyCollection<long> ids) => ids.Count > 0 && _db.With(c =>
    {
        var names = ids.Select((_, i) => "$i" + i).ToArray();
        return Exec(c, $"""
            UPDATE feedback SET dev_read = (SELECT MAX(m.id) FROM feedback_msg m WHERE m.feedback_id = feedback.id)
            WHERE id IN ({string.Join(',', names)})
              AND dev_read < (SELECT IFNULL(MAX(m.id), 0) FROM feedback_msg m WHERE m.feedback_id = feedback.id)
            """, ids.Select((id, i) => (names[i], (object?)id)).ToArray()) > 0;
    });

    /// <summary>У скількох своїх записках автор ще не бачив нового від розробника.</summary>
    public int AuthorUnread(string nick) => _db.With(c =>
    {
        using var cmd = Cmd(c, $"SELECT COUNT(*) FROM feedback f WHERE f.nick_key = $k AND {AuthorUnreadSql}", ("$k", Auth.NickKey(nick)));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    public FeedbackCount DevCount() => _db.With(c =>
    {
        using var cmd = Cmd(c, $"""
            SELECT IFNULL(SUM(status = 'new' OR {DevUnreadSql}), 0), IFNULL(SUM(status = 'new'), 0), IFNULL(SUM({DevUnreadSql}), 0)
            FROM feedback f
            """);
        using var r = cmd.ExecuteReader();
        r.Read();
        return new FeedbackCount(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
    });

    static List<FeedbackItem> Read(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        using var r = cmd.ExecuteReader();
        var list = new List<FeedbackItem>();
        static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        while (r.Read())
            list.Add(new FeedbackItem(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), S(r, 4), S(r, 5), S(r, 6),
                r.GetString(7), S(r, 8), Ts(r.GetString(9)), Ts(r.GetString(10)), r.GetInt64(11), r.GetInt64(12)));
        return list;
    }

    static string Iso(DateTimeOffset t) => t.ToString("O", CultureInfo.InvariantCulture);
    static DateTimeOffset Ts(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    static SqliteCommand Cmd(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    static int Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        return cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// Хто про що дізнається одразу, без F5: автор — скільки в нього непрочитаних відповідей (кружечок на 💡), розробник —
/// скільки записок на нього чекає. Справжня — через хаб (<see cref="HubFeedbackWire"/>), у тестах — підставна.
/// </summary>
public interface IFeedbackWire
{
    void Author(string nick, int unread);
    void Dev(FeedbackCount count);
}

/// <summary>Правила записок: що можна написати, як часто, хто кому відповідає і що з того непрочитане.</summary>
public sealed class Feedback(FeedbackStore store, IClock clock, IFeedbackWire? wire = null)
{
    /// <summary>Що додати, що змінити, що зламалось.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["idea", "change", "bug"];
    /// <summary>нове → переглянуто → у планах → зроблено / не буде.</summary>
    public static readonly IReadOnlyList<string> Statuses = ["new", "seen", "planned", "done", "nope"];
    /// <summary>
    /// Стани, про які варто сказати людині: лягають рядком у переписку й світять 💡, як відповідь. «Переглянуто» —
    /// ні: його ставить сама відповідь розробника, і окремий рядок «переглянуто» під нею — шум.
    /// </summary>
    public static readonly IReadOnlyList<string> Told = ["planned", "done", "nope"];

    public const int MinText = 5;
    public const int MaxText = 2000;
    /// <summary>Повідомлення в переписці: коротке «так» — теж відповідь, а на довге є записка.</summary>
    public const int MaxMsg = 1000;
    /// <summary>Записок на годину й на добу з одного ніка: досить, щоб висловитись, і мало, щоб засипати розробника.</summary>
    public const int PerHour = 5;
    public const int PerDay = 20;
    /// <summary>Повідомлень у переписці на годину з одного ніка (розробника це не стосується): для розмови — досхочу.</summary>
    public const int MsgPerHour = 30;
    /// <summary>Той самий текст у ту саму записку частіше — подвійний Enter, а не друга думка.</summary>
    public static readonly TimeSpan MsgTwice = TimeSpan.FromSeconds(30);
    /// <summary>Скільки своїх записок показувати людині.</summary>
    public const int MineLimit = 30;
    public const int AdminLimit = 300;

    public sealed record Result(bool Ok, string Message, long Id = 0);

    /// <summary>Записка разом із перепискою.</summary>
    public sealed record Thread(FeedbackItem Item, IReadOnlyList<FeedbackMsg> Msgs);

    public Result Submit(string nick, string? kind, string? text, string? place, string? screen, string? ua)
    {
        // Голий «гість» — це ще ніхто: відповідь розробника не буде кому показати.
        if (Nameless(nick)) return new(false, "Спершу назвись — тоді розробник знатиме, кому відповісти");
        kind = (kind ?? "").Trim().ToLowerInvariant();
        if (!Kinds.Contains(kind)) return new(false, "Не зрозумів, що це: пропозиція, зміна чи баг?");
        text = Clean(text, MaxText + 1);
        if (text.Length < MinText) return new(false, "Тяпни трохи більше — хоч кілька слів");
        if (text.Length > MaxText) return new(false, $"Задовго: до {MaxText} символів. Розбий на дві записки");
        var now = clock.UtcNow;
        if (store.Recent(nick, text, now.AddMinutes(-30))) return new(false, "Це вже тяпнуто — розробник побачить");
        if (store.CountSince(nick, now.AddHours(-1)) >= PerHour) return new(false, "За годину досить записок — решту тяпнеш трохи згодом");
        if (store.CountSince(nick, now.AddDays(-1)) >= PerDay) return new(false, "На сьогодні записок досить — завтра продовжимо");
        var id = store.Add(nick, kind, text, Short(place, 200), Short(screen, 40), Short(ua, 300), now);
        wire?.Dev(DevCount());
        return new(true, kind == "bug" ? "Дякую! Баг записано — розробник погляне" : "Дякую! Розробник прочитає", id);
    }

    /// <summary>
    /// Повідомлення в переписку. Автор пише лише у свої записки, розробник (<paramref name="dev"/>) — у будь-яку.
    /// Стан від відповіді людини не міняється (у «зроблено» теж можна відписати «дякую»), а перша відповідь
    /// розробника на нову записку робить її «переглянутою» — у фільтрі «нові» лишається лише непрочитане.
    /// </summary>
    public Result Say(long id, string nick, bool dev, string? text)
    {
        if (!dev && Nameless(nick)) return new(false, "Спершу назвись — тоді й відповідай");
        if (store.Get(id) is not { } item) return new(false, "Такої записки нема");
        if (!dev && Auth.NickKey(nick) != Auth.NickKey(item.Nick)) return new(false, "Це не твоя записка — відповісти тут може лише її автор");
        var t = Clean(text, MaxMsg + 1);
        if (t.Length == 0) return new(false, "Порожнє не тяпнеш — напиши хоч слово");
        if (t.Length > MaxMsg) return new(false, $"Задовго: до {MaxMsg} символів. Решту — наступним повідомленням");
        var now = clock.UtcNow;
        if (store.RecentMsg(id, nick, t, now - MsgTwice)) return new(false, "Це вже тяпнуто");
        if (!dev && store.MsgsSince(nick, now.AddHours(-1)) >= MsgPerHour) return new(false, "За годину досить повідомлень — решту тяпнеш трохи згодом");
        store.AddMsg(id, nick, dev, "text", t, now);
        if (dev && item.Status == "new") store.SetStatus(id, "seen", now);
        if (dev) wire?.Author(item.Nick, Unread(item.Nick));
        wire?.Dev(DevCount());
        return new(true, dev ? "Відповідь пішла — автор побачить на 💡" : "Тяпнуто — розробник побачить", id);
    }

    /// <summary>Свої записки — людині (свіжі розмови згори); переписку до них дає <see cref="WithMsgs"/>.</summary>
    public List<FeedbackItem> Mine(string nick) => store.Of(nick, MineLimit);

    public (List<FeedbackItem> Items, Dictionary<string, int> Counts) List(string? status) =>
        (store.All(status is not null && Statuses.Contains(status) ? status : null, AdminLimit), store.Counts());

    public Thread? One(long id) => store.Get(id) is { } item ? WithMsgs([item])[0] : null;

    /// <summary>Записки разом із перепискою — одним запитом на всі.</summary>
    public List<Thread> WithMsgs(List<FeedbackItem> items)
    {
        var msgs = store.Msgs(items.Select(x => x.Id).ToList());
        return items.Select(x => new Thread(x, msgs.TryGetValue(x.Id, out var list) ? list : [])).ToList();
    }

    /// <summary>
    /// Стан (і, для старого клієнта, відповідь) від розробника. Відповідь тепер — просто повідомлення в переписку:
    /// старе поле <c>reply</c> більше не переписується. Порожня відповідь нічого не стирає — переписка лишається.
    /// </summary>
    public Result Update(long id, string? status, string? reply, string devNick = "Розробник")
    {
        if (store.Get(id) is not { } item) return new(false, "Такої записки нема");
        var s = string.IsNullOrWhiteSpace(status) ? item.Status : status.Trim().ToLowerInvariant();
        if (!Statuses.Contains(s)) return new(false, "Невідомий стан");
        if (reply is not null && Clean(reply, MaxMsg + 1).Length > 0)
        {
            var said = Say(id, devNick, true, reply);
            if (!said.Ok) return said;
            item = store.Get(id)!;
        }
        if (s == item.Status) return new(true, "Збережено", id);
        var now = clock.UtcNow;
        store.SetStatus(id, s, now);
        if (Told.Contains(s))
        {
            store.AddMsg(id, devNick, true, "status", s, now);
            wire?.Author(item.Nick, Unread(item.Nick));
        }
        wire?.Dev(DevCount());
        return new(true, "Збережено", id);
    }

    /// <summary>Людина відкрила свої записки (усі чи одну): нове від розробника — прочитане. Повертає, скільки ще лишилось.</summary>
    public int ReadMine(string nick, long? id)
    {
        if (Nameless(nick)) return 0;
        var changed = store.MarkAuthorRead(nick, id);
        var left = Unread(nick);
        // інші вкладки тієї ж людини теж гасять кружечок
        if (changed) wire?.Author(nick, left);
        return left;
    }

    /// <summary>Розробник побачив ці записки: відповіді людей у них — прочитані.</summary>
    public FeedbackCount ReadAsDev(IReadOnlyCollection<long> ids)
    {
        var changed = store.MarkDevRead(ids.Distinct().Take(AdminLimit).ToList());
        var count = DevCount();
        if (changed) wire?.Dev(count);
        return count;
    }

    /// <summary>У скількох своїх записках людина ще не бачила нового від розробника — число на 💡.</summary>
    public int Unread(string nick) => Nameless(nick) ? 0 : store.AuthorUnread(nick);

    /// <summary>Що чекає розробника: нові записки й записки, де людина відписала.</summary>
    public FeedbackCount DevCount() => store.DevCount();

    public int NewCount() => store.Counts().GetValueOrDefault("new");

    static bool Nameless(string nick) => string.IsNullOrWhiteSpace(nick) || Auth.NickKey(nick) == Auth.Guest;

    /// <summary>Без керівних символів (крім переносу рядка) і без зайвих порожніх рядків по краях.</summary>
    static string Clean(string? s, int max)
    {
        var t = new string((s ?? "").Where(ch => ch == '\n' || !char.IsControl(ch)).ToArray()).Trim();
        return t.Length > max ? t[..max] : t;
    }

    static string? Short(string? s, int max)
    {
        var t = Clean(s, max);
        if (t.Length == 0) return null;
        return t.Length <= max ? t : t[..max];
    }
}

/// <summary>
/// Розробник (адмін) — окрема група хабу, куди летить «на тебе чекає N записок». Хто адмін, знає кука на
/// підключенні, тож групу ставимо фільтром на вході, а не окремим викликом із браузера: так її не підробиш і не
/// забудеш після реконекту. Фільтр, а не рядок у RadioHub, — щоб усе про записки жило в цьому файлі.
/// </summary>
public sealed class FeedbackDevGroup(ILogger<FeedbackDevGroup> log) : IHubFilter
{
    public const string Name = "feedback-dev";

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        try
        {
            if (context.Context.GetHttpContext() is { } http && Auth.IsAdmin(http))
                await context.Hub.Groups.AddToGroupAsync(context.Context.ConnectionId, Name);
        }
        catch (Exception ex) { log.LogWarning(ex, "розробника не вписало в групу записок"); }
        await next(context);
    }
}

/// <summary>Кружечок на 💡 без F5: автору — на всі його вкладки, розробнику — у групу <see cref="FeedbackDevGroup"/>.</summary>
public sealed class HubFeedbackWire(IHubContext<RadioHub> hub, Presence presence, ILogger<HubFeedbackWire> log) : IFeedbackWire
{
    public void Author(string nick, int unread)
    {
        var ids = presence.ConnectionsOf(nick);
        if (ids.Count > 0) _ = SendAsync(hub.Clients.Clients(ids), "fbUnread", new { count = unread });
    }

    public void Dev(FeedbackCount n) =>
        _ = SendAsync(hub.Clients.Group(FeedbackDevGroup.Name), "fbDev", new { count = n.Count, @new = n.New, replies = n.Replies });

    async Task SendAsync(IClientProxy to, string name, object payload)
    {
        try { await to.SendAsync(name, payload); }
        catch (Exception ex) { log.LogWarning(ex, "записки не розіслали {Event}", name); }
    }
}

public static class FeedbackSetup
{
    public sealed record FeedbackBody(string? Kind, string? Text, string? Place, string? Screen, string? Ua);
    public sealed record FeedbackPatch(string? Status, string? Reply);
    public sealed record MsgBody(string? Text);
    public sealed record ReadBody(long? Id, long[]? Ids);

    public static IServiceCollection AddHlechykyFeedback(this IServiceCollection services)
    {
        services.AddSingleton<FeedbackStore>();
        services.TryAddSingleton<IFeedbackWire, HubFeedbackWire>();
        services.AddSingleton<Feedback>();
        // Одиничка в DI: інакше SignalR створював би фільтр наново на кожен виклик хабу (а коло кличе його щосекунди).
        services.AddSingleton<FeedbackDevGroup>();
        services.Configure<HubOptions>(o => o.AddFilter<FeedbackDevGroup>());
        return services;
    }

    /// <summary>
    /// Записка з перепискою. <c>fresh</c> — повідомлення від іншого боку, якого цей бік ще не бачив (підсвітити),
    /// <c>unread</c> — чи є таке в записці. Нік розробника людині ні до чого — там просто «Розробник».
    /// </summary>
    static object View(Feedback.Thread t, bool admin)
    {
        var x = t.Item;
        var read = admin ? x.DevRead : x.AuthorRead;
        bool Fresh(FeedbackMsg m) => m.Dev != admin && m.Id > read;
        return new
        {
            id = x.Id, nick = x.Nick, kind = x.Kind, text = x.Text, status = x.Status, reply = x.Reply,
            at = x.CreatedAt, updatedAt = x.UpdatedAt,
            // де й на чому — лише адміну: людині своє «Mozilla/5.0…» ні до чого
            place = admin ? x.Place : null, screen = admin ? x.Screen : null, ua = admin ? x.Ua : null,
            msgs = t.Msgs.Select(m => new
            {
                id = m.Id, dev = m.Dev, kind = m.Kind, text = m.Text, at = m.At, nick = admin || !m.Dev ? m.Nick : null, fresh = Fresh(m),
            }),
            unread = t.Msgs.Any(Fresh),
        };
    }

    static object DevView(FeedbackCount n) => new { count = n.Count, @new = n.New, replies = n.Replies };

    static IResult Fail(string message) => Results.BadRequest(new { ok = false, message });

    public static WebApplication MapHlechykyFeedback(this WebApplication app)
    {
        var api = app.MapGroup("/api/feedback");

        api.MapPost("", (HttpContext c, FeedbackBody b, Feedback fb) =>
        {
            var r = fb.Submit(Auth.Nick(c), b.Kind, b.Text, b.Place, b.Screen, b.Ua);
            return r.Ok ? Results.Ok(new { ok = true, id = r.Id, message = r.Message }) : Fail(r.Message);
        });

        api.MapGet("/mine", (HttpContext c, Feedback fb) =>
        {
            var nick = Auth.Nick(c);
            return Results.Ok(new { items = fb.WithMsgs(fb.Mine(nick)).Select(x => View(x, false)), unread = fb.Unread(nick) });
        });

        // Людина відкрила свої записки (або одну): нове від розробника — прочитане, кружечок на 💡 гасне.
        api.MapPost("/mine/read", (HttpContext c, ReadBody? b, Feedback fb) =>
            Results.Ok(new { ok = true, unread = fb.ReadMine(Auth.Nick(c), b?.Id) }));

        // Повідомлення в переписку: автор — у свою записку, розробник — у будь-яку. Віддає записку наново.
        api.MapPost("/{id:long}/msg", (HttpContext c, long id, MsgBody b, Feedback fb) =>
        {
            var admin = Auth.IsAdmin(c);
            var r = fb.Say(id, Auth.Nick(c), admin, b.Text);
            if (!r.Ok) return Fail(r.Message);
            return Results.Ok(new { ok = true, message = r.Message, item = fb.One(id) is { } t ? View(t, admin) : null });
        });

        api.MapGet("", (HttpContext c, string? status, Feedback fb) =>
        {
            if (!Auth.IsAdmin(c)) return Fail("Це бачить лише розробник");
            var (items, counts) = fb.List(status);
            return Results.Ok(new { items = fb.WithMsgs(items).Select(x => View(x, true)), counts, waiting = DevView(fb.DevCount()) });
        });

        // Розробник побачив ці записки: відповіді людей у них — прочитані.
        api.MapPost("/read", (HttpContext c, ReadBody? b, Feedback fb) =>
        {
            if (!Auth.IsAdmin(c)) return Fail("Це вміє лише розробник");
            var ids = (b?.Ids ?? []).Concat(b?.Id is { } one ? [one] : []).ToList();
            return Results.Ok(new { ok = true, waiting = DevView(fb.ReadAsDev(ids)) });
        });

        // Число на 💡: розробнику — скільки записок чекає (нові + ті, де людина відписала), решті — у скількох своїх
        // записках є непрочитане від розробника.
        api.MapGet("/new-count", (HttpContext c, Feedback fb) =>
        {
            if (!Auth.IsAdmin(c)) return Results.Ok(new { count = fb.Unread(Auth.Nick(c)) });
            var n = fb.DevCount();
            return Results.Ok(new { count = n.Count, @new = n.New, replies = n.Replies });
        });

        api.MapPatch("/{id:long}", (HttpContext c, long id, FeedbackPatch b, Feedback fb) =>
        {
            if (!Auth.IsAdmin(c)) return Fail("Це вміє лише розробник");
            var r = fb.Update(id, b.Status, b.Reply, Auth.Nick(c));
            return r.Ok ? Results.Ok(new { ok = true, message = r.Message }) : Fail(r.Message);
        });
        return app;
    }
}
