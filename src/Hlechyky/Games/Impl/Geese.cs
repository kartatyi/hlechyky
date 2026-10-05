using System.Globalization;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Порахуй гусей» — спільне для столу (<see cref="Geese"/>) і «Гусей дня» (<see cref="GeeseDaily"/>). Раунд:
/// «готуйсь» → парад (клієнтська анімація з готового списку тварин) → відповідь (10 с) → показ (повтор параду
/// прискорено з підсвіченими тваринами-відповіддю). Правила й час — тут; клієнт лише малює за <c>el/left</c>.
/// </summary>
public abstract class GeeseBase : Game
{
    public const string PhLobby = "lobby", PhReady = "ready", PhParade = "parade", PhAnswer = "answer", PhReveal = "reveal", PhDone = "done";
    public const int MaxSeats = 8;
    public const int TickStep = 100;
    /// <summary>Очки: точно — 3, мимо на одну — 1, найшвидшому з точних — +1 (від двох гравців).</summary>
    public const int Exact = 3, Near = 1, FastBonus = 1;
    /// <summary>У скільки разів швидше крутиться повтор параду на показі.</summary>
    public const double RevealSpeed = 2.5;
    public const string AfterNote = "Дивись уважно — питання буде потім";

    // ---------- розклад партії (нащадки переозначують) ----------

    /// <summary>Рівень складності кожного раунду (0…6); довжина масиву — кількість раундів.</summary>
    protected abstract int[] Levels { get; }
    /// <summary>Скільки перших раундів показують питання до параду (рішення користувача: 1–3), далі — на пам'ять.</summary>
    protected virtual int PreRounds => 3;
    protected virtual int ReadyMs(bool pre) => pre ? 4000 : 3000;
    protected virtual int AnswerMs => 10_000;
    protected virtual int RevealTailMs => 2500;
    /// <summary>Довжина параду раунду (null — за рівнем, 10–15 с).</summary>
    protected virtual int? ParadeMs(int round) => null;
    /// <summary>Сід параду партії: стіл — з <c>Ctx.Rng</c>, день — від дати.</summary>
    protected virtual int Seed() => Ctx.Rng.Next();
    /// <summary>Чи можна кликати «🤖 + бот» (у «Гусях дня» — ні).</summary>
    protected virtual bool BotsAllowed => true;

    // ---------- стан ----------

    readonly SoloBot _solo = new();
    private protected PartyMode? _party;
    public bool Party => _party is not null;
    public const int SoloBots = 2;

    private protected readonly List<GeeseRound> _rounds = [];
    private protected int _seed;
    private protected int _r;                    // поточний раунд, від 0
    private protected string _ph = PhLobby;
    DateTimeOffset _phAt;
    int _phMs;
    private protected readonly long[] _scores = new long[MaxSeats];
    readonly int?[] _ans = new int?[MaxSeats];
    readonly int[] _ansAt = new int[MaxSeats];
    /// <summary>Очки кожного місця в кожному раунді та чи взяв бонус швидкості (для показу й підсумку).</summary>
    private protected readonly List<(int?[] Ans, int[] Pts, bool[] Fast)> _hist = [];
    readonly bool[] _plays = new bool[MaxSeats];
    int[] _bots = [];
    readonly (int At, int Value)?[] _plan = new (int, int)?[MaxSeats];
    int _humansAtStart;
    bool _touched;
    int _says;
    private protected int[] _winners = [];

    public IReadOnlyList<GeeseRound> Rounds => _rounds;
    public string Phase => _ph;
    public int RoundIndex => _r;
    public IReadOnlyList<int> Bots => _bots;
    public long ScoreOf(int seat) => _scores[seat];

    /// <summary>Місця, що можуть грати: у вечірці — 0..N−1 (вечірка садить щільно), за столом — усі місця столу
    /// (<c>Ctx.Players</c> там — скільки зайнято, а місця бувають з дірками).</summary>
    int Seats => _party is not null ? Math.Min(Ctx.Players, MaxSeats) : Math.Min(Info.MaxPlayers, MaxSeats);

    public override string SeatName(int seat) => (seat + 1).ToString(CultureInfo.InvariantCulture);

