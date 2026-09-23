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
    /// <summary>Напрямки, у яких шукаємо ряд: вправо, вниз і дві діагоналі.</summary>
    static readonly (int Dx, int Dy)[] Dirs = [(1, 0), (0, 1), (1, 1), (1, -1)];
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
    int[]? _line;
    /// <summary>Куди ліг останній хід — браузер його підсвічує, а в «Чотирьох» ще й кидає фішку згори.</summary>
    int? _last;
    /// <summary>Хто ще в грі. На двох — завжди обидва; за столом на компанію той, хто встав, випадає з черги.</summary>
    bool[] _in = [];
    readonly Series _series = new();

    static string Mark(int seat) => Letters[seat];

    /// <summary>Скільки місць за цим столом (на двох — два, на компанію — до чотирьох).</summary>
    int Seats => Info.MaxPlayers;

    /// <summary>Перед кожною партією: стіл на компанію тут обирає розмір поля за кількістю гравців.</summary>
    protected virtual void Prepare(int players) { }

    public override void Start()
    {
        Prepare(Enumerable.Range(0, Seats).Count(Ctx.Seated));
        _cells = new string?[Rules.Cells];
        _order = Rules.Keep > 0 ? [] : null;
        _in = [.. Enumerable.Range(0, Seats).Select(Ctx.Seated)];
        // Починає перше зайняте місце: за столом на компанію, де сіли троє з чотирьох, «нульове» може пустувати.
        _turn = Array.IndexOf(_in, true) is var first && first >= 0 ? first : 0;
        _winner = null;
        _line = null;
        _last = null;
        _series.Begin(Ctx, Seats);
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_winner is not null) return ActResult.Fail("Партію зіграно, тисни «Ще раз»");
        if (action == "resign") return Resign(seat);
        if (action != "move") return ActResult.Fail("Тут так не ходять");
        if (seat != _turn) return ActResult.Fail("Зараз не твій хід");
        if (Cell(payload) is not { } cell) return ActResult.Fail("Не зрозумів, куди ходити");

        var index = Place(cell);
        if (index < 0) return ActResult.Fail(Rules.Gravity ? "Ця колонка вже повна" : "Ця клітинка вже зайнята");

        var mark = Mark(seat);
        _cells[index] = mark;
        _order?.Add(index);
        _last = index;
        // Найстаріша мітка щезає ще до підрахунку ряду: виграти тим, чого вже нема на полі, не можна.
        Vanish(mark);

        if (WinLine(index, mark) is { } line)
        {
            _winner = mark;
            _line = line;
            Finish([seat], Seats == 2
                // Ніки чужі, відмінювати їх нема як, тому рахунок замість речення з відмінками.
                ? $"{Info.Title}: {Ctx.NickOf(seat)} {SeatName(seat)} 1:0 {Ctx.NickOf(Other(seat))} {SeatName(Other(seat))}"
                : $"{Info.Title}: {Ctx.NickOf(seat)} ({SeatName(seat)}) перший зібрав {Rules.Need} в ряд");
            return ActResult.Accept("Твоя взяла!");
        }
        if (_cells.All(c => c is not null))
        {
            _winner = "draw";
            Finish([], $"{Info.Title}: {Players()} зіграли внічию");
            return ActResult.Accept("Нічия");
        }
        _turn = Next(seat);
        return ActResult.Done;
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
        var names = Enumerable.Range(0, Seats).Where(s => _in[s]).Select(s => $"{Ctx.NickOf(s)} {SeatName(s)}").ToList();
        return names.Count <= 1 ? string.Join("", names) : string.Join(", ", names.Take(names.Count - 1)) + " і " + names[^1];
    }

    void Finish(int[] winners, string text)
    {
        _series.Record(Ctx, winners);
        Ctx.Finish(winners, text);
    }

    /// <summary>
    /// Здатись. На двох — перемога суперникові; за столом на компанію гравець просто випадає з черги,
    /// а його фішки лишаються на полі перешкодою. Коли лишився один — він і виграв.
    /// </summary>
    ActResult Resign(int seat)
    {
        if (!_in[seat]) return ActResult.Fail("Ти вже здався");
        Drop(seat, "здався");
        return ActResult.Accept("Здався");
    }

    const string Left = "встав з-за столу";

    void Drop(int seat, string why)
    {
        _in[seat] = false;
        var left = Enumerable.Range(0, Seats).Where(s => _in[s]).ToArray();
        if (left.Length <= 1)
        {
            _winner = left.Length == 1 ? Mark(left[0]) : "draw";
            // На двох вихід пишемо так само, як каркас пише техпоразку в усіх іграх (Game.OnLeave).
            Finish(left, left.Length == 1 && (Seats > 2 || why != Left)
                ? $"{Info.Title}: {Ctx.NickOf(seat)} {why}, {Ctx.NickOf(left[0])} {SeatName(left[0])} перемагає"
                : $"{Info.Title}: {Ctx.NickOf(seat)} {why}, партію не дограли");
            return;
        }
        Ctx.Log($"{Info.Title}: {Ctx.NickOf(seat)} {why}, решта грає далі");
        if (_turn == seat) _turn = Next(seat);
    }

    public override void OnLeave(int seat)
    {
        if (_winner is not null || !_in[seat]) return;
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
        last = _last,
        // Хто ще в грі (на компанію хтось міг здатись); на двох — завжди обидва, поки партія йде.
        active = (bool[])_in.Clone(),
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

    /// <summary>Ряд потрібної довжини через щойно поставлену клітинку, або null.</summary>
    int[]? WinLine(int index, string mark)
    {
        var (w, h) = (Rules.Width, Rules.Height);
        var (x0, y0) = (index % w, index / w);
        foreach (var (dx, dy) in Dirs)
        {
            var line = new List<int> { index };
            foreach (var step in (int[])[1, -1])
                for (int x = x0 + dx * step, y = y0 + dy * step;
                     x >= 0 && x < w && y >= 0 && y < h && _cells[y * w + x] == mark;
                     x += dx * step, y += dy * step)
                    line.Add(y * w + x);
            if (line.Count >= Rules.Need) return [.. line.Order()];
        }
        return null;
    }
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
        Hint: "Теж на двох: кидаєш фішку в колонку, вона падає вниз. Виграє той, хто перший збере чотири підряд.");

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
        "c4x", "Чотири в ряд: компанія", "чотири в ряд на компанію", GameGroup.Board, 3, 4, Start: StartMode.ByHost,
        Hint: "Троє або четверо, поле ширше, кожен своїм кольором. Збери чотири підряд і не дай сусідам — блокують тут усі.",
        Client: "c4");

    GridRules _rules = Three;
    protected override GridRules Rules => _rules;
    protected override string[] Marks { get; } = ["●", "▲", "■", "◆"];

    protected override void Prepare(int players) => _rules = players >= 4 ? Four : Three;

    public override string SeatName(int seat) => seat switch { 0 => "жовті", 1 => "зелені", 2 => "руді", _ => "білі" };
}
