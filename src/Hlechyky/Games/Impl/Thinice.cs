using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Тонкий лід: ставок плиток у два яруси на 2–8. Плитка, на яку ступив, тріщить і за секунду провалюється; з
/// верхнього ярусу падаєш на нижній, з нижнього — у воду. Стояти не можна, штовхатись теж — лише відрізати дорогу.
/// Партія — 3 раунди, очки за порядок вибування; у вечірці — один раунд з відлигою.
/// Світ — <see cref="ThiniceCore"/>, бот — <see cref="ThiniceBot"/>. Spec: <c>docs/games/specs/thinice.md</c>.
/// </summary>
public sealed class Thinice : Game, IPartyMinigame
{
    /// <summary>«Готуйсь»: перший раунд 3 с, наступні 2 с; кінець раунду 3 с (у вечірці 2 с).</summary>
    public const int ReadyFirst = 75, ReadyNext = 50, EndTicks = 75, PartyEndTicks = 50;
    public const int Rounds = 3;
    /// <summary>Фази в кадрі (<c>ph</c>): 0 готуйсь, 1 гра, 2 кінець раунду, 3 партію зіграно, 4 лобі.</summary>
    public const int PhReady = 0, PhGo = 1, PhEnd = 2, PhOver = 3, PhLobby = 4;
    /// <summary>Відлига й стеля раунду (тиків від початку гри): звичайна партія — 60 с і 120 с, вечірка — 50 с і 80 с.</summary>
    public const int ThawTicks = 1500, CapTicks = 3000, PartyThawTicks = 1250, PartyCapTicks = 2000;
    /// <summary>Намір без підтвердження живе 1,2 с (браузер досилає раз на 0,4 с), далі гравець зупиняється.</summary>
    public const int KeepTicks = 30;
    /// <summary>Самому — троє ботів: на чотирьох ставок 12×12, є кого відрізати.</summary>
    public const int SoloBots = 3;
    /// <summary>Для «Стрибунця»: стільки стрибків у виграному раунді.</summary>
    public const int JumperJumps = 5;

    static readonly string[] Names = ["синій", "рудий", "зелений", "жовтий", "бузковий", "м’ятний", "рожевий", "сірий"];
    static readonly string[] StartLines =
    [
        "Лід тонкий, а ви важкі — бо пиріжки.",
        "Ставок замерз аж на два пальці. Ну, на півтора. Ходіть легенько.",
        "Хто провалиться — той і рибу нагодує. Поїхали!",
    ];
    static readonly string[] ThawLines =
    [
        "Відлига! Лід тріщить сам — тепер уже ніхто не відсидиться.",
        "Сонечко пригріло — ставок здається. Ховатись нема де!",
    ];

    public override GameInfo Info { get; } = new(
        "thinice", "Тонкий лід", "тонкий лід", GameGroup.Live, 1, ThiniceCore.Seats, TickMs: ThiniceCore.TickMs,
        Start: StartMode.ByHost, Options: [LiveBots.LevelOption],
        Hint: "Ставок тріщить під ногами: ступив — за секунду дірка. Стояти не можна, штовхатись теж — відріж суперникам дорогу й лишись останнім на льоду. Самому — з 🤖 ботами");

    readonly SoloBot _solo = new();
    int[] _bots = [];
    readonly ThiniceBot?[] _brain = new ThiniceBot?[ThiniceCore.Seats];
    bool _botGame;
    static readonly JsonElement[] SectorEl = [.. Enumerable.Range(-1, 17).Select(a => JsonSerializer.SerializeToElement(a))];
    public IReadOnlyList<int> Bots => _bots;
    public bool BotGame => _botGame;

    ThiniceCore? _core;
    bool _started;
    int _ph = PhReady;
    int _left;
    int _round;
    int _rounds = Rounds;
    int _bodies = 2;
    readonly int[] _moveAt = new int[ThiniceCore.Seats];
    int _goAt;
    int _startPlayers;
    bool _thawSaid;
    /// <summary>Минулий раунд: хто лишився сам (−1 — нікого чи кілька) і місце кожного (−1 — не грав).</summary>
    (int Winner, bool ByTime, int[] Ranks)? _lastRound;
    int[] _winners = [];
    string[] _startNicks = [];
    readonly Series _series = new();
    PartyMode? _party;
    public bool Party => _party is not null;

    public string Howto => "Лід тріщить під ногами — не стій, біжи по цілому й лишись останнім. "
        + "Стрілки/WASD — бігти, пробіл — стрибок через дірку; на телефоні — стік і кнопка";
    public int PartyCapMs => 90_000;   // 3 с відліку + раунд до 80 с + 2 с підсумку = 85 с
    public int PartyMin => 2;
    public int PartyMax => ThiniceCore.Seats;

