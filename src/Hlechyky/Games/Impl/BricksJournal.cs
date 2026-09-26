using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Одне місце за столом «Цеглин»: серверна копія стіни плюс усе, що треба судді — номер «епохи» виправлень,
/// чи просили звірку, коли востаннє слали <c>fix</c>, що з цієї стіни вже пішло в кадр.
/// </summary>
public sealed class BricksSeat
{
    public readonly BricksCore Core = new();
    /// <summary>Грає в цьому раунді (сидів на старті раунду й не встав).</summary>
    public bool Plays;
    /// <summary>Нік на старті раунду — щоб підписати стіну навіть того, хто вже встав.</summary>
    public string? Nick;
    /// <summary>Місце в раунді: 0 — ще грає, 1 — узяв раунд, 2+ — вибув.</summary>
    public int Rank;
    public int Wins, MatchLines, MatchSent, MatchRecv;
    /// <summary>
    /// Епоха виправлень: росте на кожен <c>fix</c>. Клієнт пише її в кожну пачку (<c>f</c>), і пачки, відправлені
    /// ще до того, як він прийняв виправлення, сервер мовчки викидає — інакше вони б наздоганяли <c>fix</c> і
    /// збивали нумерацію знову.
    /// </summary>
    public int Epoch;
    public bool NeedFix;
    public int LastFixAt = -1_000_000;
    /// <summary>Стіна змінилась з минулого кадра (нова пачка, сервер вів її сам, посилка, вибування).</summary>
    public bool Dirty = true;
    /// <summary>Версія клітинок, яку вже бачили в кадрі: ряди шлемо, лише коли вона змінилась.</summary>
    public int SentCellsVer = -1;
    public bool FourAsked;
    /// <summary>Коли (тик стіни за годинником сервера) був останній справжній натиск — для «стіну покинуто» у спринті.</summary>
    public int LastKeyWall;
}

/// <summary>
/// Розбір і перевірка журналу натисків <c>j</c> (spec §3.1): пачка або приймається цілком, або відкидається
/// цілком. Годинник стіни рухають лише події журналу; сервер перевіряє вікно часу (<see cref="BricksCore.Ahead"/>
/// вперед), нумерацію й частоту, переганяє події через рушій і звіряє хеш. Будь-що підозріле — <c>fix</c>:
/// клієнт бере серверну стіну. Без алокацій: числа пачки — у полі-буфері.
/// </summary>
public sealed class BricksJournal
{
    public const int MaxNumbers = 128;
    public const int MaxSameTick = 4;
    /// <summary>Не частіше одного виправлення на місце за стільки тиків каркаса (1 с при 40 мс).</summary>
    public const int FixEvery = 25;

    public const string Malformed = "Журнал не читається";
    public const string Gap = "Журнал із дірою";
    public const string Past = "Журнал із минулого";
    public const string Future = "Журнал із майбутнього";
    public const string Burst = "Забагато натисків за раз";
    public const string Out = "Ти вже вибув — дивись, як мучаться інші";

    readonly int[] _buf = new int[MaxNumbers];
    int _n;
    int _q;
    bool _hasHash;
    uint _hash;
    bool _hasEpoch;
    int _epoch;
    bool _hasG;
    int _g;

    /// <summary>Скільки справжніх натисків (не пульсів) було в останній прийнятій пачці.</summary>
    public int Keys { get; private set; }

    /// <summary>
    /// Застосувати пачку до стіни місця. <paramref name="wallTick"/> — годинник сервера (60 Гц від «go»).
    /// Повертає відмову (текст для тестів через <c>Act</c>) або <see cref="ActResult.Done"/>; сміття, вибування
    /// й події кадра кімната забирає з рушія сама.
    /// </summary>
    public ActResult Apply(BricksSeat seat, JsonElement payload, int wallTick)
    {
        Keys = 0;
        var b = seat.Core;
        if (!b.Alive) return ActResult.Fail(Out);
        if (!Parse(payload)) return ActResult.Fail(Malformed);
        // Пачка, відправлена до того, як клієнт прийняв виправлення, — мовчки мимо: fix уже в дорозі.
        if (_hasEpoch && _epoch < seat.Epoch) return ActResult.Done;
        if (_hasEpoch && _epoch > seat.Epoch) return ActResult.Fail(Malformed);

        var count = _n / 2;
        var last = _q + count - 1;
        if (last <= b.Seq) return ActResult.Done;           // дублікат — уже все застосовано
        if (_q > b.Seq + 1) return Refuse(seat, Gap);
        var from = b.Seq + 1 - _q;                           // перекриття з уже прийнятим — пропускаємо голову

        var prev = b.Tick;
        var same = 0;
        for (var i = from; i < count; i++)
        {
            var t = _buf[i * 2];
            if (t < prev) return Refuse(seat, Past);
            if (t > wallTick + BricksCore.Ahead) return Refuse(seat, Future);
            same = t == prev && i > from ? same + 1 : 1;
            if (same > MaxSameTick) return Refuse(seat, Burst);
            prev = t;
        }

        for (var i = from; i < count; i++)
        {
            var t = _buf[i * 2];
            var k = _buf[i * 2 + 1];
            b.AdvanceTo(t);
            b.Apply(k);
            b.Seq++;
            if (k != BricksCore.KPulse) Keys++;
            if (!b.Alive) { b.Seq = last; break; }
        }
        seat.Dirty = true;
        // Хеш звіряємо, лише коли клієнт знав про ті самі посилки, що й сервер: пачка, відправлена до того, як
        // до нього долетіла подія g, і має інший хеш — це не розбіжність, він відкотиться сам. Знає більше,
        // ніж сервер надіслав, — це вже вигадка, тоді виправлення.
        if (_hasHash && b.Alive)
        {
            if (_hasG && _g > b.Gseq) seat.NeedFix = true;
            else if ((!_hasG || _g == b.Gseq) && _hash != b.Hash()) seat.NeedFix = true;
        }
        return ActResult.Done;
    }

