using System.Text.Json;
using System.Text.Json.Nodes;

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
/// e=1 — «затиснуто»), 3 join, 4 leave, 5 throw (a=x, b=y, c=fx, d=fy — кидок K18 залежить від пози), 6 horn (гудок K18),
/// 7 feel (a — своє керування гравця: рух×9 + удар×3 + приціл, rules.js FEEL); i — номер команди у відправника (щоб він упізнав свою).
/// </para>
/// <para>
/// Налаштування столу — одні на всіх: <c>Act "params"</c> {params} від будь-кого за столом (у зборі світ одразу
/// перебудовується тим самим сідом), «restart» без params бере саме їх (+ mode з команди). У виді й журналі: <c>tp</c> —
/// налаштування, <c>tb</c> — хто змінив, <c>tv</c> — номер зміни. Сервер їх не читає, як і params партії (<c>prm</c>).
/// </para>
/// <para>
/// Двоє за одним ПК (K6): місце може мати другого гравця rules (<c>Act "join2"</c> / <c>"leave2"</c>; команди 'in'/'use'
/// з <c>q = 1</c> — від нього). Разом гравців rules ≤ 4: коли всі слоти зайняті, «join2» відмовляє, а новий за столом
/// дивиться, доки хтось не встане. Перезапуск зберігає другого: гравці rules — за порядком місць, перший, потім другий.
/// </para>
/// <para>
/// K28 «люди за столом» — усе повз журнал (світ і хеш від цього не міняються): <c>nm</c> у виді й журналі — ніки
/// гравців rules; Input <c>"react"</c> {em, q, i} — емодзі над гравцем (≤ 1 на <see cref="ReactGapMs"/>), у кадрі
/// <c>r</c> = [seq, p, e, i]×N ще <see cref="ReactKeepMs"/>.
/// </para>
/// <para>
/// K62 «збір»: нова епоха (новий стіл, перезапуск) не рушає сама — годинник тиків стоїть на 0 (<c>wait = 1</c> у кадрі,
/// виді й журналі), поки хтось за столом не скаже <c>Act "go"</c>; тоді тик 0 — з цієї миті. У зборі приймаються лише
/// join/leave (вони лягають на тик 0), реакції, пінги й хеші; 'in'/'use'/'throw'/'horn' мовчки геть (браузер їх і не шле:
/// передбачений світ стоїть на тику 0).
/// </para>
/// </summary>
public sealed class Koliya : Game
{
    public override GameInfo Info { get; } = new(
        "koliya", "Колія", "«Колію»", GameGroup.Live, 1, 4,
        TickMs: StepMs, Start: StartMode.Immediate,
        Hint: "Кооп до чотирьох: рубай ліс, бий камінь, майструй рейки й веди колію поперед паротяга");

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
    public const int In = 1, Use = 2, Join = 3, Leave = 4, Throw = 5, Horn = 6, Feel = 7;
    /// <summary>Кодів свого керування (rules.js FEEL: 3 рухи × 3 удари × 3 приціли).</summary>
    public const int FeelCodes = 27;

    /// <summary>Порожній стіл чекає свого (F5, обрив).</summary>
    public override TimeSpan HoldEmpty => TimeSpan.FromSeconds(60);

    /// <summary>Хто встав кнопкою — пішов: останній такий не лишає по собі «іде партія» на хвилину.</summary>
    public override bool HoldAfterLeave => false;

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
    bool _wait;    // K62: збір — годинник стоїть на 0, поки хтось не скаже "go"
    string _table = "{}";   // налаштування столу (без mode): «restart» без params бере їх
    string? _tableBy;       // хто змінив останнім
    int _tableV;            // номер зміни (браузер упізнає нову й каже «Smaug поставив …»)

    // ---- K28: люди за столом (повз журнал: світ від цього не міняється) ----
    /// <summary>Скільки є реакцій-емодзі (web/games/koliya/net.js REACTS).</summary>
    public const int ReactKinds = 4;
    /// <summary>Не частіше за раз на стільки від одного гравця rules (≈ 1 на секунду з запасом на тремтіння мережі).</summary>
    public const int ReactGapMs = 900;
    /// <summary>Скільки реакція їде в кадрах (каркас може згорнути кадри; браузер відкидає вже бачене за номером).</summary>
    public const int ReactKeepMs = 1500;

