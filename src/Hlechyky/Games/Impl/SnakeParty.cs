using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Поле на двох–чотирьох: мотоцикли або змійки. Дуельне ядро змійки (<see cref="SnakeCore"/>) рухає рівно двох і
/// живе на фіксованому полі 26×18, а тут потрібні до чотирьох вершників, вибування посеред раунду і більше
/// поле для компанії. З проходу №3 на цьому ядрі їздять і мотоцикли вдвох (<see cref="TronGame"/>): правила
/// кроку ті самі клітинка в клітинку, а мапи, турбо, звуження й «хто кого підрізав» потрібні обом.
/// <para>
/// Вершники живуть на МІСЦЯХ (індекс = місце за столом), бо так їх бачить клієнт і каркас. Порожнє місце
/// — вершника нема зовсім: ні сліду, ні голови.
/// </para>
/// <para>
/// Зайнятість поля — сітка <see cref="Occ"/> (0 — вільно, −1 — стіна мапи чи звуження, s+1 — тіло місця s):
/// і перевірка «чи вріжусь» за O(1) без множин на кожен тик, і відповідь, ЧИЙ слід зупинив вершника.
/// </para>
/// </summary>
public sealed class ArenaCore
{
    public const int StartLen = 3;
    public const int MaxQueued = SnakeCore.MaxQueued;
    /// <summary>Турбо: 1 с подвійної швидкості (10 тиків по 100 мс), далі 5 с перезарядки.</summary>
    public const int TurboTicks = 10, TurboCool = 50;
    /// <summary>Скільки клітинок попереду старту мапа лишає вільними — щоб ніхто не влетів у колону на першому кроці.</summary>
    public const int SafeAhead = 6;

    /// <summary>Чим закінчився вершник: ще їде, стіна (край, мапа), свій слід, чужий слід, лоб у лоб, звуження.</summary>
    public const int CauseNone = 0, CauseWall = 1, CauseSelf = 2, CauseTrail = 3, CauseHead = 4, CauseSqueeze = 5;

    static readonly (int Dx, int Dy)[] Deltas = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    readonly Random _rng;
    readonly int _w, _h, _seats, _apples;
    readonly bool _tails;
    readonly Queue<int>[] _turns;
    // робочі масиви кроку — одні на все життя ядра, без алокацій на тик
    readonly int[] _next;
    readonly bool[] _ok, _grow, _ate, _mv, _dead;
    readonly List<int> _died = [];

    public ArenaCore(Random rng, int w, int h, int seats, bool tailShrinks, int apples, bool wrap = false)
    {
        _rng = rng;
        _w = w; _h = h; _seats = seats; _tails = tailShrinks; _apples = apples;
        Wrap = wrap;
        _turns = [.. Enumerable.Range(0, seats).Select(_ => new Queue<int>())];
        _next = new int[seats];
        _ok = new bool[seats]; _grow = new bool[seats]; _ate = new bool[seats]; _mv = new bool[seats]; _dead = new bool[seats];
        Bodies = [.. Enumerable.Range(0, seats).Select(_ => new List<int>())];
        Dirs = new int[seats];
        Present = new bool[seats];
        Alive = new bool[seats];
        Crash = [.. Enumerable.Repeat(-1, seats)];
        Killer = [.. Enumerable.Repeat(-1, seats)];
        Cause = new int[seats];
        Turbo = new int[seats];
        Cool = new int[seats];
        Mid = [.. Enumerable.Repeat(-1, seats)];
        Occ = new sbyte[w * h];
    }

    public int W => _w;
    public int H => _h;
    public int Seats => _seats;
    public bool TailShrinks => _tails;
    /// <summary>«Тор»: стін по краю нема — виїхав праворуч, заїхав ліворуч.</summary>
    public bool Wrap { get; }
    /// <summary>Чи можна вмикати турбо (опція столу).</summary>
    public bool TurboOn { get; set; }
    /// <summary>Команда кожного місця (−1 — сам за себе) або null — кожен сам. Слід напарника не смертельний.</summary>
    public int[]? Team { get; set; }

    /// <summary>Клітинки кожного вершника, голова перша. Порожнє місце або з'їдена смертю змійка — порожній список.</summary>
    public List<int>[] Bodies { get; }
    public int[] Dirs { get; }
    /// <summary>Чи є на цьому місці вершник у цьому раунді (сидів на старті).</summary>
    public bool[] Present { get; }
    public bool[] Alive { get; }
    /// <summary>Де вершник розбився (для хрестика на полі); -1 — ще їде або його тут нема.</summary>
    public int[] Crash { get; }
    /// <summary>Чий слід зупинив вершника (для «хто кого підрізав»); −1 — стіна, свій слід або ще їде.</summary>
    public int[] Killer { get; }
    /// <summary>Як саме розбився — <see cref="CauseNone"/> … <see cref="CauseSqueeze"/>.</summary>
    public int[] Cause { get; }
    /// <summary>Турбо: скільки тиків ще діє (0 — не діє).</summary>
    public int[] Turbo { get; }
    /// <summary>Перезарядка турбо: скільки тиків до готовності.</summary>
    public int[] Cool { get; }
    /// <summary>Клітинка першого з двох кроків турбо цього тика (−1 — крок був один): дельта-кадр несе й її.</summary>
    public int[] Mid { get; }
    /// <summary>Зайнятість: 0 — вільно, −1 — стіна, s+1 — тіло місця s.</summary>
    public sbyte[] Occ { get; }
    /// <summary>Стіни мапи (колони, хрест) — для виду; кільця звуження клієнт рахує сам із <see cref="Ring"/>.</summary>
    public List<int> Walls { get; } = [];
    /// <summary>Скільки кілець краю вже обросло стіною (звужене поле).</summary>
    public int Ring { get; private set; }
    /// <summary>Скільки кілець поле може обрости: посередині лишається смуга щонайменше шість клітинок.</summary>
    public int MaxRing => Math.Min(_w, _h) / 2 - 3;
    public List<int> Apples { get; } = [];
    public int StartIn { get; set; }

    public int AliveCount
    {
        get { var n = 0; for (var s = 0; s < _seats; s++) if (Alive[s]) n++; return n; }
    }

    public int Cell(int x, int y) => y * _w + x;

