using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Крадії груш: сад на 2–8, з дерев хвилями сиплються груші — носи їх у свою комору по краю саду, а коли на землі
/// пусто, кради з чужих. Що більша ноша — то повільніше ходиш; штовхан висипає чужу ношу. Рахунок — груші в коморі
/// на кінець раунду (2 чи 3 хв). Світ — <see cref="GrushiCore"/>, бот — <see cref="GrushiBot"/>; тут фази, опції,
/// вид, кадр, вихід посеред партії, ачівки, соло з ботами й режим вечірки. Spec: <c>docs/games/specs/grushi.md</c>.
/// </summary>
public sealed class Grushi : Game, IPartyMinigame
{
    public const int Seats = GrushiCore.Seats;
    /// <summary>Відлік «Готуйсь» — 3 с.</summary>
    public const int ReadyTicks = 3 * GrushiCore.TicksPerSec;
    /// <summary>Фази в кадрі (<c>ph</c>): 0 готуйсь, 1 гра, 3 партію зіграно, 4 лобі (номери — як у Крижини).</summary>
    public const int PhReady = 0, PhGo = 1, PhOver = 3, PhLobby = 4;
    /// <summary>Намір без підтвердження живе 1,2 с (браузер досилає затиснутий напрямок раз на 0,4 с) — як у Крижині.</summary>
    public const int KeepTicks = 30;
    /// <summary>Вечірка: 75 с гри (+3 с відліку), стеля 90 с.</summary>
    public const int PartySeconds = 75;
    /// <summary>Самому — троє ботів: на двох красти нема в кого, а вчотирьох уже є і хто краде, і з кого.</summary>
    public const int SoloBots = 3;

    static readonly string[] Names = ["синій", "рудий", "зелений", "жовтий", "бузковий", "м’ятний", "рожевий", "сірий"];

    static readonly GameOption LenOption = new("len", "Раунд",
        [("2", "2 хвилини"), ("3", "3 хвилини")], "2");

    public override GameInfo Info { get; } = new(
        "grushi", "Крадії груш", "крадіїв груш", GameGroup.Live, 1, Seats, TickMs: GrushiCore.TickMs,
        Start: StartMode.ByHost, Options: [LenOption, LiveBots.LevelOption],
        Hint: "Збирай груші з-під дерев і носи в свою комору, а як на землі пусто — кради з чужих. Повна ноша — важка, "
            + "а штовхан висипає її на землю. Самому — з 🤖 ботами");

    readonly SoloBot _solo = new();
    PartyMode? _party;
    GrushiCore? _core;
    int[] _bots = [];
    readonly GrushiBot?[] _brain = new GrushiBot?[Seats];
    bool _botGame, _started;
    int _ph = PhReady, _left, _goTicks;
    int _lenMin = 2;
    readonly int[] _moveAt = new int[Seats];
    int _goAt;
    int _startPlayers;
    int[] _winners = [];
    bool _spillSaid;
    readonly Series _series = new();
    /// <summary>Готові payload-и для бота: сектор −1..15 — без алокацій на тик.</summary>
    static readonly JsonElement[] SectorEl = [.. Enumerable.Range(-1, 17).Select(a => JsonSerializer.SerializeToElement(a))];

    public bool Party => _party is not null;
    public IReadOnlyList<int> Bots => _bots;
    public bool BotGame => _botGame;
    public int Phase => _ph;
    /// <summary>Скільки тиків триває гра (без відліку).</summary>
    public int GoTicks => _goTicks;

    public string Howto => "Збирай груші й неси у свою комору; на землі пусто — постій біля чужої комори й крадь. "
        + "Стрілки/WASD — ходити, пробіл — штовхан (висипає чужу ношу); на телефоні — стік і кнопка";
    public int PartyCapMs => 90_000;
    public int PartyMin => 2;
    public int PartyMax => Seats;

    public GrushiCore Core
    {
        get
        {
            if (_core is not null) return _core;
            _core = new GrushiCore(Ctx.Rng);
            _core.Reset(Seated());
            return _core;
        }
    }

