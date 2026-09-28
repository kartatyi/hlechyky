using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Правила покрокової гри на сітці: «постав свою мітку і збери Need підряд». Різниця між нашими
/// покроковими іграми лише в розмірі поля і в тому, чи падає фішка вниз, як у «Чотирьох у ряд».
/// </summary>
/// <param name="Need">Скільки в ряд треба зібрати.</param>
/// <param name="Gravity">Ходом називають колонку, а фішка падає на найнижче вільне місце.</param>
/// <param name="Keep">Скільки міток гравець тримає на полі: поставив ще одну — найстаріша щезає. 0 — не щезає нічого.</param>
public sealed record GridRules(int Width, int Height, int Need, bool Gravity, int Keep = 0)
{
    public int Cells => Width * Height;
}

/// <summary>
/// Спільна основа наших покрокових ігор на сітці. Клітинки лишились рядками «x»/«o», як були: так вид
/// читається очима в тестах і в консолі браузера. Для столу на компанію з'являються ще «c» і «d» — ті самі
/// літери, що й класи чіпів місць у каркасі (x, o, c, d).
/// </summary>
public abstract class GridGame : Game
{
    static readonly string[] Letters = ["x", "o", "c", "d"];

    protected abstract GridRules Rules { get; }
    /// <summary>Що намальовано в клітинці кожного місця: ✕/◯ у хрестиках, фішки в «Чотирьох».</summary>
    protected abstract string[] Marks { get; }

    string?[] _cells = [];
    /// <summary>Зникаючий режим: зайняті клітинки в порядку появи, щоб знати, чия черга щезати.</summary>
    List<int>? _order;
    int _turn;
    /// <summary>«x», «o», … , «draw» або null, поки грають — те саме, що бачив старий фронт.</summary>
    string? _winner;
    /// <summary>Хто саме виграв (у парах — обидва напарники); для виду.</summary>
    int[] _won = [];
    int[]? _line;
    /// <summary>Куди ліг останній хід — браузер його підсвічує, а в «Чотирьох» ще й кидає фішку згори.</summary>
    int? _last;
    /// <summary>Останній хід зробив годинник, а не гравець (простояв свої 20 с).</summary>
    bool _lastAuto;
    /// <summary>Хто ще в грі. На двох — завжди обидва; за столом на компанію той, хто встав, випадає з черги.</summary>
    bool[] _in = [];
    /// <summary>Імена Глеків, що підсіли на порожні місця (null — людина або пусто).</summary>
    string?[] _bots = [];
    readonly Series _series = new();

    /// <summary>Годинник ходу в секундах (0 — без годинника): простояв — фішка падає у випадкову колонку.</summary>
    protected int ClockSeconds { get; private set; }
    /// <summary>Скільки Глеків просили посадити на порожні місця.</summary>
    protected int BotsWanted { get; private set; }
    /// <summary>Пари 2×2: місця 0 і 2 проти 1 і 3, ряд із фішок напарника теж рахується.</summary>
    protected bool Teams { get; private set; }
    DateTimeOffset? _turnUntil;
    DateTimeOffset? _botAt;

    static string Mark(int seat) => Letters[seat];

    /// <summary>Скільки місць за цим столом (на двох — два, на компанію — до чотирьох).</summary>
    int Seats => Info.MaxPlayers;

    /// <summary>Перед кожною партією: стіл на компанію тут обирає розмір поля за кількістю гравців.</summary>
    protected virtual void Prepare(int players) { }

