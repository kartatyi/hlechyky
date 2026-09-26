namespace Hlechyky.Games.Impl;

/// <summary>
/// Ставка «<see cref="Q"/> × <see cref="F"/>» — «на всьому столі щонайменше Q кісточок рахуються як F».
/// <see cref="Auto"/> — поставив не гравець, а годинник (проспав перший хід раунду): клієнт малює ⏰.
/// </summary>
public readonly record struct DiceBid(int Seat, int Q, int F, bool Auto = false);

/// <summary>
/// Чим скінчилось розкриття: хто що сказав, скільки нарахували (з них <see cref="Jokers"/> — глечики, що
/// пішли за джокерів), руки всіх на мить підняття глеків, хто втратив чи повернув кісточку і хто почне далі.
/// <see cref="Next"/> змінюється, лише якщо той, хто мав починати, встав з-за столу посеред розкриття.
/// </summary>
public sealed class DiceOutcome(string kind, int caller, DiceBid bid, int count, int jokers, int[][] hands,
    int? loser, int? gainer, bool @out, int next)
{
    public string Kind { get; } = kind;
    public int Caller { get; } = caller;
    public DiceBid Bid { get; } = bid;
    public int Count { get; } = count;
    public int Jokers { get; } = jokers;
    /// <summary>Індекс — місце; порожній масив — порожнє місце чи вибулий. Знімок, а не живі руки.</summary>
    public int[][] Hands { get; } = hands;
    public int? Loser { get; } = loser;
    public int? Gainer { get; } = gainer;
    public bool Out { get; } = @out;
    public int Next { get; set; } = next;
    /// <summary>Слово Глека про розкриття — ставить <see cref="Dice"/>, бо фрази й ніки — справа кімнати.</summary>
    public string Say { get; set; } = "";
}

/// <summary>
/// Правила брехливих костей (Perudo) без жодного слова про кімнати, час і Глека: руки, ставки, порівняння,
/// розкриття, паліфіко, вибування. Кожна дія повертає текст відмови або null — так правила перевіряються в
/// тестах напряму, а <see cref="Dice"/> лише перекладає їх у мову каркаса.
/// <para>
/// Індекс — це номер місця (0..5), навіть коли місце порожнє. Руки живуть у перевикористовуваних масивах
/// на п'ять кісточок: <see cref="Count"/>[s] — скільки з них справжні, решта — сміття з минулих раундів.
/// Усе — цілі числа, жодних float і жодних алокацій у гарячому шляху.
/// </para>
/// </summary>
public sealed class DiceCore
{
    public const int MaxSeats = 6;
    /// <summary>Більше п'яти кісточок під глеком не буває: навіть «Точно!» повертає лише до стартових.</summary>
    public const int MaxDice = 5;
    /// <summary>Грань 1 — «глечик»: у звичайному раунді джокер, у паліфіко — просто найнижча грань.</summary>
    public const int Jug = 1;

    /// <summary>Гліфи граней для текстів відмов: «4 × ⚄».</summary>
    static readonly string[] Glyphs = ["⚀", "⚁", "⚂", "⚃", "⚄", "⚅"];

    public DiceCore()
    {
        Hands = new int[MaxSeats][];
        for (var s = 0; s < MaxSeats; s++) Hands[s] = new int[MaxDice];
    }

    /// <summary>Скільки кісточок дали кожному на старті (опція: 3 або 5). Це ж і стеля для «Точно!».</summary>
    public int Dice { get; private set; } = MaxDice;
    /// <summary>Кісточок у місця зараз (0 — порожнє місце, вибув або встав).</summary>
    public int[] Count { get; } = new int[MaxSeats];
    /// <summary>Грані за зростанням; справжні лише перші <see cref="Count"/>[s].</summary>
    public int[][] Hands { get; }
    /// <summary>Кому роздавали на старті партії.</summary>
    public bool[] Dealt { get; } = new bool[MaxSeats];
    /// <summary>Має хоч одну кісточку і не встав з-за столу.</summary>
    public bool[] Alive { get; } = new bool[MaxSeats];
    /// <summary>Встав з-за столу посеред партії.</summary>
    public bool[] Left { get; } = new bool[MaxSeats];
    /// <summary>Уже мав свій раунд паліфіко — другого не буде.</summary>
    public bool[] PalificoUsed { get; } = new bool[MaxSeats];
    /// <summary>Хоч раз за партію сидів на одній кісточці (для ачівки «З однієї кісточки»).</summary>
    public bool[] WasAtOne { get; } = new bool[MaxSeats];
    /// <summary>Хто натиснув «Далі» у розкритті.</summary>
    public bool[] Ready { get; } = new bool[MaxSeats];
    /// <summary>Порядок вибування: перший — той, хто вилетів найраніше.</summary>
    public List<int> Places { get; } = new(MaxSeats);

