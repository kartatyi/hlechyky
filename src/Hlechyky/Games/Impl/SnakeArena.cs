using System.Text;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Ядро «Змійок гуртом» (прохід №3). Колись змійки гуртом їздили на спільному з мотоциклами
/// <see cref="ArenaCore"/>, але мотоцикли пішли своєю дорогою (турбо, мапи, команди), а змійкам треба своє:
/// мертва змійка розсипається яблуками (№150), вибулі кидають яблука й камінці (№151), бонуси (№154),
/// поле-тор (№155) і відродження в режимі «на час» (№153). Правила кроку ті самі, що в дуелі, клітинка в клітинку:
/// стіна, будь-яке тіло (хвіст, що цього тика звільняє клітинку, — не перешкода), голови в одну клітинку — гинуть обидві.
/// <para>
/// Зайнятість — сітка <see cref="Occ"/> (0 — вільно, s+1 — тіло місця s) і <see cref="Items"/> (що лежить на
/// клітинці): перевірка «чи вріжусь» за O(1), без множин і LINQ на кожен тик.
/// </para>
/// </summary>
public sealed class SnakeArenaCore
{
    public const int StartLen = 3;
    public const int MaxQueued = SnakeCore.MaxQueued;

    /// <summary>Що лежить на клітинці.</summary>
    public const byte None = 0, Apple = 1, Loot = 2, Rock = 3, Gold = 4, Scissors = 5, Slow = 6;
    /// <summary>Стеля «здобичі» (яблука з мертвих і кинуті): більше — і поле стає садом, а кадр пухне.</summary>
    public const int LootCap = 40;
    /// <summary>Камінець вибулого живе 3 с.</summary>
    public const int RockTicks = 25;
    /// <summary>Сповільнення — 3 с (решта змійок ходять через тик).</summary>
    public const int SlowTicks = 25;
    /// <summary>Бонус лежить на полі 10 с, наступний — через 7–12 с після попереднього.</summary>
    public const int BonusTicks = 83, BonusGapMin = 58, BonusGapMax = 100;
    /// <summary>Не ближче скількох клітинок до живої голови можна кинути камінь, покласти бонус чи відродитись.</summary>
    public const int HeadGap = 3;

    static readonly (int Dx, int Dy)[] Deltas = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    readonly Random _rng;
    readonly int _w, _h, _seats, _apples;
    readonly Queue<int>[] _turns;
    // робочі масиви кроку — одні на все життя ядра
    readonly int[] _next;
    readonly bool[] _ok, _mv, _dead, _grow;
    readonly List<int> _died = [];
    readonly List<(int Cell, int Until)> _rocks = [];
    int _regular, _loot, _bonusUntil, _bonusNext;

    public SnakeArenaCore(Random rng, int w, int h, int seats, int apples, bool wrap = false, bool bonuses = false)
    {
        _rng = rng;
        _w = w; _h = h; _seats = seats; _apples = apples;
        Wrap = wrap;
        Bonuses = bonuses;
        _turns = [.. Enumerable.Range(0, seats).Select(_ => new Queue<int>())];
        _next = new int[seats];
        _ok = new bool[seats]; _mv = new bool[seats]; _dead = new bool[seats]; _grow = new bool[seats];
        Bodies = [.. Enumerable.Range(0, seats).Select(_ => new List<int>())];
        Dirs = new int[seats];
        Present = new bool[seats];
        Alive = new bool[seats];
        Crash = [.. Enumerable.Repeat(-1, seats)];
        Grow = new int[seats];
        SlowLeft = new int[seats];
        Occ = new sbyte[w * h];
        Items = new byte[w * h];
    }

    public int W => _w;
    public int H => _h;
    public int Seats => _seats;
    /// <summary>Тор: стін по краю нема — виповз праворуч, з'явився ліворуч.</summary>
    public bool Wrap { get; }
    /// <summary>Чи з'являються на полі бонуси (⭐ золоте яблуко, ✂ ножиці, ❄ сповільнення).</summary>
    public bool Bonuses { get; }

    /// <summary>Клітинки кожної змійки, голова перша. Порожнє місце або розбита змійка — порожній список.</summary>
    public List<int>[] Bodies { get; }
    public int[] Dirs { get; }
    /// <summary>Чи є на цьому місці змійка в цьому раунді (сиділа на старті).</summary>
    public bool[] Present { get; }
    public bool[] Alive { get; }
    /// <summary>Де змійка розбилась (хрестик); −1 — повзе або її тут нема.</summary>
    public int[] Crash { get; }
    /// <summary>Скільки клітинок змійка ще доросте (золоте яблуко дає +3 за три тики).</summary>
    public int[] Grow { get; }
    /// <summary>Скільки тиків змійка ще сповільнена (ходить через тик).</summary>
    public int[] SlowLeft { get; }
    public sbyte[] Occ { get; }
    public byte[] Items { get; }
    /// <summary>Бонус на полі: клітинка (−1 — нема) і що це.</summary>
    public int BonusCell { get; private set; } = -1;
    public byte BonusKind { get; private set; }
    /// <summary>Лічильник кроків ядра (і годинник камінців, бонусів, сповільнення).</summary>
    public int Now { get; private set; }
    public int StartIn { get; set; }
    /// <summary>Що сталось (для спалахів у клієнта): тик, що, чиє, де. Кадр несе події свого тика.</summary>
    public List<(int Tick, char Kind, int Seat, int Cell)> Events { get; } = [];

