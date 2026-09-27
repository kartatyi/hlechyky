using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Стан місця за партію й раунд. «Хто я» і скільки тримав горщик — таємниця місця до розкриття, решта — публічна.</summary>
public sealed class PotatoSeat
{
    /// <summary>Сидів на старті партії.</summary>
    public bool Plays;
    /// <summary>Устав посеред партії: його селянин — уже бот, а очки лишаються в таблиці.</summary>
    public bool Out;
    public string Nick = "";
    public bool Alive;
    /// <summary>Id свого селянина в цьому раунді.</summary>
    public int Me = -1;
    /// <summary>Скільки тиків цього раунду горщик був у руках — таємниця до розкриття (з нього — переможець на час).</summary>
    public int Held;
    /// <summary>Скільки разів горщик опинявся в руках (передали чи з'явився).</summary>
    public int Catches;
    /// <summary>Скільки разів горщик, що його підкинув цей гравець, рвонув у руках іншого гравця.</summary>
    public int Burns;
    /// <summary>Скільки ляпасів влучило в гравців.</summary>
    public int Slaps;
    public int Total;
    /// <summary>Очки на початок раунду — їх і видно всім, поки раунд іде (інакше «+2» назвав би, хто підкинув).</summary>
    public int ShownTotal;
    /// <summary>Тик (наскрізний) останнього <c>move</c>: затиснута стрілка без підтвердження гасне.</summary>
    public int MoveAt;
    public readonly int[] TrailX = new int[Potato.TrailLen], TrailY = new int[Potato.TrailLen];
    public int TrailHead, TrailCount;

    public bool Active => Plays && !Out;
}

/// <summary>
/// «Гарячий горщик»: Unspottable-«бомба» на сільській толоці. Серед юрми ходить горщик із жаром; хто його несе,
/// видно всім, а скільки йому до вибуху — ні, лише дим густішає. Гравці — такі самі селяни, як боти: у кадрі їх не
/// відрізнити нічим. Передай горщик упритул, ляпни підозрілого — гравець оглухне, а ти, влучивши в бота, отетерієш.
/// Правила поля — у <see cref="PotatoCore"/>, тут фази, очки, дії, вид і кадр (spec: docs/games/specs/potato.md).
/// </summary>
public sealed class Potato : Game
{
    public const string PhaseLobby = "lobby", PhaseStart = "start", PhaseGo = "go", PhaseReveal = "reveal", PhaseOver = "over";
    public const int Seats = 8;
    public const int TickMs = 40;
    /// <summary>«Роздивись» — 3 с: ходити можна, передавати й ляскати — нічого (горщиків ще нема).</summary>
    public const int StartTicks = 75;
    /// <summary>Раунд — 90 с: ≈ 4 вибухи на горщик.</summary>
    public const int RoundTicks = 2250;
    public const int RevealTicks = 150;
    /// <summary>
    /// Очки раунду: +3 переможцю, +1 кожному, хто на ногах, +2 за горщик, що рвонув у руках гравця, якому ти його
    /// підкинув, +1 за ляпас, що влучив у гравця.
    /// </summary>
    public const int PtRound = 3, PtAlive = 1, PtBurn = 2, PtSlap = 1;
    public const int RevealFrameEvery = 5;
    /// <summary>Затиснуту стрілку модуль підтверджує раз на секунду; не чули 3 с — зв'язок обірвався, селянин зупиняється.</summary>
    public const int MoveHoldTicks = 75;
    public const int TrailEvery = 12, TrailLen = 42;
    /// <summary>«Холодні руки»: виграв раунд, хоч горщик побував у тебе стільки разів.</summary>
    public const int CoolCatches = 3;

    /// <summary>«Як на толоці»: на двох — 14 ботів, далі більше, на сімох-вісьмох — 22. Тісніше за «Юрму»: горщик має до когось доходити.</summary>
    public static int BotsFor(int players) => players switch
    {
        <= 2 => 14,
        3 => 15,
        4 => 16,
        5 => 18,
        6 => 20,
        _ => 22,
    };

