using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Колія» (кооп 1–4, як Unrailed) — сервер лише «секретар»: правил гри в C# нема. Світ рахує кожен браузер сам
/// (web/games/koliya/rules.js, детерміновано), а сервер приймає команди гравців, ставить кожній номер тику партії й
/// розсилає всім однаковий журнал <c>[{tick, p, cmd}]</c>. Однаковий журнал + однаковий сід = однаковий світ.
/// <para>
/// Тик партії — від годинника: <c>T = (зараз − старт) / 50 мс</c> (тик рушія сюди не входить, пропущений тик нічого не
/// губить). Команда несе бажаний тик <c>t</c> (браузер передбачає свій світ наперед і хоче, щоб дія лягла саме туди);
/// сервер приймає його, якщо тик ще не «закрито», інакше зсуває на перший відкритий. «Закрито» — усе до водяного знаку
/// <c>W = T − <see cref="Grace"/> − 1</c>: туди нових команд уже не буде, браузер може рахувати ці тики остаточно.
/// Як браузер перекочує передбачене на остаточне — у шапці web/games/koliya/net.js.
/// </para>
/// <para>
/// Кадр (кожен тик рушія, 50 мс): <c>{ e, t, w, n, f, c }</c> — епоха (номер перезапуску), тик, водяний знак, довжина
/// журналу й записи <c>[f, n)</c>, тики яких ще не відійшли далеко за знак (<see cref="KeepTicks"/>): каркас може
/// згорнути кадри, тож запис іде в кількох кадрах поспіль. Хто відстав більше (діра в номерах) чи щойно прийшов —
/// бере журнал окремо: <c>GET /api/games/koliya/{стіл}/log?from=N</c> (<see cref="KoliyaSetup"/>).
/// Запис — 10 цілих: <c>[seq, t, p, k, a, b, c, d, e, i]</c>, k: 1 in (a=mx, b=my), 2 use (a=x, b=y, c=fx, d=fy,
/// e=1 — «затиснуто»), 3 join, 4 leave; i — номер команди у відправника (щоб він упізнав свою).
/// </para>
/// <para>
/// Двоє за одним ПК (K6): місце може мати другого гравця rules (<c>Act "join2"</c> / <c>"leave2"</c>; команди 'in'/'use'
/// з <c>q = 1</c> — від нього). Разом гравців rules ≤ 4: коли всі слоти зайняті, «join2» відмовляє, а новий за столом
/// дивиться, доки хтось не встане. Перезапуск зберігає другого: гравці rules — за порядком місць, перший, потім другий.
/// </para>
/// </summary>
public sealed class Koliya : Game, IUnlistedGame
{
    public override GameInfo Info { get; } = new(
        "koliya", "Колія", "«Колію»", GameGroup.Live, 1, 4,
        TickMs: StepMs, Start: StartMode.Immediate,
        Hint: "Кооп до чотирьох: рубай, копай, крафть рейки й веди колію поперед паротяга");

    /// <summary>Тик правил (rules.js TICK_MS) і рушія.</summary>
    public const int StepMs = 50;

    /// <summary>Скільки тиків після «зараз» ще відкрито для запізнілих команд: зсув на більше — помітний ривок.</summary>
    public const int Grace = 6;

    /// <summary>Наскільки наперед можна просити тик (далі — зрізаємо).</summary>
    public const int MaxAhead = 40;

    /// <summary>Скільки тиків запис ще їде в кадрах після водяного знаку.</summary>
    public const int KeepTicks = 10;

    /// <summary>Стеля журналу партії (≈ 5 год гри вчотирьох): далі — лише перезапуск.</summary>
    public const int MaxLog = 300_000;

    /// <summary>Скільки останніх звірок хешу пам'ятати.</summary>
    public const int HashKeep = 64;

    public const int Stride = 10;

    /// <summary>Гравців rules.js (MAX_PLAYERS) — з двома за ПК місць може бути менше.</summary>
    public const int MaxRulesPlayers = 4;
    public const int In = 1, Use = 2, Join = 3, Leave = 4;

    /// <summary>Порожній стіл чекає свого (F5, обрив).</summary>
    public override TimeSpan HoldEmpty => TimeSpan.FromSeconds(60);

