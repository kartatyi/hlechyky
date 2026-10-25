using System.Globalization;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Шаткуй разом» (контракт _tools/shatkuy-v2.md §6, docs/games/specs/shatkuy.md): 2–8 шаткарів, кожен на своєму полі
/// (<see cref="ShatkuyField"/>) з тим самим сідом — однаковий розклад за <b>ігровим</b> часом кожного; хвилина за правилами
/// Ярмарку. Особливі діють лише на того, хто розрізав (мороз сповільнює лише своє поле). Господар тисне «Почати» —
/// спільний відлік; поле, чия хвилина скінчилась, чекає решту; наприкінці — місця, переможець (нічия — ділять).
/// Сам за столом — «🤖 + боти» до чотирьох (<see cref="SoloBot"/>), без нагород.
/// </summary>
public sealed class ShatkuyParty : Game
{
    public const int Seats = 8, SoloBots = 3;
    /// <summary>Спільний відлік від «Почати» до першої ігрової мс.</summary>
    public const int LeadMs = 3_000;
    /// <summary>Суперникам «комбо ×N!» світиться стільки справжніх мс.</summary>
    public const int ComboShowMs = 1_500;
    /// <summary>Не термінові зміни (рахунки суперників) розсилаємо не частіше, ніж раз на стільки тиків.</summary>
    public const int CalmTicks = 4;
    public const string PhLobby = "lobby";

    public override GameInfo Info { get; } = new(
        "shatkuy-party", "Шаткуй разом", "шаткуй разом", GameGroup.Live, 1, Seats, TickMs: ShatkuyGame.TickMs,
        Start: StartMode.ByHost, Options: [LiveBots.LevelFor(SoloBots)], Client: "shatkuy",
        Hint: "Хвилина ярмарку на кількох: віз один на всіх, а поле в кожного своє. Хто більше нашаткує? Мороз і ×2 — лише тобі, глек — мінус десять");

    readonly ShatkuyField?[] _f = new ShatkuyField?[Seats];
    readonly bool[] _left = new bool[Seats];
    readonly ShatkuyBot?[] _brain = new ShatkuyBot?[Seats];
    readonly SoloBot _solo = new(SoloBots);
    int[] _bots = [];
    bool _started, _over, _botGame;
    int _startPlayers, _tick, _lastSent;
    DateTimeOffset _t0;
    ulong _seed;
    int[] _winners = [];
    int[] _order = [];

    public bool Started => _started;
    public bool Over => _over;
    public ulong Seed => _seed;
    public IReadOnlyList<int> Bots => _bots;
    public ShatkuyField? FieldOf(int seat) => seat is >= 0 and < Seats ? _f[seat] : null;
    public int T => (int)Math.Clamp((Ctx.Clock.UtcNow - _t0).TotalMilliseconds, -1e9, 1e9);
    public IReadOnlyList<int> Winners => _winners;

    public override string SeatName(int seat) => "шаткар";

    public override void Configure(IReadOnlyDictionary<string, string> options) => _solo.Configure(options);

    public override bool ActsInLobby => true;

    public override string? CanStart() => _solo.CanStart(Ctx, Seats);

    int[] BotSeats() => _solo.Active(Ctx, Seats) ? [.. Enumerable.Range(0, Seats).Where(s => !Ctx.Seated(s)).Take(_solo.Count(Ctx, Seats))] : [];

    bool IsBot(int seat) => Array.IndexOf(_bots, seat) >= 0 && !Ctx.Seated(seat);

    public override string? SeatBot(int seat)
    {
        if (seat is < 0 or >= Seats || Ctx.Seated(seat)) return null;
        var bots = _started ? _bots : BotSeats();
        var i = Array.IndexOf(bots, seat);
        return i < 0 ? null : bots.Length == 1 ? LiveBots.Name : $"{LiveBots.Name} {i + 1}";
    }

    string Name(int seat) => SeatBot(seat) ?? Ctx.NickOf(seat) ?? $"місце {seat + 1}";

