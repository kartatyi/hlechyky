using System.Globalization;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Padel;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hlechyky;

// =====================================================================================================================
// Купити черепки за гривні — як гроші в Падельні (08.10.2026): покупець обирає пакет, бачить картку й банку продавця
// (ті самі банки, що в його профілі Падельні), скидає гроші й тисне «✓ Скинув». Продавець бачить замовлення в «чекають»
// і тисне «✓ Отримав» — черепки падають одразу; «✕ Не прийшло» — відмова з приміткою. Рішення власника: вручну, без
// API банку; 1 грн = 100 🏺; пакети 50/100/250/500 грн без бонусу або своя сума (типово 10–5000 грн); можна купити
// другові; куплене не йде в «зароблено» (таблиці й ачівки — лише за гру).
//
// Продати черепки за гривні — теж як у Падельні, але навпаки (08.10.2026): гравець виставляє заявку «продаю N 🏺 за X грн»,
// і черепки одразу відкладаються (списуються, витратити їх уже не вийде). Купує сайт — керує адмін (адмінська кука, нік
// неважливий): бачить картку й банку гравця з його профілю Падельні, скидає гроші й тисне «✓ Скинув» або «✕ Не куплю»
// (черепки повертаються). Гравець тисне «✓ Отримав» — продано, черепки зникають із гри; «✕ Не прийшло» — заявка знову в
// адміна з приміткою. Поки адмін не скинув, гравець може скасувати. Курс 1 грн = 100 🏺, від 10 грн, лімітів нема:
// кожну заявку адмін вирішує сам. Продане й повернуте не йде ні в «зароблено», ні у «витрачено» (EconomyStore.Exchange).
// =====================================================================================================================

/// <summary>Секція <c>ShardShop</c> конфігу. Порожній <see cref="Seller"/> — купівля закрита.</summary>
public sealed class ShardShopOptions
{
    public static readonly int[] DefaultPacks = [50, 100, 250, 500];

    /// <summary>Нік продавця: покупець бачить його банки з Падельні, а підтверджує оплату він (або адмін).</summary>
    public string Seller { get; set; } = "";
    /// <summary>Скільки черепків за 1 грн.</summary>
    public int Rate { get; set; } = 100;
    /// <summary>
    /// Пакети в гривнях. Без типового значення тут: масив із конфігу біндер ДОПИСУЄ до наявного, і замість
    /// 50/100/250/500 вийшло б вісім пакетів. Тому типові — у <see cref="PackList"/>.
    /// </summary>
    public int[]? Packs { get; set; }
    /// <summary>Скільки оплат одного покупця можуть водночас чекати підтвердження.</summary>
    public int PendingMax { get; set; } = 3;
    /// <summary>Своя сума (цілі гривні) — від і до. <see cref="CustomMax"/> = 0 — лише пакети.</summary>
    public int CustomMin { get; set; } = 10;
    public int CustomMax { get; set; } = 5000;

    /// <summary>Продаж черепків сайту відкритий (керує адмін).</summary>
    public bool Sell { get; set; } = true;
    /// <summary>Скільки черепків за 1 грн, коли гравець продає.</summary>
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
/// Замовлення. <see cref="Status"/>: <c>wait</c> — покупець скинув, чекає продавця; <c>done</c> — зараховано;
/// <c>no</c> — продавець оплати не знайшов; <c>off</c> — покупець скасував.
/// </summary>
public sealed record ShardOrder(long Id, string Buyer, string For, int Uah, int Shards, string Status, DateTimeOffset At,
    DateTimeOffset? DoneAt, string? DoneBy, string Note)
{
    public bool Gift => Auth.NickKey(Buyer) != Auth.NickKey(For);
}

/// <summary>
/// Заявка на продаж. <see cref="Status"/>: <c>wait</c> — черепки відкладено, чекає адміна; <c>paid</c> — адмін скинув гроші,
/// чекає «✓ Отримав» гравця; <c>done</c> — продано; <c>no</c> — адмін не купив, черепки повернуто; <c>off</c> — гравець
/// скасував, черепки повернуто.
/// </summary>
public sealed record ShardSale(long Id, string Seller, int Uah, int Shards, string Status, DateTimeOffset At,
    DateTimeOffset? PaidAt, string? PaidBy, DateTimeOffset? DoneAt, string? DoneBy, string Note);

/// <summary>Сховище замовлень і заявок на продаж. DDL і SQL — тут.</summary>
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

