namespace Hlechyky.Games.Impl;

/// <summary>
/// Бот-змійка для «🤖 + бот» — і в дуелі (<see cref="SnakeGame"/>), і на арені (<see cref="SnakePartyGame"/>). Ядра в
/// цих ігор різні, тому бот думає над знімком поля, який гра складає перед кожним рішенням: <see cref="Begin"/>, далі
/// <see cref="Body"/> / <see cref="Block"/> / <see cref="Food"/>, і <see cref="Decide"/>. Повертає лише напрямок — гра
/// подає його тим самим поворотом, що й людина (та сама черга, ті самі 120 мс), тож чітів фізики нема.
///
/// Як обирає: з трьох ходів (прямо, ліворуч, праворуч) відкидає смертельні, а для решти заливкою «з часом» рахує,
/// скільки місця попереду і як далеко найближче яблуко. «З часом» — бо клітинка тіла звільниться через стільки кроків,
/// скільки від неї до хвоста: без цього змійка боялася б власного хвоста, що саме від неї втікає, і сама себе
/// заганяла б у кут. Місця менше, ніж треба (довжина + запас рівня), — хід майже заборонений; досить — веде до яблука.
/// Сильний, коли суперник поруч, ще й ділить з ним поле (хто куди встигне першим) і забирає собі більшу частину —
/// так він відрізає. Помилки — «хід навмання» з частотою рівня: бот, що не помиляється ніколи, — поганий бот.
/// Масиви — одні на все життя бота, заливки без алокацій.
/// </summary>
public sealed class SnakeBrain
{
    /// <summary>Клітинка, що не звільниться ніколи (камінь, стіна мапи).</summary>
    public const int Wall = int.MaxValue;

    /// <summary>
    /// Рівень: скільки клітинок хоче бачити попереду понад свою довжину, скільки ходів навмання на тисячу рішень,
    /// як боїться лоба в лоб (очки проти 10 за крок до яблука) і чи ділить поле з суперником.
    /// </summary>
    readonly record struct Style(int Room, int Blunders, int HeadFear, bool Cut);

    static readonly Style[] Styles =
    [
        new(4, 15, 120, false),    // легкий: бачить мало, часто помиляється — новачок переграє
        new(14, 4, 800, false),    // звичайний: не лізе в тупики, зрідка схибить
        new(40, 2, 800, true),     // сильний: запас місця великий, відрізає
    ];

    /// <summary>Ближче скількох кроків до суперника сильний починає ділити поле замість гнатися за яблуком.</summary>
    const int CutRange = 10;
    /// <summary>Далі заливка не шукає: яблуко за 250 клітинок однаково далеке для всіх трьох ходів, а тик має бути дешевим.</summary>
    const int Horizon = 250;

    int _w, _h;
    bool _wrap;
    int[] _free = [], _stamp = [], _queue = [], _depth = [];
    /// <summary>Сусіди клітинки (4 на клітинку, −1 — край поля): без ділення й остачі в заливках.</summary>
    int[] _nb = [];
    byte[] _own = [];
    bool[] _food = [];
    readonly List<int> _rivals = [];
    int _mark;

    /// <summary>Новий знімок поля: усе вільне, їжі й суперників нема.</summary>
    public void Begin(int w, int h, bool wrap)
    {
        var n = w * h;
        if (_free.Length != n)
        {
            _free = new int[n]; _stamp = new int[n]; _queue = new int[n]; _depth = new int[n];
            _own = new byte[n]; _food = new bool[n];
            _mark = 0;
        }
        if (_nb.Length != 4 * n || _w != w || _wrap != wrap)
        {
            (_w, _h, _wrap) = (w, h, wrap);
            _nb = new int[4 * n];
            for (var c = 0; c < n; c++)
                for (var d = 0; d < 4; d++)
                    _nb[c * 4 + d] = Step(c, d) is (var to, true) ? to : -1;
        }
        Array.Clear(_free);
        Array.Clear(_food);
        _rivals.Clear();
    }

    /// <summary>
    /// Тіло (голова перша): клітинка i звільниться через «довжина − i + ще доросте» кроків. Суперникові даємо
    /// крок запасу — він може саме з'їсти яблуко й не потягнути хвоста; його голову бот пам'ятає (лоб у лоб, поділ поля).
    /// </summary>
    public void Body(IReadOnlyList<int> body, int grow, bool rival)
    {
        var n = body.Count;
        for (var i = 0; i < n; i++)
        {
            var c = body[i];
            var t = n - i + grow + (rival ? 1 : 0);
            if (t > _free[c]) _free[c] = t;
        }
        if (rival && n > 0) _rivals.Add(body[0]);
    }

    public void Block(int cell) => _free[cell] = Wall;

    public void Food(int cell) => _food[cell] = true;

    (int Cell, bool Ok) Ahead(int c, int dir) => _nb[c * 4 + dir] is var n && n >= 0 ? (n, true) : (c, false);

    (int Cell, bool Ok) Step(int c, int dir)
    {
        var (x, y) = (c % _w, c / _w);
        switch (dir)
        {
            case 0: x++; break;
            case 1: y++; break;
            case 2: x--; break;
            default: y--; break;
        }
        if (_wrap) return (((y + _h) % _h) * _w + (x + _w) % _w, true);
        return x < 0 || x >= _w || y < 0 || y >= _h ? (c, false) : (y * _w + x, true);
    }

