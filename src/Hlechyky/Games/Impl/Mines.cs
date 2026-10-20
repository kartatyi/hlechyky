using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Поле сапера без жодного слова про кімнати, ставки й чат: розставити міни, відкрити клітинку з
/// каскадом нулів і показати рівно те, що можна показувати. Обидва наші сапери — дуель і щоденний —
/// крутять саме це ядро, тож правила відкриття в них однакові до клітинки.
/// </summary>
public sealed class MinesBoard
{
    /// <summary>Символи поля на дроті: закрито, прапорець, міна. Відкрита клітинка — цифра '0'..'8'.</summary>
    public const char Closed = '#', Flagged = 'F', Bomb = '*';

    readonly bool[] _mine;
    readonly byte[] _near;
    readonly bool[] _open;
    readonly bool[] _flag;

    public MinesBoard(int w, int h, int mines)
    {
        W = w;
        H = h;
        Mines = Math.Max(0, mines);
        _mine = new bool[w * h];
        _near = new byte[w * h];
        _open = new bool[w * h];
        _flag = new bool[w * h];
    }

    public int W { get; }
    public int H { get; }
    public int Mines { get; }
    public int Cells => W * H;

    /// <summary>Скільки на полі клітинок без міни — стільки й треба відкрити, щоб поле стало чистим.</summary>
    public int Safe => Cells - Mines;

    /// <summary>Скільки безпечних клітинок уже відкрито (міна сюди не рахується — після неї партії однаково нема).</summary>
    public int Opened { get; private set; }

    public int Left => Safe - Opened;

    /// <summary>Скільки прапорців стоїть — щоб лічильник мін над полем не рахувати рядком.</summary>
    public int Flags { get; private set; }

    /// <summary>Міни вже розставлені. До першого відкриття поле порожнє: у сапері перший клік не вбиває.</summary>
    public bool Ready { get; private set; }

    public bool Valid(int cell) => cell >= 0 && cell < Cells;
    public bool IsOpen(int cell) => _open[cell];
    public bool IsFlag(int cell) => _flag[cell];
    public bool IsMine(int cell) => _mine[cell];
    public int Near(int cell) => _near[cell];

    /// <summary>Вісім сусідів клітинки; на краю поля їх, звісно, менше.</summary>
    public IEnumerable<int> Around(int cell)
    {
        var (x0, y0) = (cell % W, cell / W);
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var (x, y) = (x0 + dx, y0 + dy);
                if (x >= 0 && x < W && y >= 0 && y < H) yield return y * W + x;
            }
    }

    /// <summary>
    /// Розставити міни так, щоб клітинка <paramref name="safe"/> і вся її околиця 3×3 лишились чистими:
    /// перший клік у сапері не має вбивати, а нуль під ним одразу відкриває шматок поля, з якого є з чого
    /// думати. Випадковість — лише з переданого генератора, тож той самий сід дає те саме поле.
    /// </summary>
    public void Generate(Random rng, int safe)
    {
        var forbidden = new HashSet<int>(Around(safe));
        if (Valid(safe)) forbidden.Add(safe);
        var free = new List<int>(Cells);
        for (var c = 0; c < Cells; c++)
            if (!forbidden.Contains(c)) free.Add(c);

        // Часткове тасування Фішера-Єйтса: беремо рівно стільки перших, скільки треба мін. Так набір
        // залежить лише від сіду, а не від того, скільки разів ми смикнули rng у невдалих спробах.
        var n = Math.Min(Mines, free.Count);
        for (var i = 0; i < n; i++)
        {
            var j = rng.Next(i, free.Count);
            (free[i], free[j]) = (free[j], free[i]);
            _mine[free[i]] = true;
        }
        Count();
    }

    /// <summary>
    /// Розставити міни рівно на ці клітинки (готове поле з <see cref="MinesFields"/> — без вгадування). Поле має бути
    /// свіже: ні відкритого, ні мін. Чужі номери клітинок мовчки пропускаємо.
    /// </summary>
    public void Place(IEnumerable<int> mines)
    {
        Array.Clear(_mine);
        foreach (var c in mines)
            if (Valid(c)) _mine[c] = true;
        Count();
    }

    /// <summary>Де стоять міни — номери клітинок за зростанням (для кешу готових полів).</summary>
    public int[] MineCells()
    {
        var list = new List<int>(Mines);
        for (var c = 0; c < Cells; c++)
            if (_mine[c]) list.Add(c);
        return [.. list];
    }

    void Count()
    {
        for (var c = 0; c < Cells; c++)
        {
            byte k = 0;
            foreach (var nb in Around(c))
                if (_mine[nb]) k++;
            _near[c] = k;
        }
        Ready = true;
    }

    /// <summary>
    /// Відкрити клітинку; нуль тягне за собою всю околицю, і так до перших цифр. Повертає, скільки
    /// клітинок відкрилось. Прапорець каскад не спиняє: інакше в чистому полі лишались би дірки, яких
    /// гравець не ставив, і партія не закінчилась би ніколи.
    /// </summary>
    public int Open(int cell)
    {
        if (!Valid(cell) || _open[cell]) return 0;
        var stack = new Stack<int>();
        stack.Push(cell);
        var n = 0;
        while (stack.Count > 0)
        {
            var c = stack.Pop();
            if (_open[c]) continue;
            _open[c] = true;
            if (_flag[c]) { _flag[c] = false; Flags--; }
            n++;
            if (_mine[c]) continue;   // міну далі не розкочуємо: на ній партія й скінчилась
            Opened++;
            if (_near[c] != 0) continue;
            foreach (var nb in Around(c))
                if (!_open[nb]) stack.Push(nb);
        }
        return n;
    }

    /// <summary>Поставити або зняти прапорець. false — так не можна: клітинка вже відкрита.</summary>
    public bool Toggle(int cell)
    {
        if (!Valid(cell) || _open[cell]) return false;
        _flag[cell] = !_flag[cell];
        Flags += _flag[cell] ? 1 : -1;
        return true;
    }

    /// <summary>
    /// Поле рядком для виду: '#' закрито, 'F' прапорець, '0'..'8' відкрито. Усі міни стають '*' лише при
    /// <paramref name="reveal"/> — інакше вид, який летить у браузер, був би готовою підказкою. Міна, на
    /// яку справді наступили, показується завжди: каскад мін не відкриває, тож така клітинка рівно одна,
    /// і сховати її означало б намалювати гравцеві порожнє місце там, де він щойно підірвався.
    /// </summary>
    public string Text(bool reveal)
    {
        var sb = new StringBuilder(Cells);
        for (var c = 0; c < Cells; c++)
            sb.Append((reveal || _open[c]) && _mine[c] ? Bomb
                : _open[c] ? (char)('0' + _near[c])
                : _flag[c] ? Flagged
                : Closed);
        return sb.ToString();
    }

    /// <summary>Стан клітинок для Save(): 'o' відкрито, 'f' прапорець, '.' закрито. Міни не зберігаємо — вони з сіду дня.</summary>
    public string Mask()
    {
        var sb = new StringBuilder(Cells);
        for (var c = 0; c < Cells; c++) sb.Append(_open[c] ? 'o' : _flag[c] ? 'f' : '.');
        return sb.ToString();
    }

    /// <summary>Відновити стан із <see cref="Mask"/> на вже розставленому полі. Чужа довжина — мовчки нічого.</summary>
    public void Restore(string mask)
    {
        if (mask.Length != Cells) return;
        Opened = 0;
        Flags = 0;
        for (var c = 0; c < Cells; c++)
        {
            _open[c] = mask[c] == 'o';
            _flag[c] = mask[c] == 'f';
            if (_open[c] && !_mine[c]) Opened++;
            if (_flag[c]) Flags++;
        }
    }
}