    List<ShardOrder> Rows(string where, params (string Name, object? Value)[] ps) => _db.With(c =>
    {
        using var cmd = Cmd(c, $"SELECT {Cols} FROM shard_orders {where}", ps);
        using var r = cmd.ExecuteReader();
        var list = new List<ShardOrder>();
        while (r.Read())
            list.Add(new ShardOrder(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetString(5),
                Ts(r.GetString(6)), r.IsDBNull(7) ? null : Ts(r.GetString(7)), r.IsDBNull(8) ? null : r.GetString(8), r.GetString(9)));
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

    /// <summary>Усі, що чекають продавця, — старші згори: першим перевіряти того, хто чекає найдовше.</summary>
    public List<ShardOrder> Waiting() => Rows("WHERE status='wait' ORDER BY id");

    /// <summary>Розглянуті (зараховані, відмовлені, скасовані) — свіжі згори.</summary>
    public List<ShardOrder> Recent(int n) => Rows("WHERE status<>'wait' ORDER BY id DESC LIMIT $n", ("$n", n));

    public int WaitingOf(string buyer) => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT COUNT(*) FROM shard_orders WHERE buyer_key=$k AND status='wait'", ("$k", Auth.NickKey(buyer)));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>Гривні зараховані з <paramref name="since"/> — продавцю «за місяць».</summary>
    public int DoneUah(DateTimeOffset since) => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT COALESCE(SUM(uah), 0) FROM shard_orders WHERE status='done' AND done_at >= $s", ("$s", Iso(since)));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>Перевести зі стану <paramref name="from"/> в <paramref name="to"/>; false — замовлення вже не в тому стані.</summary>
    public bool Move(long id, string from, string to, DateTimeOffset at, string by, string? note = null) => _db.With(c =>
        Exec(c, "UPDATE shard_orders SET status=$to, done_at=$at, done_by=$by, note=COALESCE($n, note) WHERE id=$id AND status=$from",
            ("$to", to), ("$at", Iso(at)), ("$by", by), ("$n", note), ("$id", id), ("$from", from)) > 0);

    // ---------------------------------------------------------------- продаж

    static DateTimeOffset? TsOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Ts(r.GetString(i));
    static string? StrOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

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

    int Count(string sql, params (string Name, object? Value)[] ps) => _db.With(c =>
    {
        using var cmd = Cmd(c, sql, ps);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
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

    /// <summary>Мої заявки — свіжі згори.</summary>
    public List<ShardSale> SalesOf(string nick, int n) =>
        Sales("WHERE seller_key=$k ORDER BY id DESC LIMIT $n", ("$k", Auth.NickKey(nick)), ("$n", n));

    /// <summary>Незакриті (чекають адміна чи «✓ Отримав» гравця) — старші згори.</summary>
    public List<ShardSale> SalesOpen() => Sales("WHERE status IN ('wait','paid') ORDER BY id");

    /// <summary>Закриті (продано, не куплено, скасовано) — свіжі згори.</summary>
    public List<ShardSale> SalesRecent(int n) => Sales("WHERE status IN ('done','no','off') ORDER BY id DESC LIMIT $n", ("$n", n));

    /// <summary>Скільки заявок чекає адміна — усіх чи одного гравця.</summary>
    public int SalesWaiting(string? nick = null) => nick is null
        ? Count("SELECT COUNT(*) FROM shard_sales WHERE status='wait'")
        : Count("SELECT COUNT(*) FROM shard_sales WHERE seller_key=$k AND status='wait'", ("$k", Auth.NickKey(nick)));

    /// <summary>Гривні, які адмін скинув гравцям з <paramref name="since"/>, — «за місяць».</summary>
    public int PaidOutUah(DateTimeOffset since) =>
        Count("SELECT COALESCE(SUM(uah), 0) FROM shard_sales WHERE status IN ('paid','done') AND paid_at >= $s", ("$s", Iso(since)));

    /// <summary>«✓ Скинув» адміна: wait → paid. Примітка «не прийшло» з минулого разу вже ні до чого.</summary>
    public bool SalePaid(long id, DateTimeOffset at, string by) => _db.With(c =>
        Exec(c, "UPDATE shard_sales SET status='paid', paid_at=$at, paid_by=$by, note='' WHERE id=$id AND status='wait'",
            ("$at", Iso(at)), ("$by", by), ("$id", id)) > 0);

    /// <summary>
    /// «✕ Не прийшло» гравця: paid → знову wait, з приміткою для адміна. Коли й хто скидав — лишається: така заявка вже не
    /// скасовується гравцем (інакше «не прийшло» + «скасувати» повернуло б черепки й тоді, коли гроші насправді прийшли).
    /// </summary>
    public bool SaleMissing(long id, string note) => _db.With(c =>
        Exec(c, "UPDATE shard_sales SET status='wait', note=$n WHERE id=$id AND status='paid'",
            ("$n", note), ("$id", id)) > 0);

    /// <summary>Закрити: <paramref name="to"/> — done, no чи off; false — заявка вже не в стані <paramref name="from"/>.</summary>
    public bool CloseSale(long id, string from, string to, DateTimeOffset at, string by, string? note = null) => _db.With(c =>
        Exec(c, "UPDATE shard_sales SET status=$to, done_at=$at, done_by=$by, note=COALESCE($n, note) WHERE id=$id AND status=$from",
            ("$to", to), ("$at", Iso(at)), ("$by", by), ("$n", note), ("$id", id), ("$from", from)) > 0);
}

/// <summary>Банки продавця (картка, банка) — у Падельні вони вже є в профілі, тож другий раз їх не вводять.</summary>
public interface IShardBanks
{
    IReadOnlyList<PadelBank> Of(string nick);
}

public sealed class PadelShardBanks(PadelMoneyStore store) : IShardBanks
{
    public IReadOnlyList<PadelBank> Of(string nick) => store.Banks(Pid.User(nick));
}

/// <summary>Сповіщення: тости, лічильники «чекають» продавцю та адмінам (кружечок на гаманці без F5), свіже вікно гравцю.</summary>
public interface IShardShopWire
{
    void Toast(string nick, string text);
    /// <summary>Тост адмінам: нова заявка на продаж чи «не прийшло».</summary>
    void ToastAdmins(string text);
    /// <summary>Скільки чекає: <paramref name="orders"/> оплат — продавцю й адмінам, <paramref name="sales"/> заявок на продаж — адмінам.</summary>
    void Waiting(string? seller, int orders, int sales);
    /// <summary>Заявка гравця змінилась — його вкладки перечитують вікно й кружечок.</summary>
    void Sale(string nick);
}

/// <summary>
/// Через хаб радіо: тост — подія <c>toast</c> (core.js її вже показує), лічильники — <c>shardOrders</c> на вкладки
/// продавця і в групу адмінів записок (<see cref="FeedbackDevGroup"/>): адмінська кука теж підтверджує й купує черепки.
/// Гравцю — <c>shardSale</c>: адмін скинув гроші чи не купив.
/// </summary>
public sealed class HubShardShopWire(IHubContext<RadioHub> hub, Presence presence, ILogger<HubShardShopWire> log) : IShardShopWire
{
    public void Toast(string nick, string text)
    {
        var ids = presence.ConnectionsOf(nick);
        if (ids.Count > 0) _ = SendAsync(hub.Clients.Clients(ids), "toast", new { text, kind = "ok" });
    }

    public void ToastAdmins(string text) => _ = SendAsync(hub.Clients.Group(FeedbackDevGroup.Name), "toast", new { text, kind = "ok" });

    public void Waiting(string? seller, int orders, int sales)
    {
        var payload = new { count = orders, sales };
        IReadOnlyList<string> ids = seller is null ? [] : presence.ConnectionsOf(seller);
        if (ids.Count > 0) _ = SendAsync(hub.Clients.Clients(ids), "shardOrders", payload);
        _ = SendAsync(hub.Clients.Group(FeedbackDevGroup.Name), "shardOrders", payload);
    }

    public void Sale(string nick)
    {
        var ids = presence.ConnectionsOf(nick);
        if (ids.Count > 0) _ = SendAsync(hub.Clients.Clients(ids), "shardSale", new { });
    }

    async Task SendAsync(IClientProxy to, string name, object payload)
    {
        try { await to.SendAsync(name, payload); }
        catch (Exception ex) { log.LogWarning(ex, "купівля черепків не розіслала {Event}", name); }
    }
}

/// <summary>Хто прийшов: нік, чи це акаунт і чи адмінська кука.</summary>
public sealed record ShardActor(string Nick, bool Account, bool Admin)
{
    public static ShardActor Of(HttpContext c) => new(Auth.Nick(c), Auth.IsUser(c), Auth.IsAdmin(c));
}

public sealed record ShardReply(bool Ok, string Message, int Status = 200, object? Order = null);

/// <summary>Правила купівлі й продажу: хто може купити чи продати, кому, скільки чекає, хто підтверджує.</summary>
public sealed class ShardShop(ShardShopStore store, Economy economy, Db db, IShardBanks banks, IShardShopWire wire,
    IClock clock, IOptionsMonitor<ShardShopOptions> opts, ILogger<ShardShop> log)
{
    public const string AccountsOnly = "Черепки купують лише акаунти — закріпи нік";
    public const string Closed = "Купівля черепків ще не відкрита";
    public const string SellAccountsOnly = "Черепки продають лише акаунти — закріпи нік";
    public const string SellClosed = "Продаж черепків зараз закритий";
    public const string NoBanks = "Спершу впиши, куди скинути гроші: картку чи банку";
    public const string AdminOnly = "Черепки купує адмін";
    public const string MissingNoCancel = "Адмін уже скидав гроші — тепер він перевіряє переказ, скасувати не вийде";
    const int NoteMax = 100, MineMax = 20, RecentMax = 30;
    // Підтвердження і відмова одного замовлення з двох вкладок не мусять розминутись: нарахування й зміна стану — разом
    readonly object _gate = new();

    ShardShopOptions O => opts.CurrentValue;

    /// <summary>Продавець як акаунт (його нік так, як він його закріпив); не задано чи не акаунт — null.</summary>
    string? Seller()
    {
        var s = (O.Seller ?? "").Trim();
        return s.Length == 0 ? null : db.FindAccount(s)?.Nick;
    }

    static bool Same(string? a, string? b) => Auth.NickKey(a) == Auth.NickKey(b);

    bool MayConfirm(ShardActor me, string? seller) => me.Admin || (me.Account && seller is not null && Same(me.Nick, seller));

    /// <summary>«10 000» — з нерозривним пробілом, щоб число не рвалось на два рядки.</summary>
    public static string Num(int n) => n.ToString("#,0", new NumberFormatInfo { NumberGroupSeparator = "\u00a0" });

    static string Shards(int n) => $"{Num(n)} {Economy.Shards(n)}";

    static object View(ShardOrder o) => new
    {
        id = o.Id, buyer = o.Buyer, @for = o.For, gift = o.Gift, uah = o.Uah, shards = o.Shards, status = o.Status,
        at = o.At.UtcDateTime, doneAt = o.DoneAt?.UtcDateTime, doneBy = o.DoneBy, note = o.Note,
    };

    /// <summary>Заявка на продаж; <paramref name="withBanks"/> — адміну, щоб знав, куди скидати.</summary>
    object View(ShardSale s, bool withBanks = false) => new
    {
        id = s.Id, seller = s.Seller, uah = s.Uah, shards = s.Shards, status = s.Status, at = s.At.UtcDateTime,
        paidAt = s.PaidAt?.UtcDateTime, paidBy = s.PaidBy, doneAt = s.DoneAt?.UtcDateTime, doneBy = s.DoneBy, note = s.Note,
        banks = withBanks ? BanksOf(s.Seller) : null,
    };

    List<object> BanksOf(string nick) =>
        [.. banks.Of(nick).Select(b => (object)new { bank = b.Bank, title = b.Title, card = b.Card, link = b.Link })];

    /// <summary>Лічильники «чекають» — продавцю (оплати) й адмінам (оплати й заявки на продаж).</summary>
    void Counts() => wire.Waiting(Seller(), store.Waiting().Count, store.SalesWaiting());

    // ---------------------------------------------------------------- вигляд

    /// <summary>GET /api/shards — пакети, банки продавця (лише акаунтам), мої замовлення; продавцю й адміну — черга.</summary>
    public object View(ShardActor me)
    {
        var seller = Seller();
        var o = O;
        var canConfirm = MayConfirm(me, seller);
        var waiting = canConfirm ? store.Waiting() : null;
        return new
        {
            open = seller is not null,
            account = me.Account,
            rate = o.Rate,
            packs = o.PackList.Select(u => new { uah = u, shards = u * o.Rate }).ToList(),
            custom = o.Custom ? new { min = Math.Max(1, o.CustomMin), max = o.CustomMax } : null,
            pendingMax = o.PendingMax,
            seller = seller is null ? null : new
            {
                nick = seller,
                // Номер картки — лише тим, хто закріпив нік: гість сайту бачить сторінку, але не реквізити
                banks = me.Account || me.Admin
                    ? banks.Of(seller).Select(b => new { bank = b.Bank, title = b.Title, card = b.Card, link = b.Link }).ToList()
                    : null,
            },
            isSeller = seller is not null && me.Account && Same(me.Nick, seller),
            canConfirm,
            mine = me.Account ? store.Of(me.Nick, MineMax).Select(View).ToList() : [],
            waiting = waiting?.Select(View).ToList(),
            recent = canConfirm ? store.Recent(RecentMax).Select(View).ToList() : null,
            monthUah = canConfirm ? store.DoneUah(Periods.Since("month", clock)) : (int?)null,
            admin = me.Admin,
            sell = new
            {
                open = o.Sell,
                rate = o.SellRateOk,
                min = o.SellMinOk,
                packs = o.PackList.Select(u => new { uah = u, shards = u * o.SellRateOk }).ToList(),
                balance = me.Account ? economy.Balance(me.Nick) : 0,
                // Мої банки — куди адмін скине гроші; ті самі, що в профілі Падельні
                banks = me.Account ? BanksOf(me.Nick) : null,
                pendingMax = o.PendingMax,
                mine = me.Account ? store.SalesOf(me.Nick, MineMax).Select(s => View(s)).ToList() : [],
            },
            sales = me.Admin ? SalesDesk() : null,
        };
    }

    /// <summary>Адміну: хто продає (з банками), кому вже скинуто, закриті й скільки виплачено за місяць.</summary>
    object SalesDesk()
    {
        var open = store.SalesOpen();
        return new
        {
            waiting = open.Where(s => s.Status == "wait").Select(s => View(s, withBanks: true)).ToList(),
            paid = open.Where(s => s.Status == "paid").Select(s => View(s)).ToList(),
            recent = store.SalesRecent(RecentMax).Select(s => View(s)).ToList(),
            monthUah = store.PaidOutUah(Periods.Since("month", clock)),
        };
    }

    // ---------------------------------------------------------------- покупець

    /// <summary>Хто, кому й скільки — однаково для «Далі» і «Скинув». Fail — що не так; інакше продавець, отримувач (нік так,
    /// як закріплений) і пакет.</summary>
    (ShardReply? Fail, string Seller, string To, int Uah) Validate(ShardActor me, int? uah, string? forNick)
    {
        if (!me.Account) return (new(false, AccountsOnly, 403), "", "", 0);
        if (Seller() is not { } seller) return (new(false, Closed), "", "", 0);
        if (Same(me.Nick, seller)) return (new(false, "Ти ж продавець — у себе черепки не купують"), "", "", 0);
        if (uah is not { } u || !O.Allows(u))
            return (new(false, O.Custom ? $"Сума — від {Math.Max(1, O.CustomMin)} до {Num(O.CustomMax)} грн, цілими гривнями" : "Такого пакета нема — обери один із запропонованих"), "", "", 0);
        var name = (forNick ?? "").Trim();
        if (name.Length == 0 || Same(name, me.Nick)) return (null, seller, me.Nick, u);
        return db.FindAccount(name) is { } acc
            ? (null, seller, acc.Nick, u)
            : (new(false, $"«{name}» — не акаунт: купити можна лише тому, хто закріпив нік"), "", "", 0);
    }

    ShardReply? TooMany(string buyer, string seller)
    {
        var waiting = store.WaitingOf(buyer);
        return waiting < O.PendingMax ? null
            : new(false, $"Уже {waiting} {Plural(waiting, "оплата чекає", "оплати чекають", "оплат чекають")} підтвердження — дочекайся {NickCases.Genitive(seller)}");
    }

    /// <summary>
    /// POST /api/shards/check { uah, for? } — «Далі — до оплати»: те саме, що перевірить «Скинув», але ДО реквізитів. Інакше
    /// про «Вася — не акаунт» чи «уже три оплати чекають» людина дізналась би, коли гроші вже пішли. У відповіді — нік
    /// отримувача так, як він закріплений.
    /// </summary>
    public ShardReply Check(ShardActor me, int? uah, string? forNick)
    {
        var (fail, seller, to, u) = Validate(me, uah, forNick);
        if (fail is not null) return fail;
        if (TooMany(me.Nick, seller) is { } many) return many;
        return new(true, "", Order: new { @for = to, gift = !Same(to, me.Nick), uah = u, shards = u * O.Rate });
    }

    /// <summary>POST /api/shards/paid { uah, for? } — «✓ Скинув»: замовлення стає в «чекають» продавця.</summary>
    public ShardReply Paid(ShardActor me, int? uah, string? forNick)
    {
        var (fail, seller, to, u) = Validate(me, uah, forNick);
        if (fail is not null) return fail;
        ShardOrder order;
        lock (_gate)
        {
            if (TooMany(me.Nick, seller) is { } many) return many;
            order = new ShardOrder(0, me.Nick, to, u, u * O.Rate, "wait", clock.UtcNow, null, null, "");
            order = order with { Id = store.Add(order) };
        }
        log.LogInformation("Черепки: {Buyer} скинув {Uah} грн за {Shards} (для {For}), замовлення {Id}", me.Nick, u, order.Shards, to, order.Id);
        var whom = order.Gift ? $" для {NickCases.Genitive(to)}" : "";
        wire.Toast(seller, $"💸 Від {NickCases.Genitive(me.Nick)}: скинуто {u} грн за {Num(order.Shards)} 🏺{whom} — перевір і підтверди");
        Counts();
        return new(true, $"Записав! Щойно {seller} побачить гроші — черепки впадуть{(order.Gift ? " " + NickCases.Dative(to) : "")}", Order: View(order));
    }

    /// <summary>POST /api/shards/{id}/cancel — передумав або натиснув «Скинув» передчасно. Лише поки чекає.</summary>
    public ShardReply Cancel(ShardActor me, long id)
    {
        if (!me.Account && !me.Admin) return new(false, AccountsOnly, 403);
        lock (_gate)
        {
            if (store.Get(id) is not { } o) return new(false, "Нема такого замовлення", 404);
            if (!me.Admin && !Same(o.Buyer, me.Nick)) return new(false, "Скасувати може лише той, хто купував", 403);
            if (o.Status != "wait") return new(false, Already(o));
            store.Move(id, "wait", "off", clock.UtcNow, me.Nick);
        }
        Counts();
        return new(true, "Скасовано");
    }

    // ---------------------------------------------------------------- продавець

    /// <summary>POST /api/shards/{id}/ok — «✓ Отримав»: черепки падають тому, кому купили.</summary>
    public ShardReply Confirm(ShardActor me, long id)
    {
        var seller = Seller();
        if (!MayConfirm(me, seller)) return new(false, "Підтверджує продавець", 403);
        ShardOrder o;
        lock (_gate)
        {
            if (store.Get(id) is not { } got) return new(false, "Нема такого замовлення", 404);
            if (got.Status != "wait") return new(false, Already(got));
            o = got;
            // Спершу гроші з ref на замовлення, тоді стан: падіння між ними лишить «чекає», а повторне «Отримав»
            // уже не нарахує вдруге (Duplicate) — лише допише стан
            var text = o.Gift ? $"+{Shards(o.Shards)}: подарунок від {NickCases.Genitive(o.Buyer)}" : $"+{Shards(o.Shards)}: куплено за {o.Uah} грн";
            var r = economy.Grant(o.For, o.Shards, (o.Gift ? "buy-gift:" : "buy:") + o.Id, "buy:" + o.Id, text);
            if (r is not (GrantResult.Applied or GrantResult.Duplicate)) return new(false, "Черепки не нарахувались — спробуй ще раз");
            store.Move(id, "wait", "done", clock.UtcNow, me.Nick);
        }
        log.LogInformation("Черепки: замовлення {Id} підтвердив {Who}: {For} +{Shards}", o.Id, me.Nick, o.For, o.Shards);
        if (o.Gift) wire.Toast(o.Buyer, $"✓ Оплату {o.Uah} грн підтверджено: {o.For} отримує {Num(o.Shards)} 🏺");
        Counts();
        return new(true, $"Зараховано: {o.For} +{Num(o.Shards)} 🏺");
    }

    /// <summary>POST /api/shards/{id}/no { note? } — «✕ Не прийшло»: покупець бачить відмову й примітку.</summary>
    public ShardReply Reject(ShardActor me, long id, string? note)
    {
        var seller = Seller();
        if (!MayConfirm(me, seller)) return new(false, "Відмовляє продавець", 403);
        var text = (note ?? "").Trim();
        if (text.Length > NoteMax) return new(false, $"Примітка — до {NoteMax} символів");
        ShardOrder o;
        lock (_gate)
        {
            if (store.Get(id) is not { } got) return new(false, "Нема такого замовлення", 404);
            if (got.Status != "wait") return new(false, Already(got));
            o = got;
            store.Move(id, "wait", "no", clock.UtcNow, me.Nick, text);
        }
        wire.Toast(o.Buyer, $"✕ Оплату {o.Uah} грн не знайдено{(text.Length > 0 ? $": «{text}»" : "")}. Глянь, чи пішов переказ, і напиши {NickCases.Dative(seller ?? me.Nick)}");
        Counts();
        return new(true, "Позначено: не прийшло");
    }

    // ---------------------------------------------------------------- продаж: гравець

    static string SellRef(long id) => "sell:" + id;
    static string BackRef(long id) => "sell-back:" + id;

    /// <summary>POST /api/shards/sell { uah } — «продаю»: черепки відкладаються (списуються), заявка йде адміну.</summary>
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
                return new(false, $"Уже {waiting} {Plural(waiting, "заявка чекає", "заявки чекають", "заявок чекають")} адміна — дочекайся");
            sale = new ShardSale(0, me.Nick, u, shards, "wait", clock.UtcNow, null, null, null, null, "");
            sale = sale with { Id = store.AddSale(sale) };
            // Спершу заявка, тоді списання з її номером: падіння між ними лишить заявку без списання, і «✓ Скинув» адміна
            // спише ще раз (той самий ref — двічі не спише), а повернення без списання не поверне нічого
            if (!economy.TrySpend(me.Nick, shards, SellRef(sale.Id), SellRef(sale.Id),
                    $"−{Shards(shards)}: продаю за {u} грн — відкладено, поки адмін не скине гроші"))
            {
                store.DropSale(sale.Id);
                return new(false, "Черепків уже не вистачає — щось витратилось саме зараз");
            }
        }
        log.LogInformation("Черепки: {Seller} продає {Shards} за {Uah} грн, заявка {Id}", me.Nick, shards, u, sale.Id);
        wire.ToastAdmins($"💰 {me.Nick} продає {Num(shards)} 🏺 за {u} грн — скинь гроші й натисни «✓ Скинув»");
        Counts();
        return new(true, $"Виставлено: {Num(shards)} 🏺 за {u} грн. Щойно адмін скине гроші — натиснеш «✓ Отримав»", Order: View(sale));
    }

    /// <summary>POST /api/shards/sale/{id}/cancel — передумав, поки адмін не скидав грошей: черепки назад.</summary>
    public ShardReply CancelSale(ShardActor me, long id)
    {
        if (!me.Account) return new(false, SellAccountsOnly, 403);
        lock (_gate)
        {
            if (store.GetSale(id) is not { } s) return new(false, "Нема такої заявки", 404);
            if (!Same(s.Seller, me.Nick)) return new(false, "Скасувати може лише той, хто продає", 403);
            if (s.Status != "wait") return new(false, Already(s));
            if (s.PaidAt is not null) return new(false, MissingNoCancel);
            if (Refund(s, $"+{Shards(s.Shards)}: продаж скасовано — черепки назад у глечику") is { } fail) return fail;
            store.CloseSale(id, "wait", "off", clock.UtcNow, me.Nick);
        }
        Counts();
        return new(true, "Скасовано — черепки повернулись у глечик");
    }

    /// <summary>POST /api/shards/sale/{id}/ok — «✓ Отримав»: гроші прийшли, продано.</summary>
    public ShardReply SaleReceived(ShardActor me, long id)
    {
        if (!me.Account) return new(false, SellAccountsOnly, 403);
        ShardSale s;
        lock (_gate)
        {
            if (store.GetSale(id) is not { } got) return new(false, "Нема такої заявки", 404);
            if (!Same(got.Seller, me.Nick)) return new(false, "Підтверджує той, хто продає", 403);
            if (got.Status != "paid") return new(false, Already(got));
            s = got;
            store.CloseSale(id, "paid", "done", clock.UtcNow, me.Nick);
        }
        log.LogInformation("Черепки: заявку {Id} закрито — {Seller} отримав {Uah} грн", s.Id, s.Seller, s.Uah);
        wire.Sale(s.Seller);
        return new(true, $"Продано: {Num(s.Shards)} 🏺 за {s.Uah} грн");
    }

    /// <summary>POST /api/shards/sale/{id}/missing { note? } — «✕ Не прийшло»: заявка знову в адміна, з приміткою.</summary>
    public ShardReply SaleMissing(ShardActor me, long id, string? note)
    {
        if (!me.Account) return new(false, SellAccountsOnly, 403);
        var text = (note ?? "").Trim();
        if (text.Length > NoteMax) return new(false, $"Примітка — до {NoteMax} символів");
        ShardSale s;
        lock (_gate)
        {
            if (store.GetSale(id) is not { } got) return new(false, "Нема такої заявки", 404);
            if (!Same(got.Seller, me.Nick)) return new(false, "Це не твоя заявка", 403);
            if (got.Status != "paid") return new(false, Already(got));
            s = got;
            store.SaleMissing(id, text.Length > 0 ? text : "гроші не прийшли");
        }
        wire.ToastAdmins($"⚠ {s.Seller}: {s.Uah} грн за {Num(s.Shards)} 🏺 не прийшли{(text.Length > 0 ? $" — «{text}»" : "")}. Глянь переказ");
        wire.Sale(s.Seller);
        Counts();
        return new(true, "Позначено: не прийшло — адмін перевірить переказ");
    }

    // ---------------------------------------------------------------- продаж: адмін

    /// <summary>POST /api/shards/sale/{id}/paid — «✓ Скинув»: адмін скинув гроші, гравець має підтвердити.</summary>
    public ShardReply SalePaid(ShardActor me, long id)
    {
        if (!me.Admin) return new(false, AdminOnly, 403);
        ShardSale s;
        lock (_gate)
        {
            if (store.GetSale(id) is not { } got) return new(false, "Нема такої заявки", 404);
            if (got.Status != "wait") return new(false, Already(got));
            s = got;
            // Черепки мусять бути відкладені. Звичайно це повтор (той самий ref — без другого списання); якщо ж заявка пережила
            // падіння до списання, списує зараз, а не вистачило — заявку закрито
            if (!economy.TrySpend(s.Seller, s.Shards, SellRef(id), SellRef(id)))
            {
                store.CloseSale(id, "wait", "no", clock.UtcNow, me.Nick, "черепків уже нема");
                Counts();
                return new(false, $"У {NickCases.Genitive(s.Seller)} уже нема цих черепків — заявку закрито");
            }
            store.SalePaid(id, clock.UtcNow, me.Nick);
        }
        log.LogInformation("Черепки: заявка {Id} — {Who} скинув {Seller} {Uah} грн", s.Id, me.Nick, s.Seller, s.Uah);
        wire.Toast(s.Seller, $"💸 Адмін скинув {s.Uah} грн за {Num(s.Shards)} 🏺 — глянь у банк і натисни «✓ Отримав»");
        wire.Sale(s.Seller);
        Counts();
        return new(true, $"Позначено: {s.Uah} грн скинуто — чекаємо «✓ Отримав» від {NickCases.Genitive(s.Seller)}");
    }

    /// <summary>POST /api/shards/sale/{id}/no { note? } — «✕ Не куплю»: черепки повертаються гравцю.</summary>
    public ShardReply SaleRefuse(ShardActor me, long id, string? note)
    {
        if (!me.Admin) return new(false, AdminOnly, 403);
        var text = (note ?? "").Trim();
        if (text.Length > NoteMax) return new(false, $"Примітка — до {NoteMax} символів");
        ShardSale s;
        lock (_gate)
        {
            if (store.GetSale(id) is not { } got) return new(false, "Нема такої заявки", 404);
            if (got.Status != "wait") return new(false, Already(got));
            s = got;
            if (Refund(s, $"+{Shards(s.Shards)}: продаж не відбувся — черепки назад у глечику") is { } fail) return fail;
            store.CloseSale(id, "wait", "no", clock.UtcNow, me.Nick, text);
        }
        wire.Toast(s.Seller, $"✕ Адмін не купив {Num(s.Shards)} 🏺{(text.Length > 0 ? $": «{text}»" : "")} — черепки повернулись у глечик");
        wire.Sale(s.Seller);
        Counts();
        return new(true, "Позначено: не куплено, черепки повернуто");
    }

    /// <summary>
    /// Повернути відкладене. Спершу гроші з ref на заявку, тоді стан (як у <see cref="Confirm"/>): повтор не поверне двічі.
    /// Лише те, що справді списали: заявка без списання (падіння між записом і списанням) повернула б черепки з нічого.
    /// </summary>
    ShardReply? Refund(ShardSale s, string text)
    {
        var debit = SellRef(s.Id);
        if (economy.Moves(debit)?.Any(m => m.Ref == debit && m.Delta < 0) != true) return null;
        var r = economy.Grant(s.Seller, s.Shards, BackRef(s.Id), BackRef(s.Id), text);
        return r is GrantResult.Applied or GrantResult.Duplicate ? null : new(false, "Черепки не повернулись — спробуй ще раз");
    }

    static string Already(ShardSale s) => s.Status switch
    {
        "wait" => "Заявка ще чекає адміна",
        "paid" => "Адмін уже скинув гроші — глянь у банк",
        "done" => "Уже продано",
        "no" => "Адмін уже відмовив — черепки повернуто",
        "off" => "Уже скасовано",
        _ => "Уже розглянуто",
    };

    static string Already(ShardOrder o) => o.Status switch
    {
        "done" => "Уже зараховано",
        "no" => "Уже позначено, що оплата не прийшла",
        "off" => "Уже скасовано",
        _ => "Уже розглянуто",
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
    public sealed record NoteRequest(string? Note);

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
        api.MapPost("/{id:long}/ok", Confirm);
        api.MapPost("/{id:long}/no", Reject);
        api.MapPost("/{id:long}/cancel", Cancel);
        api.MapPost("/sell", Sell);
        api.MapPost("/sale/{id:long}/cancel", CancelSale);
        api.MapPost("/sale/{id:long}/ok", SaleReceived);
        api.MapPost("/sale/{id:long}/missing", SaleMissing);
        api.MapPost("/sale/{id:long}/paid", SalePaid);
        api.MapPost("/sale/{id:long}/no", SaleRefuse);
        return app;
    }

    public sealed record SellRequest(int? Uah);

    public static IResult Sell(HttpContext c, SellRequest b, ShardShop shop) => Reply(shop.Sell(ShardActor.Of(c), b.Uah));

    public static IResult CancelSale(long id, HttpContext c, ShardShop shop) => Reply(shop.CancelSale(ShardActor.Of(c), id));

    public static IResult SaleReceived(long id, HttpContext c, ShardShop shop) => Reply(shop.SaleReceived(ShardActor.Of(c), id));

    public static IResult SaleMissing(long id, HttpContext c, NoteRequest? b, ShardShop shop) => Reply(shop.SaleMissing(ShardActor.Of(c), id, b?.Note));

    public static IResult SalePaid(long id, HttpContext c, ShardShop shop) => Reply(shop.SalePaid(ShardActor.Of(c), id));

    public static IResult SaleRefuse(long id, HttpContext c, NoteRequest? b, ShardShop shop) => Reply(shop.SaleRefuse(ShardActor.Of(c), id, b?.Note));

    public static object View(HttpContext c, ShardShop shop) => shop.View(ShardActor.Of(c));

    public static IResult Check(HttpContext c, PaidRequest b, ShardShop shop) => Reply(shop.Check(ShardActor.Of(c), b.Uah, b.For));

    public static IResult Paid(HttpContext c, PaidRequest b, ShardShop shop) => Reply(shop.Paid(ShardActor.Of(c), b.Uah, b.For));

    public static IResult Confirm(long id, HttpContext c, ShardShop shop) => Reply(shop.Confirm(ShardActor.Of(c), id));

    public static IResult Reject(long id, HttpContext c, NoteRequest? b, ShardShop shop) => Reply(shop.Reject(ShardActor.Of(c), id, b?.Note));

    public static IResult Cancel(long id, HttpContext c, ShardShop shop) => Reply(shop.Cancel(ShardActor.Of(c), id));

    static IResult Reply(ShardReply r)
    {
        var body = new { ok = r.Ok, message = r.Message, order = r.Order };
        return r.Ok ? Results.Ok(body) : Results.Json(body, statusCode: r.Status == 200 ? 400 : r.Status);
    }
}
