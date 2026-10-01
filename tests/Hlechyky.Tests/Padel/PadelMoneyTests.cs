using System.Text.Json;
using Hlechyky.Padel;
using static Hlechyky.Tests.Padel.PadelMoneyRig;

namespace Hlechyky.Tests.Padel;

/// <summary>Гроші (контракт §3.2–3.3): частки цілими гривнями, витрати зі збору, загальний баланс, платежі, банки.</summary>
public sealed class PadelMoneyTests : IDisposable
{
    readonly PadelMoneyRig _r = new();
    public void Dispose() => _r.Dispose();

    static PadelExpenseData D(string[] people, int court, string[]? rackets = null, PadelOtherLine[]? other = null, int racket = 150) =>
        new(people, court, racket, rackets ?? [], other ?? []);

    static PadelMoney.ExpenseRequest E(string? gathering = null, string? payer = null, string[]? people = null, int? court = null,
        string[]? rackets = null, PadelMoney.OtherRequest[]? other = null) =>
        new(gathering, null, payer, people, court, null, rackets, other);

    // ---------------------------------------------------------------- частки

    [Fact]
    public void Split_evenly()
    {
        var (total, shares) = PadelMoney.Split(D(["u:а", "u:б", "u:в", "u:г"], 3000), "u:а");
        Assert.Equal(3000, total);
        Assert.All(shares.Values, v => Assert.Equal(750, v));
    }

    [Fact]
    public void Remainder_goes_to_the_first_in_the_list_but_never_to_the_payer()
    {
        var (t1, s1) = PadelMoney.Split(D(["u:а", "u:б", "u:в"], 1000), "u:а");
        Assert.Equal(1000, t1);
        Assert.Equal([333, 334, 333], new[] { s1["u:а"], s1["u:б"], s1["u:в"] });
        var (_, s2) = PadelMoney.Split(D(["u:а", "u:б", "u:в"], 1001), "u:а");
        Assert.Equal([333, 334, 334], new[] { s2["u:а"], s2["u:б"], s2["u:в"] });
        var (_, s3) = PadelMoney.Split(D(["u:а", "u:б", "u:в"], 1001), "u:в");
        Assert.Equal([334, 334, 333], new[] { s3["u:а"], s3["u:б"], s3["u:в"] });
        Assert.Equal(1001, s3.Values.Sum());
    }

    [Fact]
    public void Payer_may_be_outside_the_people()
    {
        var (total, shares) = PadelMoney.Split(D(["u:б", "u:в"], 1001), "u:а");
        Assert.Equal(1001, total);
        Assert.False(shares.ContainsKey("u:а"));
        Assert.Equal([501, 500], new[] { shares["u:б"], shares["u:в"] });
    }

    [Fact]
    public void Rackets_only_for_those_who_took_and_other_lines_on_their_own_people()
    {
        // Приклад контракту: корт 3000 на чотирьох, ракетки Олі й гостю, м'ячі 300 на всіх
        var (total, shares) = PadelMoney.Split(D(["u:влад", "u:оля", "g:3", "u:таня"], 3000, ["u:оля", "g:3"],
            [new PadelOtherLine("М'ячі", 300, null)]), "u:влад");
        Assert.Equal(3600, total);
        Assert.Equal([825, 975, 975, 825], new[] { shares["u:влад"], shares["u:оля"], shares["g:3"], shares["u:таня"] });

        var (t2, s2) = PadelMoney.Split(D(["u:а", "u:б", "u:в"], 0, null, [new PadelOtherLine("Вода", 101, ["u:б", "u:в"])]), "u:а");
        Assert.Equal(101, t2);
        Assert.Equal([0, 51, 50], new[] { s2["u:а"], s2["u:б"], s2["u:в"] });
    }

    // ---------------------------------------------------------------- витрати

