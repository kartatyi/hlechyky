using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Стан місця за партію й раунд. Серця, кухлі й «хто я» — таємниця місця, решта — публічна.</summary>
public sealed class TavernSeat
{
    /// <summary>Сидів на старті партії.</summary>
    public bool Plays;
    /// <summary>Устав посеред партії: його відвідувач — уже бот, а очки лишаються в таблиці.</summary>
    public bool Out;
    public string Nick = "";
    /// <summary>На ногах цього раунду (серця ще є).</summary>
    public bool Alive;
    /// <summary>Id свого відвідувача в цьому раунді.</summary>
    public int Me = -1;
    public int Hearts;
    /// <summary>З яких місць уже випито (індекс — місце).</summary>
    public readonly bool[] Places = new bool[TavernMap.Places.Length];
    /// <summary>Кухлів у різних місцях за раунд — таємниця до розкриття, як і очки раунду.</summary>
    public int Mugs;
    /// <summary>Скільки сердець вибив із гравців і скількох вибив зовсім.</summary>
    public int Hits, Kos;
    /// <summary>Скільки разів махнув кулаком цього раунду (для «Тихого гостя»).</summary>
    public int Punches;
    public int Total;
    /// <summary>
    /// Очки на початок раунду — їх і видно всім, поки раунд іде. Інакше «+1 Олі» в ту саму мить, коли хтось хильнув
    /// кухоль чи когось відкинуло, назвав би, хто це.
    /// </summary>
    public int ShownTotal;
    /// <summary>Тик (наскрізний) останнього <c>move</c>: затиснута стрілка без підтвердження гасне.</summary>
    public int MoveAt;
    /// <summary>Слід за раунд для розкриття: кільце останніх позицій свого відвідувача.</summary>
    public readonly int[] TrailX = new int[Tavern.TrailLen], TrailY = new int[Tavern.TrailLen];
    public int TrailHead, TrailCount;
    /// <summary>Тик раунду, коли випив третій кухоль; -1 — ще ні.</summary>
    public int CompletedAt = -1;

    public bool Active => Plays && !Out;
}

/// <summary>
/// «Корчма»: Unspottable у сільській корчмі. Між столами й лавами товчуться боти, і гравці — такі самі відвідувачі:
/// у кадрі їх не відрізнити нічим (той самий крок, ті самі числа, id тасуються щораунду). Знайди суперника й вдар —
/// три серця на раунд; вдарив бота — отетерів і видав себе. Тихий шлях — три кухлі в трьох різних місцях. А корчмар
/// за шинквасом інколи гримає «Хто тут б'ється?!» — і хто махне кулаком, поки він дивиться, летить за двері.
/// Правила залу — у <see cref="TavernCore"/>, тут фази, очки, дії, вид і кадр (spec: docs/games/specs/tavern.md).
/// </summary>
public sealed class Tavern : Game
{
    public const string PhaseLobby = "lobby", PhaseStart = "start", PhaseGo = "go", PhaseReveal = "reveal", PhaseOver = "over";
    public const int Seats = 8;
    public const int TickMs = 40;
    /// <summary>«Роздивись» — 3 с: ходити й сідати можна, бити й пити — ні.</summary>
    public const int StartTicks = 75;
    /// <summary>Раунд — 60 с.</summary>
    public const int RoundTicks = 1500;
    /// <summary>Розкриття — 6 с, усі завмерли, над гравцями ніки.</summary>
    public const int RevealTicks = 150;
    public const int Hearts = 3;
    /// <summary>Три кухлі в трьох різних місцях — раунд твій.</summary>
    public const int MugsToWin = 3;
    public const int WindTicks = TavernCore.WindTicks, DrinkTicks = TavernCore.DrinkTicks;
    public const int PtMug = 1, PtHit = 1, PtKo = 2, PtRound = 3;
    /// <summary>У розкритті кадр летить раз на стільки тиків (усі стоять — частіше нема чого).</summary>
    public const int RevealFrameEvery = 5;
    /// <summary>Затиснуту стрілку модуль підтверджує раз на секунду; не чули 3 с — зв'язок обірвався, відпускаємо.</summary>
    public const int MoveHoldTicks = 75;
    /// <summary>Слід на розкритті: точка раз на 12 тиків, 42 точки — останні ≈ 20 с.</summary>
    public const int TrailEvery = 12, TrailLen = 42;

