using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Цеглини» на 2–4 (docs/games/specs/bricks.md): кожен у своїй стіні, фігурки ті самі й у тому ж порядку,
/// закриті ряди летять сусідові за стрілкою сміттям знизу. Стіну кожного передбачає його браузер, а тут —
/// суддя: переганяє журнали натисків через <see cref="BricksCore"/>, роздає сміття, вирішує вибування,
/// раунди й партію. Кадр летить лише тоді, коли щось змінилось, і несе ряди лише тих стін, що змінились.
/// </summary>
public sealed class Bricks : Game
{
    public const int Seats = 4;
    /// <summary>Відлік перед раундом (тиків каркаса по 40 мс): три секунди.</summary>
    public const int StartTicks = 75;
    /// <summary>Пауза з підсумком між раундами: п'ять секунд.</summary>
    public const int PauseTicks = 125;
    /// <summary>Ключовий кадр (ряди всіх стін) — не рідше, ніж раз на стільки тиків каркаса.</summary>
    public const int KeyEvery = 25;

    public const string PhaseLobby = "lobby", PhaseStart = "start", PhaseGo = "go", PhasePause = "pause", PhaseOver = "over";

    static readonly string[] Names = ["теракота", "бірюза", "олива", "кобальт"];

    /// <summary>Сталі рушія для клієнта: одне джерело правди — BricksCore.</summary>
    public static readonly object Rules = new
    {
        das = BricksCore.Das, arr = BricksCore.Arr, softG = BricksCore.SoftG, @lock = BricksCore.LockDelay,
        resets = BricksCore.MaxResets, clear = BricksCore.ClearTicks, ripe = BricksCore.RipeTicks,
        ahead = BricksCore.Ahead, behind = BricksCore.Behind, maxInsert = BricksCore.MaxInsert,
        grav = BricksCore.Grav, sudden = BricksCore.Sudden, suddenEvery = BricksCore.SuddenEvery, cap = BricksCore.Cap,
        sprintG = BricksCore.SprintG, sprintLines = BricksCore.SprintLines,
    };

    public override GameInfo Info { get; } = new(
        "bricks", "Цеглини", "цеглини", GameGroup.Live, 2, Seats, TickMs: 40, Start: StartMode.ByHost,
        Options:
        [
            new GameOption("wins", "Партія до", [("1", "одного раунду"), ("2", "двох виграних"), ("3", "трьох виграних")], "1"),
            new GameOption("speed", "Темп", [("calm", "Спокійно"), ("normal", "Звичайно"), ("fast", "Швидко")], "normal"),
            new GameOption("garbage", "Сміття", [("normal", "Звичайне"), ("hard", "Люте"), ("none", "Без сміття — хто довше")], "normal"),
        ],
        Hint: "Падають цеглинки з чотирьох квадратиків. Закрив ряди — суперникові знизу лізе сміття. Хто завалився — вибув, останній бере раунд");

    readonly BricksSeat[] _seats = [new(), new(), new(), new()];
    readonly BricksJournal _journal = new();
    List<object> _ev = [];
    List<object> _frameEv = [];
    readonly bool[] _inFrame = new bool[Seats];
    readonly bool[] _rowsInFrame = new bool[Seats];
    /// <summary>Хто з тих, що грали, устав з-за столу до кінця партії (для підсумку).</summary>
    readonly bool[] _left = new bool[Seats];

    string _phase = PhaseLobby;
    int _startIn;
    DateTimeOffset _goAt;
    /// <summary>Тики каркаса від створення — для ключових кадрів і частоти виправлень.</summary>
    int _rt;
    int _lastKey = -1_000_000;
    uint _seed;
    int _round;
    int _need = 1;
    string _speed = "normal", _garbage = "normal";
    int _stageTicks = 1800;
    int _mode = BricksCore.ModeNormal;
    int _nextSudden = BricksCore.Sudden;
    int _lastSec = -1;
    bool _phaseDirty, _viewDirty;
    int _frameWall;
    /// <summary>Місця в партії за перемогами, коли її дограно (для підсумку); null — ще йде.</summary>
    int[]? _finalRanks;

