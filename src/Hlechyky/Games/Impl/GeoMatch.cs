using System.Globalization;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Правила однієї партії «Де це?»: стіл читає їх з опцій, тренування підставляє фіксовані.</summary>
public sealed record GeoRules(int Rounds, int Seconds, IReadOnlySet<string> Cats, string Level, string Hints, bool Solo,
    string Mode = GeoRules.ModeClassic, bool Area = true)
{
    public const string LevelAll = "all", LevelEasy = "easy", LevelHard = "hard";
    public const string HintsFull = "full", HintsBorders = "borders", HintsNone = "none";
    /// <summary>Режими: звичайний і «⚡ Дуель на час» (15 с на фото, найшвидший влучний — +1000).</summary>
    public const string ModeClassic = "classic", ModeDuel = "duel";
    public const int DuelSeconds = 15, DuelKm = 50, DuelBonus = 1000;
    /// <summary>Підказка «область»: скільки очок раунду лишається тому, хто її взяв (−40 %).</summary>
    public const double AreaKeep = 0.6;

    public bool Duel => Mode == ModeDuel;
    /// <summary>Секунд на фото: у дуелі завжди 15, інакше — з опції.</summary>
    public int GuessSeconds => Duel ? DuelSeconds : Seconds;

    /// <summary>Тренування: п'ять фото по хвилині, усі місця, мапа з підказками.</summary>
    public static GeoRules Training => new(5, 60, GeoCats.Parse(GeoCats.All), LevelAll, HintsFull, Solo: true);

    public bool Fits(GeoPlace p) => Cats.Contains(p.Cat) && Level switch
    {
        LevelEasy => p.Difficulty == 1,
        LevelHard => p.Difficulty is 2 or 3,
        _ => true,
    };
}

/// <summary>
/// Спільне ядро «Де це?» і тренування: фази, шпильки, очки, розкриття, кінець. <see cref="Geo"/> і
/// <see cref="GeoSolo"/> — тонкі обгортки над ним, тож правила тестуються один раз і обидві гри гарантовано однакові.
/// <para>
/// Усі переходи фаз — лише в <see cref="Tick"/> за <c>Ctx.Clock</c> (як у «Скільки?»); <see cref="Act"/> тільки
/// міняє стан і ставить прапорці <c>_dirty</c> (вид) / <c>_frameDirty</c> (кадр), а найближчий тик (≤ 500 мс)
/// розсилає. Типовий тик — порівняння часу й двох прапорців, без алокацій і без жодного байта на дроті.
/// </para>
/// <para>
/// Приховане: до розкриття чужих шпильок і правди нема ні у виді, ні в кадрі — лише «хто поставив» і «хто
/// готовий». Фото — непрозорий токен <see cref="GeoPhotos"/>.
/// </para>
/// </summary>
public sealed class GeoMatch
{
    public const int BetweenMs = 2000;
    public const int RevealMs = 10_000;
    public const int PointsPerShard = 5000;
    /// <summary>Ачівка «Знавець України»: стільки очок за партію…</summary>
    public const int Expert = 20_000;
    /// <summary>…з щонайменше стількох раундів.</summary>
    public const int ExpertRounds = 5;
    /// <summary>Тренування потрапляє в Журнал лише від стількох очок.</summary>
    public const int SoloJournal = 15_000;
    /// <summary>
    /// Причина черепків за очки: <c>geo:&lt;номер партії&gt;</c>. Каркас веде її шляхом «нагород гри» — з денною
    /// стелею (<c>Economy:AwardDailyCap</c>, 30 на день), а не безстельовим <c>points:</c>, як у «Скільки?»:
    /// тут відповіді можна вивчити (банк скінченний, фото між партіями ті самі), і тренування з «Готово»/«Далі»
    /// одразу давало б ≈1200 🏺 на годину. Номер партії — щоб «Ще раз» платило знову, а повтор події — ні.
    /// </summary>
    public const string ShardReason = "geo";

    public const string PhaseLobby = "lobby", PhaseBetween = "between", PhaseGuess = "guess", PhaseReveal = "reveal", PhaseDone = "done";

    public const string NoBank = "Банк місць порожній — гра ще не готова";
    public const string NoPhotos = "Фото ще качаються — спробуй за пів хвилини";