    [Fact]
    public void Expense_from_a_gathering_takes_people_court_and_rackets_from_it()
    {
        var gid = _r.NewGathering(hours: 1.5, courts: 2);
        _r.Join(gid, "оля", racket: true);
        _r.Join(gid, "оля", pid: "g:3", racket: true);
        _r.Join(gid, "таня");
        var (ps, pb) = R(_r.Money.Preview(U("влад"), E(gid, other: [new("М'ячі", 300, null)])));
        Assert.Equal(200, ps);
        Assert.Equal(3600, pb.GetProperty("total").GetInt32());      // 1000·1,5·2 + 2·150 + 300
        Assert.Equal(0, _r.Out.Money);                                 // перегляд нічого не пише

        var (s, b) = R(_r.Money.Create(U("влад"), E(gid, other: [new("М'ячі", 300, null)])));
        Assert.Equal(200, s);
        var e = b.GetProperty("expense");
        Assert.Equal("e1", e.GetProperty("id").GetString());
        Assert.Equal(gid, e.GetProperty("gathering").GetString());
        Assert.Equal("u:влад", e.GetProperty("payer").GetString());
        Assert.Equal(3000, e.GetProperty("court").GetInt32());
        Assert.Equal(["u:оля", "g:3"], e.GetProperty("rackets").EnumerateArray().Select(x => x.GetString()));
        var shares = e.GetProperty("shares");
        Assert.Equal(825, shares.GetProperty("u:влад").GetInt32());
        Assert.Equal(975, shares.GetProperty("u:оля").GetInt32());
        Assert.Equal(3600, shares.EnumerateObject().Sum(p => p.Value.GetInt32()));
        Assert.Equal(1, _r.Out.Money);
        Assert.Equal("e1", _r.Out.Gatherings[^1].GetProperty("expense").GetString());

        var (s2, b2) = R(_r.Money.Create(U("оля"), E(gid)));
        Assert.Equal(400, s2);
        Assert.Equal("Для цього збору вже є розрахунок — відредагуй його", b2.GetProperty("message").GetString());
    }

    [Fact]
    public void Expense_without_a_gathering_defaults_to_one_court_hour()
    {
        var (s, b) = R(_r.Money.Create(U("влад"), E(people: ["u:влад", "u:оля"])));
        Assert.Equal(200, s);
        Assert.Equal(1000, b.GetProperty("expense").GetProperty("total").GetInt32());
        Assert.Equal(400, R(_r.Money.Create(U("влад"), E())).Status);                           // нема людей
        Assert.Equal(400, R(_r.Money.Create(U("влад"), E(people: ["u:влад"], court: 0))).Status); // нема що ділити
        Assert.Equal(400, R(_r.Money.Create(U("влад"), E(people: ["u:влад", "g:99"]))).Status);  // невідомий
        Assert.Equal(403, R(_r.Money.Create(SiteGuest, E(people: ["u:влад"]))).Status);
    }

    [Fact]
    public void Expense_is_edited_by_its_author_the_payer_or_admin()
    {
        var (_, b) = R(_r.Money.Create(U("влад"), E(payer: "u:оля", people: ["u:влад", "u:оля", "u:таня"], court: 900)));
        var id = b.GetProperty("expense").GetProperty("id").GetString()!;
        Assert.Equal(403, R(_r.Money.Update(U("таня"), id, E(people: ["u:таня"], court: 1))).Status);
        Assert.Equal(403, R(_r.Money.Delete(U("таня"), id)).Status);
        var (s, u) = R(_r.Money.Update(U("оля"), id, E(people: ["u:влад", "u:оля", "u:таня"], court: 1200)));
        Assert.Equal(200, s);
        Assert.Equal("u:оля", u.GetProperty("expense").GetProperty("payer").GetString());   // платник лишився
        Assert.Equal("влад", u.GetProperty("expense").GetProperty("by").GetString());       // і автор теж
        Assert.Equal(200, R(_r.Money.Delete(U("адмін", admin: true), id)).Status);
        Assert.Equal(404, R(_r.Money.Delete(U("влад"), id)).Status);
    }

    // ---------------------------------------------------------------- баланс і платежі

