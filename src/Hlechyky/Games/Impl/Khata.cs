using System.Globalization;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Здам хату» (specs/khata.md): на екрані — фото квартири й місто, більше нічого. Кожен пише, скільки, на його думку,
/// коштує оренда за місяць; що ближче до справжньої ціни — то більше балів, до <see cref="Max"/> за квартиру.
/// Квартири — з банку, який наповнюють самі гравці (<see cref="KhataStore"/>). Родич «Скільки?»: ті самі фази
/// <c>between → ask → reveal</c>, усі переходи — у <see cref="Tick"/>.
/// </summary>
public sealed class Khata : Game
{
    public const string GameId = "khata";
    /// <summary>Скільки квартир у партії, якщо господар не обрав інакше.</summary>
    public const int Flats = 5;
    public const int AskSeconds = 30;
    public const int RevealSeconds = 8;
    public const int BetweenSeconds = 3;
    public const int MaxSeats = 12;
    static readonly int[] FlatChoices = [3, 5, 7, 10];
    static readonly int[] SecondChoices = [20, 30, 45, 60];

    /// <summary>Максимум за квартиру.</summary>
    public const int Max = 1000;
    /// <summary>«В яблучко»: промах до 2 % — повні бали.</summary>
    public const double Bull = 0.02;
    /// <summary>Крутість спаду: бали = 1000 · e^(−3 · |ln(моє / правда)|) поза «яблучком».</summary>
    public const double Falloff = 3;
    /// <summary>Скільки балів партії коштує черепок (база; у гаманець іде × Economy:Boost).</summary>
    public const int PointsPerShard = 500;

    public const string PhaseBetween = "between";
    public const string PhaseAsk = "ask";
    public const string PhaseReveal = "reveal";
    public const string PhaseDone = "done";

    /// <summary>Рядок розкриття: чиє число, у скільки разів воно від правди (1 — точно) і бали.</summary>
    sealed record Row(int Seat, double Value, double Ratio, int Points);

    /// <summary>Рядок підсумку партії: квартира, правда й найкраще число; для стороннього оголошення — посилання на нього.</summary>
    sealed record Recap(string City, string Unit, string Per, int Price, string Photo, string? Link, int Best, double? Value, int Points,
        bool Inner = false);

    public override GameInfo Info { get; } = new(
        GameId, "Здам хату", "«Здам хату»", GameGroup.Party, 1, MaxSeats,
        TickMs: 1000, Start: StartMode.ByHost, Hidden: true, Rated: false,
        Options:
        [
            new GameOption("flats", "Квартир", [.. FlatChoices.Select(n => (Str(n), Str(n)))], Str(Flats)),
            new GameOption("seconds", "Час на відповідь", [.. SecondChoices.Select(n => (Str(n), $"{n} с"))], Str(AskSeconds)),
            new GameOption("region", "Міста", KhataRegions.Choices, KhataRegions.Any, Multi: true),
        ],
        Hint: "Справжні оголошення: бачиш фото житла й знаєш лише місто — вгадай, скільки воно коштує за місяць чи за ніч. Що ближче — то більше балів, до 1000 за квартиру");

    static string Str(int n) => n.ToString(CultureInfo.InvariantCulture);

    int _count = Flats;
    int _seconds = AskSeconds;
    IReadOnlySet<string>? _regions;

    readonly List<KhataFlat> _flats = [];
    readonly double?[] _answers = new double?[MaxSeats];
    readonly DateTimeOffset[] _answeredAt = new DateTimeOffset[MaxSeats];
    readonly long[] _scores = new long[MaxSeats];
    /// <summary>Бали за квартири, яких місце ще не бачило (і не саме додало): лише вони йдуть у черепки.</summary>
    readonly long[] _fresh = new long[MaxSeats];
    /// <summary>Що місце бачило ДО цієї партії — знімок на старті, бо сама партія теж записує «бачив».</summary>
    readonly HashSet<string>?[] _seenBefore = new HashSet<string>?[MaxSeats];
    /// <summary>Хто на якому місці почав партію: хтось устав — у розкритті й підсумку лишається нік, а не «гравець 2».</summary>
    readonly string?[] _names = new string?[MaxSeats];
    readonly List<Recap> _recap = [];

