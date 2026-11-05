using System.Text.RegularExpressions;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using static Hlechyky.Games.Impl.SolyanyiMap;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Глек-ведучий «Соляного шляху» (контракт §14.1): книга <see cref="SolyanyiLines"/>, запобіжник <see cref="SolyanyiGlek"/> і цілі
/// партії через кімнату (TableSaid): усе з книги, з правильними ніками, без спаму й без натяків на чужі замовлення.
/// </summary>
public class SolyanyiLinesTests
{
    static readonly (SlMoment M, Regex R)[] Patterns =
    [
        .. Enum.GetValues<SlMoment>().SelectMany(m => SolyanyiLines.Lines(m).Select(t =>
            (m, new Regex("^" + Regex.Replace(Regex.Escape(t), @"\\\{\w+}", ".+?") + "$")))),
    ];

    static SlMoment? MomentOf(string line) => Patterns.FirstOrDefault(p => p.R.IsMatch(line)) is { R: not null } hit ? hit.M : null;

    [Fact]
    public void Every_moment_has_at_least_five_lines_with_known_placeholders()
    {
        foreach (var m in Enum.GetValues<SlMoment>())
        {
            var lines = SolyanyiLines.Lines(m);
            Assert.True(lines.Count >= 5, $"{m}: лише {lines.Count}");
            Assert.True(lines.Count <= 8, $"{m}: аж {lines.Count}");
            Assert.Equal(lines.Count, lines.Distinct().Count());
            foreach (var line in lines)
                Assert.All(SolyanyiLines.Slots(line), s => Assert.Contains(s, SolyanyiLines.Known));
        }
        Assert.Equal(17, Enum.GetValues<SlMoment>().Length);
        Assert.Equal("Оля вирушає: Київ — Чернігів. Перший віз покотився",
            SolyanyiLines.Fill(SolyanyiLines.Lines(SlMoment.FirstClaim)[0], new Dictionary<string, string> { ["who"] = "Оля", ["a"] = "Київ", ["b"] = "Чернігів" }));
        Assert.All(Enum.GetValues<SlMoment>().SelectMany(SolyanyiLines.Lines), l => Assert.DoesNotContain("{", SolyanyiLines.Fill(l, null)));
    }

    [Fact]
    public void Guard_says_always_moments_and_rations_the_rest()
    {
        var g = new SolyanyiGlek();
        var rng = new Random(1);
        var t = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        Assert.NotNull(g.Say(SlMoment.Start, t, rng));
        // Решта — не того самого ходу і не раніше 20 с.
        for (var i = 0; i < 50; i++) Assert.Null(g.Say(SlMoment.Crimea, t.AddSeconds(1), rng));
        g.NewTurn();
        for (var i = 0; i < 50; i++) Assert.Null(g.Say(SlMoment.Crimea, t.AddSeconds(19), rng));
        string? said = null;
        for (var i = 0; i < 50 && said is null; i++) said = g.Say(SlMoment.Crimea, t.AddSeconds(21), rng);
        Assert.NotNull(said);
        Assert.Null(g.Say(SlMoment.Wipe, t.AddSeconds(90), rng));   // уже сказав цього ходу
        // «Завжди» — і того ж ходу, і без паузи.
        foreach (var m in new[] { SlMoment.LastRound, SlMoment.Stall, SlMoment.Win, SlMoment.Tie, SlMoment.Away, SlMoment.Back })
        {
            Assert.True(SolyanyiGlek.IsAlways(m));
            Assert.NotNull(g.Say(m, t.AddSeconds(22), rng));
        }
        Assert.True(SolyanyiGlek.IsAlways(SlMoment.Start));
        foreach (var m in new[] { SlMoment.FirstClaim, SlMoment.LongClaim, SlMoment.Crimea, SlMoment.Double, SlMoment.Blocked, SlMoment.FaceWild, SlMoment.Wipe, SlMoment.Tickets, SlMoment.RoadLead, SlMoment.LongThink })
            Assert.False(SolyanyiGlek.IsAlways(m));
        // Та сама репліка двічі поспіль не звучить.
        Assert.NotEqual(g.Say(SlMoment.Away, t, rng), g.Say(SlMoment.Away, t, rng));
    }

    [Fact]
    public void Chance_is_about_forty_percent()
    {
        var rng = new Random(2);
        var g = new SolyanyiGlek();
        var t = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        var said = 0;
        for (var i = 0; i < 2000; i++)
        {
            g.NewTurn();
            if (g.Say(SlMoment.Wipe, t.AddMinutes(i), rng) is not null) said++;
        }
        Assert.InRange(said, 700, 900);
    }

    static SlEvent Claim(int seat, int route) => new() { K = "claim", Data = [new("seat", seat), new("route", route)] };

