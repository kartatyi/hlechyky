using Microsoft.Data.Sqlite;

namespace Hlechyky;

/// <summary>
/// Хто з ніків — не людина. У «📊 Хто скільки» (таблиці, звання, «Хто кого», графіки, рекорди, газета, цілі, час)
/// ботів не показуємо: записка #31 (Smaug, 10.10).
/// <para>
/// Боти бувають двох родів. Вбудовані (🤖 + бот, селяни Мафії, боти Вечірки) сидять на вільних місцях, яких каркас не
/// бачить (<c>Ctx.Seated</c> = false), — у партії, гаманці й ачівки вони не потрапляють; їхні імена завжди з «🤖»
/// (<see cref="Looks"/>) — про всяк випадок відсіюємо й за ним. А от аі-агенти з <c>/mcp</c> сідають за стіл під
/// вигаданим ніком як люди — і отримують усе: партії, черепки за слухання, ачівки. Їх сервер запам'ятовує в таблиці
/// <c>bot_nicks</c>, щойно агент назвався (<see cref="Mark"/> із <c>set_nick</c>); тих, хто грав до цього (Мафія з
/// агентами 15–16.09), записує міграція (<see cref="Agents"/>).
/// </para>
/// Дядько Глек — не бот у цьому сенсі: його рядки (діджей, реклама, прожарки) «Хто скільки» відділяє сам (DjKeys), а
/// вкладка «🏺 Глек» показує його свідомо.
/// </summary>
public static class Bots
{
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS bot_nicks(
            nick_key TEXT PRIMARY KEY, nick TEXT NOT NULL, kind TEXT NOT NULL, created_at TEXT NOT NULL)
        """;

    /// <summary>Значок, з якого починаються імена всіх вбудованих ботів (LiveBots.Name, Mafia/Vechirka BotNames).</summary>
    public const string Mark0 = "🤖";

    /// <summary>
    /// Аі-агенти, що грали на проді до <c>bot_nicks</c>: Мафія з людьми 15.09 (Кум Панас, Тітка Одарка, Дід Мирон,
    /// Зоська Скалка, Стьопа Кувалда, Клод) і 16.09 (Скажений Макогін, Скажений Кум, Гнат Шершень, Остап Сірник).
    /// Жоден із цих ніків не має акаунта.
    /// </summary>
    public static readonly string[] Agents =
    [
        "Кум Панас", "Тітка Одарка", "Дід Мирон", "Зоська Скалка", "Стьопа Кувалда", "Клод",
        "Скажений Макогін", "Скажений Кум", "Гнат Шершень", "Остап Сірник",
    ];

    /// <summary>SQL міграції: записати <see cref="Agents"/> (раз — потім адмін може й прибрати рядок).</summary>
    public static string SeedSql(DateTimeOffset at) =>
        string.Join("\n", Agents.Select(n =>
            $"INSERT OR IGNORE INTO bot_nicks(nick_key, nick, kind, created_at) VALUES('{Esc(Auth.NickKey(n))}', '{Esc(n)}', 'mcp', '{at.ToUniversalTime():o}');"));

    static string Esc(string s) => s.Replace("'", "''");

    /// <summary>Ім'я вбудованого бота — за значком «🤖» на початку (нік чи ключ, однаково).</summary>
    public static bool Looks(string? nickOrKey) =>
        nickOrKey is not null && nickOrKey.TrimStart().StartsWith(Mark0, StringComparison.Ordinal);

    /// <summary>
    /// Умова SQL «<paramref name="col"/> — не бот»: для стовпця з ключем ніка (trim + lower, як у гаманців). Для стовпців
    /// із сирим ніком не годиться: SQLite <c>lower()</c> не знає кирилиці — там <see cref="Set.Has"/> у пам'яті.
    /// </summary>
    public static string NotBot(string col) =>
        $"({col} NOT IN (SELECT nick_key FROM bot_nicks) AND {col} NOT LIKE '{Mark0}%')";

    /// <summary>Ключі ботів з бази — для фільтра в пам'яті.</summary>
    public sealed class Set(HashSet<string> keys)
    {
        public static readonly Set Empty = new([]);
        public int Count => keys.Count;
        /// <summary>Бот? Приймає ключ або сирий нік: ключ рахується тут.</summary>
        public bool Has(string? nickOrKey)
        {
            if (string.IsNullOrEmpty(nickOrKey)) return false;
            if (Looks(nickOrKey)) return true;
            return keys.Count > 0 && keys.Contains(Auth.NickKey(nickOrKey));
        }
    }

    public static Set Load(SqliteConnection c)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT nick_key FROM bot_nicks";
        using var r = cmd.ExecuteReader();
        while (r.Read()) keys.Add(r.GetString(0));
        return new Set(keys);
    }

    public static Set Load(Db db) => db.With(Load);

    /// <summary>
    /// Запам'ятати нік як бота (<paramref name="kind"/>: «mcp» — аі-агент). Нік з акаунтом — людський (агентові під ним
    /// не сісти, а людина, що заходить своїм ніком через /mcp, лишається людиною) — такий не пишемо.
    /// </summary>
    public static bool Mark(Db db, string nick, string kind, DateTimeOffset at)
    {
        var key = Auth.NickKey(nick);
        if (key.Length == 0 || key == Auth.Guest) return false;
        if (db.FindAccount(nick) is not null) return false;
        return db.With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO bot_nicks(nick_key, nick, kind, created_at) VALUES($k, $n, $kind, $t)";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$n", nick.Trim());
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$t", at.ToUniversalTime().ToString("o"));
            return cmd.ExecuteNonQuery() > 0;
        });
    }
}
