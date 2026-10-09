using System.Globalization;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Padel;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hlechyky;

// =====================================================================================================================
// Черепки за гривні (08.10.2026) — купити й продати, обома керує адмін (адмінська кука, нік неважливий). Схема одна, як
// сказав власник: «людина платить — статус в обробці і можна скасувати; адмін підтверджує — змінюється статус та зникає
// кнопка скасувати. І все». Ні «не прийшло», ні «не куплю», ні приміток.
//
// Купити: людина обирає пакет (50/100/250/500 грн без бонусу) чи свою суму (типово 10–5000 грн), бачить картку чи банку
// сайту (адмін вписує їх у «Заявках»), скидає гроші й тисне «✓ Скинув» → «в обробці». Адмін бачить переказ у банку,
// тисне «✓ Підтвердити» — черепки падають. Можна купити другові. Куплене не йде в «зароблено» (таблиці й ачівки — за гру).
//
// Продати: людина виставляє «N 🏺 за X грн» — черепки одразу відкладаються (списуються, витратити їх уже не вийде) →
// «в обробці». Адмін бачить її картку з профілю Падельні (нема — людина впише її у вікні), переказує гроші й тисне
// «✓ Підтвердити» — продано, черепки зникають із гри. Скасувати, поки «в обробці», — черепки повертаються.
//
// Курс 1 грн = 100 🏺 в обидва боки, до 3 заявок одного гравця водночас, інших лімітів нема. Купівля, продаж і повернення
// не йдуть ні в «зароблено», ні у «витрачено» (EconomyStore.Exchange).
//
// Вимикачі — ShardShop:Buy і ShardShop:Sell (наживо, без перезапуску): вимкнене не приймає нових заявок, кнопки зникають
// (/api/me → shards), а те, що вже в обробці, людина ще бачить і може скасувати, адмін — підтвердити.
// =====================================================================================================================

/// <summary>Секція <c>ShardShop</c> конфігу.</summary>
public sealed class ShardShopOptions
{
    public static readonly int[] DefaultPacks = [50, 100, 250, 500];

    /// <summary>
    /// Купівля відкрита — коли адмін ще й вписав, куди скидати гроші. Типово вимкнено: сайт опенсорсний, і гроші за
    /// черепки вмикає той, хто його тримає (свій appsettings.Local.json).
    /// </summary>
    public bool Buy { get; set; }
    /// <summary>Скільки черепків за 1 грн, коли купують.</summary>
    public int Rate { get; set; } = 100;
    /// <summary>
    /// Пакети в гривнях. Без типового значення тут: масив із конфігу біндер ДОПИСУЄ до наявного, і замість
    /// 50/100/250/500 вийшло б вісім пакетів. Тому типові — у <see cref="PackList"/>.
    /// </summary>
    public int[]? Packs { get; set; }
    /// <summary>Скільки заявок одного гравця (кожного напрямку) можуть водночас бути в обробці.</summary>
    public int PendingMax { get; set; } = 3;
    /// <summary>Своя сума (цілі гривні) — від і до. <see cref="CustomMax"/> = 0 — лише пакети.</summary>
    public int CustomMin { get; set; } = 10;
    public int CustomMax { get; set; } = 5000;

    /// <summary>Продаж черепків сайту відкритий. Типово вимкнено — як і купівля.</summary>
    public bool Sell { get; set; }
    /// <summary>Скільки черепків за 1 грн, коли продають.</summary>
    public int SellRate { get; set; } = 100;
    /// <summary>Найменший продаж, грн.</summary>
    public int SellMin { get; set; } = 10;

    public int[] PackList => Packs is { Length: > 0 } p ? [.. p.Where(x => x > 0).Distinct().Order()] : DefaultPacks;
    public bool Custom => CustomMax > 0 && CustomMax >= CustomMin;

    /// <summary>Пакет або своя сума в межах.</summary>
    public bool Allows(int uah) => uah > 0 && (PackList.Contains(uah) || (Custom && uah >= Math.Max(1, CustomMin) && uah <= CustomMax));

    public int SellRateOk => Math.Max(1, SellRate);
    public int SellMinOk => Math.Max(1, SellMin);
}

/// <summary>
/// Купівля. <see cref="Status"/>: <c>wait</c> — в обробці (людина скинула гроші, чекає адміна); <c>done</c> — адмін
/// підтвердив, черепки впали; <c>off</c> — людина скасувала.
/// </summary>
public sealed record ShardOrder(long Id, string Buyer, string For, int Uah, int Shards, string Status, DateTimeOffset At,
    DateTimeOffset? DoneAt, string? DoneBy, string Note)
{
    public bool Gift => Auth.NickKey(Buyer) != Auth.NickKey(For);
}

/// <summary>
/// Продаж. <see cref="Status"/>: <c>wait</c> — в обробці (черепки відкладено, чекає адміна); <c>done</c> — адмін переказав
/// гроші й підтвердив, продано; <c>off</c> — людина скасувала, черепки повернуто.
/// </summary>
public sealed record ShardSale(long Id, string Seller, int Uah, int Shards, string Status, DateTimeOffset At,
    DateTimeOffset? PaidAt, string? PaidBy, DateTimeOffset? DoneAt, string? DoneBy, string Note);