    public int AliveCount
    {
        get { var n = 0; for (var s = 0; s < _seats; s++) if (Alive[s]) n++; return n; }
    }

    public int Cell(int x, int y) => y * _w + x;

    /// <summary>
    /// Нова партія для тих, хто сидить. Двоє стартують рівно як у дуелі, третя — згори, четверта — знизу; колони
    /// зсунуті, щоб прямі не зустрілись лоб у лоб в один тик.
    /// </summary>
    public void Reset(IReadOnlyList<int> present, int startTicks)
    {
        Array.Clear(Occ);
        Array.Clear(Items);
        _rocks.Clear();
        Events.Clear();
        _regular = _loot = 0;
        BonusCell = -1;
        for (var s = 0; s < _seats; s++)
        {
            Bodies[s].Clear();
            _turns[s].Clear();
            Present[s] = Alive[s] = false;
            Crash[s] = -1;
            Dirs[s] = 0;
            Grow[s] = SlowLeft[s] = 0;
        }
        var starts = Starts(present.Count);
        for (var i = 0; i < present.Count && i < starts.Length; i++)
        {
            var s = present[i];
            var (x, y, dir) = starts[i];
            var (dx, dy) = Deltas[dir];
            for (var k = 0; k < StartLen; k++) Put(s, Cell(x - dx * k, y - dy * k), atHead: false);
            Dirs[s] = dir;
            Present[s] = Alive[s] = true;
        }
        if (_apples > 0)
        {
            // перше яблуко — посередині, як у дуелі; решта — куди ляже з генератора
            var mid = Cell(_w / 2, _h / 2);
            if (Occ[mid] == 0) AddItem(mid, Apple);
            while (_regular < _apples && PlaceApple()) { }
        }
        StartIn = startTicks;
        _bonusNext = Now + startTicks + BonusGapMin;
    }

