using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Каркас міні-ігор вечірки (docs/games/specs/party-minigame.md): контекст підгри, хост (тики, стеля, винятки),
/// Крижина в режимі вечірки й стенд <c>mgprobe</c> через справжню кімнату.
/// </summary>
[Collection(SerialPerf.Name)]
public class PartyTests
{
    /// <summary>
    /// Заглушка міні-гри. Конструктор з параметром — щоб реєстр тестів її не підхопив як справжню гру.
    /// Рахує тики; «win» — Finish з очками, «award» — спроба нагороди, «boom» — виняток, «nope» — GameError.
    /// </summary>
    sealed class Fake(int tickMs, int capMs = 5000) : Game, IPartyMinigame
    {
        public override GameInfo Info { get; } = new("fake-mg", "Заглушка", "заглушку", GameGroup.Live, 2, 8, TickMs: tickMs);
        public int Ticks;
        public bool BoomOnTick;
        public PartyMode? Mode;
        public string Howto => "Тисни що хочеш";
        public int PartyCapMs => capMs;
        public int PartyMin => 2;
        public int PartyMax => 8;
        public IReadOnlyDictionary<int, long> PartyScores() =>
            Enumerable.Range(0, Ctx.Players).ToDictionary(s => s, s => (long)(s == 1 ? 7 : 1));

        public override void Configure(IReadOnlyDictionary<string, string> options) => Mode = PartyMode.Read(options);
        public override void Start() { Ctx.Log("заглушка: почали"); Ctx.Say("Глек: гайда"); }
        public override TickResult Tick()
        {
            if (BoomOnTick) throw new InvalidOperationException("бах");
            Ticks++;
            return TickResult.FrameOnly;
        }
        public override ActResult Act(int seat, string action, JsonElement payload)
        {
            switch (action)
            {
                case "win":
                    Ctx.Finish([seat], "заглушка: кінець", new Dictionary<int, long> { [0] = 3, [1] = 3, [2] = 1 });
                    Ctx.Finish([], "другий Finish — мимо");
                    return ActResult.Done;
                case "award":
                    Ctx.Award(seat, 5, "ach:test");
                    Ctx.Score(seat, 1);
                    return ActResult.Done;
                case "boom": throw new InvalidOperationException("бах");
                case "nope": throw new GameError("так не можна");
                default: return ActResult.Fail("Тут так не ходять");
            }
        }
        public override object View(int? seat) => new { seat, ticks = Ticks, nick = seat is { } s ? Ctx.NickOf(s) : null };
    }

    [Fact]
    public void Party_mode_reads_options()
    {
        Assert.Null(PartyMode.Read(new Dictionary<string, string> { ["bots"] = "1" }));
        var m = PartyMode.Read(new Dictionary<string, string> { ["party"] = "1", ["bots"] = "3,1,x,1", ["botlvl"] = "hard" })!;
        Assert.Equal([1, 3], m.Bots);
        Assert.Equal(LiveBots.Level.Hard, m.Level);
        Assert.True(m.IsBot(3));
        Assert.Empty(PartyMode.Read(new Dictionary<string, string> { ["party"] = "1" })!.Bots);
    }

    [Fact]
    public void Sub_context_maps_seats_mutes_rewards_and_intercepts_finish()
    {
        var g = new Fake(50);
        var h = new PartyHarness(g, humans: 2, bots: 1);
        h.Start();
        Assert.NotNull(g.Mode);
        Assert.Equal([2], g.Mode!.Bots);
        Assert.Equal(3, h.Ctx.Players);
        Assert.True(h.Ctx.Seated(0));
        Assert.False(h.Ctx.Seated(2));            // бот — не «сидить»
        Assert.Null(h.Ctx.NickOf(2));
        Assert.Equal(0, h.Ctx.HostSeat);
        Assert.Contains("заглушка: почали", h.Parent.Logs);
        Assert.Contains("Глек: гайда", h.Parent.Says);

        Assert.True(h.Act(1, "award").Ok);
        Assert.Equal(2, h.Ctx.Muted);
        Assert.Equal(0, h.Parent.Leaked);

        Assert.True(h.Act(1, "win").Ok);
        Assert.Equal(0, h.Parent.Finishes);       // до кімнати Finish не дійшов
        var r = h.Result!;
        Assert.Equal(MinigameEnd.Finished, r.How);
        Assert.Equal([0, 1], r.Winners);          // переможці — за scores, а не за winners гри
        Assert.Equal([1, 1, 3], r.Places);
        Assert.Equal("заглушка: кінець", r.Log);
        Assert.False(h.Act(0, "win").Ok);         // міні-гру зіграно — більше не ходять
    }

