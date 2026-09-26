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
                 ("yarmarok", "Ярмарок"), ("random", "Яка випаде")], "selo"),
            new GameOption("laps", "Кіл", [("3", "3 кола"), ("5", "5 кіл"), ("7", "7 кіл")], "3"),
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
    /// <summary>Новий рекорд траси цієї гонки — Глек скаже про нього раз, наприкінці.</summary>
    (string Nick, int Ms)? _record;

    /// <summary>Для тестів і перевірок: ядро поточної гонки (null — ще лобі), фаза, тики до таймауту, траса.</summary>
    public RallyCore? Core => _core;
    public int Phase => _ph;
    public int Left => _left;
    public RallyTrack Track => _track;

    public override string SeatName(int seat) => seat >= 0 && seat < SeatNames.Length ? SeatNames[seat] : $"гравець {seat + 1}";

    public override bool ActsInLobby => true;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
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
        _core.Rank();
        _solo = _players == 1;
        _ph = PhCount;
        _simAt = Ctx.Clock.UtcNow;
        Array.Clear(_ev);
        _left = 0;
        _results = null;
        _record = null;
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
        core.Tick();
        if (_ph == PhCount)
        {
            if (core.T >= RallyCore.CountTicks) _ph = PhRace;
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
        // таймаут рахуємо з наступного тика після фінішу: 500 повних тиків решті
        if (!fresh && _left > 0 && --_left == 0) return Over();
        if (core.T - RallyCore.CountTicks >= RallyCore.MaxRaceTicks) return Over();
        return view ? TickResult.Both : TickResult.FrameOnly;
    }

    /// <summary>Коло в рекорди траси; true — змінилась десятка у виді.</summary>
    bool Lap(int seat, RallyCar c)
    {
        if (_records is null || Nick(seat) is not { } nick) return false;
        var (rank, beat) = _records.Post(_track.Id, nick, c.Car, c.LastMs, Ctx.Clock.UtcNow);
        if (rank == 0) return false;
        if (rank == 1) _record = (nick, c.LastMs);
        if (beat) Ctx.Award(seat, 0, "ach:rally-record");
        if (rank <= 10) RefreshTop();
        return rank <= 10;
    }

    string? Nick(int seat) => _nicks[seat] ?? Ctx.NickOf(seat);

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
        if (first >= 0) winners = [first];
        else if (ranked.Count > 0)
        {
            // за 4 хвилини ніхто не доїхав: веде той, хто найдалі; двоє рівно — нічия
            var lead = ranked[0];
            var tie = ranked.Count > 1 && core.Passed(core.Cars[ranked[1]]) == core.Passed(core.Cars[lead])
                && core.GateDist(core.Cars[ranked[1]]) == core.GateDist(core.Cars[lead]);
            winners = tie ? [] : [lead];
        }
        else winners = [];

        if (_record is { } rec) Ctx.Say($"⏱ Новий рекорд «{_track.Title}»: {rec.Nick}, {Clock(rec.Ms, 2)}!");
        if (!_solo && first >= 0 && _players >= 3) Ctx.Award(first, 0, "ach:rally-win3");
        Ctx.Finish(_solo ? [] : winners, LogLine(ranked, first));
        return TickResult.Both;
    }

    string LogLine(List<int> ranked, int first)
    {
        var core = _core!;
        var head = $"{Info.Title} · {_track.Title}, {LapsWord(_laps)}";
        if (ranked.Count == 0) return $"{Info.Title}: всі роз'їхались";
        if (_solo)
        {
            var c = core.Cars[ranked[0]];
            var best = c.BestMs > 0 ? $", найкраще коло {Clock(c.BestMs, 2)}" : "";
            return c.Fin > 0
                ? $"{head}: {Nick(ranked[0])} наодинці з секундоміром — {Clock(c.FinishMs, 1)}{best}"
                : $"{head}: {Nick(ranked[0])} наодинці з секундоміром — 4 хвилини минули, фінішу нема{best}";
        }
        if (first < 0)
        {
            var lead = core.Cars[ranked[0]];
            return $"{Info.Title} · {_track.Title}: за 4 хвилини ніхто не доїхав — найдалі {Nick(ranked[0])}, {LapsWord(Math.Min(lead.Lap, _laps))} з {_laps}";
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
        Ctx.Log($"{Info.Title}: {nick} — сход з траси");
        var any = false;
        for (var i = 0; i < RallyCore.Seats; i++) any |= i != seat && _core.Cars[i].Present;
        if (!any)
        {
            _ph = PhOver;
            _results = [];
            Ctx.Finish([], $"{Info.Title}: всі роз'їхались");
            return;
        }
        if (AllFinished()) Over();
    }

    // ---------- вид і кадр ----------

    public override object? Frame()
    {
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
        var cars = new string?[RallyCore.Seats];
        for (var i = 0; i < RallyCore.Seats; i++)
            cars[i] = _core is null
                ? (Ctx.Seated(i) ? CarOf(i) : null)
                : _core.Cars[i].Present ? _core.Cars[i].Car : null;
        return new
        {
            turn = (int?)null,
            ph = _ph,
            laps = _laps,
            random = _trackOpt == "random",
            track = _track.Wire,
            cars,
            records = _top,
            results = _ph == PhOver ? _results : null,
            f = Frame(),
        };
    }
}

/// <summary>Кадр ралі на дроті: { t, ph, s, c: int[90], r: int[6] }.</summary>
public sealed record RallyFrame(int T, int Ph, int S, int[] C, int[] R);
