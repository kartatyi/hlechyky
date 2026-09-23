using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Поле на двох–чотирьох: мотоцикли або змійки. Дуельне ядро (<see cref="SnakeCore"/>) рухає рівно двох і
/// живе на фіксованому полі 26×18, а тут потрібні до чотирьох вершників, вибування посеред раунду і більше
/// поле для компанії. Дуель лишилась недоторканою — у неї ставки й Ело, і гратись вона має так само, як
/// гралась, — а це ядро повторює її правила кроку клітинка в клітинку, лише для N гравців.
/// <para>
/// Вершники живуть на МІСЦЯХ (індекс = місце за столом), бо так їх бачить клієнт і каркас. Порожнє місце
/// — вершника нема зовсім: ні сліду, ні голови.
/// </para>
/// </summary>
/// <param name="rng">Сідований генератор кімнати: яблука з тим самим сідом лягають однаково.</param>
/// <param name="w">Ширина поля в клітинках.</param>
/// <param name="h">Висота поля в клітинках.</param>
/// <param name="seats">Скільки місць за столом (довжина всіх масивів).</param>
/// <param name="tailShrinks">false — слід не зникає (мотоцикли), true — звичайна змійка.</param>
/// <param name="apples">Скільки яблук одночасно на полі; 0 — яблук нема.</param>
public sealed class ArenaCore(Random rng, int w, int h, int seats, bool tailShrinks, int apples)
{
    public const int StartLen = 3;
    public const int MaxQueued = SnakeCore.MaxQueued;

    static readonly (int Dx, int Dy)[] Deltas = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    readonly Queue<int>[] _turns = [.. Enumerable.Range(0, seats).Select(_ => new Queue<int>())];

    public int W => w;
    public int H => h;
    public int Seats => seats;
    public bool TailShrinks => tailShrinks;

    /// <summary>Клітинки кожного вершника, голова перша. Порожнє місце або з'їдена смертю змійка — порожній список.</summary>
    public List<int>[] Bodies { get; } = [.. Enumerable.Range(0, seats).Select(_ => new List<int>())];
    public int[] Dirs { get; } = new int[seats];
    /// <summary>Чи є на цьому місці вершник у цьому раунді (сидів на старті).</summary>
    public bool[] Present { get; } = new bool[seats];
    public bool[] Alive { get; } = new bool[seats];
    /// <summary>Де вершник розбився (для хрестика на полі); -1 — ще їде або його тут нема.</summary>
    public int[] Crash { get; } = [.. Enumerable.Repeat(-1, seats)];
    public List<int> Apples { get; } = [];
    public int StartIn { get; set; }

    public int AliveCount => Alive.Count(a => a);

    public int Cell(int x, int y) => y * w + x;

    /// <summary>
    /// Нова партія для тих, хто сидить. Стартові місця — за порядком серед присутніх, а не за номером
    /// місця: двоє завжди стартують так само, як у дуелі (ліворуч угорі й праворуч унизу), третій — згори,
    /// четвертий — знизу. Колони й ряди зсунуті так, щоб прямі траси ніде не перетинались в один і той самий
    /// тик: хто задумався на старті, не має влетіти в сусіда лоб у лоб через кілька кроків.
    /// </summary>
    public void Reset(IReadOnlyList<int> present, int startTicks)
    {
        for (var s = 0; s < seats; s++)
        {
            Bodies[s].Clear();
            _turns[s].Clear();
            Present[s] = Alive[s] = false;
            Crash[s] = -1;
            Dirs[s] = 0;
        }
        var starts = Starts(present.Count);
        for (var i = 0; i < present.Count && i < starts.Length; i++)
        {
            var s = present[i];
            var (x, y, dir) = starts[i];
            var (dx, dy) = Deltas[dir];
            for (var k = 0; k < StartLen; k++) Bodies[s].Add(Cell(x - dx * k, y - dy * k));   // голова попереду
            Dirs[s] = dir;
            Present[s] = Alive[s] = true;
        }
        Apples.Clear();
        if (apples > 0)
        {
            // перше яблуко — посередині, як у дуелі; решта — куди ляже з генератора
            var mid = Cell(w / 2, h / 2);
            if (!Busy().Contains(mid)) Apples.Add(mid);
            while (Apples.Count < apples && PlaceApple()) { }
        }
        StartIn = startTicks;
    }