/// <summary>
/// Розмір поля дуелі так, як його обирають у лобі. <paramref name="HuntMines"/> — скільки мін у режимі
/// «Мисливці»: там міни — це очки, і їх має бути густіше (і непарно, щоб удвох не лишалось нічиєї).
/// </summary>
public sealed record MinesSize(string Key, string Label, int W, int H, int Mines, int HuntMines);

/// <summary>Дрібниці, спільні обом саперам.</summary>
static class MinesWire
{
    /// <summary>Клітинку приймаємо і як <c>{cell:12}</c>, і як голе число — так само, як у решті наших ігор.</summary>
    public static int? Cell(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetInt32(out var n) => n,
        JsonValueKind.Object when payload.TryGetProperty("cell", out var c)
            && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) => n,
        _ => null,
    };

    /// <summary>«42,3 с» людською мовою. Культуру фіксуємо, а кому ставимо самі: рядок іде в Журнал.</summary>
    public static string Seconds(long ms) => (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " с";
}

/// <summary>
/// Сапер на 2–4: одне поле на всіх, ходять по черзі. Два режими.
/// <para>
/// «Обережно» (типовий, як було): відкрив — стільки очок, скільки клітинок відкрилось, і хід далі;
/// наступив на міну — вибув. Удвох це одразу поразка; у компанії решта грають далі, а коли живий лишився
/// один — він і виграв. Поле чисте — перемога за очками серед тих, хто вцілів.
/// </para>
/// <para>
/// «Мисливці» (як у старому «Minesweeper Flags»): міна — це очко, і після неї стріляєш ще; безпечна
/// клітинка відкривається й передає хід. Хто назбирав більше мін — виграв; партія кінчається, щойно
/// лідера вже ніхто не наздожене.
/// </para>
/// Прапорці спільні й ходу не передають: вони тут не «я знаю, де міна», а «не тисни сюди випадково».
/// </summary>
public sealed class Mines : Game
{
    public const int Seats = 4;

    static readonly MinesSize[] Sizes =
    [
        new("9x9-10", "Маленьке (9×9, 10 мін)", 9, 9, 10, 15),
        new("12x12-22", "Середнє (12×12, 22 міни)", 12, 12, 22, 29),
        new("16x16-40", "Велике (16×16, 40 мін)", 16, 16, 40, 51),
    ];

    public const string Careful = "boom", Hunt = "hunt";

    static readonly string[] Names = ["жовтий", "зелений", "глиняний", "сірий"];

