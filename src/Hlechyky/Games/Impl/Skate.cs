using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Стан місця за партію й раунд. Список ласощів і «хто я» — таємниця місця, решта — публічна.</summary>
public sealed class SkateSeat
{
    /// <summary>Сидів на старті партії.</summary>
    public bool Plays;
    /// <summary>Устав посеред партії: його селянин — уже бот, а очки лишаються в таблиці.</summary>
    public bool Out;
    public string Nick = "";
    /// <summary>На льоду: не шубовснув у цьому раунді.</summary>
    public bool Alive;
    /// <summary>Id свого селянина в цьому раунді.</summary>
    public int Me = -1;
    /// <summary>Чотири різні види ласощів, які треба підхопити.</summary>
    public readonly int[] List = new int[4];
    public readonly bool[] Got = new bool[4];
    /// <summary>Підхоплено зі списку за раунд — таємниця до розкриття, як і очки раунду.</summary>
    public int Items;
    /// <summary>Скількох гравців зіпхнув в ополонку за раунд.</summary>
    public int Kills;
    public int Total;
    /// <summary>
    /// Очки на початок раунду — їх і видно всім, поки раунд іде. Інакше «+1» комусь у ту саму мить, коли селянин
    /// над бубликом нахилився, назвав би, хто це, а «+2» — хто зіпхнув.
    /// </summary>
    public int ShownTotal;
    /// <summary>Тик (наскрізний, <c>Skate._clock</c>) останнього <c>move</c>: затиснута стрілка без підтвердження гасне.</summary>
    public int MoveAt;
    /// <summary>Слід за раунд для розкриття: кільце останніх позицій свого селянина.</summary>
    public readonly int[] TrailX = new int[Skate.TrailLen], TrailY = new int[Skate.TrailLen];
    public int TrailHead, TrailCount;
    /// <summary>Тик раунду, коли список зібрано; −1 — ще ні.</summary>
    public int CompletedAt = -1;

    public bool Active => Plays && !Out;
    public bool Complete => Got[0] && Got[1] && Got[2] && Got[3];
}

/// <summary>
/// «Ковзанка»: Hidden in Plain Sight на замерзлому ставку. Юрма селян катається на ковзанах, і гравці — такі самі:
/// та сама фізика з інерцією, той самий ввід (вісім напрямків і гальмо), id тасуються щораунду. Тихий гравець
/// збирає ласощі зі свого списку, мисливець тараном виштовхує підозрілого в ополонку. Правила льоду — у
/// <see cref="SkateCore"/>, тут фази, очки, дії, вид і кадр (spec: docs/games/specs/skate.md).
/// </summary>
public sealed class Skate : Game
{
    public const string PhaseLobby = "lobby", PhaseStart = "start", PhaseGo = "go", PhaseReveal = "reveal", PhaseOver = "over";
    public const int Seats = 8;
    public const int TickMs = 40;
    /// <summary>«Роздивись» — 3 с: кататись можна всім, ласощі ще не підбирають.</summary>
    public const int StartTicks = 75;
    /// <summary>Раунд — 90 с.</summary>
    public const int RoundTicks = 2250;
    /// <summary>Розкриття — 6 с, усі завмерли, над гравцями ніки.</summary>
    public const int RevealTicks = 150;
    public const int PtItem = 1, PtKill = 2, PtRound = 3;
    /// <summary>У розкритті кадр летить раз на стільки тиків (усі завмерли — частіше нема чого).</summary>
    public const int RevealFrameEvery = 5;
    /// <summary>Затиснуту стрілку модуль підтверджує раз на секунду; не чули 3 с — зв'язок обірвався, відпускаємо самі.</summary>
    public const int MoveHoldTicks = 75;
    /// <summary>Слід на розкритті: точка раз на 12 тиків, 42 точки — останні ≈ 20 с.</summary>
    public const int TrailEvery = 12, TrailLen = 42;
    /// <summary>
    /// Лід тріщить під кінець: нова тріщина, коли до кінця лишається стільки тиків (≈ 42, 34, 26, 18 і 10 с); за 5 с
    /// вона стає ополонкою. Останні 30 с (<see cref="GrowFrom"/>) відкриті ополонки ширшають на одиницю щодві секунди.
    /// </summary>
    public static readonly int[] CrackAt = [1050, 850, 650, 450, 250];
    public const int GrowFrom = 750, GrowEvery = 50;

