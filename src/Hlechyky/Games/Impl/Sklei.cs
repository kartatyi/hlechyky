using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Склей глек» (specs/sklei.md): картинку розбито на черепки — перетягни кожен на місце й поверни рівно. Хто склав
/// першим — той вище. Партія — три картинки поспіль, за місце в кожній очки, підсумок — сума.
/// <para>Сервер не бачить, де черепки лежать на столі в кожного: клієнт тягає їх сам і шле лише «поклав черепок k у
/// клітинку c з поворотом r, натиснувши поворот t разів». Сервер приймає тільки правильне (не туди — відмова, стан той
/// самий): так рахунок чесний, а вид маленький. Розбивка й розклад — із сіду картинки, однакові в усіх з тим самим N.</para>
/// <para>Режим вечірки (specs/party-minigame.md): одна картинка «звично» (16, з поворотами), 60 с, стеля 75 с;
/// scores — склав: 1000 − секунди, не склав — скільки черепків приросло.</para>
/// </summary>
public sealed class Sklei : Game, IPartyMinigame
{
    public const int TickMillis = 100;
    public const int Seats = 8;
    public const int ReadyMs = 3000, PauseMs = 6000;
    /// <summary>Після першого, хто склав, решті — ще стільки (але не довше за ліміт картинки).</summary>
    public const int AfterFirstMs = 60_000;
    public const int PicturesPerMatch = 3;
    public const int PartyReadyMs = 2000, PartyPlayMs = 60_000;
    /// <summary>
    /// Не частіше одного прийнятого черепка за стільки мс від одного гравця. Клієнт знає розв'язок (місце й поворот
    /// кожного черепка — інакше не намалює), тож без цього скрипт із консолі склеїть 16 за секунду. Рукою швидше
    /// ~0,4 с на черепок не вийде, а клієнт сам розносить свої put щонайменше на <see cref="PutGapMs"/> + запас.
    /// </summary>
    public const int PutGapMs = 300;
    /// <summary>Очки за місце в картинці; хто не склав — половина за своїм місцем (серед тих, хто теж не склав).</summary>
    public static readonly int[] Points = [10, 7, 5, 4, 3, 2, 1, 0];
    /// <summary>Скільки бот думає над черепком (мс) за рівнем: легкий, звичайний, сильний.</summary>
    public static readonly int[] BotMs = [6000, 3500, 2000];

    public const string PhLobby = "lobby", PhReady = "ready", PhGo = "go", PhPause = "pause", PhOver = "over";

    /// <summary>Складність: скільки черепків (N×N), чи крутяться, чи є контур-підказка, ліміт картинки.</summary>
    public sealed record Level(string Key, int N, bool Rotate, bool Hint, int LimitMs);

    public static readonly IReadOnlyList<Level> Levels =
    [
        new("easy", 3, false, true, 120_000),
        new("normal", 4, true, true, 180_000),
        new("hard", 5, true, false, 300_000),
        new("master", 6, true, false, 420_000),
    ];

    /// <summary>На телефоні — не більше 16 черепків: дрібніші пальцем не вхопиш (важко на телефоні = 16 без підказки).</summary>
    public const int PhoneMaxN = 4;

    static readonly GameOption LevelOpt = new("level", "Складність",
        [("easy", "легко · 9 черепків"), ("normal", "звично · 16, з поворотами"), ("hard", "важко · 25, без контуру"),
         ("master", "майстер · 36 (на ПК)")], "normal");

    static readonly GameOption PicsOpt = new("pics", "Картинки",
        [("all", "усі"), ("pots", "глеки й візерунки"), ("photo", "фото з «Де це?»"), ("friends", "малюнки друзів"), ("mine", "мої картинки")], "all");

    public override GameInfo Info { get; } = new(
        "sklei", "Склей глек", "«Склей глек»", GameGroup.Live, 1, Seats, TickMs: TickMillis,
        Start: StartMode.ByHost, Options: [LevelOpt, PicsOpt, LiveBots.LevelOption],
        Hint: "Глек розбили — склей черепки докупи швидше за всіх. Три картинки поспіль: розписи, фото, малюнки друзів чи своя. Самому — з 🤖 ботом");

