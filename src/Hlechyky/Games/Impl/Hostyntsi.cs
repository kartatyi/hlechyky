using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Гостинці: з гори спільним жолобом котяться гостинці, гравці стоять уздовж нього, і кожен тисне одну кнопку
/// «Хапай», коли гостинець пропливає навпроти. Глек +1, розписний +3, золотий +5 (котиться швидше), жар −3 й опік
/// на 2 с, гнилий гарбуз −1, кіт у мішку — навмання −3…+5. Кошик на 8 місць: повний більше не хапає, тож дрібноту
/// варто пропускати. Хто вище — бачить першим; щораунду місця обертаються. 3 раунди по 40 с, у вечірці — 1.
/// Spec: <c>docs/games/specs/hostyntsi.md</c>.
/// </summary>
public sealed class Hostyntsi : Game, IPartyMinigame
{
    public const int Seats = 8;
    public const int TickMs = 50;
    /// <summary>Фази (у кадрі <c>ph</c>): 0 готуйсь, 1 котиться, 2 кінець раунду (кошики висипають), 3 зіграно, 4 лобі.</summary>
    public const int PhReady = 0, PhGo = 1, PhEnd = 2, PhOver = 3, PhLobby = 4;
    /// <summary>«Готуйсь» 3 с, раунд 40 с, кінець раунду 3 с — у тиках по 50 мс.</summary>
    public const int ReadyTicks = 60, RoundTicks = 800, EndTicks = 60;
    public const int Rounds = 3;

    /// <summary>
    /// Жолоб — відрізок <c>0..L</c> умовних одиниць (згори вниз). Станції гравців — через <see cref="Spacing"/>,
    /// перша на <c>Spacing</c>, остання на <c>n·Spacing</c>, низ жолоба <c>L = (n+1)·Spacing</c>. Вікно станції —
    /// <c>центр ± HalfWin</c>: вікна не перетинаються, тож гостинець щомиті навпроти щонайбільше одного гравця.
    /// </summary>
    public const int Spacing = 1000, HalfWin = 250;
    /// <summary>
    /// Запас на джитер: натиск зараховується, якщо гостинець пішов нижче вікна не далі, ніж проїхав би за 150 мс
    /// (браузер бачив його у вікні, а натиск ішов мережею). Навіть найшвидший золотий (2550 од/с) з цим запасом
    /// не дістає вікна наступного гравця: 250 + 383 &lt; 750.
    /// </summary>
    public const int JitterMs = 150;
    public const int JitterTicks = JitterMs / TickMs;
    public const int BasketMax = 8;
    /// <summary>Опік — 2 с без хапання; промах — 0,4 с «руки порожні» (щоб не тиснути навмання); після хапу — 0,15 с.</summary>
    public const int BurnTicks = 40, MissLock = 8, GrabLock = 3;
    /// <summary>Скільки тиків подія живе в кадрі: кадри з пачки губляться, тож клієнт бачить кожну кілька разів і зводить за ключем.</summary>
    public const int EvKeep = 20;

    /// <summary>Гостинці: глек, розписний, золотий, жар, гнилий гарбуз, кіт у мішку.</summary>
    public const int Jug = 0, Painted = 1, Gold = 2, Ember = 3, Pumpkin = 4, Cat = 5;
    public static readonly int[] Value = [1, 3, 5, -3, -1, 0];
    /// <summary>Частота появи на 100.</summary>
    public static readonly int[] Weight = [34, 18, 7, 15, 14, 12];
    public const double GoldMul = 1.5;
    public const int CatMin = -3, CatMax = 5;

    static readonly string[] KindNames = ["глек", "розписний глек", "золотий глек", "жар", "гнилий гарбуз", "кіт у мішку"];
    static readonly string[] NikNames = ["подарунок", "великий подарунок", "золотий подарунок", "різочка", "гнилий гарбуз", "кіт у мішку"];
    static readonly string[] Names = ["синій", "рудий", "зелений", "жовтий", "бузковий", "м’ятний", "рожевий", "сірий"];