    /// <summary>«Як у суботу»: на двох — 24 боти, далі більше, на вісьмох — 40.</summary>
    public static int BotsFor(int players) => players switch
    {
        <= 2 => 24,
        3 => 27,
        4 => 30,
        5 => 32,
        6 => 35,
        7 => 38,
        _ => 40,
    };

    public override GameInfo Info { get; } = new(
        "tavern", "Корчма", "корчму", GameGroup.Live, 2, Seats, TickMs: TickMs,
        Start: StartMode.ByHost, Hidden: true, Score: ScoreOrder.HigherIsBetter,
        Options:
        [
            new GameOption("rounds", "Раундів", [("5", "5 раундів"), ("3", "3 раунди"), ("7", "7 раундів")], "5"),
            new GameOption("crowd", "Люду", [("auto", "Як у суботу"), ("small", "Будній день (24)"), ("big", "Весілля (40)")], "auto"),
        ],
        Hint: "Повна корчма люду — і десь серед них твої друзі. Знайди й дай кулаком або тихенько випий три кухлі. Тільки не бийся, як корчмар дивиться");

    static readonly string[] SeatNames = ["жовтий", "зелений", "рудий", "сірий", "синій", "рожевий", "фіолетовий", "червоний"];

    readonly TavernSeat[] _s = [.. Enumerable.Range(0, Seats).Select(_ => new TavernSeat())];
    TavernCore? _core;
    int _rounds = 5;
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
    /// <summary>Події цього тика — рівно ті, що в кадрі.</summary>
    readonly List<int[]> _ev = [];
    int[][] _evFrame = [];
    TavernReveal? _reveal;
    int[]? _winners;

    sealed record TavernReveal(int[] Winners, string Why, (int Seat, int Id)[] Ids, TavernRow[] Rows, (int Seat, int[] Pts)[] Trails);
    sealed record TavernRow(int Seat, int Mugs, int Hits, int Kos, bool Win, int Pts);

    TavernCore Core => _core ??= new TavernCore(Ctx.Rng);

    /// <summary>Для тестів: ядро й місця напряму.</summary>
    public TavernCore CoreForTests => Core;
    public TavernSeat SeatForTests(int seat) => _s[seat];
    public string Phase => _phase;
    public int Left => _left;
    public int RoundNo => _round;