    /// <summary>
    /// Нова партія для тих, хто сидить. Стартові місця — за порядком серед присутніх, а не за номером
    /// місця: двоє завжди стартують так само, як у дуелі (ліворуч угорі й праворуч унизу), третій — згори,
    /// четвертий — знизу. Колони й ряди зсунуті так, щоб прямі траси ніде не перетинались в один і той самий
    /// тик: хто задумався на старті, не має влетіти в сусіда лоб у лоб через кілька кроків.
    /// <paramref name="blocks"/> — перешкоди мапи; брила, що зачепила б старт чи шість клітинок попереду нього,
    /// випадає цілком (половина колони виглядала б помилкою).
    /// </summary>
    public void Reset(IReadOnlyList<int> present, int startTicks, IReadOnlyList<int[]>? blocks = null)
    {
        Array.Clear(Occ);
        Walls.Clear();
        Ring = 0;
        for (var s = 0; s < _seats; s++)
        {
            Bodies[s].Clear();
            _turns[s].Clear();
            Present[s] = Alive[s] = false;
            Crash[s] = Killer[s] = Mid[s] = -1;
            Cause[s] = Turbo[s] = Cool[s] = Dirs[s] = 0;
        }
        var starts = Starts(present.Count);
        var lanes = new HashSet<int>();
        for (var i = 0; i < present.Count && i < starts.Length; i++)
        {
            var s = present[i];
            var (x, y, dir) = starts[i];
            var (dx, dy) = Deltas[dir];
            for (var k = 0; k < StartLen; k++) Bodies[s].Add(Cell(x - dx * k, y - dy * k));   // голова попереду
            for (var k = -StartLen; k <= SafeAhead; k++)
            {
                var (lx, ly) = (x + dx * k, y + dy * k);
                if (lx >= 0 && lx < _w && ly >= 0 && ly < _h) lanes.Add(Cell(lx, ly));
            }
            Dirs[s] = dir;
            Present[s] = Alive[s] = true;
        }
        if (blocks is not null)
            foreach (var block in blocks)
            {
                if (block.Any(lanes.Contains)) continue;
                foreach (var c in block)
                    if (Occ[c] == 0) { Occ[c] = -1; Walls.Add(c); }
            }
        for (var s = 0; s < _seats; s++)
            foreach (var c in Bodies[s]) Occ[c] = (sbyte)(s + 1);
        Apples.Clear();
        if (_apples > 0)
        {
            // перше яблуко — посередині, як у дуелі; решта — куди ляже з генератора
            var mid = Cell(_w / 2, _h / 2);
            if (Occ[mid] == 0) Apples.Add(mid);
            while (Apples.Count < _apples && PlaceApple()) { }
        }
        StartIn = startTicks;
    }

    /// <summary>
    /// Стартові голови (x, y, напрямок). Двоє — рівно дуель: ряди H/2−3 і H/2+3 від протилежних стін.
    /// Троє й четверо — ще згори і знизу, на колонках, зсунутих від центру, щоб прямі не зустрілись в один тик.
    /// </summary>
    public (int X, int Y, int Dir)[] Starts(int n)
    {
        var a = (StartLen, _h / 2 - 3, 0);
        var b = (_w - 1 - StartLen, _h / 2 + 3, 2);
        if (n <= 2) return [a, b];
        // на великому полі ряди розводимо трохи ширше, щоб у верхнього й нижнього було куди звернути
        a = (StartLen, _h / 2 - 4, 0);
        var c = (_w / 2 + 4, StartLen, 1);
        var d = (_w / 2 - 5, _h - 1 - StartLen, 3);
        return [a, b, c, d];
    }

    /// <summary>Поворот: розворот на 180° і повтор ігноруємо, наступний міряємо від останнього в черзі — як у дуелі.</summary>
    public void Turn(int seat, int dir)
    {
        if (dir is < 0 or > 3 || seat < 0 || seat >= _seats || !Alive[seat]) return;
        var queue = _turns[seat];
        if (queue.Count >= MaxQueued) return;
        var last = queue.Count > 0 ? queue.Last() : Dirs[seat];
        if (dir == last || (dir + 2) % 4 == last) return;
        queue.Enqueue(dir);
    }

    /// <summary>Чи чекає вершник на ще не зроблений поворот.</summary>
    public bool Turning(int seat) => _turns[seat].Count > 0;

    /// <summary>Турбо: лише живому, лише після відліку, лише коли не діє і перезаряджене.</summary>
    public bool Boost(int seat)
    {
        if (!TurboOn || seat < 0 || seat >= _seats || !Alive[seat] || StartIn > 0 || Turbo[seat] > 0 || Cool[seat] > 0) return false;
        Turbo[seat] = TurboTicks;
        return true;
    }

    /// <summary>
    /// Один тик: крок усіх живих, а в кого діє турбо — ще один. Правила кроку ті самі, що в дуелі: стіна,
    /// будь-яке тіло (хвіст, що цього тика звільняє клітинку, — не перешкода), голови в одну клітинку — гинуть
    /// обидва. Розбиті мотоцикли лишають слід стіною до кінця раунду; розбита змійка зникає з поля.
    /// Повертає місця, що загинули (список живе в ядрі — не тримай його між тиками).
    /// </summary>
    public List<int> Step()
    {
        _died.Clear();
        var boosted = false;
        for (var s = 0; s < _seats; s++)
        {
            Mid[s] = -1;
            if (Alive[s] && Turbo[s] > 0) boosted = true;
        }
        Move(turboOnly: false);
        if (boosted)
        {
            for (var s = 0; s < _seats; s++)
                if (Alive[s] && Turbo[s] > 0) Mid[s] = Bodies[s][0];
            Move(turboOnly: true);
        }
        for (var s = 0; s < _seats; s++)
        {
            if (Turbo[s] > 0) { if (--Turbo[s] == 0) Cool[s] = TurboCool; }
            else if (Cool[s] > 0) Cool[s]--;
        }
        return _died;
    }

    void Move(bool turboOnly)
    {
        for (var s = 0; s < _seats; s++)
        {
            _mv[s] = Alive[s] && (!turboOnly || Turbo[s] > 0);
            _dead[s] = false;
            if (!_mv[s]) continue;
            if (_turns[s].Count > 0) Dirs[s] = _turns[s].Dequeue();
            (_next[s], _ok[s]) = Ahead(Bodies[s][0], Dirs[s]);
            _ate[s] = _apples > 0 && _ok[s] && Apples.Contains(_next[s]);
            _grow[s] = _ate[s] || !_tails;
        }

        for (var s = 0; s < _seats; s++)
        {
            if (!_mv[s]) continue;
            if (!_ok[s]) { _dead[s] = true; Cause[s] = CauseWall; continue; }
            var n = _next[s];
            var v = Occ[n];
            if (v == -1) { _dead[s] = true; Cause[s] = CauseWall; }
            else if (v > 0)
            {
                var o = v - 1;
                var body = Bodies[o];
                var tailLeaves = _mv[o] && !_grow[o] && body.Count > 0 && body[^1] == n;
                var mate = o != s && Team is { } t && t[s] >= 0 && t[o] == t[s];
                if (!tailLeaves && !mate)
                {
                    _dead[s] = true;
                    Cause[s] = o == s ? CauseSelf : CauseTrail;
                    Killer[s] = o == s ? -1 : o;
                }
            }
            for (var o = 0; o < _seats; o++)
                if (o != s && _mv[o] && _ok[o] && _next[o] == n)
                {
                    if (!_dead[s]) { Cause[s] = CauseHead; Killer[s] = o; }
                    _dead[s] = true;
                }
        }

        var eaten = false;
        for (var s = 0; s < _seats; s++)
        {
            if (!_mv[s] || !_ok[s]) continue;
            var body = Bodies[s];
            if (!_grow[s])
            {
                var tail = body[^1];
                body.RemoveAt(body.Count - 1);
                if (Occ[tail] == s + 1) Occ[tail] = 0;
            }
            body.Insert(0, _next[s]);
            if (Occ[_next[s]] == 0) Occ[_next[s]] = (sbyte)(s + 1);
            if (_ate[s] && Apples.Remove(_next[s])) eaten = true;
        }
        for (var s = 0; s < _seats; s++)
            if (_dead[s])
            {
                Kill(s, _ok[s] ? _next[s] : Bodies[s][0]);
                _died.Add(s);
            }
        if (eaten || Apples.Count < _apples) while (Apples.Count < _apples && PlaceApple()) { }
    }