    int Dist(int a, int b)
    {
        var dx = Math.Abs(a % _w - b % _w);
        var dy = Math.Abs(a / _w - b / _w);
        if (_wrap) { dx = Math.Min(dx, _w - dx); dy = Math.Min(dy, _h - dy); }
        return dx + dy;
    }

    /// <summary>
    /// Рішення бота з головою <paramref name="head"/>, що повзе в <paramref name="dir"/>: новий напрямок або null —
    /// повзти далі. Випадковість — лише з <paramref name="rng"/> (генератор кімнати, тести детерміновані).
    /// </summary>
    public int? Decide(int head, int dir, int len, LiveBots.Level level, Random rng)
    {
        var st = Styles[LiveBots.Index(level)];
        if (rng.Next(1000) < st.Blunders)
        {
            var d = (dir + 3 + rng.Next(3)) % 4;   // ліворуч, прямо чи праворуч — не дивлячись
            return d == dir ? null : d;
        }
        var need = len + st.Room;
        var close = false;
        if (st.Cut) foreach (var r in _rivals) if (Dist(head, r) <= CutRange) close = true;

        int best = dir;
        var bestScore = long.MinValue;
        for (var i = 0; i < 3; i++)
        {
            var d = i == 0 ? dir : i == 1 ? (dir + 1) % 4 : (dir + 3) % 4;
            var (next, ok) = Ahead(head, d);
            long score;
            if (!ok || _free[next] > 1) score = -1_000_000 + rng.Next(3);
            else
            {
                var (area, food) = Flood(next, need);
                score = area >= need ? 0 : (area - need) * 60L;
                score -= (food < 0 ? _w + _h : food) * 10L;
                if (close) score += Territory(next) * 4L;
                if (NearRival(next)) score -= st.HeadFear;
                if (i == 0) score += 3;       // без причини не крутиться
                score += rng.Next(4);
            }
            if (score > bestScore) { bestScore = score; best = d; }
        }
        return best == dir ? null : best;
    }

    /// <summary>
    /// Заливка з клітинки першого кроку: скільки місця (досить <paramref name="need"/>) і за скільки кроків найближча
    /// їжа (−1 — не видно). Клітинку на глибині k можна пройти, якщо вона звільниться не пізніше k-го кроку.
    /// </summary>
    (int Area, int Food) Flood(int from, int need)
    {
        _mark++;
        int head = 0, tail = 0, food = -1;
        _queue[tail++] = from;
        _stamp[from] = _mark;
        _depth[from] = 1;
        while (head < tail)
        {
            var c = _queue[head++];
            var k = _depth[c];
            if (food < 0 && _food[c]) food = k;
            if (tail >= need && (food >= 0 || tail >= Horizon)) break;
            for (var d = 0; d < 4; d++)
            {
                var n = _nb[c * 4 + d];
                if (n < 0 || _stamp[n] == _mark || _free[n] > k + 1) continue;
                _stamp[n] = _mark;
                _depth[n] = k + 1;
                _queue[tail++] = n;
            }
        }
        return (tail, food);
    }

    /// <summary>
    /// Поділ поля: скільки клітинок бот, ступивши на <paramref name="from"/>, досягне раніше за будь-якого суперника
    /// (вони рушають з голів тим самим тиком). Нічиї клітинки не рахуються нікому. Рахуємо лише найближчі
    /// <see cref="Horizon"/> клітинок заливки: відрізають поблизу, а повна заливка великого поля тричі на тик — зайва.
    /// </summary>
    int Territory(int from)
    {
        _mark++;
        int head = 0, tail = 0, mine = 0;
        foreach (var r in _rivals)
        {
            if (_stamp[r] == _mark) continue;
            _stamp[r] = _mark; _depth[r] = 0; _own[r] = 2;
            _queue[tail++] = r;
        }
        if (_stamp[from] == _mark) return 0;
        _stamp[from] = _mark; _depth[from] = 1; _own[from] = 1;
        _queue[tail++] = from;
        while (head < tail && head < Horizon)
        {
            var c = _queue[head++];
            var o = _own[c];
            if (o == 1) mine++;
            if (o == 3) continue;
            var k = _depth[c];
            for (var d = 0; d < 4; d++)
            {
                var n = _nb[c * 4 + d];
                if (n < 0 || _free[n] > k + 1) continue;
                if (_stamp[n] == _mark)
                {
                    // рахуємо клітинку, лише коли дістаємо її з черги — тож нічия ще встигає її забрати
                    if (_depth[n] == k + 1 && _own[n] != o) _own[n] = 3;
                    continue;
                }
                _stamp[n] = _mark; _depth[n] = k + 1; _own[n] = o;
                _queue[tail++] = n;
            }
        }
        return mine;
    }

    bool NearRival(int cell)
    {
        foreach (var r in _rivals)
            for (var d = 0; d < 4; d++)
                if (Ahead(r, d) is (var c, true) && c == cell) return true;
        return false;
    }
}