    public int Round { get; private set; }
    public int Turn { get; private set; }
    public int Starter { get; private set; }
    /// <summary>Цей раунд — паліфіко: глечики не джокери, а грань заморожена для тих, у кого більше однієї кісточки.</summary>
    public bool Palifico { get; private set; }
    public DiceBid? Bid { get; private set; }
    /// <summary>Усі ставки раунду по порядку. Строгий порядок ставок тримає її короткою (практично 5–15).</summary>
    public List<DiceBid> History { get; } = new(32);

    /// <summary>Скільки ще грає.</summary>
    public int AliveCount
    {
        get
        {
            var n = 0;
            for (var s = 0; s < MaxSeats; s++) if (Alive[s]) n++;
            return n;
        }
    }

    /// <summary>Кісточок на столі — у всіх живих разом.</summary>
    public int Total
    {
        get
        {
            var n = 0;
            for (var s = 0; s < MaxSeats; s++) if (Alive[s]) n += Count[s];
            return n;
        }
    }

    /// <summary>Скільки живих уже натиснули «Далі».</summary>
    public int ReadyCount
    {
        get
        {
            var n = 0;
            for (var s = 0; s < MaxSeats; s++) if (Alive[s] && Ready[s]) n++;
            return n;
        }
    }

    // ---------- партія й раунд ----------

    /// <summary>Нова партія: усім, хто сидить, по <paramref name="dice"/> кісточок, чистий стан. Кидки — у <see cref="NewRound"/>.</summary>
    public void Deal(IReadOnlyList<int> seats, int dice)
    {
        if (seats.Count < 2) throw new ArgumentException("треба щонайменше двоє", nameof(seats));
        Dice = Math.Clamp(dice, 1, MaxDice);
        Array.Clear(Count);
        Array.Clear(Dealt);
        Array.Clear(Alive);
        Array.Clear(Left);
        Array.Clear(PalificoUsed);
        Array.Clear(WasAtOne);
        Array.Clear(Ready);
        Places.Clear();
        History.Clear();
        Bid = null;
        Round = 0;
        Palifico = false;
        foreach (var s in seats)
        {
            Dealt[s] = Alive[s] = true;
            Count[s] = Dice;
            if (Dice == 1) WasAtOne[s] = true;
        }
    }

    /// <summary>
    /// Новий раунд від <paramref name="starter"/>. Кидки в незмінному порядку — місця за зростанням, у кожному
    /// по <c>Count</c> викликів <c>rng.Next(1, 7)</c>, — тож той самий сід дає ті самі руки. Рука сортується:
    /// порядок кидків — не інформація. <paramref name="reshake"/> — перетрушуємо той самий раунд (хтось встав):
    /// номер не росте, а паліфіко, якщо вже було в того самого стартера, лишається.
    /// </summary>
    public void NewRound(Random rng, int starter, bool palificoRule, bool reshake = false)
    {
        if (!reshake) Round++;
        for (var s = 0; s < MaxSeats; s++)
        {
            if (!Alive[s]) continue;
            var hand = Hands[s];
            var n = Count[s];
            for (var i = 0; i < n; i++) hand[i] = rng.Next(1, 7);
            Array.Sort(hand, 0, n);
        }
        var keep = reshake && Palifico && starter == Starter;
        Palifico = keep || (palificoRule && Count[starter] == 1 && !PalificoUsed[starter]);
        if (Palifico) PalificoUsed[starter] = true;
        Bid = null;
        History.Clear();
        Array.Clear(Ready);
        Starter = starter;
        Turn = starter;
    }

    /// <summary>Наступне живе місце за колом (5 → 0). Нікого — те саме місце.</summary>
    public int NextAlive(int seat)
    {
        for (var k = 1; k <= MaxSeats; k++)
        {
            var s = (seat + k) % MaxSeats;
            if (Alive[s]) return s;
        }
        return seat;
    }

    /// <summary>Перше живе місце (переможець, коли лишився один).</summary>
    public int FirstAlive()
    {
        for (var s = 0; s < MaxSeats; s++) if (Alive[s]) return s;
        return -1;
    }

    // ---------- ставки ----------