    /// <summary>Вершник вибув (врізався або встає з-за столу). Змійка при цьому зникає з поля, мотоцикл — ні.</summary>
    public void Kill(int seat, int at)
    {
        if (!Alive[seat]) return;
        Alive[seat] = false;
        Crash[seat] = at;
        Turbo[seat] = 0;
        _turns[seat].Clear();
        if (!_tails) return;
        foreach (var c in Bodies[seat])
            if (Occ[c] == seat + 1) Occ[c] = 0;
        Bodies[seat].Clear();
    }

    /// <summary>
    /// Звужене поле: ще одне кільце краю обростає стіною. Чия голова опинилась у кільці — того стіна
    /// наздогнала. Повертає місця, що загинули (той самий список, що й у <see cref="Step"/>).
    /// </summary>
    public List<int> Squeeze()
    {
        _died.Clear();
        if (Ring >= MaxRing) return _died;
        var k = Ring++;
        for (var y = k; y < _h - k; y++)
            for (var x = k; x < _w - k; x++)
            {
                // усередині кільця — лише два краї рядка, середину перестрибуємо
                if (y != k && y != _h - 1 - k && x != k && x != _w - 1 - k) { x = _w - 2 - k; continue; }
                var c = Cell(x, y);
                Occ[c] = -1;
                Apples.Remove(c);
            }
        for (var s = 0; s < _seats; s++)
        {
            if (!Alive[s]) continue;
            var head = Bodies[s][0];
            if (Occ[head] != -1) continue;
            Cause[s] = CauseSqueeze;
            Kill(s, head);
            _died.Add(s);
        }
        return _died;
    }

    public (int Cell, bool Ok) Ahead(int head, int dir)
    {
        var (dx, dy) = Deltas[dir];
        var (x, y) = (head % _w + dx, head / _w + dy);
        if (Wrap) return (Cell((x + _w) % _w, (y + _h) % _h), true);
        return x < 0 || x >= _w || y < 0 || y >= _h ? (head, false) : (Cell(x, y), true);
    }

    /// <summary>Нове яблуко на вільній клітинці. false — поле забите вщерть.</summary>
    public bool PlaceApple()
    {
        var free = 0;
        foreach (var v in Occ) if (v == 0) free++;
        if (free - Apples.Count <= 0) return false;
        int cell;
        do { cell = _rng.Next(_w * _h); } while (Occ[cell] != 0 || Apples.Contains(cell));
        Apples.Add(cell);
        return true;
    }
}

/// <summary>Мапи мотоциклів: перешкоди — брилами (колона, планка хреста), щоб старт міг викинути брилу цілком.</summary>
public static class ArenaMaps
{
    public const string Empty = "empty", Columns = "columns", Cross = "cross", Torus = "torus", Random = "random";
    /// <summary>З чого вибирає «випадкова»: порожнє поле тут не рахується — випадкову й обирають заради різноманіття.</summary>
    public static readonly string[] Pool = [Columns, Cross, Torus];

    public static GameOption Option => new("map", "Мапа",
        [(Empty, "порожнє поле"), (Columns, "🏛 колони"), (Cross, "➕ хрест"), (Torus, "🍩 тор: без стін по краю"), (Random, "🎲 випадкова щораунду")], Empty);

    public static string Parse(string? v) => v is Columns or Cross or Torus or Random ? v : Empty;

    /// <summary>
    /// Брили мапи. Колони — сітка 3×3 пар 2×2, дзеркальна відносно центру (ніхто не стартує ближче до колони,
    /// ніж суперник), а ряди колон обминають стартові ряди. Хрест — дві планки через центр.
    /// </summary>
    public static List<int[]> Blocks(string map, int w, int h)
    {
        var list = new List<int[]>();
        int C(int x, int y) => y * w + x;
        if (map == Columns)
        {
            int[] xs = [w / 5, w / 2 - 1, w - 2 - w / 5];
            int[] ys = [h / 5, h / 2 - 1, h - 2 - h / 5];
            foreach (var x in xs)
                foreach (var y in ys)
                    list.Add([C(x, y), C(x + 1, y), C(x, y + 1), C(x + 1, y + 1)]);
        }
        else if (map == Cross)
        {
            // планки завтовшки дві клітинки — інакше на парному полі хрест не стане симетричним
            var bar = new List<int>();
            for (var y = h / 2 - 1; y <= h / 2; y++)
                for (var x = w / 4; x < w - w / 4; x++) bar.Add(C(x, y));
            list.Add([.. bar]);
            var across = bar.ToHashSet();
            bar.Clear();
            for (var x = w / 2 - 1; x <= w / 2; x++)
                for (var y = h / 4; y < h - h / 4; y++)
                    if (!across.Contains(C(x, y))) bar.Add(C(x, y));
            list.Add([.. bar]);
        }
        return list;
    }
}

/// <summary>
/// Спільне для мотоциклів (удвох і гуртом) і «Змійок гуртом»: вибування посеред раунду, раунд бере останній, хто
/// лишився. Прохід №3 додав сюди те, що вмикається опціями столу (типово — вимкнене, як і було): серію раундів
/// «до 3/5 перемог» з автостартом, «хто кого підрізав», турбо, мапи, звужене поле, ботів на порожні місця й
/// команди 2×2. Змійки гуртом цих опцій поки не показують — у них усе як було.
/// </summary>
public abstract class ArenaGame : Game
{
    public const int Seats = 4;
    /// <summary>Поле для двох — те саме, що в дуелі.</summary>
    public const int SmallW = SnakeCore.W, SmallH = SnakeCore.H;
    /// <summary>Поле для компанії: на 26×18 четверо мотоциклів розбиваються за кілька секунд.</summary>
    public const int BigW = 34, BigH = 24;
    /// <summary>Табло між раундами серії: 2 с (20 тиків по 100 мс), потім новий раунд сам.</summary>
    public const int PauseTicks = 20;
    /// <summary>Відлік наступного раунду серії — коротший за перший: хто де стартує, вже всі знають.</summary>
    public const int NextStartTicks = 20;
    /// <summary>Звужене поле: після 40 с (400 кроків) край обростає кільцем кожні 3 с.</summary>
    public const int SqueezeAt = 400, SqueezeEvery = 30;

    public static readonly string[] BotNames = ["🤖 Залізяка", "🤖 Гайка", "🤖 Шуруп"];
    public static readonly string[] TeamNames = ["🔥 Жар", "❄️ Іній"];

