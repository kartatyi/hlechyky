using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Стан місця за партію й раунд. «Хто я», серія в колі й лічильники фігур — таємниця місця до розкриття.</summary>
public sealed class DanceSeat
{
    /// <summary>Сидів на старті партії.</summary>
    public bool Plays;
    /// <summary>Гравець-бот (🤖): місце порожнє в каркасі, танцюристом керує сервер (<c>Dance.Bot.cs</c>).</summary>
    public bool Bot;
    /// <summary>Устав посеред партії: його танцюрист — уже бот, а очки лишаються в таблиці.</summary>
    public bool Out;
    public string Nick = "";
    public bool Alive;
    /// <summary>Id свого танцюриста в цьому раунді.</summary>
    public int Me = -1;
    /// <summary>Скільки фігур поспіль вціляв у колі (на <see cref="Dance.RibbonStreak"/> — стрічка).</summary>
    public int Streak, Best;
    /// <summary>Фігур раунду: вціляв, схибив, вціляв у колі.</summary>
    public int Good, Bad, Circle;
    public int Kills, Slaps, SlapCool;
    public int Total;
    /// <summary>Очки на початок раунду — їх і видно всім, поки раунд іде (інакше «+2» назвав би того, хто вдарив).</summary>
    public int ShownTotal;
    /// <summary>Чим скінчилась остання фігура для мене: "ring" — у такт і в колі, "ok" — у такт поза колом, "miss".</summary>
    public string? Last;
    /// <summary>Тик (наскрізний) останнього <c>move</c>: затиснута стрілка без підтвердження гасне.</summary>
    public int MoveAt;
    public readonly int[] TrailX = new int[Dance.TrailLen], TrailY = new int[Dance.TrailLen];
    public int TrailHead, TrailCount;
    /// <summary>Перший ляпас раунду вивів гравця — на ачівку «Ляпас у яблучко».</summary>
    public bool Eye;
    /// <summary>Тик раунду, коли витанцював стрічку; -1 — ще ні.</summary>
    public int RibbonAt = -1;

    public bool Active => Plays && !Out;
}

/// <summary>
/// «Вечорниці»: Unspottable на сільських танцях. На подвір'ї гуляє й пританцьовує юрма ботів, і гравці — такі самі
/// танцюристи: у кадрі їх не відрізнити нічим (той самий крок, та сама фігура, id тасуються щораунду). Раз на 4–8 с
/// музики вигукують фігуру, і на такт її мусять станцювати всі — хто схибив, видно всім. Витанцюй стрічку в колі або
/// вибий суперників ляпасом. Правила поля — у <see cref="DanceCore"/>, тут фази, виклики, очки, дії, вид і кадр
/// (spec: docs/games/specs/dance.md).
/// </summary>
public sealed partial class Dance : Game
{
    public const string PhaseLobby = "lobby", PhaseStart = "start", PhaseGo = "go", PhaseReveal = "reveal", PhaseOver = "over";
    public const int Seats = 8;
    public const int TickMs = 40;
    /// <summary>«Роздивись» — 3 с: ходити можна, танцювати й бити — ні (музики настроюються).</summary>
    public const int StartTicks = 75;
    /// <summary>Раунд — 120 с.</summary>
    public const int RoundTicks = 3000;
    /// <summary>Розкриття — 6 с: усі завмерли, над гравцями ніки.</summary>
    public const int RevealTicks = 150;
    /// <summary>Ляпас — не частіше раз на 2 с.</summary>
    public const int SlapCoolTicks = 50;
    /// <summary>Ляснув бота — сам «отетерів» на 1,5 с: ні кроку, ні фігури, і всі бачать, хто це.</summary>
    public const int StunTicks = 38;
    /// <summary>Стрічка — за стільки фігур поспіль, вціляних у колі.</summary>
    public const int RibbonStreak = 6;
    public const int PtCircle = 1, PtKill = 2, PtRound = 3;
    public const int RevealFrameEvery = 5;
    public const int MoveHoldTicks = 75;
    public const int TrailEvery = 12, TrailLen = 42;

    /// <summary>
    /// Темп музик: такт (пульс) у тиках — 12 (125 уд/хв), 10 (150), 8 (≈190). Виклик лунає за три пульси до такту
    /// (1,44 / 1,2 / ≈1 с), а між тактами — ціле число пульсів: 13–16, 13–17, 13–15 (≈6–8 с, 5–7 с, 4–5 с).
    /// Під кінець раунду музика пришвидшується: останні 72 с — темп 1, останні 36 с — темп 2.
    /// </summary>
    public static readonly int[] Pulse = [12, 10, 8];
    static readonly int[] GapMin = [13, 13, 13], GapMax = [16, 17, 15];
    public const int LeadPulses = 3;
    public const int Tempo1Left = 1800, Tempo2Left = 900;

