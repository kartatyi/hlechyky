using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Куди бахне» (за мотивами Mortar Mayhem): пам'ять у ритмі на 1–8. Дядько Глек на даху показує стрілки, потім
/// «Пішли!» — і на кожен такт гравець ступає один крок у своєму дворі 3×3. Після такту Глек кидає горщики з жаром
/// на всі плитки, крім правильної: хто не там — мінус серце. Останній живий бере партію. Spec:
/// <c>docs/games/specs/bakhne.md</c> (там вид і кадр для клієнта до поля).
/// </summary>
public sealed class Bakhne : Game, IPartyMinigame
{
    public const int Seats = 8;
    public const int TickMs = 50;
    /// <summary>Двір — 3×3, старт і кожен новий раунд — у центрі.</summary>
    public const int Size = 3, Center = 1;

    /// <summary>Фази (<c>ph</c> у кадрі): готуйсь, Глек показує, «Пішли!», такти, кінець раунду, партію зіграно, лобі.</summary>
    public const int PhReady = 0, PhShow = 1, PhGo = 2, PhSteps = 3, PhEnd = 4, PhOver = 5, PhLobby = 6;
    /// <summary>Підступи раунду (<c>trick</c>): нема, «Глек напився» (усе навпаки), червоні стрілки, швидкий показ.</summary>
    public const int TrickNone = 0, TrickDrunk = 1, TrickRed = 2, TrickFast = 3;
    /// <summary>Події кадру (<c>ev</c>: [id, kind, a, b]).</summary>
    public const int EvArrow = 1, EvGo = 2, EvBang = 3, EvHit = 4, EvOut = 5, EvRound = 6, EvOver = 7, EvStep = 8, EvFence = 9;

    public const int Hearts = 2, PartyHearts = 1;
    public const int FirstLen = 3, PartyFirstLen = 4, MaxLen = 10;
    public const int RoundsMax = 12, PartyRounds = 6;
    /// <summary>Підступи — з четвертого раунду, з такою ймовірністю на раунд.</summary>
    public const int TrickFrom = 4;
    public const double TrickChance = 0.4;
    /// <summary>
    /// Запас на джитер: натиск, що прийшов до <c>кінця такту + Grace</c> тиків і такт ще без кроку, — зараховується
    /// тому такту. Тому й бахає такт не на межі, а на Grace тиків пізніше (150 мс).
    /// </summary>
    public const int Grace = 3;
    /// <summary>Готуйсь перед першим раундом (2 с; у вечірці 1,5 с — там уже була картка правил), пауза після раунду 2 с.</summary>
    public const int ReadyTicks = 40, PartyReadyTicks = 30, EndTicks = 40;
    /// <summary>Вечірка: новий раунд після 75 с уже не починаємо, а на 85 с кінчаємо посеред раунду (стеля хоста — 90 с).</summary>
    public const int PartyStopMs = 75_000, PartyHardMs = 85_000;
    /// <summary>Скільки тиків кадр тримає подію (хост вечірки може «склеїти» два тики в один кадр).</summary>
    const int EvKeep = 10;

    static readonly string[] Names = ["синій", "рудий", "зелений", "жовтий", "бузковий", "м’ятний", "рожевий", "сірий"];
    static readonly int[] Dx = [1, 0, -1, 0], Dy = [0, 1, 0, -1];   // 0 → , 1 ↓, 2 ←, 3 ↑ (як HGames.ui.dpad)
    static readonly JsonElement[] DirEl = [.. Enumerable.Range(0, 4).Select(d => JsonSerializer.SerializeToElement(new { d }))];

    public override GameInfo Info { get; } = new(
        "bakhne", "Куди бахне", "«Куди бахне»", GameGroup.Live, 1, Seats, TickMs: TickMs,
        Start: StartMode.ByHost, Options: [LiveBots.LevelOption],
        Hint: "Глек показує стрілки — запам'ятай і ступай у такт: бахне по всіх плитках, крім правильної. Два серця, виграє останній живий. Самому — з 🤖 ботами");