    readonly record struct Entry(int T, int P, int K, int A, int B, int C, int D, int E, int I);

    readonly List<Entry> _log = [];
    /// <summary>Місце → гравець у rules (-1 — не грає).</summary>
    readonly int[] _pOf = [-1, -1, -1, -1];
    /// <summary>Місце → другий гравець rules за тим самим ПК (-1 — нема).</summary>
    readonly int[] _p2Of = [-1, -1, -1, -1];
    /// <summary>Гравці rules: true — пішов (слот вільний, join займе перший такий, як у rules.js join).</summary>
    readonly List<bool> _out = [];
    readonly int[] _lastT = new int[4];
    readonly Dictionary<int, uint> _hashes = [];
    int _epoch;
    uint _seed;
    string _params = "{}";
    int _n;
    DateTimeOffset _startAt;
    int _w = -1;
    int _keepFrom;
    int _desync = -1;
    int _leaveT;   // K24: тик останнього leave у журналі
    bool _dirty;

    public override string SeatName(int seat) => "колійник";

    public override bool LateJoin(string nick) => true;
    public override string? LateJoinGreeting(string nick) => "Сідай — колію кладемо разом";

    public override void Start()
    {
        _epoch = 0;
        NewEpoch((uint)Ctx.Rng.Next(), "{}");
    }

    /// <summary>Нова партія світу: хто зараз сидить — гравці 0..n-1 за порядком місць, журнал з нуля.</summary>
    void NewEpoch(uint seed, string prm)
    {
        _epoch++;
        _seed = seed;
        _params = prm;
        _log.Clear();
        _out.Clear();
        _hashes.Clear();
        _desync = -1;
        _keepFrom = 0;
        _w = -1;
        _leaveT = 0;
        Array.Fill(_lastT, 0);
        _n = 0;
        // спершу кожне місце по гравцю, тоді другі за ПК — скільки влізе в 4
        for (var s = 0; s < _pOf.Length; s++)
        {
            _pOf[s] = s < Info.MaxPlayers && Ctx.Seated(s) && _n < MaxRulesPlayers ? _n++ : -1;
            if (_pOf[s] >= 0) _out.Add(false);
        }
        for (var s = 0; s < _p2Of.Length; s++)
        {
            _p2Of[s] = _p2Of[s] >= 0 && _pOf[s] >= 0 && _n < MaxRulesPlayers ? _n++ : -1;
            if (_p2Of[s] >= 0) _out.Add(false);
        }
        _startAt = Ctx.Clock.UtcNow;
        _dirty = true;
    }

    /// <summary>Тик партії зараз (від годинника).</summary>
    int NowTick() => (int)Math.Max(0, Math.Floor((Ctx.Clock.UtcNow - _startAt).TotalMilliseconds / StepMs));

    /// <summary>Водяний знак: тики ≤ W закрито. Лише росте (годинник ПК може ступити назад).</summary>
    int Mark() => _w = Math.Max(_w, NowTick() - Grace - 1);

    public override void OnJoin(int seat)
    {
        if (seat < 0 || seat >= _pOf.Length || _pOf[seat] >= 0) return;
        if (NewPlayer(seat) is { } p) _pOf[seat] = p;   // усі 4 слоти rules зайняті (двоє за ПК) — дивиться
    }

    /// <summary>rules.js join: перший вільний слот, інакше новий у кінці (не більше 4).</summary>
    int? NewPlayer(int seat)
    {
        var p = _out.IndexOf(true);
        if (p < 0 && _out.Count >= MaxRulesPlayers) return null;
        if (p < 0) { p = _out.Count; _out.Add(false); }
        else _out[p] = false;
        // K24: rules.js join бере перший вільний слот НА СВОЄМУ тику — тож не раніше за останній leave (той міг лягти
        // наперед, за _lastT того місця), інакше rules дасть інший слот чи відмовить, а місце керуватиме вибулим.
        Add(seat, new Entry(0, p, Join, 0, 0, 0, 0, 0, 0), Math.Max(NowTick(), _leaveT));
        _dirty = true;
        return p;
    }

