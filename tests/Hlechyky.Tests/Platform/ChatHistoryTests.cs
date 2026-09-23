using Hlechyky.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Історія балачок: старий шум Глека схований (але не стертий), старіше підвантажується шматками, а рядки Журналу
/// знають, про що вони — радіо чи ігри.
/// </summary>
public class ChatHistoryTests
{
    /// <summary>База «як на проді до вересневого прибирання»: анонси Глека, його вердикти «Скільки?», люди, Журнал.</summary>
    static string OldDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), "hlechyky-old-chat-" + Guid.NewGuid().ToString("N") + ".db");
        using var c = new SqliteConnection($"Data Source={path}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE chat(id INTEGER PRIMARY KEY AUTOINCREMENT, nick TEXT NOT NULL, text TEXT NOT NULL,
                kind TEXT NOT NULL DEFAULT 'chat', created_at TEXT NOT NULL, room_id TEXT, reply_to INTEGER);
            INSERT INTO chat(nick, text, kind, created_at) VALUES
                ('Оля', 'хто поставив цю пісню?', 'chat', '2026-09-16T18:00:00.0000000+00:00'),
                ('Дядько Глек', 'Моя черга. Billie Eilish — SKINNY — схоже на Billie Eilish — WILDFLOWER.', 'dj', '2026-09-16T18:01:00.0000000+00:00'),
                ('Дядько Глек', 'Тримайте: Океан Ельзи — Без бою — з нашого архіву.', 'dj', '2026-09-16T18:02:00.0000000+00:00'),
                ('Дядько Глек', 'Витягнув з полиці Один в каное — Небо — схоже на Vivienne Mort — Персефона.', 'dj', '2026-09-16T18:03:00.0000000+00:00'),
                ('Дядько Глек', 'Точнісінько — Саша! Шапки геть.', 'dj', '2026-09-17T09:23:00.0000000+00:00'),
                ('Дядько Глек', 'Порядок величин сьогодні не з нами: найближче — владік, різниця 350.', 'dj', '2026-09-17T09:24:00.0000000+00:00'),
                ('Дядько Глек', 'гість Даша — переможець раунду з різницею 2,2.', 'dj', '2026-09-17T09:25:00.0000000+00:00'),
                ('Дядько Глек', 'Ну, село, показуй пальцем. Тільки не в мене.', 'dj', '2026-09-16T20:45:00.0000000+00:00'),
                ('Дядько Глек', 'Побачив — унічтожив. Толкачева з черги прибрав.', 'dj', '2026-09-08T22:14:00.0000000+00:00'),
                ('Петро', 'я комісар', 'chat', '2026-09-17T10:00:00.0000000+00:00'),
                ('Глечики', 'Smaug скіпає The Police — Walking On The Moon', 'system', '2026-09-17T10:01:00.0000000+00:00'),
                ('Глечики', 'Оля ❤ Мотор''Ролла — Восьмий колір', 'system', '2026-09-17T10:02:00.0000000+00:00'),
                ('Глечики', 'Дядько Глек записує голосове (0:20)', 'system', '2026-09-17T10:03:00.0000000+00:00'),
                ('Глечики', 'Бомбер: Оля 3 : Петро 1', 'system', '2026-09-17T10:04:00.0000000+00:00'),
                ('Глечики', '🏅 Оля: ачівка «Слухач» (+15 черепків)', 'system', '2026-09-17T10:05:00.0000000+00:00');
            """;
        cmd.ExecuteNonQuery();
        return path;
    }

    static void Drop(string path)
    {
        using (var c = new SqliteConnection($"Data Source={path}")) SqliteConnection.ClearPool(c);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(path + suffix); } catch (IOException) { }
    }

    [Fact]
    public void Old_glek_noise_is_hidden_from_the_history_but_not_erased()
    {
        var path = OldDatabase();
        try
        {
            var db = new Db(path);
            var talk = db.RecentChat(100, 0);

            // Лишились люди й живі слова Глека (ведучий мафії, відповідь людині) — без анонсів і вердиктів.
            Assert.Equal(
                ["хто поставив цю пісню?", "Ну, село, показуй пальцем. Тільки не в мене.", "Побачив — унічтожив. Толкачева з черги прибрав.", "я комісар"],
                talk.Select(m => m.Text));
            Assert.DoesNotContain(talk, m => m.Kind is "dj-auto" or "dj-game");

            // Схований — не стертий: рядки лежать собі з іншим видом.
            using var c = new SqliteConnection($"Data Source={path}");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT kind, COUNT(*) FROM chat WHERE kind LIKE 'dj%' GROUP BY kind ORDER BY kind";
            using var r = cmd.ExecuteReader();
            var kinds = new Dictionary<string, long>();
            while (r.Read()) kinds[r.GetString(0)] = r.GetInt64(1);
            Assert.Equal(new Dictionary<string, long> { ["dj"] = 2, ["dj-auto"] = 3, ["dj-game"] = 3 }, kinds);
        }
        finally { Drop(path); }
    }

    [Fact]
    public void The_cleanup_runs_once_so_a_new_line_that_looks_old_survives_a_restart()
    {
        var path = OldDatabase();
        try
        {
            var db = new Db(path);
            db.AddChat("Дядько Глек", "Тримайте: корона ваша, Оля!", "dj");

            var again = new Db(path);   // рестарт сервера
            Assert.Contains(again.RecentChat(100, 0), m => m.Text == "Тримайте: корона ваша, Оля!" && m.Kind == "dj");
        }
        finally { Drop(path); }
    }

    [Fact]
    public void Hidden_lines_cannot_be_liked()
    {
        var path = OldDatabase();
        try
        {
            var db = new Db(path);
            Assert.Null(db.ToggleChatLike(2, "Оля"));   // «Моя черга. …» — тепер схований
            Assert.NotNull(db.ToggleChatLike(1, "Петро"));
        }
        finally { Drop(path); }
    }

    [Fact]
    public void Old_journal_lines_learn_their_topic_and_new_ones_carry_it()
    {
        var path = OldDatabase();
        try
        {
            var db = new Db(path);
            var log = db.RecentChat(0, 100).ToDictionary(m => m.Text, m => m.Topic);

            Assert.Equal("radio", log["Smaug скіпає The Police — Walking On The Moon"]);
            Assert.Equal("radio", log["Оля ❤ Мотор'Ролла — Восьмий колір"]);
            Assert.Equal("radio", log["Дядько Глек записує голосове (0:20)"]);
            Assert.Equal("games", log["Бомбер: Оля 3 : Петро 1"]);
            Assert.Equal("games", log["🏅 Оля: ачівка «Слухач» (+15 черепків)"]);

            var fresh = db.AddChat("Глечики", "Оля додає пісню", "system", topic: "radio");
            Assert.Equal("radio", fresh.Topic);
            Assert.Equal("radio", db.RecentChat(0, 100).Single(m => m.Id == fresh.Id).Topic);
        }
        finally { Drop(path); }
    }

    [Fact]
    public void Scrolling_up_brings_older_lines_in_batches_oldest_first()
    {
        using var t = new TempDb();
        var ids = new List<long>();
        for (var i = 0; i < 10; i++) ids.Add(t.Db.AddChat("Оля", $"репліка {i}", "chat").Id);
        t.Db.AddChat("Глечики", "Оля додає пісню", "system", topic: "radio");
        t.Db.AddChat("Дядько Глек", "схований старий анонс", "dj-auto");

        var older = t.Db.ChatBefore(ids[7], 3, log: false);
        Assert.Equal(["репліка 4", "репліка 5", "репліка 6"], older.Select(m => m.Text));

        var top = t.Db.ChatBefore(ids[1], 50, log: false);
        Assert.Equal(["репліка 0"], top.Select(m => m.Text));
        Assert.Empty(t.Db.ChatBefore(ids[0], 50, log: false));

        var log = t.Db.ChatBefore(long.MaxValue, 50, log: true);
        Assert.Equal(["Оля додає пісню"], log.Select(m => m.Text));
        Assert.DoesNotContain(t.Db.ChatBefore(long.MaxValue, 50, log: false), m => m.Kind == "dj-auto");
    }
}
