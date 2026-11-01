using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Як облупленого» — чи знаєш ти друзів (specs/oblup.md; основа — настолка Cross the Line). По черзі хтось стає
/// «в центрі»: обирає одне з двох питань і таємно ставить себе на шкалу між двома крайнощами («Ананаси на піці»:
/// злочин ↔ шедевр). Решта ставить рамку туди, де, на їхню думку, він стане. Широка рамка влучає частіше, але
/// дешево; вузька — ризик і багато очок. Наприкінці видно, хто кого знає як облупленого.
/// <para>
/// Як «Скільки?», партія живе від тика: фази міняє лише <see cref="Tick"/>, а <see cref="Act"/> тільки міняє стан
/// і ставить <c>_touched</c> — види доїдуть найближчим тиком. Гра <c>Hidden</c>: до риски ні чужих рамок, ні місця
/// героя у виді нема — лише галочки «поставив».
/// </para>
/// </summary>
public sealed class Oblup : Game
{
    public const int MaxSeats = 8;
    /// <summary>Шкала — цілі від 0 до <see cref="Span"/>; клієнт показує її без чисел, лише полюси.</summary>
    public const int Span = 100;

    public const int BetweenSeconds = 3, PickSeconds = 15, RevealSeconds = 8;
    /// <summary>Пішов той, хто в центрі, — коротка пауза з поясненням, і далі.</summary>
    public const int VoidSeconds = 4;
    /// <summary>Скільки секунд на ставку, якщо господар не обрав іншого.</summary>
    public const int GuessSeconds = 45;
    static readonly int[] SecondChoices = [30, 45, 60];

    /// <summary>🎯 «В яблучко»: риска не далі за стільки від центру рамки — ще <see cref="BullBonus"/>.</summary>
    public const int BullRange = 1, BullBonus = 1;
    /// <summary>Скільки очок партії коштує черепок (як у «Скільки?»).</summary>
    public const int PointsPerShard = 5;
    /// <summary>Своїх питань у черзі від одного ніка.</summary>
    public const int MineMax = 3;
    /// <summary>Коротше за це — не питання, а «а?».</summary>
    public const int MineMinQ = 6;

    public const string PhaseBetween = "between";
    public const string PhasePick = "pick";
    public const string PhaseGuess = "guess";
    public const string PhaseReveal = "reveal";
    public const string PhaseDone = "done";

    /// <summary>Рамка: ширина в одиницях шкали й очки за влучання. Рівновагу пораховано в specs/oblup.md §1.</summary>
    public sealed record Box(string Key, int Width, int Points, string Name)
    {
        public int Half => Width / 2;
    }

    public static readonly Box[] Boxes = [new("w", 30, 2, "широка"), new("m", 18, 3, "середня"), new("n", 8, 6, "вузька")];

    static Box? BoxOf(string? key) => Array.Find(Boxes, b => b.Key == key);

    /// <summary>Рядок риски: чия рамка, де, яка, чи перетнула її риска й скільки за це дали (з форою).</summary>
    sealed record Row(int Seat, int? Pos, string? Size, bool Hit, bool Bull, int Points);

    /// <summary>Зіграний раунд для підсумку партії. Ім'я героя — тоді ж: він міг уже встати з-за столу.</summary>
    sealed record Recap(string Q, string L, string R, int Hero, string Name, int? Pos, int Hits, int Guessers, bool Own);

    const string SeenGame = "oblup";

    public override GameInfo Info { get; } = new(
        "oblup", "Як облупленого", "«Як облупленого»", GameGroup.Party, 2, MaxSeats,
        TickMs: 500, Start: StartMode.ByHost, Hidden: true, Rated: false,
        Options:
        [
            new GameOption("laps", "Кіл", [("auto", "само: удвох–вчотирьох 2, більше — 1"), ("1", "1 — кожен раз у центрі"),
                ("2", "2"), ("3", "3")], "auto"),
            new GameOption("seconds", "Час на ставку", [.. SecondChoices.Select(n => (Str(n), $"{n} с"))], Str(GuessSeconds)),
            new GameOption("spicy", "🌶 Гостре", [("off", "ні"), ("on", "так — дорослі питання")], "off"),
            Handicap.Option,
        ],
        Hint: "Хтось один таємно стає на шкалу між двома крайнощами, решта вгадує де. Вузька рамка — більше очок, широка — надійніше");