    [Fact]
    public void Only_humans_of_the_minigame_can_act()
    {
        var h = new PartyHarness(new Fake(50), humans: 1, bots: 2);
        h.Start();
        Assert.False(h.Host.Act(5, "win", default).Ok);
        Assert.False(h.Host.Act(-1, "win", default).Ok);
        Assert.Null(h.Result);
        var nope = h.Act(0, "nope");
        Assert.False(nope.Ok);
        Assert.Equal("так не можна", nope.Message);
        Assert.Null(h.Result);                    // GameError — чесна відмова, не поломка
    }

    [Fact]
    public void Host_ticks_subgame_with_its_own_step_and_does_not_catch_up_much()
    {
        var g = new Fake(60);
        var h = new PartyHarness(g, humans: 2, bots: 0);
        h.Start();
        h.Tick(150);                              // 3000 мс кроками батька по 20
        Assert.Equal(50, g.Ticks);
        var before = g.Ticks;
        h.Clock.AdvanceMs(1000);                  // сервер «заснув» на секунду
        h.Host.Tick();
        Assert.Equal(before + MinigameHost.MaxCatchUp, g.Ticks);
        h.Tick(3);                                // далі — рівно своїм кроком, без хвоста
        Assert.Equal(before + MinigameHost.MaxCatchUp + 1, g.Ticks);
    }

    [Fact]
    public void Cap_ends_with_party_scores()
    {
        var g = new Fake(40, capMs: 2000);
        var h = new PartyHarness(g, humans: 2, bots: 2);
        h.Start();
        var r = h.RunToEnd();
        Assert.NotNull(r);
        Assert.Equal(MinigameEnd.Cap, r!.How);
        Assert.Equal([1], r.Winners);
        Assert.Equal(4, r.Scores.Count);
        Assert.True(h.Clock.UtcNow - h.StartedAt <= TimeSpan.FromMilliseconds(2000 + PartyHarness.ParentTickMs));
        var ticks = g.Ticks;
        h.Tick(10);
        Assert.Equal(ticks, g.Ticks);             // після кінця підгру вже не тикають
    }

    [Fact]
    public void Turn_based_subgame_is_not_ticked_but_still_capped()
    {
        var g = new Fake(0, capMs: 1500);
        var h = new PartyHarness(g, humans: 2, bots: 0);
        h.Start();
        var r = h.RunToEnd();
        Assert.Equal(0, g.Ticks);
        Assert.Equal(MinigameEnd.Cap, r!.How);
    }

    [Fact]
    public void Cap_never_exceeds_two_minutes()
    {
        var h = new PartyHarness(new Fake(40, capMs: 10 * 60_000), humans: 2, bots: 0);
        Assert.Equal(MinigameHost.MaxCapMs, h.Host.CapMs);
    }

    [Fact]
    public void Crash_in_subgame_gives_even_result_and_party_lives_on()
    {
        var g = new Fake(40) { BoomOnTick = true };
        var h = new PartyHarness(g, humans: 2, bots: 1);
        h.Start();
        h.Tick(2);
        var r = h.Result!;
        Assert.Equal(MinigameEnd.Crash, r.How);
        Assert.Equal([1, 1, 1], r.Places);
        Assert.Equal([0, 1, 2], r.Winners);

        var h2 = new PartyHarness(new Fake(40), humans: 2, bots: 0);
        h2.Start();
        Assert.False(h2.Act(0, "boom").Ok);
        Assert.Equal(MinigameEnd.Crash, h2.Result!.How);
        Assert.Equal(0, h2.Parent.Finishes);
    }

