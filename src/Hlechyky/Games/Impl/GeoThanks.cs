using System.Collections.Concurrent;
using System.Globalization;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «📷 Глек дякує» (пакет 4): коли друг <b>уперше</b> вгадує твоє фото з «Мої фото» — шпилька ближче за
/// <see cref="Km"/> км (той самий поріг, що 🟩 у квадратиках дня й «влучив» у дуелі), — автору +<see cref="Shards"/> 🏺 і
/// рядок у Журнал. Лише за перші <see cref="Photos"/> різні фото автора, назавжди; свої фото й гості не рахуються.
/// <para>
/// Ідемпотентно двічі: пам'ять/таблиця <c>geo_mine_thanks</c> (фото → автор) і ref виплати <c>geo-thanks:&lt;id фото&gt;</c>
/// у леджері. Під замком кімнати — лише пам'ять: перевірка, запис і виплата йдуть фоном (<see cref="Run"/>).
/// </para>
/// </summary>
public sealed class GeoThanks
{
    public const double Km = GeoRules.DuelKm;
    /// <summary>Як за ідеальну партію (25 000 очок = 5 🏺) чи «Де це? дня» — у ряд з рештою нагород гри.</summary>
    public const int Shards = 5;
    public const int Photos = 3;
    /// <summary>Причина в леджері: голова «award» — в історії гаманця «нагорода за гру».</summary>
    public const string Reason = "award:geo-thanks";

    const string Schema = """
        CREATE TABLE IF NOT EXISTS geo_mine_thanks(
            photo_id TEXT PRIMARY KEY, nick_key TEXT NOT NULL, guesser TEXT NOT NULL, at TEXT NOT NULL) WITHOUT ROWID;
        """;

    readonly Db? _db;
    readonly IClock _clock;
    readonly object _gate = new();
    /// <summary>Подякувані фото → ключ автора.</summary>
    readonly ConcurrentDictionary<string, string> _done = new(StringComparer.Ordinal);

    public GeoThanks(Db? db, IClock? clock = null)
    {
        _db = db;
        _clock = clock ?? new SystemClock();
        Loaded = db is null ? Task.CompletedTask : Task.Run(Load);
    }

    public Task Loaded { get; }

    /// <summary>Виплата: (нік, сума, причина, ref, текст тоста) → чи справді нараховано (не дубль).</summary>
    public Func<string, int, string, string, string, bool>? Pay { get; init; }
    /// <summary>Рядок у Журнал (розсилка ігор).</summary>
    public Action<string>? Journal { get; init; }
    /// <summary>Множник заробітку (Economy:Boost): подяка — заробіток, тож × Boost. Типово — як є (тести).</summary>
    public Func<int, int> Earn { get; init; } = n => n;
    /// <summary>Де виконати подяку: типово фоном; тести підставляють «одразу».</summary>
    public Func<Action, Task> Run { get; init; } = Task.Run;

    /// <summary>За скільки фото автора Глек уже подякував (із <see cref="Photos"/>).</summary>
    public int Count(string nickKey) => _done.Values.Count(k => k == nickKey);

    public bool Thanked(string photoId) => _done.ContainsKey(photoId);

    /// <summary>
    /// Розкриття раунду з фото друга: <paramref name="guesser"/> влучив ближче за <see cref="Km"/>. Кличеться під
    /// замком кімнати — тут лише дешеві перевірки пам'яті, решта фоном.
    /// </summary>
    public void Guessed(GeoMinePhoto photo, string? guesser)
    {
        if (guesser is null || Auth.IsGuestNick(guesser) || Auth.NickKey(guesser) == photo.NickKey) return;
        if (_done.ContainsKey(photo.Id) || Count(photo.NickKey) >= Photos) return;
        _ = Run(() => Thank(photo, guesser));
    }

    void Thank(GeoMinePhoto photo, string guesser)
    {
        try
        {
            lock (_gate)
            {
                if (_done.ContainsKey(photo.Id) || Count(photo.NickKey) >= Photos) return;
                _done[photo.Id] = photo.NickKey;
                Write(photo, guesser);
            }
            var amount = Earn(Shards);
            var paid = Pay?.Invoke(photo.Nick, amount, Reason, "geo-thanks:" + photo.Id,
                $"Глек дякує: твоє фото «{photo.Title}» вгадали") ?? false;
            if (paid) Journal?.Invoke($"📷 Глек дякує · {photo.Nick}: фото «{photo.Title}» вгадали — +{amount} 🏺");
        }
        catch (Exception)
        {
            // не заплатили зараз — ref у леджері не дасть заплатити двічі, а пам'ять не дасть смикати знову
        }
    }

    void Write(GeoMinePhoto photo, string guesser)
    {
        if (_db is null) return;
        _db.With(c =>
        {
            using (var s = c.CreateCommand()) { s.CommandText = Schema; s.ExecuteNonQuery(); }
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO geo_mine_thanks(photo_id, nick_key, guesser, at) VALUES($id, $key, $who, $at)";
            cmd.Parameters.AddWithValue("$id", photo.Id);
            cmd.Parameters.AddWithValue("$key", photo.NickKey);
            cmd.Parameters.AddWithValue("$who", guesser);
            cmd.Parameters.AddWithValue("$at", _clock.UtcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        });
    }

    void Load()
    {
        try
        {
            _db!.With(c =>
            {
                using (var s = c.CreateCommand()) { s.CommandText = Schema; s.ExecuteNonQuery(); }
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT photo_id, nick_key FROM geo_mine_thanks";
                using var r = cmd.ExecuteReader();
                while (r.Read()) _done.TryAdd(r.GetString(0), r.GetString(1));
            });
        }
        catch (Exception)
        {
            // без бази — пам'ять порожня; ref у леджері однаково не заплатить за те саме фото двічі
        }
    }
}
