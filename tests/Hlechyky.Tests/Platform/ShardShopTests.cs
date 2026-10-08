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
        public List<(string Seller, int Count)> Counts { get; } = [];
        public void Toast(string nick, string text) => Toasts.Add((nick, text));
        public void Waiting(string seller, int count) => Counts.Add((seller, count));
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
        Assert.Equal(("Влад", 1), _wire.Counts[^1]);

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
        Assert.Equal(("Влад", 0), _wire.Counts[^1]);
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
        Assert.True(EconomyStore.Bought("buy-gift:3"));
        Assert.False(EconomyStore.Bought("table-buyin:poker"));
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
}
