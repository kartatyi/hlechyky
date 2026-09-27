using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Стан місця за партію й раунд. Список кладок, головешки й «хто я» — таємниця місця, решта — публічна.</summary>
public sealed class KupalaSeat
{
    public bool Plays;
    /// <summary>Устав посеред партії: його селянин — уже бот, а очки лишаються в таблиці.</summary>
    public bool Out;
    public string Nick = "";
    public bool Alive;
    /// <summary>Id свого селянина в цьому раунді.</summary>
    public int Me = -1;
    public readonly int[] List = new int[Kupala.ListLen];
    public readonly bool[] Done = new bool[Kupala.ListLen];
    public int Torches, SlapCool, LaunchCool;
    /// <summary>Вінків зі списку за раунд — таємниця до розкриття, як і очки раунду.</summary>
    public int Wreaths;
    public int Kills;
    public bool Fern;
    public int Total;
    /// <summary>Очки на початок раунду — їх і видно всім, поки раунд іде (інакше «+2» назвав би того, хто щойно вибив).</summary>
    public int ShownTotal;
    public int Slaps;
    /// <summary>Вибив гравця, коли обидва стояли в темряві, — на ачівку «Навпомацки».</summary>
    public bool Blind;
    public int MoveAt;
    public readonly int[] TrailX = new int[Kupala.TrailLen], TrailY = new int[Kupala.TrailLen];
    public int TrailHead, TrailCount;
    public int CompletedAt = -1;

    public bool Active => Plays && !Out;
    public bool Complete => Done.All(d => d);
}

/// <summary>
/// «Купальська ніч»: Unspottable у темряві. Ніч на Івана Купала, галявина між лісом і річкою, кілька вогнищ, рідкі
/// світлячки й зарниці. Гравці — такі самі селяни, як боти, і в кадрі є лише ті, хто стоїть у світлі: решти нема
/// зовсім — ні місця, ні стану. Свій селянин у темряві зникає й для тебе. Пусти три вінки зі своїх кладок або вибий
/// суперників ляпасом. Правила поля — у <see cref="KupalaCore"/>, тут фази, очки, дії, вид і кадр
/// (spec: docs/games/specs/kupala.md).
/// </summary>
public sealed class Kupala : Game
{
    public const string PhaseLobby = "lobby", PhaseStart = "start", PhaseGo = "go", PhaseReveal = "reveal", PhaseOver = "over";
    public const int Seats = 8;
    public const int TickMs = 40;
    /// <summary>«Роздивись» — 3 с сутінок: видно всіх, ходити можна, решта — ні.</summary>
    public const int StartTicks = 75;
    public const int RoundTicks = 2250;
    public const int RevealTicks = 150;
    public const int ListLen = 3;
    public const int TorchesPerRound = 2;
    /// <summary>Між ляпасами й кидками — секунда (одна рука на обидва).</summary>
    public const int SlapCoolTicks = 25;
    public const int LaunchTicks = KupalaCore.BusyTicks;
    /// <summary>Після вінка — 2 с, поки сплетеш наступний.</summary>
    public const int LaunchCoolTicks = 50;
    public const int PtWreath = 1, PtKill = 2, PtRound = 3, PtFern = 2;
    public const int RevealFrameEvery = 5;
    public const int MoveHoldTicks = 75;
    public const int TrailEvery = 12, TrailLen = 42;

    /// <summary>«Як на Купала»: на двох — 22 боти, далі більше, на вісьмох — 34 (темрява й так ховає половину).</summary>
    public static int BotsFor(int players) => players switch
    {
        <= 2 => 22,
        3 => 24,
        4 => 26,
        5 => 28,
        6 => 30,
        7 => 32,
        _ => 34,
    };

