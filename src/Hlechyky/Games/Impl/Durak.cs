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
/// Що зараз має статись: <c>Attack</c> — атакуючий заходить або підкидає, <c>Defend</c> — на столі є
/// небита карта, <c>Taking</c> — захисник уже сказав «Беру», але підкинути ще можна, <c>Done</c> — усе.
/// </summary>
public enum DurakPhase { Attack, Defend, Taking, Done }

/// <summary>Чим скінчилось: <c>out</c> — хтось вийшов, <c>both</c> — вийшли разом, <c>left</c> — встав з-за столу.</summary>
public sealed record DurakOver(int? Winner, string Reason);

/// <summary>
/// Правила підкидного дурня на двох — без жодного слова про кімнати, ставки й чат. Кожна дія повертає
/// текст відмови або null: так одні й ті самі правила перевіряються в тестах напряму, а
/// <see cref="Durak"/> лише перекладає їх у мову каркаса.
/// </summary>
public sealed class DurakCore
{
    /// <summary>До скількох добирають із колоди.</summary>
    public const int HandSize = 6;
    /// <summary>Більше шести карт на стіл не кладуть навіть тоді, коли в захисника їх повна рука.</summary>
    public const int MaxAttacks = 6;

    public List<int>[] Hands { get; } = [[], []];
    /// <summary>Колода: беруть з початку, а остання карта — козирна, тому вона й іде в добір найпізніше.</summary>
    public List<int> Deck { get; } = [];
    public List<DurakPair> Table { get; } = [];
    public int Trump { get; set; }
    /// <summary>Скільки карт пішло у відбій — у грі вони більше не з'являться, але лічильник цікавий гравцям.</summary>
    public int Discard { get; set; }
    public int Attacker { get; set; }
    public int Defender => Attacker == 0 ? 1 : 0;
    public DurakPhase Phase { get; set; } = DurakPhase.Attack;
    /// <summary>
    /// Скільки карт було в захисника на початку відбою — стільки атак і можна викласти. Рахуємо саме на
    /// початку, а не «зараз»: інакше кожна побита карта звужувала б стіл, і відбій ніколи не дійшов би до шести.
    /// </summary>
    public int Limit { get; set; }
    public DurakOver? Over { get; private set; }

    /// <summary>Козирна карта, доки її не забрали з колоди.</summary>
    public int? TrumpCard => Deck.Count > 0 ? Deck[^1] : null;

    /// <summary>Кому зараз ходити: у фазі відбою — захиснику, в решті — атакуючому.</summary>
    public int Turn => Phase == DurakPhase.Defend ? Defender : Attacker;

    /// <summary>Чи можна підкинути саме зараз — це й показуємо атакуючому кнопкою.</summary>
    public bool CanAdd => Over is null && Phase is DurakPhase.Attack or DurakPhase.Taking && HasMore();

    // ---------- роздача ----------

    /// <summary>Нова партія: тасуємо, роздаємо по шість, козир — остання карта колоди.</summary>
    public void Deal(Random rng)
    {
        var cards = new int[DurakCards.Count];
        for (var i = 0; i < cards.Length; i++) cards[i] = i;
        for (var i = cards.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (cards[i], cards[j]) = (cards[j], cards[i]);
        }

        Hands[0].Clear();
        Hands[1].Clear();
        Table.Clear();
        Deck.Clear();
        Deck.AddRange(cards);
        Discard = 0;
        Over = null;
        Phase = DurakPhase.Attack;
        Trump = DurakCards.Suit(Deck[^1]);
        for (var i = 0; i < HandSize; i++) { Pull(0); Pull(1); }
        Attacker = FirstAttacker();
        Limit = Hands[Defender].Count;
    }

    /// <summary>Ходить той, у кого молодший козир; нема козирів ні в кого — ходить перший.</summary>
    public int FirstAttacker()
    {
        var best = int.MaxValue;
        var who = 0;
        for (var seat = 0; seat < 2; seat++)
            foreach (var card in Hands[seat])
                if (DurakCards.Suit(card) == Trump && DurakCards.Rank(card) < best) { best = DurakCards.Rank(card); who = seat; }
        return who;
    }

