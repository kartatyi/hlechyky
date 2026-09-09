using System.Text;

namespace Hlechyky.Games.Impl;

/// <summary>Три варіанти з одного набору правил: класика, шахи Фішера (960) і піддавки.</summary>
public enum ChessVariant { Classic, Fischer, Anti }

/// <summary>
/// Чим хід відрізняється від простого «пересунув фігуру»: подвійний пішак лишає по собі поле для взяття
/// на проході, взяття на проході знімає пішака не з поля призначення, рокіровка їде двома фігурами.
/// </summary>
public enum ChessMoveKind : byte { Normal, DoublePawn, EnPassant, Castle }

/// <summary>
/// Хід у внутрішньому вигляді. Рокіровка записується як «король бере власну туру» (<see cref="To"/> — поле
/// тури): у шахах Фішера король і тури стоять де завгодно, і лише ця пара називає хід однозначно.
/// <see cref="Promo"/> — тип фігури перетворення (0, якщо його нема).
/// </summary>
public readonly record struct ChessMove(byte From, byte To, sbyte Promo, ChessMoveKind Kind);

/// <summary>Усе, що треба, аби відкотити хід: збита фігура, права рокіровки й лічильники.</summary>
public struct ChessUndo
{
    public int Captured, CapturedSq, EnPassant, Halfmove, Fullmove;
    /// <summary>Файли тур, які ще мали право рокіруватись, до ходу.</summary>
    public int WK, WQ, BK, BQ;
    public bool WhiteToMove;
}

/// <summary>
/// Правила шахів без жодного слова про кімнати, ставки й чат: дошка, генерація ходів, зроби/відкоти,
/// FEN і SAN. Усе, що перевіряється перфтом, живе тут — гра (Chess.cs) лише вирішує, що з цього показати
/// гравцям і коли покликати Finish.
///
/// Дошка — <c>int[64]</c> у порядку виду: 0 — a8, 7 — h8, 63 — h1. Тобто рядок 0 — це восьма горизонталь,
/// рядок 7 — перша. Фігура: 1..6 (P N B R Q K) для білих, ті самі числа зі знаком мінус для чорних.
/// Права рокіровки тримаємо не прапорцями «KQkq», а файлами тур: у 960 інакше не вийде.
/// </summary>
public sealed class ChessCore
{
    public const int Pawn = 1, Knight = 2, Bishop = 3, Rook = 4, Queen = 5, King = 6;

    /// <summary>Порядок фігур для перетворення: спершу ферзь — його беруть за замовчуванням.</summary>
    public static readonly sbyte[] PromoPieces = [Queen, Rook, Bishop, Knight];
    /// <summary>У піддавках король — звичайна фігура, тож у нього перетворюватись теж можна.</summary>
    public static readonly sbyte[] PromoPiecesAnti = [Queen, Rook, Bishop, Knight, King];

    static readonly (int Df, int Dr)[] KnightD = [(1, 2), (2, 1), (2, -1), (1, -2), (-1, -2), (-2, -1), (-2, 1), (-1, 2)];
    static readonly (int Df, int Dr)[] KingD = [(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)];
    static readonly (int Df, int Dr)[] BishopD = [(1, 1), (1, -1), (-1, 1), (-1, -1)];
    static readonly (int Df, int Dr)[] RookD = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    readonly int[] _b = new int[64];
    /// <summary>[колір, бік] → файл тури, яка ще може рокіруватись. Колір: 0 білі, 1 чорні; бік: 0 короткий, 1 довгий; -1 — права нема.</summary>
    readonly int[,] _rook = { { -1, -1 }, { -1, -1 } };

    public ChessCore(ChessVariant variant) => Variant = variant;

    public ChessVariant Variant { get; }
    public bool WhiteToMove { get; private set; } = true;
    /// <summary>Поле, куди можна побити на проході; -1 — нема.</summary>
    public int EnPassant { get; private set; } = -1;
    public int Halfmove { get; private set; }
    public int Fullmove { get; private set; } = 1;

    /// <summary>Чий зараз хід: 0 — білі, 1 — чорні (індекс у <see cref="_rook"/>).</summary>
    int Side => WhiteToMove ? 0 : 1;

    public int PieceAt(int sq) => _b[sq];

    // ------------------------------------------------------------------------------------------
    // Координати
    // ------------------------------------------------------------------------------------------