    /// <summary>
    /// Стартові голови (x, y, напрямок). Двоє — рівно дуель: ряди H/2−3 і H/2+3 від протилежних стін.
    /// Троє й четверо — ще згори і знизу, на колонках, зсунутих від центру, щоб прямі не зустрілись в один тик.
    /// </summary>
    public (int X, int Y, int Dir)[] Starts(int n)
    {
        var a = (StartLen, h / 2 - 3, 0);
        var b = (w - 1 - StartLen, h / 2 + 3, 2);
        if (n <= 2) return [a, b];
        // на великому полі ряди розводимо трохи ширше, щоб у верхнього й нижнього було куди звернути
        a = (StartLen, h / 2 - 4, 0);
        var c = (w / 2 + 4, StartLen, 1);
        var d = (w / 2 - 5, h - 1 - StartLen, 3);
        return [a, b, c, d];
    }

    /// <summary>Поворот: розворот на 180° і повтор ігноруємо, наступний міряємо від останнього в черзі — як у дуелі.</summary>
    public void Turn(int seat, int dir)
    {
        if (dir is < 0 or > 3 || seat < 0 || seat >= seats || !Alive[seat]) return;
        var queue = _turns[seat];
        if (queue.Count >= MaxQueued) return;
        var last = queue.Count > 0 ? queue.Last() : Dirs[seat];
        if (dir == last || (dir + 2) % 4 == last) return;
        queue.Enqueue(dir);
    }

    /// <summary>
    /// Один крок усіх живих. Правила ті самі, що в дуелі: стіна, будь-яке тіло (хвіст, що цього тика
    /// звільняє клітинку, — не перешкода), голови в одну клітинку — гинуть обидва. Розбиті мотоцикли
    /// лишають слід стіною до кінця раунду; розбита змійка зникає з поля. Повертає місця, що загинули.
    /// </summary>
    public List<int> Step()
    {
        var next = new int[seats];
        var ok = new bool[seats];
        var grow = new bool[seats];
        var ate = new bool[seats];
        for (var s = 0; s < seats; s++)
        {
            if (!Alive[s]) continue;
            if (_turns[s].Count > 0) Dirs[s] = _turns[s].Dequeue();
            (next[s], ok[s]) = Ahead(Bodies[s][0], Dirs[s]);
            ate[s] = apples > 0 && ok[s] && Apples.Contains(next[s]);
            grow[s] = ate[s] || !tailShrinks;
        }

        var busy = new HashSet<int>();
        for (var s = 0; s < seats; s++)
        {
            var body = Bodies[s];
            if (body.Count == 0) continue;
            // живий, що не росте, звільняє хвіст того ж тика; мертвий мотоцикл — стіна цілком
            var take = Alive[s] && !grow[s] ? body.Count - 1 : body.Count;
            for (var i = 0; i < take; i++) busy.Add(body[i]);
        }

        var died = new List<int>();
        for (var s = 0; s < seats; s++)
        {
            if (!Alive[s]) continue;
            var dead = !ok[s] || busy.Contains(next[s]);
            for (var o = 0; o < seats && !dead; o++)
                if (o != s && Alive[o] && ok[o] && next[o] == next[s]) dead = true;
            if (dead) died.Add(s);
        }

        var eaten = false;
        for (var s = 0; s < seats; s++)
        {
            if (!Alive[s] || !ok[s]) continue;
            Bodies[s].Insert(0, next[s]);
            if (!grow[s]) Bodies[s].RemoveAt(Bodies[s].Count - 1);
            if (ate[s] && Apples.Remove(next[s])) eaten = true;
        }
        foreach (var s in died) Kill(s, ok[s] ? next[s] : Bodies[s][0]);
        if (eaten || Apples.Count < apples) while (Apples.Count < apples && PlaceApple()) { }
        return died;
    }

    /// <summary>Вершник вибув (врізався або встав з-за столу). Змійка при цьому зникає з поля, мотоцикл — ні.</summary>
    public void Kill(int seat, int at)
    {
        if (!Alive[seat]) return;
        Alive[seat] = false;
        Crash[seat] = at;
        _turns[seat].Clear();
        if (tailShrinks) Bodies[seat].Clear();
    }

    public (int Cell, bool Ok) Ahead(int head, int dir)
    {
        var (dx, dy) = Deltas[dir];
        var (x, y) = (head % w + dx, head / w + dy);
        return x < 0 || x >= w || y < 0 || y >= h ? (head, false) : (Cell(x, y), true);
    }

    HashSet<int> Busy()
    {
        var busy = new HashSet<int>(Apples);
        foreach (var b in Bodies) busy.UnionWith(b);
        return busy;
    }