    /// <summary>Горщиків «як вийде»: до чотирьох гравців — один, від п'яти — два.</summary>
    public static int PotsFor(int players) => players >= 5 ? 2 : 1;

    public override GameInfo Info { get; } = new(
        "potato", "Гарячий горщик", "гарячий горщик", GameGroup.Live, 2, Seats, TickMs: TickMs,
        Start: StartMode.ByHost, Hidden: true, Score: ScoreOrder.HigherIsBetter,
        Options:
        [
            new GameOption("rounds", "Раундів", [("3", "3 раунди"), ("1", "1 раунд"), ("5", "5 раундів")], "3"),
            new GameOption("crowd", "Люду", [("auto", "Як на толоці"), ("small", "Рідко (12)"), ("big", "Тісно (30)")], "auto"),
            new GameOption("pots", "Горщиків", [("auto", "Як вийде"), ("1", "Один"), ("2", "Два")], "auto"),
        ],
        Hint: "Толокою ходить горщик із жаром — і от-от рвоне. Передай його впритул, ляпни підозрілого, не тримай довго. Ніхто не знає, хто з селян живий");

    static readonly string[] SeatNames = ["жовтий", "зелений", "рудий", "сірий", "синій", "рожевий", "фіолетовий", "червоний"];

    readonly PotatoSeat[] _s = [.. Enumerable.Range(0, Seats).Select(_ => new PotatoSeat())];
    PotatoCore? _core;
    int _rounds = 3;
    string _crowd = "auto";
    string _potsOpt = "auto";
    int _pots = 1;
    bool _started;
    string _phase = PhaseLobby;
    int _round;
    int _left;
    int _t;
    int _clock;
    int _n;
    bool _dirty;
    string _endWhy = "end";
    readonly List<int[]> _pending = [];
    readonly List<int[]> _ev = [];
    int[][] _evFrame = [];
    PotatoReveal? _reveal;
    int[]? _winners;
    /// <summary>Ачівки, зароблені цього раунду: видаємо лише на розкритті — сповіщення посеред раунду назвало б гравця.</summary>
    readonly HashSet<(int Seat, string Key)> _awards = [];

    sealed record PotatoReveal(int[] Winners, string Why, (int Seat, int Id)[] Ids, PotatoRow[] Rows, (int Seat, int[] Pts)[] Trails);
    sealed record PotatoRow(int Seat, int Held, int Catches, int Burns, int Slaps, bool Alive, bool Win, int Pts);

    PotatoCore Core => _core ??= new PotatoCore(Ctx.Rng);

    /// <summary>Для тестів: ядро й місця напряму.</summary>
    public PotatoCore CoreForTests => Core;
    public PotatoSeat SeatForTests(int seat) => _s[seat];
    public string Phase => _phase;
    public int Left => _left;
    public int RoundNo => _round;
    public int PotCount => _pots;

