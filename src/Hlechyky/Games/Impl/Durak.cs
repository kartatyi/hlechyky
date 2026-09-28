using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Карта — це число 0..35: <c>номінал * 4 + масть</c>. Так колода тасується як звичайний масив чисел,
/// порівняння старшинства — це порівняння номіналів, а рядок «7♥» потрібен лише на дроті й в очах гравця.
/// Ім'я з префіксом гри навмисне: усі ігри лежать в одному <c>Hlechyky.Games.Impl</c>, і просте
/// <c>Cards</c> забрало б у наступної карткової гри найочевидніше ім'я, а кодування 36 карт тут суто дурневе.
/// </summary>
public static class DurakCards
{
    public const int Count = 36;

    /// <summary>Від молодшої до найстаршої: індекс — це і є старшинство.</summary>
    static readonly string[] RankNames = ["6", "7", "8", "9", "10", "J", "Q", "K", "A"];
    static readonly string[] SuitNames = ["♠", "♥", "♦", "♣"];

    public static int Rank(int card) => card / 4;
    public static int Suit(int card) => card % 4;

    public static string SuitText(int suit) => SuitNames[suit];

    public static string Text(int card) => RankNames[Rank(card)] + SuitNames[Suit(card)];

    /// <summary>Масть за символом; null — це не масть.</summary>
    public static int? SuitOf(string? text)
    {
        var i = string.IsNullOrEmpty(text) ? -1 : Array.IndexOf(SuitNames, text.Trim());
        return i < 0 ? null : i;
    }

    /// <summary>
    /// «10♦» → карта. Номінал приймаємо в будь-якому регістрі: клієнт шле рівно те, що ми йому дали,
    /// але з консолі люди пишуть як завгодно, а падати через це нема за чим.
    /// </summary>
    public static int? Parse(string? text)
    {
        var s = text?.Trim();
        if (s is null || s.Length < 2) return null;
        var suit = Array.IndexOf(SuitNames, s[^1..]);
        if (suit < 0) return null;
        var rank = Array.FindIndex(RankNames, r => string.Equals(r, s[..^1], StringComparison.OrdinalIgnoreCase));
        return rank < 0 ? null : rank * 4 + suit;
    }
}

/// <summary>Пара на столі: карта атаки і те, чим її побили (null — ще жива).</summary>
public readonly record struct DurakPair(int Attack, int? Defend);

/// <summary>
/// Що зараз має статись: <c>Attack</c> — атакуючий заходить або всі, крім захисника, підкидають,
/// <c>Defend</c> — на столі є небита карта, <c>Taking</c> — захисник уже сказав «Беру», але підкинути ще
/// можна, <c>Done</c> — усе.
/// </summary>
public enum DurakPhase { Attack, Defend, Taking, Done }

/// <summary>
/// Чим скінчилось: <c>out</c> — лишився один дурень, <c>both</c> — останні вийшли разом і дурня нема,
/// <c>left</c> — дурень встає з-за столу. <see cref="Winner"/> — хто вийшов першим (на двох це й є
/// переможець), <see cref="Fool"/> — хто лишився з картами.
/// </summary>
public sealed record DurakOver(int? Winner, string Reason)
{
    public int? Fool { get; init; }
}

/// <summary>
/// Правила підкидного дурня на 2–6 — без жодного слова про кімнати, ставки й чат. Кожна дія повертає
/// текст відмови або null: так одні й ті самі правила перевіряються в тестах напряму, а
/// <see cref="Durak"/> лише перекладає їх у мову каркаса.
///
/// Індекс руки — це номер місця за столом (0..5), навіть коли хтось із місць порожній: так ані вид,
/// ані тести не мусять перекладати «гравця» в «місце» і назад.
///
/// Компанія грає класичний підкидний: заходить атакуючий, відбивається наступний за ним, а підкидають
/// усі, крім захисника. Щоб не було гонитви, підкидання відкривається тоді ж, коли й на двох: коли все
/// на столі побито або захисник сказав «Беру». Кожен, кому є що підкинути, або підкидає, або каже «Пас»;
/// відбій закривається, щойно не лишилось нікого, хто може й не відмовився.
/// </summary>
public sealed class DurakCore
{
    /// <summary>До скількох добирають із колоди.</summary>
    public const int HandSize = 6;
    /// <summary>Більше шести карт на стіл не кладуть навіть тоді, коли в захисника їх повна рука.</summary>
    public const int MaxAttacks = 6;
    /// <summary>Шість по шість — уся колода: більше гравців 36 карт не витримають.</summary>
    public const int MaxSeats = 6;

    public DurakCore(int seats = 2)
    {
        if (seats is < 2 or > MaxSeats) throw new ArgumentOutOfRangeException(nameof(seats));
        Hands = [.. Enumerable.Range(0, seats).Select(_ => new List<int>())];
        In = new bool[seats];
        Dealt = new bool[seats];
        Passed = new bool[seats];
    }

    public List<int>[] Hands { get; }
    /// <summary>Скільки місць за столом узагалі (включно з порожніми).</summary>
    public int Seats => Hands.Length;
    /// <summary>Кому роздавали на початку партії.</summary>
    public bool[] Dealt { get; }
    /// <summary>Хто ще грає: має карти (або ще добере) і не встає з-за столу.</summary>
    public bool[] In { get; }
    /// <summary>У якому порядку виходили з гри: перший — найкраще місце.</summary>
    public List<int> Places { get; } = [];
    /// <summary>Хто в цьому вікні підкидання сказав «Пас»/«Біто». Будь-яке підкидання скидає всі позначки.</summary>
    public bool[] Passed { get; }

