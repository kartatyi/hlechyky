using System.Text.RegularExpressions;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Глек-ведучий «Сорочинського ярмарку» (контракт §14.1): книга <see cref="SorochyntsiLines"/>, запобіжник
/// <see cref="SorochyntsiGlek"/> і цілі партії через кімнату (TableSaid): усе з книги, з правильними ніками, без спаму.
/// </summary>
public class SorochyntsiLinesTests
{
    static readonly (SoMoment M, Regex R)[] Patterns =
    [
        .. Enum.GetValues<SoMoment>().SelectMany(m => SorochyntsiLines.Lines(m).Select(t =>
            (m, new Regex("^" + Regex.Replace(Regex.Escape(t), @"\\\{\w+}", ".+?") + "$")))),
    ];

    static SoMoment? MomentOf(string line) => Patterns.FirstOrDefault(p => p.R.IsMatch(line)) is { R: not null } hit ? hit.M : null;

    [Fact]
    public void Every_moment_has_at_least_five_lines_with_known_placeholders()
    {
        foreach (var m in Enum.GetValues<SoMoment>())
        {
            var lines = SorochyntsiLines.Lines(m);
            Assert.True(lines.Count >= 5, $"{m}: лише {lines.Count}");
            Assert.Equal(lines.Count, lines.Distinct().Count());
            foreach (var line in lines)
                Assert.All(SorochyntsiLines.Slots(line), s => Assert.Contains(s, SorochyntsiLines.Known));
        }
        Assert.Equal("До Оля завітав Кобзар. Ставте вареники — +3 ⭐",
            SorochyntsiLines.Fill(SorochyntsiLines.Lines(SoMoment.Guest)[0], new Dictionary<string, string> { ["who"] = "Оля", ["guest"] = "Кобзар" }));
        Assert.DoesNotContain("{", SorochyntsiLines.Fill(SorochyntsiLines.Lines(SoMoment.StarshynaLost)[0], null));
        Assert.Equal(18, Enum.GetValues<SoMoment>().Length);
    }