    public static int File(int sq) => sq & 7;
    /// <summary>Рядок у порядку виду: 0 — восьма горизонталь, 7 — перша.</summary>
    public static int Row(int sq) => sq >> 3;
    public static int Sq(int file, int row) => row * 8 + file;

    /// <summary>«e2», «h8».</summary>
    public static string Name(int sq) => $"{(char)('a' + File(sq))}{8 - Row(sq)}";

    /// <summary>«e2» → 52; -1, якщо це не поле дошки.</summary>
    public static int Parse(string? name)
    {
        if (name is not { Length: 2 }) return -1;
        var f = char.ToLowerInvariant(name[0]) - 'a';
        var rank = name[1] - '0';
        return f is < 0 or > 7 || rank is < 1 or > 8 ? -1 : Sq(f, 8 - rank);
    }

    public static int Type(int piece) => piece < 0 ? -piece : piece;
    public static bool White(int piece) => piece > 0;

    /// <summary>Літера фігури, як у FEN: великі — білі.</summary>
    public static char Letter(int piece)
    {
        var c = " pnbrqk"[Type(piece)];
        return piece > 0 ? char.ToUpperInvariant(c) : c;
    }

    static sbyte FromLetter(char c) => char.ToLowerInvariant(c) switch
    {
        'p' => Pawn, 'n' => Knight, 'b' => Bishop, 'r' => Rook, 'q' => Queen, 'k' => King, _ => 0,
    };

    // ------------------------------------------------------------------------------------------
    // Початкові позиції
    // ------------------------------------------------------------------------------------------

    /// <summary>Класична розстановка. У піддавках та сама дошка, але рокіровки там нема взагалі.</summary>
    public static ChessCore Initial(ChessVariant variant = ChessVariant.Classic)
    {
        int[] back = [Rook, Knight, Bishop, Queen, King, Bishop, Knight, Rook];
        return FromBackRank(back, variant);
    }

    /// <summary>
    /// Шахи Фішера: одна з 960 розстановок. Слонів кладемо на різнопольні клітинки, ферзя й коней —
    /// у вільні, а три поля, що лишились, завжди дають «тура — король — тура»: король між турами
    /// виходить сам собою, і жодну позицію не треба перевіряти й перекидати.
    /// 4×4 слони × 6 полів ферзя × 10 пар коней = рівно 960 однаково ймовірних розстановок.
    /// </summary>
    public static ChessCore Fischer(Random rng)
    {
        var back = new int[8];
        back[rng.Next(4) * 2] = Bishop;          // парні файли — одне поле кольору
        back[rng.Next(4) * 2 + 1] = Bishop;      // непарні — інше
        var free = Enumerable.Range(0, 8).Where(f => back[f] == 0).ToList();
        back[Take(free, rng)] = Queen;
        back[Take(free, rng)] = Knight;
        back[Take(free, rng)] = Knight;
        back[free[0]] = Rook;
        back[free[1]] = King;
        back[free[2]] = Rook;
        return FromBackRank(back, ChessVariant.Fischer);

        static int Take(List<int> free, Random rng)
        {
            var i = rng.Next(free.Count);
            var f = free[i];
            free.RemoveAt(i);
            return f;
        }
    }

    static ChessCore FromBackRank(int[] back, ChessVariant variant)
    {
        var c = new ChessCore(variant);
        for (var f = 0; f < 8; f++)
        {
            c._b[Sq(f, 0)] = -back[f];
            c._b[Sq(f, 1)] = -Pawn;
            c._b[Sq(f, 6)] = Pawn;
            c._b[Sq(f, 7)] = back[f];
        }
        if (variant != ChessVariant.Anti)
        {
            var king = Array.IndexOf(back, King);
            var shortRook = Array.FindLastIndex(back, p => p == Rook);
            var longRook = Array.IndexOf(back, Rook);
            for (var col = 0; col < 2; col++)
            {
                c._rook[col, 0] = shortRook > king ? shortRook : -1;
                c._rook[col, 1] = longRook < king ? longRook : -1;
            }
        }
        return c;
    }

    // ------------------------------------------------------------------------------------------
    // FEN
    // ------------------------------------------------------------------------------------------

