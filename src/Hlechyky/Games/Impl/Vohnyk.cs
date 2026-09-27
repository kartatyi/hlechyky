using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Вогник і Крапля» (specs/vohnyk.md): кооп-платформер на двох. Одна партія — один рівень із п'ятнадцяти. Тут —
/// фази, вибір рівня в лобі, журнал вводу з перемотуванням, вид і кадр, вихід партнера, запис прогресу. Сама фізика —
/// у <see cref="VohnykWorld"/>, і вона ж, слово в слово, крутиться в браузері (web/games/vohnyk-sim.js): свій герой
/// рухається миттєво, а сервер лишається суддею.
/// <para>
/// Час тут — кроки по 20 мс. Лічильник кроків <see cref="_s"/> росте у всіх фазах від старту й іде за справжнім
/// годинником (а не за тиками: таймер Windows тикає раз на ~48 мс замість 40), а світ рухається лише в «go»: так
/// ввід, натиснутий на відліку, лягає в журнал на свій крок і діє з першого кроку гри.
/// </para>
/// </summary>
public sealed class Vohnyk : Game
{
    public const int StepMs = 20, StepsPerTick = 2;
    public const int ReadySteps = 100, DeadSteps = 30, ClearSteps = 60;
    /// <summary>Наскільки далеко в минуле сервер перемотує пізній ввід (500 мс) і наскільки наперед приймає (200 мс: клієнт іде на 4 кроки попереду й має запас ±3 на похибку свого годинника).</summary>
    public const int Rewind = 25, Future = 10;
    public const int JournalSize = 64;
    /// <summary>Кадр раз на секунду, навіть коли нічого не рухається, — щоб клієнт знав, що зв'язок живий.</summary>
    public const int KeepaliveTicks = 25;
    /// <summary>
    /// Скільки кроків без жодного вводу від героя, що тримає клавішу, — і сервер її відпускає. Клієнт, поки тримає
    /// клавішу, нагадує про неї раз на 25 кроків, тож мовчить так довго лише той, хто зник (F5, закритий ноут, телефон
    /// без мережі), — і його герой не бігає в воду знову й знову, скидаючи рівень партнерові. Спізніле нагадування
    /// живого клієнта лягає на крок відпускання й перемотує, ніби нічого й не було.
    /// </summary>
    public const int StaleSteps = 75;
    /// <summary>Скільки кроків максимум надолужуємо за один тик, якщо сервер пригальмував (решту часу просто відпускаємо).</summary>
    public const int MaxCatchUp = 6;

    /// <summary>Фази: pick — лобі, ready — відлік, go — гра, dead — смерть і скидання, clear — «Разом!», over — кінець.</summary>
    public const int PhPick = -1, PhReady = 0, PhGo = 1, PhDead = 2, PhClear = 3, PhOver = 4;

    /// <summary>Чому скидали рівень (кадр dc): для напису на полотні.</summary>
    public const int CauseNone = 0, CauseFireWater = 1, CauseWaterLava = 2, CauseMud = 3, CauseBoth = 4, CauseReset = 5;

    public override GameInfo Info { get; } = new(
        "vohnyk", "Вогник і Крапля", "«Вогника і Краплю»", GameGroup.Live, 1, 2, TickMs: StepMs * StepsPerTick,
        Start: StartMode.ByHost,
        Hint: "Кооп-платформер на двох: Вогник боїться води, Крапля — лави, обоє — болота. Кнопки, важелі, скрині, самоцвіти й двоє дверей. Сам — керуєш обома");

    VohnykStore _store = null!;
    int _phase = PhPick;
    int _picked = 1;
    bool _explicitPick;
    /// <summary>На якому раунді каркаса партія скінчилась: раунд змінився без Start — стіл відкрили наново (лобі).</summary>
    int _overRound = -1;

    VohnykLevel? _level;
    VohnykWorld? _world;
    int _s, _pt, _t, _deaths, _goFrom, _cause, _active;
    /// <summary>Номер партії (рівня) за цим столом — у виді й кадрі, щоб клієнт не взяв кадр минулої спроби за свіжий.</summary>
    int _game;
    bool _solo;
    object? _result;
    /// <summary>Хто скільки разів загинув (0 — Вогник, 1 — Крапля) — для підсумку.</summary>
    readonly int[] _deathsBy = new int[2];
    /// <summary>Склад на старті (ключ пари): змінився посеред рівня — рекорд пари не пишемо, бо час не цієї пари.</summary>
    string _crew = "";
    /// <summary>Вид треба розіслати з найближчого тика (дія посеред партії, яку каркас сам не розсилає).</summary>
    bool _viewDirty;
    /// <summary>Від цього моменту рахуються кроки: крок n настає через n × 20 мс.</summary>
    DateTimeOffset _t0;

