using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Padel;
using Hlechyky.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Купити черепки за гривні (ShardShop.cs): «✓ Скинув» покупця → «чекають» продавця → «✓ Отримав» (черепки падають)
/// чи «✕ Не прийшло»; подарунок другові; скасування; повторні натискання; куплене не йде в «зароблено» й у «Сотню».
/// Справжня економіка на тимчасовій базі (EconomyRig), банки й розсилка — заглушки.
/// </summary>
public sealed class ShardShopTests : IDisposable
{
    sealed class FakeBanks : IShardBanks
    {
        public Dictionary<string, List<PadelBank>> Map { get; } = new(StringComparer.Ordinal);
        public IReadOnlyList<PadelBank> Of(string nick) => Map.TryGetValue(Auth.NickKey(nick), out var b) ? b : [];
    }

    sealed class FakeWire : IShardShopWire
    {
        public List<(string Nick, string Text)> Toasts { get; } = [];
        public List<(string? Seller, int Orders, int Sales)> Counts { get; } = [];
        public List<string> AdminToasts { get; } = [];
        public List<string> Sales { get; } = [];
        public void Toast(string nick, string text) => Toasts.Add((nick, text));
        public void ToastAdmins(string text) => AdminToasts.Add(text);
        public void Waiting(string? seller, int orders, int sales) => Counts.Add((seller, orders, sales));
        public void Sale(string nick) => Sales.Add(nick);
    }

    readonly EconomyRig _eco = new();
    readonly FakeBanks _banks = new();
    readonly FakeWire _wire = new();
    readonly ShardShopOptions _opts = new() { Seller = "Влад" };
    readonly ShardShop _shop;

    static readonly ShardActor Vlad = new("Влад", true, false);
    static readonly ShardActor Olia = new("Оля", true, false);
    static readonly ShardActor Petro = new("Петро", true, false);

    public ShardShopTests()
    {
        Assert.True(_eco.Db.AddAccount("Влад", "", "salt"));
        Assert.True(_eco.Db.AddAccount("Оля", "", "salt"));
        Assert.True(_eco.Db.AddAccount("Петро", "", "salt"));
        _banks.Map["влад"] = [new PadelBank("b1", "mono", "Чорна", "4441111111111111", "https://send.monobank.ua/jar/abc")];
        _shop = Fresh();
    }

    ShardShop Fresh() => new(new ShardShopStore(_eco.Db), _eco.Economy, _eco.Db, _banks, _wire, _eco.Clock,
        new FixedOptions<ShardShopOptions>(_opts), NullLogger<ShardShop>.Instance);

    public void Dispose() => _eco.Dispose();

    long Order(ShardReply r)
    {
        Assert.True(r.Ok, r.Message);
        return Views.Json(r.Order).GetProperty("id").GetInt64();
    }

    static HttpContext As(string nick, bool account = true, bool admin = false)
    {
        var c = Radio.As(nick, admin);
        if (account) c.Items["account"] = new Account(nick, "", "salt", "member");
        return c;
    }

    // =============================================================================================
    // Вітрина
    // =============================================================================================

