using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Танчики на двох–шістьох: арена згори, цегла ламається, хто перший набере фрагів — той і взяв.
/// Правила поля живуть у <see cref="TanksCore"/>, а тут — фази («готуйсь», гра, кінець), опції, вид і кадр.
/// Кадр летить 25 разів на секунду, тож у ньому лише короткі числа.
/// Прохід №3: команди з глеками («Бережи базу»), кооп проти хвиль 🤖, кущі й лід, помста, серії, підсумок.
/// </summary>
public sealed class Tanks : Game
{
    /// <summary>«Готуйсь» на свіжій мапі — дві секунди.</summary>
    public const int StartTicks = 50;
    public const string PhaseStart = "start", PhaseGo = "go", PhaseOver = "over";
    /// <summary>Командна партія довша — глек за дві хвилини не доламаєш: три хвилини.</summary>
    public const int TeamMatchTicks = 4500;
    /// <summary>Хвилі — без годинника, але й не вічно: за шість хвилин, хай і недобиті, глек вистояв.</summary>
    public const int WavesMatchTicks = 9000;
    /// <summary>Скільки тиків подія живе в кадрі (стрічка під полем): три секунди.</summary>
    public const int EventTicks = 75;
    /// <summary>«За столом»: на двох — до 5, на трьох-чотирьох — до 8, на п'ятьох-шістьох — до 12.</summary>
    public static int FragsFor(int players) => players <= 2 ? 5 : players <= 4 ? 8 : 12;
    public static readonly string[] TeamNames = ["🥒 Огірки", "🍅 Помідори"];

    public override GameInfo Info { get; } = new(
        "tanks", "Танчики", "танчики", GameGroup.Live, 2, TanksCore.Seats, TickMs: TanksCore.TickMs,
        Start: StartMode.ByHost, Score: ScoreOrder.HigherIsBetter,
        Options:
        [
            new GameOption("frags", "Грати до", [("auto", "За столом"), ("5", "5 фрагів"), ("10", "10 фрагів"), ("15", "15 фрагів")], "auto"),
            new GameOption("mode", "Грають", [("ffa", "кожен сам"), ("teams", "🏺 команди: бережи глек (2, 4, 6)"), ("waves", "🤖 разом проти хвиль (2–4)")], "ffa"),
            new GameOption("map", "Мапа", [("classic", "цегла й сталь"), ("wild", "🌳 з кущами й ❄ льодом")], "classic"),
            new GameOption("revenge", "Помста", [("0", "лише слава"), ("1", "😈 +1 фраг за помсту")], "0"),
        ],
        Hint: "Танчики згори: їдеш, стріляєш, ламаєш цеглу. Хто перший набере фрагів — той і взяв. Тримай 💥 — стріляє сам. Є команди з глеком і кооп проти хвиль 🤖");

    TanksCore? _core;
    string _phase = PhaseStart;
    int _startIn = StartTicks;
    /// <summary>Скільки фрагів до перемоги; 0 — «за столом», рахується на «Почати» зі складу.</summary>
    int _frags;
    int _need = FragsFor(2);
    bool _started;
    string _mode = "ffa";
    bool _wild, _revengeFrag;
    /// <summary>Команда кожного місця, коли команди справді вийшли (парний стіл); null — кожен сам.</summary>
    int[]? _teams;
    /// <summary>Кооп проти хвиль 🤖 цієї партії.</summary>
    bool _coop;
    /// <summary>Чому обраного режиму не вийшло — показуємо на столі.</summary>
    string? _note;
    int _limit = TanksCore.MatchTicks;
    /// <summary>Хто грав цю партію від початку — для підсумку (утікач теж у ньому).</summary>
    readonly bool[] _roster = new bool[TanksCore.Seats];
    object[]? _sum;
    /// <summary>Як скінчилось: «base» — розбили глек, «waves» — відбили хвилі, «time» — час.</summary>
    string? _end;
    readonly List<(int Id, int At, TankEvent Ev)> _feed = [];
    int _evId;

    /// <summary>
    /// Поле готове ще до старту (рамка й танки по стартах) — стіл у лобі виглядає як поле, а не як порожнеча.
    /// До старту воно звичне; скільки людей сіло, відомо лише на «Почати» — тоді й вирішується розмір.
    /// </summary>
    TanksCore Core
    {
        get
        {
            if (_core is not null) return _core;
            _core = new TanksCore(Ctx.Rng);
            _core.Layout();
            return _core;
        }
    }

