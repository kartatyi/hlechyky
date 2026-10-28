using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using static Hlechyky.Games.Impl.SkarbCards;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Скарб Полуботка» (контракт <c>docs/games/specs/skarb.md</c>): склад колоди, законність ходу, з'єднання й відкриття
/// міток, злитки, раунди й порядок випадковості — на <see cref="SkarbCore"/> напряму (з тасування потрібного поля не
/// збудуєш); тексти помилок §6 і каркас — через <see cref="RoomHarness"/>.
/// </summary>
public class SkarbTests
{
    // Сталі id карт (§1.2).
    const int X = 0, Tv = 5, Th = 10, V = 15, H = 19, Ce = 22, Cw = 26, Dx = 31, Dh = 35, Ds = 38;
    const int BreakPick = 40, BreakLamp = 43, BreakCart = 46, FixPick = 49, FixLamp = 51, FixPickLamp = 55, FixLampCart = 57;
    const int Map = 58, Rock = 64;

    /// <summary>Ядро на n місць після роздачі раунду 1: поле чисте, хід місця 0, скриня — під <paramref name="gold"/>.</summary>
    static SkarbCore Core(int n = 4, int seed = 1, int gold = 1)
    {
        var c = new SkarbCore(Enumerable.Range(0, n), new Random(seed));
        c.Begin();
        c.Turn = 0;
        c.GoldGoal = gold;
        return c;
    }

    static void Hand(SkarbCore c, int s, params int[] ids)
    {
        c.Hand[s].Clear();
        c.Hand[s].AddRange(ids);
    }

    static void Crosses(SkarbCore c, params (int X, int Y)[] cells)
    {
        foreach (var (x, y) in cells) c.Put(x, y, "x");
    }

    static (int, int)[] Row(int y, int x0, int x1) => [.. Enumerable.Range(x0, x1 - x0 + 1).Select(x => (x, y))];

    static List<SkEvent> Since(SkarbCore c, long seq) => [.. c.Events.Where(e => e.Seq > seq)];

    // =========================================================================================
    // Склад
    // =========================================================================================

    [Fact]
    public void Deck_has_67_cards_with_ids_and_masks_from_the_contract()
    {
        Assert.Equal(67, Deck.Length);
        Assert.All(Deck, (c, i) => Assert.Equal(i, c.Id));
        string[] shapes = ["x", "tv", "th", "v", "h", "ce", "cw", "dx", "dtv", "dth", "dv", "dh", "dce", "dcw", "ds", "de"];
        int[] counts = [5, 5, 5, 4, 3, 4, 5, 1, 1, 1, 1, 1, 1, 1, 1, 1];
        int[] masks = [15, 7, 14, 5, 10, 6, 12, 15, 7, 14, 5, 10, 6, 12, 4, 2];
        var id = 0;
        for (var k = 0; k < shapes.Length; k++)
            for (var i = 0; i < counts[k]; i++, id++)
            {
                Assert.Equal(SkKind.Path, Deck[id].K);
                Assert.Equal(shapes[k], Deck[id].Shape!.S);
                Assert.Equal(masks[k], Deck[id].Shape!.M);
                Assert.Equal(k < 7, Deck[id].Shape!.C);
            }
        Assert.Equal(40, id);
        Assert.Equal(31, Deck.Count(c => c.K == SkKind.Path && c.Shape!.C));
        (SkKind K, int Tools, int From, int To)[] acts =
        [
            (SkKind.Break, 1, 40, 42), (SkKind.Break, 2, 43, 45), (SkKind.Break, 4, 46, 48),
            (SkKind.Fix, 1, 49, 50), (SkKind.Fix, 2, 51, 52), (SkKind.Fix, 4, 53, 54),
            (SkKind.Fix, 3, 55, 55), (SkKind.Fix, 5, 56, 56), (SkKind.Fix, 6, 57, 57),
            (SkKind.Map, 0, 58, 63), (SkKind.Rockfall, 0, 64, 66),
        ];
        foreach (var (k, tools, from, to) in acts)
            for (var i = from; i <= to; i++)
            {
                Assert.Equal(k, Deck[i].K);
                Assert.Equal(tools, Deck[i].Tools);
            }
        Assert.Equal(15, Start.M);
        Assert.Equal(15, SkarbCards.Gold.M);
        Assert.Equal(9, StoneA.M);
        Assert.Equal(12, StoneB.M);
    }

    [Fact]
    public void Roles_hands_and_gold_per_player_count()
    {
        int[][] want = [[3, 1, 3, 6], [4, 1, 4, 6], [5, 2, 4, 6], [6, 2, 5, 5], [7, 3, 5, 5], [8, 3, 6, 4], [9, 3, 7, 4], [10, 4, 7, 4]];
        foreach (var w in want)
        {
            Assert.Equal((w[1], w[2]), Pack(w[0]));
            Assert.Equal(w[3], SkarbCards.Hand(w[0]));
            var c = new SkarbCore(Enumerable.Range(0, w[0]), new Random(w[0]));
            c.Begin();
            Assert.All(c.Order, s => Assert.Equal(w[3], c.Hand[s].Count));
            Assert.Equal(67 - w[0] * w[3], c.Deck.Count);
            Assert.Equal(w[1], c.Order.Count(s => c.Mole[s]) + (c.AsideMole ? 1 : 0));
        }
        var gold = GoldCards();
        Assert.Equal(28, gold.Length);
        Assert.Equal(44, gold.Sum());
        Assert.Equal([16, 8, 4], new[] { 1, 2, 3 }.Select(v => gold.Count(g => g == v)));
        Assert.Equal([4, 3, 3, 2], new[] { 1, 2, 3, 4 }.Select(MoleGold));
    }

