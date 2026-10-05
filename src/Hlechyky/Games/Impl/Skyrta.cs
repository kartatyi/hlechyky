using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Скирта на 1–8 (docs/games/specs/skyrta.md): над скиртою їздить сніп — тап, і він падає; що звисає за край,
/// обрізається, ідеально рівний сніп повертає трохи ширини. Хто перший доклав 25 снопів — виграв; за 90 с ніхто —
/// виграє вища скирта. Три ідеальні поспіль — вітер на сусіда (3 с його сніп швидший).
///
/// Свій сніп кожен бачить миттєво: браузер сам рахує рух за <see cref="SkyrtaCore"/> і шле <c>tap {n, t}</c> —
/// номер снопа й мить тапу від його виїзду. Тут сервер — суддя: звіряє мить із годинником, кладе сніп тим самим
/// кодом і шле кадр із верхом кожної скирти. Режим вечірки — 15 снопів або 45 с, стеля 60 с.
/// </summary>
public sealed class Skyrta : Game, IPartyMinigame
{
    public const int Seats = SkyrtaCore.Seats;
    /// <summary>Відлік перед стартом — 3 с (тиків по 40 мс).</summary>
    public const int ReadyTicks = 75;
    /// <summary>Звичайна партія: до 25 снопів або 90 с. Вечірка: 15 снопів або 45 с.</summary>
    public const int Goal = 25, LimitMs = 90_000, PartyGoal = 15, PartyLimitMs = 45_000;
    /// <summary>Кадр без подій — раз на стільки тиків (200 мс): годинник клієнта звіряється, рух він малює сам.</summary>
    public const int FrameEvery = 5;
    /// <summary>Скільки мс подія живе в кадрі: кадри зливаються (Broadcaster лишає останній), а клієнт відсіює за id.</summary>
    public const int EventMs = 1500;
    /// <summary>Фази (поле <c>ph</c> кадру): 0 відлік, 1 гра, 3 партію зіграно, 4 лобі.</summary>
    public const int PhReady = 0, PhGo = 1, PhOver = 3, PhLobby = 4;
    /// <summary>У соло з ботами — двоє: гонка на трьох жвавіша, і вітер є на кого пускати.</summary>
    public const int SoloBots = 2;

    static readonly string[] Names = ["пшениця", "жито", "овес", "ячмінь", "гречка", "просо", "льон", "кукурудза"];

    public override GameInfo Info { get; } = new(
        "skyrta", "Скирта", "скирту", GameGroup.Live, 1, Seats, TickMs: SkyrtaCore.TickMs,
        Start: StartMode.ByHost, Options: [LiveBots.LevelOption],
        Hint: "Тап — і сніп падає на скирту; що звисає — обріже. Рівно-рівнесенько — скирта ширшає, а тричі поспіль — вітер сусідові. Хто перший доклав 25 — той і господар. Самому — з 🤖 ботами");

    public string Howto => "Тапни, коли сніп над скиртою: що звисає — обріже, рівно — скирта ширшає. Вище всіх за 45 с (чи 15 снопів) — перший. "
        + "Пробіл / тап / A — кинути сніп";
    public int PartyCapMs => 60_000;   // 3 с відліку + 45 с гри + запас
    public int PartyMin => 2;
    public int PartyMax => Seats;

    readonly SoloBot _solo = new();
    PartyMode? _party;
    public bool Party => _party is not null;
    int[] _bots = [];
    readonly SkyrtaBot?[] _brain = new SkyrtaBot?[Seats];
    bool _botGame;
    public IReadOnlyList<int> Bots => _bots;

    readonly SkyrtaStack[] _st = [.. Enumerable.Range(0, Seats).Select(_ => new SkyrtaStack())];
    public IReadOnlyList<SkyrtaStack> Stacks => _st;

    bool _started;
    int _ph = PhReady;
    int _left;
    int _tick;
    DateTimeOffset _goAt;
    int _endMs;
    int _goal = Goal, _limit = LimitMs;
    int _startPlayers;
    int[] _winners = [];
    bool _dirty;
    readonly Series _series = new();
    /// <summary>Глек цієї партії вже сказав про промах / про вітер — більше не повторює.</summary>
    bool _saidMiss, _saidWind;

