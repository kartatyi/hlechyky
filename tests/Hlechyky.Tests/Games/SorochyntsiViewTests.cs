using System.Text.Json;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using static Hlechyky.Games.Impl.SorochyntsiCards;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Вид «Сорочинського ярмарку» (контракт §5, §14 «Вид»): форма на дроті, приховане (бронь наосліп і порядок колод — нікому,
/// крім власника броні), <c>me.cards</c> збігається з тим, що приймає <c>buy</c>, <c>me.take</c>/<c>can</c> — з тим, що
/// приймає <c>take</c>, а <c>why</c> — дослівно тексти помилок.
/// </summary>
public class SorochyntsiViewTests
{
    static readonly string[] RootKeys =
    [
        "v", "phase", "target", "round", "maxRounds", "last", "players", "order", "first", "turn", "bank", "rows", "decks",
        "guests", "starshyna", "bulava", "discard", "events", "seq", "deadline", "timerMs", "bots", "botIn", "log", "over", "me",
    ];

    static readonly string[] PlayerKeys =
    [
        "seat", "nick", "bot", "color", "tokens", "ntok", "bonus", "cards", "reserved", "guests", "icons", "points",
        "starshyna", "bulava", "away",
    ];

    static readonly string[] MeKeys = ["seat", "reserved", "must", "discard", "guests", "take", "can", "why", "cards", "bulava", "tip"];

    static string[] Keys(JsonElement e) => [.. e.EnumerateObject().Select(p => p.Name)];

    [Fact]
    public void View_has_the_contract_shape_for_a_player_and_a_spectator()
    {
        var h = SoRig.Table(3);
        var c = SoRig.CoreOf(h);
        var s = c.Turn;
        var v = h.View(s);
        Assert.Equal(RootKeys, Keys(v));
        Assert.Equal(PlayerKeys, Keys(v.GetProperty("players")[0]));
        Assert.Equal(MeKeys, Keys(v.GetProperty("me")));
        Assert.Equal(1, v.GetProperty("v").GetInt32());
        Assert.Equal("play", v.GetProperty("phase").GetString());
        Assert.Equal(16, v.GetProperty("target").GetInt32());
        Assert.Equal(40, v.GetProperty("maxRounds").GetInt32());
        Assert.Equal([0, 1, 2], v.GetProperty("order").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(c.First, v.GetProperty("first").GetInt32());
        Assert.Equal(s, v.GetProperty("turn").GetInt32());
        Assert.Equal(["linen", "fish", "herbs", "clay", "wool", "gold"], Keys(v.GetProperty("bank")));
        Assert.Equal(["linen", "fish", "herbs", "clay", "wool", "fern"], Keys(v.GetProperty("players")[0].GetProperty("bonus")));
        Assert.Equal(3, v.GetProperty("rows").GetArrayLength());
        Assert.All(v.GetProperty("rows").EnumerateArray(), r => Assert.Equal(4, r.GetArrayLength()));
        Assert.Equal([36, 26, 16], v.GetProperty("decks").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(4, v.GetProperty("guests").GetArrayLength());
        Assert.Equal(["id", "by"], Keys(v.GetProperty("guests")[0]));
        Assert.Equal(JsonValueKind.Null, v.GetProperty("starshyna").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("discard").ValueKind);
        Assert.Equal(60000, v.GetProperty("timerMs").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("bots").ValueKind);
        Assert.Equal(["round", "turn"], v.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("k").GetString()));
        var me = v.GetProperty("me");
        Assert.Equal("act", me.GetProperty("must").GetString());
        Assert.Equal(12, me.GetProperty("cards").EnumerateObject().Count());
        Assert.Equal(["piles", "need", "two"], Keys(me.GetProperty("take")));
        Assert.Equal(3, me.GetProperty("take").GetProperty("need").GetInt32());
        Assert.Equal(["take", "reserve", "reserveDeck", "pass"], Keys(me.GetProperty("can")));
        Assert.True(me.GetProperty("can").GetProperty("take").GetBoolean());
        Assert.False(me.GetProperty("can").GetProperty("pass").GetBoolean());
        Assert.Equal(16, me.GetProperty("bulava").GetProperty("points").GetInt32());
        Assert.Equal(6, me.GetProperty("bulava").GetProperty("goods").GetArrayLength());
        Assert.Equal(JsonValueKind.Object, me.GetProperty("tip").ValueKind);

        var spect = h.View(null);
        Assert.Equal(JsonValueKind.Null, spect.GetProperty("me").ValueKind);
        // Поза своїм ходом: can — усе false, why порожнє, must і tip — null, а ціни крамниць є.
        var o = c.Order.First(x => x != s);
        var other = h.View(o).GetProperty("me");
        Assert.Equal(JsonValueKind.Null, other.GetProperty("must").ValueKind);
        Assert.Equal(JsonValueKind.Null, other.GetProperty("tip").ValueKind);
        Assert.False(other.GetProperty("can").GetProperty("take").GetBoolean());
        Assert.All(other.GetProperty("can").GetProperty("reserveDeck").EnumerateArray(), x => Assert.False(x.GetBoolean()));
        Assert.Empty(other.GetProperty("why").EnumerateObject());
        Assert.Equal(12, other.GetProperty("cards").EnumerateObject().Count());
    }

    /// <summary>Усі числа під ключами id/card і в масивах cards/rows — так шукаємо id крамниць на дроті.</summary>
    static HashSet<int> CardIds(JsonElement e, string? key = null)
    {
        var set = new HashSet<int>();
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == "cards" && p.Value.ValueKind == JsonValueKind.Object)
                        foreach (var q in p.Value.EnumerateObject()) set.Add(int.Parse(q.Name));
                    set.UnionWith(CardIds(p.Value, p.Name));
                }
                break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray()) set.UnionWith(CardIds(x, key is "cards" or "rows" ? key : null));
                break;
            case JsonValueKind.Number when key is "id" or "card" or "cards" or "rows":
                set.Add(e.GetInt32());
                break;
        }
        return set;
    }

    [Fact]
    public void Blind_reserve_is_seen_only_by_its_owner_and_a_row_reserve_by_all()
    {
        var h = SoRig.Table(3);
        var c = SoRig.CoreOf(h);
        var s = c.Turn;
        var x = c.Decks[2][0];
        Assert.True(h.Act(s, "reserve", new { tier = 3 }).Ok);
        foreach (var who in new int?[] { (s + 1) % 3, (s + 2) % 3, null })
        {
            var v = h.View(who);
            Assert.DoesNotContain(x, CardIds(v));
            var res = v.GetProperty("players")[s].GetProperty("reserved")[0];
            Assert.Equal(JsonValueKind.Null, res.GetProperty("id").ValueKind);
            Assert.Equal(3, res.GetProperty("tier").GetInt32());
            Assert.True(res.GetProperty("blind").GetBoolean());
            var ev = v.GetProperty("events").EnumerateArray().Single(e => e.GetProperty("k").GetString() == "reserve");
            Assert.Equal(JsonValueKind.Null, ev.GetProperty("card").ValueKind);
            Assert.Equal(JsonValueKind.Null, ev.GetProperty("slot").ValueKind);
        }
        var mine = h.View(s);
        Assert.Equal(x, mine.GetProperty("players")[s].GetProperty("reserved")[0].GetProperty("id").GetInt32());
        Assert.Equal(x, mine.GetProperty("me").GetProperty("reserved")[0].GetProperty("id").GetInt32());
        Assert.True(mine.GetProperty("me").GetProperty("cards").TryGetProperty(x.ToString(), out _));
        Assert.Equal(x, mine.GetProperty("events").EnumerateArray().Single(e => e.GetProperty("k").GetString() == "reserve").GetProperty("card").GetInt32());

        // Бронь з ряду — відкрита: усі бачили, звідки взяли.
        var t = c.Turn;
        var y = c.Rows[0][2];
        Assert.True(h.Act(t, "reserve", new { card = y }).Ok);
        foreach (var who in new int?[] { s, null })
        {
            var res = h.View(who).GetProperty("players")[t].GetProperty("reserved")[0];
            Assert.Equal(y, res.GetProperty("id").GetInt32());
            Assert.False(res.GetProperty("blind").GetBoolean());
        }
    }

    [Fact]
    public void Deck_order_and_someone_elses_blind_card_never_reach_the_wire()
    {
        var h = SoRig.Table(3);
        var c = SoRig.CoreOf(h);
        var s = c.Turn;
        Assert.True(h.Act(s, "reserve", new { tier = 2 }).Ok);
        int?[] viewers = [(s + 1) % 3, (s + 2) % 3, null];
        var before = viewers.Select(v => h.View(v).GetRawText()).ToArray();
        // Інший порядок колод і інша карта в чужій броні наосліп — вид інших місць і глядача той самий.
        foreach (var d in c.Decks) d.Reverse();
        var hidden = c.Reserved[s][0].Id;
        var swap = c.Decks[1][0];
        c.Decks[1][0] = hidden;
        c.Reserved[s][0] = new SoRes(swap, true);
        Assert.Equal(before, viewers.Select(v => h.View(v).GetRawText()).ToArray());
        Assert.DoesNotContain(hidden, CardIds(h.View(null)));
    }

    [Fact]
    public void Prices_in_the_view_are_exactly_what_buy_accepts_and_charges()
    {
        var h = SoRig.Table(1, bots: "3", seed: 13);
        var c = SoRig.CoreOf(h);
        var checkedBuys = 0;
        for (var i = 0; i < 600 && !c.Over; i++)
        {
            if (c.Turn == 0 && c.Phase == SoPhase.Play)
            {
                var me = h.View(0).GetProperty("me");
                var cards = me.GetProperty("cards");
                Assert.Equal(c.Visible(0).Select(id => id.ToString()).Order(), cards.EnumerateObject().Select(p => p.Name).Order());
                foreach (var p in cards.EnumerateObject())
                {
                    var id = int.Parse(p.Name);
                    var copy = c.Clone();
                    var was = (int[])copy.Tok[0].Clone();
                    var err = copy.Buy(0, id);
                    Assert.Equal(p.Value.GetProperty("ok").GetBoolean(), err is null);
                    if (err is null)
                    {
                        var pay = JsonSerializer.Deserialize<Dictionary<string, int>>(p.Value.GetProperty("pay").GetRawText())!;
                        for (var g = 0; g < 6; g++) Assert.Equal(pay.GetValueOrDefault(Tokens[g]), was[g] - copy.Tok[0][g]);
                        checkedBuys++;
                    }
                    else
                    {
                        Assert.Equal("Не вистачає: " + p.Value.GetProperty("why").GetString()!["бракує ".Length..], err);
                        var need = JsonSerializer.Deserialize<Dictionary<string, int>>(p.Value.GetProperty("need").GetRawText())!;
                        var gold = c.Tok[0][Gold];
                        Assert.Equal(Math.Max(0, need.Values.Sum() - gold), p.Value.GetProperty("short").GetInt32());
                        Assert.All(need.Values, n => Assert.True(n > 0));
                    }
                }
            }
            SoRig.Step(h, 0);
        }
        Assert.True(checkedBuys > 5, $"перевірено лише {checkedBuys} купівель");
    }

    /// <summary>Усі списки товарів довжиною 0–4 (з повторами) — що лише може прислати клієнт.</summary>
    static IEnumerable<string[]> AllTakes()
    {
        yield return [];
        foreach (var a in Goods)
        {
            yield return [a];
            foreach (var b in Goods)
            {
                yield return [a, b];
                foreach (var d in Goods) yield return [a, b, d];
            }
        }
        yield return ["linen", "fish", "herbs", "clay"];
    }

    [Fact]
    public void Take_options_in_the_view_match_what_take_accepts_for_every_pile_state()
    {
        var c = SoRig.Core(3);
        var s = c.Turn;
        int[] sizes = [0, 1, 3, 4];
        var takes = AllTakes().ToList();
        for (var code = 0; code < 1024; code++)
        {
            var piles = new int[5];
            for (int g = 0, k = code; g < 5; g++, k /= 4) piles[g] = sizes[k % 4];
            SoRig.Piles(c, piles);
            var l = c.Legal(s);
            Assert.Equal(Enumerable.Range(0, 5).Where(g => piles[g] > 0), l.Piles);
            Assert.Equal(Math.Min(3, l.Piles.Length), l.Need);
            Assert.Equal(Enumerable.Range(0, 5).Where(g => piles[g] >= 4), l.Two);
            var any = false;
            foreach (var t in takes)
            {
                var idx = t.Select(GoodIdx).ToArray();
                var distinct = idx.Length == l.Need && idx.Distinct().Count() == idx.Length && idx.All(g => piles[g] > 0) && idx.Length > 0;
                var two = idx.Length == 2 && idx[0] == idx[1] && piles[idx[0]] >= 4;
                var ok = c.TakeError(t, out _) is null;
                Assert.True(ok == (distinct || two), $"купи [{string.Join(",", piles)}], взяти [{string.Join(",", t)}]: {c.TakeError(t, out _)}");
                any |= ok;
            }
            Assert.Equal(any, l.Take);
            // Набори Глека — ті самі законні.
            Assert.All(c.TakeOptions(), got => Assert.Null(c.TakeError(new SoMove("take", got).Goods(), out _)));
            Assert.Equal(l.Take, c.TakeOptions().Count > 0);
        }
    }

    [Fact]
    public void Why_texts_are_the_very_errors_of_act()
    {
        var h = SoRig.Table(2);
        var c = SoRig.CoreOf(h);
        var s = c.Turn;
        SoRig.Reserve(c, s, c.Decks[0][0], c.Decks[0][1], c.Decks[1][0]);
        c.Decks[2].Clear();
        SoRig.Piles(c, 0, 0, 0, 0, 0);
        SoRig.Row(c, 6, 0);                       // «Рушничок» за 3 риби — купити є що
        SoRig.Hand(c, s, fish: 3);
        var me = h.View(s).GetProperty("me");
        var why = me.GetProperty("why");
        var can = me.GetProperty("can");
        Assert.False(can.GetProperty("take").GetBoolean());
        Assert.False(can.GetProperty("reserve").GetBoolean());
        Assert.False(can.GetProperty("pass").GetBoolean());
        Assert.Equal(h.Act(s, "take", new { goods = new[] { "fish" } }).Message, why.GetProperty("take").GetString());
        Assert.Equal(h.Act(s, "reserve", new { card = c.Rows[0][0] }).Message, why.GetProperty("reserve").GetString());
        Assert.Equal(h.Act(s, "reserve", new { tier = 1 }).Message, why.GetProperty("deck1").GetString());
        Assert.Equal("Бронювати можна щонайбільше 3 крамниці", why.GetProperty("deck3").GetString());
        Assert.Equal(h.Act(s, "pass", new { }).Message, why.GetProperty("pass").GetString());
        Assert.Equal(["take", "reserve", "deck1", "deck2", "deck3", "pass"], Keys(why));
        // Ні рядів, ні колод, ні броні — лишається тільки пас.
        c.Reserved[s].Clear();
        foreach (var r in c.Rows) Array.Fill(r, -1);
        foreach (var d in c.Decks) d.Clear();
        var v = h.View(s).GetProperty("me");
        Assert.True(v.GetProperty("can").GetProperty("pass").GetBoolean());
        Assert.False(v.GetProperty("why").TryGetProperty("pass", out _));
        Assert.Equal("Колода I рівня скінчилась", v.GetProperty("why").GetProperty("deck1").GetString());
        Assert.Equal(h.Act(s, "reserve", new { tier = 2 }).Message, v.GetProperty("why").GetProperty("deck2").GetString());
        Assert.True(h.Act(s, "pass", new { }).Ok);
    }

    [Fact]
    public void Discard_and_guest_phases_show_who_and_what_with_a_legal_tip()
    {
        var h = SoRig.Table(2);
        var c = SoRig.CoreOf(h);
        var s = c.Turn;
        c.Table = [0, 5, 7];
        SoRig.Own(c, s, 0, 1, 2, 4, 8, 9, 10, 12, 16, 17, 18);
        SoRig.Hand(c, s, 2, 2, 2, 2, 2);
        Assert.True(h.Act(s, "take", new { goods = new[] { "linen", "fish", "herbs" } }).Ok);
        var spect = h.View(null);
        Assert.Equal("discard", spect.GetProperty("phase").GetString());
        Assert.Equal(s, spect.GetProperty("discard").GetProperty("seat").GetInt32());
        Assert.Equal(3, spect.GetProperty("discard").GetProperty("n").GetInt32());
        var me = h.View(s).GetProperty("me");
        Assert.Equal("discard", me.GetProperty("must").GetString());
        Assert.Equal(3, me.GetProperty("discard").GetInt32());
        Assert.False(me.GetProperty("can").GetProperty("take").GetBoolean());
        var tip = me.GetProperty("tip");
        Assert.Equal("discard", tip.GetProperty("act").GetString());
        Assert.Equal(3, tip.GetProperty("give").EnumerateObject().Sum(p => p.Value.GetInt32()));
        Assert.True(SoRig.PlayTip(h, s, tip).Ok);

        var g = h.View(s);
        Assert.Equal("guest", g.GetProperty("phase").GetString());
        me = g.GetProperty("me");
        Assert.Equal("guest", me.GetProperty("must").GetString());
        Assert.Equal([0, 5], me.GetProperty("guests").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal("guest", me.GetProperty("tip").GetProperty("act").GetString());
        Assert.True(SoRig.PlayTip(h, s, me.GetProperty("tip")).Ok);
        var after = h.View(null);
        Assert.Equal("play", after.GetProperty("phase").GetString());
        Assert.Single(after.GetProperty("guests").EnumerateArray(), x => x.GetProperty("by").ValueKind == JsonValueKind.Number);
    }

    [Fact]
    public void Bulava_hint_counts_what_is_missing_and_disappears_once_taken()
    {
        var h = SoRig.Table(2);
        var c = SoRig.CoreOf(h);
        var s = c.Turn;
        var o = c.Order.First(x => x != s);
        SoRig.Own(c, o, 45, 51);                  // 6 ⭐, полотно й риба
        var b = h.View(o).GetProperty("me").GetProperty("bulava");
        Assert.Equal(10, b.GetProperty("points").GetInt32());
        Assert.Equal(["herbs", "clay", "wool", "fern"], b.GetProperty("goods").EnumerateArray().Select(x => x.GetString()));
        SoRig.Own(c, s, 72, 75, 78, 81, 84, 86);
        Assert.True(h.Act(s, "take", new { goods = new[] { "linen", "fish", "herbs" } }).Ok);
        var v = h.View(o);
        Assert.Equal(s, v.GetProperty("bulava").GetInt32());
        Assert.True(v.GetProperty("last").GetBoolean());
        Assert.True(v.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == s).GetProperty("bulava").GetBoolean());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("me").GetProperty("bulava").ValueKind);
    }
}
