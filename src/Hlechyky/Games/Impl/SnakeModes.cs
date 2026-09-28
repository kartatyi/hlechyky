using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Дрібниці, спільні для обох режимів змійки. Клієнт шле <c>{ dir: 2 }</c>, але голе число теж приймаємо —
/// так само, як це робить дуель у <see cref="SnakeGame"/>: одна форма payload на всю родину.
/// </summary>
static class SnakeModesTurns
{
    public static int? Dir(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetInt32(out var n) => n,
        JsonValueKind.Object when payload.TryGetProperty("dir", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n) => n,
        _ => null,
    };

    /// <summary>Той самий склад за столом, що й минулого разу — від цього залежить, чи жити рахунку серії.</summary>
    public static bool Same(string?[] a, string?[] b) =>
        a.Length == b.Length && a.Zip(b).All(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Мотоцикли вдвох — рейтингова дуель на ставки. З проходу №3 їздить на спільному ядрі гурту
/// (<see cref="ArenaCore"/> на 26×18, двоє стартують рівно там, де стартували завжди), бо мапи, турбо, серія
/// раундів і «хто кого підрізав» потрібні обом. Правила кроку ті самі клітинка в клітинку. Опції столу видно
/// обом ще до того, як другий сяде, тож рейтингова партія чесна: однакові правила для обох, а серія «до 3/5»
/// — це одна партія каркаса з одним результатом (одна ставка, одна зміна Ело).
/// </summary>
public sealed class TronGame : ArenaGame
{
    /// <summary>Швидше за змійку: слід росте щотика, і на 120 мс поле закінчувалось би надто мляво.</summary>
    public const int TickMs = 100;
    /// <summary>Три секунди «готуйсь».</summary>
    public const int StartTicks = 30;

    public override GameInfo Info { get; } = new(
        "tron", "Мотоцикли", "мотоцикли", GameGroup.Live, 2, 2, TickMs: TickMs, Rated: true,
        Options: [SeriesOption, ArenaMaps.Option, TurboOption],
        Hint: "За тобою тягнеться стіна, яка не зникає. Хто врізався перший — програв. Стрілки або WASD",
        Client: "snake-modes");   // усі режими малює один web/games/snake-modes.js

    protected override bool Tails => false;
    protected override int CountdownTicks => StartTicks;
    /// <summary>
    /// Хвилина на раунд. Двом слідам на полі в 468 клітинок стільки не протриматись — це страховка від
    /// вічного раунду; тест опускає число і проходить нічию по-справжньому.
    /// </summary>
    public override int MaxMoves { get; set; } = 600;
    protected override string[] Colors => ["жовтий", "зелений"];


    /// <summary>«x», «o», «draw» або null — та сама мова, що й у дуелі змійки.</summary>
    string? XO => Outcome is null ? null : WinnerSeats.Length == 1 ? (WinnerSeats[0] == 0 ? "x" : "o") : "draw";

    /// <summary>«Мотоцикли: Оля жовтий 2:1 Петро зелений» — рахунок з боку переможця.</summary>
    string Score(int won)
    {
        var lost = 1 - won;
        var w = Wins();
        return $"{Ctx.NickOf(won)} {SeatName(won)} {w[won]}:{w[lost]} {Ctx.NickOf(lost)} {SeatName(lost)}";
    }

    protected override string RoundText(int[] winners) => winners.Length == 1
        ? $"{Info.Title}: {Score(winners[0])}"
        : $"{Info.Title}: {Ctx.NickOf(0)} {SeatName(0)} і {Ctx.NickOf(1)} {SeatName(1)} врізались одночасно";

    protected override string TimeUpText(int[] winners) =>
        $"{Info.Title}: хвилина минула, {Ctx.NickOf(0)} і {Ctx.NickOf(1)} розійшлись внічию";

    protected override string SeriesText(int[] champs) => champs.Length == 1
        ? $"{Info.Title}: серія до {Target} — {Score(champs[0])}"
        : $"{Info.Title}: серія до {Target} — нічия, {Wins()[0]}:{Wins()[1]}";

    protected override string LeaveText(int seat, int[] winners) =>
        $"{Info.Title}: {Ctx.NickOf(seat)} встає з-за столу, партію не дограли";

    protected override void ViewExtra(Dictionary<string, object?> view)
    {
        var w = Wins();
        view["duel"] = true;
        view["winsA"] = w[0];
        view["winsB"] = w[1];
        view["dirA"] = Arena.Dirs[0];
        view["dirB"] = Arena.Dirs[1];
        view["winner"] = XO;
    }

    protected override void FrameExtra(Dictionary<string, object?> frame) => frame["winner"] = XO;
}

/// <summary>
/// Одна змійка на двох. Ядро дуелі тут не годиться: воно завжди рухає дві змійки й тримає дві черги
/// поворотів, а нам потрібна одна змійка й одна спільна черга — інакше два натиски різних гравців злипались
/// би в один тик. Геометрію поля (розмір, крок через стіну) беремо з <see cref="SnakeCore"/>, щоб мотоцикли,
/// дуель і кооп грали на тому самому полі.
/// </summary>
/// <param name="rng">Сідований генератор кімнати: яблука з тим самим сідом лягають однаково.</param>
public sealed class CoopSnakeCore(Random rng)
{
    const int StartLen = 3;
    /// <summary>Далі вже не пам'ять гравця, а хвіст лагу — та сама межа, що й у дуелі.</summary>
    public const int MaxQueued = SnakeCore.MaxQueued;
    /// <summary>Скільки тиків «готуйсь»: 25 × 120 мс — рівно три секунди, як у дуелі.</summary>
    public const int StartTicks = SnakeCore.StartTicks;

    /// <summary>Спільна черга на двох: чий поворот прийшов першим, той і застосується першим тиком.</summary>
    readonly Queue<int> _turns = new();

    /// <summary>Клітинки змійки, голова перша.</summary>
    public List<int> S { get; } = [];
    public int Dir { get; set; }
    public int Apple { get; set; } = -1;
    public int StartIn { get; set; } = StartTicks;
    public int Len => S.Count;

    /// <summary>Чия це вісь у парі: місце 0 крутить вгору-вниз (1 і 3), місце 1 — вліво-вправо (0 і 2).</summary>
    public static bool OwnAxis(int seat, int dir) => seat == 0 ? dir is 1 or 3 : seat == 1 && dir is 0 or 2;

    /// <summary>Скільки місць за столом (1–4).</summary>
    public const int Seats = 4;
    const int Right = 1 << 0, Down = 1 << 1, Left = 1 << 2, Up = 1 << 3;

    /// <summary>
    /// Кнопки кожного місця — маска напрямків (біт 0 праворуч … біт 3 вгору). Роздаються за кількістю тих,
    /// хто сидить: сам — усі чотири; двоє — вертикаль і горизонталь, як було завжди; троє — вертикаль, ліво,
    /// право; четверо — по одній стрілці на брата. Типово — пара, щоб старі виклики поводились як раніше.
    /// </summary>
    public int[] Keys { get; } = [Up | Down, Left | Right, 0, 0];

    /// <summary>Розкладка для тих, хто зараз за столом (у порядку місць). Порожні місця кнопок не мають.</summary>
    public static int[] Layout(IReadOnlyList<int> seated)
    {
        int[] masks = seated.Count switch
        {
            <= 1 => [Up | Down | Left | Right],
            2 => [Up | Down, Left | Right],
            3 => [Up | Down, Left, Right],
            _ => [Up, Right, Down, Left],
        };
        var keys = new int[Seats];
        for (var i = 0; i < seated.Count && i < masks.Length; i++)
            if (seated[i] is >= 0 and < Seats) keys[seated[i]] = masks[i];
        return keys;
    }

    public void Assign(IReadOnlyList<int> seated) => Layout(seated).CopyTo(Keys, 0);

    /// <summary>Чи це кнопка цього місця.</summary>
    public bool Mine(int seat, int dir) => seat is >= 0 and < Seats && dir is >= 0 and <= 3 && (Keys[seat] & (1 << dir)) != 0;

    /// <summary>Підпис місця за маскою — це і є вся інструкція в чіпі над полем.</summary>
    public static string KeysName(int mask) => mask switch
    {
        Up | Down | Left | Right => "усі стрілки",
        Up | Down => "вгору-вниз",
        Left | Right => "вліво-вправо",
        Up => "вгору",
        Down => "вниз",
        Left => "вліво",
        Right => "вправо",
        _ => "",
    };

    /// <summary>Нова партія: змійка посеред поля, дивиться праворуч; яблуко — куди лягло з генератора.</summary>
    public void Reset()
    {
        S.Clear();
        var y = SnakeCore.H / 2;
        for (var i = 0; i < StartLen; i++) S.Add(SnakeCore.Cell(SnakeCore.W / 2 - i, y));   // голова праворуч від хвоста
        Dir = 0;
        _turns.Clear();
        StartIn = StartTicks;
        Apple = -1;
        PlaceApple();
    }

    /// <summary>
    /// Гравець просить повернути. Чужа вісь — мовчки повз (це не помилка, а домовленість: один крутить
    /// вертикаль, другий горизонталь). Далі те саме, що в дуелі: розворот на 180° і повтор не беремо, а
    /// кожен наступний поворот міряємо від останнього в черзі, щоб два швидкі натиски не злипались.
    /// </summary>
    public bool Turn(int seat, int dir)
    {
        if (!Mine(seat, dir)) return false;
        if (_turns.Count >= MaxQueued) return false;
        var last = _turns.Count > 0 ? _turns.Last() : Dir;
        if (dir == last || (dir + 2) % 4 == last) return false;
        _turns.Enqueue(dir);
        return true;
    }

    /// <summary>Один крок змійки. Повертає true, якщо цим кроком вона врізалась.</summary>
    public bool Step()
    {
        if (_turns.Count > 0) Dir = _turns.Dequeue();
        var (next, ok) = SnakeCore.Ahead(S[0], Dir);
        if (!ok) return true;                       // стіна: голову за поле не пхаємо

        var ate = next == Apple;
        // Хвіст звільняє клітинку того ж тика, коли голова рушила — якщо змійка не росте.
        var body = S.Take(S.Count - (ate ? 0 : 1)).ToHashSet();
        var dead = body.Contains(next);

        S.Insert(0, next);
        if (!ate) S.RemoveAt(S.Count - 1);
        else PlaceApple();
        return dead;
    }

    /// <summary>Нове яблуко на вільній клітинці. Випадковість — тільки з переданого генератора.</summary>
    public void PlaceApple()
    {
        var busy = S.ToHashSet();
        if (busy.Count >= SnakeCore.W * SnakeCore.H) { Apple = -1; return; }
        int cell;
        do { cell = rng.Next(SnakeCore.W * SnakeCore.H); } while (busy.Contains(cell));
        Apple = cell;
    }
}

/// <summary>
/// Змійка на всіх: одна змійка, одне яблуко, від одного до чотирьох за кермом. Кнопки діляться між тими,
/// хто сидить (<see cref="CoopSnakeCore.Layout"/>): удвох — вертикаль і горизонталь, як було від початку,
/// вчотирьох — по одній стрілці кожному. Партія не рейтингова: тут нема з ким змагатись, зате є спільний
/// рекорд — довжина, з якою змійка врізалась.
/// </summary>
public sealed class SnakeCoopGame : Game
{
    public override GameInfo Info { get; } = new(
        "snake-coop", "Змійка на всіх", "змійку на всіх", GameGroup.Live, 1, CoopSnakeCore.Seats,
        TickMs: SnakeCore.TickMs, Start: StartMode.ByHost, Rated: false, Score: ScoreOrder.HigherIsBetter, Coop: true,
        Hint: "Одна змійка на всіх: кожен крутить лише свої стрілки (удвох — вгору-вниз і вліво-вправо, вчотирьох — по одній). Домовляйтесь!",
        Client: "snake-modes");

    CoopSnakeCore? _core;
    /// <summary>Раунд дограно: змійка врізалась. Далі тикати нема чого.</summary>
    bool _over;
    bool _started;

    CoopSnakeCore Core
    {
        get
        {
            if (_core is not null) return _core;
            _core = new CoopSnakeCore(Ctx.Rng);
            _core.Reset();
            return _core;
        }
    }

    int[] Seated() => [.. Enumerable.Range(0, CoopSnakeCore.Seats).Where(Ctx.Seated)];

    /// <summary>
    /// Кнопки місця: у партії — роздані на старті (і перероздані, коли хтось встав), у лобі — такі, які
    /// дістануться, якщо почати зараз. Так чіп над полем ще до старту каже, що кому крутити.
    /// </summary>
    int KeysOf(int seat) =>
        _started ? Core.Keys[seat] : CoopSnakeCore.Layout(Seated())[seat];

    /// <summary>Підпис місця — це і є вся інструкція: людина бачить свої стрілки в чіпі над полем.</summary>
    public override string SeatName(int seat)
    {
        if (seat is < 0 or >= CoopSnakeCore.Seats) return base.SeatName(seat);
        var mask = KeysOf(seat);
        // вільне місце підписуємо тим, що дістанеться новенькому, якщо він сяде
        if (mask == 0) mask = CoopSnakeCore.Layout([.. Seated().Append(seat).Order()])[seat];
        return CoopSnakeCore.KeysName(mask);
    }

    public override void Start()
    {
        _over = false;
        _started = true;
        Core.Reset();
        Core.Assign(Seated());
    }

    /// <summary>
    /// Хтось встав — змійка не зупиняється: його стрілки перероздаються тим, хто лишився (останній отримує
    /// всі чотири). Раніше вихід напарника закривав раунд і ще й записував тому, хто лишився, «перемогу» в
    /// кооперативі, де перемагати нема кого. Місце ще зайняте (каркас звільнить його після нас), тож
    /// рахуємо без нього.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (_over) return;
        Core.Assign([.. Seated().Where(s => s != seat)]);
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action != "turn") return ActResult.Fail("Тут так не ходять");
        if (SnakeModesTurns.Dir(payload) is not { } dir) return ActResult.Done;
        // Чужа кнопка — не поламаний хід, а звичайне «це не твоє»: кажемо коротко й не міняємо стану.
        if (!Core.Mine(seat, dir))
            return ActResult.Fail("Ти крутиш " + CoopSnakeCore.KeysName(Core.Keys[seat]));
        Core.Turn(seat, dir);
        return ActResult.Done;
    }