    /// <summary>Нове яблуко на вільній клітинці. false — поле забите вщерть.</summary>
    public bool PlaceApple()
    {
        var busy = Busy();
        if (busy.Count >= w * h) return false;
        int cell;
        do { cell = rng.Next(w * h); } while (busy.Contains(cell));
        Apples.Add(cell);
        return true;
    }
}

/// <summary>
/// Спільне для «Мотоциклів гуртом» і «Змійок гуртом»: 2–4 гравці, старт на «Почати», вибування посеред
/// раунду, раунд бере останній, хто лишився. Дуелі (<see cref="TronGame"/>, <see cref="SnakeGame"/>) цим не
/// стали навмисно: там ставки й Ело, а каркас дає їх лише грі рівно на двох (Rooms.ReadStake,
/// Rewards.Elo) — розширивши дуель, ми забрали б у неї те, заради чого в неї грають.
/// </summary>
public abstract class ArenaGame : Game
{
    public const int Seats = 4;
    /// <summary>Поле для двох — те саме, що в дуелі.</summary>
    public const int SmallW = SnakeCore.W, SmallH = SnakeCore.H;
    /// <summary>Поле для компанії: на 26×18 четверо мотоциклів розбиваються за кілька секунд.</summary>
    public const int BigW = 34, BigH = 24;

    protected static GameOption FieldOption => new("field", "Поле",
        [("auto", "Під склад"), ("small", "Мале 26×18"), ("big", "Велике 34×24")], "auto");

    /// <summary>true — змійки: хвіст іде за головою, є яблука; false — мотоцикли: слід не зникає.</summary>
    protected abstract bool Tails { get; }
    protected abstract int StartTicks { get; }
    /// <summary>Скільки кроків раунд може тривати, поки не спрацює страховка.</summary>
    public abstract int MaxMoves { get; set; }
    /// <summary>Підписи місць (рід — під назву вершника).</summary>
    protected abstract string[] Colors { get; }

    ArenaCore? _core;
    string _field = "auto";
    bool _started;
    int _moves;
    /// <summary>Рахунок серії за ніками: «Ще раз» обертає місця, а перемоги мають їхати з людиною.</summary>
    readonly Dictionary<string, int> _wins = new(StringComparer.OrdinalIgnoreCase);
    string[] _crew = [];
    /// <summary>Місце у раунді: 1 — переможець; null — ще їде або його нема.</summary>
    readonly int?[] _place = new int?[Seats];
    /// <summary>null — раунд триває; "win" — є переможець(і), "draw" — нічия.</summary>
    string? _winner;
    int[] _winners = [];

    public int Moves => _moves;