    [Fact]
    public void Rotation_twice_is_identity_and_flips_edges()
    {
        for (var m = 0; m < 16; m++) Assert.Equal(m, Rot(Rot(m)));
        Assert.Equal(13, Rot(7));   // N E S → S W N
        Assert.Equal(11, Rot(14));  // E S W → W N E
        Assert.Equal(9, Rot(6));    // E S → W N
        Assert.Equal(3, Rot(12));   // S W → N E
        Assert.Equal(1, Rot(4));
        Assert.Equal(8, Rot(2));
    }

    // =========================================================================================
    // Законність ходу (§2.3)
    // =========================================================================================

    [Fact]
    public void Bounds_shaft_goal_and_occupied_cells_are_refused_with_their_texts()
    {
        var c = Core();
        Assert.Equal("Далі тверда скеля — тут не копають", c.PlaceError(13, 0, 15));
        Assert.Equal("Далі тверда скеля — тут не копають", c.PlaceError(0, 5, 15));
        Assert.Equal("Далі тверда скеля — тут не копають", c.PlaceError(-5, 0, 15));
        Assert.Equal("Тут колодязь — копай поруч", c.PlaceError(0, -1, 15));
        Assert.Equal("Тут колодязь — копай поруч", c.PlaceError(0, -4, 15));
        Assert.Equal("Тут колодязь — копай поруч", c.PlaceError(0, 0, 15));
        Assert.Equal("Тут мітка скарбу — до неї треба докопатись", c.PlaceError(8, 2, 15));
        Assert.Equal("Тут мітка скарбу — до неї треба докопатись", c.PlaceError(8, -2, 15));
        c.Put(1, 0, "x");
        Assert.Equal("Тут уже прокопано", c.PlaceError(1, 0, 15));
        Assert.Equal("Хід мусить тягнутись від колодязя", c.PlaceError(5, 3, 15));
        Assert.Null(c.PlaceError(2, 0, 15));
        // Шахта ні з чим не з'єднується й нічого не вимагає: карта поруч з глухим боком до неї лягає.
        Assert.Null(c.PlaceError(1, -1, 5));   // N S: W дивиться в шахту
    }

    [Fact]
    public void Edges_must_match_each_of_the_four_neighbours()
    {
        var c = Core();
        // (3, 1) оточена з'єднаними перехрестями з усіх чотирьох боків.
        Crosses(c, (1, 0), (2, 0), (3, 0), (4, 0), (4, 1), (2, 1), (2, 2), (3, 2));
        Assert.Null(c.PlaceError(3, 1, 15));
        foreach (var m in new[] { 14, Rot(14), 7, Rot(7) })   // по одному закритому краю: N, S, W, E
            Assert.Equal("Хід не сходиться з сусідньою картою", c.PlaceError(3, 1, m));
        // Відкритий край навпроти закритого краю сусіда — теж ні: «v» (N S) у (1, 1) під перехрестям, а праворуч — «h».
        var d = Core();
        Crosses(d, (1, 0));
        d.Put(2, 1, "v");
        Assert.Equal("Хід не сходиться з сусідньою картою", d.PlaceError(1, 1, 15));
        Assert.Null(d.PlaceError(1, 1, 5 | 4));
    }

    [Fact]
    public void Closed_goal_demands_nothing_but_open_goal_does()
    {
        var c = Core();
        Crosses(c, [(1, 0), (1, -1), (1, -2), (1, -3), .. Row(-3, 2, 7)]);
        Assert.Null(c.PlaceError(7, -2, 5));   // «v»: глухий бік до закритої мітки — можна
        Hand(c, 0, X);
        Assert.Null(c.Place(0, X, 8, -3));     // відкрили камінь згори
        Assert.True(c.GoalOpen[0]);
        // Відкрита мітка — звичайна карта: її західний край відкритий (stoneA, N W), тож закритий край навпроти — ні.
        c.Turn = 0;
        Assert.Equal("Хід не сходиться з сусідньою картою", c.PlaceError(7, -2, 5));
        Assert.Null(c.PlaceError(7, -2, 15));
    }

    [Fact]
    public void Symmetric_card_turned_over_lies_as_r0_and_is_listed_once()
    {
        var c = Core();
        Hand(c, 0, H, Cw);
        var legal = c.Legal(0);
        Assert.All(legal.Where(m => m.Card == H), m => Assert.Equal(0, m.R));
        Assert.Contains(legal, m => m.Card == Cw && m.R == 1);
        Assert.Null(c.Place(0, H, 1, 0, 1));
        var cell = Cell(1, 0);
        Assert.Equal(0, c.CellR[cell]);
        Assert.Equal(10, c.CellM[cell]);
        var place = c.Events.Last(e => e.K == "place");
        Assert.Equal(0, place["r"]);
        Assert.Equal(10, place["m"]);
    }

    [Fact]
    public void Dead_end_lies_but_nothing_attaches_beyond_it()
    {
        var c = Core();
        Hand(c, 0, Dh, X);
        Assert.Null(c.Place(0, Dh, 1, 0));
        Assert.True(c.Linked(Cell(1, 0)));
        Assert.Equal(1, c.DeadEnds[0]);
        c.Turn = 0;
        Hand(c, 0, X);
        Assert.Equal("Хід мусить тягнутись від колодязя", c.Place(0, X, 2, 0));
        Assert.DoesNotContain(c.Legal(0), m => m.X == 2 && m.Y == 0);
        // «Нора» (лише S) теж лягає, але далі нікуди.
        Hand(c, 0, Ds);
        Assert.Null(c.Place(0, Ds, 0, 1, 1));   // r = 1: край N дивиться на колодязь
    }

