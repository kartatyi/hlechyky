using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Намисто» (docs/games/specs/namysto.md): посередині Дядько Глек плює намистинами, разок котиться жолобом до криниці;
/// три однакові впритул — розсипаються. Кампанія з дванадцяти трас (<see cref="NamystoTracks"/>) і «Намисто дня»
/// (<see cref="NamystoDaily"/>) — одна траса й однакові намистини для всіх, одна спроба.
/// <para>
/// Грає браузер: та сама цілочислова симуляція (<see cref="NamystoSim"/> ≡ web/games/namysto-sim.js) крутиться в нього
/// миттєво, а сюди летить журнал «на кроці N — постріл у точку / поміняв» і «я вже на кроці s». Сервер проганяє той
/// самий журнал на своїй копії, тож очки рахує він. Сповільнити час не вийде: журнал не може відставати від годинника
/// сервера більше ніж на <see cref="Behind"/> кроків (інакше сервер сам котить разок далі, а браузер перезбирається з
/// журналу сервера — «епоха» у виді), пауза — окремою дією, і поле на ній сховане.
/// </para>
/// </summary>
public class Namysto : Game
{
    public Namysto() : this(daily: false) { }

    protected Namysto(bool daily)
    {
        _daily = daily;
        Info = daily ? DailyInfo : CampaignInfo;
    }

    public const string PhPick = "pick", PhReady = "ready", PhGo = "go", PhOver = "over";
    /// <summary>Наскільки журнал може відстати від годинника сервера (3 с), перш ніж сервер покотить разок сам.</summary>
    public const int Behind = 180;
    /// <summary>Наскільки журнал може випередити годинник сервера (0,5 с — похибка годинника й дорога).</summary>
    public const int Ahead = 30;
    public const int MaxBatch = 48;
    /// <summary>Черепки за перше проходження кожної траси кампанії (у межах денної стелі нагород).</summary>
    public const int FirstClearShards = 3;
    /// <summary>Комбо від стількох — ачівка «Ланцюжок»; «Над самою криницею» — голова була ближче за стільки намистин.</summary>
    public const int ComboAch = 5, EdgeBeads = 3;
    public const string DoneToday = "Намисто дня вже зібрано — нова траса після півночі. А поки — «Намисто» в Соло";
    const string Bad = "Тут так не ходять";

    static readonly GameInfo CampaignInfo = new(
        "namysto", "Намисто", "намисто", GameGroup.Solo, 1, 1, TickMs: 100,
        Start: StartMode.Immediate, Private: true, Score: ScoreOrder.HigherIsBetter,
        Hint: "Дядько Глек плює намистинами: збий три однакові, поки разок не докотився до криниці. Дванадцять трас — від Хати до Купальської ночі",
        Client: "namysto");

    static readonly GameInfo DailyInfo = new(
        "namysto-daily", "Намисто дня", "намисто дня", GameGroup.Solo, 1, 1, TickMs: 100,
        Start: StartMode.Immediate, Private: true, Persistent: true,
        Hint: "Одна траса й однакові намистини для всіх на цілий день — і одна спроба. Хто сьогодні збере найбільше?",
        Client: "namysto");

    public override GameInfo Info { get; }

    readonly bool _daily;
    NamystoStore? _store;
    NamystoDailyBoard? _board;

    string _phase = PhPick;
    NamystoLevel? _lv;
    uint _seed;
    /// <summary>На яку трасу стати після «Ще раз» (кампанія): та сама, або та, яку обрали на підсумку («Далі»).</summary>
    int _next;
    NamystoSim? _sim;
    DateTimeOffset _goAt, _pausedAt;
    TimeSpan _pausedTotal;
    bool _paused, _viewDirty;
    /// <summary>Епоха журналу: росте, коли браузер мусить перезібратись із журналу сервера.</summary>
    int _epoch;
    /// <summary>Браузер уже прислав пачку поточної епохи (перезібрався) — лише тоді є сенс просити його звірятись знову.</summary>
    bool _synced = true;
    readonly List<int[]> _pending = [];
    readonly List<int[]> _journal = [];
    object? _result;

    // «Намисто дня»
    string _day = "";
    bool _started, _reported, _finishOnTick, _endOnTick;

    public string Phase => _phase;
    public NamystoSim? Sim => _sim;
    public NamystoLevel? Level => _lv;
    public uint Seed => _seed;
    public int Epoch => _epoch;
    public IReadOnlyList<int[]> Journal => _journal;
    public bool Paused => _paused;

    public override string SeatName(int seat) => "Глек";