    public override GameInfo Info { get; } = new(
        "kupala", "Купальська ніч", "купальську ніч", GameGroup.Live, 2, Seats, TickMs: TickMs,
        Start: StartMode.ByHost, Hidden: true, Score: ScoreOrder.HigherIsBetter,
        Options:
        [
            new GameOption("rounds", "Раундів", [("3", "3 раунди"), ("1", "1 раунд"), ("5", "5 раундів")], "3"),
            new GameOption("folk", "Люду", [("auto", "Як на Купала"), ("small", "Жменька (16)"), ("big", "Усе село (40)")], "auto"),
        ],
        Hint: "Ніч на Івана Купала: у темряві не видно нікого — навіть себе. Пусти три вінки на воду або вистеж друзів ляпасом. Біля вогнищ світло, решту пам'ятай напам'ять");

    static readonly string[] SeatNames = ["жовтий", "зелений", "рудий", "сірий", "синій", "рожевий", "фіолетовий", "червоний"];

    readonly KupalaSeat[] _s = [.. Enumerable.Range(0, Seats).Select(_ => new KupalaSeat())];
    KupalaCore? _core;
    int _rounds = 3;
    string _folk = "auto";
    bool _started;
    string _phase = PhaseLobby;
    int _round;
    int _left;
    int _t;
    int _clock;
    int _n;
    bool _dirty;
    string _endWhy = "end";
    /// <summary>Події з дій між тиками — підуть у наступний кадр (як події ботів із самого тика).</summary>
    readonly List<int[]> _pending = [];
    /// <summary>Події цього тика ще з усіма id — сховаємо тих, хто в темряві, коли порахуємо світло.</summary>
    readonly List<int[]> _raw = [];
    int[][] _evFrame = [];
    KupalaReveal? _reveal;
    int[]? _winners;

    sealed record KupalaReveal(int[] Winners, string Why, (int Seat, int Id)[] Ids, KupalaRow[] Rows, (int Seat, int[] Pts)[] Trails);
    sealed record KupalaRow(int Seat, int Wreaths, int Kills, bool Fern, bool Win, int Pts);

    KupalaCore Core => _core ??= new KupalaCore(Ctx.Rng);

    /// <summary>Для тестів: ядро й місця напряму.</summary>
    public KupalaCore CoreForTests => Core;
    public KupalaSeat SeatForTests(int seat) => _s[seat];
    public string Phase => _phase;
    public int Left => _left;
    public int RoundNo => _round;