    /// <summary>Позиція з FEN. Поле рокіровок читаємо і як «KQkq», і як шреддерівські файли («AHah»).</summary>
    public static ChessCore FromFen(string fen, ChessVariant variant = ChessVariant.Classic)
    {
        var parts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ArgumentException($"це не FEN: «{fen}»", nameof(fen));
        var c = new ChessCore(variant);
        var rows = parts[0].Split('/');
        if (rows.Length != 8) throw new ArgumentException($"у FEN має бути вісім рядів: «{fen}»", nameof(fen));
        for (var r = 0; r < 8; r++)
        {
            var f = 0;
            foreach (var ch in rows[r])
            {
                if (char.IsDigit(ch)) f += ch - '0';
                else c._b[Sq(f++, r)] = char.IsUpper(ch) ? FromLetter(ch) : -FromLetter(ch);
            }
        }
        c.WhiteToMove = parts[1] != "b";
        if (parts.Length > 2 && parts[2] != "-" && variant != ChessVariant.Anti)
            foreach (var ch in parts[2])
            {
                var col = char.IsUpper(ch) ? 0 : 1;
                var kingFile = File(c.KingSquare(col == 0));
                var file = char.ToLowerInvariant(ch) switch
                {
                    'k' => 7,
                    'q' => 0,
                    var x when x is >= 'a' and <= 'h' => x - 'a',
                    _ => -1,
                };
                if (file < 0) continue;
                c._rook[col, file > kingFile ? 0 : 1] = file;
            }
        c.EnPassant = parts.Length > 3 ? Parse(parts[3]) : -1;
        c.Halfmove = parts.Length > 4 && int.TryParse(parts[4], out var h) ? h : 0;
        c.Fullmove = parts.Length > 5 && int.TryParse(parts[5], out var m) ? m : 1;
        return c;
    }

    /// <summary>64 символи в порядку виду: a8..h8, a7..h7 … a1..h1; «.» — порожньо.</summary>
    public string BoardString()
    {
        var sb = new StringBuilder(64);
        foreach (var p in _b) sb.Append(p == 0 ? '.' : Letter(p));
        return sb.ToString();
    }

    public string Fen() => $"{PositionKey()} {Halfmove} {Fullmove}";

    /// <summary>
    /// FEN без лічильників — саме він і є ключем повторення позиції: розстановка, черга, права рокіровки
    /// й поле взяття на проході.
    /// </summary>
    public string PositionKey()
    {
        var sb = new StringBuilder(80);
        for (var r = 0; r < 8; r++)
        {
            var empty = 0;
            for (var f = 0; f < 8; f++)
            {
                var p = _b[Sq(f, r)];
                if (p == 0) { empty++; continue; }
                if (empty > 0) { sb.Append(empty); empty = 0; }
                sb.Append(Letter(p));
            }
            if (empty > 0) sb.Append(empty);
            if (r < 7) sb.Append('/');
        }
        sb.Append(' ').Append(WhiteToMove ? 'w' : 'b').Append(' ').Append(CastleField());
        sb.Append(' ').Append(EnPassant < 0 ? "-" : Name(EnPassant));
        return sb.ToString();
    }

    /// <summary>
    /// Поле рокіровок. Класичну розстановку (тури на a/h, король на e) пишемо звичними «KQkq», решту —
    /// файлами тур: у 960 «K» не сказало б, про яку туру мова.
    /// </summary>
    string CastleField()
    {
        var sb = new StringBuilder(4);
        for (var col = 0; col < 2; col++)
        {
            var kingFile = File(KingSquare(col == 0));
            for (var side = 0; side < 2; side++)
            {
                var file = _rook[col, side];
                if (file < 0) continue;
                var standard = kingFile == 4 && file == (side == 0 ? 7 : 0);
                var ch = standard ? (side == 0 ? 'k' : 'q') : (char)('a' + file);
                sb.Append(col == 0 ? char.ToUpperInvariant(ch) : ch);
            }
        }
        return sb.Length == 0 ? "-" : sb.ToString();
    }

    // ------------------------------------------------------------------------------------------
    // Пошук фігур і бій
    // ------------------------------------------------------------------------------------------

    /// <summary>Поле короля; -1, якщо його на дошці нема (у піддавках це нормально).</summary>
    public int KingSquare(bool white)
    {
        var want = white ? King : -King;
        for (var sq = 0; sq < 64; sq++) if (_b[sq] == want) return sq;
        return -1;
    }