    [Fact]
    public void Guard_says_always_moments_and_rations_the_rest()
    {
        var g = new SorochyntsiGlek();
        var rng = new Random(1);
        var t = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        Assert.NotNull(g.Say(SoMoment.Start, t, rng));
        // Решта — не того самого ходу і не раніше 20 с.
        for (var i = 0; i < 50; i++) Assert.Null(g.Say(SoMoment.Guest, t.AddSeconds(1), rng));
        g.NewTurn();
        for (var i = 0; i < 50; i++) Assert.Null(g.Say(SoMoment.Guest, t.AddSeconds(19), rng));
        string? said = null;
        for (var i = 0; i < 50 && said is null; i++) said = g.Say(SoMoment.Guest, t.AddSeconds(21), rng);
        Assert.NotNull(said);
        Assert.Null(g.Say(SoMoment.Fern, t.AddSeconds(90), rng));   // уже сказав цього ходу
        // «Завжди» — і того ж ходу, і без паузи.
        foreach (var m in new[] { SoMoment.Bulava, SoMoment.LastRounds, SoMoment.Stall, SoMoment.Win, SoMoment.Tie, SoMoment.Away, SoMoment.Back })
            Assert.NotNull(g.Say(m, t.AddSeconds(22), rng));
        Assert.False(SorochyntsiGlek.IsAlways(SoMoment.Hoard));
        // «Майже булава» — щонайбільше раз на гравця.
        var vars = new Dictionary<string, string> { ["who"] = "Оля" };
        string? near = null;
        for (var i = 0; i < 50 && near is null; i++)
        {
            g.NewTurn();
            near = g.Say(SoMoment.NearBulava, t.AddMinutes(10 + i), rng, vars);
        }
        Assert.NotNull(near);
        for (var i = 0; i < 50; i++)
        {
            g.NewTurn();
            Assert.Null(g.Say(SoMoment.NearBulava, t.AddMinutes(100 + i), rng, vars));
        }
        // Та сама репліка двічі поспіль не звучить.
        Assert.NotEqual(g.Say(SoMoment.Away, t, rng), g.Say(SoMoment.Away, t, rng));
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
            SoRig.Step(h, 0);
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
            var h = SoRig.Table(1, bots: "3", seed: seed);
            var said = Play(h);
            var c = SoRig.CoreOf(h);
            Assert.All(said, s => Assert.DoesNotContain("{", s.Text));
            Assert.All(said, s => Assert.True(MomentOf(s.Text) is not null, $"не з книги: «{s.Text}»"));
            Assert.Equal(SoMoment.Start, MomentOf(said[0].Text));
            Assert.Single(said, s => MomentOf(s.Text) is SoMoment.Win or SoMoment.Tie or SoMoment.Stall);
            var end = said.Last();
            Assert.All(c.Winners, w => Assert.Contains(c.NameOf(w), end.Text));
            if (c.Bulava >= 0)
                Assert.Contains(said, s => MomentOf(s.Text) == SoMoment.Bulava && s.Text.Contains(c.NameOf(c.Bulava)));
            // Запобіжник: «звичайна» репліка — не раніше ніж за 20 с від попередньої будь-якої.
            DateTimeOffset? prev = null;
            foreach (var (at, text) in said)
            {
                var m = MomentOf(text)!.Value;
                if (!SorochyntsiGlek.IsAlways(m) && prev is { } p)
                    Assert.True(at - p >= SorochyntsiGlek.Cooldown, $"«{text}» лише через {(at - p).TotalSeconds} с");
                prev = at;
            }
            Assert.True(said.Count < 60, $"забагато реплік: {said.Count}");
            Assert.True(said.Count > 3, $"замало реплік: {said.Count}");
        }
    }

    [Fact]
    public void Glek_hears_a_guest_a_starshyna_and_a_blind_reserve_with_public_details_only()
    {
        var c = SorochyntsiBotTests.Core(2, 3);
        string Nick(int s) => s == 0 ? "Оля" : "Петро";
        var guest = new SoEvent { K = "guest", Data = [new("seat", 0), new("guest", 8)] };
        var heard = SorochyntsiLines.Hear(guest, c, Nick, false, false)!.Value;
        Assert.Equal(SoMoment.Guest, heard.M);
        Assert.Equal("Кобзар", heard.Vars["guest"]);
        var lost = SorochyntsiLines.Hear(new SoEvent { K = "starshyna", Data = [new("seat", 1), new("from", 0), new("icons", 4)] }, c, Nick, false, false)!.Value;
        Assert.Equal((SoMoment.StarshynaLost, "Петро", "Оля", "4"), (lost.M, lost.Vars["who"], lost.Vars["other"], lost.Vars["icons"]));
        var blind = new SoEvent { K = "reserve", Data = [new("seat", 1), new("tier", 3), new("slot", null), new("gold", 1)], SecretKey = "card", Secret = 77, Insiders = [1] };
        var b = SorochyntsiLines.Hear(blind, c, Nick, false, false)!.Value;
        Assert.Equal(SoMoment.BlindReserve, b.M);
        Assert.DoesNotContain(b.Vars.Values, v => v.Contains("Купальське"));
        var fern = SorochyntsiLines.Hear(new SoEvent { K = "buy", Data = [new("seat", 0), new("card", 88)] }, c, Nick, true, true)!.Value;
        Assert.Equal((SoMoment.Fern, "Чорт у корчмі"), (fern.M, fern.Vars["card"]));
        var first = SorochyntsiLines.Hear(new SoEvent { K = "buy", Data = [new("seat", 0), new("card", 8)] }, c, Nick, true, false)!.Value;
        Assert.Equal((SoMoment.FirstBuy, "рибу"), (first.M, first.Vars["good"]));
        Assert.Null(SorochyntsiLines.Hear(new SoEvent { K = "buy", Data = [new("seat", 0), new("card", 8)] }, c, Nick, false, false));
    }
}