    // ---------- налаштування ----------

    readonly SoloBot _solo = new();
    PartyMode? _party;
    Level _level = Levels[1];
    string _picsOpt = "all";
    public bool Party => _party is not null;

    public string Howto => "Перетягни кожен черепок на його місце й поверни рівно — приросте сам. "
        + "Тап по черепку — поворот (на ПК ще колесо чи правий клік); хто перший — той угорі";
    public int PartyCapMs => 75_000;
    public int PartyMin => 2;
    public int PartyMax => Seats;

    // ---------- стан партії ----------

    bool _started;
    string _ph = PhLobby;
    readonly bool[] _phone = new bool[Seats];
    readonly bool[] _plays = new bool[Seats];
    int[] _bots = [];
    bool _botGame;
    int _startHumans;
    LiveBots.Level _botLevel = LiveBots.Level.Normal;

    int _pics = PicturesPerMatch;
    int _picNo;
    SkleiPicture _pic = SkleiBuiltin.Picture(SkleiBuiltin.All[0].Id, SkleiBuiltin.All[0].Title);
    int _picSeed;
    readonly Dictionary<int, SkleiCut> _cuts = [];
    readonly HashSet<string> _used = new(StringComparer.Ordinal);

    readonly int[] _n = new int[Seats];
    readonly bool[][] _placed = new bool[Seats][];
    readonly int[] _count = new int[Seats];
    readonly int[] _doneMs = new int[Seats];
    readonly int[] _bad = new int[Seats];             // хибні повороти в цій картинці
    readonly bool[] _badAny = new bool[Seats];        // хоч один хибний поворот за партію
    readonly int[] _solvedPics = new int[Seats];      // скільки картинок партії склав
    readonly int[] _total = new int[Seats];
    readonly int[] _picPts = new int[Seats];
    readonly int[] _picPlace = new int[Seats];
    readonly List<object> _history = [];
    readonly DateTimeOffset[] _botNext = new DateTimeOffset[Seats];
    readonly DateTimeOffset[] _lastPut = new DateTimeOffset[Seats];

    DateTimeOffset _until, _goAt;
    bool _anyDone;
    bool _firstSaid;
    bool _dirty;
    int[] _winners = [];

    public string Phase => _ph;
    public int PictureNo => _picNo;
    public int PicturesTotal => _pics;
    public SkleiPicture Picture => _pic;
    public Level CurrentLevel => _level;
    public int Placed(int seat) => _count[seat];
    public int PiecesOf(int seat) => _n[seat] * _n[seat];
    public int Total(int seat) => _total[seat];
    public int DoneMs(int seat) => _doneMs[seat];
    public bool Plays(int seat) => seat is >= 0 and < Seats && _plays[seat];
    public IReadOnlyList<int> Bots => _bots;

    /// <summary>Розбивка поточної картинки для гравця з <paramref name="n"/>×<paramref name="n"/> черепками.</summary>
    public SkleiCut Cut(int n)
    {
        if (!_cuts.TryGetValue(n, out var cut)) _cuts[n] = cut = new SkleiCut(n, _picSeed, _level.Rotate);
        return cut;
    }

    int NFor(int seat) => _phone[seat] ? Math.Min(_level.N, PhoneMaxN) : _level.N;

    SkleiDeck? Deck => Ctx.Services.GetService(typeof(SkleiDeck)) as SkleiDeck;

    // ---------- лобі ----------

