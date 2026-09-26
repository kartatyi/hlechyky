using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;
using static Hlechyky.Tests.Support.Radio;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Картка людини (GET /api/people/{nick}) і історія ефіру з «Мої» та «Показати ще» (GET /api/history):
/// форма на дроті, регістр ніка, гості, межі тижня й місяця за київськими днями, «такого не бачили».
/// </summary>
public class PeopleTests
{
    /// <summary>Субота, 12:00 за Києвом. Тиждень почався 20.09 о 00:00 за Києвом (19.09 21:00 UTC), місяць — 28.08.</summary>
    static readonly DateTimeOffset Now = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);

    static EconomyRig Rig()
    {
        var rig = new EconomyRig();
        rig.Clock.UtcNow = Now;
        return rig;
    }

    static (int? Status, JsonElement Body) Card(EconomyRig rig, string nick) =>
        Reply(PeopleEndpoints.Person(nick, rig.Db, rig.Store, rig.Clock));

    static string[] Ids(JsonElement list) => list.EnumerateArray().Select(x => x.GetProperty("track").GetProperty("id").GetString()!).ToArray();

    // ---------- картка ----------

    [Fact]
    public void Card_shows_what_a_nick_requests_likes_and_collects()
    {
        using var rig = Rig();
        var db = rig.Db;
        db.AddAccount("Оля", "hash", "salt");
        foreach (var id in new[] { "a", "b", "c", "x" }) Track(db, id);
        Track(db, "voice-1", "Оля", "Голосове");

        Play(db, "b", "user", "оля", Now.AddDays(-40));      // інше написання того самого ніка, поза місяцем
        Play(db, "a", "user", "Оля", Now.AddDays(-20));
        Play(db, "c", "user", "Оля", Now.AddDays(-3));
        Play(db, "a", "user", "Оля", Now.AddDays(-2));
        Play(db, "a", "autodj", null, Now.AddDays(-1));      // Глек сам — не її
        Play(db, "x", "user", "Петро", Now.AddHours(-2));    // чуже
        Play(db, "a", "user", "Оля", Now.AddHours(-1));
        Play(db, "voice-1", "user", "Оля", Now.AddMinutes(-30));

        Like(db, "a", "Оля", Now.AddDays(-5));
        Like(db, "b", "оля", Now.AddDays(-1));
        Like(db, "x", "Петро", Now.AddDays(-1));
        var road = db.CreatePlaylist("Дорога", "Оля");
        db.AddToPlaylist(road, "a", "Оля");
        db.AddToPlaylist(road, "c", "Петро");
        db.CreatePlaylist("Чуже", "Петро");

        var (status, e) = Card(rig, "ОЛЯ");

        Assert.Equal(200, status);
        Assert.Equal("Оля", e.GetProperty("nick").GetString());       // як в акаунті, хоч питали капсом
        Assert.True(e.GetProperty("account").GetBoolean());
        var m = e.GetProperty("music");
        var req = m.GetProperty("requests");
        Assert.Equal(4, req.GetProperty("week").GetInt32());          // c, a, a, голосове
        Assert.Equal(5, req.GetProperty("month").GetInt32());         // + a двадцять днів тому
        Assert.Equal(6, req.GetProperty("all").GetInt32());           // + b від «оля»

        // топ: найчастіше за весь час, голосове — не музика
        var top = m.GetProperty("top");
        Assert.Equal(["a", "c", "b"], Ids(top));
        Assert.Equal(3, top[0].GetProperty("count").GetInt32());
        Assert.Equal("Пісня a", top[0].GetProperty("track").GetProperty("title").GetString());

        // останні: кожен трек раз, найсвіжіше згори, голосове теж — його ж закидали
        var recent = m.GetProperty("recent");
        Assert.Equal(["voice-1", "a", "c", "b"], Ids(recent));
        Assert.Equal(Now.AddMinutes(-30), recent[0].GetProperty("at").GetDateTimeOffset());
        Assert.Equal(Now.AddHours(-1), recent[1].GetProperty("at").GetDateTimeOffset());

        var likes = m.GetProperty("likes");
        Assert.Equal(2, likes.GetProperty("count").GetInt32());
        Assert.Equal(["b", "a"], Ids(likes.GetProperty("recent")));
        Assert.Equal(Now.AddDays(-1), likes.GetProperty("recent")[0].GetProperty("at").GetDateTimeOffset());

        var lists = m.GetProperty("playlists");
        Assert.Equal(1, lists.GetArrayLength());
        Assert.Equal(road, lists[0].GetProperty("id").GetInt64());
        Assert.Equal("Дорога", lists[0].GetProperty("name").GetString());
        Assert.Equal(2, lists[0].GetProperty("count").GetInt32());
    }

    [Fact]
    public void Card_lists_at_most_five_of_everything()
    {
        using var rig = Rig();
        for (var i = 0; i < 8; i++)
        {
            Track(rig.Db, "t" + i);
            Play(rig.Db, "t" + i, "user", "Оля", Now.AddHours(-10 + i));
            Like(rig.Db, "t" + i, "Оля", Now.AddHours(-10 + i));
        }
        // найсвіжіші — трек, якого в tracks нема (рушій після рестарту взяв його з метаданих): місця в п'ятірці не з'їдає
        Play(rig.Db, "ghost", "user", "Оля", Now.AddMinutes(-5));
        Play(rig.Db, "ghost", "user", "Оля", Now.AddMinutes(-4));
        Like(rig.Db, "ghost", "Оля", Now.AddMinutes(-5));

        var m = Card(rig, "Оля").Body.GetProperty("music");

        Assert.Equal(5, m.GetProperty("top").GetArrayLength());
        Assert.DoesNotContain("ghost", Ids(m.GetProperty("top")));
        Assert.Equal(["t7", "t6", "t5", "t4", "t3"], Ids(m.GetProperty("recent")));
        Assert.Equal(8, m.GetProperty("likes").GetProperty("count").GetInt32());
        Assert.Equal(["t7", "t6", "t5", "t4", "t3"], Ids(m.GetProperty("likes").GetProperty("recent")));
        // закидання як таке лишається закиданням — лічильник його бачить, як і «Хто скільки»
        Assert.Equal(10, m.GetProperty("requests").GetProperty("all").GetInt32());
    }

    [Fact]
    public void Week_and_month_are_kyiv_days_not_rolling_hours()
    {
        using var rig = Rig();
        Track(rig.Db, "a");
        Play(rig.Db, "a", "user", "Оля", new DateTimeOffset(2026, 8, 27, 20, 30, 0, TimeSpan.Zero));   // 27.08, 23:30 за Києвом
        Play(rig.Db, "a", "user", "Оля", new DateTimeOffset(2026, 8, 27, 21, 30, 0, TimeSpan.Zero));   // 28.08, 00:30 — перший день місяця
        Play(rig.Db, "a", "user", "Оля", new DateTimeOffset(2026, 9, 19, 20, 30, 0, TimeSpan.Zero));   // 19.09, 23:30
        Play(rig.Db, "a", "user", "Оля", new DateTimeOffset(2026, 9, 19, 21, 30, 0, TimeSpan.Zero));   // 20.09, 00:30 — перший день тижня

        var req = Card(rig, "Оля").Body.GetProperty("music").GetProperty("requests");

        // Сім разів по 24 години назад від «зараз» захопили б і 19.09 — тиждень за київськими днями його не бачить
        Assert.Equal(1, req.GetProperty("week").GetInt32());
        Assert.Equal(3, req.GetProperty("month").GetInt32());
        Assert.Equal(4, req.GetProperty("all").GetInt32());
    }

    [Fact]
    public void Nick_the_site_never_saw_is_404_with_a_human_message()
    {
        using var rig = Rig();
        Track(rig.Db, "a");
        Play(rig.Db, "a", "user", "Оля", Now);

        var (status, e) = Card(rig, "Хтосьтам");

        Assert.Equal(404, status);
        Assert.Equal("Такого тут не бачили", e.GetProperty("message").GetString());
        Assert.Equal(404, Card(rig, "   ").Status);
    }

    [Fact]
    public void Guest_who_only_chatted_is_a_person_too()
    {
        using var rig = Rig();
        Chat(rig.Db, "гість Вася", "привіт усім", Now.AddHours(-1));

        var (status, e) = Card(rig, "гість вася");

        Assert.Equal(200, status);
        Assert.Equal("гість Вася", e.GetProperty("nick").GetString());
        Assert.False(e.GetProperty("account").GetBoolean());
        var m = e.GetProperty("music");
        Assert.Equal(0, m.GetProperty("requests").GetProperty("all").GetInt32());
        Assert.Empty(m.GetProperty("top").EnumerateArray());
        Assert.Empty(m.GetProperty("recent").EnumerateArray());
        Assert.Equal(0, m.GetProperty("likes").GetProperty("count").GetInt32());
        Assert.Empty(m.GetProperty("likes").GetProperty("recent").EnumerateArray());
        Assert.Empty(m.GetProperty("playlists").EnumerateArray());
    }

    [Fact]
    public void Account_wallet_or_games_alone_are_enough_to_be_seen()
    {
        using var rig = Rig();
        rig.Db.AddAccount("Мовчун", "hash", "salt");
        rig.Economy.Grant("гість Слухач", 1, "listen", "l1");
        // партія без черепків (коротка) — гаманця нема, лишився тільки рядок результату
        rig.Store.AddResult(new ResultRow("r1", "ttt", 1, "гравець", "Гравець", "win", null, "Суперник", 0, Now));
        rig.Store.AddTime([new TimeRow("заглянув", PlayClock.Site, Days.Today(rig.Clock), 60)]);

        Assert.Equal("Мовчун", Card(rig, "мовчун").Body.GetProperty("nick").GetString());
        Assert.Equal("гість Слухач", Card(rig, "Гість слухач").Body.GetProperty("nick").GetString());
        Assert.Null(rig.Store.Wallet("гравець"));
        Assert.Equal("Гравець", Card(rig, "ГРАВЕЦЬ").Body.GetProperty("nick").GetString());
        // хвилина на сайті — теж слід, хоч написання ніка лічильник часу не пам'ятає
        Assert.Equal(200, Card(rig, "Заглянув").Status);
        Assert.Equal("Заглянув", Card(rig, "Заглянув").Body.GetProperty("nick").GetString());
    }

    [Fact]
    public void Nick_is_spelled_the_latest_way_it_was_used()
    {
        using var rig = Rig();
        Track(rig.Db, "a");
        Play(rig.Db, "a", "user", "гість вася", Now.AddDays(-3));
        Chat(rig.Db, "гість Вася", "я тут", Now.AddDays(-1));
        Like(rig.Db, "a", "ГІСТЬ ВАСЯ", Now.AddDays(-2));

        var e = Card(rig, "гість вася").Body;

        Assert.Equal("гість Вася", e.GetProperty("nick").GetString());
        Assert.Equal(1, e.GetProperty("music").GetProperty("requests").GetProperty("all").GetInt32());
        Assert.Equal(1, e.GetProperty("music").GetProperty("likes").GetProperty("count").GetInt32());
    }

    [Fact]
    public void Slash_in_a_nick_survives_the_url()
    {
        using var rig = Rig();
        Chat(rig.Db, "AC/DC", "рок", Now);
        Assert.Equal("AC/DC", Card(rig, "ac%2Fdc").Body.GetProperty("nick").GetString());
    }

    // ---------- історія ----------

    static long[] PlayIds(List<HistoryEntry> list) => list.Select(h => h.PlayId).ToArray();

    [Fact]
    public void History_by_nick_keeps_only_what_that_nick_requested()
    {
        using var db = new TempDb();
        Track(db.Db, "a");
        var mine1 = Play(db.Db, "a", "user", "Оля", Now.AddMinutes(1));
        Play(db.Db, "a", "autodj", null, Now.AddMinutes(2));
        Play(db.Db, "a", "user", "Петро", Now.AddMinutes(3));
        var mine2 = Play(db.Db, "a", "user", "оля", Now.AddMinutes(4));

        Assert.Equal([mine2, mine1], PlayIds(PeopleEndpoints.History(80, "ОЛЯ", null, db.Db)));
        Assert.Empty(PeopleEndpoints.History(80, "Ніхто", null, db.Db));
        // порожній by — як без нього
        Assert.Equal(4, PeopleEndpoints.History(80, "  ", null, db.Db).Count);
    }

    [Fact]
    public void History_before_pages_back_without_repeats()
    {
        using var db = new TempDb();
        Track(db.Db, "a");
        var ids = Enumerable.Range(0, 7).Select(i => Play(db.Db, "a", i % 2 == 0 ? "user" : "autodj", i % 2 == 0 ? "Оля" : null, Now.AddMinutes(i))).ToArray();
        var newest = ids.OrderByDescending(x => x).ToArray();

        var first = PeopleEndpoints.History(3, null, null, db.Db);
        Assert.Equal(newest[..3], PlayIds(first));
        var next = PeopleEndpoints.History(3, null, first[^1].PlayId, db.Db);
        Assert.Equal(newest[3..6], PlayIds(next));
        var last = PeopleEndpoints.History(3, null, next[^1].PlayId, db.Db);
        Assert.Equal([ids[0]], PlayIds(last));
        Assert.Empty(PeopleEndpoints.History(3, null, ids[0], db.Db));

        // «Мої» гортаються так само
        var mine = PeopleEndpoints.History(2, "оля", null, db.Db);
        Assert.Equal([ids[6], ids[4]], PlayIds(mine));
        Assert.Equal([ids[2], ids[0]], PlayIds(PeopleEndpoints.History(2, "оля", mine[^1].PlayId, db.Db)));
    }

    [Fact]
    public void History_rows_keep_their_old_shape_and_add_id()
    {
        using var db = new TempDb();
        Track(db.Db, "a");
        var id = Play(db.Db, "a", "user", "Оля", Now);
        db.Db.Exec("UPDATE plays SET via='suggestion', skipped=1 WHERE id=$id", ("$id", id));
        Like(db.Db, "a", "Петро", Now);

        var row = Views.Json(PeopleEndpoints.History(null, null, null, db.Db))[0];

        Assert.Equal(id, row.GetProperty("id").GetInt64());
        Assert.Equal(id, row.GetProperty("playId").GetInt64());   // старі клієнти читали саме його
        Assert.Equal("a", row.GetProperty("track").GetProperty("id").GetString());
        Assert.Equal("user", row.GetProperty("source").GetString());
        Assert.Equal("Оля", row.GetProperty("requestedBy").GetString());
        Assert.Equal(Now, row.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal(1, row.GetProperty("likes").GetInt32());
        Assert.Equal("suggestion", row.GetProperty("via").GetString());
        Assert.True(row.GetProperty("skipped").GetBoolean());
    }

    [Fact]
    public void Lookups_by_nick_go_through_indexes()
    {
        using var db = new TempDb();
        string Plan(string sql) => db.Db.With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
            using var r = cmd.ExecuteReader();
            var lines = new List<string>();
            while (r.Read()) lines.Add(r.GetString(3));
            return string.Join(" | ", lines);
        });

        // усі написання ніка — лише індексом, без проходу по таблиці
        Assert.Contains("COVERING INDEX ix_plays_requested", Plan("SELECT DISTINCT requested_by FROM plays WHERE requested_by IS NOT NULL"));
        Assert.Contains("COVERING INDEX ix_likes_nick", Plan("SELECT DISTINCT nick FROM likes WHERE nick IS NOT NULL"));
        Assert.Contains("COVERING INDEX ix_chat_nick", Plan("SELECT DISTINCT nick FROM chat WHERE nick IS NOT NULL"));
        Assert.Contains("ix_plays_requested", Plan("SELECT COUNT(*) FROM plays p WHERE p.source = 'user' AND p.requested_by IN ('Оля', 'оля')"));
        Assert.Contains("ix_likes_nick", Plan("SELECT COUNT(DISTINCT l.track_id) FROM likes l JOIN tracks t ON t.id = l.track_id WHERE l.nick IN ('Оля')"));
    }
}
