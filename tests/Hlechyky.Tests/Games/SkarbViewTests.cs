using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using static Hlechyky.Games.Impl.SkarbCards;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Вид «Скарбу Полуботка» (контракт §5): форма на дроті, приховане (чужі ролі, руки, мапи, набік, скриня — ніколи у фазі
/// play), підсумок раунду, і що <c>me.legal</c> — рівно те, що прийме <c>Act</c>.
/// </summary>
public class SkarbViewTests
{
    /// <summary>Усі рядкові значення JSON (без ключів), крім піддерева <paramref name="skip"/>.</summary>
    static IEnumerable<string> Strings(JsonElement e, Func<JsonElement, bool>? skip = null)
    {
        if (skip?.Invoke(e) == true) yield break;
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                yield return e.GetString()!;
                break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray()) foreach (var s in Strings(x, skip)) yield return s;
                break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == "me") continue;
                    foreach (var s in Strings(p.Value, skip)) yield return s;
                }
                break;
        }
    }

    [Fact]
    public void Lobby_view_has_no_phase_and_play_view_has_the_contract_shape()
    {
        var lobby = new RoomHarness("skarb", new { timer = "60", bots = "3", allies = "0" });
        lobby.Join("Оля");
        var v0 = lobby.View(0);
        Assert.Equal(JsonValueKind.Null, v0.GetProperty("phase").ValueKind);
        Assert.Equal(JsonValueKind.Null, v0.GetProperty("me").ValueKind);

        var h = SkarbRoom.Table(2, bots: "3");
        var v = h.View(0);
        foreach (var key in new[]
        {
            "v", "grid", "phase", "round", "rounds", "pack", "players", "order", "first", "turn", "board", "linked", "goals", "deck",
            "goldLeft", "roundEnd", "nextAt", "ready", "events", "seq", "deadline", "timerMs", "bots", "botIn", "log", "over", "me",
        })
            Assert.True(v.TryGetProperty(key, out _), $"нема {key}");
        Assert.Equal("play", v.GetProperty("phase").GetString());
        Assert.Equal(1, v.GetProperty("round").GetInt32());
        Assert.Equal(3, v.GetProperty("rounds").GetInt32());
        Assert.Equal(2, v.GetProperty("pack").GetProperty("moles").GetInt32());
        Assert.Equal(4, v.GetProperty("pack").GetProperty("diggers").GetInt32());
        var grid = v.GetProperty("grid");
        Assert.Equal(-4, grid.GetProperty("x0").GetInt32());
        Assert.Equal(12, grid.GetProperty("x1").GetInt32());
        Assert.Equal("[[8,-2],[8,0],[8,2]]", grid.GetProperty("goals").GetRawText());
        Assert.Equal("[[0,-1],[0,-2],[0,-3],[0,-4]]", grid.GetProperty("shaft").GetRawText());
        var start = v.GetProperty("board")[0];
        Assert.Equal("start", start.GetProperty("s").GetString());
        Assert.Equal(15, start.GetProperty("m").GetInt32());
        Assert.Equal(JsonValueKind.Null, start.GetProperty("id").ValueKind);
        Assert.Equal("[[0,0]]", v.GetProperty("linked").GetRawText());
        Assert.Equal(3, v.GetProperty("goals").GetArrayLength());
        Assert.All(v.GetProperty("goals").EnumerateArray(), g =>
        {
            Assert.False(g.GetProperty("open").GetBoolean());
            Assert.Equal(JsonValueKind.Null, g.GetProperty("kind").ValueKind);
        });
        Assert.Equal(67 - 5 * 6, v.GetProperty("deck").GetInt32());
        Assert.Equal(28, v.GetProperty("goldLeft").GetInt32());
        var p0 = v.GetProperty("players")[0];
        foreach (var key in new[] { "seat", "nick", "bot", "color", "hand", "broken", "gold", "role", "away" })
            Assert.True(p0.TryGetProperty(key, out _), $"нема players.{key}");
        Assert.Equal(6, p0.GetProperty("hand").GetInt32());
        Assert.Equal(JsonValueKind.Null, p0.GetProperty("role").ValueKind);
        Assert.Equal("Глек 🤖", v.GetProperty("bots")[2].GetString());
        var me = v.GetProperty("me");
        Assert.Equal(0, me.GetProperty("seat").GetInt32());
        Assert.Contains(me.GetProperty("role").GetString(), new[] { "digger", "mole" });
        Assert.Equal(JsonValueKind.Null, me.GetProperty("allies").ValueKind);
        Assert.Equal(6, me.GetProperty("hand").GetArrayLength());
        Assert.Equal(JsonValueKind.Object, me.GetProperty("peeks").ValueKind);
        // Спершу «round», потім «turn».
        var events = v.GetProperty("events");
        Assert.Equal("round", events[0].GetProperty("k").GetString());
        Assert.Equal(6, events[0].GetProperty("hand").GetInt32());
        Assert.Equal("turn", events[1].GetProperty("k").GetString());
        // Глядач і місце Глека — без «me».
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("me").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(2).GetProperty("me").ValueKind);
    }

    [Fact]
    public void Nobody_sees_others_roles_hands_peeks_the_aside_or_the_chest_during_play()
    {
        var maps = 0;
        foreach (var seed in new[] { 3, 4, 5 })
        {
            var h = SkarbRoom.Table(3, bots: "6", seed: seed);
            var c = SkarbRoom.CoreOf(h);
            for (var i = 0; i < 3000 && h.Finished.Count == 0; i++)
            {
                if (c.Phase == SkPhase.Play)
                    foreach (int? viewer in new int?[] { null, 0, 1, 2 })
                    {
                        var v = h.View(viewer);
                        // Своє — у «me» і в подіях своєї мапи; решта рядків не знає ні кротів, ні скрині.
                        // Події минулих раундів (там скриню вже відкопали всім на очах) — не рахуються.
                        var roundSeq = c.RoundSeq;
                        var strings = Strings(v, e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("k", out var k)
                            && (e.GetProperty("seq").GetInt64() < roundSeq
                                || (k.GetString() == "map" && viewer is { } vs && e.GetProperty("seat").GetInt32() == vs))).ToList();
                        Assert.DoesNotContain("mole", strings);
                        Assert.DoesNotContain("digger", strings);
                        Assert.DoesNotContain("gold", strings);
                        Assert.All(v.GetProperty("players").EnumerateArray(), p =>
                        {
                            Assert.Equal(JsonValueKind.Number, p.GetProperty("hand").ValueKind);
                            Assert.Equal(JsonValueKind.Null, p.GetProperty("role").ValueKind);
                        });
                        Assert.Equal(JsonValueKind.Null, v.GetProperty("roundEnd").ValueKind);
                        foreach (var e in v.GetProperty("events").EnumerateArray())
                            if (e.GetProperty("k").GetString() == "map")
                            {
                                maps++;
                                var own = viewer is { } vs && e.GetProperty("seat").GetInt32() == vs;
                                Assert.Equal(own, e.GetProperty("kind").ValueKind == JsonValueKind.String);
                            }
                        if (viewer is { } me)
                        {
                            var mine = v.GetProperty("me");
                            Assert.Equal(c.Mole[me] ? "mole" : "digger", mine.GetProperty("role").GetString());
                            Assert.Equal(c.Hand[me].Count, mine.GetProperty("hand").GetArrayLength());
                            foreach (var peek in mine.GetProperty("peeks").EnumerateObject())
                                Assert.Equal(c.Peek[me][int.Parse(peek.Name)] == 2 ? "gold" : "stone", peek.Value.GetString());
                        }
                        // Чужу руку не видно навіть по id: id карт є лише на полі.
                        var ids = v.GetProperty("board").EnumerateArray().Select(b => b.GetProperty("id"))
                            .Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToHashSet();
                        foreach (var o in c.Order.Where(o => o != viewer))
                            Assert.DoesNotContain(c.Hand[o], ids.Contains);
                    }
                SkarbRoom.Step(h, 0);
            }
            Assert.Single(h.Finished);
        }
        Assert.True(maps > 0, "за три партії ніхто не глянув мапу");
    }

    [Fact]
    public void Round_end_reveals_roles_aside_and_all_goals()
    {
        var h = SkarbRoom.Table(2, bots: "3", seed: 8);
        var c = SkarbRoom.CoreOf(h);
        for (var i = 0; i < 1000 && c.Phase == SkPhase.Play; i++) SkarbRoom.Step(h, 0);
        Assert.Equal(SkPhase.Round, c.Phase);
        var v = h.View(null);
        Assert.Equal("round", v.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("deadline").ValueKind);
        Assert.Equal(JsonValueKind.String, v.GetProperty("nextAt").ValueKind);
        var end = v.GetProperty("roundEnd");
        Assert.Equal(1, end.GetProperty("n").GetInt32());
        Assert.Contains(end.GetProperty("side").GetString(), new[] { "diggers", "moles", "none" });
        var roles = end.GetProperty("roles");
        foreach (var s in c.Order) Assert.Equal(c.Mole[s] ? "mole" : "digger", roles.GetProperty(s.ToString()).GetString());
        Assert.Equal(c.AsideMole ? "mole" : "digger", end.GetProperty("aside").GetString());
        Assert.Equal(c.GoldGoal, end.GetProperty("goals").EnumerateArray().Single(g => g.GetProperty("kind").GetString() == "gold")
            .GetProperty("i").GetInt32());
        Assert.Equal(c.Order.Select(o => c.Gold[o]), end.GetProperty("totals").EnumerateArray().Select(x => x.GetInt32()));
        Assert.All(v.GetProperty("players").EnumerateArray(), p =>
            Assert.Equal(c.Mole[p.GetProperty("seat").GetInt32()] ? "mole" : "digger", p.GetProperty("role").GetString()));
        Assert.All(v.GetProperty("goals").EnumerateArray(), g =>
        {
            Assert.Equal(JsonValueKind.String, g.GetProperty("kind").ValueKind);
            Assert.Equal(JsonValueKind.String, g.GetProperty("s").ValueKind);
        });
        var me = h.View(0).GetProperty("me");
        Assert.Equal("next", me.GetProperty("must").GetString());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("legal").ValueKind);
        Assert.Equal("end", v.GetProperty("events").EnumerateArray().Last().GetProperty("k").GetString());
    }

    /// <summary>Що прийме ядро для карти: перебір усіх клітинок × r, місць, інструментів, міток — на копіях.</summary>
    static HashSet<string> Accepted(SkarbCore c, int s, int id)
    {
        var ok = new HashSet<string>();
        var card = SkarbCore.CardOf(id);
        bool Try(SkMove mv) => c.Clone().Apply(s, mv) is null;
        switch (card.K)
        {
            case SkKind.Path:
                for (var x = X0 - 1; x <= X1 + 1; x++)
                    for (var y = Y0 - 1; y <= Y1 + 1; y++)
                        for (var r = 0; r < 2; r++)
                            if (Try(new SkMove(id, x, y, r))) ok.Add($"{x},{y},{(Symmetric(card.Shape!) ? 0 : r)}");
                break;
            case SkKind.Break:
                for (var t = -1; t <= 10; t++) if (Try(new SkMove(id, Seat: t))) ok.Add($"{t}");
                break;
            case SkKind.Fix:
                for (var t = -1; t <= 10; t++)
                    for (var tool = 0; tool < 3; tool++)
                        if (Try(new SkMove(id, Seat: t, Tool: tool))) ok.Add($"{t},{tool}");
                break;
            case SkKind.Map:
                for (var g = -1; g <= 3; g++) if (Try(new SkMove(id, Goal: g))) ok.Add($"{g}");
                break;
            default:
                for (var x = X0 - 1; x <= X1 + 1; x++)
                    for (var y = Y0 - 1; y <= Y1 + 1; y++)
                        if (Try(new SkMove(id, x, y))) ok.Add($"{x},{y}");
                break;
        }
        return ok;
    }

    static string Key(SkMove m) => SkarbCore.CardOf(m.Card).K switch
    {
        SkKind.Path => $"{m.X},{m.Y},{m.R}",
        SkKind.Break => $"{m.Seat}",
        SkKind.Fix => $"{m.Seat},{m.Tool}",
        SkKind.Map => $"{m.Goal}",
        _ => $"{m.X},{m.Y}",
    };

    [Fact]
    public void Legal_is_exactly_what_the_core_accepts()
    {
        var checks = 0;
        foreach (var (seed, n) in new[] { (11, 3), (12, 6), (13, 10) })
        {
            var rng = new Random(seed);
            var c = new SkarbCore(Enumerable.Range(0, n), rng);
            c.Begin();
            var bot = new SkarbBot();
            for (var step = 0; !c.Over && step < 400; step++)
            {
                if (c.Phase == SkPhase.Round) { c.NextRound(); continue; }
                var s = c.Turn;
                if (step % 3 == 0)
                    foreach (var id in c.Hand[s].Distinct())
                    {
                        var list = new List<SkMove>();
                        c.LegalFor(s, id, list);
                        var keys = list.Select(Key).ToList();
                        Assert.Equal(keys.Count, keys.Distinct().Count());
                        Assert.Equal(Accepted(c, s, id).Order(), keys.Order());
                        checks++;
                    }
                bot.Observe(c);
                Assert.Null(c.Apply(s, bot.Decide(c, s, rng)));
            }
        }
        Assert.True(checks > 100);
    }

    [Fact]
    public void Me_legal_why_and_tip_match_the_core()
    {
        var h = SkarbRoom.Table(3);
        var c = SkarbRoom.CoreOf(h);
        var s = c.Turn;
        lock (h.Room.Sync)
        {
            c.Hand[s].Clear();
            c.Hand[s].AddRange([0, 10, 19, 40, 55, 58]);
            c.Put(1, 0, "x");
            c.Broken[c.NextSeat(s)] = 2;
        }
        var me = h.View(s).GetProperty("me");
        Assert.Equal("play", me.GetProperty("must").GetString());
        var legal = me.GetProperty("legal");
        Assert.Equal(["0", "10", "19", "40", "55", "58"], legal.EnumerateObject().Select(p => p.Name));
        // За y, потім x, потім r; симетричне «h» — лише r = 0.
        var cross = legal.GetProperty("0").EnumerateArray().Select(a => (a[1].GetInt32(), a[0].GetInt32(), a[2].GetInt32())).ToList();
        Assert.Equal(cross.Order(), cross);
        Assert.All(legal.GetProperty("19").EnumerateArray(), a => Assert.Equal(0, a[2].GetInt32()));
        Assert.Contains(legal.GetProperty("10").EnumerateArray(), a => a[2].GetInt32() == 1);
        Assert.Equal(c.Order.Where(o => o != s).ToArray(), legal.GetProperty("40").EnumerateArray().Select(x => x.GetInt32()));
        var fix = Assert.Single(legal.GetProperty("55").EnumerateArray());
        Assert.Equal(c.NextSeat(s), fix[0].GetInt32());
        Assert.Equal("lamp", fix[1].GetString());
        Assert.Equal("[0,1,2]", legal.GetProperty("58").GetRawText());
        Assert.Empty(me.GetProperty("why").EnumerateObject());
        Assert.Equal("{\"id\":55,\"k\":\"fix\",\"tools\":[\"pick\",\"lamp\"]}", me.GetProperty("hand")[4].GetRawText());
        Assert.Equal("{\"id\":40,\"k\":\"break\",\"tool\":\"pick\"}", me.GetProperty("hand")[3].GetRawText());
        Assert.True(me.GetProperty("tip").TryGetProperty("card", out _));

        // Зламано — ходи порожні з «чому»; Глек радить полагодити себе.
        lock (h.Room.Sync)
        {
            c.Broken[s] = 1;
            c.Mole[s] = false;
            c.Hand[s].Clear();
            c.Hand[s].AddRange([0, 49, 64, 58]);
        }
        me = h.View(s).GetProperty("me");
        Assert.Equal("[]", me.GetProperty("legal").GetProperty("0").GetRawText());
        Assert.Equal("Кирку зламано — спершу полагодь", me.GetProperty("why").GetProperty("0").GetString());
        Assert.Equal($"[[{s},\"pick\"]]", me.GetProperty("legal").GetProperty("49").GetRawText());
        var tip = me.GetProperty("tip");
        Assert.Equal(49, tip.GetProperty("card").GetInt32());
        Assert.Equal(s, tip.GetProperty("seat").GetInt32());
        Assert.Equal("pick", tip.GetProperty("tool").GetString());
        lock (h.Room.Sync)
        {
            c.Hand[s].Clear();
            c.Hand[s].AddRange([40, 51]);
            foreach (var o in c.Order) c.Broken[o] = o == s ? 0 : 1;
        }
        var why = h.View(s).GetProperty("me").GetProperty("why");
        Assert.Equal("Усім, кому можна, вже зламано", why.GetProperty("40").GetString());
        Assert.Equal("Нема що лагодити", why.GetProperty("51").GetString());
        // «Нема що завалювати», «усі мітки відкриті», «нема де лягти».
        var d = new SkarbCore([0, 1, 2], new Random(1));
        d.Begin();
        d.Turn = 0;
        for (var g = 0; g < 3; g++) d.GoalOpen[g] = true;
        Assert.Equal("Усі мітки вже відкриті", d.WhyNot(0, 58));
        Assert.Equal("Нема що завалювати", d.WhyNot(0, 64));
        Assert.Equal("Цій карті нема де лягти", d.WhyNot(0, 0));
    }

    /// <summary>Поле з прикладу §5 (те саме, що в стенді <c>dev/fixture.js</c> «play»): linked, legal і завал — як у контракті.</summary>
    [Fact]
    public void Contract_example_board_gives_the_contract_lists()
    {
        var c = new SkarbCore(Enumerable.Range(0, 7), new Random(1));
        c.Begin();
        c.Turn = 0;
        c.GoalOpen[2] = true;
        c.GoldGoal = 1;
        c.Put(1, 0, "h", by: 1, id: 19);
        c.Put(2, 0, "x", by: 2, id: 3);
        c.Put(3, 0, "h", by: 0, id: 20);
        c.Put(2, 1, "v", by: 3, id: 15);
        c.Put(2, 2, "dv", by: 4, id: 34);
        c.Put(4, 0, "tv", r: 1, by: 1, id: 6);
        c.Put(4, -1, "cw", by: 2, id: 27);
        c.Broken[3] = 2;
        c.Hand[0].Clear();
        c.Hand[0].AddRange([12, 33, 44, 55, 60, 65]);
        var r = c.Reach();
        Assert.Equal([(1, 0), (2, 0), (3, 0), (2, 1), (2, 2), (4, 0), (4, -1)],
            c.Placed.Where(p => r[p] != 0).Select(p => (CellX(p), CellY(p))));
        string Of(int id)
        {
            var list = new List<SkMove>();
            c.LegalFor(0, id, list);
            return string.Join(" ", list.Select(Key));
        }
        // Окрім трьох клітинок біля карт, «трійник» лягає й біля самого колодязя: ліворуч (обидва повороти) і під ним.
        Assert.Equal("2,-1,0 3,-1,1 -1,0,0 -1,0,1 0,1,1 4,1,1", Of(12));
        Assert.Equal(Of(12), Of(33));
        Assert.Equal("1 2 4 5 6", Of(44));
        Assert.Equal("3,1", Of(55));
        Assert.Equal("0 1", Of(60));
        Assert.Equal("4,-1 1,0 2,0 3,0 4,0 2,1 2,2", Of(65));
    }

    [Fact]
    public void Allies_option_tells_moles_their_own()
    {
        foreach (var allies in new[] { "0", "1" })
        {
            var h = SkarbRoom.Table(5, bots: "5", allies: allies, seed: 2);
            var c = SkarbRoom.CoreOf(h);
            for (var s = 0; s < 5; s++)
            {
                var a = h.View(s).GetProperty("me").GetProperty("allies");
                if (allies == "1" && c.Mole[s])
                    Assert.Equal(c.Order.Where(o => o != s && c.Mole[o]), a.EnumerateArray().Select(x => x.GetInt32()));
                else Assert.Equal(JsonValueKind.Null, a.ValueKind);
            }
        }
    }

    [Fact]
    public void Board_linked_and_events_follow_the_game()
    {
        var h = SkarbRoom.Table(1, bots: "3", seed: 21);
        var c = SkarbRoom.CoreOf(h);
        for (var i = 0; i < 30 && c.Phase == SkPhase.Play; i++) SkarbRoom.Step(h, 0);
        var v = h.View(null);
        var board = v.GetProperty("board");
        Assert.Equal(c.Placed.Count + 1, board.GetArrayLength());
        Assert.Equal("start", board[0].GetProperty("s").GetString());
        var r = c.Reach();
        Assert.Equal(c.Placed.Count(p => r[p] != 0) + 1 + Enumerable.Range(0, 3).Count(g => c.GoalOpen[g] && r[SkarbCore.GoalCells[g]] != 0),
            v.GetProperty("linked").GetArrayLength());
        var events = v.GetProperty("events").EnumerateArray().ToList();
        Assert.InRange(events.Count, 1, 40);
        var seqs = events.Select(e => e.GetProperty("seq").GetInt64()).ToList();
        Assert.Equal(seqs.Order(), seqs);
        Assert.Equal(c.Seq, v.GetProperty("seq").GetInt64());
        Assert.InRange(v.GetProperty("log").GetArrayLength(), 1, 12);
        foreach (var e in events.Where(e => e.GetProperty("k").GetString() == "place"))
            foreach (var key in new[] { "seat", "card", "x", "y", "s", "r", "m", "c" }) Assert.True(e.TryGetProperty(key, out _));
        foreach (var e in events.Where(e => e.GetProperty("k").GetString() == "pass"))
            Assert.False(e.TryGetProperty("card", out _));
    }
}