    // знімки світу після кожного кроку: кільце на Rewind + 1 кроків, виділяється раз на Start
    int[][] _snap = [];
    // журнал вводу на героя: (крок, k), упорядкований за кроком
    readonly int[][] _jStep = [new int[JournalSize], new int[JournalSize]];
    readonly int[][] _jK = [new int[JournalSize], new int[JournalSize]];
    readonly int[] _jCount = new int[2];
    /// <summary>Крок останнього прийнятого вводу героя: минуле, раніше за нього, уже не переписати.</summary>
    readonly int[] _lastN = new int[2];
    /// <summary>На якому кроці сервера від героя востаннє щось прийшло (будь-який ввід, і нагадування теж).</summary>
    readonly int[] _heard = new int[2];
    // що бачив клієнт востаннє: кадр шлемо лише на зміну (плюс keepalive)
    uint _sentHash;
    int _sentPhase = int.MinValue, _sentActive = -1, _ticks;

    static readonly string[] SeatNames = ["Вогник", "Крапля"];

    // ---------- лише читання: для тестів і заміру швидкодії ----------
    public VohnykWorld? World => _world;
    public int StepNo => _s;
    public int PhaseNo => _phase;
    public int ClockSteps => _t;
    public int Deaths => _deaths;
    public bool SoloMode => _solo;
    public int ActiveHero => _active;
    public int LevelNo => _level?.N ?? 0;
    public int GameNo => _game;
    public int AckOf(int c) => Ack(c);
    public int KeysAt(int c, int step) => KAt(c, step);
    public VohnykStore StoreService => Store;

    public override string SeatName(int seat) => seat is 0 or 1 ? SeatNames[seat] : base.SeatName(seat);