    public override string SeatName(int seat) => seat >= 0 && seat < Seats ? SeatNames[seat] : base.SeatName(seat);

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _rounds = options.TryGetValue("rounds", out var r) && int.TryParse(r, out var n) && n is 1 or 3 or 5 ? n : 3;
        _crowd = options.TryGetValue("crowd", out var c) && c is "small" or "big" ? c : "auto";
        _potsOpt = options.TryGetValue("pots", out var p) && p is "1" or "2" ? p : "auto";
    }

    public int BotsForTable(int players) => _crowd switch
    {
        "small" => 12,
        "big" => 30,
        _ => BotsFor(players),
    };

    public int PotsForTable(int players) => _potsOpt switch
    {
        "1" => 1,
        "2" => 2,
        _ => PotsFor(players),
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
        _pots = PotsForTable(players);
        NewRound();
    }

    void NewRound()
    {
        _round++;
        _phase = PhaseStart;
        _left = StartTicks;
        _t = 0;
        _reveal = null;
        _awards.Clear();
        var owners = new List<int>(Seats);
        for (var i = 0; i < Seats; i++)
            if (_s[i].Active) owners.Add(i);
        Core.Deal(owners, Math.Max(0, _n - owners.Count));
        foreach (var v in Core.V)
            if (v.Owner >= 0) _s[v.Owner].Me = v.Id;
        foreach (var seat in owners)
        {
            var s = _s[seat];
            s.Alive = true;
            s.Held = s.Catches = s.Burns = s.Slaps = 0;
            s.ShownTotal = s.Total;
            s.MoveAt = _clock;
            s.TrailHead = s.TrailCount = 0;
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
            "pass" => Pass(seat, payload),
            "slap" => Slap(seat, payload),
            _ => ActResult.Fail("Тут так не ходять"),
        };
    }

    ActResult Move(int seat, JsonElement payload)
    {
        var dir = Dir(payload);
        if (dir is null or < -1 or > 3) return ActResult.Fail("Такого напрямку нема");
        if (_phase == PhaseOver) return ActResult.Fail("Раунд скінчився");
        var s = _s[seat];
        // Вибулому, оглушеному й у розкритті — приймаємо й мовчки не застосовуємо (оглушений лише запише, куди тягне).
        if (!s.Alive || _phase == PhaseReveal || s.Me < 0) return ActResult.Done;
        Core.V[s.Me].Want = dir.Value;
        s.MoveAt = _clock;
        return ActResult.Done;
    }

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

    ActResult? PhaseRefusal(PotatoSeat s)
    {
        if (_phase == PhaseStart) return ActResult.Fail("Зачекай, толока ще збирається");
        if (_phase != PhaseGo) return ActResult.Fail("Раунд скінчився");
        if (!s.Alive) return ActResult.Fail("Тебе вже рознесло — дивись, хто кого");
        return null;
    }

    ActResult Pass(int seat, JsonElement payload)
    {
        var s = _s[seat];
        if (PhaseRefusal(s) is { } no) return no;
        var id = Field(payload, "id", out var ok);
        if (!ok) return ActResult.Fail("Такого селянина нема");
        var r = Core.TryPass(Core.V[s.Me], id);
        Drain(_pending);
        return r switch
        {
            PotatoNo.None => ActResult.Done,
            PotatoNo.NoPot => ActResult.Fail("Нема в тебе горщика"),
            PotatoNo.Stunned => ActResult.Fail("У тебе ще іскри в очах"),
            PotatoNo.TooSoon => ActResult.Fail("Горщик ще пече руки — мить!"),
            PotatoNo.Nobody => ActResult.Fail("Нікого поруч — підійди впритул"),
            PotatoNo.Far => ActResult.Fail("Далеко — не дотягнешся"),
            PotatoNo.Self => ActResult.Fail("Сам собі не передаси"),
            PotatoNo.Down => ActResult.Fail("Лежачому горщик не тицяють"),
            PotatoNo.HasPot => ActResult.Fail("У того вже є горщик"),
            PotatoNo.Back => ActResult.Fail("Назад одразу не можна — хай хоч потримає"),
            _ => ActResult.Fail("Такого селянина нема"),
        };
    }

    ActResult Slap(int seat, JsonElement payload)
    {
        var s = _s[seat];
        if (PhaseRefusal(s) is { } no) return no;
        var id = Field(payload, "id", out var ok);
        if (!ok) return ActResult.Fail("Такого селянина нема");
        var r = Core.TrySlap(Core.V[s.Me], id);
        Drain(_pending);
        return r switch
        {
            PotatoNo.None => ActResult.Done,
            PotatoNo.Stunned => ActResult.Fail("У тебе ще іскри в очах"),
            PotatoNo.Cool => ActResult.Fail("Рука ще не відійшла"),
            PotatoNo.Nobody => ActResult.Fail("Перед тобою нікого"),
            PotatoNo.Far => ActResult.Fail("Далеко — не дотягнешся"),
            PotatoNo.Self => ActResult.Fail("Себе не ляскають"),
            PotatoNo.Down => ActResult.Fail("Лежачого не б'ють"),
            _ => ActResult.Fail("Такого селянина нема"),
        };
    }

    /// <summary>
    /// Забрати події з ядра: порахувати очки й покласти в кадр. Вид розсилаємо на кожну передачу, ляпас, вибух і
    /// новий горщик — однаково, хоч чиї вони: інакше зайвий вид після «бот передав гравцеві» викрив би гравця.
    /// </summary>
    void Drain(List<int[]> into)
    {
        var log = Core.Log;
        if (log.Count == 0) return;
        foreach (var e in log)
        {
            switch (e.Kind)
            {
                case PotatoEvent.Pass:
                {
                    var to = Core.V[e.B];
                    if (to.Owner >= 0) _s[to.Owner].Catches++;
                    into.Add([1, e.Pot, e.A, e.B]);
                    break;
                }
                case PotatoEvent.Slap:
                {
                    var a = Core.V[e.A];
                    // оглух той, кого ляснули, — значить, то гравець; платимо, якщо ляснув гравець
                    if (e.C == e.B && a.Owner >= 0)
                    {
                        _s[a.Owner].Slaps++;
                        _s[a.Owner].Total += PtSlap;
                    }
                    into.Add([2, e.A, e.B, e.C]);
                    break;
                }
                case PotatoEvent.Boom:
                {
                    var v = Core.V[e.A];
                    if (v.Owner >= 0)
                    {
                        var seat = v.Owner;
                        _s[seat].Alive = false;
                        var giver = e.B;
                        if (giver >= 0 && giver != seat && _s[giver].Active)
                        {
                            _s[giver].Burns++;
                            _s[giver].Total += PtBurn;
                            _awards.Add((giver, "ach:potato-gift"));
                        }
                        into.Add([3, e.Pot, e.A, 1, seat]);
                    }
                    else into.Add([3, e.Pot, e.A, 0, -1]);
                    break;
                }
                case PotatoEvent.Spawn:
                {
                    var v = Core.V[e.A];
                    if (v.Owner >= 0) _s[v.Owner].Catches++;
                    into.Add([4, e.Pot, e.A]);
                    break;
                }
            }
        }
        log.Clear();
        _dirty = true;
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
                HeldKeys();
                Core.TimersAll();
                Core.ThinkAll();
                Core.StepAll();
                Trail();
                if (--_left <= 0)
                {
                    _phase = PhaseGo;
                    _left = RoundTicks;
                    Core.Live = true;
                    Core.StartPots(_pots);
                    Drain(_ev);
                    _dirty = true;
                }
                return Flush(true);
            case PhaseGo:
                OpenEvents();
                _t++;
                _clock++;
                HeldKeys();
                Core.TimersAll();
                Drain(_ev);
                Core.ThinkAll();
                Drain(_ev);
                Core.StepAll();
                Hold();
                Trail();
                _left--;
                EndCheck();
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

    /// <summary>Лічильник «скільки тримав» — таємниця місця до розкриття.</summary>
    void Hold()
    {
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (s.Active && s.Alive && s.Me >= 0 && Core.V[s.Me].Pot >= 0) s.Held++;
        }
    }

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

    static int[] TrailOf(PotatoSeat s)
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
    /// Кінець раунду: на ногах ≤ 1 гравець → він і бере (нікого — без переможця); вийшов час → серед тих, хто на ногах,
    /// бере той, хто найменше тримав горщик (кілька однаково — усі); якщо ж усі живі тримали однаково (скажімо, ніхто
    /// й не торкався) — раунд без переможця: сидіти тихо в кутку й чекати — не перемога.
    /// </summary>
    void EndCheck()
    {
        int alive = 0, active = 0, last = -1;
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Active) continue;
            active++;
            if (!s.Alive) continue;
            alive++;
            last = i;
        }
        if (active >= 2 && alive <= 1) { EndRound(alive == 1 ? [last] : [], alive == 1 ? "last" : "none"); return; }
        if (_left > 0) return;

        int min = int.MaxValue, max = int.MinValue;
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Active || !s.Alive) continue;
            min = Math.Min(min, s.Held);
            max = Math.Max(max, s.Held);
        }
        if (alive >= 2 && min == max) { EndRound([], "none"); return; }
        var winners = new List<int>();
        for (var i = 0; i < Seats; i++)
            if (_s[i].Active && _s[i].Alive && _s[i].Held == min) winners.Add(i);
        EndRound([.. winners], "time");
    }

    void EndRound(int[] winners, string why)
    {
        for (var i = 0; i < Seats; i++)
            if (_s[i].Active && _s[i].Alive) _s[i].Total += PtAlive;
        foreach (var w in winners)
        {
            _s[w].Total += PtRound;
            if (_s[w].Catches >= CoolCatches) _awards.Add((w, "ach:potato-cool"));
        }
        foreach (var (seat, key) in _awards.OrderBy(a => a.Seat).ThenBy(a => a.Key, StringComparer.Ordinal))
            if (_s[seat].Active) Ctx.Award(seat, 0, key);
        _awards.Clear();
        _reveal = RevealOf(winners, why, s => s.Active);
        Freeze();
        _phase = PhaseReveal;
        _left = RevealTicks;
        _dirty = true;
    }

    /// <summary>Усі завмерли, горщики зникли.</summary>
    void Freeze()
    {
        foreach (var v in Core.V)
        {
            v.Want = -1;
            v.Moving = false;
        }
        Core.Live = false;
        Core.ClearPots();
        Core.Log.Clear();
    }

    PotatoReveal RevealOf(int[] winners, string why, Func<PotatoSeat, bool> who)
    {
        var ids = new List<(int, int)>();
        var rows = new List<PotatoRow>();
        var trails = new List<(int, int[])>();
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Plays || s.Me < 0 || !who(s)) continue;
            var win = why != "left" && Array.IndexOf(winners, i) >= 0;
            var alive = why != "left" && s.Alive && s.Active;
            ids.Add((i, s.Me));
            rows.Add(new PotatoRow(i, s.Held, s.Catches, s.Burns, s.Slaps, s.Alive,
                win, (alive ? PtAlive : 0) + (win ? PtRound : 0) + s.Burns * PtBurn + s.Slaps * PtSlap));
            trails.Add((i, TrailOf(s)));
        }
        return new PotatoReveal(winners, why, [.. ids], [.. rows], [.. trails]);
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
    /// Хтось устав: його селянин лишається на толоці й стає ботом (з горщиком — теж: шукатиме, кому віддати) — вихід
    /// нікого не викриває. Лишився один — партія його; нікого — нічия.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (!_started || seat < 0 || seat >= Seats || _phase == PhaseOver) return;
        var s = _s[seat];
        if (!s.Active) return;
        s.Out = true;
        if (s.Me >= 0 && s.Me < Core.N) Core.BecomeBot(Core.V[s.Me]);
        _dirty = true;

        var rest = new List<int>();
        for (var i = 0; i < Seats; i++)
            if (i != seat && _s[i].Active && Ctx.Seated(i)) rest.Add(i);
        if (rest.Count > 1) return;
        if (!(_phase == PhaseReveal && _reveal is not null)) _reveal = RevealOf([.. rest], "left", x => x.Me >= 0);
        Freeze();
        _phase = PhaseOver;
        _left = 0;
        _endWhy = "left";
        _winners = [.. rest];
        foreach (var i in rest) Ctx.Score(i, _s[i].Total);
        Ctx.Finish([.. rest], rest.Count == 1
            ? $"{Info.Title}: усі розійшлись — {_s[rest[0]].Nick} лишається на толоці сам-на-сам із горщиком"
            : $"{Info.Title}: усі розійшлись");
    }

    // ---------------------------------------------------------------------------------------------
    // Кадр і вид
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Кадр: тик, фаза, скільки лишилось, 4 числа на селянина, горщики (у кого, як димить) і події тика. Нічого про
    /// місця — ні owner, ні me, ні «скільки тримав»: бота від гравця за цими числами не відрізнити.
    /// </summary>
    public override object? Frame() => new
    {
        t = _t,
        ph = _phase,
        left = _left,
        v = _started ? Core.Pack() : Preview.Value.V,
        p = _started ? Core.PackPots() : [],
        ev = _evFrame,
    };

    static readonly Lazy<(int[] V, int[] Looks, string[] Names)> Preview = new(() =>
    {
        var core = new PotatoCore(new Random(2709));
        core.Deal([], 16);
        return (core.Pack(), core.Looks(), core.NamesNow());
    });

    public override object View(int? seat)
    {
        var live = _started;
        var me = seat is { } k && k >= 0 && k < Seats && _s[k].Active && live && _s[k].Me >= 0 ? MeOf(k) : null;
        // Очки й «скільки тримав» — лише на розкритті: посеред раунду «+2» чи «+1» комусь назвав би того, хто підкинув
        // чи ляснув.
        var open = _phase is PhaseReveal or PhaseOver;
        var seats = new List<object>();
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (live && s.Plays)
                seats.Add(new { seat = i, nick = s.Nick, alive = s.Alive, @out = s.Out, held = open ? s.Held : (int?)null, total = open ? s.Total : s.ShownTotal });
            else if (!live && Ctx.Seated(i))
                seats.Add(new { seat = i, nick = Ctx.NickOf(i) ?? "", alive = true, @out = false, held = (int?)null, total = 0 });
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
            width = PotatoMap.W,
            height = PotatoMap.H,
            cell = PotatoMap.Cell,
            n = live ? Core.N : Preview.Value.V.Length / 4,
            pots = live ? _pots : 0,
            map = PotatoMap.Rows,
            looks = live ? Core.Looks() : Preview.Value.Looks,
            names = live ? Core.NamesNow() : Preview.Value.Names,
            v = live ? Core.Pack() : Preview.Value.V,
            p = live ? Core.PackPots() : [],
            seats,
            dead,
            me,
            reveal = _reveal is { } r && (_phase is PhaseReveal or PhaseOver)
                ? new
                {
                    winners = r.Winners,
                    why = r.Why,
                    ids = r.Ids.Select(p => new { seat = p.Seat, id = p.Id }).ToArray(),
                    rows = r.Rows.Select(x => new { seat = x.Seat, held = x.Held, catches = x.Catches, burns = x.Burns, slaps = x.Slaps, alive = x.Alive, win = x.Win, pts = x.Pts }).ToArray(),
                    trails = r.Trails.Select(p => new { seat = p.Seat, pts = p.Pts }).ToArray(),
                }
                : null,
            result = _phase == PhaseOver && _winners is { } w
                ? new { winners = w, totals = _s.Select(x => x.Total).ToArray(), why = _endWhy }
                : null,
            turn = (int?)null,
        };
    }

    /// <summary>Своє місце: хто я, чи на ногах, оглушення, рука, скільки тримав (своє — не таємниця від себе), коли можна передати.</summary>
    object MeOf(int seat)
    {
        var s = _s[seat];
        var v = Core.V[s.Me];
        var passIn = 0;
        if (v.Pot >= 0 && v.Pot < Core.Pots.Length)
            passIn = Math.Max(0, PotatoCore.HoldMin - (Core.Now - Core.Pots[v.Pot].Since));
        return new
        {
            id = s.Me,
            alive = s.Alive,
            stun = v.Stun,
            slapCool = v.SlapCool,
            held = s.Held,
            passIn,
        };
    }
}
