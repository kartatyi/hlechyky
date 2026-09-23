using System.Globalization;
using Hlechyky.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Налаштування з'єднань бази. Без них зіткнення записів коштувало 150 мс сну драйвера, а кожен коміт чекав fsync:
/// Гончарне коло пише збереження на кожну пачку кліків, і вже 50 гравців отримували відповідь секундами.
/// </summary>
public class DbConnectionTests
{
    [Fact]
    public void Every_connection_waits_for_a_busy_db_and_commits_without_fsync()
    {
        using var t = new TempDb();
        for (var i = 0; i < 3; i++)   // і нове з'єднання, і повернуте з пулу
        {
            var (timeout, sync, mode) = t.Db.With(c => (Pragma(c, "busy_timeout"), Pragma(c, "synchronous"), Pragma(c, "journal_mode")));
            Assert.Equal("5000", timeout);
            Assert.Equal("1", sync);   // NORMAL: у WAL цілість бази не страждає, fsync робить контрольна точка
            Assert.Equal("wal", mode);
        }
    }

    static string Pragma(SqliteConnection c, string name)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA " + name;
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }
}