    public override bool ActsInLobby => true;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _solo.Configure(options);
        _party = PartyMode.Read(options);
        _level = Levels.FirstOrDefault(l => options.TryGetValue("level", out var v) && v == l.Key) ?? Levels[1];
        _picsOpt = options.TryGetValue("pics", out var p) && PicsOpt.Values.Any(x => x.Value == p) ? p : "all";
        if (_party is not null) { _level = Levels[1]; _picsOpt = "party"; }
    }

    public override string? CanStart() => _solo.CanStart(Ctx, Seats);

    int[] SoloBotSeats() => _solo.Active(Ctx, Seats) ? [.. Enumerable.Range(0, Seats).Where(s => !Ctx.Seated(s)).Take(1)] : [];

    bool IsBot(int seat) => Array.IndexOf(_bots, seat) >= 0 && !Ctx.Seated(seat);

    public override string? SeatBot(int seat) =>
        !Ctx.Seated(seat) && Array.IndexOf(_started ? _bots : SoloBotSeats(), seat) >= 0 ? LiveBots.Name : null;

    string Name(int seat) => SeatBot(seat) ?? Ctx.NickOf(seat) ?? SeatName(seat);

    public override void Start()
    {
        _started = true;
        _bots = _party is { } pm ? [.. pm.Bots.Where(s => s is >= 0 and < Seats && !Ctx.Seated(s))] : SoloBotSeats();
        _botGame = _bots.Length > 0 && _party is null;
        _botLevel = _party?.Level ?? _solo.Level;
        _startHumans = Enumerable.Range(0, Seats).Count(Ctx.Seated);
        for (var s = 0; s < Seats; s++)
        {
            _plays[s] = Ctx.Seated(s) || Array.IndexOf(_bots, s) >= 0;
            _total[s] = 0;
            _badAny[s] = false;
            _solvedPics[s] = 0;
            _lastPut[s] = DateTimeOffset.MinValue;
        }
        _pics = _party is null ? PicturesPerMatch : 1;
        _picNo = 0;
        _used.Clear();
        _history.Clear();
        _winners = [];
        _firstSaid = false;
        if (_party is null) Ctx.Say(SkleiLines.Start(Ctx.Rng));
        NextPicture();
    }

    // ---------- картинки ----------

    void NextPicture()
    {
        _picNo++;
        _pic = PickPicture();
        _used.Add(_pic.Key);
        _picSeed = Ctx.Rng.Next();
        _cuts.Clear();
        _anyDone = false;
        for (var s = 0; s < Seats; s++)
        {
            _n[s] = NFor(s);
            _placed[s] = new bool[_n[s] * _n[s]];
            _count[s] = 0;
            _doneMs[s] = -1;
            _bad[s] = 0;
            _picPts[s] = 0;
            _picPlace[s] = 0;
            if (_plays[s]) Cut(_n[s]);
        }
        _ph = PhReady;
        _until = Ctx.Clock.UtcNow.AddMilliseconds(_party is null ? ReadyMs : PartyReadyMs);
        _dirty = true;
    }

    /// <summary>
    /// Яку картинку клеїти: вид колоди за опцією (для «усі» — вагами: вбудовані частіше), всередині — навмання без
    /// повторів у партії. Порожній вид (нема фото на диску, нема малюнків) — вбудована: партія не ламається.
    /// </summary>
    SkleiPicture PickPicture()
    {
        var deck = Deck;
        var rng = Ctx.Rng;
        var mine = Enumerable.Range(0, Seats).Where(Ctx.Seated).Select(s => Auth.NickKey(Ctx.NickOf(s))).ToHashSet(StringComparer.Ordinal);
        var own = deck?.Own ?? [];
        var arts = deck?.Arts ?? [];
        var kinds = _picsOpt switch
        {
            "pots" => new[] { (SkleiKind.Builtin, 1) },
            "photo" => [(SkleiKind.Photo, 1)],
            "friends" => [(SkleiKind.Art, 1)],
            "mine" => [("mine", 1)],
            "party" => [(SkleiKind.Builtin, 1), (SkleiKind.Art, 1)],
            _ => [(SkleiKind.Builtin, 4), (SkleiKind.Photo, 3), (SkleiKind.Art, 2), (SkleiKind.Own, 2)],
        };
        var live = kinds.Where(k => k.Item1 switch
        {
            SkleiKind.Photo => (deck?.PhotoPlaces ?? 0) > 0,
            SkleiKind.Art => arts.Any(a => !_used.Contains(a.Picture.Key)),
            SkleiKind.Own => own.Any(o => !_used.Contains(o.Picture.Key)),
            "mine" => own.Any(o => mine.Contains(Auth.NickKey(o.Author)) && !_used.Contains(o.Picture.Key)),
            _ => true,
        }).ToList();
        while (live.Count > 0)
        {
            var sum = live.Sum(k => k.Item2);
            var roll = rng.Next(sum);
            var at = 0;
            while (roll >= live[at].Item2) { roll -= live[at].Item2; at++; }
            var kind = live[at].Item1;
            SkleiPicture? pic = kind switch
            {
                SkleiKind.Photo => deck?.Photo(rng, _used.Contains),
                SkleiKind.Art => Any(arts.Select(a => a.Picture), rng),
                SkleiKind.Own => Any(own.Select(o => o.Picture), rng),
                "mine" => Any(own.Where(o => mine.Contains(Auth.NickKey(o.Author))).Select(o => o.Picture), rng),
                _ => Any(SkleiBuiltin.All.Select(b => SkleiBuiltin.Picture(b.Id, b.Title)), rng),
            };
            if (pic is not null) return pic;
            live.RemoveAt(at);
        }
        // Усе вбудоване вже було (довга серія рематчів не скидає _used — скидає Start) — беремо будь-яку вбудовану.
        var b = SkleiBuiltin.All[rng.Next(SkleiBuiltin.All.Count)];
        return SkleiBuiltin.Picture(b.Id, b.Title);
    }

    SkleiPicture? Any(IEnumerable<SkleiPicture> all, Random rng)
    {
        var free = all.Where(p => !_used.Contains(p.Key)).ToList();
        return free.Count == 0 ? null : free[rng.Next(free.Count)];
    }

    // ---------- дії ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        switch (action)
        {
            case LiveBots.Toggle:
                if (_party is not null) return ActResult.Fail("У вечірці ботів садить вечірка");
                return _started && _ph != PhOver ? ActResult.Fail("Партія вже йде") : _solo.Switch(Ctx, seat, payload, Seats);
            case "dev":
                // Телефон чи ПК: на телефоні черепків не більше 16. Лише до старту — посеред партії це був би спосіб
                // з консолі ПК клеїти 16 замість 25; клієнт шле dev і після перепідключення, тож тоді мовчки «так».
                if (seat is < 0 or >= Seats) return ActResult.Fail("Ти тут не граєш");
                if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("phone", out var ph)
                    || ph.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return ActResult.Fail("Тут так не ходять");
                if (_started && _ph != PhOver) return ActResult.Done;
                if (_phone[seat] != ph.GetBoolean()) { _phone[seat] = ph.GetBoolean(); _dirty = true; }
                return ActResult.Done;
            case "put":
                return Put(seat, payload);
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    ActResult Put(int seat, JsonElement p)
    {
        if (!_started || _ph == PhOver) return ActResult.Fail("Партія ще не йде");
        if (seat is < 0 or >= Seats || !_plays[seat]) return ActResult.Fail("Ти тут не граєш");
        if (_ph != PhGo) return ActResult.Fail("Зачекай — ще не почали");
        if (_doneMs[seat] >= 0) return ActResult.Fail("Цю картинку вже склеєно");
        if (p.ValueKind != JsonValueKind.Object || !Int(p, "k", out var k) || !Int(p, "c", out var c)
            || !Int(p, "r", out var r) || !Int(p, "t", out var t))
            return ActResult.Fail("Тут так не ходять");
        var now = Ctx.Clock.UtcNow;
        if ((now - _lastPut[seat]).TotalMilliseconds < PutGapMs) return ActResult.Fail("Повільніше — клей не встигає");
        var res = Place(seat, k, c, r, t);
        if (res.Ok) _lastPut[seat] = now;
        return res;
    }

    static bool Int(JsonElement p, string name, out int v)
    {
        v = 0;
        return p.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out v);
    }

    /// <summary>Покласти черепок: людина (з Act) і бот (з Tick) ходять тут однаково.</summary>
    ActResult Place(int seat, int k, int c, int r, int t)
    {
        var pieces = _n[seat] * _n[seat];
        if (k < 0 || k >= pieces) return ActResult.Fail("Такого черепка нема");
        if (_placed[seat][k]) return ActResult.Fail("Цей черепок уже на місці");
        var cut = Cut(_n[seat]);
        // Тапи лише за годинниковою: стан черепка — r0 + t чвертей. Рівно — це 0, і клієнт каже те саме в r.
        if (t is < 0 or > 400 || (_level.Rotate ? (cut.R0(k) + t) % 4 != 0 : t != 0) || r != 0 || c != k)
            return ActResult.Fail("Черепок не туди — не приросте");
        _placed[seat][k] = true;
        _count[seat]++;
        if (_level.Rotate && t > cut.Needed(k)) { _bad[seat]++; _badAny[seat] = true; }
        _dirty = true;
        if (_count[seat] == pieces)
        {
            _doneMs[seat] = (int)Math.Max(0, (Ctx.Clock.UtcNow - _goAt).TotalMilliseconds);
            _solvedPics[seat]++;
            if (!_anyDone)
            {
                _anyDone = true;
                var cap = Ctx.Clock.UtcNow.AddMilliseconds(AfterFirstMs);
                if (_party is null && cap < _until) _until = cap;
                if (_party is null && !_firstSaid && !IsBot(seat))
                {
                    _firstSaid = true;
                    Ctx.Say(SkleiLines.First(Ctx.Rng, Name(seat), _doneMs[seat]));
                }
            }
        }
        return ActResult.Done;
    }

    // ---------- тик ----------

    public override TickResult Tick()
    {
        if (!_started || _ph == PhOver) return Flush();
        var now = Ctx.Clock.UtcNow;
        switch (_ph)
        {
            case PhReady:
                if (now < _until) break;
                _ph = PhGo;
                _goAt = now;
                _until = now.AddMilliseconds(_party is null ? _level.LimitMs : PartyPlayMs);
                var think = BotMs[LiveBots.Index(_botLevel)];
                // Перший черепок — після «погляду на картинку»; різні боти починають у різний час.
                foreach (var b in _bots) _botNext[b] = now.AddMilliseconds(think * (0.8 + Ctx.Rng.NextDouble() * 0.6));
                _dirty = true;
                break;
            case PhGo:
                BotsThink(now);
                if (now >= _until || Everyone()) return EndPicture();
                break;
            case PhPause:
                if (now < _until) break;
                NextPicture();
                break;
        }
        return Flush();
    }

    TickResult Flush()
    {
        if (!_dirty) return TickResult.None;
        _dirty = false;
        return TickResult.Both;
    }

    /// <summary>Склали всі, хто ще грає (бот і людина за столом). Хто встав — не чекаємо.</summary>
    bool Everyone()
    {
        var any = false;
        for (var s = 0; s < Seats; s++)
        {
            if (!_plays[s] || !(Ctx.Seated(s) || IsBot(s))) continue;
            any = true;
            if (_doneMs[s] < 0) return false;
        }
        return any;
    }

    /// <summary>
    /// Бот кладе правильний черепок раз на «подумати» (рівень × складність, ±30 %), тим самим <see cref="Place"/>, що й
    /// людина. Іноді (легкий частіше) задивляється — пауза вдвічі довша. Повороти бот робить рівно стільки, скільки треба.
    /// </summary>
    void BotsThink(DateTimeOffset now)
    {
        if (_bots.Length == 0) return;
        var lvl = LiveBots.Index(_botLevel);
        var scale = _level.N switch { 3 => 0.8, 4 => 1.0, 5 => 1.1, _ => 1.2 } * (_level.Rotate ? 1.0 : 0.85);
        foreach (var s in _bots)
        {
            if (!_plays[s] || Ctx.Seated(s) || _doneMs[s] >= 0 || now < _botNext[s]) continue;
            var cut = Cut(_n[s]);
            var left = Enumerable.Range(0, _placed[s].Length).Where(k => !_placed[s][k]).ToList();
            if (left.Count == 0) continue;
            var k = left[Ctx.Rng.Next(left.Count)];
            Place(s, k, k, 0, _level.Rotate ? cut.Needed(k) : 0);
            var ms = BotMs[lvl] * scale * (0.7 + Ctx.Rng.NextDouble() * 0.6);
            if (Ctx.Rng.NextDouble() < (lvl == 0 ? 0.15 : lvl == 1 ? 0.08 : 0.04)) ms *= 2;
            _botNext[s] = now.AddMilliseconds(ms);
        }
    }

    // ---------- кінці ----------

    /// <summary>Місця в картинці: хто склав — за часом; далі ті, хто ні, — за черепками (рівні — поділене місце).</summary>
    int[] PlacesNow(out bool[] finished)
    {
        var order = Enumerable.Range(0, Seats).Where(s => _plays[s]).ToList();
        var fin = new bool[Seats];
        foreach (var s in order) fin[s] = _doneMs[s] >= 0;
        finished = fin;
        // Телефон на «важко» клеїть 16, а ПК — 25 у тій самій гонці: тоді рівняємо за часом на черепок і часткою
        // прирослого. Коли черепків у всіх порівну — це той самий порядок, що й за часом / кількістю.
        var mixed = order.Select(s => _n[s]).Distinct().Count() > 1;
        double Rank(int s) => fin[s]
            ? (mixed ? (double)_doneMs[s] / (_n[s] * _n[s]) : _doneMs[s])
            : 1e12 - (mixed ? 1e6 * _count[s] / (_n[s] * _n[s]) : _count[s]);
        var places = new int[Seats];
        foreach (var s in order) places[s] = 1 + order.Count(o => Rank(o) < Rank(s));
        return places;
    }

    TickResult EndPicture()
    {
        if (_party is not null) return PartyOver();
        var places = PlacesNow(out var fin);
        var row = new List<object>();
        for (var s = 0; s < Seats; s++)
        {
            if (!_plays[s]) continue;
            var pts = _count[s] == 0 ? 0 : Points[Math.Min(places[s], Points.Length) - 1];
            if (!fin[s]) pts /= 2;
            _picPts[s] = pts;
            _picPlace[s] = places[s];
            _total[s] += pts;
            row.Add(new { seat = s, place = places[s], pts, ms = _doneMs[s], placed = _count[s], of = _n[s] * _n[s] });
        }
        _history.Add(new { pic = _pic.View(), rows = row });
        ReportSolved(fin);
        _dirty = true;
        if (_picNo >= _pics) return EndMatch();
        _ph = PhPause;
        _until = Ctx.Clock.UtcNow.AddMilliseconds(PauseMs);
        return Flush();
    }

    /// <summary>Склали картинку друга чи свою від когось — фон дасть автору +1 і порахує ачівки. Лише партія від двох людей.</summary>
    void ReportSolved(bool[] fin)
    {
        if (_party is not null || _botGame || _startHumans < 2 || _pic.Author is null || Deck is not { } deck) return;
        for (var s = 0; s < Seats; s++)
            if (fin[s] && Ctx.Seated(s) && Ctx.NickOf(s) is { } nick)
                deck.Report(new SkleiSolved(_pic.Key, _pic.Kind, _pic.Author, nick, $"{Ctx.RoomId}:{Ctx.Round}:{_picNo}"));
    }

    TickResult EndMatch()
    {
        _ph = PhOver;
        var playing = Enumerable.Range(0, Seats).Where(s => _plays[s]).ToArray();
        var scores = playing.ToDictionary(s => s, s => (long)_total[s]);
        var people = playing.Where(s => !IsBot(s) && Ctx.Seated(s)).ToArray();
        if (_botGame)
        {
            var bot = _bots[0];
            var human = people.FirstOrDefault(-1);
            if (human >= 0 && _total[human] > _total[bot])
            {
                _winners = [human];
                Ctx.Finish([human], Journal([human], playing), scores,
                    $"🏆 {Ctx.NickOf(human)} — перемога над {LiveBots.Of(_botLevel)} ботом, {_total[human]} : {_total[bot]}");
            }
            else
            {
                _winners = [];
                Ctx.Finish([], Journal([], playing), scores, human >= 0 && _total[human] == _total[bot]
                    ? $"Нічия з {LiveBots.Of(_botLevel)} ботом, {_total[bot]} : {_total[bot]}"
                    : $"🤖 Бот склеїв швидше, {_total[bot]} : {(human >= 0 ? _total[human] : 0)}");
            }
            return TickResult.Both;
        }
        var best = people.Length == 0 ? 0 : people.Max(s => _total[s]);
        _winners = [.. people.Where(s => _total[s] == best && best > 0)];
        if (_startHumans >= 2 && _level.N >= 5)
            foreach (var s in people)
                // _n[s], а не рівень: телефон на «важко» клеїть 16 (телефон/ПК після старту не міняється)
                if (_n[s] >= 5 && _solvedPics[s] == _pics && !_badAny[s]) Ctx.Award(s, 0, "ach:sklei-restorer");
        if (_winners.Length > 0) Ctx.Say(SkleiLines.End(Ctx.Rng, string.Join(", ", _winners.Select(Name))));
        Ctx.Finish(_winners, Journal(_winners, playing), scores);
        return TickResult.Both;
    }

    /// <summary>«Склей глек, 3 картинки: Оля 27 : Петро 19 : Ігор 5» — переможці першими.</summary>
    string Journal(int[] winners, int[] playing)
    {
        var order = winners.Concat(playing.Where(s => !winners.Contains(s)).OrderByDescending(s => _total[s]).ThenBy(s => s));
        var line = $"{Info.Title}, {_pics} {(_pics == 1 ? "картинка" : "картинки")}: {string.Join(" : ", order.Select(s => $"{Name(s)} {_total[s]}"))}";
        return winners.Length == 0 ? line + " — без переможця" : line;
    }

    // ---------- вечірка ----------

    /// <summary>Scores вечірки: склав — 1000 − секунди (ціла частина), ні — скільки черепків приросло. Хто не грає — 0.</summary>
    public IReadOnlyDictionary<int, long> PartyScores()
    {
        var r = new Dictionary<int, long>(Ctx.Players);
        for (var s = 0; s < Ctx.Players; s++)
            r[s] = s >= Seats || !_plays[s] ? 0 : _doneMs[s] >= 0 ? 1000 - _doneMs[s] / 1000 : _count[s];
        return r;
    }

    TickResult PartyOver()
    {
        _ph = PhOver;
        var scores = PartyScores();
        var best = scores.Count == 0 ? 0 : scores.Values.Max();
        _winners = [.. scores.Where(kv => kv.Value == best).Select(kv => kv.Key).Order()];
        var line = string.Join(" : ", scores.OrderByDescending(kv => kv.Value).Select(kv =>
            $"{Name(kv.Key)} {(kv.Value > 100 ? $"склеєно за {1000 - kv.Value} с" : $"{kv.Value} черепків")}"));
        Ctx.Finish(_winners, $"{Info.Title}: {line}", scores);
        return TickResult.Both;
    }

    // ---------- вихід ----------

    /// <summary>
    /// Устав посеред партії: місце просто більше не грає. Лишилось двоє людей і більше — клеять далі; менше (чи партія з
    /// ботом) — кінець: перемога тим, хто лишився, за очками.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (!_started || _ph == PhOver || _party is not null) return;
        var nick = Ctx.NickOf(seat);
        var left = Enumerable.Range(0, Seats).Where(s => s != seat && _plays[s] && Ctx.Seated(s)).ToArray();
        if (left.Length >= 2 && !_botGame)
        {
            Ctx.Log($"{Info.Title}: {nick} встав з-за столу — решта клеїть далі");
            _dirty = true;
            return;
        }
        _ph = PhOver;
        var best = left.Length == 0 ? 0 : left.Max(s => _total[s]);
        _winners = _botGame ? [] : [.. left.Where(s => _total[s] == best)];
        Ctx.Finish(_winners, $"{Info.Title}: {nick} встав з-за столу, партію не дограли",
            left.ToDictionary(s => s, s => (long)_total[s]));
    }

    // ---------- вид ----------

    public override object View(int? seat)
    {
        var me = seat is >= 0 and < Seats ? seat.Value : -1;
        var lobby = !_started;
        var n = me >= 0 && !lobby ? _n[me] : me >= 0 ? NFor(me) : _level.N;
        var now = Ctx.Clock.UtcNow;
        var playing = lobby ? [] : Enumerable.Range(0, Seats).Where(s => _plays[s]).ToArray();
        var places = lobby ? new int[Seats] : PlacesNow(out _);
        return new
        {
            ph = _ph,
            level = _level.Key,
            n,
            rotate = _level.Rotate,
            hint = _level.Hint,
            party = _party is not null,
            pics = _pics,
            picNo = _picNo,
            pic = lobby || _ph == PhReady && _picNo == 0 ? null : _pic.View(),
            cut = lobby ? null : Cut(n).View(),
            until = lobby || _ph == PhOver ? (DateTimeOffset?)null : _until,
            leftMs = lobby || _ph == PhOver ? 0 : (int)Math.Max(0, (_until - now).TotalMilliseconds),
            goAt = _ph is PhGo or PhPause || _ph == PhOver && _picNo > 0 ? _goAt : (DateTimeOffset?)null,
            me = me >= 0 && !lobby && _plays[me] ? new
            {
                placed = Enumerable.Range(0, _placed[me].Length).Where(k => _placed[me][k]).ToArray(),
                done = _doneMs[me] >= 0 ? _doneMs[me] : (int?)null,
                bad = _bad[me],
            } : null,
            phone = me >= 0 && _phone[me],
            players = playing.Select(s => new
            {
                seat = s,
                name = Name(s),
                bot = IsBot(s),
                placed = _count[s],
                of = _n[s] * _n[s],
                done = _doneMs[s] >= 0 ? _doneMs[s] : (int?)null,
                place = places[s],
                pts = _picPts[s],
                total = _total[s],
                gone = !IsBot(s) && !Ctx.Seated(s),
            }).ToArray(),
            history = _history.ToArray(),
            winners = _ph == PhOver ? (int[])_winners.Clone() : [],
            turn = (int?)null,
            botOffer = _party is null && _solo.Offer(Ctx, Seats),
            botWanted = _solo.Wanted,
            botLvl = _solo.LevelKey,
            bot = lobby ? SoloBotSeats() : _bots.Where(s => !Ctx.Seated(s)).ToArray(),
        };
    }

    /// <summary>Кадр — лише прогрес усіх (публічне): [місце, черепків, з, склав за мс або −1]. Ні розкладу, ні чужих наборів.</summary>
    public override object? Frame() => new
    {
        ph = _ph,
        picNo = _picNo,
        leftMs = !_started || _ph == PhOver ? 0 : (int)Math.Max(0, (_until - Ctx.Clock.UtcNow).TotalMilliseconds),
        p = Enumerable.Range(0, Seats).Where(s => _plays[s]).Select(s => new[] { s, _count[s], _n[s] * _n[s], _doneMs[s] }).ToArray(),
    };
}

