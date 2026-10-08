using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Padel;
using Hlechyky.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Черепки за гривні (ShardShop.cs): купити й продати за однією схемою — людина створює заявку («в обробці», можна
/// скасувати), адмін підтверджує (статус міняється, скасувати вже не можна). Купівля: черепки падають на «Підтвердити»,
/// подарунок другові, куплене не йде в «зароблено» й у «Сотню». Продаж: черепки відкладаються одразу, на «Підтвердити»
/// зникають, скасування повертає рівно раз. Справжня економіка на тимчасовій базі (EconomyRig), банки й розсилка — заглушки.
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
        public List<string> AdminToasts { get; } = [];
        public List<(int Orders, int Sales)> Counts { get; } = [];
        public List<string> Refreshed { get; } = [];
        public void Toast(string nick, string text) => Toasts.Add((nick, text));
        public void ToastAdmins(string text) => AdminToasts.Add(text);
        public void Waiting(int orders, int sales) => Counts.Add((orders, sales));
        public void Refresh(string nick) => Refreshed.Add(nick);
    }

    readonly EconomyRig _eco = new();
    readonly FakeBanks _banks = new();
    readonly FakeWire _wire = new();
    readonly ShardShopOptions _opts = new();
    readonly ShardShop _shop;

    static readonly ShardActor Vlad = new("Влад", true, false);
    static readonly ShardActor Olia = new("Оля", true, false);
    static readonly ShardActor Petro = new("Петро", true, false);
    static readonly ShardActor Admin = new("гість Адмін", false, true);
    static readonly PadelMoney.BankRequest SiteCard = new(null, "mono", "Чорна", "4111 1111 1111 1111", "https://send.monobank.ua/jar/abc");

    public ShardShopTests()
    {
        Assert.True(_eco.Db.AddAccount("Влад", "", "salt"));
        Assert.True(_eco.Db.AddAccount("Оля", "", "salt"));
        Assert.True(_eco.Db.AddAccount("Петро", "", "salt"));
        _shop = Fresh();
        Assert.True(_shop.SetBanks(Admin, [SiteCard]).Ok);   // куди покупцям скидати
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

    System.Text.Json.JsonElement Buy(ShardActor me) => Views.Json(_shop.View(me)).GetProperty("buy");
    System.Text.Json.JsonElement SellView(ShardActor me) => Views.Json(_shop.View(me)).GetProperty("sell");
    System.Text.Json.JsonElement Desk() => Views.Json(_shop.View(Admin)).GetProperty("desk");

    // =============================================================================================
    // Вітрина й картки сайту
    // =============================================================================================

    [Fact]
    public void View_gives_packs_and_the_site_card_to_accounts_and_the_desk_only_to_the_admin()
    {
        var b = Buy(Olia);
        Assert.True(b.GetProperty("open").GetBoolean());
        Assert.Equal(100, b.GetProperty("rate").GetInt32());
        Assert.Equal([50, 100, 250, 500], b.GetProperty("packs").EnumerateArray().Select(p => p.GetProperty("uah").GetInt32()));
        Assert.Equal([5000, 10000, 25000, 50000], b.GetProperty("packs").EnumerateArray().Select(p => p.GetProperty("shards").GetInt32()));
        Assert.Equal("4111111111111111", b.GetProperty("banks")[0].GetProperty("card").GetString());

        var olia = Views.Json(_shop.View(Olia));
        Assert.False(olia.GetProperty("admin").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, olia.GetProperty("desk").ValueKind);
        Assert.True(Views.Json(_shop.View(Admin)).GetProperty("admin").GetBoolean());

        // Гість сайту бачить пакети, але не реквізити
        Assert.Equal(System.Text.Json.JsonValueKind.Null, Buy(new ShardActor("гість Вася", false, false)).GetProperty("banks").ValueKind);
    }

    [Fact]
    public void Without_a_site_card_or_with_buy_off_buying_is_closed()
    {
        Assert.True(_shop.SetBanks(Admin, []).Ok);
        Assert.False(Buy(Olia).GetProperty("open").GetBoolean());
        Assert.Equal(ShardShop.Closed, _shop.Paid(Olia, 50, null).Message);

        Assert.True(_shop.SetBanks(Admin, [SiteCard]).Ok);
        _opts.Buy = false;
        Assert.False(Buy(Olia).GetProperty("open").GetBoolean());
        Assert.Equal(ShardShop.Closed, _shop.Check(Olia, 50, null).Message);
    }

    [Fact]
    public void Site_cards_are_set_by_the_admin_only_and_checked_like_in_padel()
    {
        Assert.Equal(403, _shop.SetBanks(Olia, [SiteCard]).Status);
        Assert.Equal("Номер картки не той — перевір 16 цифр", _shop.SetBanks(Admin, [SiteCard with { Card = "1234 5678 9012 3456" }]).Message);
        Assert.Equal("Невідомий банк", _shop.SetBanks(Admin, [SiteCard with { Bank = "swiss" }]).Message);
        Assert.Contains("до 6", _shop.SetBanks(Admin, [.. Enumerable.Repeat(SiteCard, 7)]).Message);
        // Помилка нічого не зіпсувала
        Assert.Single(Buy(Olia).GetProperty("banks").EnumerateArray());

        Assert.True(_shop.SetBanks(Admin, [SiteCard, new(null, "privat", "", null, "https://privatbank.ua/sendmoney?payment=x")]).Ok);
        var banks = Buy(Olia).GetProperty("banks");
        Assert.Equal(2, banks.GetArrayLength());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, banks[1].GetProperty("card").ValueKind);
    }

    [Fact]
    public void Packs_from_config_replace_the_defaults_instead_of_adding_to_them()
    {
        Assert.Equal([50, 100, 250, 500], new ShardShopOptions().PackList);
        Assert.Equal([20, 50], new ShardShopOptions { Packs = [50, 20, 50, 0] }.PackList);
    }

    // =============================================================================================
    // Купити: «✓ Скинув» → в обробці → адмін «✓ Підтвердити»
    // =============================================================================================

    [Fact]
    public void Paid_is_in_processing_and_tells_the_admins()
    {
        var id = Order(_shop.Paid(Olia, 100, null));
        Assert.Equal(0, _eco.Economy.Balance("Оля"));   // гроші ще не підтверджено
        var t = Assert.Single(_wire.AdminToasts);
        Assert.Contains("Від Олі", t);
        Assert.Contains("100 грн", t);
        Assert.Equal((1, 0), _wire.Counts[^1]);

        var mine = Buy(Olia).GetProperty("mine");
        Assert.Equal(id, mine[0].GetProperty("id").GetInt64());
        Assert.Equal("wait", mine[0].GetProperty("status").GetString());
        Assert.Equal(10_000, mine[0].GetProperty("shards").GetInt32());
        Assert.Equal(id, Desk().GetProperty("orders")[0].GetProperty("id").GetInt64());
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
        Assert.Equal("Сума — від 10 до 5 000 грн, цілими гривнями", r.Message);
    }

    [Fact]
    public void Own_amount_buys_at_the_same_rate()
    {
        var c = Buy(Olia).GetProperty("custom");
        Assert.Equal((10, 5000), (c.GetProperty("min").GetInt32(), c.GetProperty("max").GetInt32()));
        var id = Order(_shop.Paid(Olia, 75, null));
        Assert.True(_shop.Confirm(Admin, id).Ok);
        Assert.Equal(7_500, _eco.Economy.Balance("Оля"));
        Assert.True(_shop.Paid(Olia, 10, null).Ok);
        Assert.True(_shop.Paid(Olia, 5000, null).Ok);
    }

    [Fact]
    public void Without_own_amount_only_packs()
    {
        _opts.CustomMax = 0;
        Assert.Equal(System.Text.Json.JsonValueKind.Null, Buy(Olia).GetProperty("custom").ValueKind);
        Assert.Contains("пакета", _shop.Paid(Olia, 75, null).Message);
        Assert.True(_shop.Paid(Olia, 250, null).Ok);
    }

    [Fact]
    public void Guests_do_not_buy()
    {
        var guest = _shop.Paid(new ShardActor("гість Вася", false, false), 50, null);
        Assert.Equal((false, 403), (guest.Ok, guest.Status));
        Assert.Equal(403, _shop.Paid(Admin, 50, null).Status);   // адмінська кука без акаунта — не покупець
    }

    [Fact]
    public void Orders_in_processing_of_one_buyer_are_capped()
    {
        for (var i = 0; i < 3; i++) Order(_shop.Paid(Olia, 50, null));
        var r = _shop.Paid(Olia, 50, null);
        Assert.False(r.Ok);
        Assert.Equal("Уже 3 купівлі в обробці — дочекайся адміна", r.Message);
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
        Assert.Empty(Buy(Olia).GetProperty("mine").EnumerateArray());
        Assert.Empty(_wire.AdminToasts);

        for (var i = 0; i < 3; i++) Order(_shop.Paid(Olia, 50, null));
        Assert.Contains("Уже 3 купівлі в обробці", _shop.Check(Olia, 50, null).Message);
    }

    [Fact]
    public void Buyer_cancels_only_own_order_in_processing()
    {
        var id = Order(_shop.Paid(Olia, 50, null));
        Assert.Equal(403, _shop.Cancel(Petro, id).Status);
        Assert.True(_shop.Cancel(Olia, id).Ok);
        Assert.Equal("Уже скасовано", _shop.Cancel(Olia, id).Message);
        Assert.False(_shop.Confirm(Admin, id).Ok);
        Assert.Equal(0, _eco.Economy.Balance("Оля"));
        Assert.Equal((0, 0), _wire.Counts[^1]);
        Assert.Equal("off", Buy(Olia).GetProperty("mine")[0].GetProperty("status").GetString());
    }

    [Fact]
    public void Admin_confirms_shards_land_once_and_cancel_is_gone()
    {
        var id = Order(_shop.Paid(Olia, 250, null));
        Assert.Equal(403, _shop.Confirm(Petro, id).Status);
        Assert.Equal(403, _shop.Confirm(Olia, id).Status);
        Assert.Equal(403, _shop.Confirm(Vlad, id).Status);   // владік — такий самий гравець

        var r = _shop.Confirm(Admin, id);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(25_000, _eco.Economy.Balance("Оля"));
        var w = Assert.Single(_eco.Outbox.Of<WalletChanged>(), x => x.Nick == "Оля");
        Assert.Equal($"buy:{id}", w.Reason);
        Assert.Equal("Лови +25 000 черепків: куплено за 250 грн", w.Text);
        Assert.Contains("Оля", _wire.Refreshed);

        // Друга вкладка, подвійний тиць — не вдруге; скасувати після підтвердження — ні
        Assert.Equal("Уже підтверджено", _shop.Confirm(Admin, id).Message);
        Assert.Equal("Уже підтверджено", _shop.Cancel(Olia, id).Message);
        Assert.Equal(25_000, _eco.Economy.Balance("Оля"));
        Assert.Equal("done", Buy(Olia).GetProperty("mine")[0].GetProperty("status").GetString());
        Assert.Equal(250, Desk().GetProperty("monthIn").GetInt32());
        Assert.Empty(Desk().GetProperty("orders").EnumerateArray());
    }

    [Fact]
    public void Confirm_after_a_crash_between_grant_and_status_does_not_pay_twice()
    {
        var id = Order(_shop.Paid(Olia, 50, null));
        // Гроші вже лягли (той самий ref), а стан лишився «в обробці» — як після падіння процесу посередині
        _eco.Economy.Grant("Оля", 5_000, $"buy:{id}", $"buy:{id}");
        Assert.True(Fresh().Confirm(Admin, id).Ok);
        Assert.Equal(5_000, _eco.Economy.Balance("Оля"));
    }

    [Fact]
    public void Gift_goes_to_the_friend_and_the_buyer_hears_it_landed()
    {
        var id = Order(_shop.Paid(Olia, 50, "петро"));
        Assert.Contains("для Петра", _wire.AdminToasts[^1]);
        var mine = Buy(Petro).GetProperty("mine")[0];   // Петро бачить, що йому купують
        Assert.Equal("Петро", mine.GetProperty("for").GetString());
        Assert.True(mine.GetProperty("gift").GetBoolean());

        Assert.True(_shop.Confirm(Admin, id).Ok);
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
        Assert.False(Buy(Olia).GetProperty("mine")[0].GetProperty("gift").GetBoolean());
        Assert.True(_shop.Confirm(Admin, id).Ok);
        Assert.Equal($"buy:{id}", _eco.Outbox.Of<WalletChanged>().Single().Reason);
    }

    [Fact]
    public void Bought_shards_are_in_balance_but_not_in_earned_tables_or_the_hundred()
    {
        var id = Order(_shop.Paid(Olia, 50, null));
        Assert.True(_shop.Confirm(Admin, id).Ok);
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
        Assert.Equal("продано за гривні", _eco.Economy.Reason("sell:3"));
        Assert.Equal("повернуто — продаж не відбувся", _eco.Economy.Reason("sell-back:3"));
        Assert.Equal("повернуто з продажу", PeopleEndpoints.CatTitle(PeopleEndpoints.Cat("sell-back:3")));
        Assert.True(EconomyStore.Exchange("buy-gift:3"));
        Assert.True(EconomyStore.Exchange("sell:3"));
        Assert.True(EconomyStore.Exchange("sell-back:3"));
        Assert.False(EconomyStore.Exchange("table-buyin:poker"));
        Assert.False(EconomyStore.Exchange("seller-bonus:3"));
    }

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

        (status, _) = Radio.Reply(ShardShopSetup.Confirm(id, As("Влад"), _shop));
        Assert.Equal(403, status);
        (status, _) = Radio.Reply(ShardShopSetup.Confirm(id, As("гість Адмін", account: false, admin: true), _shop));
        Assert.Equal(200, status);
        (status, body) = Radio.Reply(ShardShopSetup.Cancel(id, As("Оля"), _shop));
        Assert.Equal(400, status);
        Assert.Equal("Уже підтверджено", body.GetProperty("message").GetString());
        (status, _) = Radio.Reply(ShardShopSetup.Confirm(999, As("гість Адмін", account: false, admin: true), _shop));
        Assert.Equal(404, status);
        (status, _) = Radio.Reply(ShardShopSetup.SetBanks(As("Оля"), new([SiteCard]), _shop));
        Assert.Equal(403, status);
    }

    // =============================================================================================
    // Продати: «Продати» → в обробці (черепки відкладено) → адмін «✓ Підтвердити» (продано)
    // =============================================================================================

    int _b0;

    /// <summary>Оля з чесно заробленими 20 000 🏺 і своєю карткою в Падельні.</summary>
    void OliaCanSell()
    {
        _eco.Economy.Grant("Оля", 20_000, "listen", "test:seed");
        _b0 = _eco.Economy.Balance("Оля");   // 20 000 і ще нагорода за «Сотню»
        _banks.Map["оля"] = [new PadelBank("b2", "privat", "", "5168111111111111", null)];
    }

    string MySale(ShardActor me, long id) =>
        SellView(me).GetProperty("mine").EnumerateArray().Single(x => x.GetProperty("id").GetInt64() == id).GetProperty("status").GetString()!;

    [Fact]
    public void Sell_holds_the_shards_at_once_and_tells_the_admins()
    {
        OliaCanSell();
        var id = Order(_shop.Sell(Olia, 100));
        Assert.Equal(_b0 - 10_000, _eco.Economy.Balance("Оля"));   // відкладено: витратити вже не вийде
        var w = _eco.Economy.Wallet("Оля");
        Assert.Equal((_b0, 0), (w.Earned, w.Spent));                // продаж — не гра: ні «зароблено», ні «витрачено»
        Assert.Contains("Оля продає 10", Assert.Single(_wire.AdminToasts));
        Assert.Equal((0, 1), _wire.Counts[^1]);
        Assert.Equal("wait", MySale(Olia, id));

        // Адмін бачить заявку разом із карткою Олі
        var row = Assert.Single(Desk().GetProperty("sales").EnumerateArray());
        Assert.Equal(("Оля", 100, 10_000), (row.GetProperty("seller").GetString(), row.GetProperty("uah").GetInt32(), row.GetProperty("shards").GetInt32()));
        Assert.Equal("5168111111111111", row.GetProperty("banks")[0].GetProperty("card").GetString());

        var sell = SellView(Olia);
        Assert.True(sell.GetProperty("open").GetBoolean());
        Assert.Equal((100, 10, _b0 - 10_000), (sell.GetProperty("rate").GetInt32(), sell.GetProperty("min").GetInt32(), sell.GetProperty("balance").GetInt32()));
    }

    [Fact]
    public void Sell_is_for_accounts_with_a_card_and_enough_shards()
    {
        OliaCanSell();
        Assert.Equal(ShardShop.SellAccountsOnly, _shop.Sell(new ShardActor("гість Вася", false, false), 50).Message);
        _eco.Economy.Grant("Петро", 20_000, "listen", "test:p");
        Assert.Equal(ShardShop.NoBanks, _shop.Sell(Petro, 50).Message);   // без картки адмін не знає, куди переказати
        Assert.Contains("від 10 грн", _shop.Sell(Olia, 5).Message);
        Assert.Contains("від 10 грн", _shop.Sell(Olia, null).Message);
        Assert.Contains("вистачить на 200 грн", _shop.Sell(Olia, 300).Message);
        Assert.Equal(_b0, _eco.Economy.Balance("Оля"));

        _opts.Sell = false;
        Assert.Equal(ShardShop.SellClosed, _shop.Sell(Olia, 50).Message);
        _opts.Sell = true;

        // До трьох продажів в обробці водночас; четвертий — коли адмін підтвердить якийсь
        for (var i = 0; i < 3; i++) Order(_shop.Sell(Olia, 10));
        Assert.Equal("Уже 3 продажі в обробці — дочекайся адміна", _shop.Sell(Olia, 10).Message);
        Assert.Equal(_b0 - 3_000, _eco.Economy.Balance("Оля"));
    }

    [Fact]
    public void Admin_confirms_the_sale_and_cancel_is_gone()
    {
        OliaCanSell();
        var id = Order(_shop.Sell(Olia, 50));
        Assert.Equal(ShardShop.AdminOnly, _shop.ConfirmSale(Vlad, id).Message);
        Assert.Equal(ShardShop.AdminOnly, _shop.ConfirmSale(Olia, id).Message);

        Assert.True(_shop.ConfirmSale(Admin, id).Ok);
        Assert.Equal("done", MySale(Olia, id));
        Assert.Contains("Продано: адмін переказав 50 грн", _wire.Toasts[^1].Text);
        Assert.Equal("Оля", _wire.Toasts[^1].Nick);
        Assert.Contains("Оля", _wire.Refreshed);
        Assert.Equal((0, 0), _wire.Counts[^1]);
        Assert.Equal("Уже підтверджено", _shop.CancelSale(Olia, id).Message);   // гроші пішли — скасувати пізно
        Assert.False(_shop.ConfirmSale(Admin, id).Ok);
        Assert.Equal(_b0 - 5_000, _eco.Economy.Balance("Оля"));

        var desk = Desk();
        Assert.Equal(50, desk.GetProperty("monthOut").GetInt32());
        Assert.Empty(desk.GetProperty("sales").EnumerateArray());
        var done = desk.GetProperty("recentSales")[0];
        Assert.Equal(("done", "гість Адмін"), (done.GetProperty("status").GetString(), done.GetProperty("doneBy").GetString()));

        // Перерахунок із леджера: баланс той самий, «зароблено» й «витрачено» продаж не чіпає
        _eco.Economy.Rebuild();
        var w = _eco.Economy.Wallet("Оля");
        Assert.Equal((_b0 - 5_000, _b0, 0), (w.Balance, w.Earned, w.Spent));
    }

    [Fact]
    public void Cancel_returns_the_shards_exactly_once()
    {
        OliaCanSell();
        var id = Order(_shop.Sell(Olia, 100));
        Assert.Equal("Скасувати може лише той, хто продає", _shop.CancelSale(Petro, id).Message);
        Assert.True(_shop.CancelSale(Olia, id).Ok);
        Assert.Equal(_b0, _eco.Economy.Balance("Оля"));
        Assert.Equal("Уже скасовано", _shop.CancelSale(Olia, id).Message);
        Assert.False(_shop.ConfirmSale(Admin, id).Ok);
        Assert.Equal(_b0, _eco.Economy.Balance("Оля"));
        Assert.Equal("off", MySale(Olia, id));

        _eco.Economy.Rebuild();
        var w = _eco.Economy.Wallet("Оля");
        Assert.Equal((_b0, _b0, 0), (w.Balance, w.Earned, w.Spent));   // повернене — не «зароблено» вдруге
    }

    [Fact]
    public void A_sale_that_lost_its_debit_is_debited_on_confirm_and_never_refunded_from_nothing()
    {
        OliaCanSell();
        var store = new ShardShopStore(_eco.Db);
        // Як після падіння між записом заявки і списанням: заявка є, черепки на місці
        var lost = store.AddSale(new ShardSale(0, "Оля", 10, 1_000, "wait", _eco.Clock.UtcNow, null, null, null, null, ""));
        Assert.True(_shop.CancelSale(Olia, lost).Ok);
        Assert.Equal(_b0, _eco.Economy.Balance("Оля"));   // повертати нічого — нічого й не впало

        var late = store.AddSale(new ShardSale(0, "Оля", 10, 1_000, "wait", _eco.Clock.UtcNow, null, null, null, null, ""));
        Assert.True(_shop.ConfirmSale(Admin, late).Ok);
        Assert.Equal(_b0 - 1_000, _eco.Economy.Balance("Оля"));   // списано зараз, один раз

        var tooMuch = store.AddSale(new ShardSale(0, "Оля", 1_000, 100_000, "wait", _eco.Clock.UtcNow, null, null, null, null, ""));
        Assert.Contains("уже нема цих черепків", _shop.ConfirmSale(Admin, tooMuch).Message);
        Assert.Equal("off", MySale(Olia, tooMuch));
        Assert.Equal(_b0 - 1_000, _eco.Economy.Balance("Оля"));
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

        (status, _) = Radio.Reply(ShardShopSetup.ConfirmSale(id, As("Петро"), _shop));
        Assert.Equal(403, status);
        (status, _) = Radio.Reply(ShardShopSetup.ConfirmSale(id, As("гість Адмін", account: false, admin: true), _shop));
        Assert.Equal(200, status);
        (status, body) = Radio.Reply(ShardShopSetup.CancelSale(id, As("Оля"), _shop));
        Assert.Equal(400, status);
        Assert.Equal("Уже підтверджено", body.GetProperty("message").GetString());
        (status, _) = Radio.Reply(ShardShopSetup.ConfirmSale(999, As("гість Адмін", account: false, admin: true), _shop));
        Assert.Equal(404, status);
    }
}