    int _at;
    string _phase = PhaseBetween;
    DateTimeOffset _endsAt;
    IReadOnlyList<Row>? _reveal;
    string? _say;
    int[]? _winners;
    bool _started;
    /// <summary>Хтось щойно написав число — на наступному тику треба розіслати види (галочки).</summary>
    bool _touched;

    KhataStore? _store;
    GameSeen? _seen;

    public override string SeatName(int seat) => (seat + 1).ToString(CultureInfo.InvariantCulture);

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("flats", out var f) && int.TryParse(f, CultureInfo.InvariantCulture, out var n)
            && FlatChoices.Contains(n)) _count = n;
        if (options.TryGetValue("seconds", out var s) && int.TryParse(s, CultureInfo.InvariantCulture, out var sec)
            && SecondChoices.Contains(sec)) _seconds = sec;
        if (options.TryGetValue("region", out var r)) _regions = KhataRegions.Parse(r);
    }

    KhataStore? Store => _store ??= Ctx.Services.GetService<KhataStore>();

    /// <summary>Порожній банк — чесно кажемо до старту, а не порожнім екраном після.</summary>
    public override string? CanStart()
    {
        (_seen ??= GameSeen.Of(Ctx.Services)).Prefetch(GameId);
        if (Store is not { } store || store.Live.Count == 0)
            return "У банку ще нема жодної квартири — додай першу кнопкою «🏠 Додати квартиру»";
        return store.CountFit(_regions) == 0 ? "Для цих міст квартир у банку ще нема — обери «Усі міста» або додай свою" : null;
    }

    public override void Start()
    {
        _started = true;
        _seen ??= GameSeen.Of(Ctx.Services);
        Array.Clear(_scores);
        Array.Clear(_fresh);
        Array.Clear(_answers);
        Array.Clear(_answeredAt);
        _recap.Clear();
        _flats.Clear();
        _flats.AddRange(Pick());
        for (var s = 0; s < MaxSeats; s++)
        {
            _names[s] = Ctx.Seated(s) ? Ctx.NickOf(s) : null;
            _seenBefore[s] = _names[s] is { Length: > 0 } nick ? [.. _seen.Peek(GameId, [GameSeen.NickKey(nick)]).Keys] : null;
        }
        _at = 0;
        _reveal = null;
        _say = null;
        _winners = null;
        _touched = false;

        if (_flats.Count == 0)
        {
            _phase = PhaseDone;
            _endsAt = Ctx.Clock.UtcNow;
            Ctx.Finish([], $"{Info.Title}: у банку нема квартир, партії не буде");
            return;
        }
        Open(Ctx.Clock.UtcNow);
    }

    /// <summary>
    /// Квартири на партію: спершу ті, яких ніхто за столом ще не бачив, далі бачені найдавніше (<see cref="GameSeen"/>).
    /// Квартири тих, хто сидить за столом, — у самий кінець черги: автор знає ціну, тож береться вона, лише коли інших бракує.
    /// </summary>
    List<KhataFlat> Pick()
    {
        var pool = Store?.Fit(_regions) ?? [];
        // Оголошення OLX — лише для столів, де всі свої (з акаунтами): гра внутрішня, гостеві з вулиці їх не показуємо.
        if (GuestAtTable()) pool.RemoveAll(f => f.Source == KhataSources.Olx);
        for (var i = pool.Count - 1; i > 0; i--)
        {
            var j = Ctx.Rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        var table = GameSeen.Table(Ctx, MaxSeats);
        var fresh = GameSeen.Order(pool, f => f.Key, _seen!.Peek(GameId, table));
        var others = fresh.Where(f => !table.Contains(GameSeen.NickKey(f.Author))).ToList();
        var mine = fresh.Where(f => table.Contains(GameSeen.NickKey(f.Author)));

        // Регіони — по черзі (Україна, Європа, світ; порядок випадковий): десять тисяч оголошень світу не мають затоптати
        // кількасот українських. У кожному регіоні — від найсвіжішого. Не більше однієї квартири з міста на партію, поки
        // міст вистачає: п'ять Парижів поспіль — це вже не гра, а завчання.
        var queues = others.GroupBy(f => f.Region, StringComparer.Ordinal).Select(g => new Queue<KhataFlat>(g)).ToList();
        for (var i = queues.Count - 1; i > 0; i--)
        {
            var j = Ctx.Rng.Next(i + 1);
            (queues[i], queues[j]) = (queues[j], queues[i]);
        }
        var picked = new List<KhataFlat>(_count);
        var cities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var spare = new List<KhataFlat>();
        while (picked.Count < _count && queues.Any(q => q.Count > 0))
            foreach (var q in queues)
            {
                if (picked.Count == _count) break;
                while (q.Count > 0)
                {
                    var f = q.Dequeue();
                    if (cities.Add(f.City)) { picked.Add(f); break; }
                    spare.Add(f);
                }
            }
        // Міст забракло — добираємо повторами, а коли й того мало — квартирами тих, хто за столом (автор свою пропустить).
        foreach (var f in spare.Concat(mine))
        {
            if (picked.Count == _count) break;
            picked.Add(f);
        }
        return picked;
    }

    /// <summary>Чи сидить за столом хтось без акаунта.</summary>
    bool GuestAtTable() => Enumerable.Range(0, MaxSeats).Any(s => Ctx.Seated(s) && Auth.IsGuestNick(Ctx.NickOf(s)));

    void Open(DateTimeOffset now)
    {
        Array.Clear(_answers);
        Array.Clear(_answeredAt);
        _reveal = null;
        _say = null;
        _phase = PhaseBetween;
        _endsAt = now.AddSeconds(BetweenSeconds);
    }

    KhataFlat? Current => _at >= 0 && _at < _flats.Count ? _flats[_at] : null;

    int Humans() => Enumerable.Range(0, MaxSeats).Count(Ctx.Seated);

    /// <summary>Це квартира того, хто сидить на місці: він знає ціну.</summary>
    bool Owns(int seat, KhataFlat flat) =>
        Ctx.NickOf(seat) is { Length: > 0 } nick && GameSeen.NickKey(nick) == GameSeen.NickKey(flat.Author);

    /// <summary>У компанії автор свою квартиру пропускає; сам на сам — грає (інакше нема в що), але без черепків за неї.</summary>
    bool SitsOut(int seat) => Current is { } flat && Owns(seat, flat) && Humans() > 1;

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action != "answer") return ActResult.Fail("Тут так не ходять");
        if (!_started || _flats.Count == 0) return ActResult.Fail("Чекаємо на «Почати»");
        if (_phase == PhaseDone) return ActResult.Fail("Партію зіграно, тисни «Ану ще раз»");
        if (_phase == PhaseReveal) return ActResult.Fail("Час на ціну вийшов");
        if (_phase != PhaseAsk) return ActResult.Fail("Мить — зараз буде квартира");
        if (Ctx.Clock.UtcNow >= _endsAt) return ActResult.Fail("От халепа — час вийшов");
        if (!Ctx.Seated(seat)) return ActResult.Fail("Ціну називають ті, хто за столом");
        if (SitsOut(seat)) return ActResult.Fail("Це твоя квартира — ти й так знаєш ціну 😉 Дивись, як мучаться інші");
        if (Number(payload) is not { } value || double.IsNaN(value) || double.IsInfinity(value)) return ActResult.Fail("Тут треба число: «15000», «15 000» або «15к»");
        if (value <= 0) return ActResult.Fail("Задарма хат не здають — ціна має бути більша за нуль");
        if (value > 1e12) return ActResult.Fail("Це вже не оренда, а викуп міста");

        _answers[seat] = value;
        _answeredAt[seat] = Ctx.Clock.UtcNow;
        _touched = true;
        // Реалтайм-кімната не розсилає види з Act, тож підтвердження гравцеві — оцей рядок.
        return ActResult.Accept($"Записав: {Money(value, Current!.Unit)}");
    }

    /// <summary>Число приймаємо і як <c>{value: 15000}</c>, і як голе число, і як рядок.</summary>
    static double? Number(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetDouble(out var n) => n,
        JsonValueKind.String => Text(payload.GetString()),
        JsonValueKind.Object when payload.TryGetProperty("value", out var v) => Number(v),
        _ => null,
    };

    /// <summary>
    /// Рядок із поля: пробіли між тисячами й знак валюти геть, кома — крапка, «к» / «k» / «тис» наприкінці — тисячі
    /// («15к», «1,5 тис»). Те саме читання — у khata.js.
    /// </summary>
    public static double? Text(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var clean = new string([.. raw.Where(ch => !char.IsWhiteSpace(ch))]).ToLowerInvariant()
            .Replace("грн", "").Replace("₴", "").Replace("$", "").Replace(',', '.').TrimEnd('.');
        var mult = 1.0;
        foreach (var suffix in (string[])["тис", "к", "k"])
            if (clean.EndsWith(suffix, StringComparison.Ordinal)) { clean = clean[..^suffix.Length].TrimEnd('.'); mult = 1000; break; }
        if (clean.Length == 0 || !clean.All(ch => char.IsAsciiDigit(ch) || ch == '.')) return null;
        return double.TryParse(clean, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) ? n * mult : null;
    }

    public override TickResult Tick()
    {
        if (_phase == PhaseDone || _flats.Count == 0) return TickResult.None;
        var now = Ctx.Clock.UtcNow;
        if (_phase == PhaseAsk && AllAnswered()) _endsAt = now;

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
                _phase = PhaseAsk;
                _endsAt = now.AddSeconds(_seconds);
                // «Бачив» — з тієї секунди, коли квартира з'явилась на екрані; до яких партія не дійшла — лишаються свіжими.
                _seen!.Remember(GameId, GameSeen.Table(Ctx, MaxSeats), [Current!.Key], now);
                break;
            case PhaseAsk:
                Reveal(now);
                break;
            case PhaseReveal:
                if (_at + 1 < _flats.Count) { _at++; Open(now); }
                else Done();
                break;
        }
        return TickResult.Both;
    }

    bool AllAnswered()
    {
        var need = 0;
        for (var s = 0; s < MaxSeats; s++)
        {
            if (!Ctx.Seated(s) || SitsOut(s)) continue;
            need++;
            if (_answers[s] is null) return false;
        }
        return need > 0;
    }

    /// <summary>
    /// Бали за число: у межах 2 % від правди — 1000; далі плавно вниз за тим, у скільки разів промах (удвічі менше й
    /// удвічі більше — однаково далеко): 10 % — близько 800, чверть — 540, удвічі — 130, утричі — 40.
    /// </summary>
    public static int Points(double guess, double price)
    {
        if (!(guess > 0) || !(price > 0) || double.IsInfinity(guess)) return 0;
        var off = Math.Abs(Math.Log(guess / price));
        var bull = Math.Log(1 + Bull);
        if (off <= bull + 1e-12) return Max;
        return (int)Math.Round(Max * Math.Exp(-Falloff * (off - bull)), MidpointRounding.AwayFromZero);
    }

    void Reveal(DateTimeOffset now)
    {
        var flat = Current!;
        var rows = new List<Row>();
        for (var s = 0; s < MaxSeats; s++)
            if (Ctx.Seated(s) && _answers[s] is { } v && !SitsOut(s))
                rows.Add(new Row(s, v, v / flat.Price, Points(v, flat.Price)));
        rows = [.. rows.OrderByDescending(r => r.Points).ThenBy(r => Math.Abs(Math.Log(r.Ratio))).ThenBy(r => _answeredAt[r.Seat]).ThenBy(r => r.Seat)];
        foreach (var r in rows)
        {
            _scores[r.Seat] += r.Points;
            // Черепки — лише за квартири, які бачиш уперше й не сам додав: напам'ять і зі своїх не нафармиш.
            if (_seenBefore[r.Seat] is { } seen && !seen.Contains(flat.Key) && !Owns(r.Seat, flat)) _fresh[r.Seat] += r.Points;
        }
        _recap.Add(new Recap(flat.City, flat.Unit, flat.Per, flat.Price, flat.Thumb, flat.Link,
            rows.Count == 0 ? -1 : rows[0].Seat, rows.Count == 0 ? null : rows[0].Value, rows.Count == 0 ? 0 : rows[0].Points,
            Inner: flat.Source == KhataSources.Olx));
        _reveal = rows;
        _say = Flavor(rows);
        _phase = PhaseReveal;
        _endsAt = now.AddSeconds(RevealSeconds);
    }

    void Done()
    {
        _phase = PhaseDone;
        var seats = Enumerable.Range(0, MaxSeats).Where(Ctx.Seated).ToList();
        var best = seats.Count == 0 ? 0 : seats.Max(s => _scores[s]);
        _winners = best > 0 ? [.. seats.Where(s => _scores[s] == best)] : [];
        foreach (var s in seats)
            if (Shards(_fresh[s]) is > 0 and var shards)
                // Номер партії в причині: «Ще раз» за тим самим столом — нова виплата, а не повтор старої.
                Ctx.Award(s, shards, $"points:{Ctx.Round.ToString(CultureInfo.InvariantCulture)}");
        var line = seats.Count == 0 ? "за столом уже ні душі"
            : string.Join(", ", seats.OrderByDescending(s => _scores[s]).ThenBy(s => s).Select(s => $"{Ctx.NickOf(s)} {Num(_scores[s])}"));
        Ctx.Finish(_winners, best > 0 || seats.Count == 0 ? $"{Info.Title}: {line}" : $"{Info.Title}: {line} — ніхто й близько не вгадав",
            seats.ToDictionary(s => s, s => _scores[s]));
    }

    /// <summary>Скільки черепків (база) за стільки «свіжих» балів партії.</summary>
    public static int Shards(long points) => points <= 0 ? 0 : (int)Math.Min(int.MaxValue, points / PointsPerShard);

    /// <summary>Хтось встав: решта грає далі (навіть один). Партія закінчується, лише коли за столом нікого.</summary>
    public override void OnLeave(int seat)
    {
        _answers[seat] = null;
        if (Enumerable.Range(0, MaxSeats).Any(s => s != seat && Ctx.Seated(s))) return;
        if (_phase == PhaseDone) return;
        _phase = PhaseDone;
        _winners = [];
        Ctx.Finish(_winners, $"{Info.Title}: гравці розійшлись, партію не дограли");
    }

    // ---------------------------------------------------------------------------------------
    // види
    // ---------------------------------------------------------------------------------------

    public override object View(int? seat)
    {
        var me = seat is { } s0 && s0 >= 0 && s0 < MaxSeats ? s0 : -1;
        var flat = Current;
        // У паузі перед квартирою ні фото, ні міста у виді нема — зазирнути в консоль раніше за інших не вийде.
        var shown = _started && flat is not null && _phase is PhaseAsk or PhaseReveal;
        // Оголошення OLX бачать лише ті, хто сидить за столом: гра внутрішня, а глядачем може зайти будь-хто.
        var inner = flat is { Source: KhataSources.Olx } && me < 0;
        var closed = shown && inner;
        if (closed) shown = false;
        var store = Store;
        return new
        {
            round = _at + 1,
            of = _started ? _flats.Count : _count,
            phase = _phase,
            closed,
            city = shown ? flat!.City : "",
            unit = shown ? flat!.Unit : null,
            // за місяць (оренда) чи за ніч (Airbnb) — стоїть біля поля поруч зі знаком валюти
            per = shown ? flat!.Per : null,
            photos = shown ? flat!.Urls.ToArray() : [],
            endsAt = _endsAt,
            seconds = _seconds,
            answered = Answered(),
            // Єдине своє в кожного місця: чужу ціну до розкриття не бачить ніхто.
            my = me >= 0 ? _answers[me] : null,
            // Моя квартира в компанії — я мовчу.
            own = shown && me >= 0 && SitsOut(me),
            reveal = _reveal is null || flat is null ? null : new
            {
                price = flat.Price,
                by = flat.Author,
                // Стороннє оголошення: звідки воно, що це за житло (до відповіді цього не видно) й посилання на нього.
                id = flat.Id,
                source = flat.Source,
                about = inner ? null : flat.About,
                link = inner ? null : flat.Link,
                say = _say,
                rows = _reveal.Select(r => new { seat = r.Seat, value = r.Value, ratio = r.Ratio, points = r.Points }).ToArray(),
            },
            scores = (long[])_scores.Clone(),
            names = (string?[])_names.Clone(),
            result = _winners is null ? null : new
            {
                winners = (int[])_winners.Clone(),
                scores = (long[])_scores.Clone(),
                // скільки балів кожного пішло в черепки (за квартири, бачені вперше)
                fresh = (long[])_fresh.Clone(),
            },
            recap = _phase != PhaseDone ? null : _recap.Select(r => new
            {
                city = r.City, unit = r.Unit, per = r.Per, price = r.Price,
                // глядачеві оголошення OLX лишаються закритими й у підсумку: місто й ціна — так, фото й посилання — ні
                photo = r.Inner && me < 0 ? null : r.Photo, link = r.Inner && me < 0 ? null : r.Link,
                best = r.Best >= 0 ? r.Best : (int?)null, value = r.Value, points = r.Points,
            }).ToArray(),
            // Скільки квартир у банку для обраних міст і всього — для лобі.
            bank = new { fit = store?.CountFit(_regions) ?? 0, total = store?.Live.Count ?? 0 },
        };
    }

    /// <summary>Кадр — лише разом із новиною (чиєсь число, зміна фази). Нічого прихованого — він летить усій кімнаті.</summary>
    public override object? Frame() => new
    {
        round = _at + 1,
        of = _flats.Count,
        phase = _phase,
        endsAt = _endsAt,
        answered = Answered(),
        scores = (long[])_scores.Clone(),
    };

    bool[] Answered()
    {
        var flags = new bool[MaxSeats];
        for (var s = 0; s < MaxSeats; s++) flags[s] = _answers[s] is not null;
        return flags;
    }

    // ---------------------------------------------------------------------------------------
    // тексти
    // ---------------------------------------------------------------------------------------

    /// <summary>Ціле з пробілами між тисячами: 17 500.</summary>
    public static string Num(double v) => Math.Round(v, MidpointRounding.AwayFromZero).ToString("#,##0", CultureInfo.InvariantCulture).Replace(",", " ");

    /// <summary>«17 500 ₴», «1 200 $».</summary>
    public static string Money(double v, string unit) => $"{Num(v)} {unit}";

    static readonly string[] Exact =
    [
        "В яблучко — {0}! Ріелтором, бува, не підробляєш?",
        "{0} — точнісінько. Таке враження, що сам там жив",
        "Копійка в копійку — {0}! Власник аж перехрестився",
    ];

    static readonly string[] Near =
    [
        "Найближче — {0}. Око-алмаз",
        "{0} — майже як в оголошенні. Торгуватись уміє",
        "Ціну чує {0}: промах — на чай власникові",
    ];

    static readonly string[] Far =
    [
        "Найближче — {0}, але й це ще не та хата",
        "{0} — найближче. Решта, схоже, шукала палац",
        "Навіть {0} промахується — ціни нині такі, що й Глек не вгадав би",
    ];

    static readonly string[] Wide =
    [
        "Ніхто й близько. З такими цінами жити вам у курені",
        "Усі мимо. Власник читає ваші числа й тихо плаче",
    ];

    static readonly string[] Silence =
    [
        "Усі мовчать. Хата так і стоїть порожня",
        "Жодної ціни. Мабуть, усі вже мають де жити",
    ];

    // Ціну назвав лише один — «найближче» і «решта» тут ні до чого.
    static readonly string[] NearAlone =
    [
        "{0} — майже як в оголошенні. Торгуватись уміє",
        "Ціну чує {0}: промах — на чай власникові",
        "Око-алмаз — {0}",
    ];

    static readonly string[] FarAlone =
    [
        "{0} ходить десь поруч, але це ще не та хата",
        "{0} — тепло, та власник хоче інакше",
        "Навіть {0} промахується — ціни нині такі, що й Глек не вгадав би",
    ];

    static readonly string[] WideAlone =
    [
        "{0} — мимо. З такими цінами — тільки в курінь",
        "{0} і близько не вгадує. Власник читає й тихо плаче",
    ];

    /// <summary>Слово Глека про раунд — на картці під таблицею. Нік завжди в називному.</summary>
    string Flavor(List<Row> rows)
    {
        if (rows.Count == 0) return Silence[Ctx.Rng.Next(Silence.Length)];
        var best = rows[0];
        var who = Ctx.NickOf(best.Seat) ?? SeatName(best.Seat);
        var alone = rows.Count == 1;
        var bank = best.Points >= Max ? Exact
            : best.Points >= 700 ? (alone ? NearAlone : Near)
            : best.Points >= 200 ? (alone ? FarAlone : Far)
            : alone ? WideAlone : Wide;
        return string.Format(CultureInfo.InvariantCulture, bank[Ctx.Rng.Next(bank.Length)], who);
    }
}