    /// <summary>Події в кадрі (<c>ev</c>): хап, промах, жар (хап із опіком), висипав кошик.</summary>
    public const int EvGrab = 1, EvMiss = 2, EvBurn = 3, EvDump = 4;

    public override GameInfo Info { get; } = new(
        "hostyntsi", "Гостинці", "гостинці", GameGroup.Live, 1, Seats, TickMs: TickMs,
        Start: StartMode.ByHost, Options: [LiveBots.LevelOption],
        Hint: "З гори котяться гостинці — хапай, коли пливе навпроти. Кошик на вісім: дрібноту пропускай, жару не чіпай. Самому — з 🤖 ботами");

    /// <summary>
    /// Двоє ботів, коли людина сама: на спільному жолобі сенс — у сусідах (хто вище, той забирає перший), а троє
    /// за три раунди рівно по разу стоять угорі, посередині й унизу.
    /// </summary>
    public const int SoloBots = 2;

    public sealed class Gift
    {
        public int Id, Kind;
        public double D, Mul;
    }

    public sealed class Player
    {
        public bool Plays, Left;
        public int Station = -1;
        /// <summary>Станція до останньої зміни: натиск, що летів мережею під час зсуву, судимо й за нею.</summary>
        public int Prev = -1;
        public readonly List<(int Kind, int Val)> Basket = [];
        /// <summary>Штраф раунду за жар (жар місця в кошику не займає — його впускаєш, а пече).</summary>
        public int Pen;
        public int Total;
        public int Burn, Lock;
        public int Golds;
        public readonly List<int> RoundSums = [];
        // для ачівок
        public bool Burned, Cat5, GoldBasket;
        public int Grabs;

        public int BasketValue
        {
            get
            {
                var v = Pen;
                foreach (var it in Basket) v += it.Val;
                return v;
            }
        }
    }

    public readonly record struct Ev(int T, int Type, int Seat, int Kind, int Val, int Id);

    readonly Player[] _p = [.. Enumerable.Range(0, Seats).Select(_ => new Player())];
    readonly List<Gift> _gifts = [];
    readonly List<Ev> _ev = [];
    readonly SoloBot _solo = new();
    int[] _bots = [];
    readonly HostyntsiBot?[] _brain = new HostyntsiBot?[Seats];
    bool _botGame;
    /// <summary>Порядок місць на жолобі в першому раунді (згори вниз) — перемішаний сідом; далі обертається.</summary>
    int[] _order = [];
    bool _started;
    int _ph = PhReady;
    int _left;
    int _round;
    int _rounds = Rounds;
    int _t, _rt, _nextSpawn, _nextId;
    int _startPlayers;
    /// <summary>Тик раунду (<c>_rt</c>), коли була остання зміна посеред раунду.</summary>
    int _shiftRt = -100;
    string[] _startNicks = [];
    int[] _winners = [];
    bool _nik;
    /// <summary>Глек у цьому раунді вже сказав про яскравий момент — більше не балакає (не спам).</summary>
    bool _saidThisRound;
    bool _saidCat;
    readonly Series _series = new();
    PartyMode? _party;

    public bool Party => _party is not null;
    public IReadOnlyList<int> Bots => _bots;
    public bool BotGame => _botGame;
    public int Phase => _ph;
    public int RoundNo => _round;
    public int RoundsTotal => _rounds;
    public int T => _t;
    public int Rt => _rt;
    public bool Nik => _nik;
    public IReadOnlyList<Gift> Gifts => _gifts;
    public Player P(int seat) => _p[seat];
    public int Stations => _order.Length;

    public string Howto => "Тисни «Хапай», коли гостинець пливе навпроти тебе: розписний +3, золотий +5, жар −3. "
        + "Кошик на вісім — дрібноту пропускай. Пробіл / дотик / A";
    public int PartyCapMs => 55_000;   // 3 с відліку + раунд 40 с, решта — запас
    public int PartyMin => 2;
    public int PartyMax => Seats;