    [Fact]
    public void Card_cut_off_by_a_rockfall_gives_nothing_to_attach_to()
    {
        var c = Core();
        Crosses(c, (1, 0), (2, 0), (3, 0));
        Assert.Null(c.PlaceError(4, 0, 15));
        Hand(c, 0, Rock);
        Assert.Null(c.Rockfall(0, Rock, 2, 0));
        Assert.False(c.Linked(Cell(3, 0)));
        Assert.True(c.Linked(Cell(1, 0)));
        Assert.Equal("Хід мусить тягнутись від колодязя", c.PlaceError(4, 0, 15));
        Assert.Equal("Хід мусить тягнутись від колодязя", c.PlaceError(3, 1, 15));
        // Відірвана карта лежить, а з'єднати знову — можна.
        Assert.Null(c.PlaceError(2, 0, 15));
        Assert.Contains(Cell(3, 0), c.Placed);
    }

    [Fact]
    public void Broken_tool_forbids_only_paths()
    {
        var c = Core();
        c.Broken[0] = 1;
        Hand(c, 0, X, Map, Rock, BreakLamp, FixPick, Ce);
        c.Put(1, 0, "x");
        var legal = c.Legal(0);
        Assert.DoesNotContain(legal, m => SkarbCore.CardOf(m.Card).K == SkKind.Path);
        Assert.Contains(legal, m => m.Card == Map);
        Assert.Contains(legal, m => m.Card == Rock);
        Assert.Contains(legal, m => m.Card == BreakLamp);
        Assert.Contains(legal, m => m.Card == FixPick && m.Seat == 0);
        Assert.Equal("Кирку зламано — спершу полагодь", c.Place(0, X, 2, 0));
        Assert.Equal("Кирку зламано — спершу полагодь", c.WhyNot(0, X));
        c.Broken[0] = 2 | 4;
        Assert.Equal("Лампу розбито — спершу полагодь", c.Place(0, X, 2, 0));
        c.Broken[0] = 4;
        Assert.Equal("Тачка без колеса — спершу полагодь", c.Place(0, X, 2, 0));
        Assert.Null(c.LookMap(0, Map, 0));
    }

    [Fact]
    public void Break_and_fix_rules()
    {
        var c = Core();
        Hand(c, 0, BreakPick, BreakPick + 1, FixPickLamp, FixLamp);
        Assert.Equal("Не зрозумів, кому", c.BreakTool(0, BreakPick, null));
        Assert.Equal("Такого копача за столом нема", c.BreakTool(0, BreakPick, 7));
        Assert.Equal("Собі ламати не годиться", c.BreakTool(0, BreakPick, 0));
        c.Broken[1] = 1;
        Assert.Equal($"У {c.NameOf(1)} кирку вже зламано", c.BreakTool(0, BreakPick, 1));
        // Різні інструменти — можна кілька.
        c.Broken[2] = 2;
        Assert.Null(c.BreakTool(0, BreakPick, 2));
        Assert.Equal(3, c.Broken[2]);

        c.Turn = 0;
        Assert.Equal("Не зрозумів, кому лагодити", c.FixTool(0, FixPickLamp, null, null));
        Assert.Equal("Такого копача за столом нема", c.FixTool(0, FixPickLamp, 9, null));
        Assert.Equal("Скажи, що саме лагодити", c.FixTool(0, FixPickLamp, 2, null));   // зламано обидва
        Assert.Equal("Скажи, що саме лагодити", c.FixTool(0, FixPickLamp, 2, 2));      // тачки ця карта не лагодить
        Assert.Equal("Скажи, що саме лагодити", c.FixTool(0, FixLamp, 2, 0));
        Assert.Equal("Тут нічого лагодити — лампа ціла", c.FixTool(0, FixLamp, 3, null));
        Assert.Equal("Тут нічого лагодити — кирка ціла", c.FixTool(0, FixPickLamp, 3, null));
        // Подвійна без tool і зламано рівно один із двох — лагодить його.
        Assert.Null(c.FixTool(0, FixPickLamp, 1, null));
        Assert.Equal(0, c.Broken[1]);
        Assert.Equal(new[] { 40, 55 }, c.Discard.TakeLast(2));
        var fix = c.Events.Last(e => e.K == "fix");
        Assert.Equal("pick", fix["tool"]);
        Assert.Equal(1, fix["target"]);
    }

    [Fact]
    public void Map_shows_the_goal_only_to_the_looker_and_rockfall_rules()
    {
        var c = Core(gold: 2);
        Hand(c, 0, Map, Map + 1, Rock);
        Assert.Equal("Не зрозумів, яку мітку", c.LookMap(0, Map, null));
        Assert.Equal("Не зрозумів, яку мітку", c.LookMap(0, Map, 3));
        Assert.Null(c.LookMap(0, Map, 2));
        Assert.Equal(2, c.Peek[0][2]);
        var ev = c.Events.Last(e => e.K == "map");
        Assert.Equal("gold", ev.For(0)["kind"]);
        Assert.Null(ev.For(1)["kind"]);
        Assert.Null(ev.For(null)["kind"]);
        Assert.Equal(2, ev.For(1)["goal"]);

        c.Turn = 0;
        c.GoalOpen[1] = true;
        Assert.Equal("Цю мітку вже відкрито", c.LookMap(0, Map + 1, 1));
        c.GoalOpen[1] = false;
        Assert.Equal("Не зрозумів, що завалити", c.Rockfall(0, Rock, 1, null));
        Assert.Equal("Колодязь і мітки не завалиш", c.Rockfall(0, Rock, 0, 0));
        Assert.Equal("Колодязь і мітки не завалиш", c.Rockfall(0, Rock, 8, 0));
        Assert.Equal("Тут нема ходу, щоб завалити", c.Rockfall(0, Rock, 3, 3));
        Assert.Equal("Тут нема ходу, щоб завалити", c.Rockfall(0, Rock, 30, 3));
        c.Put(1, 0, "dh");
        Assert.Null(c.Rockfall(0, Rock, 1, 0));
        Assert.Empty(c.Placed);
        Assert.Contains(35, c.Discard);
    }

