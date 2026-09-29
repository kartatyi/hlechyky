using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Стан місця за партію й раунд. Хто я на лузі — таємниця місця; решта відкривається на розкритті.</summary>
public sealed class FreezeSeat
{
    /// <summary>Сидів на старті партії.</summary>
    public bool Plays;
    /// <summary>Гравець-бот (🤖): місце порожнє в каркасі, селянином керує сервер (<c>Freeze.Bot.cs</c>).</summary>
    public bool Bot;
    /// <summary>Устав посеред партії: його селянин — уже бот, а очки лишаються в таблиці.</summary>
    public bool Out;
    public string Nick = "";
    /// <summary>Id свого селянина в цьому раунді.</summary>
    public int Me = -1;
    public int Total;
    /// <summary>Скільки разів Баба впіймала за раунд, скільки гравців звалив штурханом, скільки разів штурхнув узагалі.</summary>
    public int Caught, Hits, Pushes;
    /// <summary>Тик (наскрізний) останнього <c>move</c>: затиснута стрілка без підтвердження гасне.</summary>
    public int MoveAt;
    /// <summary>Слід за раунд для розкриття: кільце останніх позицій свого селянина.</summary>
    public readonly int[] TrailX = new int[Freeze.TrailLen], TrailY = new int[Freeze.TrailLen];
    public int TrailHead, TrailCount;
    /// <summary>Естафета: команда 0/1 (на партію); -1 — кожен за себе.</summary>
    public int Team = -1;
    /// <summary>Естафета: скільки разів торкнувся глека за раунд.</summary>
    public int Jugs;

    public bool Active => Plays && !Out;
}

/// <summary>
/// «Замри!»: «червоне світло — зелене світло» по-сільськи («Море хвилюється»). Довгий луг, зліва тин, справа Баба
/// Параска з глеком. Юрма селян-ботів поволі тягнеться до хати, гравці — серед них, і в кадрі їх не відрізнити нічим.
/// Баба співає — іди; обертається й кричить «Замри!» — за мить усі мусять стояти, а хто ворухнувся, того вона
/// вертає на старт. Перший, хто торкнувся глека, бере раунд. Правила лугу — у <see cref="FreezeCore"/>, тут фази, очки,
/// дії, вид і кадр (spec: docs/games/specs/freeze.md).
/// </summary>
public sealed partial class Freeze : Game
{
    public const string PhaseLobby = "lobby", PhaseStart = "start", PhaseGo = "go", PhaseReveal = "reveal", PhaseOver = "over";
    public const int Seats = 8;
    public const int TickMs = 40;
    /// <summary>«Роздивись» — 3 с: усі стоять біля тину, Баба дивиться на луг.</summary>
    public const int StartTicks = 75;
    /// <summary>Раунд — 90 с.</summary>
    public const int RoundTicks = 2250;
    /// <summary>Розкриття — 6 с, усі завмерли, над гравцями ніки.</summary>
    public const int RevealTicks = 150;
    /// <summary>Хто перший торкнувся глека — +3; як вийшов час — «сходинки»: найдальшому +2, другому +1 (якщо гравців ≥ 3).</summary>
    public const int PtJug = 3, PtFirst = 2, PtSecond = 1;
    /// <summary>
    /// Естафета (п. 195, опція <c>mode=relay</c>): дві команди, торкнувся глека — очко команді й назад до тину. Раунд — до
    /// <see cref="RelayGoal"/> очок чи до кінця часу. Кожному: +1 за свій глек, +2 кожному з команди, що взяла раунд.
    /// </summary>
    public const int RelayGoal = 5, PtRelayJug = 1, PtRelayRound = 2;
    public static readonly string[] TeamNames = ["🌻 Соняшники", "💠 Волошки"];
    /// <summary>У розкритті кадр летить раз на стільки тиків (усі стоять — частіше нема чого).</summary>
    public const int RevealFrameEvery = 5;
    /// <summary>Затиснуту стрілку модуль підтверджує раз на секунду; не чули 3 с — зв'язок обірвався, селянин стає.</summary>
    public const int MoveHoldTicks = 75;
    /// <summary>Слід на розкритті: точка раз на 12 тиків, 64 точки — останні ≈ 30 с.</summary>
    public const int TrailEvery = 12, TrailLen = 64;

