using System.Globalization;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Сільське ралі»: гонки згори на всю трасу для одного–шістьох. Фізика й кола — у <see cref="RallyCore"/>
/// (її двійник біжить у браузері й передбачає свою машину), тут — фази, ввід із міткою тика, вибір машини,
/// кінець гонки, Журнал, рекорди кіл і вид із кадром. Кадр — плаский масив чисел: 25 разів на секунду.
/// </summary>
public sealed class Rally : Game
{
    public const int PhLobby = 0, PhCount = 1, PhRace = 2, PhOver = 3;
    /// <summary>Чисел на машину в кадрі.</summary>
    public const int Stride = 15;

    /// <summary>Машини — косметика: фізика в усіх однакова (spec §9). Порядок — типова машина за місцем.</summary>
    public static readonly (string Id, string Title, string Emoji)[] Cars =
    [
        ("traktor", "Трактор", "🚜"),
        ("zapor", "«Запорожець»", "🚗"),
        ("moped", "Мопед", "🛵"),
        ("viz", "Віз із конем", "🐴"),
        ("motoblok", "Мотоблок", "🛻"),
        ("kopiyka", "«Копійка»", "🚙"),
    ];

    static readonly string[] SeatNames = ["жовтий", "зелений", "рудий", "білий", "синій", "рожевий"];

    public override GameInfo Info { get; } = new(
        "rally", "Сільське ралі", "сільське ралі", GameGroup.Live, 1, RallyCore.Seats,
        TickMs: RallyCore.TickMs,
        Start: StartMode.ByHost,
        Options:
        [
            new GameOption("track", "Траса",
                [("selo", "Село"), ("ozero", "Крижане озеро"), ("nich", "Нічна"), ("kukurudza", "Кукурудзяне поле"),
                 ("yarmarok", "Ярмарок"), ("vesillia", "Весілля"), ("hora", "Гора"), ("random", "Яка випаде")], "selo"),
            new GameOption("laps", "Кіл", [("3", "3 кола"), ("5", "5 кіл"), ("7", "7 кіл")], "3"),
            new GameOption("bots", "Суперники-боти",
                [("0", "без ботів"), ("1", "🤖 Дід Панас: тихо їде"), ("2", "🤖 Дід Панас: жене"), ("3", "🤖 Дід Панас: ас")], "0"),
            new GameOption("live", "Живність", [("0", "без живності"), ("1", "🐔 живність на дорозі")], "0"),
        ],
        Hint: "Гонки згори на всю трасу: трактор проти «запорожця», занос ручником, калюжі, копиці й турбо. Можна й самому — на час");

    string _trackOpt = "selo";
    int _laps = 3;
    RallyTrack _track = RallyTracks.All[0];
    RallyCore? _core;
    RallyLaps? _records;
    int _ph = PhLobby;
    /// <summary>Тиків до кінця гонки після першого фінішера; 0 — фінішера ще нема.</summary>
    int _left;
    int _players;
    bool _solo;
    /// <summary>Машина за ніком: «Ще раз» обертає місця, а машина їде за людиною.</summary>
    readonly Dictionary<string, string> _carByNick = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Хто сидів на старті — для Журналу й рекордів (нік міг уже встати).</summary>
    readonly string?[] _nicks = new string?[RallyCore.Seats];
    object[] _top = [];
    object[]? _results;
    /// <summary>Новий рекорд траси цієї гонки — Глек скаже про нього раз, наприкінці; Was — чий рекорд перебили.</summary>
    (string Nick, int Ms, string? WasNick, int WasMs)? _record;
    /// <summary>Ctx.Round, у якому стартувала гонка: інший — стіл відкрили наново, гонка вже не наша.</summary>
    int _round = -1;
    /// <summary>Скільки машин стояло на трасі, коли загорілось зелене (ачівка «Перший на селі»).</summary>
    int _atGreen;
    /// <summary>Тик, коли востаннє хтось крутив кермо, тиснув педалі, гудок чи ↺ (spec §2.2, «ніхто не їде»).</summary>
    int _heardT;
    readonly int[] _masks = new int[RallyCore.Seats];
    /// <summary>Чим скінчилась гонка без фінішера: стеля 4 хвилини чи тиша за кермом.</summary>
    int _why;
    const int WhyLong = 1, WhyIdle = 2;
    /// <summary>Тиків без жодного вводу, після яких гонку знімають (30 с).</summary>
    public const int IdleTicks = 750;

    /// <summary>Для тестів і перевірок: ядро поточної гонки (null — ще лобі), фаза, тики до таймауту, траса.</summary>
    public RallyCore? Core => _core;
    public int Phase => _ph;
    public int Left => _left;
    public RallyTrack Track => _track;

