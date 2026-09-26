using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Крижина: сумо на крижині посеред ставка на 2–8. Ковзаєш, штовхаєшся ривком, крижина тріскається й тане, а
/// хто шубовснув — стоїть на березі й кидає сніжки. Останній на кризі бере раунд, партія — до 1/2/3 перемог.
/// Світ живе в <see cref="IcefloeCore"/>; тут — фази (готуйсь → гра → кінець раунду → …), опція, вид, кадр,
/// рахунок партії, вихід посеред гри й ачівки. Spec: <c>docs/games/specs/icefloe.md</c>.
/// </summary>
public sealed class Icefloe : Game
{
    /// <summary>«Готуйсь» перед першим раундом — 3 с, перед наступними — 2 с. Кінець раунду — 3 с.</summary>
    public const int ReadyFirst = 75, ReadyNext = 50, EndTicks = 75;
    /// <summary>Стеля раундів — страховка від нескінченного столу.</summary>
    public const int RoundsMax = 12;
    /// <summary>Фази в кадрі (<c>ph</c>): 0 готуйсь, 1 гра, 2 кінець раунду, 3 партію зіграно, 4 лобі.</summary>
    public const int PhReady = 0, PhGo = 1, PhEnd = 2, PhOver = 3, PhLobby = 4;

    static readonly GameOption WinsOption = new("wins", "Партія до",
        [("1", "1 раунду"), ("2", "2 перемог"), ("3", "3 перемог")], "2");

    static readonly string[] Names = ["синій", "рудий", "зелений", "жовтий", "бузковий", "м’ятний", "рожевий", "сірий"];

    public override GameInfo Info { get; } = new(
        "icefloe", "Крижина", "крижину", GameGroup.Live, 2, IcefloeCore.Seats, TickMs: IcefloeCore.TickMs,
        Start: StartMode.ByHost, Options: [WinsOption],
        Hint: "Сумо на кризі: ковзай, штовхай, не шубовсни. Крижина тане й меншає, а хто випав — кидає сніжки з берега");

    IcefloeCore? _core;
    bool _started;
    int _ph = PhReady;
    int _left;
    int _need = 2;
    int _round;
    int _r0 = IcefloeCore.BaseRadius(2);
    /// <summary>Скільки людей сиділо на «Почати» — ачівки лише для справжніх партій на двох і більше.</summary>
    int _startPlayers;
    int _roundWinner = -1;
    /// <summary>Минулий раунд: хто взяв (−1 — нічия) і хто кого випхнув.</summary>
    (int Winner, (int Fell, int By)[] By)? _lastRound;
    int[] _winners = [];
    string[] _startNicks = [];
    readonly Series _series = new();

    public IcefloeCore Core
    {
        get
        {
            if (_core is not null) return _core;
            _core = new IcefloeCore(Ctx.Rng);
            _core.ResetParty(Seated());
            _core.Flat(IcefloeCore.BaseRadius(2));
            return _core;
        }
    }

    public int Need => _need;
    public int RoundNo => _round;
    public int Phase => _ph;
    public int Left => _left;

    bool[] Seated() => [.. Enumerable.Range(0, IcefloeCore.Seats).Select(Ctx.Seated)];

    /// <summary>
    /// Стіл чекає старту: партії ще не було, або дограний стіл відкрив новий гравець (сів той, кого не було на
    /// старті) — тоді показуємо свіжу кригу під новий склад, а не старий підсумок (правило з понга).
    /// </summary>
    bool Lobby => !_started || (_ph == PhOver && Enumerable.Range(0, IcefloeCore.Seats)
        .Any(s => Ctx.Seated(s) && !_startNicks.Contains(Ctx.NickOf(s) ?? "", StringComparer.OrdinalIgnoreCase)));