    public override void Start()
    {
        _bots = BotSeats();
        _botGame = _bots.Length > 0;
        _startPlayers = Enumerable.Range(0, Seats).Count(Ctx.Seated);
        _seed = (ulong)Ctx.Rng.NextInt64(1, long.MaxValue);
        _t0 = Ctx.Clock.UtcNow.AddMilliseconds(LeadMs);
        _started = true;
        _over = false;
        _winners = [];
        _order = [];
        _tick = _lastSent = 0;
        Array.Clear(_left);
        Array.Clear(_brain);
        for (var s = 0; s < Seats; s++)
        {
            var plays = Ctx.Seated(s) || Array.IndexOf(_bots, s) >= 0;
            _f[s] = plays ? new ShatkuyField(ShatkuyCore.Party) : null;
            _f[s]?.Begin(_seed);
        }
        foreach (var b in _bots) _brain[b] = new ShatkuyBot(_solo.Level, Ctx.Rng.Next());
        if (Ctx.Round == 1)
            Ctx.Say(_botGame ? "🔪 Віз один на всіх, а поле в кожного своє. Боти вже точать ножі!" : "🔪 Віз один на всіх, а поле в кожного своє. Хто більше нашаткує — той і господар ярмарку!");
    }

    // ---------- дії ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == LiveBots.Toggle)
            return _started && !_over ? ActResult.Fail("Партія вже йде") : _solo.Switch(Ctx, seat, payload, Seats);
        if (action != "cut") return ActResult.Fail("Тут так не ходять");
        if (!_started) return ActResult.Fail("Чекаємо на «Почати»");
        if (_over) return ActResult.Fail("Партію вже зіграно");
        if (seat is < 0 or >= Seats || _f[seat] is not { } f || _left[seat]) return ActResult.Fail("Ти тут не шаткуєш");
        var t = T;
        if (t < -200) return ActResult.Fail("Ще не почали");
        if (ShatkuyGame.ReadCuts(payload) is not { } cuts) return ActResult.Fail("Що різати?");
        Step(seat, f, t);
        if (f.Over || !f.TimeLeft(t)) return ActResult.Fail("Час вийшов");
        return f.CutMany(cuts, t, ShatkuyGame.MaxCuts) > 0 ? ActResult.Done : ActResult.Fail("Не влучив");
    }

    // ---------- тик ----------

    public override TickResult Tick()
    {
        if (!_started || _over) return TickResult.None;
        _tick++;
        var t = T;
        var urgent = false;
        var dirty = false;
        for (var s = 0; s < Seats; s++)
        {
            if (_f[s] is not { } f) continue;
            Step(s, f, t);
            if (!f.Over && _brain[s] is { } bot && t >= 0)
            {
                var cuts = bot.Think(f, t, f.G(t));
                if (cuts.Count > 0) f.CutMany(cuts, t, ShatkuyGame.MaxCuts);
            }
            urgent |= f.Urgent;
            dirty |= f.Dirty;
        }
        if (AllDone())
        {
            End();
            return TickResult.Both;
        }
        // термінове (новий плід в обрії, мороз, злива, кінець поля) — одразу; рахунки суперників — не частіше CalmTicks
        if (!urgent && !(dirty && _tick - _lastSent >= CalmTicks)) return TickResult.None;
        _lastSent = _tick;
        foreach (var f in _f) if (f is not null) f.Dirty = f.Urgent = false;
        return new TickResult(false, true);
    }

    /// <summary>Прожити поле місця до <paramref name="t"/>; щойно його хвилина скінчилась — ачівки (лише людська партія від двох).</summary>
    void Step(int seat, ShatkuyField f, int t)
    {
        if (f.Over) return;
        f.Advance(t);
        if (f.Over && Ctx.Seated(seat) && !_botGame && _startPlayers >= 2) ShatkuyGame.Achievements(Ctx, seat, f);
    }

    bool AllDone() => Enumerable.Range(0, Seats).All(s => _f[s] is not { } f || f.Over || _left[s]);

    /// <summary>Хто з кого: від більшого рахунку (рівні — за місцем).</summary>
    int[] Standings() => [.. Enumerable.Range(0, Seats).Where(s => _f[s] is not null).OrderByDescending(s => _f[s]!.Score).ThenBy(s => s)];

    /// <summary>Місце кожного (рівні рахунки — те саме місце: «ділять»).</summary>
    Dictionary<int, int> Places()
    {
        var order = Standings();
        var places = new Dictionary<int, int>();
        for (var i = 0; i < order.Length; i++)
            places[order[i]] = i > 0 && _f[order[i]]!.Score == _f[order[i - 1]]!.Score ? places[order[i - 1]] : i + 1;
        return places;
    }

    void End()
    {
        if (_over) return;
        _over = true;
        _order = Standings();
        var best = _order.Length == 0 ? 0 : _f[_order[0]]!.Score;
        var top = _order.Where(s => _f[s]!.Score == best).ToArray();
        // усі однаково (і їх більше одного) — нічия; кілька найкращих — ділять перемогу
        _winners = _order.Length == 0 || (_order.Length > 1 && top.Length == _order.Length) ? [] : top;
        var log = $"{Info.Title}: {string.Join(" : ", _order.Select(s => $"{Name(s)} {_f[s]!.Score}" + (_left[s] ? " (пішов)" : "")))}"
            + (_winners.Length == 0 ? " — нічия" : "");
        var scores = _order.Where(Ctx.Seated).ToDictionary(s => s, s => (long)_f[s]!.Score);
        if (_winners.Length > 0)
            Ctx.Say(_winners.Length == 1
                ? $"🔪 Господар ярмарку — {Name(_winners[0])}: {_f[_winners[0]]!.Score} {ShatkuyGame.Points(_f[_winners[0]]!.Score)}"
                : $"🔪 Ділять перемогу: {string.Join(", ", _winners.Select(Name))} — по {best}");
        if (!_botGame)
        {
            Ctx.Finish(_winners, log, scores);
            return;
        }
        var people = _winners.Where(Ctx.Seated).ToArray();
        var verdict = people.Length > 0 ? $"🏆 {Ctx.NickOf(people[0])} — перемога над {LiveBots.Of(_solo.Level)} ботами"
            : _winners.Length > 0 ? $"🤖 Ярмарок узяв {Name(_winners[0])}" : null;
        Ctx.Finish(people, log, scores, verdict);
    }

    /// <summary>Пішов посеред партії: його рахунок фіксується (поле закрито), решта шаткує. Людей не лишилось — не дограли.</summary>
    public override void OnLeave(int seat)
    {
        if (!_started || _over || seat is < 0 or >= Seats) return;
        _left[seat] = true;
        _f[seat]?.Close(ShatkuyGame.WhyLeft, T);
        var people = Enumerable.Range(0, Seats).Where(s => s != seat && Ctx.Seated(s) && _f[s] is not null).ToArray();
        if (people.Length > 0)
        {
            Ctx.Log($"{Info.Title}: {Ctx.NickOf(seat)} пішов з ярмарку з {_f[seat]?.Score ?? 0} — решта шаткує далі");
            if (AllDone()) End();
            return;
        }
        _over = true;
        _winners = [];
        Ctx.Finish([], $"{Info.Title}: {Ctx.NickOf(seat)} пішов з ярмарку, партію не дограли");
    }

    // ---------- вид ----------

    /// <summary>
    /// Вид місця: своє поле — як у соло (плоди, рахунок, серія, мороз, печія), плюс <c>party</c>: суперники (нік, рахунок,
    /// серія, множник, свіже комбо, чи дограв / пішов), чи чекаємо решту і місця наприкінці. Глядач — без плодів.
    /// </summary>
    public override object View(int? seat)
    {
        var now = Ctx.Clock.UtcNow;
        var t = _started ? T : 0;
        var mine = seat is { } s0 && s0 is >= 0 and < Seats ? _f[s0] : null;
        var myOver = mine is not null && (mine.Over || (seat is { } sl && _left[sl]));
        var phase = !_started ? PhLobby : _over || myOver ? ShatkuyGame.PhOver : ShatkuyGame.PhGo;
        var v = new Dictionary<string, object?>
        {
            ["mode"] = ShatkuyCore.Party.Id,
            ["phase"] = phase,
            ["turn"] = null,
            ["now"] = now.ToString("o", CultureInfo.InvariantCulture),
            ["t0"] = _started ? _t0.ToString("o", CultureInfo.InvariantCulture) : null,
            ["len"] = ShatkuyCore.Party.LenMs,
            ["lives"] = 0,
            ["best"] = 0,
            ["games"] = 0,
        };
        var f = mine ?? new ShatkuyField(ShatkuyCore.Party);
        foreach (var (key, value) in ShatkuyGame.FieldView(f, _started && mine is not null && !myOver, t, myOver)) v[key] = value;
        v["k"] = ShatkuyGame.Rules(ShatkuyCore.Party);
        var places = _over ? Places() : null;
        v["party"] = new
        {
            me = mine is null ? (int?)null : seat,
            wait = _started && !_over && myOver,
            over = _over,
            winners = _over ? (int[])_winners.Clone() : [],
            rivals = Enumerable.Range(0, Seats).Where(i => _f[i] is not null).Select(i =>
            {
                var r = _f[i]!;
                return new
                {
                    seat = i, nick = Name(i), bot = IsBot(i), score = r.Score, streak = r.Streak, mult = r.Mult,
                    combo = t - r.LastComboT <= ComboShowMs ? r.LastCombo : 0, done = r.Over, left = _left[i],
                    place = places?.GetValueOrDefault(i),
                };
            }).ToArray(),
            places = _over ? _order.Select(i => new { seat = i, nick = Name(i), score = _f[i]!.Score, place = places![i], left = _left[i], bot = IsBot(i) }).ToArray() : null,
        };
        v["botOffer"] = (!_started || _over) && _solo.Offer(Ctx, Seats);
        v["botWanted"] = _solo.Wanted;
        v["botFill"] = _solo.Fill;
        v["botLvl"] = _solo.LevelKey;
        v["bot"] = _started ? _bots.Where(b => !Ctx.Seated(b)).ToArray() : BotSeats();
        return v;
    }
}