    [Fact]
    public void Balance_folds_every_pair_into_one_number_and_payments_reduce_it()
    {
        R(_r.Money.Create(U("а"), E(payer: "u:а", people: ["u:а", "u:б", "u:в"], court: 900)));   // б і в винні а по 300
        R(_r.Money.Create(U("б"), E(payer: "u:б", people: ["u:а", "u:б"], court: 400)));         // а винен б 200
        Assert.Equal([("u:в", "u:а", 300), ("u:б", "u:а", 100)], _r.Money.Ledger());

        Assert.Equal(200, R(_r.Money.Pay(U("в"), new("u:в", "u:а", 100, "готівкою"))).Status);
        Assert.Equal(200, R(_r.Money.Pay(U("а"), new("u:б", "u:а", 100, null))).Status);       // а отримав — і записав сам
        Assert.Equal([("u:в", "u:а", 200)], _r.Money.Ledger());

        Assert.Equal(200, R(_r.Money.Pay(U("в"), new("u:в", "u:а", 250, null))).Status);       // переплатив — тепер а винен
        Assert.Equal([("u:а", "u:в", 50)], _r.Money.Ledger());
    }

    [Fact]
    public void A_linked_guest_owes_as_its_account()
    {
        R(_r.Money.Create(U("а"), E(payer: "u:а", people: ["u:а", "g:1"], court: 1000)));
        Assert.Equal([("g:1", "u:а", 500)], _r.Money.Ledger());
        _r.Who.Links["g:1"] = "u:петро";
        Assert.Equal([("u:петро", "u:а", 500)], _r.Money.Ledger());
    }

    [Fact]
    public void Payments_rights_limits_and_toasts()
    {
        Assert.Equal(403, R(_r.Money.Pay(U("в"), new("u:а", "u:б", 100, null))).Status);       // чужий платіж
        Assert.Equal(400, R(_r.Money.Pay(U("а"), new("u:а", "u:б", 0, null))).Status);
        Assert.Equal(400, R(_r.Money.Pay(U("а"), new("u:а", "u:б", 100_001, null))).Status);
        Assert.Equal(400, R(_r.Money.Pay(U("а"), new("u:а", "u:а", 100, null))).Status);
        Assert.Equal(403, R(_r.Money.Pay(SiteGuest, new("u:а", "u:б", 100, null))).Status);

        Assert.Equal(200, R(_r.Money.Pay(U("оля"), new("u:оля", "u:влад", 300, null))).Status);
        Assert.Equal(("влад", "💸 оля: «скинуто тобі 300 грн»"), _r.Out.Toasts[^1]);
        Assert.Equal(200, R(_r.Money.Pay(U("влад"), new("g:1", "u:влад", 200, null))).Status);  // за гостя — контрагент
        Assert.Single(_r.Out.Toasts);                                                            // гостю тост не летить
        var (_, b) = R(_r.Money.Pay(U("влад"), new("u:оля", "u:влад", 50, null)));
        Assert.Equal(("оля", "💸 влад: «отримано від тебе 50 грн»"), _r.Out.Toasts[^1]);
        var pid = b.GetProperty("id").GetString()!;
        Assert.Equal(403, R(_r.Money.Unpay(U("оля"), pid)).Status);
        Assert.Equal(200, R(_r.Money.Unpay(U("влад"), pid)).Status);
        Assert.Equal(4, _r.Out.Money);
    }