    public override string? SeatBot(int seat) =>
        !Ctx.Seated(seat) && Array.IndexOf(_ph == PhLobby ? BotSeats() : _bots, seat) >= 0 ? $"{LiveBots.Name} {SeatName(seat)}" : null;

    string Name(int seat) => SeatBot(seat) ?? Ctx.NickOf(seat) ?? SeatName(seat);

    bool IsBot(int seat) => Array.IndexOf(_bots, seat) >= 0 && !Ctx.Seated(seat);

    /// <summary>Хто ще може відповісти: людина за столом або бот.</summary>
    bool Active(int seat) => _plays[seat] && (Ctx.Seated(seat) || IsBot(seat));

    int[] BotSeats() => BotsAllowed && _party is null && _solo.Active(Ctx, Seats)
        ? [.. Enumerable.Range(0, Seats).Where(s => !Ctx.Seated(s)).Take(SoloBots)] : [];

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _solo.Configure(options);
        _party = PartyMode.Read(options);
    }

    public override bool ActsInLobby => BotsAllowed;

    // ---------- партія ----------

    public override void Start()
    {
        _bots = _party is { } pm ? [.. pm.Bots.Where(s => s < Seats && !Ctx.Seated(s))] : BotSeats();
        Array.Clear(_plays);
        for (var s = 0; s < Seats; s++) _plays[s] = _party is not null || Ctx.Seated(s) || Array.IndexOf(_bots, s) >= 0;
        _humansAtStart = Enumerable.Range(0, Seats).Count(Ctx.Seated);
        Array.Clear(_scores);
        _hist.Clear();
        _winners = [];
        _says = 0;
        _seed = Seed();
        BuildRounds();
        _r = 0;
        Open(PhReady, ReadyMs(_rounds[0].Pre));
        if (_party is null && _bots.Length == 0 && _humansAtStart >= 2 && Ctx.Rng.Next(2) == 0) Say(Hello[Ctx.Rng.Next(Hello.Length)]);
    }

    /// <summary>Усі раунди партії — одразу, з одного сіду: парад і питання від сіду не залежать від того, хто що відповів.</summary>
    private protected void BuildRounds()
    {
        _rounds.Clear();
        var rng = new Random(_seed);
        var traits = GeeseParade.PickTraits(rng);
        string? last = null;
        var levels = Levels;
        for (var i = 0; i < levels.Length; i++)
        {
            var round = GeeseParade.Make(rng, levels[i], i < PreRounds, traits, last, ParadeMs(i));
            _rounds.Add(round);
            last = round.Q.Type;
        }
    }

    /// <summary>
    /// Продовжити поточний раунд (відновлення «Гусей дня»): з «готуйсь» або, якщо парад раунду вже показували, —
    /// одразу з відповіді (парад удруге не дивляться).
    /// </summary>
    private protected void Resume(bool answer = false)
    {
        if (answer) Open(PhAnswer, AnswerMs);
        else Open(PhReady, ReadyMs(Cur.Pre));
    }

    private protected GeeseRound Cur => _rounds[Math.Clamp(_r, 0, _rounds.Count - 1)];

    void Open(string ph, int ms)
    {
        _ph = ph;
        _phAt = Ctx.Clock.UtcNow;
        _phMs = ms;
        if (ph != PhAnswer) return;
        Array.Clear(_ans);
        Array.Clear(_ansAt);
        Array.Clear(_plan);
        var level = _party?.Level ?? _solo.Level;
        foreach (var b in _bots) _plan[b] = GeeseBot.Plan(Ctx.Rng, level, Cur, AnswerMs);
    }

    /// <summary>Мс від початку фази (у лобі — 0: фази ще не було).</summary>
    int Elapsed => _ph == PhLobby ? 0 : (int)Math.Clamp((Ctx.Clock.UtcNow - _phAt).TotalMilliseconds, 0, int.MaxValue);

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == LiveBots.Toggle)
        {
            if (!BotsAllowed || _party is not null) return ActResult.Fail("Тут бота не кличуть");
            return _ph is PhLobby or PhDone ? _solo.Switch(Ctx, seat, payload, Seats) : ActResult.Fail("Партія вже йде");
        }
        if (action != "answer") return ActResult.Fail("Тут так не ходять");
        if (_ph == PhLobby) return ActResult.Fail("Чекаємо на гравців");
        if (seat is < 0 or >= MaxSeats || !_plays[seat]) return ActResult.Fail("Ти тут не граєш");
        if (_ph != PhAnswer) return ActResult.Fail(_ph is PhReady or PhParade ? "Спершу подивись парад" : "Відповідати вже пізно");
        if (_ans[seat] is not null) return ActResult.Fail("Відповідь уже є");
        var q = Cur.Q;
        int value;
        if (q.Choice)
        {
            if (!TryInt(payload, "c", out value) || value < 0 || value >= q.Opts!.Length) return ActResult.Fail("Обери одного з варіантів");
        }
        else if (!TryInt(payload, "n", out value) || value < 0 || value > GeeseParade.MaxAnswer)
            return ActResult.Fail($"Число від 0 до {GeeseParade.MaxAnswer}");
        Answer(seat, value, Elapsed);
        return ActResult.Done;
    }

    static bool TryInt(JsonElement p, string key, out int value)
    {
        value = 0;
        return p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value);
    }

    void Answer(int seat, int value, int at)
    {
        _ans[seat] = value;
        _ansAt[seat] = at;
        _touched = true;
    }

    public override TickResult Tick()
    {
        if (_ph is PhLobby or PhDone || _rounds.Count == 0) return TickResult.None;
        var el = Elapsed;
        if (_ph == PhAnswer)
        {
            foreach (var b in _bots)
                if (_ans[b] is null && _plan[b] is { } p && el >= p.At && IsBot(b)) Answer(b, p.Value, p.At);
            if (Enumerable.Range(0, Seats).Where(Active).All(s => _ans[s] is not null)) el = _phMs;
        }
        if (el < _phMs)
        {
            if (!_touched) return TickResult.None;
            _touched = false;
            return TickResult.Both;
        }
        _touched = false;
        switch (_ph)
        {
            case PhReady:
                Open(PhParade, Cur.Ms);
                break;
            case PhParade:
                Open(PhAnswer, AnswerMs);
                break;
            case PhAnswer:
                ScoreRound();
                Open(PhReveal, (int)(Cur.Ms / RevealSpeed) + RevealTailMs);
                break;
            case PhReveal:
                if (_r + 1 < _rounds.Count)
                {
                    _r++;
                    Open(PhReady, ReadyMs(Cur.Pre));
                }
                else Done();
                break;
        }
        return TickResult.Both;
    }

    /// <summary>Очки раунду: точно — 3 (вибір — лише точно), мимо на одну — 1, найшвидшому з точних — +1, якщо грає двоє й більше.</summary>
    void ScoreRound()
    {
        var round = Cur;
        var pts = new int[MaxSeats];
        var fast = new bool[MaxSeats];
        var ans = new int?[MaxSeats];
        var exact = new List<int>();
        for (var s = 0; s < Seats; s++)
        {
            if (!_plays[s] || _ans[s] is not { } a) continue;
            ans[s] = a;
            var d = Math.Abs(a - round.Answer);
            pts[s] = d == 0 ? Exact : !round.Q.Choice && d == 1 ? Near : 0;
            if (d == 0) exact.Add(s);
        }
        if (exact.Count > 0 && _plays.Count(x => x) >= 2)
        {
            var first = exact.Min(s => _ansAt[s]);
            foreach (var s in exact.Where(s => _ansAt[s] == first)) { pts[s] += FastBonus; fast[s] = true; }
        }
        for (var s = 0; s < Seats; s++) _scores[s] += pts[s];
        _hist.Add((ans, pts, fast));
        RevealSay(exact.Count, ans);
    }

    void RevealSay(int exact, int?[] ans)
    {
        var answered = ans.Count(a => a is not null);
        if (_party is not null || answered < 2 || _says >= 2) return;
        // Рідко: усі промахнулись — кпина; один-єдиний точний серед трьох і більше — похвала.
        if (exact == 0 && Ctx.Rng.Next(3) == 0) Say(Missed[Ctx.Rng.Next(Missed.Length)]);
        else if (exact == 1 && answered >= 3 && Ctx.Rng.Next(3) == 0)
        {
            var who = Enumerable.Range(0, Seats).First(s => ans[s] == Cur.Answer);
            Say(string.Format(CultureInfo.InvariantCulture, Lonely[Ctx.Rng.Next(Lonely.Length)], Name(who)));
        }
        else if (Cur.Q.Type == "trait" && Ctx.Rng.Next(4) == 0) Say(Traity[Ctx.Rng.Next(Traity.Length)]);
    }

    void Say(string text) { _says++; Ctx.Say(text); }

    /// <summary>Кінець партії: стіл — переможці й ачівки, вечірка — scores кожного місця.</summary>
    private protected virtual void Done()
    {
        _ph = PhDone;
        _phAt = Ctx.Clock.UtcNow;
        _phMs = 0;
        var playing = Enumerable.Range(0, Seats).Where(s => _plays[s]).ToArray();
        var best = playing.Length == 0 ? 0 : playing.Max(s => _scores[s]);
        _winners = best <= 0 ? [] : [.. playing.Where(s => _scores[s] == best)];
        var line = $"{Info.Title}: {string.Join(" : ", playing.OrderByDescending(s => _scores[s]).ThenBy(s => s).Select(s => $"{Name(s)} {_scores[s]}"))}";
        if (_party is not null)
        {
            Ctx.Finish(_winners, line, PartyScores());
            return;
        }
        if (_bots.Length == 0 && _humansAtStart >= 2)
        {
            foreach (var s in playing.Where(Ctx.Seated))
            {
                if (_hist.All(h => h.Pts[s] >= Exact)) Ctx.Award(s, 0, "ach:geese-shepherd");
                if (_hist.Count > 0 && !_rounds[^1].Q.Choice && _hist[^1].Ans[s] == _rounds[^1].Answer && _rounds[^1].Level >= GeeseParade.MaxLevel)
                    Ctx.Award(s, 0, "ach:geese-eagle");
            }
            if (_winners.Length == 1 && _says < 3 && Ctx.Rng.Next(2) == 0)
                Say(string.Format(CultureInfo.InvariantCulture, Bye[Ctx.Rng.Next(Bye.Length)], Name(_winners[0])));
        }
        Ctx.Finish(_winners, _winners.Length == 0 ? line + " — нічия" : line);
    }

    /// <summary>Поточні очки кожного місця 0..N−1 (вечірка рахує місця лише за ними).</summary>
    public IReadOnlyDictionary<int, long> PartyScores()
    {
        var r = new Dictionary<int, long>(Ctx.Players);
        for (var s = 0; s < Ctx.Players; s++) r[s] = s < MaxSeats ? _scores[s] : 0;
        return r;
    }

    public override void OnLeave(int seat)
    {
        if (_party is not null || _ph is PhLobby or PhDone) return;
        if (seat is >= 0 and < MaxSeats && _ph == PhAnswer && _ans[seat] is null) _touched = true;   // більше не чекаємо на нього
        if (Enumerable.Range(0, Seats).Any(s => s != seat && _plays[s] && Ctx.Seated(s))) return;
        _ph = PhDone;
        _winners = [];
        Ctx.Finish(_winners, $"{Info.Title}: гравці розійшлись, парад не дорахували");
    }

    // ---------- види ----------

    public override object View(int? seat)
    {
        var me = seat is >= 0 and < MaxSeats ? seat.Value : -1;
        var lobby = _ph == PhLobby;
        var started = !lobby && _rounds.Count > 0;
        var round = started ? Cur : null;
        var showQ = round is not null && (round.Pre || _ph is PhAnswer or PhReveal or PhDone);
        var showParade = round is not null && _ph != PhReady;
        var revealed = round is not null && _ph is PhReveal or PhDone && _hist.Count > _r;
        var el = Elapsed;
        return new
        {
            ph = _ph,
            round = started ? _r + 1 : 0,
            rounds = Levels.Length,
            level = round?.Level ?? 0,
            el,
            left = Math.Max(0, _phMs - el),
            dur = _phMs,
            pre = round?.Pre ?? false,
            note = round is not null && !round.Pre && _ph is PhReady or PhParade ? AfterNote : null,
            q = showQ ? Question(round!) : null,
            parade = showParade ? Parade(round!) : null,
            players = Enumerable.Range(0, Seats).Where(s => lobby ? Ctx.Seated(s) || Array.IndexOf(BotSeats(), s) >= 0 : _plays[s]).ToArray(),
            bot = lobby ? BotSeats() : _bots.Where(s => !Ctx.Seated(s)).ToArray(),
            scores = _scores.Take(Seats).ToArray(),
            answered = _ph == PhAnswer ? Enumerable.Range(0, Seats).Where(s => _ans[s] is not null).ToArray() : [],
            mine = me >= 0 && _ph == PhAnswer ? _ans[me] : null,
            reveal = revealed ? Reveal(_r) : null,
            recap = _ph == PhDone && me >= 0 ? Recap(me) : null,
            winners = _ph == PhDone ? _winners : null,
            botOffer = BotsAllowed && _party is null && (lobby || _ph == PhDone) && _solo.Offer(Ctx, Seats),
            botWanted = _solo.Wanted,
            botLvl = _solo.LevelKey,
            party = _party is not null,
            daily = DailyView(),
        };
    }

    /// <summary>Малий кадр для синхронізації годинника фази (повний парад — у виді).</summary>
    public override object? Frame() => new { ph = _ph, round = _r + 1, el = Elapsed, left = Math.Max(0, _phMs - Elapsed), answered = _ph == PhAnswer ? Enumerable.Range(0, Seats).Where(s => _ans[s] is not null).ToArray() : [] };

    private protected virtual object? DailyView() => null;

    static object Question(GeeseRound r) => new
    {
        type = r.Q.Type,
        text = r.Q.Text,
        kinds = r.Q.Kinds,
        trait = r.Q.Trait,
        opts = r.Q.Opts?.Select(k => { var kind = GeeseParade.Kind(k); return new { k, name = kind.One, emo = kind.Emoji }; }).ToArray(),
        max = GeeseParade.MaxAnswer,
    };

    object Parade(GeeseRound r) => new
    {
        seed = _seed ^ (_r * 7919),
        ms = r.Ms,
        m = GeeseParade.Margin,
        lanes = r.Lanes,
        rx = RevealSpeed,
        // Поки відповідають, двір схований — і складу параду у виді нема: інакше відповідь рахується одним рядком
        // у консолі. Повертається на показі.
        animals = _ph == PhAnswer ? [] : r.Animals.Select(a => new { k = a.K, tr = a.Tr, l = a.L, d = a.D, t0 = a.T0, v = a.V, y = a.Y, f = a.F }).ToArray(),
        covers = _ph == PhAnswer ? [] : r.Covers.Select(c => new { kind = c.Kind, l = c.L, x0 = c.X0, x1 = c.X1 }).ToArray(),
    };

    object Reveal(int i)
    {
        var r = _rounds[i];
        var h = _hist[i];
        return new
        {
            answer = r.Answer,
            kind = r.Q.Choice ? r.Q.Opts![r.Answer] : null,
            hits = r.Hits,
            rows = Enumerable.Range(0, Seats).Where(s => _plays[s]).Select(s => new { s, a = h.Ans[s], pts = h.Pts[s], fast = h.Fast[s] }).ToArray(),
        };
    }

    object[] Recap(int me) => [.. _hist.Select((h, i) => (object)new
    {
        q = _rounds[i].Q.Text,
        answer = _rounds[i].Q.Choice ? GeeseParade.Kind(_rounds[i].Q.Opts![_rounds[i].Answer]).One : _rounds[i].Answer.ToString(CultureInfo.InvariantCulture),
        mine = h.Ans[me] is not { } a ? null : _rounds[i].Q.Choice ? GeeseParade.Kind(_rounds[i].Q.Opts![a]).One : a.ToString(CultureInfo.InvariantCulture),
        pts = h.Pts[me],
    })];

    // ---------- Дядько Глек ----------

    static readonly string[] Hello =
    [
        "Гусака в капелюсі хтось бачив? Ні? То рахуйте уважніше.",
        "Зараз через двір побіжить уся моя худоба. Рахуйте, бо я сам збився ще вчора.",
        "Козу в хустці не рахуйте двічі — то та сама коза.",
    ];
    static readonly string[] Missed =
    [
        "Ніхто не влучив. Може, то гуси вас порахували, а не ви їх?",
        "Усі мимо. Я теж колись так рахував — і продав корову двічі.",
    ];
    static readonly string[] Lonely =
    [
        "{0} один порахував точно. Решті — окуляри з ярмарку.",
        "Око як у шуліки — це про {0}.",
    ];
    static readonly string[] Traity =
    [
        "Козу в хустці не рахуйте двічі — то та сама коза.",
        "Хустка на козі — то не мода, то щоб не плутали з сусідською.",
    ];
    static readonly string[] Bye =
    [
        "{0} — головний пастух двору. Гуси вже пишуть скаргу.",
        "Худобу перерахував {0}. Беру на роботу — платню глечиками.",
    ];
}