    static string Nick(int s) => s switch { 0 => "Оля", 1 => "Петро", 2 => "Іра", _ => "Влад" };

    [Fact]
    public void Claims_map_to_moments_from_open_facts_only()
    {
        var c = SolyanyiBotTests.Playing(4, 3);
        var f = new SlHeard();
        // Шлях поза Кримом, короткий, перший для цього місця.
        var first = SolyanyiLines.Hear(Claim(0, 2), c, Nick, f)!.Value;
        Assert.Equal((SlMoment.FirstClaim, "Оля", "Чернігів", "Ніжин"), (first.M, first.Vars["who"], first.Vars["a"], first.Vars["b"]));
        // Уже не перший — мовчить.
        Assert.Null(SolyanyiLines.Hear(Claim(0, 3), c, Nick, f));
        // Довгий.
        var longOne = SolyanyiLines.Hear(Claim(0, 37), c, Nick, f)!.Value;
        Assert.Equal((SlMoment.LongClaim, "6 возів", "Київ", "Полтава"), (longOne.M, longOne.Vars["n"], longOne.Vars["a"], longOne.Vars["b"]));
        // Крим — лише перший шлях кожного гравця.
        Assert.Equal(SlMoment.Crimea, SolyanyiLines.Hear(Claim(1, 53), c, Nick, f)!.Value.M);
        Assert.Null(SolyanyiLines.Hear(Claim(1, 56), c, Nick, f));
        // Обидва шляхи пари — на 4–5.
        c.Owner[38] = 3;
        var both = SolyanyiLines.Hear(Claim(2, 90), c, Nick, f)!.Value;
        Assert.Equal((SlMoment.Double, "Влад"), (both.M, both.Vars["other"]));
        // Парний закрито на 2–3.
        var small = SolyanyiBotTests.Playing(2, 3);
        var blocked = SolyanyiLines.Hear(Claim(0, 38), small, Nick, new SlHeard())!.Value;
        Assert.Equal((SlMoment.Blocked, "Петро"), (blocked.M, blocked.Vars["other"]));
        // Блок Глека — на кого поставив.
        c.NoteBlock(1, 12, 3);
        var block = SolyanyiLines.Hear(Claim(1, 12), c, Nick, f)!.Value;
        Assert.Equal((SlMoment.Blocked, "Влад"), (block.M, block.Vars["other"]));
    }

    [Fact]
    public void Road_lead_is_said_once_per_player_when_the_road_is_longest_and_long()
    {
        var c = SolyanyiBotTests.Playing(3, 3);
        var f = new SlHeard();
        c.Road[0] = 14;
        Assert.Null(SolyanyiLines.HearRoad(c, Nick, f, 0));
        c.Road[0] = 15;
        c.Road[1] = 15;
        Assert.Null(SolyanyiLines.HearRoad(c, Nick, f, 0));   // не найбільша
        c.Road[0] = 16;
        var said = SolyanyiLines.HearRoad(c, Nick, f, 0)!.Value;
        Assert.Equal((SlMoment.RoadLead, "Оля", "16 возів"), (said.M, said.Vars["who"], said.Vars["road"]));
        c.Road[0] = 20;
        Assert.Null(SolyanyiLines.HearRoad(c, Nick, f, 0));   // уже сказав
    }

    [Fact]
    public void Other_events_map_too()
    {
        var c = SolyanyiBotTests.Playing(3, 3);
        var f = new SlHeard();
        Assert.Equal(SlMoment.Wipe, SolyanyiLines.Hear(new SlEvent { K = "wipe" }, c, Nick, f)!.Value.M);
        var ev = new SlEvent { K = "draw", Data = [new("seat", 1), new("from", "market"), new("slot", 2), new("pick", 1), new("card", "wild")] };
        Assert.Equal((SlMoment.FaceWild, "Петро"), (SolyanyiLines.Hear(ev, c, Nick, f)!.Value.M, SolyanyiLines.Hear(ev, c, Nick, f)!.Value.Vars["who"]));
        var plain = new SlEvent { K = "draw", Data = [new("seat", 1), new("from", "market"), new("slot", 2), new("pick", 1), new("card", "salt")] };
        Assert.Null(SolyanyiLines.Hear(plain, c, Nick, f));
        var tickets = new SlEvent { K = "tickets", Data = [new("seat", 2), new("n", 3)], SecretKey = "ids", Secret = new[] { 0, 1, 2 }, Insiders = [2] };
        var heard = SolyanyiLines.Hear(tickets, c, Nick, f)!.Value;
        Assert.Equal((SlMoment.Tickets, "Іра"), (heard.M, heard.Vars["who"]));
        Assert.DoesNotContain(heard.Vars.Values, v => v.Contains("Ужгород"));
        var last = SolyanyiLines.Hear(new SlEvent { K = "last", Data = [new("seat", 0), new("wagons", 2)] }, c, Nick, f)!.Value;
        Assert.Equal((SlMoment.LastRound, "Оля", "2 вози"), (last.M, last.Vars["who"], last.Vars["n"]));
        // Виконане замовлення Глек не чує навіть у книзі.
        Assert.Null(SolyanyiLines.Hear(new SlEvent { K = "done", Only = 0, Data = [new("seat", 0), new("ticket", 3)] }, c, Nick, f));
        SlRig.Stall(c);
        Assert.Contains(c.Events, e => e.K == "over");
        Assert.Equal(SlMoment.Stall, SolyanyiLines.Hear(c.Events.Last(), c, Nick, f)!.Value.M);
    }

