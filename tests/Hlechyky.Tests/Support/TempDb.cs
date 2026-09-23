namespace Hlechyky.Tests.Support;

/// <summary>
/// Db на тимчасовому файлі. ":memory:" не годиться: Db відкриває нове з'єднання на кожен виклик, і кожне
/// бачило б свою порожню базу. Видаляється разом із -wal/-shm у Dispose.
/// </summary>
public sealed class TempDb : IDisposable
{
    public string Path { get; }
    public Db Db { get; }

    public TempDb()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hlechyky-test-" + Guid.NewGuid().ToString("N") + ".db");
        Db = new Db(Path);
    }

    public void Dispose()
    {
        // Лише свій пул (той самий рядок з'єднання, що в Db): ClearAllPools тут закривав з'єднання паралельних
        // тестів посеред запиту — випадкові ObjectDisposedException і загублені записи в чужих тестах.
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path }.ToString()))
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(c);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(Path + suffix); } catch (IOException) { /* хай лежить у temp */ }
    }
}
