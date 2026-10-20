using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Де це?», пакет 4: 🤝 виклик «Зіграй мої 5 фото» — ті самі місця й фото, автор свого не грає, відповіді не
/// просочуються до розкриття, одна спроба, продовження початого, порівняння по раундах і рядки в Журнал.
/// </summary>
public sealed class GeoChallengeTests
{
    sealed class World : IDisposable
    {
        public GeoCache Cache { get; } = new();
        public GeoChallenges Ch { get; } = new(null);
        public IServiceProvider Services =>
            new ServiceCollection().AddSingleton(Cache.Photos).AddSingleton(Ch).BuildServiceProvider();
        public void Dispose() => Cache.Dispose();

        public RoomHarness Open(string game, string nick)
        {
            var h = new RoomHarness(game, seed: 42, services: Services);
            Assert.True(h.Solo(nick).Ok, h.Reply.Message);
            return h;
        }
    }

    /// <summary>Дограти п'ять фото: шпилька в <paramref name="aim"/> від правди; повертає (місце, шлях фото) по раундах.</summary>
    static List<(string Place, string Path)> Play(RoomHarness h, GeoCache c, int rounds, Func<int, (int X, int Y), (int X, int Y)> aim,
        Action<int>? beforeGuess = null)
    {
        var seen = new List<(string, string)>();
        for (var r = 0; r < rounds; r++)
        {
            GeoTests.Until(h, GeoMatch.PhaseGuess);
            beforeGuess?.Invoke(r);
            var p = GeoTests.Current(h, c);
            var url = h.View(0).GetProperty("photo").GetString()!;
            seen.Add((p.Id, c.Photos.Resolve(url["/api/games/geo/".Length..^4])!));
            var (x, y) = aim(r, GeoTests.Truth(h, c));
            Assert.True(h.Act(0, "guess", new { x, y }).Ok);
            Assert.True(h.Act(0, "ready").Ok);
            GeoTests.Until(h, GeoMatch.PhaseReveal);
            h.Act(0, "next");
            h.Tick(1);
        }
        return seen;
    }

    static JsonElement Ch(RoomHarness h) => h.View(0).GetProperty("ch");

    static (RoomHarness H, List<(string Place, string Path)> Deal, string Id) Author(World w)
    {
        var a = w.Open("geo-solo", "Оля");
        var deal = Play(a, w.Cache, 5, (r, t) => (t.X + 3 * r, t.Y));
        Assert.Equal(GeoMatch.PhaseDone, GeoTests.Phase(a));
        Assert.True(Ch(a).GetProperty("can").GetBoolean());
        var res = a.Act(0, GeoMatch.ChallengeAct);
        Assert.True(res.Ok, res.Message);
        Assert.Equal(GeoMatch.ChallengeMade, res.Message);
        return (a, deal, w.Ch.Mine("Оля", a.Clock.UtcNow).Single().Id);
    }

    [Fact]
    public void Author_throws_a_challenge_once_and_the_journal_hears_it()
    {
        using var w = new World();
        var (a, deal, id) = Author(w);
        var ch = w.Ch.Find(id)!;
        Assert.Equal(deal.Select(d => d.Place), ch.Rounds.Select(r => r.Place));
        Assert.Equal(a.View(0).GetProperty("scores")[0].GetInt64(), ch.Total);
        Assert.Contains(a.Outbox.OfType<Journal>(), j => j.Text.Contains("Оля кидає виклик: зіграй мої 5 фото"));
        Assert.False(Ch(a).GetProperty("can").GetBoolean());
        Assert.False(a.Act(0, GeoMatch.ChallengeAct).Ok);
        // недограна партія — нічого кидати
        var b = w.Open("geo-solo", "Петро");
        Assert.False(b.Act(0, GeoMatch.ChallengeAct).Ok);
    }

