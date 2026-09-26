using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Вогник і Крапля» (specs/vohnyk.md): кооп-платформер на двох. Одна партія — один рівень із п'ятнадцяти. Тут —
/// фази, вибір рівня в лобі, журнал вводу з перемотуванням, вид і кадр, вихід партнера, запис прогресу. Сама фізика —
/// у <see cref="VohnykWorld"/>, і вона ж, слово в слово, крутиться в браузері (web/games/vohnyk-sim.js): свій герой
/// рухається миттєво, а сервер лишається суддею.
/// <para>
/// Час тут — кроки по 20 мс, по два на тик. Лічильник кроків <see cref="_s"/> росте у всіх фазах від старту, а світ
/// рухається лише в «go»: так ввід, натиснутий на відліку, лягає в журнал на свій крок і діє з першого кроку гри.
/// </para>
/// </summary>
public sealed class Vohnyk : Game
{
    public const int StepMs = 20, StepsPerTick = 2;
    public const int ReadySteps = 100, DeadSteps = 30, ClearSteps = 60;
    /// <summary>Наскільки далеко в минуле сервер перемотує пізній ввід (500 мс) і наскільки наперед приймає (120 мс).</summary>
    public const int Rewind = 25, Future = 6;
    public const int JournalSize = 64;
    /// <summary>Кадр раз на секунду, навіть коли нічого не рухається, — щоб клієнт знав, що зв'язок живий.</summary>
    public const int KeepaliveTicks = 25;

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

    VohnykLevel? _level;
    VohnykWorld? _world;
    int _s, _pt, _t, _deaths, _goFrom, _cause, _active;
    bool _solo;
    object? _result;

    // знімки світу після кожного кроку: кільце на Rewind + 1 кроків, виділяється раз на Start
    int[][] _snap = [];
    // журнал вводу на героя: (крок, k), упорядкований за кроком
    readonly int[][] _jStep = [new int[JournalSize], new int[JournalSize]];
    readonly int[][] _jK = [new int[JournalSize], new int[JournalSize]];
    readonly int[] _jCount = new int[2];
    readonly int[] _ack = new int[2];
    // що бачив клієнт востаннє: кадр шлемо лише на зміну (плюс keepalive)
    uint _sentHash;
    int _sentPhase = int.MinValue, _sentActive = -1, _ticks;

    static readonly string[] SeatNames = ["Вогник", "Крапля"];

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

    // ============================================================================================
    // Старт
    // ============================================================================================

    public override void Start()
    {
        var n = Picked();
        _level = VohnykLevels.Get(n);
        _world = new VohnykWorld(_level);
        _solo = Ctx.Players <= 1;
        _active = 0;
        for (var s = 0; s < 2; s++)
            if (_solo && Ctx.Seated(s)) { _active = s; break; }
        _phase = PhReady;
        _s = 0;
        _pt = ReadySteps;
        _goFrom = ReadySteps + 1;
        _t = 0;
        _deaths = 0;
        _cause = CauseNone;
        _result = null;
        _jCount[0] = _jCount[1] = 0;
        _ack[0] = _ack[1] = 0;
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

    ActResult Pick(int seat, JsonElement payload)
    {
        if (InPlay) return ActResult.Fail("Партія вже йде");
        if (Ctx.HostSeat != seat) return ActResult.Fail("Рівень обирає господар столу");
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("level", out var lv)
            || lv.ValueKind != JsonValueKind.Number || !lv.TryGetInt32(out var n) || n is < 1 or > VohnykLevels.Count)
            return ActResult.Fail("Такого рівня нема");
        if (!Unlocked(n)) return ActResult.Fail($"Рівень {n} ще зачинений: спершу пройдіть {n - 1}");
        _picked = n;
        _explicitPick = true;
        return ActResult.Done;
    }

