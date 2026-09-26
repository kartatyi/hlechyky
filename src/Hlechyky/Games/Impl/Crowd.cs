using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Стан місця за партію й раунд. Список лотків, камінці й «хто я» — таємниця місця, решта — публічна.</summary>
public sealed class CrowdSeat
{
    /// <summary>Сидів на старті партії.</summary>
    public bool Plays;
    /// <summary>Устав посеред партії: його селянин — уже бот, а очки лишаються в таблиці.</summary>
    public bool Out;
    public string Nick = "";
    public bool Alive;
    /// <summary>Id свого селянина в цьому раунді.</summary>
    public int Me = -1;
    public readonly int[] List = new int[4];
    public readonly bool[] Done = new bool[4];
    public int Stones, ShotCool, BuyCool;
    /// <summary>Списочних покупок за раунд (публічно).</summary>
    public int Bought;
    public int Kills;
    public int Total;
    public int Shots;
    /// <summary>Перший камінець раунду влучив у гравця — на ачівку «Око-алмаз».</summary>
    public bool Eye;
    /// <summary>Тик раунду, коли список скуплено; -1 — ще ні.</summary>
    public int CompletedAt = -1;

    public bool Active => Plays && !Out;
    public bool Complete => Done[0] && Done[1] && Done[2] && Done[3];
}

/// <summary>
/// «Юрма»: Hidden in Plain Sight на сільському ярмарку. Між лотками гуляє юрма ботів, і гравці — такі самі
/// селяни: у кадрі їх не відрізнити нічим (той самий крок, ті самі числа, id тасуються щораунду). Хто свій,
/// знає лише своє місце з виду. Скупись за списком із чотирьох лотків або вистеж суперників рогаткою.
/// Правила поля — у <see cref="CrowdCore"/>, тут фази, очки, дії, вид і кадр (spec: docs/games/specs/crowd.md).
/// </summary>
public sealed class Crowd : Game
{
    public const string PhaseLobby = "lobby", PhaseStart = "start", PhaseGo = "go", PhaseReveal = "reveal", PhaseOver = "over";
    public const int Seats = 8;
    public const int TickMs = 40;
    /// <summary>«Роздивись» — 3 с: ходити можна, стріляти й купувати — ні.</summary>
    public const int StartTicks = 75;
    /// <summary>Раунд — 90 с.</summary>
    public const int RoundTicks = 2250;
    /// <summary>Розкриття — 6 с, усі завмерли, над гравцями ніки.</summary>
    public const int RevealTicks = 150;
    public const int Stones = 3;
    public const int ShotCoolTicks = 25;
    public const int HaggleTicks = 25;
    public const int BuyCoolTicks = 50;
    public const int PtBuy = 1, PtKill = 2, PtRound = 3;
    /// <summary>У розкритті кадр летить раз на стільки тиків (усі стоять — частіше нема чого).</summary>
    public const int RevealFrameEvery = 5;

    /// <summary>«Як на ярмарку»: на двох — 24 боти, далі більше, на вісьмох — 40.</summary>
    public static int BotsFor(int players) => players switch
    {
        <= 2 => 24,
        3 => 28,
        4 => 32,
        5 => 34,
        6 => 36,
        7 => 38,
        _ => 40,
    };

    public override GameInfo Info { get; } = new(
        "crowd", "Юрма", "юрму", GameGroup.Live, 2, Seats, TickMs: TickMs,
        Start: StartMode.ByHost, Hidden: true, Score: ScoreOrder.HigherIsBetter,
        Options:
        [
            new GameOption("rounds", "Раундів", [("3", "3 раунди"), ("1", "1 раунд"), ("5", "5 раундів")], "3"),
            new GameOption("crowd", "Юрма", [("auto", "Як на ярмарку"), ("small", "Рідка (20)"), ("big", "Тиснява (48)")], "auto"),
        ],
        Hint: "Ярмарок, повно люду — і десь серед них твої друзі. Скупись за списком або вистеж їх із рогатки. Ніхто не знає, хто з селян живий");

    static readonly string[] SeatNames = ["жовтий", "зелений", "рудий", "сірий", "синій", "рожевий", "фіолетовий", "червоний"];

    readonly CrowdSeat[] _s = [.. Enumerable.Range(0, Seats).Select(_ => new CrowdSeat())];
    CrowdCore? _core;
    int _rounds = 3;
    string _crowd = "auto";
    bool _started;
    string _phase = PhaseLobby;
    int _round;
    int _left;
    /// <summary>Тик раунду (з «роздивись» включно).</summary>
    int _t;
    int _n;
    bool _dirty;
    /// <summary>Події з дій між тиками — підуть у наступний кадр.</summary>
    readonly List<int[]> _pending = [];
    /// <summary>Події цього тика — рівно ті, що в кадрі.</summary>
    readonly List<int[]> _ev = [];
    int[][] _evFrame = [];
    CrowdReveal? _reveal;
    int[]? _winners;

