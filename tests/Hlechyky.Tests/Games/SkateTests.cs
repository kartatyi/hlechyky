using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Ковзанка: фізику, зіткнення, ополонки й мозок ботів перевіряємо на голому <see cref="SkateCore"/> (там селянина
/// можна поставити рівно туди, куди треба, з рівно такою швидкістю), а раунди, очки, дії й приховане — через
/// справжню кімнату (<see cref="RoomHarness"/>). Клас у серійній колекції через перф-тести.
/// </summary>
[Collection(SerialPerf.Name)]
public class SkateTests(ITestOutputHelper output)
{
    const int Fp = SkateCore.Fp;

    // ---------- підмостки ----------

    static RoomHarness Table(int players = 2, int seed = 42, object? options = null)
    {
        var h = new RoomHarness("skate", options, seed: seed);
        foreach (var nick in new[] { "Оля", "Петро", "Ганна", "Іван", "Марта", "Юрко", "Соня", "Богдан" }.Take(players)) h.Join(nick);
        h.Start();
        return h;
    }

    static Skate G(RoomHarness h) => (Skate)h.Room.Game;
    static SkateCore Core(RoomHarness h) => G(h).CoreForTests;
    static SkateSeat S(RoomHarness h, int seat) => G(h).SeatForTests(seat);
    static SkateVillager Me(RoomHarness h, int seat) => Core(h).V[S(h, seat).Me];
    static SkateVillager Bot(RoomHarness h, int skip = 0) => Core(h).V.Where(v => v.Owner < 0).Skip(skip).First();

    static void Go(RoomHarness h)
    {
        for (var i = 0; i < 200 && G(h).Phase != Skate.PhaseGo; i++) h.Tick();
        Assert.Equal(Skate.PhaseGo, G(h).Phase);
    }

    /// <summary>Поставити селянина в точку (одиниці), зі швидкістю (саби/тик); ніде не лежить, нікуди не хоче.</summary>
    static SkateVillager Put(SkateVillager v, int x, int y, int vx = 0, int vy = 0)
    {
        v.X = x * Fp;
        v.Y = y * Fp;
        v.Vx = vx;
        v.Vy = vy;
        v.Want = -1;
        v.Fallen = v.Water = 0;
        v.Out = false;
        v.Pushing = v.Braking = false;
        SkateCore.Forget(v, 100_000);
        v.Hold = 1_000_000;          // бот «задумався» — не рушить, поки тест сам не скаже
        return v;
    }

    /// <summary>Ядро без ополонок: гравець на місці 0 і кілька ботів, що стоять і нічого не хочуть.</summary>
    static SkateCore Bare(int bots = 3, int seed = 1, int players = 1)
    {
        var core = new SkateCore(new Random(seed));
        core.Deal([.. Enumerable.Range(0, players)], bots);
        core.Holes.Clear();
        foreach (var v in core.V) { SkateCore.Forget(v, 100_000); v.Hold = 1_000_000; }
        foreach (var s in core.Slots) s.Wait = 100_000;
        return core;
    }

    /// <summary>Усіх, крім переданих, — у дальній кут ставка стояти й не заважати; ополонки й ласощі — геть.</summary>
    static void Park(RoomHarness h, params int[] keep)
    {
        var i = 0;
        foreach (var v in Core(h).V)
        {
            if (Array.IndexOf(keep, v.Id) >= 0) continue;
            // низ ставка, три шеренги по двадцять, 28 од. одне від одного — ніхто нікого не торкається
            Put(v, 160 + i % 20 * 28, (i / 20 % 3) switch { 0 => 524, 1 => 496, _ => 468 });
            i++;
        }
        Core(h).Holes.Clear();
        foreach (var s in Core(h).Slots) s.Wait = 100_000;
    }

    static string FrameText(RoomHarness h) => Views.Text(G(h).Frame());
    static JsonElement LastFrame(RoomHarness h) => Views.Json(((RoomFrame)h.Outbox.Last(o => o is RoomFrame)).Frame);
    static IEnumerable<JsonElement> Events(RoomHarness h) =>
        h.Outbox.OfType<RoomFrame>().SelectMany(f => Views.Json(f.Frame).GetProperty("ev").EnumerateArray());

    // =============================================================================================
    // Статистика: людина за клавіатурою проти бота
    // =============================================================================================

    /// <summary>
    /// Людина за клавіатурою на ковзанці: тримає напрямок 4–40 тиків, інколи одразу перемикає на сусідній (додала ще
    /// клавішу для діагоналі) чи під прямим кутом, частіше відпускає й котиться 1–30 тиків, зрідка задумується на
    /// 4–10 с, інколи гальмує. Біля ополонки — відвертає, як і всякий, хто її бачить.
    /// </summary>
    sealed class SkateHuman(Random rng)
    {
        int _left, _want = -1, _last;

        public int Next()
        {
            if (_left-- > 0) return _want;
            if (_want is >= 0 and < 8 && rng.Next(100) < 35)
            {
                var r = rng.Next(100);
                _want = (_want + (r < 50 ? (rng.Next(2) == 0 ? 1 : 7) : r < 90 ? (rng.Next(2) == 0 ? 2 : 6) : 4)) & 7;
                _left = rng.Next(4, 41);
            }
            else if (_want is >= 0 and < 8)
            {
                _last = _want;
                if (rng.Next(100) < 15) { _want = 8; _left = rng.Next(3, 13); }
                else { _want = -1; _left = rng.Next(100) < 6 ? rng.Next(100, 251) : rng.Next(1, 31); }
            }
            else
            {
                _want = rng.Next(100) < 50 ? _last : rng.Next(8);
                _left = rng.Next(4, 41);
            }
            return _want;
        }
    }

    /// <summary>Що видно в кадрах про одну сторону: швидкості, серії однакового «вводу» (s і d), стояння.</summary>
    sealed class SkateTrace
    {
        public readonly Dictionary<int, long> Speed = [];
        public long Ticks;
        public readonly Dictionary<int, int> PushRuns = [], GlideRuns = [], BrakeRuns = [], StillRuns = [];
        public readonly Dictionary<int, int> Turns = [];
        public long Falls;

        public static int SpeedBin(int d) => d switch { 0 => 0, 1 => 1, <= 3 => 2, <= 5 => 3, _ => 4 };
        public static int RunBin(int len) => len switch { <= 2 => 0, <= 5 => 1, <= 11 => 2, <= 24 => 3, <= 50 => 4, <= 100 => 5, _ => 6 };

        public static void Add(Dictionary<int, int> d, int k) => d[k] = d.GetValueOrDefault(k) + 1;
        public static int Share(Dictionary<int, int> d, int from, int to) =>
            d.Where(p => p.Key >= from && p.Key <= to).Sum(p => p.Value) * 1000 / Math.Max(1, d.Values.Sum());
        public int SpeedShare(int bin) => (int)(Speed.GetValueOrDefault(bin) * 1000 / Math.Max(1, Ticks));
    }

    /// <summary>
    /// Ставок на голому ядрі: 4 «людини» і 40 ботів, без ополонок (людина-скрипт їх не бачить), <paramref name="ticks"/>
    /// тиків на сід. Порядок тика — як у грі. Для кожного пишемо лише те, що видно в кадрі.
    /// </summary>
    static (SkateTrace Players, SkateTrace Bots) Observe(int seeds, int ticks = 6000)
    {
        var players = new SkateTrace();
        var bots = new SkateTrace();
        for (var seed = 1; seed <= seeds; seed++)
        {
            var core = new SkateCore(new Random(seed));
            core.Deal([0, 1, 2, 3], 40);
            core.Holes.Clear();
            var humans = Enumerable.Range(0, 4).Select(i => new SkateHuman(new Random(seed * 10 + i))).ToArray();
            var n = core.N;
            var px = core.V.Select(v => v.X / Fp).ToArray();
            var py = core.V.Select(v => v.Y / Fp).ToArray();
            var sym = Enumerable.Repeat(-9, n).ToArray();
            var run = new int[n];
            var still = new int[n];
            var lastPush = Enumerable.Repeat(-1, n).ToArray();
            for (var t = 0; t < ticks; t++)
            {
                foreach (var v in core.V)
                    if (v.Owner >= 0) v.Want = humans[v.Owner].Next();
                core.TimersAll();
                core.ThinkAll();
                core.StepAll();
                core.CollideAll();
                core.WaterAll();
                core.ItemsAll();
                core.Ev.Clear();
                var f = core.Pack();
                for (var i = 0; i < n; i++)
                {
                    var side = core.V[i].Owner >= 0 ? players : bots;
                    int x = f[i * 4], y = f[i * 4 + 1], d = f[i * 4 + 2], s = f[i * 4 + 3];
                    var step = Math.Max(Math.Abs(x - px[i]), Math.Abs(y - py[i]));
                    px[i] = x;
                    py[i] = y;
                    if (t < 200) continue;
                    if (s == 2 && sym[i] != -2) side.Falls++;
                    var now = s switch { 1 => d, 5 => 8, 0 => -1, _ => -2 };
                    if (s is 0 or 1 or 5)
                    {
                        side.Ticks++;
                        side.Speed[SkateTrace.SpeedBin(step)] = side.Speed.GetValueOrDefault(SkateTrace.SpeedBin(step)) + 1;
                    }
                    if (now != sym[i])
                    {
                        if (run[i] > 0)
                        {
                            var bin = SkateTrace.RunBin(run[i]);
                            if (sym[i] is >= 0 and < 8) SkateTrace.Add(side.PushRuns, bin);
                            else if (sym[i] == 8) SkateTrace.Add(side.BrakeRuns, bin);
                            else if (sym[i] == -1) SkateTrace.Add(side.GlideRuns, bin);
                        }
                        if (now is >= 0 and < 8)
                        {
                            if (lastPush[i] >= 0) SkateTrace.Add(side.Turns, Math.Min((now - lastPush[i] + 8) & 7, (lastPush[i] - now + 8) & 7));
                            lastPush[i] = now;
                        }
                        sym[i] = now;
                        run[i] = 0;
                    }
                    run[i]++;
                    if (s == 0 && step == 0) still[i]++;
                    else
                    {
                        if (still[i] > 0) SkateTrace.Add(side.StillRuns, SkateTrace.RunBin(still[i]));
                        still[i] = 0;
                    }
                }
            }
        }
        return (players, bots);
    }

    static string Show(Dictionary<int, int> d, int bins) =>
        string.Join(" ", Enumerable.Range(0, bins).Select(b => $"{b}:{SkateTrace.Share(d, b, b) / 10.0:F1}%"));

    static readonly Lazy<(SkateTrace Players, SkateTrace Bots)> Pond = new(() => Observe(3));

    /// <summary>Жодних тріщин до кінця раунду: ліміт ополонок заповнено «вічними тріщинами» далеко за мапою.</summary>
    static void NoCracks(RoomHarness h)
    {
        var holes = Core(h).Holes;
        holes.Clear();
        for (var i = 0; i < SkateCore.MaxHoles; i++) holes.Add(new SkateHole { X = -5000, Y = -5000, R = 1, Warn = int.MaxValue });
    }

    /// <summary>Покласти ласощі виду <paramref name="kind"/> під ноги місцю й дати тик: стоїть — підхопить.</summary>
    static void Feed(RoomHarness h, int seat, int kind, bool tick = true)
    {
        var me = Me(h, seat);
        var slot = Core(h).Slots.Where(s => s.Kind == kind).OrderBy(s => s.Here ? 1 : 0).First();
        slot.X = me.X / Fp;
        slot.Y = me.Y / Fp;
        slot.Wait = 0;
        me.Vx = me.Vy = 0;
        if (tick) h.Tick();
    }

    static List<int[]> Ev(RoomHarness h) => [.. LastFrame(h).GetProperty("ev").EnumerateArray().Select(e => e.EnumerateArray().Select(x => x.GetInt32()).ToArray())];

    static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "web", "games"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("не знайшов корінь репозиторію");
    }

    // =============================================================================================
    // Мапа
    // =============================================================================================

    [Fact]
    public void Map_is_30_by_20_with_400_connected_cells_of_ice()
    {
        Assert.Equal(20, SkateMap.Rows.Length);
        Assert.All(SkateMap.Rows, r => Assert.Equal(30, r.Length));
        Assert.All(SkateMap.Rows[0], c => Assert.Equal('#', c));
        Assert.All(SkateMap.Rows[19], c => Assert.Equal('#', c));
        Assert.All(SkateMap.Rows, r => Assert.True(r[0] == '#' && r[29] == '#'));
        Assert.Equal(400, SkateMap.Ice.Length);
        var seen = new HashSet<int> { SkateMap.Ice[0] };
        var q = new Queue<int>(seen);
        while (q.Count > 0)
        {
            var u = q.Dequeue();
            foreach (var n in new[] { u - 1, u + 1, u - SkateMap.W, u + SkateMap.W })
                if (n >= 0 && n < SkateMap.Cells && SkateMap.Pass[n] && seen.Add(n)) q.Enqueue(n);
        }
        Assert.Equal(400, seen.Count);
        Assert.Equal(6, SkateMap.Kinds.Select(k => k.Emoji).Distinct().Count());
        Assert.Equal(64, SkateMap.Names.Distinct().Count());
    }

    [Fact]
    public void A_skater_fits_only_on_ice_and_the_island_blocks_the_view()
    {
        Assert.True(SkateMap.BoxFits(200, 200, SkateCore.R));
        Assert.False(SkateMap.BoxFits(40, 200, SkateCore.R));          // очерет під берегом
        Assert.False(SkateMap.BoxFits(350, 300, SkateCore.R));         // острівець із вербою
        Assert.False(SkateMap.BoxFits(700, 400, SkateCore.R));         // вмерзлий човен
        Assert.True(SkateMap.DiskOnIce(400, 200, 40));
        Assert.False(SkateMap.DiskOnIce(90, 200, 40));
        Assert.True(SkateMap.Clear(150, 200, 800, 200));
        Assert.False(SkateMap.Clear(230, 300, 470, 300));              // крізь острів не видно
    }

    // =============================================================================================
    // Фізика — одна на всіх
    // =============================================================================================

    /// <summary>Сценарій вводу: розгін, діагоналі, котиться, гальмо, назад — без різкого розвороту на повній швидкості.</summary>
    static int ScriptA(int t) => t switch
    {
        < 25 => 0,
        < 45 => -1,
        < 53 => 7,
        < 61 => 1,
        < 71 => 8,
        < 101 => 4,
        < 121 => -1,
        < 140 => 8,
        < 160 => 2,
        _ => 5,
    };

    [Fact]
    public void A_bot_and_a_player_given_the_same_input_skate_the_same_path()
    {
        var core = Bare(3, seed: 2);
        var player = core.V.First(v => v.Owner == 0);
        var bot = core.V.First(v => v.Owner < 0);
        Put(player, 150, 160);
        Put(bot, 150, 230);
        var a = new List<(int, int, int, int)>();
        var b = new List<(int, int, int, int)>();
        for (var t = 0; t < 200; t++)
        {
            player.Want = bot.Want = ScriptA(t);
            core.StepAll();
            a.Add((player.X - 150 * Fp, player.Y - 160 * Fp, player.Dir, player.State));
            b.Add((bot.X - 150 * Fp, bot.Y - 230 * Fp, bot.Dir, bot.State));
        }
        Assert.Equal(a, b);
        Assert.True(a.Max(p => p.Item1) > 100 * Fp, "мав відкотити хоч сотню одиниць");
        Assert.Contains(a, p => p.Item4 == 5);           // гальмував
        Assert.Contains(a, p => p.Item3 == 7);           // і діагоналлю

        // Навіть із падіннями (різкий розворот на швидкості — кубик): той самий сід, той самий ввід — той самий шлях,
        // хоч цей селянин гравець, хоч бот.
        SkateCore Twin(bool asBot)
        {
            var c = Bare(3, seed: 9);
            var v = c.V.First(q => q.Owner == 0);
            if (asBot) v.Owner = -1;
            Put(v, 150, 200);
            return c;
        }
        var cp = Twin(false);
        var cb = Twin(true);
        var vp = cp.V.First(q => q.X == 150 * Fp && q.Y == 200 * Fp);
        var vb = cb.V[vp.Id];
        var falls = 0;
        for (var t = 0; t < 1500; t++)
        {
            var w = t % 60 < 30 ? (t / 60 % 2 == 0 ? 0 : 4) : t % 60 < 40 ? (t / 60 % 2 == 0 ? 4 : 0) : -1;
            vp.Want = vb.Want = w;
            cp.StepAll();
            cb.StepAll();
            if (vp.Fallen == SkateCore.FallTicks) falls++;
            if (vp.Fallen > 0) { vp.Fallen--; vb.Fallen--; }
            Assert.Equal((vp.X, vp.Y, vp.Vx, vp.Vy, vp.State), (vb.X, vb.Y, vb.Vx, vb.Vy, vb.State));
        }
        output.WriteLine($"падінь на різких розворотах: {falls}");
    }

    [Fact]
    public void A_push_adds_the_same_speed_in_all_eight_directions()
    {
        var core = Bare(1);
        var v = core.V[0];
        for (var d = 0; d < 8; d++)
        {
            Put(v, 400, 200);
            v.Want = d;
            SkateCore.Step(v, new Random(1));
            var sp = SkateCore.Speed2(v);
            // 40 саб/тик мінус тертя 2 — по прямій 38, по діагоналі (28, 28) ≈ 39,6 мінус 2 → 38,2
            Assert.InRange(sp, 37L * 37, 39L * 39);
            Assert.Equal(d, SkateCore.Dir8(v.Vx, v.Vy));
            Assert.Equal(d, v.Dir);
            Assert.Equal(1, v.State);
        }
    }

    [Fact]
    public void Nobody_skates_faster_than_six_units_a_tick()
    {
        var core = Bare(1);
        var v = Put(core.V[0], 120, 200);
        long top = 0;
        for (var t = 0; t < 110; t++)
        {
            v.Want = 0;
            SkateCore.Step(v, new Random(1));
            top = Math.Max(top, SkateCore.Speed2(v));
            Assert.True(SkateCore.Speed2(v) <= (long)SkateCore.Vmax * SkateCore.Vmax);
        }
        Assert.True(top >= (SkateCore.Vmax - 40L) * (SkateCore.Vmax - 40), $"{Math.Sqrt(top)}");
    }

    [Fact]
    public void Ice_is_slippery_a_glider_rolls_about_nine_cells_and_stops()
    {
        var core = Bare(1);
        var v = Put(core.V[0], 120, 200, vx: SkateCore.Vmax);
        var ticks = 0;
        var last = v.Vx;
        while (v.Vx != 0 && ticks < 1000)
        {
            SkateCore.Step(v, new Random(1));
            Assert.True(v.Vx <= last);
            last = v.Vx;
            ticks++;
        }
        var dist = v.X / Fp - 120;
        output.WriteLine($"з повного ходу котиться {dist} од. за {ticks} тиків");
        Assert.InRange(dist, 200, 330);
        Assert.InRange(ticks, 100, 400);
    }

    [Fact]
    public void The_brake_stops_within_a_second_and_never_turns_you_back()
    {
        var core = Bare(1);
        var v = Put(core.V[0], 120, 200, vx: SkateCore.Vmax);
        var ticks = 0;
        while (v.Vx != 0 && ticks < 100)
        {
            v.Want = 8;
            SkateCore.Step(v, new Random(1));
            Assert.True(v.Vx >= 0);
            Assert.Equal(5, v.State);
            ticks++;
        }
        Assert.InRange(ticks, 10, 25);
        Assert.InRange(v.X / Fp - 120, 25, 60);
    }

    [Fact]
    public void The_snowbank_bounces_you_back_at_half_speed_and_nobody_ever_leaves_the_ice()
    {
        var core = Bare(1);
        var v = Put(core.V[0], 90, 200, vx: -1200);
        var before = 0;
        for (var t = 0; t < 30 && v.Vx < 0; t++)
        {
            before = v.Vx;
            SkateCore.Step(v, new Random(1));
        }
        Assert.True(v.Vx > 0, "мав відскочити від берега");
        Assert.InRange(v.Vx, -before / 2 - 40, -before / 2);

        // юрма з випадковим вводом 3000 тиків: ні коробкою на березі, ні в острові, ні в човні
        var crowd = new SkateCore(new Random(4));
        crowd.Deal([0, 1, 2, 3], 40);
        crowd.Holes.Clear();
        var rng = new Random(5);
        for (var t = 0; t < 3000; t++)
        {
            foreach (var q in crowd.V)
                if (q.Owner >= 0 && t % 7 == 0) q.Want = rng.Next(-1, 9);
            crowd.TimersAll();
            crowd.ThinkAll();
            crowd.StepAll();
            crowd.CollideAll();
            crowd.Ev.Clear();
            Assert.All(crowd.V, q => Assert.True(SkateMap.BoxFits(q.X / Fp, q.Y / Fp, SkateCore.R), $"#{q.Id} на {q.X / Fp},{q.Y / Fp}"));
        }
    }

    [Fact]
    public void A_hard_turn_back_at_speed_sometimes_trips_you_and_gentle_turns_never_do()
    {
        var core = Bare(1);
        var v = core.V[0];
        int Trials(int vx, int want)
        {
            var falls = 0;
            var rng = new Random(11);
            for (var trial = 0; trial < 2000; trial++)
            {
                Put(v, 300, 200, vx: vx);
                for (var t = 0; t < 12 && v.Fallen == 0; t++)
                {
                    v.Want = want;
                    SkateCore.Step(v, rng);
                }
                if (v.Fallen > 0) falls++;
            }
            return falls;
        }
        var hard = Trials(1500, 4);
        output.WriteLine($"розворот з 1500 назад: {hard} падінь із 2000");
        Assert.InRange(hard, 10, 200);
        Assert.Equal(0, Trials(1500, 2));      // під прямим кутом — ніколи
        Assert.Equal(0, Trials(900, 4));       // повільно — ніколи
        Assert.Equal(0, Trials(1500, 8));      // гальмом — ніколи
    }

    [Fact]
    public void A_fallen_skater_slides_ignores_input_and_gets_up_after_two_seconds()
    {
        var core = Bare(1);
        var v = Put(core.V[0], 200, 200, vx: 1200);
        v.Fallen = SkateCore.FallTicks;
        v.Dir = 2;
        v.Want = 0;
        var x = v.X;
        core.TimersAll();
        core.StepAll();
        Assert.True(v.X > x);
        Assert.True(v.Vx < 1200 - 1200 / SkateCore.FallDrag + 1);
        Assert.Equal(2, v.Dir);
        Assert.Equal(2, v.State);
        for (var t = 1; t < SkateCore.FallTicks; t++) { core.TimersAll(); core.StepAll(); }
        Assert.Equal(0, v.Fallen);
        v.Want = 0;
        core.StepAll();
        Assert.Equal(1, v.State);
        Assert.Equal(0, v.Dir);
    }

    // =============================================================================================
    // Зіткнення
    // =============================================================================================

    [Fact]
    public void Every_collision_conserves_momentum_exactly_and_never_adds_energy()
    {
        var core = Bare(2);
        SkateVillager a = core.V[0], b = core.V[1];
        var rng = new Random(7);
        var hits = 0;
        for (var trial = 0; trial < 3000; trial++)
        {
            Put(a, 400, 200, rng.Next(-1600, 1601), rng.Next(-1600, 1601));
            Put(b, 400, 200, rng.Next(-1600, 1601), rng.Next(-1600, 1601));
            b.X += rng.Next(-14 * Fp, 14 * Fp);
            b.Y += rng.Next(-14 * Fp, 14 * Fp);
            long px = a.Vx + b.Vx, py = a.Vy + b.Vy, e = SkateCore.Speed2(a) + SkateCore.Speed2(b);
            if (core.Bump(a, b)) hits++;
            Assert.Equal(px, a.Vx + b.Vx);
            Assert.Equal(py, a.Vy + b.Vy);
            // ціле ділення може додати по сабу на вісь — це крихти проти втрат при e = 0,8
            var slack = 4 * (Math.Abs(a.Vx - b.Vx) + Math.Abs(a.Vy - b.Vy)) + 16;
            Assert.True(SkateCore.Speed2(a) + SkateCore.Speed2(b) <= e + slack, "енергії побільшало");
        }
        Assert.InRange(hits, 1000, 2000);

        // і вся юрма разом: сума імпульсів за тик зіткнень та сама
        var crowd = new SkateCore(new Random(8));
        crowd.Deal([], 56);
        var r2 = new Random(9);
        foreach (var q in crowd.V)
        {
            Put(q, 180 + r2.Next(0, 300), 150 + r2.Next(0, 100), r2.Next(-1500, 1501), r2.Next(-1500, 1501));
        }
        long sx = crowd.V.Sum(q => (long)q.Vx), sy = crowd.V.Sum(q => (long)q.Vy);
        var pairs = crowd.Pairs().Count;
        crowd.CollideAll();
        Assert.True(pairs > 20, $"{pairs}");
        Assert.Equal(sx, crowd.V.Sum(q => (long)q.Vx));
        Assert.Equal(sy, crowd.V.Sum(q => (long)q.Vy));
    }

    [Fact]
    public void A_skater_passes_nine_tenths_of_his_speed_to_one_standing_in_his_way()
    {
        var core = Bare(2);
        var a = Put(core.V[0], 400, 200, vx: 1000);
        var b = Put(core.V[1], 418, 200);
        Assert.True(core.Bump(a, b));
        Assert.Equal(900, b.Vx);
        Assert.Equal(100, a.Vx);
        Assert.Equal(0, a.Vy + b.Vy);
        Assert.Equal(a.Id, b.LastBy);
        Assert.Equal(b.Id, a.LastBy);
    }

    [Fact]
    public void Ramming_at_speed_knocks_the_slower_one_down_and_equal_speeds_knock_both()
    {
        var core = Bare(2);
        var a = Put(core.V[0], 400, 200, vx: 1200);
        var b = Put(core.V[1], 418, 200);
        core.Bump(a, b);
        Assert.Equal(0, a.Fallen);
        Assert.Equal(SkateCore.FallTicks, b.Fallen);
        Assert.Equal([SkateCore.EvKnock, a.Id, b.Id, 0], core.Ev.Single());
        core.Ev.Clear();

        Put(a, 400, 200, vx: 800);
        Put(b, 418, 200, vx: -800);
        core.Bump(a, b);
        Assert.Equal(SkateCore.FallTicks, a.Fallen);
        Assert.Equal(SkateCore.FallTicks, b.Fallen);
        Assert.Equal(1, core.Ev.Single()[3]);
        core.Ev.Clear();

        // легенько — ніхто не падає
        Put(a, 400, 200, vx: 600);
        Put(b, 418, 200);
        Assert.True(core.Bump(a, b));
        Assert.Equal(0, a.Fallen + b.Fallen);
        Assert.Empty(core.Ev);
    }

    [Fact]
    public void The_grid_finds_exactly_the_touching_pairs_that_brute_force_finds()
    {
        for (var seed = 1; seed <= 30; seed++)
        {
            var core = new SkateCore(new Random(seed));
            core.Deal([0, 1, 2, 3, 4, 5, 6, 7], 48);
            var rng = new Random(seed);
            foreach (var q in core.V)
            {
                Put(q, 0, 0);
                q.X = rng.Next(140 * Fp, 560 * Fp);
                q.Y = rng.Next(140 * Fp, 260 * Fp);
                if (rng.Next(20) == 0) q.Water = 10;           // у воді — не зіштовхуються
            }
            var grid = core.Pairs().ToList();
            var brute = core.PairsBrute();
            Assert.Equal(brute, grid);
            Assert.True(grid.Count > 10, $"сід {seed}: {grid.Count} пар");
        }
    }

    [Fact]
    public void A_ram_shows_in_the_frame_who_rammed_whom_and_the_victim_lies_down()
    {
        var h = Table(2, seed: 3);
        Go(h);
        var a = Bot(h);
        var b = Bot(h, 1);
        Park(h, a.Id, b.Id);
        Put(a, 380, 200, vx: 1300);
        Put(b, 402, 200);
        h.Tick();
        Assert.Contains(Ev(h), e => e[0] == SkateCore.EvKnock && e[1] == a.Id && e[2] == b.Id);
        Assert.Equal(2, LastFrame(h).GetProperty("v")[b.Id * 4 + 3].GetInt32());
    }

    // =============================================================================================
    // Ополонки
    // =============================================================================================

    [Fact]
    public void A_bot_that_slides_into_a_hole_climbs_out_after_four_seconds_and_skates_on()
    {
        var h = Table(2, seed: 5);
        Go(h);
        var bot = Bot(h);
        Park(h, bot.Id);
        Core(h).Holes.Add(new SkateHole { X = 420, Y = 200, R = 30 });
        Put(bot, 360, 200, vx: 900);
        var splash = -1;
        for (var t = 0; t < 30 && splash < 0; t++)
        {
            h.Tick();
            if (Ev(h).Any(e => e[0] == SkateCore.EvSplash && e[1] == bot.Id && e[2] == -1)) splash = t;
        }
        Assert.True(splash >= 0, "не шубовснув");
        Assert.Equal(3, bot.State);
        h.Tick(SkateCore.WaterTicks - 1);
        Assert.Equal(3, bot.State);
        h.Tick();
        Assert.Contains(Ev(h), e => e[0] == SkateCore.EvClimb && e[1] == bot.Id);
        Assert.True(bot.Upright);
        long dx = bot.X / Fp - 420, dy = bot.Y / Fp - 200;
        Assert.True(dx * dx + dy * dy >= 40L * 40, "виліз, а стоїть у воді");
        var at = (bot.X, bot.Y);
        h.Tick(300);
        Assert.NotEqual(at, (bot.X, bot.Y));
    }

    [Fact]
    public void A_player_who_falls_in_is_out_for_the_round_and_everyone_learns_his_nick()
    {
        var h = Table(3, seed: 7);
        Go(h);
        var him = Me(h, 1);
        Park(h, Me(h, 0).Id, him.Id, Me(h, 2).Id);
        Put(Me(h, 0), 150, 160);
        Put(Me(h, 2), 750, 160);
        Core(h).Holes.Add(new SkateHole { X = 420, Y = 200, R = 30 });
        Put(him, 360, 200, vx: 900);
        h.Tick(10);
        Assert.False(S(h, 1).Alive);
        Assert.Contains(Events(h), e => e[0].GetInt32() == SkateCore.EvSplash && e[1].GetInt32() == him.Id && e[2].GetInt32() == 1);
        var dead = h.View(null).GetProperty("dead").EnumerateArray().Single();
        Assert.Equal((1, him.Id), (dead.GetProperty("seat").GetInt32(), dead.GetProperty("id").GetInt32()));
        Assert.Equal(Skate.PhaseGo, G(h).Phase);
        h.Tick(SkateCore.WaterTicks);
        Assert.Equal(4, him.State);
        Assert.True(him.Out);
        // стрілки вибулого приймаються мовчки й нікуди не везуть
        var at = (him.X, him.Y);
        h.Input(1, "move", new { dir = 0 });
        h.Tick(20);
        Assert.Equal(at, (him.X, him.Y));
    }

    [Fact]
    public void Whoever_bumped_a_player_into_the_hole_gets_two_points_but_only_the_reveal_shows_them()
    {
        var h = Table(3, seed: 9, options: new { rounds = "3" });
        Go(h);
        var hunter = Me(h, 0);
        var prey = Me(h, 1);
        Park(h, hunter.Id, prey.Id, Me(h, 2).Id);
        NoCracks(h);
        Put(Me(h, 2), 750, 160);
        Core(h).Holes.Add(new SkateHole { X = 460, Y = 200, R = 30 });
        Put(prey, 400, 200);
        Put(hunter, 372, 200, vx: 1400);
        h.Tick(20);
        Assert.False(S(h, 1).Alive);
        Assert.Equal(1, S(h, 0).Kills);
        Assert.Equal(Skate.PtKill, S(h, 0).Total);
        foreach (int? seat in new int?[] { null, 1, 2 })
        {
            var seats = h.View(seat).GetProperty("seats").EnumerateArray().ToList();
            Assert.All(seats, s => Assert.Equal(0, s.GetProperty("total").GetInt32()));
            Assert.All(seats, s => Assert.Equal(JsonValueKind.Null, s.GetProperty("got").ValueKind));
        }
        while (G(h).Phase == Skate.PhaseGo) h.Tick();
        var open = h.View(null).GetProperty("seats").EnumerateArray().Single(s => s.GetProperty("seat").GetInt32() == 0);
        Assert.Equal(S(h, 0).Total, open.GetProperty("total").GetInt32());
        var row = h.View(null).GetProperty("reveal").GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("seat").GetInt32() == 0);
        Assert.Equal(1, row.GetProperty("kills").GetInt32());

        // штовхнув давно (понад 3 с до води) — не він зіпхнув
        var g = Table(3, seed: 10);
        Go(g);
        Park(g, Me(g, 0).Id, Me(g, 1).Id, Me(g, 2).Id);
        Put(Me(g, 2), 750, 160);
        Me(g, 1).LastBy = Me(g, 0).Id;
        Me(g, 1).LastAt = Core(g).Clock - SkateCore.CreditTicks - 5;
        Core(g).Holes.Add(new SkateHole { X = Me(g, 1).X / Fp, Y = Me(g, 1).Y / Fp, R = 30 });
        g.Tick();
        Assert.False(S(g, 1).Alive);
        Assert.Equal(0, S(g, 0).Kills);
    }

    [Fact]
    public void The_ice_cracks_five_seconds_before_a_hole_opens_and_holes_widen_at_the_end()
    {
        var h = Table(2, seed: 11);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        Put(Me(h, 0), 150, 160);
        Put(Me(h, 1), 750, 160);
        Core(h).Holes.Add(new SkateHole { X = 460, Y = 200, R = 30 });
        while (G(h).Left > Skate.CrackAt[0]) h.Tick();
        h.Tick();
        var crack = Ev(h).Single(e => e[0] == SkateCore.EvCrack)[1];
        var hs = LastFrame(h).GetProperty("h");
        Assert.Equal(0, hs[crack * 4 + 3].GetInt32());
        Assert.Equal(SkateCore.CrackR, hs[crack * 4 + 2].GetInt32());
        h.Tick(SkateCore.CrackWarn - 1);
        Assert.Equal(0, LastFrame(h).GetProperty("h")[crack * 4 + 3].GetInt32());
        h.Tick();
        Assert.Contains(Ev(h), e => e[0] == SkateCore.EvOpen && e[1] == crack);
        Assert.Equal(1, LastFrame(h).GetProperty("h")[crack * 4 + 3].GetInt32());
        // останні 30 с ополонки ширшають: перша на 1 од. щодві секунди
        var r0 = Core(h).Holes[0].R;
        while (G(h).Left > Skate.GrowFrom) h.Tick();
        h.Tick(Skate.GrowEvery * 5 + 1);
        Assert.InRange(Core(h).Holes[0].R, r0 + 4, r0 + 6);
        Assert.True(Core(h).Holes.Count >= 3);
    }

    [Fact]
    public void Nobody_starts_near_the_water_and_players_start_apart()
    {
        for (var seed = 1; seed <= 30; seed++)
        {
            var core = new SkateCore(new Random(seed));
            core.Deal([0, 1, 2, 3, 4, 5, 6, 7], 48);
            Assert.Equal(SkateCore.StartHoles, core.Holes.Count);
            foreach (var v in core.V)
                foreach (var hole in core.Holes)
                {
                    long dx = v.X / Fp - hole.X, dy = v.Y / Fp - hole.Y;
                    Assert.True(dx * dx + dy * dy >= (long)(hole.R + 28) * (hole.R + 28), $"сід {seed}: #{v.Id} біля води");
                }
            var players = core.V.Where(v => v.Owner >= 0).ToArray();
            for (var i = 0; i < players.Length; i++)
                for (var j = i + 1; j < players.Length; j++)
                {
                    long dx = players[i].X / Fp - players[j].X / Fp, dy = players[i].Y / Fp - players[j].Y / Fp;
                    Assert.True(dx * dx + dy * dy >= 96 * 96, $"сід {seed}");
                }
            Assert.Equal(56, core.V.Select(v => v.Name).Distinct().Count());
            Assert.All(core.V, v => Assert.Equal(0, SkateCore.Speed2(v)));
            Assert.All(core.Slots, s => Assert.True(s.Here && SkateMap.BoxFits(s.X, s.Y, 8)));
        }
    }

    // =============================================================================================
    // Ласощі
    // =============================================================================================

    [Fact]
    public void Only_a_slow_skater_picks_up_a_treat_and_it_comes_back_elsewhere()
    {
        var h = Table(2, seed: 13);
        Go(h);
        var bot = Bot(h);
        Park(h, bot.Id);
        var slot = Core(h).Slots[4];
        slot.X = 400;
        slot.Y = 200;
        slot.Wait = 0;
        Put(bot, 395, 200, vx: 1000);
        h.Tick();
        Assert.True(slot.Here);
        Put(bot, 398, 200, vx: 300);
        h.Tick();
        Assert.False(slot.Here);
        Assert.Contains(Ev(h), e => e[0] == SkateCore.EvPick && e[1] == bot.Id && e[2] == 4 && e[3] == slot.Kind);
        Assert.InRange(slot.Wait, SkateCore.RespawnMin - 1, SkateCore.RespawnMax);
        Assert.Equal(-1, LastFrame(h).GetProperty("it")[4 * 3 + 2].GetInt32());
        h.Tick(slot.Wait);
        Assert.True(slot.Here);
        Assert.NotEqual((400, 200), (slot.X, slot.Y));
        Assert.Equal(slot.Kind, LastFrame(h).GetProperty("it")[4 * 3 + 2].GetInt32());
    }

    [Fact]
    public void A_treat_from_the_list_counts_once_and_others_do_not_count_at_all()
    {
        var h = Table(2, seed: 15);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        Put(Me(h, 0), 200, 200);
        Put(Me(h, 1), 700, 200);
        var s = S(h, 0);
        var off = Enumerable.Range(0, 6).First(k => Array.IndexOf(s.List, k) < 0);
        Feed(h, 0, off);
        Assert.Equal(0, s.Items);
        Feed(h, 0, s.List[2]);
        Assert.Equal(1, s.Items);
        Assert.True(s.Got[2]);
        Feed(h, 0, s.List[2]);
        Assert.Equal(1, s.Items);
        Assert.Equal(Skate.PtItem, s.Total);
        var me = h.View(0).GetProperty("me");
        Assert.True(me.GetProperty("got")[2].GetBoolean());
        Assert.Equal(1, me.GetProperty("got").EnumerateArray().Count(x => x.GetBoolean()));
    }

    [Fact]
    public void Bots_pick_up_treats_too_and_every_pick_sends_views_whoever_it_was()
    {
        var h = Table(2, seed: 17);
        Go(h);
        var players = new[] { S(h, 0).Me, S(h, 1).Me };
        var picks = 0;
        for (var t = 0; t < 1500 && G(h).Phase == Skate.PhaseGo; t++)
        {
            var views = h.Outbox.OfType<RoomViews>().Count();
            h.Tick();
            var ev = Ev(h).Where(e => e[0] == SkateCore.EvPick).ToList();
            if (ev.Count == 0) continue;
            picks += ev.Count(e => Array.IndexOf(players, e[1]) < 0);
            Assert.True(h.Outbox.OfType<RoomViews>().Count() > views, "підхопили — види мають полетіти, хоч би хто");
        }
        output.WriteLine($"боти підхопили за хвилину: {picks}");
        Assert.InRange(picks, 5, 200);
    }

    // =============================================================================================
    // Боти
    // =============================================================================================

    [Fact]
    public void Bots_skate_circles_chase_each_other_go_for_treats_and_stand()
    {
        var core = new SkateCore(new Random(19));
        core.Deal([], 40);
        var modes = new HashSet<int>();
        var start = core.V.Select(v => (v.X, v.Y)).ToArray();
        var travelled = new long[core.N];
        var trips = 0;
        for (var t = 0; t < 3000; t++)
        {
            var was = core.V.Select(v => (v.X, v.Y)).ToArray();
            core.TimersAll();
            core.ThinkAll();
            core.StepAll();
            core.CollideAll();
            core.WaterAll();
            core.ItemsAll();
            trips += core.Ev.Count(e => e[0] == SkateCore.EvTrip);
            core.Ev.Clear();
            foreach (var v in core.V)
            {
                modes.Add(v.Mode);
                travelled[v.Id] += Math.Abs(v.X - was[v.Id].X) + Math.Abs(v.Y - was[v.Id].Y);
            }
        }
        Assert.Equal([0, 1, 2, 3, 4], modes.Order());
        Assert.All(travelled, d => Assert.True(d > 300L * Fp, $"{d / Fp}"));
        output.WriteLine($"падінь на рівному місці за 2 хв на 40 ботів: {trips}");
        Assert.True(trips > 0, "боти мали хоч раз упасти на рівному місці");
    }

    [Fact]
    public void Bots_keep_off_the_water_and_seldom_fall_in()
    {
        var core = new SkateCore(new Random(21));
        core.Deal([], 40);
        var splashes = 0;
        for (var t = 0; t < 2250; t++)
        {
            if (t is 1000 or 1200 or 1400 or 1600 or 1800) core.Crack();
            core.TimersAll();
            core.ThinkAll();
            core.StepAll();
            core.CollideAll();
            core.WaterAll();
            core.ItemsAll();
            core.IceAll(t > 1500 && t % 50 == 0);
            splashes += core.Ev.Count(e => e[0] == SkateCore.EvSplash);
            core.Ev.Clear();
        }
        output.WriteLine($"шубовснуло ботів за раунд: {splashes} (ополонок {core.Holes.Count})");
        Assert.InRange(splashes, 0, 25);
    }

    [Fact]
    public void A_bot_swerves_from_water_ahead_but_may_stand_right_by_a_hole()
    {
        var core = Bare(2, seed: 22);
        var bot = core.V.First(v => v.Owner < 0);
        core.Holes.Add(new SkateHole { X = 500, Y = 200, R = 30 });
        // стоїть за кілька одиниць від краю й нікуди не котиться — не тікає (людина-мисливець теж так чатує)
        Put(bot, 500 - 30 - SkateCore.R - 6, 200);
        for (var t = 0; t < 60; t++) core.Think(bot);
        Assert.Equal(-1, bot.Want);
        // котиться просто у воду — за 2–6 тиків «помічає» й тисне геть від ополонки
        Put(bot, 400, 200, vx: 1200);
        var swerved = -1;
        for (var t = 0; t < 10 && swerved < 0; t++)
        {
            core.Think(bot);
            if (bot.Want is 3 or 4 or 5) swerved = t;
        }
        Assert.InRange(swerved, 1, 7);
    }

    [Fact]
    public void A_fallen_bot_gets_up_after_two_seconds_and_skates_again()
    {
        var core = Bare(2, seed: 23);
        var bot = core.V.First(v => v.Owner < 0);
        Put(bot, 400, 200, vx: 500);
        bot.Fallen = SkateCore.FallTicks;
        for (var t = 0; t < SkateCore.FallTicks; t++)
        {
            core.TimersAll();
            core.ThinkAll();
            Assert.Equal(-1, bot.Want);
            core.StepAll();
        }
        Assert.True(bot.Upright);
        var at = (bot.X, bot.Y);
        for (var t = 0; t < 400; t++) { core.TimersAll(); core.ThinkAll(); core.StepAll(); }
        Assert.NotEqual(at, (bot.X, bot.Y));
    }

    // =============================================================================================
    // Раунд і фази
    // =============================================================================================

    [Fact]
    public void A_match_opens_with_three_seconds_to_look_around_when_everyone_skates_but_nobody_picks_up()
    {
        var h = Table(2, seed: 25);
        Assert.Equal(Skate.PhaseStart, G(h).Phase);
        Assert.Equal(Skate.StartTicks, G(h).Left);
        var me = Me(h, 0);
        var slot = Core(h).Slots[0];
        slot.X = me.X / Fp;
        slot.Y = me.Y / Fp;
        h.Input(0, "move", new { dir = 2 });
        var y = me.Y;
        h.Tick(5);
        Assert.True(me.Y > y);
        Assert.Equal(0, Core(h).Slots.Count(s => !s.Here));
        Assert.DoesNotContain(Events(h), e => e[0].GetInt32() == SkateCore.EvPick);
        h.Tick(Skate.StartTicks - 5);
        Assert.Equal(Skate.PhaseGo, G(h).Phase);
        Assert.Equal(Skate.RoundTicks, G(h).Left);
    }

    [Fact]
    public void After_ninety_seconds_the_fullest_basket_on_the_ice_wins_the_round()
    {
        var h = Table(2, seed: 27);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        NoCracks(h);
        Put(Me(h, 0), 200, 200);
        Put(Me(h, 1), 700, 200);
        Feed(h, 0, S(h, 0).List[0]);
        h.Tick(Skate.RoundTicks);
        Assert.Equal(Skate.PhaseReveal, G(h).Phase);
        var r = h.View(null).GetProperty("reveal");
        Assert.Equal("time", r.GetProperty("why").GetString());
        Assert.Equal([0], r.GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(Skate.PtItem + Skate.PtRound, S(h, 0).Total);
    }

    [Fact]
    public void Equal_baskets_at_the_end_of_time_give_a_round_without_a_winner()
    {
        var h = Table(2, seed: 29);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        NoCracks(h);
        Put(Me(h, 0), 200, 200);
        Put(Me(h, 1), 700, 200);
        Feed(h, 0, S(h, 0).List[0]);
        Feed(h, 1, S(h, 1).List[0]);
        h.Tick(Skate.RoundTicks);
        var r = h.View(null).GetProperty("reveal");
        Assert.Equal("none", r.GetProperty("why").GetString());
        Assert.Empty(r.GetProperty("winners").EnumerateArray());
        Assert.Equal(1, S(h, 0).Total);
    }

    [Fact]
    public void A_full_basket_ends_the_round_at_once_with_three_more_points()
    {
        var h = Table(2, seed: 31);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        Put(Me(h, 0), 200, 200);
        Put(Me(h, 1), 700, 200);
        foreach (var k in S(h, 0).List.Take(3)) Feed(h, 0, k);
        Assert.Equal(Skate.PhaseGo, G(h).Phase);
        Feed(h, 0, S(h, 0).List[3]);
        Assert.Equal(Skate.PhaseReveal, G(h).Phase);
        Assert.Equal("list", h.View(null).GetProperty("reveal").GetProperty("why").GetString());
        Assert.Equal(4 * Skate.PtItem + Skate.PtRound, S(h, 0).Total);
    }

    [Fact]
    public void Two_full_baskets_in_the_same_tick_both_win_the_round()
    {
        var h = Table(3, seed: 33);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id, Me(h, 2).Id);
        Put(Me(h, 0), 200, 200);
        Put(Me(h, 1), 700, 200);
        Put(Me(h, 2), 450, 160);
        foreach (var k in S(h, 0).List.Take(3)) Feed(h, 0, k);
        foreach (var k in S(h, 1).List.Take(3)) Feed(h, 1, k);
        Feed(h, 0, S(h, 0).List[3], tick: false);
        // ласощі одного виду — лише двоє на льоду; друге місце бере свій вид
        Feed(h, 1, S(h, 1).List[3], tick: false);
        if (S(h, 0).List[3] == S(h, 1).List[3])
        {
            var twins = Core(h).Slots.Where(s => s.Kind == S(h, 0).List[3]).ToArray();
            (twins[0].X, twins[0].Y) = (Me(h, 0).X / Fp, Me(h, 0).Y / Fp);
            (twins[1].X, twins[1].Y) = (Me(h, 1).X / Fp, Me(h, 1).Y / Fp);
            twins[0].Wait = twins[1].Wait = 0;
        }
        h.Tick();
        var r = h.View(null).GetProperty("reveal");
        Assert.Equal("list", r.GetProperty("why").GetString());
        Assert.Equal([0, 1], r.GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public void The_last_player_on_the_ice_wins_the_round()
    {
        var h = Table(3, seed: 35);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id, Me(h, 2).Id);
        Put(Me(h, 0), 200, 200);
        Put(Me(h, 1), 450, 200);
        Put(Me(h, 2), 700, 200);
        Core(h).Holes.Add(new SkateHole { X = 450, Y = 200, R = 30 });
        Core(h).Holes.Add(new SkateHole { X = 700, Y = 200, R = 30 });
        h.Tick();
        Assert.Equal(Skate.PhaseReveal, G(h).Phase);
        var r = h.View(null).GetProperty("reveal");
        Assert.Equal("last", r.GetProperty("why").GetString());
        Assert.Equal([0], r.GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public void The_reveal_lasts_six_seconds_freezes_everyone_and_the_next_round_starts_fresh()
    {
        var h = Table(2, seed: 37);
        Go(h);
        Feed(h, 0, S(h, 0).List[1]);
        while (G(h).Phase == Skate.PhaseGo) h.Tick();
        Assert.Equal(Skate.PhaseReveal, G(h).Phase);
        var frozen = Core(h).Pack();
        var oldV = Core(h).V;
        h.Tick(Skate.RevealTicks - 1);
        Assert.Equal(frozen, Core(h).Pack());
        Assert.Equal(Skate.PhaseReveal, G(h).Phase);
        h.Tick();
        Assert.Equal(Skate.PhaseStart, G(h).Phase);
        Assert.Equal(2, G(h).RoundNo);
        Assert.NotSame(oldV, Core(h).V);
        Assert.All(new[] { 0, 1 }, s => Assert.True(S(h, s).Alive));
        Assert.All(new[] { 0, 1 }, s => Assert.Equal(0, S(h, s).Items));
        Assert.All(new[] { 0, 1 }, s => Assert.All(S(h, s).Got, g => Assert.False(g)));
        Assert.Equal(SkateCore.StartHoles, Core(h).Holes.Count);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
    }

    [Fact]
    public void After_the_last_round_the_match_finishes_with_totals_in_the_journal()
    {
        var h = Table(2, seed: 39, options: new { rounds = "1" });
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        NoCracks(h);
        Put(Me(h, 0), 200, 200);
        Put(Me(h, 1), 700, 200);
        Feed(h, 0, S(h, 0).List[0]);
        h.Tick(Skate.RoundTicks + Skate.RevealTicks);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(Skate.PhaseOver, G(h).Phase);
        Assert.Equal([0], h.Finished.Single().Result.Winners);
        Assert.Contains("Ковзанка: Оля 4 : Петро 0", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Contains(h.Scores, s => s.Nick == "Оля" && s.Score == 4);
        Assert.Contains(h.Scores, s => s.Nick == "Петро" && s.Score == 0);
        var res = h.View(null).GetProperty("result");
        Assert.Equal("end", res.GetProperty("why").GetString());
    }

    [Fact]
    public void Equal_totals_make_shared_winners_and_all_zero_makes_a_draw()
    {
        var h = Table(3, seed: 41, options: new { rounds = "1" });
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id, Me(h, 2).Id);
        NoCracks(h);
        Put(Me(h, 0), 200, 200);
        Put(Me(h, 1), 700, 200);
        Put(Me(h, 2), 450, 160);
        Feed(h, 0, S(h, 0).List[0]);
        Feed(h, 1, S(h, 1).List[0]);
        h.Tick(Skate.RoundTicks + Skate.RevealTicks);
        Assert.Equal([0, 1], h.Finished.Single().Result.Winners);

        var g = Table(2, seed: 43, options: new { rounds = "1" });
        Go(g);
        Park(g, Me(g, 0).Id, Me(g, 1).Id);
        NoCracks(g);
        Put(Me(g, 0), 200, 200);
        Put(Me(g, 1), 700, 200);
        g.Tick(Skate.RoundTicks + Skate.RevealTicks);
        Assert.Empty(g.Finished.Single().Result.Winners);
        Assert.Contains("нічия", g.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Rounds_and_crowd_options_are_honoured_and_junk_falls_back()
    {
        Assert.Equal(1, Rounds("1"));
        Assert.Equal(5, Rounds("5"));
        Assert.Equal(3, Rounds("7"));
        static int Rounds(string r)
        {
            var h = Table(2, options: new { rounds = r });
            return Views.Json(G(h).View(null)).GetProperty("of").GetInt32();
        }
        Assert.Equal(2 + 20, Core(Table(2, options: new { crowd = "small" })).N);
        Assert.Equal(2 + 48, Core(Table(2, options: new { crowd = "big" })).N);
        Assert.Equal(3 + 28, Core(Table(3, options: new { crowd = "auto" })).N);
        Assert.Equal(8 + 40, Core(Table(8)).N);
        Assert.Equal(2 + 24, Core(Table(2, options: new { crowd = "бозна" })).N);
    }

    // =============================================================================================
    // Вихід, F5, «Ще раз»
    // =============================================================================================

    [Fact]
    public void A_leaver_becomes_a_bot_and_the_round_goes_on()
    {
        var h = Table(3, seed: 45);
        Go(h);
        var his = Me(h, 2);
        var n = Core(h).N;
        h.Leave("Ганна");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(-1, his.Owner);
        Assert.Equal(n, Core(h).N);
        var at = (his.X, his.Y);
        h.Tick(300);
        Assert.NotEqual(at, (his.X, his.Y));
        Assert.Equal(4 * n, LastFrame(h).GetProperty("v").GetArrayLength());
        var seat = h.View(null).GetProperty("seats").EnumerateArray().Single(s => s.GetProperty("seat").GetInt32() == 2);
        Assert.True(seat.GetProperty("out").GetBoolean());
        Assert.Equal("Ганна", seat.GetProperty("nick").GetString());
        while (G(h).RoundNo == 1 && h.Room.Status == RoomStatus.Playing) h.Tick();
        Assert.Equal(n, Core(h).N);
        Assert.Equal(2, Core(h).V.Count(v => v.Owner >= 0));
    }

    [Fact]
    public void When_only_one_player_remains_the_match_ends_in_his_favour()
    {
        var h = Table(2, seed: 47);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        Put(Me(h, 1), 700, 200);
        Feed(h, 1, S(h, 1).List[0]);
        h.Leave("Оля");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Finished.Single().Result.Winners);
        Assert.Contains("Петро катається на ставку сам-на-сам", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Contains(h.Scores, s => s.Nick == "Петро" && s.Score == 1);
        var v = h.View(1);
        Assert.Equal("left", v.GetProperty("result").GetProperty("why").GetString());
        Assert.Equal(2, v.GetProperty("reveal").GetProperty("ids").GetArrayLength());
    }

    [Fact]
    public void Rematch_gives_a_clean_match_with_rotated_seats()
    {
        var h = Table(2, seed: 49, options: new { rounds = "1" });
        Go(h);
        Feed(h, 0, S(h, 0).List[0]);
        for (var i = 0; i < 3000 && h.Room.Status == RoomStatus.Playing; i++) h.Tick();
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        h.Rematch("Оля");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("Петро", h.NickOf(0));
        Assert.Equal(Skate.PhaseStart, G(h).Phase);
        Assert.Equal(1, G(h).RoundNo);
        Assert.All(new[] { 0, 1 }, s => Assert.Equal(0, S(h, s).Total));
        Assert.Equal("Петро", h.View(null).GetProperty("seats")[0].GetProperty("nick").GetString());
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("result").ValueKind);
    }

    [Fact]
    public void Input_from_a_wet_player_or_during_the_reveal_is_swallowed_silently()
    {
        var h = Table(3, seed: 51);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id, Me(h, 2).Id);
        NoCracks(h);
        Put(Me(h, 0), 200, 200);
        Put(Me(h, 2), 700, 200);
        var him = Put(Me(h, 1), 450, 200);
        Core(h).Holes[0] = new SkateHole { X = 450, Y = 200, R = 30 };
        h.Tick();
        Assert.False(S(h, 1).Alive);
        Assert.True(h.Act(1, "move", new { dir = 0 }).Ok);        // мокрому — «так», але без жодного поштовху
        Assert.Equal(-1, him.Want);
        Assert.False(h.Act(0, "move", new { dir = 9 }).Ok);
        Assert.Equal("Такого напрямку нема", h.Reply.Message);
        h.Tick(Skate.RoundTicks);
        Assert.Equal(Skate.PhaseReveal, G(h).Phase);
        Assert.True(h.Act(0, "move", 2).Ok);
        Assert.Equal(-1, Me(h, 0).Want);
    }

    [Fact]
    public void A_held_direction_that_is_not_confirmed_for_three_seconds_is_let_go()
    {
        var h = Table(2, seed: 53);
        Go(h);
        var me = Me(h, 0);
        Park(h, me.Id, Me(h, 1).Id);
        Put(Me(h, 1), 700, 160);
        Put(me, 150, 240);
        h.Input(0, "move", new { dir = 0 });
        for (var i = 0; i < 2; i++)
        {
            h.Tick(25);
            h.Input(0, "move", new { dir = 0 });
        }
        Assert.Equal(1, me.State);
        h.Tick(Skate.MoveHoldTicks + 1);
        Assert.Equal(-1, me.Want);
        Assert.NotEqual(1, me.State);
        h.Tick(400);
        var at = (me.X, me.Y);
        h.Tick(20);
        Assert.Equal(at, (me.X, me.Y));
    }

    [Fact]
    public void A_match_that_has_not_started_refuses_input_and_shows_a_quiet_pond()
    {
        var h = new RoomHarness("skate", seed: 1);
        h.Join("Оля");
        h.Join("Петро");
        var view = h.View(0);
        Assert.Equal("lobby", view.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("me").ValueKind);
        Assert.Equal(24 * 4, view.GetProperty("v").GetArrayLength());
        Assert.Equal(SkateCore.Items * 3, view.GetProperty("it").GetArrayLength());
        var game = (Skate)h.Room.Game;
        Assert.Equal("Партія ще не почалась", game.Act(0, "move", Views.Payload(new { dir = 1 })).Message);
    }

    // =============================================================================================
    // Приховане
    // =============================================================================================

    [Fact]
    public void The_frame_has_only_tick_phase_time_skaters_holes_treats_and_events()
    {
        var h = Table(5, seed: 55);
        Go(h);
        h.Tick();
        var f = LastFrame(h);
        Assert.Equal(["t", "ph", "left", "v", "h", "it", "ev"], f.EnumerateObject().Select(p => p.Name));
        Assert.Equal(4 * Core(h).N, f.GetProperty("v").GetArrayLength());
        Assert.Equal(4 * Core(h).Holes.Count, f.GetProperty("h").GetArrayLength());
        Assert.Equal(3 * SkateCore.Items, f.GetProperty("it").GetArrayLength());
        Assert.All(f.GetProperty("v").EnumerateArray(), e => Assert.Equal(JsonValueKind.Number, e.ValueKind));
    }

    [Fact]
    public void The_frame_never_mentions_seats_owners_lists_or_nicks()
    {
        var h = Table(4, seed: 57);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        Put(Me(h, 0), 200, 200);
        Feed(h, 0, S(h, 0).List[0]);
        Put(Me(h, 1), 380, 200, vx: 1300);
        Put(Bot(h), 402, 200);
        h.Tick(5);
        foreach (var f in h.Outbox.OfType<RoomFrame>())
        {
            var text = Views.Text(f.Frame);
            foreach (var word in new[] { "\"seat", "\"me", "\"owner", "\"list", "\"got", "\"nick", "\"alive" })
                Assert.DoesNotContain(word, text);
        }
    }

    [Fact]
    public void Player_ids_are_shuffled_among_bots_across_seeds()
    {
        int high = 0, notFirst = 0;
        for (var seed = 1; seed <= 100; seed++)
        {
            var core = new SkateCore(new Random(seed));
            core.Deal([0, 1], 24);
            var ids = core.V.Where(v => v.Owner >= 0).Select(v => v.Id).ToArray();
            if (ids.Any(id => id >= core.N / 2)) high++;
            if (ids.Any(id => id >= 2)) notFirst++;
        }
        Assert.True(high > 50, $"{high}");
        Assert.True(notFirst > 90, $"{notFirst}");
    }

    /// <summary>Вид без <c>me</c> — щоб порівняти два види.</summary>
    static string Strip(JsonElement view) =>
        Views.Text(view.EnumerateObject().Where(p => p.Name != "me").ToDictionary(p => p.Name, p => p.Value));

    [Fact]
    public void A_watcher_has_no_me_and_other_seats_see_the_same_view_but_their_own_me()
    {
        var h = Table(3, seed: 59);
        Go(h);
        var watcher = h.View(null);
        Assert.Equal(JsonValueKind.Null, watcher.GetProperty("me").ValueKind);
        Assert.Equal(JsonValueKind.Null, watcher.GetProperty("reveal").ValueKind);
        Assert.Empty(watcher.GetProperty("dead").EnumerateArray());
        var mine = h.View(0).GetProperty("me");
        Assert.Equal(S(h, 0).Me, mine.GetProperty("id").GetInt32());
        Assert.Equal(S(h, 0).List, mine.GetProperty("list").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(Strip(watcher), Strip(h.View(1)));
        Assert.Equal(Strip(watcher), Strip(h.View(0)));
        Assert.DoesNotContain("\"list\"", Strip(h.View(0)));
    }

    [Fact]
    public void A_wet_player_sees_what_a_watcher_sees_plus_his_own_me()
    {
        var h = Table(3, seed: 61);
        Go(h);
        var him = Me(h, 1);
        Park(h, Me(h, 0).Id, him.Id, Me(h, 2).Id);
        Put(Me(h, 0), 200, 200);
        Put(Me(h, 2), 700, 200);
        Put(him, 450, 200);
        Core(h).Holes.Add(new SkateHole { X = 450, Y = 200, R = 30 });
        h.Tick();
        var wet = h.View(1);
        Assert.False(wet.GetProperty("me").GetProperty("alive").GetBoolean());
        Assert.Equal(Strip(h.View(null)), Strip(wet));
        Assert.Equal([him.Id], wet.GetProperty("dead").EnumerateArray().Select(d => d.GetProperty("id").GetInt32()));
        Assert.DoesNotContain("\"ids\"", Views.Text(wet));
    }

    [Fact]
    public void Who_was_who_appears_only_in_the_reveal_and_after_the_match()
    {
        var h = Table(2, seed: 63, options: new { rounds = "1" });
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id);
        NoCracks(h);
        Put(Me(h, 0), 200, 200);
        Put(Me(h, 1), 700, 200);
        h.Tick(Skate.RoundTicks - 1);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
        h.Tick();
        var ids = h.View(null).GetProperty("reveal").GetProperty("ids").EnumerateArray()
            .Select(e => (e.GetProperty("seat").GetInt32(), e.GetProperty("id").GetInt32())).ToList();
        Assert.Equal([(0, S(h, 0).Me), (1, S(h, 1).Me)], ids);
        Assert.Equal(2, h.View(null).GetProperty("reveal").GetProperty("trails").GetArrayLength());
        h.Tick(Skate.RevealTicks);
        Assert.Equal(Skate.PhaseOver, G(h).Phase);
        Assert.NotEqual(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
    }

    [Fact]
    public void Per_tick_steps_and_states_of_players_on_the_ice_are_a_subset_of_those_of_bots()
    {
        var h = Table(4, seed: 65);
        var rng = new Random(3);
        var core = Core(h);
        var bots = new HashSet<(int, int, int)>();
        var players = new HashSet<(int, int, int)>();
        // крок за тик кошиком (0, 1, 2–3, 4–5, 6+), чи навскоси, і стан
        var was = core.V.Select(v => (v.X / Fp, v.Y / Fp)).ToArray();
        for (var t = 0; t < 2000 && G(h).Phase is Skate.PhaseStart or Skate.PhaseGo; t++)
        {
            if (t % 9 == 0)
                for (var s = 0; s < 4; s++) h.Input(s, "move", new { dir = rng.Next(-1, 9) });
            h.Tick();
            foreach (var v in core.V)
            {
                int dx = Math.Abs(v.X / Fp - was[v.Id].Item1), dy = Math.Abs(v.Y / Fp - was[v.Id].Item2);
                var step = (SkateTrace.SpeedBin(Math.Max(dx, dy)), dx > 0 && dy > 0 ? 1 : 0, v.State);
                was[v.Id] = (v.X / Fp, v.Y / Fp);
                // хто шубовснув (3) і вибув (4), той уже названий усім — його кроки не таємниця
                if (v.State < 3 || v.State == 5) (v.Owner >= 0 ? players : bots).Add(step);
            }
        }
        output.WriteLine("лише в гравців: " + string.Join(" ", players.Except(bots)));
        Assert.Subset(bots, players);
    }

    [Fact]
    public void Players_push_glide_and_brake_in_runs_like_bots_do()
    {
        var (p, b) = Pond.Value;
        output.WriteLine($"поштовх: люди {Show(p.PushRuns, 7)}");
        output.WriteLine($"         боти {Show(b.PushRuns, 7)}");
        output.WriteLine($"котиться: люди {Show(p.GlideRuns, 7)}");
        output.WriteLine($"          боти {Show(b.GlideRuns, 7)}");
        output.WriteLine($"гальмо: люди {Show(p.BrakeRuns, 7)}");
        output.WriteLine($"        боти {Show(b.BrakeRuns, 7)}");
        output.WriteLine($"поворот (0 — той самий, 4 — назад): люди {Show(p.Turns, 5)}");
        output.WriteLine($"                                     боти {Show(b.Turns, 5)}");
        Assert.Subset(b.PushRuns.Keys.ToHashSet(), p.PushRuns.Keys.ToHashSet());
        Assert.Subset(b.GlideRuns.Keys.ToHashSet(), p.GlideRuns.Keys.ToHashSet());
        Assert.Subset(b.BrakeRuns.Keys.ToHashSet(), p.BrakeRuns.Keys.ToHashSet());
        Assert.Subset(b.Turns.Keys.ToHashSet(), p.Turns.Keys.ToHashSet());
        // що часто в людей — не рідкість і в ботів (не менше половини людської частки)
        foreach (var (from, to) in new[] { (0, 2), (3, 3), (4, 4) })
            Assert.True(SkateTrace.Share(b.PushRuns, from, to) * 2 >= SkateTrace.Share(p.PushRuns, from, to), $"поштовхи {from}–{to}");
        foreach (var (from, to) in new[] { (0, 2), (3, 3), (4, 6) })
            Assert.True(SkateTrace.Share(b.GlideRuns, from, to) * 2 >= SkateTrace.Share(p.GlideRuns, from, to), $"котиться {from}–{to}");
        foreach (var k in new[] { 0, 1, 2, 4 })
            Assert.True(SkateTrace.Share(b.Turns, k, k) * 2 >= SkateTrace.Share(p.Turns, k, k), $"поворот {k}");
        // і гальмують так само часто на поштовх
        var pb = p.BrakeRuns.Values.Sum() * 1000 / p.PushRuns.Values.Sum();
        var bb = b.BrakeRuns.Values.Sum() * 1000 / b.PushRuns.Values.Sum();
        Assert.True(bb * 2 >= pb && pb * 2 >= bb, $"гальм на тисячу поштовхів: люди {pb}, боти {bb}");
    }

    [Fact]
    public void Players_skate_as_fast_or_as_slow_as_bots_do()
    {
        var (p, b) = Pond.Value;
        string Speeds(SkateTrace s) => string.Join(" ", Enumerable.Range(0, 5).Select(k => $"{k}:{s.SpeedShare(k) / 10.0:F1}%"));
        output.WriteLine($"швидкість за тик: люди {Speeds(p)}");
        output.WriteLine($"                  боти {Speeds(b)}");
        output.WriteLine($"падінь на 10 000 тиків: люди {p.Falls * 10000 / p.Ticks}, боти {b.Falls * 10000 / b.Ticks}");
        Assert.Subset(b.Speed.Keys.ToHashSet(), p.Speed.Keys.ToHashSet());
        for (var k = 0; k < 4; k++)
        {
            int ps = p.SpeedShare(k), bs = b.SpeedShare(k);
            Assert.True(bs * 2 >= ps && ps * 2 >= bs, $"кошик {k}: люди {ps}, боти {bs}");
        }
        Assert.True(b.Falls * p.Ticks * 3 >= p.Falls * b.Ticks, "боти падають утричі рідше за людей");
    }

    [Fact]
    public void Players_stand_still_as_long_or_as_short_as_bots_do()
    {
        var (p, b) = Pond.Value;
        output.WriteLine($"стоїть: люди {Show(p.StillRuns, 7)}");
        output.WriteLine($"        боти {Show(b.StillRuns, 7)}");
        Assert.Subset(b.StillRuns.Keys.ToHashSet(), p.StillRuns.Keys.ToHashSet());
        Assert.True(SkateTrace.Share(b.StillRuns, 0, 1) * 2 >= SkateTrace.Share(p.StillRuns, 0, 1));
        Assert.True(SkateTrace.Share(b.StillRuns, 4, 6) * 2 >= SkateTrace.Share(p.StillRuns, 4, 6));
    }

    // =============================================================================================
    // Контракт і детермінізм
    // =============================================================================================

    [Fact]
    public void The_server_accepts_exactly_what_the_module_sends()
    {
        var h = Table(2, seed: 67);
        var me = Me(h, 0);
        h.Input(0, "move", new { dir = 0 });            // як шле модуль: тримаю →
        Assert.Equal(0, me.Want);
        h.Input(0, "move", new { dir = 7 });            // ↑ + → — діагональ
        Assert.Equal(7, me.Want);
        h.Input(0, "move", new { dir = 8 });            // пробіл — гальмо
        Assert.Equal(8, me.Want);
        h.Input(0, "move", 3);                           // голе число теж
        Assert.Equal(3, me.Want);
        h.Input(0, "move", new { dir = -1 });           // відпустив
        Assert.Equal(-1, me.Want);
        var before = Views.Text(h.View(0));
        foreach (var (action, payload) in new (string, object?)[] { ("move", new { dir = "up" }), ("move", new { d = 1 }), ("move", 9), ("move", -2), ("jump", null), ("shoot", new { }) })
            Assert.False(h.Act(0, action, payload).Ok);
        Assert.Equal(-1, me.Want);
        Assert.Equal(before, Views.Text(h.View(0)));
    }

    [Fact]
    public void The_module_sends_only_move_and_its_numbers_match_the_server()
    {
        var js = File.ReadAllText(Path.Combine(FindRoot(), "web", "games", "skate.js"));
        var sent = System.Text.RegularExpressions.Regex.Matches(js, @"ctx\.(?:act|input)\('(\w+)'").Select(m => m.Groups[1].Value).Distinct().ToArray();
        Assert.Equal(["move"], sent);
        Assert.Contains("ctx.input('move', { dir: d })", js);
        Assert.Contains("ctx.input('move', { dir: -1 })", js);
        int Const(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(js, @"\b" + name + @"\s*=\s*(\d+)");
            Assert.True(m.Success, name);
            return int.Parse(m.Groups[1].Value);
        }
        Assert.Equal(Skate.TickMs, Const("TICK_MS"));
        Assert.Equal(SkateMap.Cell, Const("CELL"));
        Assert.Equal(SkateMap.WorldW, Const("WW"));
        Assert.Equal(SkateMap.WorldH, Const("WH"));
        Assert.Equal(SkateCore.R, Const("BODY_R"));
        Assert.Equal(SkateCore.PickR, Const("PICK_R"));
        Assert.Equal(SkateCore.WaterTicks, Const("WATER_TICKS"));
        Assert.Equal(SkateCore.Items, Const("ITEMS"));
        Assert.Equal(SkateCore.FallTicks, Const("FALL_TICKS"));
    }

    static List<string> Replay(int seed)
    {
        var h = Table(3, seed: seed);
        var frames = new List<string>();
        for (var t = 0; t < 900; t++)
        {
            if (t % 11 == 0) h.Input(t % 3, "move", new { dir = t % 10 - 1 });
            h.Tick();
            frames.Add(Views.Text(((RoomFrame)h.Outbox.Last(o => o is RoomFrame)).Frame));
        }
        frames.Add(Views.Text(h.View(0)));
        return frames;
    }

    [Fact]
    public void Same_seed_and_same_inputs_give_byte_identical_frames()
    {
        var a = Replay(69);
        Assert.Equal(a, Replay(69));
        Assert.NotEqual(a, Replay(70));
    }

    [Fact]
    public void Views_on_the_wire_match_the_spec_shape()
    {
        var h = Table(2, seed: 71);
        Go(h);
        var v = h.View(0);
        foreach (var key in new[] { "phase", "round", "of", "left", "t", "width", "height", "cell", "n", "map", "kinds", "looks", "names", "v", "h", "it", "seats", "dead", "me", "reveal", "result", "turn" })
            Assert.True(Views.Has(v, key), key);
        var n = v.GetProperty("n").GetInt32();
        Assert.Equal(30, v.GetProperty("width").GetInt32());
        Assert.Equal(20, v.GetProperty("map").GetArrayLength());
        Assert.Equal(4 * n, v.GetProperty("looks").GetArrayLength());
        Assert.Equal(n, v.GetProperty("names").GetArrayLength());
        var kind = v.GetProperty("kinds")[0];
        Assert.Equal("Бублик", kind.GetProperty("name").GetString());
        foreach (var key in new[] { "k", "what", "emoji" }) Assert.True(Views.Has(kind, key), key);
        var me = v.GetProperty("me");
        foreach (var key in new[] { "id", "list", "got", "alive" }) Assert.True(Views.Has(me, key), key);
        var seat = v.GetProperty("seats")[0];
        foreach (var key in new[] { "seat", "nick", "alive", "out", "got", "total" }) Assert.True(Views.Has(seat, key), key);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
        output.WriteLine($"вид на {n} селян: {Views.Text(G(h).View(0)).Length} Б");
    }

    [Fact]
    public void The_catalog_lists_skate_as_live_by_host_hidden_tick_forty_with_its_own_css()
    {
        var info = new Skate().Info;
        Assert.Equal(("skate", "Ковзанка", "ковзанку"), (info.Id, info.Title, info.Accusative));
        Assert.Equal(GameGroup.Live, info.Group);
        Assert.Equal((2, 8), (info.MinPlayers, info.MaxPlayers));
        Assert.Equal(40, info.TickMs);
        Assert.Equal(StartMode.ByHost, info.Start);
        Assert.True(info.Hidden);
        Assert.False(info.Rated);
        Assert.Equal(ScoreOrder.HigherIsBetter, info.Score);
        Assert.Equal(["rounds", "crowd"], info.Options!.Select(o => o.Key));
        var root = FindRoot();
        Assert.True(File.Exists(Path.Combine(root, "web", "games", "skate.js")));
        Assert.True(File.Exists(Path.Combine(root, "web", "games", "skate.css")));
        Assert.Equal("фіолетовий", new Skate().SeatName(6));
    }

    [Fact]
    public void Ram_and_steady_feet_achievements_are_requested_exactly_when_earned()
    {
        Assert.NotNull(AchievementCatalog.Get("skate-ram"));
        Assert.NotNull(AchievementCatalog.Get("skate-clean"));
        // Оля зіпхнула Ганну; Петро тихо зібрав кошик і ні разу не впав — обоє по ачівці
        var h = Table(3, seed: 73);
        Go(h);
        Park(h, Me(h, 0).Id, Me(h, 1).Id, Me(h, 2).Id);
        Put(Me(h, 1), 700, 160);
        Put(Me(h, 2), 400, 200);
        Put(Me(h, 0), 372, 200, vx: 1400);
        Core(h).Holes.Add(new SkateHole { X = 460, Y = 200, R = 30 });
        h.Tick(20);
        Assert.False(S(h, 2).Alive);
        foreach (var k in S(h, 1).List) Feed(h, 1, k);
        Assert.Equal(Skate.PhaseReveal, G(h).Phase);
        var awards = h.Awards.Select(a => (a.Nick, a.Reason, a.Shards)).ToList();
        Assert.Contains(("Оля", "ach:skate-ram", 0), awards);
        Assert.Contains(("Петро", "ach:skate-clean", 0), awards);
        Assert.Equal(2, awards.Count);

        // упав хоч раз — кошик є, а «Твердо на ногах» нема
        var g = Table(2, seed: 75);
        Go(g);
        Park(g, Me(g, 0).Id, Me(g, 1).Id);
        Put(Me(g, 0), 200, 200);
        Put(Me(g, 1), 700, 200);
        Me(g, 0).Falls = 1;
        foreach (var k in S(g, 0).List) Feed(g, 0, k);
        Assert.Equal(Skate.PhaseReveal, G(g).Phase);
        Assert.Empty(g.Awards);
    }

    // =============================================================================================
    // Швидкодія
    // =============================================================================================

    [Fact]
    [Trait("Category", "Perf")]
    public void Three_thousand_ticks_with_eight_players_and_forty_eight_bots_fit_in_a_second()
    {
        long best = long.MaxValue;
        for (var attempt = 0; attempt < 3 && best >= 1000; attempt++)
        {
            var h = Table(8, seed: 80 + attempt, options: new { crowd = "big", rounds = "5" });
            var rng = new Random(attempt);
            var sw = Stopwatch.StartNew();
            for (var t = 0; t < 3000 && h.Room.Status == RoomStatus.Playing; t++)
            {
                if (t % 10 == 0)
                    for (var s = 0; s < 8; s++) h.Input(s, "move", new { dir = rng.Next(-1, 9) });
                h.Tick();
            }
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }
        var pure = PureTickMicros();
        output.WriteLine($"3000 тиків через кімнату: {best} мс; чистий Tick() + Frame(): {pure:F1} мкс");
        Assert.True(best < 1000, $"{best} мс");
        Assert.True(pure < 250, $"{pure} мкс на тик");
    }

    /// <summary>Середній Tick() + Frame() на 56 селянах без кімнати й розсилки, найкращий із трьох заходів.</summary>
    static double PureTickMicros()
    {
        var bestUs = double.MaxValue;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var h = Table(8, seed: 90 + attempt, options: new { crowd = "big", rounds = "5" });
            var game = G(h);
            Go(h);
            var rng = new Random(attempt);
            for (var i = 0; i < 200; i++) game.Tick();
            var sw = Stopwatch.StartNew();
            var n = 0;
            for (var t = 0; t < 3000 && game.Phase != Skate.PhaseOver; t++)
            {
                if (t % 10 == 0)
                    for (var s = 0; s < 8; s++) game.Act(s, "move", Views.Payload(new { dir = rng.Next(-1, 9) }));
                if (game.Tick().Frame) game.Frame();
                n++;
            }
            bestUs = Math.Min(bestUs, sw.Elapsed.TotalMicroseconds / Math.Max(1, n));
        }
        return bestUs;
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Fifty_six_skaters_in_a_heap_collide_pairwise_fast_and_the_grid_beats_brute_force()
    {
        var core = new SkateCore(new Random(77));
        core.Deal([0, 1, 2, 3, 4, 5, 6, 7], 48);
        core.Holes.Clear();
        var rng = new Random(78);
        void Heap()
        {
            var i = 0;
            foreach (var q in core.V)
            {
                Put(q, 300 + i % 8 * 18, 150 + i / 8 * 14, rng.Next(-1500, 1501), rng.Next(-1500, 1501));
                i++;
            }
        }
        Heap();
        var contacts = core.Pairs().Count;
        output.WriteLine($"купа з 56: {contacts} пар у дотику");
        Assert.True(contacts >= 80, $"{contacts}");
        double Time(Action a)
        {
            var best = double.MaxValue;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                Heap();
                a();
                var sw = Stopwatch.StartNew();
                for (var k = 0; k < 2000; k++) a();
                best = Math.Min(best, sw.Elapsed.TotalMicroseconds / 2000);
            }
            return best;
        }
        var grid = Time(() => core.Pairs());
        var brute = Time(() => core.PairsBrute());
        var full = Time(() => { core.StepAll(); core.CollideAll(); core.Ev.Clear(); });
        output.WriteLine($"пари: сітка {grid:F1} мкс, перебір {brute:F1} мкс; крок + зіткнення купи: {full:F1} мкс");
        Assert.True(grid < brute, "сітка мала бути швидша за перебір");
        Assert.True(full < 100, $"{full} мкс");
    }

    [Fact]
    public void A_frame_with_fifty_six_skaters_serialises_under_1300_bytes()
    {
        var h = Table(8, seed: 79, options: new { crowd = "big" });
        Go(h);
        Assert.Equal(56, Core(h).N);
        var max = 0;
        for (var t = 0; t < 2000 && G(h).Phase == Skate.PhaseGo; t++)
        {
            h.Tick();
            max = Math.Max(max, FrameText(h).Length);
        }
        output.WriteLine($"кадр на 56 селян: найбільший {max} Б (ополонок {Core(h).Holes.Count})");
        Assert.True(max < 1300, $"{max} Б");
    }
}