/// <summary>
/// Бот «Шаткуй разом»: бачить плід, коли той пролетів свою реакцію, частину пропускає, глек зрідка чіпляє, кілька плодів
/// одного тику ріже одним змахом (комбо), по великому кавуну лупить змахами раз на 150–300 мс, гарбуз — двома ударами.
/// </summary>
public sealed class ShatkuyBot(LiveBots.Level level, int seed)
{
    readonly Random _rng = new(seed);
    /// <summary>Плід → ігрова мс, коли бот його ріже (−1 — вирішив пропустити).</summary>
    readonly Dictionary<int, int> _plan = [];
    int _w = 1_000_000, _bigAt;

    int Reaction => level switch { LiveBots.Level.Easy => 520, LiveBots.Level.Hard => 230, _ => 340 };
    double Skip => level switch { LiveBots.Level.Easy => 0.22, LiveBots.Level.Hard => 0.04, _ => 0.1 };
    double GlekSlip => level switch { LiveBots.Level.Easy => 0.12, LiveBots.Level.Hard => 0.02, _ => 0.05 };
    double Exact => level switch { LiveBots.Level.Easy => 0.05, LiveBots.Level.Hard => 0.3, _ => 0.15 };

    public List<ShatkuyCut> Think(ShatkuyField f, int t, int g)
    {
        var cuts = new List<ShatkuyCut>();
        var w = ++_w;
        foreach (var fr in f.Flying(g))
        {
            if (fr.Big)
            {
                if (g - fr.At < Reaction || t < _bigAt) continue;
                _bigAt = t + _rng.Next(150, 300);
                cuts.Add(new ShatkuyCut(fr.Id, ++_w, false));
                continue;
            }
            if (!_plan.TryGetValue(fr.Id, out var at))
            {
                var skip = fr.Glek ? _rng.NextDouble() >= GlekSlip : _rng.NextDouble() < Skip;
                at = skip ? -1 : fr.At + Reaction + _rng.Next(0, 250);
                _plan[fr.Id] = at;
            }
            if (at < 0 || g < at || g > fr.At + fr.D - 40) continue;
            if (fr.K == ShatkuyCore.Pumpkin && !f.IsCracked(fr.Id))
            {
                // перший удар — тріщина; другий — новим змахом трохи згодом
                cuts.Add(new ShatkuyCut(fr.Id, w, false));
                _plan[fr.Id] = g + _rng.Next(140, 260);
                continue;
            }
            cuts.Add(new ShatkuyCut(fr.Id, fr.K == ShatkuyCore.Pumpkin ? ++_w : w, _rng.NextDouble() < Exact));
        }
        if (_plan.Count > 400) foreach (var id in _plan.Keys.Where(id => !f.Fruits.Any(x => x.Id == id && x.Until >= g)).ToList()) _plan.Remove(id);
        return cuts;
    }
}
