using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Кістка доміно: дві половинки з крапками. На руці й у базарі вона лежить у канонічному вигляді
/// (A ≥ B), а в ланцюгу — вже орієнтованою: A дивиться ліворуч, B — праворуч. Тому клієнтові не треба
/// нічого перевертати самому: як прийшло, так і малюється.
/// </summary>
public readonly record struct DominoBone(int A, int B)
{
    public bool Double => A == B;

    public int Pips => A + B;

    public bool Has(int pip) => A == pip || B == pip;

    /// <summary>Друга половинка, коли одну приклали до кінця <paramref name="pip"/>.</summary>
    public int Other(int pip) => A == pip ? B : A;

    /// <summary>Та сама кістка, хай як її повернули: 6-2 і 2-6 — одне й те саме.</summary>
    public bool Same(DominoBone other) => (A == other.A && B == other.B) || (A == other.B && B == other.A);

    /// <summary>
    /// Старшинство для першого ходу: будь-який дубль старший за будь-яку звичайну кістку, далі — сума
    /// очок, а при рівній сумі — більша половинка (6-0 старша за 5-1).
    /// </summary>
    public int Rank => Double ? 1000 + A : Pips * 10 + Math.Max(A, B);

    /// <summary>Пара чисел для дроту: [a, b].</summary>
    public int[] Wire => [A, B];
}

/// <summary>
/// Доміно на двох-чотирьох. Партія — це кілька роздач («раундів») в одній кімнаті: хто перший набрав
/// сто очок після раунду, той і виграв. «Ще раз» каркаса починає партію з нуля, а внутрішній
/// <c>round</c> живе всередині неї.
///
/// Гра Hidden: рука видається тільки своєму місцю, а базар не показуємо взагалі — лише скільки в ньому
/// лишилось. Тому й <see cref="View"/> для кожного місця свій.
/// </summary>
public sealed class Domino : Game
{
    /// <summary>Класична межа партії — сто очок. Стіл може обрати коротшу (опція «target»).</summary>
    public const int Target = 100;

    /// <summary>
    /// До скількох грати: «1» — один раунд (хто виграв роздачу, той і партію), 50 — коротка партія на
    /// чверть години, 100 — класика. Типово 50: сотня на двох — це одинадцять раундів, і до кінця доживали не всі.
    /// </summary>
    static readonly (string Value, string Label)[] Targets = [("1", "Один раунд"), ("50", "До 50 очок"), ("100", "До 100 очок")];

    const int MaxSeats = 4;

    public override GameInfo Info { get; } = new(
        "domino", "Доміно", "доміно", GameGroup.Board, 1, MaxSeats,
        Start: StartMode.ByHost, Hidden: true,
        Options:
        [
            new GameOption("mode", "Гра", [("classic", "Класика: кожен сам за себе"), ("kozel", "🐐 «Козел» 2×2: пари через одного, до 101 — хто набрав, той козел")], "classic"),
            new GameOption("target", "Партія (класика)", Targets, "50"),
            BoardBots.Option(3),
        ],
        Hint: "Доміно на 2–4: прикладай кістки однаковими половинками. Класика — до 50 чи 100 очок; «Козел» — дворове парами до 101. "
            + "Бракує людей — підсяде Глек 🤖");

    /// <summary>«Козел»: рахунок штрафний, пара, що першою набрала стільки, — козли.</summary>
    public const int KozelTarget = 101;