    readonly record struct React(int Seq, int P, int E, int I, DateTimeOffset At);
    readonly List<React> _reacts = [];
    readonly DateTimeOffset[] _reactAt = new DateTimeOffset[MaxRulesPlayers];
    int _reactSeq;

    /// <summary>Без жодної команди/пінгу від місця довше — гравець «відпав» (браузер пінгує раз на секунду).</summary>
    public const int LostMs = 3000;
    /// <summary>Місце → коли востаннє щось прислало (будь-яка дія).</summary>
    readonly DateTimeOffset[] _seen = new DateTimeOffset[4];
    /// <summary>Місце → пінг, що його браузер сам заміряв (мс), номер і час приходу останнього пінгу.</summary>
    readonly int[] _rtt = new int[4];
    readonly int[] _pingI = new int[4];
    readonly DateTimeOffset[] _pingAt = new DateTimeOffset[4];

    /// <summary>Глядач зник, якщо не озивався довше (браузер озивається раз на 2 с).</summary>
    public const int WatchMs = 5000;
    /// <summary>Скільки глядачів пам'ятати (далі — забуваємо найдавнішого).</summary>
    public const int WatchKeep = 64;
    /// <summary>Номер вкладки глядача → коли востаннє озивався.</summary>
    readonly Dictionary<string, DateTimeOffset> _watch = [];

    /// <summary>K28: глядач озвався (KoliyaSetup …/watch). Кличеться під замком кімнати. Повертає, скільки глядачів зараз.</summary>
    public int Watch(string id)
    {
        var now = Ctx.Clock.UtcNow;
        if (!_watch.ContainsKey(id) && _watch.Count >= WatchKeep)
        {
            Forget(now);
            if (_watch.Count >= WatchKeep) _watch.Remove(_watch.MinBy(x => x.Value).Key);
        }
        _watch[id] = now;
        return Watchers();
    }

    void Forget(DateTimeOffset now)
    {
        foreach (var k in _watch.Where(x => now - x.Value > TimeSpan.FromMilliseconds(WatchMs)).Select(x => x.Key).ToList()) _watch.Remove(k);
    }