    static string Str(int n) => n.ToString(CultureInfo.InvariantCulture);

    int _lapsOpt;
    int _seconds = GuessSeconds;
    bool _spicy;

    /// <summary>Черга «в центрі» на партію: місця по колу, стільки кіл, скільки треба.</summary>
    readonly List<int> _order = [];
    int _laps;
    int _at;
    string _phase = PhaseBetween;
    DateTimeOffset _endsAt;
    bool _started;

    OblupQuestion[] _choices = [];
    OblupQuestion? _q;
    int? _heroPos;
    readonly int?[] _pos = new int?[MaxSeats];
    readonly string?[] _size = new string?[MaxSeats];

    readonly long[] _scores = new long[MaxSeats];
    Dictionary<int, double> _hc = [];
    readonly double[] _hcCarry = new double[MaxSeats];

    Row[]? _rows;
    int? _revealPos;
    string? _say;
    readonly List<Recap> _recap = [];
    /// <summary>Скільки очок набрав [хто вгадував, на кому] — для «Хто кого знає».</summary>
    readonly long[,] _pair = new long[MaxSeats, MaxSeats];
    /// <summary>Скільки рамок перетнуло на цьому героєві й скільки всього стояло — «📖 Відкрита книга» й «🎭 Темна конячка».</summary>
    readonly int[] _hitsOn = new int[MaxSeats], _triesOn = new int[MaxSeats];
    int[]? _winners;
    bool _touched;

    /// <summary>✍ Свої питання столу (живуть, доки стоїть стіл) і їхній лічильник для id.</summary>
    readonly List<OblupQuestion> _mine = [];
    int _mineSeq;
    /// <summary>Що вже грали за цим столом — щоб «Ще раз» не повторював; банк вичерпався — забуваємо банкові.</summary>
    readonly HashSet<string> _used = new(StringComparer.Ordinal);
    GameSeen? _seen;

    /// <summary>Місця — числами, як у «Скільки?»: «гравець восьмий» у чіп не влізе.</summary>
    public override string SeatName(int seat) => (seat + 1).ToString(CultureInfo.InvariantCulture);

    string Who(int seat) => seat >= 0 ? Ctx.NickOf(seat) ?? SeatName(seat) : "";

    int Hero => _at >= 0 && _at < _order.Count ? _order[_at] : -1;

    List<int> Seats() => [.. Enumerable.Range(0, MaxSeats).Where(Ctx.Seated)];

    IReadOnlyList<OblupQuestion> Bank() => Ctx.Services.GetService<OblupQuestions>()?.List ?? OblupBank.All;

    string KeyOf(int seat) => Ctx.NickOf(seat) is { Length: > 0 } n ? GameSeen.NickKey(n) : "";

    // ---------------------------------------------------------------------------------------
    // партія
    // ---------------------------------------------------------------------------------------

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _lapsOpt = options.TryGetValue("laps", out var l) && int.TryParse(l, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 3 ? n : 0;
        if (options.TryGetValue("seconds", out var s) && int.TryParse(s, CultureInfo.InvariantCulture, out var sec)
            && SecondChoices.Contains(sec)) _seconds = sec;
        _spicy = options.TryGetValue("spicy", out var sp) && sp == "on";
    }

    /// <summary>✍ Свої питання можна дописати ще в лобі, до «Почати».</summary>
    public override bool ActsInLobby => true;

    public override string? CanStart()
    {
        // Пам'ять «хто що бачив» фон читає заздалегідь — щоб перше «Почати» після рестарту вже її мало.
        (_seen ??= GameSeen.Of(Ctx.Services)).Prefetch(SeenGame);
        return Bank().Count < 2 && _mine.Count == 0 ? "Питання кудись поділись — партії не буде" : null;
    }

