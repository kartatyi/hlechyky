using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Аерохокей на двох або двоє на двоє: біти йдуть за мишкою чи пальцем у своїй половині, шайба дзвенить об
/// борти, до 5/7/10 голів. Чотири хвилини без переможця — лідер бере партію, а при рівному рахунку ще дві
/// хвилини «золотого гола». Стіл і фізика — <see cref="HockeyCore"/>; тут — фази, рахунок, годинник, вид, кадр,
/// вихід посеред партії й ачівки. Spec: <c>docs/games/specs/hockey.md</c>.
/// </summary>
public sealed class Hockey : Game
{
    public const int MatchTicks = 6000, GoldenTicks = 3000;
    public const int PhReady = 0, PhGo = 1, PhOver = 3, PhLobby = 4;

    static readonly GameOption GoalsOption = new("goals", "Грати до", [("5", "5 голів"), ("7", "7 голів"), ("10", "10 голів")], "7");
    static readonly string[] TeamNames = ["сині", "руді"];

    public override GameInfo Info { get; } = new(
        "hockey", "Аерохокей", "аерохокей", GameGroup.Live, 2, HockeyCore.Seats, TickMs: HockeyCore.TickMs,
        Start: StartMode.ByHost, Options: [GoalsOption],
        Hint: "Стіл, шайба, біти. Води біту мишкою чи пальцем, не пропусти. На двох або двоє на двоє (утрьох — з 🤖 ботом), до семи");

    HockeyCore? _core;
    bool _started;
    bool _over;
    int _target = 7;
    int _left = MatchTicks;
    bool _golden;
    int? _winner;
    /// <summary>Найбільше відставання кожної команди за партію — для «Камбека».</summary>
    readonly int[] _deficit = new int[2];
    string[] _startNicks = [];
    readonly Series _series = new();
    /// <summary>Останній гол партії — для підпису під «ГОЛ!» (автогол, з-під борту): команда, номер розіграшу, прапорці.</summary>
    (int Team, int N, bool Own, bool Rail)? _lastGoal;
    /// <summary>Місце бота-напарника (утрьох — п. 180) або −1. Бот не сидить за столом: ні нагород, ні рахунку.</summary>
    int _bot = -1;
    public int Bot => _bot;

    public HockeyCore Core
    {
        get
        {
            if (_core is not null) return _core;
            _core = new HockeyCore(Ctx.Rng);
            _core.Reset(WithBot(Seated()));
            return _core;
        }
    }

    public int Target => _target;
    /// <summary>Тиків до стелі часу (сетер — для тестів годинника).</summary>
    public int Left { get => _left; set => _left = value; }
    public bool Done => _over;
    public bool Golden => _golden;

    bool[] Seated() => [.. Enumerable.Range(0, HockeyCore.Seats).Select(Ctx.Seated)];

    /// <summary>Утрьох бот займає вільне місце — і за парністю місць стає в пару до самотнього.</summary>
    static int BotSeat(bool[] seated) => seated.Count(x => x) == 3 ? Array.IndexOf(seated, false) : -1;

    static bool[] WithBot(bool[] seated)
    {
        var b = BotSeat(seated);
        if (b >= 0) seated[b] = true;
        return seated;
    }

    bool Lobby => !_started || (_over && Enumerable.Range(0, HockeyCore.Seats)
        .Any(s => Ctx.Seated(s) && !_startNicks.Contains(Ctx.NickOf(s) ?? "", StringComparer.OrdinalIgnoreCase)));