    public ThiniceCore Core
    {
        get
        {
            if (_core is not null) return _core;
            _core = new ThiniceCore(Ctx.Rng);
            _core.ResetParty(Seated());
            _core.NewRound(2, 0);
            return _core;
        }
    }

    public int Phase => _ph;
    public int RoundNo => _round;
    public int RoundsTotal => _rounds;

    bool[] Seated() => [.. Enumerable.Range(0, ThiniceCore.Seats).Select(Ctx.Seated)];

    int[] BotSeats() => _solo.Active(Ctx, ThiniceCore.Seats)
        ? [.. Enumerable.Range(0, ThiniceCore.Seats).Where(s => !Ctx.Seated(s)).Take(SoloBots)] : [];

    bool[] WithBots(int[] bots)
    {
        var r = Seated();
        foreach (var s in bots) r[s] = true;
        return r;
    }

    bool IsBot(int seat) => Array.IndexOf(_bots, seat) >= 0 && !Ctx.Seated(seat);

    public override string? SeatBot(int seat) =>
        !Ctx.Seated(seat) && Array.IndexOf(_started ? _bots : BotSeats(), seat) >= 0 ? $"{LiveBots.Name} {SeatName(seat)}" : null;

    string Name(int seat) => SeatBot(seat) ?? Ctx.NickOf(seat) ?? SeatName(seat);

    public override bool ActsInLobby => true;

    /// <summary>Чекаємо старту: партії не було, або дограний стіл відкрив новий гравець (правило з Крижини).</summary>
    bool Lobby => !_started || (_ph == PhOver && Enumerable.Range(0, ThiniceCore.Seats)
        .Any(s => Ctx.Seated(s) && !_startNicks.Contains(Ctx.NickOf(s) ?? "", StringComparer.OrdinalIgnoreCase)));