    public (int X, int Y, int Dir)[] Starts(int n)
    {
        var a = (StartLen, _h / 2 - 3, 0);
        var b = (_w - 1 - StartLen, _h / 2 + 3, 2);
        if (n <= 2) return [a, b];
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

    /// <summary>
    /// Один крок усіх живих. Сповільнена змійка ходить лише на парних тиках і тоді стоїть цілком (хвіст не
    /// звільняє). Розбита змійка розсипається яблуками — кожна друга клітинка тіла (№150). Повертає місця, що
    /// загинули (список живе до наступного кроку).
    /// </summary>
    public List<int> Step()
    {
        Now++;
        _died.Clear();
        PurgeEvents();
        Expire();

        for (var s = 0; s < _seats; s++)
        {
            _mv[s] = _dead[s] = _grow[s] = false;
            if (!Alive[s]) continue;
            if (SlowLeft[s] > 0)
            {
                SlowLeft[s]--;
                if ((Now & 1) == 1) continue;   // сповільнена стоїть через тик
            }
            _mv[s] = true;
            if (_turns[s].Count > 0) Dirs[s] = _turns[s].Dequeue();
            (_next[s], _ok[s]) = Ahead(Bodies[s][0], Dirs[s]);
            _grow[s] = Grow[s] > 0 || (_ok[s] && Gain(Items[_next[s]]) > 0);
        }

        for (var s = 0; s < _seats; s++)
        {
            if (!_mv[s]) continue;
            var dead = !_ok[s];
            if (!dead)
            {
                var c = _next[s];
                if (Items[c] == Rock) dead = true;
                else if (Occ[c] > 0)
                {
                    var o = Occ[c] - 1;
                    var body = Bodies[o];
                    dead = !(_mv[o] && !_grow[o] && body[^1] == c);   // хвіст, що саме звільняється, — не перешкода
                }
                for (var o = 0; o < _seats && !dead; o++)
                    if (o != s && _mv[o] && _ok[o] && _next[o] == c) dead = true;
            }
            _dead[s] = dead;
        }

        // спершу хвости (звільнені клітинки), тоді голови — щоб голова в чужому хвості не стерлась
        for (var s = 0; s < _seats; s++)
        {
            if (!_mv[s] || _dead[s] || _grow[s]) continue;
            var body = Bodies[s];
            var tail = body[^1];
            body.RemoveAt(body.Count - 1);
            if (Occ[tail] == s + 1) Occ[tail] = 0;
        }
        for (var s = 0; s < _seats; s++)
        {
            if (!_mv[s] || _dead[s]) continue;
            var c = _next[s];
            Put(s, c, atHead: true);
            var item = Items[c];
            Grow[s] = Math.Max(0, Grow[s] + Gain(item) - (_grow[s] ? 1 : 0));
            if (item is Apple or Loot) TakeItem(c);
            else if (item is Gold or Scissors or Slow) TakeBonus(s, c, item);
        }
        for (var s = 0; s < _seats; s++)
            if (_dead[s])
            {
                _died.Add(s);
                Kill(s, _ok[s] ? _next[s] : Bodies[s][0]);
            }

        while (_regular < _apples && PlaceApple()) { }
        if (Bonuses && BonusCell < 0 && Now >= _bonusNext) PlaceBonus();
        return _died;
    }

    static int Gain(byte item) => item switch { Apple or Loot => 1, Gold => 3, _ => 0 };

    /// <summary>Змійка вибула (врізалась або встає з-за столу): зникає з поля, кожна друга клітинка — яблуко.</summary>
    public void Kill(int seat, int at)
    {
        if (!Alive[seat]) return;
        Alive[seat] = false;
        Crash[seat] = at;
        _turns[seat].Clear();
        Grow[seat] = SlowLeft[seat] = 0;
        var body = Bodies[seat];
        for (var i = 0; i < body.Count; i++)
        {
            var c = body[i];
            if (Occ[c] != seat + 1) continue;   // у звільнений хвіст щойно заповзла чужа голова
            Occ[c] = 0;
            if (i % 2 == 0 && Items[c] == None && _loot < LootCap) AddItem(c, Loot);
        }
        body.Clear();
    }

    public (int Cell, bool Ok) Ahead(int head, int dir)
    {
        var (dx, dy) = Deltas[dir];
        var (x, y) = (head % _w + dx, head / _w + dy);
        if (Wrap) return (Cell((x + _w) % _w, (y + _h) % _h), true);
        return x < 0 || x >= _w || y < 0 || y >= _h ? (head, false) : (Cell(x, y), true);
    }

    /// <summary>Відстань «королем» між клітинками (на торі — через край, якщо так ближче).</summary>
    public int Dist(int a, int b)
    {
        var dx = Math.Abs(a % _w - b % _w);
        var dy = Math.Abs(a / _w - b / _w);
        if (Wrap) { dx = Math.Min(dx, _w - dx); dy = Math.Min(dy, _h - dy); }
        return Math.Max(dx, dy);
    }

    bool NearHead(int cell, int gap)
    {
        for (var s = 0; s < _seats; s++)
            if (Alive[s] && Bodies[s].Count > 0 && Dist(cell, Bodies[s][0]) < gap) return true;
        return false;
    }

    void Put(int seat, int cell, bool atHead)
    {
        if (atHead) Bodies[seat].Insert(0, cell); else Bodies[seat].Add(cell);
        Occ[cell] = (sbyte)(seat + 1);
    }

    void AddItem(int cell, byte kind)
    {
        Items[cell] = kind;
        if (kind == Apple) _regular++;
        else if (kind == Loot) _loot++;
    }

    void TakeItem(int cell)
    {
        var kind = Items[cell];
        Items[cell] = None;
        if (kind == Apple) _regular--;
        else if (kind == Loot) _loot--;
    }

    /// <summary>Нове звичайне яблуко на вільній клітинці. false — поле забите вщерть.</summary>
    public bool PlaceApple()
    {
        var cell = FreeCell(0, 200);
        if (cell < 0) return false;
        AddItem(cell, Apple);
        return true;
    }

    /// <summary>Випадкова вільна клітинка (нема тіла й нічого не лежить) не ближче <paramref name="gap"/> до голів; −1 — не знайшли.</summary>
    int FreeCell(int gap, int tries)
    {
        for (var i = 0; i < tries; i++)
        {
            var c = _rng.Next(_w * _h);
            if (Occ[c] == 0 && Items[c] == None && (gap == 0 || !NearHead(c, gap))) return c;
        }
        // поле майже повне — пройдемо підряд від випадкового місця
        var from = _rng.Next(_w * _h);
        for (var i = 0; i < _w * _h; i++)
        {
            var c = (from + i) % (_w * _h);
            if (Occ[c] == 0 && Items[c] == None && (gap == 0 || !NearHead(c, gap))) return c;
        }
        return -1;
    }

    // =============================================================================================
    // Бонуси (№154)
    // =============================================================================================

    void PlaceBonus()
    {
        var cell = FreeCell(HeadGap, 60);
        _bonusNext = Now + BonusTicks + _rng.Next(BonusGapMin, BonusGapMax);
        if (cell < 0) return;
        var roll = _rng.Next(10);
        var kind = roll < 4 ? Gold : roll < 7 ? Scissors : Slow;
        Items[cell] = kind;
        BonusCell = cell;
        BonusKind = kind;
        _bonusUntil = Now + BonusTicks;
    }

    void TakeBonus(int seat, int cell, byte kind)
    {
        Items[cell] = None;
        BonusCell = -1;
        _bonusNext = Now + _rng.Next(BonusGapMin, BonusGapMax);
        if (kind == Scissors) Cut(seat);
        else if (kind == Slow)
            for (var o = 0; o < _seats; o++)
                if (o != seat && Alive[o]) SlowLeft[o] = SlowTicks;
        Events.Add((Now, kind == Gold ? 'g' : kind == Scissors ? 's' : 'z', seat, cell));
    }

    /// <summary>Ножиці: хвіст навпіл (не коротше за стартову довжину) — вибратись із мішка, що сам собі сплів.</summary>
    public void Cut(int seat)
    {
        var body = Bodies[seat];
        var keep = Math.Max(StartLen, (body.Count + 1) / 2);
        if (body.Count <= keep) return;
        for (var i = keep; i < body.Count; i++)
            if (Occ[body[i]] == seat + 1) Occ[body[i]] = 0;
        body.RemoveRange(keep, body.Count - keep);
        Grow[seat] = 0;
    }

    void Expire()
    {
        for (var i = _rocks.Count - 1; i >= 0; i--)
        {
            if (_rocks[i].Until > Now) continue;
            if (Items[_rocks[i].Cell] == Rock) Items[_rocks[i].Cell] = None;
            _rocks.RemoveAt(i);
        }
        if (BonusCell >= 0 && Now >= _bonusUntil)
        {
            if (Items[BonusCell] == BonusKind) Items[BonusCell] = None;
            BonusCell = -1;
            _bonusNext = Now + _rng.Next(BonusGapMin, BonusGapMax);
        }
    }

    void PurgeEvents()
    {
        for (var i = Events.Count - 1; i >= 0; i--)
            if (Events[i].Tick < Now) Events.RemoveAt(i);
    }

    // =============================================================================================
    // Вибулі кидають (№151) і відродження (№153)
    // =============================================================================================

    /// <summary>
    /// Вибулий кидає на поле яблуко ('a') чи камінець на 3 с ('r'). Клітинка −1 — «кинь сам» (пад): яблуко —
    /// куди випаде, камінь — на чотири клітинки перед носом найдовшої змійки. Вертає null або чому не можна.
    /// </summary>
    public string? Drop(int seat, int cell, char kind)
    {
        if (kind is not ('a' or 'r')) return "Кидати можна яблуко або камінець";
        if (kind == 'a' && _loot >= LootCap) return "Яблук і так повно";
        if (cell < 0) cell = kind == 'r' ? BeforeLeader() : FreeCell(HeadGap, 80);
        if (cell < 0 || cell >= _w * _h) return "Нема куди кинути";
        if (Occ[cell] != 0 || Items[cell] != None) return "Тут зайнято";
        if (NearHead(cell, HeadGap)) return "Надто близько до голови — хоч на три клітинки далі";
        if (kind == 'r')
        {
            Items[cell] = Rock;
            _rocks.Add((cell, Now + RockTicks));
        }
        else AddItem(cell, Loot);
        Events.Add((Now + 1, kind, seat, cell));
        return null;
    }

    int BeforeLeader()
    {
        var best = -1;
        for (var s = 0; s < _seats; s++)
            if (Alive[s] && (best < 0 || Bodies[s].Count > Bodies[best].Count)) best = s;
        if (best >= 0)
        {
            var c = Bodies[best][0];
            var ok = true;
            for (var i = 0; i < HeadGap + 1 && ok; i++) (c, ok) = Ahead(c, Dirs[best]);
            if (ok && Occ[c] == 0 && Items[c] == None && !NearHead(c, HeadGap)) return c;
        }
        return FreeCell(HeadGap, 80);
    }

    /// <summary>
    /// «На час»: змійка знову на полі довжиною 3 — там, де попереду щонайменше шість вільних клітинок, а до чужих
    /// голів не ближче чотирьох (щоб не з'явитись і одразу не вбитись). false — місця зараз нема, спробуємо на тику.
    /// </summary>
    public bool Respawn(int seat)
    {
        if (Alive[seat]) return true;
        for (var t = 0; t < 80; t++)
        {
            var head = _rng.Next(_w * _h);
            var dir = _rng.Next(4);
            if (!Clear(head, dir)) continue;
            Bodies[seat].Clear();
            var back = (dir + 2) % 4;
            var c = head;
            for (var k = 0; k < StartLen; k++)
            {
                Put(seat, c, atHead: false);
                (c, _) = Ahead(c, back);
            }
            _turns[seat].Clear();
            Dirs[seat] = dir;
            Alive[seat] = true;
            Crash[seat] = -1;
            Events.Add((Now, 'b', seat, head));   // відроджуємо з тика, після кроку
            return true;
        }
        return false;
    }

    bool Clear(int head, int dir)
    {
        if (NearHead(head, HeadGap + 1)) return false;
        var back = (dir + 2) % 4;
        var c = head;
        for (var k = 0; k < StartLen; k++)
        {
            if (Occ[c] != 0 || Items[c] != None) return false;
            bool ok;
            (c, ok) = Ahead(c, back);
            if (!ok && k < StartLen - 1) return false;
        }
        c = head;
        for (var k = 0; k < 6; k++)
        {
            bool ok;
            (c, ok) = Ahead(c, dir);
            if (!ok || Occ[c] != 0 || Items[c] == Rock) return false;
        }
        return true;
    }

    // =============================================================================================
    // Дріт
    // =============================================================================================

    /// <summary>
    /// Тіло рядком: номер клітинки голови, далі по букві на кожну наступну клітинку — куди від попередньої
    /// (r праворуч, d вниз, l ліворуч, u вгору; на торі — і через край). Змійка на 80 клітинок — 85 байт замість 350.
    /// </summary>
    public string Pack(int seat, StringBuilder sb)
    {
        var body = Bodies[seat];
        if (body.Count == 0) return "";
        sb.Clear();
        sb.Append(body[0]);
        for (var i = 1; i < body.Count; i++)
        {
            int a = body[i - 1], b = body[i];
            var dx = b % _w - a % _w;
            var dy = b / _w - a / _w;
            sb.Append(dx == 1 || dx == -(_w - 1) ? 'r' : dx == -1 || dx == _w - 1 ? 'l' : dy == 1 || dy == -(_h - 1) ? 'd' : 'u');
        }
        return sb.ToString();
    }

    /// <summary>Клітинки, де лежить <paramref name="kind"/>, — для кадру.</summary>
    public int[] CellsOf(byte kind)
    {
        var n = 0;
        for (var c = 0; c < Items.Length; c++) if (Items[c] == kind) n++;
        var r = new int[n];
        n = 0;
        for (var c = 0; c < Items.Length && n < r.Length; c++) if (Items[c] == kind) r[n++] = c;
        return r;
    }
}

/// <summary>
/// «Змійки гуртом» на 2–4: яблука, хвіст за головою, розбита змійка розсипається яблуками; раунд бере остання
/// жива, а за три хвилини — найдовша. Прохід №3: серія до N перемог з автостартом (№152), «на час» із
/// відродженням (№153), бонуси (№154), тор (№155), вибулі кидають яблука й камінці (№151).
/// Ело й ставок нема: це стіл на компанію, а не дуель (у дуелі «Змійка» — своє ядро й Ело).
/// </summary>
public sealed class SnakePartyGame : Game
{
    public const int Seats = 4;
    /// <summary>Мале поле — як у дуелі; велике — для трьох-чотирьох.</summary>
    public const int SmallW = SnakeCore.W, SmallH = SnakeCore.H, BigW = 34, BigH = 24;
    /// <summary>Табло між раундами серії — 2,5 с, далі новий раунд сам, без «Ще раз».</summary>
    public const int PauseTicks = 21;
    /// <summary>Відлік наступного раунду серії — 2 с: хто де стартує, вже всі знають.</summary>
    public const int NextStartTicks = 17;
    /// <summary>«На час»: 90 с по 120 мс; розбилась — через 2 с знову на полі.</summary>
    public const int TimedMoves = 750, RespawnTicks = 17;
    /// <summary>Вибулий кидає раз на 5 с.</summary>
    public const int DropCooldown = 42;
    /// <summary>Страховка від вічної серії, де раунд за раундом нічия.</summary>
    const int MaxRoundsPerWin = 4;

