using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Глекомети: артилерія хатами на 2–6. У кожного хата на колесах із катапультою на даху; ходять по черзі —
/// посунутись, вибрати снаряд, кут і силу, стрельнути. Фізику села рахує <see cref="GlekometCore"/>, а тут —
/// фази («готуйсь», приціл, політ, пауза), черга по колу серед живих, отрута на вході в хід, вода з обраного
/// кола, пропуски, кінець партії, вихід посеред партії, серія перемог за ніками, вид і кадр (spec glekomet.md).
/// Кадр летить лише тоді, коли щось змінилось: у прицілі без руху — нічого, у польоті — 25 разів на секунду.
/// </summary>
public sealed class Glekomet : Game
{
    public const int TickMs = 40, StartTicks = 50, FlyTicks = 200, SettleShot = 30, SettleSkip = 15;
    public const int SniperGap = 500, IdleLimit = 3, SkipLimit = 3, LogLines = 6, NickMax = 14;
    /// <summary>
    /// Хід того, хто минулого разу проспав, — 10 с, а не 30: інакше п'ятеро півтори хвилини дивились на порожню дугу,
    /// поки хата засне втретє. Ворухнувся (приціл, крок) — хід одразу стає повним.
    /// </summary>
    public const int SleepySecs = 10;
    public const string PhaseLobby = "lobby", PhaseStart = "start", PhaseAim = "aim", PhaseFly = "fly", PhaseSettle = "settle", PhaseOver = "over";
    const int Seats = GlekometCore.Seats, Kinds = 6;

    /// <summary>Страховка від нескінченної партії; тести опускають її, щоб дійти до межі швидко.</summary>
    public int MaxRounds { get; set; } = 40;

    public override GameInfo Info { get; } = new(
        "glekomet", "Глекомети", "глекомети", GameGroup.Live, 2, Seats, TickMs: TickMs,
        Start: StartMode.ByHost,
        Options:
        [
            new GameOption("teams", "Склад", [("solo", "Кожен за себе"), ("teams", "Дві команди (парні проти непарних місць)")], "solo"),
            new GameOption("turn", "Час на хід", [("20", "20 с"), ("30", "30 с"), ("45", "45 с")], "30"),
            new GameOption("water", "Вода", [("6", "Підступає з 6-го кола"), ("10", "З 10-го кола"), ("0", "Без води")], "6"),
        ],
        Hint: "Артилерія хатами: цілься, дай сили, зваж на вітер — і глек полетить у сусідську хату. Земля рветься, вода підступає");

    // ---------- налаштування столу ----------
    bool _teamsWanted;
    int _turnSecs = 30;
    int _waterFrom = 6;

    // ---------- партія ----------
    GlekometCore? _core;
    GlekometCore? _preview;
    string _phase = PhaseLobby;
    /// <summary>Тиків до кінця поточної фази (start, aim, settle); у польоті — скільки вже летимо.</summary>
    int _left;
    /// <summary>
    /// Кінець ходу за годинником кімнати. Хід міряємо не тиками: каркасний годинник на Windows тикає рідше за
    /// обіцяні 25/с (заміряно ≈ 19/с), і тоді 30 секунд тиками тривали б ~39, а дуга в людей показувала б нуль,
    /// коли хід ще йде. У тестах годинник — FakeClock, що крокує рівно по тику, тож детермінізм той самий.
    /// </summary>
    DateTimeOffset _deadline;
    /// <summary>Коли почався хід і скільки секунд він триває (повний — <see cref="_turnSecs"/>, сонному — <see cref="SleepySecs"/>).</summary>
    DateTimeOffset _turnAt;
    int _turnLen = 30;
    int _t;
    int _turn = -1;
    int _cursor = Seats;
    int _round;
    int _turnNo;
    int _idle;
    bool _shotThisRound;
    bool _teams;
    bool _started;
    readonly int[] _skips = new int[Seats];
    readonly int[][] _inv = NewInv();
    readonly int[] _aimA = new int[Seats], _aimP = new int[Seats], _aimW = new int[Seats];
    readonly GlekometStat[] _stats = NewStats();
    readonly string[] _nick = new string[Seats];
    readonly List<string> _log = [];
    object? _last;
    object? _result;
    int _bestSeat = -1, _bestDmg, _bestW, _bestTo = -1;
    int _sniperGiven;

    // ---------- серія перемог за ніками («Ще раз» обертає місця, а серія їде з людиною) ----------
    readonly Dictionary<string, int> _wins = new(StringComparer.Ordinal);
    string _crew = "";

    // ---------- що змінилось між тиками (ходи приходять між ними, а кадр будує тик) ----------
    int _pendMoved;
    bool _pendHp, _pendAim, _pendView;
    bool _aimMark, _wlMark;

    static int[][] NewInv()
    {
        var inv = new int[Seats][];
        for (var i = 0; i < Seats; i++) inv[i] = new int[Kinds];
        return inv;
    }

