using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Бомбер на двох–шістьох. Правила раунду живуть у <see cref="BomberCore"/>, а тут — усе, що знає про
/// кімнату: фази («готуйсь», гра, пауза на догорілому полі), рахунок раундів до трьох перемог, вид і
/// кадр, опції столу (мапа, поле, стискання, хаос, привиди, команди), стрічка подій і підсумок партії.
/// Кадр летить кожні 60 мс, тож усе в ньому — короткі числа: клієнт домальовує плавність сам.
/// </summary>
public sealed class Bomber : Game
{
    /// <summary>«Готуйсь» на свіжому полі — десь дві секунди.</summary>
    public const int StartTicks = 33;
    /// <summary>Пауза на догорілому полі, щоб побачити, хто взяв раунд — десь секунда.</summary>
    public const int PauseTicks = 17;
    /// <summary>Партія — до трьох виграних раундів.</summary>
    public const int WinsToTake = 3;
    /// <summary>Запобіжник проти вічної партії з самих нічиїх: після дев'ятого раунду рахуємо, хто попереду.</summary>
    public const int MaxRounds = 9;
    /// <summary>Скільки тиків подія їде в кадрі: кадри зливаються, тож одного разу мало (≈2,4 с).</summary>
    public const int EventTicks = 40;

    /// <summary>Фази, які бачить клієнт у полі <c>phase</c> кадра.</summary>
    public const string PhaseStart = "start", PhaseGo = "go", PhasePause = "pause", PhaseOver = "over";

    /// <summary>Команди: зелені проти червоних — і кольором, і на смак.</summary>
    public static readonly string[] TeamNames = ["🥒 Огірки", "🍅 Помідори"];

    public override GameInfo Info { get; } = new(
        "bomber", "Бомбер", "бомбер", GameGroup.Live, 1, BomberCore.Seats, TickMs: BomberCore.TickMs,
        Start: StartMode.ByHost,
        Options:
        [
            new GameOption("map", "Мапа", [("classic", "Класика"), ("open", "Відкрита (менше ящиків)"), ("maze", "Лабіринт")], "classic"),
            new GameOption("size", "Поле", [("15", "15×13"), ("19", "19×15 — просторе"), ("auto", "19×15 від п'ятьох")], "15"),
            new GameOption("shrink", "Стискання", [("0", "ні"), ("1", "з 90-ї секунди стіни сходяться")], "0"),
            new GameOption("chaos", "Бонуси", [("0", "💣 🔥 👟"), ("1", "хаос: ще 🧤 копняк і 💀 прокляття")], "0"),
            new GameOption("ghosts", "Підірвані", [("0", "чекають раунду"), ("1", "👻 привидами з помстою")], "0"),
            new GameOption("teams", "Грають", [("0", "кожен сам"), ("1", "команди 2×2 / 3×3")], "0"),
            new GameOption("ff", "Свої бомби", [("0", "своїх не ранять"), ("1", "дружній вогонь")], "0"),
            LiveBots.LevelOption,
        ],
        Hint: "Ставиш бомби, ламаєш ящики, підриваєш суперників. Останній живий бере раунд. Самому — з трьома 🤖 ботами");

    BomberCore? _core;

    /// <summary>
    /// «🤖 + бот»: людина сама за столом — на вільні місця сідають <see cref="SoloBots"/> боти. Троє, бо бомбер —
    /// гра «останній живий» на чотирьох кутах: удвох із ботом раунд — дуель, а вчотирьох боти б'ються й між
    /// собою, на людину не гуртом, і вижити серед хаосу цікавіше. З опцією «команди» це якраз 2×2: людина з ботом проти двох.
    /// </summary>
    public const int SoloBots = 3;
    readonly SoloBot _solo = new();
    /// <summary>Місця ботів цієї партії (після неї — теж, для підсумку).</summary>
    readonly bool[] _isBot = new bool[BomberCore.Seats];
    BomberBot[] _brains = [];
    /// <summary>Партія з ботами: без нагород і з вердиктом «перемога над ботами».</summary>
    bool _botGame;

