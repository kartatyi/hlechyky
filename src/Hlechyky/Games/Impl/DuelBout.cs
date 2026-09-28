namespace Hlechyky.Games.Impl;

/// <summary>Що повертає тик поєдинку: нічого, кадр, оголошений раунд (кадр + види) чи кінець паузи після раунду.</summary>
public enum BoutTick { None, Frame, Announced, PauseOver }

/// <summary>
/// Один поєдинок двох стрільців — серце <see cref="Duel"/> і <see cref="DuelCup"/> (турнір — це ті самі
/// поєдинки, лише пари міняються). Сторона 0/1 — ліворуч/праворуч на вулиці, <see cref="Seats"/> каже, які це
/// місця за столом. Уся правда про раунд тут: коли «ВОГОНЬ!», обманки, сигнал, поправка на пінг.
/// <para>
/// Фази міняє лише <see cref="Tick"/>; постріли тільки записують, чим раунд скінчився. Проміжок від пострілу до
/// тика — той «один тик» зі spec, коли другий ще встигає долучитись (і «обидва поспішили» ловиться).
/// </para>
/// </summary>
public sealed class DuelBout(IRoomContext ctx)
{
    /// <summary>Сигнали «ВОГОНЬ!» (п. 67): напис зі спалахом, лише дзвін, спалах сонця, зміна неба.</summary>
    public static readonly string[] Signals = ["word", "bell", "sun", "sky"];
    /// <summary>Обманки (п. 65): усі на «ВО…», щоб рука смикнулась раніше, ніж голова дочитала.</summary>
    public static readonly string[] Baits = ["ВОДА!", "ВОРОН!", "ВОГНИК!", "ВОВК!", "ВОРОТА!", "ВОЛИ!", "ВОЗИ!"];
    /// <summary>Коротше «Цілься…» обманку не ставимо: між нею і «ВОГОНЬ!» має бути хоч 0,7 с.</summary>
    public const int BaitMinAimMs = 2300, BaitAfterAimMs = 600, BaitBeforeFireMs = 700;
    /// <summary>Постріл до «ВОГОНЬ!» у межах цього часу після обманки — «стрельнув на ВОРОН».</summary>
    public const int BaitBlameMs = 2500;

    /// <summary>Які місця за столом стоять ліворуч і праворуч.</summary>
    public readonly int[] Seats = [0, 1];
    public int WinsNeeded = Duel.WinsNeeded;
    public bool Decoys, MixSignals;
    /// <summary>Поправка на пінг за місцем кімнати (мс); null — без поправки.</summary>
    public Func<int, int>? Comp;
    /// <summary>Влучний постріл (сторона, чесні мс для таблиці) → позначка рекорду 'pb'|'week'|null.</summary>
    public Func<int, long, string?>? Scored;
    /// <summary>Постріл зарахували в статистику партії (сторона, мс з поправкою).</summary>
    public Action<int, long>? Counted;

    public DuelPhase Phase { get; private set; } = DuelPhase.Ready;
    public int Round { get; private set; } = 1;
    public readonly int[] Wins = new int[2];
    public int Idle { get; private set; }
    public string Signal { get; private set; } = "word";

    DateTimeOffset _aimAt, _fireAt, _deadline;
    string? _reason;
    int? _winner;
    int _first = -1;
    readonly long?[] _raw = new long?[2], _ms = new long?[2];

    DateTimeOffset? _baitAt;
    string? _baitWord;
    bool _baitShown, _baited;
    DateTimeOffset _baitShownAt;
    int _baitN;

    bool _hasLast;
    string? _lastReason, _lastBait;
    int? _lastWinner;
    readonly long?[] _lastMs = new long?[2], _lastPc = new long?[2];
    readonly string?[] _lastNr = new string?[2];
    bool _lastHasPc;

    public int? LastWinner => _hasLast ? _lastWinner : null;