    /// <summary>Колода: беруть з початку, а остання карта — козирна, тому вона й іде в добір найпізніше.</summary>
    public List<int> Deck { get; } = [];
    public List<DurakPair> Table { get; } = [];
    public int Trump { get; set; }
    /// <summary>Скільки карт пішло у відбій — у грі вони більше не з'являться, але лічильник цікавий гравцям.</summary>
    public int Discard { get; set; }
    /// <summary>Хто зайшов у цьому відбої (головний атакуючий).</summary>
    public int Attacker { get; set; }
    /// <summary>Хто відбивається. Тримаємо окремо, а не рахуємо від атакуючого: той може встати посеред відбою.</summary>
    public int Defender { get; set; } = 1;
    public DurakPhase Phase { get; set; } = DurakPhase.Attack;
    /// <summary>
    /// Скільки карт було в захисника на початку відбою — стільки атак і можна викласти. Рахуємо саме на
    /// початку, а не «зараз»: інакше кожна побита карта звужувала б стіл, і відбій ніколи не дійшов би до шести.
    /// </summary>
    public int Limit { get; set; }
    public DurakOver? Over { get; private set; }

    /// <summary>Козирна карта, доки її не забрали з колоди.</summary>
    public int? TrumpCard => Deck.Count > 0 ? Deck[^1] : null;

    /// <summary>Скільки ще гравців із картами.</summary>
    public int Playing => In.Count(x => x);

    /// <summary>
    /// Кого чекає стіл — для глядача й для рядка «Ходить X»: у відбої — захисника, на порожньому столі —
    /// атакуючого, у вікні підкидання — першого за колом, хто ще може підкинути й не сказав «Пас».
    /// </summary>
    public int Turn => Phase switch
    {
        DurakPhase.Defend => Defender,
        DurakPhase.Attack when Table.Count == 0 => Attacker,
        DurakPhase.Attack or DurakPhase.Taking => Throwers().Where(Blocking).Select(s => (int?)s).FirstOrDefault() ?? Attacker,
        _ => Attacker,
    };

    /// <summary>Чий хід для цього місця: якщо воно саме може щось зробити — воно, інакше спільне <see cref="Turn"/>.</summary>
    public int TurnFor(int seat) => MayAct(seat) ? seat : Turn;

    /// <summary>Чи може атакуючий підкинути просто зараз (так було на двох; для кожного місця — <see cref="CanAddFor"/>).</summary>
    public bool CanAdd => CanAddFor(Attacker);

    /// <summary>Чи може це місце покласти карту на стіл просто зараз.</summary>
    public bool CanAddFor(int seat)
    {
        if (Over is not null || seat < 0 || seat >= Seats || !In[seat]) return false;
        if (Phase == DurakPhase.Attack && Table.Count == 0) return seat == Attacker && Hands[seat].Count > 0;
        return Phase is DurakPhase.Attack or DurakPhase.Taking && Blocking(seat);
    }

    /// <summary>Чи чекає стіл саме на це місце (захист, перша карта або підкидання).</summary>
    public bool MayAct(int seat)
    {
        if (Over is not null || seat < 0 || seat >= Seats || !In[seat]) return false;
        return Phase == DurakPhase.Defend ? seat == Defender : CanAddFor(seat);
    }

    // ---------- роздача ----------

    /// <summary>
    /// Нова партія: тасуємо, роздаємо по шість по колу, козир — остання карта колоди. <paramref name="players"/> —
    /// хто сидить; без нього — усі місця. На шістьох колода розходиться вся, і козирна карта опиняється в
    /// когось на руці — тоді видно лише масть.
    /// </summary>
    public void Deal(Random rng, IReadOnlyList<int>? players = null)
    {
        var seats = players ?? [.. Enumerable.Range(0, Seats)];
        if (seats.Count < 2) throw new ArgumentException("треба щонайменше двоє", nameof(players));

        var cards = new int[DurakCards.Count];
        for (var i = 0; i < cards.Length; i++) cards[i] = i;
        for (var i = cards.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (cards[i], cards[j]) = (cards[j], cards[i]);
        }

        Reset();
        foreach (var seat in seats) Dealt[seat] = In[seat] = true;
        Deck.AddRange(cards);
        Trump = DurakCards.Suit(Deck[^1]);
        for (var i = 0; i < HandSize; i++)
            foreach (var seat in seats)
                if (Deck.Count > 0) Pull(seat);
        Attacker = FirstAttacker();
        Defender = NextIn(Attacker);
        StartBout();
    }

    void Reset()
    {
        foreach (var hand in Hands) hand.Clear();
        Array.Clear(In);
        Array.Clear(Dealt);
        Array.Clear(Passed);
        Places.Clear();
        Table.Clear();
        Deck.Clear();
        Discard = 0;
        Over = null;
        Phase = DurakPhase.Attack;
    }

    /// <summary>Ходить той, у кого молодший козир; нема козирів ні в кого — перший за столом.</summary>
    public int FirstAttacker()
    {
        var best = int.MaxValue;
        var who = Array.IndexOf(In, true);
        for (var seat = 0; seat < Seats; seat++)
        {
            if (!In[seat]) continue;
            foreach (var card in Hands[seat])
                if (DurakCards.Suit(card) == Trump && DurakCards.Rank(card) < best) { best = DurakCards.Rank(card); who = seat; }
        }
        return Math.Max(0, who);
    }