    sealed class Round(GeoPlace place, int photo, string path)
    {
        public GeoPlace Place { get; } = place;
        public int Photo { get; } = photo;
        /// <summary>Файл фото на диску (кеш банку чи фото друзів) — лише для токенів, у вид не йде ніколи.</summary>
        public string Path { get; } = path;
        public string? Token { get; set; }
        public string? Url => Token is null ? null : $"/api/games/geo/{Token}.jpg";
        /// <summary>Запечатане фото (<see cref="GeoPhotos.IssueSealed"/>): тягнеться під час розкриття попереднього раунду.</summary>
        public string? Sealed { get; set; }
        public byte[]? Key { get; set; }
        public string? SealedUrl => Sealed is null ? null : $"/api/games/geo/{Sealed}.bin";
    }

    sealed record Row(int Seat, int? X, int? Y, double? Km, int Points, bool Best, bool Bull, DateTimeOffset At, bool Area = false, bool Fast = false);

    sealed record RevealData(int X, int Y, GeoPlace Place, GeoPhoto Photo, string? Say, IReadOnlyList<Row> Rows);

    /// <summary>Рядок «Як це було»: <c>Best</c> — 🏆 раунду (лише в компанії), <c>Top</c> — найближчий (і самому).</summary>
    public sealed record Recap(string Name, string Region, string? Photo, int[] Best, int? Top, double? Km, int Points);

    readonly IRoomContext _ctx;
    readonly GeoRules _rules;
    readonly GeoPhotos? _photos;
    readonly int _max;
    readonly string _title;

    string _phase = PhaseLobby;
    int _round;
    readonly List<Round> _rounds = [];
    DateTimeOffset _endsAt;
    int _phaseMs;
    readonly int[] _pinX, _pinY;
    readonly DateTimeOffset[] _pinAt;
    readonly bool[] _ready, _next, _left, _pinnedEver, _bullAsked, _area;
    readonly GeoSeen? _memory;
    readonly long[] _scores;
    /// <summary>
    /// Хто грав на кожному місці цієї партії. Каркас забуває нік, щойно людина встала (чи закрила вкладку вже
    /// після кінця), а підсумок і розкриття мають і далі казати «Оля», а не «місце 3».
    /// </summary>
    readonly string?[] _nicks;
    RevealData? _reveal;
    readonly List<Recap> _recap = [];
    int[]? _winners;
    /// <summary>Місця, що вже випадали за цим столом (між «Ще раз» теж): свіже — першим.</summary>
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    bool _dirty, _frameDirty;

    public GeoMatch(IRoomContext ctx, GeoRules rules, GeoPhotos? photos, int maxSeats, string title, GeoSeen? memory = null)
    {
        _ctx = ctx;
        _memory = memory;
        _rules = rules;
        _photos = photos;
        _max = maxSeats;
        _title = title;
        _pinX = new int[maxSeats];
        _pinY = new int[maxSeats];
        _pinAt = new DateTimeOffset[maxSeats];
        _ready = new bool[maxSeats];
        _next = new bool[maxSeats];
        _left = new bool[maxSeats];
        _pinnedEver = new bool[maxSeats];
        _bullAsked = new bool[maxSeats];
        _area = new bool[maxSeats];
        _scores = new long[maxSeats];
        _nicks = new string?[maxSeats];
        Array.Fill(_pinX, -1);
        Array.Fill(_pinY, -1);
    }

    public GeoRules Rules => _rules;
    public string Phase => _phase;
    /// <summary>«Де це? дня»: день (київський), з якого сіються ті самі п'ять фото всім. null — звичайна партія.</summary>
    public string? Day { get; set; }
    /// <summary>«Де це? дня»: нагорода дня (раз на добу) — від стількох очок, щоб «тицьнув навмання» не платило.</summary>
    public const int DailyRewardFrom = 2500;