    static bool Int(JsonElement o, string name, out int v)
    {
        v = 0;
        return o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out v);
    }

    /// <summary>
    /// Ввід героя: {n — крок клієнта, c — герой, k — утримуване}. Лягає в журнал на свій крок; пізній (крок уже минув)
    /// у фазі go — перемотування від знімка, ранній — чекає свого кроку.
    /// </summary>
    void Input(int seat, JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !Int(p, "n", out var n) || !Int(p, "c", out var c) || !Int(p, "k", out var k)
            || c is not (0 or 1) || k is < 0 or > 7)
            throw new GameError("Кривий ввід");
        if (!_solo && c != seat) throw new GameError("Це не твій герой");
        if (!InPlay || _world is null) return;
        if (_solo) _active = c;
        if (n < _s - Rewind + 1) n = _s - Rewind + 1;
        if (n > _s + Future) n = _s + Future;
        if (n < 1) n = 1;
        if (!Record(c, n, k)) return;
        Ack(c);
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

    /// <summary>Утримуване героєм c на кроці s: останній запис журналу з кроком ≤ s.</summary>
    int KAt(int c, int s)
    {
        var steps = _jStep[c];
        for (var i = _jCount[c] - 1; i >= 0; i--)
            if (steps[i] <= s) return _jK[c][i];
        return 0;
    }

    void Ack(int c)
    {
        var steps = _jStep[c];
        for (var i = _jCount[c] - 1; i >= 0; i--)
            if (steps[i] <= _s) { _ack[c] = steps[i]; return; }
    }

    int[] Snap(int step) => _snap[((step % _snap.Length) + _snap.Length) % _snap.Length];

    /// <summary>
    /// Перемотати від кроку n до поточного з журналом, що вже знає пізній ввід. Правда сервера тут трохи добріша за
    /// фізику: самоцвіт назад не забирається, а смерть чи «пройдено», що трапились у повторі, стаються зараз, а не в минулому.
    /// </summary>
    void Replay(int n)
    {
        var w = _world!;
        if (n < _goFrom) n = _goFrom;
        if (n < _s - Rewind + 1) n = _s - Rewind + 1;
        if (n > _s) return;
        var gemsBefore = w.Gems;
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
        if ((w.Gems | gemsBefore) != w.Gems)
        {
            w.Gems |= gemsBefore;
            w.Save(Snap(_s));
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
        var view = false;
        for (var i = 0; i < StepsPerTick && InPlay; i++)
        {
            var was = _phase;
            Advance();
            if (_phase != was && (_phase is PhClear or PhOver || was == PhReady)) view = true;
        }
        _ticks++;
        if (_phase == PhOver) return TickResult.Both;
        var h = _world.Hash();
        var frame = h != _sentHash || _phase != _sentPhase || _active != _sentActive || _ticks % KeepaliveTicks == 0;
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
        _result = new
        {
            cleared = true, level = lv.N, ms, deaths = _deaths, stars, gems = Bits(w.Gems), gemsAll = lv.Gems.Length,
            nicks = names, next,
        };
        _phase = PhOver;
        _picked = next;
        _explicitPick = true;
        Store.Record(nicks, lv.N, ms, _deaths, stars, Ctx.Clock.UtcNow);
        var scores = new Dictionary<int, long>();
        for (var s = 0; s < 2; s++)
            if (Ctx.Seated(s)) scores[s] = ms;
        var who = nicks.Count == 1 ? $"{names} за двох:" : $"{names} пройшли";
        Ctx.Finish([], $"{Info.Title}: {who} рівень {lv.N} «{lv.Name}» за {Clock(ms)} {Stars(stars)}", scores);

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
        _result = new
        {
            cleared = false, level = lv.N, ms = _t * StepMs, deaths = _deaths, stars = 0, gems = Bits(_world!.Gems),
            gemsAll = lv.Gems.Length, nicks = names, next = lv.N,
        };
        _phase = PhOver;
        _picked = lv.N;
        _explicitPick = true;
        var verb = nicks.Count == 1 ? "відступає" : "відступають";
        Ctx.Finish([], $"{Info.Title}: {names} {verb} перед рівнем {lv.N} «{lv.Name}»");
    }

    /// <summary>
    /// Хтось устав посеред рівня. Удвох — той, хто лишився, бере обох героїв, і партія триває; сам — кінець.
    /// Встали на «Разом!» — рівень таки пройдено, записуємо.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (InPlay && seat is 0 or 1)
        {
            var other = 1 - seat;
            if (Ctx.Seated(other))
            {
                if (!_solo)
                {
                    _solo = true;
                    Ctx.Log($"{Info.Title}: {Ctx.NickOf(seat)} встав з-за столу — {Ctx.NickOf(other)} веде обох");
                }
                _active = other;
                // герой того, хто пішов, відпускає клавіші — інакше біг би в стіну, доки його не підхоплять
                if (Record(seat, _s + 1, 0)) Ack(seat);
                return;
            }
            if (_phase == PhClear)
            {
                FinishCleared();
                return;
            }
            _phase = PhOver;
            _picked = _level?.N ?? _picked;
            _explicitPick = true;
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
            dc = _cause,
            ack = new[] { _ack[0], _ack[1] },
            w,
        };
    }

    public override object View(int? seat)
    {
        var playing = InPlay || _phase == PhOver;
        var shown = playing && _level is not null ? _level : VohnykLevels.Get(Picked());
        var world = playing ? _world : null;
        return new
        {
            turn = (int?)null,
            phase = PhaseName(_phase),
            solo = _solo,
            active = _active,
            picked = playing && _phase != PhOver ? shown.N : Picked(),
            levels = LevelList(),
            level = Static(shown),
            run = new
            {
                t = playing ? _t : 0,
                deaths = playing ? _deaths : 0,
                gems = world is null ? 0 : Bits(world.Gems),
                gemsAll = shown.Gems.Length,
            },
            result = _result,
            f = InPlay ? Frame() : null,
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
