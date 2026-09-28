using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Перестрілка — дуель «кожен проти кожного» на трьох-чотирьох. Та сама вулиця, те саме «Готуйсь…
/// Цілься… ВОГОНЬ!», але тепер спершу треба вирішити, в кого цілишся (і всі бачать, хто в кого), а на
/// «ВОГОНЬ!» кожен має один патрон. Постріли сервер розбирає в тому порядку, в якому вони долетіли:
/// хто перший — той і влучив, а вбитий уже не вистрілить. Ціль уже лежить — куля в молоко. Раунд бере
/// той, хто лишився на ногах сам; дуель — хто перший узяв три раунди.
/// <para>
/// Окрема гра, а не режим <see cref="Duel"/>: дуель на двох має рейтинг Ело, ставки й ачівки, а платформа
/// знає їх лише для ігор рівно на двох. Так двоє грають як грали, а компанія — тут.
/// </para>
/// </summary>
public sealed class Shootout : Game
{
    public const int Seats = 4;
    /// <summary>До трьох виграних раундів.</summary>
    public const int WinsNeeded = 3;
    /// <summary>Пауза між раундами трохи довша, ніж у дуелі: тут є що роздивитись — хто кого й за скільки.</summary>
    public const int ResultMs = 3200;
    /// <summary>Режим «очки» (п. 173, типовий): +1 за влучання, +1 найшвидшому влучанню раунду, партія до семи.</summary>
    public const int PointsNeeded = 7;

    public override GameInfo Info { get; } = new(
        "shootout", "Перестрілка", "перестрілку", GameGroup.Live, 3, Seats, TickMs: Duel.TickMs,
        Start: StartMode.ByHost, Score: ScoreOrder.LowerIsBetter, Client: "duel",
        Options:
        [
            new GameOption("score", "Рахунок",
                [("points", "очки: +1 за влучання, +1 найшвидшому, до 7"), ("rounds", "раунди: останній на ногах, до 3")], "points"),
            DuelKit.BaitOption, DuelKit.SignalOption,
        ],
        Hint: "Троє-четверо на одній вулиці. Поки «Цілься…» — обери, в кого цілишся. На ВОГОНЬ у кожного один патрон: хто перший, той влучив. +1 за влучання, +1 найшвидшому — до семи очок");

    DuelPhase _phase = DuelPhase.Ready;
    int _round = 1;
    readonly int[] _wins = new int[Seats];
    int _idle;
    /// <summary>Очки (п. 173) чи «останній на ногах бере раунд» (як було до 29.09).</summary>
    bool _points = true;
    int Target => _points ? PointsNeeded : WinsNeeded;
    DuelKit? _kit;
    DuelKit Kit => _kit ??= new DuelKit(this, Seats);
    static readonly int[] All = [0, 1, 2, 3];
    string _sig = "word";
    DateTimeOffset? _baitAt;
    string? _baitWord;
    bool _baitShown;
    DateTimeOffset _baitShownAt;
    int _baitN;
    readonly bool[] _baited = new bool[Seats];
    readonly int[] _gain = new int[Seats];
    int? _fast;
    string? _lastBait;
    readonly string?[] _nr = new string?[Seats];

    /// <summary>Хто грає цей раунд (сидів на його старті й не встав).</summary>
    readonly bool[] _plays = new bool[Seats];
    readonly bool[] _alive = new bool[Seats];
    /// <summary>У кого цілиться кожен; null — нема в кого (сам лишився або не грає).</summary>
    readonly int?[] _aim = new int?[Seats];
    /// <summary>Коли вистрілив після «ВОГОНЬ!» (мс реакції); null — ще не стріляв.</summary>
    readonly long?[] _shot = new long?[Seats];
    /// <summary>У кого влучив (null — промах або не стріляв).</summary>
    readonly int?[] _hit = new int?[Seats];
    /// <summary>Поспішив — куля в небо, до кінця раунду без патрона (але мішенню лишається).</summary>
    readonly bool[] _false = new bool[Seats];
    readonly int?[] _killedBy = new int?[Seats];

