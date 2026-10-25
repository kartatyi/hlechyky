namespace Hlechyky.Games.Impl;

/// <summary>Один розріз від браузера: плід, номер змаху й прапорець «через центр» (v2 §2).</summary>
public readonly record struct ShatkuyCut(int Id, int W, bool Precise);

/// <summary>
/// Поле одного шаткаря (docs/games/specs/shatkuy.md, контракт _tools/shatkuy-v2.md): розклад, плоди в повітрі, розрізи,
/// промахи, особливі, серія й свій ігровий час (мороз сповільнює лише це поле). Без <c>Ctx</c>: соло-ігри тримають одне
/// поле, «Шаткуй разом» — по полю на місце з тим самим сідом. Час — справжні мс від t0 (<c>t</c>), ігровий — <see cref="G"/>.
/// </summary>
public sealed class ShatkuyField(ShatkuyMode mode)
{
    public ShatkuyMode Mode { get; } = mode;
    ShatkuyPlan? _plan;
    readonly Dictionary<int, ShatkuyFruit> _byId = [];
    /// <summary>Ще не розрізані й не впалі — їх <see cref="Advance"/> перевіряє на промах.</summary>
    readonly List<ShatkuyFruit> _air = [];
    /// <summary>Відомі, але ще за обрієм виду (At &gt; g + LookMs): вид треба розіслати, щойно котрийсь увійде в обрій (#68).</summary>
    readonly List<ShatkuyFruit> _soon = [];
    readonly HashSet<int> _cut = [], _missed = [];
    /// <summary>Тріснуті гарбузи: перший удар — яким змахом і коли (справжні мс).</summary>
    readonly Dictionary<int, (int W, int T)> _cracked = [];
    /// <summary>Удари по кожному великому (кавун, велетень) і змахи, що вже вдарили.</summary>
    readonly Dictionary<int, int> _bigOf = [];
    readonly HashSet<(int Id, int W)> _bigWs = [];
    readonly Dictionary<int, int> _combo = [];
    readonly List<(int S, int E)> _frost = [], _double = [], _burn = [];
    int _planSeen, _showers, _bigAt = -1_000_000;
    int _nextLife = ShatkuyCore.LifeEvery, _nextGiant = ShatkuyCore.GiantEvery;

    public ulong Seed => _plan?.Seed ?? 0;
    public bool Started => _plan is not null;
    public bool Over { get; private set; }
    public string Why { get; private set; } = "";
    public int EndT { get; private set; }
    public int Score { get; private set; }
    public int Misses { get; private set; }
    public int BestCombo { get; private set; }
    public int Gleks { get; private set; }
    public int Cuts { get; private set; }
    /// <summary>Усі удари по великих (кавун Ярмарку й велетні Класики).</summary>
    public int BigHits { get; private set; }
    /// <summary>Удари саме по великому кавуну (ачівка «Кавунище»).</summary>
    public int KavunHits { get; private set; }
    public int Streak { get; private set; }
    public int BestStreak { get; private set; }
    public int Precise { get; private set; }
    /// <summary>Останнє комбо ≥ 3 і коли (справжні мс) — суперникам «комбо ×5!».</summary>
    public int LastCombo { get; private set; }
    public int LastComboT { get; private set; } = -1_000_000;
    /// <summary>Щось змінилось — вид варто розіслати.</summary>
    public bool Dirty { get; set; }
    /// <summary>Новий плід увійшов в обрій (чи злива, зубчики, кінець) — вид треба розіслати не відкладаючи.</summary>
    public bool Urgent { get; set; }

    public IReadOnlyCollection<ShatkuyFruit> Fruits => _byId.Values;
    public bool IsCut(int id) => _cut.Contains(id);
    public bool IsCracked(int id) => _cracked.ContainsKey(id);
    public IReadOnlyList<(int S, int E)> Frost => _frost;
    public IReadOnlyList<(int S, int E)> Burn => _burn;
    public int Mult => Mode.Series ? ShatkuyCore.Mult(Streak) : 1;

    /// <summary>Ігрова мс поля для справжньої <paramref name="t"/>.</summary>
    public int G(int t) => ShatkuyCore.Game(Math.Max(0, t), _frost);

    public void Reset()
    {
        _plan = null;
        _byId.Clear(); _air.Clear(); _soon.Clear(); _cut.Clear(); _missed.Clear(); _cracked.Clear(); _bigOf.Clear(); _bigWs.Clear();
        _combo.Clear(); _frost.Clear(); _double.Clear(); _burn.Clear();
        _planSeen = _showers = 0;
        _bigAt = -1_000_000;
        _nextLife = ShatkuyCore.LifeEvery;
        _nextGiant = ShatkuyCore.GiantEvery;
        Over = false;
        Why = "";
        Score = Misses = BestCombo = Gleks = Cuts = BigHits = KavunHits = Streak = BestStreak = Precise = EndT = LastCombo = 0;
        LastComboT = -1_000_000;
        Dirty = Urgent = true;
    }