    public override GameInfo Info { get; } = new(
        "snake-party", "Змійки гуртом", "змійки гуртом", GameGroup.Live, 2, Seats, TickMs: SnakeCore.TickMs,
        Start: StartMode.ByHost,
        Options: [
            new("field", "Поле", [("auto", "Під склад"), ("small", "Мале 26×18"), ("big", "Велике 34×24")], "auto"),
            new("series", "Партія", [("1", "один раунд"), ("3", "до 3 перемог"), ("5", "до 5 перемог")], "1"),
            new("mode", "Раунд", [("last", "до останньої живої"), ("time", "⏱ на час: 90 с, розбилась — знову в грі")], "last"),
            new("wrap", "Край поля", [("0", "стіни"), ("1", "🌀 тор: виповз праворуч — з'явився ліворуч")], "0"),
            new("bonus", "Бонуси", [("0", "без бонусів"), ("1", "⭐ золоте яблуко, ✂ ножиці, ❄ сповільнення")], "0"),
        ],
        Hint: "Змійки на 2–4: їж яблука, не врізайся. Розбита змійка розсипається яблуками, а вибулі кидають яблука й камінці. Раунд бере остання жива, за три хвилини — найдовша",
        Client: "snake-party");

    /// <summary>Скільки кроків раунд «до останньої» може тривати — далі перемагає найдовша (три хвилини).</summary>
    public int MaxMoves { get; set; } = 1500;