    public override GameInfo Info { get; } = new(
        "mines", "Сапер-дуель", "сапер-дуель", GameGroup.Board, 2, Seats, Start: StartMode.ByHost,
        Options:
        [
            new GameOption("size", "Поле", [.. Sizes.Select(s => (s.Key, s.Label))], Sizes[0].Key),
            new GameOption("mode", "Правила",
                [(Careful, "Обережно: міна — вибув"), (Hunt, "Мисливці: міна — твоє очко")], Careful),
        ],
        Hint: "Одне поле на 2–4, ходите по черзі. Відкрив число — очко, підірвався — вибув. Або навпаки: полюй на міни");

    MinesSize _size = Sizes[0];
    string _mode = Careful;
    MinesBoard _board = new(Sizes[0].W, Sizes[0].H, Sizes[0].Mines);
    int _turn;
    readonly int[] _points = new int[Seats];
    readonly bool[] _in = new bool[Seats];
    /// <summary>Чому місце вибуло: boom | resign | left; null — ще грає (або не грало зовсім).</summary>
    readonly string?[] _out = new string?[Seats];
    /// <summary>Хто вибув, по порядку: перший тут — останнє місце.</summary>
    readonly List<int> _gone = [];
    /// <summary>Мисливці: чия міна (місце), -1 — нічия. Довжина — клітинки поля.</summary>
    int[] _owner = [];
    int? _last;
    int? _lastBy;
    int[] _winners = [];
    /// <summary>Чому партія скінчилась: boom | cleared | hunted | resign | left. null — партія триває.</summary>
    string? _reason;

    bool Hunting => _mode == Hunt;