    /// <summary>Опції зчитуємо спільно: годинник, Глеки й пари є лише в тих «Чотирьох», що їх оголосили.</summary>
    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        ClockSeconds = options.TryGetValue("clock", out var c) && c is "20" ? 20 : 0;
        BotsWanted = Info.Options?.Any(o => o.Key == "bots") == true ? BoardBots.Read(options, Seats - 1) : 0;
        Teams = options.TryGetValue("teams", out var t) && t == "1" && Seats == 4;
    }

    public override void Start()
    {
        _bots = BoardBots.Seat(Ctx, Seats, Teams ? Math.Min(BotsWanted, 4) : BotsWanted);
        _in = [.. Enumerable.Range(0, Seats).Select(s => Ctx.Seated(s) || _bots[s] is not null)];
        // Пари — лише коли за столом рівно четверо; утрьох грають кожен сам за себе.
        _pairs = Teams && _in.All(x => x);
        Prepare(_in.Count(x => x));
        _cells = new string?[Rules.Cells];
        _order = Rules.Keep > 0 ? [] : null;
        // Починає перше зайняте місце: за столом на компанію, де сіли троє з чотирьох, «нульове» може пустувати.
        _turn = Array.IndexOf(_in, true) is var first && first >= 0 ? first : 0;
        _winner = null;
        _won = [];
        _line = null;
        _last = null;
        _lastAuto = false;
        _series.Begin(Ctx, Seats);
        Arm();
    }

    bool _pairs;

    /// <summary>Напарник у парах 2×2 (через одного), інакше — сам.</summary>
    int Partner(int seat) => _pairs ? (seat + 2) % 4 : seat;

    bool IsBot(int seat) => seat >= 0 && seat < _bots.Length && _bots[seat] is not null;
    public override string? SeatBot(int seat) => IsBot(seat) ? _bots[seat] : null;

    /// <summary>Ім'я за столом: нік людини або «Глек 🤖».</summary>
    protected string Nick(int seat) => IsBot(seat) ? _bots[seat]! : Ctx.NickOf(seat) ?? SeatName(seat);

    /// <summary>Черга змінилась: заводимо годинник людині або «думку» Глекові.</summary>
    void Arm()
    {
        var now = Ctx.Clock.UtcNow;
        _turnUntil = null;
        _botAt = null;
        if (_winner is not null) return;
        if (IsBot(_turn)) _botAt = now.AddMilliseconds(BoardBots.ThinkMs);
        else if (ClockSeconds > 0) _turnUntil = now.AddSeconds(ClockSeconds);
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_winner is not null) return ActResult.Fail("Партію зіграно, тисни «Ану ще раз»");
        if (action == "resign") return Resign(seat);
        if (action == BoardBots.Nudge) return BotTurn();
        if (action == "timeout") return Timeout();
        if (action != "move") return ActResult.Fail("Тут так не ходять");
        if (seat != _turn) return ActResult.Fail("Не так швидко — зараз не твій хід");
        if (Cell(payload) is not { } cell) return ActResult.Fail("Не зрозумів, куди ходити");
        return Move(seat, cell, auto: false);
    }

    /// <summary>Хід, коли його перевірено: людини, годинника чи Глека.</summary>
    ActResult Move(int seat, int cell, bool auto)
    {
        var index = Place(cell);
        if (index < 0) return ActResult.Fail(Rules.Gravity ? "Ця колонка вже повна" : "Ця клітинка вже зайнята");

        var mark = Mark(seat);
        _cells[index] = mark;
        _order?.Add(index);
        _last = index;
        _lastAuto = auto;
        // Найстаріша мітка щезає ще до підрахунку ряду: виграти тим, чого вже нема на полі, не можна.
        Vanish(mark);

        if (WinLine(index, seat) is { } line)
        {
            _winner = mark;
            _line = line;
            if (_pairs)
            {
                var mate = Partner(seat);
                Finish([seat, mate], $"{Info.Title}: пара {Nick(seat)} і {Nick(mate)} збирає {Rules.Need} в ряд і бере партію");
            }
            else Finish([seat], Seats == 2
                // Ніки чужі, відмінювати їх нема як, тому рахунок замість речення з відмінками.
                ? $"{Info.Title}: {Nick(seat)} {SeatName(seat)} 1:0 {Nick(Other(seat))} {SeatName(Other(seat))}"
                : $"{Info.Title}: {Nick(seat)} ({SeatName(seat)}) збирає {Rules.Need} в ряд і бере партію");
            return auto ? ActResult.Done : ActResult.Accept("Твоя взяла!");
        }
        if (_cells.All(c => c is not null))
        {
            _winner = "draw";
            Finish([], $"{Info.Title}: {Players()} зіграли внічию");
            return auto ? ActResult.Done : ActResult.Accept("Нічия! Ану ще раз?");
        }
        _turn = Next(seat);
        Arm();
        return ActResult.Done;
    }

    /// <summary>
    /// Годинник ходу: хто простояв свої секунди, за того фішка падає у випадкову колонку. Тика в гри нема — про кінець
    /// часу каже клієнт котрогось гравця, а сервер сам перевіряє, що час справді вийшов.
    /// </summary>
    ActResult Timeout()
    {
        if (_turnUntil is not { } until || Ctx.Clock.UtcNow < until) return ActResult.Fail("Час ще є");
        var free = FreeMoves();
        if (free.Count == 0) return ActResult.Fail("Ходити нікуди");
        return Move(_turn, free[Ctx.Rng.Next(free.Count)], auto: true);
    }

    /// <summary>Глек, чия черга, ходить — якщо вже «подумав».</summary>
    ActResult BotTurn()
    {
        if (!IsBot(_turn) || _botAt is not { } at || Ctx.Clock.UtcNow < at) return ActResult.Fail("Глек ще думає");
        var col = GridBot.Pick(Ctx.Rng, _cells, Rules, Mark(_turn), Mark(Partner(_turn)), Foes(_turn));
        if (col < 0) return ActResult.Fail("Ходити нікуди");
        return Move(_turn, col, auto: false);
    }

    /// <summary>Мітки суперників у порядку черги після цього місця (найближчий — перший).</summary>
    string[] Foes(int seat)
    {
        var foes = new List<string>();
        for (var i = 1; i < Seats; i++)
        {
            var s = (seat + i) % Seats;
            if (_in[s] && s != Partner(seat)) foes.Add(Mark(s));
        }
        return [.. foes];
    }

    /// <summary>Куди можна сходити: колонки (у «Чотирьох») або клітинки.</summary>
    List<int> FreeMoves()
    {
        var list = new List<int>();
        if (Rules.Gravity) { for (var c = 0; c < Rules.Width; c++) if (_cells[c] is null) list.Add(c); }
        else for (var c = 0; c < _cells.Length; c++) if (_cells[c] is null) list.Add(c);
        return list;
    }

    /// <summary>На двох — суперник; для рядка Журналу.</summary>
    static int Other(int seat) => seat == 0 ? 1 : 0;

    /// <summary>Наступне місце в черзі, що ще грає.</summary>
    int Next(int seat)
    {
        for (var i = 1; i <= Seats; i++)
        {
            var s = (seat + i) % Seats;
            if (_in[s]) return s;
        }
        return seat;
    }

    string Players()
    {
        var names = Enumerable.Range(0, Seats).Where(s => _in[s]).Select(s => $"{Nick(s)} {SeatName(s)}").ToList();
        return names.Count <= 1 ? string.Join("", names) : string.Join(", ", names.Take(names.Count - 1)) + " і " + names[^1];
    }

    void Finish(int[] winners, string text)
    {
        _won = winners;
        _turnUntil = _botAt = null;
        // Місце Глека в каркасі порожнє: ні серії, ні вечора, ні черепків він не бере (ніка нема).
        _series.Record(Ctx, winners);
        Ctx.Finish(winners, text);
    }

    /// <summary>
    /// Здатись. На двох — перемога суперникові; за столом на компанію гравець просто випадає з черги,
    /// а його фішки лишаються на полі перешкодою. Коли лишився один — він і виграв.
    /// </summary>
    ActResult Resign(int seat)
    {
        if (seat < 0 || seat >= Seats || !_in[seat] || IsBot(seat)) return ActResult.Fail("Ти вже поза грою — дивись, хто кого");
        Drop(seat, "здається");
        return ActResult.Accept(_winner is null ? "Ти вибуваєш — решта грає далі" : "Партію віддано — ану ще раз?");
    }

    const string Left = "встає з-за столу";

    void Drop(int seat, string why)
    {
        _in[seat] = false;
        var left = Enumerable.Range(0, Seats).Where(s => _in[s]).ToArray();
        // У парах пара жива, поки за столом хоч один із двох.
        var sides = _pairs ? left.Select(s => s % 2).Distinct().Count() : left.Length;
        // Людей не лишилось — Глеки самі з собою не догравають.
        var people = left.Any(s => !IsBot(s));
        if (sides <= 1 || !people)
        {
            _winner = left.Length >= 1 && (sides == 1 || left.Length == 1) ? Mark(left[0]) : "draw";
            var won = _winner == "draw" ? [] : _pairs ? new[] { left[0] % 2, left[0] % 2 + 2 } : left;
            // На двох вихід пишемо так само, як каркас пише техпоразку в усіх іграх (Game.OnLeave).
            Finish(won, won.Length >= 1 && (Seats > 2 || why != Left)
                ? $"{Info.Title}: {Nick(seat)} {why}, " + (_pairs
                    ? $"пара {Nick(won[0])} і {Nick(won[1])} перемагає"
                    : $"{Nick(won[0])} {SeatName(won[0])} перемагає")
                : $"{Info.Title}: {Nick(seat)} {why}, партію не дограли");
            return;
        }
        Ctx.Log($"{Info.Title}: {Nick(seat)} {why}, решта грає далі");
        if (_turn == seat)
        {
            _turn = Next(seat);
            Arm();
        }
    }

    public override void OnLeave(int seat)
    {
        if (_winner is not null || seat < 0 || seat >= _in.Length || !_in[seat]) return;
        Drop(seat, Left);
    }

    /// <summary>Хід приймаємо і як <c>{cell:4}</c>, і як голе число — клієнтам так простіше.</summary>
    static int? Cell(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetInt32(out var n) => n,
        JsonValueKind.Object when payload.TryGetProperty("cell", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) => n,
        _ => null,
    };

    public override object View(int? seat) => new
    {
        width = Rules.Width,
        height = Rules.Height,
        need = Rules.Need,
        cells = (string?[])_cells.Clone(),
        turn = _winner is null ? _turn : (int?)null,
        marks = (string[])Marks.Clone(),
        line = _line is null ? null : (int[])_line.Clone(),
        fading = Fading(),
        winner = _winner,
        // Хто саме виграв — у парах це двоє.
        won = (int[])_won.Clone(),
        last = _last,
        auto = _lastAuto,
        // Хто ще в грі (на компанію хтось міг здатись); на двох — завжди обидва, поки партія йде.
        active = (bool[])_in.Clone(),
        // Годинник ходу: скільки секунд на хід і до коли (ISO) — лише коли чекаємо людину.
        clock = ClockSeconds,
        until = _turnUntil,
        // Те саме в мілісекундах від «зараз»: годинник браузера буває не в ногу з сервером.
        clockIn = _turnUntil is { } tu ? Math.Max(0, (int)(tu - Ctx.Clock.UtcNow).TotalMilliseconds) : (int?)null,
        // Імена Глеків на місцях (null — людина) і коли Глек, чия черга, уже «подумав».
        bots = _bots.Any(b => b is not null) ? (string?[])_bots.Clone() : null,
        botIn = _botAt is { } at ? Math.Max(0, (int)(at - Ctx.Clock.UtcNow).TotalMilliseconds) : (int?)null,
        pairs = _pairs,
        series = _series.View(Ctx, Seats),
    };
    /// <summary>Зникаючий режим: гравець поставив зайву мітку — найстаріша його щезає з поля.</summary>
    void Vanish(string mark)
    {
        if (_order is null) return;
        var mine = _order.Where(c => _cells[c] == mark).ToList();
        if (mine.Count <= Rules.Keep) return;
        _cells[mine[0]] = null;
        _order.Remove(mine[0]);
    }

    /// <summary>Яка мітка щезне наступним ходом: найстаріша в того, хто ходить, коли він уже набрав ліміт.</summary>
    int? Fading()
    {
        if (_order is null || _winner is not null) return null;
        var mine = _order.Where(c => _cells[c] == Mark(_turn)).ToList();
        return mine.Count >= Rules.Keep ? mine[0] : null;
    }

    /// <summary>Куди насправді ляже хід: у грі з гравітацією cell — це колонка, інакше сама клітинка. -1 — так не можна.</summary>
    int Place(int cell)
    {
        var (w, h) = (Rules.Width, Rules.Height);
        if (!Rules.Gravity) return cell >= 0 && cell < _cells.Length && _cells[cell] is null ? cell : -1;
        if (cell < 0 || cell >= w) return -1;
        for (var row = h - 1; row >= 0; row--)
            if (_cells[row * w + cell] is null) return row * w + cell;
        return -1;
    }

    /// <summary>Ряд потрібної довжини через щойно поставлену клітинку, або null. У парах свої — і фішки напарника.</summary>
    int[]? WinLine(int index, int seat) => GridBot.Line(_cells, Rules, index, Mark(seat), Mark(Partner(seat)));
}