    // =========================================================================================
    // Мітки (§2.4)
    // =========================================================================================

    /// <summary>Підійти до мітки з напряму й відкрити її; повертає поворот.</summary>
    static int Approach(int goal, string from)
    {
        var c = Core(gold: 1);
        static ((int, int)[], (int X, int Y, int Card, int R)) P((int, int)[] p, (int, int, int, int) l) => (p, l);
        (int, int)[] path;
        (int X, int Y, int Card, int R) last;
        if (goal == 0)
            (path, last) = from switch
            {
                "W" => P([(1, 0), (1, -1), (1, -2), (1, -3), .. Row(-3, 2, 7)], (7, -2, X, 0)),
                "N" => P([(1, 0), (1, -1), (1, -2), (1, -3), .. Row(-3, 2, 7)], (8, -3, X, 0)),
                "S" => P([(1, 0), (1, -1), .. Row(-1, 2, 7)], (8, -1, Ce, 1)),   // N W: S закритий — до скрині нижче не тягнеться
                _ => P([(1, 0), (1, -1), (1, -2), (1, -3), (1, -4), .. Row(-4, 2, 9), (9, -3)], (9, -2, X, 0)),
            };
        else
            (path, last) = from switch
            {
                "W" => P([(1, 0), (1, 1), (1, 2), (1, 3), .. Row(3, 2, 7)], (7, 2, X, 0)),
                "N" => P([(1, 0), (1, 1), .. Row(1, 2, 7)], (8, 1, Cw, 0)),    // S W: N закритий
                "S" => P([(1, 0), (1, 1), (1, 2), (1, 3), .. Row(3, 2, 7)], (8, 3, X, 0)),
                _ => P([(1, 0), (1, 1), (1, 2), (1, 3), (1, 4), .. Row(4, 2, 9), (9, 3)], (9, 2, X, 0)),
            };
        Crosses(c, path);
        Hand(c, 0, last.Card);
        var seq = c.Seq;
        Assert.Null(c.Place(0, last.Card, last.X, last.Y, last.R));
        Assert.True(c.GoalOpen[goal], $"мітка {goal} з {from} не відкрилась");
        Assert.False(c.GoalOpen[1]);
        var ev = Assert.Single(Since(c, seq), e => e.K == "goal");
        Assert.Equal(goal, ev["i"]);
        Assert.Equal("stone", ev["kind"]);
        Assert.Equal(goal == 0 ? "stoneA" : "stoneB", ev["s"]);
        Assert.Equal(Mask(goal == 0 ? StoneA : StoneB, c.GoalR[goal]), ev["m"]);
        Assert.Equal(0, ev["by"]);
        Assert.Equal(SkPhase.Play, c.Phase);
        return c.GoalR[goal];
    }

    [Theory]
    [InlineData(0, "W", 0)]
    [InlineData(0, "N", 0)]
    [InlineData(0, "S", 1)]
    [InlineData(0, "E", 1)]
    [InlineData(2, "W", 0)]
    [InlineData(2, "N", 1)]
    [InlineData(2, "S", 0)]
    [InlineData(2, "E", 1)]
    public void Goal_opens_turned_by_the_side_it_was_reached_from(int goal, string from, int r) =>
        Assert.Equal(r, Approach(goal, from));

    [Fact]
    public void Stone_that_links_the_path_on_opens_the_chest_in_a_cascade()
    {
        var c = Core(gold: 1);
        Crosses(c, [.. Row(0, 1, 6), (6, -1), (6, -2)]);
        // Відірваний хід за каменем: із (8, -3) униз правим боком до скрині.
        Crosses(c, (8, -3), (9, -3), (9, -2), (9, -1), (9, 0));
        Hand(c, 2, X);
        c.Turn = 2;
        var seq = c.Seq;
        Assert.Null(c.Place(2, X, 7, -2));
        var goals = Since(c, seq).Where(e => e.K == "goal").ToList();
        Assert.Equal([0, 1], goals.Select(e => (int)e["i"]!));
        Assert.Equal(["stone", "gold"], goals.Select(e => (string)e["kind"]!));
        Assert.Equal(SkPhase.Round, c.Phase);
        Assert.Equal(2, c.Ends[^1].Finisher);
        Assert.Equal("diggers", c.Ends[^1].Side);
    }

    [Fact]
    public void Two_goals_in_one_move_come_in_index_order()
    {
        var c = Core(gold: 1);
        Crosses(c, [.. Row(0, 1, 4), (5, -1), (5, -2), (6, -2), (7, -2), (5, 1), (5, 2), (6, 2), (7, 2)]);
        Hand(c, 0, X);
        var seq = c.Seq;
        Assert.Null(c.Place(0, X, 5, 0));
        Assert.Equal([0, 2], Since(c, seq).Where(e => e.K == "goal").Select(e => (int)e["i"]!));
        Assert.Equal(SkPhase.Play, c.Phase);
        Assert.False(c.GoalOpen[1]);
    }