    /// <summary>Нова серія з чистого рахунку. <paramref name="extraReadyMs"/> — довше «Готуйсь…» (у турнірі: «наступна пара»).</summary>
    public void Reset(DateTimeOffset now, int extraReadyMs = 0)
    {
        Wins[0] = Wins[1] = 0;
        Round = 1;
        Idle = 0;
        _hasLast = false;
        _lastReason = _lastBait = null;
        _lastWinner = null;
        Array.Clear(_lastMs);
        Array.Clear(_lastPc);
        Array.Clear(_lastNr);
        NewRound(now, extraReadyMs);
    }

    void NewRound(DateTimeOffset now, int extraReadyMs = 0)
    {
        Phase = DuelPhase.Ready;
        _aimAt = now.AddMilliseconds(Duel.ReadyMs + extraReadyMs);
        var aim = Duel.AimMs(ctx.Rng);
        _fireAt = _aimAt.AddMilliseconds(aim);
        _deadline = _fireAt;
        _reason = null;
        _winner = null;
        _first = -1;
        _raw[0] = _raw[1] = _ms[0] = _ms[1] = null;
        _baitAt = null;
        _baitWord = null;
        _baitShown = _baited = false;
        // Генератор чіпаємо лише з увімкненими опціями: типова дуель іде тим самим сідом, що й раніше.
        Signal = MixSignals ? Signals[ctx.Rng.Next(Signals.Length)] : "word";
        if (Decoys && aim >= BaitMinAimMs && ctx.Rng.Next(100) < 65)
        {
            _baitAt = _aimAt.AddMilliseconds(BaitAfterAimMs + ctx.Rng.Next(aim - BaitAfterAimMs - BaitBeforeFireMs));
            _baitWord = Baits[ctx.Rng.Next(Baits.Length)];
        }
    }

    /// <summary>Постріл сторони <paramref name="side"/>: до «ВОГОНЬ!» — фальстарт, після — реакція.</summary>
    public ActResult Shoot(int side, DateTimeOffset now, string doneText)
    {
        if (Phase is DuelPhase.Result) return ActResult.Fail("Раунд уже скінчився — мить, і буде наступний");
        if (Phase is DuelPhase.Done) return ActResult.Fail(doneText);
        if (Phase == DuelPhase.Fire) Hit(side, now);
        else FalseStart(side, now);
        return ActResult.Done;
    }

    void Hit(int side, DateTimeOffset now)
    {
        // Вікно зачинилось, а фазу міняє тільки Tick: за правилом це вже «заснули обидва».
        if (now >= _deadline) return;
        if (_raw[side] is not null) return;   // двічі за раунд не стріляють
        var raw = (long)Math.Max(1, Math.Round((now - _fireAt).TotalMilliseconds));
        _raw[side] = raw;
        _ms[side] = Math.Max(1, raw - CompOf(side));
        if (_reason is null)
        {
            _reason = "shot";
            _first = side;
        }
    }

    void FalseStart(int side, DateTimeOffset now)
    {
        if (_baitShown && (now - _baitShownAt).TotalMilliseconds <= BaitBlameMs) _baited = true;
        var other = 1 - side;
        if (_reason is null)
        {
            _reason = "false";
            _winner = other;
            return;
        }
        if (_reason == "false" && _winner == side)
        {
            _reason = "both-false";
            _winner = null;
        }
    }

    int CompOf(int side) => Comp is null ? 0 : Math.Max(0, Comp(Seats[side]));

    /// <summary>
    /// Чи можна вже оголошувати. Без поправки — одразу. З поправкою перший постріл ще не перемога: суперник
    /// із гіршим пінгом може прилетіти пізніше, але з меншою реакцією. Чекаємо рівно стільки, скільки йому дає
    /// поправка (≤ стелі) — далі він уже не обжене.
    /// </summary>
    bool Settled(DateTimeOffset now)
    {
        if (_reason != "shot") return true;
        if (_raw[0] is not null && _raw[1] is not null) return true;
        var other = 1 - _first;
        var until = _fireAt.AddMilliseconds(_ms[_first]!.Value + CompOf(other));
        return now >= until || now >= _deadline;
    }

