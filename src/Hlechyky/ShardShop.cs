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
// другові; куплене не йде в «зароблено» (таблиці й ачівки — лише за гру). Черепки назад у гривні не міняються ніколи:
// інакше гра на черепки стала б грою на гроші.
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

    public int[] PackList => Packs is { Length: > 0 } p ? [.. p.Where(x => x > 0).Distinct().Order()] : DefaultPacks;
    public bool Custom => CustomMax > 0 && CustomMax >= CustomMin;

    /// <summary>Пакет або своя сума в межах.</summary>
    public bool Allows(int uah) => uah > 0 && (PackList.Contains(uah) || (Custom && uah >= Math.Max(1, CustomMin) && uah <= CustomMax));
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

/// <summary>Сховище замовлень. DDL і SQL — тут.</summary>
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
        """;
    const string Cols = "id, buyer, for_nick, uah, shards, status, at, done_at, done_by, note";

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

/// <summary>Сповіщення: тост людині й лічильник «чекають» продавцю та адмінам (кружечок на гаманці без F5).</summary>
public interface IShardShopWire
{
    void Toast(string nick, string text);
    void Waiting(string seller, int count);
}

/// <summary>
/// Через хаб радіо: тост — подія <c>toast</c> (core.js її вже показує), лічильник — <c>shardOrders</c> на вкладки
/// продавця і в групу адмінів записок (<see cref="FeedbackDevGroup"/>): адмінська кука теж підтверджує.
/// </summary>
public sealed class HubShardShopWire(IHubContext<RadioHub> hub, Presence presence, ILogger<HubShardShopWire> log) : IShardShopWire
{
    public void Toast(string nick, string text)
    {
        var ids = presence.ConnectionsOf(nick);
        if (ids.Count > 0) _ = SendAsync(hub.Clients.Clients(ids), "toast", new { text, kind = "ok" });
    }

    public void Waiting(string seller, int count)
    {
        var payload = new { count };
        var ids = presence.ConnectionsOf(seller);
        if (ids.Count > 0) _ = SendAsync(hub.Clients.Clients(ids), "shardOrders", payload);
        _ = SendAsync(hub.Clients.Group(FeedbackDevGroup.Name), "shardOrders", payload);
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

/// <summary>Правила купівлі: хто може купити, кому, скільки чекає, хто підтверджує.</summary>
public sealed class ShardShop(ShardShopStore store, Economy economy, Db db, IShardBanks banks, IShardShopWire wire,
    IClock clock, IOptionsMonitor<ShardShopOptions> opts, ILogger<ShardShop> log)
{
    public const string AccountsOnly = "Черепки купують лише акаунти — закріпи нік";
    public const string Closed = "Купівля черепків ще не відкрита";
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
        wire.Waiting(seller, store.Waiting().Count);
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
        if (Seller() is { } seller) wire.Waiting(seller, store.Waiting().Count);
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
        if (seller is not null) wire.Waiting(seller, store.Waiting().Count);
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
        if (seller is not null) wire.Waiting(seller, store.Waiting().Count);
        return new(true, "Позначено: не прийшло");
    }

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
        return app;
    }

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