    bool[] Seated() => [.. Enumerable.Range(0, Seats).Select(Ctx.Seated)];

    int[] BotSeats() => _solo.Active(Ctx, Seats)
        ? [.. Enumerable.Range(0, Seats).Where(s => !Ctx.Seated(s)).Take(SoloBots)] : [];

    bool IsBot(int seat) => Array.IndexOf(_bots, seat) >= 0 && !Ctx.Seated(seat);

    public override string SeatName(int seat) => seat is >= 0 and < Seats ? Names[seat] : base.SeatName(seat);

    public override string? SeatBot(int seat) =>
        !Ctx.Seated(seat) && Array.IndexOf(_started ? _bots : BotSeats(), seat) >= 0 ? $"{LiveBots.Name} {SeatName(seat)}" : null;

    string Name(int seat) => SeatBot(seat) ?? Ctx.NickOf(seat) ?? SeatName(seat);

    public override bool ActsInLobby => true;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _lenMin = options.TryGetValue("len", out var v) && v == "3" ? 3 : 2;
        _solo.Configure(options);
        _party = PartyMode.Read(options);
    }

    public override string? CanStart() => _solo.CanStart(Ctx, Seats);

    public override void Start()
    {
        _started = true;
        _bots = _party is { } pm ? [.. pm.Bots.Where(s => s < Seats && s < Ctx.Players && !Ctx.Seated(s))] : BotSeats();
        _botGame = _bots.Length > 0;
        Array.Clear(_brain);
        var level = _party?.Level ?? _solo.Level;
        // Думають у різні тики, щоб не смикались хором.
        for (var i = 0; i < _bots.Length; i++) _brain[_bots[i]] = new GrushiBot(level, i);
        var plays = Seated();
        foreach (var s in _bots) plays[s] = true;
        _startPlayers = Enumerable.Range(0, Seats).Count(Ctx.Seated);
        _series.Begin(Ctx, Seats);
        Core.Reset(plays);
        Array.Clear(_moveAt);
        _goAt = 0;
        _winners = [];
        _spillSaid = false;
        _goTicks = (_party is not null ? PartySeconds : _lenMin * 60) * GrushiCore.TicksPerSec;
        _ph = PhReady;
        _left = ReadyTicks;
        if (_party is null && Ctx.Round == 1) Ctx.Say("Хто груші не крав — хай перший кине огризок 🍐");
    }

    // ---------- ввід ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == LiveBots.Toggle)
            return _started && _ph != PhOver ? ActResult.Fail("Партія вже йде") : _solo.Switch(Ctx, seat, payload, Seats);
        if (!_started) return ActResult.Fail("Чекаємо на гравців");
        if (seat is < 0 or >= Seats || !Core.Bodies[seat].Plays) return ActResult.Fail("Ти тут не граєш");
        if (_ph == PhOver) return ActResult.Fail("Партію вже зіграно");
        switch (action)
        {
            case "move":
                // Намір, а не крок: приймаємо й на відліку — рушиш, щойно скажуть «гайда».
                if (Sector(payload) is not { } a || a is < -1 or > 15) return ActResult.Fail("Такого напрямку нема");
                Core.Move(seat, a);
                _moveAt[seat] = Core.T;
                return ActResult.Done;
            case "push":
                if (_ph != PhGo) return ActResult.Fail("Зачекай, зараз почнемо");
                return Core.Push(seat) is { } no ? ActResult.Fail(no) : ActResult.Done;
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    /// <summary>Сектор — і як <c>{ a: 5 }</c>, і як голе <c>5</c>; лише ціле число.</summary>
    static int? Sector(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetInt32(out var n) => n,
        JsonValueKind.Object when payload.TryGetProperty("a", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out var n) => n,
        _ => null,
    };

    // ---------- тик ----------

    public override TickResult Tick()
    {
        if (!_started) return TickResult.None;
        var c = Core;
        switch (_ph)
        {
            case PhReady:
                c.Idle();
                if (--_left > 0) return c.T % 5 == 0 ? TickResult.FrameOnly : TickResult.None;
                _ph = PhGo;
                _goAt = c.T;
                return TickResult.Both;
            case PhGo:
                // Боти ходять до кроку світу — їхній ввід лягає в цей тик, як людський між тиками.
                BotsThink(c);
                c.Step();
                Expire(c);
                Moment(c);
                if (c.Rt >= _goTicks) return Over();
                return TickResult.FrameOnly;
            default:
                return TickResult.None;
        }
    }

    void Expire(GrushiCore c)
    {
        for (var s = 0; s < Seats; s++)
        {
            var b = c.Bodies[s];
            if (b.Want >= 0 && c.T - Math.Max(_moveAt[s], _goAt) >= KeepTicks) b.Want = -1;
        }
    }

    void BotsThink(GrushiCore c)
    {
        foreach (var s in _bots)
        {
            if (Ctx.Seated(s) || _brain[s] is not { } bot || !c.Bodies[s].Plays || !bot.Due(c.T)) continue;
            var m = bot.Think(c, s, Ctx.Rng);
            if (m.Sector is { } a) Act(s, "move", SectorEl[a + 1]);
            if (m.Push) Act(s, "push", default);
        }
    }

    /// <summary>Глек раз на партію коментує велику розсипку (4+ груш) — у вечірці мовчить.</summary>
    void Moment(GrushiCore c)
    {
        if (_spillSaid || _party is not null) return;
        foreach (var e in c.Events())
        {
            if (e[0] != GrushiCore.EvSpill || e[2] < 4) continue;
            _spillSaid = true;
            Ctx.Say($"Ой, {Name(e[1])} — і {e[2]} {(e[2] < 5 ? "груші" : "груш")} на землі! Підбирайте, люди добрі, поки не згнили");
            return;
        }
    }

    /// <summary>Хто грає партію: сидить за столом або бот цієї партії.</summary>
    int[] Playing() => [.. Enumerable.Range(0, Seats).Where(s => Core.Bodies[s].Plays && (Ctx.Seated(s) || IsBot(s)))];

    /// <summary>Час вийшов: переможці — у кого найповніша комора (усі по нулях — нічия).</summary>
    TickResult Over()
    {
        var c = Core;
        _ph = PhOver;
        if (_party is not null)
        {
            var ps = PartyScores();
            var top = ps.Values.DefaultIfEmpty(0).Max();
            _winners = [.. ps.Where(kv => kv.Value == top && kv.Value > 0).Select(kv => kv.Key).Order()];
            Ctx.Finish(_winners, Journal(_winners, [.. ps.Keys.Where(s => s < Seats && c.Bodies[s].HasLarder)]), ps);
            return TickResult.Both;
        }
        var playing = Playing();
        var best = playing.Length == 0 ? 0 : playing.Max(s => c.Bodies[s].Larder);
        _winners = best == 0 ? [] : [.. playing.Where(s => c.Bodies[s].Larder == best)];
        var scores = playing.Where(Ctx.Seated).ToDictionary(s => s, s => (long)c.Bodies[s].Larder);
        if (!_botGame)
        {
            if (_startPlayers >= 2)
                foreach (var s in playing)
                {
                    var b = c.Bodies[s];
                    if (b.Stole >= 10) Ctx.Award(s, 0, "ach:grushi-robin");
                    if (b.Lost == 0 && b.Larder >= 10) Ctx.Award(s, 0, "ach:grushi-guard");
                    if (b.Loads5 >= 5) Ctx.Award(s, 0, "ach:grushi-porter");
                }
            _series.Record(Ctx, _winners);
            if (_winners.Length == 1 && c.Bodies[_winners[0]] is { } w && w.Stole * 2 >= w.Larder && w.Larder > 0)
                Ctx.Say($"{Name(_winners[0])}, пів комори в тебе — крадене. Господар, нічого не скажеш 😏");
            Ctx.Finish(_winners, Journal(_winners, playing), scores);
            return TickResult.Both;
        }
        // З ботами — без серії й нагород: перемога бота — порожні winners і вердикт, людська — вердикт з рівнем.
        var people = _winners.Where(Ctx.Seated).ToArray();
        var verdict = people.Length > 0 ? $"🏆 {Ctx.NickOf(people[0])} — перемога над {LiveBots.Of(_solo.Level)}и ботами"
            : _winners.Length > 0 ? $"🤖 Найповніша комора — у {Name(_winners[0])}" : null;
        Ctx.Finish(people, Journal(_winners, playing), scores, verdict);
        return TickResult.Both;
    }

    /// <summary>«Крадії груш: Оля 23 : Петро 17 : Ігор 9» — переможці першими, далі за коморою.</summary>
    string Journal(int[] winners, int[] playing)
    {
        var c = Core;
        var order = winners.Concat(playing.Where(s => !winners.Contains(s)).OrderByDescending(s => c.Bodies[s].Larder).ThenBy(s => s));
        var line = $"{Info.Title}: {string.Join(" : ", order.Select(s => $"{Name(s)} {c.Bodies[s].Larder}"))}";
        return winners.Length == 0 ? line + " — нічия" : line;
    }

    /// <summary>
    /// Scores вечірки: груші в коморі кожного місця 0..N−1 (золота — три). Місце, що не грало, — −1. Хто відпав
    /// посеред міні-гри, стоїть у саду, а його комора лічиться (і її можна обчистити).
    /// </summary>
    public IReadOnlyDictionary<int, long> PartyScores()
    {
        var c = Core;
        var r = new Dictionary<int, long>(Ctx.Players);
        for (var s = 0; s < Ctx.Players; s++)
            r[s] = s < Seats && c.Bodies[s].HasLarder ? c.Bodies[s].Larder : -1;
        return r;
    }

    /// <summary>
    /// Хтось устав посеред партії: ноша висипається, тіло зникає, а комора лишається в саду (її ще можна обчистити,
    /// у підсумок вона не йде). Лишилось двоє й більше людей — грають далі; партія з ботами без людини кінчається.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (!_started || _ph == PhOver || _party is not null || seat is < 0 or >= Seats) return;
        var c = Core;
        if (!c.Bodies[seat].Plays) return;
        var nick = Ctx.NickOf(seat);
        c.Drop(seat);
        var left = Enumerable.Range(0, Seats).Where(s => s != seat && c.Bodies[s].Plays && Ctx.Seated(s)).ToArray();
        if (left.Length >= 2)
        {
            Ctx.Log($"{Info.Title}: {nick} встав з-за столу — решта грає далі");
            return;
        }
        _ph = PhOver;
        var best = left.Length == 0 ? 0 : left.Max(s => c.Bodies[s].Larder);
        _winners = _botGame || best == 0 ? [] : [.. left.Where(s => c.Bodies[s].Larder == best)];
        if (!_botGame) _series.Record(Ctx, _winners);
        Ctx.Finish(_winners, $"{Info.Title}: {nick} встав з-за столу, партію не дограли",
            left.ToDictionary(s => s, s => (long)c.Bodies[s].Larder));
    }

    // ---------- вид і кадр ----------

    bool Lobby => !_started;

    /// <summary>Сад для малювання: справжній у партії, а в лобі — порожній, з коморами тих, хто вже сів (і ботів).</summary>
    GrushiCore Field
    {
        get
        {
            if (!Lobby) return Core;
            var plays = Seated();
            foreach (var s in BotSeats()) plays[s] = true;
            var preview = new GrushiCore(Ctx.Rng);   // Reset випадковості не питає
            preview.Reset(plays);
            return preview;
        }
    }

    public override object View(int? seat)
    {
        var lobby = Lobby;
        var c = Field;
        var larders = new int[]?[Seats];
        var stats = new int[]?[Seats];
        for (var i = 0; i < Seats; i++)
        {
            var b = c.Bodies[i];
            if (!b.HasLarder) continue;
            larders[i] = [(int)b.LarderX, (int)b.LarderY];
            if (!lobby) stats[i] = [b.Larder, b.Stole, b.Lost, b.Pushes, b.Spilled, b.Loads5];
        }
        return new
        {
            phase = lobby ? "lobby" : _ph switch { PhReady => "ready", PhGo => "go", _ => "over" },
            party = _party is not null,
            len = _party is not null ? PartySeconds : _lenMin * 60,
            size = (int)GrushiCore.Size,
            bodyR = (int)GrushiCore.BodyR,
            pearR = (int)GrushiCore.PearR,
            larderR = (int)GrushiCore.LarderR,
            pushR = (int)GrushiCore.PushR,
            carryMax = GrushiCore.CarryMax,
            goldValue = GrushiCore.GoldValue,
            pushCd = GrushiCore.PushCd,
            stealTicks = GrushiCore.StealTicks,
            shakeTicks = GrushiCore.ShakeTicks,
            tickMs = GrushiCore.TickMs,
            trees = GrushiCore.Trees.Select(t => new[] { (int)t.X, (int)t.Y }).ToArray(),
            larders,
            stats,
            winners = !lobby && _ph == PhOver ? (int[])_winners.Clone() : [],
            series = _series.View(Ctx, Seats),
            turn = (int?)null,
            botOffer = _solo.Offer(Ctx, Seats),
            botWanted = _solo.Wanted,
            botLvl = _party is { } pm ? LiveBots.Key(pm.Level) : _solo.LevelKey,
            bot = lobby ? BotSeats() : _bots.Where(s => !Ctx.Seated(s)).ToArray(),
            frame = Shot(c, lobby),
        };
    }

    public override object? Frame() => Shot(Field, Lobby);

    /// <summary>
    /// Кадр (spec §4): <c>p</c> за місцями [x, y, face, carry, gold, fl, cd, steal] (null — не грає), <c>g</c> груші
    /// на землі [id, x, y, gold, lock], <c>l</c> комори за місцями (null — нема), <c>sh</c> [дерево, тиків] чи null,
    /// <c>ev</c> події тика. Масиви нові щоразу: кадр серіалізують уже поза замком кімнати.
    /// </summary>
    object Shot(GrushiCore c, bool lobby)
    {
        var p = new int[]?[Seats];
        var l = new int?[Seats];
        for (var i = 0; i < Seats; i++)
        {
            var b = c.Bodies[i];
            if (b.HasLarder) l[i] = b.Larder;
            if (!b.Plays) continue;
            var robbed = false;
            for (var j = 0; j < Seats && !robbed; j++)
                robbed = j != i && c.Bodies[j].Plays && c.Bodies[j].StealFrom == i && c.Bodies[j].StealProg > 0;
            var fl = (b.Want >= 0 ? 1 : 0) | (b.Stun > 0 ? 2 : 0) | (b.Immune > 0 ? 4 : 0)
                | (b.StealProg > 0 ? 8 : 0) | (robbed ? 16 : 0) | (IsBot(i) || (lobby && !Ctx.Seated(i)) ? 32 : 0);
            p[i] = [(int)Math.Round(b.X), (int)Math.Round(b.Y), b.Face, b.Carry, b.Gold, fl, b.Cd, b.StealProg];
        }
        var g = new int[c.Ground.Count][];
        for (var i = 0; i < g.Length; i++)
        {
            var q = c.Ground[i];
            g[i] = [q.Id, (int)Math.Round(q.X), (int)Math.Round(q.Y), q.Gold ? 1 : 0, q.Lock];
        }
        var ph = lobby ? PhLobby : _ph;
        return new
        {
            t = c.T,
            ph,
            left = ph == PhGo ? Math.Max(0, _goTicks - c.Rt) : ph == PhReady ? _left : 0,
            p,
            g,
            l,
            sh = c.ShakeTree >= 0 ? new[] { c.ShakeTree, c.ShakeLeft } : null,
            ev = lobby ? [] : c.Events(),
        };
    }
}
