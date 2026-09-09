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
    /// <summary>Скільки очок треба набрати, щоб партія скінчилась.</summary>
    public const int Target = 100;

    const int MaxSeats = 4;

    public override GameInfo Info { get; } = new(
        "domino", "Доміно", "доміно", GameGroup.Board, 2, MaxSeats,
        Start: StartMode.ByHost, Hidden: true,
        Hint: "Класичне доміно: прикладай кістки однаковими половинками. Гра до 100 очок");

    /// <summary>Рука кожного місця. Масив завжди на всі місця — індекс тут це номер місця, а не гравця.</summary>
    readonly List<DominoBone>[] _hands = [.. Enumerable.Range(0, MaxSeats).Select(_ => new List<DominoBone>())];
    /// <summary>Ланцюг зліва направо, уже орієнтований.</summary>
    readonly List<DominoBone> _line = [];
    readonly List<DominoBone> _boneyard = [];
    /// <summary>Хто грає цей раунд: той, хто встав з-за столу, лишається з рахунком, але без кісток.</summary>
    readonly bool[] _in = new bool[MaxSeats];
    int[] _scores = new int[MaxSeats];
    int _turn;
    int _round;
    /// <summary>Переможець партії; поки null — граємо далі.</summary>
    int? _winner;
    /// <summary>Хто виграв минулий раунд — той і починає наступний.</summary>
    int? _lastWinner;
    RoundEnd? _last;

    /// <summary>Чим скінчився минулий раунд — рядок для картки, а не для правил.</summary>
    sealed record RoundEnd(int? Winner, int Points, string Reason);

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
        _scores = new int[MaxSeats];
        _round = 0;
        _winner = null;
        _lastWinner = null;
        _last = null;
        Deal();
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
            _in[seat] = Ctx.Seated(seat);
        }

        var deck = Deck();
        Shuffle(deck);
        // Двоє беруть по сім, компанія — по п'ять: інакше на чотирьох базару майже не лишиться.
        var each = Alive == 2 ? 7 : 5;
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
        if (action is not ("play" or "draw" or "pass")) return ActResult.Fail("Тут так не ходять");
        if (_winner is not null) return ActResult.Fail("Партію зіграно, тисни «Ще раз»");
        if (!_in[seat]) return ActResult.Fail("Ти вже не в цій партії");
        if (seat != _turn) return ActResult.Fail("Зараз не твій хід");

        return action switch
        {
            "play" => Play(seat, payload),
            "draw" => Draw(seat),
            _ => Pass(seat),
        };
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
            return ActResult.Accept("Раунд твій!");
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
        _last = new RoundEnd(winner, points, reason);
        // Переможець раунду відкриває наступний. Після риби з рівними руками переможця нема — тоді
        // _lastWinner теж стає порожнім, і знову діє правило найстаршого дубля.
        _lastWinner = winner;
        if (winner is { } seat) _scores[seat] += points;

        // У Журнал підсумки раундів не пишемо: Журнал спільний на весь сайт (радіо й Балачки), а
        // раундів у партії до ста очок буває під два десятки. Гравцям те саме каже картка через
        // lastRound, а в Журнал іде один рядок на всю партію — з Ctx.Finish нижче.
        if (winner is { } champion && _scores[champion] >= Target)
        {
            _winner = champion;
            Ctx.Finish([champion], Scoreline(champion));
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
        return $"{Info.Title}: " + string.Join(" : ", order.Select(s => $"{Ctx.NickOf(s)} {_scores[s]}"))
            + $" (за {Rounds(_round)})";
    }

    /// <summary>«1 раунд», «2 раунди», «11 раундів» — число в рядку має читатись по-людськи.</summary>
    static string Rounds(int n) =>
        n % 100 is >= 11 and <= 14 ? $"{n} раундів"
        : n % 10 == 1 ? $"{n} раунд"
        : n % 10 is >= 2 and <= 4 ? $"{n} раунди"
        : $"{n} раундів";

    /// <summary>
    /// Хтось встав з-за столу: його кістки йдуть у базар, решта грає далі. Лишився один — партія його,
    /// і це та сама техпоразка, що й у каркаса, просто з нашим текстом.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (_winner is not null || seat < 0 || seat >= MaxSeats || !_in[seat]) return;

        _in[seat] = false;
        _boneyard.AddRange(_hands[seat]);
        _hands[seat].Clear();

        if (Alive <= 1)
        {
            var last = FirstAlive;
            _winner = last >= 0 ? last : null;
            if (last >= 0) Ctx.Finish([last], $"{Info.Title}: усі встали з-за столу, перемога — {Ctx.NickOf(last)}");
            else Ctx.Finish([], $"{Info.Title}: за столом уже нікого");
            return;
        }

        Ctx.Log($"{Info.Title}: {Ctx.NickOf(seat)} встав з-за столу, кістки пішли в базар");
        if (_turn == seat) NextTurn();
        CheckFish();
    }

    // ---------- вид ----------

    public override object View(int? seat)
    {
        var me = seat is { } s && s >= 0 && s < MaxSeats ? s : -1;
        var mine = me >= 0 && me == _turn && _winner is null && _in[me];
        return new
        {
            turn = _winner is null ? _turn : (int?)null,
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
            lastRound = _last is null ? null : new { winner = _last.Winner, points = _last.Points, reason = _last.Reason },
            result = _winner is null ? null : new { winner = _winner.Value, scores = (int[])_scores.Clone() },
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