    [Fact]
    public void Rockfall_and_map_never_open_a_goal()
    {
        var c = Core(gold: 1);
        Crosses(c, Row(0, 1, 7));   // (7, 0) уже дивиться на скриню, але мітки відкриваються лише після покладеного ходу
        Hand(c, 0, Rock, Map);
        Assert.Null(c.Rockfall(0, Rock, 3, 0));
        c.Turn = 0;
        Assert.Null(c.LookMap(0, Map, 1));
        Assert.DoesNotContain(true, c.GoalOpen);
    }

    // =========================================================================================
    // Злитки (§2.6, §2.7)
    // =========================================================================================

    /// <summary>Місце <paramref name="s"/> відкриває скриню під середньою міткою.</summary>
    static void Dig(SkarbCore c, int s)
    {
        c.GoldGoal = 1;
        Crosses(c, Row(0, 1, 6));
        Hand(c, s, X);
        c.Turn = s;
        Assert.Null(c.Place(s, X, 7, 0));
        Assert.Equal("diggers", c.Ends[^1].Side);
    }

    static void Roles(SkarbCore c, params int[] moles)
    {
        Array.Clear(c.Mole);
        foreach (var m in moles) c.Mole[m] = true;
    }

    [Fact]
    public void Diggers_split_gold_backwards_from_the_finisher_five_players()
    {
        var c = Core(5);
        Roles(c, 1);
        c.GoldDeck = [1, 3, 2, 1, 2, 3, 3];
        Dig(c, 3);
        // [3 2 2 1 1] по колу 3 → 2 → (1 кріт) → 0 → 4 → 3.
        var end = c.Ends[^1];
        Assert.Equal([(3, 4), (2, 2), (0, 2), (4, 1)], end.Gold.Select(g => (g.Seat, g.N)));
        Assert.Equal([3, 1], end.Gold[0].Cards!);
        Assert.Equal([2, 0, 2, 4, 1], c.Order.Select(o => c.Gold[o]));
        Assert.Equal([3, 3], c.GoldDeck);
        Assert.Equal(3, end.Finisher);
        Assert.Equal(1, c.Finished[3]);
    }

    [Fact]
    public void Mole_finisher_gives_the_biggest_card_to_the_first_digger_backwards()
    {
        var c = Core(5);
        Roles(c, 1);
        c.GoldDeck = [1, 3, 2, 1, 2];
        Dig(c, 1);
        var end = c.Ends[^1];
        Assert.Equal(1, end.Finisher);
        Assert.Equal([(0, 4), (4, 2), (3, 2), (2, 1)], end.Gold.Select(g => (g.Seat, g.N)));
        Assert.Equal(0, c.Gold[1]);
        Assert.Equal(0, c.Won[1]);
        Assert.Equal(1, c.Won[0]);
    }

    [Fact]
    public void Diggers_split_nine_cards_at_a_table_of_ten()
    {
        var c = Core(10);
        Roles(c, 2, 5, 7);
        c.GoldDeck = [1, 1, 2, 3, 1, 2, 1, 3, 1, 2, 2];
        Dig(c, 4);
        // [3 3 2 2 1 1 1 1 1]: 4, 3, 1, 0, 9, 8, 6, і знову 4, 3.
        var end = c.Ends[^1];
        Assert.Equal([(4, 4), (3, 4), (1, 2), (0, 2), (9, 1), (8, 1), (6, 1)], end.Gold.Select(g => (g.Seat, g.N)));
        Assert.Equal([3, 1], end.Gold[0].Cards!);
        Assert.Equal([2, 2], c.GoldDeck);
        Assert.Equal(new[] { 2, 2, 0, 4, 4, 0, 1, 0, 1, 1 }, end.Totals);
    }

    /// <summary>Довести раунд до кінця пасами (колода й руки скінчились).</summary>
    static void Exhaust(SkarbCore c)
    {
        c.Deck.Clear();
        foreach (var o in c.Order) c.Hand[o].Clear();
        Hand(c, c.Turn, Map);
        Assert.Null(c.Pass(c.Turn, Map));
    }

    [Theory]
    [InlineData(4, new[] { 2 }, 4)]
    [InlineData(5, new[] { 0, 3 }, 3)]
    [InlineData(8, new[] { 1, 4, 6 }, 3)]
    [InlineData(10, new[] { 0, 2, 4, 9 }, 2)]
    public void Moles_win_when_deck_and_hands_run_out(int n, int[] moles, int each)
    {
        var c = Core(n);
        Roles(c, moles);
        var left = c.GoldDeck.Count;
        Exhaust(c);
        var end = c.Ends[^1];
        Assert.Equal("moles", end.Side);
        Assert.Null(end.Finisher);
        Assert.All(c.Order, o => Assert.Equal(moles.Contains(o) ? each : 0, c.Gold[o]));
        Assert.All(end.Gold, g => Assert.Null(g.Cards));
        Assert.Equal(left, c.GoldDeck.Count);   // не з колоди злитків
        Assert.All(moles, m => Assert.Equal(each, c.MoleGoldGot[m]));
    }

    [Fact]
    public void No_moles_and_no_gold_means_nobody_gets_anything()
    {
        var c = Core(3);
        Roles(c);
        c.AsideMole = true;
        Exhaust(c);
        Assert.Equal("none", c.Ends[^1].Side);
        Assert.All(c.Order, o => Assert.Equal(0, c.Gold[o]));
        Assert.Empty(c.Ends[^1].Gold);
        Assert.All(c.Order, o => Assert.Equal(0, c.Won[o]));
    }

