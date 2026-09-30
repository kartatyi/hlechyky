using System.Numerics;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Правила реверсі (класичне Отелло 8×8) без кімнат. Дошка — два бітборди, по <c>ulong</c> на колір:
/// біт i — поле i. Нотація Отелло: стовпці a–h зліва направо, рядки 1–8 ЗГОРИ вниз, i = (рядок − 1)·8 + стовпець,
/// 0 = a1 (лівий верхній кут), 63 = h8. Сторона 0 — чорні (ходять першими), 1 — білі.
/// <para>
/// Ходи шукаємо зсувами цілих бітбордів у 8 напрямках (без циклу по полях) — так швидко, що двигун Глека
/// встигає сотні тисяч позицій за ~100 мс.
/// </para>
/// </summary>
public sealed class ReversiCore
{
    public const int Black = 0, White = 1;

    /// <summary>Бітборди: [0] — чорні, [1] — білі.</summary>
    public ulong[] Discs { get; } = new ulong[2];
    /// <summary>Чия черга: 0 — чорні, 1 — білі.</summary>
    public int Side { get; set; }

    public static ReversiCore Start()
    {
        var c = new ReversiCore();
        c.Discs[Black] = Bit(Parse("e4")!.Value) | Bit(Parse("d5")!.Value);
        c.Discs[White] = Bit(Parse("d4")!.Value) | Bit(Parse("e5")!.Value);
        c.Side = Black;
        return c;
    }

    public ReversiCore Clone()
    {
        var c = new ReversiCore { Side = Side };
        c.Discs[0] = Discs[0];
        c.Discs[1] = Discs[1];
        return c;
    }

    public static ulong Bit(int i) => 1UL << i;

    public static string Name(int i) => $"{(char)('a' + i % 8)}{i / 8 + 1}";

    public static int? Parse(string? s)
    {
        if (s is not { Length: 2 }) return null;
        int col = char.ToLowerInvariant(s[0]) - 'a', row = s[1] - '1';
        return col is >= 0 and < 8 && row is >= 0 and < 8 ? row * 8 + col : null;
    }

    // ---------- бітова механіка (статична — нею ж рахує двигун) ----------

    /// <summary>Усі поля, куди може поставити фішку той, чиї фішки <paramref name="me"/>.</summary>
    /// <remarks>Чужі фішки в стовпцях a і h для бокових напрямків маскуємо: їх однаково не затиснеш, а зсув
    /// тоді не загортається через край — і перевіряти край на кожному кроці не треба.</remarks>
    public static ulong Moves(ulong me, ulong opp)
    {
        var empty = ~(me | opp);
        var side = opp & Inner;
        return MovesL(me, side, empty, 1) | MovesR(me, side, empty, 1)
            | MovesL(me, opp, empty, 8) | MovesR(me, opp, empty, 8)
            | MovesL(me, side, empty, 7) | MovesR(me, side, empty, 7)
            | MovesL(me, side, empty, 9) | MovesR(me, side, empty, 9);
    }

    const ulong Inner = 0x7E7E7E7E7E7E7E7EUL;   // без стовпців a і h

    static ulong MovesL(ulong me, ulong opp, ulong empty, int s)
    {
        var x = (me << s) & opp;
        x |= (x << s) & opp;
        x |= (x << s) & opp;
        x |= (x << s) & opp;
        x |= (x << s) & opp;
        x |= (x << s) & opp;
        return (x << s) & empty;
    }

    static ulong MovesR(ulong me, ulong opp, ulong empty, int s)
    {
        var x = (me >> s) & opp;
        x |= (x >> s) & opp;
        x |= (x >> s) & opp;
        x |= (x >> s) & opp;
        x |= (x >> s) & opp;
        x |= (x >> s) & opp;
        return (x >> s) & empty;
    }

    /// <summary>Фішки, що перевернуться від ходу на поле <paramref name="cell"/> (0 — хід незаконний).</summary>
    public static ulong Flips(ulong me, ulong opp, int cell)
    {
        var at = Bit(cell);
        if (((me | opp) & at) != 0) return 0;
        var side = opp & Inner;
        return FlipL(at, me, side, 1) | FlipR(at, me, side, 1)
            | FlipL(at, me, opp, 8) | FlipR(at, me, opp, 8)
            | FlipL(at, me, side, 7) | FlipR(at, me, side, 7)
            | FlipL(at, me, side, 9) | FlipR(at, me, side, 9);
    }