    /// <summary>
    /// Розклад руками — для тестів правил: із випадкової роздачі не побудуєш ситуацію «козир проти козиря»
    /// чи «шоста карта на стіл». Проду не потрібен.
    /// </summary>
    public void Arrange(string trump, string[] hand0, string[] hand1, string[]? deck = null, int attacker = 0) =>
        Arrange(trump, [hand0, hand1], deck, attacker);

    /// <summary>Те саме на кількох: <paramref name="hands"/>[i] — рука місця i, і всі вони в грі.</summary>
    public void Arrange(string trump, string[][] hands, string[]? deck = null, int attacker = 0)
    {
        if (hands.Length > Seats) throw new ArgumentException("рук більше, ніж місць", nameof(hands));
        Reset();
        Trump = DurakCards.SuitOf(trump) ?? throw new ArgumentException($"невідома масть «{trump}»", nameof(trump));
        for (var seat = 0; seat < hands.Length; seat++)
        {
            Fill(Hands[seat], hands[seat]);
            Dealt[seat] = In[seat] = true;
        }
        Fill(Deck, deck ?? []);
        Attacker = attacker;
        Defender = NextIn(attacker);
        StartBout();
    }

    static void Fill(List<int> to, string[] cards)
    {
        to.Clear();
        foreach (var text in cards)
            to.Add(DurakCards.Parse(text) ?? throw new ArgumentException($"невідома карта «{text}»", nameof(cards)));
    }

    // ---------- ходи ----------

    /// <summary>Зайти картою (атакуючий) або підкинути (будь-хто, крім захисника). Повертає текст відмови або null.</summary>
    public string? Attack(int seat, int card)
    {
        if (Over is not null) return "Партію зіграно";
        if (!Valid(seat) || !In[seat]) return "Ти вже поза грою — дивись, хто лишиться дурнем";
        if (seat == Defender) return Table.Count == 0 ? "Зараз ходить суперник" : "Ти відбиваєшся — підкидають інші";
        if (Table.Count == 0 && seat != Attacker) return "Зараз ходить суперник";
        if (Mates(seat, Defender)) return "Своєму напарникові не підкидають";
        if (Phase is not (DurakPhase.Attack or DurakPhase.Taking)) return "Спершу дай суперникові відбитись";
        if (!Hands[seat].Contains(card)) return "Такої карти в тебе нема";
        if (Table.Count > 0)
        {
            if (Table.Count >= MaxAttacks) return "На стіл більше не влізе";
            if (Table.Count >= Limit) return "У суперника стільки карт нема";
            if (!TableRanks().Contains(DurakCards.Rank(card))) return "Підкидати можна лише те, що вже на столі";
        }

        Hands[seat].Remove(card);
        Table.Add(new DurakPair(card, null));
        // Нова карта — нові номінали на столі: хто вже казав «Пас», хай подивиться ще раз.
        Array.Clear(Passed);
        // Підкидання поверх «Беру» відбою не відкриває: щойно підкидати нікому — стіл їде до захисника.
        if (Phase == DurakPhase.Taking) { if (!AnyBlocking()) EndBout(taken: true); }
        else Phase = DurakPhase.Defend;
        return null;
    }

    /// <summary>Побити карту зі столу. Повертає текст відмови або null.</summary>
    public string? Defend(int seat, int attack, int card)
    {
        if (Over is not null) return "Партію зіграно";
        if (seat != Defender) return "Відбивається суперник";
        if (Phase != DurakPhase.Defend) return "Зараз нема чого бити";
        if (!Hands[seat].Contains(card)) return "Такої карти в тебе нема";
        var i = Table.FindIndex(p => p.Attack == attack);
        if (i < 0) return "Такої карти на столі нема";
        if (Table[i].Defend is not null) return "Цю карту вже побито";
        if (!Beats(card, attack)) return "Такою не поб'єш";

        Hands[seat].Remove(card);
        Table[i] = Table[i] with { Defend = card };
        // Усе побито: далі або підкидають, або відбій. Коли підкидати нікому — не змушуємо тиснути «Біто».
        if (Table.All(p => p.Defend is not null))
        {
            Phase = DurakPhase.Attack;
            if (!AnyBlocking()) EndBout(taken: false);
        }
        return null;
    }

    /// <summary>Переводний дурень (№210): захисник може перевести карти далі, поки ще нічого не побив.</summary>
    public bool AllowTransfer { get; set; }

    /// <summary>
    /// Пари 2×2 (№232): номер пари кожного місця (0/1, -1 — не грає) або null — кожен сам за себе.
    /// Напарник того, хто відбивається, йому не підкидає; пара, в якої вийшли обоє, — у безпеці.
    /// </summary>
    public int[]? Team { get; set; }

    /// <summary>Чи ці двоє — одна пара (сам собі не напарник).</summary>
    public bool Mates(int a, int b) => Team is { } t && a != b && Valid(a) && Valid(b) && t[a] >= 0 && t[a] == t[b];