    /// <summary>Чи б'є хоч хтось із кольору byWhite поле sq. Фігуру на самому полі не враховуємо — тільки нападників.</summary>
    public bool Attacked(int sq, bool byWhite)
    {
        int f = File(sq), r = Row(sq);
        // Білий пішак б'є «вгору» (рядок меншає), тож стоїть рядком нижче за поле, яке тримає під боєм.
        var pr = byWhite ? r + 1 : r - 1;
        if (pr is >= 0 and < 8)
        {
            var pawn = byWhite ? Pawn : -Pawn;
            if (f > 0 && _b[Sq(f - 1, pr)] == pawn) return true;
            if (f < 7 && _b[Sq(f + 1, pr)] == pawn) return true;
        }
        if (Hits(f, r, KnightD, byWhite ? Knight : -Knight)) return true;
        if (Hits(f, r, KingD, byWhite ? King : -King)) return true;
        if (Ray(f, r, RookD, byWhite, Rook)) return true;
        return Ray(f, r, BishopD, byWhite, Bishop);
    }

    bool Hits(int f, int r, (int Df, int Dr)[] deltas, int piece)
    {
        foreach (var (df, dr) in deltas)
        {
            int x = f + df, y = r + dr;
            if (x is >= 0 and < 8 && y is >= 0 and < 8 && _b[Sq(x, y)] == piece) return true;
        }
        return false;
    }

    /// <summary>Промінь у чотирьох напрямках: шукаємо далекобійну фігуру потрібного типу або ферзя.</summary>
    bool Ray(int f, int r, (int Df, int Dr)[] deltas, bool byWhite, int type)
    {
        var want = byWhite ? type : -type;
        var queen = byWhite ? Queen : -Queen;
        foreach (var (df, dr) in deltas)
            for (int x = f + df, y = r + dr; x is >= 0 and < 8 && y is >= 0 and < 8; x += df, y += dr)
            {
                var p = _b[Sq(x, y)];
                if (p == 0) continue;
                if (p == want || p == queen) return true;
                break;
            }
        return false;
    }

    /// <summary>Чи стоїть король кольору white під боєм. У піддавках короля можуть і збити — тоді false.</summary>
    public bool KingAttacked(bool white)
    {
        var king = KingSquare(white);
        return king >= 0 && Attacked(king, !white);
    }

    /// <summary>Шах тому, хто ходить. У піддавках шаху нема за правилами.</summary>
    public bool InCheck() => Variant != ChessVariant.Anti && KingAttacked(WhiteToMove);

    // ------------------------------------------------------------------------------------------
    // Генерація ходів
    // ------------------------------------------------------------------------------------------

    /// <summary>Псевдоходи: усе, що фігури вміють, ще без перевірки «а чи не лишиться король під боєм».</summary>
    public List<ChessMove> PseudoLegal()
    {
        var moves = new List<ChessMove>(48);
        var white = WhiteToMove;
        for (var sq = 0; sq < 64; sq++)
        {
            var p = _b[sq];
            if (p == 0 || White(p) != white) continue;
            switch (Type(p))
            {
                case Pawn: PawnMoves(moves, sq, white); break;
                case Knight: Steps(moves, sq, KnightD, white); break;
                case King: Steps(moves, sq, KingD, white); break;
                case Bishop: Slides(moves, sq, BishopD, white); break;
                case Rook: Slides(moves, sq, RookD, white); break;
                case Queen: Slides(moves, sq, BishopD, white); Slides(moves, sq, RookD, white); break;
            }
        }
        if (Variant != ChessVariant.Anti) CastleMoves(moves, white);
        return moves;
    }

    void PawnMoves(List<ChessMove> into, int sq, bool white)
    {
        int f = File(sq), r = Row(sq);
        var dr = white ? -1 : 1;              // білі йдуть до восьмої горизонталі, тобто до рядка 0
        var startRow = white ? 6 : 1;
        var lastRow = white ? 0 : 7;
        var one = r + dr;
        if (one is >= 0 and < 8 && _b[Sq(f, one)] == 0)
        {
            AddPawn(into, sq, Sq(f, one), one == lastRow, ChessMoveKind.Normal);
            var two = r + dr * 2;
            if (r == startRow && _b[Sq(f, two)] == 0) into.Add(new ChessMove((byte)sq, (byte)Sq(f, two), 0, ChessMoveKind.DoublePawn));
        }
        foreach (var df in (int[])[-1, 1])
        {
            int x = f + df, y = one;
            if (x is < 0 or > 7 || y is < 0 or > 7) continue;
            var to = Sq(x, y);
            var target = _b[to];
            if (target != 0 && White(target) != white) AddPawn(into, sq, to, y == lastRow, ChessMoveKind.Normal);
            else if (target == 0 && to == EnPassant) into.Add(new ChessMove((byte)sq, (byte)to, 0, ChessMoveKind.EnPassant));
        }
    }