    static ActResult Refuse(BricksSeat seat, string why)
    {
        seat.NeedFix = true;
        return ActResult.Fail(why);
    }

    /// <summary>
    /// <c>{ q, e: [t, k, …], h?, g?, f? }</c>: цілі, парна довжина до 128, коди з таблиці §3.1. <c>g</c> — номер
    /// останньої посилки, яку клієнт урахував у <c>h</c>; <c>f</c> — епоха виправлень, яку він прийняв.
    /// </summary>
    bool Parse(JsonElement p)
    {
        _n = 0;
        _hasHash = _hasEpoch = _hasG = false;
        if (p.ValueKind != JsonValueKind.Object) return false;
        if (!p.TryGetProperty("q", out var q) || q.ValueKind != JsonValueKind.Number || !q.TryGetInt32(out _q) || _q < 1) return false;
        if (!p.TryGetProperty("e", out var e) || e.ValueKind != JsonValueKind.Array) return false;
        var len = e.GetArrayLength();
        if (len == 0 || len % 2 != 0 || len > MaxNumbers) return false;
        foreach (var x in e.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out var v)) return false;
            if (_n % 2 == 0 ? v < 0 : !BricksCore.KnownKey(v)) return false;
            _buf[_n++] = v;
        }
        if (p.TryGetProperty("h", out var h) && h.ValueKind != JsonValueKind.Null)
        {
            if (h.ValueKind != JsonValueKind.Number || !h.TryGetUInt32(out _hash)) return false;
            _hasHash = true;
        }
        if (p.TryGetProperty("g", out var g) && g.ValueKind != JsonValueKind.Null)
        {
            if (g.ValueKind != JsonValueKind.Number || !g.TryGetInt32(out _g) || _g < 0) return false;
            _hasG = true;
        }
        if (p.TryGetProperty("f", out var f) && f.ValueKind != JsonValueKind.Null)
        {
            if (f.ValueKind != JsonValueKind.Number || !f.TryGetInt32(out _epoch) || _epoch < 0) return false;
            _hasEpoch = true;
        }
        return true;
    }

    /// <summary>
    /// Сервер веде стіну сам, якщо клієнт мовчить довше за <see cref="BricksCore.Behind"/> тиків: гравітація
    /// падає далі, а коли клієнт озветься зі старими мітками — отримає «з минулого» і виправлення.
    /// </summary>
    public static bool Drive(BricksSeat seat, int wallTick)
    {
        var b = seat.Core;
        if (!b.Alive || wallTick - b.Tick <= BricksCore.Behind) return false;
        b.AdvanceTo(wallTick - BricksCore.Behind);
        seat.Dirty = true;
        return true;
    }

    /// <summary>Чи вже можна слати цьому місцю виправлення (не частіше за раз на секунду).</summary>
    public static bool FixDue(BricksSeat seat, int roomTick) => seat.NeedFix && roomTick - seat.LastFixAt >= FixEvery;
}

/// <summary>Стіни на дріт (spec §4): повний <c>BoardWire</c> для виду й виправлень, короткий запис для кадра.</summary>
public static class BricksWire
{
    /// <summary>Повний стан стіни — з нього клієнт відновлюється після F5 чи виправлення.</summary>
    public static object Board(int s, BricksSeat seat)
    {
        var b = seat.Core;
        return new
        {
            s,
            nk = seat.Nick,
            a = b.Alive ? 1 : 0,
            rk = seat.Rank,
            l = b.Lines,
            sn = b.Sent,
            rc = b.Recv,
            k = b.Tick,
            q = b.Seq,
            g = b.Gseq,
            fx = seat.Epoch,
            r = b.WireRows(),
            p = b.WirePiece(),
            hd = b.Hold,
            hu = b.HoldUsed ? 1 : 0,
            pi = b.Pi,
            n = b.Next3(),
            cb = b.Combo,
            bb = b.B2b,
            ky = b.Keys,
            dr = b.Dir,
            ds = b.DasT,
            gt = b.GravT,
            lt = b.LockT,
            rs = b.Resets,
            ly = b.LowY,
            cl = b.Clearing,
            lr = b.LastRot ? 1 : 0,
            cr = b.WireCredits(),
            h = b.Hash(),
        };
    }

    /// <summary>Стіна в кадрі: лише те, що рухається; ряди — коли змінились або це ключовий кадр.</summary>
    public static object Frame(int s, BricksSeat seat, bool rows)
    {
        var b = seat.Core;
        // Два різні записи, а не r = null: порожнього поля на дроті не має бути зовсім — кадр летить 25 разів на секунду.
        if (rows)
            return new
            {
                s,
                a = b.Alive ? 1 : 0,
                l = b.Lines,
                pd = b.PendingRows,
                rp = b.RipeRows,
                v = b.CellsVer,
                r = b.WireRows(),
                p = b.WirePiece(),
                hd = b.Hold,
                n = b.Next3(),
                k = b.Tick,
                q = b.Seq,
                g = b.Gseq,
                x = b.Hash(),
            };
        return new
        {
            s,
            a = b.Alive ? 1 : 0,
            l = b.Lines,
            pd = b.PendingRows,
            rp = b.RipeRows,
            v = b.CellsVer,
            p = b.WirePiece(),
            hd = b.Hold,
            n = b.Next3(),
            k = b.Tick,
            q = b.Seq,
            g = b.Gseq,
            x = b.Hash(),
        };
    }
}