    public override string SeatName(int seat) => seat is >= 0 and < Seats ? Names[seat] : base.SeatName(seat);

    public override string? SeatBot(int seat) =>
        !Ctx.Seated(seat) && Array.IndexOf(_started ? _bots : BotSeats(), seat) >= 0 ? $"{LiveBots.Name} {SeatName(seat)}" : null;

    string Name(int seat) => SeatBot(seat) ?? Ctx.NickOf(seat) ?? SeatName(seat);

    /// <summary>Назва гостинця з урахуванням Миколаєвого скіну (подарунки, різочка).</summary>
    public string KindName(int kind) => (_nik ? NikNames : KindNames)[kind];

    /// <summary>1–19 грудня за Києвом — Миколаїв скін: замість глеків подарунки, замість жару різочка, сніг.</summary>
    public static bool NikDay(DateTimeOffset now)
    {
        var k = TimeZoneInfo.ConvertTime(now, Days.Kyiv);
        return k.Month == 12 && k.Day <= 19;
    }

    public override bool ActsInLobby => true;

    bool Lobby => !_started || (_ph == PhOver && _party is null && Enumerable.Range(0, Seats)
        .Any(s => Ctx.Seated(s) && !_startNicks.Contains(Ctx.NickOf(s) ?? "", StringComparer.OrdinalIgnoreCase)));

    int[] BotSeats() => _solo.Active(Ctx, Seats)
        ? [.. Enumerable.Range(0, Seats).Where(s => !Ctx.Seated(s)).Take(SoloBots)] : [];