    public override void Start()
    {
        _started = true;
        _seen ??= GameSeen.Of(Ctx.Services);
        Array.Clear(_scores);
        Array.Clear(_pair);
        Array.Clear(_hitsOn);
        Array.Clear(_triesOn);
        _recap.Clear();
        _winners = null;
        _touched = false;

        var seated = Seats();
        _hc = Handicap.ForSeats(Ctx, Info.Id, seated, Info.Rated);
        Array.Clear(_hcCarry);
        if (Handicap.Note(Ctx, _hc) is { } note) Ctx.Log(note);

        // Хто перший у центрі — навмання, далі по колу місць: «Ще раз» не починає щоразу з господаря.
        _laps = _lapsOpt > 0 ? _lapsOpt : seated.Count <= 4 ? 2 : 1;
        _order.Clear();
        var first = seated.Count == 0 ? 0 : Ctx.Rng.Next(seated.Count);
        for (var lap = 0; lap < _laps; lap++)
            for (var i = 0; i < seated.Count; i++) _order.Add(seated[(first + i) % seated.Count]);
        _at = 0;

        if (Bank().Count < 2 && _mine.Count == 0)
        {
            _phase = PhaseDone;
            _winners = [];
            Ctx.Finish([], $"{Info.Title}: питання не знайшлись, партії не буде");
            return;
        }
        Open(Ctx.Clock.UtcNow);
    }

    /// <summary>Новий раунд: чистий стіл і коротке «у центрі — Оля».</summary>
    void Open(DateTimeOffset now)
    {
        _choices = [];
        _q = null;
        _heroPos = null;
        Array.Clear(_pos);
        Array.Clear(_size);
        _rows = null;
        _revealPos = null;
        _say = null;
        _phase = PhaseBetween;
        _endsAt = now.AddSeconds(BetweenSeconds);
    }

    /// <summary>Наступний, хто ще за столом; черга скінчилась — кінець партії.</summary>
    void Next(DateTimeOffset now)
    {
        do _at++;
        while (_at < _order.Count && !Ctx.Seated(_order[_at]));
        if (_at < _order.Count) Open(now);
        else Done();
    }

    /// <summary>
    /// Два питання на вибір героєві: своє питання столу (найдавніше дописане, не його) — одне в парі, щоб завжди
    /// лишався й банк; банкові — спершу ті, яких ніхто за столом не бачив або бачив найдавніше.
    /// </summary>
    OblupQuestion[] Choose(int hero)
    {
        var key = KeyOf(hero);
        var picked = new List<OblupQuestion>(2);
        var own = _mine.FirstOrDefault(q => !_used.Contains(q.Id) && q.Author != key);
        if (own is not null) picked.Add(own);

        var bank = Fresh();
        if (bank.Count < 2)
        {
            // Банк за цим столом зіграно до дна — забуваємо банкові, свої лишаються зіграними.
            _used.RemoveWhere(id => !id.StartsWith("u:", StringComparison.Ordinal));
            bank = Fresh();
        }
        for (var i = bank.Count - 1; i > 0; i--)
        {
            var j = Ctx.Rng.Next(i + 1);
            (bank[i], bank[j]) = (bank[j], bank[i]);
        }
        var seen = _seen?.Peek(SeenGame, GameSeen.Table(Ctx, MaxSeats)) ?? new Dictionary<string, GameSeen.Seen>();
        foreach (var q in GameSeen.Order(bank, q => q.Id, seen))
        {
            if (picked.Count == 2) break;
            picked.Add(q);
        }
        // Своє питання — не завжди ліворуч, інакше видно, чиє воно, ще до напису «✍».
        if (picked.Count == 2 && Ctx.Rng.Next(2) == 0) (picked[0], picked[1]) = (picked[1], picked[0]);
        return [.. picked];
    }

    List<OblupQuestion> Fresh() => [.. Bank().Where(q => (_spicy || !q.Spicy) && !_used.Contains(q.Id))];

    /// <summary>Питання обрано — ставки: герой ставить себе, решта — рамки. «Бачили» — з цієї миті й усі за столом.</summary>
    void Guess(DateTimeOffset now)
    {
        _q ??= _choices[0];
        _used.Add(_q.Id);
        if (_q.Author is null) _seen?.Remember(SeenGame, GameSeen.Table(Ctx, MaxSeats), [_q.Id], now);
        _phase = PhaseGuess;
        _endsAt = now.AddSeconds(_seconds);
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == "mine") return Mine(seat, payload);
        if (action is not ("pick" or "place")) return ActResult.Fail("Тут так не ходять");
        if (!Ctx.Seated(seat)) return ActResult.Fail("Ставлять ті, хто за столом");
        if (_phase == PhaseDone) return ActResult.Fail("Партію зіграно, тисни «Ану ще раз»");
        if (!_started) return ActResult.Fail("Чекаємо на «Почати»");
        var now = Ctx.Clock.UtcNow;