    public override bool ActsInLobby => true;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        if (!VohnykLevels.Available) throw new GameError("Рівні не знайдено");
        _store = Ctx.Services.GetService<VohnykStore>() ?? new VohnykStore(null);
    }

    VohnykStore Store => _store ??= Ctx.Services.GetService<VohnykStore>() ?? new VohnykStore(null);

    // ============================================================================================
    // Хто за столом і який рівень
    // ============================================================================================

    List<string> Nicks()
    {
        var list = new List<string>(2);
        for (var s = 0; s < 2; s++)
            if (Ctx.NickOf(s) is { } n) list.Add(n);
        return list;
    }

    List<string> Keys() => [.. Nicks().Select(VohnykStore.Key)];

    bool Unlocked(int n) => Store.Unlocked(Keys(), n);

    /// <summary>Типовий вибір: найнижчий відчинений рівень, якого господар ще не проходив; усе пройдено — п'ятнадцятий.</summary>
    int DefaultLevel()
    {
        var host = Ctx.HostSeat is { } hs && Ctx.NickOf(hs) is { } hn ? hn : Nicks().FirstOrDefault();
        var key = host is null ? "" : VohnykStore.Key(host);
        var keys = Keys();
        for (var n = 1; n <= VohnykLevels.Count; n++)
            if (Store.Unlocked(keys, n) && Store.Stars(key, n) == 0) return n;
        return VohnykLevels.Count;
    }

    /// <summary>Рівень, який зараз обрано в лобі (явний вибір — якщо він ще відчинений для цього складу).</summary>
    int Picked() => _explicitPick && Unlocked(_picked) ? _picked : DefaultLevel();

    bool InPlay => _phase is PhReady or PhGo or PhDead or PhClear;

    /// <summary>
    /// Партію дограли, а потім на вільне місце хтось сів — каркас відкрив стіл наново (раунд +1), а Start ще не
    /// було. Це знову лобі: вид має показувати мапу й прев'ю обраного рівня, а не підсумок минулої партії.
    /// </summary>
    bool Reopened => _phase == PhOver && Ctx.Round != _overRound;

    /// <summary>Фаза, як її бачить стіл (відкритий наново стіл — це вже лобі).</summary>
    int Phase => Reopened ? PhPick : _phase;

    // ============================================================================================
    // Старт
    // ============================================================================================

    public override void Start()
    {
        _solo = Ctx.Players <= 1;
        _active = 0;
        for (var s = 0; s < 2; s++)
            if (_solo && Ctx.Seated(s)) { _active = s; break; }
        _crew = VohnykStore.PairKey(Nicks());
        StartLevel(Picked());
    }

    /// <summary>Рівень n з нуля: відлік, порожній журнал, знімки. Кличеться зі Start і коли на відліку обрали інший рівень.</summary>
    void StartLevel(int n)
    {
        _level = VohnykLevels.Get(n);
        _world = new VohnykWorld(_level) { Solo = _solo };
        _game++;
        _phase = PhReady;
        _s = 0;
        _t0 = Ctx.Clock.UtcNow;
        _pt = ReadySteps;
        _goFrom = ReadySteps + 1;
        _t = 0;
        _deaths = 0;
        _deathsBy[0] = _deathsBy[1] = 0;
        _cause = CauseNone;
        _result = null;
        _jCount[0] = _jCount[1] = 0;
        _lastN[0] = _lastN[1] = 0;
        _heard[0] = _heard[1] = 0;
        var len = _world.StateLength;
        _snap = new int[Rewind + 1][];
        for (var i = 0; i < _snap.Length; i++)
        {
            _snap[i] = new int[len];
            _world.Save(_snap[i]);
        }
        _sentHash = 0;
        _sentPhase = int.MinValue;
        _sentActive = -1;
        _ticks = 0;
    }

    // ============================================================================================
    // Дії
    // ============================================================================================

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        switch (action)
        {
            case "in":
                Input(seat, payload);
                return ActResult.Done;
            case "pick":
                return Pick(seat, payload);
            case "reset":
                if (_phase != PhGo) return ActResult.Fail("Зараз не можна");
                EnterDead(CauseReset);
                return ActResult.Done;
            case "giveup":
                if (_phase is not (PhReady or PhGo or PhDead)) return ActResult.Fail("Зараз не можна");
                GiveUp();
                return ActResult.Done;
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    /// <summary>
    /// Вибір рівня. У лобі — господар (мапа під полотном). На відліку нової партії — будь-хто з сидячих: так
    /// після кінця партії можна одразу взяти інший рівень (модуль тисне «Ще раз» каркаса і тут же обирає рівень),
    /// а не вставати з-за столу заради мапи. Далі, коли рівень уже йде, — ні.
    /// </summary>
    ActResult Pick(int seat, JsonElement payload)
    {
        var phase = Phase;
        if (phase is PhGo or PhDead or PhClear) return ActResult.Fail("Партія вже йде");
        if (phase != PhReady && Ctx.HostSeat != seat) return ActResult.Fail("Рівень обирає господар столу");
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("level", out var lv)
            || lv.ValueKind != JsonValueKind.Number || !lv.TryGetInt32(out var n) || n is < 1 or > VohnykLevels.Count)
            return ActResult.Fail("Такого рівня нема");
        if (!Unlocked(n)) return ActResult.Fail($"Рівень {n} ще зачинений: спершу пройдіть {n - 1}");
        _picked = n;
        _explicitPick = true;
        if (phase == PhReady)
        {
            if (_level?.N != n) StartLevel(n);
            _viewDirty = true;
            return ActResult.Done;
        }
        // стіл відкрили наново — підсумок минулої партії вже нікому не потрібен
        _phase = PhPick;
        _result = null;
        return ActResult.Done;
    }

    static bool Int(JsonElement o, string name, out int v)
    {
        v = 0;
        return o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out v);
    }

    /// <summary>
    /// Ввід героя: {n — крок клієнта, c — герой, k — утримуване}. Лягає в журнал на свій крок; пізній (крок уже минув)
    /// у фазі go — перемотування від знімка, ранній — чекає свого кроку. Переписати вже надіслане не можна: ввід
    /// лягає не раніше за попередній від того ж героя (чесний клієнт і так шле кроки по зростанню).
    /// </summary>
    void Input(int seat, JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !Int(p, "n", out var n) || !Int(p, "c", out var c) || !Int(p, "k", out var k)
            || c is not (0 or 1) || k is < 0 or > 7)
            throw new GameError("Кривий ввід");
        if (!_solo && c != seat) throw new GameError("Це не твій герой");
        if (!InPlay || _world is null) return;
        if (_solo) _active = c;
        _heard[c] = _s;
        if (n < _s - Rewind + 1) n = _s - Rewind + 1;
        if (n > _s + Future) n = _s + Future;
        if (n < _lastN[c]) n = _lastN[c];
        if (n < 1) n = 1;
        _lastN[c] = n;
        if (!Record(c, n, k)) return;
        if (_phase == PhGo && n <= _s) Replay(n);
    }

    /// <summary>Записати (n, k) у журнал героя. Новіший ввід переписує все, що було задумано на пізніше. false — нічого не змінилось.</summary>
    bool Record(int c, int n, int k)
    {
        var steps = _jStep[c];
        var ks = _jK[c];
        var cnt = _jCount[c];
        if (cnt > 0 && steps[cnt - 1] == n && ks[cnt - 1] == k) return false;   // дубль
        var dropped = false;
        while (cnt > 0 && steps[cnt - 1] >= n) { cnt--; dropped = true; }
        var prev = cnt > 0 ? ks[cnt - 1] : 0;
        if (prev != k)
        {
            if (cnt == JournalSize)
            {
                Array.Copy(steps, 1, steps, 0, JournalSize - 1);
                Array.Copy(ks, 1, ks, 0, JournalSize - 1);
                cnt--;
            }
            steps[cnt] = n;
            ks[cnt] = k;
            cnt++;
        }
        _jCount[c] = cnt;
        return dropped || prev != k;
    }

    /// <summary>Сервер сам відпускає клавіші героя з кроку n (вийшов, зник): журнал і межа «минулого» — разом.</summary>
    void Release(int c, int n)
    {
        Record(c, n, 0);
        if (_lastN[c] < n) _lastN[c] = n;
    }

    /// <summary>Утримуване героєм c на кроці s: останній запис журналу з кроком ≤ s.</summary>
    int KAt(int c, int s)
    {
        var steps = _jStep[c];
        for (var i = _jCount[c] - 1; i >= 0; i--)
            if (steps[i] <= s) return _jK[c][i];
        return 0;
    }

    /// <summary>Останній уже застосований ввід героя c: найбільший крок запису журналу, що не пізніший за поточний.</summary>
    int Ack(int c)
    {
        var steps = _jStep[c];
        for (var i = _jCount[c] - 1; i >= 0; i--)
            if (steps[i] <= _s) return steps[i];
        return 0;
    }

    int[] Snap(int step) => _snap[((step % _snap.Length) + _snap.Length) % _snap.Length];

    /// <summary>
    /// Перемотати від кроку n до поточного з журналом, що вже знає пізній ввід. Правдою стає нова гілка цілком —
    /// разом із самоцвітами (інакше «зонд» у минуле збирав би самоцвіт, до якого герой так і не дійшов). Лише смерть
    /// чи «пройдено», що трапились у повторі, стаються зараз, а не в минулому: фазу назад не відкрутиш.
    /// </summary>
    void Replay(int n)
    {
        var w = _world!;
        if (n < _goFrom) n = _goFrom;
        if (n < _s - Rewind + 1) n = _s - Rewind + 1;
        if (n > _s) return;
        w.Load(Snap(n - 1));
        var frozen = false;
        for (var s = n; s <= _s; s++)
        {
            if (!frozen)
            {
                w.Step(KAt(0, s), KAt(1, s));
                frozen = w.AnyDied || w.Cleared != 0;
            }
            w.Save(Snap(s));
        }
        if (w.AnyDied) EnterDead(DeathCause());
        else if (w.Cleared != 0) EnterClear();
    }

    int DeathCause()
    {
        var w = _world!;
        if (w.Died[0] != 0 && w.Died[1] != 0) return CauseBoth;
        var who = w.Died[0] != 0 ? 0 : 1;
        var tile = w.DeathTile(who);
        return tile == VohnykLevel.Mud ? CauseMud : who == 0 ? CauseFireWater : CauseWaterLava;
    }

    void EnterDead(int cause)
    {
        _phase = PhDead;
        _pt = DeadSteps;
        _deaths++;
        _cause = cause;
        if (cause != CauseReset)
            for (var h = 0; h < 2; h++)
                if (_world!.Died[h] != 0) _deathsBy[h]++;
    }

    void EnterClear()
    {
        _phase = PhClear;
        _pt = ClearSteps;
    }

    // ============================================================================================
    // Тик
    // ============================================================================================

    public override TickResult Tick()
    {
        if (!InPlay || _world is null) return TickResult.None;
        var view = _viewDirty;
        _viewDirty = false;
        // Скільки кроків належить за справжнім годинником: тик каркаса на Windows приходить раз на ~48 мс, а не 40,
        // і з «два кроки на тик» гра йшла б на п'яту частину повільніше, а рекорди залежали б від таймера машини.
        var due = (long)((Ctx.Clock.UtcNow - _t0).TotalMilliseconds / StepMs);
        var steps = due - _s;
        if (steps > MaxCatchUp)
        {
            _t0 = _t0.AddMilliseconds((steps - MaxCatchUp) * StepMs);
            steps = MaxCatchUp;
        }
        for (var i = 0; i < steps && InPlay; i++)
        {
            var was = _phase;
            Advance();
            if (_phase != was && (_phase is PhClear or PhOver || was == PhReady)) view = true;
        }
        _ticks++;
        if (_phase == PhOver) return TickResult.Both;
        var h = _world.Hash();
        // Поза грою (відлік, смерть, «Разом!») кадр щотика: коротко, а клієнт саме тут звіряє годинник кроків — щоб
        // перший натиск після відліку ліг на свій крок, а не на обрізаний «наперед».
        var frame = h != _sentHash || _phase != _sentPhase || _active != _sentActive || _phase != PhGo || _ticks % KeepaliveTicks == 0;
        if (frame)
        {
            _sentHash = h;
            _sentPhase = _phase;
            _sentActive = _active;
        }
        return new TickResult(frame || view, view);
    }

    /// <summary>Один крок партії: фаза вирішує, чи рухається світ. Той самий автомат крутить і клієнт.</summary>
    void Advance()
    {
        var w = _world!;
        _s++;
        switch (_phase)
        {
            case PhReady:
                if (--_pt <= 0)
                {
                    _phase = PhGo;
                    _goFrom = _s + 1;
                    _pt = 0;
                }
                break;
            case PhGo:
                ReleaseStale();
                w.Step(KAt(0, _s), KAt(1, _s));
                _t++;
                if (w.AnyDied) EnterDead(DeathCause());
                else if (w.Cleared != 0) EnterClear();
                break;
            case PhDead:
                _t++;
                if (--_pt <= 0)
                {
                    w.Reset(keepGems: true);
                    _phase = PhGo;
                    _goFrom = _s + 1;
                    _pt = 0;
                    _cause = CauseNone;
                }
                break;
            case PhClear:
                if (--_pt <= 0)
                {
                    w.Save(Snap(_s));
                    FinishCleared();
                    return;
                }
                break;
        }
        w.Save(Snap(_s));
    }

    /// <summary>
    /// Герой тримає клавішу, а його гравець мовчить уже понад <see cref="StaleSteps"/> кроків (зник зі зв'язку) — клавішу
    /// відпускаємо з цього кроку: інакше він біг би в воду знову й знову, і рівень скидався б обом раз у раз.
    /// </summary>
    void ReleaseStale()
    {
        for (var c = 0; c < 2; c++)
            if (_s - _heard[c] > StaleSteps && KAt(c, _s) != 0) Release(c, _s);
    }

    // ============================================================================================
    // Кінець
    // ============================================================================================

    static string Clock(int ms)
    {
        var s = ms / 1000;
        return $"{s / 60}:{s % 60:00}";
    }

    static string Stars(int n) => new string('★', n) + new string('☆', 3 - n);

    static int Bits(int v) => System.Numerics.BitOperations.PopCount((uint)v);

    void EnterOver(int next)
    {
        _phase = PhOver;
        _overRound = Ctx.Round;
        _picked = next;
        _explicitPick = true;
    }

    object Result(bool cleared, int stars, int next)
    {
        var lv = _level!;
        return new
        {
            cleared, level = lv.N, ms = _t * StepMs, deaths = _deaths, deathsBy = new[] { _deathsBy[0], _deathsBy[1] }, stars,
            gems = Bits(_world!.Gems), gemsAll = lv.Gems.Length, nicks = VohnykStore.Names(Nicks()), next,
        };
    }

    void FinishCleared()
    {
        var w = _world!;
        var lv = _level!;
        var ms = _t * StepMs;
        var allGems = w.Gems == lv.AllGemsMask;
        var stars = 1 + (allGems ? 1 : 0) + (ms <= lv.Par ? 1 : 0);
        var nicks = Nicks();
        var names = VohnykStore.Names(nicks);
        var next = Math.Min(VohnykLevels.Count, lv.N + 1);
        _result = Result(true, stars, next);
        EnterOver(next);
        // Зірки й «пройдено» — усім, хто сидить (і тому, хто встає просто на «Разом!»). Рекорд пари — лише коли склад
        // той самий, що на старті: інакше час пари ліг би в соло-таблицю того, хто лишився догравати.
        Store.Record(nicks, lv.N, ms, _deaths, stars, Ctx.Clock.UtcNow, best: VohnykStore.PairKey(nicks) == _crew);
        var scores = new Dictionary<int, long>();
        for (var s = 0; s < 2; s++)
            if (Ctx.Seated(s)) scores[s] = ms;
        var line = nicks.Count == 1
            ? $"{names} за двох — рівень {lv.N} «{lv.Name}» пройдено за {Clock(ms)} {Stars(stars)}"
            : $"{names} пройшли рівень {lv.N} «{lv.Name}» за {Clock(ms)} {Stars(stars)}";
        Ctx.Finish([], $"{Info.Title}: {line}", scores);

        // ачівки: удвох різними ніками — «Вогонь і вода»; усі п'ятнадцять на три зірки — «Кришталева печера»
        var keys = Keys();
        var duo = keys.Distinct(StringComparer.Ordinal).Count() >= 2;
        for (var s = 0; s < 2; s++)
        {
            if (Ctx.NickOf(s) is not { } nick) continue;
            if (duo) Ctx.Award(s, 0, "ach:vohnyk-duo");
            if (Store.AllThreeStars(VohnykStore.Key(nick))) Ctx.Award(s, 0, "ach:vohnyk-cave");
        }
    }

    void GiveUp()
    {
        var lv = _level!;
        var nicks = Nicks();
        var names = VohnykStore.Names(nicks);
        _result = Result(false, 0, lv.N);
        EnterOver(lv.N);
        var line = nicks.Count == 1
            ? $"{names} відступає перед рівнем {lv.N} «{lv.Name}»"
            : $"{names} здались на рівні {lv.N} «{lv.Name}»";
        Ctx.Finish([], $"{Info.Title}: {line}");
    }

    /// <summary>
    /// Хтось устав посеред рівня. Удвох — той, хто лишився, бере обох героїв, і партія триває; сам — кінець.
    /// Встали на «Разом!» — рівень таки пройдено: зараховуємо зараз, поки той, хто встає, ще за столом.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (InPlay && seat is 0 or 1)
        {
            if (_phase == PhClear)
            {
                FinishCleared();
                return;
            }
            var other = 1 - seat;
            if (Ctx.Seated(other))
            {
                if (!_solo)
                {
                    _solo = true;
                    _world!.Solo = true;
                    Ctx.Log($"{Info.Title}: {Ctx.NickOf(seat)} встав з-за столу — {Ctx.NickOf(other)} веде обох");
                }
                _active = other;
                // герой того, хто пішов, відпускає клавіші — інакше біг би в стіну, доки його не підхоплять
                Release(seat, _s + 1);
                return;
            }
            _result = _level is null ? null : Result(false, 0, _level.N);
            EnterOver(_level?.N ?? _picked);
        }
        base.OnLeave(seat);
    }

    // ============================================================================================
    // Вид і кадр
    // ============================================================================================

    static string PhaseName(int ph) => ph switch
    {
        PhReady => "ready",
        PhGo => "go",
        PhDead => "dead",
        PhClear => "clear",
        PhOver => "over",
        _ => "pick",
    };

    public override object? Frame()
    {
        if (_world is null || _level is null) return null;
        var w = new int[_world.StateLength];
        _world.Save(w);
        return new
        {
            n = _s,
            ph = _phase is >= PhReady and <= PhClear ? _phase : PhClear,
            pt = _pt,
            t = _t,
            d = _deaths,
            h = (int)VohnykWorld.Hash(w, w.Length),
            a = _active,
            lv = _level.N,
            gi = _game,
            dc = _cause,
            ack = new[] { Ack(0), Ack(1) },
            w,
        };
    }

    public override object View(int? seat)
    {
        var phase = Phase;
        var over = phase == PhOver;
        var playing = InPlay || over;
        var shown = playing && _level is not null ? _level : VohnykLevels.Get(Picked());
        var world = playing ? _world : null;
        return new
        {
            turn = (int?)null,
            phase = PhaseName(phase),
            solo = _solo,
            active = _active,
            picked = playing && !over ? shown.N : Picked(),
            gi = _game,
            levels = LevelList(),
            level = Static(shown),
            run = new
            {
                t = playing ? _t : 0,
                deaths = playing ? _deaths : 0,
                gems = world is null ? 0 : Bits(world.Gems),
                gemsAll = shown.Gems.Length,
            },
            result = over ? _result : null,
            f = InPlay || (over && _world is not null) ? Frame() : null,   // після кінця — останній світ, щоб F5 бачив, де все скінчилось
        };
    }

    object[] LevelList()
    {
        var keys = new string?[2];
        for (var s = 0; s < 2; s++) keys[s] = Ctx.NickOf(s) is { } n ? VohnykStore.Key(n) : null;
        var crew = Keys();
        var pair = VohnykStore.PairKey(Nicks());
        var all = VohnykLevels.All;
        var list = new object[all.Length];
        for (var i = 0; i < all.Length; i++)
        {
            var l = all[i];
            var best = crew.Count > 0 ? Store.Best(pair, l.N) : null;
            list[i] = new
            {
                n = l.N,
                name = l.Name,
                par = l.Par,
                gems = new[] { l.GemsOf(0), l.GemsOf(1) },
                unlocked = Store.Unlocked(crew, l.N),
                stars = new int?[] { keys[0] is { } k0 ? Store.Stars(k0, l.N) : null, keys[1] is { } k1 ? Store.Stars(k1, l.N) : null },
                best = best is null ? null : new { ms = best.Ms, deaths = best.Deaths, stars = best.Stars, nicks = best.Nicks },
            };
        }
        return list;
    }

    /// <summary>Статика кожного рівня будується раз на процес: рівні незмінні, а вид летить на кожну подію.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<VohnykLevel, object> StaticCache = new();

    /// <summary>Статика рівня для клієнта — у тій самій формі, що й файл, але без solution і check.</summary>
    static object Static(VohnykLevel l) => StaticCache.GetValue(l, BuildStatic);

    static object BuildStatic(VohnykLevel l)
    {
        static string Who(int w) => w == 0 ? "fire" : "water";
        return new
        {
            n = l.N,
            name = l.Name,
            par = l.Par,
            w = l.W,
            h = l.H,
            rows = l.Rows,
            spawn = new { fire = new[] { l.Spawn[0].Col, l.Spawn[0].Row }, water = new[] { l.Spawn[1].Col, l.Spawn[1].Row } },
            gems = l.Gems.Select(g => new { who = Who(g.Who), at = new[] { g.Col, g.Row } }).ToArray(),
            exits = new { fire = new[] { l.Exits[0].Col, l.Exits[0].Row }, water = new[] { l.Exits[1].Col, l.Exits[1].Row } },
            buttons = l.Buttons.Select(b => new { id = b.Id, at = new[] { b.Col, b.Row } }).ToArray(),
            levers = l.Levers.Select(x => new { id = x.Id, at = new[] { x.Col, x.Row }, init = x.Init }).ToArray(),
            doors = l.Doors.Select(d => new { id = d.Id, at = new[] { d.Col, d.Row }, h = d.Tiles, by = d.By, mode = d.All ? "all" : "any", inv = d.Inv }).ToArray(),
            lifts = l.Lifts.Select(x => new { id = x.Id, at = new[] { x.Col, x.Row }, w = x.Tiles, to = new[] { x.ToCol, x.ToRow }, by = x.By, mode = x.All ? "all" : "any", inv = x.Inv }).ToArray(),
            boxes = l.Boxes.Select(b => new { at = new[] { b.Col, b.Row } }).ToArray(),
            hints = l.Hints.Select(h => new { at = new[] { h.Col, h.Row }, w = h.W, text = h.Text }).ToArray(),
        };
    }
}