    [Fact]
    public void Result_places_share_ties_and_fill_missing_seats()
    {
        var r = MinigameResult.From(4, new Dictionary<int, long> { [0] = 5, [1] = 9, [2] = 5 }, "", null, MinigameEnd.Finished);
        Assert.Equal([2, 1, 2, 4], r.Places);    // seat 3 без score → 0 → останнє
        Assert.Equal([1], r.Winners);
        Assert.Equal(0, r.Scores[3]);
    }

    [Fact]
    public void Pool_has_icefloe_and_not_ordinary_games()
    {
        Assert.True(PartyPool.Has("icefloe"));
        Assert.False(PartyPool.Has("chess"));
        Assert.False(PartyPool.Has("mgprobe"));
        Assert.All(PartyPool.Games, g => Assert.True(PartyPool.Create(g.Id) is IPartyMinigame));
    }

    // ---------- Крижина в режимі вечірки ----------

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(2)]
    public void Icefloe_party_with_only_bots_ends_before_cap_with_scores_for_all(int bots)
    {
        var h = new PartyHarness("icefloe", humans: 0, bots: bots, seed: 7);
        h.Start();
        var r = h.RunToEnd();
        Assert.NotNull(r);
        Assert.Equal(MinigameEnd.Finished, r!.How);
        Assert.Equal(bots, r.Scores.Count);
        Assert.Equal(bots, r.Places.Length);
        Assert.NotEmpty(r.Winners);
        Assert.True(h.Clock.UtcNow - h.StartedAt <= TimeSpan.FromMilliseconds(h.Host.CapMs));
        Assert.Equal(0, h.Ctx.Muted);             // ні ачівок, ні рекордів
        Assert.Equal(0, h.Parent.Leaked);
        Assert.Equal(0, h.Parent.Finishes);
        // Місця — за тим, хто довше протримався: переможець не падав раніше за інших.
        var ice = (Icefloe)h.Game;
        Assert.Equal(1, ice.RoundNo);
    }

    [Fact]
    public void Icefloe_party_idle_human_does_not_stall_and_bots_play()
    {
        var h = new PartyHarness("icefloe", humans: 2, bots: 2, level: LiveBots.Level.Hard, seed: 3);
        h.Start();
        var ice = (Icefloe)h.Game;
        Assert.Equal([2, 3], ice.Bots);
        Assert.True(ice.BotGame);
        Assert.Equal(1, ice.Need);
        var r = h.RunToEnd();
        Assert.NotNull(r);
        Assert.Equal(4, r!.Scores.Count);
        // двоє людей стоять — сильні боти їх виштовхують, тож вгорі бот
        Assert.Contains(r.Winners, w => w >= 2);
    }

    [Fact]
    public void Icefloe_party_is_deterministic_by_seed()
    {
        string Run()
        {
            var h = new PartyHarness("icefloe", humans: 0, bots: 5, seed: 11);
            h.Start();
            var r = h.RunToEnd()!;
            return string.Join(",", r.Places) + "|" + h.Clock.UtcNow.ToUnixTimeMilliseconds();
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Icefloe_party_cap_scores_rank_survivors_above_fallen()
    {
        var h = new PartyHarness("icefloe", humans: 3, bots: 0);
        h.Start();
        h.Tick(200);
        var s = ((IPartyMinigame)h.Game).PartyScores();
        Assert.Equal(3, s.Count);
        Assert.All(s.Values, v => Assert.Equal(s[0], v));   // ніхто не впав — усі рівні
        Assert.Contains("Виштовхни", h.Host.Howto);
    }

    [Fact]
    public void Icefloe_ordinary_options_ignore_party_keys_in_lobby()
    {
        // party/bots у звичайному столі відкидає Rooms.Effective — Крижина лишається звичайною
        var room = new RoomHarness("icefloe", options: new { party = "1", bots = "1,2" });
        room.Join("Оля");
        Assert.False(((Icefloe)room.Room.Game).Party);
    }

    [Fact, Trait("Category", "Perf")]
    public void Host_1000_parent_ticks_with_8_icefloe_bots_under_2s()
    {
        var h = new PartyHarness("icefloe", humans: 0, bots: 8, seed: 5);
        h.Start();
        var sw = Stopwatch.StartNew();
        h.Tick(1000);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} мс");
    }

    // ---------- mgprobe через справжню кімнату ----------

    static RoomHarness Probe(int bots = 2)
    {
        var h = new RoomHarness("mgprobe", options: new { game = "icefloe", bots = bots.ToString() }, seed: 9);
        h.Join("Оля");
        h.Join("Петро");
        return h;
    }

    [Fact]
    public void Mgprobe_is_unlisted_and_offers_pool_games()
    {
        var reg = RoomHarness.NewRegistry();
        Assert.True(reg.Catalog.Single(g => g.Id == "mgprobe").Unlisted);
        Assert.False(reg.Catalog.Single(g => g.Id == "icefloe").Unlisted);
        var opt = reg.Info("mgprobe")!.Options!.Single(o => o.Key == "game");
        Assert.Contains(opt.Values, v => v.Value == "icefloe");
    }

    [Fact]
    public void Mgprobe_runs_minigame_shows_places_and_plays_again()
    {
        var h = Probe();
        Assert.True(h.Start().Ok);
        var v = h.View(0);
        Assert.Equal("howto", v.GetProperty("phase").GetString());
        Assert.Contains("Виштовхни", v.GetProperty("howto").GetString());
        Assert.Equal(4, v.GetProperty("n").GetInt32());
        Assert.False(h.Act(0, "mg", new { a = "dash" }).Ok);   // ще картка правил

        h.Tick(MgProbe.HowtoMs / MgProbe.TickMillis + 1);
        v = h.View(1);
        Assert.Equal("play", v.GetProperty("phase").GetString());
        Assert.Equal(1, v.GetProperty("sub").GetInt32());
        h.Input(0, "mg", new { a = "move", p = new { a = 4 } });
        Assert.False(h.Act(0, "mg", new { a = "fly" }).Ok);
        Assert.False(h.Act(0, "mg", new { x = 1 }).Ok);

        var probe = (MgProbe)h.Room.Game;
        for (var i = 0; i < 120_000 / MgProbe.TickMillis && probe.Phase != "result"; i++) h.Tick();
        Assert.Equal("result", probe.Phase);
        v = h.View(0);
        Assert.Equal(4, v.GetProperty("result").GetProperty("places").GetArrayLength());
        Assert.Empty(h.Finished);                 // стенд не кінчає партію кімнати: ні черепків, ні таблиць
        Assert.Contains(h.Outbox.OfType<TableSaid>(), s => s.Line.Text.Contains("🎯"));

        Assert.False(h.Act(1, "again").Ok);       // не господар
        Assert.False(h.Act(0, "again", new { game = "chess" }).Ok);
        Assert.True(h.Act(0, "again").Ok);
        Assert.Equal("howto", probe.Phase);
        Assert.Equal(2, h.View(0).GetProperty("round").GetInt32());
    }

    [Fact]
    public void Mgprobe_tops_up_bots_to_minimum_and_falls_back_on_unknown_game()
    {
        var h = new RoomHarness("mgprobe", options: new { game = "icefloe", bots = "0" });
        h.Join("Оля");
        h.Start();
        Assert.Equal(2, h.View(0).GetProperty("n").GetInt32());   // одна людина + бот до PartyMin
        // чужого id нема серед значень опції — кімната бере типову міні-гру з пулу
        var other = new RoomHarness("mgprobe", options: new { game = "chess" });
        Assert.True(other.Join("Оля").Ok);
        other.Start();
        Assert.Equal(PartyPool.Games[0].Id, other.View(0).GetProperty("game").GetString());
    }
}