    public override string SeatName(int seat) => seat >= 0 && seat < Seats ? SeatNames[seat] : base.SeatName(seat);

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _rounds = options.TryGetValue("rounds", out var r) && int.TryParse(r, out var n) && n is 3 or 5 or 7 ? n : 5;
        _crowd = options.TryGetValue("crowd", out var c) && c is "small" or "big" ? c : "auto";
    }

    /// <summary>Скільки ботів за складом і опцією.</summary>
    public int BotsForTable(int players) => _crowd switch
    {
        "small" => 24,
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
        _ev.Clear();
        _evFrame = [];
        var players = 0;
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            s.Plays = Ctx.Seated(i);
            s.Out = false;
            s.Nick = Ctx.NickOf(i) ?? "";
            s.Total = s.ShownTotal = 0;
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
        Core.Open = false;
        foreach (var v in Core.V)
            if (v.Owner >= 0) _s[v.Owner].Me = v.Id;
        foreach (var seat in owners)
        {
            var s = _s[seat];
            s.Alive = true;
            s.Hearts = Hearts;
            Array.Clear(s.Places);
            s.Mugs = s.Hits = s.Kos = s.Punches = 0;
            s.ShownTotal = s.Total;
            s.MoveAt = _clock;
            s.TrailHead = s.TrailCount = 0;
            s.CompletedAt = -1;
        }
        for (var i = 0; i < Seats; i++)
            if (!_s[i].Active) _s[i].Me = -1;
        _dirty = true;
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
            "punch" => Punch(seat, payload),
            "drink" => DrinkAct(seat),
            "sit" => SitAct(seat),
            _ => ActResult.Fail("Тут так не ходять"),
        };
    }

    ActResult Move(int seat, JsonElement payload)
    {
        var dir = Dir(payload);
        if (dir is null or < -1 or > 3) return ActResult.Fail("Такого напрямку нема");
        if (_phase == PhaseOver) return ActResult.Fail("Раунд скінчився");
        var s = _s[seat];
        // Вибулому й у розкритті — приймаємо й мовчки не застосовуємо: тост нічого не мусить викривати.
        if (!s.Alive || _phase == PhaseReveal || s.Me < 0) return ActResult.Done;
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

    ActResult? PhaseRefusal(TavernSeat s)
    {
        if (_phase == PhaseStart) return ActResult.Fail("Зачекай, корчма ще не відчинилась");
        if (_phase != PhaseGo) return ActResult.Fail("Раунд скінчився");
        if (!s.Alive) return ActResult.Fail("Тебе вже винесли — дивись, хто кого");
        return null;
    }

    /// <summary>Чим зайнятий відвідувач, що не може ні вдарити, ні випити; null — вільний.</summary>
    static string? Busy(TavernGuest v) =>
        v.Fallen > 0 ? "Спершу підведись"
        : v.Knock > 0 || v.Dazed > 0 ? "Тебе ще хитає"
        : v.Wind > 0 ? "Ти вже замахнувся"
        : v.Drink > 0 ? "Спершу допий"
        : null;

    ActResult Punch(int seat, JsonElement payload)
    {
        var s = _s[seat];
        var dir = Field(payload, "dir", out var ok);
        if (!ok || dir is < 0 or > 3) return ActResult.Fail("Такого напрямку нема");
        if (PhaseRefusal(s) is { } no) return no;
        var me = Core.V[s.Me];
        if (Busy(me) is { } busy) return ActResult.Fail(busy);
        if (me.Sit) return ActResult.Fail("Сидячи не розмахнешся — встань");
        if (me.PunchCool > 0) return ActResult.Fail("Кулак ще не відпочив");
        // Замах іде в кадр станом 3 наступного тика — так само, як у бота. Вид не розсилаємо: «хтось замахнувся» не
        // мусить приходити інакше, ніж у бота.
        TavernCore.Swing(me, dir ?? -1, WindTicks);
        s.Punches++;
        return ActResult.Done;
    }

    ActResult DrinkAct(int seat)
    {
        var s = _s[seat];
        if (PhaseRefusal(s) is { } no) return no;
        var me = Core.V[s.Me];
        if (Busy(me) is { } busy) return ActResult.Fail(busy == "Спершу допий" ? "Ти вже п'єш" : busy);
        if (me.Sit) return ActResult.Fail("Кухоль — біля шинквасу чи бочки, не на лаві");
        var place = TavernCore.PlaceAt(me);
        if (place < 0) return ActResult.Fail("Підійди до шинквасу чи бочки");
        if (me.DrinkCool > 0) return ActResult.Fail("Дай духу перевести");
        // П'є секунду обличчям до бочки; кухоль у руці видно всім (стан 4) — як і в бота, що п'є.
        me.Drink = DrinkTicks;
        me.DrinkPlace = place;
        me.Dir = TavernMap.Places[place].Face;
        me.Moving = false;
        return ActResult.Done;
    }

    ActResult SitAct(int seat)
    {
        var s = _s[seat];
        if (_phase is PhaseReveal or PhaseOver) return ActResult.Fail("Раунд скінчився");
        if (!s.Alive) return ActResult.Fail("Тебе вже винесли — дивись, хто кого");
        var me = Core.V[s.Me];
        if (Busy(me) is { } busy) return ActResult.Fail(busy);
        if (me.Sit)
        {
            // Устати — та сама мить, що й після стрілки: Step рахує її однаково для всіх.
            if (me.Rise == 0) me.Rise = TavernCore.RiseTicks;
            return ActResult.Done;
        }
        var face = TavernCore.BenchAt(me);
        if (face < 0) return ActResult.Fail("Тут нема лави — сідають на лаву біля столу");
        me.Sit = true;
        me.Dir = face;
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
                _ev.Clear();
                _t++;
                _clock++;
                HeldKeys();
                Core.BarmanTick();
                Core.ThinkAll();
                Core.StepAll();
                Trail();
                if (--_left <= 0)
                {
                    _phase = PhaseGo;
                    _left = RoundTicks;
                    Core.Open = true;
                    _dirty = true;
                }
                return Flush(true);
            case PhaseGo:
                _ev.Clear();
                _t++;
                _clock++;
                HeldKeys();
                Core.TimersAll();
                if (Core.BarmanTick()) _ev.Add([4, 1]);
                foreach (var id in Core.Struck) Strike(Core.V[id]);
                foreach (var id in Core.Drank) Drank(Core.V[id]);
                Core.ThinkAll();
                Core.StepAll();
                Trail();
                _left--;
                EndCheck();
                return Flush(true);
            case PhaseReveal:
                _ev.Clear();
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

    TickResult Flush(bool frame)
    {
        _evFrame = _ev.Count == 0 ? [] : [.. _ev];
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
            if (!s.Active || !s.Alive || s.Me < 0) continue;
            var v = Core.V[s.Me];
            if (v.Want >= 0 && _clock - s.MoveAt > MoveHoldTicks) v.Want = -1;
        }
    }

    /// <summary>Раз на <see cref="TrailEvery"/> тиків — точка в слід кожного гравця на ногах (для розкриття).</summary>
    void Trail()
    {
        if (_t % TrailEvery != 0) return;
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Active || !s.Alive || s.Me < 0) continue;
            var v = Core.V[s.Me];
            s.TrailX[s.TrailHead] = v.X;
            s.TrailY[s.TrailHead] = v.Y;
            s.TrailHead = (s.TrailHead + 1) % TrailLen;
            if (s.TrailCount < TrailLen) s.TrailCount++;
        }
    }

    /// <summary>Слід місця від найстарішої точки до найновішої: x, y, x, y…</summary>
    static int[] TrailOf(TavernSeat s)
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
    /// Кулак долетів. Гравець → бот: бот падає й обурено кричить, а гравець отетеріло кліпає (так і видає себе).
    /// Гравець → гравець: мінус серце й відкидає (нуль — вибув, нік усім). Бот → будь-хто: навпіл — упав (і бот
    /// отетерів) або відкинуло, без сердець. Тож і «впав + отетерів», і «відкинуло» бувають від ботів — удар не вирок.
    /// Махнув кулаком, поки корчмар дивиться, — за двері (гравцеві ще й мінус серце).
    /// </summary>
    void Strike(TavernGuest a)
    {
        if (a.Out) return;
        a.PunchCool = TavernCore.PunchCoolTicks;
        var watched = Core.Barman.Watching;
        var t = Core.Reach(a);
        if (t < 0) _ev.Add([1, a.Id, -1, 0, -1]);
        else
        {
            var q = Core.V[t];
            if (a.Owner >= 0 && q.Owner >= 0)
            {
                var victim = _s[q.Owner];
                var by = _s[a.Owner];
                victim.Hearts--;
                by.Hits++;
                by.Total += PtHit;
                if (victim.Hearts <= 0)
                {
                    by.Kos++;
                    by.Total += PtKo;
                    KnockOutSeat(q);
                    _ev.Add([1, a.Id, t, 3, q.Owner]);
                }
                else
                {
                    Core.Hit(a, q, fall: false);
                    _ev.Add([1, a.Id, t, 2, -1]);
                }
            }
            else
            {
                var fall = a.Owner >= 0 || Core.CoinFall();
                Core.Hit(a, q, fall);
                _ev.Add([1, a.Id, t, fall ? 1 : 2, -1]);
            }
            Core.Provoke();
        }
        if (watched) ThrowOut(a);
        _dirty = true;
    }

    /// <summary>Корчмар бачив, як ти махнув кулаком: за двері. Гравцеві — мінус серце; останнє — вибув.</summary>
    void ThrowOut(TavernGuest a)
    {
        Core.ThrowOut(a);
        var seat = -1;
        if (a.Owner >= 0 && !a.Out)
        {
            var s = _s[a.Owner];
            if (--s.Hearts <= 0)
            {
                KnockOutSeat(a);
                seat = a.Owner;
            }
        }
        _ev.Add([3, a.Id, seat]);
    }

    void KnockOutSeat(TavernGuest v)
    {
        TavernCore.KnockOut(v);
        var s = _s[v.Owner];
        s.Hearts = 0;
        s.Alive = false;
    }

    /// <summary>
    /// Допив кухоль: подія всім (кухоль і так видно в руці), вид розсилаємо щоразу — хоч пив гравець, хоч бот. Гравцеві
    /// зараховано, якщо тут він ще не пив.
    /// </summary>
    void Drank(TavernGuest v)
    {
        var place = v.DrinkPlace;
        v.DrinkPlace = -1;
        if (place < 0) return;
        v.DrinkCool = TavernCore.DrinkCoolTicks;
        _ev.Add([2, place]);
        _dirty = true;
        if (v.Owner < 0) return;
        var s = _s[v.Owner];
        if (!s.Alive || s.Places[place]) return;
        s.Places[place] = true;
        s.Mugs++;
        s.Total += PtMug;
        if (s.Mugs >= MugsToWin) s.CompletedAt = _t;
    }

    /// <summary>Кінець раунду: хтось допив третій кухоль цього тика → на ногах ≤ 1 → вийшов час.</summary>
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
        if (done is not null) { EndRound([.. done], "mugs"); return; }
        if (active >= 2 && alive <= 1) { EndRound(alive == 1 ? [last] : [], alive == 1 ? "last" : "none"); return; }
        if (_left > 0) return;

        // вийшов час: у кого більше кухлів серед тих, хто на ногах; порівну — у кого більше сердець; знову порівну — ніхто
        int bestMugs = -1, bestHearts = -1, count = 0, who = -1;
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Active || !s.Alive) continue;
            if (s.Mugs > bestMugs || (s.Mugs == bestMugs && s.Hearts > bestHearts))
            {
                bestMugs = s.Mugs;
                bestHearts = s.Hearts;
                count = 1;
                who = i;
            }
            else if (s.Mugs == bestMugs && s.Hearts == bestHearts) count++;
        }
        if (count == 1) EndRound([who], "time");
        else EndRound([], "none");
    }

    void EndRound(int[] winners, string why)
    {
        foreach (var w in winners)
        {
            _s[w].Total += PtRound;
            if (why == "mugs" && _s[w].Punches == 0) Ctx.Award(w, 0, "ach:tavern-quiet");
        }
        for (var i = 0; i < Seats; i++)
            if (_s[i].Active && _s[i].Kos >= 2) Ctx.Award(i, 0, "ach:tavern-ko");
        _reveal = RevealOf(winners, why, s => s.Active);
        Freeze();
        _phase = PhaseReveal;
        _left = RevealTicks;
        _dirty = true;
    }

    /// <summary>Усі завмирають: недопите не рахується, замах не долітає, кого несло — зупинився.</summary>
    void Freeze()
    {
        foreach (var v in Core.V)
        {
            v.Want = -1;
            v.Moving = false;
            v.Wind = 0;
            v.Drink = 0;
            v.DrinkPlace = -1;
            v.Knock = 0;
            v.Rise = 0;
        }
    }

    /// <summary>Розкриття: хто ким був, рядки раунду й сліди — для місць, що пройшли фільтр.</summary>
    TavernReveal RevealOf(int[] winners, string why, Func<TavernSeat, bool> who)
    {
        var ids = new List<(int, int)>();
        var rows = new List<TavernRow>();
        var trails = new List<(int, int[])>();
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Plays || s.Me < 0 || !who(s)) continue;
            // «усі розійшлись» — не виграний раунд: +3 не платимо, тож і в рядку його нема
            var win = why != "left" && Array.IndexOf(winners, i) >= 0;
            ids.Add((i, s.Me));
            rows.Add(new TavernRow(i, s.Mugs, s.Hits, s.Kos, win, s.Mugs * PtMug + s.Hits * PtHit + s.Kos * PtKo + (win ? PtRound : 0)));
            trails.Add((i, TrailOf(s)));
        }
        return new TavernReveal(winners, why, [.. ids], [.. rows], [.. trails]);
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
    /// Хтось устав: його відвідувач лишається в корчмі й стає ботом — вихід нікого не викриває. Лишився один — партія
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
            TavernCore.Forget(v);
        }
        _dirty = true;

        var rest = new List<int>();
        for (var i = 0; i < Seats; i++)
            if (i != seat && _s[i].Active && Ctx.Seated(i)) rest.Add(i);
        if (rest.Count > 1) return;
        // Партія скінчилась посеред раунду: корчма завмирає, а всім показуємо, хто ким був, — і того, хто пішов. Якщо
        // раунд уже розкрито, лишаємо його підсумок.
        if (!(_phase == PhaseReveal && _reveal is not null)) _reveal = RevealOf([.. rest], "left", x => x.Me >= 0);
        Freeze();
        _phase = PhaseOver;
        _left = 0;
        _endWhy = "left";
        _winners = [.. rest];
        foreach (var i in rest) Ctx.Score(i, _s[i].Total);
        Ctx.Finish([.. rest], rest.Count == 1
            ? $"{Info.Title}: усі розійшлись — {_s[rest[0]].Nick} допиває сам-на-сам із корчмарем"
            : $"{Info.Title}: усі розійшлись");
    }

    // ---------------------------------------------------------------------------------------------
    // Кадр і вид
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Кадр: тик, фаза, скільки лишилось, 4 числа на відвідувача, 4 числа корчмаря і події цього тика. Нічого про
    /// місця — ні owner, ні me, ні сердець, ні кухлів: бота від гравця за цими числами не відрізнити.
    /// </summary>
    public override object? Frame() => new
    {
        t = _t,
        ph = _phase,
        left = _left,
        v = _started ? Core.Pack() : Preview.Value.V,
        k = _started ? Core.PackBarman() : Preview.Value.K,
        ev = _evFrame,
    };

    /// <summary>Статична корчма для лобі: люд стоїть і сидить, поки господар не натисне «Почати».</summary>
    static readonly Lazy<(int[] V, int[] K, int[] Looks, string[] Names)> Preview = new(() =>
    {
        var core = new TavernCore(new Random(2709));
        core.Deal([], 24);
        return (core.Pack(), core.PackBarman(), core.Looks(), core.NamesNow());
    });

    static readonly object[] Places =
    [
        .. TavernMap.Places.Select(p => (object)new
        {
            i = p.I, name = p.Name, what = p.What, emoji = p.Emoji, x = p.X, y = p.Y, w = p.W, face = p.Face,
            cells = p.Cells, fx = p.Fx, fy = p.Fy,
        }),
    ];

    public override object View(int? seat)
    {
        var live = _started;
        var me = seat is { } k && k >= 0 && k < Seats && _s[k].Active && live && _s[k].Me >= 0 ? MeOf(k) : null;
        // Кухлі й очки раунду — лише на розкритті: посеред раунду «+1» комусь назвав би того, хто щойно хильнув чи вдарив.
        var open = _phase is PhaseReveal or PhaseOver;
        var seats = new List<object>();
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (live && s.Plays)
                seats.Add(new { seat = i, nick = s.Nick, alive = s.Alive, @out = s.Out, mugs = open ? s.Mugs : (int?)null, total = open ? s.Total : s.ShownTotal });
            else if (!live && Ctx.Seated(i))
                seats.Add(new { seat = i, nick = Ctx.NickOf(i) ?? "", alive = true, @out = false, mugs = (int?)null, total = 0 });
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
            width = TavernMap.W,
            height = TavernMap.H,
            cell = TavernMap.Cell,
            n = live ? Core.N : Preview.Value.V.Length / 4,
            map = TavernMap.Rows,
            places = Places,
            looks = live ? Core.Looks() : Preview.Value.Looks,
            names = live ? Core.NamesNow() : Preview.Value.Names,
            v = live ? Core.Pack() : Preview.Value.V,
            k = live ? Core.PackBarman() : Preview.Value.K,
            seats,
            dead,
            me,
            reveal = _reveal is { } r && (_phase is PhaseReveal or PhaseOver)
                ? new
                {
                    winners = r.Winners,
                    why = r.Why,
                    ids = r.Ids.Select(p => new { seat = p.Seat, id = p.Id }).ToArray(),
                    rows = r.Rows.Select(x => new { seat = x.Seat, mugs = x.Mugs, hits = x.Hits, kos = x.Kos, win = x.Win, pts = x.Pts }).ToArray(),
                    trails = r.Trails.Select(p => new { seat = p.Seat, pts = p.Pts }).ToArray(),
                }
                : null,
            result = _phase == PhaseOver && _winners is { } w
                ? new { winners = w, totals = _s.Select(x => x.Total).ToArray(), why = _endWhy }
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
            hearts = s.Hearts,
            places = (bool[])s.Places.Clone(),
            mugs = s.Mugs,
            cool = v.PunchCool,
            drinkCool = v.DrinkCool,
            drink = v.Drink,
            sit = v.Sit,
            alive = s.Alive,
        };
    }
}