    /// <summary>Квадратики раунду за відстанню: 🟩 ≤ 50 км, 🟨 ≤ 200, 🟧 ≤ 500, ⬛ далі чи без шпильки.</summary>
    public string Squares()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in _recap)
            sb.Append(r.Km switch { <= 50 => "🟩", <= 200 => "🟨", <= 500 => "🟧", _ => "⬛" });
        return sb.ToString();
    }

    /// <summary>Рядок «похвалитись» «Де це? дня»: «📍 Де це? дня №12: 18 450 🟩🟩🟨⬛🟩».</summary>
    public string Share() => $"📍 {_title} №{Days.Number(Day!).ToString(CultureInfo.InvariantCulture)}: {GeoText.Num(_scores[0])} {Squares()}";
    /// <summary>Ключ сіду «Де це? дня».</summary>
    public const string DailyPuzzle = "geo-daily";
    public int RoundsTotal => _rounds.Count;
    public long Total(int seat) => _scores[seat];
    public IReadOnlyList<Recap> RecapList => _recap;

    /// <summary>Ключі ніків за столом — так їх пам'ятає <see cref="GeoSeen"/>.</summary>
    List<string> NickKeys()
    {
        var keys = new List<string>(_max);
        for (var s = 0; s < _max; s++)
            if (Present(s) && _ctx.NickOf(s) is { } n && GeoSeen.NickKey(n) is var k && !keys.Contains(k)) keys.Add(k);
        return keys;
    }

    // ---------------------------------------------------------------------------------------
    // старт
    // ---------------------------------------------------------------------------------------

    /// <summary>Готові місця під правила столу; <paramref name="fresh"/> — без уже бачених за цим столом.</summary>
    List<GeoPlace> Pool(bool fresh)
    {
        var pool = new List<GeoPlace>();
        if (_photos is null) return pool;
        foreach (var p in _photos.Bank.Places)
            if (_rules.Fits(p) && _photos.Ready(p.Id) && (!fresh || !_seen.Contains(p.Id))) pool.Add(p);
        return pool;
    }

    /// <summary>Чи є в що грати: null — так, інакше текст відмови (і штовхнути завантажувач).</summary>
    public string? CanStart()
    {
        if (_photos is null || _photos.Bank.Places.Count == 0) return NoBank;
        if (Pool(fresh: false).Count > 0) return null;
        _photos.Poke();
        return NoPhotos;
    }

    public void Start()
    {
        Array.Fill(_pinX, -1);
        Array.Fill(_pinY, -1);
        Array.Clear(_pinAt);
        Array.Clear(_ready);
        Array.Clear(_next);
        Array.Clear(_left);
        Array.Clear(_pinnedEver);
        Array.Clear(_bullAsked);
        Array.Clear(_area);
        Array.Clear(_scores);
        _rounds.Clear();
        _recap.Clear();
        _reveal = null;
        _winners = null;
        _round = 0;
        _dirty = _frameDirty = false;
        for (var s = 0; s < _max; s++) _nicks[s] = _ctx.NickOf(s);

        var rng = _ctx.Rng;
        if (Day is not null) { DealDaily(); return; }
        var pool = Pool(fresh: true);
        Shuffle(pool, rng);
        // Пам'ять ніків (п. 51): серед свіжих за цим столом спершу ті, яких не бачив ніхто з гравців, далі —
        // бачені найдавніше. OrderBy стабільний — порядок однаково свіжих лишається з тасування.
        var last = _memory?.LastSeen(NickKeys());
        if (last is { Count: > 0 }) pool = [.. pool.OrderBy(p => last.TryGetValue(p.Id, out var at) ? at : DateTimeOffset.MinValue)];
        if (pool.Count < _rules.Rounds)
        {
            // Свіжого на цілу партію вже нема: беремо все свіже, а бракуюче добираємо з уже баченого (теж
            // навмання). Пам'ять починається наново з цієї партії — що не випало зараз, наступного разу знову
            // свіже. Інакше після скидання наступна партія повторювала б пів попередньої.
            var seen = new List<GeoPlace>();
            foreach (var p in Pool(fresh: false)) if (_seen.Contains(p.Id)) seen.Add(p);
            Shuffle(seen, rng);
            if (last is { Count: > 0 }) seen = [.. seen.OrderBy(p => last.TryGetValue(p.Id, out var at) ? at : DateTimeOffset.MinValue)];
            pool.AddRange(seen);
            _seen.Clear();
        }
        var take = Math.Min(_rules.Rounds, pool.Count);
        for (var i = 0; i < take; i++)
        {
            var ready = _photos!.ReadyPhotos(pool[i].Id);
            var idx = ready[ready.Count == 1 ? 0 : rng.Next(ready.Count)];
            _rounds.Add(new Round(pool[i], idx, _photos.PathFor(pool[i].Photos[idx])));
        }
        Begin();
    }

    /// <summary>
    /// «Де це? дня»: ті самі п'ять місць і фото всім за київську добу — сід від дня, пул — усі готові місця
    /// банку за id (порядок файла не впливає). Категорій і складності тут нема: день один на всіх.
    /// </summary>
    void DealDaily()
    {
        var pool = new List<GeoPlace>();
        if (_photos is not null)
            foreach (var p in _photos.Bank.Places) if (_photos.Ready(p.Id)) pool.Add(p);
        pool.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        var rng = new Random(Days.Seed(DailyPuzzle, Day!));
        Shuffle(pool, rng);
        for (var i = 0; i < Math.Min(_rules.Rounds, pool.Count); i++)
        {
            var ready = _photos!.ReadyPhotos(pool[i].Id);
            var idx = ready[rng.Next(ready.Count)];
            _rounds.Add(new Round(pool[i], idx, _photos.PathFor(pool[i].Photos[idx])));
        }
        Begin();
    }

    /// <summary>
    /// «Де це? дня» після перезаходу: зіграні раунди вже в <paramref name="recap"/> і <paramref name="total"/>
    /// (їх фото й правду гравець бачив), далі — з наступного незіграного. Усе зіграно — одразу підсумок, без
    /// повторного Finish: таблиця й нагорода вже були.
    /// </summary>
    public void Resume(IReadOnlyList<Recap> recap, long total)
    {
        if (_rounds.Count == 0 || recap.Count == 0) return;
        _recap.Clear();
        foreach (var r in recap) _recap.Add(r with { Photo = null });
        _scores[0] = total;
        if (total > 0) _pinnedEver[0] = true;
        var now = _ctx.Clock.UtcNow;
        if (_recap.Count >= _rounds.Count)
        {
            _round = _rounds.Count;
            _reveal = null;
            _phase = PhaseDone;
            _winners = total > 0 ? [0] : [];
            _endsAt = now;
            return;
        }
        Open(_recap.Count + 1, now);
    }

    void Begin()
    {
        var now = _ctx.Clock.UtcNow;
        if (_rounds.Count == 0)
        {
            _phase = PhaseDone;
            _endsAt = now;
            _winners = [];
            _photos?.Poke();
            _ctx.Finish([], _rules.Solo ? $"{_title}: фото ще качаються — спробуй за пів хвилини" : $"{_title}: фото ще не докачались, спробуй ще раз");
            return;
        }
        Open(1, now);
    }

    /// <summary>Тасування Фішера — Єйтса на <c>Ctx.Rng</c>: той самий сід — ті самі місця.</summary>
    static void Shuffle(List<GeoPlace> list, Random rng)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>Новий раунд: чиста мапа, токен фото і дві секунди «готуйсь» (фото встигає завантажитись).</summary>
    void Open(int round, DateTimeOffset now)
    {
        _round = round;
        Array.Fill(_pinX, -1);
        Array.Fill(_pinY, -1);
        Array.Clear(_ready);
        Array.Clear(_next);
        _reveal = null;
        Array.Clear(_area);
        var r = _rounds[round - 1];
        r.Token ??= _photos!.IssueFile(r.Path);
        _seen.Add(r.Place.Id);
        _memory?.Mark(NickKeys(), r.Place.Id, now);
        _phase = PhaseBetween;
        _phaseMs = BetweenMs;
        _endsAt = now.AddMilliseconds(BetweenMs);
    }

    // ---------------------------------------------------------------------------------------
    // дії
    // ---------------------------------------------------------------------------------------

    bool Present(int s) => !_left[s] && _ctx.Seated(s);

    public ActResult Act(int seat, string action, JsonElement payload)
    {
        if (seat < 0 || seat >= _max) return ActResult.Fail("Ти тут не граєш");
        return action switch
        {
            "guess" => Guess(seat, payload),
            "ready" => Ready(seat),
            "next" => Next(seat),
            "area" => Area(seat),
            _ => ActResult.Fail("Тут так не ходять"),
        };
    }

    ActResult Guess(int seat, JsonElement payload)
    {
        if (_phase == PhaseDone) return ActResult.Fail("Партію зіграно, тисни «Ану ще раз»");
        if (_phase != PhaseGuess) return ActResult.Fail("Зараз не вгадують");
        if (_left[seat]) return ActResult.Fail("Ти вже встав з-за столу");
        if (_ready[seat]) return ActResult.Fail("Ти вже натиснув «Готово»");
        if (!ReadXY(payload, out var x, out var y)) return ActResult.Fail("Тут треба дві цілі координати");
        if (!GeoMap.Inside(x, y)) return ActResult.Fail("Шпилька поза мапою");
        var first = _pinX[seat] < 0;
        _pinX[seat] = x;
        _pinY[seat] = y;
        // Час саме останньої шпильки: хто довше вагався, той і визначився пізніше.
        _pinAt[seat] = _ctx.Clock.UtcNow;
        _pinnedEver[seat] = true;
        // Видів на шпильку не шлемо: автор малює її сам одразу, а підтвердження — відповідь на цей Act. Вид
        // із «my» потрібен лише після F5 — і він там буде, бо вид будується зі стану. Усім — лише 📍 у кадрі,
        // і лише на першу шпильку раунду: пересування нікого, крім автора, не цікавить.
        if (first) _frameDirty = true;
        return ActResult.Done;
    }

    ActResult Ready(int seat)
    {
        if (_phase == PhaseDone) return ActResult.Fail("Партію зіграно, тисни «Ану ще раз»");
        if (_phase != PhaseGuess) return ActResult.Fail("Зараз не вгадують");
        if (_left[seat]) return ActResult.Fail("Ти вже встав з-за столу");
        if (_pinX[seat] < 0) return ActResult.Fail("Спершу постав шпильку на мапу");
        if (_ready[seat]) return ActResult.Fail("Уже готово");
        _ready[seat] = true;
        _frameDirty = true;             // ✓ у чіпі — кадром; «Готово» автора підтверджує відповідь на Act
        return ActResult.Done;
    }

    /// <summary>
    /// 💡 Підказка «область» (п. 53): мапа підсвічує область правди лише тому, хто попросив, а очки раунду
    /// йому — ×0,6. Назва області йде лише в його вид (<c>area</c>), чужим — нічого.
    /// </summary>
    ActResult Area(int seat)
    {
        if (_phase == PhaseDone) return ActResult.Fail("Партію зіграно, тисни «Ану ще раз»");
        if (_phase != PhaseGuess) return ActResult.Fail("Зараз не вгадують");
        if (!_rules.Area) return ActResult.Fail("За цим столом без підказок");
        if (_left[seat]) return ActResult.Fail("Ти вже встав з-за столу");
        if (_ready[seat]) return ActResult.Fail("Ти вже натиснув «Готово»");
        if (_area[seat]) return ActResult.Fail("Підказку вже взято");
        _area[seat] = true;
        _dirty = true;
        return ActResult.Done;
    }

    ActResult Next(int seat)
    {
        if (_phase == PhaseDone) return ActResult.Fail("Партію зіграно, тисни «Ану ще раз»");
        if (_phase != PhaseReveal) return ActResult.Fail("Зараз нічого пропускати");
        if (_left[seat]) return ActResult.Fail("Ти вже встав з-за столу");
        if (_next[seat]) return ActResult.Fail("Уже натиснув");
        _next[seat] = true;
        _frameDirty = true;
        return ActResult.Done;
    }

    /// <summary><c>{x, y}</c> — цілі числа або цілі в рядку («1234»): дробове, порожнє й решта — відмова.</summary>
    public static bool ReadXY(JsonElement payload, out int x, out int y)
    {
        x = y = 0;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("x", out var ex) && Int(ex, out x)
            && payload.TryGetProperty("y", out var ey) && Int(ey, out y);
    }

    static bool Int(JsonElement e, out int v)
    {
        v = 0;
        return e.ValueKind switch
        {
            JsonValueKind.Number => e.TryGetInt32(out v),
            JsonValueKind.String => int.TryParse(e.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v),
            _ => false,
        };
    }

    // ---------------------------------------------------------------------------------------
    // тик
    // ---------------------------------------------------------------------------------------

    public TickResult Tick()
    {
        if (_phase == PhaseDone || _phase == PhaseLobby) return TickResult.None;
        var now = _ctx.Clock.UtcNow;
        // Усі визначились — таймер не чекаємо: перехід цим же тиком (тобто за ≤ 500 мс після останнього «Готово»).
        if ((_phase == PhaseGuess && AllPresent(_ready)) || (_phase == PhaseReveal && AllPresent(_next))) _endsAt = now;

        if (now < _endsAt)
        {
            if (!_dirty && !_frameDirty) return TickResult.None;
            var r = new TickResult(_frameDirty, _dirty);
            _dirty = _frameDirty = false;
            return r;
        }

        switch (_phase)
        {
            case PhaseBetween:
                _phase = PhaseGuess;
                _phaseMs = _rules.GuessSeconds * 1000;
                _endsAt = now.AddMilliseconds(_phaseMs);
                break;
            case PhaseGuess:
                Reveal(now);
                break;
            case PhaseReveal:
                if (_round < _rounds.Count) Open(_round + 1, now);
                else Done();
                break;
        }
        _dirty = _frameDirty = false;
        return TickResult.Both;
    }

    bool AllPresent(bool[] flags)
    {
        var any = false;
        for (var s = 0; s < _max; s++)
        {
            if (!Present(s)) continue;
            if (!flags[s]) return false;
            any = true;
        }
        return any;
    }

    /// <summary>
    /// Розкриття: відстань кожної шпильки гаверсинусом, очки, значки 🎯 (≤ 1 км) і 🏆 (найближчі, лише в
    /// компанії), фраза Глека. Порядок рядків — за очками, далі хто раніше визначився, далі за місцем.
    /// </summary>
    void Reveal(DateTimeOffset now)
    {
        var round = _rounds[_round - 1];
        var place = round.Place;
        var (tx, ty) = GeoMap.Project(place.Lat, place.Lon);
        var rows = new List<Row>(_max);
        var present = 0;
        var min = double.MaxValue;
        for (var s = 0; s < _max; s++)
        {
            if (!Present(s)) continue;
            present++;
            if (_pinX[s] < 0) { rows.Add(new Row(s, null, null, null, 0, false, false, DateTimeOffset.MaxValue, _area[s])); continue; }
            var km = GeoScore.KmFromGrid(_pinX[s], _pinY[s], place.Lat, place.Lon);
            if (km < min) min = km;
            var pts = GeoScore.Points(km);
            if (_area[s]) pts = (int)Math.Round(pts * GeoRules.AreaKeep, MidpointRounding.AwayFromZero);
            rows.Add(new Row(s, _pinX[s], _pinY[s], km, pts, false, km <= GeoScore.BullKm, _pinAt[s], _area[s]));
        }
        // ⚡ Дуель на час: хто визначився першим (час останньої шпильки) і влучив ближче 50 км — +1000.
        // Самому не дається: «першим з одного» — не перегони.
        if (_rules.Duel && present > 1)
        {
            var first = DateTimeOffset.MaxValue;
            foreach (var r in rows) if (r.Km is { } k && k <= GeoRules.DuelKm && r.At < first) first = r.At;
            if (first != DateTimeOffset.MaxValue)
                for (var i = 0; i < rows.Count; i++)
                    if (rows[i].Km is { } k && k <= GeoRules.DuelKm && rows[i].At == first)
                        rows[i] = rows[i] with { Points = rows[i].Points + GeoRules.DuelBonus, Fast = true };
        }
        // 🏆 найближчим — з допуском в один метр; самому — ні: «найближчий з одного» — не заслуга.
        if (present > 1)
            for (var i = 0; i < rows.Count; i++)
                if (rows[i].Km is { } k && k - min <= 0.001) rows[i] = rows[i] with { Best = true };
        rows.Sort((a, b) =>
        {
            if (a.Km is null != b.Km is null) return a.Km is null ? 1 : -1;
            var c = b.Points.CompareTo(a.Points);
            if (c != 0) return c;
            c = a.At.CompareTo(b.At);
            return c != 0 ? c : a.Seat.CompareTo(b.Seat);
        });
        foreach (var r in rows)
        {
            _scores[r.Seat] += r.Points;
            if (r.Bull && !_bullAsked[r.Seat])
            {
                _bullAsked[r.Seat] = true;
                _ctx.Award(r.Seat, 0, "ach:geo-bull");
            }
        }
        var top = rows.Count > 0 && rows[0].Km is not null ? rows[0] : null;
        // Відстань у фразі — та сама, що в рядку розкриття (до 0,1 км), інакше Глек казав би «233 км», а рядок — «234 км».
        var topKm = top?.Km is { } tk ? Math.Round(tk, 1, MidpointRounding.AwayFromZero) : (double?)null;
        var say = GeoLines.Pick(_ctx.Rng, top is null ? null : _ctx.NickOf(top.Seat), topKm, top?.Points ?? 0, alone: present == 1);
        // 🏆 у «Як це було» — рівно ті, що в рядках розкриття (самому — нікого); найближчий окремо, щоб
        // підсумок і самому, і вдвох-після-того-як-хтось-устав казав, чия це відстань.
        var best = rows.Where(r => r.Best).Select(r => r.Seat).ToArray();
        _recap.Add(new Recap(place.Name, place.Region, round.Url, best, top?.Seat, top?.Km, top?.Points ?? 0));
        _reveal = new RevealData(tx, ty, place, place.Photos[round.Photo], say, rows);
        _phase = PhaseReveal;
        _phaseMs = RevealMs;
        _endsAt = now.AddMilliseconds(RevealMs);
        Array.Clear(_next);
        // Фото без очікування (п. 50): наступне тягнеться зараз, запечатаним; ключ прийде у «Готуйсь».
        if (_round < _rounds.Count && _photos is not null)
        {
            var nx = _rounds[_round];
            (nx.Sealed, nx.Key) = _photos.IssueSealed(nx.Path);
        }
    }

    /// <summary>
    /// Кінець партії: переможці — найбільша сума серед присутніх (нуль у всіх — нічия); очки в таблицю тим, хто
    /// хоч раз ставив шпильку; черепок за кожні <see cref="PointsPerShard"/> очок; рядок у Журнал.
    /// </summary>
    void Done()
    {
        _phase = PhaseDone;
        var seats = new List<int>();
        for (var s = 0; s < _max; s++) if (Present(s)) seats.Add(s);
        if (seats.Count == 0)
        {
            _winners = [];
            _ctx.Finish([], $"{_title}: гравці розійшлись, партію не дограли");
            return;
        }
        var best = seats.Max(s => _scores[s]);
        _winners = best > 0 ? [.. seats.Where(s => _scores[s] == best)] : [];
        var scores = seats.ToDictionary(s => s, s => _scores[s]);
        foreach (var s in seats)
        {
            if (_pinnedEver[s]) _ctx.Score(s, _scores[s]);
            if (_scores[s] / PointsPerShard is > 0 and var shards)
                // Номер партії в причині: «Ще раз» за тим самим столом — нова виплата, а не повтор старої.
                _ctx.Award(s, (int)Math.Min(int.MaxValue, shards), $"{ShardReason}:{_ctx.Round.ToString(CultureInfo.InvariantCulture)}");
            if (_scores[s] >= Expert && _rounds.Count >= ExpertRounds) _ctx.Award(s, 0, "ach:geo-20k");
            if (Day is not null && _scores[s] >= DailyRewardFrom) _ctx.Award(s, 0, "daily:" + DailyPuzzle);
        }
        _ctx.Finish(_winners, Journal(seats, best), scores);
    }

    string Journal(List<int> seats, long best)
    {
        if (Day is not null) return $"{_title}: {_ctx.NickOf(seats[0])} — {GeoText.Num(_scores[seats[0]])} {Squares()}";
        if (_rules.Solo)
        {
            var s = seats[0];
            // «набрав»/«набрала» — роду ніка не знаємо, тож без дієслова
            return _scores[s] >= SoloJournal ? $"Де це?: {_ctx.NickOf(s)} — {GeoText.Num(_scores[s])} очок у тренуванні" : "";
        }
        var line = string.Join(", ", seats.OrderByDescending(s => _scores[s]).ThenBy(s => s)
            .Select(s => $"{_ctx.NickOf(s)} {GeoText.Num(_scores[s])}"));
        if (best <= 0) return $"{_title}: {line} — ніхто нікуди не влучив";
        if (seats.Count == 1) return $"{_title}: {line}";
        var names = _winners!.Select(s => _ctx.NickOf(s)).ToList();
        return $"{_title}: {line} — найкраще око: {string.Join(" і ", names)}";
    }

    /// <summary>
    /// Хтось встав посеред партії: його шпилька цього раунду скасовується, решта грає далі (навіть один — далі
    /// сам). Нікого не лишилось — кінець без очок.
    /// </summary>
    public void OnLeave(int seat)
    {
        if (seat < 0 || seat >= _max || _phase == PhaseDone || _phase == PhaseLobby) return;
        _left[seat] = true;
        _pinX[seat] = _pinY[seat] = -1;
        _ready[seat] = _next[seat] = false;
        _dirty = _frameDirty = true;
        for (var s = 0; s < _max; s++) if (s != seat && Present(s)) return;
        _phase = PhaseDone;
        _winners = [];
        _ctx.Finish([], $"{_title}: гравці розійшлись, партію не дограли");
    }

    // ---------------------------------------------------------------------------------------
    // вид і кадр
    // ---------------------------------------------------------------------------------------

    int[] Seats(bool[] flags)
    {
        var n = 0;
        for (var s = 0; s < _max; s++) if (flags[s]) n++;
        var a = new int[n];
        n = 0;
        for (var s = 0; s < _max; s++) if (flags[s]) a[n++] = s;
        return a;
    }

    int[] Pinned()
    {
        var n = 0;
        for (var s = 0; s < _max; s++) if (_pinX[s] >= 0) n++;
        var a = new int[n];
        n = 0;
        for (var s = 0; s < _max; s++) if (_pinX[s] >= 0) a[n++] = s;
        return a;
    }

    bool Timed => _phase is PhaseBetween or PhaseGuess or PhaseReveal;

    public object View(int? seat)
    {
        var me = seat is { } s && s >= 0 && s < _max ? s : -1;
        var guess = _phase == PhaseGuess;
        var round = _round >= 1 && _round <= _rounds.Count ? _rounds[_round - 1] : null;
        return new
        {
            phase = _phase,
            round = _round,
            rounds = _rounds.Count,
            endsAt = Timed ? _endsAt : (DateTimeOffset?)null,
            phaseMs = Timed ? _phaseMs : 0,
            seconds = _rules.GuessSeconds,
            hints = _rules.Hints,
            mode = _rules.Mode,
            areaOn = _rules.Area,
            // 💡 область правди — лише тому, хто взяв підказку, і лише поки вгадують (на розкритті вона й так видна)
            area = guess && me >= 0 && _area[me] && round is not null ? round.Place.Region : null,
            // запечатане наступне фото: під час розкриття — що тягнути, у «Готуйсь» — ключ до вже стягнутого
            pre = _phase == PhaseReveal && _round < _rounds.Count ? _rounds[_round].SealedUrl : null,
            seal = _phase == PhaseBetween && round?.Key is { } key ? new { url = round.SealedUrl, key = Convert.ToBase64String(key) } : null,
            day = Day,
            share = Day is not null && _phase == PhaseDone ? Share() : null,
            // Фото — лише токен. У підсумку партії (done) лишається останнє: розкриття вже публічне.
            photo = _phase == PhaseLobby ? null : round?.Url,
            pinned = guess ? Pinned() : [],
            ready = guess ? Seats(_ready) : [],
            next = _phase == PhaseReveal ? Seats(_next) : [],
            // Єдине своє у виді: власна шпилька, і лише поки вгадують. Глядачу — нічого.
            my = guess && me >= 0 && _pinX[me] >= 0 ? new { x = _pinX[me], y = _pinY[me], ready = _ready[me] } : null,
            reveal = _reveal is { } rv && _phase is PhaseReveal or PhaseDone ? new
            {
                x = rv.X,
                y = rv.Y,
                lat = rv.Place.Lat,
                lon = rv.Place.Lon,
                name = rv.Place.Name,
                region = rv.Place.Region,
                cat = rv.Place.Cat,
                wikidata = rv.Place.Wikidata,
                photo = new { title = rv.Photo.Title, author = rv.Photo.Author, license = rv.Photo.License, licenseUrl = rv.Photo.LicenseUrl, page = rv.Photo.Page },
                say = rv.Say,
                rows = rv.Rows.Select(r => new
                {
                    seat = r.Seat,
                    x = r.X,
                    y = r.Y,
                    km = r.Km is { } k ? Math.Round(k, 1, MidpointRounding.AwayFromZero) : (double?)null,
                    points = r.Points,
                    best = r.Best,
                    bull = r.Bull,
                    area = r.Area,
                    fast = r.Fast,
                }).ToArray(),
            } : null,
            scores = (long[])_scores.Clone(),
            left = Seats(_left),
            // лише для розкриття й підсумку: там імена потрібні й тих, хто вже встав; у guess — зайві байти
            nicks = _phase is PhaseReveal or PhaseDone ? (string?[])_nicks.Clone() : null,
            result = _winners is null ? null : new { winners = (int[])_winners.Clone(), scores = (long[])_scores.Clone() },
            recap = _phase != PhaseDone ? null : _recap.Select(r => new
            {
                name = r.Name,
                region = r.Region,
                photo = r.Photo,
                best = r.Best,
                top = r.Top,
                km = r.Km is { } k ? Math.Round(k, 1, MidpointRounding.AwayFromZero) : (double?)null,
                points = r.Points,
            }).ToArray(),
            turn = (int?)null,
        };
    }

    /// <summary>Кадр усій кімнаті: фаза, раунд, кінець фази й «хто поставив / готовий / далі». Координат — ніколи.</summary>
    public object Frame() => new
    {
        ph = _phase,
        r = _round,
        ends = Timed ? _endsAt : (DateTimeOffset?)null,
        pin = _phase == PhaseGuess ? Pinned() : [],
        rdy = _phase == PhaseGuess ? Seats(_ready) : [],
        nxt = _phase == PhaseReveal ? Seats(_next) : [],
    };
}