    public BoutTick Tick(DateTimeOffset now)
    {
        if (Phase == DuelPhase.Done) return BoutTick.None;
        if (Phase == DuelPhase.Result) return now >= _deadline ? BoutTick.PauseOver : BoutTick.None;
        if (_reason is not null && Settled(now)) { Announce(now); return BoutTick.Announced; }

        if (Phase == DuelPhase.Ready && now >= _aimAt)
        {
            Phase = DuelPhase.Aim;
            return BoutTick.Frame;
        }
        if (Phase == DuelPhase.Aim)
        {
            if (now >= _fireAt)
            {
                // fireAt — мить, коли кадр «ВОГОНЬ!» справді пішов, а не запланована точка між тиками.
                Phase = DuelPhase.Fire;
                _fireAt = now;
                _deadline = now.AddMilliseconds(Duel.FireWindowMs);
                return BoutTick.Frame;
            }
            if (_baitAt is { } b && !_baitShown && now >= b)
            {
                _baitShown = true;
                _baitShownAt = now;
                _baitN++;
                return BoutTick.Frame;
            }
        }
        if (Phase == DuelPhase.Fire && now >= _deadline)
        {
            if (_reason is null) { _reason = "sleep"; _winner = null; }
            Announce(now);
            return BoutTick.Announced;
        }
        return BoutTick.None;
    }

    void Announce(DateTimeOffset now)
    {
        if (_reason == "shot")
            _winner = _ms[0] is { } a && _ms[1] is { } b ? (a < b ? 0 : b < a ? 1 : _first) : _ms[0] is not null ? 0 : 1;
        if (_winner is { } w) Wins[w]++;
        _lastHasPc = Comp is not null;
        for (var side = 0; side < 2; side++)
        {
            _lastNr[side] = null;
            _lastPc[side] = _raw[side] is { } r && _ms[side] is { } m ? r - m : null;
            if (_raw[side] is not { } raw) continue;
            Counted?.Invoke(side, _ms[side]!.Value);
            // Нелюдські мілісекунди (скрипт у консолі) у таблицю й рекорди не пускаємо.
            if (raw >= Duel.HumanFloorMs && Scored is not null) _lastNr[side] = Scored(side, raw);
        }
        Idle = _reason == "sleep" ? Idle + 1 : 0;
        _hasLast = true;
        _lastReason = _reason;
        _lastWinner = _winner;
        _lastBait = _baited && _reason is "false" or "both-false" ? _baitWord : null;
        _lastMs[0] = _ms[0];
        _lastMs[1] = _ms[1];
        Phase = DuelPhase.Result;
        _deadline = now.AddMilliseconds(Duel.ResultMs);
    }

    /// <summary>Після паузи — наступний раунд. Перегравання номер раунду не рухає.</summary>
    public void Next(DateTimeOffset now)
    {
        if (_lastWinner is not null) Round++;
        NewRound(now);
    }

    public void Finish() => Phase = DuelPhase.Done;

    /// <summary>Хтось із пари пішов: поєдинок стоїть, доки гра не вирішить, що далі.</summary>
    public void Stop() => Phase = DuelPhase.Done;

    /// <summary>Поля кадру, спільні для дуелі й турніру.</summary>
    public void Fill(Dictionary<string, object?> o, DateTimeOffset now)
    {
        o["phase"] = Wire(Phase);
        o["round"] = Round;
        o["wins"] = (int[])Wins.Clone();
        o["last"] = Last();
        o["nextIn"] = NextIn(now);
        if (MixSignals) o["sig"] = Signal;
        if (Phase == DuelPhase.Aim && _baitShown) o["decoy"] = new { w = _baitWord, n = _baitN };
    }