    /// <summary>Скільки глядачів озивалось за останні <see cref="WatchMs"/>.</summary>
    int Watchers()
    {
        var old = Ctx.Clock.UtcNow - TimeSpan.FromMilliseconds(WatchMs);
        var n = 0;
        foreach (var at in _watch.Values) if (at >= old) n++;
        return n;
    }

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
        _votes.Clear(); _snap = null; _snapWait = null;   // K44: знімки старої епохи — не для нової
        _reacts.Clear();   // K28: номери реакцій не скидаємо — браузер відкидає бачене за номером
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
        _wait = true;   // K62: чекаємо, поки зберуться (друг іде за посиланням)
        for (var s = 0; s < _seen.Length; s++) if (_seen[s] == default) _seen[s] = _startAt;   // K28: перший старт — усі живі
        _dirty = true;
    }

    /// <summary>Тик партії зараз (від годинника).</summary>
    int NowTick() => _wait ? 0 : (int)Math.Max(0, Math.Floor((Ctx.Clock.UtcNow - _startAt).TotalMilliseconds / StepMs));

    /// <summary>Водяний знак: тики ≤ W закрито. Лише росте (годинник ПК може ступити назад).</summary>
    int Mark() => _w = Math.Max(_w, NowTick() - Grace - 1);

    public override void OnJoin(int seat)
    {
        if (seat < 0 || seat >= _pOf.Length || _pOf[seat] >= 0) return;
        _seen[seat] = Ctx.Clock.UtcNow; _rtt[seat] = 0; _pingI[seat] = 0;   // K28: новий за місцем — ще не «відпав»
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
        _seen[seat] = Ctx.Clock.UtcNow;   // K28: живий (і старою епохою теж)
        // K24: ввід зі старої епохи (летів, поки хтось перезапустив) — мовчки геть: його тик і поза — зі старого світу
        // (use з позою телепортував би гравця в новому світі, може й у скелю), хеш — звірка зі старим світом.
        if (action is not ("restart" or "join2" or "leave2" or "params") && Stale(payload)) return ActResult.Done;
        // K62: у зборі світ стоїть — рух і дії не пишемо (тик 0 мусить лишитись однаковим, хоч би хто що тиснув)
        if (_wait && action is "in" or "use" or "throw" or "horn") return ActResult.Done;
        switch (action)
        {
            case "ping":    // K28: { i, r } — номер пінгу (луна в кадрі pa) і заміряний браузером пінг для всіх
                _pingI[seat] = Clamp(payload, "i", 0, int.MaxValue);
                _pingAt[seat] = Ctx.Clock.UtcNow;
                _rtt[seat] = Clamp(payload, "r", 0, 9999);
                return ActResult.Done;
            case "restart":   // з params — як прислали (маршрут дня); без — налаштування столу + mode
                NewEpoch(ReadSeed(payload), HasParams(payload) ? ReadParams(payload) : WithMode(_table, ReadMode(payload)));
                return ActResult.Done;
            case "params":    // налаштування столу — усім; у зборі ще ніхто не рушив, тож світ перебудовуємо одразу
                _table = WithMode(ReadParams(payload), null);
                _tableBy = Ctx.NickOf(seat);
                _tableV++;
                _dirty = true;
                if (_wait) NewEpoch(_seed, WithMode(_table, ModeOf(_params)));
                return ActResult.Done;
            case "go":      // K62: «▶ Почали» — годинник рушає з цієї миті (повтор — нічого)
                if (_wait) { _wait = false; _startAt = Ctx.Clock.UtcNow; _dirty = true; }
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
            case "throw":   // K18: кидок речі — з позою, як use
            {
                var p = Player(seat, payload);
                Add(seat, new Entry(0, p, Throw, Clamp(payload, "x", 0, 10_000_000), Clamp(payload, "y", 0, 10_000_000),
                    Clamp(payload, "fx", -1, 1), Clamp(payload, "fy", -1, 1), 0, Clamp(payload, "i", 0, int.MaxValue)), ReqTick(payload));
                return ActResult.Done;
            }
            case "horn":    // K18: гудок — без пози
            {
                var p = Player(seat, payload);
                Add(seat, new Entry(0, p, Horn, 0, 0, 0, 0, 0, Clamp(payload, "i", 0, int.MaxValue)), ReqTick(payload));
                return ActResult.Done;
            }
            case "feel":    // своє керування (рух, удар, приціл) — у журнал, щоб усі браузери поклали його тому самому гравцю
            {
                var p = Player(seat, payload);
                Add(seat, new Entry(0, p, Feel, Clamp(payload, "f", 0, FeelCodes - 1), 0, 0, 0, 0, Clamp(payload, "i", 0, int.MaxValue)),
                    _wait ? null : ReqTick(payload));
                return ActResult.Done;
            }
            case "hash":
                Hash(seat, payload);
                return ActResult.Done;
            case "react":   // K28: емодзі над гравцем — повз журнал правил, світ і хеш не чіпає
            {
                var p = Player(seat, payload);
                var e = Clamp(payload, "em", -1, 1000);   // не "e": то епоха (K24, Stale)
                if (e < 0 || e >= ReactKinds) throw new GameError("Такої реакції нема");
                var now = Ctx.Clock.UtcNow;
                if (now - _reactAt[p] < TimeSpan.FromMilliseconds(ReactGapMs)) throw new GameError("Не так часто");
                _reactAt[p] = now;
                _reacts.Add(new React(++_reactSeq, p, e, Clamp(payload, "i", 0, int.MaxValue), now));
                return ActResult.Done;
            }
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
    void Hash(int seat, JsonElement payload)
    {
        var t = Clamp(payload, "t", 0, int.MaxValue);
        if (!payload.TryGetProperty("h", out var he) || !he.TryGetInt64(out var hl) || hl < 0 || hl > uint.MaxValue) throw new GameError("Поганий хеш");
        if (t > _w + 1) return;   // ще не закритий тик — звіряти нема з чим
        var h = (uint)hl;
        if (_hashes.TryGetValue(t, out var was))
        {
            if (was != h && _desync < 0) { _desync = t; _dirty = true; }
            if (was == h) { _votes[t] = _votes.GetValueOrDefault(t) | 1 << seat; Confirm(); }   // K44
            return;
        }
        _hashes[t] = h;
        _votes[t] = 1 << seat;
        if (_hashes.Count > HashKeep) { var k = _hashes.Keys.Min(); _hashes.Remove(k); _votes.Remove(k); }
        Confirm();
    }

    // ---- K44: знімки стану для пізнього входу ----
    /// <summary>Стеля знімка (стиснутий світ смуги — кілька КБ; нескінченна — більше).</summary>
    public const int MaxSnap = 256 * 1024;
    /// <summary>Тик → місця (біти), чий хеш на цьому тику збігся з першим (еталоном).</summary>
    readonly Dictionary<int, int> _votes = [];
    sealed record Snap(int T, uint H, bool Z, byte[] B);
    /// <summary>Чекає підтвердження хешами (_snapWait) і останній підтверджений (_snap) — віддається з журналом.</summary>
    Snap? _snapWait, _snap;

    /// <summary>
    /// K44: браузер прислав знімок остаточного світу на тик t (до кроку t) з його хешем h — непрозорий blob, сервер не
    /// розбирає. Підтверджений він, коли хеш тику t від ≥ 2 місць (чи від одного, якщо грає одне місце) — такий самий.
    /// Кличеться під замком кімнати. Повертає помилку чи null.
    /// </summary>
    public string? PutSnap(int e, int t, long h, bool z, byte[] b)
    {
        if (e != _epoch) return "стара епоха";
        if (b.Length == 0 || b.Length > MaxSnap) return "розмір";
        if (h < 0 || h > uint.MaxValue || t <= 0 || t > Mark() + 1) return "тик чи хеш";
        if (_snap is { } s && t <= s.T) return null;               // уже є свіжіший підтверджений
        if (_snapWait is { } q && t <= q.T) return null;           // цей тик (чи новіший) уже чекає — повтор від іншого
        if (_hashes.TryGetValue(t, out var was) && was != (uint)h) return "хеш не той";
        _snapWait = new Snap(t, (uint)h, z, b);
        Confirm();
        return null;
    }

    /// <summary>Скільки місць мусять підтвердити знімок: 2, а якщо грає одне — 1.</summary>
    int Need()
    {
        var n = 0;
        foreach (var p in _pOf) if (p >= 0) n++;
        return Math.Clamp(n, 1, 2);
    }

    void Confirm()
    {
        if (_snapWait is not { } q || !_hashes.TryGetValue(q.T, out var h)) return;
        if (h != q.H || _desync >= 0 && _desync <= q.T) { _snapWait = null; return; }   // світ не той — геть
        if (System.Numerics.BitOperations.PopCount((uint)_votes.GetValueOrDefault(q.T)) < Need()) return;
        _snap = q;
        _snapWait = null;
    }

    /// <summary>Тик підтвердженого знімка (-1 — нема), для тестів.</summary>
    public int SnapTick => _snap?.T ?? -1;

    /// <summary>K62: стіл у зборі (годинник стоїть), для тестів.</summary>
    public bool Waiting => _wait;

    public override TickResult Tick()
    {
        var w = Mark();
        while (_keepFrom < _log.Count && _log[_keepFrom].T <= w - KeepTicks) _keepFrom++;
        var old = Ctx.Clock.UtcNow - TimeSpan.FromMilliseconds(ReactKeepMs);   // K28
        _reacts.RemoveAll(r => r.At < old);
        if (NowTick() % 100 == 0) Forget(Ctx.Clock.UtcNow);
        if (!_dirty) return TickResult.FrameOnly;
        _dirty = false;
        return TickResult.Both;
    }

    public override object? Frame() => new
    {
        e = _epoch, t = NowTick(), w = _w, n = _log.Count, f = _keepFrom, c = Flat(_keepFrom), d = _desync, wait = _wait ? 1 : 0,
        r = Reacts(),   // K28: [seq, p, e, i] × N — свіжі реакції
        pg = Pings(), pa = PingEcho(), sp = Watchers(),
    };

    /// <summary>K28: гравець rules p → пінг його браузера (мс) або -1 — «відпав» (місце мовчить довше за <see cref="LostMs"/>).</summary>
    int[] Pings()
    {
        var a = new int[_out.Count];
        var now = Ctx.Clock.UtcNow;
        for (var s = 0; s < _pOf.Length; s++)
        {
            var v = now - _seen[s] > TimeSpan.FromMilliseconds(LostMs) ? -1 : _rtt[s];
            if (_pOf[s] >= 0 && _pOf[s] < a.Length) a[_pOf[s]] = v;
            if (_p2Of[s] >= 0 && _p2Of[s] < a.Length) a[_p2Of[s]] = v;
        }
        return a;
    }

    /// <summary>K28: місце → [номер останнього пінгу, скільки мс він тут пролежав] — браузер віднімає це від свого заміру.</summary>
    int[] PingEcho()
    {
        var a = new int[_pOf.Length * 2];
        var now = Ctx.Clock.UtcNow;
        for (var s = 0; s < _pOf.Length; s++)
        {
            if (_pingI[s] == 0) continue;
            a[s * 2] = _pingI[s];
            a[s * 2 + 1] = (int)Math.Clamp((now - _pingAt[s]).TotalMilliseconds, 0, 60_000);
        }
        return a;
    }

    int[] Reacts()
    {
        var a = new int[_reacts.Count * 4];
        for (int k = 0, j = 0; k < _reacts.Count; k++, j += 4)
        {
            var r = _reacts[k];
            a[j] = r.Seq; a[j + 1] = r.P; a[j + 2] = r.E; a[j + 3] = r.I;
        }
        return a;
    }

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
        nm = Names(), wait = _wait ? 1 : 0, tp = Table(), tb = _tableBy, tv = _tableV,
    };

    /// <summary>
    /// K28: ніки гравців rules — p → нік місця (другий за тим самим ПК — «нік ²»); null — слот вільний (пішов).
    /// Їде у вид і в журнал: міняється лише разом із місцями, а мітки над гравцями — з нього.
    /// </summary>
    public string?[] Names()
    {
        var a = new string?[_out.Count];
        for (var s = 0; s < _pOf.Length; s++)
        {
            var nick = Ctx.NickOf(s);
            if (nick is null) continue;
            if (_pOf[s] >= 0 && _pOf[s] < a.Length) a[_pOf[s]] = nick;
            if (_p2Of[s] >= 0 && _p2Of[s] < a.Length) a[_p2Of[s]] = nick + " ²";
        }
        return a;
    }

    /// <summary>Журнал від запису <paramref name="from"/> — пізньому, після F5 чи діри в кадрах. Кличеться під замком кімнати.</summary>
    /// <remarks>
    /// K44: <paramref name="snap"/> (лише з from = 0) — якщо є підтверджений знімок, журнал не з нуля, а з першого запису
    /// з тиком ≥ тику знімка (<c>f</c>), і <c>s = {t, h, z, b}</c> — знімок (b — base64). Записи після f з тиком &lt; t
    /// (просились наперед раніше за інші) браузер пропускає: вони вже у знімку.
    /// </remarks>
    public object Log(int from, bool snap = false)
    {
        var s = snap && from == 0 ? _snap : null;
        if (s is not null) { from = 0; while (from < _log.Count && _log[from].T < s.T) from++; }
        return new
        {
            e = _epoch, seed = _seed, prm = Prm(), n0 = _n, t = NowTick(), w = Mark(), n = _log.Count, f = Math.Clamp(from, 0, _log.Count),
            c = Flat(from), pOf = _pOf.ToArray(), p2Of = _p2Of.ToArray(), d = _desync, nm = Names(), wait = _wait ? 1 : 0,
            tp = Table(), tb = _tableBy, tv = _tableV,
            s = s is null ? null : new { t = s.T, h = s.H, z = s.Z ? 1 : 0, b = s.B },
        };
    }

    JsonElement Prm()
    {
        using var doc = JsonDocument.Parse(_params);
        return doc.RootElement.Clone();
    }

    JsonElement Table()
    {
        using var doc = JsonDocument.Parse(_table);
        return doc.RootElement.Clone();
    }

    /// <summary>Налаштування для тесту: що лежить зараз.</summary>
    public string TableJson => _table;

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

    static bool HasParams(JsonElement p) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("params", out _);

    static string? ReadMode(JsonElement p) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

    /// <summary>mode партії (навчання чи ні) — щоб перебудова світу в зборі не вивела з навчання.</summary>
    static string? ModeOf(string prm) =>
        JsonNode.Parse(prm) is JsonObject o && o["mode"] is JsonValue v && v.TryGetValue<string>(out var m) ? m : null;

    /// <summary>Налаштування столу + mode партії: лише 'tutorial' щось важить (звичайна смуга — без ключа).</summary>
    static string WithMode(string table, string? mode)
    {
        var o = JsonNode.Parse(table) as JsonObject ?? [];
        o.Remove("mode");
        if (mode == "tutorial") o["mode"] = "tutorial";
        return o.ToJsonString();
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