    static ulong FlipL(ulong at, ulong me, ulong opp, int s)
    {
        ulong run = 0;
        var x = at << s;
        while ((x & opp) != 0) { run |= x; x <<= s; }
        return (x & me) != 0 ? run : 0;
    }

    static ulong FlipR(ulong at, ulong me, ulong opp, int s)
    {
        ulong run = 0;
        var x = at >> s;
        while ((x & opp) != 0) { run |= x; x >>= s; }
        return (x & me) != 0 ? run : 0;
    }

    public static int Pop(ulong b) => BitOperations.PopCount(b);

    public static IEnumerable<int> Cells(ulong b)
    {
        while (b != 0)
        {
            yield return BitOperations.TrailingZeroCount(b);
            b &= b - 1;
        }
    }

    // ---------- зручності для кімнати ----------

    public ulong LegalMask(int side) => Moves(Discs[side], Discs[1 - side]);

    public int[] Legal(int side) => [.. Cells(LegalMask(side))];

    public ulong Flips(int side, int cell) => cell is >= 0 and < 64 ? Flips(Discs[side], Discs[1 - side], cell) : 0;

    public bool HasMove(int side) => LegalMask(side) != 0;

    public int Count(int side) => Pop(Discs[side]);

    public int Empties => 64 - Pop(Discs[0] | Discs[1]);

    /// <summary>Гра скінчилась: ходу нема ні в кого (зокрема повна дошка чи хтось без фішок).</summary>
    public bool Over => !HasMove(0) && !HasMove(1);

    /// <summary>
    /// Поставити фішку того, чия черга, на <paramref name="cell"/>. Повертає перевернуті фішки; 0 — хід незаконний,
    /// і тоді дошка не змінилась. Черга переходить суперникові — пас вирішує <see cref="PassIfStuck"/>.
    /// </summary>
    public ulong Play(int cell)
    {
        var flips = Flips(Side, cell);
        if (flips == 0) return 0;
        Discs[Side] |= flips | Bit(cell);
        Discs[1 - Side] &= ~flips;
        Side = 1 - Side;
        return flips;
    }

    /// <summary>У того, чия черга, ходу нема, а в суперника є — черга повертається супернику. true — був пас.</summary>
    public bool PassIfStuck()
    {
        if (HasMove(Side) || !HasMove(1 - Side)) return false;
        Side = 1 - Side;
        return true;
    }

    /// <summary>64 символи: '.' порожнє, 'b' чорна, 'w' біла.</summary>
    public string BoardString()
    {
        var s = new char[64];
        for (var i = 0; i < 64; i++)
            s[i] = (Discs[Black] & Bit(i)) != 0 ? 'b' : (Discs[White] & Bit(i)) != 0 ? 'w' : '.';
        return new string(s);
    }

    /// <summary>Позиція з рядка <see cref="BoardString"/> (для тестів і збережень). null — рядок кривий.</summary>
    public static ReversiCore? FromString(string board, int side)
    {
        if (board.Length != 64 || side is not (0 or 1)) return null;
        var c = new ReversiCore { Side = side };
        for (var i = 0; i < 64; i++)
            switch (board[i])
            {
                case 'b': c.Discs[Black] |= Bit(i); break;
                case 'w': c.Discs[White] |= Bit(i); break;
                case '.': break;
                default: return null;
            }
        return c;
    }

    /// <summary>
    /// Perft: скільки позицій на глибині <paramref name="depth"/> півходів. Пас рахується півходом, кінець гри — листом.
    /// Від початку: 4, 12, 56, 244, 1396, 8200, 55092, 390216 (OEIS A124004).
    /// </summary>
    public static long Perft(ulong me, ulong opp, int depth, bool passed = false)
    {
        if (depth == 0) return 1;
        var moves = Moves(me, opp);
        if (moves == 0)
            return passed ? 1 : Perft(opp, me, depth - 1, true);
        if (depth == 1) return Pop(moves);
        long n = 0;
        foreach (var cell in Cells(moves))
        {
            var f = Flips(me, opp, cell);
            n += Perft(opp & ~f, me | f | Bit(cell), depth - 1);
        }
        return n;
    }
}
