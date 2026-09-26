using System.Globalization;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Цеглини: 40 рядів» — соло на час на тому самому рушії, що й дуель (docs/games/specs/bricks-sprint.md).
/// «Готовий?» → відлік → секундомір → сорок рядів. Стіну передбачає браузер, а час рахує сервер зі своєї
/// копії: не швидше, ніж годинник сервера мінус пів секунди на дорогу. Таблиця — найкращий час у секундах.
/// </summary>
public sealed class BricksSprint : Game
{
    public const int StartTicks = 75;
    /// <summary>Хвилина без жодного натиску у фазі «go» — стіну покинуто (тики стіни, 60 Гц).</summary>
    public const int IdleTicks = 3600;
    /// <summary>Скільки тиків стіни «дороги» дозволено клієнтові: офіційний час не менший за годинник сервера мінус це.</summary>
    public const int Slack = 30;
    public const int KeyEvery = 25;
    /// <summary>Швидше за дві хвилини — ачівка «Швидкий муляр».</summary>
    public const double FastSeconds = 120;

    public const string PhaseReady = "ready", PhaseStart = "start", PhaseGo = "go", PhaseOver = "over";

    public override GameInfo Info { get; } = new(
        "bricks-sprint", "Цеглини: 40 рядів", "цеглини на час", GameGroup.Solo, 1, 1, TickMs: 40,
        Start: StartMode.Immediate, Score: ScoreOrder.LowerIsBetter,
        Hint: "Сорок рядів на час. Сам, без сміття — лише ти, стіна й секундомір. Таблиця — найкращий час",
        Client: "bricks");

    readonly BricksSeat _seat = new();
    readonly BricksJournal _journal = new();
    List<object> _ev = [];
    List<object> _frameEv = [];
    string _phase = PhaseReady;
    int _startIn;
    DateTimeOffset _goAt;
    int _rt;
    int _lastKey = -1_000_000;
    int _lastSec = -1;
    uint _seed;
    bool _phaseDirty, _viewDirty, _inFrame, _rowsInFrame;
    int _frameWall;
    /// <summary>Офіційний час (тиків стіни), коли сорок рядів складено; −1 — ще ні.</summary>
    int _finishTicks = -1;

    public override void Start()
    {
        _seed = (uint)Ctx.Rng.Next(1, int.MaxValue);
        _seat.Plays = true;
        _seat.Nick = Ctx.NickOf(0);
        _seat.Rank = 0;
        _seat.NeedFix = false;
        _seat.Dirty = true;
        _seat.SentCellsVer = -1;
        _seat.LastKeyWall = 0;
        _seat.LastEvT = -1;
        _seat.LastEvN = 0;
        _seat.Core.Reset(_seed, BricksCore.ModeNone, 0);
        _phase = PhaseReady;
        _startIn = 0;
        _lastSec = -1;
        _finishTicks = -1;
        _ev.Clear();
        _phaseDirty = true;
        _viewDirty = true;
    }

    int Wall() => _phase is PhaseReady or PhaseStart ? 0
        : (int)Math.Max(0, (Ctx.Clock.UtcNow - _goAt).Ticks * 60 / TimeSpan.TicksPerSecond);

    // ---------- ввід ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (seat != 0) return ActResult.Fail("Ти тут не граєш");
        switch (action)
        {
            case "ready":
                if (_phase != PhaseReady) return ActResult.Fail("Уже почали");
                _phase = PhaseStart;
                _startIn = StartTicks;
                _phaseDirty = true;
                _viewDirty = true;
                return ActResult.Done;
            case "j":
                switch (_phase)
                {
                    case PhaseReady: return ActResult.Fail("Партія ще не почалась");
                    case PhaseStart: return ActResult.Fail("Зачекай, зараз почнемо");
                    case PhaseOver: return ActResult.Fail("Партію вже зіграно");
                }
                var wall = Wall();
                var r = _journal.Apply(_seat, payload, wall, _rt);
                if (r.Ok)
                {
                    if (_journal.Keys > 0) _seat.LastKeyWall = wall;
                    Settle(wall);
                }
                return r;
            case "sync":
                if (_phase != PhaseGo) return ActResult.Fail("Зараз нема чого звіряти");
                _seat.NeedFix = true;
                return ActResult.Done;
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    /// <summary>Після пачки чи серверного ведення: сорок рядів — фініш, завал — кінець без часу.</summary>
    void Settle(int wall)
    {
        var b = _seat.Core;
        var lines = b.Lines;
        for (var i = b.ClearCount - 1; i >= 0; i--) lines -= b.Clears[i].Lines;
        for (var i = 0; i < b.ClearCount && _finishTicks < 0; i++)
        {
            var c = b.Clears[i];
            _ev.Add(new object[] { "c", 0, c.Lines, c.Kind, c.Combo, c.B2b, c.Sent, -1 });
            lines += c.Lines;
            if (lines >= BricksCore.SprintLines) _finishTicks = Math.Max(c.Tick, wall - Slack);
        }
        b.ClearCount = 0;
        b.Inserted = 0;
        _seat.Dirty = true;
        if (_finishTicks >= 0) { Done(); return; }
        if (!b.Alive) Fell();
    }

    void Done()
    {
        _phase = PhaseOver;
        _seat.Rank = 1;
        _viewDirty = true;
        var sec = Seconds(_finishTicks);
        var nick = Ctx.NickOf(0);
        Ctx.Score(0, sec);
        if (sec < FastSeconds) Ctx.Award(0, 0, "ach:bricks-sprint-2m");
        Ctx.Finish([0], $"Цеглини: {nick} — 40 рядів за {Clock(sec)}", new Dictionary<int, long> { [0] = _finishTicks });
    }

    void Fell()
    {
        _phase = PhaseOver;
        _seat.Rank = 2;
        _viewDirty = true;
        _ev.Add(new object[] { "o", 0, 2, _seat.Core.OutTick });
        Ctx.Finish([], $"Цеглини: {Ctx.NickOf(0)} — стіна впала, {Rows(_seat.Core.Lines)} з 40");
    }

    /// <summary>Тики (60 Гц) → секунди з сотими.</summary>
    public static double Seconds(int ticks) => Math.Round(ticks / 60.0, 2, MidpointRounding.AwayFromZero);

    /// <summary>97.35 → «1:37,35».</summary>
    public static string Clock(double seconds)
    {
        var cs = (long)Math.Round(seconds * 100, MidpointRounding.AwayFromZero);
        var m = cs / 6000;
        var s = cs / 100 % 60;
        var f = cs % 100;
        return string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00},{f:00}");
    }