    /// <summary>Та сама відкрита послідовність, той самий сід, інші чужі замовлення → ті самі рядки.</summary>
    [Fact]
    public void Lines_do_not_depend_on_foreign_tickets()
    {
        var a = SolyanyiBotTests.Playing(3, 8);
        var b = a.Clone();
        var other = a.Order[1];
        // У копії в місця інші замовлення — і зробимо одне виконаним.
        b.Tickets[other].Clear();
        b.Tickets[other].AddRange(Enumerable.Range(0, 40).Where(t => !a.Order.Any(s => a.Tickets[s].Contains(t))).Take(5));
        b.Done[b.Tickets[other][0]] = true;
        var linesA = Replay(a, 3);
        var linesB = Replay(b, 3);
        Assert.NotEmpty(linesA);
        Assert.Equal(linesA, linesB);
    }

    static List<string> Replay(SolyanyiCore c, int seed)
    {
        var g = new SolyanyiGlek();
        var f = new SlHeard();
        var rng = new Random(seed);
        var t = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        var said = new List<string>();
        // Відкрита послідовність: шляхи й ринок — той самий сценарій, незалежно від рук.
        foreach (var route in new[] { 2, 53, 37, 13, 56, 72, 62, 7, 25, 88 })
        {
            g.NewTurn();
            t = t.AddMinutes(1);
            var seat = route % 3;
            if (SolyanyiLines.Hear(Claim(seat, route), c, Nick, f) is { } h && g.Say(h.M, t, rng, h.Vars) is { } line) said.Add(line);
            g.NewTurn();
            t = t.AddMinutes(1);
            if (SolyanyiLines.Hear(new SlEvent { K = "wipe" }, c, Nick, f) is { } w && g.Say(w.M, t, rng, w.Vars) is { } wiped) said.Add(wiped);
        }
        return said;
    }

    static List<(DateTimeOffset At, string Text)> Play(RoomHarness h)
    {
        var said = new List<(DateTimeOffset, string)>();
        var seen = 0;
        void Collect()
        {
            var all = h.Outbox.OfType<TableSaid>().ToList();
            for (; seen < all.Count; seen++) said.Add((h.Clock.UtcNow, all[seen].Line.Text));
        }
        Collect();
        for (var i = 0; i < 3000 && h.Finished.Count == 0; i++)
        {
            SlRig.Step(h, 0);
            Collect();
        }
        Assert.Single(h.Finished);
        return said;
    }

    [Fact]
    public void Whole_games_speak_from_the_book_obey_the_guard_and_name_the_right_people()
    {
        foreach (var seed in new[] { 11, 12, 13 })
        {
            var h = SlRig.Play(1, bots: "3", seed: seed);
            var said = Play(h);
            var c = SlRig.CoreOf(h);
            Assert.All(said, s => Assert.DoesNotContain("{", s.Text));
            Assert.All(said, s => Assert.True(MomentOf(s.Text) is not null, $"не з книги: «{s.Text}»"));
            Assert.Equal(SlMoment.Start, MomentOf(said[0].Text));
            Assert.Single(said, s => MomentOf(s.Text) is SlMoment.Win or SlMoment.Tie or SlMoment.Stall);
            var end = said.Last();
            Assert.All(c.Winners, w => Assert.Contains(c.NameOf(w), end.Text));
            Assert.Contains(said, s => MomentOf(s.Text) == SlMoment.LastRound && s.Text.Contains(c.NameOf(c.LastBy)));
            // Запобіжник: «звичайна» репліка — не раніше ніж за 20 с від попередньої будь-якої.
            DateTimeOffset? prev = null;
            foreach (var (at, text) in said)
            {
                var m = MomentOf(text)!.Value;
                if (!SolyanyiGlek.IsAlways(m) && prev is { } p)
                    Assert.True(at - p >= SolyanyiGlek.Cooldown, $"«{text}» лише через {(at - p).TotalSeconds} с");
                prev = at;
            }
            Assert.True(said.Count < 90, $"забагато реплік: {said.Count}");
            Assert.True(said.Count > 3, $"замало реплік: {said.Count}");
        }
    }
}