    public override string SeatName(int seat) => seat >= 0 && seat < SeatNames.Length ? SeatNames[seat] : $"гравець {seat + 1}";

    public override bool ActsInLobby => true;

    /// <summary>Скільки машин разом із ботами (№87): Дід Панас із компанією добирають вільні місця до чотирьох.</summary>
    public const int BotsUpTo = 4;
    /// <summary>Боти: ім'я, машина, стеля швидкості для складності «тихо їде».</summary>
    public static readonly (string Name, string Car)[] BotCrew =
        [("🤖 Дід Панас", "traktor"), ("🤖 Баба Параска", "viz"), ("🤖 Кум Степан", "motoblok")];
    /// <summary>Стеля газу пілота за складністю (sub/тик; 0 — без стелі): 1 — тихо їде, 2 — жене, 3 — ас.</summary>
    static readonly int[] BotCap = [0, 600, 740, 0];
    int _botLevel;
    /// <summary>Живі перешкоди (№89): курка через дорогу, гуси на Селі, віз на Ярмарку — щоразу з іншим зерном.</summary>
    bool _live;
    readonly bool[] _bot = new bool[RallyCore.Seats];
    int _bots;
    RallyPilot? _pilot;
    /// <summary>Тиків поспіль, коли бот стоїть (застряг) — повертаємо його на трасу, як людина тисне ↺.</summary>
    readonly int[] _botSlow = new int[RallyCore.Seats];