    /// <summary>Нова партія з розкладом від <paramref name="seed"/>; перша хвиля — одразу в обрій (#68: <c>Grow(0)</c>).</summary>
    public void Begin(ulong seed)
    {
        Reset();
        _plan = new ShatkuyPlan(Mode, seed);
        Grow(0);
    }

    /// <summary>Підсумок, відновлений зі збереження (Шаткуй дня): партії нема, лише цифри.</summary>
    public void Restore(int score, int cuts, int combo, int big, int gleks, string why, int streak, int precise)
    {
        Reset();
        Over = true;
        Score = score; Cuts = cuts; BestCombo = combo; BigHits = KavunHits = big; Gleks = gleks; Why = why; BestStreak = streak; Precise = precise;
    }

    /// <summary>Закрити партію поля (час, промахи, глек у Класиці, пішов).</summary>
    public void Close(string why, int t)
    {
        if (Over) return;
        Over = true;
        Why = why;
        EndT = t;
        Dirty = Urgent = true;
    }

    // ---------- розклад ----------

    void Put(ShatkuyFruit f)
    {
        if (!_byId.TryAdd(f.Id, f)) return;
        if (!f.Big) _air.Add(f);
        _soon.Add(f);
        Dirty = true;
    }

    public void Grow(int g)
    {
        if (_plan is null) return;
        _plan.Until(g + ShatkuyCore.LookMs);
        while (_planSeen < _plan.Fruits.Count) Put(_plan.Fruits[_planSeen++]);
        // плід хвилі, що вилітає пізніше за обрій, інакше доходив би лише з наступною хвилею — уже в польоті
        if (_soon.RemoveAll(f => f.At <= g + ShatkuyCore.LookMs) > 0) Dirty = Urgent = true;
    }

    /// <summary>
    /// Прожити поле до справжньої <paramref name="t"/>: докинути розклад, порахувати впалі (промах рве серію, у Класиці —
    /// ще й життя; перець пече), кінець часу. Під час відліку (t &lt; 0) — лише розклад наперед.
    /// </summary>
    public void Advance(int t)
    {
        if (_plan is null || Over) return;
        var g = G(t);
        Grow(g);
        if (t < 0) return;
        for (var i = _air.Count - 1; i >= 0 && !Over; i--)
        {
            var f = _air[i];
            if (f.Until >= g) continue;
            _air.RemoveAt(i);
            if (f.Glek) continue;
            if (f.K == ShatkuyCore.Pepper)
            {
                // пропущений перець: життя не бере, але пече й рве серію
                ShatkuyCore.AddSpan(_burn, t, ShatkuyCore.BurnMs);
                Streak = 0;
                Dirty = Urgent = true;
                continue;
            }
            if (!ShatkuyCore.IsMiss(f.K)) continue;
            if (Streak > 0) { Streak = 0; Dirty = true; }
            if (!Mode.Lives) continue;
            _missed.Add(f.Id);
            Misses++;
            Dirty = true;
            if (Misses >= ShatkuyCore.Lives) Close(ShatkuyGame.WhyMiss, t);
        }
        if (!Over && Mode.Timed && g >= Mode.LenMs + ShatkuyCore.LagMs) Close(ShatkuyGame.WhyTime, t);
    }

    // ---------- розріз ----------

    /// <summary>Чи можна ще різати на справжній <paramref name="t"/> (час режиму не вийшов).</summary>
    public bool TimeLeft(int t) => !Mode.Timed || G(t) <= Mode.LenMs + ShatkuyCore.LagMs;

    /// <summary>
    /// Пачка розрізів на справжній <paramref name="t"/>: плід ріжеться, лише якщо вже вилетів і ще в повітрі за ігровим
    /// часом поля (з запасом <see cref="ShatkuyCore.LagMs"/>). Майбутній, уже розрізаний, невідомий — мовчки ні.
    /// Скільки плодів зачепило (тріщина гарбуза й удар по великому — теж).
    /// </summary>
    public int CutMany(IEnumerable<ShatkuyCut> cuts, int t, int max)
    {
        if (_plan is null || Over) return 0;
        var g = G(t);
        Grow(g);
        var any = 0;
        var n = 0;
        foreach (var c in cuts)
        {
            if (++n > max || Over) break;
            if (!_byId.TryGetValue(c.Id, out var f) || g < f.At - 100 || g > f.Until) continue;
            if (Cut(f, c.W, c.Precise, t, g)) any++;
        }
        if (any > 0) Dirty = true;
        return any;
    }