    public override string SeatName(int seat) => seat >= 0 && seat < Seats ? Names[seat] : base.SeatName(seat);

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        // Каркас уже звів опції до дозволених значень — лишається знайти по них розмір і режим.
        if (options.TryGetValue("size", out var key) && Sizes.FirstOrDefault(s => s.Key == key) is { } found)
            _size = found;
        if (options.TryGetValue("mode", out var mode) && mode is Careful or Hunt) _mode = mode;
        // Поле збираємо вже тут: стіл, що чекає на суперника, має показувати ту дошку, яку обрали,
        // а не типову дев'ятку.
        Reset();
    }

    public override void Start()
    {
        Reset();
        for (var s = 0; s < Seats; s++) _in[s] = Ctx.Seated(s);
        _turn = First();
    }

    void Reset()
    {
        _board = new MinesBoard(_size.W, _size.H, Hunting ? _size.HuntMines : _size.Mines);
        _owner = new int[_board.Cells];
        Array.Fill(_owner, -1);
        _turn = 0;
        Array.Clear(_points);
        Array.Clear(_in);
        Array.Clear(_out);
        _gone.Clear();
        _last = null;
        _lastBy = null;
        _winners = [];
        _reason = null;
    }

    bool Alive(int seat) => seat >= 0 && seat < Seats && _in[seat] && _out[seat] is null;
    IEnumerable<int> Players => Enumerable.Range(0, Seats).Where(s => _in[s]);
    IEnumerable<int> Living => Enumerable.Range(0, Seats).Where(Alive);
    int First() => Enumerable.Range(0, Seats).FirstOrDefault(Alive, 0);

    int NextAlive(int seat)
    {
        for (var i = 1; i <= Seats; i++)
            if (Alive((seat + i) % Seats)) return (seat + i) % Seats;
        return seat;
    }

    string Nick(int seat) => Ctx.NickOf(seat) ?? SeatName(seat);

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_reason is not null) return ActResult.Fail("Партію зіграно, тисни «Ану ще раз»");
        if (seat < 0 || seat >= Seats || !_in[seat]) return ActResult.Fail("Ти тут не граєш");
        // Здатись можна й не в свою чергу: чекати ходу, щоб сказати «здаюсь», — знущання.
        if (action == "resign") return Resign(seat);
        if (action is not ("open" or "flag")) return ActResult.Fail("Тут так не ходять");
        if (!Alive(seat)) return ActResult.Fail("Ти вже поза грою — лишається дивитись");
        if (seat != _turn) return ActResult.Fail("Не так швидко — зараз не твій хід");
        if (MinesWire.Cell(payload) is not { } cell || !_board.Valid(cell))
            return ActResult.Fail("Не зрозумів, куди тиснути");
        return action == "flag" ? Flag(cell) : Open(seat, cell);
    }

    ActResult Flag(int cell)
    {
        if (!_board.Toggle(cell)) return ActResult.Fail("Тут уже відкрито");
        return ActResult.Done;   // прапорець ходу не передає: ним лише позначають, куди не тиснути
    }

    ActResult Open(int seat, int cell)
    {
        if (_board.IsOpen(cell)) return ActResult.Fail("Тут уже відкрито");
        if (_board.IsFlag(cell)) return ActResult.Fail("Тут прапорець — спершу зніми його");
        // Міни розставляємо аж тепер: так перший клік партії гарантовано безпечний, як у класиці.
        if (!_board.Ready) _board.Generate(Ctx.Rng, cell);

        _last = cell;
        _lastBy = seat;
        if (_board.IsMine(cell)) return Hunting ? Claim(seat, cell) : Boom(seat, cell);

        var opened = _board.Open(cell);
        if (!Hunting) _points[seat] += opened;   // у мисливців очки — лише міни
        if (!Hunting && _board.Left == 0) return Cleared(seat);
        _turn = NextAlive(seat);
        return ActResult.Done;
    }

    /// <summary>«Обережно»: наступив на міну — вибув. Удвох це кінець партії, у компанії — лише твоїй.</summary>
    ActResult Boom(int seat, int cell)
    {
        _board.Open(cell);
        Drop(seat, "boom");
        var living = Living.ToArray();
        if (living.Length >= 2)
        {
            _turn = NextAlive(seat);
            return ActResult.Accept("Бабах! Ти поза грою — дивись, хто кого");
        }
        _reason = "boom";
        _winners = living;
        if (Players.Count() == 2 && living.Length == 1)
        {
            var other = living[0];
            Ctx.Finish(living, $"{Info.Title}: {Ctx.NickOf(seat)} {SeatName(seat)} підривається на міні — "
                + $"{Ctx.NickOf(other)} {SeatName(other)} бере партію {_points[other]}:{_points[seat]}", Scores());
        }
        else
            Ctx.Finish(living, $"{Info.Title}: {Ctx.NickOf(seat)} {SeatName(seat)} підривається на міні — {Ranking()}", Scores());
        return ActResult.Accept("Бабах. Це була міна");
    }

    /// <summary>«Мисливці»: знайшов міну — вона твоя, і стріляєш ще.</summary>
    ActResult Claim(int seat, int cell)
    {
        _board.Open(cell);
        _owner[cell] = seat;
        _points[seat]++;
        var left = _board.Mines - _points.Sum();
        var ranked = Living.OrderByDescending(s => _points[s]).ToArray();
        var lead = _points[ranked[0]];
        var second = ranked.Length > 1 ? _points[ranked[1]] : 0;
        // Лідера вже не наздогнати — нема сенсу докопувати решту поля.
        if (left == 0 || lead - second > left)
        {
            _reason = "hunted";
            _winners = [.. ranked.Where(s => _points[s] == lead)];
            if (_winners.Length == ranked.Length && ranked.Length > 1)
            {
                _winners = [];
                Ctx.Finish([], $"{Info.Title}: мисливці поділили міни порівну — {Ranking()}", Scores());
                return ActResult.Accept("Міна! Усе поділено порівну");
            }
            Ctx.Finish(_winners, $"{Info.Title}: полювання скінчено — {Ranking()}", Scores());
            return ActResult.Accept("Міна твоя — і полювання теж!");
        }
        return ActResult.Accept($"Є! Міна твоя — шукай ще (лишилось {left})");
    }

    /// <summary>Поле розміновано. <paramref name="seat"/> — хто відкрив останню клітинку: не конче той, хто виграв.</summary>
    ActResult Cleared(int seat)
    {
        _reason = "cleared";
        var living = Living.ToArray();
        var top = living.Max(s => _points[s]);
        var best = living.Where(s => _points[s] == top).ToArray();
        if (best.Length == living.Length)
        {
            _winners = [];
            var text = living.Length == 2
                ? $"{Info.Title}: {Ctx.NickOf(living[0])} і {Ctx.NickOf(living[1])} розмінували поле порівну, {top}:{top}"
                : $"{Info.Title}: поле розміноване порівну — {Ranking()}";
            Ctx.Finish([], text, Scores());
            return ActResult.Accept("Поле чисте — нічия! Ану ще раз?");
        }
        _winners = best;
        if (Players.Count() == 2)
        {
            var won = best[0];
            var lost = Players.First(s => s != won);
            Ctx.Finish(best, $"{Info.Title}: {Ctx.NickOf(won)} {SeatName(won)} {_points[won]}:{_points[lost]} "
                + $"{Ctx.NickOf(lost)} {SeatName(lost)}", Scores());
        }
        else Ctx.Finish(best, $"{Info.Title}: поле чисте — {Ranking()}", Scores());
        return ActResult.Accept(best.Contains(seat) ? "Є! Поле чисте — твоя взяла!" : "Поле чисте!");
    }

    ActResult Resign(int seat)
    {
        if (!Alive(seat)) return ActResult.Fail("Ти вже поза грою — лишається дивитись");
        Drop(seat, "resign");
        var living = Living.Where(Ctx.Seated).ToArray();
        if (Living.Count() >= 2)
        {
            if (_turn == seat) _turn = NextAlive(seat);
            Ctx.Log($"{Info.Title}: {Ctx.NickOf(seat)} здається, решта грають далі");
            return ActResult.Accept("Ти поза грою — дивись, хто кого");
        }
        _reason = "resign";
        _winners = living;
        _turn = seat;
        if (living.Length == 1)
            Ctx.Finish(living, $"{Info.Title}: {Ctx.NickOf(seat)} {SeatName(seat)} здається — "
                + $"{Ctx.NickOf(living[0])} {SeatName(living[0])} бере партію", Scores());
        else
            Ctx.Finish([], $"{Info.Title}: {Ctx.NickOf(seat)} здається, а грати вже нема з ким", Scores());
        return ActResult.Accept("Партію віддано — ану ще раз?");
    }

    void Drop(int seat, string why)
    {
        _out[seat] = why;
        _gone.Add(seat);
    }

    /// <summary>
    /// Вийшов посеред партії. Удвох — техпоразка, як і було (текст і Finish пише каркас). У компанії партія
    /// не ламається: той, хто пішов, просто вибуває, решта грають далі.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (!Alive(seat) || _reason is not null) return;   // вибулий глядач може йти спокійно
        Drop(seat, "left");
        var living = Living.ToArray();
        if (living.Length >= 2)
        {
            if (_turn == seat) _turn = NextAlive(seat);
            Ctx.Log($"{Info.Title}: {Ctx.NickOf(seat)} встає з-за столу, решта грають далі");
            return;
        }
        _reason = "left";
        _winners = living;
        Ctx.Finish(living, $"{Info.Title}: {Ctx.NickOf(seat)} встає з-за столу, партію не дограли", Scores());
    }

    /// <summary>Місця від першого до останнього: хто вцілів — за очками, далі вибулі у зворотному порядку.</summary>
    int[] Places() =>
        [.. Living.OrderByDescending(s => _points[s]).Concat(Enumerable.Reverse(_gone))];

    string Ranking() => string.Join(", ", Places().Select(s => $"{Nick(s)} {_points[s]}"));

    Dictionary<int, long> Scores() => Players.ToDictionary(s => s, s => (long)_points[s]);

    /// <summary>Мисливці: чиї міни — рядок на всі клітинки, '0'..'3' або '.', лише для відкритих мін.</summary>
    string? Owners()
    {
        if (!Hunting) return null;
        var chars = new char[_owner.Length];
        for (var c = 0; c < chars.Length; c++) chars[c] = _owner[c] >= 0 ? (char)('0' + _owner[c]) : '.';
        return new string(chars);
    }

    public override object View(int? seat) => new
    {
        w = _board.W,
        h = _board.H,
        mines = _board.Mines,
        mode = _mode,
        turn = _reason is null ? _turn : (int?)null,
        cells = _board.Text(_reason is not null),
        scores = _points.ToArray(),
        left = _board.Left,
        unclaimed = _board.Mines - (Hunting ? _points.Sum() : 0),
        owners = Owners(),
        players = Players.ToArray(),
        @out = _out.ToArray(),
        lastOpen = _last,
        lastBy = _lastBy,
        result = _reason is null ? null : new
        {
            winner = _winners.Length == 1 ? _winners[0] : (int?)null,
            winners = _winners,
            reason = _reason,
            places = Places(),
        },
    };
}