        if (action == "pick")
        {
            if (_phase != PhasePick) return ActResult.Fail("Зараз питання не обирають");
            if (seat != Hero) return ActResult.Fail("Питання обирає той, хто в центрі");
            var i = Int(payload, "i");
            if (i is not (0 or 1) || i >= _choices.Length) return ActResult.Fail("Нема такого питання");
            _q = _choices[i.Value];
            _endsAt = now;
            _touched = true;
            return ActResult.Done;
        }

        if (_phase == PhasePick) return ActResult.Fail(seat == Hero ? "Спершу обери питання" : "Мить — питання ще обирають");
        if (_phase != PhaseGuess) return ActResult.Fail("Зараз не ставлять — дивись на риску");
        if (now >= _endsAt) return ActResult.Fail("От халепа — час вийшов");
        if (Int(payload, "pos") is not { } pos) return ActResult.Fail("Тут треба місце на шкалі");
        if (seat == Hero)
        {
            _heroPos = Math.Clamp(pos, 0, Span);
            _touched = true;
            return ActResult.Done;
        }
        var box = BoxOf(payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("size", out var se)
            && se.ValueKind == JsonValueKind.String ? se.GetString() : null);
        if (box is null) return ActResult.Fail("Нема такої рамки");
        // Рамка цілком на шкалі: центр не ближче до краю, ніж пів рамки.
        _pos[seat] = Math.Clamp(pos, box.Half, Span - box.Half);
        _size[seat] = box.Key;
        _touched = true;
        return ActResult.Done;
    }

    /// <summary>Ціле з <c>{name: 42}</c> (дробове округлюємо — повзунок міг дати 41,7); не число — null.</summary>
    static int? Int(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetDouble(out var d) && !double.IsNaN(d) && Math.Abs(d) < 1e6 ? (int)Math.Round(d) : null;

    /// <summary>✍ Своє питання: до <see cref="MineMax"/> у черзі від ніка; <c>{del: id}</c> — прибрати своє.</summary>
    ActResult Mine(int seat, JsonElement payload)
    {
        if (!Ctx.Seated(seat)) return ActResult.Fail("Питання дописують ті, хто за столом");
        if (payload.ValueKind != JsonValueKind.Object) return ActResult.Fail("Тут треба питання й два краї");
        var key = KeyOf(seat);
        if (payload.TryGetProperty("del", out var de) && de.ValueKind == JsonValueKind.String)
        {
            var id = de.GetString();
            var removed = _mine.RemoveAll(q => q.Id == id && q.Author == key && !_used.Contains(q.Id));
            if (removed == 0) return ActResult.Fail("Такого твого питання в черзі нема");
            _touched = true;
            return ActResult.Accept("Прибрав твоє питання");
        }
        string Text(string name) => payload.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? OblupBank.Squeeze(e.GetString()) : "";
        var (q, l, r) = (Text("q"), Text("l"), Text("r"));
        if (q.Length < MineMinQ) return ActResult.Fail("Закоротке питання — допиши, про що саме");
        if (l.Length == 0 || r.Length == 0) return ActResult.Fail("Потрібні обидва краї шкали: що ліворуч і що праворуч");
        if (!OblupBank.Fits(q, l, r))
            return ActResult.Fail($"Задовго: питання — до {OblupBank.MaxQ} знаків, кожен край — до {OblupBank.MaxPole}");
        if (_mine.Count(x => x.Author == key && !_used.Contains(x.Id)) >= MineMax)
            return ActResult.Fail($"У черзі вже {MineMax} твоїх питання — хай спершу зіграють");
        _mineSeq++;
        _mine.Add(new OblupQuestion("u:" + Str(_mineSeq), q, l, r, [], false, key));
        _touched = true;
        return ActResult.Accept("Записав! Твоє питання дістанеться комусь у центрі — тобі воно не випаде");
    }

    public override TickResult Tick()
    {
        if (_phase == PhaseDone || !_started) return TickResult.None;
        var now = Ctx.Clock.UtcNow;
        // Усі поставили — чекати на таймер нема сенсу; перехід лишається тут, наступним рухом циклу.
        if (_phase == PhaseGuess && AllPlaced()) _endsAt = now;

        if (now < _endsAt)
        {
            if (!_touched) return TickResult.None;
            _touched = false;
            return TickResult.Both;
        }

        _touched = false;
        switch (_phase)
        {
            case PhaseBetween:
                if (!Ctx.Seated(Hero)) { Next(now); break; }
                _choices = Choose(Hero);
                if (_choices.Length == 0) { Done(); break; }
                if (_choices.Length == 1) { Guess(now); break; }
                _phase = PhasePick;
                _endsAt = now.AddSeconds(PickSeconds);
                break;
            case PhasePick:
                Guess(now);
                break;
            case PhaseGuess:
                Reveal(now);
                break;
            case PhaseReveal:
                Next(now);
                break;
        }
        return TickResult.Both;
    }

    bool AllPlaced()
    {
        var hero = Hero;
        if (_heroPos is null) return false;
        foreach (var s in Seats()) if (s != hero && _pos[s] is null) return false;
        return true;
    }

    /// <summary>
    /// Риска: місце героя крізь рамки всіх. Влучання — риска в рамці (межі включно), 🎯 — ще й за ±1 від центру.
    /// Герой не поставив себе — раунд без очок, рамки однаково показуємо.
    /// </summary>
    void Reveal(DateTimeOffset now)
    {
        var hero = Hero;
        var rows = new List<Row>();
        int hits = 0, guessers = 0;
        foreach (var s in Seats())
        {
            if (s == hero) continue;
            if (_pos[s] is not { } c || BoxOf(_size[s]) is not { } box) { rows.Add(new Row(s, null, null, false, false, 0)); continue; }
            guessers++;
            if (_heroPos is not { } p) { rows.Add(new Row(s, c, box.Key, false, false, 0)); continue; }
            var d = Math.Abs(p - c);
            var hit = d <= box.Half;
            var bull = hit && d <= BullRange;
            var got = Handicap.Scale(hit ? box.Points + (bull ? BullBonus : 0) : 0, Handicap.Of(_hc, s), ref _hcCarry[s]);
            _scores[s] += got;
            _pair[s, hero] += got;
            if (hit) hits++;
            rows.Add(new Row(s, c, box.Key, hit, bull, got));
        }
        if (_heroPos is not null && guessers > 0)
        {
            _hitsOn[hero] += hits;
            _triesOn[hero] += guessers;
        }
        _rows = [.. rows];
        _revealPos = _heroPos;
        _say = Flavor(hero, rows, hits, guessers);
        if (_q is { } q) _recap.Add(new Recap(q.Q, q.L, q.R, hero, Who(hero), _heroPos, hits, guessers, q.Author is not null));
        _phase = PhaseReveal;
        _endsAt = now.AddSeconds(RevealSeconds);
    }

    void Done()
    {
        _phase = PhaseDone;
        var seats = Seats();
        var best = seats.Count == 0 ? 0 : seats.Max(s => _scores[s]);
        _winners = best > 0 ? [.. seats.Where(s => _scores[s] == best)] : [];
        foreach (var s in seats)
            if (Shards(_scores[s]) is > 0 and var shards)
                // Номер партії в причині: «Ще раз» за тим самим столом — нова виплата, а не повтор старої.
                Ctx.Award(s, shards, $"points:{Str(Ctx.Round)}");
        var line = string.Join(", ", seats.OrderByDescending(s => _scores[s]).ThenBy(s => s).Select(s => $"{Who(s)} {_scores[s]}"));
        Ctx.Finish(_winners, best > 0 ? $"{Info.Title}: {line}" : $"{Info.Title}: {line} — ніхто ні разу не влучив",
            seats.ToDictionary(s => s, s => _scores[s]));
    }

    /// <summary>Скільки черепків за стільки очок партії.</summary>
    public static int Shards(long points) => points <= 0 ? 0 : (int)Math.Min(int.MaxValue, points / PointsPerShard);

    /// <summary>
    /// Хтось встав. Його рамки й черга в центрі зникають; пішов герой посеред раунду — раунд без очок. Менше двох — кінець.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (!_started || _phase == PhaseDone || seat is < 0 or >= MaxSeats) return;
        _pos[seat] = null;
        _size[seat] = null;
        for (var i = _order.Count - 1; i > _at; i--) if (_order[i] == seat) _order.RemoveAt(i);
        var left = Seats().Where(s => s != seat).ToList();
        if (left.Count < Info.MinPlayers)
        {
            _phase = PhaseDone;
            _winners = [];
            Ctx.Finish([], $"{Info.Title}: гравці розійшлись, партію не дограли");
            return;
        }
        if (seat == Hero && _phase is PhasePick or PhaseGuess)
        {
            _rows = [];
            _revealPos = null;
            _say = $"{Who(seat)} — уже не за столом. Цей раунд без очок.";
            _phase = PhaseReveal;
            _endsAt = Ctx.Clock.UtcNow.AddSeconds(VoidSeconds);
        }
        _touched = true;
    }

    // ---------------------------------------------------------------------------------------
    // види
    // ---------------------------------------------------------------------------------------

    static object Card(OblupQuestion q) => new { q = q.Q, l = q.L, r = q.R, own = q.Author is not null };

    public override object View(int? seat)
    {
        var me = seat is { } s0 && s0 >= 0 && s0 < MaxSeats ? s0 : -1;
        var hero = _started && _phase != PhaseDone ? Hero : -1;
        var key = me >= 0 ? KeyOf(me) : null;
        object? my = me < 0 || _phase is not (PhaseGuess or PhaseReveal) ? null
            : me == hero ? _heroPos is { } hp ? new { pos = hp, size = (string?)null } : null
            : _pos[me] is { } p ? new { pos = p, size = _size[me] } : null;
        return new
        {
            phase = _phase,
            round = _at + 1,
            of = _order.Count,
            hero,
            endsAt = _endsAt,
            seconds = _seconds,
            laps = _laps,
            // Два питання на вибір бачить лише той, хто в центрі.
            choices = me >= 0 && me == hero && _phase == PhasePick ? _choices.Select(Card).ToArray() : null,
            q = _q is { } q && _phase is PhaseGuess or PhaseReveal ? Card(q) : null,
            done = Placed(),
            my,
            sizes = Boxes.Select(b => new { key = b.Key, width = b.Width, points = b.Points, name = b.Name }).ToArray(),
            bull = BullBonus,
            span = Span,
            reveal = _phase != PhaseReveal || _rows is null ? null : new
            {
                pos = _revealPos,
                say = _say,
                rows = _rows.Select(r => new { seat = r.Seat, pos = r.Pos, size = r.Size, hit = r.Hit, bull = r.Bull, points = r.Points }).ToArray(),
            },
            scores = (long[])_scores.Clone(),
            handicap = _hc.Count == 0 ? null : Enumerable.Range(0, MaxSeats).Select(s => Math.Round(Handicap.Of(_hc, s), 2)).ToArray(),
            result = _winners is null ? null : new { winners = (int[])_winners.Clone(), scores = (long[])_scores.Clone() },
            recap = _phase != PhaseDone ? null : RecapView(),
            mine = new
            {
                list = _mine.Where(x => key is not null && x.Author == key && !_used.Contains(x.Id))
                    .Select(x => new { id = x.Id, q = x.Q, l = x.L, r = x.R }).ToArray(),
                max = MineMax,
                total = _mine.Count(x => !_used.Contains(x.Id)),
            },
        };
    }

    /// <summary>Хто вже поставив: герой — себе, решта — рамку.</summary>
    bool[] Placed()
    {
        var flags = new bool[MaxSeats];
        if (_phase != PhaseGuess) return flags;
        var hero = Hero;
        for (var s = 0; s < MaxSeats; s++) flags[s] = s == hero ? _heroPos is not null : _pos[s] is not null;
        return flags;
    }

    /// <summary>
    /// Підсумок партії: усі раунди, «Хто кого знає» (для кожного героя — хто набрав на ньому найбільше), «📖 Відкрита
    /// книга» й «🎭 Темна конячка» — найчастіше й найрідше влучали (лише коли є що порівнювати: двоє героїв від двох рамок).
    /// </summary>
    object RecapView()
    {
        var heroes = _recap.Select(r => r.Hero).Distinct().Order().ToList();
        var knows = new List<object>();
        foreach (var h in heroes)
        {
            long top = 0;
            for (var g = 0; g < MaxSeats; g++) top = Math.Max(top, _pair[g, h]);
            if (top <= 0) continue;
            var by = Enumerable.Range(0, MaxSeats).Where(g => _pair[g, h] == top).ToArray();
            knows.Add(new { hero = h, name = Who(h), by, names = by.Select(Who).ToArray(), points = top });
        }
        var rated = heroes.Where(h => _triesOn[h] >= 2).Select(h => (H: h, Rate: (double)_hitsOn[h] / _triesOn[h])).ToList();
        int? open = null, dark = null;
        if (rated.Count >= 2 && rated.Max(x => x.Rate) > rated.Min(x => x.Rate))
        {
            var hi = rated.Max(x => x.Rate);
            var lo = rated.Min(x => x.Rate);
            if (rated.Count(x => x.Rate == hi) == 1) open = rated.First(x => x.Rate == hi).H;
            if (rated.Count(x => x.Rate == lo) == 1) dark = rated.First(x => x.Rate == lo).H;
        }
        return new
        {
            rounds = _recap.Select(r => new { q = r.Q, l = r.L, r = r.R, hero = r.Hero, name = r.Name, pos = r.Pos, hits = r.Hits, of = r.Guessers, own = r.Own }).ToArray(),
            knows,
            open = open is { } o ? new { seat = o, name = Who(o), hits = _hitsOn[o], of = _triesOn[o] } : null,
            dark = dark is { } d ? new { seat = d, name = Who(d), hits = _hitsOn[d], of = _triesOn[d] } : null,
        };
    }

    /// <summary>Кадр — лише з новиною (галочка, зміна фази). Нічого прихованого: летить усій кімнаті.</summary>
    public override object? Frame() => new
    {
        round = _at + 1,
        phase = _phase,
        endsAt = _endsAt,
        done = Placed(),
        scores = (long[])_scores.Clone(),
    };

    // ---------------------------------------------------------------------------------------
    // Дядько Глек
    // ---------------------------------------------------------------------------------------
    // Ніки лише в називному — підметом або після тире: чужі імена ми відмінювати не вміємо. І без минулого часу
    // («поставив/поставила»): роду ніка ми не знаємо. {0} — герой, {1} — хто взяв найбільше, {2} — влучили, {3} — з скількох.

    static readonly string[] NoHero =
    [
        "{0}: позначки на шкалі нема — раунд без очок.",
        "Шкала порожня — {0} так і лишається загадкою. Без очок.",
    ];

    static readonly string[] NoGuess =
    [
        "Жодної рамки на столі — {0} лишається загадкою.",
        "Ніхто не наважився вгадати. {0} — неприступна фортеця.",
    ];

    static readonly string[] Bull =
    [
        "🎯 {1} — в яблучко! Оце знає як облупленого.",
        "🎯 {1} цілить точнісінько в риску. Шапки геть.",
        "🎯 В яблучко — {1}! Ви там часом не родичі?",
    ];

    static readonly string[] AllHit =
    [
        "{0} — відкрита книга: влучили всі.",
        "Усі в ціль! {0} — як на долоні.",
        "{0} нічого не приховає: риска перетнула всі рамки.",
    ];

    static readonly string[] NoneHit =
    [
        "{0} вміє дивувати: ніхто не влучив.",
        "Мимо всі. {0} — темна конячка цього столу.",
        "Ого! Таким {0} цей стіл ще не бачив.",
    ];

    static readonly string[] Narrow =
    [
        "{1} — вузькою рамкою в ціль. Сміливо й точно.",
        "Вузька рамка і влучання — {1} знає, що каже.",
    ];

    static readonly string[] Some =
    [
        "Влучили {2} з {3}. Найдорожче влучання — {1}.",
        "{2} з {3} у ціль, найбільше бере {1}.",
    ];

    static readonly string[] SoloHit = ["{1} — в ціль!", "Є влучання — {1} не підводить."];
    static readonly string[] SoloMiss = ["Мимо: {0} цього разу хитріше.", "Не вгадано — {0} ще має секрети."];

    string Flavor(int hero, List<Row> rows, int hits, int guessers)
    {
        string[] bank;
        var top = rows.Where(r => r.Hit).OrderByDescending(r => r.Points).ThenBy(r => r.Seat).FirstOrDefault();
        if (_heroPos is null) bank = NoHero;
        else if (guessers == 0) bank = NoGuess;
        else if (rows.Any(r => r.Bull)) { bank = Bull; top = rows.First(r => r.Bull); }
        else if (guessers == 1) bank = hits == 1 ? SoloHit : SoloMiss;
        else if (hits == guessers) bank = AllHit;
        else if (hits == 0) bank = NoneHit;
        else if (rows.FirstOrDefault(r => r.Hit && r.Size == "n") is { } narrow) { bank = Narrow; top = narrow; }
        else bank = Some;
        return string.Format(CultureInfo.InvariantCulture, bank[Ctx.Rng.Next(bank.Length)],
            Who(hero), top is null ? "" : Who(top.Seat), hits, guessers);
    }
}