    void AddPawn(List<ChessMove> into, int from, int to, bool promotes, ChessMoveKind kind)
    {
        if (!promotes) { into.Add(new ChessMove((byte)from, (byte)to, 0, kind)); return; }
        foreach (var promo in Variant == ChessVariant.Anti ? PromoPiecesAnti : PromoPieces)
            into.Add(new ChessMove((byte)from, (byte)to, promo, kind));
    }

    void Steps(List<ChessMove> into, int sq, (int Df, int Dr)[] deltas, bool white)
    {
        int f = File(sq), r = Row(sq);
        foreach (var (df, dr) in deltas)
        {
            int x = f + df, y = r + dr;
            if (x is < 0 or > 7 || y is < 0 or > 7) continue;
            var target = _b[Sq(x, y)];
            if (target == 0 || White(target) != white) into.Add(new ChessMove((byte)sq, (byte)Sq(x, y), 0, ChessMoveKind.Normal));
        }
    }

    void Slides(List<ChessMove> into, int sq, (int Df, int Dr)[] deltas, bool white)
    {
        int f = File(sq), r = Row(sq);
        foreach (var (df, dr) in deltas)
            for (int x = f + df, y = r + dr; x is >= 0 and < 8 && y is >= 0 and < 8; x += df, y += dr)
            {
                var target = _b[Sq(x, y)];
                if (target != 0 && White(target) == white) break;
                into.Add(new ChessMove((byte)sq, (byte)Sq(x, y), 0, ChessMoveKind.Normal));
                if (target != 0) break;
            }
    }

    /// <summary>
    /// Рокіровка за правилами 960 (класика — їх окремий випадок): король їде на g або c, тура — на f або d,
    /// усі поля обох маршрутів вільні від інших фігур, а король ніде дорогою не стає під бій.
    /// </summary>
    void CastleMoves(List<ChessMove> into, bool white)
    {
        var col = white ? 0 : 1;
        var back = white ? 7 : 0;
        var kingFrom = KingSquare(white);
        if (kingFrom < 0 || Row(kingFrom) != back) return;
        for (var side = 0; side < 2; side++)
        {
            var file = _rook[col, side];
            if (file < 0) continue;
            var rookFrom = Sq(file, back);
            if (_b[rookFrom] != (white ? Rook : -Rook)) continue;
            var kingTo = Sq(side == 0 ? 6 : 2, back);
            var rookTo = Sq(side == 0 ? 5 : 3, back);
            if (!PathClear(kingFrom, kingTo, kingFrom, rookFrom) || !PathClear(rookFrom, rookTo, kingFrom, rookFrom)) continue;
            if (!KingWalkSafe(kingFrom, kingTo, white)) continue;
            into.Add(new ChessMove((byte)kingFrom, (byte)rookFrom, 0, ChessMoveKind.Castle));
        }
    }

    /// <summary>Відрізок горизонталі вільний, якщо не рахувати самих короля й туру, які й поїдуть.</summary>
    bool PathClear(int from, int to, int kingFrom, int rookFrom)
    {
        var row = Row(from);
        int a = Math.Min(File(from), File(to)), b = Math.Max(File(from), File(to));
        for (var f = a; f <= b; f++)
        {
            var sq = Sq(f, row);
            if (sq != kingFrom && sq != rookFrom && _b[sq] != 0) return false;
        }
        return true;
    }

    /// <summary>Король не стоїть під шахом і не проходить через бите поле — включно з кінцевим.</summary>
    bool KingWalkSafe(int from, int to, bool white)
    {
        var row = Row(from);
        var step = Math.Sign(File(to) - File(from));
        for (var f = File(from); ; f += step)
        {
            if (Attacked(Sq(f, row), !white)) return false;
            if (f == File(to) || step == 0) break;
        }
        return true;
    }

