using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Брід: через річку — каміння, що ховається під водою, і безпечної стежки не видно. Стрибаєш на сусідній камінь;
/// не той — шубовсть, 1,5 с мокрий і знову на березі. На безпечному камені лишається мокрий слід, що висихає за 5 с,
/// а затоплений камінь видно до кінця раунду — лідер ризикує, решта підглядає. Двоє можуть стояти на одному камені.
/// Партія — 3 раунди (5×9, 6×10, 7×11); перші троє на тому березі беруть 3/2/1 очки; раунд — поки троє не вийдуть
/// або 60 с. Світ — <see cref="BridCore"/>, бот — <see cref="BridBot"/>. Spec: <c>docs/games/specs/brid.md</c>.
/// </summary>
public sealed class Brid : Game, IPartyMinigame
{
    public const int Seats = BridCore.Seats;
    /// <summary>Відлік перед першим раундом — 3 с, перед наступними — 2 с; підсумок раунду — 3 с (у вечірці 2 с).</summary>
    public const int ReadyFirst = 60, ReadyNext = 40, EndTicks = 60, PartyEndTicks = 40;
    public const int Rounds = 3;
    /// <summary>Фази в кадрі (<c>ph</c>): 0 відлік, 1 гра, 2 кінець раунду, 3 партію зіграно, 4 лобі.</summary>
    public const int PhReady = 0, PhGo = 1, PhEnd = 2, PhOver = 3, PhLobby = 4;
    /// <summary>Очки за місце на тому березі в раунді.</summary>
    public static readonly int[] RoundPoints = [3, 2, 1];
    /// <summary>
    /// Самому — двоє ботів: удвох із ботом «перший бере все» — лотерея першого стрибка, а втрьох є за ким підглядати
    /// й кого обганяти, і очки 3/2/1 справді діляться.
    /// </summary>
    public const int SoloBots = 2;
    static readonly string[] Names = ["синій", "рудий", "зелений", "жовтий", "бузковий", "м’ятний", "рожевий", "сірий"];
    /// <summary>Готові payload-и для бота: <c>{ d }</c> — без алокацій на тик.</summary>
    static readonly JsonElement[] DirEl = [.. Enumerable.Range(0, 4).Select(d => JsonSerializer.SerializeToElement(new { d }))];

    public override GameInfo Info { get; } = new(
        "brid", "Брід", "брід", GameGroup.Live, 1, Seats, TickMs: BridCore.TickMs,
        Start: StartMode.ByHost, Options: [LiveBots.LevelOption],
        Hint: "Перейди річку по каменях, що ховаються під водою: ступив не туди — шубовсть і назад на берег. Мокрі сліди сохнуть за 5 с — підглядай за сміливими.");

    readonly SoloBot _solo = new();
    int[] _bots = [];
    readonly BridBot?[] _brain = new BridBot?[Seats];
    bool _botGame;
    BridCore? _core;
    bool _started;
    int _ph = PhReady;
    int _left;
    int _round;
    int _startPlayers;
    bool[] _plays = new bool[Seats];
    int[] _winners = [];
    int[]? _last;
    string[] _startNicks = [];
    bool _saidWet;
    readonly Series _series = new();
    /// <summary>Режим вечірки (docs/games/specs/party-minigame.md): один раунд 5×9, боти на місцях <c>bots</c>, без нагород.</summary>
    PartyMode? _party;

    public bool Party => _party is not null;
    public IReadOnlyList<int> Bots => _bots;
    public bool BotGame => _botGame;
    public int Phase => _ph;
    public int RoundNo => _round;
    public BridCore Core => _core ??= new BridCore(Ctx.Rng);

    public string Howto => "Перейди річку по каменях: стежка під водою, ступив не туди — шубовсть і на берег. "
        + "Мокрі сліди підказують дорогу. Стрілки/WASD або тап по сусідньому каменю";
    public int PartyCapMs => 75_000;   // 3 с відліку + раунд до 60 с + 2 с підсумку
    public int PartyMin => 2;
    public int PartyMax => Seats;