    [Fact]
    public void Winner_is_the_richest_then_more_rounds_won_then_shared()
    {
        var c = Core(3);
        c.Round = 3;
        Roles(c);
        c.Gold[0] = 5; c.Gold[1] = 5; c.Gold[2] = 3;
        c.Won[0] = 1; c.Won[1] = 2;
        Exhaust(c);
        Assert.True(c.Over);
        Assert.Equal([1], c.Winners);
        var over = c.Events.Last(e => e.K == "over");
        Assert.Equal([1], (int[])over["winners"]!);

        var d = Core(3);
        d.Round = 3;
        Roles(d);
        d.Gold[0] = 5; d.Gold[1] = 5; d.Gold[2] = 3;
        d.Won[0] = 2; d.Won[1] = 2;
        Exhaust(d);
        Assert.Equal([0, 1], d.Winners);
    }

    // =========================================================================================
    // Раунд (§2.1, §2.2)
    // =========================================================================================

    [Fact]
    public void Empty_hands_are_skipped_once_with_an_event()
    {
        var c = Core(4);
        c.Deck.Clear();
        Hand(c, 0, Map, Map + 1);
        Hand(c, 1);
        Hand(c, 2, Map + 2);
        Hand(c, 3);
        var seq = c.Seq;
        Assert.Null(c.Pass(0, Map));
        var evs = Since(c, seq);
        Assert.Equal(["pass", "skip", "turn"], evs.Select(e => e.K));
        Assert.Equal(1, evs[1]["seat"]);
        Assert.Equal(2, c.Turn);
        Assert.Contains($"у {c.NameOf(1)} порожні руки", c.Log);
        seq = c.Seq;
        Assert.Null(c.Pass(2, Map + 2));
        // 3 і 1 уже без карт: 3 — уперше, 1 — уже казали.
        Assert.Equal(["pass", "skip", "turn"], Since(c, seq).Select(e => e.K));
        Assert.Equal(0, c.Turn);
    }

    [Fact]
    public void Drawing_the_last_card_says_deckout()
    {
        var c = Core(3);
        c.Deck = [Map + 3];
        Hand(c, 0, Map);
        var seq = c.Seq;
        Assert.Null(c.Pass(0, Map));
        Assert.Contains(Since(c, seq), e => e.K == "deckout");
        Assert.Equal([Map + 3], c.Hand[0]);
    }

    [Fact]
    public void Next_round_starts_after_the_last_mover_with_new_roles()
    {
        var c = new SkarbCore(Enumerable.Range(0, 5), new Random(7));
        c.Begin();
        var rounds = new List<bool[]>();
        for (var r = 1; r <= 3; r++)
        {
            Assert.Equal(r, c.Round);
            rounds.Add((bool[])c.Mole.Clone());
            Assert.Equal(c.First, c.Turn);
            var bot = new SkarbBot();
            while (c.Phase == SkPhase.Play)
            {
                bot.Observe(c);
                Assert.Null(c.Apply(c.Turn, bot.Decide(c, c.Turn, new Random(r))));
            }
            if (r == 3) break;
            var last = c.LastMover;
            Assert.Null(c.NextRound());
            Assert.Equal(c.NextSeat(last), c.First);
            Assert.All(c.Order, o => Assert.Equal(6, c.Hand[o].Count));
            Assert.Empty(c.Placed);
            Assert.All(c.Broken, b => Assert.Equal(0, b));
            Assert.DoesNotContain(true, c.GoalOpen);
        }
        Assert.True(c.Over);
        Assert.Equal("Зараз не перерва між раундами", c.NextRound());
        Assert.Equal(3, c.Ends.Count);
    }

    [Fact]
    public void Random_calls_follow_the_contract_order()
    {
        foreach (var seed in new[] { 1, 42, 777 })
            foreach (var n in new[] { 3, 7, 10 })
            {
                var c = new SkarbCore(Enumerable.Range(0, n), new Random(seed));
                c.Begin();
                var rng = new Random(seed);
                var gold = GoldCards();
                for (var i = gold.Length - 1; i >= 1; i--) { var j = rng.Next(i + 1); (gold[i], gold[j]) = (gold[j], gold[i]); }
                Assert.Equal(gold, c.GoldDeck);
                var first = rng.Next(n);
                Assert.Equal(first, c.First);
                var (m, d) = Pack(n);
                var roles = Enumerable.Repeat(true, m).Concat(Enumerable.Repeat(false, d)).ToArray();
                for (var i = roles.Length - 1; i >= 1; i--) { var j = rng.Next(i + 1); (roles[i], roles[j]) = (roles[j], roles[i]); }
                for (var k = 0; k < n; k++) Assert.Equal(roles[k], c.Mole[(first + k) % n]);
                Assert.Equal(roles[n], c.AsideMole);
                Assert.Equal(rng.Next(3), c.GoldGoal);
                var deck = Enumerable.Range(0, 67).ToArray();
                for (var i = deck.Length - 1; i >= 1; i--) { var j = rng.Next(i + 1); (deck[i], deck[j]) = (deck[j], deck[i]); }
                var hand = SkarbCards.Hand(n);
                for (var k = 0; k < n; k++) Assert.Equal(deck.Skip(k * hand).Take(hand), c.Hand[(first + k) % n]);
                Assert.Equal(deck.Skip(n * hand), c.Deck);
            }
    }