    protected static GameOption FieldOption => new("field", "Поле",
        [("auto", "Під склад"), ("small", "Мале 26×18"), ("big", "Велике 34×24")], "auto");
    protected static GameOption SeriesOption => new("series", "Партія",
        [("1", "один раунд"), ("3", "до 3 перемог"), ("5", "до 5 перемог")], "1");
    protected static GameOption TurboOption => new("turbo", "Турбо",
        [("0", "без турбо"), ("1", "🚀 з турбо")], "0");
    protected static GameOption SqueezeOption => new("squeeze", "Край поля",
        [("0", "стоїть"), ("1", "🧱 звужується після 40 с")], "0");
    protected static GameOption BotsOption => new("bots", "Боти",
        [("0", "без ботів"), ("1", "+1 🤖"), ("2", "+2 🤖"), ("3", "+3 🤖")], "0");
    protected static GameOption TeamsOption => new("teams", "Грають",
        [("0", "кожен сам"), ("1", "🔥❄️ команди 2×2")], "0");

    /// <summary>true — змійки: хвіст іде за головою, є яблука; false — мотоцикли: слід не зникає.</summary>
    protected abstract bool Tails { get; }
    protected abstract int CountdownTicks { get; }
    /// <summary>Скільки кроків раунд може тривати, поки не спрацює страховка.</summary>
    public abstract int MaxMoves { get; set; }
    /// <summary>Підписи місць (рід — під назву вершника).</summary>
    protected abstract string[] Colors { get; }

    /// <summary>Скільки місць за столом: дуель — 2, гурт — 4.</summary>
    protected int N => Info.MaxPlayers;

    /// <summary>
    /// «🤖 + бот» (мотоцикли вдвох): стан виклику бота або null — гра садить ботів інакше (гурт — опцією «Боти»).
    /// Коли кликали й людина сама, на вільне місце сідає один бот рівня з опції столу.
    /// </summary>
    protected virtual SoloBot? Solo => null;
    /// <summary>У цій партії сидить бот, покликаний «🤖 + бот» (а не опцією гурту).</summary>
    bool _soloBot;

    ArenaCore? _core;
    string _field = "auto", _map = ArenaMaps.Empty, _roundMap = ArenaMaps.Empty;
    int _target = 1, _botsWanted;
    bool _turbo, _squeeze, _teamsOn;
    bool _started;
    int _moves;
    /// <summary>Рахунок вечора за іменами: «Ще раз» обертає місця, а перемоги мають їхати з людиною.</summary>
    readonly Dictionary<string, int> _wins = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Підрізи за іменами — стільки разів чийсь слід зупинив суперника (вечір, як і перемоги).</summary>
    readonly Dictionary<string, int> _cuts = new(StringComparer.OrdinalIgnoreCase);
    string[] _crew = [];
    /// <summary>Місце у раунді: 1 — переможець; null — ще їде або його нема.</summary>
    int?[] _place = [];
    /// <summary>Хто з місць — бот цієї партії (ім'я), null — людина або порожньо.</summary>
    string?[] _bots = [];
    /// <summary>Команда місця цієї партії (−1 — нема), null — кожен сам.</summary>
    int[]? _teams;
    /// <summary>Перемоги в раундах цієї серії (за місцями; серія живе в межах однієї партії каркаса).</summary>
    int[] _sw = [];
    int _round, _pause;
    /// <summary>Хто кого цього раунду: [жертва, чий слід (−1 — нічий), як] — для стрічки на полі.</summary>
    readonly List<int[]> _ko = [];
    /// <summary>Підрізи всієї партії — для рядка Журналу наприкінці (у серії Журнал бачить один рядок, а не п'ять).</summary>
    readonly List<(string Killer, string Victim)> _cutLog = [];
    /// <summary>null — раунд триває; "win" — є переможець(і), "draw" — нічия.</summary>
    string? _winner;
    int[] _winners = [];
    /// <summary>Партія дограна (Finish уже був) — на відміну від паузи між раундами серії.</summary>
    bool _over;
    readonly BotBrain _brain = new();

    public int Moves => _moves;
    /// <summary>Номер раунду в серії (з 1).</summary>
    public int RoundNo => _round;
    public int Target => _target;
    public bool Over => _over;
    /// <summary>Ядро поточного раунду — для тестів.</summary>
    public ArenaCore Arena => Core;
    /// <summary>Серія не тягнеться вічно: стільки раундів — і хто попереду, той і взяв.</summary>
    public int MaxRounds { get; set; } = 25;