    public override string SeatName(int seat) => seat >= 0 && seat < Seats ? SeatNames[seat] : base.SeatName(seat);

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _rounds = options.TryGetValue("rounds", out var r) && int.TryParse(r, out var n) && n is 1 or 3 or 5 ? n : 3;
        _folk = options.TryGetValue("folk", out var c) && c is "small" or "big" ? c : "auto";
    }

    public int BotsForTable(int players) => _folk switch
    {
        "small" => 16,
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
        _raw.Clear();
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
        _pending.Clear();
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
            s.Torches = TorchesPerRound;
            s.SlapCool = s.LaunchCool = 0;
            s.Wreaths = s.Kills = s.Slaps = 0;
            s.Fern = s.Blind = false;
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

    /// <summary>Список: по одній кладці ліворуч, посередині й праворуч, порядок випадковий.</summary>
    static void DealList(Random rng, KupalaSeat s)
    {
        s.List[0] = KupalaMap.LeftSpots[rng.Next(KupalaMap.LeftSpots.Length)];
        s.List[1] = KupalaMap.MidSpots[rng.Next(KupalaMap.MidSpots.Length)];
        s.List[2] = KupalaMap.RightSpots[rng.Next(KupalaMap.RightSpots.Length)];
        for (var i = ListLen - 1; i > 0; i--)
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
            "slap" => Slap(seat, payload),
            "launch" => Launch(seat, payload),
            "torch" => Torch(seat, payload),
            _ => ActResult.Fail("Тут так не ходять"),
        };
    }

    ActResult Move(int seat, JsonElement payload)
    {
        var dir = Dir(payload);
        if (dir is null or < -1 or > 3) return ActResult.Fail("Такого напрямку нема");
        if (_phase == PhaseOver) return ActResult.Fail("Раунд скінчився");
        var s = _s[seat];
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

    /// <summary>Спільне для ляпаса, вінка й головешки: фаза, живий, не отетерів.</summary>
    ActResult? Refusal(KupalaSeat s)
    {
        if (_phase == PhaseStart) return ActResult.Fail("Зачекай, сонце ще не сіло");
        if (_phase != PhaseGo) return ActResult.Fail("Раунд скінчився");
        if (!s.Alive) return ActResult.Fail("Тебе вже вибили — дивись, хто кого");
        if (Core.V[s.Me].Stun > 0) return ActResult.Fail("Ти ще отетерілий — постій");
        return null;
    }

    ActResult Slap(int seat, JsonElement payload)
    {
        var s = _s[seat];
        if (Refusal(s) is { } no) return no;
        var me = Core.V[s.Me];
        if (me.Busy > 0) return ActResult.Fail("Руки зайняті — ти пускаєш вінок");
        if (s.SlapCool > 0) return ActResult.Fail("Рука ще не відійшла");
        var id = Field(payload, "id", out var ok);
        if (!ok) return ActResult.Fail("Такого селянина нема");
        int target;
        if (id is { } want)
        {
            if (want < 0 || want >= Core.N) return ActResult.Fail("Такого селянина нема");
            if (want == s.Me) return ActResult.Fail("Себе по щоці? Не треба");
            var q = Core.V[want];
            if (!q.Upright) return ActResult.Fail("Лежачого не б'ють");
            if (KupalaCore.Dist2(q, me.X, me.Y) > (long)KupalaCore.SlapReachMax * KupalaCore.SlapReachMax)
                return ActResult.Fail("Не дотягнешся");
            target = want;
        }
        // Без цілі — найближчий перед собою, хоч у світлі, хоч у темряві. Нікого — махнув повз: це теж чують усі.
        else target = Core.Cone(me);

        s.SlapCool = SlapCoolTicks;
        s.Slaps++;
        // «обидва в темряві» — за світлом останнього тика, тим самим, що бачили всі
        var blind = target >= 0 && !Core.Lit[me.Id] && !Core.Lit[target];
        var hit = Core.Slap(me, target);
        MovePending();
        if (hit is { Owner: >= 0 } victim)
        {
            var v = _s[victim.Owner];
            v.Alive = false;
            s.Kills++;
            s.Total += PtKill;
            if (blind) s.Blind = true;
        }
        _dirty = true;
        return ActResult.Done;
    }

    ActResult Torch(int seat, JsonElement payload)
    {
        var s = _s[seat];
        if (Refusal(s) is { } no) return no;
        if (payload.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined)) return ActResult.Fail("Тут так не ходять");
        var me = Core.V[s.Me];
        if (me.Busy > 0) return ActResult.Fail("Руки зайняті — ти пускаєш вінок");
        if (s.Torches <= 0) return ActResult.Fail("Головешки скінчились");
        if (s.SlapCool > 0) return ActResult.Fail("Рука ще не відійшла");
        s.Torches--;
        s.SlapCool = SlapCoolTicks;
        Core.Throw(me);
        MovePending();
        _dirty = true;
        return ActResult.Done;
    }

    ActResult Launch(int seat, JsonElement payload)
    {
        var s = _s[seat];
        if (Refusal(s) is { } no) return no;
        var me = Core.V[s.Me];
        if (me.Busy > 0) return ActResult.Fail("Ти вже пускаєш вінок");
        var want = Field(payload, "spot", out var ok);
        if (!ok) return ActResult.Fail("Такої кладки нема");
        if (want is { } bad && (bad < 0 || bad >= KupalaMap.Spots.Length)) return ActResult.Fail("Такої кладки нема");

        // Папороть — рвуть там, де цвіте (кнопка та сама, що й для вінка). Боти її не рвуть: це принада.
        if (want is null && Core.FernCell >= 0 && KupalaMap.CellOf(me.X, me.Y) == Core.FernCell)
        {
            Busy(me, KupalaCore.FernWhat);
            return ActResult.Done;
        }
        if (s.LaunchCool > 0) return ActResult.Fail("Сплітаєш новий вінок…");
        var spot = KupalaCore.SpotAt(me);
        if (spot < 0 || (want is { } k && k != spot)) return ActResult.Fail("Стань на кладку над водою");
        // Секунду стоїмо обличчям до води. Вид не розсилаємо: «хтось почав» видав би гравця раніше за вінок.
        Busy(me, spot);
        return ActResult.Done;
    }

    static void Busy(KupalaVillager me, int what)
    {
        me.Busy = LaunchTicks;
        me.BusyWhat = what;
        me.Dir = 1;
        me.Moving = false;
    }

    /// <summary>Події, які ядро породило за дію гравця, — у чергу наступного кадру.</summary>
    void MovePending()
    {
        _pending.AddRange(Core.Events);
        Core.Events.Clear();
    }

    // ---------------------------------------------------------------------------------------------
    // Тик
    // ---------------------------------------------------------------------------------------------

    public override TickResult Tick()
    {
        switch (_phase)
        {
            case PhaseStart:
                Open();
                _t++;
                _clock++;
                Core.T = _t;
                HeldKeys();
                Core.LightsTick();
                Core.ThinkAll();
                Core.StepAll();
                Core.ComputeLit(true);
                Trail();
                if (--_left <= 0)
                {
                    _phase = PhaseGo;
                    _left = RoundTicks;
                    Core.Night = true;
                    _dirty = true;
                }
                return Flush(true);
            case PhaseGo:
                Open();
                _t++;
                _clock++;
                Core.T = _t;
                HeldKeys();
                Timers();
                Core.LightsTick();
                Core.ThinkAll();
                Core.StepAll();
                Core.ComputeLit(Core.Sky > 0);
                Trail();
                _left--;
                EndCheck();
                return Flush(true);
            case PhaseReveal:
                Open();
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

    void Open()
    {
        _raw.Clear();
        _raw.AddRange(_pending);
        _pending.Clear();
    }

    /// <summary>
    /// Події в кадр: id того, хто в темряві, стає -1 — бо в кадрі його нема, і подія не мусить казати більше за
    /// кадр. Виняток — вибитий гравець: його id і так публічний (<c>dead</c>).
    /// </summary>
    TickResult Flush(bool frame)
    {
        _raw.AddRange(Core.Events);
        Core.Events.Clear();
        if (_raw.Count > 0) _dirty = true;
        var lit = Core.Lit;
        bool Seen(int id) => id >= 0 && id < lit.Length && lit[id];
        var ev = new int[_raw.Count][];
        for (var i = 0; i < _raw.Count; i++)
        {
            var e = (int[])_raw[i].Clone();
            switch (e[0])
            {
                case 1:
                    if (!Seen(e[1])) e[1] = -1;
                    if (e[3] != 1 && !Seen(e[2])) e[2] = -1;
                    break;
                case 3:
                case 4:
                    if (!Seen(e[1])) e[1] = -1;
                    break;
            }
            ev[i] = e;
        }
        _raw.Clear();
        _evFrame = ev;
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

    static int[] TrailOf(KupalaSeat s)
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
            if (!s.Active) continue;
            if (s.SlapCool > 0) s.SlapCool--;
            if (s.LaunchCool > 0) s.LaunchCool--;
        }
        Core.TimersAll();
        foreach (var id in Core.Done) Finished(Core.V[id]);
    }

    /// <summary>
    /// Секунда минула: вінок на воді (чий завгодно — спалах однаковий) або папороть у руках. Гравцеві зі списку —
    /// зараховано; очки видно лише на розкритті.
    /// </summary>
    void Finished(KupalaVillager v)
    {
        var what = v.BusyWhat;
        v.BusyWhat = -1;
        if (what == KupalaCore.FernWhat)
        {
            if (Core.PickFern(v) && v.Owner >= 0)
            {
                var f = _s[v.Owner];
                f.Fern = true;
                f.Total += PtFern;
            }
            return;
        }
        if (what < 0) return;
        Core.Launch(what);
        if (v.Owner < 0) return;
        var s = _s[v.Owner];
        s.LaunchCool = LaunchCoolTicks;
        for (var i = 0; i < ListLen; i++)
        {
            if (s.List[i] != what || s.Done[i]) continue;
            s.Done[i] = true;
            s.Wreaths++;
            s.Total += PtWreath;
            if (s.Complete) s.CompletedAt = _t;
            break;
        }
    }

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
            if (s.Wreaths > best) { best = s.Wreaths; count = 1; who = i; }
            else if (s.Wreaths == best) count++;
        }
        if (count == 1) EndRound([who], "time");
        else EndRound([], "none");
    }

    void EndRound(int[] winners, string why)
    {
        foreach (var w in winners) _s[w].Total += PtRound;
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Active) continue;
            if (s.Fern) Ctx.Award(i, 0, "ach:kupala-fern");
            if (s.Blind) Ctx.Award(i, 0, "ach:kupala-blind");
        }
        _reveal = RevealOf(winners, why, s => s.Active);
        Freeze();
        _phase = PhaseReveal;
        _left = RevealTicks;
        Core.Night = false;
        Core.ComputeLit(true);          // на розкритті світає: видно всіх
        _dirty = true;
    }

    void Freeze()
    {
        foreach (var v in Core.V)
        {
            v.Want = -1;
            v.Moving = false;
            v.Busy = 0;              // недоплетений вінок не рахується
            v.BusyWhat = -1;
        }
    }

    KupalaReveal RevealOf(int[] winners, string why, Func<KupalaSeat, bool> who)
    {
        var ids = new List<(int, int)>();
        var rows = new List<KupalaRow>();
        var trails = new List<(int, int[])>();
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (!s.Plays || s.Me < 0 || !who(s)) continue;
            var win = why != "left" && Array.IndexOf(winners, i) >= 0;
            ids.Add((i, s.Me));
            rows.Add(new KupalaRow(i, s.Wreaths, s.Kills, s.Fern, win,
                s.Wreaths * PtWreath + s.Kills * PtKill + (s.Fern ? PtFern : 0) + (win ? PtRound : 0)));
            trails.Add((i, TrailOf(s)));
        }
        return new KupalaReveal(winners, why, [.. ids], [.. rows], [.. trails]);
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
    /// Хтось устав: його селянин лишається на галявині й стає ботом — вихід нікого не викриває. Лишився один —
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
            KupalaCore.Forget(v);
        }
        _dirty = true;

        var rest = new List<int>();
        for (var i = 0; i < Seats; i++)
            if (i != seat && _s[i].Active && Ctx.Seated(i)) rest.Add(i);
        if (rest.Count > 1) return;
        if (!(_phase == PhaseReveal && _reveal is not null)) _reveal = RevealOf([.. rest], "left", x => x.Me >= 0);
        Freeze();
        Core.Night = false;
        Core.ComputeLit(true);
        _phase = PhaseOver;
        _left = 0;
        _endWhy = "left";
        _winners = [.. rest];
        foreach (var i in rest) Ctx.Score(i, _s[i].Total);
        Ctx.Finish([.. rest], rest.Count == 1
            ? $"{Info.Title}: усі розійшлись — {_s[rest[0]].Nick} зустрічає світанок сам-на-сам із селом"
            : $"{Info.Title}: усі розійшлись");
    }

    // ---------------------------------------------------------------------------------------------
    // Кадр і вид
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Кадр: тик, фаза, скільки лишилось, зарниця, селяни У СВІТЛІ (по 5 чисел за зростанням id), світло й події.
    /// Нічого про місця, а про тих, хто в темряві, — зовсім нічого.
    /// </summary>
    public override object? Frame() => new
    {
        t = _t,
        ph = _phase,
        left = _left,
        sky = _started ? Core.Sky : 0,
        v = _started ? Core.Pack() : Preview.Value.V,
        l = _started ? Core.PackLights() : Preview.Value.L,
        ev = _evFrame,
    };

    /// <summary>Сутінки в лобі: село стоїть біля вогнищ, поки господар не натисне «Почати».</summary>
    static readonly Lazy<(int[] V, int[] L, int[] Looks, string[] Names, int N)> Preview = new(() =>
    {
        var core = new KupalaCore(new Random(2406));
        core.Deal([], 22);
        return (core.Pack(), core.PackLights(), core.Looks(), core.NamesNow(), core.N);
    });

    static readonly object[] Spots =
    [
        .. KupalaMap.Spots.Select(s => (object)new { i = s.I, name = s.Name, where = s.Where, emoji = s.Emoji, x = s.X, y = s.Y }),
    ];

    static readonly object[] Fires = [.. KupalaMap.Fires.Select(f => (object)new { x = f.X, y = f.Y })];

    public override object View(int? seat)
    {
        var live = _started;
        var me = seat is { } k && k >= 0 && k < Seats && _s[k].Active && live && _s[k].Me >= 0 ? MeOf(k) : null;
        var open = _phase is PhaseReveal or PhaseOver;
        var seats = new List<object>();
        for (var i = 0; i < Seats; i++)
        {
            var s = _s[i];
            if (live && s.Plays)
                seats.Add(new { seat = i, nick = s.Nick, alive = s.Alive, @out = s.Out, wreaths = open ? s.Wreaths : (int?)null, total = open ? s.Total : s.ShownTotal });
            else if (!live && Ctx.Seated(i))
                seats.Add(new { seat = i, nick = Ctx.NickOf(i) ?? "", alive = true, @out = false, wreaths = (int?)null, total = 0 });
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
            width = KupalaMap.W,
            height = KupalaMap.H,
            cell = KupalaMap.Cell,
            n = live ? Core.N : Preview.Value.N,
            map = KupalaMap.Rows,
            spots = Spots,
            fires = Fires,
            looks = live ? Core.Looks() : Preview.Value.Looks,
            names = live ? Core.NamesNow() : Preview.Value.Names,
            v = live ? Core.Pack() : Preview.Value.V,
            l = live ? Core.PackLights() : Preview.Value.L,
            sky = live ? Core.Sky : 0,
            seats,
            dead,
            me,
            reveal = _reveal is { } r && open
                ? new
                {
                    winners = r.Winners,
                    why = r.Why,
                    ids = r.Ids.Select(p => new { seat = p.Seat, id = p.Id }).ToArray(),
                    rows = r.Rows.Select(x => new { seat = x.Seat, wreaths = x.Wreaths, kills = x.Kills, fern = x.Fern, win = x.Win, pts = x.Pts }).ToArray(),
                    trails = r.Trails.Select(p => new { seat = p.Seat, pts = p.Pts }).ToArray(),
                }
                : null,
            result = _phase == PhaseOver && _winners is { } w
                ? new { winners = w, totals = _s.Select(x => x.Total).ToArray(), why = _endWhy }
                : null,
            turn = (int?)null,
        };
    }

    /// <summary>
    /// Своє місце: хто я, список, що вже пустив, головешки, перезарядки. Жодних координат — у темряві ти не знаєш, де
    /// ти, так само як і всі. Де стоїш у світлі, клієнт і так бачить у кадрі за своїм id.
    /// </summary>
    object MeOf(int seat)
    {
        var s = _s[seat];
        var v = Core.V[s.Me];
        return new
        {
            id = s.Me,
            list = (int[])s.List.Clone(),
            done = (bool[])s.Done.Clone(),
            torches = s.Torches,
            cool = s.SlapCool,
            launchCool = s.LaunchCool,
            stun = v.Stun,
            busy = v.Busy,
            fern = s.Fern,
            alive = s.Alive,
        };
    }
}