    bool Cut(ShatkuyFruit f, int w, bool precise, int t, int g)
    {
        var dbl = Mode.Specials && ShatkuyCore.Inside(_double, t) ? 2 : 1;
        if (f.Big)
        {
            // удар — раз на змах і не частіше за BigGapMs (браузер дає кожному заходу леза свій номер)
            var hits = _bigOf.GetValueOrDefault(f.Id);
            if (hits >= ShatkuyCore.BigMaxHits || t - _bigAt < ShatkuyCore.BigGapMs || !_bigWs.Add((f.Id, w))) return false;
            _bigAt = t;
            _bigOf[f.Id] = hits + 1;
            BigHits++;
            if (f.K == ShatkuyCore.Big) KavunHits++;
            Score += dbl;
            AfterScore();
            return true;
        }
        if (_cut.Contains(f.Id)) return false;
        if (f.K == ShatkuyCore.Pumpkin)
        {
            // гарбуз: перший удар — тріщина (+0), другий — іншим змахом і не раніше CrackGapMs
            if (!_cracked.TryGetValue(f.Id, out var cr))
            {
                _cracked[f.Id] = (w, t);
                return true;
            }
            if (cr.W == w || t - cr.T < ShatkuyCore.CrackGapMs) return false;
        }
        _cut.Add(f.Id);
        _air.Remove(f);
        if (f.Glek)
        {
            Gleks++;
            Streak = 0;
            if (Mode.Lives) Close(ShatkuyGame.WhyGlek, t);
            else Score = Math.Max(0, Score - ShatkuyCore.GlekPenalty);
            return true;
        }
        Cuts++;
        Streak++;
        BestStreak = Math.Max(BestStreak, Streak);
        // серія множить лише основну ціну; комбо (третій одним змахом +3, далі по +1) і точний удар — зверху
        var gain = ShatkuyCore.Price(f.K) * Mult;
        var c = _combo[w] = _combo.GetValueOrDefault(w) + 1;
        if (c == 3) gain += 3;
        else if (c > 3) gain += 1;
        if (c >= 3) { LastCombo = c; LastComboT = t; }
        BestCombo = Math.Max(BestCombo, c >= 3 ? c : 0);
        if (_combo.Count > 12) foreach (var old in _combo.Keys.Where(k => k < w - 8).ToList()) _combo.Remove(old);
        if (precise) { gain += 1; Precise++; }
        Score += gain * dbl;
        switch (f.K)
        {
            case ShatkuyCore.Frost: ShatkuyCore.AddFrost(_frost, t); Urgent = true; break;
            case ShatkuyCore.Double: ShatkuyCore.AddSpan(_double, t, ShatkuyCore.DoubleMs); Urgent = true; break;
            case ShatkuyCore.Shower:
                foreach (var s in ShatkuyCore.ShowerOf(_plan!.Seed, ++_showers, g)) Put(s);
                Urgent = true;
                break;
            case ShatkuyCore.Garlic:
                foreach (var s in ShatkuyCore.ClovesOf(_plan!.Seed, f, g)) Put(s);
                Urgent = true;
                break;
        }
        AfterScore();
        return true;
    }

    /// <summary>Класика: кожні сто очок — життя назад і гарбуз-велетень наступною хвилею.</summary>
    void AfterScore()
    {
        if (Mode.Lives)
            while (Score >= _nextLife)
            {
                if (Misses > 0) Misses--;
                _nextLife += ShatkuyCore.LifeEvery;
            }
        if (Mode.Giants && _plan is not null)
            while (Score >= _nextGiant)
            {
                _plan.QueueGiant();
                _nextGiant += ShatkuyCore.GiantEvery;
            }
    }

    // ---------- вид ----------

    static double R(double x) => Math.Round(x, 3);

    /// <summary>Плоди в обрії виду на ігровій <paramref name="g"/> (наступна секунда й щойно впалі).</summary>
    public object[] FruitsView(int g) =>
        [.. _byId.Values.Where(f => f.At <= g + ShatkuyCore.LookMs && f.Until + 400 >= g).OrderBy(f => f.Id).Select(FruitView)];

    object FruitView(ShatkuyFruit f)
    {
        var d = new Dictionary<string, object?>
        {
            ["id"] = f.Id, ["k"] = f.K.ToString(), ["at"] = f.At, ["d"] = f.D, ["x0"] = R(f.X0), ["x1"] = R(f.X1), ["h"] = R(f.H), ["sp"] = R(f.Spin),
            ["c"] = _cut.Contains(f.Id), ["m"] = _missed.Contains(f.Id),
        };
        // нові поля — лише там, де є що сказати: старий клієнт їх не бачить, вид не роздувається
        if (f.K == ShatkuyCore.Pumpkin && _cracked.ContainsKey(f.Id) && !_cut.Contains(f.Id)) d["cr"] = true;
        if (f.K == ShatkuyCore.Clove) d["from"] = (f.Id - ShatkuyCore.CloveBase) / 4;
        if (f.Big) d["bh"] = _bigOf.GetValueOrDefault(f.Id);
        return d;
    }

    /// <summary>Чи плід зараз у повітрі неторканий (для ботів).</summary>
    public IEnumerable<ShatkuyFruit> Flying(int g) =>
        _byId.Values.Where(f => !_cut.Contains(f.Id) && f.At <= g && g <= f.At + f.D && (!f.Big || _bigOf.GetValueOrDefault(f.Id) < ShatkuyCore.BigMaxHits));

    public static int[][] Spans(IReadOnlyList<(int S, int E)> spans) => [.. spans.Select(s => new[] { s.S, s.E })];

    public int[][] DoubleSpans => Spans(_double);
}