    public override string SeatName(int seat) => seat >= 0 && seat < Colors.Length ? Colors[seat] : base.SeatName(seat);

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _field = options.TryGetValue("field", out var f) && f is "small" or "big" ? f : "auto";
        _target = options.GetValueOrDefault("series") switch { "3" => 3, "5" => 5, _ => 1 };
        _turbo = options.GetValueOrDefault("turbo") == "1";
        _squeeze = options.GetValueOrDefault("squeeze") == "1";
        _map = ArenaMaps.Parse(options.GetValueOrDefault("map"));
        _botsWanted = int.TryParse(options.GetValueOrDefault("bots"), out var b) ? Math.Clamp(b, 0, Seats - 1) : 0;
        _teamsOn = options.GetValueOrDefault("teams") == "1";
    }

    /// <summary>Чи може ця гра садити ботів (опція є лише в мотоциклах гуртом).</summary>
    bool BotsAllowed => Info.Options?.Any(o => o.Key == "bots") == true;

    /// <summary>Хто зараз сидить — у порядку місць.</summary>
    int[] Seated() => [.. Enumerable.Range(0, N).Where(Ctx.Seated)];

    (int W, int H) Size(int players) => _field switch
    {
        "small" => (SmallW, SmallH),
        "big" => (BigW, BigH),
        _ => players <= 2 ? (SmallW, SmallH) : (BigW, BigH),
    };

    int ApplesFor(int players) => !Tails ? 0 : players <= 2 ? 1 : 2;

    /// <summary>
    /// Поле є ще до старту: стіл, що чекає на гравців, показує, хто де стартує, і мапу. До «Почати» ядро щоразу
    /// будується під тих, хто зараз сидить (без генератора — яблука з'являться на старті), після — живе своє.
    /// </summary>
    ArenaCore Core
    {
        get
        {
            if (_core is not null && _started) return _core;
            var seated = Seated();
            var (w, h) = Size(seated.Length);
            var map = _map == ArenaMaps.Random ? ArenaMaps.Empty : _map;
            if (_core is null || _core.W != w || _core.H != h || _roundMap != map
                || !Enumerable.Range(0, N).All(i => _core.Present[i] == seated.Contains(i)))
            {
                _roundMap = map;
                _core = new ArenaCore(new Random(0), w, h, N, Tails, 0, map == ArenaMaps.Torus);
                _core.Reset(seated, CountdownTicks, ArenaMaps.Blocks(map, w, h));
                EnsureArrays();
            }
            return _core;
        }
    }

    void EnsureArrays()
    {
        if (_place.Length == N) return;
        _place = new int?[N];
        _bots = new string?[N];
        _sw = new int[N];
    }

    /// <summary>Ім'я місця: нік, бот або колір.</summary>
    protected string Name(int seat) => Ctx.NickOf(seat) ?? (seat < _bots.Length ? _bots[seat] : null) ?? SeatName(seat);

    bool HasBots => _bots.Any(b => b is not null);

    /// <summary>Бот на порожньому місці під час і після партії — каркас покаже його в списку й підсумку.</summary>
    public override string? SeatBot(int seat) => _started && seat >= 0 && seat < _bots.Length && !Ctx.Seated(seat) ? _bots[seat] : null;

    /// <summary>Місце бота «🤖 + бот»: у партії — де сидить, у лобі — куди сяде, якщо почати зараз; null — нема.</summary>
    protected int? SoloBotSeat()
    {
        if (_soloBot)
            for (var s = 0; s < N; s++) if (_bots[s] is not null && !Ctx.Seated(s)) return s;
        if (Solo is { } solo && solo.Active(Ctx, N))
            for (var s = 0; s < N; s++) if (!Ctx.Seated(s)) return s;
        return null;
    }

    public override void Start()
    {
        EnsureArrays();
        var seated = Seated();
        Array.Clear(_bots);
        _soloBot = false;
        if (Solo is { } solo && solo.Active(Ctx, N))
        {
            for (var s = 0; s < N; s++)
                if (!Ctx.Seated(s)) { _bots[s] = LiveBots.Name; _soloBot = true; break; }
        }
        else if (BotsAllowed)
        {
            // сам за столом — один бот навіть без опції: інакше раунд закінчувався б, не почавшись
            var want = Math.Max(_botsWanted, seated.Length < 2 ? 1 : 0);
            var k = 0;
            for (var s = 0; s < N && want > 0; s++)
                if (!Ctx.Seated(s)) { _bots[s] = BotNames[k++ % BotNames.Length]; want--; }
        }
        _started = true;
        _over = false;
        _round = 0;
        Array.Clear(_sw);
        _cutLog.Clear();

        // Вечір живе, поки за столом ті самі (порядок не важить — «Ще раз» його обертає).
        var crew = Riders().Select(Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!crew.SequenceEqual(_crew, StringComparer.OrdinalIgnoreCase)) { _wins.Clear(); _cuts.Clear(); }
        _crew = crew;
        NewRound();
    }

    /// <summary>Хто їде цієї партії: люди, що сидять, і боти.</summary>
    int[] Riders() => [.. Enumerable.Range(0, N).Where(s => Ctx.Seated(s) || _bots[s] is not null)];

    void NewRound()
    {
        var riders = Riders();
        var (w, h) = Size(riders.Length);
        var map = _map;
        if (map == ArenaMaps.Random)
        {
            // випадкова — але не та сама двічі поспіль
            var pool = ArenaMaps.Pool.Where(m => m != _roundMap).ToArray();
            map = pool[Ctx.Rng.Next(pool.Length)];
        }
        _roundMap = map;
        _core = new ArenaCore(Ctx.Rng, w, h, N, Tails, ApplesFor(riders.Length), map == ArenaMaps.Torus) { TurboOn = _turbo };
        _core.Reset(riders, _round == 0 ? CountdownTicks : NextStartTicks, ArenaMaps.Blocks(map, w, h));
        _teams = null;
        if (_teamsOn)
        {
            // жовтий+рожевий проти зеленого+синього: старти пар дзеркальні, тож ніхто не має фори
            var t = new int[N];
            for (var s = 0; s < N; s++) t[s] = !_core.Present[s] ? -1 : s is 0 or 3 ? 0 : 1;
            if (t.Contains(0) && t.Contains(1)) _teams = t;
        }
        _core.Team = _teams;
        _round++;
        _moves = 0;
        _pause = 0;
        _winner = null;
        _winners = [];
        _ko.Clear();
        Array.Clear(_place);
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == "turbo")
        {
            if (_started && _winner is null) Core.Boost(seat);
            return ActResult.Done;
        }
        if (action != "turn") return ActResult.Fail("Тут так не ходять");
        if (SnakeModesTurns.Dir(payload) is { } dir && _started && _winner is null) Core.Turn(seat, dir);
        return ActResult.Done;
    }

    /// <summary>
    /// Встав посеред партії — вибув, як врізався, але для решти вона не зупиняється. Лишився один — партія
    /// його, і закриваємо її ОДРАЗУ, поки місце того, хто встав, ще зайняте (каркас звільняє його після нас):
    /// тож він у результаті серед тих, хто програв, і не втече від ставки чи Ело посеред серії.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (!_started || _over || seat < 0 || seat >= N) return;
        var core = Core;
        var rest = Riders().Where(s => s != seat).ToArray();
        if (_winner is null && core.Alive[seat])
        {
            core.Kill(seat, core.Bodies[seat].Count > 0 ? core.Bodies[seat][0] : -1);
            _place[seat] = core.AliveCount + 1;
        }
        var oneTeam = _teams is not null && rest.Length > 0 && rest.All(s => _teams[s] == _teams[rest[0]]);
        if (rest.Length < 2 || rest.All(s => _bots[s] is not null) || oneTeam)
        {
            var winners = rest.Where(s => Ctx.Seated(s)).ToArray();
            _winners = winners;
            _winner = winners.Length == 0 ? "draw" : "win";
            foreach (var s in winners) _place[s] = 1;
            Close(winners, LeaveText(seat, winners));
            return;
        }
        if (_winner is null && RoundOver()) Settle([]);
        else Ctx.Log($"{Info.Title}: {Ctx.NickOf(seat)} встає з-за столу, решта їде далі");
    }

    /// <summary>Рядок Журналу, коли партію закрив вихід гравця.</summary>
    protected virtual string LeaveText(int seat, int[] winners) =>
        winners.Length == 1
            ? $"{Info.Title}: {Ctx.NickOf(seat)} встає з-за столу — партія {NickCases.Dative(Name(winners[0]))}. {Series()}"
            : $"{Info.Title}: {Ctx.NickOf(seat)} встає з-за столу, партію не дограли. {Series()}";

    bool RoundOver()
    {
        var core = Core;
        if (core.AliveCount <= 1) return true;
        if (_teams is null) return false;
        var team = -1;
        for (var s = 0; s < N; s++)
            if (core.Alive[s])
            {
                if (team == -1) team = _teams[s];
                else if (_teams[s] != team) return false;
            }
        return true;
    }

    public override TickResult Tick()
    {
        if (!_started || _over) return TickResult.None;
        if (_winner is not null)
        {
            // табло між раундами серії: 2 с — і далі сам, без «Ще раз»
            if (_target <= 1) return TickResult.None;
            if (--_pause > 0) return TickResult.FrameOnly;
            NewRound();
            return TickResult.Both;
        }
        var core = Core;
        if (core.StartIn > 0)
        {
            core.StartIn--;
            return TickResult.FrameOnly;
        }

        if (HasBots) _brain.Think(core, _bots, Ctx.Rng, _soloBot ? Solo?.Level : null);
        var alive = core.AliveCount;
        var died = core.Step();
        _moves++;
        var any = died.Count > 0;
        if (any) Crashed(died, alive);
        if (_squeeze && _moves >= SqueezeAt && (_moves - SqueezeAt) % SqueezeEvery == 0 && core.Ring < core.MaxRing && !RoundOver())
        {
            alive = core.AliveCount;
            died = core.Squeeze();
            if (died.Count > 0) { any = true; Crashed(died, alive); }
        }

        if (RoundOver())
        {
            Settle(died);
            return TickResult.Both;
        }
        if (_moves >= MaxMoves)
        {
            TimeUp();
            return TickResult.Both;
        }
        // хтось вибув — повний вид, щоб усі побачили хрестик, місце й «хто кого»; інакше вистачить кадру
        return any ? TickResult.Both : TickResult.FrameOnly;
    }

    /// <summary>Хтось розбився: місце (одночасно розбиті ділять його), «хто кого підрізав» і лічильник підрізів.</summary>
    void Crashed(List<int> died, int aliveBefore)
    {
        var core = Core;
        foreach (var s in died)
        {
            _place[s] = aliveBefore - died.Count + 1;
            var k = core.Killer[s];
            _ko.Add([s, k, core.Cause[s]]);
            if (core.Cause[s] != ArenaCore.CauseTrail || k < 0) continue;
            _cuts[Name(k)] = _cuts.GetValueOrDefault(Name(k)) + 1;
            _cutLog.Add((Name(k), Name(s)));
        }
    }

    /// <summary>
    /// Кінець раунду. Один живий — його. У командах — уся команда, чий хтось ще їде (разом із тим, хто вже
    /// вибув: перемога спільна). Живих нема — останні загинули в один тик: якщо це весь стіл одразу, нічия;
    /// якщо до них хтось уже вибув — останні ділять перемогу, бо протрималися довше за решту.
    /// </summary>
    void Settle(List<int> lastDied)
    {
        var core = Core;
        var present = Enumerable.Range(0, N).Where(s => core.Present[s]).ToArray();
        int[] winners;
        if (_teams is not null)
        {
            var alive = present.Where(s => core.Alive[s]).ToArray();
            var from = alive.Length > 0 ? alive : [.. lastDied];
            var teams = from.Select(s => _teams[s]).Distinct().ToArray();
            winners = teams.Length == 1 && from.Length < present.Length ? [.. present.Where(s => _teams[s] == teams[0])] : [];
        }
        else if (core.AliveCount == 1) winners = [Array.FindIndex(core.Alive, a => a)];
        else if (lastDied.Count > 0 && lastDied.Count < present.Length) winners = [.. lastDied];
        else winners = [];
        RoundWon(winners, timeUp: false);
    }

    /// <summary>Рядок Журналу про раунд (для партії з одного раунду — він і є рядок партії).</summary>
    protected virtual string RoundText(int[] winners)
    {
        if (_teams is not null && winners.Length > 0)
            return $"{Info.Title}: раунд бере {TeamNames[_teams[winners[0]]]} ({Names(winners)}). {Series()}";
        return winners.Length switch
        {
            0 => $"{Info.Title}: усі врізались одночасно — нічия. {Series()}",
            1 => $"{Info.Title}: раунд бере {Name(winners[0])} ({SeatName(winners[0])}). {Series()}",
            _ => $"{Info.Title}: {Names(winners)} врізались останніми в один тик — очко кожному. {Series()}",
        };
    }

    /// <summary>
    /// Страховка від вічного раунду. У мотоциклах поле закінчується раніше, ніж вона спрацює; змійки ж
    /// можуть кружляти скільки завгодно — тоді перемагає найдовша, а рівні по довжині ділять раунд.
    /// </summary>
    void TimeUp()
    {
        var core = Core;
        var alive = Enumerable.Range(0, N).Where(s => core.Alive[s]).ToArray();
        var best = alive.Max(s => core.Bodies[s].Count);
        var winners = !Tails ? [] : alive.Where(s => core.Bodies[s].Count == best).ToArray();
        if (winners.Length == alive.Length) winners = [];
        foreach (var s in alive) _place[s] = winners.Contains(s) ? 1 : winners.Length + 1;
        RoundWon(winners, timeUp: true);
    }

    protected virtual string TimeUpText(int[] winners) => winners.Length == 0
        ? $"{Info.Title}: час вийшов, розійшлись внічию. {Series()}"
        : $"{Info.Title}: час вийшов — найдовша в {Names(winners, genitive: true)}. {Series()}";

    /// <summary>Раунд скінчився: очко переможцям; у серії — або табло й наступний раунд сам, або кінець партії.</summary>
    void RoundWon(int[] winners, bool timeUp)
    {
        _winners = winners;
        _winner = winners.Length == 0 ? "draw" : "win";
        foreach (var s in winners) _place[s] = 1;
        if (_target <= 1)
        {
            foreach (var s in winners) _wins[Name(s)] = _wins.GetValueOrDefault(Name(s)) + 1;
            Close(winners, timeUp ? TimeUpText(winners) : RoundText(winners));
            return;
        }
        foreach (var s in winners) _sw[s]++;
        var best = _sw.Max();
        if (best < _target && _round < MaxRounds)
        {
            _pause = PauseTicks;
            return;
        }
        var present = Enumerable.Range(0, N).Where(s => Core.Present[s]).ToArray();
        var champs = present.Where(s => _sw[s] == best).ToArray();
        if (champs.Length == present.Length) champs = [];   // рівно в усіх — нічия
        foreach (var s in champs) _wins[Name(s)] = _wins.GetValueOrDefault(Name(s)) + 1;
        _winners = champs;
        _winner = champs.Length == 0 ? "draw" : "win";
        Close(champs, SeriesText(champs));
    }

    protected virtual string SeriesText(int[] champs)
    {
        var who = champs.Length == 0 ? "нічия"
            : _teams is not null ? $"перемогли {TeamNames[_teams[champs[0]]]} ({Names(champs)})"
            : $"перемога — {Names(champs)}";
        return $"{Info.Title}: серія до {_target} — {who}. {Series()}";
    }

    void Close(int[] winners, string log)
    {
        _over = true;
        if (_cutLog.Count > 0) log += " " + CutsLine();
        if (_soloBot && Solo is { } solo)
        {
            // «🤖 + бот»: людина одна, тож Rewards не дасть нічого й з нею в переможцях; бота в переможцях нема
            var human = winners.Where(Ctx.Seated).ToArray();
            Ctx.Finish(human, $"{log} (з 🤖 — без нагород)",
                verdict: winners.Length == 0 ? "🤝 Нічия · з 🤖 — на інтерес"
                    : human.Length == 0 ? "🤖 Бот переміг"
                    : $"🏆 {Names(human)} — перемога над {LiveBots.Of(solo.Level)} ботом");
            return;
        }
        // з ботами — без нагород і рейтингу: результат іде нічиєю, а хто взяв — видно в рядку Журналу
        Ctx.Finish(HasBots ? [] : winners, HasBots ? $"{log} (з 🤖 — без нагород)" : log,
            verdict: !HasBots ? null : winners.Length == 0 ? "🤝 Нічия · з 🤖 — на інтерес" : $"🏆 {Names(winners)} · з 🤖 — на інтерес");
    }

    /// <summary>«✂ Петро влітає у слід Олі, Іра — у слід Петра» — привід для підколок.</summary>
    string CutsLine()
    {
        var parts = _cutLog.Take(6).Select((c, i) => i == 0
            ? $"{c.Victim} влітає у слід {Gen(c.Killer)}"
            : $"{c.Victim} — у слід {Gen(c.Killer)}");
        return "✂ " + string.Join(", ", parts) + (_cutLog.Count > 6 ? "…" : "");
    }

    /// <summary>Родовий відмінок імені; у бота відмінюємо ім'я без «🤖 » («у слід 🤖 Гайки»).</summary>
    static string Gen(string name) => name.StartsWith("🤖 ", StringComparison.Ordinal) ? "🤖 " + NickCases.Genitive(name[3..]) : NickCases.Genitive(name);

    /// <summary>«Оля, Петро і Іра»; <paramref name="genitive"/> — «Олі і Петра» для «найдовша в …» (через NickCases).</summary>
    protected string Names(IEnumerable<int> seats, bool genitive = false)
    {
        var names = seats.Select(s => genitive ? Gen(Name(s)) : Name(s)).ToList();
        return names.Count <= 1 ? string.Concat(names) : string.Join(", ", names[..^1]) + " і " + names[^1];
    }

    /// <summary>«Рахунок: Оля 2 ✂1 · Петро 1» — у серії рахунок серії, інакше — вечора.</summary>
    protected string Series()
    {
        var core = Core;
        var parts = Enumerable.Range(0, N).Where(s => core.Present[s])
            .Select(s =>
            {
                var cuts = _cuts.GetValueOrDefault(Name(s));
                return $"{Name(s)} {(_target > 1 ? _sw[s] : _wins.GetValueOrDefault(Name(s)))}" + (cuts > 0 ? $" ✂{cuts}" : "");
            });
        return "Рахунок: " + string.Join(" · ", parts);
    }

    bool Riding(int s) => Ctx.Seated(s) || (s < _bots.Length && _bots[s] is not null);

    /// <summary>Перемоги кожного місця: у серії — раунди цієї серії, інакше — вечір за столом.</summary>
    protected int[] Wins() => [.. Enumerable.Range(0, N).Select(s => _target > 1 && _sw.Length == N ? _sw[s] : Riding(s) ? _wins.GetValueOrDefault(Name(s)) : 0)];
    protected int[] Cuts() => [.. Enumerable.Range(0, N).Select(s => Riding(s) ? _cuts.GetValueOrDefault(Name(s)) : 0)];

    int AliveMask()
    {
        var core = Core;
        var mask = 0;
        for (var s = 0; s < N; s++) if (core.Alive[s]) mask |= 1 << s;
        return mask;
    }

    /// <summary>
    /// Мотоцикли: кадр — дельта (голови й маска живих), сліди живуть у клієнта. Змійки короткі — їм дешевше
    /// слати повний стан. Необов'язкове — лише коли ввімкнене: <c>h2</c> (перша клітинка подвійного кроку
    /// турбо), <c>tb</c> (турбо: &gt;0 діє, &lt;0 перезарядка, 0 готове), <c>rg</c>/<c>sq</c> (кілець звуження і
    /// кроків до наступного), <c>nx</c> (тиків до наступного раунду серії).
    /// </summary>
    public override object? Frame()
    {
        var core = Core;
        var f = new Dictionary<string, object?>(8);
        if (!Tails)
        {
            var h = new int[N];
            for (var s = 0; s < N; s++) h[s] = core.Bodies[s].Count > 0 ? core.Bodies[s][0] : -1;
            f["h"] = h;
            for (var s = 0; s < N; s++)
                if (core.Mid[s] >= 0) { f["h2"] = core.Mid.ToArray(); break; }
        }
        else
        {
            f["t"] = core.Bodies.Select(b => b.ToArray()).ToArray();
            f["ap"] = core.Apples.ToArray();
        }
        f["al"] = AliveMask();
        f["startIn"] = core.StartIn;
        f["winner"] = _winner;
        if (_turbo) f["tb"] = TurboState();
        if (_squeeze) { f["rg"] = core.Ring; f["sq"] = SqueezeIn(); }
        if (_pause > 0) f["nx"] = _pause;
        FrameExtra(f);
        return f;
    }

    int[] TurboState()
    {
        var core = Core;
        var tb = new int[N];
        for (var s = 0; s < N; s++) tb[s] = core.Turbo[s] > 0 ? core.Turbo[s] : -core.Cool[s];
        return tb;
    }

    /// <summary>Скільки кроків до наступного кільця (−1 — вже не буде).</summary>
    int SqueezeIn()
    {
        var core = Core;
        if (!_squeeze || core.Ring >= core.MaxRing || _winner is not null) return -1;
        return _moves < SqueezeAt ? SqueezeAt - _moves : SqueezeEvery - (_moves - SqueezeAt) % SqueezeEvery;
    }

    public override object View(int? seat)
    {
        var core = Core;
        var v = new Dictionary<string, object?>
        {
            ["width"] = core.W,
            ["height"] = core.H,
            ["turn"] = null,
            ["mode"] = Tails ? "snake" : "tron",
            ["t"] = core.Bodies.Select(b => b.ToArray()).ToArray(),
            ["dirs"] = core.Dirs.ToArray(),
            ["present"] = core.Present.ToArray(),
            ["al"] = AliveMask(),
            ["crash"] = core.Crash.ToArray(),
            ["place"] = _place.ToArray(),
            ["wins"] = Wins(),
            ["ap"] = core.Apples.ToArray(),
            ["startIn"] = core.StartIn,
            ["winner"] = _winner,
            ["winners"] = _winners,
        };
        if (!Tails)
        {
            v["cuts"] = Cuts();
            v["ko"] = _ko.ToArray();
            v["kt"] = KoTexts();
            v["map"] = _roundMap;
            if (core.Walls.Count > 0) v["walls"] = core.Walls.ToArray();
            if (core.Wrap) v["wrap"] = true;
        }
        if (_target > 1) { v["ser"] = _target; v["round"] = _round; v["over"] = _over; }
        if (_pause > 0) v["nx"] = _pause;
        if (_turbo) v["tb"] = TurboState();
        if (_squeeze) { v["rg"] = core.Ring; v["sq"] = SqueezeIn(); }
        if (HasBots) v["bots"] = _bots.ToArray();
        if (_teams is not null) v["teams"] = _teams.ToArray();
        else if (_teamsOn && _started) v["noteams"] = true;
        ViewExtra(v);
        return v;
    }

    /// <summary>
    /// Стрічка «хто кого» цього раунду готовими рядками — відмінювати ніки вміє лише сервер (<see cref="NickCases"/>).
    /// Лоб у лоб — один рядок на пару, а не два.
    /// </summary>
    string[] KoTexts()
    {
        var list = new List<string>(_ko.Count);
        foreach (var k in _ko)
        {
            var (s, o, how) = (k[0], k[1], k[2]);
            if (how == ArenaCore.CauseHead && o >= 0 && o < s && _ko.Any(x => x[0] == o && x[1] == s)) continue;
            list.Add(how switch
            {
                ArenaCore.CauseTrail when o >= 0 => $"✂ {Name(s)} влітає у слід {Gen(Name(o))}",
                ArenaCore.CauseHead when o >= 0 => $"💥 {Name(s)} і {Name(o)} — лоб у лоб",
                ArenaCore.CauseSelf => $"🌀 {Name(s)} — у власний слід",
                ArenaCore.CauseSqueeze => $"🧱 {Name(s)}: стіна наздогнала",
                _ => $"💥 {Name(s)} — у стіну",
            });
        }
        return [.. list];
    }

    /// <summary>Дуель додає свої поля (winsA/winsB, «x»/«o»), не ламаючи спільного виду.</summary>
    protected virtual void ViewExtra(Dictionary<string, object?> view) { }
    protected virtual void FrameExtra(Dictionary<string, object?> frame) { }
    /// <summary>Хто переміг у раунді/партії (місця) — для дуелі, що каже «x»/«o».</summary>
    protected int[] WinnerSeats => _winners;
    protected string? Outcome => _winner;
}