    public string Howto => "Запам'ятай стрілки Глека й на кожен такт ступи один крок; червона стрілка й «Глек напився» — навпаки. "
        + "Стрілки/WASD, на телефоні — хрестовина";
    public int PartyCapMs => 90_000;
    public int PartyMin => 2;
    public int PartyMax => Seats;

    /// <summary>Скільки ботів, коли людина сама: двоє — «останній живий» на двох був би дуеллю, а втрьох є драма.</summary>
    public const int SoloBots = 2;

    sealed class Yard
    {
        public bool Plays, Alive;
        public int Hearts, X = Center, Y = Center, OutRound, OutBeat;
        /// <summary>Крок кожного такту раунду (−1 — не ступав).</summary>
        public readonly int[] Slots = new int[MaxLen];
        public bool LostThisRound, LostAny, HitNow;
        /// <summary>Бот: для якого такту вирішено, на якому тику ступить і чи помилиться (0 — ні, 1 — не туди, 2 — завис).</summary>
        public int BotBeat = -1, BotAt, BotErr;
    }

    readonly Yard[] _y = [.. Enumerable.Range(0, Seats).Select(_ => new Yard())];
    readonly SoloBot _solo = new();
    PartyMode? _party;
    int[] _bots = [];
    bool _botGame, _started;
    int _startPlayers;
    string[] _startNicks = [];
    readonly Series _series = new();
    LiveBots.Level _level = LiveBots.Level.Normal;

    int _t, _ph = PhReady, _left, _round, _len, _trick;
    /// <summary>Правильні кроки раунду, показані стрілки й червоні (показана ≠ правильна).</summary>
    readonly int[] _need = new int[MaxLen], _shown = new int[MaxLen];
    readonly bool[] _red = new bool[MaxLen];
    int _arrowTicks, _beatTicks, _shownCount, _phaseAt, _resolved;
    (int X, int Y)? _safe;
    int[] _winners = [];
    DateTimeOffset _startAt;
    bool _drunkSaid, _greeted;
    readonly List<int[]> _ev = [];
    int _evId;
    bool _dirty;

    public bool Party => _party is not null;
    public IReadOnlyList<int> Bots => _bots;
    public bool BotGame => _botGame;
    public int Phase => _ph;
    public int RoundNo => _round;
    public int Len => _len;
    public int Trick => _trick;
    public int T => _t;
    public int BeatTicks => _beatTicks;
    public int ArrowTicks => _arrowTicks;
    public int StartHearts => _party is null ? Hearts : PartyHearts;
    public IReadOnlyList<int> Need => _need[.._len];
    public IReadOnlyList<int> Shown => _shown[.._len];
    public int HeartsOf(int seat) => _y[seat].Hearts;
    public bool AliveOf(int seat) => _y[seat].Plays && _y[seat].Alive;
    public bool PlaysOf(int seat) => _y[seat].Plays;
    public (int X, int Y) PosOf(int seat) => Live(seat);
    public int OutRoundOf(int seat) => _y[seat].OutRound;
    /// <summary>Поточний такт (−1 — ще не йдемо) і скільки тактів уже бахнуло.</summary>
    public int Beat => _ph == PhSteps ? Math.Min(_len - 1, (_t - _phaseAt) / _beatTicks) : -1;
    public int Resolved => _resolved;
    public int[] Winners => [.. _winners];

    // ---------- стіл ----------

    bool Lobby => !_started || (_ph == PhOver && Enumerable.Range(0, Seats)
        .Any(s => Ctx.Seated(s) && !_startNicks.Contains(Ctx.NickOf(s) ?? "", StringComparer.OrdinalIgnoreCase)));

    int[] BotSeats() => _solo.Active(Ctx, Seats) ? [.. Enumerable.Range(0, Seats).Where(s => !Ctx.Seated(s)).Take(SoloBots)] : [];

    bool IsBot(int seat) => Array.IndexOf(_bots, seat) >= 0 && !Ctx.Seated(seat);

    public override string SeatName(int seat) => seat is >= 0 and < Seats ? Names[seat] : base.SeatName(seat);

    public override string? SeatBot(int seat) =>
        !Ctx.Seated(seat) && Array.IndexOf(_started ? _bots : BotSeats(), seat) >= 0 ? $"{LiveBots.Name} {SeatName(seat)}" : null;