/// <summary>
/// Сапер дня — спільне двох полів дня: велике 16×16 (<see cref="MinesDaily"/>, щоденна гра з плиткою в «☀ Сьогодні») і
/// легке 9×9 (<see cref="MinesDailyEasy"/>, прихована, лише перемикачем із картки). Одне поле на всіх, сід від дня, центр
/// відкритий одразу — так «перший клік безпечний» лишається правдою, а поле в усіх справді однакове, з тієї самої точки.
/// Рахунок — час у мілісекундах, менше краще; підірвався — спроба провалена (промах дня), можна почати заново на тому
/// самому полі; «🏳 Здаюсь» закриває день. Щоденна нагорода — одна на обидва поля (реф <c>daily:mines-daily:день:нік</c>).
/// </summary>
public abstract class MinesDayGame : Game
{
    /// <summary>Id обох полів: табло на картці показує обидва рядки, а нагорода дня — одна, під <see cref="BigId"/>.</summary>
    public const string BigId = "mines-daily", EasyId = "mines-daily-easy";

    /// <summary>Поле дня з розставленими мінами (те саме для всіх і для кожної спроби).</summary>
    protected abstract MinesBoard NewField(string day);

    /// <summary>Звідки починають усі: центр поля. Ця клітинка та її околиця мін не мають ніколи.</summary>
    protected abstract int Origin { get; }

    /// <summary>Порахувати поле дня в кеш — з Configure, поза замком кімнати.</summary>
    protected abstract void WarmField(string day);

    /// <summary>«big» чи «easy» — для перемикача полів на картці.</summary>
    protected abstract string Size { get; }

    /// <summary>Привид найшвидшого: лише на великому полі (<see cref="MinesGhosts"/> тримає одного на день).</summary>
    protected virtual bool Ghosts => false;