    static GlekometStat[] NewStats()
    {
        var s = new GlekometStat[Seats];
        for (var i = 0; i < Seats; i++) s[i] = new GlekometStat();
        return s;
    }

    /// <summary>Ядро партії (для тестів: поставити хату, підправити землю). До старту — null.</summary>
    public GlekometCore? Core => _core;
    public string Phase => _phase;
    public int Turn => _turn;
    public int Round => _round;
    public int LeftTicks => _phase == PhaseAim ? (int)Math.Ceiling((_deadline - Ctx.Clock.UtcNow).TotalMilliseconds / TickMs) : _left;
    public bool Teams => _teams;

    public override string SeatName(int seat) => seat switch
    {
        0 => "жовта",
        1 => "зелена",
        2 => "руда",
        3 => "сіра",
        4 => "синя",
        _ => "рожева",
    };

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _teamsWanted = options.TryGetValue("teams", out var t) && t == "teams";
        _turnSecs = options.TryGetValue("turn", out var s) && int.TryParse(s, out var n) && n is 20 or 30 or 45 ? n : 30;
        _waterFrom = options.TryGetValue("water", out var w) && int.TryParse(w, out var m) && m is 0 or 6 or 10 ? m : 6;
    }

    // =============================================================================================
    // Старт
    // =============================================================================================

    public override void Start()
    {
        _core ??= new GlekometCore(Ctx.Rng);
        var plays = new bool[Seats];
        int even = 0, odd = 0;
        for (var s = 0; s < Seats; s++)
        {
            plays[s] = Ctx.Seated(s);
            _nick[s] = Ctx.NickOf(s) ?? "";
            if (!plays[s]) continue;
            if (s % 2 == 0) even++; else odd++;
        }
        BeginSeries();

        _log.Clear();
        Note("Готуйсь");
        // Команди — лише рівні: парних стільки ж, скільки непарних; інакше тихо граємо кожен за себе.
        _teams = _teamsWanted && even == odd && even > 0;
        if (_teamsWanted && !_teams) Note("Команди — лише парним складом: граємо кожен за себе");

        _core.Teams = _teams;
        _core.Generate(plays);
        for (var s = 0; s < Seats; s++)
        {
            for (var w = 0; w < Kinds; w++) _inv[s][w] = plays[s] ? GlekometCore.Weapons[w].Stock : 0;
            _aimA[s] = _core.Huts[s].X < GlekometCore.W / 2 ? 45 : 135;
            _aimP[s] = 60;
            _aimW[s] = 0;
            _skips[s] = 0;
            _stats[s].Clear();
        }
        _started = true;
        _phase = PhaseStart;
        _left = StartTicks;
        _t = 0;
        _turn = -1;
        _cursor = Seats;
        _round = 0;
        _turnNo = 0;
        _idle = 0;
        _shotThisRound = false;
        _last = null;
        _result = null;
        _bestSeat = _bestTo = -1;
        _bestDmg = _bestW = 0;
        _sniperGiven = 0;
        _pendMoved = 0;
        _pendHp = _pendAim = false;
        _pendView = true;
    }

    void BeginSeries()
    {
        var crew = string.Join("|", _nick.Where(n => n.Length > 0).Select(Key).Order(StringComparer.Ordinal));
        if (crew == _crew) return;
        _crew = crew;
        _wins.Clear();
    }

    static string Key(string nick) => nick.Trim().ToLowerInvariant();

    // =============================================================================================
    // Дії
    // =============================================================================================

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        switch (action)
        {
            case "fire": return Fire(seat, payload);
            case "move": return Move(seat, payload);
            case "skip": return Skip(seat);
            case "aim": return Aim(seat, payload);
            default: return ActResult.Fail("Тут так не ходять");
        }
    }

    /// <summary>Спільні перші п'ять перевірок fire/move/skip (spec §3). null — можна ходити.</summary>
    string? Gate(int seat)
    {
        if (_phase == PhaseOver) return "Партію вже зіграно";
        if (_phase is PhaseStart or PhaseLobby || _core is null) return "Зачекай, зараз почнемо";
        if (_phase is PhaseFly or PhaseSettle) return "Зачекай, глек ще летить";
        if (seat != _turn) return "Зараз не твій хід";
        if (seat < 0 || seat >= Seats || !_core.Huts[seat].Alive) return "Твоя хата вже руїна — дивись далі";
        return null;
    }

    static int? Int(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    ActResult Fire(int seat, JsonElement payload)
    {
        if (Gate(seat) is { } no) return ActResult.Fail(no);
        var w = Int(payload, "w");
        if (w is null or < 0 or >= Kinds) return ActResult.Fail("Такої зброї в коморі нема");
        if (_inv[seat][w.Value] == 0) return ActResult.Fail("Цього вже не лишилось");
        var a = Int(payload, "a");
        if (a is null or < 0 or > 180) return ActResult.Fail("Кут — від 0 до 180");
        var p = Int(payload, "p");
        if (p is null or < 5 or > 100) return ActResult.Fail("Сила — від 5 до 100");

        var core = _core!;
        if (_inv[seat][w.Value] > 0) _inv[seat][w.Value]--;
        _aimA[seat] = a.Value;
        _aimP[seat] = p.Value;
        _aimW[seat] = w.Value;
        core.Fire(seat, a.Value, p.Value, w.Value);
        _phase = PhaseFly;
        _left = 0;
        _stats[seat].Shots++;
        _skips[seat] = 0;
        _idle = 0;
        _shotThisRound = true;
        _pendView = true;
        return ActResult.Done;
    }

    ActResult Move(int seat, JsonElement payload)
    {
        if (Gate(seat) is { } no) return ActResult.Fail(no);
        int? dir = payload.ValueKind == JsonValueKind.Number && payload.TryGetInt32(out var bare) ? bare : Int(payload, "dir");
        if (dir is not (-1 or 1)) return ActResult.Fail("Такого напрямку нема");
        var core = _core!;
        var hp = core.Huts[seat].Hp;
        if (core.Move(seat, dir.Value) is { } why) return ActResult.Fail(why);

        Wake();
        _pendMoved |= 1 << seat;
        var hut = core.Huts[seat];
        if (hut.Hp != hp) _pendHp = true;
        if (core.ShotFall[seat] > 0)
            Note(hut.Alive ? $"{Name(seat)}: хата впала у вирву −{core.ShotFall[seat]}" : $"{Name(seat)}: хата впала у вирву — руїна");
        if (!hut.Alive && hut.Reason == "drown") Note($"{Name(seat)}: хату затопило");
        if (!hut.Alive)
        {
            // на полі в паузі — саме цей рядок, а не минулий постріл
            var drowned = hut.Reason == "drown";
            _last = new
            {
                by = seat,
                w = -1,
                hits = new object[] { new { seat, dmg = core.ShotFall[seat], kind = drowned ? "drown" : "fall" } },
                text = _log[^1],
            };
            _pendView = true;
            _phase = PhaseSettle;
            _left = SettleSkip;
        }
        else if (core.ShotFall[seat] > 0) _pendView = true;   // журнал помінявся — хай бачать
        return ActResult.Done;
    }

    ActResult Skip(int seat)
    {
        if (Gate(seat) is { } no) return ActResult.Fail(no);
        _skips[seat] = 0;
        var text = $"{Name(seat)}: хід пропущено";
        Note(text);
        _last = new { by = seat, w = -1, hits = Array.Empty<object>(), text };
        _phase = PhaseSettle;
        _left = SettleSkip;
        _pendView = true;
        return ActResult.Accept("Хід пропущено");
    }

    /// <summary>Косметичний приціл того, хто ходить: лише для кадру, правил не міняє.</summary>
    ActResult Aim(int seat, JsonElement payload)
    {
        if (_phase != PhaseAim || seat != _turn || _core is null || !_core.Huts[seat].Alive) return ActResult.Fail("Зараз не твій хід");
        var a = Int(payload, "a");
        var p = Int(payload, "p");
        var w = Int(payload, "w");
        if (a is null or < 0 or > 180 || p is null or < 5 or > 100 || w is null or < 0 or >= Kinds) return ActResult.Fail("Такого прицілу нема");
        Wake();
        if (_aimA[seat] == a && _aimP[seat] == p && _aimW[seat] == w) return ActResult.Done;
        _aimA[seat] = a.Value;
        _aimP[seat] = p.Value;
        _aimW[seat] = w.Value;
        _pendAim = true;
        return ActResult.Done;
    }

    /// <summary>Сонний хід, а людина ворухнулась — віддаємо повний; новий endsAt летить видом, дуга в усіх подовжиться.</summary>
    void Wake()
    {
        if (_phase != PhaseAim || _turnLen >= _turnSecs) return;
        _turnLen = _turnSecs;
        _deadline = _turnAt.AddSeconds(_turnSecs);
        _pendView = true;
    }

    // =============================================================================================
    // Тик
    // =============================================================================================

    public override TickResult Tick()
    {
        if (_core is null || _phase is PhaseOver or PhaseLobby) return TickResult.None;
        var core = _core;
        _t++;
        core.ClearMarks();
        core.MovedMask |= _pendMoved;
        core.HpChanged |= _pendHp;
        _aimMark = _pendAim;
        _wlMark = false;
        var view = _pendView;
        _pendMoved = 0;
        _pendHp = _pendAim = _pendView = false;
        var frame = _aimMark || core.MovedMask != 0 || core.HpChanged;

        switch (_phase)
        {
            case PhaseStart:
                if (_left == StartTicks) view = true;
                frame = true;
                if (--_left <= 0)
                {
                    NextTurn();
                    view = true;
                }
                break;
            case PhaseAim:
                --_left;
                if (Ctx.Clock.UtcNow >= _deadline)
                {
                    TimeOut();
                    view = frame = true;
                }
                break;
            case PhaseFly:
                core.Step();
                _left++;
                frame = true;
                if (core.LiveShells == 0 || _left >= FlyTicks)
                {
                    EndFlight();
                    view = true;
                }
                break;
            case PhaseSettle:
                if (--_left <= 0)
                {
                    if (!CheckEnd()) NextTurn();
                    view = frame = true;
                }
                break;
        }
        if (_phase == PhaseOver) return TickResult.Both;
        return view ? TickResult.Both : frame ? TickResult.FrameOnly : TickResult.None;
    }

    /// <summary>
    /// Наступний хід (spec §2.3): перше живе місце з більшим номером, а як нема — найменше живе, і тоді нове
    /// коло (порожні кола, вода, страховка). На вході в хід — отрута; хрін може й добити, тоді хід іде далі.
    /// </summary>
    void NextTurn()
    {
        var core = _core!;
        for (var guard = 0; guard < 4 * Seats; guard++)
        {
            if (_phase == PhaseOver) return;
            if (core.AliveCount == 0) { CheckEnd(); return; }
            var s = NextAlive(_cursor);
            if (s < 0)
            {
                if (_round > 0)
                {
                    _idle = _shotThisRound ? 0 : _idle + 1;
                    if (_idle >= IdleLimit)
                    {
                        Over([], "idle", $"{Info.Title}: так ніхто й не стрельнув — розійшлись");
                        return;
                    }
                }
                _shotThisRound = false;
                _round++;
                _cursor = -1;
                if (_round > MaxRounds)
                {
                    Over([], "draw", $"{Info.Title}: село стоїть, порох скінчився — нічия");
                    return;
                }
                if (_waterFrom > 0 && _round >= _waterFrom && core.Water < GlekometCore.WaterTop)
                {
                    core.RaiseWater(GlekometCore.WaterRiseFor(core.AliveCount));
                    _wlMark = true;
                    Note("Вода піднялась");
                    for (var i = 0; i < Seats; i++)
                        if ((core.ShotDied & (1 << i)) != 0) Note($"{Name(i)}: хату затопило");
                    if (CheckEnd()) return;
                }
                continue;
            }
            _cursor = s;
            var hut = core.Huts[s];
            if (hut.Poison > 0)
            {
                var dmg = Math.Min(GlekometCore.PoisonDmg, hut.Hp);
                hut.Hp -= dmg;
                hut.Poison--;
                core.HpChanged = true;
                Note($"Хрін дошкуляє: {Name(s)} −{dmg}");
                var by = hut.PoisonBy;
                if (by >= 0 && by != s && !Ally(by, s)) _stats[by].Dmg += dmg;
                if (hut.Hp <= 0)
                {
                    core.Kill(s, "poison");
                    Note($"{Name(s)}: хрін добив хату");
                    if (by >= 0 && by != s && !Ally(by, s)) _stats[by].Kills++;
                    if (CheckEnd()) return;
                    continue;
                }
            }
            core.Wind = Ctx.Rng.Next(11) - 5;
            _turn = s;
            _turnNo++;
            _phase = PhaseAim;
            _turnLen = _skips[s] > 0 ? Math.Min(SleepySecs, _turnSecs) : _turnSecs;
            _left = _turnLen * 1000 / TickMs;
            _turnAt = Ctx.Clock.UtcNow;
            _deadline = _turnAt.AddSeconds(_turnLen);
            return;
        }
        // сюди звичайна гра не доходить; хай краще буде нічия, ніж зависла партія
        Over([], "draw", $"{Info.Title}: усі хати в руїнах — нічия");
    }

    int NextAlive(int after)
    {
        for (var s = after + 1; s < Seats; s++)
            if (_core!.Huts[s].Alive) return s;
        return -1;
    }

    bool Ally(int a, int b) => _teams && a != b && a % 2 == b % 2;

    /// <summary>Час вийшов: хід пропущено, а третій пропуск поспіль — хата вибуває.</summary>
    void TimeOut()
    {
        var s = _turn;
        _skips[s]++;
        var text = $"{Name(s)}: хід прогавлено";
        Note(text);
        if (_skips[s] >= SkipLimit)
        {
            // Третє коло поспіль без жодного пострілу — нічия (§2.3), навіть коли саме цей пропуск вибив би
            // передостанню хату. Інакше, коли спить увесь стіл, перемога й «Ні подряпини» діставались хаті з
            // більшим номером, яка теж не стрельнула жодного разу: її третій пропуск просто не встигав настати.
            if (!_shotThisRound && _idle + 1 >= IdleLimit && DecidedWithout(s))
            {
                Over([], "idle", $"{Info.Title}: так ніхто й не стрельнув — розійшлись");
                return;
            }
            _core!.Kill(s, "afk");
            text = $"{Name(s)}: хата заснула — вибула";
            Note(text);
        }
        _last = new { by = s, w = -1, hits = Array.Empty<object>(), text };
        _phase = PhaseSettle;
        _left = SettleSkip;
    }

    /// <summary>Усі снаряди лягли: статистика, найкращий постріл, журнал, «Далекобійник» — і пауза на показ.</summary>
    void EndFlight()
    {
        var core = _core!;
        // Страховка: снаряди, що пережили FlyTicks, гасимо (ядро й так гасить їх на восьмій секунді).
        for (var i = 0; i < GlekometCore.MaxShells; i++) core.Shells[i].Alive = false;
        var by = core.ShotBy;
        var w = core.ShotW;
        var st = _stats[by];
        var hits = new List<object>();
        int total = 0, top = -1, topDmg = 0;
        for (var t = 0; t < Seats; t++)
        {
            var dmg = core.ShotDmg[t];
            if (dmg <= 0) continue;
            hits.Add(new { seat = t, dmg, kind = "hit" });
            if (t == by || Ally(by, t)) { st.Self += dmg; continue; }
            st.Dmg += dmg;
            total += dmg;
            if (dmg > topDmg) { topDmg = dmg; top = t; }
        }
        // Влучання — постріл, що зачепив хоч одну чужу хату: розсипний у трьох — одне влучання, а не три,
        // інакше в підсумку виходило «4 влучання з 3 пострілів».
        if (total > 0) st.Hits++;
        for (var t = 0; t < Seats; t++)
        {
            if (core.ShotFall[t] > 0) hits.Add(new { seat = t, dmg = core.ShotFall[t], kind = "fall" });
            if ((core.ShotPoisoned & (1 << t)) != 0) hits.Add(new { seat = t, dmg = 0, kind = "poison" });
            if ((core.ShotDied & (1 << t)) != 0 && core.Huts[t].Reason == "drown") hits.Add(new { seat = t, dmg = 0, kind = "drown" });
        }
        if (core.ShotSplash > 0 && core.ShotBooms == 0 && core.ShotHay == 0 && core.ShotStork == 0)
            hits.Add(new { seat = by, dmg = 0, kind = "splash" });
        st.Best = Math.Max(st.Best, total);
        if (total > _bestDmg)
        {
            _bestDmg = total;
            _bestSeat = by;
            _bestW = w;
            _bestTo = top;
        }

        // «Далекобійник»: пряме в чужу хату з пів села.
        for (var t = 0; t < Seats; t++)
        {
            if ((core.ShotDirect & (1 << t)) == 0 || t == by || Ally(by, t)) continue;
            if (Math.Abs(core.ShotFromX - core.Huts[t].X) < SniperGap || (_sniperGiven & (1 << by)) != 0) continue;
            _sniperGiven |= 1 << by;
            Ctx.Award(by, 0, "ach:glekomet-sniper");
        }

        var text = ShotText(by, w);
        Note(text);
        for (var t = 0; t < Seats; t++)
        {
            if (core.ShotFall[t] > 0 && core.Huts[t].Reason != "fall")
                Note($"{Name(t)}: хата впала у вирву −{core.ShotFall[t]}");
            if ((core.ShotDied & (1 << t)) == 0) continue;
            switch (core.Huts[t].Reason)
            {
                case "hit":
                    Note(t == by ? $"{Name(t)}: хата в руїнах (сама себе)" : $"{Name(t)}: хата в руїнах ({Name(by)})");
                    if (t != by && !Ally(by, t)) st.Kills++;
                    break;
                case "fall": Note($"{Name(t)}: хата впала у вирву — руїна"); break;
                case "drown": Note($"{Name(t)}: хату затопило"); break;
            }
        }
        _last = new { by, w, hits = hits.ToArray(), text };
        _phase = PhaseSettle;
        _left = SettleShot;
    }

    /// <summary>Рядок про постріл без дієслів із ніком-підметом («Оля → Петро: −35», «Оля: мимо»).</summary>
    string ShotText(int by, int w)
    {
        var core = _core!;
        var me = Name(by);
        var lower = w switch
        {
            GlekometCore.Pot => "глек",
            GlekometCore.Shards => "розсипний",
            GlekometCore.Varenyk => "вареник",
            GlekometCore.Hay => "копа",
            GlekometCore.Stork => "лелека",
            _ => "хрін",
        };
        if (w == GlekometCore.Hay && core.ShotHay > 0) return $"{me}: копа сіна виросла";
        if (w == GlekometCore.Stork)
        {
            if (core.ShotStork > 0) return $"Лелека несе хату: {me}";
            if (core.ShotSplash > 0) return $"{me}: лелека на воду не сідає";
            if (core.ShotOut > 0) return $"{me}: лелека — у сусіднє село";
            if (!core.Huts[by].Alive) return $"{me}: лелеці нема кого нести";   // устав з-за столу посеред польоту
            return $"{me}: лелеці ніде сісти";
        }

        var parts = new List<string>();
        int targets = 0, single = -1, selfOnly = 1;
        for (var t = 0; t < Seats; t++)
        {
            if (core.ShotDmg[t] <= 0) continue;
            targets++;
            single = t;
            if (t != by) selfOnly = 0;
            parts.Add($"{Name(t)} −{core.ShotDmg[t]}");
        }
        string text;
        if (targets == 1 && w == GlekometCore.Pot && single != by) text = $"{me} → {Name(single)}: −{core.ShotDmg[single]}";
        else if (targets > 0) text = $"{me}: {lower} → {string.Join(", ", parts)}" + (selfOnly == 1 ? " (у свою хату!)" : "");
        else if (w == GlekometCore.Khrin && core.ShotPoisoned != 0) text = $"{me}: хрін → {Poisoned()} отруєно";
        else if (core.ShotBooms > 0) text = me + Vary(Misses, by).Replace("{w}", lower);
        else if (core.ShotSplash > 0) text = me + Vary(Splashes, by);
        else if (core.ShotOut > 0) text = me + Vary(Outs, by).Replace("{w}", lower);
        else if (core.ShotCloud > 0) text = $"{me}: {lower} — у хмари";
        else text = $"{me}: мимо";
        if (w == GlekometCore.Khrin && targets > 0 && core.ShotPoisoned != 0) text += ", отрута";
        return Clip(text);
    }

    // Десятки однакових «Оля: мимо» за партію нудні — кілька варіантів на подію. Вибір — від номера ходу, а не з
    // Ctx.Rng: так вітер і мапа «Ще раз» за тим самим сідом лишаються тими самими. Снаряди тут усі чоловічого
    // роду (глек, розсипний, вареник, хрін), тож «пішов» пасує; про стрільця — без дієслів, роду ніка ми не знаємо.
    static readonly string[] Misses =
    [
        ": мимо", ": мимо — курка вціліла", ": {w} пішов по гриби", ": мимо, зате город переорано", ": мимо — сусіди й не помітили",
    ];
    static readonly string[] Splashes = [": бризки", ": бризки на пів ставка", ": плюсь — жаби в захваті"];
    static readonly string[] Outs = [": {w} — у сусіднє село", ": {w} — аж за обрій", ": {w} — сусідам на гостинець"];

    string Vary(string[] pool, int by) => pool[(_turnNo + by) % pool.Length];

    string Poisoned()
    {
        var names = new List<string>();
        for (var t = 0; t < Seats; t++)
            if ((_core!.ShotPoisoned & (1 << t)) != 0) names.Add(Name(t));
        return string.Join(", ", names);
    }

    static string Clip(string text) => text.Length <= 60 ? text : text[..59] + "…";

    /// <summary>Нік для рядків на полі: довгий обрізаємо, щоб рядок журналу лишався рядком.</summary>
    string Name(int seat)
    {
        var n = _nick[seat] ?? "";
        if (n.Length == 0) n = SeatName(seat);
        return n.Length <= NickMax ? n : n[..(NickMax - 1)] + "…";
    }

    void Note(string text)
    {
        _log.Add(Clip(text));
        if (_log.Count > LogLines) _log.RemoveAt(0);
    }

    // =============================================================================================
    // Кінець партії
    // =============================================================================================

    /// <summary>Лишилась одна хата (одна команда) — перемога; нікого — нічия. true — партію закінчено.</summary>
    bool CheckEnd()
    {
        if (_phase == PhaseOver) return true;
        var core = _core!;
        if (_teams)
        {
            var mask = 0;
            for (var s = 0; s < Seats; s++) if (core.Huts[s].Alive) mask |= 1 << (s % 2);
            if (mask == 0) { Over([], "draw", $"{Info.Title}: усі хати в руїнах — нічия"); return true; }
            if (mask is 1 or 2)
            {
                var team = mask == 1 ? 0 : 1;
                var winners = new List<int>();
                for (var s = 0; s < Seats; s++) if (s % 2 == team && core.Huts[s].Plays && Ctx.Seated(s)) winners.Add(s);
                Win([.. winners], "team");
                return true;
            }
            return false;
        }
        var alive = core.AliveCount;
        if (alive == 0) { Over([], "draw", $"{Info.Title}: усі хати в руїнах — нічия"); return true; }
        if (alive == 1)
        {
            for (var s = 0; s < Seats; s++)
                if (core.Huts[s].Alive) { Win([s], "last"); return true; }
        }
        return false;
    }

    /// <summary>Чи вирішиться партія, якщо хата <paramref name="seat"/> вибуде: лишиться ≤ 1 хата (≤ 1 команда).</summary>
    bool DecidedWithout(int seat)
    {
        var core = _core!;
        int alive = 0, mask = 0;
        for (var s = 0; s < Seats; s++)
            if (s != seat && core.Huts[s].Alive) { alive++; mask |= 1 << (s % 2); }
        return _teams ? mask != 3 : alive <= 1;
    }

    /// <summary>
    /// Чи був бій: хоч одну хату суперників розбито, повалено, затоплено чи добито хроном — а не лише «заснула»
    /// чи «встав з-за столу». Без цього «Ні подряпини» діставалась би за стіл, де суперник просто спав.
    /// </summary>
    bool Fought(int[] winners)
    {
        var core = _core!;
        for (var s = 0; s < Seats; s++)
            if (core.Huts[s].Plays && Array.IndexOf(winners, s) < 0 && core.Huts[s].Reason is "hit" or "fall" or "drown" or "poison") return true;
        return false;
    }

    /// <summary>«Глекомети: Оля — остання хата в селі · Петро, Ганна — руїни»: останній вибулий — першим після переможця.</summary>
    void Win(int[] winners, string reason)
    {
        var core = _core!;
        var rest = new List<string>();
        for (var i = core.OutCount - 1; i >= 0; i--)
        {
            var s = core.OutOrder[i];
            if (Array.IndexOf(winners, s) < 0 && _nick[s].Length > 0) rest.Add(_nick[s]);
        }
        var who = string.Join(" + ", winners.Select(s => _nick[s]));
        var text = $"{Info.Title}: {who} — " + (winners.Length > 1 ? "останні хати в селі" : "остання хата в селі");
        if (rest.Count > 0) text += $" · {string.Join(", ", rest)} — руїни";
        if (winners.Length == 0) text = $"{Info.Title}: усі хати в руїнах — нічия";
        Over(winners, winners.Length == 0 ? "draw" : reason, text);
    }

    void Over(int[] winners, string reason, string text)
    {
        if (_phase == PhaseOver) return;
        var core = _core!;
        _phase = PhaseOver;
        _turn = -1;
        _left = 0;
        _result = new
        {
            winners,
            reason,
            best = _bestSeat < 0 ? null : new { seat = _bestSeat, dmg = _bestDmg, w = _bestW, to = _bestTo },
        };
        foreach (var s in winners)
            if (_nick[s].Length > 0) _wins[Key(_nick[s])] = _wins.GetValueOrDefault(Key(_nick[s])) + 1;
        // «Ні подряпини» — лише за виграний бій: хтось із суперників упав у бою (а не заснув чи встав), а сам
        // переможець хоч раз стрельнув
        if (reason is "last" or "team" && Fought(winners))
            foreach (var s in winners)
                if (core.Huts[s].Alive && core.Huts[s].Hp == 100 && _stats[s].Shots > 0) Ctx.Award(s, 0, "ach:glekomet-clean");
        var scores = new Dictionary<int, long>();
        for (var s = 0; s < Seats; s++)
            if (core.Huts[s].Plays && Ctx.Seated(s)) scores[s] = _stats[s].Dmg;
        _pendView = true;
        Ctx.Finish(winners, text, scores);
    }

    /// <summary>
    /// Встав посеред партії: хата — порожня руїна, партія йде далі; якщо за столом лишився один — партія його
    /// (техпоразка тому, хто пішов). Його хід — пауза й далі по колу.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (!_started || _core is null || _phase == PhaseOver) return;
        var core = _core;
        if (core.Huts[seat].Alive)
        {
            core.Kill(seat, "left");
            _pendHp = true;
        }
        var gone = $"{Name(seat)}: за столом нема, хата порожня";
        Note(gone);
        _pendView = true;
        var others = new List<int>();
        for (var s = 0; s < Seats; s++) if (s != seat && Ctx.Seated(s)) others.Add(s);
        if (others.Count <= 1)
        {
            Over([.. others], "left", $"{Info.Title}: {_nick[seat]} — за столом нема, партію не дограли");
            return;
        }
        if (CheckEnd()) return;
        if (_phase == PhaseAim && _turn == seat)
        {
            _last = new { by = seat, w = -1, hits = Array.Empty<object>(), text = gone };
            _phase = PhaseSettle;
            _left = SettleSkip;
        }
    }

    // =============================================================================================
    // Вид і кадр
    // =============================================================================================

    /// <summary>Село для лобі: стале (без Ctx.Rng — інакше партія за тим самим сідом не відтворилась би).</summary>
    GlekometCore Preview()
    {
        if (_preview is not null) return _preview;
        _preview = new GlekometCore(new Random(20260927));
        _preview.Generate([true, true, true, true, true, true]);
        return _preview;
    }

    public override object View(int? seat)
    {
        var lobby = !_started || _core is null;
        var core = lobby ? Preview() : _core!;
        var huts = new object[Seats];
        for (var s = 0; s < Seats; s++)
        {
            var h = core.Huts[s];
            var here = lobby ? Ctx.Seated(s) : h.Plays;
            huts[s] = new
            {
                seat = s,
                // нік того, чия хата: хто встав посеред партії, того каркас уже не назве, а руїна підписана
                nick = lobby ? Ctx.NickOf(s) ?? "" : here ? _nick[s] ?? "" : "",
                x = h.X,
                y = h.Y,
                hp = here ? (lobby ? 100 : h.Hp) : 0,
                alive = here && (lobby || h.Alive),
                team = s % 2,
                poison = lobby ? 0 : h.Poison,
                fuel = here ? (lobby ? GlekometCore.FuelMax : h.Fuel) : 0,
                skips = lobby ? 0 : _skips[s],
                reason = lobby || !here ? "" : h.Reason,
            };
        }
        var inv = new int[Seats][];
        for (var s = 0; s < Seats; s++) inv[s] = lobby ? [.. GlekometCore.Weapons.Select(w => Ctx.Seated(s) ? w.Stock : 0)] : (int[])_inv[s].Clone();
        var stats = new object[Seats];
        for (var s = 0; s < Seats; s++) stats[s] = _stats[s].Wire();
        var wins = new int[Seats];
        for (var s = 0; s < Seats; s++)
            if (Ctx.NickOf(s) is { } n) wins[s] = _wins.GetValueOrDefault(Key(n));
        var aiming = !lobby && _turn >= 0 && _phase is PhaseAim or PhaseFly or PhaseSettle;
        return new
        {
            phase = _phase,
            turn = aiming ? _turn : (int?)null,
            round = _round,
            endsAt = _phase == PhaseAim ? _deadline : (DateTimeOffset?)null,
            // довжина саме цього ходу: сонному — 10 с, щоб дуга в усіх показувала правду
            turnMs = (_phase == PhaseAim ? _turnLen : _turnSecs) * 1000,
            startIn = _phase == PhaseStart ? _left : 0,
            wind = lobby ? 0 : core.Wind,
            water = core.Water,
            waterFrom = _waterFrom,
            w = GlekometCore.W,
            hgt = GlekometCore.Hgt,
            step = GlekometCore.ColW,
            h = (int[])core.H.Clone(),
            teams = lobby ? _teamsWanted : _teams,
            huts,
            inv,
            aim = aiming ? new[] { _aimA[_turn], _aimP[_turn], _aimW[_turn] } : null,
            shells = Shells(core),
            last = _last,
            log = _log.ToArray(),
            stats,
            wins,
            result = _result,
            turnNo = _turnNo,
            t = _t,
        };
    }

    static int[][] Shells(GlekometCore core)
    {
        var n = core.LiveShells;
        var list = new int[n][];
        var k = 0;
        for (var i = 0; i < GlekometCore.MaxShells && k < n; i++)
        {
            ref var s = ref core.Shells[i];
            if (!s.Alive) continue;
            list[k++] = [(int)Math.Round(s.X), (int)Math.Round(s.Y), s.Kind];
        }
        return list;
    }

    /// <summary>Кадр — лише те, що змінилось цього тика (spec §4.2); поля, яким нічого сказати, відсутні.</summary>
    public override object? Frame()
    {
        if (_core is null) return null;
        var core = _core;
        var f = new Dictionary<string, object?>(8, StringComparer.Ordinal)
        {
            ["t"] = _t,
            ["ph"] = _phase,
        };
        if (_phase == PhaseFly || (_phase == PhaseSettle && _left == SettleShot)) f["sh"] = Shells(core);
        if (core.MovedMask != 0)
        {
            var moved = new List<int[]>();
            for (var s = 0; s < Seats; s++)
                if ((core.MovedMask & (1 << s)) != 0) moved.Add([s, core.Huts[s].X, core.Huts[s].Y]);
            f["hx"] = moved;
        }
        if (core.ExCount > 0)
        {
            var ex = new object[core.ExCount];
            for (var i = 0; i < core.ExCount; i++) ex[i] = new object[] { core.ExX[i], core.ExY[i], core.ExR[i], GlekometCore.ExNames[core.ExK[i]] };
            f["ex"] = ex;
        }
        if (core.DirtyHi >= 0)
        {
            var runs = new List<int[]>();
            var c = core.DirtyLo;
            while (c <= core.DirtyHi)
            {
                if (!core.Dirty[c]) { c++; continue; }
                var end = c;
                while (end + 1 <= core.DirtyHi && core.Dirty[end + 1]) end++;
                var run = new int[end - c + 2];
                run[0] = c;
                for (var k = c; k <= end; k++) run[k - c + 1] = core.H[k];
                runs.Add(run);
                c = end + 1;
            }
            f["dh"] = runs;
        }
        if (core.HpChanged)
        {
            var hp = new int[Seats];
            for (var s = 0; s < Seats; s++) hp[s] = core.Huts[s].Plays ? core.Huts[s].Hp : 0;
            f["hp"] = hp;
        }
        if (_aimMark && _turn >= 0) f["aim"] = new[] { _aimA[_turn], _aimP[_turn], _aimW[_turn] };
        if (_phase == PhaseStart) f["si"] = _left;
        if (_wlMark) f["wl"] = core.Water;
        return f;
    }
}

/// <summary>Статистика місця за партію: постріли, влучні постріли (зачепили чужу хату), шкода чужим, руїни, по своїх, найкращий постріл.</summary>
public sealed class GlekometStat
{
    public int Shots, Hits, Dmg, Kills, Self, Best;

    public void Clear() => Shots = Hits = Dmg = Kills = Self = Best = 0;

    public object Wire() => new { shots = Shots, hits = Hits, dmg = Dmg, kills = Kills, self = Self, best = Best };
}