/// <summary>
/// «Порахуй гусей» за столом: 1–8 гравців (можна й самому, з «🤖 + бот» — двоє ботів), 7 раундів зі зростанням
/// складності. У режимі вечірки — 3 раунди (~60–75 с), scores = очки.
/// </summary>
public sealed class Geese : GeeseBase, IPartyMinigame
{
    public override GameInfo Info { get; } = new(
        "geese", "Порахуй гусей", "«Порахуй гусей»", GameGroup.Party, 1, MaxSeats,
        TickMs: TickStep, Start: StartMode.ByHost, Options: [LiveBots.LevelOption],
        Hint: "Через двір біжить уся худоба Дядька Глека, а потім він питає: «Скільки курей?» Хто точніший і швидший — той і пастух. Можна й самому");

    static readonly int[] TableLevels = [0, 1, 2, 3, 4, 5, 6];
    static readonly int[] PartyLevels = [1, 2, 4];

    protected override int[] Levels => Party ? PartyLevels : TableLevels;
    protected override int PreRounds => Party ? 1 : 3;
    protected override int ReadyMs(bool pre) => Party ? (pre ? 3500 : 3000) : base.ReadyMs(pre);
    protected override int AnswerMs => Party ? 8000 : base.AnswerMs;
    protected override int RevealTailMs => Party ? 2000 : base.RevealTailMs;
    protected override int? ParadeMs(int round) => Party ? 8000 + 500 * round : null;

