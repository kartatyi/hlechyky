using System.Text.Json;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using static Hlechyky.Games.Impl.SolyanyiMap;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Вид «Соляного шляху» на дроті (контракт §5, §7, §9): форма JSON, приховане (чужа рука, замовлення, пропозиції, порядок
/// колод, виконане) ні гравцю, ні глядачу, узгодженість <c>me.tickets</c> і <c>me.claims</c>, поради, кінець партії.
/// </summary>
public class SolyanyiViewTests
{
    static readonly string[] RootKeys =
    [
        "v", "phase", "board", "round", "turns", "wagons", "last", "left", "players", "order", "first", "turn", "claimed", "closed",
        "market", "deck", "discard", "ticketsLeft", "longest", "events", "seq", "deadline", "timerMs", "bots", "botIn", "log", "over", "me",
    ];

    static string[] Keys(JsonElement e) => [.. e.EnumerateObject().Select(p => p.Name)];

    [Fact]
    public void Shape_on_the_wire_follows_the_contract()
    {
        var h = SlRig.Play(2, bots: "1");
        var v = h.View(0);
        Assert.Equal(RootKeys, Keys(v));
        Assert.Equal(1, v.GetProperty("v").GetInt32());
        Assert.Equal("01c5d1de", v.GetProperty("board").GetProperty("sig").GetString());
        Assert.Equal("one", v.GetProperty("board").GetProperty("doubles").GetString());
        Assert.Equal(["seat", "nick", "bot", "color", "hand", "tickets", "wagons", "points", "road", "routes", "ready", "away"], Keys(v.GetProperty("players")[0]));
        var me = v.GetProperty("me");
        Assert.Equal(["seat", "own", "hand", "tickets", "score", "must", "offer", "keepMin", "can", "why", "claims", "tip"], Keys(me));
        Assert.Equal(Cards, Keys(me.GetProperty("hand")));
        Assert.Equal(["routes", "done", "open", "total"], Keys(me.GetProperty("score")));
        Assert.Equal(["draw", "deck", "market", "tickets", "claim", "pass"], Keys(me.GetProperty("can")));
        Assert.Equal(5, v.GetProperty("market").GetArrayLength());
        Assert.Equal(5, v.GetProperty("bots").GetArrayLength());
        Assert.Equal("Глек 🤖", v.GetProperty("bots")[2].GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("over").ValueKind);
        Assert.Equal(["len", "seats"], Keys(v.GetProperty("longest")));
        Assert.Equal(0, v.GetProperty("longest").GetProperty("len").GetInt32());
        Assert.Equal(60000, v.GetProperty("timerMs").GetInt32());
        // Глядач — без me, усе інше так само.
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("me").ValueKind);
        var four = SlRig.Play(4);
        Assert.Equal("both", four.View(0).GetProperty("board").GetProperty("doubles").GetString());
        Assert.Equal(JsonValueKind.Null, four.View(0).GetProperty("bots").ValueKind);
    }

    [Fact]
    public void Before_the_start_the_view_is_empty_but_valid()
    {
        var h = new RoomHarness("solyanyi", new { timer = "60", bots = "0", wagons = "35" }, seed: 2);
        h.Join("Оля");
        var v = h.View(0);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("phase").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("me").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
        Assert.Equal(35, v.GetProperty("wagons").GetInt32());
    }

    [Fact]
    public void Setup_shows_who_chose_but_not_what_and_the_offer_only_to_its_owner()
    {
        var h = SlRig.Table(3, seed: 4);
        var c = SlRig.CoreOf(h);
        var s = c.First;
        var o = c.Next(s);
        var v = h.View(s);
        Assert.Equal("setup", v.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
        Assert.Equal(0, v.GetProperty("round").GetInt32());
        var me = v.GetProperty("me");
        Assert.Equal("setup", me.GetProperty("must").GetString());
        Assert.Equal(2, me.GetProperty("keepMin").GetInt32());
        Assert.Equal(c.Offer[s], me.GetProperty("offer").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));
        foreach (var x in me.GetProperty("offer").EnumerateArray())
            Assert.Equal(Tickets[x.GetProperty("id").GetInt32()].D, x.GetProperty("need").GetInt32());
        Assert.Equal("keep", me.GetProperty("tip").GetProperty("act").GetString());
        Assert.All(new[] { "draw", "deck", "tickets", "claim", "pass" }, k => Assert.False(me.GetProperty("can").GetProperty(k).GetBoolean()));
        Assert.Empty(me.GetProperty("why").EnumerateObject());

        Assert.True(h.Act(s, "keep", new { ids = c.Offer[s].Take(2).ToArray() }).Ok);
        var kept = c.Tickets[s].ToArray();
        var other = h.View(o);
        var seat = other.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == s);
        Assert.True(seat.GetProperty("ready").GetBoolean());
        Assert.Equal(2, seat.GetProperty("tickets").GetInt32());
        var theirs = other.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == o);
        Assert.False(theirs.GetProperty("ready").GetBoolean());
        Assert.Equal(c.Offer[o], other.GetProperty("me").GetProperty("offer").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));
        Assert.DoesNotContain(other.GetProperty("me").GetProperty("tickets").EnumerateArray(), x => kept.Contains(x.GetProperty("id").GetInt32()));
        var me2 = h.View(s).GetProperty("me");
        Assert.Equal(JsonValueKind.Null, me2.GetProperty("must").ValueKind);
        Assert.Empty(me2.GetProperty("offer").EnumerateArray());
        Assert.Equal(0, me2.GetProperty("keepMin").GetInt32());
        Assert.Equal(JsonValueKind.Null, me2.GetProperty("tip").ValueKind);
    }

    /// <summary>Для місця: перемішати приховане інших гравців, колоду й колоду замовлень, зберігши всі лічильники.</summary>
    static void Scramble(SolyanyiCore c, int? keep, Random rng)
    {
        var pool = new List<int>(c.Deck);
        foreach (var o in c.Order)
            if (o != keep)
                for (var k = 0; k < 9; k++) pool.AddRange(Enumerable.Repeat(k, c.Hand[o][k]));
        var mixed = pool.OrderBy(_ => rng.Next()).ToList();
        var at = 0;
        foreach (var o in c.Order)
        {
            if (o == keep) continue;
            var n = c.Hand[o].Sum();
            c.Hand[o] = new int[9];
            for (var i = 0; i < n; i++) c.Hand[o][mixed[at++]]++;
        }
        c.Deck = mixed.Skip(at).ToList();
        var tickets = new List<int>(c.TicketDeck);
        foreach (var o in c.Order)
            if (o != keep)
            {
                tickets.AddRange(c.Tickets[o].Where(t => !c.Done[t]));
                tickets.AddRange(c.Offer[o]);
            }
        var shuffled = tickets.OrderBy(_ => rng.Next()).ToList();
        at = 0;
        foreach (var o in c.Order)
        {
            if (o == keep) continue;
            c.Tickets[o] = [.. c.Tickets[o].Select(t => c.Done[t] ? t : shuffled[at++])];
            c.Offer[o] = [.. c.Offer[o].Select(_ => shuffled[at++])];
        }
        c.TicketDeck = shuffled.Skip(at).ToList();
    }

    [Fact]
    public void Foreign_hands_tickets_offers_and_deck_order_do_not_reach_the_view()
    {
        var h = SlRig.Table(3, seed: 9);
        var c = SlRig.CoreOf(h);
        var rng = new Random(3);
        void Check()
        {
            var seen = new Dictionary<int, string> { [-1] = h.View(null).GetRawText() };
            foreach (var s in c.Order)
            {
                var v = h.View(s).GetRawText();
                seen[s] = v;
            }
            // Глядач: перемішане нічого не міняє в його виді.
            var snap = c.Snapshot();
            Scramble(c, null, rng);
            Assert.Equal(seen[-1], h.View(null).GetRawText());
            Restore(c, snap);
            // Гравець: перемішане чужими — той самий вид, разом із порадою й цінами шляхів.
            foreach (var s in c.Order)
            {
                snap = c.Snapshot();
                Scramble(c, s, rng);
                Assert.Equal(seen[s], h.View(s).GetRawText());
                Restore(c, snap);
            }
        }
        Check();
        SlRig.Ready(h);
        for (var i = 0; i < 70 && h.Finished.Count == 0; i++)
        {
            Check();
            SlRig.Step(h, 0);
        }
    }

    static void Restore(SolyanyiCore c, SlCoreSnap s)
    {
        c.Hand = s.Hand;
        c.Deck = [.. s.Deck];
        c.TicketDeck = [.. s.TicketDeck];
        c.Tickets = [.. s.Tickets.Select(t => t.ToList())];
        c.Offer = [.. s.Offer.Select(t => t.ToList())];
    }

    [Fact]
    public void Events_hide_blind_cards_ticket_ids_and_done_from_everyone_else()
    {
        var h = SlRig.Play(3, seed: 6);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        var o = c.Next(s);
        SlRig.Market(c, Salt, Fish, Grain, Tar, Honey);
        foreach (var x in c.Order) c.Tickets[x].Clear();
        c.Tickets[s].Add(39);   // Коростень — Вінниця: 61 (2) + 65 (2) через Житомир
        c.Owner[61] = s;
        c.Taken[s].Add(61);
        SlRig.Give(c, s, (Wild, 2));
        Assert.True(h.Act(s, "claim", new { route = 65, c = "wild", w = 2 }).Ok);
        Assert.True(c.Done[39]);
        Assert.True(h.Act(o, "draw", new { deck = true }).Ok);
        Assert.True(h.Act(o, "draw", new { deck = true }).Ok);
        var t = c.Turn;
        Assert.True(h.Act(t, "tickets").Ok);
        var ids = c.Offer[t].ToArray();

        JsonElement[] Ev(int? seat, string k) => [.. h.View(seat).GetProperty("events").EnumerateArray().Where(e => e.GetProperty("k").GetString() == k)];
        // done — лише власнику.
        Assert.Equal(39, Assert.Single(Ev(s, "done")).GetProperty("ticket").GetInt32());
        Assert.Empty(Ev(o, "done"));
        Assert.Empty(Ev(null, "done"));
        // Спільний seq подія «лише власнику» не рухає: у чужому виді він суцільний, ніякого пропуску, що виказав би виконане.
        var ghostSeq = h.View(null).GetProperty("events").EnumerateArray().Select(e => e.GetProperty("seq").GetInt64()).ToArray();
        Assert.Equal(Enumerable.Range(0, ghostSeq.Length).Select(i => ghostSeq[0] + i), ghostSeq);
        Assert.Equal(h.View(s).GetProperty("seq").GetInt64(), h.View(o).GetProperty("seq").GetInt64());
        Assert.Equal(h.View(s).GetProperty("seq").GetInt64(), h.View(null).GetProperty("seq").GetInt64());
        // Свій лічильник — лише власнику: номер у події й у me.own; решта не бачить навіть свого нуля як чужого.
        Assert.Equal(1, Assert.Single(Ev(s, "done")).GetProperty("own").GetInt64());
        Assert.Equal(1, h.View(s).GetProperty("me").GetProperty("own").GetInt64());
        Assert.Equal(0, h.View(o).GetProperty("me").GetProperty("own").GetInt64());
        Assert.DoesNotContain("\"own\"", h.View(null).GetRawText());
        Assert.All(h.View(o).GetProperty("events").EnumerateArray(), e => Assert.False(e.TryGetProperty("own", out _)));
        // Наосліп — карту бачить лише той, хто взяв.
        var blindOwn = Ev(o, "draw");
        Assert.All(blindOwn, e => Assert.Equal(JsonValueKind.String, e.GetProperty("card").ValueKind));
        Assert.All(Ev(s, "draw"), e => Assert.Equal(JsonValueKind.Null, e.GetProperty("card").ValueKind));
        Assert.All(Ev(null, "draw"), e => Assert.Equal(JsonValueKind.Null, e.GetProperty("card").ValueKind));
        // Замовлення — ids лише тому, хто взяв.
        Assert.Equal(ids, Assert.Single(Ev(t, "tickets")).GetProperty("ids").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(JsonValueKind.Null, Assert.Single(Ev(s, "tickets")).GetProperty("ids").ValueKind);
        Assert.Equal(JsonValueKind.Null, Assert.Single(Ev(null, "tickets")).GetProperty("ids").ValueKind);
        Assert.Equal(ids.Length, Assert.Single(Ev(null, "tickets")).GetProperty("n").GetInt32());
        // Очки замовлень не в players[].points; журнал мовчить про виконане.
        var pl = h.View(o).GetProperty("players").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == s);
        Assert.Equal(c.RoutePts[s], pl.GetProperty("points").GetInt32());
        Assert.DoesNotContain(h.View(o).GetProperty("log").EnumerateArray(), l => l.GetString()!.Contains("виконав"));
        // Серіалізований вид глядача: ні пропозицій, ні замовлень (їхні id їздять лише під ключем «id» у me).
        var ghost = h.View(null).GetRawText();
        Assert.DoesNotContain("\"offer\"", ghost);
        Assert.DoesNotContain("\"id\":", ghost);
    }

    /// <summary>
    /// Двічі виконане: спільний seq рухається лише зі спільними подіями (суперник порівнює сусідні номери — і нічого не вичитує),
    /// а власник отримує подію з новим own навіть у виді, де seq той самий. Клієнт, що «відпав» на цей час, бере її за own (§7).
    /// </summary>
    [Fact]
    public void Owner_only_events_keep_their_own_numbering_through_reconnects_and_saves()
    {
        var h = SlRig.Play(2, seed: 6);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        var o = c.Next(s);
        foreach (var x in c.Order) c.Tickets[x].Clear();
        c.Tickets[s].Add(39);   // Коростень — Вінниця: 61 + 65 через Житомир
        c.Owner[61] = s;
        c.Taken[s].Add(61);
        SlRig.Give(c, s, (Wild, 2));

        var seenSeq = h.View(s).GetProperty("seq").GetInt64();
        var seenOwn = h.View(s).GetProperty("me").GetProperty("own").GetInt64();
        var seqBefore = c.Seq;
        Assert.True(h.Act(s, "claim", new { route = 65, c = "wild", w = 2 }).Ok);

        // Подія сидить після claim і має той самий seq — суперник бачить ті самі номери, без дірок.
        var ev = c.Events.Where(e => e.Seq > seqBefore).ToArray();
        var done = ev.Single(e => e.K == "done");
        Assert.Equal(ev.Single(e => e.K == "claim").Seq, done.Seq);
        var pub = ev.Where(e => e.Only < 0).Select(e => e.Seq).ToArray();
        Assert.Equal(pub.Length, c.Seq - seqBefore);
        Assert.Equal(Enumerable.Range(1, pub.Length).Select(i => seqBefore + i), pub);

        // «Відпав і вернувся»: на дроті є все, що новіше за обидва лічильники, рівно по разу.
        var back = h.View(s);
        var fresh = back.GetProperty("events").EnumerateArray().Where(e =>
            e.TryGetProperty("own", out var n) ? n.GetInt64() > seenOwn : e.GetProperty("seq").GetInt64() > seenSeq).ToArray();
        Assert.Single(fresh, e => e.GetProperty("k").GetString() == "done");
        Assert.Single(fresh, e => e.GetProperty("k").GetString() == "claim");
        var ownNow = back.GetProperty("me").GetProperty("own").GetInt64();
        Assert.Equal(seenOwn + 1, ownNow);
        Assert.DoesNotContain(back.GetProperty("events").EnumerateArray(), e => e.TryGetProperty("own", out var n) && n.GetInt64() > ownNow);

        // Лічильник переживає знімок: після перезапуску сервера ті самі події не стануть новими.
        var restored = SolyanyiCore.FromSnapshot(JsonSerializer.Deserialize<SlCoreSnap>(JsonSerializer.Serialize(c.Snapshot()))!);
        Assert.Equal(c.OwnSeq, restored.OwnSeq);
        Assert.Equal(done.Own, restored.Events.Single(e => e.K == "done").Own);
        Assert.Equal(1, restored.Events.Single(e => e.K == "done").For(s)!["own"]! switch { long l => l, int i => i, var x => Convert.ToInt64(x) });
    }

    /// <summary>Вікно на 40 подій рахується по видимих: його довжина не виказує, скільки там чужих прихованих.</summary>
    [Fact]
    public void Hidden_events_do_not_shrink_the_public_window()
    {
        var h = SlRig.Play(2, seed: 6);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        for (var i = 0; i < 30; i++) Assert.True(SlRig.PlayTip(h, c.Turn, h.View(c.Turn).GetProperty("me").GetProperty("tip")).Ok);
        for (var i = 0; i < 39; i++) c.Events.Add(new SlEvent { Seq = c.Seq, K = "done", Only = s, Own = i + 1, Data = [new("seat", s), new("ticket", 1)] });
        Assert.Equal(40, h.View(null).GetProperty("events").GetArrayLength());
        Assert.Equal(40, h.View(c.Next(s)).GetProperty("events").GetArrayLength());
    }

    [Fact]
    public void Open_information_is_open()
    {
        var h = SlRig.Play(2, seed: 6);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        SlRig.Give(c, s, (Wild, 3));
        Assert.True(h.Act(s, "claim", new { route = 0, c = "wild", w = 3 }).Ok);
        var v = h.View(null);
        Assert.Equal(c.Market.Select(m => m >= 0 ? Cards[m] : null), v.GetProperty("market").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(c.Deck.Count, v.GetProperty("deck").GetInt32());
        Assert.Equal(c.Discard.Count, v.GetProperty("discard").GetInt32());
        Assert.Equal(c.TicketDeck.Count, v.GetProperty("ticketsLeft").GetInt32());
        var claimed = Assert.Single(v.GetProperty("claimed").EnumerateArray());
        Assert.Equal((0, s), (claimed.GetProperty("route").GetInt32(), claimed.GetProperty("seat").GetInt32()));
        var pl = v.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == s);
        Assert.Equal(new[] { 0 }, pl.GetProperty("routes").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(3, pl.GetProperty("road").GetInt32());
        Assert.Equal(4, pl.GetProperty("points").GetInt32());
        Assert.Equal(c.Hand[s].Sum(), pl.GetProperty("hand").GetInt32());
        Assert.Equal(3, v.GetProperty("longest").GetProperty("len").GetInt32());
        Assert.Equal(new[] { s }, v.GetProperty("longest").GetProperty("seats").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Contains($"{h.NickOf(s)} займає Київ — Чернігів (3 вози, +4 ⭐)", v.GetProperty("log").EnumerateArray().Select(l => l.GetString()));
    }

    [Fact]
    public void Claims_list_every_free_route_for_every_player_at_any_time()
    {
        var h = SlRig.Play(3, seed: 6);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        var o = c.Next(s);
        SlRig.Give(c, s, (Wild, 2));
        Assert.True(h.Act(s, "claim", new { route = 39, c = "wild", w = 2 }).Ok);
        foreach (var seat in c.Order)
        {
            var me = h.View(seat).GetProperty("me");
            var claims = me.GetProperty("claims");
            Assert.Equal(101, claims.EnumerateObject().Count());
            Assert.False(claims.TryGetProperty("39", out _));
            var closed = claims.GetProperty("91");
            Assert.False(closed.GetProperty("ok").GetBoolean());
            Assert.Equal(0, closed.GetProperty("short").GetInt32());
            Assert.Equal("За столом на 2–3 парний шлях один, і його вже зайнято", closed.GetProperty("why").GetString());
            foreach (var p in claims.EnumerateObject())
            {
                var q = c.Quote(seat, int.Parse(p.Name));
                Assert.Equal(q.Ok, p.Value.GetProperty("ok").GetBoolean());
                if (!q.Ok) continue;
                Assert.Equal(q.Combos.Count, p.Value.GetProperty("combos").GetArrayLength());
                Assert.InRange(p.Value.GetProperty("best").GetInt32(), 0, q.Combos.Count - 1);
            }
        }
        Assert.Equal([91], h.View(null).GetProperty("closed").EnumerateArray().Select(x => x.GetInt32()));
        // Поза ходом: усе «ні», а ціни є.
        var other = h.View(s).GetProperty("me");
        Assert.All(new[] { "draw", "deck", "tickets", "claim", "pass" }, k => Assert.False(other.GetProperty("can").GetProperty(k).GetBoolean()));
        Assert.Empty(other.GetProperty("can").GetProperty("market").EnumerateArray());
        Assert.Empty(other.GetProperty("why").EnumerateObject());
        Assert.Equal(JsonValueKind.Null, other.GetProperty("must").ValueKind);
        // На ході.
        var mine = h.View(o).GetProperty("me");
        Assert.Equal("act", mine.GetProperty("must").GetString());
        Assert.True(mine.GetProperty("can").GetProperty("draw").GetBoolean());
        Assert.Equal("Пропускати не можна — є що робити", mine.GetProperty("why").GetProperty("pass").GetString());
    }

    [Fact]
    public void Draw_phase_view_offers_only_what_can_be_taken_second()
    {
        var h = SlRig.Play(2, seed: 6);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        SlRig.Market(c, Wild, Fish, Grain, Wild, Honey);
        SlRig.Top(c, Salt, Tar, Wool);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, h.View(s).GetProperty("me").GetProperty("can").GetProperty("market").EnumerateArray().Select(x => x.GetInt32()));
        Assert.True(h.Act(s, "draw", new { slot = 1 }).Ok);
        var v = h.View(s);
        Assert.Equal("draw", v.GetProperty("phase").GetString());
        var me = v.GetProperty("me");
        Assert.Equal("draw", me.GetProperty("must").GetString());
        var can = me.GetProperty("can");
        Assert.True(can.GetProperty("draw").GetBoolean());
        Assert.True(can.GetProperty("deck").GetBoolean());
        Assert.False(can.GetProperty("claim").GetBoolean());
        Assert.False(can.GetProperty("tickets").GetBoolean());
        Assert.DoesNotContain(can.GetProperty("market").EnumerateArray().Select(x => x.GetInt32()), k => c.Market[k] == Wild);
        Assert.Equal("draw", me.GetProperty("tip").GetProperty("act").GetString());
    }

    [Fact]
    public void Tickets_need_path_and_dead_are_consistent()
    {
        var h = SlRig.Play(3, seed: 6);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        var o = c.Next(s);
        foreach (var x in c.Order) c.Tickets[x].Clear();
        c.Tickets[s].AddRange([18, 39, 11]);
        // Шляхи: свій уже рахується як 0, чужий — зникає, закритий пара-правилом — теж.
        c.Owner[61] = s;
        c.Taken[s].Add(61);
        c.Owner[50] = o;
        c.Taken[o].Add(50);
        foreach (var r in Adj[26]) if (c.Owner[r.Id] < 0) c.Owner[r.Id] = o;   // Ізмаїл відрізано: 11 мертве
        var cost = c.Costs(s);
        var me = h.View(s).GetProperty("me");
        var list = me.GetProperty("tickets").EnumerateArray().ToArray();
        Assert.Equal(new[] { 18, 39, 11 }, list.Select(t => t.GetProperty("id").GetInt32()));
        var odesa = list[0];
        var need = odesa.GetProperty("need").GetInt32();
        var path = odesa.GetProperty("path").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        Assert.False(odesa.GetProperty("done").GetBoolean());
        Assert.False(odesa.GetProperty("dead").GetBoolean());
        Assert.Equal(path.Order(), path);
        Assert.Equal(need, path.Sum(r => Routes[r].Len));
        Assert.All(path, r =>
        {
            Assert.True(c.Owner[r] < 0);
            Assert.True(cost[r] > 0);
            Assert.DoesNotContain(r, c.Closed());
        });
        Assert.Equal(need, Dijkstra(25, 0, cost));
        // Коростень — Вінниця: 61 своя, лишається 65.
        Assert.Equal(2, list[1].GetProperty("need").GetInt32());
        Assert.Equal(new[] { 65 }, list[1].GetProperty("path").EnumerateArray().Select(x => x.GetInt32()));
        var dead = list[2];
        Assert.True(dead.GetProperty("dead").GetBoolean());
        Assert.Equal(JsonValueKind.Null, dead.GetProperty("need").ValueKind);
        Assert.Empty(dead.GetProperty("path").EnumerateArray());
        // Рахунок: routes + done − open.
        var score = me.GetProperty("score");
        var open = Tickets[18].Pts + Tickets[39].Pts + Tickets[11].Pts;
        Assert.Equal(open, score.GetProperty("open").GetInt32());
        Assert.Equal(c.RoutePts[s] - open, score.GetProperty("total").GetInt32());
        // Виконане: done, need 0, порожній path.
        SlRig.Give(c, s, (Wild, 2));
        Assert.True(h.Act(s, "claim", new { route = 65, c = "wild", w = 2 }).Ok);
        var done = h.View(s).GetProperty("me").GetProperty("tickets")[1];
        Assert.True(done.GetProperty("done").GetBoolean());
        Assert.Equal(0, done.GetProperty("need").GetInt32());
        Assert.Empty(done.GetProperty("path").EnumerateArray());
        Assert.Equal(Tickets[39].Pts, h.View(s).GetProperty("me").GetProperty("score").GetProperty("done").GetInt32());
    }

    [Fact]
    public void Keep_phase_view_has_the_offer_and_the_minimum()
    {
        var h = SlRig.Play(2, seed: 6);
        var c = SlRig.CoreOf(h);
        var s = c.Turn;
        Assert.True(h.Act(s, "tickets").Ok);
        var v = h.View(s);
        Assert.Equal("keep", v.GetProperty("phase").GetString());
        var me = v.GetProperty("me");
        Assert.Equal("keep", me.GetProperty("must").GetString());
        Assert.Equal(1, me.GetProperty("keepMin").GetInt32());
        Assert.Equal(3, me.GetProperty("offer").GetArrayLength());
        var tip = me.GetProperty("tip");
        Assert.Equal("keep", tip.GetProperty("act").GetString());
        Assert.True(SlRig.PlayTip(h, s, tip).Ok);
    }

    [Fact]
    public void Tip_is_the_same_until_the_table_changes_and_follows_the_contract_formats()
    {
        var h = SlRig.Play(2, seed: 6);
        var c = SlRig.CoreOf(h);
        var seen = new HashSet<string>();
        for (var i = 0; i < 400 && h.Finished.Count == 0; i++)
        {
            var s = c.Turn;
            var tip = h.View(s).GetProperty("me").GetProperty("tip");
            Assert.Equal(tip.GetRawText(), h.View(s).GetProperty("me").GetProperty("tip").GetRawText());
            var act = tip.GetProperty("act").GetString()!;
            seen.Add(act);
            Assert.Equal(act switch
            {
                "claim" => ["act", "route", "c", "w"],
                "draw" when tip.TryGetProperty("slot", out _) => ["act", "slot"],
                "draw" => ["act", "deck"],
                "keep" => ["act", "ids"],
                _ => new[] { "act" },
            }, Keys(tip));
            Assert.True(SlRig.PlayTip(h, s, tip).Ok);
        }
        Assert.Single(h.Finished);
        Assert.Contains("claim", seen);
        Assert.Contains("draw", seen);
    }

    [Fact]
    public void Over_opens_everything()
    {
        var h = SlRig.Play(1, bots: "2", seed: 7);
        var c = SlRig.CoreOf(h);
        for (var i = 0; i < 1500 && h.Finished.Count == 0; i++) SlRig.Step(h, 0);
        Assert.Single(h.Finished);
        var v = h.View(null);
        Assert.Equal("over", v.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("deadline").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("botIn").ValueKind);
        var over = v.GetProperty("over");
        Assert.Equal(["winners", "reason", "lastBy", "points", "final", "longest", "stats"], Keys(over));
        Assert.Equal(c.Order.Select(c.Total), over.GetProperty("points").EnumerateArray().Select(x => x.GetInt32()));
        var final = over.GetProperty("final");
        Assert.Equal(3, final.GetArrayLength());
        foreach (var f in final.EnumerateArray())
        {
            var s = f.GetProperty("seat").GetInt32();
            Assert.Equal(["seat", "routes", "done", "open", "bonus", "total", "road", "tickets"], Keys(f));
            Assert.Equal(f.GetProperty("routes").GetInt32() + f.GetProperty("done").GetInt32() - f.GetProperty("open").GetInt32() + f.GetProperty("bonus").GetInt32(), f.GetProperty("total").GetInt32());
            Assert.Equal(c.Tickets[s], f.GetProperty("tickets").EnumerateArray().Select(t => t.GetProperty("id").GetInt32()));
            Assert.All(f.GetProperty("tickets").EnumerateArray(), t => Assert.Equal(c.Done[t.GetProperty("id").GetInt32()], t.GetProperty("done").GetBoolean()));
        }
        var longest = over.GetProperty("longest");
        var len = longest.GetProperty("len").GetInt32();
        foreach (var p in longest.GetProperty("routes").EnumerateObject())
            Assert.Equal(len, p.Value.EnumerateArray().Sum(r => Routes[r.GetInt32()].Len));
        var stats = over.GetProperty("stats");
        Assert.Equal(c.Round, stats.GetProperty("rounds").GetInt32());
        Assert.Equal(c.Turns, stats.GetProperty("turns").GetInt32());
        Assert.True(stats.GetProperty("ms").GetInt64() > 0);
        var timeline = stats.GetProperty("timeline");
        Assert.Equal(new int[5], timeline[0].EnumerateArray().Select(x => x.GetInt32()));
        Assert.True(timeline.GetArrayLength() >= c.Round);
        Assert.Equal(c.RoutePts, timeline[timeline.GetArrayLength() - 1].EnumerateArray().Select(x => x.GetInt32()));
        var seat = stats.GetProperty("seats")[0];
        Assert.Equal(["seat", "claims", "wagonsUsed", "biggest", "draws", "drawn", "facedown", "faceWild", "wildsPaid", "ticketDraws", "ticketsKept", "blocks", "crimea", "firstClaim"], Keys(seat));
        var keys = v.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("k").GetString()).ToArray();
        Assert.Equal("over", keys[^1]);
        Assert.Equal("longest", keys[^2]);
    }

    [Fact]
    public void Events_are_at_most_forty_with_growing_seq_and_log_at_most_twelve()
    {
        var h = SlRig.Play(1, bots: "4", seed: 7);
        for (var i = 0; i < 90 && h.Finished.Count == 0; i++)
        {
            SlRig.Step(h, 0);
            var v = h.View(0);
            var ev = v.GetProperty("events").EnumerateArray().ToArray();
            var seq = ev.Select(e => e.GetProperty("seq").GetInt64()).ToArray();
            Assert.True(seq.Length <= 40);
            Assert.Equal(seq.Order(), seq);
            // Спільні номери не повторюються; подія «лише власнику» стоїть слідом за спільною зі своїм seq і має власний номер (§7).
            var pub = ev.Where(e => !e.TryGetProperty("own", out _)).Select(e => e.GetProperty("seq").GetInt64()).ToArray();
            Assert.Equal(pub.Distinct().Count(), pub.Length);
            Assert.True(v.GetProperty("seq").GetInt64() >= seq[^1]);
            Assert.True(v.GetProperty("log").GetArrayLength() <= 12);
        }
    }

    [Fact]
    public void Turn_order_of_events_matches_the_contract()
    {
        var c = SolyanyiBotTests.Playing(2, 5);
        var s = c.Turn;
        SlRig.Market(c, Salt, Fish, Grain, Tar, Honey);
        SlRig.Top(c, Wool, Hides, Linen);
        var seq = c.Seq;
        Assert.Null(c.Draw(s, 0, false));
        Assert.Null(c.Draw(s, 1, false));
        Assert.Equal(["draw", "refill", "draw", "refill", "turn"], c.Events.Where(e => e.Seq > seq).Select(e => e.K));
        // claim, що запускає останнє коло й починає нове коло.
        var o = c.Turn;
        c.Wagons[o] = 3;
        SlRig.Give(c, o, (Wild, 1));
        seq = c.Seq;
        c.Tickets[o].Clear();
        c.Tickets[o].Add(39);
        c.Owner[61] = o;
        c.Taken[o].Add(61);
        c.Owner[65] = -1;
        Assert.Null(c.Claim(o, 2, "wild", 1));
        var k = c.Events.Where(e => e.Seq > seq).Select(e => e.K).ToArray();
        Assert.Equal("claim", k[0]);
        Assert.Equal("last", k[^3]);
        Assert.Equal("round", k[^2]);
        Assert.Equal("turn", k[^1]);
    }
}