    public override string SoloKey(string nickKey, IClock clock) =>
        _daily ? $"daily:{Info.Id}:{Days.Today(clock)}:{nickKey}" : base.SoloKey(nickKey, clock);

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _store = Ctx.Services.GetService<NamystoStore>();
        _board = Ctx.Services.GetService<NamystoDailyBoard>();
    }

    public override string? CanStart() => _daily && _reported && _day == Days.Today(Ctx.Clock) ? DoneToday : null;

    public override bool ActsBetween(string action) => !_daily && action == "next";

    public override void Start()
    {
        _sim = null;
        _paused = false;
        _pausedTotal = TimeSpan.Zero;
        _pending.Clear();
        _journal.Clear();
        _result = null;
        _viewDirty = true;
        if (_daily)
        {
            var today = Days.Today(Ctx.Clock);
            if (_day != today) { _day = today; _started = _reported = false; }
            _lv = NamystoTracks.Daily(_day);
            _seed = (uint)Math.Max(1, Days.Seed("namysto", _day));
            if (_reported)
            {
                _phase = PhOver;
                Ctx.Finish([0], "");
                return;
            }
            _phase = PhReady;
            return;
        }
        if (_next > 0)
        {
            Prepare(_next);
            _phase = PhReady;
        }
        else _phase = PhPick;
    }

    void Prepare(int n)
    {
        _lv = NamystoTracks.Level(n);
        _seed = (uint)Ctx.Rng.Next(1, int.MaxValue);
    }

    int OpenFor() => Ctx.NickOf(0) is { } nick && _store is not null ? _store.Open(nick) : 1;

    // ---------- годинник ----------

    /// <summary>Скільки кроків минуло на годиннику сервера від «котити» (без пауз).</summary>
    public int Real()
    {
        if (_phase != PhGo) return 0;
        var now = Ctx.Clock.UtcNow;
        var t = now - _goAt - _pausedTotal - (_paused ? now - _pausedAt : TimeSpan.Zero);
        return (int)Math.Max(0, t.Ticks * NamystoSim.StepsPerSecond / TimeSpan.TicksPerSecond);
    }

    // ---------- ввід ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (seat != 0) return ActResult.Fail("Ти тут не граєш");
        switch (action)
        {
            case "level":
                if (_daily) return ActResult.Fail("У «Намисті дня» траса одна на всіх");
                if (_phase is not (PhPick or PhReady)) return ActResult.Fail("Уже котиться");
                if (!TryInt(payload, "n", out var n) || n < 1 || n > NamystoTracks.Count) return ActResult.Fail(Bad);
                if (n > OpenFor()) return ActResult.Fail("Цю трасу ще не відкрито — пройди попередню");
                Prepare(n);
                _phase = PhReady;
                _viewDirty = true;
                return ActResult.Done;
            case "pick":
                if (_daily || _phase != PhReady) return ActResult.Fail(Bad);
                _phase = PhPick;
                _viewDirty = true;
                return ActResult.Done;
            case "go":
                if (_phase != PhReady || _lv is null) return ActResult.Fail(_phase == PhGo ? "Уже котиться" : Bad);
                if (_daily && _reported) return ActResult.Fail(DoneToday);
                _sim = new NamystoSim(_lv.Track, _lv.Colors, _lv.Beads, _lv.Speed, _lv.Par, _seed);
                _goAt = Ctx.Clock.UtcNow;
                _pausedTotal = TimeSpan.Zero;
                _paused = false;
                _pending.Clear();
                _journal.Clear();
                _synced = true;
                _phase = PhGo;
                if (_daily) _started = true;
                _viewDirty = true;
                return ActResult.Done;
            case "j":
                return Batch(payload);
            case "pause":
            {
                if (_phase != PhGo || _sim is null) return ActResult.Fail("Зараз нема чого ставити на паузу");
                if (_paused) return ActResult.Done;
                var r = Batch(payload);
                if (!r.Ok) return r;
                if (_phase != PhGo) return ActResult.Done;
                _paused = true;
                _pausedAt = Ctx.Clock.UtcNow;
                _viewDirty = true;
                return ActResult.Done;
            }
            case "resume":
                if (_phase != PhGo || !_paused) return ActResult.Done;
                _pausedTotal += Ctx.Clock.UtcNow - _pausedAt;
                _paused = false;
                _viewDirty = true;
                return ActResult.Done;
            case "next":
                // за дограним столом: на яку трасу стати після «Ще раз» (0 — до списку трас)
                if (_daily || !TryInt(payload, "n", out var nx) || nx < 0 || nx > NamystoTracks.Count) return ActResult.Fail(Bad);
                if (nx > OpenFor()) return ActResult.Fail("Цю трасу ще не відкрито — пройди попередню");
                _next = nx;
                return ActResult.Done;
            default:
                return ActResult.Fail(Bad);
        }
    }

    static bool TryInt(JsonElement p, string name, out int v)
    {
        v = 0;
        return p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out v);
    }

    /// <summary>
    /// Пачка журналу: <c>{ s, ep, e: [[крок, 1, x, y] | [крок, 2], …] }</c> — «я на кроці s, ось що натиснув». Сервер
    /// докочує свою копію до s (не далі за свій годинник + <see cref="Ahead"/>), застосовуючи події на їхніх кроках.
    /// </summary>
    ActResult Batch(JsonElement p)
    {
        if (_phase != PhGo || _sim is null) return ActResult.Fail("Зараз не котиться");
        if (_paused) return ActResult.Fail("Пауза");
        if (!TryInt(p, "s", out var s)) return ActResult.Fail(Bad);
        // пачка зі старої епохи — браузер ще не перезібрався з журналу сервера
        if (TryInt(p, "ep", out var ep) && ep != _epoch) return ActResult.Fail("Звіряємось");
        _synced = true;
        // кроки подій не йдуть назад — ні в пачці, ні відносно того, що вже чекає; що раніше за крок сервера — нижче
        var floor = _pending.Count > 0 ? _pending[^1][0] : int.MinValue;
        var evs = new List<int[]>();
        if (p.TryGetProperty("e", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            if (arr.GetArrayLength() > MaxBatch) return ActResult.Fail(Bad);
            foreach (var it in arr.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Array) return ActResult.Fail(Bad);
                var len = it.GetArrayLength();
                if (len is not (2 or 4)) return ActResult.Fail(Bad);
                var v = new int[4];
                var i = 0;
                foreach (var x in it.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out v[i])) return ActResult.Fail(Bad);
                    i++;
                }
                if (v[1] == NamystoSim.EvFire ? len != 4 || v[2] is < -2000 or > 3000 || v[3] is < -2000 or > 3000
                    : v[1] != NamystoSim.EvSwap || len != 2) return ActResult.Fail(Bad);
                if (v[0] < floor || v[0] > s) return ActResult.Fail(Bad);
                floor = v[0];
                evs.Add(v);
            }
        }
        if (evs.Count > 0 && evs[0][0] < _sim.S)
        {
            // браузер натиснув на кроці, який сервер уже прокрутив без нього (відставав понад 3 с) — перезібратись
            Fix();
            return ActResult.Fail("Звіряємось");
        }
        _pending.AddRange(evs);
        Advance(Math.Min(s, Real() + Ahead));
        return ActResult.Done;
    }

    /// <summary>
    /// Докотити свою копію до кроку <paramref name="to"/>, застосовуючи події журналу на їхніх кроках. Разок скінчився —
    /// кінець партії одразу, а з <see cref="Load"/> (там нема куди слати нагороди) — на першому тику.
    /// </summary>
    void Advance(int to, bool finish = true)
    {
        var sim = _sim!;
        while (sim.S < to && sim.State == NamystoSim.Playing)
        {
            while (_pending.Count > 0 && _pending[0][0] <= sim.S)
            {
                var e = _pending[0];
                _pending.RemoveAt(0);
                if (e[0] < sim.S) continue;
                if (sim.Apply(e[1], e[2], e[3])) _journal.Add(e[1] == NamystoSim.EvFire ? e : [e[0], e[1]]);
            }
            sim.Step();
        }
        if (sim.State == NamystoSim.Playing || _phase != PhGo) return;
        if (finish) End();
        else _endOnTick = true;
    }

    void Fix()
    {
        _epoch++;
        _synced = false;
        _pending.Clear();
        _viewDirty = true;
    }

    /// <summary>Пауза від сервера: за кімнатою ніхто не стежить (закрили вкладку, пропав зв'язок).</summary>
    void Hold()
    {
        if (_phase != PhGo || _paused) return;
        _paused = true;
        _pausedAt = Ctx.Clock.UtcNow;
        // браузер міг натиснути щось, чого сервер ще не бачив, — хай перезбереться з журналу сервера
        Fix();
    }

    public override void Unwatched() => Hold();

    // ---------- тик ----------

    public override TickResult Tick()
    {
        if (_finishOnTick)
        {
            _finishOnTick = false;
            Ctx.Finish([0], "");
            return TickResult.Both;
        }
        if (_endOnTick)
        {
            _endOnTick = false;
            End();
            return TickResult.Both;
        }
        if (_phase == PhGo && _sim is not null && !_paused)
        {
            var lim = Real() - Behind;
            if (_sim.S < lim)
            {
                Advance(lim);
                if (_phase == PhGo && _synced) Fix();
            }
        }
        if (!_viewDirty) return TickResult.None;
        _viewDirty = false;
        return new TickResult(false, true);
    }

    // ---------- кінець ----------

    void End()
    {
        var sim = _sim!;
        var lv = _lv!;
        var won = sim.State == NamystoSim.Won;
        var pts = (int)Math.Min(int.MaxValue, sim.Score);
        var nick = Ctx.NickOf(0) ?? "";
        _phase = PhOver;
        _viewDirty = true;
        if (sim.MaxCombo >= ComboAch) Ctx.Award(0, 0, "ach:namysto-combo");
        if (won && sim.MaxHead >= sim.End - EdgeBeads * NamystoSim.Dm) Ctx.Award(0, 0, "ach:namysto-edge");
        if (won && sim.Misses == 0) Ctx.Award(0, 0, "ach:namysto-clean");

        if (_daily)
        {
            _result = Summary(sim, won, false, false, null);
            Report(pts, sim.Popped, won);
            return;
        }
        var (first, record) = _store?.Record(nick, lv.N, pts, won, Ctx.Clock.UtcNow) ?? (false, false);
        if (first) Ctx.Award(0, FirstClearShards, $"namysto:{lv.N}");
        if (won && _store?.AllCleared(nick) == true) Ctx.Award(0, 0, "ach:namysto-all");
        _result = Summary(sim, won, first, record, _store?.Best(nick, lv.N)?.Best);
        _next = lv.N;
        Ctx.Score(0, pts);
        var log = first ? $"Намисто: {nick} — «{lv.Name}» пройдено вперше, {pts} {Tyr.Ochok(pts)}" : "";
        Ctx.Finish(won ? [0] : [], log, new Dictionary<int, long> { [0] = pts }, won ? "📿 Разок зібрано" : "💧 Намисто впало в криницю");
    }

    object Summary(NamystoSim sim, bool won, bool first, bool record, long? best) => new
    {
        won,
        score = sim.Score,
        popped = sim.Popped,
        combo = sim.MaxCombo,
        fired = sim.Fired,
        misses = sim.Misses,
        sec = Math.Round(sim.S / (double)NamystoSim.StepsPerSecond, 1),
        clear = sim.ClearBonus,
        time = sim.TimeBonus,
        first,
        record,
        best,
        shards = first ? FirstClearShards : 0,
    };

    /// <summary>Спроба дня — у таблицю дня (перша й остання), черепки дня, рядок у Журнал.</summary>
    void Report(int pts, int popped, bool won)
    {
        _phase = PhOver;
        _viewDirty = true;
        if (_reported) { Ctx.Finish([0], ""); return; }
        _reported = true;
        var nick = Ctx.NickOf(0) ?? "";
        _board?.Record(_day, nick, pts, popped, won, Ctx.Clock.UtcNow);
        // щоденний каркас рахує спроби й час («менше — краще»); очки лежать у своїй таблиці дня, тож тут — одна спроба
        Ctx.Score(0, 0, 1);
        Ctx.Award(0, 0, $"daily:{Info.Id}");
        Ctx.Finish([0], $"{Info.Title}: {nick} — {pts} {Tyr.Ochok(pts)}" + (won ? ", разок зібрано" : ""),
            new Dictionary<int, long> { [0] = pts }, won ? "📿 Разок зібрано" : "💧 Намисто впало в криницю");
    }

    // ---------- вид ----------

    public override object View(int? seat)
    {
        var nick = Ctx.NickOf(0);
        var lv = _lv;
        return new
        {
            mode = _daily ? "daily" : "campaign",
            phase = _phase,
            paused = _paused,
            ep = _epoch,
            tv = NamystoSetup.TracksVersion.Value,
            lv = lv is null ? null : new { n = lv.N, name = lv.Name, place = lv.Place, colors = lv.Colors, beads = lv.Beads, speed = lv.Speed, par = lv.Par },
            seed = lv is null ? 0 : _seed,
            s = _sim?.S ?? 0,
            // годинник сервера (кроки): браузер, що перезбирається, докочує свою копію до нього, а не стоїть на s
            real = Real(),
            jr = _phase == PhGo ? _journal.ToArray() : null,
            result = _result,
            levels = _daily || nick is null ? null : Levels(nick),
            daily = _daily ? DailyWire(nick) : null,
        };
    }

    object[] Levels(string nick)
    {
        var open = _store?.Open(nick) ?? 1;
        var list = new object[NamystoTracks.Count];
        for (var n = 1; n <= NamystoTracks.Count; n++)
        {
            var mine = _store?.Best(nick, n);
            var top = _store?.Top(n, 3) ?? [];
            list[n - 1] = new
            {
                n,
                open = n <= open,
                best = mine?.Best,
                cleared = mine?.Cleared == true,
                top = top.Select(b => new { nick = b.Nick, best = b.Best, cleared = b.Cleared }).ToArray(),
            };
        }
        return list;
    }

    object DailyWire(string? nick)
    {
        var top = _board?.Top(_day) ?? [];
        var mine = nick is null ? -1 : top.ToList().FindIndex(r => NamystoStore.Key(r.Nick) == NamystoStore.Key(nick));
        var done = _phase == PhOver;
        var me = nick is null ? null : _board?.Of(_day, nick);
        var no = _day.Length == 0 ? 0 : Days.Number(_day);
        return new
        {
            day = _day,
            no,
            // таблицю дня показуємо лише після своєї спроби: інакше це підказка, скільки там узагалі можна набрати
            board = !done ? null : top.Take(10).Select(r => new { nick = r.Nick, points = r.Points, won = r.Won }).ToArray(),
            place = !done || mine < 0 ? (int?)null : mine + 1,
            players = top.Count,
            points = done ? me?.Points : null,
            share = !done || me is null ? null
                : $"Намисто дня №{no} — {me.Points} {Tyr.Ochok(me.Points)}" + (me.Won ? ", разок зібрано 📿" : "") + "\nhttps://hlechyky.pp.ua/#games",
        };
    }

    // ---------- збереження («Намисто дня») ----------

    sealed record Saved(string Day, bool Started, bool Reported, int S, int Epoch, int[][]? Journal);

    /// <summary>День і спроба: почата спроба береже журнал — після перезапуску сервера чи закритої кімнати вона продовжиться.</summary>
    public override string? Save() => !_daily || _day.Length == 0 ? null
        : JsonSerializer.Serialize(new Saved(_day, _started, _reported, _sim?.S ?? 0, _epoch,
            _phase == PhGo && _sim is not null ? [.. _journal] : null));

    /// <summary>
    /// Сьогоднішній день: уже зіграний — підсумок (кінець партії на першому тику); почату й недограну спробу не починаємо
    /// заново (друга спроба з уже баченим разком) — проганяємо збережений журнал і ставимо на паузу.
    /// </summary>
    public override void Load(string json)
    {
        if (!_daily) return;
        Saved? s;
        try { s = JsonSerializer.Deserialize<Saved>(json); }
        catch (JsonException) { return; }
        if (s is null || s.Day != _day) return;
        _started = s.Started;
        _reported = s.Reported;
        if (_reported)
        {
            _phase = PhOver;
            _finishOnTick = true;
            _viewDirty = true;
            return;
        }
        if (!_started || _lv is null) return;
        var sim = new NamystoSim(_lv.Track, _lv.Colors, _lv.Beads, _lv.Speed, _lv.Par, _seed);
        _sim = sim;
        _journal.Clear();
        _pending.Clear();
        _phase = PhGo;
        foreach (var e in s.Journal ?? [])
            if (e is { Length: 2 or 4 }) _pending.Add(e.Length == 4 ? e : [e[0], e[1], 0, 0]);
        Advance(Math.Max(0, s.S), finish: false);
        if (_endOnTick) return;   // журнал довів до кінця — зарахуємо на першому тику
        var now = Ctx.Clock.UtcNow;
        // з округленням угору: інакше Real() після ділення назад дав би на крок менше
        _goAt = now - TimeSpan.FromTicks(((long)sim.S * TimeSpan.TicksPerSecond + NamystoSim.StepsPerSecond - 1) / NamystoSim.StepsPerSecond);
        _pausedTotal = TimeSpan.Zero;
        _paused = true;
        _pausedAt = now;
        _epoch = s.Epoch + 1;
        _synced = false;
        _viewDirty = true;
    }
}

/// <summary>«Намисто дня» — одна траса й однакові намистини для всіх до київської півночі, одна спроба в таблицю дня.</summary>
public sealed class NamystoDaily : Namysto, IDailyGame
{
    public NamystoDaily() : base(daily: true) { }
}