    /// <summary>
    /// Легальні ходи. У класиці й 960 з псевдоходів викидаємо ті, після яких свій король лишається під боєм.
    /// У піддавках короля не бережуть, зате взяття обов'язкове: є чим бити — інших ходів просто нема.
    /// </summary>
    public List<ChessMove> Legal()
    {
        var pseudo = PseudoLegal();
        if (Variant == ChessVariant.Anti)
        {
            var caps = pseudo.FindAll(IsCapture);
            return caps.Count > 0 ? caps : pseudo;
        }
        var white = WhiteToMove;
        var legal = new List<ChessMove>(pseudo.Count);
        foreach (var m in pseudo)
        {
            Make(m, out var undo);
            if (!KingAttacked(white)) legal.Add(m);
            Unmake(m, undo);
        }
        return legal;
    }

    public bool IsCapture(ChessMove m) => m.Kind == ChessMoveKind.EnPassant || (m.Kind != ChessMoveKind.Castle && _b[m.To] != 0);

    /// <summary>Яку фігуру зніме цей хід (0 — жодної). Потрібно, щоб зібрати рядок збитих фігур над дошкою.</summary>
    public int CapturedPiece(ChessMove m) => m.Kind switch
    {
        ChessMoveKind.EnPassant => _b[WhiteToMove ? m.To + 8 : m.To - 8],
        ChessMoveKind.Castle => 0,
        _ => _b[m.To],
    };

    // ------------------------------------------------------------------------------------------
    // Зроби / відкоти
    // ------------------------------------------------------------------------------------------

    public void Make(in ChessMove m, out ChessUndo undo)
    {
        var piece = _b[m.From];
        var white = White(piece);
        var col = white ? 0 : 1;
        undo = new ChessUndo
        {
            Captured = 0, CapturedSq = -1,
            EnPassant = EnPassant, Halfmove = Halfmove, Fullmove = Fullmove, WhiteToMove = WhiteToMove,
            WK = _rook[0, 0], WQ = _rook[0, 1], BK = _rook[1, 0], BQ = _rook[1, 1],
        };
        Halfmove++;
        if (m.Kind == ChessMoveKind.Castle)
        {
            var rook = _b[m.To];
            var back = white ? 7 : 0;
            var kingSide = File(m.To) > File(m.From);
            // Спершу звільняємо обидва поля призначення, і лише потім ставимо фігури: у 960 король і тура
            // легко міняються місцями, і порядок «поставив — стер» з'їв би одну з них.
            _b[m.From] = 0;
            _b[m.To] = 0;
            _b[Sq(kingSide ? 6 : 2, back)] = piece;
            _b[Sq(kingSide ? 5 : 3, back)] = rook;
            _rook[col, 0] = _rook[col, 1] = -1;
            EnPassant = -1;
        }
        else
        {
            var capSq = m.Kind == ChessMoveKind.EnPassant ? (white ? m.To + 8 : m.To - 8) : m.To;
            var captured = _b[capSq];
            if (captured != 0)
            {
                undo.Captured = captured;
                undo.CapturedSq = capSq;
                _b[capSq] = 0;
                Halfmove = 0;
                if (Type(captured) == Rook) DropRookRight(white ? 1 : 0, capSq);
            }
            _b[m.From] = 0;
            _b[m.To] = m.Promo != 0 ? (white ? m.Promo : -m.Promo) : piece;
            if (Type(piece) == Pawn) Halfmove = 0;
            EnPassant = m.Kind == ChessMoveKind.DoublePawn ? (white ? m.From - 8 : m.From + 8) : -1;
            if (Type(piece) == King) _rook[col, 0] = _rook[col, 1] = -1;
            else if (Type(piece) == Rook) DropRookRight(col, m.From);
        }
        if (!WhiteToMove) Fullmove++;
        WhiteToMove = !WhiteToMove;
    }

    /// <summary>Тура зрушила зі свого поля або її збили — права на цей бік більше нема.</summary>
    void DropRookRight(int col, int sq)
    {
        if (Row(sq) != (col == 0 ? 7 : 0)) return;
        var file = File(sq);
        for (var side = 0; side < 2; side++) if (_rook[col, side] == file) _rook[col, side] = -1;
    }