    public static int TempoAt(int left) => left > Tempo1Left ? 0 : left > Tempo2Left ? 1 : 2;

    /// <summary>«Як на вечорницях»: на двох — 24 боти, далі більше, на вісьмох — 40.</summary>
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
        "dance", "Вечорниці", "вечорниці", GameGroup.Live, 1, Seats, TickMs: TickMs,
        Start: StartMode.ByHost, Hidden: true, Score: ScoreOrder.HigherIsBetter,
        Options:
        [
            new GameOption("rounds", "Раундів", [("3", "3 раунди"), ("1", "1 раунд"), ("5", "5 раундів")], "3"),
            new GameOption("crowd", "Люду", [("auto", "Як на вечорницях"), ("small", "Небагато (20)"), ("big", "Повна хата (48)")], "auto"),
            LiveBots.LevelOption,
        ],
        Hint: "Музики кличуть фігури — плескай, присідай, крутись. Ти один із танцюристів, і ніхто не знає, хто живий. Витанцюй стрічку в колі або вибий суперників ляпасом. Самому — з 🤖 ботами");

    static readonly string[] SeatNames = ["жовтий", "зелений", "рудий", "сірий", "синій", "рожевий", "фіолетовий", "червоний"];

    readonly DanceSeat[] _s = [.. Enumerable.Range(0, Seats).Select(_ => new DanceSeat())];
    DanceCore? _core;
    int _rounds = 3;
    string _crowd = "auto";
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
    DanceReveal? _reveal;
    int[]? _winners;

    // ---- виклики музик ----
    /// <summary>Фігура, що зараз лунає (-1 — ніяка), тик її такту (між викликами — такт останнього), темп.</summary>
    int _callFig = -1, _beat, _lead, _tempo;
    bool _calling, _judged;
    /// <summary>Тик, коли скінчився останній виклик: хто тисне трохи після — «проґавив», а не «ще не кликали».</summary>
    int _lastEnd = -1_000;

    sealed record DanceReveal(int[] Winners, string Why, (int Seat, int Id)[] Ids, DanceRow[] Rows, (int Seat, int[] Pts)[] Trails);
    sealed record DanceRow(int Seat, int Good, int Bad, int Circle, int Best, int Kills, bool Win, int Pts);

    DanceCore Core => _core ??= new DanceCore(Ctx.Rng);

    /// <summary>Для тестів: ядро, місця й виклик напряму.</summary>
    public DanceCore CoreForTests => Core;
    public DanceSeat SeatForTests(int seat) => _s[seat];
    public string Phase => _phase;
    public int Left => _left;
    public int RoundNo => _round;
    public int T => _t;
    public int Beat => _beat;
    public int Lead => _lead;
    public int CallFig => _callFig;
    public int TempoNow => _tempo;

    public override string SeatName(int seat) => seat >= 0 && seat < Seats ? SeatNames[seat] : base.SeatName(seat);

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _rounds = options.TryGetValue("rounds", out var r) && int.TryParse(r, out var n) && n is 1 or 3 or 5 ? n : 3;
        _crowd = options.TryGetValue("crowd", out var c) && c is "small" or "big" ? c : "auto";
        _solo.Configure(options);
    }

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
        _pending.Clear();
        _ev.Clear();
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
        _callFig = -1;
        _calling = _judged = false;
        _beat = 0;
        _lead = Pulse[0] * LeadPulses;
        _tempo = 0;
        _lastEnd = -1_000;
        var owners = new List<int>(Seats);
        for (var i = 0; i < Seats; i++)
            if (_s[i].Active) owners.Add(i);
        Core.Deal(owners, Math.Max(0, _n - owners.Count));
        Core.T = 0;
        foreach (var v in Core.V)
            if (v.Owner >= 0) _s[v.Owner].Me = v.Id;
        foreach (var seat in owners)
        {
            var s = _s[seat];
            s.Alive = true;
            s.Streak = s.Best = s.Good = s.Bad = s.Circle = 0;
            s.Kills = s.Slaps = s.SlapCool = 0;
            s.ShownTotal = s.Total;
            s.Last = null;
            s.MoveAt = _clock;
            s.TrailHead = s.TrailCount = 0;
            s.Eye = false;
            s.RibbonAt = -1;
        }
        for (var i = 0; i < Seats; i++)
            if (!_s[i].Active) _s[i].Me = -1;
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
        if (seat < 0 || seat >= Seats || !_s[seat].Active) return ActResult.Fail("Тут так не танцюють");
        if (_s[seat].Bot) return ActResult.Fail("Тут танцює 🤖 бот — зачекай кінця партії");
        return action switch
        {
            "move" => Move(seat, payload),
            "fig" => Figure(seat, payload),
            "slap" => Slap(seat, payload),
            _ => ActResult.Fail("Тут так не танцюють"),
        };
    }

    ActResult Move(int seat, JsonElement payload)
    {
        var dir = Number(payload, "dir");
        if (dir is null or < -1 or > 3) return ActResult.Fail("Такого напрямку нема");
        if (_phase == PhaseOver) return ActResult.Fail("Раунд скінчився");
        var s = _s[seat];
        // Вибулому й у розкритті — приймаємо й мовчки не застосовуємо: тост нічого не мусить викривати.
        if (!s.Alive || _phase == PhaseReveal || s.Me < 0) return ActResult.Done;
        Core.V[s.Me].Want = dir.Value;
        s.MoveAt = _clock;
        return ActResult.Done;
    }

    /// <summary>Ціле число — і як <c>{name: n}</c>, і як голе число.</summary>
    static int? Number(JsonElement payload, string name) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetInt32(out var n) => n,
        JsonValueKind.Object when payload.TryGetProperty(name, out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n) => n,
        _ => null,
    };

    /// <summary>Необов'язкове ціле поле payload: нема (або null) — null; криве — false в ok.</summary>
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

    ActResult? PhaseRefusal(DanceSeat s)
    {
        if (_phase == PhaseStart) return ActResult.Fail("Музики ще настроюються");
        if (_phase != PhaseGo) return ActResult.Fail("Раунд скінчився");
        if (!s.Alive) return ActResult.Fail("Тебе вже вивели з танцю — дивись, хто кого");
        return null;
    }

    /// <summary>
    /// Фігура. Та сама <see cref="DanceCore.Perform"/>, що й у бота: гравець тисне між тиками, тож фігуру вперше видно
    /// в кадрі наступного тика — від нього й рахуємо, чи влучив у такт. Вид НЕ розсилаємо: інакше «хтось натиснув
    /// саме зараз» видав би гравця раніше, ніж будь-яка фігура в кадрі.
    /// </summary>
    ActResult Figure(int seat, JsonElement payload)
    {
        var fig = Number(payload, "f");
        if (fig is null or < 0 or >= DanceCore.Figures) return ActResult.Fail("Такої фігури нема");
        var s = _s[seat];
        if (PhaseRefusal(s) is { } no) return no;
        var me = Core.V[s.Me];
        if (me.Stun > 0) return ActResult.Fail("Ти ще отетерілий — не до танців");
        if (_callFig < 0)
            return ActResult.Fail(_t - _lastEnd <= 50 ? "Проґавив — чекай наступної фігури" : "Музики ще не кликали фігуру");
        if (me.Danced) return ActResult.Fail("Ти вже відтанцював цю фігуру");
        var tick = _t + 1;
        if (tick < _beat - DanceCore.Early) return ActResult.Fail("Зарано — дочекайся такту");
        return Core.Perform(me, fig.Value, tick, true) ? ActResult.Done : ActResult.Fail("Проґавив — чекай наступної фігури");
    }

    ActResult Slap(int seat, JsonElement payload)
    {
        var id = Field(payload, "id", out var ok);
        return SlapAt(seat, id, ok);
    }

    /// <summary>Ляпас — одна дорога і для людини, і для 🤖 бота (та сама перезарядка, дальність і отетеріння).</summary>
    ActResult SlapAt(int seat, int? id, bool ok = true)
    {
        var s = _s[seat];
        if (PhaseRefusal(s) is { } no) return no;
        var me = Core.V[s.Me];
        if (me.Stun > 0) return ActResult.Fail("Ти ще отетерілий — не до танців");
        if (me.Pose > 0) return ActResult.Fail("Спершу дотанцюй");
        if (s.SlapCool > 0) return ActResult.Fail("Рука ще не відійшла");

        if (!ok) return ActResult.Fail("Такого танцюриста нема");
        int target;
        if (id is { } want)
        {
            if (want < 0 || want >= Core.N) return ActResult.Fail("Такого танцюриста нема");
            if (want == s.Me) return ActResult.Fail("Себе по щоці? Не годиться");
            var q = Core.V[want];
            if (!q.Upright) return ActResult.Fail("Той і так уже сидить");
            if (DanceCore.Dist2(q, me.X, me.Y) > (long)DanceCore.SlapRangeMax * DanceCore.SlapRangeMax)
                return ActResult.Fail("Не дотягнешся — підійди ближче");
            target = want;
        }
        else
        {
            target = Core.Reach(me);
            if (target < 0) return ActResult.Fail("Поруч нікого — ляпас у повітря");
        }

        s.SlapCool = SlapCoolTicks;
        s.Slaps++;
        var hit = Core.V[target];
        BotsSawShot(me.Id);
        // обернутись до того, кого б'єш (клік міг бути й збоку)
        int dx = hit.X - me.X, dy = hit.Y - me.Y;
        if (dx != 0 || dy != 0) me.Dir = Math.Abs(dx) >= Math.Abs(dy) ? (dx > 0 ? 0 : 2) : (dy > 0 ? 1 : 3);
        me.Moving = false;
        if (hit.Owner >= 0)
        {
            var victim = _s[hit.Owner];
            victim.Alive = false;
            hit.Dead = true;
            hit.Pose = 0;
            hit.Miss = 0;
            hit.Stun = 0;
            hit.Want = -1;
            hit.Moving = false;
            s.Kills++;
            s.Total += PtKill;
            if (s.Slaps == 1) s.Eye = true;
            _pending.Add([1, me.Id, target, 1, hit.Owner]);
        }
        else
        {
            // бот ображено сідає, а той, хто вдарив, отетерів — і всім видно, хто це був
            _eye.Clear(target);     // сів ображений — отже, просто танцюрист: це бачать усі, і боти теж
            hit.Offended = DanceCore.OffendTicks;
            hit.Pose = 0;
            hit.Miss = 0;
            hit.Moving = false;
            hit.PlanAt = hit.PlanFig = -1;
            DanceCore.Forget(hit);
            me.Stun = StunTicks;
            _pending.Add([1, me.Id, target, 0, -1]);
        }
        _dirty = true;
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
                Core.T = _t;
                HeldKeys();
                Core.TimersAll();
                Core.ThinkAll();
                BotsThink();
                Core.StepAll();
                BotsWatch();
                Trail();
                if (--_left <= 0)
                {
                    _phase = PhaseGo;
                    _left = RoundTicks;
                    // музика рушає: пульс відлічуємо від цього тика, перший такт — за 6–9 пульсів
                    _beat = _t + Pulse[0] * Ctx.Rng.Next(6, 10);
                    _lead = Pulse[0] * LeadPulses;
                    _tempo = 0;
                    _dirty = true;
                }
                return Flush(true);
            case PhaseGo:
                OpenEvents();
                _t++;
                _clock++;
                Core.T = _t;
                HeldKeys();
                Timers();
                if (!_calling && _t == _beat - _lead) Announce();
                Core.ThinkAll();
                BotsThink();
                Core.StepAll();
                BotsWatch();
                Trail();
                if (_calling && !_judged && _t == _beat + DanceCore.Late + 1) Judge();
                if (_calling && _t >= _beat + DanceCore.LateMax) EndCall();
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

    /// <summary>Музики вигукують фігуру: за <see cref="_lead"/> тиків до такту. Боти одразу планують свій танець.</summary>
    void Announce()
    {
        _callFig = Ctx.Rng.Next(DanceCore.Figures);
        _calling = true;
        _judged = false;
        Core.Announce(_callFig, _beat);
        BotsPlan();
    }

    /// <summary>Вікно такту закрилось: хто схибив — «?» над головою (і бот, і гравець), гравцям — серія й очки.</summary>
    void Judge()
    {
        _judged = true;
        Core.Judge();
        BotsSawMiss();
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Active || !s.Alive || s.Me < 0) continue;
            var v = Core.V[s.Me];
            if (v.HitIn)
            {
                s.Good++;
                s.Circle++;
                s.Total += PtCircle;
                s.Streak++;
                s.Best = Math.Max(s.Best, s.Streak);
                s.Last = "ring";
                if (s.Streak >= RibbonStreak && s.RibbonAt < 0) s.RibbonAt = _t;
            }
            else if (v.Hit)
            {
                s.Good++;
                s.Streak = 0;           // «поспіль у колі»: вийшов із кола на такт — серія з нуля
                s.Last = "ok";
            }
            else
            {
                s.Bad++;
                s.Streak = 0;
                s.Last = "miss";
            }
        }
        // вид летить усім на кожен такт однаково — хоч хто вціляв, хоч ні
        _dirty = true;
    }

    /// <summary>Виклик скінчився; наступний такт — через ціле число пульсів у темпі, який диктує час раунду.</summary>
    void EndCall()
    {
        Core.EndCall();
        _calling = false;
        _callFig = -1;
        _lastEnd = _t;
        _tempo = TempoAt(_left);
        var pulse = Pulse[_tempo];
        _beat += pulse * Ctx.Rng.Next(GapMin[_tempo], GapMax[_tempo] + 1);
        _lead = pulse * LeadPulses;
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

    static int[] TrailOf(DanceSeat s)
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

    void Timers()
    {
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (s.Active && s.SlapCool > 0) s.SlapCool--;
        }
        Core.TimersAll();
    }

    /// <summary>Кінець раунду: хтось витанцював стрічку цього тика → на ногах ≤ 1 → вийшов час.</summary>
    void EndCheck()
    {
        List<int>? ribbon = null;
        int alive = 0, active = 0, last = -1;
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Active) continue;
            active++;
            if (!s.Alive) continue;
            alive++;
            last = i;
            if (s.RibbonAt == _t) (ribbon ??= []).Add(i);
        }
        if (ribbon is not null) { EndRound([.. ribbon], "ribbon"); return; }
        if (active >= 2 && alive <= 1) { EndRound(alive == 1 ? [last] : [], alive == 1 ? "last" : "none"); return; }
        if (_left > 0) return;

        int best = -1, count = 0, who = -1;
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Active || !s.Alive) continue;
            if (s.Circle > best) { best = s.Circle; count = 1; who = i; }
            else if (s.Circle == best) count++;
        }
        if (count == 1 && best > 0) EndRound([who], "time");
        else EndRound([], "none");
    }

    void EndRound(int[] winners, string why)
    {
        // з 🤖 ботами — без ачівок: партія тренувальна
        var awards = _bots.Length == 0;
        foreach (var w in winners)
        {
            _s[w].Total += PtRound;
            if (awards && why == "ribbon" && _s[w].Slaps == 0) Ctx.Award(w, 0, "ach:dance-ribbon");
        }
        for (var i = 0; i < Seats; i++)
            if (awards && _s[i].Active && _s[i].Eye) Ctx.Award(i, 0, "ach:dance-slap");
        _reveal = RevealOf(winners, why, s => s.Active);
        Freeze();
        _phase = PhaseReveal;
        _left = RevealTicks;
        _dirty = true;
    }

    /// <summary>Музика стихла: усі завмирають там, де стояли, недотанцьоване не рахується.</summary>
    void Freeze()
    {
        Core.EndCall();
        _calling = false;
        _callFig = -1;
        foreach (var v in Core.V)
        {
            v.Want = -1;
            v.Moving = false;
            v.Pose = 0;
            v.Miss = 0;
            v.Stun = 0;
        }
    }

    DanceReveal RevealOf(int[] winners, string why, Func<DanceSeat, bool> who)
    {
        var ids = new List<(int, int)>();
        var rows = new List<DanceRow>();
        var trails = new List<(int, int[])>();
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Plays || s.Me < 0 || !who(s)) continue;
            var win = why != "left" && Array.IndexOf(winners, i) >= 0;
            ids.Add((i, s.Me));
            rows.Add(new DanceRow(i, s.Good, s.Bad, s.Circle, s.Best, s.Kills, win,
                s.Circle * PtCircle + s.Kills * PtKill + (win ? PtRound : 0)));
            trails.Add((i, TrailOf(s)));
        }
        return new DanceReveal(winners, why, [.. ids], [.. rows], [.. trails]);
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
        var order = winners.Concat(active.Where(i => Array.IndexOf(winners, i) < 0).OrderByDescending(i => _s[i].Total));
        var line = string.Join(" : ", order.Select(i => $"{BotNick(i)} {_s[i].Total}"));
        if (_bots.Length > 0)
        {
            FinishWithBots(winners, line);
            return;
        }
        foreach (var i in active) Ctx.Score(i, _s[i].Total);
        Ctx.Finish(winners, winners.Length > 0 ? $"{Info.Title}: {line}" : $"{Info.Title}: {line} — нічия");
    }

    /// <summary>
    /// Хтось устав: його танцюрист лишається на подвір'ї й стає ботом — вихід нікого не викриває. Лишився один —
    /// партія його; нікого — нічия.
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
            DanceCore.Forget(v);
        }
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
            ? $"{Info.Title}: усі розійшлись — {_s[rest[0]].Nick} лишається танцювати сам-на-сам із музиками"
            : $"{Info.Title}: усі розійшлись");
    }

    // ---------------------------------------------------------------------------------------------
    // Кадр і вид
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Кадр: тик, фаза, скільки лишилось, 4 числа на танцюриста, виклик музик і події цього тика. Нічого про місця —
    /// ні owner, ні me, ні серій: бота від гравця за цими числами не відрізнити.
    /// </summary>
    public override object? Frame() => new
    {
        t = _t,
        ph = _phase,
        left = _left,
        v = _started ? Core.Pack() : Preview.Value.V,
        m = Music(),
        ev = _evFrame,
    };

    /// <summary>Виклик: [фігура (-1 — ніякої), тик такту (між викликами — останнього), тиків від виклику до такту, темп].</summary>
    int[] Music() => [_callFig, _beat, _lead, _tempo];

    static readonly Lazy<(int[] V, int[] Looks, string[] Names)> Preview = new(() =>
    {
        var core = new DanceCore(new Random(2709));
        core.Deal([], 24);
        return (core.Pack(), core.Looks(), core.NamesNow());
    });

    static readonly object Circle = new { x = DanceMap.CircleX, y = DanceMap.CircleY, r = DanceMap.CircleR };

    public override object View(int? seat)
    {
        var live = _started;
        var me = seat is { } k && k >= 0 && k < Seats && _s[k].Active && live && _s[k].Me >= 0 ? MeOf(k) : null;
        // Фігури й очки раунду — лише на розкритті: «схибив +1» в ту саму мить, коли над кимось знак питання, назвав би
        // гравця, а «+2» — того, хто вдарив.
        var open = _phase is PhaseReveal or PhaseOver;
        var seats = new List<object>();
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (live && s.Plays)
                seats.Add(new
                {
                    seat = i, nick = s.Nick, alive = s.Alive, @out = s.Out,
                    good = open ? s.Good : (int?)null, bad = open ? s.Bad : (int?)null,
                    total = open ? s.Total : s.ShownTotal,
                });
            else if (!live && Ctx.Seated(i))
                seats.Add(new { seat = i, nick = Ctx.NickOf(i) ?? "", alive = true, @out = false, good = (int?)null, bad = (int?)null, total = 0 });
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
            width = DanceMap.W,
            height = DanceMap.H,
            cell = DanceMap.Cell,
            n = live ? Core.N : Preview.Value.V.Length / 4,
            map = DanceMap.Rows,
            circle = Circle,
            need = RibbonStreak,
            looks = live ? Core.Looks() : Preview.Value.Looks,
            names = live ? Core.NamesNow() : Preview.Value.Names,
            v = live ? Core.Pack() : Preview.Value.V,
            m = Music(),
            seats,
            dead,
            me,
            reveal = _reveal is { } r && (_phase is PhaseReveal or PhaseOver)
                ? new
                {
                    winners = r.Winners,
                    why = r.Why,
                    ids = r.Ids.Select(p => new { seat = p.Seat, id = p.Id }).ToArray(),
                    rows = r.Rows.Select(x => new { seat = x.Seat, good = x.Good, bad = x.Bad, circle = x.Circle, best = x.Best, kills = x.Kills, win = x.Win, pts = x.Pts }).ToArray(),
                    trails = r.Trails.Select(p => new { seat = p.Seat, pts = p.Pts }).ToArray(),
                }
                : null,
            result = _phase == PhaseOver && _winners is { } w
                ? new { winners = w, totals = _s.Select(x => x.Total).ToArray(), why = _endWhy }
                : null,
            turn = (int?)null,
            botOffer = _solo.Offer(Ctx, Seats),
            botWanted = _solo.Wanted,
            botLvl = _solo.LevelKey,
            bot = BotView(),
        };
    }

    object MeOf(int seat)
    {
        var s = _s[seat];
        var v = Core.V[s.Me];
        return new
        {
            id = s.Me,
            alive = s.Alive,
            streak = s.Streak,
            good = s.Good,
            bad = s.Bad,
            circle = s.Circle,
            danced = v.Danced && _calling,
            last = s.Last,
            slapCool = s.SlapCool,
            stun = v.Stun,
        };
    }
}
