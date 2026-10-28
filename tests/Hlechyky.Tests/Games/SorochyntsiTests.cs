using System.Text.Json;
using System.Text.RegularExpressions;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using static Hlechyky.Games.Impl.SorochyntsiCards;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Сорочинський ярмарок» (контракт <c>docs/games/specs/sorochyntsi.md</c> §14): склад колоди й відбиток проти клієнта,
/// порядок Ctx.Rng, кожна дія з кожною помилкою своїм текстом, кінець ходу (повернення, гості), старшина, булава й
/// останнє коло, переможець, запобіжники; через кімнату — опції, старт, розбір payload, Finish і ачівки.
/// </summary>
public class SorochyntsiTests
{
    // ---------- склад (§1) ----------

    [Fact]
    public void Deck_has_ninety_shops_forty_thirty_twenty_with_ids_in_place()
    {
        Assert.Equal(90, Deck.Length);
        for (var i = 0; i < Deck.Length; i++) Assert.Equal(i, Deck[i].Id);
        Assert.Equal(40, Deck.Count(c => c.Tier == 1));
        Assert.Equal(30, Deck.Count(c => c.Tier == 2));
        Assert.Equal(20, Deck.Count(c => c.Tier == 3));
        Assert.All(Deck.Take(40), c => Assert.Equal(1, c.Tier));
        Assert.All(Deck.Skip(40).Take(30), c => Assert.Equal(2, c.Tier));
        Assert.All(Deck.Skip(70), c => Assert.Equal(3, c.Tier));
        Assert.Equal([5, 55, 80], [.. Enumerable.Range(1, 3).Select(t => Deck.Where(c => c.Tier == t).Sum(c => c.Pts))]);
        Assert.Equal([5, 10, 7], [.. Enumerable.Range(1, 3).Select(t => Deck.Where(c => c.Tier == t).Sum(c => c.K))]);
        Assert.Equal(22, Deck.Count(c => c.K == 1));
        // Цвіт — лише бонус 85–89; ціна завжди в п'яти буденних товарах.
        Assert.Equal([85, 86, 87, 88, 89], [.. Deck.Where(c => c.Bonus == Fern).Select(c => c.Id)]);
        Assert.All(Deck, c => Assert.Equal(5, c.Cost.Length));
        // Кожен товар у цінах рівня — однаково (33 / 41 / 40).
        foreach (var (t, each) in new[] { (1, 33), (2, 41), (3, 40) })
            for (var g = 0; g < 5; g++) Assert.Equal(each, Deck.Where(c => c.Tier == t).Sum(c => c.Cost[g]));
    }

    [Theory]
    [InlineData(0, 1, Linen, 0, 0, new[] { 0, 1, 1, 1, 1 }, "Прядка")]
    [InlineData(3, 1, Linen, 0, 1, new[] { 1, 0, 1, 3, 0 }, "Білильниця")]
    [InlineData(7, 1, Linen, 1, 0, new[] { 0, 0, 0, 4, 0 }, "Полотняний рядок")]
    [InlineData(44, 2, Linen, 2, 0, new[] { 0, 0, 0, 0, 5 }, "Полотняний віз")]
    [InlineData(58, 2, Clay, 1, 1, new[] { 2, 0, 3, 0, 2 }, "Гончарний круг")]
    [InlineData(71, 3, Linen, 4, 1, new[] { 3, 6, 0, 3, 0 }, "Вишиванки на весь ярмарок")]
    [InlineData(85, 3, Fern, 3, 1, new[] { 3, 3, 3, 2, 0 }, "Червона свитка")]
    [InlineData(89, 3, Fern, 3, 0, new[] { 3, 3, 2, 0, 3 }, "Купальський вогонь")]
    public void Sample_shops_match_the_table(int id, int tier, int bonus, int pts, int k, int[] cost, string name)
    {
        var c = Deck[id];
        Assert.Equal((tier, bonus, pts, k, name), (c.Tier, c.Bonus, c.Pts, c.K, c.Name));
        Assert.Equal(cost, c.Cost);
    }