    /// <summary>
    /// Чи може захисник перевести: ще нічого не побито, у нього є карта того самого номіналу, а в наступного за ним —
    /// досить карт, щоб відбиватись від усього столу (і не більше шести).
    /// </summary>
    public bool CanTransfer(int seat)
    {
        if (!AllowTransfer || Over is not null || seat != Defender || Phase != DurakPhase.Defend || Table.Count == 0) return false;
        if (Table.Any(p => p.Defend is not null)) return false;
        var next = Foe(seat);
        if (next == seat || Table.Count + 1 > Math.Min(MaxAttacks, Hands[next].Count)) return false;
        var rank = DurakCards.Rank(Table[0].Attack);
        return Hands[seat].Any(c => DurakCards.Rank(c) == rank);
    }

    /// <summary>Перевести: кладе карту того самого номіналу, і відбивається вже наступний. Текст відмови або null.</summary>
    public string? Transfer(int seat, int card)
    {
        if (!AllowTransfer) return "Тут підкидний — переводити не можна";
        if (Over is not null) return "Партію зіграно";
        if (seat != Defender) return "Переводить той, хто відбивається";
        if (Phase != DurakPhase.Defend || Table.Count == 0) return "Зараз нема чого переводити";
        if (Table.Any(p => p.Defend is not null)) return "Уже почав відбиватись — переводити пізно";
        if (!Hands[seat].Contains(card)) return "Такої карти в тебе нема";
        if (DurakCards.Rank(card) != DurakCards.Rank(Table[0].Attack)) return "Переводять картою того самого номіналу";
        var next = Foe(seat);
        if (next == seat || Table.Count + 1 > Math.Min(MaxAttacks, Hands[next].Count)) return "У наступного стільки карт нема — не переведеш";

        Hands[seat].Remove(card);
        Table.Add(new DurakPair(card, null));
        // Той, хто перевів, тепер головний атакуючий: від нього і підкидання, і добір.
        Attacker = seat;
        Defender = next;
        Limit = Hands[next].Count;
        Array.Clear(Passed);
        return null;
    }

    /// <summary>Номінали на столі — для Глека: що можна підкинути.</summary>
    public IReadOnlySet<int> Ranks() => TableRanks();

    /// <summary>«Беру». Повертає текст відмови або null.</summary>
    public string? Take(int seat)
    {
        if (Over is not null) return "Партію зіграно";
        if (seat != Defender) return "Бере той, хто відбивається";
        if (Phase != DurakPhase.Defend) return "Зараз нема чого брати";

        Phase = DurakPhase.Taking;
        if (!AnyBlocking()) EndBout(taken: true);
        return null;
    }

    /// <summary>
    /// «Біто»/«Пас» (усе побито, більше не підкидаю) або «Досить» (після «Беру»). Відбій закривається, коли
    /// так сказали всі, кому ще є що підкинути. Повертає текст відмови або null.
    /// </summary>
    public string? Done(int seat)
    {
        if (Over is not null) return "Партію зіграно";
        if (!Valid(seat) || !In[seat]) return "Ти вже поза грою — дивись, хто лишиться дурнем";
        if (seat == Defender) return "«Біто» каже той, хто ходить";
        if (Phase == DurakPhase.Defend) return "Спершу дай суперникові відбитись";
        if (Table.Count == 0) return seat == Attacker ? "Спершу зайди картою" : "Зараз ходить суперник";

        Passed[seat] = true;
        if (!AnyBlocking()) EndBout(taken: Phase == DurakPhase.Taking);
        return null;
    }

    /// <summary>Гра на двох обірвана: хтось встав, і дурнем лишається він. <paramref name="winner"/> — хто лишився.</summary>
    public void Quit(int? winner, int? foolSeat = null)
    {
        if (Over is not null) return;
        Phase = DurakPhase.Done;
        var fool = foolSeat ?? (winner is { } w ? Enumerable.Range(0, Seats).Where(s => s != w && In[s]).Select(s => (int?)s).FirstOrDefault() : null);
        Over = new DurakOver(winner, "left") { Fool = fool };
    }

    /// <summary>
    /// Компанія грає далі без того, хто встав: його карти — у відбій, а якщо відбивався саме він — відбій
    /// скасовано, стіл теж у відбій, і заходить наступний за ним. Кличеться, лише коли після виходу за
    /// столом лишається щонайменше двоє з картами — інакше це <see cref="Quit"/>.
    /// </summary>
    public void Leave(int seat)
    {
        if (Over is not null || !Valid(seat) || !In[seat]) return;

        Discard += Hands[seat].Count;
        Hands[seat].Clear();
        In[seat] = false;
        Passed[seat] = false;

        if (seat == Defender)
        {
            Discard += Table.Sum(p => p.Defend is null ? 1 : 2);
            Table.Clear();
            foreach (var s in Throwers()) Refill(s);
            NextBout(NextIn(seat));
            return;
        }
        if (Phase == DurakPhase.Attack && Table.Count == 0)
        {
            // Атакуючий пішов, не зайшовши: заходить той, хто сидить перед захисником.
            if (seat == Attacker) Attacker = PrevIn(Defender);
            return;
        }
        // Пішов той, хто підкидав: можливо, тепер підкидати вже нікому.
        if (Phase is DurakPhase.Attack or DurakPhase.Taking && !AnyBlocking()) EndBout(taken: Phase == DurakPhase.Taking);
    }

    // ---------- правила ----------

    /// <summary>Козир б'є будь-що не козирне; решта — тільки старшою тієї ж масті (козир козирем — теж «та сама масть»).</summary>
    public bool Beats(int card, int against) =>
        DurakCards.Suit(card) == DurakCards.Suit(against)
            ? DurakCards.Rank(card) > DurakCards.Rank(against)
            : DurakCards.Suit(card) == Trump;