    public override string SeatName(int seat) => seat is >= 0 and < Seats ? Names[seat] : base.SeatName(seat);

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _solo.Configure(options);
        _party = PartyMode.Read(options);
    }

    public override bool ActsInLobby => true;
    public override string? CanStart() => _solo.CanStart(Ctx, Seats);

    int[] BotSeats() => _solo.Active(Ctx, Seats) ? [.. Enumerable.Range(0, Seats).Where(s => !Ctx.Seated(s)).Take(SoloBots)] : [];

    bool IsBot(int seat) => Array.IndexOf(_bots, seat) >= 0 && !Ctx.Seated(seat);

    public override string? SeatBot(int seat) =>
        !Ctx.Seated(seat) && Array.IndexOf(_started ? _bots : BotSeats(), seat) >= 0 ? $"{LiveBots.Name} {SeatName(seat)}" : null;

    string Name(int seat) => SeatBot(seat) ?? Ctx.NickOf(seat) ?? SeatName(seat);

    /// <summary>Стіл чекає старту, або дограний стіл відкрив новий гравець — тоді свіжий берег, а не старий підсумок.</summary>
    bool Lobby => !_started || (_ph == PhOver && Enumerable.Range(0, Seats)
        .Any(s => Ctx.Seated(s) && !_startNicks.Contains(Ctx.NickOf(s) ?? "", StringComparer.OrdinalIgnoreCase)));

    bool Achievements => _party is null && !_botGame && _startPlayers >= 2;

    public override void Start()
    {
        _started = true;
        _bots = _party is { } pm
            ? [.. pm.Bots.Where(s => s < Seats && s < Ctx.Players && !Ctx.Seated(s)).Distinct()]
            : BotSeats();
        _botGame = _bots.Length > 0;
        Array.Clear(_brain);
        var level = _party?.Level ?? _solo.Level;
        for (var i = 0; i < _bots.Length; i++) _brain[_bots[i]] = new BridBot(level, i);
        _plays = new bool[Seats];
        for (var s = 0; s < Seats; s++) _plays[s] = (_party is null || s < Ctx.Players) && (Ctx.Seated(s) || Array.IndexOf(_bots, s) >= 0);
        _startNicks = [.. Enumerable.Range(0, Seats).Where(Ctx.Seated).Select(s => Ctx.NickOf(s) ?? "")];
        _startPlayers = Enumerable.Range(0, Seats).Count(Ctx.Seated);
        _series.Begin(Ctx, Seats);
        _winners = [];
        _last = null;
        _saidWet = false;
        _round = 1;
        foreach (var r in Core.Runners) r.Points = 0;
        BeginRound(ReadyFirst);
        if (_party is null && Ctx.Round == 1) Ctx.Say("Не знаєш броду — не лізь у воду. А ви лізете.");
    }

    void BeginRound(int ready)
    {
        var (cols, rows) = BridFord.SizeOf(_party is null ? _round : 1);
        Core.NewRound(cols, rows, _plays);
        _ph = PhReady;
        _left = ready;
    }

    /// <summary>Скільки людей мусить вийти на той берег, щоб раунд скінчився (вечірка — усі, крім останнього).</summary>
    int Need()
    {
        var n = _plays.Count(p => p);
        return _party is null ? Math.Min(RoundPoints.Length, n) : Math.Max(1, n - 1);
    }

    // ---------- ввід ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == LiveBots.Toggle)
            return _started && _ph != PhOver ? ActResult.Fail("Партія вже йде") : _solo.Switch(Ctx, seat, payload, Seats);
        if (!_started) return ActResult.Fail("Чекаємо на гравців");
        if (action != "step") return ActResult.Fail("Тут так не ходять");
        if (seat is < 0 or >= Seats || !Core.Runners[seat].Plays) return ActResult.Fail("Ти тут не граєш");
        if (_ph == PhOver) return ActResult.Fail("Партію вже зіграно");
        if (_ph == PhEnd) return ActResult.Fail("Раунд скінчився — зараз новий брід");
        if (_ph != PhGo) return ActResult.Fail("Зачекай, зараз почнемо");
        var r = Core.Runners[seat];
        if (r.Place > 0) return ActResult.Fail("Ти вже на тому березі");
        if (r.Wet > 0) return ActResult.Fail("Обтрусись — зараз знову на березі");
        if (Dir(payload, r) is not { } d) return ActResult.Fail("Стрибати можна лише на сусідній камінь");
        if (r.Jump > 0)
        {
            // У повітрі: запам'ятати й скочити, щойно приземлиться, — затиснута стрілка веде без пауз.
            r.Queue = d;
            return ActResult.Done;
        }
        return Core.TryStep(seat, d) is { } no ? ActResult.Fail(no) : ActResult.Done;
    }

    /// <summary>Напрямок: <c>{ d: 0..3 }</c>, голе число або <c>{ x, y }</c> — сусідній камінь (тап).</summary>
    static int? Dir(JsonElement p, BridRunner r)
    {
        if (p.ValueKind == JsonValueKind.Number) return p.TryGetInt32(out var n) && n is >= 0 and <= 3 ? n : null;
        if (p.ValueKind != JsonValueKind.Object) return null;
        if (p.TryGetProperty("d", out var d))
            return d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var k) && k is >= 0 and <= 3 ? k : null;
        if (!p.TryGetProperty("x", out var xe) || !p.TryGetProperty("y", out var ye)
            || xe.ValueKind != JsonValueKind.Number || ye.ValueKind != JsonValueKind.Number
            || !xe.TryGetInt32(out var x) || !ye.TryGetInt32(out var y)) return null;
        int dx = x - r.X, dy = y - r.Y;
        for (var i = 0; i < 4; i++)
            if (BridCore.Dirs[i] == (dx, dy)) return i;
        return null;
    }

    // ---------- тик ----------

    public override TickResult Tick()
    {
        var c = Core;
        switch (_ph)
        {
            case PhOver:
                return TickResult.None;
            case PhReady:
                c.Idle();
                if (--_left > 0) return c.T % 5 == 0 ? TickResult.FrameOnly : TickResult.None;
                _ph = PhGo;
                return TickResult.Both;
            case PhGo:
                BotsThink(c);
                var before = c.Order.Count;
                c.Step();
                if (_party is null) Splashes(c);
                var crossed = c.Order.Count > before;
                if (crossed) Crossed(c, before);
                if (c.Order.Count >= Need() || c.Rt >= BridCore.RoundTicks) return EndRound();
                return crossed ? TickResult.Both : c.Busy || c.AnyEvents || c.T % 5 == 0 ? TickResult.FrameOnly : TickResult.None;
            default:
                c.Idle();
                if (--_left > 0) return c.T % 5 == 0 ? TickResult.FrameOnly : TickResult.None;
                return AfterRound();
        }
    }

    /// <summary>Боти дивляться й стрибають до кроку світу тим самим <see cref="Act"/>, що й людина.</summary>
    void BotsThink(BridCore c)
    {
        foreach (var s in _bots)
        {
            if (Ctx.Seated(s) || _brain[s] is not { } bot || !bot.Due(c.T)) continue;
            bot.Observe(c);
            var d = bot.Think(c, s, Ctx.Rng);
            if (d >= 0) Act(s, "step", DirEl[d]);
        }
    }

    /// <summary>Дядько Глек — раз на партію, коли хтось ушосте за раунд у воді.</summary>
    void Splashes(BridCore c)
    {
        if (_saidWet) return;
        foreach (var e in c.RawEvents)
        {
            if (e[0] != BridCore.EvSplash || c.Runners[e[1]].Falls < 6) continue;
            _saidWet = true;
            Ctx.Say($"{Name(e[1])} ушосте у воді. Риби вже питають, чи не родич.");
            return;
        }
    }

    /// <summary>Хтось вийшов на той берег: очки 3/2/1 і ачівки (лише в людській партії від двох).</summary>
    void Crossed(BridCore c, int from)
    {
        if (_party is not null) return;
        for (var i = from; i < c.Order.Count; i++)
        {
            var s = c.Order[i];
            var r = c.Runners[s];
            if (r.Place <= RoundPoints.Length) r.Points += RoundPoints[r.Place - 1];
            if (!Achievements) continue;
            if (r.Falls == 0) Ctx.Award(s, 0, "ach:brid-dry");
            if (r.Place == 1 && r.Pioneer && r.News > 0) Ctx.Award(s, 0, "ach:brid-brave");
            if (r.Place == 1 && r.News == 0) Ctx.Award(s, 0, "ach:brid-sly");
        }
    }

    TickResult EndRound()
    {
        _ph = PhEnd;
        _left = _party is null ? EndTicks : PartyEndTicks;
        _last = [.. Core.Order];
        return TickResult.Both;
    }

    TickResult AfterRound()
    {
        if (_party is not null) return PartyOver();
        if (_round >= Rounds) return Over();
        _round++;
        BeginRound(ReadyNext);
        return TickResult.Both;
    }

    /// <summary>Хто ще грає партію: сидить за столом або бот цієї партії.</summary>
    int[] Playing() => [.. Enumerable.Range(0, Seats).Where(s => _plays[s] && (Ctx.Seated(s) || IsBot(s)))];

    TickResult Over()
    {
        var c = Core;
        _ph = PhOver;
        var playing = Playing();
        var best = playing.Length == 0 ? 0 : playing.Max(s => c.Runners[s].Points);
        int[] winners = best == 0 ? [] : [.. playing.Where(s => c.Runners[s].Points == best)];
        _winners = winners;
        var scores = playing.Where(Ctx.Seated).ToDictionary(s => s, s => (long)c.Runners[s].Points);
        if (winners.Length == 1) Ctx.Say($"Брід за {Name(winners[0])}. Решту — сушити на тину.");
        if (!_botGame)
        {
            _series.Record(Ctx, winners);
            Ctx.Finish(winners, Journal(winners, playing), scores);
            return TickResult.Both;
        }
        // Перемога — лише коли людина сама вгорі; нарівні з ботом — нічия, а не «🏆» (як у CrowdEye).
        var people = winners.Where(Ctx.Seated).ToArray();
        var lvl = LiveBots.Of(_solo.Level);
        int[] real = winners.Length == 1 && people.Length == 1 ? people : [];
        var verdict = real.Length == 1 ? $"🏆 {Ctx.NickOf(real[0])} — перемога над {lvl}и ботами"
            : people.Length > 0 ? $"🤝 {Ctx.NickOf(people[0])} нарівні з {lvl}и ботами"
            : winners.Length > 0 ? $"🤖 Брід узяв {Name(winners[0])}" : null;
        Ctx.Finish(real, Journal(winners, playing), scores, verdict);
        return TickResult.Both;
    }

    /// <summary>«Брід: Оля 5 : Петро 3 : Ігор 0» — переможці першими, далі за очками й місцем.</summary>
    string Journal(int[] winners, int[] playing)
    {
        var c = Core;
        var order = winners.Concat(playing.Where(s => !winners.Contains(s))
            .OrderByDescending(s => c.Runners[s].Points).ThenBy(s => s));
        var line = $"{Info.Title}: {string.Join(" : ", order.Select(s => $"{Name(s)} {c.Runners[s].Points}"))}";
        return winners.Length == 0 ? line + " — ніхто не перейшов" : line;
    }

    /// <summary>
    /// Scores вечірки: вийшов на той берег — (N − місце)·100 (перший — найбільше), ні — найдальший ряд, на якому
    /// стояв (0..9, тож завжди нижче за будь-кого, хто вийшов). N — скільки місць грає. Місця, що не грали, — −1.
    /// </summary>
    public IReadOnlyDictionary<int, long> PartyScores()
    {
        var c = Core;
        var n = _plays.Count(p => p);
        var r = new Dictionary<int, long>(Ctx.Players);
        for (var s = 0; s < Ctx.Players; s++)
        {
            if (s >= Seats || !_plays[s]) { r[s] = -1; continue; }
            var x = c.Runners[s];
            r[s] = x.Place > 0 ? (long)(n - x.Place) * 100 : x.Best;
        }
        return r;
    }

    TickResult PartyOver()
    {
        _ph = PhOver;
        var scores = PartyScores();
        var best = scores.Count == 0 ? 0 : scores.Values.Max();
        _winners = [.. scores.Where(kv => kv.Value == best && kv.Value >= 0).Select(kv => kv.Key).Order()];
        var c = Core;
        var line = string.Join(" : ", scores.Where(kv => kv.Value >= 0).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
            .Select(kv => c.Runners[kv.Key].Place > 0 ? $"{Name(kv.Key)} {c.Runners[kv.Key].Place}-й" : $"{Name(kv.Key)} ряд {kv.Value}"));
        Ctx.Finish(_winners, $"{Info.Title}: {line}", scores);
        return TickResult.Both;
    }

    /// <summary>Устав посеред партії — решта бреде далі; лишилась одна людина (чи жодної) — партію закінчено.</summary>
    public override void OnLeave(int seat)
    {
        if (!_started || _ph == PhOver || _party is not null || seat is < 0 or >= Seats) return;
        var c = Core;
        var nick = Ctx.NickOf(seat);
        c.Runners[seat].Plays = false;
        _plays[seat] = false;
        var left = Enumerable.Range(0, Seats).Where(s => s != seat && _plays[s] && Ctx.Seated(s)).ToArray();
        if (left.Length >= 2)
        {
            Ctx.Log($"{Info.Title}: {nick} встав з-за столу — решта бреде далі");
            return;
        }
        _ph = PhOver;
        _winners = left;
        if (!_botGame) _series.Record(Ctx, left);
        Ctx.Finish(left, $"{Info.Title}: {nick} встав з-за столу, партію не дограли",
            left.ToDictionary(s => s, s => (long)c.Runners[s].Points));
    }

    // ---------- вид і кадр ----------

    public override object View(int? seat)
    {
        var lobby = Lobby;
        var c = Core;
        var (cols, rows) = lobby || c.Ford is null ? BridFord.SizeOf(1) : (c.Cols, c.Rows);
        var pts = new int?[Seats];
        for (var i = 0; i < Seats; i++) if (!lobby && _plays[i]) pts[i] = c.Runners[i].Points;
        var shown = !lobby && _ph is PhEnd or PhOver && c.Ford is not null;
        return new
        {
            phase = lobby ? "lobby" : _ph switch { PhReady => "ready", PhGo => "go", PhEnd => "end", _ => "over" },
            round = lobby ? 0 : _round,
            rounds = _party is null ? Rounds : 1,
            party = _party is not null,
            cols,
            rows,
            need = lobby ? Math.Min(RoundPoints.Length, Enumerable.Range(0, Seats).Count(s => Ctx.Seated(s)) + BotSeats().Length) : Need(),
            roundTicks = BridCore.RoundTicks,
            tickMs = BridCore.TickMs,
            jumpTicks = BridCore.JumpTicks,
            wetTicks = BridCore.WetTicks,
            trailTicks = BridCore.TrailTicks,
            points = (int[])RoundPoints.Clone(),
            pts,
            order = lobby ? [] : c.Order.ToArray(),
            last = shown && _last is not null ? (int[])_last.Clone() : null,
            // Раунд скінчився — стежку й тупики вже можна показати всім.
            reveal = shown ? Reveal(c.Ford!) : null,
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

    static object Reveal(BridFord f) => new
    {
        path = f.Path.Select(p => new[] { p.X, p.Y }).ToArray(),
        dead = f.Branches.Select(p => new[] { p.X, p.Y }).ToArray(),
    };

    public override object? Frame() => Shot(Lobby);

    /// <summary>
    /// Кадр — пласкі масиви цілих (spec §4): <c>p</c> за місцями [x, y, fx, fy, jump, wet, place, falls, best]
    /// (null — не грає), <c>tr</c> мокрі сліди [x, y, тиків до висихання], <c>sk</c> затоплені [x, y],
    /// <c>ev</c> події тика [вид, місце, x, y]. Нічого про безпечні камені, крім того, що й так видно.
    /// </summary>
    object Shot(bool lobby)
    {
        var c = Core;
        var p = new int[]?[Seats];
        if (lobby)
        {
            var bots = BotSeats();
            var who = Enumerable.Range(0, Seats).Where(s => Ctx.Seated(s) || Array.IndexOf(bots, s) >= 0).ToArray();
            var (cols, _) = BridFord.SizeOf(1);
            for (var k = 0; k < who.Length; k++)
            {
                var x = BridCore.BankCol(k, who.Length, cols);
                p[who[k]] = [x, -1, x, -1, 0, 0, 0, 0, 0];
            }
        }
        else
        {
            for (var i = 0; i < Seats; i++)
            {
                var r = c.Runners[i];
                if (r.Plays) p[i] = [r.X, r.Y, r.Fx, r.Fy, r.Jump, r.Wet, r.Place, r.Falls, r.Best];
            }
        }
        var tr = new List<int[]>();
        var sk = new List<int[]>();
        if (!lobby && c.Ford is not null)
        {
            for (var i = 0; i < c.Trail.Length; i++)
            {
                if (c.Trail[i] > c.T) tr.Add([i % c.Cols, i / c.Cols, c.Trail[i] - c.T]);
                if (c.Sunk[i]) sk.Add([i % c.Cols, i / c.Cols]);
            }
        }
        var ph = lobby ? PhLobby : _ph;
        return new
        {
            t = c.T,
            ph,
            left = ph == PhGo ? Math.Max(0, BridCore.RoundTicks - c.Rt) : ph is PhReady or PhEnd ? _left : 0,
            p,
            tr = tr.ToArray(),
            sk = sk.ToArray(),
            ev = lobby ? [] : c.Events(),
        };
    }
}