    [Fact]
    public void Same_seed_same_game_through_the_room()
    {
        static string Run()
        {
            var h = new RoomHarness("skarb", new { timer = "60", bots = "4", allies = "0" }, seed: 9);
            h.Join("Оля");
            Assert.True(h.Start().Ok);
            for (var i = 0; i < 2000 && h.Finished.Count == 0; i++) SkarbRoom.Step(h, 0);
            Assert.Single(h.Finished);
            return h.View(0).GetRawText();
        }
        Assert.Equal(Run(), Run());
    }

    // =========================================================================================
    // Тексти помилок через кімнату (§6)
    // =========================================================================================

    [Fact]
    public void Act_errors_say_the_contract_texts()
    {
        var h = SkarbRoom.Table(3);
        var game = (Skarb)h.Room.Game;
        var c = game.Core;
        var t = c.Turn;
        var other = c.NextSeat(t);
        Assert.Equal($"Зараз копає {c.NameOf(t)}", h.Act(other, "play", new { card = c.Hand[other][0] }).Message);
        Assert.Equal("Тут так не ходять", h.Act(t, "dance").Message);
        Assert.Equal("Не зрозумів, яку карту", h.Act(t, "play", new { x = 1 }).Message);
        Assert.Equal("Не зрозумів, яку карту", h.Act(t, "play", new { card = "x" }).Message);
        var foreign = Enumerable.Range(0, 67).First(id => !c.Hand[t].Contains(id));
        Assert.Equal("Такої карти в тебе нема", h.Act(t, "play", new { card = foreign }).Message);
        Assert.Equal("Не зрозумів, яку карту скинути", h.Act(t, "pass", new { }).Message);
        Assert.Equal("Такої карти в тебе нема", h.Act(t, "pass", new { card = foreign }).Message);
        Assert.Equal("Зараз не перерва між раундами", h.Act(t, "next").Message);

        lock (h.Room.Sync)
        {
            Hand(c, t, X, BreakCart, FixLampCart, Map, Rock, V);
            c.Put(1, 0, "x");
        }
        Assert.Equal("Не зрозумів, куди класти", h.Act(t, "play", new { card = X, x = 2 }).Message);
        Assert.Equal("Карту можна лише перевернути: 0 або 180°", h.Act(t, "play", new { card = V, x = 2, y = 0, r = 2 }).Message);
        Assert.Equal("Далі тверда скеля — тут не копають", h.Act(t, "play", new { card = X, x = 13, y = 0 }).Message);
        Assert.Equal("Тут колодязь — копай поруч", h.Act(t, "play", new { card = X, x = 0, y = -2 }).Message);
        Assert.Equal("Тут мітка скарбу — до неї треба докопатись", h.Act(t, "play", new { card = X, x = 8, y = 0 }).Message);
        Assert.Equal("Тут уже прокопано", h.Act(t, "play", new { card = X, x = 1, y = 0 }).Message);
        Assert.Equal("Хід не сходиться з сусідньою картою", h.Act(t, "play", new { card = V, x = 2, y = 0 }).Message);
        Assert.Equal("Хід мусить тягнутись від колодязя", h.Act(t, "play", new { card = X, x = 4, y = 2 }).Message);
        Assert.Equal("Не зрозумів, кому", h.Act(t, "play", new { card = BreakCart }).Message);
        Assert.Equal("Такого копача за столом нема", h.Act(t, "play", new { card = BreakCart, seat = 6 }).Message);
        Assert.Equal("Собі ламати не годиться", h.Act(t, "play", new { card = BreakCart, seat = t }).Message);
        Assert.Equal("Не зрозумів, кому лагодити", h.Act(t, "play", new { card = FixLampCart }).Message);
        Assert.Equal("Такого копача за столом нема", h.Act(t, "play", new { card = FixLampCart, seat = 6 }).Message);
        Assert.Equal("Скажи, що саме лагодити", h.Act(t, "play", new { card = FixLampCart, seat = t, tool = "pick" }).Message);
        Assert.Equal("Скажи, що саме лагодити", h.Act(t, "play", new { card = FixLampCart, seat = t, tool = "spoon" }).Message);
        Assert.Equal("Тут нічого лагодити — лампа ціла", h.Act(t, "play", new { card = FixLampCart, seat = t }).Message);
        Assert.Equal("Тут нічого лагодити — тачка ціла", h.Act(t, "play", new { card = FixLampCart, seat = t, tool = "cart" }).Message);
        Assert.Equal("Не зрозумів, яку мітку", h.Act(t, "play", new { card = Map }).Message);
        Assert.Equal("Не зрозумів, яку мітку", h.Act(t, "play", new { card = Map, goal = 5 }).Message);
        Assert.Equal("Не зрозумів, що завалити", h.Act(t, "play", new { card = Rock, x = 1 }).Message);
        Assert.Equal("Колодязь і мітки не завалиш", h.Act(t, "play", new { card = Rock, x = 0, y = 0 }).Message);
        Assert.Equal("Тут нема ходу, щоб завалити", h.Act(t, "play", new { card = Rock, x = 5, y = 0 }).Message);
        lock (h.Room.Sync)
        {
            c.Broken[other] = 4;
            c.GoalOpen[2] = true;
        }
        Assert.Equal($"У {c.NameOf(other)} тачку вже зламано", h.Act(t, "play", new { card = BreakCart, seat = other }).Message);
        Assert.Equal("Цю мітку вже відкрито", h.Act(t, "play", new { card = Map, goal = 2 }).Message);
        lock (h.Room.Sync) c.GoalOpen[2] = false;

        // Нелегальне нічого не міняє.
        var before = h.View(t).GetRawText();
        Assert.False(h.Act(t, "play", new { card = X, x = 9, y = 3 }).Ok);
        Assert.Equal(before, h.View(t).GetRawText());

        // Легальний хід: r = 1 «тх» лежачого — працює, а «тут грає Глек» / «не граєш» — на рівні гри.
        Assert.True(h.Act(t, "play", new { card = X, x = 2, y = 0 }).Ok);
        lock (h.Room.Sync)
        {
            Assert.Equal("Ти не граєш за цим столом", game.Act(7, "play", Views.Payload(new { card = 1 })).Message);
            Assert.Equal("Тут так не ходять", game.Act(t, "dig", Views.Payload(null)).Message);
        }
    }

