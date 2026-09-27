using System.Globalization;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Правила однієї партії «Де це?»: стіл читає їх з опцій, тренування підставляє фіксовані.</summary>
public sealed record GeoRules(int Rounds, int Seconds, IReadOnlySet<string> Cats, string Level, string Hints, bool Solo)
{
    public const string LevelAll = "all", LevelEasy = "easy", LevelHard = "hard";
    public const string HintsFull = "full", HintsBorders = "borders", HintsNone = "none";

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

    public const string PhaseLobby = "lobby", PhaseBetween = "between", PhaseGuess = "guess", PhaseReveal = "reveal", PhaseDone = "done";

    public const string NoBank = "Банк місць порожній — гра ще не готова";
    public const string NoPhotos = "Фото ще качаються — спробуй за пів хвилини";

    sealed class Round(GeoPlace place, int photo)
    {
        public GeoPlace Place { get; } = place;
        public int Photo { get; } = photo;
        public string? Token { get; set; }
        public string? Url => Token is null ? null : $"/api/games/geo/{Token}.jpg";
    }

    sealed record Row(int Seat, int? X, int? Y, double? Km, int Points, bool Best, bool Bull, DateTimeOffset At);

    sealed record RevealData(int X, int Y, GeoPlace Place, GeoPhoto Photo, string? Say, IReadOnlyList<Row> Rows);

    sealed record Recap(string Name, string Region, string? Photo, int[] Best, double? Km, int Points);

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
    readonly bool[] _ready, _next, _left, _pinnedEver, _bullAsked;
    readonly long[] _scores;
    RevealData? _reveal;
    readonly List<Recap> _recap = [];
    int[]? _winners;
    /// <summary>Місця, що вже випадали за цим столом (між «Ще раз» теж): свіже — першим.</summary>
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    bool _dirty, _frameDirty;

    public GeoMatch(IRoomContext ctx, GeoRules rules, GeoPhotos? photos, int maxSeats, string title)
    {
        _ctx = ctx;
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
        _scores = new long[maxSeats];
        Array.Fill(_pinX, -1);
        Array.Fill(_pinY, -1);
    }

    public GeoRules Rules => _rules;
    public string Phase => _phase;

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
        Array.Clear(_scores);
        _rounds.Clear();
        _recap.Clear();
        _reveal = null;
        _winners = null;
        _round = 0;
        _dirty = _frameDirty = false;

        var pool = Pool(fresh: true);
        if (pool.Count < _rules.Rounds)
        {
            // Свіжого на цілу партію вже нема — забуваємо бачене й тасуємо все знову.
            _seen.Clear();
            pool = Pool(fresh: false);
        }
        var rng = _ctx.Rng;
        for (var i = pool.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        var take = Math.Min(_rules.Rounds, pool.Count);
        for (var i = 0; i < take; i++)
        {
            var ready = _photos!.ReadyPhotos(pool[i].Id);
            _rounds.Add(new Round(pool[i], ready[ready.Count == 1 ? 0 : rng.Next(ready.Count)]));
        }

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

    /// <summary>Новий раунд: чиста мапа, токен фото і дві секунди «готуйсь» (фото встигає завантажитись).</summary>
    void Open(int round, DateTimeOffset now)
    {
        _round = round;
        Array.Fill(_pinX, -1);
        Array.Fill(_pinY, -1);
        Array.Clear(_ready);
        Array.Clear(_next);
        _reveal = null;
        var r = _rounds[round - 1];
        r.Token ??= _photos!.Issue(r.Place.Id, r.Photo);
        _seen.Add(r.Place.Id);
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
            _ => ActResult.Fail("Тут так не ходять"),
        };
    }

    ActResult Guess(int seat, JsonElement payload)
    {
        if (_phase == PhaseDone) return ActResult.Fail("Партію зіграно, тисни «Ще раз»");
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
        if (_phase == PhaseDone) return ActResult.Fail("Партію зіграно, тисни «Ще раз»");
        if (_phase != PhaseGuess) return ActResult.Fail("Зараз не вгадують");
        if (_left[seat]) return ActResult.Fail("Ти вже встав з-за столу");
        if (_pinX[seat] < 0) return ActResult.Fail("Спершу постав шпильку на мапу");
        if (_ready[seat]) return ActResult.Fail("Уже готово");
        _ready[seat] = true;
        _frameDirty = true;             // ✓ у чіпі — кадром; «Готово» автора підтверджує відповідь на Act
        return ActResult.Done;
    }

    ActResult Next(int seat)
    {
        if (_phase == PhaseDone) return ActResult.Fail("Партію зіграно, тисни «Ще раз»");
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
                _phaseMs = _rules.Seconds * 1000;
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
            if (_pinX[s] < 0) { rows.Add(new Row(s, null, null, null, 0, false, false, DateTimeOffset.MaxValue)); continue; }
            var km = GeoScore.KmFromGrid(_pinX[s], _pinY[s], place.Lat, place.Lon);
            if (km < min) min = km;
            rows.Add(new Row(s, _pinX[s], _pinY[s], km, GeoScore.Points(km), false, km <= GeoScore.BullKm, _pinAt[s]));
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
        var say = GeoLines.Pick(_ctx.Rng, top is null ? null : _ctx.NickOf(top.Seat), top?.Km, top?.Points ?? 0, alone: present == 1);
        var best = rows.Where(r => r.Best || (present == 1 && r.Km is not null)).Select(r => r.Seat).ToArray();
        _recap.Add(new Recap(place.Name, place.Region, round.Url, best, top?.Km, top?.Points ?? 0));
        _reveal = new RevealData(tx, ty, place, place.Photos[round.Photo], say, rows);
        _phase = PhaseReveal;
        _phaseMs = RevealMs;
        _endsAt = now.AddMilliseconds(RevealMs);
        Array.Clear(_next);
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
                _ctx.Award(s, (int)Math.Min(int.MaxValue, shards), $"points:{_ctx.Round.ToString(CultureInfo.InvariantCulture)}");
            if (_scores[s] >= Expert && _rounds.Count >= ExpertRounds) _ctx.Award(s, 0, "ach:geo-20k");
        }
        _ctx.Finish(_winners, Journal(seats, best), scores);
    }

    string Journal(List<int> seats, long best)
    {
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
            seconds = _rules.Seconds,
            hints = _rules.Hints,
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
                }).ToArray(),
            } : null,
            scores = (long[])_scores.Clone(),
            left = Seats(_left),
            result = _winners is null ? null : new { winners = (int[])_winners.Clone(), scores = (long[])_scores.Clone() },
            recap = _phase != PhaseDone ? null : _recap.Select(r => new
            {
                name = r.Name,
                region = r.Region,
                photo = r.Photo,
                best = r.Best,
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