    /// <summary>Рука в тому порядку, в якому її показують: масті купками, козир останній, у масті — від молодшої.</summary>
    public IEnumerable<int> Sorted(int seat) => Hands[seat]
        .OrderBy(c => DurakCards.Suit(c) == Trump ? 1 : 0)
        .ThenBy(DurakCards.Suit)
        .ThenBy(DurakCards.Rank);

    /// <summary>Хто може підкидати в цьому відбої — по колу, починаючи з атакуючого. Захисника тут нема.</summary>
    public IEnumerable<int> Throwers()
    {
        for (var i = 0; i < Seats; i++)
        {
            var seat = (Attacker + i) % Seats;
            if (In[seat] && seat != Defender) yield return seat;
        }
    }

    bool Valid(int seat) => seat >= 0 && seat < Seats;

    /// <summary>Чи є в цього місця що покласти на вже початий стіл — без огляду на фазу й на «Пас».</summary>
    bool CanThrow(int seat)
    {
        if (!In[seat] || seat == Defender || Hands[seat].Count == 0) return false;
        // Своєму напарникові не підкидають — у парах це було б грою проти себе.
        if (Mates(seat, Defender)) return false;
        if (Table.Count == 0) return false;
        if (Table.Count >= MaxAttacks || Table.Count >= Limit) return false;
        var ranks = TableRanks();
        return Hands[seat].Any(c => ranks.Contains(DurakCards.Rank(c)));
    }

    /// <summary>Може підкинути й ще не відмовився — відбій чекає саме на нього.</summary>
    bool Blocking(int seat) => !Passed[seat] && CanThrow(seat);

    bool AnyBlocking() => Throwers().Any(Blocking);

    /// <summary>Номінали, які вже лежать на столі, — і серед атак, і серед захистів.</summary>
    HashSet<int> TableRanks()
    {
        var ranks = new HashSet<int>();
        foreach (var pair in Table)
        {
            ranks.Add(DurakCards.Rank(pair.Attack));
            if (pair.Defend is { } card) ranks.Add(DurakCards.Rank(card));
        }
        return ranks;
    }

    /// <summary>Наступний за колом, хто ще грає; нікого — він сам.</summary>
    int NextIn(int seat)
    {
        for (var i = 1; i <= Seats; i++)
        {
            var s = (seat + i) % Seats;
            if (In[s]) return s;
        }
        return seat;
    }

    /// <summary>
    /// На кого йти від цього місця: наступний за колом, хто ще грає, — а в парах наступний суперник (напарник,
    /// що лишився без пари суперників поруч, пропускається: своїх не атакують і на своїх не переводять).
    /// </summary>
    int Foe(int seat)
    {
        for (var i = 1; i <= Seats; i++)
        {
            var s = (seat + i) % Seats;
            if (In[s] && !Mates(seat, s)) return s;
        }
        return NextIn(seat);
    }

    int PrevIn(int seat)
    {
        for (var i = 1; i <= Seats; i++)
        {
            var s = ((seat - i) % Seats + Seats) % Seats;
            if (In[s]) return s;
        }
        return seat;
    }

    /// <summary>Кінець відбою: стіл або у відбій, або до захисника; далі добір і, можливо, кінець партії.</summary>
    void EndBout(bool taken)
    {
        // Добирають по колу від атакуючого, захисник — останнім (а якщо взяв — не добирає зовсім).
        var order = Throwers().ToList();
        if (taken)
        {
            foreach (var pair in Table)
            {
                Hands[Defender].Add(pair.Attack);
                if (pair.Defend is { } card) Hands[Defender].Add(card);
            }
            Table.Clear();
            foreach (var s in order) Refill(s);
            // Узяв — пропускає свій хід: заходить наступний за ним (на двох це той самий атакуючий).
            NextBout(NextIn(Defender));
        }
        else
        {
            Discard += Table.Sum(p => p.Defend is null ? 1 : 2);
            Table.Clear();
            foreach (var s in order) Refill(s);
            Refill(Defender);
            // Відбився — тепер заходить він.
            NextBout(Defender);
        }
    }

    /// <summary>Хто вийшов — виходить; хто лишився — починає новий відбій від <paramref name="next"/>.</summary>
    void NextBout(int next)
    {
        Settle();
        if (Over is not null) return;
        if (!In[next]) next = NextIn(next);
        Attacker = next;
        Defender = Foe(next);
        StartBout();
    }

    void StartBout()
    {
        Phase = DurakPhase.Attack;
        Array.Clear(Passed);
        Limit = Hands[Defender].Count;
    }

    void Refill(int seat)
    {
        while (Hands[seat].Count < HandSize && Deck.Count > 0) Pull(seat);
    }

    void Pull(int seat)
    {
        Hands[seat].Add(Deck[0]);
        Deck.RemoveAt(0);
    }

