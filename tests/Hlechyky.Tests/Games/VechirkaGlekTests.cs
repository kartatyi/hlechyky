using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Вечірка S1.4: Дядько Глек (книга фраз, підстановки, темп, озвучка §13), корона «Голова вечірки» (§10),
/// стеля міні-гри (повільна гра доходить до Cap, вечір іде далі).
/// </summary>
public sealed class VechirkaGlekTests
{
    static Vechirka G(RoomHarness h) => (Vechirka)h.Room.Game;

    static bool Until(RoomHarness h, Func<bool> done, int maxSeconds)
    {
        for (var k = 0; k < maxSeconds * 50; k++) { if (done()) return true; h.Tick(); }
        return done();
    }

    static List<string> Dj(RoomHarness h) =>
        [.. h.Outbox.OfType<TableSaid>().Where(t => t.Line.Kind == "dj").Select(t => t.Line.Text)];

    static RoomHarness Voiced(FakeSvoyaVoice voice, string name = "ostap", int seed = 3)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ISvoyaVoice>(voice);
        var h = new RoomHarness("vechirka", new { len = "30", voice = name }, seed, sc.BuildServiceProvider());
        h.Join("Оля");
        for (var k = 0; k < 2; k++) Assert.True(h.Act(0, "bot", new { on = true }).Ok);
        return h;
    }

    // ---------- книга фраз ----------

    [Fact]
    public void Book_from_data_has_starred_lines_to_warm_and_only_allowed_slots()
    {
        var book = VechirkaLines.Book;
        foreach (var pool in VechirkaLines.Starred)
            Assert.True(book.Pool(pool).Count(l => !l.Contains('{')) >= 3, $"у ✱-пулі {pool} замало рядків без підстановок");
        foreach (var (pool, allowed) in VechirkaLines.Allowed)
            Assert.All(book.Pool(pool), l => Assert.All(VechirkaLines.Slots(l), x => Assert.Contains(x, allowed)));
        var warm = book.Warm(Vechirka.WarmMax);
        Assert.Equal(Vechirka.WarmMax, warm.Count);
        Assert.Equal(warm.Count, warm.Distinct().Count());
        Assert.DoesNotContain(warm, l => l.Contains('{'));
        Assert.Contains(warm[0], book.Pool("buy"));   // найчастіша ✱-репліка — перша в черзі
        Assert.True(book.Warm(5).Count == 5);
    }

    [Fact]
    public void Render_picks_only_lines_whose_slots_are_given()
    {
        var b = new VechirkaLines(new()
        {
            ["tie"] = ["Нічия! {nick} ділять перемогу.", "Порівну!"],
            ["bonus.steps"] = ["Глек за кроки — {nick}!", "«{title}» — нікому."],
        });
        for (var k = 0; k < 20; k++)
        {
            Assert.Equal("Порівну!", b.Render("tie", null));
            Assert.Equal("«Ходок» — нікому.", b.Render("bonus.steps", new Dictionary<string, string> { ["nick"] = "", ["title"] = "Ходок" }));
        }
        Assert.Null(b.Render("nosuch", null));
        Assert.Null(new VechirkaLines(new() { ["x"] = ["{nick}!"] }).Render("x", null));
    }

    // ---------- темп ----------

    [Fact]
    public void Ordinary_lines_and_minigame_lines_come_at_most_once_in_20_seconds()
    {
        var h = VechirkaTests.Table(2, 0);
        h.Start();
        var g = G(h);
        h.Clock.Advance(TimeSpan.FromSeconds(25));
        h.Tick();
        h.Clock.Advance(TimeSpan.FromSeconds(21));   // без тику: щойно сказане не глушить «паузу»
        var before = g.LastSay?.Id ?? 0;
        Assert.True(h.Act(0, "pause", new { on = true }).Ok);
        Assert.Equal(before + 1, g.LastSay!.Value.Id);
        Assert.True(h.Act(0, "pause", new { on = false }).Ok);
        Assert.Equal(before + 1, g.LastSay!.Value.Id);   // «граємо далі» за мить після «паузи» — промовчано

        Assert.True(g.SubSay("Крижина: хтось у воді!"));
        Assert.False(g.SubSay("Крижина: ще один!"));
        h.Clock.Advance(TimeSpan.FromSeconds(21));
        Assert.True(g.SubSay("Крижина: третій!"));
    }

    [Fact]
    public void Whole_evening_with_the_real_book_never_says_a_raw_slot()
    {
        var h = VechirkaTests.Table(1, 3, seed: 9);
        h.Start();
        var c = G(h).Core!;
        Assert.True(Until(h, () => h.Room.Status == RoomStatus.Finished, c.S.Rounds * 4 * 30 + c.S.Rounds * 140));
        var dj = Dj(h);
        Assert.True(dj.Count >= 5, $"Глек сказав лише {dj.Count}");
        Assert.DoesNotContain(dj, t => t.Contains('{') || t.Contains('}'));
    }

    // ---------- озвучка ----------

    [Fact]
    public void Voice_warms_starred_lines_and_fills_the_url_when_the_clip_is_ready()
    {
        var voice = new FakeSvoyaVoice(ready: false);
        var h = Voiced(voice);
        Assert.True(h.Start().Ok);
        var g = G(h);
        Assert.InRange(voice.Prepared.Count, 1, Vechirka.WarmMax);
        Assert.DoesNotContain(voice.Prepared, l => l.Contains('{'));

        h.Tick();
        var say = h.View(0).GetProperty("say");   // intro ✱ — прогрів ще не встиг: текст без звуку, рядок у чергу першим
        Assert.Equal(JsonValueKind.Null, say.GetProperty("url").ValueKind);
        Assert.Contains(say.GetProperty("text").GetString(), voice.Urgent);

        voice.Ready_ = true;
        h.Tick();
        Assert.StartsWith("/tts/", h.View(0).GetProperty("say").GetProperty("url").GetString());
        Assert.True(g.VoiceOn);
    }

    [Fact]
    public void A_clip_later_than_one_and_a_half_seconds_stays_text_only()
    {
        var voice = new FakeSvoyaVoice(ready: false);
        var h = Voiced(voice);
        h.Start();
        h.Tick();
        h.Clock.Advance(TimeSpan.FromMilliseconds(Vechirka.OnFlyMs + 100));
        h.Tick();
        voice.Ready_ = true;
        var id = h.View(0).GetProperty("say").GetProperty("id").GetInt32();
        h.Tick();
        var say = h.View(0).GetProperty("say");
        if (say.GetProperty("id").GetInt32() == id) Assert.Equal(JsonValueKind.Null, say.GetProperty("url").ValueKind);
    }

    [Fact]
    public void Without_voice_nothing_is_prepared_and_there_is_no_url()
    {
        var voice = new FakeSvoyaVoice();
        var h = Voiced(voice, "none");
        h.Start();
        h.Tick();
        Assert.False(G(h).VoiceOn);
        Assert.Empty(voice.Prepared);
        Assert.Empty(voice.Urgent);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("say").GetProperty("url").ValueKind);
    }

    // ---------- корона ----------

    [Fact]
    public async Task Crown_goes_to_the_winner_of_an_evening_of_three_humans()
    {
        var h = VechirkaTests.Table(3, 0, seed: 6);
        var keeper = new VechirkaSetup.CrownKeeper(h.Events, h.Store);
        await keeper.StartAsync(default);
        h.Start();
        var c = G(h).Core!;
        Assert.True(Until(h, () => h.Room.Status == RoomStatus.Finished, c.S.Rounds * 3 * 30 + c.S.Rounds * 140));
        var crown = h.Store.LoadState(VechirkaSetup.CrownKey);
        Assert.NotNull(crown);
        var nicks = JsonDocument.Parse(crown).RootElement.GetProperty("nicks").EnumerateArray().Select(x => x.GetString()).ToList();
        var winners = Enumerable.Range(0, c.N).Where(i => c.PlaceOf(i) == 1).Select(i => c[i].Nick).ToList();
        Assert.Equal(winners.OrderBy(x => x), nicks.OrderBy(x => x));
        Assert.Contains(h.Awards, a => a.Reason == "ach:vechirka-win");
        await keeper.StopAsync(default);
    }

    [Fact]
    public async Task Two_humans_do_not_take_the_crown()
    {
        var h = VechirkaTests.Table(2, 2, seed: 6);
        var keeper = new VechirkaSetup.CrownKeeper(h.Events, h.Store);
        await keeper.StartAsync(default);
        h.Start();
        var c = G(h).Core!;
        Assert.True(Until(h, () => h.Room.Status == RoomStatus.Finished, c.S.Rounds * 4 * 30 + c.S.Rounds * 140));
        Assert.Null(h.Store.LoadState(VechirkaSetup.CrownKey));
    }

    // ---------- стеля міні-гри ----------

    [Fact]
    public void A_minigame_that_never_ends_is_capped_and_the_evening_goes_on()
    {
        var h = VechirkaTests.Table(1, 2, seed: 5);
        var g = G(h);
        g.PoolFactory = () => [new VechirkaPoolEntry(SlowGame.Id, "Повільна", "", "tap", 0, 1, 2, 8, SlowGame.CapMs)];
        g.RunnerFactory = (game, _) => new SlowRunner(game);
        h.Start();
        var c = g.Core!;
        Assert.True(Until(h, () => c.S.Phase == "mg", 600));
        var started = h.Clock.UtcNow;
        Assert.True(Until(h, () => c.S.Phase == "results", 60));
        Assert.Equal("Cap", c.S.M!.How);
        Assert.InRange((h.Clock.UtcNow - started).TotalMilliseconds, SlowGame.CapMs - 100, SlowGame.CapMs + 2000);
        // scores зі стелі: місце 0 — найкраще (PartyScores дає більше меншим місцям)
        Assert.Equal(c.S.M.Results!.Max(r => r.Coins), c.S.M.Results!.First(r => r.I == c.S.M.Seats[0]).Coins);
        Assert.True(Until(h, () => c.S.Phase == "turn", 30));
    }

    /// <summary>Міні-гра, що сама не кінчається ніколи: лише стеля каркаса її зупиняє.</summary>
    sealed class SlowGame : Game, IPartyMinigame
    {
        public const string Id = "t-slow";
        public const int CapMs = 3_000;
        public override GameInfo Info { get; } = new(Id, "Повільна", "повільну", GameGroup.Party, 2, 8, TickMs: 20);
        public override void Start() { }
        public override object View(int? seat) => new { slow = true };
        public string Howto => "Чекай.";
        public int PartyCapMs => CapMs;
        public int PartyMin => 2;
        public int PartyMax => 8;
        public IReadOnlyDictionary<int, long> PartyScores() =>
            Enumerable.Range(0, Ctx.Players).ToDictionary(s => s, s => (long)(100 - s));
    }

    sealed class SlowRunner(Vechirka game) : IMgRunner
    {
        MinigameHost? _host;
        public bool Begin(string id, int[] pSeats, bool[] bot, LiveBots.Level level)
        {
            var ctx = new VechirkaMgCtx(game, 7);
            var seats = pSeats.Select((p, k) => bot[k] ? PartySeat.BotAt(p) : PartySeat.Human(p)).ToArray();
            _host = new MinigameHost(new SlowGame(), ctx, seats, level);
            _host.Start();
            return true;
        }
        public string Title => _host?.Title ?? "";
        public string Howto => _host?.Howto ?? "";
        public int CapMs => _host?.CapMs ?? 0;
        public ActResult Act(int p, string action, JsonElement payload) => _host!.Act(p, action, payload);
        public TickResult Tick() => _host?.Tick() ?? TickResult.None;
        public object? View(int? p) => _host?.View(p);
        public object? Frame() => _host?.Frame();
        public string[] Names() => _host?.Names() ?? [];
        public string[] SeatNames() => _host?.SeatNames() ?? [];
        public MinigameResult? Result => _host?.Result;
    }
}