    [Fact]
    public void Friend_plays_exactly_the_same_photos_and_answers_do_not_leak_before_reveal()
    {
        using var w = new World();
        var (_, deal, id) = Author(w);
        var f = w.Open("geo-challenge", "Петро");
        var pick = Ch(f);
        Assert.True(pick.GetProperty("pick").GetBoolean());
        var item = pick.GetProperty("list").EnumerateArray().Single();
        Assert.Equal("Оля", item.GetProperty("by").GetString());
        Assert.Equal("new", item.GetProperty("state").GetString());
        var names = deal.Select(d => w.Cache.Bank.Places.First(p => p.Id == d.Place).Name).ToList();
        foreach (var n in names) Assert.DoesNotContain(n, f.View(0).GetRawText());

        Assert.True(f.Act(0, GeoChallenge.Take, new { id }).Ok);
        Assert.False(f.Act(0, GeoChallenge.Take, new { id }).Ok);   // уже граю
        var got = Play(f, w.Cache, 5, (r, t) => (t.X + 40, t.Y), r =>
        {
            // автор — лише по вже розкритих раундах; назви місця раунду наперед нема
            var v = f.View(0);
            Assert.Equal(r, v.GetProperty("ch").GetProperty("author").GetArrayLength());
            Assert.DoesNotContain(names[r], v.GetRawText());
            Assert.Equal(JsonValueKind.Null, v.GetProperty("ch").GetProperty("total").ValueKind);
        });
        Assert.Equal(deal, got);
        Assert.Equal(GeoMatch.PhaseDone, GeoTests.Phase(f));
        var done = Ch(f);
        Assert.Equal(5, done.GetProperty("author").GetArrayLength());
        Assert.Equal(w.Ch.Find(id)!.Total, done.GetProperty("total").GetInt64());
        Assert.Equal(5, done.GetProperty("cmp").GetProperty("names").GetArrayLength());
        Assert.Empty(f.Scores);   // виклики — не в таблицю рекордів
        var mine = f.View(0).GetProperty("scores")[0].GetInt64();
        Assert.Contains(f.Outbox.OfType<Journal>(), j => j.Text.StartsWith("📍 Виклик «Де це?»: Петро ", StringComparison.Ordinal)
            && j.Text.Contains(GeoText.Num(mine)) && j.Text.EndsWith(GeoText.Num(w.Ch.Find(id)!.Total) + " Оля", StringComparison.Ordinal));
        var t = w.Ch.Find(id)!.TryOf("петро")!;
        Assert.True(t.Done);
        Assert.Equal(5, t.Points.Length);
        Assert.Equal(mine, t.Total);
    }

    [Fact]
    public void One_attempt_the_author_cannot_play_and_others_see_the_comparison()
    {
        using var w = new World();
        var (_, _, id) = Author(w);
        var own = w.Open("geo-challenge", "Оля");
        Assert.Empty(Ch(own).GetProperty("list").EnumerateArray());
        Assert.Equal(GeoChallenges.Own, own.Act(0, GeoChallenge.Take, new { id }).Message);

        var f = w.Open("geo-challenge", "Петро");
        Assert.True(f.Act(0, GeoChallenge.Take, new { id }).Ok);
        Play(f, w.Cache, 5, (r, t) => (t.X, t.Y + 10));
        Assert.True(f.Rematch().Ok, f.Reply.Message);
        var again = Ch(f);
        Assert.True(again.GetProperty("pick").GetBoolean());
        Assert.Equal("done", again.GetProperty("list")[0].GetProperty("state").GetString());
        Assert.Equal(GeoChallenges.Played, f.Act(0, GeoChallenge.Take, new { id }).Message);

        var g = w.Open("geo-challenge", "Ганна");
        Assert.True(g.Act(0, GeoChallenge.Take, new { id }).Ok);
        Play(g, w.Cache, 5, (r, t) => (t.X + 200, t.Y));
        var rows = Ch(g).GetProperty("cmp").GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal("Петро", Assert.Single(rows).GetProperty("nick").GetString());
        Assert.Equal(5, rows[0].GetProperty("points").GetArrayLength());
        // автор бачить, хто зіграв
        var mine = Ch(own).GetProperty("mine")[0].GetProperty("tries").EnumerateArray().Select(e => e.GetProperty("nick").GetString()).ToList();
        Assert.Equal(["Ганна", "Петро"], mine.Order().ToList());
    }

