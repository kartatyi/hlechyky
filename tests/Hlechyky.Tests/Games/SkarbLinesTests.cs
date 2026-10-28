using System.Text.RegularExpressions;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Глек-ведучий «Скарбу Полуботка» (контракт §14.1): книга <see cref="SkarbLines"/>, запобіжник <see cref="SkarbGlek"/>, цілі
/// партії через кімнату (TableSaid) і те, що посеред раунду вибір рядка не залежить від справжніх ролей.
/// </summary>
public class SkarbLinesTests
{
    /// <summary>Шаблони книги як регулярки: плейсхолдер — будь-що.</summary>
    static readonly (SkMoment M, Regex R)[] Patterns =
    [
        .. Enum.GetValues<SkMoment>().SelectMany(m => SkarbLines.Lines(m).Select(t =>
            (m, new Regex("^" + Regex.Replace(Regex.Escape(t), @"\\\{\w+}", ".+?") + "$")))),
    ];

    static SkMoment? MomentOf(string line) => Patterns.FirstOrDefault(p => p.R.IsMatch(line)) is { R: not null } hit ? hit.M : null;

    [Fact]
    public void Every_moment_has_at_least_five_lines_with_known_placeholders()
    {
        foreach (var m in Enum.GetValues<SkMoment>())
        {
            var lines = SkarbLines.Lines(m);
            Assert.True(lines.Count >= 5, $"{m}: лише {lines.Count}");
            Assert.Equal(lines.Count, lines.Distinct().Count());
            foreach (var line in lines)
                Assert.All(SkarbLines.Slots(line), s => Assert.Contains(s, SkarbLines.Known));
        }
        Assert.Equal("Раунд 2. Кротів у колоді — до 3. Хто з вас — не скажу, бо й сам не знаю",
            SkarbLines.Fill(SkarbLines.Lines(SkMoment.RoundStart)[0], new Dictionary<string, string> { ["round"] = "2", ["moles"] = "3" }));
        Assert.DoesNotContain("{", SkarbLines.Fill(SkarbLines.Lines(SkMoment.Break)[0], null));
    }

    [Fact]
    public void Guard_says_always_moments_and_rations_the_rest()
    {
        var g = new SkarbGlek();
        var rng = new Random(1);
        var t = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        Assert.NotNull(g.Say(SkMoment.Start, t, rng));
        // Решта — не в той самий хід і не раніше 25 с.
        for (var i = 0; i < 50; i++) Assert.Null(g.Say(SkMoment.DeadEnd, t.AddSeconds(1), rng));
        g.NewTurn();
        for (var i = 0; i < 50; i++) Assert.Null(g.Say(SkMoment.DeadEnd, t.AddSeconds(24), rng));
        string? said = null;
        for (var i = 0; i < 50 && said is null; i++) said = g.Say(SkMoment.DeadEnd, t.AddSeconds(30), rng);
        Assert.NotNull(said);
        Assert.Null(g.Say(SkMoment.Map, t.AddSeconds(90), rng));   // уже сказав цього ходу
        // «Завжди» — і того ж ходу, і без паузи.
        foreach (var m in new[] { SkMoment.RoundStart, SkMoment.LastRound, SkMoment.Gold, SkMoment.MolesWin, SkMoment.NoMoles,
                     SkMoment.Win, SkMoment.Tie, SkMoment.Away, SkMoment.Back })
            Assert.NotNull(g.Say(m, t.AddSeconds(31), rng));
        // Та сама репліка двічі поспіль не звучить.
        var a = g.Say(SkMoment.Away, t, rng);
        var b = g.Say(SkMoment.Away, t, rng);
        Assert.NotEqual(a, b);
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
        for (var i = 0; i < 4000 && h.Finished.Count == 0; i++)
        {
            SkarbRoom.Step(h, 0);
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
            var h = SkarbRoom.Table(1, bots: "5", seed: seed);
            var said = Play(h);
            var c = SkarbRoom.CoreOf(h);
            Assert.All(said, s => Assert.DoesNotContain("{", s.Text));
            Assert.All(said, s => Assert.True(MomentOf(s.Text) is not null, $"не з книги: «{s.Text}»"));
            Assert.Equal(SkMoment.Start, MomentOf(said[0].Text));
            Assert.Equal(3, said.Count(s => MomentOf(s.Text) is SkMoment.RoundStart or SkMoment.LastRound));
            Assert.Single(said, s => MomentOf(s.Text) == SkMoment.LastRound);
            Assert.Equal(1, said.Count(s => MomentOf(s.Text) is SkMoment.Win or SkMoment.Tie));
            var last = said.Last(s => MomentOf(s.Text) is SkMoment.Win or SkMoment.Tie);
            Assert.All(c.Winners, w => Assert.Contains(c.NameOf(w), last.Text));
            // Скриня — завжди, з ніком того, хто відкрив; кроти — завжди, з усіма кротами.
            var golds = said.Where(s => MomentOf(s.Text) == SkMoment.Gold).ToList();
            var wins = c.Ends.Where(e => e.Side == "diggers").ToList();
            Assert.Equal(wins.Count, golds.Count);
            for (var i = 0; i < wins.Count; i++) Assert.Contains(c.NameOf(wins[i].Finisher!.Value), golds[i].Text);
            var moles = said.Where(s => MomentOf(s.Text) == SkMoment.MolesWin).ToList();
            var moleEnds = c.Ends.Where(e => e.Side == "moles").ToList();
            Assert.Equal(moleEnds.Count, moles.Count);
            for (var i = 0; i < moleEnds.Count; i++)
                Assert.All(c.Order.Where(o => moleEnds[i].Mole[o]), m => Assert.Contains(c.NameOf(m), moles[i].Text));
            // Запобіжник: «звичайна» репліка — не раніше ніж за 25 с від попередньої будь-якої.
            DateTimeOffset? prev = null;
            foreach (var (at, text) in said)
            {
                var m = MomentOf(text)!.Value;
                if (!SkarbGlek.IsAlways(m) && prev is { } p)
                    Assert.True(at - p >= SkarbGlek.Cooldown, $"«{text}» лише через {(at - p).TotalSeconds} с");
                prev = at;
            }
            Assert.True(said.Count < 80, $"забагато реплік: {said.Count}");
        }
    }