    /// <summary>Нова мапа під склад: на п'ятьох-шістьох — велика. Намір-напрямок кожного переживає заміну.</summary>
    void Rebuild(bool[] plays)
    {
        var (w, h) = TanksCore.SizeFor(plays.Count(p => p));
        if (_core is null || _core.W != w || _core.H != h)
        {
            var fresh = new TanksCore(Ctx.Rng, w, h);
            if (_core is not null)
                for (var i = 0; i < TanksCore.Seats; i++) fresh.Tanks[i].Want = _core.Tanks[i].Want;
            _core = fresh;
        }
        _core.Wild = _wild;
        _core.RevengeFrag = _revengeFrag;
        _core.SetSides(_teams, bases: _teams is not null, waves: _coop);
        _core.Reset(plays);
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

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _frags = options.TryGetValue("frags", out var v) && int.TryParse(v, out var n) && n is 5 or 10 or 15 ? n : 0;
        _need = _frags > 0 ? _frags : FragsFor(2);
        _mode = options.GetValueOrDefault("mode") is "teams" or "waves" ? options["mode"] : "ffa";
        _wild = options.GetValueOrDefault("map") == "wild";
        _revengeFrag = options.GetValueOrDefault("revenge") == "1";
    }

    public override void Start()
    {
        _started = true;
        _phase = PhaseStart;
        _startIn = StartTicks;
        _sum = null;
        _end = null;
        _feed.Clear();
        var plays = Enumerable.Range(0, TanksCore.Seats).Select(Ctx.Seated).ToArray();
        for (var i = 0; i < TanksCore.Seats; i++) _roster[i] = plays[i];
        SetupSides(plays);
        _need = _frags > 0 ? _frags : FragsFor(plays.Count(p => p));
        _limit = _coop ? WavesMatchTicks : _teams is not null ? TeamMatchTicks : TanksCore.MatchTicks;
        Rebuild(plays);
    }

    /// <summary>
    /// Команди — на двох, чотирьох чи шістьох: місця за столом по черзі (перше, третє, п'яте — 🥒 ліворуч,
    /// друге, четверте, шосте — 🍅 праворуч). Хвилі — для двох-чотирьох: усі разом ліворуч, біля одного глека.
    /// </summary>
    void SetupSides(bool[] plays)
    {
        _teams = null;
        _coop = false;
        _note = null;
        var seated = Enumerable.Range(0, TanksCore.Seats).Where(s => plays[s]).ToArray();
        if (_mode == "teams")
        {
            if (seated.Length is 2 or 4 or 6)
            {
                _teams = [.. Enumerable.Repeat(-1, TanksCore.Seats)];
                for (var i = 0; i < seated.Length; i++) _teams[seated[i]] = i % 2;
            }
            else _note = "Команд не буде: треба двоє, четверо або шестеро — граємо кожен сам";
        }
        else if (_mode == "waves")
        {
            if (seated.Length <= 4)
            {
                _coop = true;
                _teams = [.. Enumerable.Repeat(-1, TanksCore.Seats)];
                foreach (var s in seated) _teams[s] = 0;
            }
            else _note = "Хвилі — для двох-чотирьох, а вас більше: граємо кожен сам";
        }
    }

    // ---------- ввід ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (!_started) return ActResult.Fail("Партія ще не почалась");
        if (seat < 0 || seat >= TanksCore.Seats) return ActResult.Fail("Ти тут не граєш");
        if (_phase == PhaseOver) return ActResult.Fail("Партію вже зіграно");