    object? Last()
    {
        if (!_hasLast) return null;
        var o = new Dictionary<string, object?>
        {
            ["winner"] = _lastWinner,
            ["reason"] = _lastReason,
            ["ms"] = (long?[])_lastMs.Clone(),
        };
        if (_lastBait is not null) o["bait"] = _lastBait;
        if (_lastNr[0] is not null || _lastNr[1] is not null) o["nr"] = (string?[])_lastNr.Clone();
        if (_lastHasPc) o["pc"] = (long?[])_lastPc.Clone();
        return o;
    }

    /// <summary>У «Цілься…» відліку нема навмисно: будь-яке число там і є момент «ВОГОНЬ!».</summary>
    int? NextIn(DateTimeOffset now)
    {
        var until = Phase switch
        {
            DuelPhase.Ready => _aimAt,
            DuelPhase.Fire or DuelPhase.Result => _deadline,
            _ => (DateTimeOffset?)null,
        };
        if (until is not { } at) return null;
        return (int)Math.Max(0, Math.Round((at - now).TotalMilliseconds));
    }

    public static string Wire(DuelPhase phase) => phase switch
    {
        DuelPhase.Ready => "ready",
        DuelPhase.Aim => "aim",
        DuelPhase.Fire => "fire",
        DuelPhase.Result => "result",
        _ => "done",
    };
}

/// <summary>
/// Пінг кожного стрільця (п. 70), заміряний сервером: кадр пішов → клієнт відлунив <c>pong</c> з номером кадру →
/// різниця і є повна дорога туди й назад (та сама, яку проходить «ВОГОНЬ!» і постріл у відповідь). Клієнт може лише
/// затримати відлуння, тож беремо медіану останніх замірів і стелю <see cref="CapMs"/>: хитрун виграє не більше за неї.
/// </summary>
public sealed class PingMeter(int seats)
{
    /// <summary>Більше за це не компенсуємо: мобільний інтернет — так, «я на супутнику» — ні.</summary>
    public const int CapMs = 80;
    /// <summary>Скільки замірів треба, щоб довіряти медіані.</summary>
    public const int MinSamples = 3;
    const int Keep = 9, Frames = 8;
    /// <summary>Заміри довші — вкладка спала чи мережа кашлянула; не беремо.</summary>
    const int MaxSampleMs = 1500;

    readonly long[] _fid = new long[Frames];
    readonly DateTimeOffset[] _sent = new DateTimeOffset[Frames];
    readonly int[][] _samples = [.. Enumerable.Range(0, seats).Select(_ => new int[Keep])];
    readonly int[] _count = new int[seats], _next = new int[seats];
    readonly long[] _lastFid = new long[seats];
    readonly int[] _sort = new int[Keep];

    public void Clear()
    {
        Array.Clear(_count);
        Array.Clear(_next);
        Array.Clear(_lastFid);
    }

    public void Sent(long fid, DateTimeOffset at)
    {
        var i = (int)(fid % Frames);
        _fid[i] = fid;
        _sent[i] = at;
    }

    public void Pong(int seat, long fid, DateTimeOffset now)
    {
        if (seat < 0 || seat >= _count.Length || fid <= _lastFid[seat]) return;
        var i = (int)(fid % Frames);
        if (_fid[i] != fid) return;
        var ms = (now - _sent[i]).TotalMilliseconds;
        if (ms < 0 || ms > MaxSampleMs) return;
        _lastFid[seat] = fid;
        _samples[seat][_next[seat]] = (int)Math.Round(ms);
        _next[seat] = (_next[seat] + 1) % Keep;
        if (_count[seat] < Keep) _count[seat]++;
    }

    /// <summary>Поправка місця: медіана замірів, не більше стелі; замало замірів — нуль.</summary>
    public int Comp(int seat)
    {
        if (seat < 0 || seat >= _count.Length) return 0;
        var n = _count[seat];
        if (n < MinSamples) return 0;
        Array.Copy(_samples[seat], _sort, n);
        Array.Sort(_sort, 0, n);
        var med = n % 2 == 1 ? _sort[n / 2] : (_sort[n / 2 - 1] + _sort[n / 2]) / 2;
        return Math.Min(CapMs, med);
    }
}