    public override string SeatName(int seat) => seat >= 0 && seat < Colors.Length ? Colors[seat] : base.SeatName(seat);

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _field = options.TryGetValue("field", out var f) && f is "small" or "big" ? f : "auto";
    }

    /// <summary>Хто зараз сидить — у порядку місць.</summary>
    int[] Seated() => [.. Enumerable.Range(0, Seats).Where(Ctx.Seated)];

    (int W, int H) Size(int players) => _field switch
    {
        "small" => (SmallW, SmallH),
        "big" => (BigW, BigH),
        _ => players <= 2 ? (SmallW, SmallH) : (BigW, BigH),
    };

    int ApplesFor(int players) => !Tails ? 0 : players <= 2 ? 1 : 2;

    /// <summary>
    /// Поле є ще до старту: стіл, що чекає на гравців, показує, хто де стартує. До «Почати» ядро щоразу
    /// будується під тих, хто зараз сидить (без генератора — яблука з'являться на старті), після — живе своє.
    /// </summary>
    ArenaCore Core
    {
        get
        {
            if (_core is not null && _started) return _core;
            var seated = Seated();
            var (w, h) = Size(seated.Length);
            if (_core is null || _core.W != w || _core.H != h || !_core.Present.Select((p, i) => p == seated.Contains(i)).All(x => x))
            {
                _core = new ArenaCore(new Random(0), w, h, Seats, Tails, 0);
                _core.Reset(seated, StartTicks);
            }
            return _core;
        }
    }

    public override void Start()
    {
        var seated = Seated();
        var (w, h) = Size(seated.Length);
        _core = new ArenaCore(Ctx.Rng, w, h, Seats, Tails, ApplesFor(seated.Length));
        _core.Reset(seated, StartTicks);
        _started = true;
        _moves = 0;
        _winner = null;
        _winners = [];
        Array.Clear(_place);

        // Серія живе, поки за столом ті самі люди (порядок не важить — «Ще раз» його обертає).
        var crew = seated.Select(s => Ctx.NickOf(s)!).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!crew.SequenceEqual(_crew, StringComparer.OrdinalIgnoreCase)) _wins.Clear();
        _crew = crew;
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action != "turn") return ActResult.Fail("Тут так не ходять");
        if (SnakeModesTurns.Dir(payload) is { } dir && _started && _winner is null) Core.Turn(seat, dir);
        return ActResult.Done;
    }

    /// <summary>
    /// Встав посеред раунду — вибув, як врізався, але партія для решти не зупиняється. Лишився один — раунд
    /// його. Місце ще зайняте (каркас звільняє його після нас), тож у результаті той, хто встав, — серед
    /// тих, хто програв.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (!_started || _winner is not null) return;
        var core = Core;
        if (seat < 0 || seat >= Seats || !core.Alive[seat]) return;
        var rank = core.AliveCount;
        core.Kill(seat, core.Bodies[seat].Count > 0 ? core.Bodies[seat][0] : -1);
        _place[seat] = rank;
        if (core.AliveCount <= 1) Settle([]);
        else Ctx.Log($"{Info.Title}: {Ctx.NickOf(seat)} встав з-за столу, решта їде далі");
    }

    public override TickResult Tick()
    {
        if (!_started || _winner is not null) return TickResult.None;
        var core = Core;
        if (core.StartIn > 0)
        {
            core.StartIn--;
            return TickResult.FrameOnly;
        }

        var alive = core.AliveCount;
        var died = core.Step();
        _moves++;
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
        // хтось вибув — повний вид, щоб усі побачили хрестик і місце; інакше вистачить кадру
        return died.Count > 0 ? TickResult.Both : TickResult.FrameOnly;
    }

    /// <summary>
    /// Кінець раунду, коли живих лишилось ≤ 1. Один — його перемога. Нуль — останні загинули в один тик:
    /// якщо це весь стіл одразу, нічия; якщо до них хтось уже вибув — останні ділять перемогу, бо
    /// протрималися довше за решту.
    /// </summary>
    void Settle(List<int> lastDied)
    {
        var core = Core;
        var present = Enumerable.Range(0, Seats).Where(s => core.Present[s]).ToArray();
        int[] winners;
        if (core.AliveCount == 1) winners = [Array.FindIndex(core.Alive, a => a)];
        else if (lastDied.Count > 0 && lastDied.Count < present.Length) winners = [.. lastDied];
        else winners = [];

        foreach (var s in winners) _place[s] = 1;
        foreach (var s in winners)
            if (Ctx.NickOf(s) is { } nick) _wins[nick] = _wins.GetValueOrDefault(nick) + 1;
        Close(winners, winners.Length switch
        {
            0 => $"{Info.Title}: усі врізались одночасно — нічия. {Series()}",
            1 => $"{Info.Title}: раунд бере {Ctx.NickOf(winners[0])} ({SeatName(winners[0])}). {Series()}",
            _ => $"{Info.Title}: {Names(winners)} врізались останніми в один тик — очко кожному. {Series()}",
        });
    }

    /// <summary>
    /// Страховка від вічного раунду. У мотоциклах поле закінчується раніше, ніж вона спрацює; змійки ж
    /// можуть кружляти скільки завгодно — тоді перемагає найдовша, а рівні по довжині ділять раунд.
    /// </summary>
    void TimeUp()
    {
        var core = Core;
        var alive = Enumerable.Range(0, Seats).Where(s => core.Alive[s]).ToArray();
        var best = alive.Max(s => core.Bodies[s].Count);
        var winners = !Tails ? [] : alive.Where(s => core.Bodies[s].Count == best).ToArray();
        if (winners.Length == alive.Length) winners = [];
        foreach (var s in alive) _place[s] = winners.Contains(s) ? 1 : winners.Length + 1;
        foreach (var s in winners)
            if (Ctx.NickOf(s) is { } nick) _wins[nick] = _wins.GetValueOrDefault(nick) + 1;
        Close(winners, winners.Length == 0
            ? $"{Info.Title}: час вийшов, розійшлись внічию. {Series()}"
            : $"{Info.Title}: час вийшов — найдовша в {Names(winners)}. {Series()}");
    }

    void Close(int[] winners, string log)
    {
        _winners = winners;
        _winner = winners.Length == 0 ? "draw" : "win";
        Ctx.Finish(winners, log);
    }

    string Names(IEnumerable<int> seats)
    {
        var names = seats.Select(s => Ctx.NickOf(s) ?? SeatName(s)).ToList();
        return names.Count <= 1 ? string.Concat(names) : string.Join(", ", names[..^1]) + " і " + names[^1];
    }

    /// <summary>«Рахунок: Оля 2 · Петро 1 · Іра 0» — лише коли за столом більше двох або серія вже йде.</summary>
    string Series()
    {
        var core = Core;
        var parts = Enumerable.Range(0, Seats).Where(s => core.Present[s] && Ctx.NickOf(s) is not null)
            .Select(s => $"{Ctx.NickOf(s)} {_wins.GetValueOrDefault(Ctx.NickOf(s)!)}");
        return "Рахунок: " + string.Join(" · ", parts);
    }

    int[] Wins() => [.. Enumerable.Range(0, Seats).Select(s => Ctx.NickOf(s) is { } n ? _wins.GetValueOrDefault(n) : 0)];

    /// <summary>
    /// Мотоцикли: кадр — дельта (голови й маска живих), сліди живуть у клієнта, як у дуелі. Змійки короткі
    /// — їм дешевше слати повний стан.
    /// </summary>
    public override object? Frame()
    {
        var core = Core;
        var mask = 0;
        for (var s = 0; s < Seats; s++) if (core.Alive[s]) mask |= 1 << s;
        if (!Tails)
            return new
            {
                h = Enumerable.Range(0, Seats).Select(s => core.Bodies[s].Count > 0 ? core.Bodies[s][0] : -1).ToArray(),
                al = mask,
                startIn = core.StartIn,
                winner = _winner,
            };
        return new
        {
            t = core.Bodies.Select(b => b.ToArray()).ToArray(),
            ap = core.Apples.ToArray(),
            al = mask,
            startIn = core.StartIn,
            winner = _winner,
        };
    }

    public override object View(int? seat)
    {
        var core = Core;
        var mask = 0;
        for (var s = 0; s < Seats; s++) if (core.Alive[s]) mask |= 1 << s;
        return new
        {
            width = core.W,
            height = core.H,
            turn = (int?)null,
            mode = Tails ? "snake" : "tron",
            t = core.Bodies.Select(b => b.ToArray()).ToArray(),
            dirs = core.Dirs.ToArray(),
            present = core.Present.ToArray(),
            al = mask,
            crash = core.Crash.ToArray(),
            place = _place.ToArray(),
            wins = Wins(),
            ap = core.Apples.ToArray(),
            startIn = core.StartIn,
            winner = _winner,
            winners = _winners,
        };
    }
}