    public void Unmake(in ChessMove m, in ChessUndo undo)
    {
        WhiteToMove = undo.WhiteToMove;
        EnPassant = undo.EnPassant;
        Halfmove = undo.Halfmove;
        Fullmove = undo.Fullmove;
        _rook[0, 0] = undo.WK; _rook[0, 1] = undo.WQ; _rook[1, 0] = undo.BK; _rook[1, 1] = undo.BQ;
        var white = undo.WhiteToMove;
        if (m.Kind == ChessMoveKind.Castle)
        {
            var back = white ? 7 : 0;
            var kingSide = File(m.To) > File(m.From);
            _b[Sq(kingSide ? 6 : 2, back)] = 0;
            _b[Sq(kingSide ? 5 : 3, back)] = 0;
            _b[m.From] = white ? King : -King;
            _b[m.To] = white ? Rook : -Rook;
        }
        else
        {
            _b[m.From] = m.Promo != 0 ? (white ? Pawn : -Pawn) : _b[m.To];
            _b[m.To] = 0;
            if (undo.CapturedSq >= 0) _b[undo.CapturedSq] = undo.Captured;
        }
    }

    /// <summary>Скільки листків у дереві на задану глибину — головна перевірка правил (docs/games/specs/chess.md).</summary>
    public long Perft(int depth)
    {
        var moves = Legal();
        if (depth <= 1) return depth <= 0 ? 1 : moves.Count;
        var total = 0L;
        foreach (var m in moves)
        {
            Make(m, out var undo);
            total += Perft(depth - 1);
            Unmake(m, undo);
        }
        return total;
    }

    // ------------------------------------------------------------------------------------------
    // Нічиї за матеріалом
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Матеріалу не вистачить на мат за жодної гри: голі королі, король зі слоном чи конем проти голого
    /// і два однопольні слони. Решту (двоє коней, наприклад) правила нічиєю не оголошують.
    /// </summary>
    public bool InsufficientMaterial()
    {
        if (Variant == ChessVariant.Anti) return false;
        int wn = 0, wb = 0, bn = 0, bb = 0, wbColor = -1, bbColor = -1;
        for (var sq = 0; sq < 64; sq++)
        {
            var p = _b[sq];
            if (p == 0) continue;
            switch (Type(p))
            {
                case King: break;
                case Knight: if (p > 0) wn++; else bn++; break;
                case Bishop:
                    if (p > 0) { wb++; wbColor = (File(sq) + Row(sq)) & 1; }
                    else { bb++; bbColor = (File(sq) + Row(sq)) & 1; }
                    break;
                default: return false;      // пішак, тура або ферзь — матеріалу досить
            }
        }
        var w = wn + wb;
        var b = bn + bb;
        if (w == 0 && b == 0) return true;                    // K проти K
        if (w + b == 1) return true;                          // K+B або K+N проти K
        return wb == 1 && bb == 1 && w == 1 && b == 1 && wbColor == bbColor;   // однопольні слони
    }

    // ------------------------------------------------------------------------------------------
    // SAN
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Хід у людському записі («e4», «Nf3», «exd5», «O-O», «e8=Q»). Знак шаху чи мата дописує той, хто
    /// вже зробив хід і бачить, що лишилось суперникові.
    /// </summary>
    public string San(ChessMove m, IReadOnlyList<ChessMove> legal)
    {
        if (m.Kind == ChessMoveKind.Castle) return File(m.To) > File(m.From) ? "O-O" : "O-O-O";
        var piece = _b[m.From];
        var type = Type(piece);
        var capture = IsCapture(m);
        var to = Name(m.To);
        var promo = m.Promo == 0 ? "" : $"={char.ToUpperInvariant(" pnbrqk"[m.Promo])}";
        if (type == Pawn) return (capture ? $"{(char)('a' + File(m.From))}x" : "") + to + promo;

        // Уточнення потрібне лише тоді, коли на це саме поле може піти ще така сама фігура.
        var twins = legal.Where(o => o.To == m.To && o.From != m.From && Type(_b[o.From]) == type
                                     && o.Kind != ChessMoveKind.Castle).ToList();
        var mark = "";
        if (twins.Count > 0)
        {
            var sameFile = twins.Any(o => File(o.From) == File(m.From));
            var sameRow = twins.Any(o => Row(o.From) == Row(m.From));
            mark = !sameFile ? $"{(char)('a' + File(m.From))}"
                 : !sameRow ? $"{8 - Row(m.From)}"
                 : Name(m.From);
        }
        return $"{char.ToUpperInvariant(" pnbrqk"[type])}{mark}{(capture ? "x" : "")}{to}";
    }
}