    /// <summary>
    /// Розклад руками — для тестів правил: із випадкової роздачі не побудуєш ситуацію «козир проти козиря»
    /// чи «шоста карта на стіл». Проду не потрібен.
    /// </summary>
    public void Arrange(string trump, string[] hand0, string[] hand1, string[]? deck = null, int attacker = 0)
    {
        Trump = DurakCards.SuitOf(trump) ?? throw new ArgumentException($"невідома масть «{trump}»", nameof(trump));
        Fill(Hands[0], hand0);
        Fill(Hands[1], hand1);
        Fill(Deck, deck ?? []);
        Table.Clear();
        Discard = 0;
        Over = null;
        Phase = DurakPhase.Attack;
        Attacker = attacker;
        Limit = Hands[Defender].Count;
    }

    static void Fill(List<int> to, string[] cards)
    {
        to.Clear();
        foreach (var text in cards)
            to.Add(DurakCards.Parse(text) ?? throw new ArgumentException($"невідома карта «{text}»", nameof(cards)));
    }

    // ---------- ходи ----------

    /// <summary>Зайти картою або підкинути. Повертає текст відмови або null.</summary>
    public string? Attack(int seat, int card)
    {
        if (Over is not null) return "Партію зіграно";
        if (seat != Attacker) return "Зараз ходить суперник";
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
        // Підкидання поверх «Беру» відбою не відкриває: щойно підкидати нічим — стіл їде до захисника.
        if (Phase == DurakPhase.Taking) { if (!HasMore()) EndBout(taken: true); }
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
        // Усе побито: далі або підкидають, або відбій. Коли підкидати нічим — не змушуємо тиснути «Біто».
        if (Table.All(p => p.Defend is not null))
        {
            if (HasMore()) Phase = DurakPhase.Attack;
            else EndBout(taken: false);
        }
        return null;
    }

    /// <summary>«Беру». Повертає текст відмови або null.</summary>
    public string? Take(int seat)
    {
        if (Over is not null) return "Партію зіграно";
        if (seat != Defender) return "Бере той, хто відбивається";
        if (Phase != DurakPhase.Defend) return "Зараз нема чого брати";

        Phase = DurakPhase.Taking;
        if (!HasMore()) EndBout(taken: true);
        return null;
    }

    /// <summary>«Біто» (усе побито) або «Досить» (після «Беру»). Повертає текст відмови або null.</summary>
    public string? Done(int seat)
    {
        if (Over is not null) return "Партію зіграно";
        if (seat != Attacker) return "«Біто» каже той, хто ходить";
        if (Phase == DurakPhase.Taking) { EndBout(taken: true); return null; }
        if (Phase != DurakPhase.Attack) return "Спершу дай суперникові відбитись";
        if (Table.Count == 0) return "Спершу зайди картою";
        EndBout(taken: false);
        return null;
    }