    /// <summary>
    /// Найменша законна кількість для грані <paramref name="f"/> після ставки <paramref name="prev"/>; 0 — цю
    /// грань зараз не можна взагалі (глечики на відкритті звичайного раунду, чужа грань у паліфіко).
    /// Стелю «не більше, ніж кісточок на столі» перевіряє <see cref="Higher"/> окремо. Ту саму таблицю
    /// дублює клієнт (<c>web/games/dice.js</c>, <c>minQ</c>). Обидва звіряються зі знімком <c>DiceRuleTable.json</c>:
    /// C# — тест <c>The_rule_table_the_client_duplicates_matches_the_server</c>, JS — тест
    /// <c>The_client_minQ_in_dice_js_gives_the_same_table</c> (читає функцію з модуля й виконує її).
    /// </summary>
    public static int MinQ(DiceBid? prev, int f, bool palifico, int bidderDice)
    {
        if (f is < 1 or > 6) return 0;
        if (prev is not { } p) return !palifico && f == Jug ? 0 : 1;
        if (palifico)
        {
            // Грань заморожена для всіх, у кого більше однієї кісточки; одна — міняй як хочеш, глечик — найнижча.
            if (bidderDice > 1) return f == p.F ? p.Q + 1 : 0;
            return f > p.F ? p.Q : p.Q + 1;
        }
        if (p.F != Jug) return f == Jug ? (p.Q + 1) / 2 : f > p.F ? p.Q : p.Q + 1;
        return f == Jug ? p.Q + 1 : 2 * p.Q + 1;
    }

    /// <summary>Чи вища ставка <paramref name="q"/> × <paramref name="f"/> за <paramref name="prev"/>: null — так, інакше чому ні.</summary>
    public static string? Higher(DiceBid? prev, int q, int f, bool palifico, int bidderDice, int total)
    {
        if (f is < 1 or > 6) return "Грань — від 1 до 6";
        if (q < 1) return "Не зрозумів ставки";
        if (q > total) return "Стільки кісточок на столі нема";
        var min = MinQ(prev, f, palifico, bidderDice);
        if (prev is not { } p) return min == 0 ? "Раунд не починають з глечиків" : null;
        if (palifico && bidderDice > 1 && f != p.F) return "Паліфіко: грань не міняють — лише більше кісточок";
        return min == 0 || q < min ? Hint(p, palifico, bidderDice, total) : null;
    }

    /// <summary>
    /// Відмова «не вище» одним рядком, із тим, що саме приймуть: «Треба вище за 4 × ⚄: 5 × будь-що, 4 × ⚅
    /// або 2 × глечики». Варіанти, що не влазять у стіл, не пропонуємо; не влазить нічого — кажемо про «Брешеш!».
    /// </summary>
    public static string Hint(DiceBid prev, bool palifico, int bidderDice, int total)
    {
        var what = BidText(prev.Q, prev.F);
        if (palifico && bidderDice > 1)
            return prev.Q + 1 <= total
                ? $"Паліфіко: щонайменше {prev.Q + 1} × {Glyph(prev.F)}"
                : $"Вище за {what} уже нема куди — кажи «Брешеш!»";
        var options = new List<string>(3);
        if (palifico || prev.F != Jug)
        {
            if (prev.Q + 1 <= total) options.Add($"{prev.Q + 1} × будь-що");
            if (prev.F < 6) options.Add($"{prev.Q} × {Glyph(prev.F + 1)}" + (prev.F + 1 < 6 ? " чи вище" : ""));
            if (!palifico) options.Add($"{(prev.Q + 1) / 2} × глечики");
        }
        else
        {
            if (prev.Q + 1 <= total) options.Add($"{prev.Q + 1} × глечики");
            if (2 * prev.Q + 1 <= total) options.Add($"{2 * prev.Q + 1} × будь-що");
        }
        if (options.Count == 0) return $"Вище за {what} уже нема куди — кажи «Брешеш!»";
        var list = options.Count == 1 ? options[0] : string.Join(", ", options.Take(options.Count - 1)) + " або " + options[^1];
        return $"Треба вище за {what}: {list}";
    }

    public static string Glyph(int f) => f is >= 1 and <= 6 ? Glyphs[f - 1] : "?";

    /// <summary>«4 × ⚄», «3 × глечики».</summary>
    public static string BidText(int q, int f) => f == Jug ? $"{q} × глечики" : $"{q} × {Glyph(f)}";

    /// <summary>Ставка від <paramref name="seat"/>: перевіряє порядок і передає хід далі за колом.</summary>
    public string? PlaceBid(int seat, int q, int f, bool auto = false)
    {
        if (Higher(Bid, q, f, Palifico, Count[seat], Total) is { } why) return why;
        var bid = new DiceBid(seat, q, f, auto);
        Bid = bid;
        History.Add(bid);
        Turn = NextAlive(seat);
        return null;
    }

    /// <summary>Найнижча законна ставка на відкритті: 1 × ⚁ у звичайному раунді, 1 × глечик у паліфіко.</summary>
    public DiceBid Lowest(int seat) => new(seat, 1, Palifico ? Jug : 2, true);