    public override TickResult Tick()
    {
        if (_over) return TickResult.None;
        if (Core.StartIn > 0)
        {
            Core.StartIn--;
            return TickResult.FrameOnly;
        }
        if (!Core.Step()) return TickResult.FrameOnly;

        _over = true;
        var len = Core.Len;
        var seated = Seated();
        // Кооп: результатом партії йде довжина — з неї і збереться таблиця.
        foreach (var s in seated) Ctx.Score(s, len);
        // Переможців тут нема: у кооперативі програти одне одному неможливо, а «перемога» всім коштувала б
        // дорого — ачівки перемог («Перша перемога», «Серія», «Десять перемог») каркас видає повз стелю
        // черепків (Economy/Rewards.cs: цикл ачівок стоїть поза `if (rewarded)`), тож компанія вибивала б їх
        // за кілька хвилин у грі, де не можна програти. Порожній список — це нічия: DrawReward усім і
        // жодних перемог. Довжина від цього не губиться, вона йде окремо, у Scores.
        var names = seated.Select(s => Ctx.NickOf(s)!).ToList();
        var who = names.Count switch
        {
            0 => "",
            1 => names[0],
            _ => string.Join(", ", names[..^1]) + " і " + names[^1],
        };
        Ctx.Finish([],
            names.Count == 1
                ? $"{Info.Title}: {who} наодинці — змійка доросла до {len}"
                : $"{Info.Title}: {who} виростили змійку до {len}",
            seated.ToDictionary(s => s, _ => (long)len), verdict: $"🐍 Змійка доросла до {len}");
        return TickResult.Both;
    }

    /// <summary>Змійка одна й росте лише з яблук, тож повний стан у кадрі коштує дешево — дельта тут зайва.</summary>
    public override object? Frame() => new
    {
        s = Core.S.ToArray(),
        apple = Core.Apple,
        dir = Core.Dir,
        startIn = Core.StartIn,
        len = Core.Len,
        winner = Winner,
    };

    public override object View(int? seat) => new
    {
        width = SnakeCore.W,
        height = SnakeCore.H,
        turn = (int?)null,
        s = Core.S.ToArray(),
        apple = Core.Apple,
        dir = Core.Dir,
        startIn = Core.StartIn,
        len = Core.Len,
        winner = Winner,
        // маска стрілок кожного місця (біт 0 праворуч … біт 3 вгору): клієнт показує лише свої кнопки
        keys = Enumerable.Range(0, CoopSnakeCore.Seats).Select(KeysOf).ToArray(),
    };

    /// <summary>Переможця тут нема — є кінець раунду. Клієнтові цього досить, щоб притемнити поле.</summary>
    string? Winner => _over ? "end" : null;
}
