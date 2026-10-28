using System.Text.Json;
using System.Text.RegularExpressions;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Глек-ведучий «Хуторів» у балачці столу: події ядра → репліки книги <see cref="KhutoryLines"/> через запобіжник
/// <see cref="KhutoryGlek"/>. Ганяємо цілі партії з ботами через <see cref="RoomHarness"/> і читаємо TableSaid.
/// </summary>
public class KhutoryVoiceTests
{
    static readonly string[] Crew = ["Оля", "Петро", "Іра", "Влад"];

    static readonly KhMoment[] Always =
    [
        KhMoment.Start, KhMoment.Win, KhMoment.Longest, KhMoment.LongestLost, KhMoment.Army,
        KhMoment.Away, KhMoment.Back, KhMoment.SevenAgain,
    ];

    /// <summary>Шаблони книги як регулярки: плейсхолдер — будь-що.</summary>
    static readonly (KhMoment M, Regex R)[] Patterns =
    [
        .. Enum.GetValues<KhMoment>().SelectMany(m => KhutoryLines.Lines(m).Select(t =>
            (m, new Regex("^" + Regex.Replace(Regex.Escape(t), @"\\\{\w+}", ".+?") + "$")))),
    ];

    static KhMoment? MomentOf(string line) => Patterns.FirstOrDefault(p => p.R.IsMatch(line)) is { R: not null } hit ? hit.M : null;

    static RoomHarness Table(int people, string bots, int seed, string map = "random")
    {
        var h = new RoomHarness("khutory", new { map, timer = "60", bots }, seed: seed);
        for (var i = 0; i < people; i++) h.Join(Crew[i]);
        Assert.True(h.Start().Ok);
        return h;
    }

    static KhutoryCore CoreOf(RoomHarness h) => ((Khutory)h.Room.Game).Core;

    static List<string> Said(RoomHarness h) => [.. h.Outbox.OfType<TableSaid>().Select(x => x.Line.Text)];

    /// <summary>Ціла партія: людина за столом лише штовхає Глеків і таймер. Повертає репліки з часом.</summary>
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
        var steps = 0;
        while (h.Finished.Count == 0)
        {
            Assert.True(++steps < 6000, "партія не кінчається");
            var botIn = h.View(0).GetProperty("botIn");
            if (botIn.ValueKind == JsonValueKind.Number)
            {
                // Задрімала людина має 5 с на свій кидок — тоді botIn довший за ThinkMs.
                h.Clock.AdvanceMs(Math.Max(BoardBots.ThinkMs, botIn.GetInt32()));
                Assert.True(h.Act(0, "bot").Ok);
            }
            else
            {
                h.Clock.Advance(181);
                Assert.True(h.Act(0, "timeout").Ok);
            }
            Collect();
        }
        return said;
    }

    [Fact]
    public void Start_says_a_line_from_the_book()
    {
        var h = Table(2, "0", seed: 3);
        var said = Said(h);
        Assert.Single(said);
        Assert.Equal(KhMoment.Start, MomentOf(said[0]));
    }

    [Fact]
    public void Whole_games_obey_the_guard_and_never_leak()
    {
        var steals = 0;
        foreach (var seed in new[] { 11, 12, 13 })
        {
            var h = Table(1, "3", seed);
            var said = Play(h);
            var core = CoreOf(h);

            Assert.All(said, s => Assert.DoesNotContain("{", s.Text));
            Assert.All(said, s => Assert.NotNull(MomentOf(s.Text)));
            Assert.Equal(KhMoment.Start, MomentOf(said[0].Text));

            // Перемога — завжди, останнім словом, з ніком і очками переможця.
            var w = core.Winner!.Value;
            var win = said.Where(s => MomentOf(s.Text) == KhMoment.Win).ToList();
            Assert.Single(win);
            Assert.Contains(core.NameOf(w), win[0].Text);
            Assert.Contains(core.Points(w).ToString(), win[0].Text);

            // Перша сімка партії — завжди.
            if (core.Sevens > 0)
                Assert.Contains(said, s => MomentOf(s.Text) == KhMoment.Seven);

            // Запобіжник: «звичайна» репліка — не раніше ніж за 25 с від попередньої будь-якої.
            DateTimeOffset? last = null;
            var sevenYet = false;
            foreach (var (at, text) in said)
            {
                var m = MomentOf(text)!.Value;
                var always = Always.Contains(m) || (m == KhMoment.Seven && !sevenYet);
                if (m == KhMoment.Seven) sevenYet = true;
                if (!always && last is { } prev)
                    Assert.True(at - prev >= KhutoryGlek.Cooldown, $"«{text}» лише через {(at - prev).TotalSeconds} с");
                last = at;
            }
            Assert.True(said.Count < 80, $"забагато реплік: {said.Count}");

            // Крадіжка — без назви вкраденого (у книзі лише хто в кого).
            foreach (var (_, text) in said.Where(s => MomentOf(s.Text) == KhMoment.Steal))
            {
                steals++;
                foreach (var res in new[] { "wood", "clay", "wool", "grain", "ore" })
                    foreach (var form in KhutoryLines.ResVars(res).Values)
                        Assert.DoesNotContain(form, text);
            }
        }
        Assert.True(steals > 0, "за три партії Глек жодного разу не озвався на крадіжку");
    }

    [Fact]
    public void Sleeper_hears_away()
    {
        var h = Table(2, "0", seed: 5);
        var core = CoreOf(h);
        var a = core.Order[0];
        var b = core.Order[1];
        h.Clock.Advance(61);
        Assert.True(h.Act(b, "timeout").Ok);
        h.Clock.Advance(61);
        Assert.True(h.Act(a, "timeout").Ok);
        h.Clock.Advance(61);
        Assert.True(h.Act(a, "timeout").Ok);       // b проспав двічі — Глек за нього
        var away = Said(h).Where(s => MomentOf(s) == KhMoment.Away).ToList();
        Assert.Single(away);
        Assert.Contains(core.NameOf(b), away[0]);
    }
}