    SnakeArenaCore? _core;
    string _field = "auto";
    int _target = 1;
    bool _timed, _wrap, _bonus;
    bool _started, _over;
    int _moves, _round, _pause;
    /// <summary>Рахунок «Ще раз» за ніками: «Ще раз» обертає місця, а перемоги мають їхати з людиною.</summary>
    readonly Dictionary<string, int> _wins = new(StringComparer.OrdinalIgnoreCase);
    string[] _crew = [];
    /// <summary>Перемоги в поточній серії — за місцями (посеред серії місця не обертаються).</summary>
    readonly int[] _sw = new int[Seats];
    readonly int?[] _place = new int?[Seats];
    readonly int[] _respawn = new int[Seats];
    readonly int[] _dropAt = new int[Seats];
    readonly bool[] _gone = new bool[Seats];
    string? _winner;
    int[] _winners = [];
    readonly StringBuilder _sb = new();

    public int Moves => _moves;
    public int Round => _round;
    public int Target => _target;
    public bool Timed => _timed;
    public SnakeArenaCore Arena => Core;

    static readonly string[] Colors = ["жовта", "зелена", "синя", "рожева"];
    public override string SeatName(int seat) => seat >= 0 && seat < Colors.Length ? Colors[seat] : base.SeatName(seat);

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _field = options.TryGetValue("field", out var f) && f is "small" or "big" ? f : "auto";
        _target = options.GetValueOrDefault("series") switch { "3" => 3, "5" => 5, _ => 1 };
        _timed = options.GetValueOrDefault("mode") == "time";
        _wrap = options.GetValueOrDefault("wrap") == "1";
        _bonus = options.GetValueOrDefault("bonus") == "1";
        _core = null;
    }

    int[] Seated() => [.. Enumerable.Range(0, Seats).Where(Ctx.Seated)];