    [Fact]
    public void View_gives_packs_at_the_rate_and_the_sellers_banks_to_accounts_only()
    {
        var v = Views.Json(_shop.View(Olia));
        Assert.True(v.GetProperty("open").GetBoolean());
        Assert.Equal(100, v.GetProperty("rate").GetInt32());
        Assert.Equal([50, 100, 250, 500], v.GetProperty("packs").EnumerateArray().Select(p => p.GetProperty("uah").GetInt32()));
        Assert.Equal([5000, 10000, 25000, 50000], v.GetProperty("packs").EnumerateArray().Select(p => p.GetProperty("shards").GetInt32()));
        var seller = v.GetProperty("seller");
        Assert.Equal("Влад", seller.GetProperty("nick").GetString());
        Assert.Equal("4441111111111111", seller.GetProperty("banks")[0].GetProperty("card").GetString());
        Assert.False(v.GetProperty("canConfirm").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, v.GetProperty("waiting").ValueKind);

        // Гість сайту бачить пакети, але не реквізити
        var guest = Views.Json(_shop.View(new ShardActor("гість Вася", false, false)));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, guest.GetProperty("seller").GetProperty("banks").ValueKind);
    }

    [Fact]
    public void No_seller_in_config_or_seller_without_account_means_closed()
    {
        _opts.Seller = "";
        Assert.False(Views.Json(_shop.View(Olia)).GetProperty("open").GetBoolean());
        Assert.Equal(ShardShop.Closed, _shop.Paid(Olia, 50, null).Message);
        _opts.Seller = "Незнайомець";
        Assert.False(Views.Json(_shop.View(Olia)).GetProperty("open").GetBoolean());
    }

    [Fact]
    public void Packs_from_config_replace_the_defaults_instead_of_adding_to_them()
    {
        Assert.Equal([50, 100, 250, 500], new ShardShopOptions().PackList);
        Assert.Equal([20, 50], new ShardShopOptions { Packs = [50, 20, 50, 0] }.PackList);
    }

    // =============================================================================================
    // Покупець
    // =============================================================================================

    [Fact]
    public void Paid_puts_the_order_into_waiting_and_tells_the_seller()
    {
        var id = Order(_shop.Paid(Olia, 100, null));
        Assert.Equal(0, _eco.Economy.Balance("Оля"));   // гроші ще не підтверджено
        var t = Assert.Single(_wire.Toasts);
        Assert.Equal("Влад", t.Nick);
        Assert.Contains("Від Олі", t.Text);
        Assert.Contains("100 грн", t.Text);
        Assert.Equal(("Влад", 1, 0), _wire.Counts[^1]);

        var mine = Views.Json(_shop.View(Olia)).GetProperty("mine");
        Assert.Equal(id, mine[0].GetProperty("id").GetInt64());
        Assert.Equal("wait", mine[0].GetProperty("status").GetString());
        Assert.Equal(10_000, mine[0].GetProperty("shards").GetInt32());

        var seller = Views.Json(_shop.View(Vlad));
        Assert.True(seller.GetProperty("isSeller").GetBoolean());
        Assert.Equal(id, seller.GetProperty("waiting")[0].GetProperty("id").GetInt64());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-50)]
    [InlineData(9)]
    [InlineData(5001)]
    public void Own_amount_only_within_bounds(int? uah)
    {
        var r = _shop.Paid(Olia, uah, null);
        Assert.False(r.Ok);
        Assert.Equal("Сума — від 10 до 5\u00a0000 грн, цілими гривнями", r.Message);
    }

    [Fact]
    public void Own_amount_buys_at_the_same_rate()
    {
        var c = Views.Json(_shop.View(Olia)).GetProperty("custom");
        Assert.Equal((10, 5000), (c.GetProperty("min").GetInt32(), c.GetProperty("max").GetInt32()));
        var id = Order(_shop.Paid(Olia, 75, null));
        Assert.True(_shop.Confirm(Vlad, id).Ok);
        Assert.Equal(7_500, _eco.Economy.Balance("Оля"));
        Assert.True(_shop.Paid(Olia, 10, null).Ok);
        Assert.True(_shop.Paid(Olia, 5000, null).Ok);
    }

    [Fact]
    public void Without_own_amount_only_packs()
    {
        _opts.CustomMax = 0;
        Assert.Equal(System.Text.Json.JsonValueKind.Null, Views.Json(_shop.View(Olia)).GetProperty("custom").ValueKind);
        Assert.Contains("пакета", _shop.Paid(Olia, 75, null).Message);
        Assert.True(_shop.Paid(Olia, 250, null).Ok);
    }

    [Fact]
    public void Guests_and_the_seller_himself_do_not_buy()
    {
        var guest = _shop.Paid(new ShardActor("гість Вася", false, false), 50, null);
        Assert.Equal((false, 403), (guest.Ok, guest.Status));
        Assert.False(_shop.Paid(Vlad, 50, null).Ok);
        Assert.False(_shop.Paid(Vlad, 50, "Оля").Ok);
    }

    [Fact]
    public void Waiting_orders_of_one_buyer_are_capped()
    {
        for (var i = 0; i < 3; i++) Order(_shop.Paid(Olia, 50, null));
        var r = _shop.Paid(Olia, 50, null);
        Assert.False(r.Ok);
        Assert.Contains("Уже 3 оплати чекають", r.Message);
        Order(_shop.Paid(Petro, 50, null));   // в іншого — своя межа
    }

    [Fact]
    public void Check_before_the_bank_details_says_the_same_as_paid_and_records_nothing()
    {
        Assert.Contains("не акаунт", _shop.Check(Olia, 50, "Вася").Message);
        Assert.Contains("від 10 до", _shop.Check(Olia, 7, null).Message);
        Assert.Equal(403, _shop.Check(new ShardActor("гість Вася", false, false), 50, null).Status);
        var ok = _shop.Check(Olia, 50, "петро");
        Assert.True(ok.Ok, ok.Message);
        var v = Views.Json(ok.Order);
        Assert.Equal(("Петро", true, 5_000), (v.GetProperty("for").GetString(), v.GetProperty("gift").GetBoolean(), v.GetProperty("shards").GetInt32()));
        Assert.Empty(Views.Json(_shop.View(Olia)).GetProperty("mine").EnumerateArray());
        Assert.Empty(_wire.Toasts);

        for (var i = 0; i < 3; i++) Order(_shop.Paid(Olia, 50, null));
        Assert.Contains("Уже 3 оплати чекають", _shop.Check(Olia, 50, null).Message);
    }

    [Fact]
    public void Buyer_cancels_only_own_waiting_order()
    {
        var id = Order(_shop.Paid(Olia, 50, null));
        Assert.Equal(403, _shop.Cancel(Petro, id).Status);
        Assert.True(_shop.Cancel(Olia, id).Ok);
        Assert.Equal("Уже скасовано", _shop.Cancel(Olia, id).Message);
        Assert.False(_shop.Confirm(Vlad, id).Ok);
        Assert.Equal(0, _eco.Economy.Balance("Оля"));
        Assert.Equal(("Влад", 0, 0), _wire.Counts[^1]);
    }

    // =============================================================================================
    // Продавець
    // =============================================================================================

    [Fact]
    public void Seller_confirms_and_shards_land_once()
    {
        var id = Order(_shop.Paid(Olia, 250, null));
        Assert.Equal(403, _shop.Confirm(Petro, id).Status);
        Assert.Equal(403, _shop.Confirm(Olia, id).Status);

        var r = _shop.Confirm(Vlad, id);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(25_000, _eco.Economy.Balance("Оля"));
        var w = Assert.Single(_eco.Outbox.Of<WalletChanged>(), x => x.Nick == "Оля");
        Assert.Equal($"buy:{id}", w.Reason);
        Assert.Equal("Лови +25\u00a0000 черепків: куплено за 250 грн", w.Text);

        // Друга вкладка, подвійний тиць — не вдруге
        Assert.Equal("Уже зараховано", _shop.Confirm(Vlad, id).Message);
        Assert.Equal("Уже зараховано", _shop.Reject(Vlad, id, null).Message);
        Assert.Equal(25_000, _eco.Economy.Balance("Оля"));
        Assert.Equal("done", Views.Json(_shop.View(Olia)).GetProperty("mine")[0].GetProperty("status").GetString());
        Assert.Equal(250, Views.Json(_shop.View(Vlad)).GetProperty("monthUah").GetInt32());
    }

    [Fact]
    public void Admin_cookie_confirms_too()
    {
        var id = Order(_shop.Paid(Olia, 50, null));
        Assert.True(_shop.Confirm(new ShardActor("гість Адмін", false, true), id).Ok);
        Assert.Equal(5_000, _eco.Economy.Balance("Оля"));
    }

    [Fact]
    public void Confirm_after_a_crash_between_grant_and_status_does_not_pay_twice()
    {
        var id = Order(_shop.Paid(Olia, 50, null));
        // Гроші вже лягли (той самий ref), а стан лишився «чекає» — як після падіння процесу посередині
        _eco.Economy.Grant("Оля", 5_000, $"buy:{id}", $"buy:{id}");
        Assert.True(Fresh().Confirm(Vlad, id).Ok);
        Assert.Equal(5_000, _eco.Economy.Balance("Оля"));
    }

    [Fact]
    public void Gift_goes_to_the_friend_and_the_buyer_hears_it_landed()
    {
        var id = Order(_shop.Paid(Olia, 50, "петро"));
        Assert.Contains("для Петра", _wire.Toasts[^1].Text);
        var mine = Views.Json(_shop.View(Petro)).GetProperty("mine")[0];   // Петро бачить, що йому купують
        Assert.Equal("Петро", mine.GetProperty("for").GetString());
        Assert.True(mine.GetProperty("gift").GetBoolean());

        Assert.True(_shop.Confirm(Vlad, id).Ok);
        Assert.Equal(5_000, _eco.Economy.Balance("Петро"));
        Assert.Equal(0, _eco.Economy.Balance("Оля"));
        var w = Assert.Single(_eco.Outbox.Of<WalletChanged>(), x => x.Nick == "Петро");
        Assert.Equal($"buy-gift:{id}", w.Reason);
        Assert.Contains("подарунок від Олі", w.Text);
        Assert.Equal("Оля", _wire.Toasts[^1].Nick);
        Assert.Contains("Петро отримує", _wire.Toasts[^1].Text);
    }

    [Fact]
    public void Gift_only_to_an_account()
    {
        var r = _shop.Paid(Olia, 50, "Вася");
        Assert.False(r.Ok);
        Assert.Contains("не акаунт", r.Message);
        // Собі за власним ніком — це не подарунок
        var id = Order(_shop.Paid(Olia, 50, "оля"));
        Assert.False(Views.Json(_shop.View(Olia)).GetProperty("mine")[0].GetProperty("gift").GetBoolean());
        Assert.True(_shop.Confirm(Vlad, id).Ok);
        Assert.Equal($"buy:{id}", _eco.Outbox.Of<WalletChanged>().Single().Reason);
    }

    [Fact]
    public void Reject_tells_the_buyer_with_the_note()
    {
        var id = Order(_shop.Paid(Olia, 100, null));
        Assert.Equal(403, _shop.Reject(Petro, id, null).Status);
        Assert.False(_shop.Reject(Vlad, id, new string('я', 101)).Ok);
        Assert.True(_shop.Reject(Vlad, id, "на картку нічого").Ok);
        var t = _wire.Toasts[^1];
        Assert.Equal("Оля", t.Nick);
        Assert.Contains("«на картку нічого»", t.Text);
        Assert.Contains("Владові", t.Text);
        Assert.False(_shop.Confirm(Vlad, id).Ok);
        Assert.Equal(0, _eco.Economy.Balance("Оля"));
        var o = Views.Json(_shop.View(Olia)).GetProperty("mine")[0];
        Assert.Equal(("no", "на картку нічого"), (o.GetProperty("status").GetString(), o.GetProperty("note").GetString()));
    }

    // =============================================================================================
    // Куплене — не «зароблено»
    // =============================================================================================

    [Fact]
    public void Bought_shards_are_in_balance_but_not_in_earned_tables_or_the_hundred()
    {
        var id = Order(_shop.Paid(Olia, 50, null));
        Assert.True(_shop.Confirm(Vlad, id).Ok);
        _eco.Economy.Grant("Петро", 30, "listen", "test:p");

        var w = _eco.Economy.Wallet("Оля");
        Assert.Equal((5_000, 0), (w.Balance, w.Earned));
        Assert.False(_eco.Achievements.Has("Оля", "rich-100"));
        // Таблиця «заробив»: Оля з куплених 5000 не випереджає Петра з чесних 30
        Assert.Equal("Петро", _eco.Economy.Top(5, "earned")[0].Nick);
        Assert.Equal(0, _eco.Store.TopShards(5, DateTimeOffset.MinValue, byBalance: false).Single(r => r.Nick == "Оля").Earned);

        // Перерахунок гаманців із леджера дає те саме
        _eco.Economy.Rebuild();
        w = _eco.Economy.Wallet("Оля");
        Assert.Equal((5_000, 0), (w.Balance, w.Earned));

        // Сотня — коли набрано грою; куплене на руках не заважає
        _eco.Economy.Grant("Оля", 100, "listen", "test:o");
        Assert.True(_eco.Achievements.Has("Оля", "rich-100"));
    }

    [Fact]
    public void Ledger_reasons_read_like_people_talk()
    {
        Assert.Equal("куплено за гривні", _eco.Economy.Reason("buy:7"));
        Assert.Equal("подарунок — куплені черепки", _eco.Economy.Reason("buy-gift:7"));
        Assert.Equal("куплено за гривні", PeopleEndpoints.CatTitle(PeopleEndpoints.Cat("buy:7")));
        Assert.True(EconomyStore.Exchange("buy-gift:3"));
        Assert.False(EconomyStore.Exchange("table-buyin:poker"));
    }

    // =============================================================================================
    // HTTP
    // =============================================================================================

    [Fact]
    public void Routes_answer_with_ok_and_message()
    {
        var (status, body) = Radio.Reply(ShardShopSetup.Paid(As("Оля"), new(100, null), _shop));
        Assert.Equal(200, status);
        Assert.True(body.GetProperty("ok").GetBoolean());
        var id = body.GetProperty("order").GetProperty("id").GetInt64();

        (status, body) = Radio.Reply(ShardShopSetup.Paid(As("гість Вася", account: false), new(100, null), _shop));
        Assert.Equal(403, status);
        Assert.Equal(ShardShop.AccountsOnly, body.GetProperty("message").GetString());

        (status, body) = Radio.Reply(ShardShopSetup.Confirm(id, As("Петро"), _shop));
        Assert.Equal(403, status);
        (status, _) = Radio.Reply(ShardShopSetup.Confirm(id, As("Влад"), _shop));
        Assert.Equal(200, status);
        (status, body) = Radio.Reply(ShardShopSetup.Cancel(id, As("Оля"), _shop));
        Assert.Equal(400, status);
        Assert.Equal("Уже зараховано", body.GetProperty("message").GetString());
        (status, _) = Radio.Reply(ShardShopSetup.Reject(999, As("Влад"), null, _shop));
        Assert.Equal(404, status);
    }

    // =============================================================================================
    // Продаж: гравець виставляє → адмін «✓ Скинув» / «✕ Не куплю» → гравець «✓ Отримав» / «✕ Не прийшло»
    // =============================================================================================

    static readonly ShardActor Admin = new("гість Адмін", false, true);

    /// <summary>Оля з чесно заробленими 20 000 🏺 і своєю карткою в Падельні.</summary>
    int _b0;

    void OliaCanSell()
    {
        _eco.Economy.Grant("Оля", 20_000, "listen", "test:seed");
        _b0 = _eco.Economy.Balance("Оля");   // 20 000 і ще нагорода за «Сотню»
        _banks.Map["оля"] = [new PadelBank("b2", "privat", "", "5168111111111111", null)];
    }

    static string Status(System.Text.Json.JsonElement v, long id) =>
        v.EnumerateArray().Single(x => x.GetProperty("id").GetInt64() == id).GetProperty("status").GetString()!;

    string MySale(ShardActor me, long id) => Status(Views.Json(_shop.View(me)).GetProperty("sell").GetProperty("mine"), id);

    [Fact]
    public void Sell_holds_the_shards_at_once_and_tells_the_admins()
    {
        OliaCanSell();
        var id = Order(_shop.Sell(Olia, 100));
        Assert.Equal(_b0 - 10_000, _eco.Economy.Balance("Оля"));   // відкладено: витратити вже не вийде
        var w = _eco.Economy.Wallet("Оля");
        Assert.Equal((_b0, 0), (w.Earned, w.Spent));          // продаж — не гра: ні «зароблено», ні «витрачено»
        Assert.Contains("Оля продає 10", Assert.Single(_wire.AdminToasts));
        Assert.Equal(("Влад", 0, 1), _wire.Counts[^1]);
        Assert.Equal("wait", MySale(Olia, id));

        // Адмін бачить заявку разом із карткою Олі; гравцям адмінської каси не видно
        var desk = Views.Json(_shop.View(Admin)).GetProperty("sales");
        var row = Assert.Single(desk.GetProperty("waiting").EnumerateArray());
        Assert.Equal(("Оля", 100, 10_000), (row.GetProperty("seller").GetString(), row.GetProperty("uah").GetInt32(), row.GetProperty("shards").GetInt32()));
        Assert.Equal("5168111111111111", row.GetProperty("banks")[0].GetProperty("card").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, Views.Json(_shop.View(Olia)).GetProperty("sales").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, Views.Json(_shop.View(Vlad)).GetProperty("sales").ValueKind);

        var sell = Views.Json(_shop.View(Olia)).GetProperty("sell");
        Assert.True(sell.GetProperty("open").GetBoolean());
        Assert.Equal((100, 10, _b0 - 10_000), (sell.GetProperty("rate").GetInt32(), sell.GetProperty("min").GetInt32(), sell.GetProperty("balance").GetInt32()));
    }

    [Fact]
    public void Sell_is_for_accounts_with_a_card_and_enough_shards()
    {
        OliaCanSell();
        Assert.Equal(ShardShop.SellAccountsOnly, _shop.Sell(new ShardActor("гість Вася", false, false), 50).Message);
        _eco.Economy.Grant("Петро", 20_000, "listen", "test:p");
        Assert.Equal(ShardShop.NoBanks, _shop.Sell(Petro, 50).Message);   // без картки адмін не знає, куди скинути
        Assert.Contains("від 10 грн", _shop.Sell(Olia, 5).Message);
        Assert.Contains("від 10 грн", _shop.Sell(Olia, null).Message);
        Assert.Contains("вистачить на 200 грн", _shop.Sell(Olia, 300).Message);
        Assert.Equal(_b0, _eco.Economy.Balance("Оля"));

        _opts.Sell = false;
        Assert.Equal(ShardShop.SellClosed, _shop.Sell(Olia, 50).Message);
        _opts.Sell = true;

        // До трьох заявок водночас; четверта — коли адмін розгляне якусь
        for (var i = 0; i < 3; i++) Order(_shop.Sell(Olia, 10));
        Assert.Contains("заявки чекають адміна", _shop.Sell(Olia, 10).Message);
        Assert.Equal(_b0 - 3_000, _eco.Economy.Balance("Оля"));
    }

    [Fact]
    public void Admin_pays_then_the_player_confirms_and_the_shards_are_gone()
    {
        OliaCanSell();
        var id = Order(_shop.Sell(Olia, 50));
        Assert.Equal(ShardShop.AdminOnly, _shop.SalePaid(Vlad, id).Message);   // продавець купівлі — ще не адмін
        Assert.False(_shop.SaleReceived(Olia, id).Ok);                        // гроші ще не скинуто

        Assert.True(_shop.SalePaid(Admin, id).Ok);
        Assert.Equal("paid", MySale(Olia, id));
        Assert.Contains("Адмін скинув 50 грн", _wire.Toasts[^1].Text);
        Assert.Equal("Оля", _wire.Toasts[^1].Nick);
        Assert.Contains("Оля", _wire.Sales);
        Assert.Equal(("Влад", 0, 0), _wire.Counts[^1]);
        Assert.Equal("Адмін уже скинув гроші — глянь у банк", _shop.CancelSale(Olia, id).Message);
        Assert.False(_shop.SalePaid(Admin, id).Ok);
        Assert.False(_shop.SaleReceived(Petro, id).Ok);

        Assert.True(_shop.SaleReceived(Olia, id).Ok);
        Assert.Equal("done", MySale(Olia, id));
        Assert.Equal(_b0 - 5_000, _eco.Economy.Balance("Оля"));
        Assert.Equal(50, Views.Json(_shop.View(Admin)).GetProperty("sales").GetProperty("monthUah").GetInt32());

        // Перерахунок із леджера: баланс той самий, «зароблено» й «витрачено» продаж не чіпає
        _eco.Economy.Rebuild();
        var w = _eco.Economy.Wallet("Оля");
        Assert.Equal((_b0 - 5_000, _b0, 0), (w.Balance, w.Earned, w.Spent));
    }

    [Fact]
    public void Cancel_or_refuse_returns_the_shards_exactly_once()
    {
        OliaCanSell();
        var id = Order(_shop.Sell(Olia, 100));
        Assert.Equal("Скасувати може лише той, хто продає", _shop.CancelSale(Petro, id).Message);
        Assert.True(_shop.CancelSale(Olia, id).Ok);
        Assert.Equal(_b0, _eco.Economy.Balance("Оля"));
        Assert.Equal("Уже скасовано", _shop.CancelSale(Olia, id).Message);
        Assert.Equal("off", MySale(Olia, id));

        var id2 = Order(_shop.Sell(Olia, 100));
        Assert.Equal(ShardShop.AdminOnly, _shop.SaleRefuse(Olia, id2, null).Message);
        Assert.True(_shop.SaleRefuse(Admin, id2, "зараз нема грошей").Ok);
        Assert.Equal(_b0, _eco.Economy.Balance("Оля"));
        Assert.Contains("«зараз нема грошей»", _wire.Toasts[^1].Text);
        Assert.False(_shop.SaleRefuse(Admin, id2, null).Ok);
        Assert.False(_shop.CancelSale(Olia, id2).Ok);
        Assert.Equal(_b0, _eco.Economy.Balance("Оля"));

        _eco.Economy.Rebuild();
        var w = _eco.Economy.Wallet("Оля");
        Assert.Equal((_b0, _b0, 0), (w.Balance, w.Earned, w.Spent));   // повернене — не «зароблено» вдруге
    }

    [Fact]
    public void Not_received_sends_the_sale_back_to_the_admin_with_a_note()
    {
        OliaCanSell();
        var id = Order(_shop.Sell(Olia, 30));
        Assert.True(_shop.SalePaid(Admin, id).Ok);
        Assert.False(_shop.SaleMissing(Petro, id, null).Ok);
        Assert.True(_shop.SaleMissing(Olia, id, "на картці пусто").Ok);
        Assert.Contains("не прийшли — «на картці пусто»", _wire.AdminToasts[^1]);
        Assert.Equal(("Влад", 0, 1), _wire.Counts[^1]);
        var row = Views.Json(_shop.View(Admin)).GetProperty("sales").GetProperty("waiting")[0];
        Assert.Equal(("wait", "на картці пусто"), (row.GetProperty("status").GetString(), row.GetProperty("note").GetString()));
        Assert.Equal(_b0 - 3_000, _eco.Economy.Balance("Оля"));   // черепки так і лежать відкладені
        Assert.Equal(ShardShop.MissingNoCancel, _shop.CancelSale(Olia, id).Message);   // гроші скидали — вирішує адмін

        Assert.True(_shop.SalePaid(Admin, id).Ok);              // знайшов помилку й скинув ще раз
        Assert.Equal("", Views.Json(_shop.View(Olia)).GetProperty("sell").GetProperty("mine")[0].GetProperty("note").GetString());
        Assert.True(_shop.SaleReceived(Olia, id).Ok);
        Assert.Equal(_b0 - 3_000, _eco.Economy.Balance("Оля"));
    }

    [Fact]
    public void A_sale_that_lost_its_debit_is_debited_on_paid_and_never_refunded_from_nothing()
    {
        OliaCanSell();
        var store = new ShardShopStore(_eco.Db);
        // Як після падіння між записом заявки і списанням: заявка є, черепки на місці
        var lost = store.AddSale(new ShardSale(0, "Оля", 10, 1_000, "wait", _eco.Clock.UtcNow, null, null, null, null, ""));
        Assert.True(_shop.SaleRefuse(Admin, lost, null).Ok);
        Assert.Equal(_b0, _eco.Economy.Balance("Оля"));   // повертати нічого — нічого й не впало

        var late = store.AddSale(new ShardSale(0, "Оля", 10, 1_000, "wait", _eco.Clock.UtcNow, null, null, null, null, ""));
        Assert.True(_shop.SalePaid(Admin, late).Ok);
        Assert.Equal(_b0 - 1_000, _eco.Economy.Balance("Оля"));   // списано зараз, один раз

        var tooMuch = store.AddSale(new ShardSale(0, "Оля", 1_000, 100_000, "wait", _eco.Clock.UtcNow, null, null, null, null, ""));
        Assert.Contains("уже нема цих черепків", _shop.SalePaid(Admin, tooMuch).Message);
        Assert.Equal("no", MySale(Olia, tooMuch));
        Assert.Equal(_b0 - 1_000, _eco.Economy.Balance("Оля"));
    }

    [Fact]
    public void Sale_ledger_reasons_read_like_people_talk_and_stay_out_of_earned_and_spent()
    {
        Assert.Equal("продано за гривні", _eco.Economy.Reason("sell:3"));
        Assert.Equal("повернуто — продаж не відбувся", _eco.Economy.Reason("sell-back:3"));
        Assert.Equal("повернуто з продажу", PeopleEndpoints.CatTitle(PeopleEndpoints.Cat("sell-back:3")));
        Assert.True(EconomyStore.Exchange("sell:3"));
        Assert.True(EconomyStore.Exchange("sell-back:3"));
        Assert.False(EconomyStore.Exchange("seller-bonus:3"));
    }

    [Fact]
    public void Sale_routes_answer_with_ok_and_message()
    {
        OliaCanSell();
        var (status, body) = Radio.Reply(ShardShopSetup.Sell(As("Оля"), new(50), _shop));
        Assert.Equal(200, status);
        var id = body.GetProperty("order").GetProperty("id").GetInt64();

        (status, body) = Radio.Reply(ShardShopSetup.Sell(As("гість Вася", account: false), new(50), _shop));
        Assert.Equal(403, status);
        Assert.Equal(ShardShop.SellAccountsOnly, body.GetProperty("message").GetString());

        (status, _) = Radio.Reply(ShardShopSetup.SalePaid(id, As("Петро"), _shop));
        Assert.Equal(403, status);
        (status, _) = Radio.Reply(ShardShopSetup.SalePaid(id, As("гість Адмін", account: false, admin: true), _shop));
        Assert.Equal(200, status);
        (status, _) = Radio.Reply(ShardShopSetup.SaleReceived(id, As("Оля"), _shop));
        Assert.Equal(200, status);
        (status, body) = Radio.Reply(ShardShopSetup.CancelSale(id, As("Оля"), _shop));
        Assert.Equal(400, status);
        Assert.Equal("Уже продано", body.GetProperty("message").GetString());
        (status, _) = Radio.Reply(ShardShopSetup.SaleRefuse(999, As("гість Адмін", account: false, admin: true), null, _shop));
        Assert.Equal(404, status);
    }
}