    string _day = "";
    protected MinesBoard _board = new(1, 1, 0);
    DateTimeOffset _startedAt;
    int _attempts = 1;
    bool _solved;
    bool _dead;
    /// <summary>«🏳 Здаюсь»: день закінчено без розв'язку — поле розкрите, спроб більше нема.</summary>
    bool _resigned;
    long _ms;
    int? _last;
    /// <summary>Ходи цієї спроби для привида: «сотні секунди від старту, o/f/c, клітинка» через пробіл.</summary>
    readonly StringBuilder _moves = new();
    /// <summary>Спроба записана повністю (не відновлена з половини й не задовга) — годиться в привиди.</summary>
    bool _movesOk = true;
    /// <summary>Стеля запису ходів: вид із привидом має лишатись легким (≈ 6 КБ).</summary>
    public const int MaxMovesChars = 6000;
    DailyCard? _card;
    MinesGhosts? _ghosts;
    MinesWeek? _week;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        // Табло дня й привид живуть у пам'яті сервісів; тут, поза замком, лише кажемо їм дочитати день фоном.
        _card = Ctx.Services.GetService<DailyCard>();
        _ghosts = Ghosts ? Ctx.Services.GetService<MinesGhosts>() : null;
        _card?.Warm(BigId);
        _card?.Warm(EasyId);
        _week = Ctx.Services.GetService<MinesWeek>();
        _week?.Get();
        var day = Days.Today(Ctx.Clock);
        _ghosts?.Warm(day);
        // поле без вгадування рахується тут, поза замком кімнати; Start/«Ану ще раз» візьмуть готове з кешу
        WarmField(day);
    }

    void Move(char kind, int cell)
    {
        if (!_movesOk) return;
        var cs = Math.Max(0, (long)(Ctx.Clock.UtcNow - _startedAt).TotalMilliseconds / 10);
        if (_moves.Length > 0) _moves.Append(' ');
        _moves.Append(cs.ToString(CultureInfo.InvariantCulture)).Append(kind).Append(cell.ToString(CultureInfo.InvariantCulture));
        if (_moves.Length > MaxMovesChars) { _movesOk = false; _moves.Clear(); }
    }

    /// <summary>Один ключ на ніка на день — саме з нього сервіси дістають гру й день (specs/daily.md).</summary>
    public override string SoloKey(string nickKey, IClock clock) => $"daily:{Info.Id}:{Days.Today(clock)}:{nickKey}";

    public override void Start()
    {
        _day = Days.Today(Ctx.Clock);
        _attempts = 1;
        _solved = false;
        _resigned = false;
        _ms = 0;
        Deal();
    }

    /// <summary>Нова спроба: поле те саме (сід від дня), відлік — від зараз.</summary>
    void Deal()
    {
        _board = NewField(_day);
        _board.Open(Origin);
        _dead = false;
        _last = Origin;
        _startedAt = Ctx.Clock.UtcNow;
        _moves.Clear();
        _movesOk = true;
        Move('o', Origin);
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_solved) return ActResult.Fail("Поле дня вже розміноване, приходь завтра");
        if (_resigned) return ActResult.Fail("На сьогодні все — завтра нове поле");
        if (action == "resign") return Resign();
        if (action == "restart")
        {
            _attempts++;
            Deal();
            return ActResult.Accept($"Спроба {_attempts} — гайда, час пішов заново");
        }
        if (_dead) return ActResult.Fail("Бабах уже був — тисни «Спробувати ще»");
        if (action is not ("open" or "flag")) return ActResult.Fail("Тут так не ходять");
        if (MinesWire.Cell(payload) is not { } cell || !_board.Valid(cell))
            return ActResult.Fail("Не зрозумів, куди тиснути");

        if (action == "flag")
        {
            if (!_board.Toggle(cell)) return ActResult.Fail("Тут уже відкрито");
            Move('f', cell);
            return ActResult.Done;
        }
        if (_board.IsOpen(cell)) return Chord(cell);
        if (_board.IsFlag(cell)) return ActResult.Fail("Тут прапорець — спершу зніми його");

        _last = cell;
        _board.Open(cell);
        Move('o', cell);
        if (_board.IsMine(cell))
        {
            // Кімнату не закриваємо: спроба провалилась, а день — ні, і «Спробувати ще» має працювати.
            Boom();
            return ActResult.Accept("Бабах. Спроба не вийшла");
        }
        return _board.Left == 0 ? Solved() : ActResult.Done;
    }

    /// <summary>
    /// Тиск по відкритому числу, довкола якого вже стоїть рівно стільки прапорців, — відкрити решту сусідів
    /// одним махом, як у класичному сапері. На полі дня, де міряють час, це не забаганка: без цього
    /// рахунок залежить від того, як швидко клацаєш, а не як думаєш. Прапорець не там — бабах, як і в класиці.
    /// </summary>
    ActResult Chord(int cell)
    {
        var n = _board.Near(cell);
        if (_board.IsMine(cell) || n == 0) return ActResult.Fail("Тут уже відкрито");
        var around = _board.Around(cell).ToArray();
        if (around.Count(_board.IsFlag) != n) return ActResult.Fail($"Довкола має стояти прапорців: {n}");
        var closed = around.Where(c => !_board.IsOpen(c) && !_board.IsFlag(c)).ToArray();
        if (closed.Length == 0) return ActResult.Fail("Довкола вже все відкрито");
        if (closed.FirstOrDefault(_board.IsMine, -1) is var mine and >= 0)
        {
            _last = mine;
            _board.Open(mine);
            Boom();
            return ActResult.Accept("Бабах. Один прапорець стояв не там");
        }
        foreach (var c in closed) _board.Open(c);
        _last = cell;
        Move('c', cell);
        return _board.Left == 0 ? Solved() : ActResult.Done;
    }

    /// <summary>
    /// Вибух: спроба пропала, і це видно — промах дня йде в daily_results (<c>solved = 0</c>) і на табло «✗ Оля (2)».
    /// Спробувати ще можна, як і раніше: пізніший розв'язок того самого дня перекриває промах.
    /// </summary>
    void Boom()
    {
        _dead = true;
        Ctx.DailyMiss(0, _attempts);
        _card?.NoteMiss(Info.Id, Ctx.NickOf(0) ?? "", _attempts);
        NoteWeek(false);
    }

    /// <summary>Залік тижня — лише за велике поле.</summary>
    void NoteWeek(bool solved)
    {
        if (Info.Id == BigId && Ctx.NickOf(0) is { } nick)
            _week?.Note(nick, solved, _attempts, (int)Math.Min(int.MaxValue, _ms));
    }

    /// <summary>
    /// «🏳 Здаюсь»: день закінчено — поле розкривається, промах записано, кімната закривається. У Журнал нічого:
    /// здатись — не подія для всього сайту.
    /// </summary>
    ActResult Resign()
    {
        _resigned = true;
        Ctx.DailyMiss(0, _attempts);
        _card?.NoteMiss(Info.Id, Ctx.NickOf(0) ?? "", _attempts);
        NoteWeek(false);
        Ctx.Finish([], "");
        return ActResult.Accept("Поле розкрито — ось де були міни. Завтра нове");
    }

    ActResult Solved()
    {
        _solved = true;
        // Спроби йдуть у подію окремо: топ дня сортує спершу за ними, і той, хто підірвався двічі, а потім
        // пройшов уже знайоме поле, не має обганяти того, хто розмінував з першого разу. Нижче секунди час
        // не опускаємо — це страховка для таблиць, що вгадують «спроби чи мілісекунди» з величини числа.
        _ms = Math.Max(1000, (long)(Ctx.Clock.UtcNow - _startedAt).TotalMilliseconds);
        Ctx.Score(0, _ms, _attempts);
        // нагорода дня одна на обидва поля: реф daily:mines-daily:<день>:<нік> однаковий, хоч яке поле розв'язане
        Ctx.Award(0, 0, $"daily:{BigId}");
        var nick = Ctx.NickOf(0) ?? "";
        _card?.Note(Info.Id, nick, _attempts, (int)Math.Min(int.MaxValue, _ms));
        NoteWeek(true);
        if (_movesOk && _moves.Length > 0) _ghosts?.Offer(_day, new MinesGhost(nick, _attempts, (int)Math.Min(int.MaxValue, _ms), _moves.ToString()));
        Ctx.Finish([0], $"{Info.Title}: {Ctx.NickOf(0)} — поле дня чисте за {MinesWire.Seconds(_ms)}"
            + (_attempts > 1 ? $" (спроба {_attempts})" : ""));
        return ActResult.Accept($"Є! Чисто за {MinesWire.Seconds(_ms)}");
    }

    public override object View(int? seat)
    {
        var over = _solved || _dead || _resigned;
        var elapsed = _solved ? _ms : Math.Max(0, (long)(Ctx.Clock.UtcNow - _startedAt).TotalMilliseconds);
        // Вибух поле НЕ розкриває: спроба провалилась, а головоломка дня — ні. Показали б усі сорок мін —
        // і «Спробувати ще» на тому самому полі перетворилося б на «розмінуй за десять секунд».
        // Видно лише ту міну, на яку наступили (її малює сам Text), решта лишається закритою.
        return new
        {
            w = _board.W,
            h = _board.H,
            mines = _board.Mines,
            turn = over ? (int?)null : 0,
            cells = _board.Text(_solved || _resigned),
            scores = new[] { _board.Opened },
            left = _board.Left,
            lastOpen = _last,
            result = _solved ? new { winner = (int?)0, reason = "cleared" }
                : _resigned ? new { winner = (int?)null, reason = "resign" }
                : _dead ? new { winner = (int?)null, reason = "boom" }
                : null,
            day = _day,
            attempts = _attempts,
            startedAt = _startedAt,
            solved = _solved,
            resigned = _resigned,
            ms = _ms,
            elapsedMs = elapsed,
            // табло дня й серія 🔥 — з пам'яті сервісу; привид — лише після власного розв'язку (інакше це підказка, де міни)
            board = DailyCard.Wire(_card?.Get(Info.Id)),
            // пакет 4: перемикач полів і обидва рядки табло на картці («16×16: …», «9×9: …»)
            size = Size,
            boards = new { big = DailyCard.Wire(_card?.Get(BigId)), easy = DailyCard.Wire(_card?.Get(EasyId)) },
            // залік тижня (велике поле): сума місць за 7 днів, до 6 рядків
            week = MinesWeek.Wire(_week?.Get()),
            streak = _card is null || Ctx.NickOf(0) is not { } me ? 0 : _card.StreakOf(Info.Id, me),
            ghost = _solved && _ghosts?.Best(_day) is { } g ? new { n = g.Nick, a = g.Attempts, ms = g.Ms, mv = g.Moves } : null,
        };
    }

    /// <summary>Що лягає в game_state. Мін тут нема — вони однозначно виводяться з дня.</summary>
    sealed record State(string Day, string Mask, DateTimeOffset StartedAt, int Attempts, bool Solved, bool Dead, long Ms, int? Last,
        string? Moves = null, bool Resigned = false);

    public override string? Save() =>
        JsonSerializer.Serialize(new State(_day, _board.Mask(), _startedAt, _attempts, _solved, _dead, _ms, _last, _movesOk ? _moves.ToString() : null, _resigned));

    public override void Load(string json)
    {
        State? s;
        try { s = JsonSerializer.Deserialize<State>(json); }
        catch (JsonException) { return; }   // зіпсований стан — граємо з чистого поля, це не привід падати
        // Поле іншого дня нам ні до чого: ключ кімнати вже містить день, але страховка дешева.
        if (s is null || s.Day != _day) return;
        _board.Restore(s.Mask);
        _startedAt = s.StartedAt;
        _attempts = Math.Max(1, s.Attempts);
        _solved = s.Solved;
        _dead = s.Dead;
        _resigned = s.Resigned;
        _ms = s.Ms;
        _last = s.Last is { } last && _board.Valid(last) ? last : null;
        // запис ходів дочитуємо, якщо він є; старий стан без нього — спроба в привиди вже не годиться
        _moves.Clear();
        _movesOk = s.Moves is { Length: > 0 and <= MaxMovesChars };
        if (_movesOk) _moves.Append(s.Moves);
    }
}