    /// <summary>«Як на лузі»: на двох — 20 ботів, далі більше, на вісьмох — 32.</summary>
    public static int BotsFor(int players) => players switch
    {
        <= 2 => 20,
        3 => 22,
        4 => 24,
        5 => 26,
        6 => 28,
        7 => 30,
        _ => 32,
    };

    public override GameInfo Info { get; } = new(
        "freeze", "Замри!", "«Замри!»", GameGroup.Live, 1, Seats, TickMs: TickMs,
        Start: StartMode.ByHost, Hidden: true, Score: ScoreOrder.HigherIsBetter,
        Options:
        [
            new GameOption("rounds", "Раундів", [("3", "3 раунди"), ("1", "1 раунд"), ("5", "5 раундів")], "3"),
            new GameOption("crowd", "Селян", [("auto", "Як на лузі"), ("small", "Жменька (14)"), ("big", "Ціле село (40)")], "auto"),
            new GameOption("mode", "Гра", [("solo", "Кожен за себе"), ("relay", "🏺 Естафета: дві команди")], "solo"),
            LiveBots.LevelOption,
        ],
        Hint: "Баба Параска співає — іди до глека. Обернулась і крикнула «Замри!» — стій, як укопаний. Ти — один із юрми, і ніхто не знає, хто з селян живий. Самому — з 🤖 ботами");

    static readonly string[] SeatNames = ["жовтий", "зелений", "рудий", "сірий", "синій", "рожевий", "фіолетовий", "червоний"];

    readonly FreezeSeat[] _s = [.. Enumerable.Range(0, Seats).Select(_ => new FreezeSeat())];
    FreezeCore? _core;
    int _rounds = 3;
    string _crowd = "auto";
    bool _started;
    string _phase = PhaseLobby;
    int _round;
    int _left;
    /// <summary>Тик раунду (з «роздивись» включно).</summary>
    int _t;
    /// <summary>Наскрізний тик партії — для «коли востаннє чули стрілку».</summary>
    int _clock;
    int _n;
    bool _dirty;
    /// <summary>Чим скінчилась партія: «end» — дограли, «left» — усі розійшлись.</summary>
    string _endWhy = "end";
    /// <summary>Події з дій між тиками — підуть у наступний кадр.</summary>
    readonly List<int[]> _pending = [];
    int[][] _evFrame = [];
    FreezeReveal? _reveal;
    int[]? _winners;

    sealed record FreezeReveal(int[] Winners, string Why, (int Seat, int Id)[] Ids, FreezeRow[] Rows, (int Seat, int[] Pts)[] Trails);
    sealed record FreezeRow(int Seat, int X, int Caught, int Hits, bool Win, int Pts, int Jugs);

    FreezeCore Core => _core ??= new FreezeCore(Ctx.Rng);

    /// <summary>Для тестів: ядро й місця напряму.</summary>
    public FreezeCore CoreForTests => Core;
    public FreezeSeat SeatForTests(int seat) => _s[seat];
    public string Phase => _phase;
    public int Left => _left;
    public int RoundNo => _round;