    [Fact]
    public void Away_is_said_when_a_sleeper_hands_over()
    {
        var h = SkarbRoom.Table(3);
        var c = SkarbRoom.CoreOf(h);
        var t = c.Turn;
        for (var i = 0; i < 4; i++)
        {
            h.Clock.Advance(65);
            Assert.True(h.Act(0, "timeout").Ok);
        }
        var away = h.Outbox.OfType<TableSaid>().Select(s => s.Line.Text).Where(s => MomentOf(s) == SkMoment.Away).ToList();
        Assert.Single(away);
        Assert.Contains(c.NameOf(t), away[0]);
    }

    /// <summary>
    /// Та сама публічна послідовність ходів посеред раунду, але ролі переставлені — Глек з тим самим сідом каже ті самі
    /// рядки: він ролей не читає.
    /// </summary>
    [Fact]
    public void Mid_round_lines_do_not_depend_on_who_is_really_a_mole()
    {
        var compared = 0;
        for (var seed = 1; seed <= 40; seed++)
        {
            var n = 3 + seed % 8;
            var a = new SkarbCore(Enumerable.Range(0, n), new Random(seed));
            a.Begin();
            var b = new SkarbCore(Enumerable.Range(0, n), new Random(seed));
            b.Begin();
            // Ролі в b — зсунуті по колу.
            var roles = b.Order.Select(o => b.Mole[o]).ToArray();
            for (var i = 0; i < n; i++) b.Mole[b.Order[i]] = roles[(i + 1) % n];
            if (Enumerable.Range(0, n).All(i => a.Mole[i] == b.Mole[i])) continue;
            var bot = new SkarbBot();
            var rng = new Random(seed * 7);
            while (a.Phase == SkPhase.Play)
            {
                bot.Observe(a);
                var mv = bot.Decide(a, a.Turn, rng);
                var s = a.Turn;
                Assert.Null(a.Apply(s, mv));
                Assert.Null(b.Apply(s, mv));
            }
            Assert.Equal(a.Seq, b.Seq);
            string Nick(int s) => $"гравець {s}";
            List<string> Hear(SkarbCore c)
            {
                var g = new SkarbGlek();
                var r = new Random(seed);
                var t0 = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
                var lines = new List<string>();
                foreach (var e in c.Events.TakeWhile(e => e.K != "end"))
                {
                    if (e.K == "turn") { g.NewTurn(); continue; }
                    if (SkarbLines.Hear(e, c, Nick) is { } m && g.Say(m.M, t0.AddSeconds(e.Seq * 9), r, m.Vars) is { } line)
                        lines.Add(line);
                }
                return lines;
            }
            var la = Hear(a);
            Assert.Equal(la, Hear(b));
            compared += la.Count;
        }
        Assert.True(compared > 50, $"порівняли лише {compared} рядків");
    }
}