    [Fact]
    public void Deck_signature_is_the_contract_one_and_equals_the_client_deck()
    {
        Assert.Equal("f97ba6b0", DeckSig());
        var js = File.ReadAllText(Path.Combine(FindRoot(), "web", "games", "sorochyntsi", "consts.js"));
        Assert.Equal(DeckSig(), Regex.Match(js, @"DECK_SIG = '([0-9a-f]{8})'").Groups[1].Value);
        // Колоду клієнта перераховуємо тим самим FNV — збігається з сервером рядок у рядок.
        var rows = Regex.Matches(js, @"\[(\d+), (\d), '(\w+)', (\d), (\d), \[([\d, ]+)\]\]").Select(m => new SoCard(
            int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), Array.IndexOf(Bonuses, m.Groups[3].Value),
            int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value),
            [.. m.Groups[6].Value.Split(", ").Select(int.Parse)], "")).ToList();
        Assert.Equal(90, rows.Count);
        Assert.Equal(DeckSig(), Sig(rows));
        // І назви — ті самі.
        var names = Regex.Match(js, @"CARD_NAME = \[(.*?)\];", RegexOptions.Singleline).Groups[1].Value;
        Assert.Equal(Deck.Select(c => c.Name), Regex.Matches(names, "'([^']*)'").Select(m => m.Groups[1].Value));
    }

    internal static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "web", "games"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("не знайшов корінь репозиторію");
    }

    [Fact]
    public void Ten_guests_ask_for_bonuses_in_pairs_of_four_and_triples_of_three()
    {
        Assert.Equal(10, Guests.Length);
        Assert.Equal("Солопій Черевик", Guests[0].Name);
        Assert.Equal([4, 4, 0, 0, 0], Guests[0].Need);
        Assert.Equal([3, 0, 0, 3, 3], Guests[8].Need);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(8, Guests[i].Need.Sum());
            Assert.Equal(4, Guests[i].Need[i]);
            Assert.Equal(4, Guests[i].Need[(i + 1) % 5]);
            Assert.Equal(9, Guests[5 + i].Need.Sum());
            for (var d = 0; d < 3; d++) Assert.Equal(3, Guests[5 + i].Need[(i + d) % 5]);
        }
    }

    [Theory]
    [InlineData(2, 4)]
    [InlineData(3, 5)]
    [InlineData(4, 7)]
    public void Bank_and_guests_depend_on_the_number_of_players(int n, int pile)
    {
        var c = SoRig.Core(n);
        Assert.Equal([pile, pile, pile, pile, pile, 5], c.Bank);
        Assert.Equal(n + 1, c.Table.Count);
        Assert.All(c.Order, s => Assert.Equal(0, c.NTok(s) + c.Cards[s].Count + c.Reserved[s].Count + c.Points(s)));
        Assert.Equal([36, 26, 16], c.Decks.Select(d => d.Count));
        Assert.All(c.Rows, r => Assert.All(r, id => Assert.True(id >= 0)));
    }

    [Fact]
    public void Layout_follows_the_fixed_order_of_random_calls()
    {
        foreach (var seed in new[] { 1, 7, 42 })
        {
            var c = SoRig.Core(3, seed);
            var rng = new Random(seed);
            var lo = 0;
            for (var t = 0; t < 3; t++)
            {
                var ids = Enumerable.Range(lo, TierSize[t]).ToArray();
                for (var i = ids.Length - 1; i > 0; i--)
                {
                    var j = rng.Next(i + 1);
                    (ids[i], ids[j]) = (ids[j], ids[i]);
                }
                Assert.Equal(ids.Take(4), c.Rows[t]);
                Assert.Equal(ids.Skip(4), c.Decks[t]);
                lo += TierSize[t];
            }
            var g = Enumerable.Range(0, 10).ToArray();
            for (var i = g.Length - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (g[i], g[j]) = (g[j], g[i]);
            }
            Assert.Equal(g.Take(4), c.Table);
            Assert.Equal(c.Order[rng.Next(3)], c.First);
            Assert.Equal(c.First, c.Turn);
        }
        // Той самий сід — той самий стіл через кімнату.
        Assert.Equal(SoRig.Table(2, seed: 9).View(null).GetRawText(), SoRig.Table(2, seed: 9).View(null).GetRawText());
    }

    // ---------- взяти (§2.3) ----------

    [Theory]
    [InlineData(new[] { "fish", "gold" }, "Дукат — лише разом із бронею")]
    [InlineData(new[] { "fern" }, "Цвіт папороті жетоном не буває — лише знижкою з крамниці")]
    [InlineData(new[] { "fish", "salt", "clay" }, "Такого товару на ярмарку нема")]
    [InlineData(new[] { "linen", "fish", "herbs" }, "Купа «полотно» порожня")]
    [InlineData(new[] { "clay", "clay" }, "Два однакові — лише з купи, де щонайменше 4 (у купі «глина» — 3)")]
    [InlineData(new[] { "fish", "fish", "herbs" }, "Три — то три різні товари")]
    [InlineData(new[] { "fish", "herbs" }, "Бери три різні товари або два однакові")]
    [InlineData(new[] { "fish", "herbs", "clay", "wool" }, "Бери три різні товари або два однакові")]
    public void Every_take_mistake_has_its_own_words(string[] goods, string error)
    {
        var c = SoRig.Core(3);
        SoRig.Piles(c, 0, 5, 5, 3, 5);
        var before = SoRig.State(c);
        Assert.Equal(error, c.Take(c.Turn, goods));
        Assert.Equal(before, SoRig.State(c));
    }

    [Fact]
    public void Short_piles_take_one_of_each_and_empty_bank_says_so()
    {
        var c = SoRig.Core(3);
        SoRig.Piles(c, 0, 2, 0, 5, 0);
        var s = c.Turn;
        Assert.Equal("Бери по одному з кожної непорожньої купи (2) або два однакові", c.Take(s, ["fish"]));
        Assert.Null(c.Take(s, ["fish", "clay"]));
        Assert.Equal([0, 1, 0, 1, 0, 0], c.Tok[s]);
        s = c.Turn;
        SoRig.Piles(c, 0, 0, 0, 1, 0);
        Assert.Equal("Бери по одному з кожної непорожньої купи (1) або два однакові", c.Take(s, []));
        Assert.Null(c.Take(s, ["clay"]));
        s = c.Turn;
        SoRig.Piles(c, 0, 0, 0, 0, 0);
        Assert.Equal("Купи порожні", c.Take(s, ["clay"]));
        Assert.False(c.Legal(s).Take);
        Assert.Equal("Купи порожні", c.Legal(s).Why["take"]);
    }

    [Fact]
    public void Three_different_or_two_alike_from_a_pile_of_four()
    {
        var c = SoRig.Core(2);
        var s = c.Turn;
        Assert.Null(c.Take(s, ["linen", "herbs", "wool"]));
        Assert.Equal([1, 0, 1, 0, 1, 0], c.Tok[s]);
        Assert.Equal([3, 4, 3, 4, 3, 5], c.Bank);
        var e = c.Events.Last(x => x.K == "take");
        Assert.Equal("{\"seat\":" + s + ",\"got\":{\"linen\":1,\"herbs\":1,\"wool\":1}}", SoRig.Wire(e, s, "seat", "got"));
        Assert.Contains($"{c.NameOf(s)} бере полотно, зілля й вовну", c.Log);
        s = c.Turn;
        Assert.Equal("Два однакові — лише з купи, де щонайменше 4 (у купі «полотно» — 3)", c.Take(s, ["linen", "linen"]));
        Assert.Null(c.Take(s, ["fish", "fish"]));
        Assert.Equal(2, c.Tok[s][Fish]);
        Assert.Contains($"{c.NameOf(s)} бере 2 риби", c.Log);
    }

    [Fact]
    public void Taking_with_ten_in_hand_is_allowed_and_asks_to_give_back()
    {
        var c = SoRig.Core(3);
        var s = c.Turn;
        SoRig.Hand(c, s, 2, 2, 2, 2, 2);
        Assert.Null(c.Take(s, ["linen", "fish", "herbs"]));
        Assert.Equal(SoPhase.Discard, c.Phase);
        Assert.Equal(3, c.DiscardN);
        Assert.Equal(s, c.Turn);
        Assert.Equal("Спершу поверни зайве: у руці щонайбільше 10 жетонів", c.Take(s, ["clay", "wool", "fish"]));
        Assert.Equal("Спершу поверни зайве: у руці щонайбільше 10 жетонів", c.Buy(s, c.Rows[0][0]));
    }

    // ---------- бронь (§2.4) ----------

    [Fact]
    public void Reserving_from_a_row_refills_the_slot_and_gives_a_ducat()
    {
        var c = SoRig.Core(3);
        var s = c.Turn;
        var id = c.Rows[1][2];
        var top = c.Decks[1][0];
        Assert.Null(c.Reserve(s, id, null));
        Assert.Equal([new SoRes(id, false)], c.Reserved[s]);
        Assert.Equal(top, c.Rows[1][2]);
        Assert.Equal(1, c.Tok[s][Gold]);
        Assert.Equal(4, c.Bank[Gold]);
        var ev = c.Events.TakeLast(3).Select(e => e.K).ToArray();
        Assert.Equal(["reserve", "refill", "turn"], ev);
        Assert.Equal($"{{\"seat\":{s},\"tier\":2,\"slot\":2,\"card\":{id},\"gold\":1}}", SoRig.Wire(c.Events[^3], null, "seat", "tier", "slot", "card", "gold"));
        Assert.Equal($"{{\"tier\":2,\"slot\":2,\"card\":{top}}}", SoRig.Wire(c.Events[^2], null, "tier", "slot", "card"));
        Assert.Contains($"{c.NameOf(s)} бронює «{Deck[id].Name}» і бере дукат", c.Log);
    }

    [Fact]
    public void Empty_deck_leaves_the_slot_empty_and_blind_reserve_is_refused()
    {
        var c = SoRig.Core(3);
        var s = c.Turn;
        c.Decks[2].Clear();
        var id = c.Rows[2][0];
        Assert.Equal("Колода III рівня скінчилась", c.Reserve(s, null, 3));
        Assert.Null(c.Reserve(s, id, null));
        Assert.Equal(-1, c.Rows[2][0]);
        Assert.Equal("{\"tier\":3,\"slot\":0,\"card\":null}", SoRig.Wire(c.Events[^2], null, "tier", "slot", "card"));
        Assert.False(c.Legal(c.Turn).Deck[2]);
        Assert.Equal("Колода III рівня скінчилась", c.Legal(c.Turn).Why["deck3"]);
    }

    [Fact]
    public void Blind_reserve_takes_the_top_card_and_hides_it_from_the_others()
    {
        var c = SoRig.Core(3);
        var s = c.Turn;
        var top = c.Decks[0][0];
        var rows = c.Rows.Select(r => (int[])r.Clone()).ToArray();
        Assert.Null(c.Reserve(s, null, 1));
        Assert.Equal([new SoRes(top, true)], c.Reserved[s]);
        Assert.Equal(rows, c.Rows);
        Assert.Equal(35, c.Decks[0].Count);
        var e = c.Events.Last(x => x.K == "reserve");
        Assert.Equal($"{{\"slot\":null,\"card\":{top}}}", SoRig.Wire(e, s, "slot", "card"));
        foreach (var other in new int?[] { (s + 1) % 3, null })
            Assert.Equal("{\"slot\":null,\"card\":null}", SoRig.Wire(e, other, "slot", "card"));
        Assert.Contains($"{c.NameOf(s)} бронює наосліп з колоди I і бере дукат", c.Log);
    }

    [Fact]
    public void No_ducats_in_the_bank_means_a_reserve_without_one()
    {
        var c = SoRig.Core(3);
        var s = c.Turn;
        c.Bank[Gold] = 0;
        c.Bank0[Gold] = 0;
        var id = c.Rows[0][1];
        Assert.Null(c.Reserve(s, id, null));
        Assert.Equal(0, c.Tok[s][Gold]);
        Assert.Contains($"{c.NameOf(s)} бронює «{Deck[id].Name}» — дукатів нема", c.Log);
        Assert.Equal("{\"gold\":0}", SoRig.Wire(c.Events.Last(x => x.K == "reserve"), null, "gold"));
    }

    [Fact]
    public void Fourth_reserve_is_refused_and_a_reserve_at_ten_tokens_gives_one_back()
    {
        var c = SoRig.Core(3);
        var s = c.Turn;
        SoRig.Reserve(c, s, c.Decks[0][0], c.Decks[1][0], c.Decks[2][0]);
        var before = SoRig.State(c);
        Assert.Equal("Бронювати можна щонайбільше 3 крамниці", c.Reserve(s, c.Rows[0][0], null));
        Assert.Equal("Бронювати можна щонайбільше 3 крамниці", c.Reserve(s, null, 2));
        Assert.Equal(before, SoRig.State(c));
        var l = c.Legal(s);
        Assert.False(l.Reserve);
        Assert.Equal("Бронювати можна щонайбільше 3 крамниці", l.Why["reserve"]);
        Assert.Equal("Бронювати можна щонайбільше 3 крамниці", l.Why["deck2"]);

        var d = SoRig.Core(3);
        s = d.Turn;
        SoRig.Hand(d, s, 2, 2, 2, 2, 2);
        Assert.Null(d.Reserve(s, d.Rows[0][0], null));
        Assert.Equal(SoPhase.Discard, d.Phase);
        Assert.Equal(1, d.DiscardN);
        Assert.Null(d.Discard(s, [new("gold", 1)]));
        Assert.Equal(10, d.NTok(s));
        Assert.NotEqual(s, d.Turn);
    }

    [Fact]
    public void Reserve_mistakes_have_their_own_words()
    {
        var c = SoRig.Core(3);
        var s = c.Turn;
        Assert.Equal("Не зрозумів, що бронюєш", c.Reserve(s, null, null));
        Assert.Equal("Цієї крамниці в рядах нема", c.Reserve(s, c.Decks[0][0], null));
        Assert.Equal("Такої колоди нема", c.Reserve(s, null, 4));
        Assert.Equal("Такої колоди нема", c.Reserve(s, null, 0));
        var h = SoRig.Table(2);
        var t = SoRig.CoreOf(h).Turn;
        Assert.Equal("Не зрозумів, що бронюєш", h.Act(t, "reserve", new { card = "7" }).Message);
    }

    // ---------- купити (§2.5) ----------

    [Fact]
    public void Price_is_cost_minus_bonuses_paid_with_own_tokens_first()
    {
        var c = SoRig.Core(3);
        var s = c.Turn;
        SoRig.Own(c, s, 16);                      // знижка зілля 1
        SoRig.Row(c, 41, 1);                      // «Вибійка»: 2 полотна, 3 зілля, 3 глини
        SoRig.Hand(c, s, linen: 2, herbs: 2, clay: 3, fish: 1);
        var bank = (int[])c.Bank.Clone();
        var top = c.Decks[1][0];
        Assert.Null(c.Buy(s, 41));
        Assert.Equal([0, 1, 0, 0, 0, 0], c.Tok[s]);
        Assert.Equal(bank[Linen] + 2, c.Bank[Linen]);
        Assert.Equal(bank[Herbs] + 2, c.Bank[Herbs]);
        Assert.Equal(bank[Clay] + 3, c.Bank[Clay]);
        Assert.Equal([16, 41], c.Cards[s]);
        Assert.Equal(1, c.Bonus[s][Linen]);
        Assert.Equal(1, c.Points(s));
        Assert.Equal(top, c.Rows[1][1]);
        var buy = c.Events.Last(e => e.K == "buy");
        Assert.Equal($"{{\"seat\":{s},\"card\":41,\"from\":\"row\",\"slot\":1,\"pay\":{{\"linen\":2,\"herbs\":2,\"clay\":3}},\"bonus\":\"linen\",\"pts\":1}}",
            SoRig.Wire(buy, null, "seat", "card", "from", "slot", "pay", "bonus", "pts"));
        Assert.Contains($"{c.NameOf(s)} купує «Вибійка» (+1 ⭐)", c.Log);
    }

    [Fact]
    public void Ducats_cover_only_the_gap_exactly_at_the_limit()
    {
        var c = SoRig.Core(3);
        var s = c.Turn;
        SoRig.Row(c, 0, 0);                       // «Прядка»: по 1 риби, зілля, глини, вовни
        SoRig.Hand(c, s, fish: 1, herbs: 1, gold: 2);
        var q = c.Quote(s, 0);
        Assert.Equal([0, 1, 1, 0, 0, 2], q.Pay);
        Assert.Null(c.Buy(s, 0));
        Assert.Equal(0, c.NTok(s));
        Assert.Contains($"{c.NameOf(s)} купує «Прядка»", c.Log);

        s = c.Turn;
        SoRig.Row(c, 6, 1);                       // «Рушничок»: 3 риби
        SoRig.Hand(c, s, gold: 2);
        Assert.Equal("Не вистачає: 3 риби, дукатів у тебе: 2", c.Buy(s, 6));
        SoRig.Hand(c, s);
        Assert.Equal("Не вистачає: 3 риби", c.Buy(s, 6));
        SoRig.Row(c, 41, 0);
        SoRig.Hand(c, s, herbs: 1, gold: 1);
        Assert.Equal("Не вистачає: 2 полотна, 2 зілля й 3 глини, дукатів у тебе: 1", c.Buy(s, 41));
        Assert.Equal("бракує 2 полотна, 2 зілля й 3 глини, дукатів у тебе: 1", SorochyntsiCore.WhyShort(c.Quote(s, 41), 1));
        Assert.Equal(6, c.Quote(s, 41).Short);
    }

    [Fact]
    public void Own_reserve_can_be_bought_but_not_someone_elses()
    {
        var c = SoRig.Core(3);
        var s = c.Turn;
        var o = c.Order[(Array.IndexOf(c.Order, s) + 1) % 3];
        SoRig.Reserve(c, s, 44);                  // 5 вовни
        SoRig.Reserve(c, o, 6);                   // 3 риби
        SoRig.Hand(c, s, fish: 3, wool: 5);
        Assert.Equal("Цієї крамниці нема ні в рядах, ні в твоїй броні", c.Buy(s, 6));
        Assert.Equal("Не зрозумів, що купуєш", c.Buy(s, null));
        var rows = c.Rows.Select(r => (int[])r.Clone()).ToArray();
        Assert.Null(c.Buy(s, 44));
        Assert.Empty(c.Reserved[s]);
        Assert.Equal(rows, c.Rows);
        Assert.Equal("{\"from\":\"reserve\",\"slot\":0}", SoRig.Wire(c.Events.Last(e => e.K == "buy"), null, "from", "slot"));
        Assert.DoesNotContain(c.Events.TakeLast(3), e => e.K == "refill");
    }

    // ---------- кінець ходу (§2.6) ----------

    [Fact]
    public void Giving_back_must_be_exact_and_may_include_ducats()
    {
        var c = SoRig.Core(3);
        var s = c.Turn;
        SoRig.Hand(c, s, 2, 2, 2, 2, 1, gold: 1);
        Assert.Null(c.Take(s, ["linen", "fish", "herbs"]));
        Assert.Equal(3, c.DiscardN);
        var before = SoRig.State(c);
        Assert.Equal("Поверни рівно 3 жетони", c.Discard(s, [new("fish", 2)]));
        Assert.Equal("Стільки вовни у тебе нема", c.Discard(s, [new("wool", 2), new("fish", 1)]));
        Assert.Equal("Стільки дукатів у тебе нема", c.Discard(s, [new("gold", 2), new("fish", 1)]));
        Assert.Equal("Такого жетона нема", c.Discard(s, [new("fern", 3)]));
        Assert.Equal("Не зрозумів, що повертаєш", c.Discard(s, null));
        Assert.Equal("Гостей зараз не кличуть", c.Guest(s, 1));
        Assert.Equal(before, SoRig.State(c));
        Assert.Null(c.Discard(s, [new("gold", 1), new("fish", 2)]));
        Assert.Equal([3, 1, 3, 2, 1, 0], c.Tok[s]);
        Assert.Equal(SoPhase.Play, c.Phase);
        Assert.NotEqual(s, c.Turn);
        Assert.Equal("Зараз нічого повертати", c.Discard(c.Turn, [new("fish", 1)]));
        Assert.Contains($"{c.NameOf(s)} повертає 3 жетони", c.Log);
        Assert.Equal("{\"gave\":{\"fish\":2,\"gold\":1}}", SoRig.Wire(c.Events.Last(e => e.K == "discard"), null, "gave"));
        var ks = c.Events.TakeLast(3).Select(e => e.K).ToArray();
        Assert.Equal(["discard", "turn"], ks[1..]);
    }

    [Fact]
    public void One_guest_comes_by_himself_for_bonuses_not_tokens()
    {
        var c = SoRig.Core(3);
        c.Table = [0, 1, 2, 3];
        var s = c.Turn;
        SoRig.Hand(c, s, 4, 3);                   // жетонами гостя не покличеш
        Assert.Null(c.Take(s, ["fish", "clay", "wool"]));
        Assert.Empty(c.GuestsOf[s]);
        s = c.Turn;
        SoRig.Own(c, s, 0, 1, 2, 4, 8, 9, 10, 12); // 4 полотна + 4 риби
        Assert.Null(c.Take(s, ["herbs", "clay", "wool"]));
        Assert.Equal([0], c.GuestsOf[s]);
        Assert.Equal(s, c.GuestBy[0]);
        Assert.Equal(3, c.Points(s));
        Assert.Contains($"До {c.NameOf(s)} у гості — Солопій Черевик (+3 ⭐)", c.Log);
        Assert.Equal($"{{\"seat\":{s},\"guest\":0}}", SoRig.Wire(c.Events.Last(e => e.K == "guest"), null, "seat", "guest"));
    }

    [Fact]
    public void Two_guests_at_once_ask_to_choose_and_the_other_comes_next_turn()
    {
        var c = SoRig.Core(3);
        c.Table = [0, 5, 7, 9];
        var s = c.Turn;
        SoRig.Own(c, s, 0, 1, 2, 4, 8, 9, 10, 12, 16, 17, 18);   // 4 полотна, 4 риби, 3 зілля
        Assert.Null(c.Take(s, ["clay", "wool", "herbs"]));
        Assert.Equal(SoPhase.Guest, c.Phase);
        Assert.Equal([0, 5], c.GuestChoice);
        Assert.Equal("Спершу обери гостя", c.Take(s, ["clay", "wool", "herbs"]));
        Assert.Equal("Цей гість до тебе ще не піде", c.Guest(s, 7));
        Assert.Equal("Не зрозумів, кого кличеш", c.Guest(s, null));
        Assert.Equal("Зараз нічого повертати", c.Discard(s, [new("clay", 1)]));
        Assert.Null(c.Guest(s, 5));
        Assert.Equal([5], c.GuestsOf[s]);
        Assert.Equal(-1, c.GuestBy[0]);
        Assert.NotEqual(s, c.Turn);
        while (c.Turn != s) Assert.Null(c.Take(c.Turn, ["clay", "wool", "herbs"]) ?? c.Take(c.Turn, ["linen", "fish", "herbs"]));
        Assert.Null(c.Take(s, ["linen", "fish", "herbs"]));
        Assert.Equal([5, 0], c.GuestsOf[s]);
        Assert.Equal(6, c.Points(s));
    }

    // ---------- Січова старшина (§2.7) ----------

    [Fact]
    public void Starshyna_goes_to_the_first_with_three_and_moves_only_for_strictly_more()
    {
        var c = SoRig.Core(2);
        var a = c.Turn;
        var b = c.Order.First(o => o != a);
        SoRig.Own(c, a, 3, 11);                   // 2 ⚔
        SoRig.Own(c, b, 27, 35);                  // 2 ⚔
        SoRig.Row(c, 19, 0);                      // ⚔, «Ятка насіння»: 3 полотна, 1 зілля, 1 вовни
        SoRig.Hand(c, a, linen: 3, herbs: 1, wool: 1);
        Assert.Null(c.Buy(a, 19));
        Assert.Equal(a, c.Starshyna);
        Assert.Equal($"{{\"seat\":{a},\"from\":null,\"icons\":3}}", SoRig.Wire(c.Events.Last(e => e.K == "starshyna"), null, "seat", "from", "icons"));
        Assert.Equal(3, c.Points(a));
        Assert.Contains($"Січова старшина — у {c.NameOf(a)} (3 ⚔)", c.Log);

        // Рівність — у власника.
        SoRig.Row(c, 40, 0);                      // ⚔, «Ткацький верстат»: 2 риби, 2 зілля, 3 вовни
        SoRig.Hand(c, b, fish: 2, herbs: 2, wool: 3);
        Assert.Null(c.Buy(b, 40));
        Assert.Equal(a, c.Starshyna);
        Assert.Equal(1, c.Points(b));
        Assert.Null(c.Take(a, ["clay", "wool", "fish"]));

        // Строго більше — переходить, очки ±3.
        SoRig.Row(c, 46, 0);                      // ⚔, «Рибна лава»: 3 полотна, 2 зілля, 2 глини
        SoRig.Hand(c, b, linen: 3, herbs: 2, clay: 2);
        Assert.Null(c.Buy(b, 46));
        Assert.Equal(b, c.Starshyna);
        Assert.Equal($"{{\"seat\":{b},\"from\":{a},\"icons\":4}}", SoRig.Wire(c.Events.Last(e => e.K == "starshyna"), null, "seat", "from", "icons"));
        Assert.Equal(0, c.Points(a));
        Assert.Equal(5, c.Points(b));
    }

    // ---------- булава й кінець (§2.6, §2.9) ----------

    /// <summary>Місцю — 25 ⭐ і всі буденні знижки (72 75 78 81 84), з цвітом — ще 85.</summary>
    static void Rich(SorochyntsiCore c, int s, bool fern)
    {
        SoRig.Own(c, s, 72, 75, 78, 81, 84);
        if (fern) SoRig.Own(c, s, 86);
    }

    [Fact]
    public void Stars_without_fern_bring_no_bulava()
    {
        var c = SoRig.Core(3);
        var s = c.Turn;
        Rich(c, s, fern: false);
        Assert.Equal(25, c.Points(s));
        Assert.Null(c.Take(s, ["linen", "fish", "herbs"]));
        Assert.Equal(-1, c.Bulava);
        Assert.False(c.Last);
    }

    [Fact]
    public void Bulava_starts_the_last_round_played_up_to_the_seat_before_first()
    {
        var c = SoRig.Core(3);
        SoRig.First(c, 0);
        Assert.Null(c.Take(0, ["linen", "fish", "herbs"]));
        Rich(c, 1, fern: true);
        Assert.Null(c.Take(1, ["linen", "fish", "herbs"]));
        Assert.Equal(1, c.Bulava);
        Assert.True(c.Last);
        Assert.Equal(["bulava", "last", "turn"], c.Events.TakeLast(3).Select(e => e.K));
        Assert.Equal("{\"reason\":\"bulava\"}", SoRig.Wire(c.Events[^2], null, "reason"));
        Assert.Contains($"{c.NameOf(1)} бере гетьманську булаву! Останнє коло", c.Log);
        // Другий, що виконав умову, — нічого не бере; а ⭐ у нього більше — він і переможе.
        SoRig.Own(c, 2, 70, 73, 76, 79, 82, 71, 85, 87);     // 30 ⭐, усі шість знижок
        Assert.Equal(2, c.Turn);
        Assert.False(c.Over);
        Assert.Null(c.Take(2, ["linen", "fish", "herbs"]));
        Assert.Equal(1, c.Bulava);
        Assert.True(c.Over);
        Assert.Equal("bulava", c.Reason);
        Assert.Equal([2], c.Winners);
        Assert.Equal(1, c.Round);
        Assert.Equal("{\"winners\":[2],\"reason\":\"bulava\",\"points\":[0,28,30]}", SoRig.Wire(c.Events[^1], null, "winners", "reason", "points"));
        Assert.Equal("Партія вже скінчилась", c.Take(0, ["linen", "fish", "herbs"]));
    }

    [Fact]
    public void Bulava_by_the_last_in_the_round_ends_the_game_at_once()
    {
        var c = SoRig.Core(3);
        SoRig.First(c, 0);
        Assert.Null(c.Take(0, ["linen", "fish", "herbs"]));
        Assert.Null(c.Take(1, ["linen", "fish", "herbs"]));
        Rich(c, 2, fern: true);
        Assert.Null(c.Take(2, ["clay", "wool", "fish"]));
        Assert.True(c.Over);
        Assert.Equal([2], c.Winners);
        Assert.Equal(2, c.Bulava);
        Assert.Equal(["bulava", "last", "over"], c.Events.TakeLast(3).Select(e => e.K));
        Assert.Single(c.Timeline);
    }

    [Fact]
    public void Ties_go_to_fewer_shops_then_are_shared()
    {
        foreach (var (extra, winners) in new[] { (true, new[] { 0 }), (false, new[] { 0, 1 }) })
        {
            var c = SoRig.Core(3);
            SoRig.First(c, 0);
            SoRig.Own(c, 0, 70, 73, 76, 79, 82, 86);              // 23 ⭐, 6 крамниць, булава
            SoRig.Own(c, 1, 71, 74, 77, 80, 83, 0);               // 20 ⭐ + старшина = 23, 6 крамниць
            if (extra) SoRig.Own(c, 1, 1);                        // сьома крамниця без ⭐
            c.Starshyna = 1;
            Assert.Equal(c.Points(0), c.Points(1));
            Assert.Null(c.Take(0, ["linen", "fish", "herbs"]));
            Assert.Equal(0, c.Bulava);
            Assert.Null(c.Take(1, ["linen", "fish", "herbs"]));
            Assert.Null(c.Take(2, ["linen", "fish", "herbs"]));
            Assert.True(c.Over);
            Assert.Equal(winners, c.Winners);
        }
    }

    [Theory]
    [InlineData(14, true)]
    [InlineData(18, false)]
    public void Target_option_decides_how_many_stars_the_bulava_needs(int target, bool taken)
    {
        var h = SoRig.Table(2, vp: target.ToString());
        var c = SoRig.CoreOf(h);
        Assert.Equal(target, c.Target);
        Assert.Equal(target, h.View(null).GetProperty("target").GetInt32());
        var s = c.Turn;
        SoRig.Own(c, s, 7, 15, 23, 31, 39, 85, 70, 73);   // усі шість знижок, рівно 16 ⭐
        Assert.Equal(16, c.Points(s));
        Assert.True(h.Act(s, "take", new { goods = new[] { "linen", "fish", "herbs" } }).Ok);
        Assert.Equal(taken ? s : -1, c.Bulava);
    }

    // ---------- запобіжники (§2.8, §2.9) ----------

    [Fact]
    public void Pass_only_when_nothing_else_and_everyone_passing_ends_the_fair()
    {
        var c = SoRig.Core(2);
        var s = c.Turn;
        Assert.Equal("Пропускати не можна — є що робити", c.Pass(s));
        SoRig.Piles(c, 0, 0, 0, 0, 0);
        foreach (var o in c.Order) SoRig.Reserve(c, o, c.Decks[2][0], c.Decks[2][1], c.Decks[2][2]);
        Assert.True(c.Legal(s).Pass);
        Assert.Null(c.Pass(s));
        Assert.False(c.Over);
        Assert.Contains($"{c.NameOf(s)} пропускає хід — нема що робити", c.Log);
        Assert.Null(c.Pass(c.Turn));
        Assert.True(c.Over);
        Assert.Equal("stall", c.Reason);
        Assert.Equal(["pass", "turn", "pass", "over"], c.Events.TakeLast(4).Select(e => e.K));
    }

    [Fact]
    public void A_pass_streak_is_broken_by_any_other_action()
    {
        var c = SoRig.Core(2);
        var s = c.Turn;
        var o = c.Order.First(x => x != s);
        SoRig.Piles(c, 0, 0, 0, 0, 0);
        SoRig.Reserve(c, s, c.Decks[2][0], c.Decks[2][1], c.Decks[2][2]);
        Assert.Null(c.Pass(s));
        Assert.Null(c.Reserve(o, null, 1));
        Assert.Equal(0, c.Passes);
        Assert.Null(c.Pass(s));
        Assert.False(c.Over);
    }

    [Fact]
    public void Round_forty_is_the_last_one()
    {
        var c = SoRig.Core(3);
        c.Target = 99;
        c.Round = 38;
        var ended = false;
        for (var i = 0; i < 100 && !c.Over; i++) SorochyntsiBot.Step(c, c.Turn);
        ended = c.Over;
        Assert.True(ended);
        Assert.Equal("rounds", c.Reason);
        Assert.Equal(40, c.Round);
        var last = c.Events.Single(e => e.K == "last");
        Assert.Equal("{\"reason\":\"rounds\"}", SoRig.Wire(last, null, "reason"));
        var idx = c.Events.IndexOf(last);
        Assert.Equal("round", c.Events[idx + 1].K);
        Assert.Equal(40, c.Events[idx + 1]["n"]);
        Assert.Contains("Коло 40 — останнє: ярмарок зачиняється", c.Log);
    }

    // ---------- через кімнату ----------

    [Fact]
    public void Lobby_options_and_a_lone_host_need_a_bot()
    {
        var h = new RoomHarness("sorochyntsi", new { bots = "0" }, seed: 3);
        h.Join("Оля");
        var info = h.Room.Info;
        Assert.Equal(("Сорочинський ярмарок", GameGroup.Board, 1, 4, StartMode.ByHost, true, false), (info.Title, info.Group, info.MinPlayers, info.MaxPlayers, info.Start, info.Hidden, info.Rated));
        Assert.Equal(["bots", "vp", "timer"], info.Options!.Select(o => o.Key));
        Assert.Equal("16", info.Options!.Single(o => o.Key == "vp").Default);
        Assert.Equal("60", info.Options!.Single(o => o.Key == "timer").Default);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("phase").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("me").ValueKind);
        Assert.False(h.Start().Ok);
        Assert.Equal("Самому нема з ким торгувати: хай хтось сяде — або додай 🤖 Глека", h.Reply.Message);

        var b = new RoomHarness("sorochyntsi", new { bots = "1" }, seed: 3);
        b.Join("Оля");
        Assert.True(b.Start().Ok);
        var v = b.View(0);
        Assert.Equal("play", v.GetProperty("phase").GetString());
        Assert.Equal(2, v.GetProperty("players").GetArrayLength());
        Assert.Equal("Глек 🤖", v.GetProperty("players")[1].GetProperty("bot").GetString());
        Assert.Equal("сині", ((Sorochyntsi)b.Room.Game).SeatName(1));
    }

    [Fact]
    public void Act_checks_seat_turn_and_payload_in_the_contract_order()
    {
        var h = SoRig.Table(2);
        var c = SoRig.CoreOf(h);
        var s = c.Turn;
        var o = c.Order.First(x => x != s);
        Assert.Equal("Тут так не ходять", h.Act(o, "dance").Message);
        Assert.Equal($"Зараз ходить {h.NickOf(s)}", h.Act(o, "take", new { goods = new[] { "fish", "herbs", "clay" } }).Message);
        Assert.Equal("Не зрозумів, що береш", h.Act(s, "take", new { goods = "fish" }).Message);
        Assert.Equal("Не зрозумів, що береш", h.Act(s, "take", new { goods = new object[] { "fish", 2 } }).Message);
        Assert.Equal("Не зрозумів, що купуєш", h.Act(s, "buy", new { }).Message);
        Assert.Equal("Пропускати не можна — є що робити", h.Act(s, "pass", new { }).Message);
        Assert.Equal("Зараз нічого повертати", h.Act(s, "discard", new { give = new { fish = 1 } }).Message);
        Assert.Equal("Гостей зараз не кличуть", h.Act(s, "guest", new { guest = 1 }).Message);
        Assert.Equal("Ще є час", h.Act(o, "timeout").Message);
        Assert.Equal("Глекам нема що робити", h.Act(o, BoardBots.Nudge).Message);
        var game = SoRig.GameOf(h);
        lock (h.Room.Sync) Assert.Equal("Ти не граєш за цим столом", game.Act(3, "take", Views.Payload(new { goods = new[] { "fish" } })).Message);
        Assert.True(h.Act(s, "take", new { goods = new[] { "fish", "herbs", "clay" } }).Ok);
        Assert.Equal(o, c.Turn);
        // Повернути: не об'єкт чисел — не зрозумів.
        SoRig.Hand(c, o, 2, 2, 2, 2, 2);
        Assert.True(h.Act(o, "take", new { goods = new[] { "fish", "herbs", "clay" } }).Ok);
        Assert.Equal("Не зрозумів, що повертаєш", h.Act(o, "discard", new { give = new { fish = -1, herbs = 4 } }).Message);
        Assert.Equal("Не зрозумів, що повертаєш", h.Act(o, "discard", new { give = new[] { "fish" } }).Message);
        Assert.Equal("Поверни рівно 3 жетони", h.Act(o, "discard", new { give = new { fish = 1 } }).Message);
        Assert.True(h.Act(o, "discard", new { give = new { fish = 1, herbs = 1, clay = 1 } }).Ok);
    }

    [Fact]
    public void Whole_game_with_bots_finishes_once_and_names_the_winner()
    {
        var h = SoRig.Table(1, bots: "3", seed: 21);
        for (var i = 0; i < 3000 && h.Finished.Count == 0; i++) SoRig.Step(h, 0);
        Assert.Single(h.Finished);
        var c = SoRig.CoreOf(h);
        Assert.True(c.Over);
        var v = h.View(0);
        Assert.Equal("over", v.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
        var over = v.GetProperty("over");
        Assert.Equal(c.Reason, over.GetProperty("reason").GetString());
        Assert.Equal(c.Order.Select(c.Points), over.GetProperty("points").EnumerateArray().Select(x => x.GetInt32()));
        var stats = over.GetProperty("stats");
        Assert.Equal(c.Timeline.Count, stats.GetProperty("rounds").GetInt32());
        Assert.Equal(4, stats.GetProperty("seats").GetArrayLength());
        Assert.All(stats.GetProperty("timeline").EnumerateArray(), r => Assert.Equal(4, r.GetArrayLength()));
        var journal = h.Outbox.OfType<Journal>().Select(j => j.Text).Last(t => t.StartsWith("Сорочинський ярмарок: ") && t.Contains("⭐ ("));
        Assert.Contains(c.NameOf(c.Winners[0]), journal);
        Assert.Equal("Партія вже скінчилась", SoRig.GameOf(h).Act(0, "take", Views.Payload(new { goods = new[] { "fish" } })).Message);
    }

    [Fact]
    public void Achievements_go_to_the_person_at_the_table()
    {
        var h = SoRig.Table(1, bots: "2", seed: 4);
        var c = SoRig.CoreOf(h);
        // Довести до ходу людини.
        for (var i = 0; i < 20 && c.Turn != 0; i++) SoRig.Step(h, 0);
        Assert.Equal(0, c.Turn);
        SoRig.Own(c, 0, 85, 86, 72, 75, 78, 81, 84);         // 2 цвіти, 31 ⭐
        SoRig.Own(c, 0, 3, 11, 19, 27, 35, 40);              // 6 ⚔
        c.Starshyna = 0;
        c.Ferns[0] = 2;                                      // статистика купівель — так, наче купила сама
        foreach (var g in c.Table.Take(3))
        {
            c.GuestBy[g] = 0;
            c.GuestsOf[0].Add(g);
        }
        Assert.True(h.Act(0, "take", new { goods = new[] { "linen", "fish", "herbs" } }).Ok);
        Assert.Equal(0, c.Bulava);
        for (var i = 0; i < 20 && h.Finished.Count == 0; i++) SoRig.Step(h, 0);
        Assert.Single(h.Finished);
        Assert.Equal([0], c.Winners);
        var got = h.Awards.Where(a => a.Nick == "Оля").Select(a => a.Reason).ToHashSet();
        Assert.Superset(new HashSet<string> { "ach:sor-bulava", "ach:sor-fern", "ach:sor-guests", "ach:sor-kish", "ach:sor-honest" }, got);
        Assert.All(new[] { "sor-bulava", "sor-fern", "sor-guests", "sor-kish", "sor-honest" },
            k => Assert.Contains(Hlechyky.Games.Economy.AchievementCatalog.All, a => a.Key == k));
    }

    [Fact]
    public void Rematch_starts_clean_and_seq_keeps_growing()
    {
        var h = SoRig.Table(1, bots: "1", seed: 8);
        for (var i = 0; i < 3000 && h.Finished.Count == 0; i++) SoRig.Step(h, 0);
        Assert.Single(h.Finished);
        var seq = SoRig.CoreOf(h).Seq;
        Assert.True(h.Rematch("Оля").Ok, h.Reply.Message);
        var c = SoRig.CoreOf(h);
        Assert.False(c.Over);
        Assert.True(c.Seq > seq);
        Assert.Equal(1, c.Round);
        Assert.All(c.Order, s => Assert.Equal(0, c.Points(s) + c.NTok(s)));
        Assert.Equal(c.Seq, h.View(null).GetProperty("seq").GetInt64());
    }
}