/// <summary>Дядько Глек у балачці «Склей глек»: на початку, коли хтось склав першим, і в кінці. Не частіше.</summary>
public static class SkleiLines
{
    static readonly string[] StartLines =
    [
        "Розбили — склеїмо. Не вперше.",
        "Хто розбив — не питаю. Клей на столі, черепки теж.",
        "Мій глек колись так само склеювали. Досі тече, але ж гарний!",
        "Обережно, гострі краї. Кров на розписі — то вже інша картинка.",
    ];

    static readonly string[] FirstLines =
    [
        "{0}: склеєно за {1} — клей ще й не висох!",
        "{0} — готово, за {1}. Решта — не поспішайте, я почекаю.",
        "Оце руки! {0} — {1}, і жодної щілини.",
    ];

    static readonly string[] EndLines =
    [
        "{0} — головний реставратор столу. Музей кличе.",
        "Склеїли! {0} — найкращі руки. Наливайте в цей глек — не протече.",
        "{0} виграє. Решті — по шматку клею на згадку.",
    ];

    static string Of(string[] lines, Random rng) => lines[rng.Next(lines.Length)];

    public static string Start(Random rng) => Of(StartLines, rng);

    public static string First(Random rng, string name, int ms) =>
        string.Format(Of(FirstLines, rng), name, ms >= 60_000 ? $"{ms / 60_000} хв {ms / 1000 % 60} с" : $"{ms / 1000} с");

    public static string End(Random rng, string names) => string.Format(Of(EndLines, rng), names);
}