    [Fact]
    public void Money_view_is_for_accounts_only_and_shows_creditor_banks_to_the_debtor_only()
    {
        Assert.Equal(200, R(_r.Money.SetBanks(U("влад"), [new(null, "mono", "Чорна моно", "4111 1111 1111 1111", null)])).Status);
        Assert.Equal(200, R(_r.Money.SetBanks(U("оля"), [new(null, "privat", "", null, "https://www.privat24.ua/send/abc")])).Status);
        R(_r.Money.Create(U("влад"), E(people: ["u:влад", "u:оля", "g:1"], court: 900)));

        var (gs, gb) = R(_r.Money.View(SiteGuest));
        Assert.Equal(403, gs);
        Assert.Equal("Гроші бачать лише акаунти", gb.GetProperty("message").GetString());

        var (_, olya) = R(_r.Money.View(U("оля")));
        Assert.Equal("u:оля", olya.GetProperty("me").GetString());
        var owe = Assert.Single(olya.GetProperty("owe").EnumerateArray());
        Assert.Equal("u:влад", owe.GetProperty("pid").GetString());
        Assert.Equal(300, owe.GetProperty("amount").GetInt32());
        var bank = Assert.Single(owe.GetProperty("banks").EnumerateArray());
        Assert.Equal("4111111111111111", bank.GetProperty("card").GetString());
        Assert.Empty(olya.GetProperty("owed").EnumerateArray());
        Assert.Equal(2, olya.GetProperty("all").GetArrayLength());
        Assert.Equal(1000, olya.GetProperty("defaults").GetProperty("courtPerHour").GetInt32());
        Assert.Equal(150, olya.GetProperty("defaults").GetProperty("racketPrice").GetInt32());
        Assert.Single(olya.GetProperty("expenses").EnumerateArray());

        var (_, vlad) = R(_r.Money.View(U("влад")));
        Assert.Empty(vlad.GetProperty("owe").EnumerateArray());
        var owed = vlad.GetProperty("owed").EnumerateArray().ToList();
        Assert.Equal(2, owed.Count);
        Assert.All(owed, o => Assert.False(o.TryGetProperty("banks", out _)));                  // боржників банки не видно
        Assert.True(owed.Single(o => o.GetProperty("pid").GetString() == "g:1").GetProperty("guest").GetBoolean());
        Assert.DoesNotContain("privat24", vlad.ToString());

        Assert.Equal(403, R(_r.Money.Banks(U("таня"), "u:влад")).Status);
        Assert.Equal(200, R(_r.Money.Banks(U("адмін", admin: true), "u:влад")).Status);
        Assert.Equal(403, R(_r.Money.Banks(SiteGuest, null)).Status);
        var (_, mine) = R(_r.Money.Banks(U("оля"), null));
        Assert.Equal("privat", mine.GetProperty("banks")[0].GetProperty("bank").GetString());
    }

    // ---------------------------------------------------------------- банки

    [Theory]
    [InlineData("mono", "4111 1111 1111 1111", null, "")]
    [InlineData("mono", "4111-1111-1111-1111", null, "")]
    [InlineData("mono", "5375 4141 0000 0002", null, "Номер картки не той — перевір 16 цифр")]   // Луна не сходиться
    [InlineData("mono", "4111 1111 1111 111", null, "Номер картки не той — перевір 16 цифр")]
    [InlineData("mono", "4111 1111 1111 111x", null, "Номер картки не той — перевір 16 цифр")]
    [InlineData("mono", null, "https://send.monobank.ua/jar/abc", "")]
    [InlineData("mono", null, "http://send.monobank.ua/jar/abc", "Посилання — https-адреса до 300 символів")]
    [InlineData("mono", null, "javascript:alert(1)", "Посилання — https-адреса до 300 символів")]
    [InlineData("mono", null, null, "Додай картку або посилання")]
    [InlineData("swiss", "4111 1111 1111 1111", null, "Невідомий банк")]
    public void Bank_validation(string bank, string? card, string? link, string error) =>
        Assert.Equal(error, PadelMoney.BankError(new PadelMoney.BankRequest(null, bank, "", card, link)));

    [Fact]
    public void Banks_are_capped_titled_and_keep_their_ids()
    {
        var seven = Enumerable.Range(0, 7).Select(_ => new PadelMoney.BankRequest(null, "mono", "", "4111111111111111", null)).ToArray();
        Assert.Equal(400, R(_r.Money.SetBanks(U("влад"), seven)).Status);
        Assert.Equal(400, R(_r.Money.SetBanks(U("влад"), [new(null, "mono", new string('я', 41), "4111111111111111", null)])).Status);
        var (_, b) = R(_r.Money.SetBanks(U("влад"), [new(null, "mono", "Чорна", "4111111111111111", null), new("bmy1", "izi", "", null, "https://izibank.com.ua/x")]));
        var ids = b.GetProperty("banks").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToList();
        Assert.Equal("bmy1", ids[1]);
        Assert.StartsWith("b", ids[0]);
        Assert.Equal(403, R(_r.Money.SetBanks(SiteGuest, [])).Status);
        Assert.Equal(200, R(_r.Money.SetBanks(U("влад"), [])).Status);
        Assert.Empty(_r.MoneyStore.Banks("u:влад"));
    }

    [Fact]
    public void Luhn_check() => Assert.Equal([true, false, true], new[] { "4111111111111111", "4111111111111112", "5375414100000008" }.Select(PadelMoney.Luhn));
}