/// <summary>
/// Боти мотоциклів: з трьох ходів (прямо, ліворуч, праворуч) обирають той, де попереду найбільше вільного
/// місця (заливка до <see cref="Reach"/> клітинок), з невеликою любов'ю їхати прямо й острахом лоба в лоб.
/// Масиви — одні на все життя, заливка без алокацій: на трьох ботів тик дорожчає на мікросекунди.
/// </summary>
public sealed class BotBrain
{
    public const int Reach = 90;
    int[] _stamp = [], _queue = [];
    int _mark;

    /// <summary>
    /// Як їздить бот: скільки клітинок заливки досить, скільки ходів навмання на тисячу рішень, як боїться лоба в лоб
    /// і чи тисне на суперника (обирає хід, після якого тому лишається менше місця).
    /// </summary>
    readonly record struct Style(int Reach, int Blunders, int HeadFear, bool Press);

    /// <summary>Боти гурту — як їздили завжди (без помилок навмання, без тиску).</summary>
    static readonly Style Party = new(Reach, 0, 40, false);

    /// <summary>«🤖 + бот» у мотоциклах удвох: легкий / звичайний / сильний.</summary>
    static readonly Style[] Solo =
    [
        new(14, 25, 10, false),    // легкий: бачить на кілька клітинок, частіше схибить — заганяє себе в кут
        new(Reach, 4, 40, false),  // звичайний: як боти гурту, зрідка схибить
        new(220, 1, 60, true),     // сильний: бачить далеко й підрізає
    ];