    readonly int[] _wins = new int[BomberCore.Seats];
    string _phase = PhaseStart;
    int _startIn = StartTicks;
    int _round = 1;
    /// <summary>Партія вже стартувала хоч раз: до того поле — це просто картинка для лобі.</summary>
    bool _started;

    // опції столу
    BomberMap _map = BomberMap.Classic;
    string _size = "15";
    bool _shrink, _chaos, _ghosts, _teamsOn, _ff;

    /// <summary>Команда кожного місця на цю партію; null — кожен сам.</summary>
    int[]? _teams;
    /// <summary>Чому команд не вийшло (непарний стіл) — рядок для лобі й статусу.</summary>
    string? _note;

    // підсумок партії: по місцях
    readonly int[] _kills = new int[BomberCore.Seats];
    readonly int[] _selfs = new int[BomberCore.Seats];
    readonly int[] _teamKills = new int[BomberCore.Seats];
    readonly int[] _revenges = new int[BomberCore.Seats];
    readonly int[] _boxes = new int[BomberCore.Seats];
    readonly int[] _lived = new int[BomberCore.Seats];
    object[]? _sum;

    /// <summary>Стрічка подій: id, тик сервера, подія. Тримаємо кілька останніх.</summary>
    readonly List<(int Id, int At, BomberEvent Ev)> _feed = [];
    int _evId;
    /// <summary>Тики від старту партії наскрізь — для віку подій у стрічці.</summary>
    int _clock;

    /// <summary>
    /// Поле готове ще до старту: стіл, який чекає на гравців, має виглядати як поле, а не як порожнеча.
    /// Ящиків там нема свідомо — <see cref="BomberCore.Layout"/> не бере жодного числа з <c>Ctx.Rng</c>,
    /// і партія лишається такою самою, скільки б разів у лобі не перемальовували картку. До старту поле
    /// ще й підлаштовується під стіл («19×15 від п'ятьох» — щойно сів п'ятий).
    /// </summary>
    BomberCore Core
    {
        get
        {
            if (_core is not null && (_started || _core.Width == SizeFor(SeatedCount()).W)) return _core;
            _core = NewCore(SeatedCount());
            _core.Layout();
            return _core;
        }
    }

    int SeatedCount()
    {
        var n = 0;
        for (var s = 0; s < BomberCore.Seats; s++) if (In(s)) n++;
        return n;
    }

    /// <summary>На полі: людина за столом або бот цієї партії.</summary>
    bool In(int s) => Ctx.Seated(s) || _isBot[s];

    /// <summary>Нік для рядка Журналу; бот — «🤖 бот».</summary>
    string Nick(int s) => Ctx.NickOf(s) ?? (_isBot[s] ? LiveBots.Name : SeatName(s));

    /// <summary>Куди сядуть боти, якщо їх покликали (і людина сама): перші вільні місця.</summary>
    int[] BotSeats() => !_solo.Active(Ctx, BomberCore.Seats) ? []
        : [.. Enumerable.Range(0, BomberCore.Seats).Where(s => !Ctx.Seated(s)).Take(SoloBots)];

    public override bool ActsInLobby => true;

    public override string? CanStart() => _solo.CanStart(Ctx, BomberCore.Seats);

    public override string? SeatBot(int seat) =>
        _started && seat >= 0 && seat < BomberCore.Seats && _isBot[seat] && !Ctx.Seated(seat) ? LiveBots.Name : null;

    (int W, int H) SizeFor(int seated) =>
        _size == "19" || (_size == "auto" && seated >= 5) ? (BomberCore.BigW, BomberCore.BigH) : (BomberCore.W, BomberCore.H);