/// <summary>Мотоцикли на 2–4: слід лишається стіною до кінця раунду, раунд бере останній, хто їде.</summary>
public sealed class TronPartyGame : ArenaGame
{
    public override GameInfo Info { get; } = new(
        "tron-party", "Мотоцикли гуртом", "мотоцикли гуртом", GameGroup.Live, 2, Seats, TickMs: TronGame.TickMs,
        Start: StartMode.ByHost, Options: [FieldOption],
        Hint: "Мотоцикли на 2–4: за кожним тягнеться стіна. Врізався — вибув, раунд бере останній, хто їде. Стрілки або WASD",
        Client: "snake-modes");

    protected override bool Tails => false;
    protected override int StartTicks => TronGame.StartTicks;
    /// <summary>Дві хвилини: на великому полі слід заповнює його значно раніше, це лише страховка.</summary>
    public override int MaxMoves { get; set; } = 1200;
    protected override string[] Colors => ["жовтий", "зелений", "синій", "рожевий"];
}

/// <summary>Змійки на 2–4: яблука, хвіст за головою, розбита змійка зникає; раунд бере остання жива.</summary>
public sealed class SnakePartyGame : ArenaGame
{
    public override GameInfo Info { get; } = new(
        "snake-party", "Змійки гуртом", "змійки гуртом", GameGroup.Live, 2, Seats, TickMs: SnakeCore.TickMs,
        Start: StartMode.ByHost, Options: [FieldOption],
        Hint: "Змійки на 2–4: їж яблука, не врізайся. Раунд бере остання жива, а за три хвилини — найдовша. Стрілки або WASD",
        Client: "snake-modes");

    protected override bool Tails => true;
    protected override int StartTicks => SnakeCore.StartTicks;
    /// <summary>Три хвилини по 120 мс — далі перемагає найдовша.</summary>
    public override int MaxMoves { get; set; } = 1500;
    protected override string[] Colors => ["жовта", "зелена", "синя", "рожева"];
}