    /// <summary>Скільки очок закриває партію; 1 — партія з одного раунду.</summary>
    int _target = 50;
    /// <summary>Дворовий «Козел» парами (№208).</summary>
    bool _kozel;
    int _botsWanted;
    /// <summary>Імена Глеків на місцях (null — людина або пусто).</summary>
    string?[] _bots = new string?[MaxSeats];
    DateTimeOffset? _botAt;
    int _botSeat = -1;
    /// <summary>Козли минулої партії «Козла» — ніки (або імена Глеків), для 🐐 біля ніка.</summary>
    string[] _goats = [];

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _target = options.TryGetValue("target", out var t) && int.TryParse(t, out var n) && Targets.Any(x => x.Value == t) ? n : 50;
        _kozel = options.TryGetValue("mode", out var m) && m == "kozel";
        _botsWanted = BoardBots.Read(options, 3);
        if (_kozel) _target = KozelTarget;
    }

    /// <summary>Сам із собою не зіграєш; «Козел» — рівно вчотирьох (люди й Глеки разом).</summary>
    public override string? CanStart()
    {
        var humans = BoardBots.Humans(Ctx, MaxSeats);
        var total = humans + Math.Min(_botsWanted, MaxSeats - humans);
        if (_kozel && total < 4) return "«Козел» — це дві пари, рівно четверо: хай підсядуть люди або відкрий стіл з 🤖 Глеком";
        return total >= 2 ? null : "Самому нема з ким: хай хтось сяде — або відкрий стіл з «🤖 Глек підсідає»";
    }

    bool IsBot(int seat) => seat >= 0 && seat < MaxSeats && _bots[seat] is not null;
    public override string? SeatBot(int seat) => IsBot(seat) ? _bots[seat] : null;

    /// <summary>Ім'я за столом: нік або «Глек 🤖».</summary>
    string Nick(int seat) => IsBot(seat) ? _bots[seat]! : Ctx.NickOf(seat) ?? SeatName(seat);

    /// <summary>Пара в «Козлі»: 0 з 2, 1 з 3.</summary>
    static int Team(int seat) => seat % 2;

    /// <summary>Рука кожного місця. Масив завжди на всі місця — індекс тут це номер місця, а не гравця.</summary>
    readonly List<DominoBone>[] _hands = [.. Enumerable.Range(0, MaxSeats).Select(_ => new List<DominoBone>())];
    /// <summary>Ланцюг зліва направо, уже орієнтований.</summary>
    readonly List<DominoBone> _line = [];
    readonly List<DominoBone> _boneyard = [];
    /// <summary>Хто грає цей раунд: той, хто встає з-за столу, лишається з рахунком, але без кісток.</summary>
    readonly bool[] _in = new bool[MaxSeats];
    int[] _scores = new int[MaxSeats];
    int _turn;
    int _round;
    /// <summary>Переможець партії; поки null — граємо далі.</summary>
    int? _winner;
    /// <summary>Хто виграв минулий раунд — той і починає наступний.</summary>
    int? _lastWinner;
    RoundEnd? _last;
    /// <summary>Партію з одного раунду закрила риба порівну — переможця нема.</summary>
    bool _draw;

    /// <summary>
    /// Чим скінчився минулий раунд — для картки, а не для правил. <paramref name="Left"/> — що в кого
    /// лишилось на руках: після раунду кістки й так показують одне одному, а без них «+23» нічого не пояснює.
    /// </summary>
    sealed record RoundEnd(int? Winner, int Points, string Reason, int Round = 0, int[][][]? Left = null);

    public override string SeatName(int seat) => seat switch
    {
        0 => "перший",
        1 => "другий",
        2 => "третій",
        3 => "четвертий",
        _ => $"гравець {seat + 1}",
    };

    // ---------- партія і роздача ----------

    public override void Start()
    {
        _bots = BoardBots.Seat(Ctx, MaxSeats, _botsWanted);
        _botAt = null;
        _botSeat = -1;
        _scores = new int[MaxSeats];
        _round = 0;
        _winner = null;
        _lastWinner = null;
        _last = null;
        _draw = false;
        Deal();
        Arm();
    }

    /// <summary>Нова роздача всередині тієї самої партії: рахунок лишається, кістки — ні.</summary>
    void Deal()
    {
        _round++;
        _line.Clear();
        _boneyard.Clear();
        for (var seat = 0; seat < MaxSeats; seat++)
        {
            _hands[seat].Clear();
            _in[seat] = Ctx.Seated(seat) || _bots[seat] is not null;
        }

        var deck = Deck();
        Shuffle(deck);
        // Двоє беруть по сім, компанія — по п'ять: інакше на чотирьох базару майже не лишиться.
        // «Козел» — уся кістка на руки, по сім кожному, базару нема.
        var each = Alive == 2 || _kozel ? 7 : 5;
        foreach (var seat in Seats())
        {
            for (var i = 0; i < each && deck.Count > 0; i++)
            {
                _hands[seat].Add(deck[^1]);
                deck.RemoveAt(deck.Count - 1);
            }
            Sort(_hands[seat]);
        }
        _boneyard.AddRange(deck);
        _turn = FirstSeat();
    }

    /// <summary>Повний набір 0–6: 28 кісток, кожна пара половинок рівно раз.</summary>
    static List<DominoBone> Deck()
    {
        var deck = new List<DominoBone>(28);
        for (var a = 0; a <= 6; a++)
            for (var b = 0; b <= a; b++)
                deck.Add(new DominoBone(a, b));
        return deck;
    }

    /// <summary>Тасування Фішера-Єйтса на генераторі кімнати: та сама кімната з тим самим сідом роздасть те саме.</summary>
    void Shuffle(List<DominoBone> deck)
    {
        for (var i = deck.Count - 1; i > 0; i--)
        {
            var j = Ctx.Rng.Next(i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }
    }

    /// <summary>Рука сортована — старші попереду: так її легше читати очима й вона не стрибає після «Тягнути».</summary>
    static void Sort(List<DominoBone> hand) => hand.Sort((x, y) => y.Rank.CompareTo(x.Rank));

    /// <summary>
    /// Хто починає раунд: переможець попереднього, а на першій роздачі — власник найстаршого дубля,
    /// і лише коли дублів не роздали нікому — власник найстаршої кістки.
    /// </summary>
    int FirstSeat()
    {
        if (_lastWinner is { } won && _in[won]) return won;
        // «Козла» на першій роздачі (і після риби) відкриває той, у кого дубль 1-1 — так заведено у дворі.
        if (_kozel)
            foreach (var seat in Seats())
                if (_hands[seat].Any(b => b.A == 1 && b.B == 1)) return seat;
        var best = -1;
        var bestRank = -1;
        foreach (var seat in Seats())
            foreach (var bone in _hands[seat])
                if (bone.Rank > bestRank) (best, bestRank) = (seat, bone.Rank);
        return best >= 0 ? best : FirstAlive;
    }

    IEnumerable<int> Seats()
    {
        for (var seat = 0; seat < MaxSeats; seat++)
            if (_in[seat]) yield return seat;
    }

    int Alive => _in.Count(x => x);

    int FirstAlive => Array.IndexOf(_in, true);

    // ---------- ходи ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == BoardBots.Nudge) return BotTurn();
        if (action is not ("play" or "draw" or "pass")) return ActResult.Fail("Тут так не ходять");
        if (_winner is not null || _draw) return ActResult.Fail("Партію зіграно, тисни «Ану ще раз»");
        if (seat < 0 || seat >= MaxSeats || !_in[seat] || IsBot(seat)) return ActResult.Fail("Ти вже не в цій партії");
        if (seat != _turn) return ActResult.Fail("Не так швидко — зараз не твій хід");

        var result = action switch
        {
            "play" => Play(seat, payload),
            "draw" => Draw(seat),
            _ => Pass(seat),
        };
        if (result.Ok) Arm();
        return result;
    }

    /// <summary>Черга дійшла до Глека — хай «подумає» перед ходом.</summary>
    void Arm()
    {
        if (_winner is not null || _draw || !IsBot(_turn)) { _botAt = null; _botSeat = -1; return; }
        if (_botAt is null || _botSeat != _turn)
        {
            _botAt = Ctx.Clock.UtcNow.AddMilliseconds(BoardBots.ThinkMs);
            _botSeat = _turn;
        }
    }

    /// <summary>
    /// Хід Глека (його штовхає клієнт людини): тягне з базару, поки нема чим ходити, і кладе найважчу кістку —
    /// дублі й великі очки на руках лише шкодять. Нема чим і базар порожній — пас.
    /// </summary>
    ActResult BotTurn()
    {
        if (_winner is not null || _draw || !IsBot(_turn) || _botAt is not { } at || Ctx.Clock.UtcNow < at)
            return ActResult.Fail("Глек ще думає");
        var seat = _turn;
        _botAt = null;
        while (!CanPlay(seat) && _boneyard.Count > 0)
        {
            _hands[seat].Add(_boneyard[^1]);
            _boneyard.RemoveAt(_boneyard.Count - 1);
        }
        Sort(_hands[seat]);
        if (!CanPlay(seat))
        {
            NextTurn();
            CheckFish();
            Arm();
            return ActResult.Done;
        }
        DominoBone? best = null;
        string? end = null;
        foreach (var bone in _hands[seat])
        {
            var fits = _line.Count == 0 || bone.Has(_line[0].A) || bone.Has(_line[^1].B);
            if (!fits) continue;
            if (best is { } b && (bone.Double ? 1000 : 0) + bone.Pips <= (b.Double ? 1000 : 0) + b.Pips) continue;
            best = bone;
            end = _line.Count == 0 ? null : bone.Has(_line[^1].B) ? "right" : "left";
        }
        var i = _hands[seat].FindIndex(x => x.Same(best!.Value));
        Put(best!.Value, end);
        _hands[seat].RemoveAt(i);
        if (_hands[seat].Count == 0) EndRound(seat, Seats().Where(s => s != seat).Sum(Pips), "out");
        else
        {
            NextTurn();
            CheckFish();
        }
        Arm();
        return ActResult.Done;
    }

    ActResult Play(int seat, JsonElement payload)
    {
        if (ReadBone(payload) is not { } asked) return ActResult.Fail("Не зрозумів, яку кістку класти");
        if (!ReadEnd(payload, out var end)) return ActResult.Fail("Не зрозумів, з якого боку класти");
        var index = _hands[seat].FindIndex(b => b.Same(asked));
        if (index < 0) return ActResult.Fail("Такої кістки в тебе нема");

        var bone = _hands[seat][index];
        if (!Put(bone, end)) return ActResult.Fail("Ця кістка сюди не підходить");
        _hands[seat].RemoveAt(index);

        if (_hands[seat].Count == 0)
        {
            // Вийшов: забирає все, що лишилось на руках у решти.
            var points = Seats().Where(s => s != seat).Sum(Pips);
            EndRound(seat, points, "out");
            return ActResult.Accept("Є! Раунд твій!");
        }
        NextTurn();
        CheckFish();
        return ActResult.Done;
    }

    ActResult Draw(int seat)
    {
        if (CanPlay(seat)) return ActResult.Fail("Є чим ходити");
        if (_boneyard.Count == 0) return ActResult.Fail("Базар порожній, лишається пас");

        _hands[seat].Add(_boneyard[^1]);
        _boneyard.RemoveAt(_boneyard.Count - 1);
        Sort(_hands[seat]);
        // Тягнути можна скільки завгодно, аж поки не з'явиться чим ходити — черга при цьому не переходить.
        CheckFish();
        return ActResult.Done;
    }

    ActResult Pass(int seat)
    {
        if (CanPlay(seat)) return ActResult.Fail("Є чим ходити");
        if (_boneyard.Count > 0) return ActResult.Fail("У базарі ще є кістки — тягни");

        // У Журнал пас не пишемо: він буває по три рази поспіль, а Журнал — спільний на весь сайт.
        // Іншим досить того, що черга поїхала далі, а той, хто пасував, бачить тост.
        NextTurn();
        CheckFish();
        return ActResult.Accept("Пас: ходити нема чим");
    }

    /// <summary>
    /// Кладе кістку на потрібний кінець. <paramref name="end"/> порожній — підбираємо самі: клієнт
    /// питає гравця тільки тоді, коли кістка лягає з обох боків.
    /// </summary>
    bool Put(DominoBone bone, string? end)
    {
        if (_line.Count == 0)
        {
            _line.Add(bone);
            return true;
        }
        var (left, right) = (_line[0].A, _line[^1].B);
        var toLeft = end == "left";
        var toRight = end == "right";
        if (!toLeft && !toRight)
        {
            toRight = bone.Has(right);
            toLeft = !toRight && bone.Has(left);
        }
        if (toRight && bone.Has(right))
        {
            _line.Add(new DominoBone(right, bone.Other(right)));
            return true;
        }
        if (toLeft && bone.Has(left))
        {
            _line.Insert(0, new DominoBone(bone.Other(left), left));
            return true;
        }
        return false;
    }

    /// <summary>Кістка з payload: <c>{ tile: [a, b] }</c>. Порядок половинок байдужий.</summary>
    static DominoBone? ReadBone(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        if (!payload.TryGetProperty("tile", out var tile) || tile.ValueKind != JsonValueKind.Array) return null;
        if (tile.GetArrayLength() != 2) return null;
        var halves = new int[2];
        var i = 0;
        foreach (var half in tile.EnumerateArray())
        {
            if (half.ValueKind != JsonValueKind.Number || !half.TryGetInt32(out var n) || n is < 0 or > 6) return null;
            halves[i++] = n;
        }
        return new DominoBone(halves[0], halves[1]);
    }

    /// <summary>
    /// Бік із payload: <c>{ end: 'left'|'right' }</c>. Боку може й не бути — тоді сервер підбирає сам,
    /// а от казна-що замість боку тихо ковтати не можна: краще сказати гравцеві правду, ніж покласти
    /// кістку не туди, куди він просив.
    /// </summary>
    static bool ReadEnd(JsonElement payload, out string? end)
    {
        end = null;
        if (payload.ValueKind != JsonValueKind.Object) return true;
        if (!payload.TryGetProperty("end", out var raw) || raw.ValueKind == JsonValueKind.Null) return true;
        if (raw.ValueKind != JsonValueKind.String) return false;
        var side = raw.GetString();
        if (side is not ("left" or "right")) return false;
        end = side;
        return true;
    }

    void NextTurn()
    {
        if (Alive == 0) return;
        do { _turn = (_turn + 1) % MaxSeats; } while (!_in[_turn]);
    }

    bool CanPlay(int seat)
    {
        if (!_in[seat] || _hands[seat].Count == 0) return false;
        if (_line.Count == 0) return true;
        var (left, right) = (_line[0].A, _line[^1].B);
        return _hands[seat].Any(b => b.Has(left) || b.Has(right));
    }

    int Pips(int seat) => _hands[seat].Sum(b => b.Pips);

    /// <summary>
    /// «Риба»: базар порожній і ходити нема кому. Рахуємо одразу, не ганяючи всіх по колу через пас —
    /// пас лишається для випадку, коли ходити не може саме той, чия черга.
    /// </summary>
    void CheckFish()
    {
        if (_boneyard.Count > 0 || Seats().Any(CanPlay)) return;

        if (_kozel)
        {
            // «Козел»: риба — пара, в якої на руках більше, забирає очки всіх чотирьох рук. Порівну — нікому.
            var a = Seats().Where(s => Team(s) == 0).Sum(Pips);
            var b = Seats().Where(s => Team(s) == 1).Sum(Pips);
            EndRound(a == b ? null : a > b ? 1 : 0, a == b ? 0 : a + b, "fish");
            return;
        }
        var low = Seats().Min(Pips);
        var best = Seats().Where(s => Pips(s) == low).ToArray();
        if (best.Length != 1)
        {
            // Порівну — очки нікому: інакше нагороду за глухий кут довелося б ділити навпіл.
            EndRound(null, 0, "fish");
            return;
        }
        EndRound(best[0], Seats().Where(s => s != best[0]).Sum(Pips) - low, "fish");
    }

    // ---------- підсумки ----------

    void EndRound(int? winner, int points, string reason)
    {
        if (_kozel)
        {
            KozelRound(winner, points, reason);
            return;
        }
        _last = new RoundEnd(winner, points, reason, _round,
            [.. Enumerable.Range(0, MaxSeats).Select(s => _in[s] ? _hands[s].Select(b => b.Wire).ToArray() : [])]);
        // Переможець раунду відкриває наступний. Після риби з рівними руками переможця нема — тоді
        // _lastWinner теж стає порожнім, і знову діє правило найстаршого дубля.
        _lastWinner = winner;
        if (winner is { } seat) _scores[seat] += points;

        // У Журнал підсумки раундів не пишемо: Журнал спільний на весь сайт (радіо й Балачки), а
        // раундів у партії до ста очок буває під два десятки. Гравцям те саме каже картка через
        // lastRound, а в Журнал іде один рядок на всю партію — з Ctx.Finish нижче.
        if (winner is { } champion && (_scores[champion] >= _target || _target <= 1))
        {
            _winner = champion;
            Ctx.Finish([champion], Scoreline(champion));
            return;
        }
        // Партія з одного раунду, а раунд скінчився рибою порівну: переможця нема — нічия.
        if (_target <= 1)
        {
            _draw = true;
            Ctx.Finish([], $"{Info.Title}: риба порівну — нічия");
            return;
        }
        Deal();
    }

    /// <summary>
    /// Кінець роздачі «Козла». <paramref name="winner"/> — хто вийшов (його пара виграла роздачу), а при рибі — будь-хто
    /// з пари, що виграла рибу. Штраф пишеться парі, що програла: при виході — очки на руках обох її гравців, при рибі —
    /// <paramref name="points"/> (усі руки). Хто перша набрала 101 — та пара козли, друга перемагає.
    /// </summary>
    void KozelRound(int? winner, int points, string reason)
    {
        var loserTeam = winner is { } w ? 1 - Team(w) : -1;
        if (reason == "out" && loserTeam >= 0) points = Seats().Where(s => Team(s) == loserTeam).Sum(Pips);
        _last = new RoundEnd(winner, points, reason, _round,
            [.. Enumerable.Range(0, MaxSeats).Select(s => _in[s] ? _hands[s].Select(b => b.Wire).ToArray() : [])]);
        // Наступну роздачу відкриває той, хто вийшов; після риби — знову власник 1-1.
        _lastWinner = reason == "out" ? winner : null;
        if (loserTeam >= 0)
            // Рахунок пари тримаємо на обох місцях, щоб вид лишився той самий: scores[i] — очки пари місця i.
            for (var s = loserTeam; s < MaxSeats; s += 2) _scores[s] += points;
        if (loserTeam >= 0 && _scores[loserTeam] >= KozelTarget)
        {
            var won = 1 - loserTeam;
            _winner = won;
            _goats = [Nick(loserTeam), Nick(loserTeam + 2)];
            Ctx.Finish([won, won + 2],
                $"{Info.Title} «Козел»: {Nick(loserTeam)} і {Nick(loserTeam + 2)} — козли 🐐 ({_scores[loserTeam]} : {_scores[won]}, "
                + $"за {Rounds(_round)}); перемога — {Nick(won)} і {Nick(won + 2)}");
            return;
        }
        Deal();
    }

    /// <summary>
    /// «Доміно: Оля 104 : Петро 61 (за 9 раундів)» — переможець першим, ніки в називному, бо відмінювати
    /// їх нема як. Це єдиний слід партії в Журналі, тому раунди рахуємо тут же.
    /// </summary>
    string Scoreline(int winner)
    {
        var order = Seats().OrderByDescending(s => s == winner).ThenByDescending(s => _scores[s]);
        return $"{Info.Title}: " + string.Join(" : ", order.Select(s => $"{Nick(s)} {_scores[s]}"))
            + $" (за {Rounds(_round)})";
    }

    /// <summary>«1 раунд», «2 раунди», «11 раундів» — число в рядку має читатись по-людськи.</summary>
    static string Rounds(int n) =>
        n % 100 is >= 11 and <= 14 ? $"{n} раундів"
        : n % 10 == 1 ? $"{n} раунд"
        : n % 10 is >= 2 and <= 4 ? $"{n} раунди"
        : $"{n} раундів";

    /// <summary>
    /// Хтось встає з-за столу: його кістки йдуть у базар, решта грає далі. Лишився один — партія його,
    /// і це та сама техпоразка, що й у каркаса, просто з нашим текстом.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (_winner is not null || _draw || seat < 0 || seat >= MaxSeats || !_in[seat]) return;

        // Людей за столом не лишилось — Глеки самі з собою не догравають.
        if (!Seats().Any(s => s != seat && !IsBot(s)))
        {
            _winner = null;
            _draw = true;
            _botAt = null;
            Ctx.Finish([], $"{Info.Title}: {Nick(seat)} встає з-за столу, а з Глеками догравати нікому");
            return;
        }
        // «Козел» без четвертого не живе: замість того, хто встав, сідає Глек і грає його кістками.
        if (_kozel)
        {
            var used = _bots.Count(b => b is not null);
            _bots[seat] = BoardBots.Names[used % BoardBots.Names.Length];
            Ctx.Log($"{Info.Title}: {Ctx.NickOf(seat)} встає з-за столу — його кістки бере {_bots[seat]}");
            Arm();
            return;
        }
        _in[seat] = false;
        _boneyard.AddRange(_hands[seat]);
        _hands[seat].Clear();

        if (Alive <= 1)
        {
            var last = FirstAlive;
            _winner = last >= 0 ? last : null;
            if (last >= 0) Ctx.Finish([last], $"{Info.Title}: усі встали з-за столу, перемога — {Ctx.NickOf(last)}");
            else Ctx.Finish([], $"{Info.Title}: за столом уже ні душі");
            return;
        }

        Ctx.Log($"{Info.Title}: {Ctx.NickOf(seat)} встає з-за столу, кістки йдуть у базар");
        if (_turn == seat) NextTurn();
        CheckFish();
        Arm();
    }

    // ---------- вид ----------

    public override object View(int? seat)
    {
        var me = seat is { } s && s >= 0 && s < MaxSeats ? s : -1;
        var over = _winner is not null || _draw;
        var mine = me >= 0 && me == _turn && !over && _in[me];
        return new
        {
            turn = over ? null : (int?)_turn,
            target = _target,
            players = Alive,
            round = _round,
            line = _line.Select(b => new { tile = b.Wire, @double = b.Double }).ToArray(),
            ends = _line.Count == 0 ? null : new[] { _line[0].A, _line[^1].B },
            // Чужі руки не показуємо взагалі — ні гравцям, ні глядачам: гра Hidden.
            hand = me >= 0 ? _hands[me].Select(b => b.Wire).ToArray() : null,
            counts = _hands.Select(h => h.Count).ToArray(),
            boneyard = _boneyard.Count,
            scores = (int[])_scores.Clone(),
            canPlay = mine && CanPlay(me),
            mustDraw = mine && !CanPlay(me) && _boneyard.Count > 0,
            lastRound = _last is null ? null : new
            {
                winner = _last.Winner, points = _last.Points, reason = _last.Reason,
                round = _last.Round, left = _last.Left,
            },
            result = !over ? null : new { winner = _winner, scores = (int[])_scores.Clone() },
            // Прохід №3: «Козел» (пари 0+2 і 1+3, scores — штраф пари), Глеки на місцях і коли Глек «подумав».
            kozel = _kozel,
            bots = _bots.Any(b => b is not null) ? (string?[])_bots.Clone() : null,
            botIn = _botAt is { } at && !over ? Math.Max(0, (int)(at - Ctx.Clock.UtcNow).TotalMilliseconds) : (int?)null,
            goats = _goats.Length > 0 ? (string[])_goats.Clone() : null,
        };
    }

    // ---------- збереження ----------

    /// <summary>Стан партії в JSON. Гра не Persistent, але з цим і тести, і майбутнє відновлення простіші.</summary>
    sealed record Snapshot(
        int[][] Line, int[][] Yard, int[][][] Hands, bool[] In, int[] Scores,
        int Turn, int Round, int? Winner, int? LastWinner,
        int? LastRoundWinner, int LastPoints, string LastReason);

    public override string? Save() => JsonSerializer.Serialize(new Snapshot(
        [.. _line.Select(b => b.Wire)],
        [.. _boneyard.Select(b => b.Wire)],
        [.. _hands.Select(h => h.Select(b => b.Wire).ToArray())],
        (bool[])_in.Clone(), (int[])_scores.Clone(),
        _turn, _round, _winner, _lastWinner,
        _last?.Winner, _last?.Points ?? 0, _last?.Reason ?? ""));

    public override void Load(string json)
    {
        if (JsonSerializer.Deserialize<Snapshot>(json) is not { } s) return;

        _line.Clear();
        _line.AddRange(Bones(s.Line));
        _boneyard.Clear();
        _boneyard.AddRange(Bones(s.Yard));
        for (var seat = 0; seat < MaxSeats; seat++)
        {
            _hands[seat].Clear();
            if (s.Hands is not null && seat < s.Hands.Length) _hands[seat].AddRange(Bones(s.Hands[seat]));
            _in[seat] = s.In is not null && seat < s.In.Length && s.In[seat];
            _scores[seat] = s.Scores is not null && seat < s.Scores.Length ? s.Scores[seat] : 0;
        }
        _turn = s.Turn is >= 0 and < MaxSeats ? s.Turn : 0;
        _round = s.Round;
        _winner = s.Winner;
        _lastWinner = s.LastWinner;
        _last = string.IsNullOrEmpty(s.LastReason) ? null : new RoundEnd(s.LastRoundWinner, s.LastPoints, s.LastReason);
    }

    static IEnumerable<DominoBone> Bones(int[][]? raw) =>
        (raw ?? []).Where(p => p is { Length: 2 }).Select(p => new DominoBone(p[0], p[1]));
}