    string Name(int seat) => SeatBot(seat) ?? Ctx.NickOf(seat) ?? SeatName(seat);

    public override bool ActsInLobby => true;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _solo.Configure(options);
        _party = PartyMode.Read(options);
    }

    public override string? CanStart() => _solo.CanStart(Ctx, Seats);

    public override void Start()
    {
        _started = true;
        _bots = _party is { } pm ? [.. pm.Bots.Where(s => s is >= 0 and < Seats && !Ctx.Seated(s)).Distinct()] : BotSeats();
        _botGame = _bots.Length > 0;
        _level = _party?.Level ?? _solo.Level;
        _startNicks = [.. Enumerable.Range(0, Seats).Where(Ctx.Seated).Select(s => Ctx.NickOf(s) ?? "")];
        _startPlayers = Enumerable.Range(0, Seats).Count(Ctx.Seated);
        _startAt = Ctx.Clock.UtcNow;
        _series.Begin(Ctx, Seats);
        for (var s = 0; s < Seats; s++)
        {
            var y = _y[s];
            y.Plays = (Ctx.Seated(s) || Array.IndexOf(_bots, s) >= 0) && (_party is null || s < Ctx.Players);
            y.Alive = y.Plays;
            y.Hearts = y.Plays ? StartHearts : 0;
            y.X = y.Y = Center;
            y.OutRound = y.OutBeat = 0;
            y.LostAny = y.LostThisRound = y.HitNow = false;
            y.BotBeat = -1;
            Array.Fill(y.Slots, -1);
        }
        _t = 0;
        _round = 0;
        _len = 0;
        _trick = TrickNone;
        _resolved = 0;
        _safe = null;
        _winners = [];
        _drunkSaid = false;
        _ev.Clear();
        _ph = PhReady;
        _left = _party is null ? ReadyTicks : PartyReadyTicks;
        _phaseAt = 0;
        if (_party is null && !_greeted && (_greeted = true))
            Ctx.Say("Хто пам'ятає, куди ступати, — той і їсть борщ. А хто ні — тому горщик на маківку 🏺");
    }

    // ---------- раунд ----------

    void NewRound()
    {
        _round++;
        _len = Math.Min(MaxLen, (_party is null ? FirstLen : PartyFirstLen) + _round - 1);
        _trick = _round >= TrickFrom && Ctx.Rng.NextDouble() < TrickChance ? 1 + Ctx.Rng.Next(3) : TrickNone;
        // Правильні кроки — випадкове блукання від центру, що не виходить за двір і не вертається одразу назад
        // (туди-сюди — нудно), хіба що інакше нікуди.
        int x = Center, y = Center, prev = -1;
        Span<int> opts = stackalloc int[4];
        for (var i = 0; i < _len; i++)
        {
            var n = 0;
            for (var d = 0; d < 4; d++)
                if (In(x + Dx[d], y + Dy[d]) && (prev < 0 || d != (prev + 2) % 4)) opts[n++] = d;
            if (n == 0) for (var d = 0; d < 4; d++) if (In(x + Dx[d], y + Dy[d])) opts[n++] = d;
            var pick = opts[Ctx.Rng.Next(n)];
            _need[i] = pick;
            x += Dx[pick];
            y += Dy[pick];
            prev = pick;
            _red[i] = false;
        }
        if (_trick == TrickRed)
        {
            var reds = Math.Max(1, _len / 3);
            for (var k = 0; k < reds; k++) _red[Ctx.Rng.Next(_len)] = true;
        }
        for (var i = 0; i < _len; i++)
            _shown[i] = _trick == TrickDrunk || _red[i] ? (_need[i] + 2) % 4 : _need[i];
        var showMs = Math.Max(350, 600 - 25 * (_round - 1));
        if (_trick == TrickFast) showMs = showMs * 6 / 10;
        _arrowTicks = Math.Max(5, (showMs + TickMs / 2) / TickMs);
        _beatTicks = Math.Max(12, (Math.Max(600, 900 - 30 * (_round - 1)) + TickMs / 2) / TickMs);
        _resolved = 0;
        _shownCount = 0;
        _safe = null;
        foreach (var yd in _y)
        {
            yd.X = yd.Y = Center;
            yd.LostThisRound = false;
            yd.HitNow = false;
            yd.BotBeat = -1;
            Array.Fill(yd.Slots, -1);
        }
        Event(EvRound, _round, _trick);
        if (_trick == TrickDrunk && !_drunkSaid)
        {
            _drunkSaid = true;
            Ctx.Say("Ой, щось мене хитає… Сьогодні все навпаки, дітки! 🍶");
        }
        Enter(PhShow, _len * _arrowTicks);
    }

    static bool In(int x, int y) => x is >= 0 and < Size && y is >= 0 and < Size;

    void Enter(int ph, int left)
    {
        _ph = ph;
        _left = left;
        _phaseAt = _t;
    }

    /// <summary>Подія летить у найближчому ж кадрі: стрілка й бах мусять прийти в такт, а не з чергового «раз на 200 мс».</summary>
    void Event(int kind, int a = 0, int b = 0)
    {
        _ev.Add([++_evId, kind, a, b, _t]);
        _dirty = true;
    }

    // ---------- ввід ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == LiveBots.Toggle)
            return _started && _ph != PhOver ? ActResult.Fail("Партія вже йде") : _solo.Switch(Ctx, seat, payload, Seats);
        if (!_started) return ActResult.Fail("Чекаємо на гравців");
        if (action != "step") return ActResult.Fail("Тут так не ходять");
        if (seat is < 0 or >= Seats || !_y[seat].Plays) return ActResult.Fail("Ти тут не граєш");
        if (_ph == PhOver) return ActResult.Fail("Партію вже зіграно");
        if (!_y[seat].Alive) return ActResult.Fail("Тебе вже накрило — дивись, як інші");
        if (Dir(payload) is not { } d || d is < 0 or > 3) return ActResult.Fail("Такого напрямку нема");
        if (SlotFor(seat, d) is not { } k) return ActResult.Fail(_ph == PhShow ? "Зачекай, Глек ще показує" : "Зараз не ступають");
        var y = _y[seat];
        if (y.Slots[k] >= 0) return ActResult.Fail("Один крок на такт");
        y.Slots[k] = d;
        // Паркан: крок за двір не вийде — такт з'їдено, стоїш, де стояв (і бахне, якщо треба було йти).
        var (x0, y0) = PosBefore(seat, k);
        Event(In(x0 + Dx[d], y0 + Dy[d]) ? EvStep : EvFence, seat, d);
        return ActResult.Done;
    }

    /// <summary>Напрямок приймаємо і як <c>{ d: 1 }</c>, і як голе <c>1</c>.</summary>
    static int? Dir(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetInt32(out var n) => n,
        JsonValueKind.Object when payload.TryGetProperty("d", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out var n) => n,
        _ => null,
    };

    /// <summary>
    /// Якому такту ляже натиск напрямком d, що прийшов зараз: на «Пішли!» — лише в останні Grace тиків (поспішив на
    /// дрібку — не біда), у тактах — такту, що йде. Перші Grace тиків такту — вікно джитера: якщо попередній такт ще
    /// не бахнув, стоїть без кроку і цей крок веде саме туди, куди треба було тоді, — це запізнілий натиск минулого
    /// такту. Інакше — свіжий крок цього такту (хто пропустив такт і ступив рівно «в долю», не дістане двох горщиків).
    /// null — зараз не ступають.
    /// </summary>
    int? SlotFor(int seat, int d)
    {
        if (_ph == PhGo) return _left <= Grace ? 0 : null;
        if (_ph != PhSteps) return null;
        var into = _t - _phaseAt;
        var k = into / _beatTicks;
        if (k > 0 && k - 1 >= _resolved && into - k * _beatTicks < Grace && _y[seat].Slots[k - 1] < 0)
        {
            var (x, y) = PosBefore(seat, k - 1);
            if (k >= _len || (x + Dx[d], y + Dy[d]) == Target(k - 1)) return k - 1;
        }
        return k < _len ? k : null;
    }

    /// <summary>Позиція перед кроком такту k: зарахована (після останнього баху) плюс кроки тактів між ними.</summary>
    (int X, int Y) PosBefore(int seat, int k)
    {
        var y = _y[seat];
        int x = y.X, yy = y.Y;
        for (var i = _resolved; i < k; i++)
        {
            var d = y.Slots[i];
            if (d >= 0 && In(x + Dx[d], yy + Dy[d])) { x += Dx[d]; yy += Dy[d]; }
        }
        return (x, yy);
    }

    /// <summary>Де гравець стоїть зараз: зарахована позиція плюс уже зроблені, ще не бахнуті кроки.</summary>
    (int X, int Y) Live(int seat) => PosBefore(seat, Math.Min(_len, _resolved + 2));

    // ---------- тик ----------

    public override TickResult Tick()
    {
        if (!_started || _ph == PhOver) return TickResult.None;
        _t++;
        Prune();
        var view = false;
        switch (_ph)
        {
            case PhReady:
                if (--_left <= 0) { NewRound(); view = true; }
                break;
            case PhShow:
            {
                var i = (_t - _phaseAt - 1) / _arrowTicks;
                if (i < _len && i >= _shownCount) { _shownCount = i + 1; Event(EvArrow, i, _shown[i]); }
                if (--_left <= 0)
                {
                    _shownCount = _len;
                    Enter(PhGo, _beatTicks);
                    Event(EvGo);
                    view = true;
                }
                break;
            }
            case PhGo:
                if (--_left <= 0) { Enter(PhSteps, _len * _beatTicks + Grace); view = true; }
                break;
            case PhSteps:
                BotsThink();
                // Такт k бахає через Grace тиків після свого кінця — щоб запізнілий на джитер натиск устиг.
                while (_resolved < _len && _t - _phaseAt >= (_resolved + 1) * _beatTicks + Grace)
                {
                    view |= Bang(_resolved);
                    if (_ph == PhOver) return TickResult.Both;
                }
                if (_resolved >= _len) { Enter(PhEnd, EndTicks); view = true; RoundDone(); }
                break;
            case PhEnd:
                if (--_left <= 0) { view = true; AfterRound(); }
                break;
        }
        if (_ph == PhOver) return TickResult.Both;
        if (_party is not null && (Ctx.Clock.UtcNow - _startAt).TotalMilliseconds >= PartyHardMs) return PartyOver();
        var dirty = _dirty;
        _dirty = false;
        return view ? TickResult.Both : dirty || _t % 4 == 0 ? TickResult.FrameOnly : TickResult.None;
    }

    void Prune()
    {
        var n = 0;
        while (n < _ev.Count && _ev[n][4] < _t - EvKeep) n++;
        if (n > 0) _ev.RemoveRange(0, n);
    }

    /// <summary>Такт k: горщики на всі плитки, крім правильної. Хто не там — мінус серце й переносить на правильну.</summary>
    bool Bang(int k)
    {
        var (sx, sy) = Target(k);
        _safe = (sx, sy);
        Event(EvBang, k, sy * Size + sx);
        var any = false;
        var died = new List<int>();
        for (var s = 0; s < Seats; s++)
        {
            var y = _y[s];
            y.HitNow = false;
            if (!y.Plays || !y.Alive) continue;
            var d = y.Slots[k];
            if (d >= 0 && In(y.X + Dx[d], y.Y + Dy[d])) { y.X += Dx[d]; y.Y += Dy[d]; }
            if (y.X == sx && y.Y == sy) continue;
            // Влучило: серце геть, а самого Глек «переносить» на правильну плитку — щоб і далі було куди ступати.
            any = true;
            y.HitNow = y.LostThisRound = y.LostAny = true;
            y.Hearts--;
            y.X = sx;
            y.Y = sy;
            Event(EvHit, s, y.Hearts);
            if (y.Hearts > 0) continue;
            y.Alive = false;
            y.OutRound = _round;
            y.OutBeat = k;
            died.Add(s);
            Event(EvOut, s);
        }
        _resolved = k + 1;
        if (died.Count > 0) CheckLast(died);
        return any;
    }

    /// <summary>Правильна плитка такту k — однакова в усіх дворах: усі стартують з центру й ідуть тим самим шляхом.</summary>
    (int X, int Y) Target(int k)
    {
        int x = Center, y = Center;
        for (var i = 0; i <= k; i++) { x += Dx[_need[i]]; y += Dy[_need[i]]; }
        return (x, y);
    }

    /// <summary>Лишився один живий — його партія; нікого — поділена між тими, кого накрило цим тактом.</summary>
    void CheckLast(List<int> died)
    {
        var alive = Enumerable.Range(0, Seats).Where(s => _y[s].Plays && _y[s].Alive).ToArray();
        if (alive.Length > 1) return;
        if (_party is not null) { PartyOver(); return; }
        Over(alive.Length == 1 ? alive : [.. died]);
    }

    /// <summary>Раунд дограно: ачівки «Слон пам'ятає» (10 кроків чисто) і «Тверезий» (п'яний раунд без втрат).</summary>
    void RoundDone()
    {
        if (!AchOk) return;
        for (var s = 0; s < Seats; s++)
        {
            var y = _y[s];
            if (!y.Plays || !y.Alive || y.LostThisRound || !Ctx.Seated(s)) continue;
            if (_len >= MaxLen) Ctx.Award(s, 0, "ach:bakhne-elephant");
            if (_trick == TrickDrunk) Ctx.Award(s, 0, "ach:bakhne-sober");
        }
    }

    /// <summary>Ачівки — лише в людській партії від двох і не у вечірці.</summary>
    bool AchOk => _party is null && !_botGame && _startPlayers >= 2;

    void AfterRound()
    {
        if (_party is not null)
        {
            if (_round >= PartyRounds || (Ctx.Clock.UtcNow - _startAt).TotalMilliseconds >= PartyStopMs) { PartyOver(); return; }
            NewRound();
            return;
        }
        if (_round >= RoundsMax)
        {
            var alive = Enumerable.Range(0, Seats).Where(s => _y[s].Plays && _y[s].Alive).ToArray();
            var best = alive.Length == 0 ? 0 : alive.Max(s => _y[s].Hearts);
            Over([.. alive.Where(s => _y[s].Hearts == best)]);
            return;
        }
        NewRound();
    }

    /// <summary>Хто ще грає партію: сидить за столом або бот цієї партії.</summary>
    int[] Playing() => [.. Enumerable.Range(0, Seats).Where(s => _y[s].Plays && (Ctx.Seated(s) || IsBot(s)))];

    long ScoreOf(int s) => _y[s].Alive ? _round + 1 : _y[s].OutRound;

    void Over(int[] winners)
    {
        _ph = PhOver;
        _winners = winners;
        Event(EvOver);
        var playing = Playing();
        if (AchOk)
            foreach (var w in winners)
                if (Ctx.Seated(w) && !_y[w].LostAny) Ctx.Award(w, 0, "ach:bakhne-clean");
        var scores = playing.Where(Ctx.Seated).ToDictionary(s => s, ScoreOf);
        var people = winners.Where(Ctx.Seated).ToArray();
        if (people.Length > 0)
            Ctx.Say(people.Length == 1 ? $"{Ctx.NickOf(people[0])} пам'ятає кожну плитку — борщ твій 🍲"
                : "Кілька голів пам'ятають — борщу на всіх вистачить 🍲");
        if (!_botGame)
        {
            _series.Record(Ctx, winners);
            Ctx.Finish(winners, Journal(winners, playing), scores);
            return;
        }
        var verdict = people.Length > 0 ? $"🏆 {Ctx.NickOf(people[0])} — перемога над {LiveBots.Of(_level)}и ботами"
            : winners.Length > 0 ? $"🤖 Пам'ять міцніша в {Name(winners[0])}" : null;
        Ctx.Finish(people, Journal(winners, playing), scores, verdict);
    }

    /// <summary>
    /// Scores вечірки: раунд вибування; хто ще живий — поточний раунд + 1 (рівні між собою). Більше = краще. Місця,
    /// що не грали, — −1.
    /// </summary>
    public IReadOnlyDictionary<int, long> PartyScores()
    {
        var r = new Dictionary<int, long>(Ctx.Players);
        for (var s = 0; s < Ctx.Players; s++)
            r[s] = s < Seats && _y[s].Plays ? ScoreOf(s) : -1;
        return r;
    }

    TickResult PartyOver()
    {
        _ph = PhOver;
        Event(EvOver);
        var scores = PartyScores();
        var best = scores.Count == 0 ? 0 : scores.Values.Max();
        _winners = [.. scores.Where(kv => kv.Value == best && kv.Value >= 0).Select(kv => kv.Key).Order()];
        Ctx.Finish(_winners, Journal(_winners, [.. Enumerable.Range(0, Seats).Where(s => _y[s].Plays)]), scores);
        return TickResult.Both;
    }

    /// <summary>«Куди бахне: Оля ❤2 : Петро (3-й раунд) : …» — переможці першими, далі хто довше протримався.</summary>
    string Journal(int[] winners, int[] playing)
    {
        var order = winners.Concat(playing.Where(s => !winners.Contains(s)).OrderByDescending(ScoreOf).ThenBy(s => s));
        var line = $"{Info.Title}: {string.Join(" : ", order.Select(s => _y[s].Alive ? $"{Name(s)} ❤{_y[s].Hearts}" : $"{Name(s)} (раунд {_y[s].OutRound})"))}";
        return winners.Length == 0 ? line + " — нічия" : line;
    }

    public override void OnLeave(int seat)
    {
        if (!_started || _ph == PhOver || _party is not null || seat is < 0 or >= Seats) return;
        var nick = Ctx.NickOf(seat);
        _y[seat].Plays = false;
        _y[seat].Alive = false;
        var left = Enumerable.Range(0, Seats).Where(s => s != seat && _y[s].Plays && _y[s].Alive && Ctx.Seated(s)).ToArray();
        if (left.Length >= 2)
        {
            Ctx.Log($"{Info.Title}: {nick} встав з-за столу — решта грає далі");
            return;
        }
        _ph = PhOver;
        _winners = left;
        if (!_botGame) _series.Record(Ctx, left);
        Ctx.Finish(left, $"{Info.Title}: {nick} встав з-за столу, партію не дограли", left.ToDictionary(s => s, ScoreOf));
    }

    // ---------- бот ----------

    /// <summary>
    /// Боти «пам'ятають» правильний крок, але помиляються з імовірністю на крок за рівнем — частіше на довгих
    /// послідовностях і в підступних раундах. Ступають тим самим <see cref="Act"/>, що й людина, у випадковий момент
    /// такту (сильний — раніше й рівніше).
    /// </summary>
    void BotsThink()
    {
        if (_ph != PhSteps) return;
        var k = (_t - _phaseAt) / _beatTicks;
        if (k >= _len) return;
        foreach (var s in _bots)
        {
            var y = _y[s];
            if (Ctx.Seated(s) || !y.Plays || !y.Alive || y.Slots[k] >= 0) continue;
            if (y.BotBeat != k)
            {
                // Новий такт: коли ступить і чи помилиться. Легкий — то зарано, то під кінець; сильний — у першій половині.
                y.BotBeat = k;
                var (lo, hi) = _level switch
                {
                    LiveBots.Level.Easy => (1, _beatTicks - 2),
                    LiveBots.Level.Hard => (1, _beatTicks / 2),
                    _ => (2, _beatTicks * 3 / 4),
                };
                y.BotAt = _phaseAt + k * _beatTicks + Ctx.Rng.Next(lo, Math.Max(lo + 1, hi));
                // Помилка: третина — завис і не ступив, решта — не туди.
                y.BotErr = Ctx.Rng.NextDouble() >= ErrorRate() ? 0 : Ctx.Rng.NextDouble() < 0.33 ? 2 : 1;
            }
            if (_t < y.BotAt || y.BotErr == 2) continue;
            var d = y.BotErr == 1 ? (_need[k] + 1 + Ctx.Rng.Next(3)) % 4 : _need[k];
            Act(s, "step", DirEl[d]);
        }
    }

    double ErrorRate()
    {
        var extra = Math.Max(0, _len - 3);
        var (b, perStep, trick) = _level switch
        {
            LiveBots.Level.Easy => (0.10, 0.02, 0.10),
            LiveBots.Level.Hard => (0.012, 0.004, 0.02),
            _ => (0.04, 0.01, 0.05),
        };
        return b + perStep * extra + (_trick is TrickDrunk or TrickRed ? trick : 0);
    }

    // ---------- вид і кадр ----------

    public override object View(int? seat)
    {
        var lobby = Lobby;
        int?[] hearts = new int?[Seats], outRound = new int?[Seats];
        for (var i = 0; i < Seats; i++)
        {
            if (lobby || !_y[i].Plays) continue;
            hearts[i] = _y[i].Hearts;
            outRound[i] = _y[i].Alive ? null : _y[i].OutRound;
        }
        return new
        {
            phase = lobby ? "lobby" : _ph switch
            {
                PhReady => "ready", PhShow => "show", PhGo => "go", PhSteps => "steps", PhEnd => "end", _ => "over",
            },
            round = lobby ? 0 : _round,
            roundsMax = _party is null ? RoundsMax : PartyRounds,
            heartsMax = StartHearts,
            tickMs = TickMs,
            grace = Grace,
            party = _party is not null,
            hearts,
            @out = outRound,
            winners = !lobby && _ph == PhOver ? (int[])_winners.Clone() : [],
            series = _series.View(Ctx, Seats),
            turn = (int?)null,
            botOffer = _party is null && _solo.Offer(Ctx, Seats),
            botWanted = _solo.Wanted,
            botLvl = LiveBots.Key(_party?.Level ?? _solo.Level),
            bot = lobby ? BotSeats() : _bots.Where(s => !Ctx.Seated(s)).ToArray(),
            frame = Shot(lobby),
        };
    }

    public override object? Frame() => Shot(Lobby);

    /// <summary>
    /// Кадр (spec §4): фаза й годинник, стрілки, що вже показано (у тактах — лише ті, що вже бахнули: пам'ять, а не
    /// підказка), правильні кроки бахнутих тактів, двори [x, y, серця, прапорці] і події. Масиви нові щоразу.
    /// </summary>
    object Shot(bool lobby)
    {
        var p = new int[]?[Seats];
        var beat = Beat;
        for (var i = 0; i < Seats; i++)
        {
            var y = _y[i];
            var plays = lobby ? Ctx.Seated(i) || Array.IndexOf(BotSeats(), i) >= 0 : y.Plays;
            if (!plays) continue;
            if (lobby) { p[i] = [Center, Center, Hearts, 1]; continue; }
            var (x, yy) = Live(i);
            var fl = (y.Alive ? 1 : 0) | (y.HitNow ? 2 : 0)
                | (beat >= 0 && y.Slots[beat] >= 0 || _ph == PhGo && y.Slots[0] >= 0 ? 4 : 0);
            p[i] = [x, yy, y.Hearts, fl];
        }
        var ph = lobby ? PhLobby : _ph;
        var shown = lobby ? 0 : _ph == PhShow ? _shownCount : _ph is PhGo or PhSteps ? _resolved : _len;
        var arrows = new int[shown][];
        for (var i = 0; i < shown; i++) arrows[i] = [_shown[i], _red[i] ? 1 : 0];
        var done = lobby ? 0 : _ph is PhShow or PhGo ? 0 : _ph == PhSteps ? _resolved : _len;
        var need = new int[done];
        for (var i = 0; i < done; i++) need[i] = _need[i];
        return new
        {
            t = _t,
            ph,
            round = lobby ? 0 : _round,
            len = lobby ? 0 : _len,
            trick = lobby ? 0 : _trick,
            left = lobby || _ph == PhOver ? 0 : _ph == PhSteps ? Math.Max(0, _len * _beatTicks - (_t - _phaseAt)) : _left,
            at = _phaseAt,
            ar = _arrowTicks,
            bt = _beatTicks,
            beat,
            done = _resolved,
            a = arrows,
            need,
            safe = _safe is { } sf && !lobby ? new[] { sf.X, sf.Y } : null,
            p,
            ev = lobby ? [] : _ev.Select(e => new[] { e[0], e[1], e[2], e[3] }).ToArray(),
        };
    }
}