    /// <summary>Хтось встав з-за столу: партія обірвана, дурнем лишається той, хто пішов.</summary>
    public void Quit(int? winner)
    {
        if (Over is not null) return;
        Phase = DurakPhase.Done;
        Over = new DurakOver(winner, "left");
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

    /// <summary>Чи є в атакуючого що покласти на стіл просто зараз — без огляду на фазу.</summary>
    bool HasMore()
    {
        if (Hands[Attacker].Count == 0) return false;
        if (Table.Count == 0) return true;
        if (Table.Count >= MaxAttacks || Table.Count >= Limit) return false;
        var ranks = TableRanks();
        return Hands[Attacker].Any(c => ranks.Contains(DurakCards.Rank(c)));
    }

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

    /// <summary>Кінець відбою: стіл або у відбій, або до захисника; далі добір і, можливо, кінець партії.</summary>
    void EndBout(bool taken)
    {
        if (taken)
        {
            foreach (var pair in Table)
            {
                Hands[Defender].Add(pair.Attack);
                if (pair.Defend is { } card) Hands[Defender].Add(card);
            }
            Table.Clear();
            // Захисник щойно набрав повні руки — добирає лише той, хто підкидав, і ходить він же знову.
            Refill(Attacker);
        }
        else
        {
            Discard += Table.Sum(p => p.Defend is null ? 1 : 2);
            Table.Clear();
            Refill(Attacker);      // атакуючий добирає першим — так на столі й роздають
            Refill(Defender);
            Attacker = Defender;   // відбився — тепер заходить він
        }
        Phase = DurakPhase.Attack;
        Limit = Hands[Defender].Count;
        Settle();
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

    /// <summary>Колода порожня і в когось порожня рука — партії кінець. Обидва порожні — нічия.</summary>
    void Settle()
    {
        if (Deck.Count > 0) return;
        var first = Hands[0].Count == 0;
        var second = Hands[1].Count == 0;
        if (!first && !second) return;
        Phase = DurakPhase.Done;
        Over = first && second ? new DurakOver(null, "both") : new DurakOver(first ? 0 : 1, "out");
    }
}

/// <summary>
/// Підкидний дурень на двох. Строго на двох: так ліміти підкидання лишаються одним числом, а не таблицею
/// «хто кому підкидає». Гра прихована (<c>Hidden</c>) — рука кожного їде тільки йому.
/// </summary>
public sealed class Durak : Game
{
    public override GameInfo Info { get; } = new(
        "durak", "Дурень", "дурня", GameGroup.Board, 2, 2, Hidden: true, Rated: true,
        Hint: "Підкидний дурень на двох: 36 карт, козир, відбивайся або бери");

    DurakCore _core = new();
    /// <summary>Чи вже сказали каркасові про кінець: Finish буває лише раз на партію.</summary>
    bool _announced;
    /// <summary>
    /// Нік дурня, запам'ятаний у мить виходу: каркас звільняє місце одразу після OnLeave, тож потім
    /// імені того, хто пішов, уже нізвідки взяти, а рядок статусу називає саме дурня.
    /// </summary>
    string? _foolNick;

    public override string SeatName(int seat) => seat == 0 ? "перший" : "другий";

    public override void Start()
    {
        _core = new DurakCore();
        _core.Deal(Ctx.Rng);
        _announced = false;
        _foolNick = null;
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        string? error;
        switch (action)
        {
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
        return ActResult.Done;
    }

    /// <summary>Хтось встав посеред партії: дурень — той, хто пішов, і в результаті це видно причиною «left».</summary>
    public override void OnLeave(int seat)
    {
        var other = seat == 0 ? 1 : 0;
        var winner = Ctx.Seated(other) ? other : (int?)null;
        _core.Quit(winner);
        _announced = true;
        _foolNick = Ctx.NickOf(seat);
        Ctx.Finish(winner is { } w ? [w] : [],
            $"{Info.Title}: {Ctx.NickOf(seat)} встав з-за столу — дурнем лишився він");
    }

    public override object View(int? seat)
    {
        var over = _core.Over;
        int? turn = over is null ? _core.Turn : null;
        string[]? hand = seat is { } s && s is 0 or 1 ? [.. _core.Sorted(s).Select(DurakCards.Text)] : null;
        object? result = over is null ? null : new { winner = over.Winner, reason = over.Reason, foolNick = _foolNick };
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
            counts = new[] { _core.Hands[0].Count, _core.Hands[1].Count },
            discard = _core.Discard,
            canAdd = _core.CanAdd,
            result,
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
        if (over.Winner is not { } won)
        {
            Ctx.Finish([], $"{Info.Title}: {Ctx.NickOf(0)} і {Ctx.NickOf(1)} вийшли разом — нічия");
            return;
        }
        // Ніки чужі, відмінювати їх нема як, тому рахунок і слово «дурень» окремим реченням.
        var lost = won == 0 ? 1 : 0;
        Ctx.Finish([won], $"{Info.Title}: {Ctx.NickOf(won)} 1:0 {Ctx.NickOf(lost)}, дурень — {Ctx.NickOf(lost)}");
    }

    static int? Named(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? DurakCards.Parse(p.GetString())
            : null;

    /// <summary>Заходити можна й голим рядком — з консолі так простіше, а шкоди нуль.</summary>
    static int? Bare(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.String ? DurakCards.Parse(payload.GetString()) : null;
}