    public override string SeatName(int seat) => seat is >= 0 and < ThiniceCore.Seats ? Names[seat] : base.SeatName(seat);

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _solo.Configure(options);
        _party = PartyMode.Read(options);
        _rounds = _party is null ? Rounds : 1;
    }

    public override string? CanStart() => _solo.CanStart(Ctx, ThiniceCore.Seats);

    public override void Start()
    {
        _started = true;
        _bots = _party is { } pm ? [.. pm.Bots.Where(s => s < ThiniceCore.Seats && !Ctx.Seated(s))] : BotSeats();
        _botGame = _bots.Length > 0;
        Array.Clear(_brain);
        var level = _party?.Level ?? _solo.Level;
        for (var i = 0; i < _bots.Length; i++) _brain[_bots[i]] = new ThiniceBot(level, i);
        var seated = WithBots(_bots);
        _startNicks = [.. Enumerable.Range(0, ThiniceCore.Seats).Where(Ctx.Seated).Select(s => Ctx.NickOf(s) ?? "")];
        _startPlayers = Enumerable.Range(0, ThiniceCore.Seats).Count(Ctx.Seated);
        _bodies = seated.Count(x => x);
        _series.Begin(Ctx, ThiniceCore.Seats);
        _winners = [];
        _lastRound = null;
        _round = 1;
        Core.ResetParty(seated);
        BeginRound(ReadyFirst);
        // Глек — на початку партії, не щораунду й не у вечірці (там міні-ігор багато).
        if (_party is null && (Ctx.Round == 1 || Ctx.Rng.Next(3) == 0))
            Ctx.Say(StartLines[Ctx.Rng.Next(StartLines.Length)]);
    }

    void BeginRound(int ready)
    {
        var c = Core;
        c.NewRound(_bodies, Ctx.Rng.NextDouble());
        c.ThawAt = _party is null ? ThawTicks : PartyThawTicks;
        Array.Clear(_moveAt);
        _ph = PhReady;
        _left = ready;
        _thawSaid = false;
    }

    // ---------- ввід ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == LiveBots.Toggle)
            return _started && _ph != PhOver ? ActResult.Fail("Партія вже йде") : _solo.Switch(Ctx, seat, payload, ThiniceCore.Seats);
        if (!_started) return ActResult.Fail("Чекаємо на гравців");
        if (seat is < 0 or >= ThiniceCore.Seats || !Core.Bodies[seat].Plays) return ActResult.Fail("Ти тут не граєш");
        if (_ph == PhOver) return ActResult.Fail("Партію вже зіграно");
        switch (action)
        {
            case "move":
                if (Sector(payload) is not { } a || a is < -1 or > 15) return ActResult.Fail("Такого напрямку нема");
                Core.Move(seat, a);
                _moveAt[seat] = Core.T;
                return ActResult.Done;
            case "jump":
                if (_ph != PhGo) return ActResult.Fail("Зачекай, зараз почнемо");
                return Core.Jump(seat) is { } no ? ActResult.Fail(no) : ActResult.Done;
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    /// <summary>Сектор приймаємо і як <c>{ a: 5 }</c>, і як голе <c>5</c> — лише ціле число.</summary>
    static int? Sector(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetInt32(out var n) => n,
        JsonValueKind.Object when payload.TryGetProperty("a", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out var n) => n,
        _ => null,
    };

    // ---------- тик ----------

    public override TickResult Tick()
    {
        var c = Core;
        c.BeginTick();
        switch (_ph)
        {
            case PhOver:
                return TickResult.None;
            case PhReady:
                c.T++;
                if (--_left > 0) return c.T % 5 == 0 ? TickResult.FrameOnly : TickResult.None;
                _ph = PhGo;
                _goAt = c.T;
                return TickResult.Both;
            case PhGo:
                BotsThink(c);
                c.Step();
                Expire();
                if (c.Thawing && !_thawSaid)
                {
                    _thawSaid = true;
                    if (_party is not null || Ctx.Rng.Next(2) == 0) Ctx.Say(ThawLines[Ctx.Rng.Next(ThawLines.Length)]);
                }
                if (c.AliveCount <= 1) return EndRound(false);
                if (c.Rt >= (_party is null ? CapTicks : PartyCapTicks)) return EndRound(true);
                return TickResult.FrameOnly;
            default:
                // Кінець раунду: світ стоїть (хто ще на льоду — не тріщить), лише відлік.
                c.T++;
                if (--_left > 0) return c.T % 5 == 0 ? TickResult.FrameOnly : TickResult.None;
                return AfterRound();
        }
    }

    void Expire()
    {
        var c = Core;
        for (var s = 0; s < ThiniceCore.Seats; s++)
        {
            var b = c.Bodies[s];
            if (b.Want >= 0 && c.T - Math.Max(_moveAt[s], _goAt) >= KeepTicks) b.Want = -1;
        }
    }

    /// <summary>Місце тіла в раунді: скільки впало раніше; хто ще на льоду — скільки впало всього.</summary>
    int Rank(ThiniceBody b) => !b.Plays ? -1 : b.In ? Core.Out.Count : b.OutRank;

    /// <summary>
    /// Раунд скінчився (лишився один, нікого, або стеля часу): кожному — очки за місце, одинокому на льоду —
    /// виграний раунд. <paramref name="byTime"/> — на льоду лишилось кілька, і вони рівні.
    /// </summary>
    TickResult EndRound(bool byTime)
    {
        var c = Core;
        _ph = PhEnd;
        _left = _party is null ? EndTicks : PartyEndTicks;
        var ranks = new int[ThiniceCore.Seats];
        var alive = new List<int>();
        for (var s = 0; s < ThiniceCore.Seats; s++)
        {
            var b = c.Bodies[s];
            ranks[s] = Rank(b);
            if (!b.Plays) continue;
            b.Points += ranks[s];
            if (b.In) alive.Add(s);
        }
        var winner = alive.Count == 1 ? alive[0] : -1;
        if (winner >= 0)
        {
            c.Bodies[winner].RoundWins++;
            if (Awards && c.Bodies[winner].Jumps >= JumperJumps) Ctx.Award(winner, 0, "ach:thinice-jumper");
        }
        _lastRound = (winner, byTime, ranks);
        c.Event(ThiniceCore.EvRound, winner);
        return TickResult.Both;
    }

    /// <summary>Ачівки — лише у звичайній партії від двох людей і без ботів.</summary>
    bool Awards => _party is null && !_botGame && _startPlayers >= 2;

    TickResult AfterRound()
    {
        if (_party is not null) return PartyOver();
        if (_round >= _rounds) return Over();
        _round++;
        BeginRound(ReadyNext);
        return TickResult.Both;
    }

    int[] Playing() => [.. Enumerable.Range(0, ThiniceCore.Seats).Where(s => Core.Bodies[s].Plays && (Ctx.Seated(s) || IsBot(s)))];

    void BotsThink(ThiniceCore c)
    {
        foreach (var s in _bots)
        {
            if (Ctx.Seated(s) || _brain[s] is not { } bot || !c.Bodies[s].Plays || !c.Bodies[s].In) continue;
            var (a, jump) = bot.Think(c, s, Ctx.Rng);
            if (a != c.Bodies[s].Want || c.T - _moveAt[s] >= KeepTicks / 2) Act(s, "move", SectorEl[a + 1]);
            if (jump) Act(s, "jump", default);
        }
    }

    /// <summary>
    /// Переможці партії: найбільше очок, за рівності — більше виграних раундів; рівні й далі — ділять перемогу.
    /// Усі рівні між собою — нічия (порожньо).
    /// </summary>
    int[] Best(int[] playing)
    {
        var c = Core;
        if (playing.Length == 0) return [];
        var top = playing.Max(s => (c.Bodies[s].Points, c.Bodies[s].RoundWins));
        var w = playing.Where(s => (c.Bodies[s].Points, c.Bodies[s].RoundWins) == top).ToArray();
        return w.Length == playing.Length && w.Length > 1 ? [] : w;
    }

    TickResult Over()
    {
        var c = Core;
        _ph = PhOver;
        var playing = Playing();
        _winners = Best(playing);
        if (Awards)
        {
            foreach (var w in _winners)
                if (!c.Bodies[w].Dropped) Ctx.Award(w, 0, "ach:thinice-figure");
            if (_winners.Length == 1 && c.Bodies[_winners[0]].RoundWins >= Rounds) Ctx.Award(_winners[0], 0, "ach:thinice-sweep");
        }
        var scores = playing.Where(Ctx.Seated).ToDictionary(s => s, s => (long)c.Bodies[s].Points);
        if (_winners.Length == 1 && _party is null && Ctx.Rng.Next(2) == 0)
            Ctx.Say($"{Name(_winners[0])} пройшов ставок, як по паркету. Решта — сушіть валянки.");
        if (!_botGame)
        {
            _series.Record(Ctx, _winners);
            Ctx.Finish(_winners, Journal(_winners, playing), scores);
            return TickResult.Both;
        }
        var people = _winners.Where(Ctx.Seated).ToArray();
        var verdict = people.Length > 0 ? $"🏆 {Ctx.NickOf(people[0])} — перемога над {LiveBots.Of(_solo.Level)}и ботами"
            : _winners.Length > 0 ? $"🤖 Ставок за {Name(_winners[0])}" : null;
        Ctx.Finish(people, Journal(_winners, playing), scores, verdict);
        return TickResult.Both;
    }

    /// <summary>
    /// Scores вечірки: хто впав першим — 0, другим — 1…, хто ще на льоду — скільки впало (рівні між собою).
    /// Місця, що не грали, — −1.
    /// </summary>
    public IReadOnlyDictionary<int, long> PartyScores()
    {
        var c = Core;
        var r = new Dictionary<int, long>(Ctx.Players);
        for (var s = 0; s < Ctx.Players; s++)
            r[s] = s >= ThiniceCore.Seats ? -1 : Rank(c.Bodies[s]);
        return r;
    }

    TickResult PartyOver()
    {
        _ph = PhOver;
        var scores = PartyScores();
        var best = scores.Count == 0 ? 0 : scores.Values.Max();
        _winners = [.. scores.Where(kv => kv.Value == best && kv.Value >= 0).Select(kv => kv.Key).Order()];
        Ctx.Finish(_winners, Journal(_winners, Playing()), scores);
        return TickResult.Both;
    }

    /// <summary>«Тонкий лід: Оля 5 : Петро 3 : Ігор 1» — переможці першими, далі за очками.</summary>
    string Journal(int[] winners, int[] playing)
    {
        var c = Core;
        var order = winners.Concat(playing.Where(s => !winners.Contains(s))
            .OrderByDescending(s => c.Bodies[s].Points).ThenByDescending(s => c.Bodies[s].RoundWins).ThenBy(s => s));
        var line = $"{Info.Title}: {string.Join(" : ", order.Select(s => $"{Name(s)} {c.Bodies[s].Points}"))}";
        return winners.Length == 0 ? line + " — нічия" : line;
    }

    /// <summary>
    /// Хтось устав посеред партії: тіло зникає, решта грає далі; людей лишилось менше двох — партія їм.
    /// У вечірці вихід грі не прокидається (місце дограє стоячи).
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (!_started || _ph == PhOver || _party is not null) return;
        var c = Core;
        var nick = Ctx.NickOf(seat);
        c.Drop(seat);
        var left = Enumerable.Range(0, ThiniceCore.Seats).Where(s => s != seat && c.Bodies[s].Plays && Ctx.Seated(s)).ToArray();
        if (left.Length >= 2)
        {
            Ctx.Log($"{Info.Title}: {nick} встав з-за столу — решта грає далі");
            return;
        }
        _ph = PhOver;
        _winners = left;
        if (!_botGame) _series.Record(Ctx, left);
        Ctx.Finish(left, $"{Info.Title}: {nick} встав з-за столу, партію не дограли",
            left.ToDictionary(s => s, s => (long)c.Bodies[s].Points));
    }

    // ---------- вид і кадр ----------

    /// <summary>Світ для малювання: справжній у партії, а в лобі — свіжий цілий ставок під тих, хто вже сів.</summary>
    ThiniceCore Field
    {
        get
        {
            if (!Lobby) return Core;
            var seated = WithBots(BotSeats());
            var preview = new ThiniceCore(Ctx.Rng);   // NewRound з поворотом 0 генератора не чіпає
            preview.ResetParty(seated);
            preview.NewRound(Math.Max(2, seated.Count(x => x)), 0);
            return preview;
        }
    }

    public override object View(int? seat)
    {
        var lobby = Lobby;
        var c = Field;
        int?[] points = new int?[ThiniceCore.Seats], wins = new int?[ThiniceCore.Seats];
        for (var i = 0; i < ThiniceCore.Seats; i++)
        {
            if (!c.Bodies[i].Plays || lobby) continue;
            points[i] = c.Bodies[i].Points;
            wins[i] = c.Bodies[i].RoundWins;
        }
        return new
        {
            phase = lobby ? "lobby" : _ph switch { PhReady => "ready", PhGo => "go", PhEnd => "end", _ => "over" },
            round = lobby ? 0 : _round,
            rounds = _rounds,
            party = _party is not null,
            n = c.N,
            sub = ThiniceCore.Sub,
            tickMs = ThiniceCore.TickMs,
            speed = ThiniceCore.Speed,
            crack = ThiniceCore.CrackTicks,
            air = ThiniceCore.AirTicks,
            fall = ThiniceCore.FallTicks,
            jumpCd = ThiniceCore.JumpCd,
            thawAt = _party is null ? ThawTicks : PartyThawTicks,
            capAt = _party is null ? CapTicks : PartyCapTicks,
            points,
            wins,
            @out = lobby ? [] : c.Out.ToArray(),
            lastRound = lobby || _lastRound is not { } lr ? null : new
            {
                winner = lr.Winner,
                byTime = lr.ByTime,
                ranks = (int[])lr.Ranks.Clone(),
            },
            winner = !lobby && _ph == PhOver && _winners.Length > 0 ? _winners[0] : (int?)null,
            winners = !lobby && _ph == PhOver ? (int[])_winners.Clone() : [],
            series = _series.View(Ctx, ThiniceCore.Seats),
            turn = (int?)null,
            botOffer = _party is null && _solo.Offer(Ctx, ThiniceCore.Seats),
            botWanted = _solo.Wanted,
            botLvl = _party is { } pm ? LiveBots.Key(pm.Level) : _solo.LevelKey,
            bot = lobby ? BotSeats() : _bots.Where(s => !Ctx.Seated(s)).ToArray(),
            frame = Shot(c, lobby),
        };
    }

    public override object? Frame() => Shot(Field, Lobby);

    /// <summary>
    /// Кадр (spec §4): <c>p</c> за місцями [x, y, ярус, fl, air, fall, cd, face] (null — не грає), <c>ice</c> —
    /// два рядки ярусів, <c>ev</c> — події тика. Масиви нові щоразу: кадр серіалізують поза замком кімнати.
    /// </summary>
    object Shot(ThiniceCore c, bool lobby)
    {
        var p = new int[]?[ThiniceCore.Seats];
        for (var i = 0; i < ThiniceCore.Seats; i++)
        {
            var b = c.Bodies[i];
            if (!b.Plays) continue;
            var moving = b.In && b.Fall == 0 && (b.Air > 0 ? b.JumpDir : b.Want) >= 0;
            var fl = (b.In ? 1 : 4) | (moving ? 2 : 0);
            p[i] = [b.X, b.Y, b.Tier, fl, b.Air, b.Fall, b.Cd, b.Face];
        }
        var ph = lobby ? PhLobby : _ph;
        var cap = _party is null ? CapTicks : PartyCapTicks;
        return new
        {
            t = c.T,
            ph,
            left = ph == PhGo ? Math.Max(0, cap - c.Rt) : ph is PhReady or PhEnd ? _left : 0,
            rt = lobby ? 0 : c.Rt,
            thaw = !lobby && c.Thawing,
            ice = new[] { c.Row(0), c.Row(1) },
            p,
            ev = lobby ? [] : c.Events(),
        };
    }
}