    public bool IsBot(int seat) => _bot[seat];

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _botLevel = options.TryGetValue("bots", out var b) && int.TryParse(b, out var bl) && bl is >= 0 and <= 3 ? bl : 0;
        _live = options.TryGetValue("live", out var lv) && lv == "1";
        _trackOpt = options.TryGetValue("track", out var t) && (t == "random" || RallyTracks.Ids.Contains(t)) ? t : "selo";
        _laps = options.TryGetValue("laps", out var l) && int.TryParse(l, out var n) && n is 3 or 5 or 7 ? n : 3;
        _track = RallyTracks.Get(_trackOpt);
        // сервіс створюється тут, поза замком кімнати: перший виклик читає рекорди зі сховища
        _records = Ctx.Services.GetService(typeof(RallyLaps)) as RallyLaps;
        RefreshTop();
    }

    public override void Start()
    {
        if (_trackOpt == "random") _track = RallyTracks.All[Ctx.Rng.Next(RallyTracks.All.Length)];
        _core = new RallyCore(_track, _laps);
        if (_live && _track.Critters.Length > 0) _core.Live = Ctx.Rng.Next(1, 9973);
        _players = 0;
        for (var i = 0; i < RallyCore.Seats; i++)
        {
            _nicks[i] = Ctx.NickOf(i);
            if (_nicks[i] is null) continue;
            _players++;
            // машина їде за людиною й тоді, коли її не обирали: «Ще раз» обертає місця, але трактор лишається трактором
            var car = CarOf(i);
            _carByNick[_nicks[i]!] = car;
            _core.Grid(i, car);
        }
        // Дід Панас із компанією — на вільні місця, поки машин не стане чотири (без нагород, рекордів і ачівок)
        Array.Clear(_bot);
        Array.Clear(_botSlow);
        _bots = 0;
        _pilot = null;
        if (_botLevel > 0)
            for (var i = 0; i < RallyCore.Seats && _players + _bots < BotsUpTo; i++)
            {
                if (_nicks[i] is not null) continue;
                var (name, car) = BotCrew[_bots];
                _nicks[i] = name;
                _bot[i] = true;
                _bots++;
                _core.Grid(i, car);
            }
        if (_bots > 0) _pilot = new RallyPilot(_track) { Cap = BotCap[_botLevel], Care = _botLevel == 3 ? 1 : 2 };
        _core.Rank();
        _solo = _players == 1 && _bots == 0;
        _ph = PhCount;
        _round = Ctx.Round;
        _simAt = Ctx.Clock.UtcNow;
        Array.Clear(_ev);
        Array.Clear(_masks);
        _left = 0;
        _atGreen = 0;
        _heardT = 0;
        _why = 0;
        _results = null;
        _record = null;
        _picked = false;
        _photo = null;
        RefreshTop();
    }

    /// <summary>
    /// Дограний стіл, за який сів новий гравець, каркас сам переводить у лобі (Rooms.Join, reopen) і збільшує
    /// Round, а гру про це не питає. Тоді минула гонка вже не наша: лобі з решіткою й вибором машини, без старих
    /// результатів і без машини того, хто встав. «Ще раз» теж збільшує Round, але одразу кличе Start().
    /// </summary>
    void Settle()
    {
        if (_ph == PhLobby || Ctx.Round == _round) return;
        _core = null;
        _ph = PhLobby;
        _results = null;
        _record = null;
        _left = 0;
        _players = 0;
        _solo = false;
        Array.Clear(_nicks);
        Array.Clear(_bot);
        _bots = 0;
        _pilot = null;
        RefreshTop();
    }

    string CarOf(int seat)
    {
        var nick = Ctx.NickOf(seat);
        return nick is not null && _carByNick.TryGetValue(nick, out var car) ? car : Cars[seat].Id;
    }

    void RefreshTop()
    {
        if (_records is null || (_trackOpt == "random" && _core is null))
        {
            _top = [];
            return;
        }
        _top = [.. _records.Top(_track.Id, 10).Select(r => (object)new { nick = r.Nick, ms = r.Ms, car = r.Car })];
    }

    // ---------- ввід ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (seat < 0 || seat >= RallyCore.Seats) return ActResult.Fail("Ти тут не граєш");
        Settle();
        switch (action)
        {
            case "car":
            {
                if (_ph != PhLobby) return ActResult.Fail("Посеред гонки не пересідають");
                var car = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("car", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString() : null;
                var found = Array.FindIndex(Cars, x => x.Id == car);
                if (found < 0) return ActResult.Fail("Такої машини в селі нема");
                if (Ctx.NickOf(seat) is not { } nick) return ActResult.Fail("Ти тут не граєш");
                _carByNick[nick] = Cars[found].Id;
                return ActResult.Accept($"{Cars[found].Emoji} {Cars[found].Title} — твоя");
            }
            case "track":
                return PickTrack(payload);
            case "garage":
                return Garage(seat, payload);
            case "ctl":
            {
                if (_core is null || _ph is not (PhCount or PhRace)) return ActResult.Fail("Гонка ще не почалась");
                if (!Read(payload, out var t, out var k)) return ActResult.Fail("Кривий ввід");
                var car = _core.Cars[seat];
                if (!car.Present) return ActResult.Fail("Твоєї машини на трасі вже нема");
                // тик вводу не спадає: пізніша маска не має лягти раніше за попередню
                if (t < car.It) t = car.It;
                _core.Schedule(seat, t, k);
                return ActResult.Done;
            }
            case "reset":
            {
                if (_core is null || _ph != PhRace) return ActResult.Fail("Гонка ще не почалась");
                var car = _core.Cars[seat];
                if (!car.Present || car.Ghost) return ActResult.Fail("Ти вже фінішував — катайся як хочеш");
                if (car.ResetCd > 0) return ActResult.Fail("Щойно ж повертали — зачекай");
                _core.Respawn(seat);
                return ActResult.Done;
            }
            case "horn":
            {
                if (_core is null || _ph is not (PhCount or PhRace)) return ActResult.Fail("Гонка ще не почалась");
                var car = _core.Cars[seat];
                if (!car.Present) return ActResult.Fail("Твоєї машини на трасі вже нема");
                if (car.HornCd > 0) return ActResult.Fail("Не сигналь так часто");
                _core.Honk(seat);
                return ActResult.Done;
            }
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    /// <summary>Скільки тиків відліку після «Ще раз» можна ще перекинути гонку на іншу трасу (1 с — червоне світло).</summary>
    public const int PickTicks = 25;
    /// <summary>Трасу цієї гонки вже обрали з підсумку — вдруге не перекидаємо (двоє клацнули різне — хто перший).</summary>
    bool _picked;

    /// <summary>
    /// Вибір траси між гонками (прохід №3, №85): у підсумку людина клацає трасу — клієнт тисне «Ще раз» каркаса і
    /// тут же шле <c>track</c>; на першій секунді відліку гонка перебудовується на обраній трасі. Далі «Ще раз» їде
    /// туди ж, поки хтось не обере іншу. 🎲 — «яка випаде» щоразу.
    /// </summary>
    ActResult PickTrack(JsonElement payload)
    {
        var id = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("track", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() : null;
        if (id is null || (id != "random" && !RallyTracks.Ids.Contains(id))) return ActResult.Fail("Такої траси в селі нема");
        if (_core is null || _ph != PhCount || _core.T > PickTicks || _picked) return ActResult.Fail("Гонка вже рушила — трасу обереш наступного разу");
        _trackOpt = id;
        _track = RallyTracks.Get(id);
        Start();
        _picked = true;
        return ActResult.Accept(id == "random" ? "🎲 Яка випаде — така й буде" : $"Їдемо: {_track.Title}");
    }

    /// <summary>Фарб у гаражі (клієнт знає їхні кольори за номером); −1 — колір місця, як було.</summary>
    public const int Paints = 12;
    /// <summary>Найдовший напис на номері.</summary>
    public const int PlateMax = 6;
    /// <summary>Гараж за ніком (№91): фарба й напис на номері їдуть за людиною, як і машина.</summary>
    readonly Dictionary<string, (int Paint, string Plate)> _garage = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// «🎨 Гараж»: фарба машини (0..11, −1 — колір місця) і напис на номері до шести літер/цифр. У лобі й на відліку
    /// (підсумок каркас до гри не пускає: клієнт пам'ятає вибір і шле його на відліку «Ще раз»). Без тосту.
    /// </summary>
    ActResult Garage(int seat, JsonElement payload)
    {
        if (_ph is not (PhLobby or PhCount)) return ActResult.Fail("Посеред гонки в гараж не заїжджають");
        if (Ctx.NickOf(seat) is not { } nick) return ActResult.Fail("Ти тут не граєш");
        if (payload.ValueKind != JsonValueKind.Object) return ActResult.Fail("Кривий гараж");
        var paint = payload.TryGetProperty("paint", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var pv) ? pv : -2;
        if (paint < -1 || paint >= Paints) return ActResult.Fail("Такої фарби в селі нема");
        var raw = payload.TryGetProperty("plate", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
        if (Plate(raw) is not { } plate) return ActResult.Fail($"На номері — до {PlateMax} літер і цифр");
        _garage[nick] = (paint, plate);
        return ActResult.Done;
    }

    /// <summary>Напис на номері: великі літери, цифри, пробіл і дефіс, до шести; інакше null.</summary>
    public static string? Plate(string raw)
    {
        var t = raw.Trim().ToUpperInvariant();
        if (t.Length > PlateMax) return null;
        foreach (var ch in t)
            if (!char.IsLetterOrDigit(ch) && ch != ' ' && ch != '-') return null;
        return t;
    }

    /// <summary>{ t: ціле, k: 0..31 } — інакше це не ввід, а сміття.</summary>
    static bool Read(JsonElement payload, out int t, out int k)
    {
        t = k = 0;
        if (payload.ValueKind != JsonValueKind.Object) return false;
        if (!payload.TryGetProperty("t", out var tv) || tv.ValueKind != JsonValueKind.Number || !tv.TryGetInt32(out t)) return false;
        if (!payload.TryGetProperty("k", out var kv) || kv.ValueKind != JsonValueKind.Number || !kv.TryGetInt32(out k)) return false;
        return k is >= 0 and <= 31;
    }

    // ---------- тик ----------

    /// <summary>Найбільше кроків симуляції за один виклик Tick(): довший борг прощаємо (сервер спав).</summary>
    public const int MaxSteps = 3;
    /// <summary>Годинник, до якого дорахована симуляція: мить Start() + T · 40 мс.</summary>
    DateTimeOffset _simAt;
    /// <summary>Події машин за всі кроки цього виклику — у кадр (між кадрами крок міг бути не один).</summary>
    readonly int[] _ev = new int[RallyCore.Seats];

    /// <summary>
    /// Каркас будить кімнату кроком 20 мс, а зерно таймера Windows — ~15,6 мс, тож 40-мс тик насправді приходить
    /// раз на 47–63 мс, і гонка йшла б у півтора раза повільніше за задумане. Ралі тримає свої 25 кроків на
    /// секунду само: за виклик — стільки кроків, скільки набіг час (1–3). Пропущене понад це не надолужуємо.
    /// У тестах годинник іде рівно на 40 мс за тик — там завжди один крок.
    /// </summary>
    public override TickResult Tick()
    {
        if (_core is null || _ph is PhLobby or PhOver) return TickResult.None;
        var now = Ctx.Clock.UtcNow;
        var steps = (int)((now - _simAt).Ticks / (RallyCore.TickMs * TimeSpan.TicksPerMillisecond));
        if (steps > MaxSteps)
        {
            _simAt = now - TimeSpan.FromMilliseconds(RallyCore.TickMs * MaxSteps);
            steps = MaxSteps;
        }
        if (steps < 1) steps = 1;
        _simAt += TimeSpan.FromMilliseconds(RallyCore.TickMs * steps);
        Array.Clear(_ev);
        bool frame = false, view = false;
        for (var n = 0; n < steps; n++)
        {
            var r = Step();
            for (var i = 0; i < RallyCore.Seats; i++) _ev[i] |= _core.Cars[i].Ev;
            frame |= r.Frame;
            view |= r.View;
            if (_ph == PhOver) break;
        }
        return new TickResult(frame, view);
    }

    /// <summary>Один крок симуляції (40 мс ігрового часу): фізика, кола, рекорди, кінець гонки.</summary>
    TickResult Step()
    {
        var core = _core!;
        if (_pilot is not null) Drive(core);
        core.Tick();
        Heard(core);
        if (_ph == PhCount)
        {
            if (core.T >= RallyCore.CountTicks)
            {
                _ph = PhRace;
                for (var i = 0; i < RallyCore.Seats; i++) _atGreen += core.Cars[i].Present && !_bot[i] ? 1 : 0;
            }
            var s = RallyCore.CountTicks - core.T;
            var any = false;
            for (var i = 0; i < RallyCore.Seats; i++) any |= core.Cars[i].Ev != 0;
            return s % 5 == 0 || any ? TickResult.FrameOnly : TickResult.None;
        }

        var view = false;
        var fresh = false;
        if (core.AnyLap)
            for (var i = 0; i < RallyCore.Seats; i++)
            {
                var c = core.Cars[i];
                if (!c.Present || (c.Ev & RallyCore.EvLap) == 0) continue;
                view |= Lap(i, c);
                if (c.Fin > 1 && _photo is null) view |= Photo(i, c);
                if (c.Fin == 1)
                {
                    view = true;
                    if (!_solo)
                    {
                        _left = RallyCore.TimeoutTicks;
                        fresh = true;
                        Ctx.Say($"🏁 {Nick(i)} — перше місце на фініші, {Clock(c.FinishMs, 1)}!");
                    }
                }
            }

        if (AllFinished()) return Over();
        // люди всі доїхали, а боти ще в дорозі — чекаємо їх лише 5 с, а не 20
        if (_bots > 0 && HumansFinished() && (_left == 0 || _left > BotWaitTicks)) _left = BotWaitTicks;
        // таймаут рахуємо з наступного тика після фінішу: 500 повних тиків решті
        if (!fresh && _left > 0 && --_left == 0) return Over();
        if (core.T - RallyCore.CountTicks >= RallyCore.MaxRaceTicks)
        {
            _why = WhyLong;
            return Over();
        }
        // усі відійшли від клавіатури: пів хвилини ні керма, ні педалей — не чекаємо стелі в 4 хвилини
        if (core.T - Math.Max(_heardT, RallyCore.CountTicks) >= IdleTicks)
        {
            _why = WhyIdle;
            return Over();
        }
        return view ? TickResult.Both : TickResult.FrameOnly;
    }

    /// <summary>Боти кермують: маска пілота на наступний тик; застряг на 4 с — назад на трасу.</summary>
    void Drive(RallyCore core)
    {
        for (var i = 0; i < RallyCore.Seats; i++)
        {
            if (!_bot[i]) continue;
            var c = core.Cars[i];
            if (!c.Present) continue;
            core.Schedule(i, core.T + 1, _pilot!.Mask(core, i));
            if (_ph != PhRace || c.Ghost) continue;
            _botSlow[i] = Math.Abs(c.VF) < 60 ? _botSlow[i] + 1 : 0;
            if (_botSlow[i] >= 100 && c.ResetCd == 0)
            {
                _botSlow[i] = 0;
                core.Respawn(i);
            }
        }
    }

    /// <summary>
    /// Хтось із присутніх змінив маску (кермо, газ, гальмо, ручник), сигналив чи повертався на трасу — гонку
    /// ведуть. Дивимось на чинні маски ядра, а не на дії: так рахується й ввід, що приїхав наперед у кільце.
    /// </summary>
    void Heard(RallyCore core)
    {
        for (var i = 0; i < RallyCore.Seats; i++)
        {
            var c = core.Cars[i];
            if (!c.Present || _bot[i]) continue;
            if (c.Mask != _masks[i] || (c.Ev & (RallyCore.EvHorn | RallyCore.EvReset)) != 0) _heardT = core.T;
            _masks[i] = c.Mask;
        }
    }

    /// <summary>Коло в рекорди траси; true — змінилась десятка у виді.</summary>
    bool Lap(int seat, RallyCar c)
    {
        if (_records is null || _bot[seat] || Nick(seat) is not { } nick) return false;
        // хто тримав рекорд до цього кола — щоб Глек сказав, чий рекорд упав (кола рідкі, копія з одного — дрібниця)
        var top = _records.Top(_track.Id, 1);
        var (rank, beat) = _records.Post(_track.Id, nick, c.Car, c.LastMs, Ctx.Clock.UtcNow);
        if (rank == 0) return false;
        if (rank == 1)
        {
            // той самий нік покращив уже свій рекорд цієї гонки — лишаємо, чий рекорд він побив першим
            var (wasNick, wasMs) = _record is { } r && string.Equals(r.Nick, nick, StringComparison.OrdinalIgnoreCase)
                ? (r.WasNick, r.WasMs)
                : top.Count > 0 && !string.Equals(top[0].Nick, nick, StringComparison.OrdinalIgnoreCase) ? (top[0].Nick, top[0].Ms) : (null, 0);
            _record = (nick, c.LastMs, wasNick, wasMs);
        }
        if (beat) Ctx.Award(seat, 0, "ach:rally-record");
        if (rank <= 10) RefreshTop();
        return rank <= 10;
    }

    /// <summary>Різниця на фініші, менша за цю (мс), — «📸 Фотофініш» (№88).</summary>
    public const int PhotoMs = 300;
    /// <summary>Перша за гонку пара сусідів на фініші ближче ніж 0,3 с: хто, за ким, на скільки мс, на якому тику.</summary>
    (int A, int B, int Gap, int T)? _photo;

    /// <summary>Фінішер за місцем Fin−1 доїхав менше ніж на 0,3 с раніше — клієнти покажуть уповільнений фотофініш.</summary>
    bool Photo(int seat, RallyCar c)
    {
        var core = _core!;
        for (var i = 0; i < RallyCore.Seats; i++)
        {
            var p = core.Cars[i];
            if (i == seat || !p.Present || p.Fin != c.Fin - 1) continue;
            var gap = c.FinishMs - p.FinishMs;
            if (gap >= PhotoMs) return false;
            _photo = (i, seat, gap, core.T);
            return true;
        }
        return false;
    }

    string? Nick(int seat) => _nicks[seat] ?? Ctx.NickOf(seat);

    /// <summary>Скільки ще чекаємо ботів, коли всі люди вже на фініші (5 с).</summary>
    public const int BotWaitTicks = 125;

    bool HumansFinished()
    {
        var any = false;
        for (var i = 0; i < RallyCore.Seats; i++)
        {
            var c = _core!.Cars[i];
            if (!c.Present || _bot[i]) continue;
            any = true;
            if (c.Fin == 0) return false;
        }
        return any;
    }

    bool AllFinished()
    {
        var any = false;
        for (var i = 0; i < RallyCore.Seats; i++)
        {
            var c = _core!.Cars[i];
            if (!c.Present) continue;
            any = true;
            if (c.Fin == 0) return false;
        }
        return any;
    }

    /// <summary>Кінець гонки: результати, Журнал, репліка Глека про рекорд, ачівка переможцю, Finish.</summary>
    TickResult Over()
    {
        var core = _core!;
        _ph = PhOver;
        core.Rank();
        var rows = new List<object>();
        var ranked = new List<int>();
        for (var p = 0; p < RallyCore.Seats; p++)
        {
            var s = core.Order[p];
            var c = core.Cars[s];
            if (!c.Present) continue;
            ranked.Add(s);
            rows.Add(new { seat = s, fin = c.Fin, ms = c.Fin > 0 ? c.FinishMs : 0, best = c.BestMs, laps = Math.Min(c.Lap, _laps) });
        }
        _results = [.. rows];

        int[] winners;
        var first = ranked.Count > 0 && core.Cars[ranked[0]].Fin > 0 ? ranked[0] : -1;
        if (_bots > 0)
        {
            // з ботами переможець у черепках — найкраща людина на фініші, і лише коли людей хоча б двоє
            var humans = 0;
            var bestHuman = -1;
            foreach (var s in ranked)
                if (!_bot[s])
                {
                    humans++;
                    if (bestHuman < 0 && core.Cars[s].Fin > 0) bestHuman = s;
                }
            var log0 = LogLine(ranked, first, first >= 0 ? [first] : []);
            var rec0 = RecordLine();
            if (rec0 is not null) Ctx.Say($"⏱ Новий рекорд «{_track.Title}»: {rec0}!");
            if (humans >= 3 && bestHuman >= 0 && _atGreen >= 3) Ctx.Award(bestHuman, 0, "ach:rally-win3");
            Ctx.Finish(humans >= 2 && bestHuman >= 0 ? [bestHuman] : [], rec0 is null ? log0 : $"{log0} · новий рекорд траси: {rec0}");
            return TickResult.Both;
        }
        if (first >= 0) winners = [first];
        else if (ranked.Count > 0)
        {
            // ніхто не доїхав: веде той, хто найдалі; двоє рівно — нічия; ніхто й перших воріт не взяв — теж нічия
            var lead = ranked[0];
            var tie = ranked.Count > 1 && core.Passed(core.Cars[ranked[1]]) == core.Passed(core.Cars[lead])
                && core.GateDist(core.Cars[ranked[1]]) == core.GateDist(core.Cars[lead]);
            winners = tie || core.Passed(core.Cars[lead]) == 0 ? [] : [lead];
        }
        else winners = [];

        var recLine = RecordLine();
        if (recLine is not null) Ctx.Say($"⏱ Новий рекорд «{_track.Title}»: {recLine}!");
        // «Перший на селі» — лише справжня гонка: троє на зеленому світлі й хоч один суперник не встав до кінця
        if (!_solo && first >= 0 && _atGreen >= 3 && ranked.Count >= 2) Ctx.Award(first, 0, "ach:rally-win3");
        var log = LogLine(ranked, first, winners);
        Ctx.Finish(_solo ? [] : winners, recLine is null || ranked.Count == 0 ? log : $"{log} · новий рекорд траси: {recLine}");
        return TickResult.Both;
    }

    /// <summary>«Оля, 0:10,46» або «Оля, 0:10,46 (було — Петро, 0:10,98)»; null — рекорду в цій гонці не було.</summary>
    string? RecordLine() => _record is not { } rec ? null
        : rec.WasNick is null ? $"{rec.Nick}, {Clock(rec.Ms, 2)}"
        : $"{rec.Nick}, {Clock(rec.Ms, 2)} (було — {rec.WasNick}, {Clock(rec.WasMs, 2)})";

    string LogLine(List<int> ranked, int first, int[] winners)
    {
        var core = _core!;
        var head = $"{Info.Title} · {_track.Title}, {LapsWord(_laps)}";
        if (ranked.Count == 0) return $"{Info.Title}: всі роз'їхались";
        if (_solo)
        {
            var c = core.Cars[ranked[0]];
            var best = c.BestMs > 0 ? $", найкраще коло {Clock(c.BestMs, 2)}" : "";
            var none = _why == WhyIdle ? "пів хвилини без керма, фінішу нема" : "4 хвилини минули, фінішу нема";
            return c.Fin > 0
                ? $"{head}: {Nick(ranked[0])} наодинці з секундоміром — {Clock(c.FinishMs, 1)}{best}"
                : $"{head}: {Nick(ranked[0])} наодинці з секундоміром — {none}{best}";
        }
        if (first < 0)
        {
            var why = _why == WhyIdle ? "пів хвилини ніхто не торкався керма" : "за 4 хвилини ніхто не доїхав";
            if (winners.Length == 0) return $"{Info.Title} · {_track.Title}: {why} — нічия";
            var lead = core.Cars[winners[0]];
            return $"{Info.Title} · {_track.Title}: {why} — найдалі {Nick(winners[0])}, {LapsWord(Math.Min(lead.Lap, _laps))} з {_laps}";
        }
        var parts = new List<string>();
        var winMs = core.Cars[first].FinishMs;
        var place = 0;
        foreach (var s in ranked)
        {
            var c = core.Cars[s];
            if (c.Fin == 0)
            {
                parts.Add($"{Nick(s)} — без фінішу");
                continue;
            }
            place++;
            var medal = place switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => $"{place}-й" };
            parts.Add(place == 1 ? $"{medal} {Nick(s)} {Clock(c.FinishMs, 1)}" : $"{medal} {Nick(s)} +{Gap(c.FinishMs - winMs)}");
        }
        var bestSeat = -1;
        foreach (var s in ranked)
        {
            var c = core.Cars[s];
            if (c.BestMs > 0 && (bestSeat < 0 || c.BestMs < core.Cars[bestSeat].BestMs)) bestSeat = s;
        }
        var tail = bestSeat >= 0 ? $" · найкраще коло — {Nick(bestSeat)} {Clock(core.Cars[bestSeat].BestMs, 2)}" : "";
        return $"{head}: {string.Join(" · ", parts)}{tail}";
    }

    static string LapsWord(int n) => n switch { 1 => "1 коло", 2 or 3 or 4 => $"{n} кола", _ => $"{n} кіл" };   // 0 кіл, 5 кіл, 7 кіл

    /// <summary>м:сс,д (digits = 1) або м:сс,сс (digits = 2) — десяті чи соті частки відкидаємо, не округлюємо.</summary>
    public static string Clock(int ms, int digits)
    {
        if (ms < 0) ms = 0;
        var m = ms / 60000;
        var s = ms / 1000 % 60;
        var frac = digits == 1 ? (ms % 1000 / 100).ToString(CultureInfo.InvariantCulture) : (ms % 1000 / 10).ToString("00", CultureInfo.InvariantCulture);
        return $"{m}:{s:00},{frac}";
    }

    /// <summary>Відставання «1,2» — секунди з десятими.</summary>
    public static string Gap(int ms) => $"{Math.Max(0, ms) / 1000},{Math.Max(0, ms) % 1000 / 100}";

    // ---------- вихід ----------

    public override void OnLeave(int seat)
    {
        if (_core is null || _ph is PhLobby or PhOver) return;
        var nick = Nick(seat);
        _core.Drop(seat);
        // теперішній час — рід ніка невідомий («зійшов/зійшла»), а «сход» — калька
        Ctx.Log($"{Info.Title}: {nick} сходить з траси");
        var any = false;
        // самі боти гонку не доїжджають: людей не лишилось — роз'їхались
        for (var i = 0; i < RallyCore.Seats; i++) any |= i != seat && _core.Cars[i].Present && !_bot[i];
        if (!any)
        {
            _ph = PhOver;
            _results = [];
            Ctx.Finish([], $"{Info.Title}: всі роз'їхались");
            return;
        }
        if (AllFinished()) Over();
    }

    string?[] BotNames()
    {
        var names = new string?[RallyCore.Seats];
        for (var i = 0; i < RallyCore.Seats; i++) names[i] = _bot[i] ? _nicks[i] : null;
        return names;
    }

    // ---------- вид і кадр ----------

    public override object? Frame()
    {
        Settle();
        if (_core is null) return LobbyFrame();
        var core = _core;
        var c = new int[RallyCore.Seats * Stride];
        var r = new int[RallyCore.Seats];
        for (var i = 0; i < RallyCore.Seats; i++)
        {
            var car = core.Cars[i];
            var o = i * Stride;
            r[i] = core.Place[i];
            if (!car.Present)
            {
                c[o + 10] = -1;
                continue;
            }
            c[o] = car.X;
            c[o + 1] = car.Y;
            c[o + 2] = car.A;
            c[o + 3] = car.VF;
            c[o + 4] = car.VL;
            c[o + 5] = car.Mask;
            c[o + 6] = car.Lap;
            c[o + 7] = car.Next;
            c[o + 8] = _ev[i];
            c[o + 9] = car.Timers;
            c[o + 10] = car.Fin;
            c[o + 11] = car.BestMs;
            c[o + 12] = car.LastMs;
            c[o + 13] = car.Lt;
            c[o + 14] = car.It;
        }
        var s = _ph == PhCount ? RallyCore.CountTicks - core.T : _ph == PhRace ? _left : 0;
        return new RallyFrame(core.T, _ph, s, c, r);
    }

    /// <summary>Лобі: машини на слотах тих, хто вже сів.</summary>
    RallyFrame LobbyFrame()
    {
        var c = new int[RallyCore.Seats * Stride];
        var r = new int[RallyCore.Seats];
        var place = 0;
        for (var i = 0; i < RallyCore.Seats; i++)
        {
            var o = i * Stride;
            if (!Ctx.Seated(i))
            {
                c[o + 10] = -1;
                continue;
            }
            c[o] = _track.SlotX[i];
            c[o + 1] = _track.SlotY[i];
            c[o + 2] = _track.Heading;
            c[o + 7] = 1;
            r[i] = ++place;
        }
        return new RallyFrame(0, PhLobby, 0, c, r);
    }

    public override object View(int? seat)
    {
        Settle();
        var cars = new string?[RallyCore.Seats];
        for (var i = 0; i < RallyCore.Seats; i++)
            cars[i] = _core is null
                ? (Ctx.Seated(i) ? CarOf(i) : null)
                : _core.Cars[i].Present ? _core.Cars[i].Car : null;
        var paint = new int[RallyCore.Seats];
        var plates = new string?[RallyCore.Seats];
        for (var i = 0; i < RallyCore.Seats; i++)
        {
            paint[i] = -1;
            if (cars[i] is null || Ctx.NickOf(i) is not { } nick || !_garage.TryGetValue(nick, out var g)) continue;
            paint[i] = g.Paint;
            plates[i] = g.Plate.Length > 0 ? g.Plate : null;
        }
        return new
        {
            turn = (int?)null,
            ph = _ph,
            laps = _laps,
            random = _trackOpt == "random",
            track = _track.Wire,
            cars,
            paint,
            plates,
            bots = _bots > 0 ? BotNames() : null,
            live = _core?.Live ?? 0,
            records = _top,
            results = _ph == PhOver ? _results : null,
            photo = _photo is { } ph && _ph is PhRace or PhOver ? new[] { ph.A, ph.B, ph.Gap, ph.T } : null,
            f = Frame(),
        };
    }
}

/// <summary>Кадр ралі на дроті: { t, ph, s, c: int[90], r: int[6] }.</summary>
public sealed record RallyFrame(int T, int Ph, int S, int[] C, int[] R);