    (int W, int H) Size(int players) => _field switch
    {
        "small" => (SmallW, SmallH),
        "big" => (BigW, BigH),
        _ => players <= 2 ? (SmallW, SmallH) : (BigW, BigH),
    };

    static int ApplesFor(int players) => players <= 2 ? 1 : 2;

    /// <summary>До «Почати» поле щоразу будується під тих, хто сидить (без яблук), — стіл показує, хто де стартує.</summary>
    SnakeArenaCore Core
    {
        get
        {
            if (_core is not null && _started) return _core;
            var seated = Seated();
            var (w, h) = Size(seated.Length);
            var same = _core is not null && _core.W == w && _core.H == h && _core.Wrap == _wrap;
            for (var s = 0; s < Seats && same; s++) same = _core!.Present[s] == seated.Contains(s);
            if (!same)
            {
                _core = new SnakeArenaCore(new Random(0), w, h, Seats, 0, _wrap);
                _core.Reset(seated, SnakeCore.StartTicks);
            }
            return _core!;
        }
    }

    public override void Start()
    {
        var seated = Seated();
        var (w, h) = Size(seated.Length);
        _core = new SnakeArenaCore(Ctx.Rng, w, h, Seats, ApplesFor(seated.Length), _wrap, _bonus);
        _started = true;
        _over = false;
        _round = 0;
        Array.Clear(_sw);
        Array.Clear(_gone);
        NewRound(SnakeCore.StartTicks);

        // «Ще раз»: рахунок живе, поки за столом ті самі люди (порядок не важить — «Ще раз» його обертає).
        var crew = seated.Select(s => Ctx.NickOf(s)!).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!crew.SequenceEqual(_crew, StringComparer.OrdinalIgnoreCase)) _wins.Clear();
        _crew = crew;
    }

    void NewRound(int startTicks)
    {
        var core = _core!;
        var seated = Seated().Where(s => !_gone[s]).ToArray();
        core.Reset(seated, startTicks);
        _round++;
        _moves = 0;
        _pause = 0;
        _winner = null;
        _winners = [];
        Array.Clear(_place);
        Array.Clear(_respawn);
        Array.Clear(_dropAt);
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == "turn")
        {
            if (SnakeModesTurns.Dir(payload) is { } dir && _started && _winner is null) Core.Turn(seat, dir);
            return ActResult.Done;
        }
        if (action == "drop") return Drop(seat, payload);
        return ActResult.Fail("Тут так не ходять");
    }

    /// <summary>№151: вибулий кидає яблуко чи камінець. Лише в раунді «до останньої» — «на час» ніхто не вибуває надовго.</summary>
    ActResult Drop(int seat, JsonElement payload)
    {
        var core = Core;
        if (!_started || _winner is not null || core.StartIn > 0 || _timed) return ActResult.Fail("Зараз кидати нічого");
        if (seat is < 0 or >= Seats || !core.Present[seat] || core.Alive[seat]) return ActResult.Fail("Кидають лише ті, хто вибув");
        if (_moves < _dropAt[seat]) return ActResult.Fail("Ще не час — раз на 5 секунд");
        var cell = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("cell", out var c) && c.TryGetInt32(out var n) ? n : -1;
        var kind = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("k", out var k) && k.GetString() == "r" ? 'r' : 'a';
        if (core.Drop(seat, cell, kind) is { } why) return ActResult.Fail(why);
        _dropAt[seat] = _moves + DropCooldown;
        return ActResult.Done;
    }

    /// <summary>
    /// Встала посеред раунду — вибула, як розбилась; «на час» — і більше не відроджується. Лишилась одна в
    /// серії — раунд (і серія) її. Місце ще зайняте (каркас звільняє його після нас).
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (!_started || _over || seat is < 0 or >= Seats) return;
        var core = Core;
        _gone[seat] = true;
        _respawn[seat] = 0;
        var left = 0;
        for (var s = 0; s < Seats; s++) if (core.Present[s] && !_gone[s]) left++;
        if (_winner is not null)
        {
            // між раундами серії: хто лишився сам — забирає партію
            if (left <= 1) FinishSeries(true);
            return;
        }
        if (core.Alive[seat])
        {
            var rank = core.AliveCount;
            core.Kill(seat, core.Bodies[seat].Count > 0 ? core.Bodies[seat][0] : -1);
            _place[seat] = rank;
        }
        if (_timed ? left <= 1 : core.AliveCount <= 1)
        {
            if (_timed) TimeUp();
            else Settle([]);
        }
        else Ctx.Log($"{Info.Title}: {Ctx.NickOf(seat)} встає з-за столу, решта повзе далі");
    }

    public override TickResult Tick()
    {
        if (!_started || _over) return TickResult.None;
        if (_winner is not null)
        {
            if (_target <= 1) return TickResult.None;
            if (--_pause > 0) return TickResult.FrameOnly;
            NewRound(NextStartTicks);
            return TickResult.Both;
        }
        var core = Core;
        if (core.StartIn > 0)
        {
            core.StartIn--;
            return TickResult.FrameOnly;
        }

        var alive = core.AliveCount;
        var died = core.Step();
        _moves++;
        var changed = died.Count > 0;

        if (_timed)
        {
            foreach (var s in died) if (!_gone[s]) _respawn[s] = _moves + RespawnTicks;
            for (var s = 0; s < Seats; s++)
                if (_respawn[s] > 0 && _moves >= _respawn[s] && core.Respawn(s))
                {
                    _respawn[s] = 0;
                    changed = true;
                }
            if (_moves >= TimedMoves)
            {
                TimeUp();
                return TickResult.Both;
            }
            return changed ? TickResult.Both : TickResult.FrameOnly;
        }

        foreach (var s in died) _place[s] = alive - died.Count + 1;   // одночасно розбиті ділять місце
        if (core.AliveCount <= 1)
        {
            Settle(died);
            return TickResult.Both;
        }
        if (_moves >= MaxMoves)
        {
            TimeUp();
            return TickResult.Both;
        }
        return changed ? TickResult.Both : TickResult.FrameOnly;
    }

    /// <summary>
    /// Кінець раунду «до останньої»: одна — її перемога. Нуль — останні загинули в один тик: якщо це весь стіл
    /// одразу, нічия; якщо до них хтось уже вибув — останні ділять перемогу.
    /// </summary>
    void Settle(List<int> lastDied)
    {
        var core = Core;
        var present = 0;
        for (var s = 0; s < Seats; s++) if (core.Present[s]) present++;
        int[] winners;
        if (core.AliveCount == 1) winners = [Array.FindIndex(core.Alive, a => a)];
        else if (lastDied.Count > 0 && lastDied.Count < present) winners = [.. lastDied];
        else winners = [];
        RoundWon(winners, winners.Length switch
        {
            0 => $"{Info.Title}: усі врізались одночасно — нічия. {Series()}",
            1 => $"{Info.Title}: раунд бере {Ctx.NickOf(winners[0])} ({SeatName(winners[0])}). {Series(winners)}",
            _ => $"{Info.Title}: {Names(winners)} врізались останніми в один тик — очко кожній. {Series(winners)}",
        });
    }

    /// <summary>
    /// Час вийшов: перемагає найдовша серед тих, хто ще в грі (у «на час» — і ті, що саме чекають відродження:
    /// у них нуль). Рівні по довжині ділять раунд; рівні всі — нічия.
    /// </summary>
    void TimeUp()
    {
        var core = Core;
        var inGame = Enumerable.Range(0, Seats).Where(s => core.Present[s] && !_gone[s] && (_timed || core.Alive[s])).ToArray();
        if (inGame.Length == 0) inGame = [.. Enumerable.Range(0, Seats).Where(s => core.Alive[s])];
        var best = inGame.Length == 0 ? 0 : inGame.Max(s => core.Bodies[s].Count);
        var winners = inGame.Length == 1 ? inGame : inGame.Where(s => core.Bodies[s].Count == best && best > 0).ToArray();
        if (winners.Length == inGame.Length && inGame.Length > 1) winners = [];
        foreach (var s in inGame) _place[s] = winners.Contains(s) ? 1 : winners.Length + 1;
        RoundWon(winners, winners.Length == 0
            ? $"{Info.Title}: час вийшов, розійшлись внічию. {Series()}"
            : $"{Info.Title}: час вийшов — найдовша в {Names(winners, genitive: true)} ({best}). {Series(winners)}");
    }

    /// <summary>
    /// Раунд скінчився. Один раунд — кінець партії. Серія: очко переможцям, і хто набрав N — бере партію, інакше
    /// 2,5 с табло й новий раунд сам (№152).
    /// </summary>
    void RoundWon(int[] winners, string text)
    {
        _winners = winners;
        _winner = winners.Length == 0 ? "draw" : "win";
        foreach (var s in winners) _place[s] = 1;
        if (_target <= 1)
        {
            foreach (var s in winners) if (Ctx.NickOf(s) is { } nick) _wins[nick] = _wins.GetValueOrDefault(nick) + 1;
            Close(winners, text);
            return;
        }
        foreach (var s in winners) _sw[s]++;
        var best = _sw.Max();
        var left = 0;
        for (var s = 0; s < Seats; s++) if (Core.Present[s] && !_gone[s]) left++;
        if (best < _target && _round < _target * MaxRoundsPerWin && left > 1)
        {
            _pause = PauseTicks;
            return;
        }
        FinishSeries(false);
    }

    void FinishSeries(bool someoneLeft)
    {
        var core = Core;
        var inSeries = Enumerable.Range(0, Seats).Where(s => core.Present[s] && !_gone[s]).ToArray();
        int[] champs;
        if (someoneLeft && inSeries.Length == 1) champs = inSeries;
        else
        {
            var best = inSeries.Length == 0 ? 0 : inSeries.Max(s => _sw[s]);
            champs = [.. inSeries.Where(s => _sw[s] == best)];
            if (champs.Length == inSeries.Length && inSeries.Length > 1) champs = [];
        }
        foreach (var s in champs) if (Ctx.NickOf(s) is { } nick) _wins[nick] = _wins.GetValueOrDefault(nick) + 1;
        _winners = champs;
        _winner = champs.Length == 0 ? "draw" : "win";
        var who = champs.Length == 0 ? "нічия" : $"перемога — {Names(champs)}";
        Close(champs, $"{Info.Title}: серія до {_target} — {who}. {Series()}");
    }

    void Close(int[] winners, string log)
    {
        _over = true;
        _pause = 0;
        Ctx.Finish(winners, log);
    }

    string Names(IEnumerable<int> seats, bool genitive = false)
    {
        var names = seats.Select(s => Ctx.NickOf(s) is { } n ? genitive ? NickCases.Genitive(n) : n : SeatName(s)).ToList();
        return names.Count <= 1 ? string.Concat(names) : string.Join(", ", names[..^1]) + " і " + names[^1];
    }

    /// <summary>«Рахунок: Оля 2 · Петро 1» — у серії рахунок серії, інакше перемоги «Ще раз» за ніками.</summary>
    string Series(int[]? justWon = null)
    {
        var core = Core;
        var parts = Enumerable.Range(0, Seats).Where(s => core.Present[s] && Ctx.NickOf(s) is not null)
            .Select(s => $"{Ctx.NickOf(s)} {Score(s, justWon)}");
        return "Рахунок: " + string.Join(" · ", parts);
    }

    int Score(int s, int[]? justWon = null)
    {
        if (_target > 1) return _sw[s];
        var n = Ctx.NickOf(s) is { } nick ? _wins.GetValueOrDefault(nick) : 0;
        return n + (justWon is not null && justWon.Contains(s) ? 1 : 0);   // лог пишемо до того, як рахунок оновився
    }

    int[] Wins() => [.. Enumerable.Range(0, Seats).Select(s => _target > 1 ? _sw[s] : Ctx.NickOf(s) is { } n ? _wins.GetValueOrDefault(n) : 0)];

    int Mask(bool[] xs)
    {
        var m = 0;
        for (var s = 0; s < Seats; s++) if (xs[s]) m |= 1 << s;
        return m;
    }

    int SlowMask(SnakeArenaCore core)
    {
        var m = 0;
        for (var s = 0; s < Seats; s++) if (core.SlowLeft[s] > 0 && core.Alive[s]) m |= 1 << s;
        return m;
    }

    string[] Packed(SnakeArenaCore core)
    {
        var r = new string[Seats];
        for (var s = 0; s < Seats; s++) r[s] = core.Pack(s, _sb);
        return r;
    }

    /// <summary>
    /// Кадр — повний стан (тіла рядками, див. <see cref="SnakeArenaCore.Pack"/>): змійки короткі, а дельта ламалась
    /// би об ножиці й відродження. Необов'язкові поля — лише коли є що сказати.
    /// </summary>
    public override object? Frame()
    {
        var core = Core;
        var f = new Dictionary<string, object?>
        {
            ["b"] = Packed(core),
            ["ap"] = core.CellsOf(SnakeArenaCore.Apple),
            ["al"] = Mask(core.Alive),
            ["startIn"] = core.StartIn,
            ["winner"] = _winner,
        };
        Extras(core, f);
        return f;
    }

    void Extras(SnakeArenaCore core, Dictionary<string, object?> f)
    {
        var lt = core.CellsOf(SnakeArenaCore.Loot);
        if (lt.Length > 0) f["lt"] = lt;
        var rk = core.CellsOf(SnakeArenaCore.Rock);
        if (rk.Length > 0) f["rk"] = rk;
        if (core.BonusCell >= 0) f["bn"] = new[] { core.BonusCell, (int)core.BonusKind };
        var sl = SlowMask(core);
        if (sl != 0) f["sl"] = sl;
        if (_timed && _started) f["tl"] = Math.Max(0, TimedMoves - _moves);
        if (_pause > 0) f["nx"] = _pause;
        if (_timed && _respawn.Any(r => r > 0)) f["rs"] = _respawn.Select(r => r > 0 ? Math.Max(1, r - _moves) : 0).ToArray();
        if (!_timed && _winner is null && core.StartIn == 0)
        {
            // вибулим — скільки тиків до наступного кидка (0 — можна)
            int[]? dc = null;
            for (var s = 0; s < Seats; s++)
                if (core.Present[s] && !core.Alive[s] && _dropAt[s] > _moves) (dc ??= new int[Seats])[s] = _dropAt[s] - _moves;
            if (dc is not null) f["dc"] = dc;
        }
        var ev = core.Events.Where(e => e.Tick == core.Now).Select(e => new object[] { e.Kind.ToString(), e.Seat, e.Cell }).ToArray();
        if (ev.Length > 0) f["ev"] = ev;
    }

    public override object View(int? seat)
    {
        var core = Core;
        var v = new Dictionary<string, object?>
        {
            ["width"] = core.W,
            ["height"] = core.H,
            ["turn"] = null,
            ["b"] = Packed(core),
            ["dirs"] = core.Dirs.ToArray(),
            ["present"] = core.Present.ToArray(),
            ["al"] = Mask(core.Alive),
            ["crash"] = core.Crash.ToArray(),
            ["place"] = _place.ToArray(),
            ["wins"] = Wins(),
            ["ap"] = core.CellsOf(SnakeArenaCore.Apple),
            ["startIn"] = core.StartIn,
            ["winner"] = _winner,
            ["winners"] = _winners,
        };
        if (_wrap) v["wrap"] = true;
        if (_bonus) v["bonus"] = true;
        if (_timed) v["timed"] = TimedMoves;
        if (_target > 1) { v["ser"] = _target; v["round"] = _round; v["over"] = _over; }
        Extras(core, v);
        return v;
    }
}