    public override string SeatName(int seat)
    {
        if (seat is < 0 or >= HockeyCore.Seats) return base.SeatName(seat);
        // Вільне місце (чи місце, що не грає цю партію) — за парністю, як на столі на чотирьох; зайняте — за командою.
        int team;
        if (Lobby || _core is null) team = Ctx.Seated(seat) ? HockeyCore.TeamOf(seat, Seated()) : seat % 2;
        else team = Core.Plays[seat] ? Core.Team[seat] : seat % 2;
        return team == 0 ? "синій" : "рудий";
    }

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _target = options.TryGetValue("goals", out var v) && int.TryParse(v, out var n) && n is 5 or 7 or 10 ? n : 7;
    }

    public override void Start()
    {
        _started = true;
        _over = false;
        _winner = null;
        _golden = false;
        _left = MatchTicks;
        _deficit[0] = _deficit[1] = 0;
        _lastGoal = null;
        _startNicks = [.. Enumerable.Range(0, HockeyCore.Seats).Where(Ctx.Seated).Select(s => Ctx.NickOf(s) ?? "")];
        _series.Begin(Ctx, HockeyCore.Seats);
        var seated = Seated();
        _bot = BotSeat(seated);
        Core.Reset(WithBot(seated));
    }

    // ---------- ввід ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (!_started) return ActResult.Fail("Партія ще не почалась");
        if (seat is < 0 or >= HockeyCore.Seats || !Core.Plays[seat]) return ActResult.Fail("Ти тут не граєш");
        if (_over) return ActResult.Fail("Партію вже зіграно");
        switch (action)
        {
            case "to":
                // Світові координати: клієнт уже розвернув свій екран у світ; у половину підтягує сервер.
                if (Num(payload, "x") is not { } x || Num(payload, "y") is not { } y) return ActResult.Fail("Тут так не ходять");
                // швидкість руки (необов'язкова): сервер веде ціль нею далі, поки летить наступний намір
                Core.Aim(seat, x, y, Num(payload, "vx") ?? 0, Num(payload, "vy") ?? 0);
                return ActResult.Done;
            case "move":
                if (payload.ValueKind != JsonValueKind.Object) return ActResult.Fail("Тут так не ходять");
                var dx = Axis(payload, "dx");
                var dy = Axis(payload, "dy");
                if (dx is null || dy is null) return ActResult.Fail("Тут так не ходять");
                Core.Move(seat, dx.Value, dy.Value);
                return ActResult.Done;
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    static double? Num(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;

    /// <summary>Вісь напрямку: число → знак (−1/0/1); нема поля — 0; не число — відмова (null).</summary>
    static int? Axis(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var p)) return 0;
        return p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var n) && double.IsFinite(n) ? Math.Sign(n) : null;
    }

    // ---------- тик ----------

    public override TickResult Tick()
    {
        if (_over) return TickResult.None;
        var c = Core;
        if (_bot >= 0)
        {
            // на місце бота сіла людина — вона й грає цією битою
            if (Ctx.Seated(_bot)) _bot = -1;
            else c.BotThink(_bot);
        }
        var wasReady = c.StartIn > 0;
        var scored = c.Step();
        if (wasReady && c.StartIn > 0) return c.Still && c.T % 5 != 0 ? TickResult.None : TickResult.FrameOnly;
        // Годинник іде з самого свистка: шайба в цьому тику вже летить.
        _left--;
        if (scored >= 0)
        {
            _lastGoal = (scored, c.N, c.GoalOwn, c.GoalRail);
            var other = 1 - scored;
            _deficit[other] = Math.Max(_deficit[other], c.S[scored] - c.S[other]);
            if (_golden || c.S[scored] >= _target) return Over(scored);
            return TickResult.Both;
        }
        if (_left <= 0)
        {
            if (c.S[0] != c.S[1]) return Over(c.S[0] > c.S[1] ? 0 : 1);
            if (!_golden)
            {
                _golden = true;
                _left = GoldenTicks;
                return TickResult.Both;
            }
            return Over(-1);
        }
        if (wasReady) return TickResult.Both;                    // свисток: пішла гра
        return c.Still && c.T % 5 != 0 ? TickResult.None : TickResult.FrameOnly;
    }

    /// <summary>Місця команди, які ще грають і сидять.</summary>
    int[] Members(int team) => [.. Enumerable.Range(0, HockeyCore.Seats).Where(s => Core.Plays[s] && Core.Team[s] == team && Ctx.Seated(s))];

    string Names(int team) => string.Join(" і ", Members(team).Select(s => Ctx.NickOf(s) ?? "")
        .Concat(_bot >= 0 && Core.Plays[_bot] && Core.Team[_bot] == team ? ["🤖 бот"] : []));

    /// <summary>Кінець партії: команда <paramref name="team"/> (−1 — нічия), рядок Журналу, особисті голи, серія, ачівки.</summary>
    TickResult Over(int team)
    {
        var c = Core;
        _over = true;
        _winner = team >= 0 ? team : null;
        var playing = Enumerable.Range(0, HockeyCore.Seats).Where(s => c.Plays[s] && Ctx.Seated(s)).ToArray();
        var winners = team >= 0 ? Members(team) : [];
        string log;
        if (team >= 0)
        {
            var lose = 1 - team;
            log = $"{Info.Title}: {Names(team)} {c.S[team]}:{c.S[lose]} {Names(lose)}";
            foreach (var s in winners)
            {
                if (c.S[team] >= _target && c.S[lose] == 0) Ctx.Award(s, 0, "ach:hockey-dry");
                if (_deficit[team] >= 3) Ctx.Award(s, 0, "ach:hockey-comeback");
            }
        }
        else
        {
            log = $"{Info.Title}: {Names(0)} {c.S[0]}:{c.S[1]} {Names(1)} — нічия";
        }
        _series.Record(Ctx, winners);
        Ctx.Finish(winners, log, playing.ToDictionary(s => s, s => (long)c.Goals[s]));
        return TickResult.Both;
    }

    /// <summary>
    /// Хтось устав. На двох — перемога тому, хто лишився. На чотирьох біта зникає, команда грає одною; пішла вся
    /// команда — перемога іншій.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (!_started || _over) return;
        var c = Core;
        var nick = Ctx.NickOf(seat);
        var team = c.Team[seat];
        c.Drop(seat);
        var mine = Members(team).Where(s => s != seat).ToArray();
        var theirs = Members(1 - team).Where(s => s != seat).ToArray();
        if (mine.Length > 0 && theirs.Length > 0)
        {
            Ctx.Log($"{Info.Title}: {nick} встав з-за столу — {TeamNames[team]} грають одною битою");
            return;
        }
        _over = true;
        _winner = mine.Length > 0 ? team : theirs.Length > 0 ? 1 - team : null;
        var winners = mine.Length > 0 ? mine : theirs;
        _series.Record(Ctx, winners);
        Ctx.Finish(winners, $"{Info.Title}: {nick} встав з-за столу, партію не дограли",
            winners.ToDictionary(s => s, s => (long)c.Goals[s]));
    }

    // ---------- вид і кадр ----------

    HockeyCore Field
    {
        get
        {
            if (!Lobby) return Core;
            var preview = new HockeyCore(Ctx.Rng);   // Reset випадковості не питає — сідована партія та сама
            preview.Reset(WithBot(Seated()));
            return preview;
        }
    }

    public override object View(int? seat)
    {
        var lobby = Lobby;
        var c = Field;
        int?[] goals = new int?[HockeyCore.Seats], own = new int?[HockeyCore.Seats];
        for (var i = 0; i < HockeyCore.Seats; i++)
        {
            if (!c.Plays[i]) continue;
            goals[i] = c.Goals[i];
            own[i] = c.Own[i];
        }
        return new
        {
            phase = lobby ? "lobby" : _over ? "over" : c.StartIn > 0 ? "ready" : "go",
            score = new[] { c.S[0], c.S[1] },
            target = _target,
            teams = new[] { TeamSeats(c, 0), TeamSeats(c, 1) },
            goals,
            own,
            left = lobby ? MatchTicks : _left,
            golden = !lobby && _golden,
            winner = lobby ? null : _winner,
            bot = lobby ? (BotSeat(Seated()) is var b && b >= 0 ? b : (int?)null) : _bot >= 0 ? _bot : null,
            lastGoal = lobby || _lastGoal is not { } lg ? null : new { team = lg.Team, n = lg.N, own = lg.Own, rail = lg.Rail },
            series = _series.View(Ctx, HockeyCore.Seats),
            table = new
            {
                w = (int)HockeyCore.W, h = (int)HockeyCore.TableH, goal = new[] { (int)HockeyCore.GoalLo, (int)HockeyCore.GoalHi },
                puckR = HockeyCore.PuckR, padR = (int)HockeyCore.PadR, padSpeed = (int)HockeyCore.PadSpeed,
                serve = HockeyCore.GoalServeTicks,
            },
            turn = (int?)null,
            frame = Shot(c, lobby),
        };
    }

    static int[] TeamSeats(HockeyCore c, int team)
    {
        var n = 0;
        for (var i = 0; i < HockeyCore.Seats; i++) if (c.Plays[i] && c.Team[i] == team) n++;
        var r = new int[n];
        n = 0;
        for (var i = 0; i < HockeyCore.Seats; i++) if (c.Plays[i] && c.Team[i] == team) r[n++] = i;
        return r;
    }

    public override object? Frame() => Shot(Field, Lobby);

    /// <summary>
    /// Кадр (spec §4.2): шайба й біти до 0.1, рахунок, номер розіграшу, паузи, хто вдарив і хто забив у цьому
    /// тику. Масиви нові щоразу — кадр серіалізують поза замком кімнати.
    /// </summary>
    object Shot(HockeyCore c, bool lobby)
    {
        var p = new double?[HockeyCore.Seats * 2];
        for (var i = 0; i < HockeyCore.Seats; i++)
        {
            if (!c.Plays[i]) continue;
            p[2 * i] = R(c.Pads[i].X);
            p[2 * i + 1] = R(c.Pads[i].Y);
        }
        return new
        {
            t = c.T,
            ph = lobby ? PhLobby : _over ? PhOver : c.StartIn > 0 ? PhReady : PhGo,
            left = lobby ? MatchTicks : _left,
            x = R(c.Puck.X),
            y = R(c.Puck.Y),
            vx = R(c.Puck.Vx),
            vy = R(c.Puck.Vy),
            p,
            s = new[] { c.S[0], c.S[1] },
            n = c.N,
            serveIn = c.ServeIn,
            startIn = c.StartIn,
            hit = c.HitBy,
            goal = c.GoalBy,
            rally = c.Rally,
            nudge = c.Nudged,
            foul = c.FoulTo,
            golden = !lobby && _golden,
        };
    }

    static double R(double v) => Math.Round(v, 1);
}