    sealed record CrowdReveal(int[] Winners, string Why, (int Seat, int Id)[] Ids, CrowdRow[] Rows);
    sealed record CrowdRow(int Seat, int Buy, int Kills, bool Win, int Pts);

    CrowdCore Core => _core ??= new CrowdCore(Ctx.Rng);

    /// <summary>Для тестів: ядро й місця напряму.</summary>
    public CrowdCore CoreForTests => Core;
    public CrowdSeat SeatForTests(int seat) => _s[seat];
    public string Phase => _phase;
    public int Left => _left;
    public int RoundNo => _round;

    public override string SeatName(int seat) => seat >= 0 && seat < Seats ? SeatNames[seat] : base.SeatName(seat);

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _rounds = options.TryGetValue("rounds", out var r) && int.TryParse(r, out var n) && n is 1 or 3 or 5 ? n : 3;
        _crowd = options.TryGetValue("crowd", out var c) && c is "small" or "big" ? c : "auto";
    }

    /// <summary>Скільки ботів за складом і опцією.</summary>
    public int BotsForTable(int players) => _crowd switch
    {
        "small" => 20,
        "big" => 48,
        _ => BotsFor(players),
    };

    public override void Start()
    {
        _started = true;
        _round = 0;
        _winners = null;
        _reveal = null;
        _pending.Clear();
        _ev.Clear();
        _evFrame = [];
        var players = 0;
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            s.Plays = Ctx.Seated(i);
            s.Out = false;
            s.Nick = Ctx.NickOf(i) ?? "";
            s.Total = 0;
            if (s.Plays) players++;
        }
        _n = players + BotsForTable(players);
        NewRound();
    }

    void NewRound()
    {
        _round++;
        _phase = PhaseStart;
        _left = StartTicks;
        _t = 0;
        _reveal = null;
        var owners = new List<int>(Seats);
        for (var i = 0; i < Seats; i++)
            if (_s[i].Active) owners.Add(i);
        Core.Deal(owners, Math.Max(0, _n - owners.Count));
        foreach (var v in Core.V)
            if (v.Owner >= 0) _s[v.Owner].Me = v.Id;
        var rng = Ctx.Rng;
        foreach (var seat in owners)
        {
            var s = _s[seat];
            s.Alive = true;
            s.Stones = Stones;
            s.ShotCool = s.BuyCool = 0;
            s.Bought = s.Kills = s.Shots = 0;
            s.Eye = false;
            s.CompletedAt = -1;
            DealList(rng, s);
        }
        for (var i = 0; i < Seats; i++)
            if (!_s[i].Active) _s[i].Me = -1;
        _dirty = true;
    }

    /// <summary>Список: по одному з верхнього ряду, нижнього й боків, четвертий — будь-який із решти; порядок випадковий.</summary>
    static void DealList(Random rng, CrowdSeat s)
    {
        var top = CrowdMap.TopStalls[rng.Next(4)];
        var bottom = CrowdMap.BottomStalls[rng.Next(4)];
        var side = CrowdMap.SideStalls[rng.Next(4)];
        Span<int> rest = stackalloc int[CrowdMap.Stalls.Length];
        var m = 0;
        for (var k = 0; k < CrowdMap.Stalls.Length; k++)
            if (k != top && k != bottom && k != side) rest[m++] = k;
        var any = rest[rng.Next(m)];
        s.List[0] = top;
        s.List[1] = bottom;
        s.List[2] = side;
        s.List[3] = any;
        for (var i = 3; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (s.List[i], s.List[j]) = (s.List[j], s.List[i]);
        }
        Array.Clear(s.Done);
    }

    // ---------------------------------------------------------------------------------------------
    // Дії
    // ---------------------------------------------------------------------------------------------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (!_started) return ActResult.Fail("Партія ще не почалась");
        if (seat < 0 || seat >= Seats || !_s[seat].Active) return ActResult.Fail("Тут так не ходять");
        return action switch
        {
            "move" => Move(seat, payload),
            "shoot" => Shoot(seat, payload),
            "buy" => Buy(seat, payload),
            _ => ActResult.Fail("Тут так не ходять"),
        };
    }

    ActResult Move(int seat, JsonElement payload)
    {
        var dir = Dir(payload);
        if (dir is null or < -1 or > 3) return ActResult.Fail("Такого напрямку нема");
        if (_phase == PhaseOver) return ActResult.Fail("Раунд скінчився");
        var s = _s[seat];
        // Мертвому й у розкритті — приймаємо й мовчки не застосовуємо: тост нічого не мусить викривати.
        if (!s.Alive || _phase == PhaseReveal || s.Me < 0) return ActResult.Done;
        Core.V[s.Me].Want = dir.Value;
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

    ActResult? PhaseRefusal(CrowdSeat s)
    {
        if (_phase == PhaseStart) return ActResult.Fail("Зачекай, ярмарок ще не відкрився");
        if (_phase != PhaseGo) return ActResult.Fail("Раунд скінчився");
        if (!s.Alive) return ActResult.Fail("Тебе вже збили — дивись, хто кого");
        return null;
    }

    ActResult Shoot(int seat, JsonElement payload)
    {
        var s = _s[seat];
        if (PhaseRefusal(s) is { } no) return no;
        var me = Core.V[s.Me];
        if (me.Haggle > 0) return ActResult.Fail("Спершу доторгуйся");
        if (s.Stones <= 0) return ActResult.Fail("Камінці скінчились");
        if (s.ShotCool > 0) return ActResult.Fail("Рогатка ще натягується");

        var id = Field(payload, "id", out var ok);
        if (!ok) return ActResult.Fail("Такого селянина нема");
        int target;
        if (id is { } want)
        {
            if (want < 0 || want >= Core.N) return ActResult.Fail("Такого селянина нема");
            if (want == s.Me) return ActResult.Fail("У себе стріляти не годиться");
            var q = Core.V[want];
            if (!q.Upright) return ActResult.Fail("Лежачого не б'ють");
            if (CrowdCore.Dist2(q, me.X, me.Y) > (long)CrowdCore.ShotRangeMax * CrowdCore.ShotRangeMax)
                return ActResult.Fail("Далеко — не долетить");
            target = want;
        }
        else
        {
            target = Core.Cone(me);
            if (target < 0) return ActResult.Fail("Перед тобою нікого");
        }

        s.Stones--;
        s.ShotCool = ShotCoolTicks;
        s.Shots++;
        var hit = Core.V[target];
        if (hit.Owner >= 0)
        {
            var victim = _s[hit.Owner];
            victim.Alive = false;
            hit.Dead = true;
            hit.Haggle = 0;          // збили під час торгу — покупки нема
            hit.HaggleStall = -1;
            hit.Want = -1;
            hit.Moving = false;
            s.Kills++;
            s.Total += PtKill;
            if (s.Shots == 1) s.Eye = true;
            _pending.Add([1, me.Id, target, 1, hit.Owner]);
        }
        else
        {
            hit.Fallen = CrowdCore.FallTicks;
            hit.Moving = false;
            CrowdCore.Forget(hit);
            _pending.Add([1, me.Id, target, 0, -1]);
        }
        _dirty = true;
        return ActResult.Done;
    }

    ActResult Buy(int seat, JsonElement payload)
    {
        var s = _s[seat];
        if (PhaseRefusal(s) is { } no) return no;
        var me = Core.V[s.Me];
        if (me.Haggle > 0) return ActResult.Fail("Ти вже торгуєшся");
        if (s.BuyCool > 0) return ActResult.Fail("Продавець ще рахує решту");

        var want = Field(payload, "stall", out var ok);
        if (!ok) return ActResult.Fail("Такого лотка нема");
        if (want is { } bad && (bad < 0 || bad >= CrowdMap.Stalls.Length)) return ActResult.Fail("Такого лотка нема");
        // Торгуються лише з прилавка — двох клітинок стежки перед корпусом. Там само стоять і боти, тож торг
        // «із трави збоку» гравця не видасть: такого місця в юрмі просто нема.
        var stall = CrowdCore.CounterAt(me);
        if (stall < 0 || (want is { } k && k != stall)) return ActResult.Fail("Підійди до лотка ближче");

        // Торг: секунду стоїмо обличчям до лотка. Вид не розсилаємо — інакше «хтось почав торгуватись» видав би
        // гравця раніше за спалах; свій відлік клієнт веде сам від відповіді.
        me.Haggle = HaggleTicks;
        me.HaggleStall = stall;
        me.Dir = CrowdMap.Stalls[stall].Face;
        me.Moving = false;
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
                Core.ThinkAll();
                Core.StepAll();
                if (--_left <= 0)
                {
                    _phase = PhaseGo;
                    _left = RoundTicks;
                    _dirty = true;
                }
                return Flush(true);
            case PhaseGo:
                OpenEvents();
                _t++;
                Timers();
                Core.ThinkAll();
                Core.StepAll();
                _left--;
                EndCheck();
                return Flush(true);
            case PhaseReveal:
                OpenEvents();
                _t++;
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
        _ev.Clear();
        _ev.AddRange(_pending);
        _pending.Clear();
    }

    TickResult Flush(bool frame)
    {
        _evFrame = _ev.Count == 0 ? [] : [.. _ev];
        var view = _dirty;
        _dirty = false;
        return new TickResult(frame, view);
    }

    void Timers()
    {
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Active) continue;
            if (s.ShotCool > 0) s.ShotCool--;
            if (s.BuyCool > 0) s.BuyCool--;
        }
        foreach (var v in Core.V)
        {
            if (v.Haggle > 0 && --v.Haggle == 0) Bought(v);
            if (v.Fallen > 0 && --v.Fallen == 0) CrowdCore.Forget(v);
        }
    }

    /// <summary>Торг скінчився: лоток спалахує для всіх; зі списку — зараховано.</summary>
    void Bought(CrowdVillager v)
    {
        var stall = v.HaggleStall;
        v.HaggleStall = -1;
        if (v.Owner < 0 || stall < 0) return;
        var s = _s[v.Owner];
        s.BuyCool = BuyCoolTicks;
        _ev.Add([2, stall]);
        _dirty = true;
        for (var i = 0; i < 4; i++)
        {
            if (s.List[i] != stall || s.Done[i]) continue;
            s.Done[i] = true;
            s.Bought++;
            s.Total += PtBuy;
            if (s.Complete) s.CompletedAt = _t;
            break;
        }
    }

    /// <summary>Кінець раунду: хтось скупився цього тика → живих ≤ 1 → вийшов час.</summary>
    void EndCheck()
    {
        List<int>? done = null;
        int alive = 0, active = 0, last = -1;
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Active) continue;
            active++;
            if (!s.Alive) continue;
            alive++;
            last = i;
            if (s.CompletedAt == _t) (done ??= []).Add(i);
        }
        if (done is not null) { EndRound([.. done], "list"); return; }
        if (active >= 2 && alive <= 1) { EndRound(alive == 1 ? [last] : [], alive == 1 ? "last" : "none"); return; }
        if (_left > 0) return;

        int best = -1, count = 0, who = -1;
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Active || !s.Alive) continue;
            if (s.Bought > best) { best = s.Bought; count = 1; who = i; }
            else if (s.Bought == best) count++;
        }
        if (count == 1) EndRound([who], "time");
        else EndRound([], "none");
    }

    void EndRound(int[] winners, string why)
    {
        foreach (var w in winners)
        {
            _s[w].Total += PtRound;
            if (why == "list" && _s[w].Shots == 0) Ctx.Award(w, 0, "ach:crowd-quiet");
        }
        var ids = new List<(int, int)>();
        var rows = new List<CrowdRow>();
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Active) continue;
            if (s.Eye) Ctx.Award(i, 0, "ach:crowd-eye");
            var win = Array.IndexOf(winners, i) >= 0;
            ids.Add((i, s.Me));
            rows.Add(new CrowdRow(i, s.Bought, s.Kills, win, s.Bought * PtBuy + s.Kills * PtKill + (win ? PtRound : 0)));
        }
        _reveal = new CrowdReveal(winners, why, [.. ids], [.. rows]);
        foreach (var v in Core.V)
        {
            v.Want = -1;
            v.Moving = false;
            v.Haggle = 0;            // недоторгувались — не рахується
            v.HaggleStall = -1;
        }
        _phase = PhaseReveal;
        _left = RevealTicks;
        _dirty = true;
    }

    void FinishMatch()
    {
        _phase = PhaseOver;
        _left = 0;
        var active = new List<int>();
        for (var i = 0; i < Seats; i++)
            if (_s[i].Active) active.Add(i);
        var best = active.Count == 0 ? 0 : active.Max(i => _s[i].Total);
        var top = active.Where(i => _s[i].Total == best).ToArray();
        var winners = top.Length == active.Count ? [] : top;
        _winners = winners;
        _dirty = true;
        foreach (var i in active) Ctx.Score(i, _s[i].Total);
        var order = winners.Concat(active.Where(i => Array.IndexOf(winners, i) < 0).OrderByDescending(i => _s[i].Total));
        var line = string.Join(" : ", order.Select(i => $"{_s[i].Nick} {_s[i].Total}"));
        Ctx.Finish(winners, winners.Length > 0 ? $"{Info.Title}: {line}" : $"{Info.Title}: {line} — нічия");
    }

    /// <summary>
    /// Хтось устав: його селянин лишається в юрмі й стає ботом — вихід нікого не викриває. Лишився один — партія
    /// його; нікого — нічия.
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
            v.Haggle = 0;
            v.HaggleStall = -1;
            CrowdCore.Forget(v);
        }
        _dirty = true;

        var rest = new List<int>();
        for (var i = 0; i < Seats; i++)
            if (i != seat && _s[i].Active && Ctx.Seated(i)) rest.Add(i);
        if (rest.Count > 1) return;
        _phase = PhaseOver;
        _left = 0;
        _winners = [.. rest];
        foreach (var i in rest) Ctx.Score(i, _s[i].Total);
        Ctx.Finish([.. rest], rest.Count == 1
            ? $"{Info.Title}: усі розійшлись — {_s[rest[0]].Nick} лишається на ярмарку сам-на-сам із юрмою"
            : $"{Info.Title}: усі розійшлись");
    }

    // ---------------------------------------------------------------------------------------------
    // Кадр і вид
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Кадр: тик, фаза, скільки лишилось, 4 числа на селянина і події цього тика. Нічого про місця — ні owner,
    /// ні me, ні списків: бота від гравця за цими числами не відрізнити.
    /// </summary>
    public override object? Frame() => new
    {
        t = _t,
        ph = _phase,
        left = _left,
        v = _started ? Core.Pack() : Preview.Value.V,
        ev = _evFrame,
    };

    /// <summary>Статичний ярмарок для лобі: юрма стоїть, поки господар не натисне «Почати».</summary>
    static readonly Lazy<(int[] V, int[] Looks, string[] Names)> Preview = new(() =>
    {
        var core = new CrowdCore(new Random(2709));
        core.Deal([], 24);
        return (core.Pack(), core.Looks(), core.NamesNow());
    });

    static readonly object[] Stalls =
    [
        .. CrowdMap.Stalls.Select(s => (object)new
        {
            i = s.I, name = s.Name, what = s.What, emoji = s.Emoji, x = s.X, y = s.Y, fx = s.Fx, fy = s.Fy, face = s.Face,
        }),
    ];

    public override object View(int? seat)
    {
        var live = _started;
        var me = seat is { } k && k >= 0 && k < Seats && _s[k].Active && live && _s[k].Me >= 0 ? MeOf(k) : null;
        var seats = new List<object>();
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (live && s.Plays)
                seats.Add(new { seat = i, nick = s.Nick, alive = s.Alive, @out = s.Out, bought = s.Bought, total = s.Total });
            else if (!live && Ctx.Seated(i))
                seats.Add(new { seat = i, nick = Ctx.NickOf(i) ?? "", alive = true, @out = false, bought = 0, total = 0 });
        }
        var dead = new List<object>();
        if (live)
            for (var i = 0; i < Seats; i++)
                if (_s[i].Plays && !_s[i].Alive && _s[i].Me >= 0 && _phase != PhaseLobby)
                    dead.Add(new { seat = i, id = _s[i].Me });

        return new
        {
            phase = _phase,
            round = _round,
            of = _rounds,
            left = _left,
            t = _t,
            width = CrowdMap.W,
            height = CrowdMap.H,
            cell = CrowdMap.Cell,
            n = live ? Core.N : Preview.Value.V.Length / 4,
            map = CrowdMap.Rows,
            stalls = Stalls,
            looks = live ? Core.Looks() : Preview.Value.Looks,
            names = live ? Core.NamesNow() : Preview.Value.Names,
            v = live ? Core.Pack() : Preview.Value.V,
            seats,
            dead,
            me,
            reveal = _reveal is { } r && (_phase is PhaseReveal or PhaseOver)
                ? new
                {
                    winners = r.Winners,
                    why = r.Why,
                    ids = r.Ids.Select(p => new { seat = p.Seat, id = p.Id }).ToArray(),
                    rows = r.Rows.Select(x => new { seat = x.Seat, buy = x.Buy, kills = x.Kills, win = x.Win, pts = x.Pts }).ToArray(),
                }
                : null,
            result = _phase == PhaseOver && _winners is { } w
                ? new { winners = w, totals = _s.Select(x => x.Total).ToArray() }
                : null,
            turn = (int?)null,
        };
    }

    object MeOf(int seat)
    {
        var s = _s[seat];
        var v = Core.V[s.Me];
        return new
        {
            id = s.Me,
            list = (int[])s.List.Clone(),
            done = (bool[])s.Done.Clone(),
            stones = s.Stones,
            cool = s.ShotCool,
            buyCool = s.BuyCool,
            haggle = v.Haggle,
            alive = s.Alive,
        };
    }
}