    bool IsBot(int seat) => Array.IndexOf(_bots, seat) >= 0 && !Ctx.Seated(seat);

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _solo.Configure(options);
        _party = PartyMode.Read(options);
        _rounds = _party is null ? Rounds : 1;
    }

    public override string? CanStart() => _solo.CanStart(Ctx, Seats);

    public override void Start()
    {
        _started = true;
        _bots = _party is { } pm ? [.. pm.Bots.Where(s => s < Seats && !Ctx.Seated(s))] : BotSeats();
        _botGame = _bots.Length > 0;
        Array.Clear(_brain);
        var level = _party?.Level ?? _solo.Level;
        for (var i = 0; i < _bots.Length; i++) _brain[_bots[i]] = new HostyntsiBot(level);
        _startNicks = [.. Enumerable.Range(0, Seats).Where(Ctx.Seated).Select(s => Ctx.NickOf(s) ?? "")];
        _startPlayers = Enumerable.Range(0, Seats).Count(Ctx.Seated);
        for (var s = 0; s < Seats; s++)
        {
            var plays = Ctx.Seated(s) || Array.IndexOf(_bots, s) >= 0;
            _p[s] = new Player { Plays = plays };
        }
        // Хто вище — бачить першим: стартовий порядок тягнемо сідом, щоб угорі не стояв завжди господар.
        var order = Enumerable.Range(0, Seats).Where(s => _p[s].Plays).ToArray();
        for (var i = order.Length - 1; i > 0; i--)
        {
            var j = Ctx.Rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        _order = order;
        _nik = NikDay(Ctx.Clock.UtcNow);
        _series.Begin(Ctx, Seats);
        _winners = [];
        _t = 0;
        _nextId = 0;
        _saidCat = false;
        _round = 1;
        BeginRound();
        if (_party is null && Ctx.Round == 1)
            Ctx.Say(_nik ? "Миколай котить подарунки. Не хапай усе підряд — неслухняним різочка"
                : "Не хапай усе підряд — то не гостинці, то жадібність");
    }

    /// <summary>
    /// Скільки змін у раунді. Хто вище — бачить першим і забирає краще (заміри: угорі ×3–5 очок проти низу), тож
    /// місця обертаються щораунду, а коли людей більше, ніж раундів, — ще й посеред раунду: <c>⌈n / раундів⌉</c>
    /// змін, на кожній усі зсуваються на крок угору, а верхній іде вниз. За партію кожен постоїть на кожній
    /// станції (на вісьмох у вечірці — по 5 с; на трьох у звичайній — рівно по раунду).
    /// </summary>
    public int Shifts => Math.Max(1, (_order.Length + _rounds - 1) / _rounds);

    public int ShiftTicks => RoundTicks / Shifts;

    /// <summary>Станція k-го в стартовому порядку на зміні <paramref name="shift"/> (наскрізній за партію): хто був нижче — тепер вище.</summary>
    public int StationOf(int k, int shift)
    {
        var n = _order.Length;
        if (n == 0) return -1;
        return ((k - shift) % n + n) % n;
    }

    /// <summary>Наскрізний номер зміни: раунди до цього плюс зміна в раунді.</summary>
    int ShiftNo => (_round - 1) * Shifts + Math.Min(Shifts - 1, _rt / ShiftTicks);

    void Place()
    {
        var j = ShiftNo;
        for (var k = 0; k < _order.Length; k++)
        {
            var p = _p[_order[k]];
            p.Prev = p.Station;
            p.Station = StationOf(k, j);
        }
        _shiftRt = _rt;
        foreach (var b in _brain) b?.Reset();
    }

    void BeginRound()
    {
        _gifts.Clear();
        _ev.Clear();
        _rt = 0;
        Place();
        _shiftRt = -100;
        foreach (var p in _p)
        {
            p.Basket.Clear();
            p.Pen = 0;
            p.Burn = p.Lock = 0;
            p.Golds = 0;
        }
        _ph = PhReady;
        _left = ReadyTicks;
        _nextSpawn = 0;
        _saidThisRound = false;
        foreach (var b in _brain) b?.Reset();
    }

    // ---------- жолоб ----------

    /// <summary>Довжина жолоба під <paramref name="n"/> станцій.</summary>
    public static int Length(int n) => (Math.Max(1, n) + 1) * Spacing;

    public static int Center(int station) => (station + 1) * Spacing;

    /// <summary>Швидкість жолоба, од/с: раунд за раундом швидше, і за раунд — ще на 500.</summary>
    public double Speed => 900 + 150 * (_round - 1) + 500.0 * Math.Min(1, (double)_rt / RoundTicks);

    /// <summary>Середня пауза між гостинцями, мс: на більше людей — густіше, під кінець раунду — густіше.</summary>
    public double IntervalMs => 950 / (1 + 0.12 * (Math.Max(1, _order.Length) - 1)) * (1 - 0.3 * Math.Min(1, (double)_rt / RoundTicks));

    /// <summary>Нижня межа вікна натиску з запасом на джитер для гостинця зі швидкістю <paramref name="v"/>, од/с.</summary>
    public static double Late(double v) => HalfWin + v * JitterMs / 1000.0;

    public static bool InReach(int station, double d, double v) => d >= Center(station) - HalfWin && d <= Center(station) + Late(v);

    void Spawn()
    {
        var r = Ctx.Rng.Next(100);
        var kind = 0;
        while (r >= Weight[kind]) r -= Weight[kind++];
        _gifts.Add(new Gift { Id = ++_nextId, Kind = kind, D = 0, Mul = kind == Gold ? GoldMul : 1 });
        _nextSpawn = _rt + Math.Max(4, (int)Math.Round(IntervalMs * (0.8 + 0.4 * Ctx.Rng.NextDouble()) / TickMs));
    }

    /// <summary>Покласти гостинець на жолоб у точку <paramref name="d"/> — для тестів (сід не чіпає).</summary>
    public Gift Put(int kind, double d)
    {
        var g = new Gift { Id = ++_nextId, Kind = kind, D = d, Mul = kind == Gold ? GoldMul : 1 };
        _gifts.Add(g);
        return g;
    }

    // ---------- ввід ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == LiveBots.Toggle)
            return _started && _ph != PhOver ? ActResult.Fail("Партія вже йде") : _solo.Switch(Ctx, seat, payload, Seats);
        if (!_started) return ActResult.Fail("Чекаємо на гравців");
        if (action != "grab") return ActResult.Fail("Тут так не ходять");
        if (seat is < 0 or >= Seats || !_p[seat].Plays || _p[seat].Left) return ActResult.Fail("Ти тут не граєш");
        if (_ph == PhOver) return ActResult.Fail("Партію вже зіграно");
        if (_ph != PhGo) return ActResult.Fail("Зачекай, ще не котиться");
        var p = _p[seat];
        if (p.Burn > 0) return ActResult.Fail("Пече! Ще мить");
        if (p.Lock > 0) return ActResult.Fail("Руки зайняті");
        if (p.Basket.Count >= BasketMax) return ActResult.Fail("Кошик повний — чекай кінця раунду");
        int? want = payload.ValueKind switch
        {
            JsonValueKind.Number when payload.TryGetInt32(out var n) => n,
            JsonValueKind.Object when payload.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var n) => n,
            _ => null,
        };
        var v = Speed;
        Gift? g = null;
        foreach (var x in _gifts)
        {
            if (!InReach(p.Station, x.D, v * x.Mul)
                && !(_rt - _shiftRt <= JitterTicks && p.Prev >= 0 && InReach(p.Prev, x.D, v * x.Mul))) continue;
            // Браузер назвав гостинець — беремо лише його: інакше натиск на розписний, що вже проплив, схопив би жар за ним.
            if (want is { } w) { if (x.Id == w) { g = x; break; } continue; }
            if (g is null || x.D > g.D) g = x;   // без id — той, що ось-ось піде
        }
        if (g is null)
        {
            p.Lock = MissLock;
            _ev.Add(new Ev(_t, EvMiss, seat, -1, 0, want ?? 0));
            return ActResult.Done;
        }
        _gifts.Remove(g);
        p.Grabs++;
        if (g.Kind == Ember)
        {
            p.Pen += Value[Ember];
            p.Burn = BurnTicks;
            p.Burned = true;
            _ev.Add(new Ev(_t, EvBurn, seat, g.Kind, Value[Ember], g.Id));
            return ActResult.Done;
        }
        var val = g.Kind == Cat ? Ctx.Rng.Next(CatMin, CatMax + 1) : Value[g.Kind];
        p.Basket.Add((g.Kind, val));
        p.Lock = GrabLock;
        if (g.Kind == Gold && ++p.Golds >= 3) p.GoldBasket = true;
        _ev.Add(new Ev(_t, EvGrab, seat, g.Kind, val, g.Id));
        if (g.Kind == Cat && val == CatMax)
        {
            p.Cat5 = true;
            if (!_saidCat && (_party is null ? !_saidThisRound : true))
            {
                _saidCat = _saidThisRound = true;
                Ctx.Say($"{Name(seat)} розв'язав мішок — а там кіт із п'ятіркою в зубах. Ризик — шляхетна справа");
            }
        }
        else if (g.Kind == Gold && p.Golds == 3 && _party is null && !_saidThisRound)
        {
            _saidThisRound = true;
            Ctx.Say($"У {Name(seat)} третій золотий у кошику — Дядько Глек аж окуляри протер");
        }
        return ActResult.Done;
    }

    // ---------- тик ----------

    public override TickResult Tick()
    {
        switch (_ph)
        {
            case PhOver:
                return TickResult.None;
            case PhReady:
                _t++;
                if (--_left > 0) return _t % 5 == 0 ? TickResult.FrameOnly : TickResult.None;
                _ph = PhGo;
                return TickResult.Both;
            case PhGo:
                _t++;
                return Roll();
            default:
                _t++;
                Prune();
                if (--_left > 0) return _t % 5 == 0 ? TickResult.FrameOnly : TickResult.None;
                return AfterRound();
        }
    }

    TickResult Roll()
    {
        // Боти тиснуть до кроку жолоба — їхній натиск лягає в цей тик, як людський між тиками.
        BotsThink();
        _rt++;
        var v = Speed;
        var len = Length(_order.Length);
        for (var i = _gifts.Count - 1; i >= 0; i--)
        {
            var g = _gifts[i];
            g.D += v * g.Mul * TickMs / 1000.0;
            if (g.D > len) _gifts.RemoveAt(i);   // доїхав до низу — у яр
        }
        if (_rt >= _nextSpawn) Spawn();
        foreach (var p in _p)
        {
            if (p.Burn > 0) p.Burn--;
            if (p.Lock > 0) p.Lock--;
        }
        Prune();
        if (_rt >= RoundTicks) return EndRound();
        if (Shifts > 1 && _rt % ShiftTicks == 0 && _rt / ShiftTicks < Shifts)
        {
            Place();
            return TickResult.Both;
        }
        return TickResult.FrameOnly;
    }

    void Prune()
    {
        var i = 0;
        while (i < _ev.Count && _t - _ev[i].T > EvKeep) i++;
        if (i > 0) _ev.RemoveRange(0, i);
    }

    void BotsThink()
    {
        foreach (var s in _bots)
        {
            if (Ctx.Seated(s) || _brain[s] is not { } bot || !_p[s].Plays) continue;
            if (bot.Think(this, s, Ctx.Rng) is { } id) Act(s, "grab", JsonSerializer.SerializeToElement(id));
        }
    }

    /// <summary>Раунд скінчився: кошики висипають у рахунок, гостинці, що котились, — у яр.</summary>
    TickResult EndRound()
    {
        _gifts.Clear();
        // Спершу фаза: кошики вже в рахунку, і PartyScores не має додати їх удруге.
        _ph = PhEnd;
        for (var s = 0; s < Seats; s++)
        {
            var p = _p[s];
            if (!p.Plays) continue;
            var sum = p.BasketValue;
            p.Total += sum;
            p.RoundSums.Add(sum);
            if (!p.Left) _ev.Add(new Ev(_t, EvDump, s, p.Basket.Count, sum, 0));
        }
        if (_party is not null) return PartyOver();
        _left = EndTicks;
        return TickResult.Both;
    }

    TickResult AfterRound()
    {
        if (_round >= _rounds) return Over();
        _round++;
        BeginRound();
        return TickResult.Both;
    }

    int[] Playing() => [.. Enumerable.Range(0, Seats).Where(s => _p[s].Plays && !_p[s].Left && (Ctx.Seated(s) || IsBot(s)))];

    TickResult Over()
    {
        _ph = PhOver;
        var playing = Playing();
        var best = playing.Length == 0 ? 0 : playing.Max(s => _p[s].Total);
        var top = playing.Where(s => _p[s].Total == best).ToArray();
        // Усі рівні (і не один гравець) — нічия.
        _winners = top.Length == playing.Length && playing.Length > 1 ? [] : top;
        if (!_botGame && _startPlayers >= 2)
            foreach (var s in playing.Where(Ctx.Seated))
            {
                var p = _p[s];
                if (!p.Burned && p.Grabs >= 10) Ctx.Award(s, 0, "ach:hostyntsi-noburn");
                if (p.Cat5) Ctx.Award(s, 0, "ach:hostyntsi-cat5");
                if (p.GoldBasket) Ctx.Award(s, 0, "ach:hostyntsi-gold3");
            }
        var scores = playing.Where(Ctx.Seated).ToDictionary(s => s, s => (long)_p[s].Total);
        var log = Journal(_winners, playing);
        if (_winners.Length > 0)
            Ctx.Say(_winners.Length == 1
                ? $"{Name(_winners[0])} несе додому найповніший кошик — {_p[_winners[0]].Total}"
                : $"Поділили гостинці по-братськи: {string.Join(" і ", _winners.Select(Name))}");
        if (!_botGame)
        {
            _series.Record(Ctx, _winners);
            Ctx.Finish(_winners, log, scores);
            return TickResult.Both;
        }
        var people = _winners.Where(Ctx.Seated).ToArray();
        var verdict = people.Length > 0 ? $"🏆 {Ctx.NickOf(people[0])} — перемога над {LiveBots.Of(_solo.Level)}и ботами"
            : _winners.Length > 0 ? $"🤖 Найповніший кошик — у {Name(_winners[0])}" : null;
        Ctx.Finish(people, log, scores, verdict);
        return TickResult.Both;
    }

    /// <summary>Scores вечірки: очки кожного місця 0..N−1 (рахунок плюс те, що зараз у кошику). Хто не грав — 0.</summary>
    public IReadOnlyDictionary<int, long> PartyScores()
    {
        var r = new Dictionary<int, long>(Ctx.Players);
        for (var s = 0; s < Ctx.Players; s++)
            r[s] = s < Seats && _p[s].Plays ? _p[s].Total + (_ph == PhGo ? _p[s].BasketValue : 0) : 0;
        return r;
    }

    TickResult PartyOver()
    {
        _ph = PhOver;
        var scores = PartyScores();
        var best = scores.Count == 0 ? 0 : scores.Values.Max();
        _winners = [.. scores.Where(kv => kv.Value == best).Select(kv => kv.Key).Order()];
        Ctx.Finish(_winners, Journal(_winners, [.. Enumerable.Range(0, Seats).Where(s => _p[s].Plays)]), scores);
        return TickResult.Both;
    }

    /// <summary>«Гостинці: Оля 23 : Петро 17 : Ігор −2» — переможці першими, далі за очками.</summary>
    string Journal(int[] winners, int[] playing)
    {
        var order = winners.Concat(playing.Where(s => !winners.Contains(s)).OrderByDescending(s => _p[s].Total).ThenBy(s => s));
        var line = $"{Info.Title}: {string.Join(" : ", order.Select(s => $"{Name(s)} {_p[s].Total}"))}";
        return winners.Length == 0 ? line + " — нічия" : line;
    }

    /// <summary>
    /// Хтось устав посеред партії: його станція порожніє (гостинці пливуть повз), очки не в заліку. Лишилось двоє
    /// й більше людей — грають далі; інакше партію не дограли.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (!_started || _ph == PhOver || _party is not null || seat is < 0 or >= Seats || !_p[seat].Plays) return;
        var nick = Ctx.NickOf(seat);
        _p[seat].Left = true;
        var left = Enumerable.Range(0, Seats).Where(s => s != seat && _p[s].Plays && !_p[s].Left && Ctx.Seated(s)).ToArray();
        if (left.Length >= 2)
        {
            Ctx.Log($"{Info.Title}: {nick} встав з-за столу — решта грає далі");
            return;
        }
        _ph = PhOver;
        _winners = left;
        if (!_botGame) _series.Record(Ctx, left);
        Ctx.Finish(left, $"{Info.Title}: {nick} встав з-за столу, партію не дограли",
            left.ToDictionary(s => s, s => (long)_p[s].Total));
    }

    // ---------- вид і кадр ----------

    /// <summary>Станції в лобі — за тими, хто вже сів (і ботами, що сядуть), за місцями.</summary>
    int?[] LobbyStations()
    {
        var bots = BotSeats();
        var r = new int?[Seats];
        var k = 0;
        for (var s = 0; s < Seats; s++) if (Ctx.Seated(s) || Array.IndexOf(bots, s) >= 0) r[s] = k++;
        return r;
    }

    public override object View(int? seat)
    {
        var lobby = Lobby;
        var totals = new int?[Seats];
        var sums = new int[Seats][];
        for (var s = 0; s < Seats; s++)
        {
            sums[s] = lobby || !_p[s].Plays ? [] : [.. _p[s].RoundSums];
            if (!lobby && _p[s].Plays) totals[s] = _p[s].Total;
        }
        return new
        {
            phase = lobby ? "lobby" : _ph switch { PhReady => "ready", PhGo => "go", PhEnd => "end", _ => "over" },
            round = lobby ? 0 : _round,
            rounds = _rounds,
            party = _party is not null,
            skin = (lobby ? NikDay(Ctx.Clock.UtcNow) : _nik) ? "nik" : "",
            basketMax = BasketMax,
            spacing = Spacing,
            halfWin = HalfWin,
            jitterMs = JitterMs,
            tickMs = TickMs,
            roundTicks = RoundTicks,
            shifts = lobby ? 1 : Shifts,
            values = (int[])Value.Clone(),
            cat = new[] { CatMin, CatMax },
            goldMul = GoldMul,
            totals,
            sums,
            left = lobby ? new bool[Seats] : _p.Select(p => p.Left).ToArray(),
            winner = !lobby && _ph == PhOver && _winners.Length > 0 ? _winners[0] : (int?)null,
            winners = !lobby && _ph == PhOver ? (int[])_winners.Clone() : [],
            series = _series.View(Ctx, Seats),
            turn = (int?)null,
            botOffer = _solo.Offer(Ctx, Seats),
            botWanted = _solo.Wanted,
            botLvl = _solo.LevelKey,
            bot = lobby ? BotSeats() : _bots.Where(s => !Ctx.Seated(s)).ToArray(),
            frame = Shot(lobby),
        };
    }

    public override object? Frame() => Shot(Lobby);

    /// <summary>
    /// Кадр (spec §4): <c>st</c> станція кожного місця (null — не грає), <c>g</c> гостинці [id, вид, d, mul×100],
    /// <c>p</c> за місцями [у кошику, вартість кошика, рахунок, опік, руки зайняті] (тики), <c>b</c> кошики
    /// пласко [вид, вартість, …], <c>ev</c> події останніх 20 тиків [t, тип, місце, вид, вартість, id].
    /// </summary>
    object Shot(bool lobby)
    {
        var st = lobby ? LobbyStations() : new int?[Seats];
        var p = new int[]?[Seats];
        var b = new int[]?[Seats];
        var n = 0;
        for (var s = 0; s < Seats; s++)
        {
            if (lobby) { if (st[s] is not null) n++; continue; }
            var q = _p[s];
            if (!q.Plays) continue;
            n++;
            st[s] = q.Station;
            p[s] = [q.Basket.Count, q.BasketValue, q.Total, q.Burn, q.Lock];
            var flat = new int[q.Basket.Count * 2];
            for (var i = 0; i < q.Basket.Count; i++) { flat[2 * i] = q.Basket[i].Kind; flat[2 * i + 1] = q.Basket[i].Val; }
            b[s] = flat;
        }
        var g = new int[lobby ? 0 : _gifts.Count][];
        for (var i = 0; i < g.Length; i++)
        {
            var x = _gifts[i];
            g[i] = [x.Id, x.Kind, (int)Math.Round(x.D), (int)Math.Round(x.Mul * 100)];
        }
        var ev = new int[lobby ? 0 : _ev.Count][];
        for (var i = 0; i < ev.Length; i++)
        {
            var e = _ev[i];
            ev[i] = [e.T, e.Type, e.Seat, e.Kind, e.Val, e.Id];
        }
        var ph = lobby ? PhLobby : _ph;
        return new
        {
            t = lobby ? 0 : _t,
            ph,
            r = lobby ? 0 : _round,
            left = ph == PhGo ? Math.Max(0, RoundTicks - _rt) : ph is PhReady or PhEnd ? _left : 0,
            sl = ph == PhGo && Shifts > 1 && _rt / ShiftTicks < Shifts - 1 ? ShiftTicks - _rt % ShiftTicks : 0,
            v = (int)Math.Round(lobby ? 900 : Speed),
            len = Length(n),
            st,
            g,
            p,
            b,
            ev,
        };
    }
}