    void Gone(int seat, int p)
    {
        _leaveT = Math.Max(_leaveT, Add(seat, new Entry(0, p, Leave, 0, 0, 0, 0, 0, 0), null));   // K24
        _out[p] = true;
        _dirty = true;
    }

    /// <summary>Не техпоразка: гравець іде (rules кладе його речі на землю), решта грає далі. Другий за його ПК — теж.</summary>
    public override void OnLeave(int seat)
    {
        if (seat < 0 || seat >= _pOf.Length) return;
        if (_p2Of[seat] >= 0) { Gone(seat, _p2Of[seat]); _p2Of[seat] = -1; }
        if (_pOf[seat] < 0) return;
        Gone(seat, _pOf[seat]);
        _pOf[seat] = -1;
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (seat < 0 || seat >= _pOf.Length) throw new GameError("Ти не за столом");
        // K24: ввід зі старої епохи (летів, поки хтось перезапустив) — мовчки геть: його тик і поза — зі старого світу
        // (use з позою телепортував би гравця в новому світі, може й у скелю), хеш — звірка зі старим світом.
        if (action is not ("restart" or "join2" or "leave2") && Stale(payload)) return ActResult.Done;
        switch (action)
        {
            case "restart":
                NewEpoch(ReadSeed(payload), ReadParams(payload));
                return ActResult.Done;
            case "join2":
                Player(seat);
                if (_p2Of[seat] < 0) _p2Of[seat] = NewPlayer(seat) ?? throw new GameError("Усі четверо вже грають");
                return ActResult.Done;
            case "leave2":
                if (_p2Of[seat] >= 0) { Gone(seat, _p2Of[seat]); _p2Of[seat] = -1; }
                return ActResult.Done;
            case "in":
            {
                var p = Player(seat, payload);
                Add(seat, new Entry(0, p, In, Clamp(payload, "mx", -100, 100), Clamp(payload, "my", -100, 100), 0, 0, 0, Clamp(payload, "i", 0, int.MaxValue)), ReqTick(payload));
                return ActResult.Done;
            }
            case "use":
            {
                var p = Player(seat, payload);
                Add(seat, new Entry(0, p, Use, Clamp(payload, "x", 0, 10_000_000), Clamp(payload, "y", 0, 10_000_000),
                    Clamp(payload, "fx", -1, 1), Clamp(payload, "fy", -1, 1), Clamp(payload, "h", 0, 1), Clamp(payload, "i", 0, int.MaxValue)), ReqTick(payload));
                return ActResult.Done;
            }
            case "hash":
                Hash(payload);
                return ActResult.Done;
            default:
                throw new GameError("Тут так не ходять");
        }
    }

    int Player(int seat) => _pOf[seat] >= 0 ? _pOf[seat] : throw new GameError("Ти ще не в цій партії — перезапусти");

    /// <summary>Гравець команди: q = 1 — другий за тим самим ПК.</summary>
    int Player(int seat, JsonElement payload) =>
        Clamp(payload, "q", 0, 1) == 0 ? Player(seat) : _p2Of[seat] >= 0 ? _p2Of[seat] : throw new GameError("Другого гравця нема");

    /// <summary>Записати команду: тик — бажаний, але не в закрите, не раніше попередньої цього ж місця й не надто наперед.</summary>
    int Add(int seat, Entry e, int? want)
    {
        if (_log.Count >= MaxLog) throw new GameError("Партія задовга — перезапусти");
        var now = NowTick();
        var t = Math.Max(want ?? now, Mark() + 1);
        t = Math.Min(t, now + MaxAhead);
        t = Math.Max(t, _lastT[seat]);
        _lastT[seat] = t;
        _log.Add(e with { T = t });
        return t;
    }