    [Fact]
    public void Closed_tab_mid_challenge_continues_from_the_next_photo()
    {
        using var w = new World();
        var (_, deal, id) = Author(w);
        var f = w.Open("geo-challenge", "Петро");
        Assert.True(f.Act(0, GeoChallenge.Take, new { id }).Ok);
        Play(f, w.Cache, 2, (r, t) => (t.X, t.Y));
        GeoTests.Until(f, GeoMatch.PhaseGuess);
        // вкладку закрито: нова кімната (інший процес каркаса), той самий сервіс викликів
        var back = w.Open("geo-challenge", "Петро");
        var v = back.View(0);
        Assert.Equal(3, v.GetProperty("round").GetInt32());
        Assert.Equal(2, v.GetProperty("ch").GetProperty("author").GetArrayLength());
        var rest = Play(back, w.Cache, 3, (r, t) => (t.X, t.Y));
        Assert.Equal(deal.Skip(2), rest);
        Assert.True(w.Ch.Find(id)!.TryOf("петро")!.Done);
        Assert.Equal(5, w.Ch.Find(id)!.TryOf("петро")!.Points.Length);
    }

    [Fact]
    public void Old_challenges_drop_off_the_list_and_daily_limit_holds()
    {
        var svc = new GeoChallenges(null);
        var now = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        GeoChRound[] rounds = [new("g0001", 0, 1, 5000)];
        var (old, _) = svc.Add("Оля", rounds, null, now.AddDays(-15));
        for (var i = 0; i < GeoChallenges.PerDay; i++) Assert.NotNull(svc.Add("Оля", rounds, null, now).Ch);
        Assert.Equal(GeoChallenges.TooMany, svc.Add("Оля", rounds, null, now).Error);
        Assert.DoesNotContain(svc.For("Петро", now), c => c.Id == old!.Id);
        Assert.Equal(GeoChallenges.Gone, svc.Begin(old!.Id, "Петро", now).Error);
        Assert.Equal(GeoChallenges.PerDay, svc.For("Петро", now).Count);
    }

    [Fact]
    public async Task Challenges_survive_a_restart_through_the_database()
    {
        var dir = Path.Combine(Path.GetTempPath(), "geo-ch-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try
        {
            var db = new Db(Path.Combine(dir, "t.db"));
            var svc = new GeoChallenges(db);
            await svc.Loaded;
            var now = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
            var ch = svc.Add("Оля", [new("g0001", 0, 12.5, 4700), new("g0002", 1, null, 0)], null, now).Ch!;
            svc.Begin(ch.Id, "Петро", now);
            svc.Progress(ch.Id, "петро", 3.2, 4900);
            // записи фонові — чекаємо, поки остання зміна ляже в базу
            for (var i = 0; i < 100; i++)
            {
                var probe = new GeoChallenges(db);
                await probe.Loaded;
                if (probe.Find(ch.Id)?.TryOf("петро")?.Points.Length == 1) break;
                await Task.Delay(50);
            }
            var again = new GeoChallenges(db);
            await again.Loaded;
            var back = again.Find(ch.Id)!;
            Assert.Equal("Оля", back.Nick);
            Assert.Equal(4700, back.Total);
            Assert.Null(back.Rounds[1].Km);
            Assert.Equal([4900], back.TryOf("петро")!.Points);
            Assert.Equal(ch.Id, again.Unfinished("Петро")!.Id);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Guest_sees_no_challenge_button_and_cannot_throw()
    {
        using var w = new World();
        var a = w.Open("geo-solo", "гість Оля");
        Play(a, w.Cache, 5, (r, t) => t);
        Assert.Equal(GeoMatch.PhaseDone, GeoTests.Phase(a));
        Assert.Equal(JsonValueKind.Null, Ch(a).ValueKind);
        Assert.False(a.Act(0, GeoMatch.ChallengeAct).Ok);
    }
}