/// <summary>Спільне для тестів «Сорочинського ярмарку»: столи, ядро з розкладом і ручне розкладання стану.</summary>
internal static class SoRig
{
    public static readonly string[] Crew = ["Оля", "Петро", "Іра", "Влад"];

    public static RoomHarness Table(int people, string bots = "0", string timer = "60", string vp = "16", int seed = 5)
    {
        var h = new RoomHarness("sorochyntsi", new { timer, bots, vp }, seed: seed);
        for (var i = 0; i < people; i++) h.Join(Crew[i]);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    public static Sorochyntsi GameOf(RoomHarness h) => (Sorochyntsi)h.Room.Game;
    public static SorochyntsiCore CoreOf(RoomHarness h) => GameOf(h).Core;

    public static SorochyntsiCore Core(int players, int seed = 5) => SorochyntsiBotTests.Core(players, seed);

    /// <summary>
    /// Один крок столу від місця <paramref name="by"/>: штовхнути Глека; нема на кого — людина, чий хід, грає підказку
    /// Глека (поза її ходом — нічого).
    /// </summary>
    public static void Step(RoomHarness h, int by)
    {
        var v = h.View(by);
        if (v.GetProperty("botIn").ValueKind == JsonValueKind.Number)
        {
            h.Clock.AdvanceMs(Math.Max(BoardBots.ThinkMs, v.GetProperty("botIn").GetInt32()));
            var r = h.Act(by, BoardBots.Nudge);
            Assert.True(r.Ok, r.Message);
            return;
        }
        var turn = v.GetProperty("turn").GetInt32();
        var tip = h.View(turn).GetProperty("me").GetProperty("tip");
        var res = PlayTip(h, turn, tip);
        Assert.True(res.Ok, res.Message);
    }

    /// <summary>Зіграти «💡 Глек радить» як дію людини (тим самим payload, що шле клієнт).</summary>
    public static ActResult PlayTip(RoomHarness h, int seat, JsonElement tip)
    {
        var act = tip.GetProperty("act").GetString()!;
        return act switch
        {
            "take" => h.Act(seat, "take", new { goods = tip.GetProperty("goods").EnumerateArray().Select(x => x.GetString()).ToArray() }),
            "reserve" when tip.TryGetProperty("card", out var card) => h.Act(seat, "reserve", new { card = card.GetInt32() }),
            "reserve" => h.Act(seat, "reserve", new { tier = tip.GetProperty("tier").GetInt32() }),
            "buy" => h.Act(seat, "buy", new { card = tip.GetProperty("card").GetInt32() }),
            "discard" => h.Act(seat, "discard", new { give = JsonSerializer.Deserialize<Dictionary<string, int>>(tip.GetProperty("give").GetRawText()) }),
            "guest" => h.Act(seat, "guest", new { guest = tip.GetProperty("guest").GetInt32() }),
            _ => h.Act(seat, "pass", new { }),
        };
    }

    /// <summary>Знімок стану ядра для «помилка нічого не міняє».</summary>
    public static string State(SorochyntsiCore c) => JsonSerializer.Serialize(c.Snapshot());

    /// <summary>Поля події так, як їх побачить <paramref name="seat"/> на дроті.</summary>
    public static string Wire(SoEvent e, int? seat, params string[] keys)
    {
        var d = e.For(seat);
        return JsonSerializer.Serialize(keys.ToDictionary(k => k, k => d[k]), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    /// <summary>Перший гравець — <paramref name="s"/> (на початку партії: коло 1, його хід).</summary>
    public static void First(SorochyntsiCore c, int s)
    {
        c.First = s;
        c.Turn = s;
    }

    /// <summary>Купи буденних товарів (банк на початку теж — щоб інваріанти сходились).</summary>
    public static void Piles(SorochyntsiCore c, params int[] goods)
    {
        for (var g = 0; g < 5; g++)
        {
            c.Bank[g] = goods[g];
            c.Bank0[g] = goods[g] + c.Order.Sum(s => c.Tok[s][g]);
        }
    }

    /// <summary>Рука місця рівно така; банк на початку підправляється, щоб банк + руки сходились.</summary>
    public static void Hand(SorochyntsiCore c, int s, int linen = 0, int fish = 0, int herbs = 0, int clay = 0, int wool = 0, int gold = 0)
    {
        int[] a = [linen, fish, herbs, clay, wool, gold];
        for (var i = 0; i < 6; i++)
        {
            c.Bank0[i] += a[i] - c.Tok[s][i];
            c.Tok[s][i] = a[i];
        }
    }

    /// <summary>Прибрати крамницю звідусіль (колода, ряд — тоді місце поповнюється з колоди, бронь).</summary>
    static void Detach(SorochyntsiCore c, int id)
    {
        var t = Deck[id].Tier - 1;
        if (c.Decks[t].Remove(id)) return;
        if (c.InRows(id) is { } at)
        {
            var next = c.Decks[t].Count > 0 ? c.Decks[t][0] : -1;
            if (next >= 0) c.Decks[t].RemoveAt(0);
            c.Rows[at.Tier][at.Slot] = next;
            return;
        }
        foreach (var r in c.Reserved) r.RemoveAll(x => x.Id == id);
    }

    /// <summary>Місце вже купило ці крамниці.</summary>
    public static void Own(SorochyntsiCore c, int s, params int[] ids)
    {
        foreach (var id in ids)
        {
            Detach(c, id);
            c.Cards[s].Add(id);
            c.Bonus[s][Deck[id].Bonus]++;
            c.CardPts[s] += Deck[id].Pts;
            c.Icons[s] += Deck[id].K;
        }
    }

    /// <summary>Місце вже забронювало ці крамниці (з ряду — відкрито).</summary>
    public static void Reserve(SorochyntsiCore c, int s, params int[] ids)
    {
        foreach (var id in ids)
        {
            Detach(c, id);
            c.Reserved[s].Add(new SoRes(id, false));
        }
    }

    /// <summary>Покласти крамницю в ряд її рівня на місце <paramref name="slot"/> (те, що там було, — туди, де була вона).</summary>
    public static void Row(SorochyntsiCore c, int id, int slot)
    {
        var t = Deck[id].Tier - 1;
        var cur = c.Rows[t][slot];
        if (cur == id) return;
        if (c.InRows(id) is { } at) c.Rows[t][at.Slot] = cur;
        else
        {
            var k = c.Decks[t].IndexOf(id);
            Assert.True(k >= 0, $"крамниці {id} нема в колоді");
            if (cur >= 0) c.Decks[t][k] = cur;
            else c.Decks[t].RemoveAt(k);
        }
        c.Rows[t][slot] = id;
    }
}