    /// <summary>
    /// Колода порожня — хто без карт, той вийшов (по колу від атакуючого, в тому порядку й місця).
    /// Лишився з картами один — він дурень; не лишилось нікого — останні вийшли разом, дурня нема.
    /// </summary>
    void Settle()
    {
        if (Deck.Count > 0) return;
        for (var i = 0; i < Seats; i++)
        {
            var seat = (Attacker + i) % Seats;
            if (In[seat] && Hands[seat].Count == 0) { In[seat] = false; Places.Add(seat); }
        }
        var left = Enumerable.Range(0, Seats).Where(s => In[s]).ToArray();
        // У парах гра скінчена, щойно з картами лишилась одна пара: дурень — той із двох, у кого карт більше.
        if (Team is { } team && left.Length == 2 && Mates(left[0], left[1]))
        {
            Phase = DurakPhase.Done;
            var fool = Hands[left[1]].Count > Hands[left[0]].Count ? left[1] : left[0];
            Over = new DurakOver(Places.Count > 0 ? Places[0] : null, "out") { Fool = fool };
            return;
        }
        if (left.Length >= 2) return;
        Phase = DurakPhase.Done;
        Over = left.Length == 1
            ? new DurakOver(Places.Count > 0 ? Places[0] : null, "out") { Fool = left[0] }
            : new DurakOver(null, "both");
    }
}

/// <summary>
/// Підкидний дурень на 2–6. Гра прихована (<c>Hidden</c>) — рука кожного їде тільки йому. Стіл
/// ставить господар і тисне «Почати», коли зібрались усі.
/// </summary>
public sealed class Durak : Game
{
    public override GameInfo Info { get; } = new(
        "durak", "Дурень", "дурня", GameGroup.Board, 1, DurakCore.MaxSeats, Start: StartMode.ByHost, Hidden: true,
        Options:
        [
            new GameOption("mode", "Дурень", [("podkydnoy", "Підкидний"), ("perevodnoy", "🔁 Переводний: відбиваєшся — можеш перевести тим самим номіналом далі")], "podkydnoy"),
            new GameOption("teams", "Склад", [("0", "кожен сам за себе"), ("1", "👥 пари 2×2: рівно вчотирьох, напарник — через одного й тобі не підкидає, дурень — уся пара")], "0"),
            BoardBots.Option(5),
        ],
        Hint: "Дурень на 2–6: 36 карт, козир, відбивайся або бери. Підкидний або переводний, можна й парами 2×2; бракує людей — підсяде Глек 🤖. "
            + "Дурень носить 🎖 погони до наступної партії");

    static readonly string[] Names = ["перший", "другий", "третій", "четвертий", "п'ятий", "шостий"];