    /// <param name="level">Рівень «🤖 + бот»; null — боти гурту.</param>
    public void Think(ArenaCore core, string?[] bots, Random rng, LiveBots.Level? level = null)
    {
        if (_stamp.Length != core.W * core.H) { _stamp = new int[core.W * core.H]; _queue = new int[core.W * core.H]; _mark = 0; }
        var st = level is { } l ? Solo[LiveBots.Index(l)] : Party;
        for (var s = 0; s < bots.Length && s < core.Seats; s++)
        {
            if (bots[s] is null || !core.Alive[s] || core.Turning(s)) continue;
            var cur = core.Dirs[s];
            var head = core.Bodies[s][0];
            if (st.Blunders > 0 && rng.Next(1000) < st.Blunders)
            {
                var d = (cur + 3 + rng.Next(3)) % 4;   // ліворуч, прямо чи праворуч — не дивлячись
                if (d != cur) core.Turn(s, d);
                continue;
            }
            var rival = st.Press ? Rival(core, s) : -1;
            int best = cur, bestScore = int.MinValue;
            for (var i = 0; i < 3; i++)
            {
                var dir = i == 0 ? cur : i == 1 ? (cur + 1) % 4 : (cur + 3) % 4;
                var (next, ok) = core.Ahead(head, dir);
                int score;
                if (!ok || Blocked(core, s, next)) score = -1000 + rng.Next(3);
                else
                {
                    score = Fill(core, s, next, st.Reach) * 4 + (i == 0 ? 3 : 0) + rng.Next(3);
                    if (rival >= 0) score -= Squeezed(core, rival, next, st.Reach) * 2;
                    if (NearHead(core, s, next)) score -= st.HeadFear;
                }
                if (score > bestScore) { bestScore = score; best = dir; }
            }
            if (best != cur) core.Turn(s, best);
            else if (core.TurboOn && bestScore >= st.Reach * 4 && rng.Next(40) == 0) core.Boost(s);
        }
    }