    /// <summary>Події кадру: [id, вид, …] з моментом (мс гри) — у кадр ідуть свіжі за <see cref="EventMs"/>.</summary>
    readonly List<(int At, object[] Ev)> _ev = [];
    int _evId;

    public int Phase => _ph;
    public int GoalNow => _goal;
    public int Limit => _limit;

    // ---------- місця, боти ----------

    int[] BotSeats() => _solo.Active(Ctx, Seats) ? [.. Enumerable.Range(0, Seats).Where(s => !Ctx.Seated(s)).Take(SoloBots)] : [];

    bool IsBot(int seat) => Array.IndexOf(_bots, seat) >= 0 && !Ctx.Seated(seat);

    public override string? SeatBot(int seat) =>
        !Ctx.Seated(seat) && Array.IndexOf(_started ? _bots : BotSeats(), seat) >= 0 ? $"{LiveBots.Name} {SeatName(seat)}" : null;

    string Name(int seat) => SeatBot(seat) ?? Ctx.NickOf(seat) ?? SeatName(seat);

    public override string SeatName(int seat) => seat is >= 0 and < Seats ? Names[seat] : base.SeatName(seat);

    public override bool ActsInLobby => _party is null;

    /// <summary>Ачівки — лише у звичайній партії людей (від двох), не з ботами і не у вечірці.</summary>
    bool Awards => _party is null && !_botGame && _startPlayers >= 2;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _solo.Configure(options);
        _party = PartyMode.Read(options);
    }

    public override string? CanStart() => _party is null ? _solo.CanStart(Ctx, Seats) : null;

    public override void Start()
    {
        _started = true;
        if (_party is { } pm)
        {
            _bots = [.. pm.Bots.Where(s => s < Seats && s < Ctx.Players && !Ctx.Seated(s))];
            _goal = PartyGoal;
            _limit = PartyLimitMs;
        }
        else
        {
            _bots = BotSeats();
            _goal = Goal;
            _limit = LimitMs;
        }
        _botGame = _bots.Length > 0;
        Array.Clear(_brain);
        var level = _party?.Level ?? _solo.Level;
        foreach (var s in _bots) _brain[s] = new SkyrtaBot(level);
        _startPlayers = Enumerable.Range(0, Seats).Count(Ctx.Seated);
        for (var s = 0; s < Seats; s++)
            // У вечірці грають усі місця столу: людина, що відпала, лишає скирту стояти, а не зникає.
            _st[s].Reset(_party is not null ? s < Ctx.Players : Ctx.Seated(s) || IsBot(s));
        _series.Begin(Ctx, Seats);
        _winners = [];
        _ev.Clear();
        _ph = PhReady;
        _left = ReadyTicks;
        _tick = 0;
        _endMs = 0;
        _dirty = false;
        _saidMiss = _saidWind = false;
        if (_party is null && Ctx.Rng.Next(3) == 0) Ctx.Say(Pick(StartLines));
    }

    // ---------- час ----------

    /// <summary>Мс гри від старту фази «go» (годинник кімнати; пропущені тики не губляться).</summary>
    public int Now() => _ph switch
    {
        PhGo => (int)Math.Clamp((Ctx.Clock.UtcNow - _goAt).TotalMilliseconds, 0, 10_000_000),
        PhOver => _endMs,
        _ => 0,
    };

    // ---------- ввід ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == LiveBots.Toggle)
            return _party is not null ? ActResult.Fail("Тут так не ходять")
                : _started && _ph != PhOver ? ActResult.Fail("Партія вже йде") : _solo.Switch(Ctx, seat, payload, Seats);
        if (!_started) return ActResult.Fail("Чекаємо на гравців");
        if (seat is < 0 or >= Seats || !_st[seat].Plays || _st[seat].Gone) return ActResult.Fail("Ти тут не граєш");
        if (action != "tap") return ActResult.Fail("Тут так не ходять");
        if (_ph == PhOver) return ActResult.Fail("Партію вже зіграно");
        if (_ph != PhGo) return ActResult.Fail("Зачекай, зараз почнемо");
        if (!Int(payload, "n", out var n) || !Int(payload, "t", out var t) || t is < 0 or > 600_000)
            return ActResult.Fail("Не зрозумів, коли кидати");
        return Tap(seat, n, t, Now());
    }

    static bool Int(JsonElement p, string key, out int v)
    {
        v = 0;
        return p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var e)
            && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out v);
    }

    /// <summary>
    /// Тап: сніп <paramref name="n"/> падає через <paramref name="t"/> мс після свого виїзду. Мить звіряємо з
    /// годинником (<see cref="SkyrtaCore.MaxLag"/>/<see cref="SkyrtaCore.Lead"/>), кладемо тим самим кодом, що й браузер.
    /// </summary>
    public ActResult Tap(int seat, int n, int t, int now)
    {
        var st = _st[seat];
        if (st.DoneAt >= 0) return ActResult.Fail("Скирту вже докладено");
        if (n != st.N) return ActResult.Fail("Цей сніп уже впав");
        var a = st.S + t;
        if (a > now + SkyrtaCore.Lead) a = now;
        if (a < now - SkyrtaCore.MaxLag) a = now - SkyrtaCore.MaxLag;
        if (a < st.S) return ActResult.Fail("Сніп ще не виїхав");
        t = a - st.S;
        var v = SkyrtaCore.Speed(st.H);
        var l = st.LeftAt(t);
        var (nl, nw, kind) = SkyrtaCore.Land(st.L, st.W, l, v, st.Streak);
        var id = ++_evId;
        _ev.Add((now, ["l", id, seat, n, a, l, st.W, nl, nw, kind]));
        if (kind == SkyrtaCore.KindMiss)
        {
            st.Misses++;
            st.Streak = 0;
            st.BigCut = true;
            if (!_saidMiss && (_party is null || Ctx.Rng.Next(2) == 0))
            {
                _saidMiss = true;
                Ctx.Say("Хто так скирти кладе? У мене дід так клав — то вже на третій день розвіяло");
            }
        }
        else
        {
            if (kind == SkyrtaCore.KindCut && st.W - nw > st.W / 2) st.BigCut = true;
            st.H++;
            st.L = nl;
            st.W = nw;
            st.Sheaves.Add([nl, nw]);
            if (kind == SkyrtaCore.KindPerfect)
            {
                st.Perfects++;
                st.Streak++;
                st.BestStreak = Math.Max(st.BestStreak, st.Streak);
                if (st.Streak == 10 && Awards) Ctx.Award(seat, 0, "ach:skyrta-line");
                if (st.Streak % SkyrtaCore.WindStreak == 0) Blow(seat, now);
            }
            else st.Streak = 0;
        }
        st.N++;
        st.S = a + (kind == SkyrtaCore.KindMiss ? SkyrtaCore.MissGap : SkyrtaCore.Gap);
        Prune(st);
        if (st.H >= _goal && st.DoneAt < 0)
        {
            st.DoneAt = a;
            _ev.Add((now, ["g", ++_evId, seat, a]));
            if (_goal == Goal && !st.BigCut && Awards) Ctx.Award(seat, 0, "ach:skyrta-neat");
        }
        _dirty = true;
        return ActResult.Done;
    }

    /// <summary>Старі вікна вітру, що скінчились до виїзду снопа, новому снопу не потрібні.</summary>
    static void Prune(SkyrtaStack st)
    {
        for (var i = st.Wind.Count - 2; i >= 0; i -= 2)
            if (st.Wind[i + 1] <= st.S) st.Wind.RemoveRange(i, 2);
    }

    /// <summary>Наступне за місцем, хто ще кладе скирту (не докладену й не покинуту); −1 — нема.</summary>
    public int Neighbor(int seat)
    {
        for (var d = 1; d < Seats; d++)
        {
            var s = (seat + d) % Seats;
            var o = _st[s];
            if (o.Plays && !o.Gone && o.DoneAt < 0) return s;
        }
        return -1;
    }

    /// <summary>Три ідеальні поспіль — вітер на сусіда. Уже дме (чи налітає) — не стакається: другий порив мимо.</summary>
    void Blow(int seat, int now)
    {
        var to = Neighbor(seat);
        if (to < 0) return;
        var o = _st[to];
        if (o.Windy(now) || o.Wind.Count >= 2 * SkyrtaCore.MaxWinds) return;
        var wa = now + SkyrtaCore.WindDelay;
        var wb = wa + SkyrtaCore.WindMs;
        o.Wind.Add(wa);
        o.Wind.Add(wb);
        o.WindVer++;
        var st = _st[seat];
        st.Winds++;
        _ev.Add((now, ["w", ++_evId, seat, to, wa, wb]));
        if (st.Winds == 3 && Awards) Ctx.Award(seat, 0, "ach:skyrta-storm");
        if (!_saidWind && _party is null)
        {
            _saidWind = true;
            Ctx.Say($"💨 {Name(seat)} напустив вітру на {Name(to)}. Тримайте снопи!");
        }
    }

    // ---------- тик ----------

    public override TickResult Tick()
    {
        switch (_ph)
        {
            case PhReady:
                if (--_left > 0) return _left % FrameEvery == 0 ? TickResult.FrameOnly : TickResult.None;
                _ph = PhGo;
                _goAt = Ctx.Clock.UtcNow;
                _tick = 0;
                return TickResult.Both;
            case PhGo:
                var now = Now();
                BotsThink(now);
                _ev.RemoveAll(e => e.At < now - EventMs);
                if (Done() is { Length: > 0 } first) return Over(first);
                if (now >= _limit) return Over(Highest());
                _tick++;
                if (_dirty) { _dirty = false; return TickResult.Both; }
                return _tick % FrameEvery == 0 ? TickResult.FrameOnly : TickResult.None;
            default:
                return TickResult.None;
        }
    }

    /// <summary>Боти тапають тим самим <see cref="Tap"/>, що й люди, коли настала спланована мить.</summary>
    void BotsThink(int now)
    {
        foreach (var s in _bots)
        {
            if (Ctx.Seated(s) || _brain[s] is not { } bot) continue;
            var st = _st[s];
            if (!st.Plays || st.Gone || st.DoneAt >= 0 || now < st.S) continue;
            var t = bot.Plan(st, now, Ctx.Rng);
            if (now >= st.S + t) Tap(s, st.N, t, now);
        }
    }

    /// <summary>Хто вже доклав до мети — найраніші за миттю тапу (однакова мить — усі).</summary>
    int[] Done()
    {
        var best = int.MaxValue;
        foreach (var o in _st) if (o.Plays && !o.Gone && o.DoneAt >= 0) best = Math.Min(best, o.DoneAt);
        return best == int.MaxValue ? [] : [.. Enumerable.Range(0, Seats).Where(s => _st[s].Plays && !_st[s].Gone && _st[s].DoneAt == best)];
    }

    /// <summary>Час вийшов: найвища скирта, рівні — ширший верх; ніхто нічого не поклав — нічия.</summary>
    int[] Highest()
    {
        var live = Playing();
        if (live.Length == 0) return [];
        var best = live.Max(Rank);
        return _st[live.First(s => Rank(s) == best)].H == 0 ? [] : [.. live.Where(s => Rank(s) == best)];
    }

    long Rank(int s) => _st[s].H * 1000L + _st[s].W;

    int[] Playing() => [.. Enumerable.Range(0, Seats).Where(s => _st[s].Plays && !_st[s].Gone)];

    /// <summary>
    /// Scores вечірки: висота·1000 + ширина верху (ширина ≤ 400 — лише тай-брейк). Хто доклав до мети першим, і
    /// так найвищий. Місця поза столом — 0.
    /// </summary>
    public IReadOnlyDictionary<int, long> PartyScores()
    {
        var r = new Dictionary<int, long>(Ctx.Players);
        for (var s = 0; s < Ctx.Players; s++) r[s] = s < Seats && _st[s].Plays ? Rank(s) : 0;
        return r;
    }

    TickResult Over(int[] winners)
    {
        _endMs = Now();
        _ph = PhOver;
        _winners = winners;
        _dirty = false;
        if (_party is not null)
        {
            var scores = PartyScores();
            var top = scores.Values.Max();
            _winners = [.. scores.Where(kv => kv.Value == top).Select(kv => kv.Key).Order()];
            Ctx.Finish(_winners, Journal(_winners), scores);
            return TickResult.Both;
        }
        var people = Playing().Where(Ctx.Seated).ToDictionary(s => s, s => (long)_st[s].H);
        if (winners.Length > 0) Ctx.Say(Pick(EndLines).Replace("{0}", Name(winners[0])));
        if (!_botGame)
        {
            _series.Record(Ctx, winners);
            Ctx.Finish(winners, Journal(winners), people);
            return TickResult.Both;
        }
        var humans = winners.Where(Ctx.Seated).ToArray();
        var verdict = humans.Length > 0 ? $"🏆 {Ctx.NickOf(humans[0])} — перемога над {LiveBots.Of(_solo.Level)}и ботами"
            : winners.Length > 0 ? $"🤖 Скирту першим доклав {Name(winners[0])}" : null;
        Ctx.Finish(humans, Journal(winners), people, verdict);
        return TickResult.Both;
    }

    /// <summary>«Скирта: Оля 25 : Петро 19 : 🤖 бот жито 12» — переможці першими, далі за висотою й шириною.</summary>
    string Journal(int[] winners)
    {
        var order = winners.Concat(Playing().Where(s => !winners.Contains(s)).OrderByDescending(Rank).ThenBy(s => s));
        var line = $"{Info.Title}: {string.Join(" : ", order.Select(s => $"{Name(s)} {_st[s].H}"))}";
        return winners.Length == 0 ? line + " — нічия" : line;
    }

    /// <summary>
    /// Устав посеред партії: скирта лишається на полі, але більше не грає. Лишився один (чи нікого) — партія
    /// кінчається; з ботами — коли пішла людина. У вечірці вихід грі не прокидається (контракт).
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (!_started || _ph == PhOver || _party is not null || seat is < 0 or >= Seats) return;
        var nick = Ctx.NickOf(seat);
        _st[seat].Gone = true;
        _dirty = true;
        var people = Enumerable.Range(0, Seats).Where(s => s != seat && Ctx.Seated(s) && _st[s].Plays && !_st[s].Gone).ToArray();
        if (people.Length >= (_botGame ? 1 : 2))
        {
            Ctx.Log($"{Info.Title}: {nick} встав з-за столу — решта кладе далі");
            return;
        }
        _endMs = Now();
        _ph = PhOver;
        _winners = _botGame ? [] : people;
        if (!_botGame) _series.Record(Ctx, people);
        Ctx.Finish(_winners, $"{Info.Title}: {nick} встав з-за столу, скирту не доклали",
            people.ToDictionary(s => s, s => (long)_st[s].H));
    }

    // ---------- Глек ----------

    static readonly string[] StartLines =
    [
        "Ану, хлопці й дівчата, до скирти! Хто криво покладе — той узимку сіно й носитиме",
        "Скирту кладуть рівно, як під шнурок. Хто кладе криво — того вітер навчить",
        "Сніп до снопа — і буде скирта до неба. А як ні — то буде купа",
    ];

    static readonly string[] EndLines =
    [
        "{0} доклав скирту першим — хоч зараз на виставку в район",
        "Оце скирта! {0}, тобі б у колгосп бригадиром",
        "{0} — господар. Решта — несіть вила, будемо переробляти",
    ];

    string Pick(string[] lines) => lines[Ctx.Rng.Next(lines.Length)];

    // ---------- вид і кадр ----------

    /// <summary>Сталі рушія для клієнта: одне джерело правди — <see cref="SkyrtaCore"/>.</summary>
    public static readonly object Rules = new
    {
        tickMs = SkyrtaCore.TickMs,
        fieldW = SkyrtaCore.FieldW, lo = SkyrtaCore.Lo, hi = SkyrtaCore.Hi,
        baseW = SkyrtaCore.BaseW, baseL = SkyrtaCore.BaseL,
        v0 = SkyrtaCore.V0, vk = SkyrtaCore.Vk, vmax = SkyrtaCore.Vmax,
        gap = SkyrtaCore.Gap, missGap = SkyrtaCore.MissGap,
        windMs = SkyrtaCore.WindMs, windDelay = SkyrtaCore.WindDelay, windStreak = SkyrtaCore.WindStreak,
        swayFrom = SkyrtaCore.SwayFrom, swayA = SkyrtaCore.SwayA, swayP = SkyrtaCore.SwayP,
        grow = SkyrtaCore.Grow, growBig = SkyrtaCore.GrowBig, bigFrom = SkyrtaCore.BigFrom,
        maxLag = SkyrtaCore.MaxLag, lead = SkyrtaCore.Lead,
    };

    bool Lobby => !_started;

    /// <summary>Хто на полі: у лобі — ті, що сидять, і боти, що сядуть; далі — ті, хто грає.</summary>
    bool OnField(int s) => Lobby ? Ctx.Seated(s) || Array.IndexOf(BotSeats(), s) >= 0 : _st[s].Plays;

    public override object View(int? seat)
    {
        var lobby = Lobby;
        var stacks = new int[]?[Seats][];
        for (var s = 0; s < Seats; s++)
            if (OnField(s))
                stacks[s] = lobby ? [[SkyrtaCore.BaseL, SkyrtaCore.BaseW]] : [.. _st[s].Sheaves.Select(x => (int[])x.Clone())];
        return new
        {
            phase = lobby ? "lobby" : _ph switch { PhReady => "ready", PhGo => "go", _ => "over" },
            party = _party is not null,
            goal = lobby ? (_party is null ? Goal : PartyGoal) : _goal,
            limit = lobby ? (_party is null ? LimitMs : PartyLimitMs) : _limit,
            rules = Rules,
            stacks,
            stats = Enumerable.Range(0, Seats).Select(s => lobby || !_st[s].Plays ? null
                : new { perfects = _st[s].Perfects, best = _st[s].BestStreak, misses = _st[s].Misses, winds = _st[s].Winds }).ToArray(),
            winner = !lobby && _ph == PhOver && _winners.Length > 0 ? _winners[0] : (int?)null,
            winners = !lobby && _ph == PhOver ? (int[])_winners.Clone() : [],
            series = _series.View(Ctx, Seats),
            turn = (int?)null,
            botOffer = _party is null && _solo.Offer(Ctx, Seats),
            botWanted = _solo.Wanted,
            botLvl = _party?.Level is { } pl ? LiveBots.Key(pl) : _solo.LevelKey,
            bot = lobby ? BotSeats() : _bots.Where(s => !Ctx.Seated(s)).ToArray(),
            frame = Shot(),
        };
    }

    public override object? Frame() => Shot();

    /// <summary>
    /// Кадр (spec §4): <c>ms</c> — годинник гри (на відліку — мінус скільки лишилось), <c>p</c> за місцями
    /// [n, s0, l, w, h, streak, fl] (null — не грає), <c>wind</c> — вікна вітру на поточний сніп, <c>ev</c> — свіжі
    /// події з id. Масиви нові щоразу: кадр серіалізують уже поза замком кімнати.
    /// </summary>
    object Shot()
    {
        var lobby = Lobby;
        var now = Now();
        var p = new int[]?[Seats];
        var wind = new int[]?[Seats];
        for (var s = 0; s < Seats; s++)
        {
            if (!OnField(s)) continue;
            if (lobby) { p[s] = [1, 0, SkyrtaCore.BaseL, SkyrtaCore.BaseW, 0, 0, 0]; continue; }
            var o = _st[s];
            var fl = (o.DoneAt >= 0 ? 1 : 0) | (IsBot(s) ? 2 : 0) | (o.Gone ? 4 : 0) | (o.Windy(now) ? 8 : 0);
            p[s] = [o.N, o.S, o.L, o.W, o.H, o.Streak, fl];
            if (o.Wind.Count > 0) wind[s] = [.. o.Wind];
        }
        var ph = lobby ? PhLobby : _ph;
        return new
        {
            ph,
            ms = ph == PhReady ? -_left * SkyrtaCore.TickMs : now,
            left = ph == PhGo ? Math.Max(0, _limit - now) : 0,
            p,
            wind,
            ev = lobby ? [] : _ev.Select(e => (object[])e.Ev.Clone()).ToArray(),
        };
    }
}