    [Fact]
    public void Bot_seat_and_finished_game_refuse()
    {
        var h = SkarbRoom.Table(1, bots: "2");
        var game = (Skarb)h.Room.Game;
        lock (h.Room.Sync)
        {
            Assert.Equal("Тут грає Глек", game.Act(1, "pass", Views.Payload(new { card = 1 })).Message);
            game.Core.Phase = SkPhase.Round;
            Assert.Equal("Раунд скінчився — чекаємо наступного", game.Core.Pass(0, 1));
            game.Core.Phase = SkPhase.Over;
            Assert.Equal("Партія вже скінчилась", game.Act(0, "pass", Views.Payload(new { card = 1 })).Message);
            game.Core.Phase = SkPhase.Play;
        }
    }

    [Fact]
    public void Info_options_and_can_start()
    {
        var info = new Skarb().Info;
        Assert.Equal("skarb", info.Id);
        Assert.Equal(GameGroup.Party, info.Group);
        Assert.Equal((1, 10), (info.MinPlayers, info.MaxPlayers));
        Assert.True(info.Hidden);
        Assert.False(info.Rated);
        Assert.Equal(StartMode.ByHost, info.Start);
        Assert.Equal(0, info.TickMs);
        Assert.Equal(["bots", "timer", "allies"], info.Options!.Select(o => o.Key));
        Assert.Equal(10, info.Options![0].Values.Count);
        Assert.Equal(("9", "дев'ять"), info.Options[0].Values[9]);
        Assert.Equal("60", info.Options[1].Default);
        Assert.Equal(["none", "30", "60", "90"], info.Options[1].Values.Select(v => v.Value));
        Assert.Equal("0", info.Options[2].Default);
        // Інші ігри бачать ті самі перші підписи й імена.
        Assert.Equal(6, BoardBots.Option(5).Values.Count);
        Assert.Equal(["Глек 🤖", "Макітра 🤖", "Горнятко 🤖", "Куманець 🤖", "Глечик 🤖"], BoardBots.Names.Take(5));
        Assert.Equal(10, BoardBots.Names.Distinct().Count());

        var h = new RoomHarness("skarb", new { timer = "60", bots = "1", allies = "0" });
        h.Join("Оля");
        Assert.False(h.Start().Ok);
        Assert.Equal("Утрьох щонайменше: поклич людей або додай 🤖 Глеків", h.Reply.Message);
        h.Join("Петро");
        Assert.True(h.Start().Ok);
        Assert.Equal(3, ((Skarb)h.Room.Game).Core.Seats);
    }

    [Fact]
    public void Ten_at_the_table_have_ten_different_names()
    {
        var h = SkarbRoom.Table(1, bots: "9");
        var players = h.View(0).GetProperty("players");
        Assert.Equal(10, players.GetArrayLength());
        Assert.Equal(10, players.EnumerateArray().Select(p => p.GetProperty("nick").GetString()).Distinct().Count());
    }
}

/// <summary>Спільне для тестів «Скарбу» через кімнату.</summary>
static class SkarbRoom
{
    public static readonly string[] Crew = ["Оля", "Петро", "Іра", "Влад", "Марта"];

    public static RoomHarness Table(int people, string bots = "0", string timer = "60", string allies = "0", int seed = 5)
    {
        var h = new RoomHarness("skarb", new { timer, bots, allies }, seed: seed);
        for (var i = 0; i < people; i++) h.Join(Crew[i]);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    public static SkarbCore CoreOf(RoomHarness h) => ((Skarb)h.Room.Game).Core;

    /// <summary>
    /// Один крок столу від місця <paramref name="by"/>: штовхнути Глека; нема на кого — пауза між раундами («Далі»),
    /// інакше людина, чий хід, грає підказку Глека, а без часу — строк минув.
    /// </summary>
    public static void Step(RoomHarness h, int by)
    {
        var v = h.View(by);
        if (v.GetProperty("phase").GetString() == "round")
        {
            h.Clock.Advance(16);
            Assert.True(h.Act(by, "next").Ok);
            return;
        }
        if (v.GetProperty("botIn").ValueKind == JsonValueKind.Number)
        {
            h.Clock.AdvanceMs(Math.Max(BoardBots.ThinkMs, v.GetProperty("botIn").GetInt32()));
            var r = h.Act(by, BoardBots.Nudge);
            Assert.True(r.Ok, r.Message);
            return;
        }
        var turn = v.GetProperty("turn").GetInt32();
        var tip = h.View(turn).GetProperty("me").GetProperty("tip");
        var res = tip.TryGetProperty("pass", out _)
            ? h.Act(turn, "pass", new { card = tip.GetProperty("card").GetInt32() })
            : h.Act(turn, "play", JsonSerializer.Deserialize<Dictionary<string, object>>(tip.GetRawText()));
        Assert.True(res.Ok, res.Message);
    }
}