    bool _transfer;
    /// <summary>Пари 2×2 (№232): лише вчотирьох; напарники — через одного в порядку місць.</summary>
    bool _teams;
    int _botsWanted;
    string?[] _bots = new string?[DurakCore.MaxSeats];
    DateTimeOffset? _botAt;
    /// <summary>Погони (№211): нік (чи ім'я Глека) дурня минулої партії за цим столом — носить 🎖 до кінця наступної.</summary>
    string? _pogony;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _transfer = options.TryGetValue("mode", out var m) && m == "perevodnoy";
        _botsWanted = BoardBots.Read(options, 5);
        _teams = options.TryGetValue("teams", out var t) && t == "1";
    }

    public override string? CanStart()
    {
        var humans = BoardBots.Humans(Ctx, DurakCore.MaxSeats);
        if (_teams)
        {
            var all = humans + Math.Min(_botsWanted, Math.Max(0, 4 - humans));
            return all == 4 ? null : all < 4
                ? $"Парами грають рівно вчотирьох, а за столом {all}: хай хтось підсяде — або відкрий стіл з «🤖 Глек підсідає»"
                : $"Парами грають рівно вчотирьох, а за столом {all}: хтось зайвий — або грайте кожен сам за себе";
        }
        return humans + Math.Min(_botsWanted, DurakCore.MaxSeats - humans) >= 2 ? null
            : "Самому нема з ким: хай хтось сяде — або відкрий стіл з «🤖 Глек підсідає»";
    }

    bool IsBot(int seat) => seat >= 0 && seat < _bots.Length && _bots[seat] is not null;
    public override string? SeatBot(int seat) => IsBot(seat) ? _bots[seat] : null;

    DurakCore _core = new(DurakCore.MaxSeats);
    /// <summary>Чи вже сказали каркасові про кінець: Finish буває лише раз на партію.</summary>
    bool _announced;
    /// <summary>
    /// Нік дурня, запам'ятаний у мить виходу: каркас звільняє місце одразу після OnLeave, тож потім
    /// імені того, хто пішов, уже нізвідки взяти, а рядок статусу називає саме дурня.
    /// </summary>
    string? _foolNick;
    /// <summary>Ніки всіх, хто сів грати, — щоб і після чийогось виходу було кого назвати в підсумку.</summary>
    string?[] _nicks = new string?[DurakCore.MaxSeats];

    public override string SeatName(int seat) => seat >= 0 && seat < Names.Length ? Names[seat] : $"гравець {seat + 1}";

    public override void Start()
    {
        _core = new DurakCore(DurakCore.MaxSeats) { AllowTransfer = _transfer };
        _bots = BoardBots.Seat(Ctx, DurakCore.MaxSeats,
            _teams ? Math.Min(_botsWanted, Math.Max(0, 4 - BoardBots.Humans(Ctx, DurakCore.MaxSeats))) : _botsWanted);
        var seats = Enumerable.Range(0, DurakCore.MaxSeats).Where(s => Ctx.Seated(s) || _bots[s] is not null).ToArray();
        if (_teams && seats.Length == 4)
        {
            // Пари — через одного за колом: перший і третій проти другого й четвертого.
            var team = Enumerable.Repeat(-1, DurakCore.MaxSeats).ToArray();
            for (var i = 0; i < seats.Length; i++) team[seats[i]] = i % 2;
            _core.Team = team;
        }
        _nicks = [.. Enumerable.Range(0, DurakCore.MaxSeats).Select(s => _bots[s] ?? Ctx.NickOf(s))];
        _core.Deal(Ctx.Rng, seats);
        _announced = false;
        _foolNick = null;
        _botAt = null;
        Arm();
    }

    /// <summary>Стіл чекає на когось із Глеків — хай «подумає» (після кожної зміни на столі — наново).</summary>
    void Arm()
    {
        _botAt = null;
        if (_core.Over is not null) return;
        for (var s = 0; s < _core.Seats; s++)
            if (IsBot(s) && _core.MayAct(s)) { _botAt = Ctx.Clock.UtcNow.AddMilliseconds(BoardBots.ThinkMs); return; }
    }

    /// <summary>Хід Глека (його штовхає клієнт людини): один крок першого Глека, на якого чекає стіл.</summary>
    ActResult BotTurn()
    {
        if (_core.Over is not null || _botAt is not { } at || Ctx.Clock.UtcNow < at) return ActResult.Fail("Глек ще думає");
        var turn = _core.Turn;
        for (var i = 0; i < _core.Seats; i++)
        {
            var s = (turn + i) % _core.Seats;
            if (!IsBot(s) || !_core.MayAct(s)) continue;
            DurakBot.Move(_core, s, Ctx.Rng);
            Announce();
            Arm();
            return ActResult.Done;
        }
        _botAt = null;
        return ActResult.Fail("Глекам нема що робити");
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == BoardBots.Nudge) return BotTurn();
        if (IsBot(seat)) return ActResult.Fail("Тут грає Глек");
        string? error;
        switch (action)
        {
            case "transfer":
                if ((Named(payload, "card") ?? Bare(payload)) is not { } given) return ActResult.Fail("Не зрозумів, яка карта");
                error = _core.Transfer(seat, given);
                break;
            case "attack":
                if ((Named(payload, "card") ?? Bare(payload)) is not { } card) return ActResult.Fail("Не зрозумів, яка карта");
                error = _core.Attack(seat, card);
                break;
            case "defend":
                if (Named(payload, "attack") is not { } target || Named(payload, "card") is not { } weapon)
                    return ActResult.Fail("Не зрозумів, чим і що бити");
                error = _core.Defend(seat, target, weapon);
                break;
            case "take":
                error = _core.Take(seat);
                break;
            case "done":
                error = _core.Done(seat);
                break;
            default:
                return ActResult.Fail("Тут так не ходять");
        }
        if (error is not null) return ActResult.Fail(error);
        Announce();
        Arm();
        return ActResult.Done;
    }

    /// <summary>
    /// Хтось встав посеред партії. Той, хто вже вийшов із картами «в нуль», нікому не заважає — граємо далі.
    /// Лишилось двоє з картами — дурнем стає той, хто пішов. Більше — компанія грає далі без нього.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (_core.Over is not null || seat < 0 || seat >= _core.Seats || !_core.In[seat]) return;
        var nick = Ctx.NickOf(seat) ?? _nicks[seat] ?? SeatName(seat);

        // Людей із картами не лишилось — Глеки самі з собою не догравають.
        if (!Enumerable.Range(0, _core.Seats).Any(s => s != seat && _core.In[s] && !IsBot(s)))
        {
            _core.Quit(null);
            _announced = true;
            _botAt = null;
            Ctx.Finish([], $"{Info.Title}: {nick} встає з-за столу, а з Глеками догравати нікому");
            return;
        }
        // Пари: хто встав — підвів напарника, дурнями лишається їхня пара.
        if (_core.Team is { } team)
        {
            _core.Quit(null, seat);
            _announced = true;
            _foolNick = nick;
            _pogony = nick;
            _botAt = null;
            var mate = Enumerable.Range(0, _core.Seats).First(s => _core.Mates(s, seat));
            var rest = Enumerable.Range(0, _core.Seats).Where(s => _core.Dealt[s] && team[s] != team[seat] && Ctx.Seated(s)).ToArray();
            Ctx.Finish(rest, $"{Info.Title}: {nick} встає з-за столу — пара {nick} і {_nicks[mate]} лишається дурнями");
            return;
        }
        if (_core.Playing > 2)
        {
            _core.Leave(seat);
            Ctx.Log($"{Info.Title}: {nick} встає з-за столу, карти йдуть у відбій — грають далі");
            Announce();
            Arm();
            return;
        }

        var other = Enumerable.Range(0, _core.Seats).Where(s => s != seat && _core.In[s]).Select(s => (int?)s).FirstOrDefault();
        var winner = other is { } o && Ctx.Seated(o) ? o : (int?)null;
        _core.Quit(winner);
        _announced = true;
        _foolNick = nick;
        _pogony = nick;
        _botAt = null;
        var winners = Enumerable.Range(0, _core.Seats).Where(s => s != seat && _core.Dealt[s] && Ctx.Seated(s)).ToArray();
        Ctx.Finish(winners, $"{Info.Title}: {nick} встає з-за столу — і лишається дурнем");
    }

    public override object View(int? seat)
    {
        var over = _core.Over;
        var me = seat is { } s && s >= 0 && s < _core.Seats && _core.Dealt[s] ? s : -1;
        int? turn = over is null ? (me >= 0 ? _core.TurnFor(me) : _core.Turn) : null;
        string[]? hand = me >= 0 ? [.. _core.Sorted(me).Select(DurakCards.Text)] : null;
        object? result = over is null ? null : new
        {
            winner = over.Winner,
            fool = over.Fool,
            reason = over.Reason,
            foolNick = _foolNick,
            places = _core.Places.ToArray(),
        };
        return new
        {
            turn,
            attacker = _core.Attacker,
            defender = _core.Defender,
            phase = Phase(_core.Phase),
            trump = DurakCards.SuitText(_core.Trump),
            trumpCard = _core.TrumpCard is { } card ? DurakCards.Text(card) : null,
            deck = _core.Deck.Count,
            table = _core.Table
                .Select(p => new { attack = DurakCards.Text(p.Attack), defend = p.Defend is { } d ? DurakCards.Text(d) : null })
                .ToArray(),
            hand,
            // Індекс — номер місця (на всі шість): скільки карт у кого, хто грає, хто вже вийшов і хто сказав «Пас».
            counts = _core.Hands.Select(h => h.Count).ToArray(),
            dealt = (bool[])_core.Dealt.Clone(),
            @in = (bool[])_core.In.Clone(),
            passed = (bool[])_core.Passed.Clone(),
            places = _core.Places.ToArray(),
            // Ніки тих, кому роздавали: місце того, хто встав, каркас звільняє, а картка має назвати його й далі.
            names = (string?[])_nicks.Clone(),
            discard = _core.Discard,
            // Скільки ще карт можна покласти в цьому відбої — ліміт «шість і не більше, ніж у захисника».
            room = Math.Max(0, Math.Min(DurakCore.MaxAttacks, _core.Limit) - _core.Table.Count),
            canAdd = me >= 0 ? _core.CanAddFor(me) : _core.CanAdd,
            result,
            // Прохід №3: переводний, Глеки на місцях і коли Глек «подумав», погони дурня минулої партії.
            transfer = _core.AllowTransfer,
            canTransfer = me >= 0 && _core.CanTransfer(me),
            bots = _bots.Any(b => b is not null) ? (string?[])_bots.Clone() : null,
            botIn = _botAt is { } at && over is null ? Math.Max(0, (int)(at - Ctx.Clock.UtcNow).TotalMilliseconds) : (int?)null,
            pogony = _pogony,
            // Пари 2×2: номер пари кожного місця (-1 — не грає) або null.
            teams = _core.Team is { } tm ? (int[])tm.Clone() : null,
        };
    }

    static string Phase(DurakPhase phase) => phase switch
    {
        DurakPhase.Attack => "attack",
        DurakPhase.Defend => "defend",
        DurakPhase.Taking => "taking",
        _ => "done",
    };

    /// <summary>Партію оголошуємо один раз: далі каркас сам відбиває ходи й пропонує «Ще раз».</summary>
    void Announce()
    {
        if (_announced || _core.Over is not { } over) return;
        _announced = true;
        _botAt = null;
        if (over.Fool is { } f) _pogony = _nicks[f] ?? Ctx.NickOf(f);
        var dealt = Enumerable.Range(0, _core.Seats).Where(s => _core.Dealt[s]).ToArray();
        var two = dealt.Length == 2;
        if (over.Fool is not { } fool)
        {
            Ctx.Finish([], two
                ? $"{Info.Title}: {_nicks[dealt[0]]} і {_nicks[dealt[1]]} вийшли разом — нічия"
                : $"{Info.Title}: останні вийшли разом — дурня цього разу нема, нічия");
            return;
        }
        if (_core.Team is { } team)
        {
            // Пари: дурень — уся пара того, хто лишився з картами; погони — йому самому.
            var mate = dealt.First(s => _core.Mates(s, fool));
            var champs = dealt.Where(s => team[s] != team[fool]).ToArray();
            Ctx.Finish([.. champs.Where(Ctx.Seated)],
                $"{Info.Title}: дурні — пара {_nicks[fool]} 🎖 і {_nicks[mate]}, бере пара {_nicks[champs[0]]} і {_nicks[champs[1]]}");
            return;
        }
        var winners = dealt.Where(s => s != fool && Ctx.Seated(s)).ToArray();
        // Ніки чужі, відмінювати їх нема як, тому рахунок і слово «дурень» окремим реченням.
        if (two)
        {
            var won = dealt[0] == fool ? dealt[1] : dealt[0];
            Ctx.Finish(winners, $"{Info.Title}: {_nicks[won]} 1:0 {_nicks[fool]}, дурень — {_nicks[fool]} 🎖");
            return;
        }
        var first = _core.Places.Count > 0 ? _nicks[_core.Places[0]] : null;
        Ctx.Finish(winners, $"{Info.Title}: дурень — {_nicks[fool]} 🎖" + (first is null ? "" : $", перше місце — {first}")
            + $" (грали {Company(dealt.Length)})");
    }

    /// <summary>«утрьох», «вчотирьох»… — скільки сіло грати, по-людськи.</summary>
    static string Company(int n) => n switch
    {
        3 => "утрьох",
        4 => "вчотирьох",
        5 => "вп'ятьох",
        6 => "вшістьох",
        _ => $"у {n}",
    };

    static int? Named(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? DurakCards.Parse(p.GetString())
            : null;

    /// <summary>Заходити можна й голим рядком — з консолі так простіше, а шкоди нуль.</summary>
    static int? Bare(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.String ? DurakCards.Parse(payload.GetString()) : null;
}