/// <summary>Хрестики-нолики: три на три, три в ряд, нічия буває.</summary>
public sealed class TicTacToe : GridGame
{
    public override GameInfo Info { get; } = new(
        "ttt", "Хрестики-нолики", "хрестики-нолики", GameGroup.Board, 2, 2, Rated: true,
        Hint: "Стіл рівно на двох: хто поставив — за ✕, хто сів другим — за ◯.");

    protected override GridRules Rules { get; } = new(3, 3, 3, false);
    protected override string[] Marks { get; } = ["✕", "◯"];

    public override string SeatName(int seat) => seat == 0 ? "✕" : "◯";
}

/// <summary>
/// Те саме поле, але кожен тримає на ньому лише три мітки: поставив четверту — найстаріша щезає.
/// Нічия неможлива, партія триває, поки хтось не збере ряд.
/// </summary>
public sealed class FadingTicTacToe : GridGame
{
    public override GameInfo Info { get; } = new(
        "ttt3", "Зникаючі хрестики-нолики", "зникаючі хрестики-нолики", GameGroup.Board, 2, 2, Rated: true,
        Hint: "Кожен тримає на полі лише три мітки: ставиш четверту — найстаріша щезає, тож нічиїх тут не буває.",
        Client: "ttt");   // правила ті самі, тож малює їх той самий web/games/ttt.js