    public override string SeatName(int seat) => seat >= 0 && seat < Seats ? SeatNames[seat] : base.SeatName(seat);

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _rounds = options.TryGetValue("rounds", out var r) && int.TryParse(r, out var n) && n is 1 or 3 or 5 ? n : 3;
        _crowd = options.TryGetValue("crowd", out var c) && c is "small" or "big" ? c : "auto";
        _relay = options.TryGetValue("mode", out var m) && m == "relay";
        _solo.Configure(options);
    }

    bool _relay;
    /// <summary>Естафета: очки команд за раунд і за партію, виграні раунди, вага глека (менша команда 2 на 1 — глек за два).</summary>
    readonly int[] _teamPts = new int[2], _teamTotal = new int[2], _teamRounds = new int[2], _teamWeight = [1, 1];
    public bool Relay => _relay;
    public int TeamPtsForTests(int team) => _teamPts[team];

    /// <summary>Команди: хто сидить — по черзі в Соняшники й Волошки. Менша вдвічі команда (2 на 1) рахує глек за два.</summary>
    void DealTeams()
    {
        var k = 0;
        int[] size = [0, 0];
        for (var i = 0; i < Seats; i++)
        {
            _s[i].Team = -1;
            if (!_relay || !_s[i].Plays) continue;
            _s[i].Team = k % 2;
            size[k % 2]++;
            k++;
        }
        _teamWeight[0] = size[0] > 0 && size[1] > size[0] ? size[1] / size[0] : 1;
        _teamWeight[1] = size[1] > 0 && size[0] > size[1] ? size[0] / size[1] : 1;
        Array.Clear(_teamTotal);
        Array.Clear(_teamRounds);
    }

    /// <summary>Скільки ботів за складом і опцією.</summary>
    public int BotsForTable(int players) => _crowd switch
    {
        "small" => 14,
        "big" => 40,
        _ => BotsFor(players),
    };

    public override void Start()
    {
        _started = true;
        _round = 0;
        _clock = 0;
        _endWhy = "end";
        _winners = null;
        _reveal = null;
        _pending.Clear();
        _evFrame = [];
        _bots = _solo.Active(Ctx, Seats) ? BotSeats() : [];
        var players = 0;
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            s.Bot = Array.IndexOf(_bots, i) >= 0;
            s.Plays = Ctx.Seated(i) || s.Bot;
            s.Out = false;
            s.Nick = s.Bot ? LiveBots.Name : Ctx.NickOf(i) ?? "";
            s.Total = 0;
            if (s.Plays) players++;
        }
        _n = players + BotsForTable(players);
        DealTeams();
        NewRound();
    }

    void NewRound()
    {
        _round++;
        _phase = PhaseStart;
        _left = StartTicks;
        _t = 0;
        _reveal = null;
        _pending.Clear();
        var owners = new List<int>(Seats);
        for (var i = 0; i < Seats; i++)
            if (_s[i].Active) owners.Add(i);
        Core.Deal(owners, Math.Max(0, _n - owners.Count));
        foreach (var v in Core.V)
            if (v.Owner >= 0) _s[v.Owner].Me = v.Id;
        foreach (var seat in owners)
        {
            var s = _s[seat];
            s.Caught = s.Hits = s.Pushes = s.Jugs = 0;
            s.MoveAt = _clock;
            s.TrailHead = s.TrailCount = 0;
        }
        for (var i = 0; i < Seats; i++)
            if (!_s[i].Active) _s[i].Me = -1;
        Array.Clear(_teamPts);
        BotsNewRound();
        _dirty = true;
    }

    // ---------------------------------------------------------------------------------------------
    // Дії
    // ---------------------------------------------------------------------------------------------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == LiveBots.Toggle && (!_started || _phase == PhaseOver)) return _solo.Switch(Ctx, seat, payload, Seats);
        if (!_started) return ActResult.Fail("Партія ще не почалась");
        if (seat < 0 || seat >= Seats || !_s[seat].Active) return ActResult.Fail("Тут так не ходять");
        if (_s[seat].Bot) return ActResult.Fail("Тут грає 🤖 бот — зачекай кінця партії");
        return action switch
        {
            "move" => Move(seat, payload),
            "push" => Push(seat, payload),
            _ => ActResult.Fail("Тут так не ходять"),
        };
    }

    ActResult Move(int seat, JsonElement payload)
    {
        var dir = Dir(payload);
        if (dir is null or < -1 or > 3) return ActResult.Fail("Такого напрямку нема");
        if (_phase == PhaseOver) return ActResult.Fail("Раунд скінчився");
        var s = _s[seat];
        // У розкритті — приймаємо й мовчки не застосовуємо. На «роздивись» — записуємо: крок почнеться, щойно Баба
        // заспіває (так само, як у бота, що стоїть напоготові).
        if (_phase == PhaseReveal || s.Me < 0) return ActResult.Done;
        Core.V[s.Me].Want = dir.Value;
        s.MoveAt = _clock;
        return ActResult.Done;
    }

    /// <summary>Напрямок — і як <c>{dir:1}</c>, і як голе число.</summary>
    static int? Dir(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetInt32(out var n) => n,
        JsonValueKind.Object when payload.TryGetProperty("dir", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n) => n,
        _ => null,
    };

    /// <summary>Необов'язкове ціле поле payload: нема (або null) — null; криве — false у ok.</summary>
    static int? Field(JsonElement payload, string name, out bool ok)
    {
        ok = true;
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (payload.ValueKind != JsonValueKind.Object) { ok = false; return null; }
        if (!payload.TryGetProperty(name, out var f) || f.ValueKind == JsonValueKind.Null) return null;
        if (f.ValueKind == JsonValueKind.Number && f.TryGetInt32(out var n)) return n;
        ok = false;
        return null;
    }

    ActResult Push(int seat, JsonElement payload)
    {
        var id = Field(payload, "id", out var ok);
        return PushAt(seat, id, ok);
    }

    /// <summary>Штурхан — одна дорога і для людини, і для 🤖 бота (ті самі руки, дальність і отетеріння).</summary>
    ActResult PushAt(int seat, int? id, bool ok = true)
    {
        if (_phase == PhaseStart) return ActResult.Fail("Зачекай, Баба ще не заспівала");
        if (_phase != PhaseGo) return ActResult.Fail("Раунд скінчився");
        var s = _s[seat];
        var me = Core.V[s.Me];
        if (!Core.PushAllowed) return ActResult.Fail("Баба дивиться — замри!");
        if (me.Caught > 0) return ActResult.Fail("Тебе впіймали — вертайся на старт");
        if (me.Down > 0) return ActResult.Fail("Ти лежиш — спершу встань");
        if (me.Dazed > 0) return ActResult.Fail("Ти ще отетерілий");
        if (me.PushCool > 0) return ActResult.Fail("Руки ще не відійшли");

        if (!ok) return ActResult.Fail("Такого селянина нема");
        int target;
        if (id is { } want)
        {
            if (want < 0 || want >= Core.N) return ActResult.Fail("Такого селянина нема");
            if (want == s.Me) return ActResult.Fail("Себе штурхати — якось дивно");
            var q = Core.V[want];
            if (q.Down > 0 || q.Caught > 0) return ActResult.Fail("Лежачого не штурхають");
            if (q.Guard > 0) return ActResult.Fail("Дай людині встати");
            if (FreezeCore.Dist2(q, me) > (long)FreezeCore.PushRangeMax * FreezeCore.PushRangeMax)
                return ActResult.Fail("Далеко — не дотягнешся");
            target = want;
        }
        else
        {
            target = Core.Nearest(me);
            if (target < 0) return ActResult.Fail("Нікого поруч");
        }

        var hit = Core.V[target];
        s.Pushes++;
        if (hit.Owner >= 0) s.Hits++;
        // Подія — у наступний кадр, разом із тим, що сталось у тику. Вид не розсилаємо: штурхан бота теж його не шле.
        _pending.Add(Core.Push(me, hit));
        return ActResult.Done;
    }

    // ---------------------------------------------------------------------------------------------
    // Тик
    // ---------------------------------------------------------------------------------------------

    public override TickResult Tick()
    {
        switch (_phase)
        {
            case PhaseStart:
                OpenEvents();
                _t++;
                _clock++;
                // роздивляються: ніхто не ходить — ні боти, ні гравці (стоять біля тину, Баба дивиться)
                if (--_left <= 0)
                {
                    _phase = PhaseGo;
                    _left = RoundTicks;
                    Core.Open();
                    _dirty = true;
                }
                return Flush(true);
            case PhaseGo:
                OpenEvents();
                _t++;
                _clock++;
                HeldKeys();
                var babaWas = Core.Baba;
                BotsThink();
                Core.TickGo();
                BotsSee();
                foreach (var e in Core.Ev)
                    if (e[0] == 2 && Core.V[e[1]].Owner >= 0) _s[Core.V[e[1]].Owner].Caught++;
                Trail();
                _left--;
                EndCheck(babaWas);
                return Flush(true);
            case PhaseReveal:
                OpenEvents();
                _t++;
                _clock++;
                if (--_left <= 0)
                {
                    if (_round < _rounds) NewRound();
                    else FinishMatch();
                    return Flush(true);
                }
                return Flush(_left % RevealFrameEvery == 0);
            default:
                return TickResult.None;
        }
    }

    void OpenEvents()
    {
        Core.Ev.Clear();
        Core.Ev.AddRange(_pending);
        _pending.Clear();
    }

    TickResult Flush(bool frame)
    {
        _evFrame = Core.Ev.Count == 0 ? [] : [.. Core.Ev];
        var view = _dirty;
        _dirty = false;
        return new TickResult(frame, view);
    }

    /// <summary>Стрілку, яку давно не підтверджували (обрив зв'язку), відпускаємо самі.</summary>
    void HeldKeys()
    {
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Active || s.Me < 0) continue;
            var v = Core.V[s.Me];
            if (v.Want >= 0 && _clock - s.MoveAt > MoveHoldTicks) v.Want = -1;
        }
    }

    /// <summary>Раз на <see cref="TrailEvery"/> тиків — точка в слід кожного гравця (для розкриття).</summary>
    void Trail()
    {
        if (_t % TrailEvery != 0) return;
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Active || s.Me < 0) continue;
            var v = Core.V[s.Me];
            s.TrailX[s.TrailHead] = v.X;
            s.TrailY[s.TrailHead] = v.Y;
            s.TrailHead = (s.TrailHead + 1) % TrailLen;
            if (s.TrailCount < TrailLen) s.TrailCount++;
        }
    }

    static int[] TrailOf(FreezeSeat s)
    {
        var a = new int[s.TrailCount * 2];
        var start = (s.TrailHead - s.TrailCount + TrailLen) % TrailLen;
        for (var i = 0; i < s.TrailCount; i++)
        {
            var j = (start + i) % TrailLen;
            a[i * 2] = s.TrailX[j];
            a[i * 2 + 1] = s.TrailY[j];
        }
        return a;
    }

    /// <summary>
    /// Кінець раунду: хтось із гравців торкнувся глека (перетнув фінішну смугу й не впійманий цього ж тика) — усі такі
    /// цього тика беруть по +3; інакше, як вийшов час, — «сходинки» за тим, хто далі зайшов.
    /// </summary>
    void EndCheck(int babaWas)
    {
        if (_relay) { RelayCheck(); return; }
        List<int>? jug = null;
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Active || s.Me < 0) continue;
            var v = Core.V[s.Me];
            if (v.X >= FreezeCore.FinishX && v.Caught == 0) (jug ??= []).Add(i);
        }
        if (jug is not null)
        {
            // «Під самим носом»: дійшов, коли Баба вже кричала «Замри!» (благодать)
            var bold = babaWas == FreezeCore.Turn || Core.Baba == FreezeCore.Turn;
            // з 🤖 ботами — без ачівок: партія тренувальна
            foreach (var w in jug)
            {
                if (_bots.Length > 0) break;
                if (bold) Ctx.Award(w, 0, "ach:freeze-bold");
                if (_s[w].Caught == 0) Ctx.Award(w, 0, "ach:freeze-clean");
            }
            EndRound([.. jug], "jug");
            return;
        }
        if (_left > 0) return;
        EndRound(Podium(), "time");
    }

    /// <summary>
    /// Естафета: хто торкнувся глека (і Баба не впіймала) — очко команді (з вагою), +1 собі й назад до тину, раунд іде
    /// далі. Команда набрала <see cref="RelayGoal"/> — раунд її; вийшов час — чия команда більше, рівно — нічия.
    /// </summary>
    void RelayCheck()
    {
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Active || s.Me < 0 || s.Team < 0) continue;
            var v = Core.V[s.Me];
            if (v.X < FreezeCore.FinishX || v.Caught != 0) continue;
            s.Jugs++;   // +1 собі — на розкритті: посеред раунду «+1 Олі» назвав би, хто щойно торкнувся глека
            _teamPts[s.Team] += _teamWeight[s.Team];
            Core.RelayBack(v, s.Team);
            _dirty = true;
        }
        int a = _teamPts[0], b = _teamPts[1];
        if (a >= RelayGoal || b >= RelayGoal || _left <= 0)
        {
            var team = a > b ? 0 : b > a ? 1 : -1;
            var winners = team < 0 ? [] : Enumerable.Range(0, Seats).Where(i => _s[i].Active && _s[i].Team == team).ToArray();
            EndRound(winners, team < 0 ? "none" : a >= RelayGoal || b >= RelayGoal ? "relay" : "time");
        }
    }

    /// <summary>Хто далі зайшов (за x свого селянина): перші — найдальші, рівні — поруч.</summary>
    int[] Podium()
    {
        var active = new List<int>();
        for (var i = 0; i < Seats; i++)
            if (_s[i].Active && _s[i].Me >= 0) active.Add(i);
        if (active.Count == 0) return [];
        var best = active.Max(i => Core.V[_s[i].Me].X);
        return [.. active.Where(i => Core.V[_s[i].Me].X == best)];
    }

    void EndRound(int[] winners, string why)
    {
        var pts = new int[Seats];
        if (_relay)
        {
            // естафета: свої глеки (+1 кожен) і раунд команді (+2 кожному)
            for (var i = 0; i < Seats; i++) if (_s[i].Active) { pts[i] = _s[i].Jugs * PtRelayJug; _s[i].Total += pts[i]; }
            foreach (var w in winners) { pts[w] += PtRelayRound; _s[w].Total += PtRelayRound; }
            for (var t = 0; t < 2; t++) _teamTotal[t] += _teamPts[t];
            if (winners.Length > 0) _teamRounds[_s[winners[0]].Team]++;
            _reveal = RevealOf(winners, why, s => s.Active, pts);
            StandStill();
            _phase = PhaseReveal;
            _left = RevealTicks;
            _dirty = true;
            return;
        }
        if (why == "jug")
            foreach (var w in winners) pts[w] = PtJug;
        else if (why == "time")
        {
            // «сходинки»: найдальшим +2; наступним за ними +1 — якщо гравців хоча б троє (на двох це була б нагорода за участь)
            var active = Enumerable.Range(0, Seats).Where(i => _s[i].Active && _s[i].Me >= 0).ToList();
            foreach (var w in winners) pts[w] = PtFirst;
            var rest = active.Where(i => pts[i] == 0).ToList();
            if (active.Count >= 3 && rest.Count > 0)
            {
                var second = rest.Max(i => Core.V[_s[i].Me].X);
                foreach (var i in rest.Where(i => Core.V[_s[i].Me].X == second)) pts[i] = PtSecond;
            }
        }
        for (var i = 0; i < Seats; i++) _s[i].Total += pts[i];
        _reveal = RevealOf(winners, why, s => s.Active, pts);
        StandStill();
        _phase = PhaseReveal;
        _left = RevealTicks;
        _dirty = true;
    }

    /// <summary>Усі завмерли: розкриття чи кінець.</summary>
    void StandStill()
    {
        foreach (var v in Core.V)
        {
            v.Want = -1;
            v.Moving = false;
        }
    }

    FreezeReveal RevealOf(int[] winners, string why, Func<FreezeSeat, bool> who, int[] pts)
    {
        var ids = new List<(int, int)>();
        var rows = new List<FreezeRow>();
        var trails = new List<(int, int[])>();
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Plays || s.Me < 0 || !who(s)) continue;
            var win = why != "left" && Array.IndexOf(winners, i) >= 0;
            ids.Add((i, s.Me));
            rows.Add(new FreezeRow(i, Core.V[s.Me].X, s.Caught, s.Hits, win, pts[i], s.Jugs));
            trails.Add((i, TrailOf(s)));
        }
        return new FreezeReveal(winners, why, [.. ids], [.. rows], [.. trails]);
    }

    void FinishMatch()
    {
        _phase = PhaseOver;
        _left = 0;
        var active = new List<int>();
        for (var i = 0; i < Seats; i++)
            if (_s[i].Active) active.Add(i);
        if (_relay) { FinishRelay(active); return; }
        var best = active.Count == 0 ? 0 : active.Max(i => _s[i].Total);
        var top = active.Where(i => _s[i].Total == best).ToArray();
        var winners = top.Length == active.Count ? [] : top;
        _winners = winners;
        _dirty = true;
        var order = winners.Concat(active.Where(i => Array.IndexOf(winners, i) < 0).OrderByDescending(i => _s[i].Total));
        var line = string.Join(" : ", order.Select(i => $"{BotNick(i)} {_s[i].Total}"));
        if (_bots.Length > 0)
        {
            FinishWithBots(winners, $"«{Info.Title}»: {line}", line);
            return;
        }
        foreach (var i in active) Ctx.Score(i, _s[i].Total);
        Ctx.Finish(winners, winners.Length > 0 ? $"«{Info.Title}»: {line}" : $"«{Info.Title}»: {line} — нічия");
    }

    /// <summary>
    /// Естафета: партію бере команда з більшою кількістю виграних раундів, рівно — з більшою сумою очок, рівно й тут —
    /// нічия. Журнал: «Замри!» естафета: 🌻 Соняшники (Оля, Іван) 2 : 1 💠 Волошки (Петро, Ганна).
    /// </summary>
    void FinishRelay(List<int> active)
    {
        int Cmp() => _teamRounds[0] != _teamRounds[1] ? _teamRounds[0].CompareTo(_teamRounds[1]) : _teamTotal[0].CompareTo(_teamTotal[1]);
        var c = Cmp();
        var team = c > 0 ? 0 : c < 0 ? 1 : -1;
        var winners = team < 0 ? [] : active.Where(i => _s[i].Team == team).ToArray();
        _winners = winners;
        _dirty = true;
        string Side(int t) => $"{TeamNames[t]} ({string.Join(", ", active.Where(i => _s[i].Team == t).Select(i => _s[i].Nick))})";
        var first = team < 0 ? 0 : team;
        var line = $"«{Info.Title}» естафета: {Side(first)} {_teamRounds[first]} : {_teamRounds[1 - first]} {Side(1 - first)}";
        if (_bots.Length > 0)
        {
            FinishWithBots(winners, line, $"{Side(first)} {_teamRounds[first]} : {_teamRounds[1 - first]} {Side(1 - first)}");
            return;
        }
        foreach (var i in active) Ctx.Score(i, _s[i].Total);
        Ctx.Finish(winners, team < 0 ? line + " — нічия" : line);
    }

    /// <summary>
    /// Хтось устав: його селянин лишається на лузі й стає ботом — вихід нікого не викриває. Лишився один — партія його;
    /// нікого — нічия.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (!_started || seat < 0 || seat >= Seats || _phase == PhaseOver) return;
        var s = _s[seat];
        if (!s.Active) return;
        s.Out = true;
        if (s.Me >= 0 && s.Me < Core.N)
        {
            var v = Core.V[s.Me];
            v.Owner = -1;
            FreezeCore.Forget(v);
            // бот живе за Бабою: якщо вона вже обертається чи дивиться — стоїть, як усі
            if (Core.Baba is FreezeCore.Turn or FreezeCore.Watch) v.Hold = true;
        }
        _dirty = true;

        var rest = new List<int>();
        for (var i = 0; i < Seats; i++)
            if (i != seat && _s[i].Active && Ctx.Seated(i)) rest.Add(i);
        // естафета: поки в обох командах хтось є — граємо далі (друга команда зосталась сама — їй і перемога)
        var teamGone = _relay && rest.Count > 0 && rest.All(i => _s[i].Team == _s[rest[0]].Team);
        if (rest.Count > 1 && !teamGone) return;
        if (!(_phase == PhaseReveal && _reveal is not null)) _reveal = RevealOf([.. rest], "left", x => x.Me >= 0, new int[Seats]);
        StandStill();
        _phase = PhaseOver;
        _left = 0;
        _endWhy = "left";
        _winners = [.. rest];
        foreach (var i in rest) Ctx.Score(i, _s[i].Total);
        Ctx.Finish([.. rest], teamGone && rest.Count > 1
            ? $"«{Info.Title}»: суперники розійшлись — перемога за {TeamNames[_s[rest[0]].Team]}"
            : rest.Count == 1
            ? $"«{Info.Title}»: усі розійшлись — {_s[rest[0]].Nick} сам-на-сам із Бабою Параскою"
            : $"«{Info.Title}»: усі розійшлись");
    }

    // ---------------------------------------------------------------------------------------------
    // Кадр і вид
    // ---------------------------------------------------------------------------------------------

    /// <summary>Що показувати про Бабу: на «роздивись» вона дивиться, а відлік — до старту.</summary>
    int BabaNow => _phase == PhaseGo ? Core.Baba : FreezeCore.Watch;
    int BabaLeftNow => _phase switch
    {
        PhaseGo => Core.Countdown,
        PhaseStart => _left,
        _ => 0,
    };

    /// <summary>
    /// Кадр: тик, фаза, скільки лишилось, що робить Баба й скільки ще дивитиметься, 4 числа на селянина і події цього
    /// тика. Нічого про місця — ні owner, ні me: бота від гравця за цими числами не відрізнити.
    /// </summary>
    public override object? Frame() => new
    {
        t = _t,
        ph = _phase,
        left = _left,
        b = BabaNow,
        bl = BabaLeftNow,
        v = _started ? Core.Pack() : Preview.Value.V,
        ev = _evFrame,
    };

    /// <summary>Лобі: юрма стоїть біля тину, поки господар не натисне «Почати».</summary>
    static readonly Lazy<(int[] V, int[] Looks, string[] Names)> Preview = new(() =>
    {
        var core = new FreezeCore(new Random(2709));
        core.Deal([], 20);
        return (core.Pack(), core.Looks(), core.NamesNow());
    });

    public override object View(int? seat)
    {
        var live = _started;
        var me = seat is { } k && k >= 0 && k < Seats && _s[k].Active && live && _s[k].Me >= 0 ? MeOf(k) : null;
        var seats = new List<object>();
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (live && s.Plays)
                seats.Add(new { seat = i, nick = s.Nick, @out = s.Out, total = s.Total });
            else if (!live && Ctx.Seated(i))
                seats.Add(new { seat = i, nick = Ctx.NickOf(i) ?? "", @out = false, total = 0 });
        }
        return new
        {
            phase = _phase,
            round = _round,
            of = _rounds,
            left = _left,
            t = _t,
            w = FreezeCore.WorldW,
            h = FreezeCore.WorldH,
            finish = FreezeCore.FinishX,
            n = live ? Core.N : Preview.Value.V.Length / 4,
            looks = live ? Core.Looks() : Preview.Value.Looks,
            names = live ? Core.NamesNow() : Preview.Value.Names,
            v = live ? Core.Pack() : Preview.Value.V,
            b = BabaNow,
            bl = BabaLeftNow,
            seats,
            me,
            reveal = _reveal is { } r && (_phase is PhaseReveal or PhaseOver)
                ? new
                {
                    winners = r.Winners,
                    why = r.Why,
                    ids = r.Ids.Select(p => new { seat = p.Seat, id = p.Id }).ToArray(),
                    rows = r.Rows.Select(x => new { seat = x.Seat, x = x.X, caught = x.Caught, hits = x.Hits, win = x.Win, pts = x.Pts, jugs = x.Jugs }).ToArray(),
                    trails = r.Trails.Select(p => new { seat = p.Seat, pts = p.Pts }).ToArray(),
                }
                : null,
            result = _phase == PhaseOver && _winners is { } w
                ? new { winners = w, totals = _s.Select(x => x.Total).ToArray(), why = _endWhy }
                : null,
            // естафета — публічно: хто в якій команді, очки раунду й партії (хто ким на лузі — ні)
            relay = !_relay ? null : new
            {
                goal = RelayGoal,
                teams = Enumerable.Range(0, 2).Select(t => new
                {
                    name = TeamNames[t],
                    seats = Enumerable.Range(0, Seats).Where(i => _s[i].Team == t && (live ? _s[i].Plays : Ctx.Seated(i))).ToArray(),
                    pts = _teamPts[t],
                    total = _teamTotal[t],
                    rounds = _teamRounds[t],
                    weight = _teamWeight[t],
                }).ToArray(),
            },
            turn = (int?)null,
            botOffer = _solo.Offer(Ctx, Seats),
            botWanted = _solo.Wanted,
            botLvl = _solo.LevelKey,
            bot = BotView(),
        };
    }

    /// <summary>
    /// Своє: хто я на лузі, скільки ще відходять руки і скільки разів мене впіймали. Вид летить лише на зміну фази, тож
    /// ці числа свіжі після F5, а посеред раунду модуль рахує їх сам — розсилка на кожен штурхан видала б гравця.
    /// </summary>
    object MeOf(int seat)
    {
        var s = _s[seat];
        var v = Core.V[s.Me];
        // естафета: своїх у команді знаєш у лице — [місце, id…]; суперників — ні
        int[]? mates = null;
        if (_relay && s.Team >= 0)
        {
            var list = new List<int>();
            for (var i = 0; i < Seats; i++)
                if (i != seat && _s[i].Active && _s[i].Team == s.Team && _s[i].Me >= 0) { list.Add(i); list.Add(_s[i].Me); }
            mates = [.. list];
        }
        return new { id = s.Me, cool = v.PushCool, caught = s.Caught, mates };
    }
}
