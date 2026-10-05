using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Скирта: рух і посадку снопа — на голому <see cref="SkyrtaCore"/>, партію, вітер, кінці, ботів і режим вечірки —
/// через кімнату (<see cref="RoomHarness"/>) і хост вечірки (<see cref="PartyHarness"/>), як гратимуть люди.
/// </summary>
[Collection(SerialPerf.Name)]
public class SkyrtaTests(ITestOutputHelper output)
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марко", "Соня", "Тарас", "Леся"];

    // ---------- підмостки ----------

    static RoomHarness Table(int players = 2, int seed = 42)
    {
        var h = new RoomHarness("skyrta", null, seed);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static RoomHarness SoloWithBots(LiveBots.Level level = LiveBots.Level.Normal, int seed = 42)
    {
        var h = new RoomHarness("skyrta", new { botlvl = LiveBots.Key(level) }, seed);
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static Skyrta G(RoomHarness h) => (Skyrta)h.Room.Game;

    static void ToGo(RoomHarness h)
    {
        for (var i = 0; i < 200 && G(h).Phase != Skyrta.PhGo; i++) h.Tick();
        Assert.Equal(Skyrta.PhGo, G(h).Phase);
    }

    /// <summary>
    /// Гравець, що кладе ідеально: шукає ту саму мить, що й бот (<see cref="SkyrtaBot.Aim"/>), і тапає в перший
    /// тик після неї — мить тапу в межах тика, тож сервер її не притискає.
    /// </summary>
    sealed class Ace(int seat)
    {
        int _n = -1, _ver = -1, _t;

        public void Step(Skyrta g, Func<int, int, ActResult> tap)
        {
            var st = g.Stacks[seat];
            if (g.Phase != Skyrta.PhGo || st.DoneAt >= 0) return;
            var now = g.Now();
            if (now < st.S) return;
            if (_n != st.N || _ver != st.WindVer)
            {
                _n = st.N;
                _ver = st.WindVer;
                _t = SkyrtaBot.Aim(st, Math.Max(0, now - st.S));
            }
            if (now >= st.S + _t) Assert.True(tap(st.N, _t).Ok);
        }
    }

    static Func<int, int, ActResult> Tapper(RoomHarness h, int seat) => (n, t) => h.Act(seat, "tap", new { n, t });

    /// <summary>Тапнути сніп зараз (мить = годинник гри) — куди вже вийде.</summary>
    static ActResult TapNow(RoomHarness h, int seat)
    {
        var st = G(h).Stacks[seat];
        return h.Act(seat, "tap", new { n = st.N, t = Math.Max(0, G(h).Now() - st.S) });
    }

    static void TickUntilDone(RoomHarness h, int max, Action? each = null)
    {
        for (var i = 0; i < max && h.Room.Status == RoomStatus.Playing; i++) { each?.Invoke(); h.Tick(); }
    }

    // ---------- рушій ----------

    [Fact]
    public void Sheaf_starts_at_its_side_and_bounces_between_lo_and_hi()
    {
        var v = SkyrtaCore.Speed(0);
        Assert.Equal(SkyrtaCore.Lo, SkyrtaCore.Center(0, v, true, false, 0, []));
        Assert.Equal(SkyrtaCore.Hi, SkyrtaCore.Center(0, v, false, false, 0, []));
        // Час на один прохід поля — і сніп біля протилежного краю, ще стільки ж — знову вдома.
        var pass = SkyrtaCore.Span * 1000 / v;
        Assert.InRange(SkyrtaCore.Center(pass, v, true, false, 0, []), SkyrtaCore.Hi - 2, SkyrtaCore.Hi);
        Assert.InRange(SkyrtaCore.Center(2 * pass + 2, v, true, false, 0, []), SkyrtaCore.Lo, SkyrtaCore.Lo + 2);
        for (var t = 0; t < 20_000; t += 37)
            Assert.InRange(SkyrtaCore.Center(t, SkyrtaCore.Vmax, t % 2 == 0, false, 0, []), SkyrtaCore.Lo, SkyrtaCore.Hi);
    }

    [Fact]
    public void Speed_grows_with_height_up_to_the_ceiling_and_tolerance_grows_with_speed()
    {
        Assert.Equal(SkyrtaCore.V0, SkyrtaCore.Speed(0));
        Assert.True(SkyrtaCore.Speed(10) > SkyrtaCore.Speed(5));
        Assert.Equal(SkyrtaCore.Vmax, SkyrtaCore.Speed(1000));
        Assert.True(SkyrtaCore.Tol(SkyrtaCore.Vmax) > SkyrtaCore.Tol(SkyrtaCore.V0));
    }

    [Fact]
    public void Wind_makes_the_sheaf_half_again_faster_only_inside_its_window()
    {
        var v = 400;
        int[] wind = [1000, 2000];
        // До вікна — як без вітру; у вікні шлях росте в 1,5 раза швидше; після — знову рівно.
        Assert.Equal(SkyrtaCore.Center(900, v, true, false, 0, []), SkyrtaCore.Center(900, v, true, false, 0, wind));
        Assert.Equal(1000, SkyrtaCore.WindOverlap(0, 3000, wind));
        Assert.Equal(500, SkyrtaCore.WindOverlap(1500, 3000, wind));
        Assert.Equal(0, SkyrtaCore.WindOverlap(2500, 3000, wind));
        // Шлях за 1,5 с, з них 0,5 с у вітрі: 400·(1,5 + 0,25) = 700 одиниць від Lo — у межах прямого проходу.
        Assert.Equal(SkyrtaCore.Lo + 700, SkyrtaCore.Center(1500, v, true, false, 0, wind));
    }

    [Fact]
    public void Sway_is_zero_at_start_and_bounded()
    {
        Assert.Equal(0, SkyrtaCore.Sway(0));
        for (var t = 0; t < 5000; t += 7) Assert.InRange(SkyrtaCore.Sway(t), -SkyrtaCore.SwayA, SkyrtaCore.SwayA);
        Assert.NotEqual(SkyrtaCore.Center(300, 500, true, false, 0, []), SkyrtaCore.Center(300, 500, true, true, 0, []));
    }

    [Fact]
    public void Land_cuts_overhang_rewards_perfect_and_misses_off_the_stack()
    {
        var v = SkyrtaCore.V0;
        // Зсув на 50 праворуч: лишається 350, звисле обрізано.
        Assert.Equal((350, 350, SkyrtaCore.KindCut), SkyrtaCore.Land(300, 400, 350, v, 0));
        Assert.Equal((300, 350, SkyrtaCore.KindCut), SkyrtaCore.Land(300, 400, 250, v, 0));
        // У межах допуску — ідеально: на місце попереднього, ширина та сама (вже підвалина — ширше не буває).
        Assert.Equal((300, 400, SkyrtaCore.KindPerfect), SkyrtaCore.Land(300, 400, 300 + SkyrtaCore.Tol(v), v, 0));
        // Вузька скирта ширшає: +Grow, а в серії від BigFrom — +GrowBig, по центру.
        Assert.Equal((397, 106, SkyrtaCore.KindPerfect), SkyrtaCore.Land(400, 100, 401, v, 0));
        Assert.Equal((392, 116, SkyrtaCore.KindPerfect), SkyrtaCore.Land(400, 100, 401, v, SkyrtaCore.BigFrom - 1));
        // Повністю мимо — промах, верх не змінився.
        Assert.Equal((300, 100, SkyrtaCore.KindMiss), SkyrtaCore.Land(300, 100, 400, v, 0));
        Assert.Equal((300, 100, SkyrtaCore.KindMiss), SkyrtaCore.Land(300, 100, 150, v, 0));
    }

    /// <summary>
    /// Еталонні вектори для паритету з браузером: web/games/skyrta.js мусить дати рівно ці числа
    /// (docs/games/dev/skyrta-parity.json; перевірка клієнта — docs/games/dev/skyrta-parity.js). Файл пише цей
    /// тест, якщо його нема або є змінна SKYRTA_PARITY_WRITE=1; інакше — звіряє.
    /// </summary>
    [Fact]
    public void Parity_vectors_match_the_file_the_client_checks_against()
    {
        var rng = new Random(2026);
        var centers = new List<int[]>();
        for (var i = 0; i < 300; i++)
        {
            var t = rng.Next(0, 40_000);
            var h = rng.Next(0, 30);
            var n = rng.Next(1, 40);
            var s0 = rng.Next(0, 60_000);
            int[] wind = rng.Next(3) == 0 ? [s0 + rng.Next(-2000, 3000), 0] : [];
            if (wind.Length > 0) wind[1] = wind[0] + SkyrtaCore.WindMs;
            var c = SkyrtaCore.Center(t, SkyrtaCore.Speed(h), SkyrtaCore.FromLeft(n), SkyrtaCore.Sways(h), s0, wind);
            centers.Add([t, h, n, s0, wind.Length > 0 ? wind[0] : -1, wind.Length > 0 ? wind[1] : -1, c]);
        }
        var lands = new List<int[]>();
        for (var i = 0; i < 200; i++)
        {
            var pw = rng.Next(1, SkyrtaCore.BaseW + 1);
            var pl = rng.Next(0, SkyrtaCore.FieldW - pw);
            var l = pl + rng.Next(-pw - 20, pw + 20);
            var v = SkyrtaCore.Speed(rng.Next(0, 30));
            var streak = rng.Next(0, 6);
            var (nl, nw, k) = SkyrtaCore.Land(pl, pw, l, v, streak);
            lands.Add([pl, pw, l, v, streak, nl, nw, k]);
        }
        var json = JsonSerializer.Serialize(new { center = centers, land = lands });
        var path = Path.Combine(FindRoot(), "docs", "games", "dev", "skyrta-parity.json");
        if (!File.Exists(path) || Environment.GetEnvironmentVariable("SKYRTA_PARITY_WRITE") == "1")
            File.WriteAllText(path, json);
        Assert.Equal(json, File.ReadAllText(path).Trim());
    }

    /// <summary>
    /// Паритет JS живе в браузері (<c>skyrta-parity.py</c>), і dotnet test його не проганяє. Тож тут — сторож: сталі
    /// рушія в <c>skyrta.js</c> мусять дорівнювати C#, а сам блок рушія закріплено хешем. Змінив його — прожени
    /// паритет у браузері й онови хеш (спека §4).
    /// </summary>
    [Fact]
    public void Js_engine_constants_match_and_core_block_is_pinned()
    {
        var js = File.ReadAllText(Path.Combine(FindRoot(), "web", "games", "skyrta.js")).Replace("\r\n", "\n");
        int K(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(js, @"\b" + name + @" = (\d+)[,;]");
            Assert.True(m.Success, name);
            return int.Parse(m.Groups[1].Value);
        }
        Assert.Equal(SkyrtaCore.FieldW, K("FIELD"));
        Assert.Equal(SkyrtaCore.Lo, K("LO"));
        Assert.Equal(SkyrtaCore.Hi, K("HI"));
        Assert.Equal(SkyrtaCore.BaseW, K("BASE_W"));
        Assert.Equal(SkyrtaCore.V0, K("V0"));
        Assert.Equal(SkyrtaCore.Vk, K("VK"));
        Assert.Equal(SkyrtaCore.Vmax, K("VMAX"));
        Assert.Equal(SkyrtaCore.Gap, K("GAP"));
        Assert.Equal(SkyrtaCore.MissGap, K("MISS_GAP"));
        Assert.Equal(SkyrtaCore.SwayFrom, K("SWAY_FROM"));
        Assert.Equal(SkyrtaCore.SwayA, K("SWAY_A"));
        Assert.Equal(SkyrtaCore.SwayP, K("SWAY_P"));
        Assert.Equal(SkyrtaCore.Grow, K("GROW"));
        Assert.Equal(SkyrtaCore.GrowBig, K("GROW_BIG"));
        Assert.Equal(SkyrtaCore.BigFrom, K("BIG_FROM"));
        Assert.Equal(SkyrtaCore.KindCut, K("CUT"));
        Assert.Equal(SkyrtaCore.KindPerfect, K("PERFECT"));
        Assert.Equal(SkyrtaCore.KindMiss, K("MISS"));
        var a = js.IndexOf("  const FIELD = ", StringComparison.Ordinal);
        var b = js.IndexOf("  const sheafLeft = ", StringComparison.Ordinal);
        Assert.True(a > 0 && b > a);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(js[a..b])))[..16];
        Assert.True(hash == PinnedCore, $"Рушій у skyrta.js змінився (хеш {hash}): прожени docs/games/dev/skyrta-parity.py і онови PinnedCore");
    }

    /// <summary>Хеш блоку рушія в skyrta.js, з яким паритет у браузері востаннє пройшов.</summary>
    const string PinnedCore = "3000A29DCE6752DB";

    static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "web", "games"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("не знайшов корінь репозиторію");
    }

    // ---------- партія ----------

    [Fact]
    public void Lobby_shows_bases_and_offers_a_bot_to_the_lone_host()
    {
        var h = new RoomHarness("skyrta", null, 1);
        h.Join("Оля");
        var v = h.View(0);
        Assert.Equal("lobby", v.GetProperty("phase").GetString());
        Assert.True(v.GetProperty("botOffer").GetBoolean());
        Assert.Equal(1, v.GetProperty("stacks")[0].GetArrayLength());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("stacks")[1].ValueKind);
        Assert.False(h.Start().Ok);   // сам на сам — лише з ботом
    }

    [Fact]
    public void Countdown_then_go_and_taps_before_go_are_refused_without_changing_the_view()
    {
        var h = Table();
        Assert.Equal("ready", h.View(0).GetProperty("phase").GetString());
        var before = h.View(0).GetRawText();
        Assert.False(h.Act(0, "tap", new { n = 1, t = 0 }).Ok);
        Assert.Equal(before, h.View(0).GetRawText());
        h.Tick(Skyrta.ReadyTicks - 1);
        Assert.Equal(Skyrta.PhReady, G(h).Phase);
        h.Tick();
        Assert.Equal(Skyrta.PhGo, G(h).Phase);
    }

    [Fact]
    public void Bad_taps_are_refused_and_change_nothing()
    {
        var h = Table();
        ToGo(h);
        h.Tick(10);
        var before = h.View(0).GetRawText();
        Assert.False(h.Act(0, "tap", new { n = 2, t = 100 }).Ok);           // не той сніп
        Assert.False(h.Act(0, "tap", new { n = 1 }).Ok);                     // без моменту
        Assert.False(h.Act(0, "tap", new { n = 1, t = -5 }).Ok);
        Assert.False(h.Act(0, "tap", new { n = "1", t = 5 }).Ok);
        Assert.False(h.Act(0, "tap", 5).Ok);
        Assert.False(h.Act(0, "dance", new { n = 1, t = 5 }).Ok);
        Assert.Equal(before, h.View(0).GetRawText());
    }

    [Fact]
    public void Tap_lands_the_sheaf_and_the_same_number_cannot_land_twice()
    {
        var h = Table();
        ToGo(h);
        h.Tick(20);   // 800 мс: сніп зліва вже наїхав на підвалину
        var st = G(h).Stacks[0];
        var expectL = st.LeftAt(800);
        Assert.True(h.Act(0, "tap", new { n = 1, t = 800 }).Ok);
        Assert.Equal(1, st.H);
        Assert.Equal(2, st.N);
        Assert.Equal(800 + SkyrtaCore.Gap, st.S);
        var (nl, nw, _) = SkyrtaCore.Land(SkyrtaCore.BaseL, SkyrtaCore.BaseW, expectL, SkyrtaCore.V0, 0);
        Assert.Equal((nl, nw), (st.L, st.W));
        Assert.False(h.Act(0, "tap", new { n = 1, t = 800 }).Ok);
        h.Tick();
        var stacks = h.View(1).GetProperty("stacks");
        Assert.Equal(2, stacks[0].GetArrayLength());
        Assert.Equal(1, stacks[1].GetArrayLength());
        // Подія посадки — у кадрі з id і обрізком (старий сніп мінус новий верх).
        var ev = h.View(null).GetProperty("frame").GetProperty("ev");
        Assert.Contains(ev.EnumerateArray(), e => e[0].GetString() == "l" && e[2].GetInt32() == 0);
    }

    [Fact]
    public void Tap_moments_are_clamped_to_the_server_clock()
    {
        var h = Table();
        ToGo(h);
        h.Tick(50);   // 2 с
        var st = G(h).Stacks[0];
        // Із майбутнього — сніп падає «зараз».
        Assert.True(h.Act(0, "tap", new { n = 1, t = 9_000 }).Ok);
        Assert.Equal(2000 + SkyrtaCore.Gap, st.S);
        h.Tick(100);   // 6 с; сніп 2 виїхав о 2,35 с
        // Із давнього минулого (до виїзду снопа) — не раніше, ніж MaxLag тому.
        Assert.True(h.Act(1, "tap", new { n = 1, t = 0 }).Ok);
        Assert.Equal(6000 - SkyrtaCore.MaxLag + SkyrtaCore.Gap, G(h).Stacks[1].S);
    }

    [Fact]
    public void Three_perfect_sheaves_send_wind_to_the_next_seat_and_it_does_not_stack()
    {
        var h = Table(3);
        ToGo(h);
        var ace = new Ace(0);
        var g = G(h);
        for (var i = 0; i < 2000 && g.Stacks[0].Perfects < 3; i++) { ace.Step(g, Tapper(h, 0)); h.Tick(); }
        Assert.True(g.Stacks[0].Perfects >= 3);
        Assert.Equal(1, g.Stacks[0].Winds);
        Assert.Equal(2, g.Stacks[1].Wind.Count);     // сусід праворуч — місце 1
        Assert.Empty(g.Stacks[2].Wind);
        var f = h.View(null).GetProperty("frame");
        Assert.Equal(8, f.GetProperty("p")[1][6].GetInt32() & 8);
        Assert.Contains(f.GetProperty("ev").EnumerateArray(), e => e[0].GetString() == "w" && e[2].GetInt32() == 0 && e[3].GetInt32() == 1);
        // Ще три ідеальні, поки вітер на сусіді ще дме, — другий порив мимо.
        var winds = g.Stacks[1].Wind.Count;
        TickUntilDone(h, 2000, () => { if (g.Stacks[0].Perfects < 6 && g.Stacks[1].Windy(g.Now())) ace.Step(g, Tapper(h, 0)); });
        Assert.Equal(winds, g.Stacks[1].Wind.Count);
    }

    [Fact]
    public void Ace_reaches_the_goal_first_wins_and_earns_achievements()
    {
        var h = Table(2);
        ToGo(h);
        var g = G(h);
        var ace = new Ace(0);
        var slow = 0;
        TickUntilDone(h, 4000, () =>
        {
            ace.Step(g, Tapper(h, 0));
            if (++slow % 60 == 0 && g.Phase == Skyrta.PhGo) TapNow(h, 1);
        });
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var fin = Assert.Single(h.Finished);
        Assert.Equal([0], fin.Result.Winners);
        Assert.Equal(Skyrta.Goal, fin.Result.Scores![0]);
        Assert.True(fin.Result.Scores[1] < Skyrta.Goal);
        Assert.StartsWith("Скирта: Оля 25 : Петро", fin.Result.Text);
        var mine = h.Awards.Where(a => a.Nick == "Оля").Select(a => a.Reason).ToHashSet();
        Assert.Contains("ach:skyrta-line", mine);
        Assert.Contains("ach:skyrta-neat", mine);
        Assert.Contains("ach:skyrta-storm", mine);
        Assert.DoesNotContain(h.Awards, a => a.Nick == "Петро");
        foreach (var key in new[] { "skyrta-line", "skyrta-neat", "skyrta-storm" }) Assert.NotNull(AchievementCatalog.Get(key));
        Assert.Equal("over", h.View(0).GetProperty("phase").GetString());
        Assert.False(h.Act(0, "tap", new { n = g.Stacks[0].N, t = 0 }).Ok);
    }

    [Fact]
    public void Time_limit_with_nobody_stacking_is_a_draw_and_one_sheaf_wins_it()
    {
        var h = Table(2);
        ToGo(h);
        h.Tick(Skyrta.LimitMs / SkyrtaCore.TickMs + 2);
        var fin = Assert.Single(h.Finished);
        Assert.Empty(fin.Result.Winners);
        Assert.EndsWith("нічия", fin.Result.Text);

        var h2 = Table(2, seed: 3);
        ToGo(h2);
        h2.Tick(20);
        Assert.True(TapNow(h2, 1).Ok);
        h2.Tick(Skyrta.LimitMs / SkyrtaCore.TickMs + 2);
        var fin2 = Assert.Single(h2.Finished);
        if (G(h2).Stacks[1].H > 0) Assert.Equal([1], fin2.Result.Winners);
    }

    [Fact]
    public void Leaving_mid_game_keeps_the_rest_playing_until_one_is_left()
    {
        var h = Table(3);
        ToGo(h);
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.True(G(h).Stacks[1].Gone);
        Assert.Equal(2, G(h).Neighbor(0));
        h.Tick();
        h.Leave("Ганна");
        var fin = Assert.Single(h.Finished);
        Assert.Equal([0], fin.Result.Winners);
    }

    [Fact]
    public void Rematch_starts_fresh_stacks()
    {
        var h = Table(2);
        ToGo(h);
        h.Tick(Skyrta.LimitMs / SkyrtaCore.TickMs + 2);
        Assert.Single(h.Finished);
        Assert.True(h.Rematch().Ok, h.Reply.Message);
        var g = G(h);
        Assert.Equal(Skyrta.PhReady, g.Phase);
        Assert.All(g.Stacks.Where(s => s.Plays), s => { Assert.Equal(0, s.H); Assert.Equal(1, s.N); Assert.Single(s.Sheaves); });
    }

    [Fact]
    public void Views_are_the_same_for_everyone_nothing_is_hidden()
    {
        var h = Table(3);
        ToGo(h);
        h.Tick(30);
        TapNow(h, 0);
        h.Tick();
        var a = h.View(0).GetRawText();
        Assert.Equal(a, h.View(1).GetRawText());
        Assert.Equal(a, h.View(null).GetRawText());
        var v = h.View(0);
        foreach (var key in new[] { "phase", "goal", "limit", "rules", "stacks", "stats", "winners", "series", "botOffer", "frame" })
            Assert.True(v.TryGetProperty(key, out _), key);
        var p = v.GetProperty("frame").GetProperty("p");
        Assert.Equal(SkyrtaCore.Seats, p.GetArrayLength());
        Assert.Equal(7, p[0].GetArrayLength());
        Assert.Equal(JsonValueKind.Null, p[3].ValueKind);
    }

    [Fact]
    public void Same_seed_gives_the_same_bot_game()
    {
        string Run()
        {
            var h = SoloWithBots(LiveBots.Level.Normal, 9);
            var frames = new List<string>();
            TickUntilDone(h, 3000, () => frames.Add(JsonSerializer.Serialize(G(h).Frame())));
            return string.Join("\n", frames) + Assert.Single(h.Finished).Result.Text;
        }
        Assert.Equal(Run(), Run());
    }

    // ---------- боти ----------

    [Fact]
    public void Solo_with_bots_bots_really_stack_and_nothing_is_awarded()
    {
        var h = SoloWithBots(LiveBots.Level.Hard, 4);
        var g = G(h);
        Assert.Equal(Skyrta.SoloBots, g.Bots.Count);
        Assert.StartsWith(LiveBots.Name, h.Room.Game.SeatBot(g.Bots[0]));
        TickUntilDone(h, 3000);
        var fin = Assert.Single(h.Finished);
        Assert.True(g.Bots.Max(b => g.Stacks[b].H) >= 10, string.Join(",", g.Bots.Select(b => g.Stacks[b].H)));
        Assert.Empty(fin.Result.Winners);           // Оля стояла — перемога бота
        Assert.StartsWith("🤖", fin.Result.Verdict);
        Assert.Empty(h.Awards);
    }

    /// <summary>
    /// Боти одного рівня в гонці до 15 снопів: сильний доходить помітно швидше, кладе здебільшого ідеально й майже не
    /// промахується, легкий — повільніше й з промахами. Різниця в scores мала (гонка кінчається на першому), тож
    /// міряємо час до кінця й частку ідеальних.
    /// </summary>
    [Fact]
    public void Hard_bot_stacks_clearly_better_than_easy()
    {
        (double EndMs, double Perfect, double Misses, double Width) Run(LiveBots.Level level)
        {
            double end = 0, perf = 0, miss = 0, width = 0, n = 0;
            for (var seed = 1; seed <= 4; seed++)
            {
                var h = new PartyHarness("skyrta", humans: 0, bots: 4, level: level, seed: seed);
                h.Start();
                Assert.NotNull(h.RunToEnd());
                var g = (Skyrta)h.Game;
                end += g.Now();
                foreach (var st in g.Stacks.Where(s => s.Plays))
                {
                    perf += st.H == 0 ? 0 : st.Perfects / (double)st.H;
                    miss += st.Misses;
                    width += st.W;
                    n++;
                }
            }
            return (end / 4, perf / n, miss / n, width / n);
        }
        var easy = Run(LiveBots.Level.Easy);
        var normal = Run(LiveBots.Level.Normal);
        var hard = Run(LiveBots.Level.Hard);
        output.WriteLine($"легкий {easy}, звичайний {normal}, сильний {hard}");
        Assert.True(hard.EndMs < easy.EndMs * 0.85, $"легкий {easy}, сильний {hard}");
        Assert.True(hard.Perfect > easy.Perfect * 1.5, $"легкий {easy}, сильний {hard}");
        Assert.True(hard.Perfect < 0.97, $"сильний не ідеальний: {hard}");
        Assert.True(easy.Width < normal.Width && normal.Width < hard.Width, $"{easy} {normal} {hard}");
        Assert.True(easy.Misses > hard.Misses, $"{easy} {hard}");
    }

    // ---------- режим вечірки ----------

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    public void Party_with_only_bots_finishes_before_cap_with_scores_for_all(int bots)
    {
        var h = new PartyHarness("skyrta", humans: 0, bots: bots, seed: 7);
        h.Start();
        var g = (Skyrta)h.Game;
        Assert.True(g.Party);
        Assert.Equal(Skyrta.PartyGoal, g.GoalNow);
        var r = h.RunToEnd();
        Assert.NotNull(r);
        Assert.Equal(MinigameEnd.Finished, r!.How);
        Assert.Equal(bots, r.Scores.Count);
        Assert.All(r.Scores.Values, s => Assert.True(s > 0));
        Assert.True(h.Clock.UtcNow - h.StartedAt <= TimeSpan.FromMilliseconds(h.Host.CapMs));
        Assert.Equal(0, h.Ctx.Muted);
    }

    [Fact]
    public void Party_ace_human_is_on_top_and_idle_human_does_not_stall()
    {
        var h = new PartyHarness("skyrta", humans: 2, bots: 3, level: LiveBots.Level.Hard, seed: 3);
        h.Start();
        var g = (Skyrta)h.Game;
        var ace = new Ace(0);
        var mid = false;
        var r = h.RunToEnd(x =>
        {
            ace.Step(g, (n, t) => x.Act(0, "tap", new { n, t }));
            if (!mid && g.Phase == Skyrta.PhGo && g.Now() > 10_000)
            {
                mid = true;
                var s = g.PartyScores();
                Assert.Equal(5, s.Count);
                Assert.True(s.Keys.Order().SequenceEqual(Enumerable.Range(0, 5)));
            }
        });
        Assert.True(mid);
        Assert.Equal(MinigameEnd.Finished, r!.How);
        Assert.Equal([0], r.Winners);
        Assert.Equal(1, r.Places[0]);
        Assert.Equal(Skyrta.PartyGoal * 1_000_000_000L + (g.Limit - g.Stacks[0].DoneAt + 1) * 1000L, r.Scores[0]);
        Assert.True(r.Scores[1] < r.Scores[0]);    // людина 1 стояла
        Assert.Equal(0, h.Ctx.Muted);
        Assert.True(h.Clock.UtcNow - h.StartedAt <= TimeSpan.FromMilliseconds(h.Host.CapMs));
    }

    [Fact]
    public void Party_with_idle_humans_ends_by_time_before_cap()
    {
        var h = new PartyHarness("skyrta", humans: 3, bots: 0, seed: 5);
        h.Start();
        var r = h.RunToEnd()!;
        Assert.Equal(MinigameEnd.Finished, r.How);
        Assert.Equal(3, r.Scores.Count);
        Assert.True(h.Clock.UtcNow - h.StartedAt <= TimeSpan.FromMilliseconds(3000 + Skyrta.PartyLimitMs + 200));
    }

    [Fact]
    public void Party_is_deterministic_by_seed_and_bot_toggle_is_refused()
    {
        string Run()
        {
            var h = new PartyHarness("skyrta", humans: 0, bots: 6, seed: 11);
            h.Start();
            var r = h.RunToEnd()!;
            return string.Join(",", r.Scores.OrderBy(kv => kv.Key).Select(kv => kv.Value)) + r.Log;
        }
        Assert.Equal(Run(), Run());
        var p = new PartyHarness("skyrta", humans: 1, bots: 1, seed: 1);
        p.Start();
        Assert.False(p.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.False(p.View(0).GetProperty("botOffer").GetBoolean());
        Assert.True(PartyPool.Has("skyrta"));
    }

    // ---------- швидкодія ----------

    [Fact]
    [Trait("Category", "Perf")]
    public void Thousand_ticks_with_eight_hard_bots_are_fast()
    {
        var sw = Stopwatch.StartNew();
        var p = new PartyHarness("skyrta", humans: 0, bots: 8, level: LiveBots.Level.Hard, seed: 2);
        p.Start();
        for (var i = 0; i < 1000 && !p.Host.Over; i++)
        {
            p.TickSub();
            _ = p.Frame();
        }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} мс");
    }
}