/// <summary>Сховище купівель, продажів і банків сайту. DDL і SQL — тут.</summary>
public sealed class ShardShopStore
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS shard_orders(
            id INTEGER PRIMARY KEY AUTOINCREMENT, buyer TEXT NOT NULL, buyer_key TEXT NOT NULL, for_nick TEXT NOT NULL,
            for_key TEXT NOT NULL, uah INTEGER NOT NULL, shards INTEGER NOT NULL, status TEXT NOT NULL, at TEXT NOT NULL,
            done_at TEXT, done_by TEXT, note TEXT NOT NULL DEFAULT '');
        CREATE INDEX IF NOT EXISTS shard_orders_buyer ON shard_orders(buyer_key, id);
        CREATE INDEX IF NOT EXISTS shard_orders_for ON shard_orders(for_key, id);
        CREATE INDEX IF NOT EXISTS shard_orders_status ON shard_orders(status, id);
        CREATE TABLE IF NOT EXISTS shard_sales(
            id INTEGER PRIMARY KEY AUTOINCREMENT, seller TEXT NOT NULL, seller_key TEXT NOT NULL, uah INTEGER NOT NULL,
            shards INTEGER NOT NULL, status TEXT NOT NULL, at TEXT NOT NULL, paid_at TEXT, paid_by TEXT, done_at TEXT,
            done_by TEXT, note TEXT NOT NULL DEFAULT '');
        CREATE INDEX IF NOT EXISTS shard_sales_seller ON shard_sales(seller_key, id);
        CREATE INDEX IF NOT EXISTS shard_sales_status ON shard_sales(status, id);
        CREATE TABLE IF NOT EXISTS shard_shop(key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL, at TEXT NOT NULL);
        """;
    const string Cols = "id, buyer, for_nick, uah, shards, status, at, done_at, done_by, note";
    const string SaleCols = "id, seller, uah, shards, status, at, paid_at, paid_by, done_at, done_by, note";

    readonly Db _db;

    public ShardShopStore(Db db)
    {
        _db = db;
        _db.With(c => { Exec(c, Schema); });
    }

    static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    static DateTimeOffset Ts(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    static DateTimeOffset? TsOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Ts(r.GetString(i));
    static string? StrOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

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

    int Count(string sql, params (string Name, object? Value)[] ps) => _db.With(c =>
    {
        using var cmd = Cmd(c, sql, ps);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    // ---------------------------------------------------------------- купівля

    List<ShardOrder> Rows(string where, params (string Name, object? Value)[] ps) => _db.With(c =>
    {
        using var cmd = Cmd(c, $"SELECT {Cols} FROM shard_orders {where}", ps);
        using var r = cmd.ExecuteReader();
        var list = new List<ShardOrder>();
        while (r.Read())
            list.Add(new ShardOrder(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetString(5),
                Ts(r.GetString(6)), TsOrNull(r, 7), StrOrNull(r, 8), r.GetString(9)));
        return list;
    });

    public long Add(ShardOrder o) => _db.With(c =>
    {
        using var cmd = Cmd(c, """
            INSERT INTO shard_orders(buyer, buyer_key, for_nick, for_key, uah, shards, status, at, note)
            VALUES($b, $bk, $f, $fk, $u, $s, $st, $at, $n); SELECT last_insert_rowid();
            """, ("$b", o.Buyer), ("$bk", Auth.NickKey(o.Buyer)), ("$f", o.For), ("$fk", Auth.NickKey(o.For)), ("$u", o.Uah),
            ("$s", o.Shards), ("$st", o.Status), ("$at", Iso(o.At)), ("$n", o.Note));
        return (long)cmd.ExecuteScalar()!;
    });

    public ShardOrder? Get(long id) => Rows("WHERE id=$id", ("$id", id)).FirstOrDefault();

    /// <summary>Мої: що я купив (собі чи комусь) і що купили мені. Свіжі згори.</summary>
    public List<ShardOrder> Of(string nick, int n) =>
        Rows("WHERE buyer_key=$k OR for_key=$k ORDER BY id DESC LIMIT $n", ("$k", Auth.NickKey(nick)), ("$n", n));

    /// <summary>Усі в обробці — старші згори: першим перевіряти того, хто чекає найдовше.</summary>
    public List<ShardOrder> Waiting() => Rows("WHERE status='wait' ORDER BY id");

    /// <summary>Закриті — свіжі згори.</summary>
    public List<ShardOrder> Recent(int n) => Rows("WHERE status<>'wait' ORDER BY id DESC LIMIT $n", ("$n", n));

    public int WaitingOf(string buyer) =>
        Count("SELECT COUNT(*) FROM shard_orders WHERE buyer_key=$k AND status='wait'", ("$k", Auth.NickKey(buyer)));

    /// <summary>Гривні, що прийшли за куплене з <paramref name="since"/>, — «за місяць».</summary>
    public int DoneUah(DateTimeOffset since) =>
        Count("SELECT COALESCE(SUM(uah), 0) FROM shard_orders WHERE status='done' AND done_at >= $s", ("$s", Iso(since)));

    /// <summary>Перевести зі стану <paramref name="from"/> в <paramref name="to"/>; false — замовлення вже не в тому стані.</summary>
    public bool Move(long id, string from, string to, DateTimeOffset at, string by) => _db.With(c =>
        Exec(c, "UPDATE shard_orders SET status=$to, done_at=$at, done_by=$by WHERE id=$id AND status=$from",
            ("$to", to), ("$at", Iso(at)), ("$by", by), ("$id", id), ("$from", from)) > 0);

    // ---------------------------------------------------------------- продаж

    List<ShardSale> Sales(string where, params (string Name, object? Value)[] ps) => _db.With(c =>
    {
        using var cmd = Cmd(c, $"SELECT {SaleCols} FROM shard_sales {where}", ps);
        using var r = cmd.ExecuteReader();
        var list = new List<ShardSale>();
        while (r.Read())
            list.Add(new ShardSale(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetString(4), Ts(r.GetString(5)),
                TsOrNull(r, 6), StrOrNull(r, 7), TsOrNull(r, 8), StrOrNull(r, 9), r.GetString(10)));
        return list;
    });

    public long AddSale(ShardSale s) => _db.With(c =>
    {
        using var cmd = Cmd(c, """
            INSERT INTO shard_sales(seller, seller_key, uah, shards, status, at, note)
            VALUES($s, $sk, $u, $n, $st, $at, $note); SELECT last_insert_rowid();
            """, ("$s", s.Seller), ("$sk", Auth.NickKey(s.Seller)), ("$u", s.Uah), ("$n", s.Shards), ("$st", s.Status),
            ("$at", Iso(s.At)), ("$note", s.Note));
        return (long)cmd.ExecuteScalar()!;
    });

    /// <summary>Прибрати заявку, яку щойно записали, а черепки списати не вдалось (хтось витратив їх ту саму мить).</summary>
    public void DropSale(long id) => _db.With(c => Exec(c, "DELETE FROM shard_sales WHERE id=$id AND status='wait'", ("$id", id)));

    public ShardSale? GetSale(long id) => Sales("WHERE id=$id", ("$id", id)).FirstOrDefault();

    /// <summary>Мої продажі — свіжі згори.</summary>
    public List<ShardSale> SalesOf(string nick, int n) =>
        Sales("WHERE seller_key=$k ORDER BY id DESC LIMIT $n", ("$k", Auth.NickKey(nick)), ("$n", n));

    /// <summary>В обробці — старші згори: першим платити тому, хто чекає найдовше.</summary>
    public List<ShardSale> SalesWaitingList() => Sales("WHERE status='wait' ORDER BY id");

    /// <summary>Закриті — свіжі згори.</summary>
    public List<ShardSale> SalesRecent(int n) => Sales("WHERE status<>'wait' ORDER BY id DESC LIMIT $n", ("$n", n));

    /// <summary>Скільки продажів в обробці — усіх чи одного гравця.</summary>
    public int SalesWaiting(string? nick = null) => nick is null
        ? Count("SELECT COUNT(*) FROM shard_sales WHERE status='wait'")
        : Count("SELECT COUNT(*) FROM shard_sales WHERE seller_key=$k AND status='wait'", ("$k", Auth.NickKey(nick)));

    /// <summary>Гривні, які адмін виплатив гравцям з <paramref name="since"/>, — «за місяць».</summary>
    public int PaidOutUah(DateTimeOffset since) =>
        Count("SELECT COALESCE(SUM(uah), 0) FROM shard_sales WHERE status='done' AND paid_at >= $s", ("$s", Iso(since)));

    /// <summary>«✓ Підтвердити» адміна: wait → done — гроші переказано, продано.</summary>
    public bool SaleDone(long id, DateTimeOffset at, string by) => _db.With(c =>
        Exec(c, "UPDATE shard_sales SET status='done', paid_at=$at, paid_by=$by, done_at=$at, done_by=$by WHERE id=$id AND status='wait'",
            ("$at", Iso(at)), ("$by", by), ("$id", id)) > 0);

    /// <summary>Закрити без продажу (скасовано людиною чи черепків уже нема); false — заявка вже не в обробці.</summary>
    public bool SaleOff(long id, DateTimeOffset at, string by, string note = "") => _db.With(c =>
        Exec(c, "UPDATE shard_sales SET status='off', done_at=$at, done_by=$by, note=$n WHERE id=$id AND status='wait'",
            ("$at", Iso(at)), ("$by", by), ("$n", note), ("$id", id)) > 0);

    // ---------------------------------------------------------------- банки сайту (куди покупцям скидати гроші)

    public List<PadelBank> ShopBanks() => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT value FROM shard_shop WHERE key='banks'");
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<List<PadelBank>>(json) ?? [] : [];
    });

    public void SetShopBanks(List<PadelBank> banks, DateTimeOffset at) => _db.With(c =>
        Exec(c, "INSERT INTO shard_shop(key, value, at) VALUES('banks', $v, $at) ON CONFLICT(key) DO UPDATE SET value=excluded.value, at=excluded.at",
            ("$v", JsonSerializer.Serialize(banks)), ("$at", Iso(at))));
}

/// <summary>Банки гравця (картка, банка) — у Падельні вони вже є в профілі, тож другий раз їх не вводять.</summary>
public interface IShardBanks
{
    IReadOnlyList<PadelBank> Of(string nick);
}

public sealed class PadelShardBanks(PadelMoneyStore store) : IShardBanks
{
    public IReadOnlyList<PadelBank> Of(string nick) => store.Banks(Pid.User(nick));
}

/// <summary>Сповіщення: тости, лічильник «в обробці» адмінам (кружечок на гаманці без F5), свіже вікно людині.</summary>
public interface IShardShopWire
{
    void Toast(string nick, string text);
    /// <summary>Тост адмінам: нова купівля чи продаж.</summary>
    void ToastAdmins(string text);
    /// <summary>Скільки в обробці — адмінам: купівель і продажів.</summary>
    void Waiting(int orders, int sales);
    /// <summary>Заявка людини змінилась — її вкладки перечитують вікно.</summary>
    void Refresh(string nick);
}

/// <summary>
/// Через хаб радіо: тост — подія <c>toast</c> (core.js її вже показує), лічильник — <c>shardOrders</c> у групу адмінів
/// записок (<see cref="FeedbackDevGroup"/>), людині — <c>shardMine</c>.
/// </summary>
public sealed class HubShardShopWire(IHubContext<RadioHub> hub, Presence presence, ILogger<HubShardShopWire> log) : IShardShopWire
{
    public void Toast(string nick, string text)
    {
        var ids = presence.ConnectionsOf(nick);
        if (ids.Count > 0) _ = SendAsync(hub.Clients.Clients(ids), "toast", new { text, kind = "ok" });
    }

    public void ToastAdmins(string text) => _ = SendAsync(hub.Clients.Group(FeedbackDevGroup.Name), "toast", new { text, kind = "ok" });

    public void Waiting(int orders, int sales) =>
        _ = SendAsync(hub.Clients.Group(FeedbackDevGroup.Name), "shardOrders", new { count = orders, sales });

    public void Refresh(string nick)
    {
        var ids = presence.ConnectionsOf(nick);
        if (ids.Count > 0) _ = SendAsync(hub.Clients.Clients(ids), "shardMine", new { });
    }

    async Task SendAsync(IClientProxy to, string name, object payload)
    {
        try { await to.SendAsync(name, payload); }
        catch (Exception ex) { log.LogWarning(ex, "черепки за гривні не розіслали {Event}", name); }
    }
}

/// <summary>Хто прийшов: нік, чи це акаунт і чи адмінська кука.</summary>
public sealed record ShardActor(string Nick, bool Account, bool Admin)
{
    public static ShardActor Of(HttpContext c) => new(Auth.Nick(c), Auth.IsUser(c), Auth.IsAdmin(c));
}

public sealed record ShardReply(bool Ok, string Message, int Status = 200, object? Order = null);

/// <summary>Правила купівлі й продажу: хто може, скільки в обробці, хто підтверджує.</summary>
public sealed class ShardShop(ShardShopStore store, Economy economy, Db db, IShardBanks banks, IShardShopWire wire,
    IClock clock, IOptionsMonitor<ShardShopOptions> opts, ILogger<ShardShop> log)
{
    public const string AccountsOnly = "Черепки купують лише акаунти — закріпи нік";
    public const string Closed = "Купівля черепків ще не відкрита";
    public const string SellAccountsOnly = "Черепки продають лише акаунти — закріпи нік";
    public const string SellClosed = "Продаж черепків зараз закритий";
    public const string NoBanks = "Спершу впиши, куди скинути гроші: картку чи банку";
    public const string AdminOnly = "Підтверджує адмін";
    const int MineMax = 20, RecentMax = 30;
    // Підтвердження і скасування однієї заявки з двох вкладок не мусять розминутись: гроші й зміна стану — разом
    readonly object _gate = new();

    ShardShopOptions O => opts.CurrentValue;

    bool BuyOpen => O.Buy && store.ShopBanks().Count > 0;

    static bool Same(string? a, string? b) => Auth.NickKey(a) == Auth.NickKey(b);

    /// <summary>«10 000» — з нерозривним пробілом, щоб число не рвалось на два рядки.</summary>
    public static string Num(int n) => n.ToString("#,0", new NumberFormatInfo { NumberGroupSeparator = " " });

    static string Shards(int n) => $"{Num(n)} {Economy.Shards(n)}";

    static object View(ShardOrder o) => new
    {
        id = o.Id, buyer = o.Buyer, @for = o.For, gift = o.Gift, uah = o.Uah, shards = o.Shards, status = o.Status,
        at = o.At.UtcDateTime, doneAt = o.DoneAt?.UtcDateTime, doneBy = o.DoneBy,
    };

    /// <summary>Продаж; <paramref name="withBanks"/> — адміну, щоб знав, куди переказувати.</summary>
    object View(ShardSale s, bool withBanks = false) => new
    {
        id = s.Id, seller = s.Seller, uah = s.Uah, shards = s.Shards, status = s.Status, at = s.At.UtcDateTime,
        doneAt = s.DoneAt?.UtcDateTime, doneBy = s.DoneBy, note = s.Note,
        banks = withBanks ? BanksView(banks.Of(s.Seller)) : null,
    };

    static List<object> BanksView(IEnumerable<PadelBank> list) =>
        [.. list.Select(b => (object)new { id = b.Id, bank = b.Bank, title = b.Title, card = b.Card, link = b.Link })];

    /// <summary>Лічильник «в обробці» адмінам.</summary>
    void Counts() => wire.Waiting(store.Waiting().Count, store.SalesWaiting());

    // ---------------------------------------------------------------- вигляд

    /// <summary>
    /// GET /api/shards — купити (пакети, картка сайту — лише акаунтам, мої купівлі), продати (мої банки й продажі), адміну —
    /// «Заявки»: що в обробці (продажі — з банками людини), закриті, суми за місяць і картки сайту.
    /// </summary>
    public object View(ShardActor me)
    {
        var o = O;
        var shop = store.ShopBanks();
        var since = Periods.Since("month", clock);
        return new
        {
            account = me.Account,
            admin = me.Admin,
            buy = new
            {
                // on — увімкнено в конфігу (ShardShop:Buy); open — ще й адмін вписав, куди скидати гроші
                on = o.Buy,
                open = o.Buy && shop.Count > 0,
                rate = o.Rate,
                packs = o.PackList.Select(u => new { uah = u, shards = u * o.Rate }).ToList(),
                custom = o.Custom ? new { min = Math.Max(1, o.CustomMin), max = o.CustomMax } : null,
                pendingMax = o.PendingMax,
                // Номер картки — лише тим, хто закріпив нік: гість сайту бачить сторінку, але не реквізити
                banks = me.Account || me.Admin ? BanksView(shop) : null,
                mine = me.Account ? store.Of(me.Nick, MineMax).Select(View).ToList() : [],
            },
            sell = new
            {
                on = o.Sell,
                open = o.Sell,
                rate = o.SellRateOk,
                min = o.SellMinOk,
                packs = o.PackList.Select(u => new { uah = u, shards = u * o.SellRateOk }).ToList(),
                balance = me.Account ? economy.Balance(me.Nick) : 0,
                // Мої банки — куди адмін перекаже гроші; ті самі, що в профілі Падельні
                banks = me.Account ? BanksView(banks.Of(me.Nick)) : null,
                pendingMax = o.PendingMax,
                mine = me.Account ? store.SalesOf(me.Nick, MineMax).Select(s => View(s)).ToList() : [],
            },
            desk = me.Admin ? new
            {
                orders = store.Waiting().Select(View).ToList(),
                sales = store.SalesWaitingList().Select(s => View(s, withBanks: true)).ToList(),
                recentOrders = store.Recent(RecentMax).Select(View).ToList(),
                recentSales = store.SalesRecent(RecentMax).Select(s => View(s)).ToList(),
                monthIn = store.DoneUah(since),
                monthOut = store.PaidOutUah(since),
                buyOn = o.Buy,
                sellOn = o.Sell,
            } : null,
        };
    }

    // ---------------------------------------------------------------- купити

    /// <summary>Хто, кому й скільки — однаково для «Далі» і «Скинув». Fail — що не так; інакше отримувач (нік так, як
    /// закріплений) і сума.</summary>
    (ShardReply? Fail, string To, int Uah) Validate(ShardActor me, int? uah, string? forNick)
    {
        if (!me.Account) return (new(false, AccountsOnly, 403), "", 0);
        if (!BuyOpen) return (new(false, Closed), "", 0);
        if (uah is not { } u || !O.Allows(u))
            return (new(false, O.Custom ? $"Сума — від {Math.Max(1, O.CustomMin)} до {Num(O.CustomMax)} грн, цілими гривнями" : "Такого пакета нема — обери один із запропонованих"), "", 0);
        var name = (forNick ?? "").Trim();
        if (name.Length == 0 || Same(name, me.Nick)) return (null, me.Nick, u);
        return db.FindAccount(name) is { } acc
            ? (null, acc.Nick, u)
            : (new(false, $"«{name}» — не акаунт: купити можна лише тому, хто закріпив нік"), "", 0);
    }

    ShardReply? TooMany(string buyer)
    {
        var waiting = store.WaitingOf(buyer);
        return waiting < O.PendingMax ? null
            : new(false, $"Уже {waiting} {Plural(waiting, "купівля", "купівлі", "купівель")} в обробці — дочекайся адміна");
    }

    /// <summary>
    /// POST /api/shards/check { uah, for } — «Далі — до оплати»: те саме, що перевірить «Скинув», але ДО реквізитів. Інакше
    /// про «Вася — не акаунт» чи «уже три в обробці» людина дізналась би, коли гроші вже пішли.
    /// </summary>
    public ShardReply Check(ShardActor me, int? uah, string? forNick)
    {
        var (fail, to, u) = Validate(me, uah, forNick);
        if (fail is not null) return fail;
        if (TooMany(me.Nick) is { } many) return many;
        return new(true, "", Order: new { @for = to, gift = !Same(to, me.Nick), uah = u, shards = u * O.Rate });
    }

    /// <summary>POST /api/shards/paid { uah, for } — «✓ Скинув»: купівля в обробці, адміни бачать її в «Заявках».</summary>
    public ShardReply Paid(ShardActor me, int? uah, string? forNick)
    {
        var (fail, to, u) = Validate(me, uah, forNick);
        if (fail is not null) return fail;
        ShardOrder order;
        lock (_gate)
        {
            if (TooMany(me.Nick) is { } many) return many;
            order = new ShardOrder(0, me.Nick, to, u, u * O.Rate, "wait", clock.UtcNow, null, null, "");
            order = order with { Id = store.Add(order) };
        }
        log.LogInformation("Черепки: {Buyer} скинув {Uah} грн за {Shards} (для {For}), купівля {Id}", me.Nick, u, order.Shards, to, order.Id);
        var whom = order.Gift ? $" для {NickCases.Genitive(to)}" : "";
        wire.ToastAdmins($"💸 Від {NickCases.Genitive(me.Nick)}: скинуто {u} грн за {Num(order.Shards)} 🏺{whom} — перевір і підтверди");
        Counts();
        return new(true, $"В обробці! Щойно адмін побачить гроші — черепки впадуть{(order.Gift ? " " + NickCases.Dative(to) : "")}", Order: View(order));
    }

    /// <summary>POST /api/shards/{id}/cancel — передумав або натиснув «Скинув» передчасно. Лише поки в обробці.</summary>
    public ShardReply Cancel(ShardActor me, long id)
    {
        if (!me.Account) return new(false, AccountsOnly, 403);
        lock (_gate)
        {
            if (store.Get(id) is not { } o) return new(false, "Нема такої купівлі", 404);
            if (!Same(o.Buyer, me.Nick)) return new(false, "Скасувати може лише той, хто купував", 403);
            if (o.Status != "wait") return new(false, Already(o.Status));
            store.Move(id, "wait", "off", clock.UtcNow, me.Nick);
        }
        Counts();
        return new(true, "Скасовано");
    }

    /// <summary>POST /api/shards/{id}/ok — «✓ Підтвердити» адміна: гроші прийшли, черепки падають тому, кому купили.</summary>
    public ShardReply Confirm(ShardActor me, long id)
    {
        if (!me.Admin) return new(false, AdminOnly, 403);
        ShardOrder o;
        lock (_gate)
        {
            if (store.Get(id) is not { } got) return new(false, "Нема такої купівлі", 404);
            if (got.Status != "wait") return new(false, Already(got.Status));
            o = got;
            // Спершу гроші з ref на купівлю, тоді стан: падіння між ними лишить «в обробці», а повторне «Підтвердити»
            // уже не нарахує вдруге (Duplicate) — лише допише стан
            var text = o.Gift ? $"+{Shards(o.Shards)}: подарунок від {NickCases.Genitive(o.Buyer)}" : $"+{Shards(o.Shards)}: куплено за {o.Uah} грн";
            var r = economy.Grant(o.For, o.Shards, (o.Gift ? "buy-gift:" : "buy:") + o.Id, "buy:" + o.Id, text);
            if (r is not (GrantResult.Applied or GrantResult.Duplicate)) return new(false, "Черепки не нарахувались — спробуй ще раз");
            store.Move(id, "wait", "done", clock.UtcNow, me.Nick);
        }
        log.LogInformation("Черепки: купівлю {Id} підтвердив {Who}: {For} +{Shards}", o.Id, me.Nick, o.For, o.Shards);
        if (o.Gift) wire.Toast(o.Buyer, $"✓ Підтверджено: {o.For} отримує {Num(o.Shards)} 🏺 за {o.Uah} грн");
        wire.Refresh(o.Buyer);
        Counts();
        return new(true, $"Підтверджено: {o.For} +{Num(o.Shards)} 🏺");
    }

    // ---------------------------------------------------------------- продати

    static string SellRef(long id) => "sell:" + id;
    static string BackRef(long id) => "sell-back:" + id;

    /// <summary>POST /api/shards/sell { uah } — «Продати»: черепки відкладаються (списуються), продаж в обробці.</summary>
    public ShardReply Sell(ShardActor me, int? uah)
    {
        if (!me.Account) return new(false, SellAccountsOnly, 403);
        var o = O;
        if (!o.Sell) return new(false, SellClosed);
        var rate = o.SellRateOk;
        var min = o.SellMinOk;
        if (uah is not { } u || u < min || u > int.MaxValue / rate)
            return new(false, $"Продати можна від {min} грн, цілими гривнями");
        var shards = u * rate;
        if (banks.Of(me.Nick).Count == 0) return new(false, NoBanks);
        ShardSale sale;
        lock (_gate)
        {
            var have = economy.Balance(me.Nick);
            if (have < shards)
                return new(false, have / rate >= min
                    ? $"У глечику {Shards(have)} — вистачить на {Num(have / rate)} грн"
                    : $"У глечику {Shards(have)} — замало: продати можна від {Shards(min * rate)}");
            var waiting = store.SalesWaiting(me.Nick);
            if (waiting >= o.PendingMax)
                return new(false, $"Уже {waiting} {Plural(waiting, "продаж", "продажі", "продажів")} в обробці — дочекайся адміна");
            sale = new ShardSale(0, me.Nick, u, shards, "wait", clock.UtcNow, null, null, null, null, "");
            sale = sale with { Id = store.AddSale(sale) };
            // Спершу заявка, тоді списання з її номером: падіння між ними лишить заявку без списання, і «Підтвердити» адміна
            // спише ще раз (той самий ref — двічі не спише), а скасування без списання не поверне нічого
            if (!economy.TrySpend(me.Nick, shards, SellRef(sale.Id), SellRef(sale.Id),
                    $"−{Shards(shards)}: продаю за {u} грн — відкладено, поки адмін не перекаже гроші"))
            {
                store.DropSale(sale.Id);
                return new(false, "Черепків уже не вистачає — щось витратилось саме зараз");
            }
        }
        log.LogInformation("Черепки: {Seller} продає {Shards} за {Uah} грн, продаж {Id}", me.Nick, shards, u, sale.Id);
        wire.ToastAdmins($"💰 {me.Nick} продає {Num(shards)} 🏺 за {u} грн — перекажи гроші й підтверди");
        Counts();
        return new(true, $"В обробці: {Num(shards)} 🏺 за {u} грн. Щойно адмін перекаже гроші — продаж підтвердять", Order: View(sale));
    }

    /// <summary>POST /api/shards/sale/{id}/cancel — передумав, поки в обробці: черепки назад.</summary>
    public ShardReply CancelSale(ShardActor me, long id)
    {
        if (!me.Account) return new(false, SellAccountsOnly, 403);
        lock (_gate)
        {
            if (store.GetSale(id) is not { } s) return new(false, "Нема такого продажу", 404);
            if (!Same(s.Seller, me.Nick)) return new(false, "Скасувати може лише той, хто продає", 403);
            if (s.Status != "wait") return new(false, Already(s.Status));
            if (Refund(s, $"+{Shards(s.Shards)}: продаж скасовано — черепки назад у глечику") is { } fail) return fail;
            store.SaleOff(id, clock.UtcNow, me.Nick);
        }
        Counts();
        return new(true, "Скасовано — черепки повернулись у глечик");
    }

    /// <summary>POST /api/shards/sale/{id}/ok — «✓ Підтвердити» адміна: гроші переказано — продано, черепки зникають із гри.</summary>
    public ShardReply ConfirmSale(ShardActor me, long id)
    {
        if (!me.Admin) return new(false, AdminOnly, 403);
        ShardSale s;
        lock (_gate)
        {
            if (store.GetSale(id) is not { } got) return new(false, "Нема такого продажу", 404);
            if (got.Status != "wait") return new(false, Already(got.Status));
            s = got;
            // Черепки мусять бути відкладені. Звичайно це повтор (той самий ref — без другого списання); якщо ж заявка пережила
            // падіння до списання, списує зараз, а не вистачило — заявку закрито
            if (!economy.TrySpend(s.Seller, s.Shards, SellRef(id), SellRef(id)))
            {
                store.SaleOff(id, clock.UtcNow, me.Nick, "черепків уже нема");
                wire.Refresh(s.Seller);
                Counts();
                return new(false, $"У {NickCases.Genitive(s.Seller)} уже нема цих черепків — продаж закрито, гроші не переказуй");
            }
            store.SaleDone(id, clock.UtcNow, me.Nick);
        }
        log.LogInformation("Черепки: продаж {Id} — {Who} переказав {Seller} {Uah} грн", s.Id, me.Nick, s.Seller, s.Uah);
        wire.Toast(s.Seller, $"✓ Продано: адмін переказав {s.Uah} грн за {Num(s.Shards)} 🏺 — глянь у банк");
        wire.Refresh(s.Seller);
        Counts();
        return new(true, $"Підтверджено: {NickCases.Dative(s.Seller)} {s.Uah} грн за {Num(s.Shards)} 🏺");
    }

    /// <summary>
    /// Повернути відкладене. Спершу гроші з ref на заявку, тоді стан: повтор не поверне двічі. Лише те, що справді
    /// списали: заявка без списання (падіння між записом і списанням) повернула б черепки з нічого.
    /// </summary>
    ShardReply? Refund(ShardSale s, string text)
    {
        var debit = SellRef(s.Id);
        if (economy.Moves(debit)?.Any(m => m.Ref == debit && m.Delta < 0) != true) return null;
        var r = economy.Grant(s.Seller, s.Shards, BackRef(s.Id), BackRef(s.Id), text);
        return r is GrantResult.Applied or GrantResult.Duplicate ? null : new(false, "Черепки не повернулись — спробуй ще раз");
    }

    // ---------------------------------------------------------------- банки сайту

    public const int BanksMax = 6;

    /// <summary>PUT /api/shards/banks { banks } — адмін вписує, куди покупцям скидати гроші (ті самі поля, що в Падельні).</summary>
    public ShardReply SetBanks(ShardActor me, PadelMoney.BankRequest[]? list)
    {
        if (!me.Admin) return new(false, "Картки сайту міняє адмін", 403);
        var given = list ?? [];
        if (given.Length > BanksMax) return new(false, $"Банків — до {BanksMax}");
        var clean = new List<PadelBank>();
        foreach (var b in given)
        {
            var error = PadelMoney.BankError(b);
            if (error.Length > 0) return new(false, error);
            var digits = (b.Card ?? "").Replace(" ", "").Replace("-", "").Replace(" ", "");
            var link = (b.Link ?? "").Trim();
            clean.Add(new PadelBank("b" + Guid.NewGuid().ToString("N")[..8], b.Bank!.Trim().ToLowerInvariant(), (b.Title ?? "").Trim(),
                digits.Length > 0 ? digits : null, link.Length > 0 ? link : null));
        }
        store.SetShopBanks(clean, clock.UtcNow);
        log.LogInformation("Черепки: {Who} змінив картки сайту ({Count})", me.Nick, clean.Count);
        return new(true, clean.Count > 0 ? "Збережено — покупці бачать, куди скидати" : "Карток нема — купівля закрита", Order: BanksView(clean));
    }

    static string Already(string status) => status switch
    {
        "done" => "Уже підтверджено",
        "off" => "Уже скасовано",
        _ => "Уже не в обробці",
    };

    static string Plural(int n, string one, string few, string many)
    {
        var t = n % 100;
        var u = n % 10;
        return t is >= 11 and <= 14 ? many : u == 1 ? one : u is >= 2 and <= 4 ? few : many;
    }
}

/// <summary>Підключення й маршрути <c>/api/shards…</c>. Кожен маршрут — статичний метод, щоб тести кликали його так само.</summary>
public static class ShardShopSetup
{
    public sealed record PaidRequest(int? Uah, string? For);
    public sealed record SellRequest(int? Uah);
    public sealed record BanksRequest(PadelMoney.BankRequest[]? Banks);

    public static IServiceCollection AddHlechykyShardShop(this IServiceCollection services)
    {
        services.AddOptions<ShardShopOptions>().BindConfiguration("ShardShop");
        services.AddSingleton<ShardShopStore>();
        services.TryAddSingleton<IShardBanks, PadelShardBanks>();
        services.TryAddSingleton<IShardShopWire, HubShardShopWire>();
        services.AddSingleton<ShardShop>();
        return services;
    }

    public static WebApplication MapHlechykyShardShop(this WebApplication app)
    {
        var api = app.MapGroup("/api/shards");
        api.MapGet("", View);
        api.MapPost("/check", Check);
        api.MapPost("/paid", Paid);
        api.MapPost("/{id:long}/cancel", Cancel);
        api.MapPost("/{id:long}/ok", Confirm);
        api.MapPost("/sell", Sell);
        api.MapPost("/sale/{id:long}/cancel", CancelSale);
        api.MapPost("/sale/{id:long}/ok", ConfirmSale);
        api.MapPut("/banks", SetBanks);
        return app;
    }

    public static object View(HttpContext c, ShardShop shop) => shop.View(ShardActor.Of(c));

    public static IResult Check(HttpContext c, PaidRequest b, ShardShop shop) => Reply(shop.Check(ShardActor.Of(c), b.Uah, b.For));

    public static IResult Paid(HttpContext c, PaidRequest b, ShardShop shop) => Reply(shop.Paid(ShardActor.Of(c), b.Uah, b.For));

    public static IResult Cancel(long id, HttpContext c, ShardShop shop) => Reply(shop.Cancel(ShardActor.Of(c), id));

    public static IResult Confirm(long id, HttpContext c, ShardShop shop) => Reply(shop.Confirm(ShardActor.Of(c), id));

    public static IResult Sell(HttpContext c, SellRequest b, ShardShop shop) => Reply(shop.Sell(ShardActor.Of(c), b.Uah));

    public static IResult CancelSale(long id, HttpContext c, ShardShop shop) => Reply(shop.CancelSale(ShardActor.Of(c), id));

    public static IResult ConfirmSale(long id, HttpContext c, ShardShop shop) => Reply(shop.ConfirmSale(ShardActor.Of(c), id));

    public static IResult SetBanks(HttpContext c, BanksRequest b, ShardShop shop) => Reply(shop.SetBanks(ShardActor.Of(c), b.Banks));

    static IResult Reply(ShardReply r)
    {
        var body = new { ok = r.Ok, message = r.Message, order = r.Order };
        return r.Ok ? Results.Ok(body) : Results.Json(body, statusCode: r.Status == 200 ? 400 : r.Status);
    }
}