    /// <summary>
    /// Звірка: браузер шле хеш остаточного світу на тик t (до того, як рахувати тик t). Перший хеш тику — еталон;
    /// інший від будь-кого — розбіжність, номер першого такого тику йде в кадр (<c>d</c>) і в вид.
    /// </summary>
    void Hash(JsonElement payload)
    {
        var t = Clamp(payload, "t", 0, int.MaxValue);
        if (!payload.TryGetProperty("h", out var he) || !he.TryGetInt64(out var hl) || hl < 0 || hl > uint.MaxValue) throw new GameError("Поганий хеш");
        if (t > _w + 1) return;   // ще не закритий тик — звіряти нема з чим
        var h = (uint)hl;
        if (_hashes.TryGetValue(t, out var was))
        {
            if (was != h && _desync < 0) { _desync = t; _dirty = true; }
            return;
        }
        _hashes[t] = h;
        if (_hashes.Count > HashKeep) _hashes.Remove(_hashes.Keys.Min());
    }

    public override TickResult Tick()
    {
        var w = Mark();
        while (_keepFrom < _log.Count && _log[_keepFrom].T <= w - KeepTicks) _keepFrom++;
        if (!_dirty) return TickResult.FrameOnly;
        _dirty = false;
        return TickResult.Both;
    }

    public override object? Frame() => new
    {
        e = _epoch, t = NowTick(), w = _w, n = _log.Count, f = _keepFrom, c = Flat(_keepFrom), d = _desync,
    };

    int[] Flat(int from)
    {
        from = Math.Clamp(from, 0, _log.Count);
        var a = new int[(_log.Count - from) * Stride];
        for (int i = from, j = 0; i < _log.Count; i++, j += Stride)
        {
            var e = _log[i];
            a[j] = i; a[j + 1] = e.T; a[j + 2] = e.P; a[j + 3] = e.K; a[j + 4] = e.A;
            a[j + 5] = e.B; a[j + 6] = e.C; a[j + 7] = e.D; a[j + 8] = e.E; a[j + 9] = e.I;
        }
        return a;
    }

    public override object View(int? seat) => new
    {
        e = _epoch, seed = _seed, prm = Prm(), n0 = _n, p = seat is { } s && s >= 0 && s < _pOf.Length ? _pOf[s] : -1,
        pOf = _pOf.ToArray(), p2 = seat is { } s2 && s2 >= 0 && s2 < _p2Of.Length ? _p2Of[s2] : -1, p2Of = _p2Of.ToArray(), d = _desync,
    };

    /// <summary>Журнал від запису <paramref name="from"/> — пізньому, після F5 чи діри в кадрах. Кличеться під замком кімнати.</summary>
    public object Log(int from) => new
    {
        e = _epoch, seed = _seed, prm = Prm(), n0 = _n, t = NowTick(), w = Mark(), n = _log.Count, f = Math.Clamp(from, 0, _log.Count),
        c = Flat(from), pOf = _pOf.ToArray(), p2Of = _p2Of.ToArray(), d = _desync,
    };

    JsonElement Prm()
    {
        using var doc = JsonDocument.Parse(_params);
        return doc.RootElement.Clone();
    }

    /// <summary>K24: команда несе епоху <c>e</c> (net.js) — і вона не поточна. Без <c>e</c> — як було.</summary>
    bool Stale(JsonElement p) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty("e", out var e) && e.ValueKind == JsonValueKind.Number
        && e.TryGetInt32(out var v) && v != _epoch;

    static int? ReqTick(JsonElement p) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var v) ? v : null;

    static int Clamp(JsonElement p, string name, int min, int max)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var e)) return Math.Clamp(0, min, max);
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var v) && double.IsFinite(v))
            return (int)Math.Clamp(Math.Round(v), min, max);
        throw new GameError("Погана команда");
    }

    static uint ReadSeed(JsonElement p)
    {
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("seed", out var s) && s.ValueKind == JsonValueKind.Number
            && s.TryGetDouble(out var v) && double.IsFinite(v) && v >= 0 && v <= uint.MaxValue)
            return (uint)v;
        throw new GameError("Який сід?");
    }

    /// <summary>Параметри світу — як прийшли (об'єкт ≤ 4 КБ); сервер їх не читає, лише віддає всім.</summary>
    static string ReadParams(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("params", out var prm)) return "{}";
        if (prm.ValueKind != JsonValueKind.Object) throw new GameError("Погані параметри");
        var raw = prm.GetRawText();
        return raw.Length <= 4096 ? raw : throw new GameError("Задовгі параметри");
    }
}