    /// <summary>«Як на ковзанці»: на двох — 24 боти, далі більше, на вісьмох — 40.</summary>
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
        "skate", "Ковзанка", "ковзанку", GameGroup.Live, 2, Seats, TickMs: TickMs,
        Start: StartMode.ByHost, Hidden: true, Score: ScoreOrder.HigherIsBetter,
        Options:
        [
            new GameOption("rounds", "Раундів", [("3", "3 раунди"), ("1", "1 раунд"), ("5", "5 раундів")], "3"),
            new GameOption("crowd", "На льоду", [("auto", "Як на свято"), ("small", "Рідко (20)"), ("big", "Тиснява (48)")], "auto"),
        ],
        Hint: "Замерзлий ставок, пів села на ковзанах — і десь серед них твої друзі. Збери свої ласощі або зіпхни підозрілого в ополонку. Ніхто не знає, хто з юрми живий");

    static readonly string[] SeatNames = ["жовтий", "зелений", "рудий", "сірий", "синій", "рожевий", "фіолетовий", "червоний"];

    readonly SkateSeat[] _s = [.. Enumerable.Range(0, Seats).Select(_ => new SkateSeat())];
    SkateCore? _core;
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
    /// <summary>Події цього тика — рівно ті, що в кадрі.</summary>
    readonly List<int[]> _ev = [];
    int[][] _evFrame = [];
    SkateReveal? _reveal;
    int[]? _winners;

    sealed record SkateReveal(int[] Winners, string Why, (int Seat, int Id)[] Ids, SkateRow[] Rows, (int Seat, int[] Pts)[] Trails);
    sealed record SkateRow(int Seat, int Got, int Kills, bool Win, int Pts);

    SkateCore Core => _core ??= new SkateCore(Ctx.Rng);

    /// <summary>Для тестів: ядро й місця напряму.</summary>
    public SkateCore CoreForTests => Core;
    public SkateSeat SeatForTests(int seat) => _s[seat];
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
        Core.Trading = false;
        foreach (var v in Core.V)
            if (v.Owner >= 0) _s[v.Owner].Me = v.Id;
        var rng = Ctx.Rng;
        foreach (var seat in owners)
        {
            var s = _s[seat];
            s.Alive = true;
            s.Items = s.Kills = 0;
            s.ShownTotal = s.Total;
            s.MoveAt = _clock;
            s.TrailHead = s.TrailCount = 0;
            s.CompletedAt = -1;
            DealList(rng, s);
        }
        for (var i = 0; i < Seats; i++)
            if (!_s[i].Active) _s[i].Me = -1;
        _dirty = true;
    }

    /// <summary>Список: чотири різні види з шести, у випадковому порядку (частковий Фішер–Єйтс).</summary>
    static void DealList(Random rng, SkateSeat s)
    {
        Span<int> kinds = stackalloc int[SkateMap.Kinds.Length];
        for (var i = 0; i < kinds.Length; i++) kinds[i] = i;
        for (var i = 0; i < 4; i++)
        {
            var j = rng.Next(i, kinds.Length);
            (kinds[i], kinds[j]) = (kinds[j], kinds[i]);
            s.List[i] = kinds[i];
        }
        Array.Clear(s.Got);
    }

    // ---------------------------------------------------------------------------------------------
    // Дії
    // ---------------------------------------------------------------------------------------------

    /// <summary>Єдина дія гри — <c>move</c>: що тримаєш (−1 нічого, 0…7 напрямок, 8 гальмо). Решта — фізика.</summary>
    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (!_started) return ActResult.Fail("Партія ще не почалась");
        if (seat < 0 || seat >= Seats || !_s[seat].Active) return ActResult.Fail("Тут так не катаються");
        if (action != "move") return ActResult.Fail("Тут так не катаються");
        var dir = Dir(payload);
        if (dir is null or < -1 or > 8) return ActResult.Fail("Такого напрямку нема");
        if (_phase == PhaseOver) return ActResult.Fail("Раунд скінчився");
        var s = _s[seat];
        // Мокрому й у розкритті — приймаємо й мовчки не застосовуємо: відповідь нічого не мусить викривати.
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
                Ice(false);
                if (--_left <= 0)
                {
                    _phase = PhaseGo;
                    _left = RoundTicks;
                    Core.Trading = true;
                    _dirty = true;
                }
                return Flush(true);
            case PhaseGo:
                _ev.Clear();
                _t++;
                _clock++;
                Ice(_left <= GrowFrom && _left % GrowEvery == 0);
                // нова тріщина — після кроку льоду: її 125 тиків попередження йдуть від наступного тика
                if (Array.IndexOf(CrackAt, _left) >= 0)
                {
                    Core.Crack();
                    Drain();
                }
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

    /// <summary>
    /// Один тик льоду, однаковий у «роздивись» і в грі: годинники, мозок ботів, крок усіх (одна фізика), зіткнення,
    /// ополонки, ласощі, тріщини. Події ядра — у кадр і в очки.
    /// </summary>
    void Ice(bool grow)
    {
        HeldKeys();
        var core = Core;
        core.TimersAll();
        core.ThinkAll();
        core.StepAll();
        core.CollideAll();
        core.WaterAll();
        core.ItemsAll();
        core.IceAll(grow);
        Drain();
        Trail();
    }

    /// <summary>Події ядра — в очки місць і в кадр цього тика.</summary>
    void Drain()
    {
        var core = Core;
        foreach (var e in core.Ev) Absorb(e);
        _ev.AddRange(core.Ev);
        core.Ev.Clear();
    }

    /// <summary>Що означає подія ядра для місць: шубовснув гравець — вибув і, можливо, хтось його зіпхнув; підхопили ласощі — чиї.</summary>
    void Absorb(int[] e)
    {
        switch (e[0])
        {
            case SkateCore.EvSplash:
            {
                // Вид летить на кожне «шубовсть», хоч чиє: інакше «вид прийшов» казав би «це був гравець».
                _dirty = true;
                var v = Core.V[e[1]];
                if (v.Owner < 0) return;
                var s = _s[v.Owner];
                if (!s.Active || !s.Alive) return;
                s.Alive = false;
                if (v.LastBy >= 0 && Core.Clock - v.LastAt <= SkateCore.CreditTicks)
                {
                    var by = Core.V[v.LastBy].Owner;
                    if (by >= 0 && by != v.Owner && _s[by].Active)
                    {
                        _s[by].Kills++;
                        _s[by].Total += PtKill;
                    }
                }
                return;
            }
            case SkateCore.EvPick:
            {
                // Так само вид летить на кожне «підхопив»: бот чи гравець, зі списку чи ні — зовні однаково.
                _dirty = true;
                var v = Core.V[e[1]];
                if (v.Owner < 0) return;
                var s = _s[v.Owner];
                if (!s.Active || !s.Alive) return;
                for (var i = 0; i < 4; i++)
                {
                    if (s.List[i] != e[3] || s.Got[i]) continue;
                    s.Got[i] = true;
                    s.Items++;
                    s.Total += PtItem;
                    if (s.Complete) s.CompletedAt = _t;
                    break;
                }
                return;
            }
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

    /// <summary>Раз на <see cref="TrailEvery"/> тиків — точка в слід кожного гравця на льоду (для розкриття).</summary>
    void Trail()
    {
        if (_t % TrailEvery != 0) return;
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Active || !s.Alive || s.Me < 0) continue;
            var v = Core.V[s.Me];
            s.TrailX[s.TrailHead] = v.X / SkateCore.Fp;
            s.TrailY[s.TrailHead] = v.Y / SkateCore.Fp;
            s.TrailHead = (s.TrailHead + 1) % TrailLen;
            if (s.TrailCount < TrailLen) s.TrailCount++;
        }
    }

    /// <summary>Слід місця від найстарішої точки до найновішої: x, y, x, y…</summary>
    static int[] TrailOf(SkateSeat s)
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

    /// <summary>Кінець раунду: хтось зібрав список цього тика → на льоду ≤ 1 гравця → вийшов час.</summary>
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
            if (s.Items > best) { best = s.Items; count = 1; who = i; }
            else if (s.Items == best) count++;
        }
        if (count == 1) EndRound([who], "time");
        else EndRound([], "none");
    }

    void EndRound(int[] winners, string why)
    {
        foreach (var w in winners)
        {
            _s[w].Total += PtRound;
            if (why == "list" && _s[w].Me >= 0 && Core.V[_s[w].Me].Falls == 0) Ctx.Award(w, 0, "ach:skate-clean");
        }
        for (var i = 0; i < Seats; i++)
            if (_s[i].Active && _s[i].Kills > 0) Ctx.Award(i, 0, "ach:skate-ram");
        _reveal = RevealOf(winners, why, s => s.Active);
        Freeze();
        _phase = PhaseReveal;
        _left = RevealTicks;
        _dirty = true;
    }

    /// <summary>Розкриття й кінець: усі завмирають там, де стояли (ніхто не доїжджає в ополонку після свистка).</summary>
    void Freeze()
    {
        foreach (var v in Core.V)
        {
            v.Want = -1;
            v.Vx = v.Vy = 0;
            v.Pushing = v.Braking = false;
        }
    }

    /// <summary>Розкриття: хто ким був, рядки раунду й сліди — для місць, що пройшли фільтр.</summary>
    SkateReveal RevealOf(int[] winners, string why, Func<SkateSeat, bool> who)
    {
        var ids = new List<(int, int)>();
        var rows = new List<SkateRow>();
        var trails = new List<(int, int[])>();
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Plays || s.Me < 0 || !who(s)) continue;
            // «усі розійшлись» — не виграний раунд: +3 не платимо, тож і в рядку його нема
            var win = why != "left" && Array.IndexOf(winners, i) >= 0;
            ids.Add((i, s.Me));
            rows.Add(new SkateRow(i, s.Items, s.Kills, win, s.Items * PtItem + s.Kills * PtKill + (win ? PtRound : 0)));
            trails.Add((i, TrailOf(s)));
        }
        return new SkateReveal(winners, why, [.. ids], [.. rows], [.. trails]);
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
    /// Хтось устав: його селянин лишається на льоду й стає ботом — вихід нікого не викриває. Лишився один — партія
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
            SkateCore.Forget(v);
        }
        _dirty = true;

        var rest = new List<int>();
        for (var i = 0; i < Seats; i++)
            if (i != seat && _s[i].Active && Ctx.Seated(i)) rest.Add(i);
        if (rest.Count > 1) return;
        // Партія скінчилась посеред раунду: лід завмирає, а всім показуємо, хто ким був, — і того, хто пішов. Якщо
        // раунд уже розкрито, лишаємо його підсумок.
        if (!(_phase == PhaseReveal && _reveal is not null)) _reveal = RevealOf([.. rest], "left", x => x.Me >= 0);
        Freeze();
        _phase = PhaseOver;
        _left = 0;
        _endWhy = "left";
        _winners = [.. rest];
        foreach (var i in rest) Ctx.Score(i, _s[i].Total);
        Ctx.Finish([.. rest], rest.Count == 1
            ? $"{Info.Title}: усі розійшлись — {_s[rest[0]].Nick} катається на ставку сам-на-сам із селом"
            : $"{Info.Title}: усі розійшлись");
    }

    // ---------------------------------------------------------------------------------------------
    // Кадр і вид
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Кадр: тик, фаза, скільки лишилось, 4 числа на селянина, ополонки, ласощі й події цього тика. Нічого про місця
    /// — ні owner, ні me, ні списків: бота від гравця за цими числами не відрізнити.
    /// </summary>
    public override object? Frame() => new
    {
        t = _t,
        ph = _phase,
        left = _left,
        v = _started ? Core.Pack() : Preview.Value.V,
        h = _started ? Core.PackHoles() : Preview.Value.H,
        it = _started ? Core.PackItems() : Preview.Value.It,
        ev = _evFrame,
    };

    /// <summary>Статичний ставок для лобі: юрма стоїть, поки господар не натисне «Почати».</summary>
    static readonly Lazy<(int[] V, int[] H, int[] It, int[] Looks, string[] Names)> Preview = new(() =>
    {
        var core = new SkateCore(new Random(2709));
        core.Deal([], 24);
        return (core.Pack(), core.PackHoles(), core.PackItems(), core.Looks(), core.NamesNow());
    });

    static readonly object[] Kinds =
    [
        .. SkateMap.Kinds.Select((k, i) => (object)new { k = i, name = k.Name, what = k.What, emoji = k.Emoji }),
    ];

    public override object View(int? seat)
    {
        var live = _started;
        var me = seat is { } k && k >= 0 && k < Seats && _s[k].Active && live && _s[k].Me >= 0 ? MeOf(k) : null;
        // Ласощі й очки раунду — лише на розкритті: посеред раунду «+1» чи «+2» комусь назвав би того, хто щойно
        // нахилився над бубликом чи зіпхнув. Поки раунд іде, видно очки на його початок і «got: null».
        var open = _phase is PhaseReveal or PhaseOver;
        var seats = new List<object>();
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (live && s.Plays)
                seats.Add(new { seat = i, nick = s.Nick, alive = s.Alive, @out = s.Out, got = open ? s.Items : (int?)null, total = open ? s.Total : s.ShownTotal });
            else if (!live && Ctx.Seated(i))
                seats.Add(new { seat = i, nick = Ctx.NickOf(i) ?? "", alive = true, @out = false, got = (int?)null, total = 0 });
        }
        var dead = new List<object>();
        if (live)
            for (var i = 0; i < Seats; i++)
                if (_s[i].Plays && !_s[i].Alive && _s[i].Me >= 0)
                    dead.Add(new { seat = i, id = _s[i].Me });

        return new
        {
            phase = _phase,
            round = _round,
            of = _rounds,
            left = _left,
            t = _t,
            width = SkateMap.W,
            height = SkateMap.H,
            cell = SkateMap.Cell,
            n = live ? Core.N : Preview.Value.V.Length / 4,
            map = SkateMap.Rows,
            kinds = Kinds,
            looks = live ? Core.Looks() : Preview.Value.Looks,
            names = live ? Core.NamesNow() : Preview.Value.Names,
            v = live ? Core.Pack() : Preview.Value.V,
            h = live ? Core.PackHoles() : Preview.Value.H,
            it = live ? Core.PackItems() : Preview.Value.It,
            seats,
            dead,
            me,
            reveal = _reveal is { } r && (_phase is PhaseReveal or PhaseOver)
                ? new
                {
                    winners = r.Winners,
                    why = r.Why,
                    ids = r.Ids.Select(p => new { seat = p.Seat, id = p.Id }).ToArray(),
                    rows = r.Rows.Select(x => new { seat = x.Seat, got = x.Got, kills = x.Kills, win = x.Win, pts = x.Pts }).ToArray(),
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
        return new
        {
            id = s.Me,
            list = (int[])s.List.Clone(),
            got = (bool[])s.Got.Clone(),
            alive = s.Alive,
        };
    }
}