    /// <summary>Перший живий суперник (у дуелі він один) — на нього тисне сильний бот; −1 — нема.</summary>
    static int Rival(ArenaCore core, int s)
    {
        for (var o = 0; o < core.Seats; o++)
            if (o != s && core.Alive[o] && core.Bodies[o].Count > 0) return o;
        return -1;
    }

    /// <summary>Скільки місця лишиться суперникові, якщо бот стане на <paramref name="cell"/>: клітинку на мить займаємо й повертаємо.</summary>
    int Squeezed(ArenaCore core, int rival, int cell, int reach)
    {
        var was = core.Occ[cell];
        core.Occ[cell] = -1;
        var n = Fill(core, rival, core.Bodies[rival][0], reach);
        core.Occ[cell] = was;
        return n;
    }

    static bool Blocked(ArenaCore core, int s, int cell)
    {
        var v = core.Occ[cell];
        if (v == 0) return false;
        if (v < 0) return true;
        var o = v - 1;
        return !(o != s && core.Team is { } t && t[s] >= 0 && t[o] == t[s]);
    }

    /// <summary>Чи може чиясь жива голова наступним кроком стати сюди ж (лоб у лоб).</summary>
    static bool NearHead(ArenaCore core, int s, int cell)
    {
        for (var o = 0; o < core.Seats; o++)
        {
            if (o == s || !core.Alive[o]) continue;
            var h = core.Bodies[o][0];
            for (var d = 0; d < 4; d++)
                if (core.Ahead(h, d) is (var c, true) && c == cell) return true;
        }
        return false;
    }

    int Fill(ArenaCore core, int s, int from, int reach)
    {
        _mark++;
        int head = 0, tail = 0;
        _queue[tail++] = from;
        _stamp[from] = _mark;
        while (head < tail && tail < reach)
        {
            var c = _queue[head++];
            for (var d = 0; d < 4 && tail < reach; d++)
            {
                var (n, ok) = core.Ahead(c, d);
                if (!ok || _stamp[n] == _mark || Blocked(core, s, n)) continue;
                _stamp[n] = _mark;
                _queue[tail++] = n;
            }
        }
        return tail;
    }
}

/// <summary>Мотоцикли на 1–4: слід лишається стіною до кінця раунду, раунд бере останній, хто їде. Сам — з 🤖.</summary>
public sealed class TronPartyGame : ArenaGame
{
    public override GameInfo Info { get; } = new(
        "tron-party", "Мотоцикли гуртом", "мотоцикли гуртом", GameGroup.Live, 1, Seats, TickMs: TronGame.TickMs,
        Start: StartMode.ByHost,
        Options: [FieldOption, SeriesOption, ArenaMaps.Option, TurboOption, SqueezeOption, BotsOption, TeamsOption],
        Hint: "Мотоцикли на 2–4: за кожним тягнеться стіна. Врізався — вибув, раунд бере останній, хто їде. Стрілки або WASD. Нема компанії — посади 🤖",
        Client: "snake-modes");

    protected override bool Tails => false;
    protected override int CountdownTicks => TronGame.StartTicks;
    /// <summary>Дві хвилини: на великому полі слід заповнює його значно раніше, це лише страховка.</summary>
    public override int MaxMoves { get; set; } = 1200;
    protected override string[] Colors => ["жовтий", "зелений", "синій", "рожевий"];
}