/// <summary>Сапер дня, велике поле: 16×16, сорок мін. Щоденна гра — плитка в «☀ Сьогодні», серія, привид найшвидшого.</summary>
public sealed class MinesDaily : MinesDayGame, IDailyGame
{
    public const int W = 16, H = 16, MineCount = 40;

    /// <summary>Звідки починають усі: центр поля. Ця клітинка та її околиця мін не мають ніколи.</summary>
    public const int Center = 7 * W + 7;

    /// <summary>Ключ головоломки для сіду дня. Свідомо «mines», а не Id: поле дня одне на всю родину саперів.</summary>
    const string Puzzle = "mines";

    public override GameInfo Info { get; } = new(
        BigId, "Сапер дня", "сапера дня", GameGroup.Solo, 1, 1,
        Start: StartMode.Immediate, Private: true, Persistent: true, Score: ScoreOrder.LowerIsBetter,
        Hint: "Одне поле 16×16 на всіх, сорок мін. Центр уже відкрито, час пішов — хто швидше?",
        Client: "mines");   // малює той самий web/games/mines.js, що й дуель

    protected override int Origin => Center;
    protected override string Size => "big";
    protected override bool Ghosts => true;
    protected override MinesBoard NewField(string day) => Field(day);

    protected override void WarmField(string day)
    {
        if (NoGuess(day)) MinesFields.Get(Puzzle, day, W, H, MineCount, Center);
    }