        switch (action)
        {
            case "move":
                // Намір, а не хід: стрілку тиснуть ще на відліку, і танк рушає з першого тика.
                var dir = Dir(payload);
                if (dir is null or < -1 or > 3) return ActResult.Fail("Такого напрямку нема");
                Core.Turn(seat, dir.Value);
                return ActResult.Done;
            case "fire":
                // {on:true} — затиснув «💥» (стріляє сам, щойно можна), {on:false} — відпустив, без on — один натиск.
                var hold = Hold(payload);
                var me = Core.Tanks[seat];
                if (hold == false) { Core.Hold(seat, false); return ActResult.Done; }
                if (_phase != PhaseGo || !me.Alive)
                {
                    if (hold == true) me.Hold = true;    // затиснув на відліку чи поки підбитий — бахне, щойно зможе
                    return ActResult.Fail(_phase != PhaseGo ? "Мить — зараз почнемо" : "Тебе підбили — мить, і знову в бій");
                }
                if (hold == true) Core.Hold(seat, true);
                else Core.Press(seat);                   // зарано — натиск чекає ~150 мс і стріляє сам
                return ActResult.Done;
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    /// <summary>Напрямок приймаємо і як <c>{dir:1}</c>, і як голе число.</summary>
    static int? Dir(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetInt32(out var n) => n,
        JsonValueKind.Object when payload.TryGetProperty("dir", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n) => n,
        _ => null,
    };

    /// <summary><c>{on:true|1}</c> — тримає, <c>{on:false|0}</c> — відпустив, інакше — просто натиск (null).</summary>
    static bool? Hold(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("on", out var on)) return null;
        return on.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when on.TryGetInt32(out var n) => n != 0,
            _ => null,
        };
    }

    // ---------- тик ----------

    public override TickResult Tick()
    {
        switch (_phase)
        {
            case PhaseOver:
                return TickResult.None;
            case PhaseStart:
                if (--_startIn > 0) return TickResult.FrameOnly;
                _phase = PhaseGo;
                return TickResult.FrameOnly;
            default:
                Core.Step();
                Drain();
                if (_coop)
                {
                    if (!Core.BaseUp[0]) return OverCoop(false);
                    if (Core.Won || Core.Ticks >= _limit) return OverCoop(true);
                    return TickResult.FrameOnly;
                }
                if (_teams is not null)
                {
                    for (var t = 0; t < 2; t++)
                        if (!Core.BaseUp[t]) { _end = "base"; return Over(TeamSeats(1 - t)); }
                    if (Core.Ticks >= _limit) { _end = "time"; return Over(TeamLeaders()); }
                    return TickResult.FrameOnly;
                }
                var top = Playing().Where(s => Core.Tanks[s].Frags >= _need).ToArray();
                if (top.Length > 0) return Over(top);
                if (Core.Ticks >= _limit) { _end = "time"; return Over(Leaders()); }
                return TickResult.FrameOnly;
        }
    }

    /// <summary>Події тика — у стрічку (три секунди в кадрі); п'ять і вісім поспіль Глек каже й у балачку столу.</summary>
    void Drain()
    {
        var events = Core.Events;
        if (events.Count == 0) return;
        foreach (var ev in events)
        {
            _feed.Add((++_evId, Core.Ticks, ev));
            if (ev.How is TankHow.Kill or TankHow.Revenge && ev.A < TanksCore.Seats && ev.N is 5 or 8)
                Ctx.Say(ev.N == 5
                    ? $"🔥 {Ctx.NickOf(ev.A)}: п'ять поспіль! Хто-небудь, зупиніть цей танк"
                    : $"🔥🔥 {Ctx.NickOf(ev.A)}: вісім поспіль — це вже не танк, це стихійне лихо");
        }
        events.Clear();
        if (_feed.Count > 8) _feed.RemoveRange(0, _feed.Count - 8);
    }

    int[] Playing() => [.. Enumerable.Range(0, TanksCore.Seats).Where(s => Ctx.Seated(s) && Core.Tanks[s].Plays)];

    int[] TeamSeats(int team) => [.. Playing().Where(s => _teams![s] == team)];

    int TeamFrags(int team) => Enumerable.Range(0, TanksCore.Seats).Where(s => _teams![s] == team).Sum(s => Core.Tanks[s].Frags);

    /// <summary>Хто попереду за фрагами; порожньо — нічия (попереду всі одразу).</summary>
    int[] Leaders()
    {
        var playing = Playing();
        if (playing.Length == 0) return [];
        var best = playing.Max(s => Core.Tanks[s].Frags);
        var leaders = playing.Where(s => Core.Tanks[s].Frags == best).ToArray();
        return leaders.Length == playing.Length ? [] : leaders;
    }

    int[] TeamLeaders()
    {
        var (a, b) = (TeamFrags(0), TeamFrags(1));
        return a == b ? [] : TeamSeats(a > b ? 0 : 1);
    }

    /// <summary>Кінець партії: рядок Журналу — фраги, переможець першим; фраги кожного — у таблицю.</summary>
    TickResult Over(int[] winners)
    {
        _phase = PhaseOver;
        winners = [.. winners.Where(Ctx.Seated)];
        _sum = Summary();
        string score;
        if (_teams is not null)
        {
            var first = winners.Length > 0 ? _teams[winners[0]] : 0;
            score = string.Join(" : ", new[] { first, 1 - first }.Select(t =>
                $"{TeamNames[t]} ({string.Join(", ", TeamSeats(t).Select(Ctx.NickOf))}) {TeamFrags(t)}"));
            if (_end == "base") score += " — глек розбито";
        }
        else
        {
            var rest = Enumerable.Range(0, TanksCore.Seats).Where(s => Ctx.Seated(s) && !winners.Contains(s));
            score = string.Join(" : ", winners.Concat(rest).Select(s => $"{Ctx.NickOf(s)} {Core.Tanks[s].Frags}"));
        }
        foreach (var s in Playing()) Ctx.Score(s, Core.Tanks[s].Frags);
        Ctx.Finish(winners, winners.Length > 0 ? $"{Info.Title}: {score}" : $"{Info.Title}: {score} — нічия");
        return TickResult.Both;
    }

    /// <summary>
    /// Кінець коопу. Проти 🤖 не платимо й у таблицю не пишемо: переможців нема (ставки — повернення), а як
    /// скінчилось — у рядку Журналу й на мапі.
    /// </summary>
    TickResult OverCoop(bool held)
    {
        _phase = PhaseOver;
        _end = held ? "waves" : "base";
        _sum = Summary();
        var who = string.Join(", ", Playing().Select(s => $"{Ctx.NickOf(s)} {Core.Tanks[s].Frags}"));
        Ctx.Finish([], held
            ? $"{Info.Title} 🤖: відбили {Core.Wave} {Waves(Core.Wave)} — глек цілий ({who})"
            : $"{Info.Title} 🤖: глек розбили на {Core.Wave}-й хвилі ({who})",
            verdict: held ? $"🏆 Глек вистояв: відбили {Core.Wave} {Waves(Core.Wave)}" : $"💔 Глек розбили на {Core.Wave}-й хвилі");
        return TickResult.Both;
    }

    static string Waves(int n) => n % 10 == 1 && n % 100 != 11 ? "хвилю" : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? "хвилі" : "хвиль";

    /// <summary>
    /// Підсумок партії — рядок на кожного, хто грав: <c>[місце, фраги, смерті, пострілів, влучань, найдовша серія,
    /// хто його найчастіше підбивав (-1 — ніхто), скільки разів, помст]</c>.
    /// </summary>
    object[] Summary()
    {
        var rows = new List<object>();
        for (var s = 0; s < TanksCore.Seats; s++)
        {
            if (!_roster[s]) continue;
            var t = Core.Tanks[s];
            var (nem, most) = (-1, 0);
            for (var k = 0; k < TanksCore.Seats; k++)
                if (k != s && Core.KillsBy[k, s] > most) (nem, most) = (k, Core.KillsBy[k, s]);
            rows.Add(new[] { s, t.Frags, t.Deaths, t.Shots, t.Hits, t.BestStreak, nem, most, t.Revenges });
        }
        return [.. rows];
    }

    /// <summary>Хтось устав: танк утікача зникає, партія триває; лишився один (одна команда) — партія його. Кооп грає й самотою.</summary>
    public override void OnLeave(int seat)
    {
        if (_started) Core.Drop(seat);
        var left = Enumerable.Range(0, TanksCore.Seats).Where(s => s != seat && Ctx.Seated(s)).ToArray();
        if (_phase == PhaseOver) return;
        if (_coop && _started && left.Length > 0) return;
        var oneTeam = _teams is not null && !_coop && left.Length > 0 && left.All(s => _teams[s] == _teams[left[0]]);
        if (left.Length > 1 && !oneTeam) return;
        _phase = PhaseOver;
        _sum = _started ? Summary() : null;
        Ctx.Finish(_coop ? [] : left, $"{Info.Title}: {Ctx.NickOf(seat)} встає з-за столу, партію не дограли");
    }

    // ---------- вид і кадр ----------

    /// <summary>
    /// Кадр на кожен тик. Нове з проходу №3 — лише коли є що сказати: <c>ev</c> (свіжі події <c>[id, як, хто, кого,
    /// серія]</c>), <c>e</c> (ворожі 🤖 <c>[слот, x, y, дуло, щит]</c>), <c>wv</c> (<c>[хвиля, скільки 🤖 лишилось]</c>).
    /// </summary>
    public override object? Frame()
    {
        var f = new Dictionary<string, object>
        {
            ["t"] = Core.Ticks,
            ["p"] = Men(),
            ["s"] = Shots(),
            ["pw"] = Loot(),
            ["bricks"] = Core.BrickCells(),
            ["phase"] = _phase,
            ["startIn"] = _startIn,
            ["left"] = Math.Max(0, _limit - Core.Ticks),
        };
        var ev = Feed();
        if (ev is not null) f["ev"] = ev;
        if (_coop)
        {
            f["e"] = Bots();
            f["wv"] = new[] { Core.Wave, Core.WaveLeft + Core.BotsAlive() };
        }
        return f;
    }

    public override object View(int? seat)
    {
        var v = (Dictionary<string, object>)Frame()!;
        v["width"] = Core.W;
        v["height"] = Core.H;
        v["sub"] = TanksCore.Sub;
        v["turn"] = null!;
        v["need"] = _teams is not null ? 0 : _need;          // у командах і хвилях фрагами не виграють
        v["walls"] = Core.SteelCells();                      // сталь не міняється за партію (крім 💥) — клієнт малює її раз
        if (_wild)
        {
            v["bush"] = Core.BushCells();
            v["ice"] = Core.IceCells();
        }
        if (_teams is not null) v["teams"] = (int[])_teams.Clone();
        var bases = new List<int[]>();
        for (var t = 0; t < 2; t++)
            if (Core.BaseCell[t] >= 0) bases.Add([Core.BaseCell[t], t, Core.BaseUp[t] ? 1 : 0]);
        if (bases.Count > 0) v["bases"] = bases.ToArray();
        if (_coop) v["coop"] = TanksCore.WaveCount;
        if (_revengeFrag) v["rv"] = 1;
        if (_note is not null) v["note"] = _note;
        if (_end is not null) v["end"] = _end;
        if (_sum is not null) v["sum"] = _sum;
        return v;
    }

    int[][]? Feed()
    {
        List<int[]>? list = null;
        foreach (var (id, at, ev) in _feed)
            if (Core.Ticks - at < EventTicks) (list ??= []).Add([id, (int)ev.How, ev.A, ev.B, ev.N]);
        return list is null ? null : [.. list];
    }

    int[][] Bots()
    {
        var n = Core.BotsAlive();
        var list = new int[n][];
        n = 0;
        for (var i = TanksCore.Seats; i < TanksCore.All; i++)
        {
            var t = Core.Tanks[i];
            if (t.Alive) list[n++] = [i, Core.PosX(t), Core.PosY(t), t.Dir, t.Shield > 0 ? 1 : 0];
        }
        return list;
    }

    object[] Shots() => [.. Core.Shells.Select(s => (object)new { i = s.Id, x = s.X, y = s.Y, d = s.Dir, big = s.Pierce })];

    object[] Loot() => [.. Core.Drops.Select(d => (object)new { x = Core.X(d.Cell), y = Core.Y(d.Cell), kind = Kind(d.Kind) })];

    static string Kind(TankBonus kind) => kind switch
    {
        TankBonus.Speed => "speed",
        TankBonus.Twin => "twin",
        TankBonus.Rapid => "rapid",
        TankBonus.Shield => "shield",
        TankBonus.Bounce => "bounce",
        _ => "pierce",
    };

    /// <summary>Танки по місцях; до старту «живий» = «за місцем хтось сидить», щоб лобі показувало, кого чекати.</summary>
    object[] Men()
    {
        var list = new object[TanksCore.Seats];
        for (var i = 0; i < list.Length; i++)
        {
            var t = Core.Tanks[i];
            list[i] = new
            {
                x = Core.PosX(t),
                y = Core.PosY(t),
                d = t.Dir,
                alive = _started ? t.Alive : Ctx.Seated(i),
                shield = t.Shield,
                frags = t.Frags,
                reload = t.Reload,
                back = t.Respawn,
                perks = (t.Fast ? "s" : "") + (t.Twin ? "t" : "") + (t.Rapid ? "r" : "") + (t.Pierce ? "p" : "") + (t.Bounce ? "b" : ""),
            };
        }
        return list;
    }
}