    protected override GridRules Rules { get; } = new(3, 3, 3, false, Keep: 3);
    protected override string[] Marks { get; } = ["✕", "◯"];

    public override string SeatName(int seat) => seat == 0 ? "✕" : "◯";
}

/// <summary>Чотири в ряд: кидаєш фішку в колонку, вона падає вниз.</summary>
public sealed class ConnectFour : GridGame
{
    public override GameInfo Info { get; } = new(
        "c4", "Чотири в ряд", "чотири в ряд", GameGroup.Board, 2, 2, Rated: true,
        Options: [Clock],
        Hint: "Теж на двох: кидаєш фішку в колонку, вона падає вниз. Виграє той, хто перший збере чотири підряд.");

    /// <summary>Годинник ходу (№203): простояв 20 с — фішка падає сама у випадкову колонку. Типово — без годинника.</summary>
    public static readonly GameOption Clock = new("clock", "Годинник ходу",
        [("0", "без годинника"), ("20", "⏱ 20 с на хід — простояв, і фішка падає сама")], "0");

    protected override GridRules Rules { get; } = new(7, 6, 4, true);
    protected override string[] Marks { get; } = ["●", "●"];

    public override string SeatName(int seat) => seat == 0 ? "жовті" : "зелені";
}

/// <summary>
/// «Чотири в ряд» на компанію: троє або четверо, поле ширше, кожен своїм кольором і своєю позначкою.
/// Окрема гра, а не опція класичних «Чотирьох»: у тих лишаються ставка й рейтинг, які мають сенс лише на двох.
/// Хто встав або здався — випадає з черги, його фішки лишаються на полі. Лишився один — він і виграв.
/// </summary>
public sealed class ConnectFourParty : GridGame
{
    /// <summary>Троє — 9×7, четверо — 10×8: на кожного приблизно стільки ж клітинок, скільки й на двох на 7×6.</summary>
    static readonly GridRules Three = new(9, 7, 4, true), Four = new(10, 8, 4, true);