    /// <summary>
    /// З якого дня поле дня — без вгадування (<see cref="MinesFields"/>). Старші дні — старим генератором: стан кімнати
    /// й привиди зберігають лише маску відкритого, а міни виводяться з дня, тож поле вже зіграного дня мінятись не сміє.
    /// </summary>
    public const string NoGuessFrom = "2026-10-21";

    /// <summary>Чи день грає полем без вгадування.</summary>
    public static bool NoGuess(string day) => string.CompareOrdinal(day, NoGuessFrom) >= 0;

    /// <summary>
    /// Поле дня з розставленими мінами. Сід від дня, а не від кімнати: інакше в кожного було б своє поле, і порівнювати
    /// час не було б сенсу. Нове поле береться з кешу <see cref="MinesFields"/> (прогрів — з Configure, поза замком).
    /// </summary>
    public static MinesBoard Field(string day)
    {
        var board = new MinesBoard(W, H, MineCount);
        if (NoGuess(day)) board.Place(MinesFields.Get(Puzzle, day, W, H, MineCount, Center).Mines);
        else board.Generate(new Random(Days.Seed(Puzzle, day)), Center);
        return board;
    }
}

/// <summary>
/// Сапер дня, легке поле: 9×9, десять мін, завжди без вгадування. Прихована гра (без своєї плитки в «☀ Сьогодні») —
/// відкривається перемикачем із картки Сапера дня; результат лягає в daily_results під своїм id і свій рядок табло,
/// а щоденна нагорода — та сама, що й за велике поле (одна на день).
/// </summary>
public sealed class MinesDailyEasy : MinesDayGame
{
    public const int W = 9, H = 9, MineCount = 10;
    public const int Center = 4 * W + 4;
    const string Puzzle = "mines-easy";

    public override GameInfo Info { get; } = new(
        EasyId, "Сапер дня 9×9", "легкого сапера дня", GameGroup.Solo, 1, 1,
        Start: StartMode.Immediate, Hidden: true, Private: true, Persistent: true, Score: ScoreOrder.LowerIsBetter,
        Hint: "Легке поле дня 9×9, десять мін, без вгадування. Центр уже відкрито — хто швидше?",
        Client: "mines");

    protected override int Origin => Center;
    protected override string Size => "easy";
    protected override MinesBoard NewField(string day) => Field(day);
    protected override void WarmField(string day) => MinesFields.Get(Puzzle, day, W, H, MineCount, Center);

    /// <summary>Легке поле дня — з кешу <see cref="MinesFields"/>, без вгадування з першого ж дня.</summary>
    public static MinesBoard Field(string day)
    {
        var board = new MinesBoard(W, H, MineCount);
        board.Place(MinesFields.Get(Puzzle, day, W, H, MineCount, Center).Mines);
        return board;
    }
}