    // ---------- розкриття ----------

    /// <summary>
    /// Скільки кісточок на столі рахуються як <paramref name="face"/>: у звичайному раунді для F ≠ 1 — ще й
    /// усі глечики (<paramref name="jokers"/> — скільки саме), у паліфіко і для F = 1 — лише сама грань.
    /// </summary>
    public int CountFace(int face, out int jokers)
    {
        var wild = !Palifico && face != Jug;
        int count = 0, j = 0;
        for (var s = 0; s < MaxSeats; s++)
        {
            if (!Alive[s]) continue;
            var hand = Hands[s];
            for (var i = 0; i < Count[s]; i++)
            {
                var v = hand[i];
                if (v == face) count++;
                else if (wild && v == Jug) { count++; j++; }
            }
        }
        jokers = j;
        return count;
    }

    /// <summary>
    /// Глеки догори. <paramref name="kind"/>: «liar» і «timeout» — не повірили поточній ставці (правдива —
    /// губить той, хто не повірив, брехлива — автор ставки); «exact» — «Точно!»: рівно — той, хто сказав,
    /// повертає кісточку (не вище стартових), інакше губить. Кісточка знімається одразу; хто вибув — у
    /// <see cref="Places"/>. Наступний раунд починає той, хто втратив, або той, хто сказав «Точно!»; вибув — наступний за ним.
    /// </summary>
    public DiceOutcome Resolve(string kind, int caller)
    {
        if (Bid is not { } bid) throw new InvalidOperationException("нема ставки — нема що розкривати");
        var count = CountFace(bid.F, out var jokers);
        var hands = new int[MaxSeats][];
        for (var s = 0; s < MaxSeats; s++) hands[s] = Alive[s] ? Hands[s][..Count[s]] : [];

        int? loser = null, gainer = null;
        int first;
        if (kind == "exact")
        {
            first = caller;
            if (count == bid.Q)
            {
                gainer = caller;
                Count[caller] = Math.Min(Dice, Count[caller] + 1);
            }
            else loser = caller;
        }
        else
        {
            loser = count >= bid.Q ? caller : bid.Seat;
            first = loser.Value;
        }

        var gone = false;
        if (loser is { } l)
        {
            Count[l]--;
            if (Count[l] == 1) WasAtOne[l] = true;
            if (Count[l] <= 0)
            {
                Count[l] = 0;
                Alive[l] = false;
                Places.Add(l);
                gone = true;
            }
        }
        Array.Clear(Ready);
        var next = Alive[first] ? first : NextAlive(first);
        return new DiceOutcome(kind, caller, bid, count, jokers, hands, loser, gainer, gone, next);
    }

    /// <summary>«Далі» у розкритті. true — позначку поставили вперше.</summary>
    public bool MarkReady(int seat)
    {
        if (Ready[seat]) return false;
        Ready[seat] = true;
        return true;
    }

    /// <summary>Встав з-за столу: вибуває цієї ж миті, кісточки — геть. Вибулий раніше — лише позначка «встав».</summary>
    public void Drop(int seat)
    {
        Left[seat] = true;
        if (!Alive[seat]) return;
        Alive[seat] = false;
        Count[seat] = 0;
        Ready[seat] = false;
        Places.Add(seat);
    }

    /// <summary>Мої кісточки за зростанням (новий масив — для виду).</summary>
    public int[] HandOf(int seat) => Alive[seat] && Count[seat] > 0 ? Hands[seat][..Count[seat]] : [];

    /// <summary>
    /// Тестовий помічник: покласти руки руками (як у дурня). <c>hands[s] == null</c> — місце не чіпаємо,
    /// порожній масив — вибулий. Кількість кісточок береться з довжини руки.
    /// </summary>
    public void Arrange(int[]?[] hands)
    {
        for (var s = 0; s < MaxSeats && s < hands.Length; s++)
        {
            if (hands[s] is not { } h) continue;
            if (h.Length > MaxDice) throw new ArgumentException("більше п'яти кісточок під глеком не буває", nameof(hands));
            Array.Copy(h, Hands[s], h.Length);
            Array.Sort(Hands[s], 0, h.Length);
            Count[s] = h.Length;
            Alive[s] = h.Length > 0;
            if (h.Length > 0) Dealt[s] = true;
            if (h.Length == 1) WasAtOne[s] = true;
        }
    }

    /// <summary>Тестовий помічник: хто ходить (щоб не крутити раунди заради потрібного стартера).</summary>
    public void SetTurn(int seat) => Turn = seat;

    /// <summary>Тестовий помічник: зробити цей раунд паліфіко (або звичайним) незалежно від кісточок.</summary>
    public void SetPalifico(bool on) => Palifico = on;
}