    DateTimeOffset _aimAt, _fireAt, _deadline;
    /// <summary>Щось змінилось між тиками (ціль, постріл, фальстарт) — наступний тик шле кадр.</summary>
    bool _dirty;

    bool _hasLast;
    int? _lastWinner;
    string? _lastReason;

    static readonly string[] Names = ["шериф", "бандит", "шулер", "гробар"];
    public override string SeatName(int seat) => seat is >= 0 and < Seats ? Names[seat] : base.SeatName(seat);

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _points = !(options.TryGetValue("score", out var sc) && sc == "rounds");
        // Пінг тут не поправляємо: постріли розбираються в порядку приходу (вбитий уже не стріляє), і відкласти
        // розбір на стелю поправки — означало б, що вбитий ще «встигає» вистрілити. Див. звіт проходу №3.
        Kit.Configure(options, pingAllowed: false);
    }

    public override void Start()
    {
        Array.Clear(_wins);
        Kit.Reset();
        Array.Clear(_aim);
        _round = 1;
        _idle = 0;
        _hasLast = false;
        _lastWinner = null;
        _lastReason = null;
        _lastBait = null;
        Array.Clear(_gain);
        Array.Clear(_nr);
        _fast = null;
        NewRound();
    }

    void NewRound()
    {
        var now = Ctx.Clock.UtcNow;
        for (var s = 0; s < Seats; s++)
        {
            _plays[s] = Ctx.Seated(s);
            _alive[s] = _plays[s];
            _shot[s] = null;
            _hit[s] = null;
            _false[s] = false;
            _killedBy[s] = null;
            _baited[s] = false;
        }
        // Ціль із минулого раунду лишається, якщо та людина ще за столом: переобирати щоразу — морока.
        for (var s = 0; s < Seats; s++)
            _aim[s] = _plays[s] ? (_aim[s] is { } t && t != s && _plays[t] ? t : NextTarget(s, 1)) : null;
        _phase = DuelPhase.Ready;
        _aimAt = now.AddMilliseconds(Duel.ReadyMs);
        var aim = Duel.AimMs(Ctx.Rng);
        _fireAt = _aimAt.AddMilliseconds(aim);
        _deadline = _fireAt;
        _dirty = false;
        // Обманки й сигнали — як у дуелі (DuelBout); генератор чіпаємо лише з увімкненою опцією.
        _baitAt = null;
        _baitWord = null;
        _baitShown = false;
        _sig = Kit.MixSignals ? DuelBout.Signals[Ctx.Rng.Next(DuelBout.Signals.Length)] : "word";
        if (Kit.Decoys && aim >= DuelBout.BaitMinAimMs && Ctx.Rng.Next(100) < 65)
        {
            _baitAt = _aimAt.AddMilliseconds(DuelBout.BaitAfterAimMs + Ctx.Rng.Next(aim - DuelBout.BaitAfterAimMs - DuelBout.BaitBeforeFireMs));
            _baitWord = DuelBout.Baits[Ctx.Rng.Next(DuelBout.Baits.Length)];
        }
    }

    /// <summary>Наступна жива ціль від <paramref name="seat"/> по колу в бік <paramref name="step"/> (±1).</summary>
    int? NextTarget(int seat, int step, int? from = null)
    {
        var start = from ?? seat;
        for (var i = 1; i <= Seats; i++)
        {
            var t = ((start + step * i) % Seats + Seats) % Seats;
            if (t != seat && _alive[t]) return t;
        }
        return null;
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (seat is < 0 or >= Seats) return ActResult.Fail("Ти тут не граєш");
        if (_phase is DuelPhase.Result) return ActResult.Fail("Раунд уже скінчився — мить, і буде наступний");
        if (_phase is DuelPhase.Done) return ActResult.Fail("Перестрілку зіграно, тисни «Ану ще раз»");
        if (!_plays[seat]) return ActResult.Fail("Ти в цьому раунді не граєш");
        if (!_alive[seat]) return ActResult.Fail("Ти вже лежиш у пилюці — чекай наступного раунду");

        switch (action)
        {
            case "pong":
                return ActResult.Done;
            case "aim":
                return Aim(seat, payload);
            case "shoot":
                if (_phase == DuelPhase.Fire) Shoot(seat, Ctx.Clock.UtcNow);
                else FalseStart(seat);
                return ActResult.Done;
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    /// <summary>
    /// Ціль: <c>{ at: місце }</c> — прямо в когось, або <c>{ step: ±1 }</c> — наступний по колу (стрілки,
    /// джойстик). Цілитись можна й під час «ВОГОНЬ!» — але поки переводиш ствол, хтось уже стріляє.
    /// </summary>
    ActResult Aim(int seat, JsonElement payload)
    {
        int? want = null;
        if (payload.ValueKind == JsonValueKind.Object)
        {
            if (payload.TryGetProperty("at", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt32(out var a)) want = a;
            else if (payload.TryGetProperty("step", out var st) && st.ValueKind == JsonValueKind.Number && st.TryGetInt32(out var d))
                want = NextTarget(seat, d < 0 ? -1 : 1, _aim[seat]);
        }
        else if (payload.ValueKind == JsonValueKind.Number && payload.TryGetInt32(out var n)) want = n;
        if (want is not { } t || t is < 0 or >= Seats || t == seat) return ActResult.Fail("У себе не цілься");
        if (!_alive[t]) return ActResult.Fail("Там уже ні душі");
        if (_aim[seat] != t) { _aim[seat] = t; _dirty = true; }
        return ActResult.Done;
    }

    /// <summary>
    /// Постріл після «ВОГОНЬ!». Розбираємо одразу, в порядку надходження: влучив у живого — той падає й
    /// свого пострілу вже не зробить. Ціль уже лежить — промах. Патрон один.
    /// </summary>
    void Shoot(int seat, DateTimeOffset now)
    {
        if (now >= _deadline || _false[seat] || _shot[seat] is not null) return;
        _shot[seat] = (long)Math.Max(1, Math.Round((now - _fireAt).TotalMilliseconds));
        if (_aim[seat] is { } t && _alive[t])
        {
            _alive[t] = false;
            _killedBy[t] = seat;
            _hit[seat] = t;
        }
        _dirty = true;
    }

    void FalseStart(int seat)
    {
        if (_false[seat]) return;
        _false[seat] = true;
        if (_baitShown && (Ctx.Clock.UtcNow - _baitShownAt).TotalMilliseconds <= DuelBout.BaitBlameMs) _baited[seat] = true;
        _dirty = true;
    }

    /// <summary>Раунд вирішено: на ногах лишився один (або ніхто), чи всі, хто ще може стріляти, вже стріляли.</summary>
    bool Settled()
    {
        var alive = 0;
        var armed = 0;
        for (var s = 0; s < Seats; s++)
        {
            if (!_alive[s]) continue;
            alive++;
            if (!_false[s] && _shot[s] is null) armed++;
        }
        return alive <= 1 || armed == 0;
    }

    public override TickResult Tick()
    {
        if (_phase == DuelPhase.Done) return TickResult.None;
        var now = Ctx.Clock.UtcNow;

        if (_phase == DuelPhase.Result)
            return now >= _deadline ? Next() : TickResult.None;

        // Усі живі поспішили ще до «ВОГОНЬ!» — стріляти нікому, раунд порожній.
        if (_phase is DuelPhase.Ready or DuelPhase.Aim && Settled()) return Announce(now);

        if (_phase == DuelPhase.Ready && now >= _aimAt)
        {
            _phase = DuelPhase.Aim;
            _dirty = false;
            return TickResult.FrameOnly;
        }
        if (_phase == DuelPhase.Aim && now >= _fireAt)
        {
            // Як і в дуелі: реакцію міряємо від тика, в якому кадр «ВОГОНЬ!» справді пішов у браузери.
            _phase = DuelPhase.Fire;
            _fireAt = now;
            _deadline = now.AddMilliseconds(Duel.FireWindowMs);
            _dirty = false;
            return TickResult.FrameOnly;
        }
        if (_phase == DuelPhase.Aim && _baitAt is { } ba && !_baitShown && now >= ba)
        {
            _baitShown = true;
            _baitShownAt = now;
            _baitN++;
            _dirty = false;
            return TickResult.FrameOnly;
        }
        if (_phase == DuelPhase.Fire && (Settled() || now >= _deadline)) return Announce(now);
        if (_dirty)
        {
            _dirty = false;
            return TickResult.FrameOnly;
        }
        return TickResult.None;
    }

    TickResult Announce(DateTimeOffset now)
    {
        var standing = Enumerable.Range(0, Seats).Where(s => _alive[s]).ToArray();
        var anyShot = Enumerable.Range(0, Seats).Any(s => _shot[s] is not null);
        var anyFalse = Enumerable.Range(0, Seats).Any(s => _false[s]);
        _lastWinner = null;
        Array.Clear(_gain);
        _fast = null;
        if (_points)
        {
            // +1 за кожне влучання, ще +1 найшвидшому влучанню раунду. Лежати не штраф: наступного раунду знову в грі.
            for (var s = 0; s < Seats; s++)
            {
                if (_hit[s] is null || _shot[s] is not { } ms) continue;
                _gain[s]++;
                if (_fast is not { } f || ms < _shot[f]!.Value) _fast = s;
            }
            if (_fast is { } fast) _gain[fast]++;
            for (var s = 0; s < Seats; s++) _wins[s] += _gain[s];
            _lastReason = anyShot || anyFalse ? "points" : "sleep";
        }
        else if (standing.Length == 1 && anyShot)
        {
            _lastWinner = standing[0];
            _wins[standing[0]]++;
            _lastReason = "last";
        }
        else _lastReason = anyShot || anyFalse ? "many" : "sleep";

        for (var s = 0; s < Seats; s++)
        {
            _nr[s] = null;
            if (_shot[s] is not { } ms) continue;
            Kit.Count(s, ms);
            if (ms >= Duel.HumanFloorMs) _nr[s] = Kit.Scored(s, ms);
        }
        var baited = false;
        for (var s = 0; s < Seats; s++) baited |= _baited[s];
        _lastBait = baited ? _baitWord : null;
        _idle = _lastReason == "sleep" ? _idle + 1 : 0;
        _hasLast = true;
        _phase = DuelPhase.Result;
        _deadline = now.AddMilliseconds(ResultMs);
        _dirty = false;
        return TickResult.Both;
    }

    TickResult Next()
    {
        var seated = Enumerable.Range(0, Seats).Where(Ctx.Seated).ToArray();
        var top = seated.Length == 0 ? 0 : seated.Max(s => _wins[s]);
        // Хто перший дотягнув до мети — але лише коли він один нагорі (двоє по сім — ще раунд, до різниці).
        if (top >= Target && seated.Count(s => _wins[s] == top) == 1)
        {
            var won = seated.First(s => _wins[s] == top);
            _phase = DuelPhase.Done;
            Ctx.Finish([won], $"{Info.Title}: {Board(won)}");
            return TickResult.Both;
        }
        if (_idle >= Duel.IdleRounds)
        {
            _phase = DuelPhase.Done;
            Ctx.Finish([], $"{Info.Title}: {string.Join(", ", seated.Select(s => Ctx.NickOf(s)))} так і не вистрілили — перестрілка не відбулась");
            return TickResult.Both;
        }
        if (_lastReason != "sleep") _round++;
        NewRound();
        return TickResult.Both;
    }

    /// <summary>«Оля 3 · Петро 1 · Ігор 0» — переможець першим, далі за рахунком.</summary>
    string Board(int first)
    {
        var rest = Enumerable.Range(0, Seats).Where(s => s != first && Ctx.Seated(s)).OrderByDescending(s => _wins[s]);
        return string.Join(" · ", new[] { first }.Concat(rest).Select(s => $"{Ctx.NickOf(s)} {_wins[s]}"));
    }

    /// <summary>
    /// Хтось устав. Решту перестрілки не ламаємо: той, хто пішов, зникає з вулиці, а хто цілився в нього —
    /// переводить ствол на наступного. Лишився за столом один — партія його.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (seat is < 0 or >= Seats) return;
        var others = Enumerable.Range(0, Seats).Where(s => s != seat && Ctx.Seated(s)).ToArray();
        if (others.Length <= 1)
        {
            _phase = DuelPhase.Done;
            Ctx.Finish(others, $"{Info.Title}: {Ctx.NickOf(seat)} встає з-за столу, партію не дограли");
            return;
        }
        _plays[seat] = false;
        _alive[seat] = false;
        _aim[seat] = null;
        _wins[seat] = 0;
        for (var s = 0; s < Seats; s++)
            if (_aim[s] == seat) _aim[s] = NextTarget(s, 1, seat);
        _dirty = true;
        Ctx.Log($"{Info.Title}: {Ctx.NickOf(seat)} іде з вулиці — решта стріляється далі");
    }

    public override object? Frame() => Shot(false);

    public override object View(int? seat) => Shot(true);

    object Shot(bool full)
    {
        var o = new Dictionary<string, object?>
        {
            ["phase"] = Wire(_phase),
            ["round"] = _round,
            ["wins"] = (int[])_wins.Clone(),
            ["plays"] = (bool[])_plays.Clone(),
            ["alive"] = (bool[])_alive.Clone(),
            ["aim"] = (int?[])_aim.Clone(),
            ["shot"] = (long?[])_shot.Clone(),
            ["hit"] = (int?[])_hit.Clone(),
            ["fs"] = (bool[])_false.Clone(),
            ["last"] = _hasLast ? Last() : null,
            ["nextIn"] = NextIn(),
            ["target"] = Target,
        };
        if (_points) o["pts"] = true;
        if (Kit.MixSignals) o["sig"] = _sig;
        if (_phase == DuelPhase.Aim && _baitShown) o["decoy"] = new { w = _baitWord, n = _baitN };
        Kit.Fill(o, full, All);
        return o;
    }

    Dictionary<string, object?> Last()
    {
        var l = new Dictionary<string, object?> { ["winner"] = _lastWinner, ["reason"] = _lastReason };
        if (_points) { l["gain"] = (int[])_gain.Clone(); l["fast"] = _fast; }
        if (_lastBait is not null) l["bait"] = _lastBait;
        if (Array.Exists(_nr, x => x is not null)) l["nr"] = (string?[])_nr.Clone();
        return l;
    }

    /// <summary>Як і в дуелі: у «Цілься…» відліку нема — інакше він і є момент «ВОГОНЬ!».</summary>
    int? NextIn()
    {
        var until = _phase switch
        {
            DuelPhase.Ready => _aimAt,
            DuelPhase.Fire or DuelPhase.Result => _deadline,
            _ => (DateTimeOffset?)null,
        };
        if (until is not { } at) return null;
        return (int)Math.Max(0, Math.Round((at - Ctx.Clock.UtcNow).TotalMilliseconds));
    }

    static string Wire(DuelPhase phase) => phase switch
    {
        DuelPhase.Ready => "ready",
        DuelPhase.Aim => "aim",
        DuelPhase.Fire => "fire",
        DuelPhase.Result => "result",
        _ => "done",
    };
}
