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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(Path + suffix); } catch (IOException) { /* хай лежить у temp */ }
    }
}