    public override GameInfo Info { get; } = new(
        "c4x", "Чотири в ряд: компанія", "чотири в ряд на компанію", GameGroup.Board, 1, 4, Start: StartMode.ByHost,
        Options:
        [
            new GameOption("teams", "Склад", [("0", "кожен сам за себе"), ("1", "👥 пари 2×2: через одного, четвірка з фішок напарника теж рахується")], "0"),
            ConnectFour.Clock,
            BoardBots.Option(3),
        ],
        Hint: "Троє або четверо, поле ширше, кожен своїм кольором. Збери чотири підряд і не дай сусідам — блокують тут усі. "
            + "Учотирьох можна парами, а на порожнє місце підсяде Глек 🤖",
        Client: "c4");

    /// <summary>Сіли люди й Глеки: утрьох-учотирьох, а пари — рівно вчотирьох.</summary>
    public override string? CanStart()
    {
        var humans = BoardBots.Humans(Ctx, 4);
        var total = humans + Math.Min(BotsWanted, 4 - humans);
        if (Teams && total < 4) return "Пари 2×2 — це рівно четверо: хай підсяде ще хтось (або Глек 🤖 — опція столу)";
        return total >= 3 ? null : "Тут треба щонайменше троє — хай підсядуть друзі або відкрий стіл з «🤖 Глек підсідає»";
    }

    GridRules _rules = Three;
    protected override GridRules Rules => _rules;
    protected override string[] Marks { get; } = ["●", "▲", "■", "◆"];

    protected override void Prepare(int players) => _rules = players >= 4 ? Four : Three;

    public override string SeatName(int seat) => seat switch { 0 => "жовті", 1 => "зелені", 2 => "руді", _ => "білі" };
}
