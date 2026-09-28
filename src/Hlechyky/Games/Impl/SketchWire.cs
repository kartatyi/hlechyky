namespace Hlechyky.Games.Impl;

/// <summary>
/// Компактний малюнок на дроті (Піктіонарі, прохід №3, п. 32): операції <see cref="Sketch"/> не масивами чисел у JSON
/// («[0,12,1,8,523,301,527,304,…]» — ~4 байти на координату), а байтами в base64: координати — різницею з
/// попередньою точкою (зиґзаґ + varint, здебільшого один байт). Повний вид із малюнком — у ~3 рази менший.
/// <para>
/// Формат операції: вид (байт), штрих (varint), колір (байт), товщина (байт), точок (varint), перша точка x, y
/// (varint), далі dx, dy кожної наступної (зиґзаґ-varint). Той самий розбір — у web/games/pictionary.js (<c>unpack</c>)
/// і в публічному альбомі. Зіпсований телефон шле масиви, як і раніше: цей клас — лише для Піктіонарі.
/// </para>
/// </summary>
public static class SketchWire
{
    /// <summary>Операції з <paramref name="from"/> до кінця — рядком base64.</summary>
    public static string Pack(IReadOnlyList<int[]> ops, int from = 0)
    {
        var buf = new Buf();
        for (var i = Math.Max(0, from); i < ops.Count; i++)
        {
            var op = ops[i];
            if (op.Length < 6) continue;
            buf.Byte(op[0]);
            buf.Var((uint)op[1]);
            buf.Byte(op[2]);
            buf.Byte(op[3]);
            var n = (op.Length - 4) / 2;
            buf.Var((uint)n);
            int px = op[4], py = op[5];
            buf.Var((uint)px);
            buf.Var((uint)py);
            for (var k = 1; k < n; k++)
            {
                int x = op[4 + 2 * k], y = op[5 + 2 * k];
                buf.Var(Zig(x - px));
                buf.Var(Zig(y - py));
                px = x; py = y;
            }
        }
        return Convert.ToBase64String(buf.Data, 0, buf.Length);
    }

    /// <summary>Назад у масиви (тести, перевірка того, що лягає в альбом). null — рядок битий.</summary>
    public static List<int[]>? Unpack(string? z, int maxOps = Sketch.DefaultMaxOps)
    {
        if (z is null) return null;
        byte[] b;
        try { b = Convert.FromBase64String(z); }
        catch (FormatException) { return null; }
        var ops = new List<int[]>();
        var at = 0;
        try
        {
            while (at < b.Length)
            {
                if (ops.Count >= maxOps) return null;
                var kind = b[at++];
                var stroke = (int)Var(b, ref at);
                var color = b[at++];
                var width = b[at++];
                var n = (int)Var(b, ref at);
                if (kind > 1 || n < 1 || n > Sketch.MaxChunkPoints || color >= Sketch.Colors) return null;
                var op = new int[4 + 2 * n];
                op[0] = kind; op[1] = stroke; op[2] = color; op[3] = width;
                int x = (int)Var(b, ref at), y = (int)Var(b, ref at);
                op[4] = x; op[5] = y;
                for (var k = 1; k < n; k++)
                {
                    x += Unzig(Var(b, ref at));
                    y += Unzig(Var(b, ref at));
                    op[4 + 2 * k] = x; op[5 + 2 * k] = y;
                }
                for (var k = 4; k < op.Length; k += 2)
                    if (op[k] is < 0 or > Sketch.CanvasW || op[k + 1] is < 0 or > Sketch.CanvasH) return null;
                ops.Add(op);
            }
        }
        catch (IndexOutOfRangeException) { return null; }
        return ops;
    }

    static uint Zig(int v) => (uint)((v << 1) ^ (v >> 31));
    static int Unzig(uint v) => (int)(v >> 1) ^ -(int)(v & 1);

    static uint Var(byte[] b, ref int at)
    {
        uint v = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var x = b[at++];
            v |= (uint)(x & 0x7f) << shift;
            if (x < 0x80) return v;
        }
        throw new IndexOutOfRangeException();
    }

    sealed class Buf
    {
        public byte[] Data = new byte[256];
        public int Length;

        public void Byte(int v)
        {
            if (Length == Data.Length) Array.Resize(ref Data, Data.Length * 2);
            Data[Length++] = (byte)v;
        }

        public void Var(uint v)
        {
            while (v >= 0x80) { Byte((int)(v & 0x7f) | 0x80); v >>= 7; }
            Byte((int)v);
        }
    }
}