    public override string SeatName(int seat) => seat is >= 0 and < IcefloeCore.Seats ? Names[seat] : base.SeatName(seat);

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _need = options.TryGetValue("wins", out var v) && int.TryParse(v, out var n) && n is >= 1 and <= 3 ? n : 2;
    }

    public override void Start()
    {
        _started = true;
        var seated = Seated();
        _startNicks = [.. Enumerable.Range(0, IcefloeCore.Seats).Where(Ctx.Seated).Select(s => Ctx.NickOf(s) ?? "")];
        _startPlayers = seated.Count(x => x);
        _r0 = IcefloeCore.BaseRadius(_startPlayers);
        _series.Begin(Ctx, IcefloeCore.Seats);
        _winners = [];
        _lastRound = null;
        _roundWinner = -1;
        _round = 1;
        Core.ResetParty(seated);
        BeginRound(ReadyFirst);
    }

    void BeginRound(int ready)
    {
        Core.NewRound(_r0);
        _ph = PhReady;
        _left = ready;
        _roundWinner = -1;
    }

    // ---------- ввід ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (!_started) return ActResult.Fail("Партія ще не почалась");
        if (seat is < 0 or >= IcefloeCore.Seats || !Core.Bodies[seat].Plays) return ActResult.Fail("Ти тут не граєш");
        if (_ph == PhOver) return ActResult.Fail("Партію вже зіграно");
        switch (action)
        {
            case "move":
                // Намір, а не хід: сектор приймаємо й на відліку, і з води (там це приціл сніжки).
                if (Sector(payload) is not { } a || a is < -1 or > 15) return ActResult.Fail("Такого напрямку нема");
                Core.Move(seat, a);
                return ActResult.Done;
            case "dash":
                if (_ph != PhGo) return ActResult.Fail("Зачекай, зараз почнемо");
                return Core.Dash(seat) is { } no ? ActResult.Fail(no) : ActResult.Done;
            case "throw":
                if (_ph != PhGo) return ActResult.Fail("Зачекай, зараз почнемо");
                return Core.Throw(seat) is { } nope ? ActResult.Fail(nope) : ActResult.Done;
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
        switch (_ph)
        {
            case PhOver:
                return TickResult.None;
            case PhReady:
                c.T++;
                c.Idle();
                if (--_left > 0) return c.T % 5 == 0 ? TickResult.FrameOnly : TickResult.None;
                _ph = PhGo;
                c.Rt = 0;
                return TickResult.Both;
            case PhGo:
                c.Step(true);
                if (c.AliveCount <= 1) return EndRound(Survivor());
                if (c.Rt >= IcefloeCore.CapTicks) return EndRound(-1);
                return c.Broke ? TickResult.Both : TickResult.FrameOnly;
            default:
                c.Step(false);
                if (--_left > 0) return c.Moving || c.T % 5 == 0 ? TickResult.FrameOnly : TickResult.None;
                return AfterRound();
        }
    }

    int Survivor()
    {
        var c = Core;
        for (var i = 0; i < IcefloeCore.Seats; i++) if (c.Bodies[i].Plays && c.Bodies[i].Alive) return i;
        return -1;
    }

    /// <summary>Раунд скінчився: переможець (−1 — нічия) бере очко, три секунди «Раунд — Оля!».</summary>
    TickResult EndRound(int winner)
    {
        var c = Core;
        _ph = PhEnd;
        _left = EndTicks;
        _roundWinner = winner;
        if (winner >= 0) c.Bodies[winner].Wins++;
        _lastRound = (winner, [.. c.ByList]);
        c.ClearCrack();
        c.Event(IcefloeCore.EvRound, winner);
        return TickResult.Both;
    }

    TickResult AfterRound()
    {
        var c = Core;
        if (_roundWinner >= 0 && c.Bodies[_roundWinner].Plays && c.Bodies[_roundWinner].Wins >= _need)
            return Over([_roundWinner]);
        if (_round >= RoundsMax)
        {
            var playing = Playing();
            var best = playing.Length == 0 ? 0 : playing.Max(s => c.Bodies[s].Wins);
            return Over(best == 0 ? [] : [.. playing.Where(s => c.Bodies[s].Wins == best)]);
        }
        _round++;
        BeginRound(ReadyNext);
        return TickResult.Both;
    }

    /// <summary>Хто ще грає партію й сидить за столом.</summary>
    int[] Playing() => [.. Enumerable.Range(0, IcefloeCore.Seats).Where(s => Core.Bodies[s].Plays && Ctx.Seated(s))];

    /// <summary>Кінець партії: рядок Журналу, випхнуті кожного в таблицю результатів, серія й ачівки.</summary>
    TickResult Over(int[] winners)
    {
        var c = Core;
        _ph = PhOver;
        _winners = winners;
        var playing = Playing();
        if (_startPlayers >= 2)
        {
            foreach (var w in winners)
                if (!c.Bodies[w].Wet) Ctx.Award(w, 0, "ach:icefloe-dry");
            foreach (var s in playing)
                if (c.Bodies[s].Pushouts >= 5) Ctx.Award(s, 0, "ach:icefloe-push5");
        }
        _series.Record(Ctx, winners);
        Ctx.Finish(winners, Journal(winners, playing), playing.ToDictionary(s => s, s => (long)c.Bodies[s].Pushouts));
        return TickResult.Both;
    }

    /// <summary>«Крижина: Оля 2 : Петро 1 : Ігор 0» — переможці першими, далі за раундами, випхнутими, місцем.</summary>
    string Journal(int[] winners, int[] playing)
    {
        var c = Core;
        var order = winners.Concat(playing.Where(s => !winners.Contains(s))
            .OrderByDescending(s => c.Bodies[s].Wins).ThenByDescending(s => c.Bodies[s].Pushouts).ThenBy(s => s));
        var line = $"{Info.Title}: {string.Join(" : ", order.Select(s => $"{Ctx.NickOf(s)} {c.Bodies[s].Wins}"))}";
        return winners.Length == 0 ? line + " — нічия" : line;
    }

    /// <summary>
    /// Хтось устав посеред партії: тіло зникає (без «шубовсь» і без заліку), решта грає далі. Лишився один —
    /// партія його; нікого — нічия. Раунд, у якому живих стало ≤ 1, закінчить звичайний тик.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (!_started || _ph == PhOver) return;
        var c = Core;
        var nick = Ctx.NickOf(seat);
        c.Drop(seat);
        var left = Enumerable.Range(0, IcefloeCore.Seats).Where(s => s != seat && c.Bodies[s].Plays && Ctx.Seated(s)).ToArray();
        if (left.Length >= 2)
        {
            Ctx.Log($"{Info.Title}: {nick} встав з-за столу — решта грає далі");
            return;
        }
        _ph = PhOver;
        _winners = left;
        _series.Record(Ctx, left);
        Ctx.Finish(left, $"{Info.Title}: {nick} встав з-за столу, партію не дограли",
            left.ToDictionary(s => s, s => (long)c.Bodies[s].Pushouts));
    }

    // ---------- вид і кадр ----------

    /// <summary>Світ для малювання: справжній у партії, а в лобі — свіжа рівна крижина під тих, хто вже сів.</summary>
    IcefloeCore Field
    {
        get
        {
            if (!Lobby) return Core;
            var seated = Seated();
            var preview = new IcefloeCore(Ctx.Rng);   // генератора не чіпає: Flat і Spawn випадковості не питають
            preview.ResetParty(seated);
            preview.Flat(IcefloeCore.BaseRadius(Math.Max(2, seated.Count(x => x))));
            return preview;
        }
    }

    public override object View(int? seat)
    {
        var lobby = Lobby;
        var c = Field;
        int?[] wins = new int?[IcefloeCore.Seats], pushouts = new int?[IcefloeCore.Seats];
        for (var i = 0; i < IcefloeCore.Seats; i++)
        {
            if (!c.Bodies[i].Plays || lobby) continue;
            wins[i] = c.Bodies[i].Wins;
            pushouts[i] = c.Bodies[i].Pushouts;
        }
        var v = new int[IcefloeCore.Vertices];
        for (var i = 0; i < v.Length; i++) v[i] = (int)Math.Round(c.R[i]);
        return new
        {
            phase = lobby ? "lobby" : _ph switch { PhReady => "ready", PhGo => "go", PhEnd => "end", _ => "over" },
            round = lobby ? 0 : _round,
            need = _need,
            roundsMax = RoundsMax,
            wins,
            pushouts,
            ice = new { r0 = c.R0, v, iv = c.Iv },
            pond = (int)IcefloeCore.Pond,
            shore = (int)c.Shore,
            bank = (int)c.Bank,
            bodyR = (int)IcefloeCore.BodyR,
            lastRound = lobby || _lastRound is not { } lr ? null : new
            {
                winner = lr.Winner,
                by = lr.By.Select(p => new[] { p.Fell, p.By }).ToArray(),
            },
            @out = lobby ? [] : c.Out.ToArray(),
            winner = !lobby && _ph == PhOver && _winners.Length > 0 ? _winners[0] : (int?)null,
            winners = !lobby && _ph == PhOver ? (int[])_winners.Clone() : [],
            series = _series.View(Ctx, IcefloeCore.Seats),
            turn = (int?)null,
            frame = Shot(c, lobby),
        };
    }

    public override object? Frame() => Shot(Field, Lobby);

    /// <summary>
    /// Кадр — пласкі масиви цілих (spec §4.2): <c>p</c> за місцями [x, y, vx, vy, face, fl, cd, ammo] (null —
    /// не грає), <c>s</c> сніжки [id, x, y, vx, vy], <c>k</c> підбирачки [x, y, kind], <c>ev</c> події тика.
    /// Масиви нові щоразу: кадр серіалізують уже поза замком кімнати.
    /// </summary>
    object Shot(IcefloeCore c, bool lobby)
    {
        var p = new int[]?[IcefloeCore.Seats];
        for (var i = 0; i < IcefloeCore.Seats; i++)
        {
            var b = c.Bodies[i];
            if (!b.Plays) continue;
            var fl = (b.Alive ? 1 : 16) | (b.Spikes > 0 ? 2 : 0) | (b.Jug > 0 ? 4 : 0) | (b.Hit > 0 ? 8 : 0) | (b.Want >= 0 ? 32 : 0);
            p[i] = b.Alive
                ? [(int)Math.Round(b.B.X), (int)Math.Round(b.B.Y), (int)Math.Round(b.B.Vx), (int)Math.Round(b.B.Vy), b.Face, fl, b.Cd, b.Ammo]
                : [(int)Math.Round(b.B.X), (int)Math.Round(b.B.Y), 0, 0, b.Face, fl, b.ThrowCd, b.BankAmmo];
        }
        var balls = 0;
        foreach (var ball in c.Balls) if (ball.On) balls++;
        var s = new int[balls][];
        var n = 0;
        foreach (var ball in c.Balls)
            if (ball.On) s[n++] = [ball.Id, (int)Math.Round(ball.X), (int)Math.Round(ball.Y), (int)Math.Round(ball.Vx), (int)Math.Round(ball.Vy)];
        var picks = 0;
        foreach (var k in c.Pickups) if (k.On) picks++;
        var kk = new int[picks][];
        n = 0;
        foreach (var k in c.Pickups)
            if (k.On) kk[n++] = [(int)Math.Round(k.X), (int)Math.Round(k.Y), k.Kind];
        var ph = lobby ? PhLobby : _ph;
        return new
        {
            t = c.T,
            ph,
            left = ph == PhGo ? Math.Max(0, IcefloeCore.CapTicks - c.Rt) : ph is PhReady or PhEnd ? _left : 0,
            melt = c.Melt,
            iv = c.Iv,
            crack = c.CrackS >= 0 ? new[] { c.CrackS, c.CrackL } : null,
            p,
            s,
            k = kk,
            ev = lobby ? [] : c.Events(),
        };
    }
}