    public string Howto => "Рахуй живність, що біжить двором, і відповідай: точно — 3 очки, мимо на одну — 1, найшвидшому з точних +1. "
        + "−/+ або цифри, Enter — відповісти";
    public int PartyCapMs => 100_000;   // 3 раунди ≈ 75 с, коли ніхто не відповідає (з відповідями — ~60 с)
    public int PartyMin => 2;
    public int PartyMax => MaxSeats;
}

/// <summary>
/// Бот «Порахуй гусей»: знає правильну відповідь і псує її шумом за рівнем; думає один раз на початку відповіді
/// (рандом — <c>Ctx.Rng</c>), відповідає із затримкою. Сильний частіше точний і швидший, але не ідеальний.
/// </summary>
public static class GeeseBot
{
    public static (int At, int Value) Plan(Random rng, LiveBots.Level level, GeeseRound round, int answerMs)
    {
        var i = LiveBots.Index(level);
        // Затримка: легкий думає довго, сильний — швидко; усі вкладаються у вікно відповіді.
        (int lo, int hi) = i switch { 0 => (4000, 7500), 1 => (2500, 6000), _ => (1500, 4500) };
        hi = Math.Min(hi, answerMs - 300);
        lo = Math.Min(lo, hi);
        var at = lo + rng.Next(hi - lo + 1);
        // Густий парад збиває й ботів; на пам'ять (питання «після») — теж важче, як і людині.
        var hard = (round.Level >= 4 ? 0.1 : 0) + (round.Pre ? 0 : 0.1);
        if (round.Q.Choice)
        {
            var p = i switch { 0 => 0.45, 1 => 0.7, _ => 0.88 } - hard;
            if (rng.NextDouble() < p || round.Q.Opts!.Length < 2) return (at, round.Answer);
            var other = rng.Next(round.Q.Opts!.Length - 1);
            return (at, other >= round.Answer ? other + 1 : other);
        }
        var exact = i switch { 0 => 0.25, 1 => 0.5, _ => 0.75 } - hard;
        if (rng.NextDouble() < exact) return (at, round.Answer);
        var off = i switch
        {
            0 => 1 + rng.Next(3),
            1 => rng.NextDouble() < 0.8 ? 1 : 2,
            _ => 1,
        };
        var sign = rng.Next(2) == 0 ? -1 : 1;
        if (round.Answer - off < 0) sign = 1;
        return (at, Math.Clamp(round.Answer + sign * off, 0, GeeseParade.MaxAnswer));
    }
}