    public override string SeatName(int seat) => seat >= 0 && seat < Names.Length ? Names[seat] : base.SeatName(seat);

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _need = options.TryGetValue("wins", out var w) && int.TryParse(w, out var n) && n is >= 1 and <= 3 ? n : 1;
        _speed = options.TryGetValue("speed", out var sp) && sp is "calm" or "normal" or "fast" ? sp : "normal";
        _garbage = options.TryGetValue("garbage", out var g) && g is "normal" or "hard" or "none" ? g : "normal";
        _stageTicks = StageFor(_speed);
        _mode = _garbage switch { "hard" => BricksCore.ModeHard, "none" => BricksCore.ModeNone, _ => BricksCore.ModeNormal };
    }

    /// <summary>Скільки тиків стіни (60 Гц) триває етап темпу.</summary>
    public static int StageFor(string speed) => speed switch { "calm" => 2700, "fast" => 1200, _ => 1800 };

    public override void Start()
    {
        _round = 0;
        _finalRanks = null;
        Array.Clear(_left);
        foreach (var st in _seats)
        {
            st.Wins = st.MatchLines = st.MatchSent = st.MatchRecv = 0;
            st.FourAsked = false;
        }
        NewRound();
    }

    void NewRound()
    {
        _round++;
        _seed = (uint)Ctx.Rng.Next(1, int.MaxValue);
        for (var s = 0; s < Seats; s++)
        {
            var st = _seats[s];
            st.Plays = Ctx.Seated(s);
            st.Nick = st.Plays ? Ctx.NickOf(s) : null;
            st.Rank = 0;
            st.NeedFix = false;
            st.Dirty = true;
            st.SentCellsVer = -1;
            st.Core.Reset(_seed, _mode, _stageTicks);
        }
        _phase = PhaseStart;
        _startIn = StartTicks;
        _nextSudden = BricksCore.Sudden;
        _lastSec = -1;
        _ev.Clear();
        _phaseDirty = true;
        _viewDirty = true;
    }

    // ---------- годинник ----------

    /// <summary>Скільки тиків стіни (60 Гц) минуло від «go» цього раунду за годинником сервера.</summary>
    int Wall() => _phase is PhaseLobby or PhaseStart ? 0
        : (int)Math.Max(0, (Ctx.Clock.UtcNow - _goAt).Ticks * 60 / TimeSpan.TicksPerSecond);

    // ---------- ввід ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (seat < 0 || seat >= Seats) return ActResult.Fail("Ти тут не граєш");
        switch (action)
        {
            case "j":
                return Journal(seat, payload);
            case "sync":
                if (_phase != PhaseGo || !_seats[seat].Plays) return ActResult.Fail("Зараз нема чого звіряти");
                _seats[seat].NeedFix = true;
                return ActResult.Done;
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    ActResult Journal(int seat, JsonElement payload)
    {
        switch (_phase)
        {
            case PhaseLobby: return ActResult.Fail("Партія ще не почалась");
            case PhaseStart or PhasePause: return ActResult.Fail("Зачекай, зараз почнемо");
            case PhaseOver: return ActResult.Fail("Партію вже зіграно");
        }
        var st = _seats[seat];
        if (!st.Plays) return ActResult.Fail("Ти тут не граєш");
        var r = _journal.Apply(st, payload, Wall());
        if (r.Ok)
        {
            Settle(seat);
            CheckRound();
        }
        return r;
    }

    /// <summary>Що рушій натворив після пачки чи серверного ведення: напади → посилки, четвірка, вибування.</summary>
    void Settle(int seat)
    {
        var st = _seats[seat];
        var b = st.Core;
        for (var i = 0; i < b.ClearCount; i++)
        {
            var c = b.Clears[i];
            var target = Target(seat);
            _ev.Add(new object[] { "c", seat, c.Lines, c.Kind, c.Combo, c.B2b, c.Sent, target });
            st.MatchLines += c.Lines;
            st.MatchSent += c.Sent;
            if (c.Sent > 0 && target >= 0) Credit(target, c.Sent, seat, ripeNow: false);
            if (c.Lines == 4 && !st.FourAsked)
            {
                st.FourAsked = true;
                Ctx.Award(seat, 0, "ach:bricks-four");
            }
        }
        b.ClearCount = 0;
        st.MatchRecv += b.Inserted;
        b.Inserted = 0;
        if (!b.Alive && st.Rank == 0) Fall(seat);
    }

    /// <summary>
    /// Посилка сміття жертві. Стає в її черзі там, де сервер її стіну зараз бачить: після події <c>after</c>,
    /// на тику <c>at</c> (§5.4) — клієнт жертви відкотиться туди й повторить свої натиски.
    /// </summary>
    void Credit(int victim, int rows, int from, bool ripeNow)
    {
        var v = _seats[victim];
        var b = v.Core;
        if (!v.Plays || !b.Alive) return;
        var g = b.Gseq + 1;
        var hole = Ctx.Rng.Next(BricksCore.W);
        var at = b.Tick;
        b.AddCredit(g, rows, hole, ripeNow ? at : at + BricksCore.RipeTicks, from);
        _ev.Add(new object[] { "g", victim, g, rows, hole, at, b.Seq, from });
        v.Dirty = true;
    }

    /// <summary>Наступне живе місце за колом праворуч; −1 — нікого (лишився сам).</summary>
    public int Target(int seat)
    {
        for (var d = 1; d < Seats; d++)
        {
            var s = (seat + d) % Seats;
            if (_seats[s].Plays && _seats[s].Core.Alive) return s;
        }
        return -1;
    }

    int Alive()
    {
        var n = 0;
        foreach (var st in _seats) if (st.Plays && st.Core.Alive) n++;
        return n;
    }

    /// <summary>Стіна впала: місце = скільки живих лишилось + 1; клієнтові — виправлення з кінцевою стіною.</summary>
    void Fall(int seat)
    {
        var st = _seats[seat];
        st.Rank = Alive() + 1;
        _ev.Add(new object[] { "o", seat, st.Rank, st.Core.OutTick });
        st.NeedFix = true;
        st.LastFixAt = -1_000_000;
        st.Dirty = true;
        _viewDirty = true;
    }

    void CheckRound()
    {
        if (_phase != PhaseGo) return;
        var alive = 0;
        var last = -1;
        for (var s = 0; s < Seats; s++)
            if (_seats[s].Plays && _seats[s].Core.Alive) { alive++; last = s; }
        if (alive > 1) return;
        EndRound(alive == 1 ? [last] : []);
    }

    /// <summary>8:00 — раунд зупиняється: більше рядів — вище, рівні ділять місце, найкращі разом беруть раунд.</summary>
    void CapRound()
    {
        var best = -1;
        for (var s = 0; s < Seats; s++)
            if (_seats[s].Plays && _seats[s].Core.Alive) best = Math.Max(best, _seats[s].Core.Lines);
        if (best < 0) { EndRound([]); return; }
        var winners = new List<int>();
        for (var s = 0; s < Seats; s++)
        {
            var st = _seats[s];
            if (!st.Plays || !st.Core.Alive) continue;
            var above = 0;
            for (var o = 0; o < Seats; o++)
                if (_seats[o].Plays && _seats[o].Core.Alive && _seats[o].Core.Lines > st.Core.Lines) above++;
            st.Rank = above + 1;
            if (st.Core.Lines == best) winners.Add(s);
        }
        EndRound([.. winners]);
    }

    void EndRound(int[] winners)
    {
        foreach (var w in winners)
        {
            _seats[w].Rank = 1;
            _seats[w].Wins++;
            _ev.Add(new object[] { "w", w });
        }
        _viewDirty = true;
        _phaseDirty = true;
        var done = false;
        foreach (var st in _seats) if (st.Plays && st.Wins >= _need) done = true;
        if (done) { Over(); return; }
        _phase = PhasePause;
        _startIn = PauseTicks;
    }

    /// <summary>
    /// Партію дограно: місця — за виграними раундами, рівних розводить місце в останньому раунді; Журнал у тому ж
    /// порядку («Цеглини: Оля 2 : Петро 1 : Іван 0»), у таблицю — ряди за партію.
    /// </summary>
    void Over()
    {
        _phase = PhaseOver;
        // У підсумку — усі, хто грав останній раунд, і ті, хто посеред нього встав: їхня стіна впала з місцем,
        // як у звичайного вибулого. Інакше утікач лишався без місця («0-й»), а той, хто впав раніше за нього,
        // підіймався вище. Очки порожнього місця каркас однаково не запише.
        var played = new List<int>();
        for (var s = 0; s < Seats; s++) if (_seats[s].Nick is not null) played.Add(s);
        _finalRanks = new int[Seats];
        foreach (var s in played) _finalRanks[s] = 1 + played.Count(o => Better(o, s));
        MarkLeft();
        var winners = played.Where(s => _seats[s].Plays && _seats[s].Wins >= _need).ToArray();
        var order = played.OrderBy(s => _finalRanks[s]).ThenBy(s => s).ToList();
        var scores = played.ToDictionary(s => s, s => (long)_seats[s].MatchLines);
        var log = $"{Info.Title}: " + string.Join(" : ", order.Select(s => $"{_seats[s].Nick} {_seats[s].Wins}"));
        Ctx.Finish(winners, log, scores);
    }

    /// <summary>Хто з тих, що грали раунд, уже не сидить за столом — підсумок напише «встав з-за столу».</summary>
    void MarkLeft()
    {
        for (var s = 0; s < Seats; s++) _left[s] = _seats[s].Nick is not null && !_seats[s].Plays;
    }

    /// <summary>Чи місце <paramref name="a"/> в підсумку партії вище за <paramref name="b"/>.</summary>
    bool Better(int a, int b)
    {
        var x = _seats[a];
        var y = _seats[b];
        if (x.Wins != y.Wins) return x.Wins > y.Wins;
        var rx = x.Rank == 0 ? 1 : x.Rank;
        var ry = y.Rank == 0 ? 1 : y.Rank;
        return rx < ry;
    }

    /// <summary>
    /// Хтось устав посеред партії: його стіна падає тут же (місце — як у звичайного вибулого), партія йде далі,
    /// поки за столом хоча б двоє. Лишився один — партія його.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (seat < 0 || seat >= Seats) return;
        var st = _seats[seat];
        if (st.Plays && _phase is PhaseStart or PhaseGo && st.Core.Alive)
        {
            st.Core.Retire(BricksCore.OutLeft);
            Fall(seat);
        }
        st.Plays = false;
        st.NeedFix = false;
        _viewDirty = true;
        _phaseDirty = true;
        if (_phase == PhaseOver) return;
        var others = Enumerable.Range(0, Seats).Where(s => s != seat && Ctx.Seated(s)).ToArray();
        if (others.Length <= 1)
        {
            FinishLeft(others, $"{Info.Title}: {Ctx.NickOf(seat)} встав з-за столу, партію не дограли");
            return;
        }
        CheckRound();
    }

    /// <summary>
    /// За столом лишився один — партія його, скільки б раундів хто не виграв: у підсумку він перший, решта
    /// (і той, хто встав) — за ним у звичайному порядку. Інакше підсумок писав би «Раунд нікому».
    /// </summary>
    void FinishLeft(int[] stay, string log)
    {
        _phase = PhaseOver;
        _finalRanks = new int[Seats];
        var rest = new List<int>();
        for (var s = 0; s < Seats; s++)
        {
            if (_seats[s].Nick is null) continue;
            if (Array.IndexOf(stay, s) >= 0)
            {
                _finalRanks[s] = 1;
                if (_seats[s].Core.Alive) _seats[s].Rank = 1;
            }
            else rest.Add(s);
        }
        foreach (var s in rest) _finalRanks[s] = 1 + stay.Length + rest.Count(o => Better(o, s));
        MarkLeft();
        var scores = new Dictionary<int, long>();
        for (var s = 0; s < Seats; s++) if (_seats[s].Nick is not null) scores[s] = _seats[s].MatchLines;
        Ctx.Finish(stay, log, scores);
    }

    // ---------- тик ----------

    public override TickResult Tick()
    {
        _rt++;
        switch (_phase)
        {
            case PhaseLobby:
            case PhaseOver:
                return TickResult.None;
            case PhaseStart:
                if (--_startIn <= 0)
                {
                    _phase = PhaseGo;
                    _goAt = Ctx.Clock.UtcNow;
                    _lastSec = 0;
                }
                _phaseDirty = true;
                break;
            case PhasePause:
                if (--_startIn <= 0) NewRound();
                else if (_startIn % 25 == 0) _phaseDirty = true;
                break;
            default:
                GoTick();
                break;
        }
        return Emit();
    }

    void GoTick()
    {
        var wall = Wall();
        for (var s = 0; s < Seats; s++)
            if (_seats[s].Plays && BricksJournal.Drive(_seats[s], wall)) Settle(s);
        CheckRound();
        if (_phase != PhaseGo) return;

        while (wall >= _nextSudden && _nextSudden < BricksCore.Cap)
        {
            // Раптова смерть: земля підіймається всім живим, посилка дозріла одразу.
            for (var s = 0; s < Seats; s++) Credit(s, 1, -1, ripeNow: true);
            _nextSudden += BricksCore.SuddenEvery;
            _phaseDirty = true;
        }
        if (wall >= BricksCore.Cap)
        {
            CapRound();
            return;
        }
        var sec = wall / 60;
        if (sec != _lastSec)
        {
            _lastSec = sec;
            _phaseDirty = true;
        }
        for (var s = 0; s < Seats; s++)
        {
            var st = _seats[s];
            if (!st.Plays || !BricksJournal.FixDue(st, _rt)) continue;
            st.Epoch++;
            st.NeedFix = false;
            st.LastFixAt = _rt;
            _ev.Add(new object[] { "f", s, BricksWire.Board(s, st) });
        }
    }

    /// <summary>Чи летить кадр: лише якщо щось змінилось. Тут же вирішуємо, чиї ряди в нього покласти.</summary>
    TickResult Emit()
    {
        var any = _phaseDirty || _ev.Count > 0;
        for (var s = 0; s < Seats && !any; s++) any = _seats[s].Nick is not null && _seats[s].Dirty;
        var view = _viewDirty;
        _viewDirty = false;
        if (!any) return view ? new TickResult(false, true) : TickResult.None;

        var key = _phase == PhaseGo && _rt - _lastKey >= KeyEvery;
        if (key) _lastKey = _rt;
        for (var s = 0; s < Seats; s++)
        {
            var st = _seats[s];
            var inRound = st.Nick is not null;
            _inFrame[s] = inRound && (key || st.Dirty);
            _rowsInFrame[s] = _inFrame[s] && (key || st.Core.CellsVer != st.SentCellsVer);
            if (_rowsInFrame[s]) st.SentCellsVer = st.Core.CellsVer;
            st.Dirty = false;
        }
        (_ev, _frameEv) = (_frameEv, _ev);
        _ev.Clear();
        _frameWall = Wall();
        _phaseDirty = false;
        return view ? TickResult.Both : TickResult.FrameOnly;
    }

    // ---------- вид і кадр ----------

    public override object? Frame()
    {
        var n = 0;
        for (var s = 0; s < Seats; s++) if (_inFrame[s]) n++;
        var b = new object[n];
        n = 0;
        for (var s = 0; s < Seats; s++) if (_inFrame[s]) b[n++] = BricksWire.Frame(s, _seats[s], _rowsInFrame[s]);
        return new
        {
            t = _frameWall,
            ph = _phase,
            @in = _startIn,
            lvl = Level(_frameWall),
            sd = Sd(_frameWall),
            b,
            ev = _frameEv.Count == 0 ? [] : _frameEv.ToArray(),
        };
    }

    int Level(int wall) => _phase == PhaseGo || _phase == PhasePause ? wall / _stageTicks : 0;

    int Sd(int wall) => _phase == PhaseGo && wall >= BricksCore.Sudden ? Math.Max(0, _nextSudden - wall) : -1;

    public override object View(int? seat)
    {
        var wall = Wall();
        var boards = new List<object>(Seats);
        for (var s = 0; s < Seats; s++)
            if (_seats[s].Nick is not null && _phase != PhaseLobby) boards.Add(BricksWire.Board(s, _seats[s]));
        var wins = new int[Seats];
        var target = new int[Seats];
        for (var s = 0; s < Seats; s++)
        {
            wins[s] = _seats[s].Wins;
            target[s] = _phase == PhaseLobby || !_seats[s].Plays || !_seats[s].Core.Alive ? -1 : Target(s);
        }
        object? result = null;
        if (_finalRanks is not null)
        {
            var lines = new int[Seats];
            for (var s = 0; s < Seats; s++) lines[s] = _seats[s].MatchLines;
            result = new { ranks = (int[])_finalRanks.Clone(), lines, left = (bool[])_left.Clone() };
        }
        return new
        {
            turn = (int?)null,
            phase = _phase,
            startIn = _startIn,
            t = wall,
            round = _round,
            need = _need,
            wins,
            seed = _seed,
            speed = _speed,
            garbage = _garbage,
            stage = _stageTicks,
            target,
            lvl = Level(wall),
            sd = Sd(wall),
            rules = Rules,
            boards,
            result,
        };
    }

    // ---------- для тестів ----------

    public BricksSeat SeatState(int seat) => _seats[seat];
    public string Phase => _phase;
    public uint Seed => _seed;
    public int WallTick => Wall();
}