    /// <summary>«1 ряд», «23 ряди», «5 рядів».</summary>
    public static string Rows(int n)
    {
        var t = n % 100;
        var o = n % 10;
        var word = t is > 10 and < 20 ? "рядів" : o == 1 ? "ряд" : o is >= 2 and <= 4 ? "ряди" : "рядів";
        return $"{n} {word}";
    }

    // ---------- тик ----------

    public override TickResult Tick()
    {
        _rt++;
        switch (_phase)
        {
            case PhaseReady:
            case PhaseOver:
                return _viewDirty ? Emit() : TickResult.None;
            case PhaseStart:
                if (--_startIn <= 0)
                {
                    _phase = PhaseGo;
                    _goAt = Ctx.Clock.UtcNow;
                    _lastSec = 0;
                    _seat.LastKeyWall = 0;
                }
                _phaseDirty = true;
                return Emit();
        }

        var wall = Wall();
        if (BricksJournal.Drive(_seat, wall)) Settle(wall);
        if (_phase != PhaseGo) return Emit();
        if (wall - _seat.LastKeyWall >= IdleTicks)
        {
            _phase = PhaseOver;
            _viewDirty = true;
            Ctx.Finish([], $"Цеглини: {Ctx.NickOf(0)} — стіну покинуто");
            return Emit();
        }
        var sec = wall / 60;
        if (sec != _lastSec)
        {
            _lastSec = sec;
            _phaseDirty = true;
        }
        if (BricksJournal.FixDue(_seat, _rt))
        {
            _seat.Epoch++;
            _seat.NeedFix = false;
            _seat.LastFixAt = _rt;
            _ev.Add(new object[] { "f", 0, BricksWire.Board(0, _seat) });
        }
        return Emit();
    }

    TickResult Emit()
    {
        var view = _viewDirty;
        _viewDirty = false;
        if (!_phaseDirty && _ev.Count == 0 && !_seat.Dirty) return view ? new TickResult(false, true) : TickResult.None;
        var key = _phase == PhaseGo && _rt - _lastKey >= KeyEvery;
        if (key) _lastKey = _rt;
        _inFrame = key || _seat.Dirty;
        _rowsInFrame = _inFrame && (key || _seat.Core.CellsVer != _seat.SentCellsVer);
        if (_rowsInFrame) _seat.SentCellsVer = _seat.Core.CellsVer;
        _seat.Dirty = false;
        (_ev, _frameEv) = (_frameEv, _ev);
        _ev.Clear();
        _frameWall = Wall();
        _phaseDirty = false;
        return view ? TickResult.Both : TickResult.FrameOnly;
    }

    // ---------- вид і кадр ----------

    public override object? Frame() => new
    {
        t = _frameWall,
        ph = _phase,
        @in = _startIn,
        lvl = 0,
        sd = -1,
        b = _inFrame ? new[] { BricksWire.Frame(0, _seat, _rowsInFrame) } : [],
        ev = _frameEv.Count == 0 ? [] : _frameEv.ToArray(),
    };

    public override object View(int? seat) => new
    {
        turn = (int?)null,
        phase = _phase,
        startIn = _startIn,
        t = Wall(),
        round = 1,
        need = 1,
        wins = new[] { _seat.Rank == 1 ? 1 : 0 },
        seed = _seed,
        speed = "normal",
        garbage = "none",
        stage = 0,
        target = Array.Empty<int>(),
        lvl = 0,
        sd = -1,
        rules = Bricks.Rules,
        boards = _seat.Nick is null ? [] : new[] { BricksWire.Board(0, _seat) },
        result = _phase == PhaseOver
            ? new { ranks = new[] { _seat.Rank }, lines = new[] { _seat.Core.Lines }, sec = _finishTicks >= 0 ? Seconds(_finishTicks) : (double?)null }
            : null,
    };

    public BricksSeat SeatState => _seat;
    public string Phase => _phase;
    public int WallTick => Wall();
}