    BomberCore NewCore(int seated)
    {
        var (w, h) = SizeFor(seated);
        return new BomberCore(Ctx.Rng, w, h, _map)
        {
            Chaos = _chaos, Ghosts = _ghosts, Shrink = _shrink, FriendlyFire = _ff, Teams = _teams,
        };
    }

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _map = options.GetValueOrDefault("map") switch
        {
            "open" => BomberMap.Open,
            "maze" => BomberMap.Maze,
            _ => BomberMap.Classic,
        };
        _size = options.GetValueOrDefault("size") is "19" or "auto" ? options["size"] : "15";
        _shrink = options.GetValueOrDefault("shrink") == "1";
        _chaos = options.GetValueOrDefault("chaos") == "1";
        _ghosts = options.GetValueOrDefault("ghosts") == "1";
        _teamsOn = options.GetValueOrDefault("teams") == "1";
        _ff = options.GetValueOrDefault("ff") == "1";
        _solo.Configure(options);
        _core = null;
    }

    public override string SeatName(int seat) => seat switch
    {
        0 => "жовтий",
        1 => "зелений",
        2 => "рудий",
        3 => "сірий",
        4 => "синій",
        _ => "рожевий",
    };

    public override void Start()
    {
        Array.Clear(_wins);
        foreach (var a in new[] { _kills, _selfs, _teamKills, _revenges, _boxes, _lived }) Array.Clear(a);
        _sum = null;
        _feed.Clear();
        _clock = 0;
        _round = 1;
        _started = true;
        _phase = PhaseStart;
        _startIn = StartTicks;
        Array.Clear(_isBot);
        var bots = BotSeats();
        foreach (var b in bots) _isBot[b] = true;
        _botGame = bots.Length > 0;
        _brains = [.. bots.Select(b => new BomberBot(b, _solo.Level))];
        SetupTeams();
        _core = NewCore(SeatedCount());
        Core.Reset(Plays());
    }

    /// <summary>
    /// Команди — лише на парному столі від чотирьох: 2×2 або 3×3. Місця за столом по черзі: перше, третє,
    /// п'яте — «Огірки» (верхній край поля), друге, четверте, шосте — «Помідори» (нижній).
    /// </summary>
    void SetupTeams()
    {
        _teams = null;
        _note = null;
        if (!_teamsOn) return;
        var seated = Enumerable.Range(0, BomberCore.Seats).Where(In).ToArray();
        if (seated.Length is not (4 or 6))
        {
            _note = "Команд не буде: треба четверо або шестеро за столом — граємо кожен сам";
            return;
        }
        _teams = [.. Enumerable.Repeat(-1, BomberCore.Seats)];
        for (var i = 0; i < seated.Length; i++) _teams[seated[i]] = i % 2;
    }

    /// <summary>Хто цього раунду на полі. Місця, з яких устали, назад не повертаються.</summary>
    bool[] Plays() => [.. Enumerable.Range(0, BomberCore.Seats).Select(In)];

    // ---------- ввід ----------

    /// <summary>
    /// Реалтайм-ввід із хабового <c>Input</c>: «тримаю напрямок» і «клади бомбу». Відповіді ніхто не
    /// побачить, але тексти все одно людські — той самий метод кличе і <c>Act</c> у тестах.
    /// </summary>
    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == LiveBots.Toggle)
            return _started && _phase != PhaseOver ? ActResult.Fail("Партія вже йде") : _solo.Switch(Ctx, seat, payload, BomberCore.Seats);
        if (!_started) return ActResult.Fail("Партія ще не почалась");
        if (seat < 0 || seat >= BomberCore.Seats) return ActResult.Fail("Ти тут не граєш");

        switch (action)
        {
            case "move":
                // «Тримаю напрямок» — це лише намір, і приймаємо його в будь-якій фазі: людина тисне
                // стрілку ще на відліку, і бомбер має поїхати з першого тика раунду. Зрушити раніше
                // нікому — у фазах «готуйсь» і «пауза» Core.Step() не кличеться взагалі.
                if (_phase == PhaseOver) return ActResult.Fail("Партію вже зіграно");
                var dir = Dir(payload);
                if (dir is null or < -1 or > 3) return ActResult.Fail("Такого напрямку нема");
                Core.Turn(seat, dir.Value);
                return ActResult.Done;
            case "bomb":
                if (_phase != PhaseGo) return ActResult.Fail("Мить — зараз почнемо");
                var me = Core.Players[seat];
                if (me.Ghost)
                    return !me.Revenge ? ActResult.Fail("Помсту вже кинуто — одна на раунд")
                        : Core.Revenge(seat) ? ActResult.Done : ActResult.Fail("Сюди помсту не кинеш — лети на вільну клітинку");
                if (!me.Alive) return ActResult.Fail("Тебе вже підірвали, чекай наступного раунду");
                return Core.Bomb(seat) ? ActResult.Done : ActResult.Fail("Бомби скінчились");
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    /// <summary>Напрямок приймаємо і як <c>{dir:1}</c>, і як голе число — клієнтам так простіше.</summary>
    static int? Dir(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetInt32(out var n) => n,
        JsonValueKind.Object when payload.TryGetProperty("dir", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n) => n,
        _ => null,
    };

    // ---------- тик ----------

    public override TickResult Tick()
    {
        if (_phase != PhaseOver) _clock++;
        switch (_phase)
        {
            case PhaseOver:
                return TickResult.None;

            case PhaseStart:
                if (--_startIn > 0) return TickResult.FrameOnly;
                _phase = PhaseGo;
                return TickResult.FrameOnly;

            case PhasePause:
                if (--_startIn > 0) return TickResult.FrameOnly;
                _round++;
                Core.Reset(Plays());
                _phase = PhaseStart;
                _startIn = StartTicks;
                return TickResult.Both;   // рахунок раундів змінився ще раніше, але вид з ним їде саме тут

            default:
                return Play();
        }
    }

    TickResult Play()
    {
        // Боти думають перед кроком світу — тим самим вводом, що й людина між тиками.
        foreach (var bot in _brains)
            if (_isBot[bot.Seat] && !Ctx.Seated(bot.Seat)) bot.Think(Core, Ctx.Rng);
        Core.Step();
        Drain();
        // Соло з ботами (кожен сам): людину підірвали — чекати, поки боти доб'ють одне одного, нудно, а сильні можуть
        // кружляти й до нічиєї за часом. Раунд тоді за ботами, що встояли, — кожному по перемозі.
        var botsTake = _botGame && _teams is null && !Core.RoundOver
            && !Enumerable.Range(0, BomberCore.Seats).Any(s => Ctx.Seated(s) && Core.Players[s].Alive);
        if (!Core.RoundOver && !botsTake) return TickResult.FrameOnly;

        // підсумок раунду: хто скільки прожив і скільки ящиків розбив
        for (var s = 0; s < BomberCore.Seats; s++)
        {
            if (Core.Players[s].Alive) _lived[s] += Core.Ticks;
            _boxes[s] += Core.Broke[s];
        }

        if (_teams is not null) return TeamRound();
        if (botsTake)
        {
            var alive = Enumerable.Range(0, BomberCore.Seats).Where(s => _isBot[s] && Core.Players[s].Alive).ToArray();
            foreach (var s in alive) _wins[s]++;
            var done = alive.Where(s => _wins[s] >= WinsToTake).ToArray();
            if (done.Length > 0) return Over(done);
            if (_round >= MaxRounds) return Over(Leaders());
            return Pause();
        }

        var took = Core.LastStanding;    // -1 — усі полягли разом або вийшов час
        if (took >= 0) _wins[took]++;

        if (took >= 0 && _wins[took] >= WinsToTake) return Over([took]);
        if (_round >= MaxRounds) return Over(Leaders());
        return Pause();
    }

    /// <summary>Раунд команді: +1 кожному з неї (рахунок над полем — однаковий у команди).</summary>
    TickResult TeamRound()
    {
        var team = Core.LastTeam;
        if (team >= 0)
            for (var s = 0; s < BomberCore.Seats; s++)
                if (_teams![s] == team) _wins[s]++;
        if (team >= 0 && _wins.Where((_, s) => _teams![s] == team).Max() >= WinsToTake) return Over(TeamSeats(team));
        if (_round >= MaxRounds) return Over(Leaders());
        return Pause();
    }

    int[] TeamSeats(int team) => [.. Enumerable.Range(0, BomberCore.Seats).Where(s => _teams![s] == team && In(s))];

    TickResult Pause()
    {
        _phase = PhasePause;
        _startIn = PauseTicks;
        return TickResult.Both;
    }

    /// <summary>Події тика — у стрічку й у підсумок партії.</summary>
    void Drain()
    {
        if (Core.Events.Count == 0) return;
        foreach (var ev in Core.Events)
        {
            _lived[ev.Victim] += Core.Ticks - 1;   // загинув на цьому тику — прожив на один менше за того, хто встояв
            switch (ev.How)
            {
                case BomberHow.Kill: _kills[ev.Killer]++; break;
                case BomberHow.Self: _selfs[ev.Victim]++; break;
                case BomberHow.Team: _teamKills[ev.Killer]++; break;
                case BomberHow.Revenge: _revenges[ev.Killer]++; break;
            }
            _feed.Add((++_evId, _clock, ev));
        }
        Core.Events.Clear();
        if (_feed.Count > 8) _feed.RemoveRange(0, _feed.Count - 8);
    }

    /// <summary>Хто попереду за раундами; порожньо — якщо попереду всі одразу (тоді це нічия).</summary>
    int[] Leaders()
    {
        var playing = Enumerable.Range(0, BomberCore.Seats).Where(In).ToArray();
        if (playing.Length == 0) return [];
        var best = playing.Max(s => _wins[s]);
        var leaders = playing.Where(s => _wins[s] == best).ToArray();
        return leaders.Length == playing.Length ? [] : leaders;
    }

    /// <summary>Кінець партії: рядок Журналу — рахунок раундів, переможець першим; і підсумок «хто чим відзначився».</summary>
    TickResult Over(int[] winners)
    {
        _phase = PhaseOver;
        winners = [.. winners.Where(In)];
        _sum = Summary();
        string score;
        if (_teams is not null)
        {
            var first = winners.Length > 0 ? _teams[winners[0]] : 0;
            score = string.Join(" : ", new[] { first, 1 - first }.Select(t =>
            {
                var seats = TeamSeats(t);
                return $"{TeamNames[t]} ({string.Join(", ", seats.Select(Nick))}) {(seats.Length > 0 ? _wins[seats[0]] : 0)}";
            }));
        }
        else
        {
            var rest = Enumerable.Range(0, BomberCore.Seats).Where(s => In(s) && !winners.Contains(s));
            score = string.Join(" : ", winners.Concat(rest).Select(s => $"{Nick(s)} {_wins[s]}"));
        }
        var log = winners.Length > 0 ? $"{Info.Title}: {score}" : $"{Info.Title}: {score} — нічия";
        if (_botGame) FinishSolo(winners, log, score);
        else Ctx.Finish(winners, log);
        return TickResult.Both;
    }

    /// <summary>
    /// Кінець партії з ботами: переможці — лише люди (бот нагород не бере, та й Rewards рахує від двох людей), а
    /// вердикт каже, над ким перемога. Команда людини з ботом виграла — перемога людини.
    /// </summary>
    void FinishSolo(int[] winners, string log, string score)
    {
        var people = winners.Where(Ctx.Seated).ToArray();
        var human = Enumerable.Range(0, BomberCore.Seats).FirstOrDefault(Ctx.Seated, -1);
        var foes = Enumerable.Range(0, BomberCore.Seats)
            .Count(s => _isBot[s] && (_teams is null || human < 0 || _teams[s] != _teams[human]));
        var lvl = _solo.Level switch { LiveBots.Level.Easy => "легкими", LiveBots.Level.Hard => "сильними", _ => "звичайними" };
        var many = foes switch { 1 => $"{LiveBots.Of(_solo.Level)} ботом", 2 => $"двома {lvl} ботами", _ => $"трьома {lvl} ботами" };
        var verdict = people.Length > 0 ? $"🏆 {Nick(people[0])} — перемога над {many}"
            : winners.Length > 0 ? $"🤖 Бот переміг — {score}"
            : "🤝 Нічия з ботами";
        Ctx.Finish(people, log, verdict: verdict);
    }

    /// <summary>
    /// Підсумок партії — звання тим, хто відзначився: <c>{k, n, s: [місця]}</c> (нічия — кілька місць).
    /// Порожні звання (ніхто не підірвав сам себе) не показуємо.
    /// </summary>
    object[] Summary()
    {
        var seated = Enumerable.Range(0, BomberCore.Seats).Where(In).ToArray();
        var list = new List<object>();
        void Title(string key, int[] by)
        {
            if (seated.Length == 0) return;
            var best = seated.Max(s => by[s]);
            if (best <= 0) return;
            list.Add(new { k = key, n = best, s = seated.Where(s => by[s] == best).ToArray() });
        }
        Title("long", _lived);
        Title("kills", _kills);
        Title("revenge", _revenges);
        Title("boxes", _boxes);
        Title("self", _selfs);
        Title("team", _teamKills);
        return [.. list];
    }

    /// <summary>
    /// Хтось устав. На чотирьох це не привід ламати партію решті: бомбер утікача просто зникає з поля.
    /// А коли за столом лишається один (чи одна команда) — грати вже нема з ким, і партія їхня.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (_started && seat >= 0 && seat < BomberCore.Seats)
        {
            var p = Core.Players[seat];
            p.Alive = false;
            p.Ghost = false;
            p.Plays = false;
            p.Want = -1;
        }
        var left = Enumerable.Range(0, BomberCore.Seats).Where(s => s != seat && Ctx.Seated(s)).ToArray();
        var oneTeam = _teams is not null && left.Length > 0 && left.All(s => _teams[s] == _teams[left[0]]);
        if (left.Length > 1 && !oneTeam) return;
        _phase = PhaseOver;
        Ctx.Finish(left, $"{Info.Title}: {Ctx.NickOf(seat)} встає з-за столу, партію не дограли");
    }

    // ---------- вид і кадр ----------

    /// <summary>
    /// Кадр на кожен тик. Координати бомберів — у дванадцятих частках клітинки (щоб клієнт міг плавно
    /// інтерполювати), координати бомб і бонусів — у цілих клітинках: вони й так стоять по центру.
    /// Нове з проходу №3 — лише коли є що сказати: <c>ev</c> (свіжі події), <c>sh</c> (скільки забрало
    /// стискання), а в бомбера — <c>mv</c> (куди йде крок), <c>g</c> (привид), <c>cu</c> (прокляття), <c>k</c> (рукавиця).
    /// </summary>
    public override object? Frame()
    {
        var f = new Dictionary<string, object>
        {
            ["t"] = Core.Ticks,
            ["p"] = Men(),
            ["b"] = BombsOut(),
            ["f"] = Core.FlameCells(),
            ["boxes"] = Core.BoxCells(),
            ["pw"] = Core.Drops.Select(d => new { x = Core.X(d.Cell), y = Core.Y(d.Cell), kind = Kind(d.Kind) }).ToArray(),
            ["wins"] = (int[])_wins.Clone(),
            ["phase"] = _phase,
            ["startIn"] = _startIn,
        };
        if (_shrink) f["sh"] = Core.Shrunk;
        var ev = Feed();
        if (ev is not null) f["ev"] = ev;
        return f;
    }

    public override object View(int? seat)
    {
        var v = (Dictionary<string, object>)Frame()!;
        v["width"] = Core.Width;
        v["height"] = Core.Height;
        v["sub"] = BomberCore.Sub;
        v["turn"] = null!;                          // бомбер не покроковий: «чия черга» тут не буває
        v["need"] = WinsToTake;
        v["round"] = _round;
        v["limit"] = BomberCore.RoundTicks;         // скільки тиків триває раунд до нічиєї — для годинника над полем
        v["walls"] = Core.BaseWalls;                // рамка, стовпи й лабіринт не міняються — клієнт малює їх раз
        v["map"] = _map.ToString().ToLowerInvariant();
        if (_shrink) v["shrinkAt"] = BomberCore.ShrinkFrom;
        if (_ghosts) v["ghosts"] = true;
        if (_chaos) v["chaos"] = true;
        if (_teams is not null)
        {
            v["teams"] = (int[])_teams.Clone();
            v["ff"] = _ff;
        }
        if (_note is not null) v["note"] = _note;
        if (_sum is not null) v["sum"] = _sum;
        // «🤖 + бот»: кнопку малює core.js; bot — місця ботів цієї партії (у лобі — куди сядуть)
        v["botOffer"] = _solo.Offer(Ctx, BomberCore.Seats);
        v["botWanted"] = _solo.Wanted;
        v["botLvl"] = _solo.LevelKey;
        v["bot"] = _started ? Enumerable.Range(0, BomberCore.Seats).Where(s => _isBot[s]).ToArray() : BotSeats();
        return v;
    }

    /// <summary>Свіжі події для стрічки: <c>[id, як, хто, кого]</c> (як: 0 підірвав, 1 сам себе, 2 свого, 3 помста, 4 стіна).</summary>
    int[][]? Feed()
    {
        List<int[]>? list = null;
        foreach (var (id, at, ev) in _feed)
            if (_clock - at < EventTicks) (list ??= []).Add([id, (int)ev.How, ev.Killer, ev.Victim]);
        return list is null ? null : [.. list];
    }

    object[] BombsOut()
    {
        var list = new object[Core.Bombs.Count];
        for (var i = 0; i < list.Length; i++)
        {
            var b = Core.Bombs[i];
            list[i] = b.Revenge
                ? new { x = Core.X(b.Cell), y = Core.Y(b.Cell), fuse = b.Fuse, rv = 1 }
                : new { x = Core.X(b.Cell), y = Core.Y(b.Cell), fuse = b.Fuse };
        }
        return list;
    }

    /// <summary>
    /// Бомбери по місцях. Поки партія не почалась, «живий» означає «за цим місцем хтось сидить» — щоб
    /// стіл у лобі показував, кого вже чекати, а не порожні кути.
    /// </summary>
    object[] Men()
    {
        var men = new object[BomberCore.Seats];
        for (var i = 0; i < men.Length; i++)
        {
            var p = Core.Players[i];
            var m = new Dictionary<string, object>
            {
                ["x"] = Core.PosX(p),
                ["y"] = Core.PosY(p),
                ["alive"] = _started ? p.Alive : Ctx.Seated(i),
                ["bombs"] = p.Bombs,
                ["range"] = p.Range,
                ["boots"] = p.Boots,
            };
            if (p.Move >= 0) m["mv"] = p.Move;
            if (p.Ghost) m["g"] = p.Revenge ? 1 : 2;
            if (p.Kick) m["k"] = 1;
            if (p.Curse != BomberCurse.None) m["cu"] = p.Curse switch
            {
                BomberCurse.Reverse => "rev",
                BomberCurse.Slow => "slow",
                _ => "bombs",
            };
            men[i] = m;
        }
        return men;
    }

    static string Kind(BomberBonus kind) => kind switch
    {
        BomberBonus.Range => "range",
        BomberBonus.Bomb => "bomb",
        BomberBonus.Kick => "kick",
        BomberBonus.Skull => "skull",
        _ => "boots",
    };
}
