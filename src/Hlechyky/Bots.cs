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
/// <para>
/// Гостей у «Хто скільки» теж нема (11.10, користувач): людина в статистиці — це акаунт. Гість — будь-який нік без
/// акаунта: і «гість Іван», і старі ніки ще до акаунтів («Іван», «Даша»), і Клодіки, що писали в Балачки руками
/// («Клодік», «Клод (справжній)», «микола і його клод»). Тож <see cref="NotBot"/> і <see cref="Set.Has"/> пускають лише
/// ніки з <c>accounts</c>, і то не з <c>bot_nicks</c>.
/// </para>
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

    /// <summary>
    /// Клодіки й інші аі, що писали в Балачки на проді (07.09–09.10) без /mcp: без акаунта їх і так не видно, але якщо
    /// хтось колись займе такий нік акаунтом — однаково не людина.
    /// </summary>
    public static readonly string[] Claudes =
    [
        "Клодік", "Клод (справжній)", "микола і його клод", "cursor", "гість Клод",
    ];

    /// <summary>SQL міграції: записати ніки (раз — потім адмін може й прибрати рядок).</summary>
    public static string SeedSql(DateTimeOffset at) => SeedSql(Agents, at);

    public static string SeedSql(IEnumerable<string> nicks, DateTimeOffset at) =>
        string.Join("\n", nicks.Select(n =>
            $"INSERT OR IGNORE INTO bot_nicks(nick_key, nick, kind, created_at) VALUES('{Esc(Auth.NickKey(n))}', '{Esc(n)}', 'mcp', '{at.ToUniversalTime():o}');"));

    static string Esc(string s) => s.Replace("'", "''");

    /// <summary>Ім'я вбудованого бота — за значком «🤖» на початку (нік чи ключ, однаково).</summary>
    public static bool Looks(string? nickOrKey) =>
        nickOrKey is not null && nickOrKey.TrimStart().StartsWith(Mark0, StringComparison.Ordinal);

    /// <summary>
    /// Умова SQL «<paramref name="col"/> — людина»: акаунт і не бот. Для стовпця з ключем ніка (trim + lower, як у
    /// гаманців). Для стовпців із сирим ніком не годиться: SQLite <c>lower()</c> не знає кирилиці — там
    /// <see cref="Set.Has"/> у пам'яті.
    /// </summary>
    public static string NotBot(string col) =>
        $"({col} IN (SELECT nick_key FROM accounts) AND {col} NOT IN (SELECT nick_key FROM bot_nicks) AND {col} NOT LIKE '{Mark0}%')";

    /// <summary>Ключі ботів і акаунтів з бази — для фільтра в пам'яті.</summary>
    public sealed class Set(HashSet<string> keys, HashSet<string> people)
    {
        public int Count => keys.Count;
        /// <summary>Не людина (бот чи гість)? Приймає ключ або сирий нік: ключ рахується тут.</summary>
        public bool Has(string? nickOrKey)
        {
            if (string.IsNullOrEmpty(nickOrKey)) return false;
            if (Looks(nickOrKey)) return true;
            var key = Auth.NickKey(nickOrKey);
            return keys.Contains(key) || !people.Contains(key);
        }

        /// <summary>Саме бот (не гість): <c>bot_nicks</c> чи «🤖».</summary>
        public bool Bot(string? nickOrKey) =>
            !string.IsNullOrEmpty(nickOrKey) && (Looks(nickOrKey) || keys.Contains(Auth.NickKey(nickOrKey)));
    }

    public static Set Load(SqliteConnection c) =>
        new(Keys(c, "SELECT nick_key FROM bot_nicks"), Keys(c, "SELECT nick_key FROM accounts"));

    static HashSet<string> Keys(SqliteConnection c, string sql)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read()) keys.Add(r.GetString(0));
        return keys;
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
