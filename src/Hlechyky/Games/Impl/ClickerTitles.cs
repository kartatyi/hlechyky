using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Звання гончаря: ключ, значок, назва, за що дають і рід (<see cref="Clicker.TitleTop"/>, <see cref="Clicker.TitleDay"/>,
/// <see cref="Clicker.TitleRare"/>, <see cref="Clicker.TitleSecret"/>, <see cref="Clicker.TitleMemory"/>).
/// </summary>
public sealed record ClickerTitle(string Key, string Icon, string Name, string Desc, string Kind);

/// <summary>
/// Звання округи (docs/games/specs/clicker-titles.md). П'ять родів:
/// <list type="bullet">
/// <item>«перші в окрузі» — одне на всю округу, тримає той, у кого найбільше (клейм, спійманих глеків, гостей…),
///   і його можна перехопити;</item>
/// <item>звання дня — учорашні переможці тримають їх увесь сьогоднішній день;</item>
/// <item>рідкісні — вибити може кожен, лишаються назавжди;</item>
/// <item>таємні — те саме, але умову видно лише після того, як хтось в окрузі вибив їх першим;</item>
/// <item>пам'ятні — лише для тих, хто грав до цього оновлення.</item>
/// </list>
/// Звання нічого не множать — лише статус: до трьох значків біля ніка, стіна звань у хаті, рядок у Журналі.
/// Спільне (хто що тримає) живе в <see cref="ClickerGuildService"/>; тут — те, що належить гончареві: зароблене,
/// обрані значки, лічильники, яких гра раніше не вела, і лічильники дня. Разом зі званнями — подарунок округи
/// (<see cref="GiftKey"/>): три години власного пасиву й пам'ятний глечик, раз на гончаря.
/// </summary>
public sealed partial class Clicker
{
    public const string TitleTop = "top", TitleDay = "day", TitleRare = "rare", TitleSecret = "secret", TitleMemory = "memory";
    /// <summary>«Перший гончар округи» — найбільше клейм; у списку цеху його нік золотий.</summary>
    public const string TitleFirst = "first";
    /// <summary>«Вічний учень» — живе, поки гончар учень; у збереженні лежить лише позначка «вже оголошено».</summary>
    public const string TitleApprentice = "x10";
    public const int BadgesMax = 3;

    /// <summary>Подарунок округи за оновлення зі званнями: три години «без тебе» і пам'ятний глечик.</summary>
    public const string GiftKey = "v9.2";
    public const int GiftMinutes = 180;

    public const int TenThousand = 10_000, HundredStreak = 100, FlawlessRun = 20;
    public const int SleepyMissed = 300, HooksBroken = 100, CatKnockTimes = 10, OverburnCracks = 25, ApprenticeFired = 1000;
    public const int NicholasGifts = 3, CircleMin = 2;
    /// <summary>«В останню мить»: спіймати розписний глек не раніше, ніж за стільки до втечі.</summary>
    public static readonly TimeSpan LastMoment = TimeSpan.FromMilliseconds(500);
    /// <summary>«День бабака»: другий обпал майстерні не пізніше, ніж через стільки після першого.</summary>
    public static readonly TimeSpan GroundhogGap = TimeSpan.FromHours(1);
    /// <summary>«Остання копійка» — лише для покупок від мільйона: на голому колі кишеня порожніє сама.</summary>
    public const double PennyFrom = 1e6;
    /// <summary>«Висхідна зірка» — від мільйона глеків на ранок: інакше новачок із десятьма глеками вигравав би щодня.</summary>
    public const double RisingFrom = 1e6;
    /// <summary>З котрої години київський ранок: «Перший півень» — перший клік після неї, «Нічна варта» — кліки до неї.</summary>
    public const int MorningHour = 5;
    /// <summary>Як часто кімната звітує цехові про звання, коли нічого особливого не сталось.</summary>
    static readonly TimeSpan TitlesEvery = TimeSpan.FromMinutes(1);

    public static readonly ClickerTitle[] Titles =
    [
        // «Перші в окрузі»: одне на округу, переходить до того, в кого більше.
        new("first", "👑", "Перший гончар округи", "Найбільше клейм в окрузі", TitleTop),
        new("hunter", "🎯", "Мисливець за розписними", "Найбільше спійманих розписних глеків", TitleTop),
        new("collector", "📒", "Збирач", "Найбільше клітинок в альбомі", TitleTop),
        new("lord", "🏛", "Пан на хаті", "Найбільше оздоби в хаті", TitleTop),
        new("sage", "🧙", "Знавець секретів", "Найбільше родинних секретів", TitleTop),
        new("kilnlord", "🔥", "Володар горна", "Найбільше обпалених виробів", TitleTop),
        new("generous", "🎁", "Щедра душа", "Найбільше подарованих друзям виробів", TitleTop),
        new("host", "🎪", "Гостинний господар", "Найбільше прийнятих гостей ярмарку", TitleTop),
        new("fairman", "🧺", "Ярмарковий", "Найбільше виконаних замовлень сіл", TitleTop),
        new("lucky", "🍀", "Щасливчик", "Найбільше щасливих кліків", TitleTop),
        new("catlover", "🐈", "Котолюб", "Найчастіше гладив кота", TitleTop),
        new("wonderer", "🔮", "Дивовижник", "Найбільше знайдених дивовиж", TitleTop),
        new("pillar", "🛒", "Опора цеху", "Найбільше покладено на вози цеху", TitleTop),
        // Звання дня: учорашні переможці тримають їх сьогодні.
        new("rising", "⭐", "Висхідна зірка", "Найбільший приріст глеків за день, у відсотках", TitleDay),
        new("rooster", "🐓", "Перший півень", "Першим крутнув коло після п'ятої ранку", TitleDay),
        new("night", "🦉", "Нічна варта", "Найбільше кліків між північчю й п'ятою ранку", TitleDay),
        new("bee", "🐝", "Бджілка", "Найбільше кліків за день", TitleDay),
        new("catch", "🎣", "Улов дня", "Найбільше спійманих глеків з полиці за день", TitleDay),
        // Рідкісні: кожен може, назавжди.
        new("sich", "🏰", "Гончар на Січі", "Купити Гончарню на Січі", TitleRare),
        new("tenk", "🔟", "Десять тисяч", "Зібрати 10 000 клейм", TitleRare),
        new("hundred", "💯", "Сотня", "Серія зі 100 спійманих глеків з полиці", TitleRare),
        new("album", "📚", "Повний альбом", "Відкрити всі клітинки альбому", TitleRare),
        new("stars", "🌟", "Зоряний альбом", "Зірка в кожній клітинці альбому", TitleRare),
        new("wonders", "🧿", "Усі дива", "Знайти всі дивовижі", TitleRare),
        new("flawless", "🏆", "Бездоганне горно", "20 обпалів горна поспіль власноруч — і все дзвінке", TitleRare),
        new("suns", "🌞", "Три сонця", "Щасливий клік, коли разом тривають натхнення і ярмарок", TitleRare),
        new("moment", "⏱", "В останню мить", "Спіймати розписний глек за пів секунди до того, як він утече", TitleRare),
        // Таємні: ключі навмисно ні про що не кажуть — каталог їде до кожного клієнта.
        new("x1", "😴", "Соня", "300 розписних глеків утекли неспійманими", TitleSecret),
        new("x2", "🫠", "Руки-крюки", "100 глеків з полиці розбились, поки ти клацав коло", TitleSecret),
        new("x3", "🐈‍⬛", "Котяча жертва", "Кіт 10 разів збив у тебе глек із полиці", TitleSecret),
        new("x4", "🥵", "Перепалив", "25 обпалів горна з тріснутими виробами", TitleSecret),
        new("x5", "🕛", "Опівнічний палій", "Обпалити майстерню між 00:00 і 00:05", TitleSecret),
        new("x6", "🔁", "День бабака", "Два обпали майстерні за одну годину", TitleSecret),
        new("x7", "🪙", "Остання копійка", "Купити верстат так, що в кишені лишилось менше, ніж коло дає за секунду", TitleSecret),
        new("x8", "🤝", "Кругова порука", "Отримати дарунок від кожного гончаря округи", TitleSecret),
        new("x9", "🎅", "Святий Миколай", "За один день подарувати вироби трьом різним гончарям", TitleSecret),
        new(TitleApprentice, "🐌", "Вічний учень", "Обпалити тисячу виробів і так і не здати цехові майстерштук", TitleSecret),
        // Пам'ятні: більше не вибити.
        new("firstclay", "🏺", "Перша глина", "Грав ще до того, як в окрузі з'явились звання", TitleMemory),
        new("veteran", "🩹", "Ветеран реформи 9.1", "Мав понад тисячу клейм, коли клейма переробили", TitleMemory),
    ];

    static readonly Dictionary<string, ClickerTitle> TitleByKey = Titles.ToDictionary(t => t.Key, StringComparer.Ordinal);

    public static ClickerTitle? TitleDef(string? key) => key is not null && TitleByKey.TryGetValue(key, out var t) ? t : null;

    /// <summary>Наскільки звання рідкісне для значків: «перший гончар» завжди перший, далі таємні, рідкісні, перші в окрузі, пам'ятні, дня.</summary>
    static int Rarity(ClickerTitle t) => t.Key == TitleFirst ? 0 : t.Kind switch
    {
        TitleSecret => 1, TitleRare => 2, TitleTop => 3, TitleMemory => 4, _ => 5,
    };

    /// <summary>Звання за рідкістю, а всередині роду — у порядку каталогу.</summary>
    public static IEnumerable<string> ByRarity(IEnumerable<string> keys) => keys
        .Distinct(StringComparer.Ordinal)
        .Select(TitleDef).OfType<ClickerTitle>()
        .OrderBy(Rarity).ThenBy(t => Array.IndexOf(Titles, t))
        .Select(t => t.Key);

    /// <summary>Значки біля ніка: обрані з тих, що ще є, а коли нічого з обраного вже нема — найрідкісніші.</summary>
    public static List<string> Badges(IEnumerable<string> has, IEnumerable<string> show)
    {
        var set = has.ToHashSet(StringComparer.Ordinal);
        var chosen = show.Where(set.Contains).Distinct(StringComparer.Ordinal).Take(BadgesMax).ToList();
        return chosen.Count > 0 ? chosen : ByRarity(set).Take(BadgesMax).ToList();
    }

    // ---------- стан гончаря ----------

    /// <summary>Рідкісні, таємні й пам'ятні звання назавжди — і коли вибив.</summary>
    readonly Dictionary<string, DateTimeOffset> _titles = new(StringComparer.Ordinal);
    /// <summary>Обрані значки біля ніка (порожньо — найрідкісніші самі).</summary>
    readonly List<string> _titleShow = [];
    /// <summary>Лічильники, яких гра раніше не вела: рахуються з дня звань.</summary>
    int _goldenMissed, _brokenBusy, _catKnocks, _cracked, _perfectRun;
    DateTimeOffset _lastFireAt;
    /// <summary>Від кого приходили дарунки (ключі ніків) — для «Кругової поруки».</summary>
    readonly HashSet<string> _giftFrom = new(StringComparer.Ordinal);
    /// <summary>Кому подаровано сьогодні — для «Святого Миколая».</summary>
    string _giftToDay = "";
    readonly HashSet<string> _giftTo = new(StringComparer.Ordinal);
    /// <summary>Лічильники київського дня для звань дня: з чого почали день, кліки, нічні кліки, улов, перший ранковий клік.</summary>
    string _tDay = "";
    double _tDayStart;
    long _tClicks, _tNight;
    int _tCatch;
    DateTimeOffset _tRooster;
    /// <summary>Подарунки округи, які гончар уже забрав.</summary>
    readonly HashSet<string> _gifts = new(StringComparer.Ordinal);

    /// <summary>Звання, вибиті цією дією: після неї — у Журнал і цехові.</summary>
    readonly List<string> _titleNews = [];
    /// <summary>Коли востаннє звітували цехові (лише в пам'яті: після перезапуску перша ж дія звітує).</summary>
    DateTimeOffset _titlesAt;
    bool _titlesForce;

    bool ApprenticeNow => _guildRank == 0 && FiredTotal >= ApprenticeFired;

    void ResetTitleFields()
    {
        _titles.Clear();
        _titleShow.Clear();
        _goldenMissed = _brokenBusy = _catKnocks = _cracked = _perfectRun = 0;
        _lastFireAt = default;
        _giftFrom.Clear();
        _giftToDay = "";
        _giftTo.Clear();
        _tDay = "";
        _tDayStart = 0;
        _tClicks = _tNight = 0;
        _tCatch = 0;
        _tRooster = default;
        _gifts.Clear();
        _titleNews.Clear();
        _titlesAt = default;
        _titlesForce = false;
    }

    /// <summary>Нове коло: звань ще нема, а подарунок округи — для тих, хто грав до оновлення, тож новачкові він уже «забраний».</summary>
    void ResetTitles()
    {
        ResetTitleFields();
        _gifts.Add(GiftKey);
    }

    /// <summary>Вибив звання назавжди: запам'ятати й після дії сказати в Журнал і цехові.</summary>
    void TitleEarn(string key)
    {
        if (_titles.ContainsKey(key) || TitleDef(key) is null) return;
        _titles[key] = Ctx.Clock.UtcNow;
        _titleNews.Add(key);
    }

    /// <summary>Що гончар має сам по собі, без цеху: зароблене назавжди й «Вічний учень», поки він учень.</summary>
    List<string> HasNow()
    {
        var has = _titles.Keys.Where(k => k != TitleApprentice).ToList();
        if (ApprenticeNow) has.Add(TitleApprentice);
        return has;
    }

    /// <summary>Усе, що гончар має просто зараз: своє плюс те, що тримає в окрузі («перші» й учорашні звання дня).</summary>
    List<string> MineNow(DateTimeOffset now)
    {
        var mine = HasNow();
        if (_guildSvc is { } svc && GuildKey.Length > 0) mine.AddRange(svc.TitlesHeld(GuildKey, now));
        return ByRarity(mine).ToList();
    }

    /// <summary>Числа для «перших в окрузі» — ті самі, що <see cref="TitleStatsFromSave"/> дістає зі збереження.</summary>
    Dictionary<string, double> TopValues() => new(StringComparer.Ordinal)
    {
        ["first"] = _stamps,
        ["hunter"] = _caught,
        ["collector"] = AlbumOpenCount(_albumCells),
        ["lord"] = _decor.Count,
        ["sage"] = _secrets.Count,
        ["kilnlord"] = FiredTotal,
        ["generous"] = _giftsSent,
        ["host"] = _mktGuests,
        ["fairman"] = _mktDelivered,
        ["lucky"] = _lucky,
        ["catlover"] = _petted,
        ["wonderer"] = _wonders.Count,
        ["pillar"] = _guildGiven,
    };

    /// <summary>Приріст глеків за сьогодні (частка) — від мільйона на ранок, інакше нуль.</summary>
    double TitleGrowth() => _tDayStart >= RisingFrom ? Math.Max(0, _total / _tDayStart - 1) : 0;

    // ---------- гачки ----------

    /// <summary>
    /// Новий київський день: учорашні лічильники — цехові останнім звітом, а сьогоднішні з нуля. «З чого почали день» —
    /// глеки за весь час на першій дії дня (разом із тим, що коло накрутило вночі).
    /// </summary>
    void TitlesDayRoll(DateTimeOffset now)
    {
        var today = Days.Of(now);
        if (_tDay == today) return;
        if (_tDay.Length > 0) TitlesReport(now, force: true);
        _tDay = today;
        _tDayStart = _total;
        _tClicks = _tNight = 0;
        _tCatch = 0;
        _tRooster = default;
    }

    /// <summary>Зараховані кліки: у денний лічильник, уночі — ще й у нічний, а перший після п'ятої ранку — «півень».</summary>
    void TitlesClicks(int taken, DateTimeOffset now)
    {
        if (taken <= 0) return;
        TitlesDayRoll(now);
        _tClicks += taken;
        if (TimeZoneInfo.ConvertTime(now, Days.Kyiv).Hour < MorningHour) _tNight += taken;
        else if (_tRooster == default) _tRooster = now;
    }

    void TitlesGrab(DateTimeOffset now)
    {
        TitlesDayRoll(now);
        _tCatch++;
    }

    /// <summary>Обпал майстерні: опівночі — «Опівнічний палій», другий за годину — «День бабака». Клейма змінились — звітуємо одразу.</summary>
    void TitlesOnFire(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Days.Kyiv);
        if (local.Hour == 0 && local.Minute < 5) TitleEarn("x5");
        if (_lastFireAt != default && now - _lastFireAt <= GroundhogGap) TitleEarn("x6");
        _lastFireAt = now;
        _titlesForce = true;
    }

    /// <summary>Купівля верстата: після неї в кишені менше, ніж коло давало за секунду до неї, — «Остання копійка».</summary>
    void TitlesOnBuy(double lastPrice, double second)
    {
        if (lastPrice >= PennyFrom && second > 0 && _pots < second) TitleEarn("x7");
    }

    /// <summary>Горно відкрите: бездоганні ручні обпали поспіль і обпали з тріщинами. Палій (не власноруч) нічого не міняє.</summary>
    void TitlesOnKiln(bool manual, bool perfect, bool cracked)
    {
        if (!manual) return;
        _perfectRun = perfect ? _perfectRun + 1 : 0;
        if (cracked) _cracked++;
    }

    /// <summary>Дарунок від друга: коли дарунки приходили від кожного з округи (хоч двох) — «Кругова порука».</summary>
    void TitlesGiftFrom(string from, DateTimeOffset now)
    {
        var key = ClickerGuildService.Key(from);
        if (key.Length == 0 || key == GuildKey) return;
        _giftFrom.Add(key);
        if (_guildSvc is not { } svc || GuildKey.Length == 0) return;
        var others = svc.ActiveOthers(GuildKey, now, ClickerGuildService.CircleActiveDays);
        if (others.Count >= CircleMin && others.All(_giftFrom.Contains)) TitleEarn("x8");
    }

    /// <summary>Подарував другові: троє різних за один київський день — «Святий Миколай».</summary>
    void TitlesGiftTo(string to, DateTimeOffset now)
    {
        var day = Days.Of(now);
        if (_giftToDay != day)
        {
            _giftToDay = day;
            _giftTo.Clear();
        }
        _giftTo.Add(ClickerGuildService.Key(to));
        if (_giftTo.Count >= NicholasGifts) TitleEarn("x9");
    }

    /// <summary>Що з лічильників уже стало званням. Кличеться після кожної дії — перевірки дешеві.</summary>
    void TitlesCheck()
    {
        if (Level("sich") > 0) TitleEarn("sich");
        if (_stamps >= TenThousand) TitleEarn("tenk");
        if (_fallStreak >= HundredStreak) TitleEarn("hundred");
        if (AlbumOpenCount(_albumCells) >= AlbumSize) TitleEarn("album");
        if (AlbumStarCount(_albumStars) >= AlbumSize) TitleEarn("stars");
        if (_wonders.Count >= Wonders.Length) TitleEarn("wonders");
        if (_perfectRun >= FlawlessRun) TitleEarn("flawless");
        if (_goldenMissed >= SleepyMissed) TitleEarn("x1");
        if (_brokenBusy >= HooksBroken) TitleEarn("x2");
        if (_catKnocks >= CatKnockTimes) TitleEarn("x3");
        if (_cracked >= OverburnCracks) TitleEarn("x4");
        if (ApprenticeNow) TitleEarn(TitleApprentice);
    }

    /// <summary>
    /// Після кожної дії: лічильники → звання, нові звання — у Журнал (першому в окрузі — з умовою, бо таємне від цієї
    /// миті видно всім), і звіт цехові — одразу, коли щось сталось, інакше раз на хвилину.
    /// </summary>
    void TitlesAfterAct(DateTimeOffset now)
    {
        TitlesCheck();
        var svc = _guildSvc;
        var key = GuildKey;
        foreach (var k in _titleNews)
        {
            var t = TitleDef(k)!;
            var first = svc is not null && key.Length > 0 && svc.TitleEarned(key, GuildNick, k, now);
            var what = t.Kind == TitleSecret ? "таємне звання" : "звання";
            var head = t.Kind == TitleSecret ? "🙈" : "🏅";
            Ctx.Log(first
                ? $"{head} {GuildNick} — перший(а) в окрузі, хто вибив {what} {t.Icon} «{t.Name}»: {Lower(t.Desc)}"
                : $"{head} {GuildNick} вибив(ла) {what} {t.Icon} «{t.Name}»");
        }
        var force = _titleNews.Count > 0 || _titlesForce;
        _titleNews.Clear();
        _titlesForce = false;
        TitlesReport(now, force);
    }

    static string Lower(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    /// <summary>Звіт цехові: не частіше, ніж раз на хвилину, хіба що <paramref name="force"/>. Перехоплені звання — у Журнал.</summary>
    void TitlesReport(DateTimeOffset now, bool force)
    {
        if (_guildSvc is not { } svc || GuildKey.Length == 0) return;
        if (!force && _titlesAt != default && now - _titlesAt < TitlesEvery) return;
        _titlesAt = now;
        var report = new TitleReport(TopValues(), _titleShow.ToList(), HasNow(), _tDay,
            _tDay.Length > 0 ? new TitleDayReport(_tClicks, _tNight, _tCatch, _tRooster == default ? null : _tRooster, TitleGrowth()) : null);
        foreach (var (title, from) in svc.TitlesReport(GuildKey, GuildNick, now, report))
            if (TitleDef(title) is { } t && from.Length > 0) Ctx.Log($"{t.Icon} {GuildNick} забирає в {from} звання «{t.Name}»");
    }

    // ---------- дії ----------

    /// <summary><c>titles { op: "show", keys: [...] }</c> — які звання стоять біля ніка (до трьох; порожньо — найрідкісніші).</summary>
    ActResult? ActTitles(string action, JsonElement payload)
    {
        if (action != "titles") return null;
        if (Str(payload, "op") != "show") return ActResult.Fail("Зі званнями так не роблять");
        var mine = MineNow(Ctx.Clock.UtcNow).ToHashSet(StringComparer.Ordinal);
        var keys = new List<string>();
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("keys", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var k in arr.EnumerateArray())
            {
                if (k.ValueKind != JsonValueKind.String || k.GetString() is not { } key) continue;
                if (!mine.Contains(key)) return ActResult.Fail("Такого звання в тебе нема");
                if (!keys.Contains(key)) keys.Add(key);
            }
        if (keys.Count > BadgesMax) return ActResult.Fail($"Біля ніка — не більше {BadgesMax} значків");
        _titleShow.Clear();
        _titleShow.AddRange(keys);
        _titlesForce = true;
        return ActResult.Accept(keys.Count == 0
            ? "🎖 Біля ніка — найрідкісніші звання, які маєш"
            : "🎖 Біля ніка тепер: " + string.Join(" ", keys.Select(k => TitleDef(k)!.Icon)));
    }

    /// <summary>
    /// Подарунок округи: три години власного «без тебе» глеками (вони йдуть і в «за весь час») і пам'ятний глечик на
    /// стіні звань. Забирається разом із «Що нового» — раз на гончаря; новачок, якому новини ні до чого, його не має.
    /// </summary>
    ActResult? TakeGift()
    {
        if (_gifts.Contains(GiftKey)) return null;
        _gifts.Add(GiftKey);
        var gain = TreatGain(GiftMinutes);
        Add(gain);
        return ActResult.Accept($"🎁 Подарунок округи: +{PotsShort(gain)} — три години твого «без тебе». "
            + "А на стіні звань — пам'ятний глечик «Округа»");
    }

    // ---------- вид і каталог ----------

    object TitlesView(DateTimeOffset now)
    {
        var mine = MineNow(now);
        var today = _tDay == Days.Of(now);
        var values = TopValues();
        values["bee"] = today ? _tClicks : 0;
        values["night"] = today ? _tNight : 0;
        values["catch"] = today ? _tCatch : 0;
        values["rising"] = today ? TitleGrowth() : 0;
        values["rooster"] = today && _tRooster != default ? _tRooster.ToUnixTimeMilliseconds() : 0;
        // [скільки є, скільки треба] — рідкісні й таємні з лічильником (таємні клієнт показує лише відкриті).
        var progress = new Dictionary<string, double[]>(StringComparer.Ordinal)
        {
            ["sich"] = [Level("sich") > 0 ? 1 : 0, 1],
            ["tenk"] = [Math.Min(_stamps, TenThousand), TenThousand],
            ["hundred"] = [Math.Min(_fallStreak, HundredStreak), HundredStreak],
            ["album"] = [AlbumOpenCount(_albumCells), AlbumSize],
            ["stars"] = [AlbumStarCount(_albumStars), AlbumSize],
            ["wonders"] = [_wonders.Count, Wonders.Length],
            ["flawless"] = [Math.Min(_perfectRun, FlawlessRun), FlawlessRun],
            ["x1"] = [Math.Min(_goldenMissed, SleepyMissed), SleepyMissed],
            ["x2"] = [Math.Min(_brokenBusy, HooksBroken), HooksBroken],
            ["x3"] = [Math.Min(_catKnocks, CatKnockTimes), CatKnockTimes],
            ["x4"] = [Math.Min(_cracked, OverburnCracks), OverburnCracks],
        };
        // «Вічний учень» можливий лише для учня: хто вже склав майстерштук, тому прогрес ні до чого.
        if (_guildRank == 0) progress[TitleApprentice] = [Math.Min(FiredTotal, ApprenticeFired), ApprenticeFired];
        return new
        {
            mine,
            earned = _titles.Where(x => x.Key != TitleApprentice || ApprenticeNow).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            show = _titleShow,
            badges = Badges(mine, _titleShow),
            values,
            progress,
            // Свої таємні — з назвою й значком: вивіска мусить знати, що малювати, ще до списку цеху.
            secrets = mine.Select(TitleDef).OfType<ClickerTitle>().Where(t => t.Kind == TitleSecret)
                .ToDictionary(t => t.Key, t => (object)new { icon = t.Icon, name = t.Name, desc = t.Desc }, StringComparer.Ordinal),
            gift = _gifts.Contains(GiftKey),
        };
    }

    /// <summary>Каталог звань: таємні — лише ключем (назву й умову відкриває дошка цеху, коли хтось їх вибив).</summary>
    static object CatalogTitles() => new
    {
        list = Titles.Select(t => t.Kind == TitleSecret
            ? (object)new { key = t.Key, kind = t.Kind }
            : new { key = t.Key, icon = t.Icon, name = t.Name, desc = t.Desc, kind = t.Kind }),
        badgesMax = BadgesMax,
        activeDays = ClickerGuildService.TitleActiveDays,
        beeMin = ClickerGuildService.BeeMin,
        nightMin = ClickerGuildService.NightMin,
        catchMin = ClickerGuildService.CatchMin,
        morning = MorningHour,
        giftMinutes = GiftMinutes,
    };

    // ---------- збереження ----------

    sealed record TitlesRow(
        Dictionary<string, DateTimeOffset>? Earned = null, List<string>? Show = null,
        int GoldenMissed = 0, int BrokenBusy = 0, int CatKnocks = 0, int Cracked = 0, int PerfectRun = 0,
        DateTimeOffset LastFire = default, List<string>? GiftFrom = null, string? GiftDay = null, List<string>? GiftTo = null,
        string? Day = null, double DayStart = 0, long DayClicks = 0, long DayNight = 0, int DayCatch = 0, DateTimeOffset DayRooster = default,
        List<string>? Gifts = null);

    TitlesRow SaveTitles() => new(
        new Dictionary<string, DateTimeOffset>(_titles, StringComparer.Ordinal), _titleShow.ToList(),
        _goldenMissed, _brokenBusy, _catKnocks, _cracked, _perfectRun,
        _lastFireAt, _giftFrom.Order(StringComparer.Ordinal).ToList(), _giftToDay, _giftTo.Order(StringComparer.Ordinal).ToList(),
        _tDay, _tDayStart, _tClicks, _tNight, _tCatch, _tRooster,
        _gifts.Order(StringComparer.Ordinal).ToList());

    /// <summary>
    /// Звання зі збереження. Збереження з часів до звань (<paramref name="row"/> — null) дістає пам'ятні: «Перша глина»
    /// кожному, хто вже щось наліпив, і «Ветеран реформи» тому, в кого на той час було понад тисячу клейм. Кличеться
    /// наприкінці Load — глеки й клейма вже прочитані.
    /// </summary>
    void LoadTitles(TitlesRow? row)
    {
        ResetTitleFields();
        var now = Ctx.Clock.UtcNow;
        if (row is null)
        {
            if (_total > 0) _titles["firstclay"] = now;
            if (_stamps >= StampSoftFrom) _titles["veteran"] = now;
            return;
        }
        // Лише те, що справді заробляється назавжди: «перші» й звання дня живуть у цеху, а не в збереженні.
        foreach (var (k, at) in row.Earned ?? [])
            if (TitleDef(k) is { Kind: not TitleTop and not TitleDay }) _titles[k] = at > now ? now : at;
        foreach (var k in row.Show ?? [])
            if (TitleDef(k) is not null && !_titleShow.Contains(k) && _titleShow.Count < BadgesMax) _titleShow.Add(k);
        _goldenMissed = Math.Max(0, row.GoldenMissed);
        _brokenBusy = Math.Max(0, row.BrokenBusy);
        _catKnocks = Math.Max(0, row.CatKnocks);
        _cracked = Math.Max(0, row.Cracked);
        _perfectRun = Math.Max(0, row.PerfectRun);
        _lastFireAt = row.LastFire > now ? now : row.LastFire;
        foreach (var k in row.GiftFrom ?? []) if (k is { Length: > 0 and <= 64 }) _giftFrom.Add(k);
        _giftToDay = row.GiftDay ?? "";
        foreach (var k in row.GiftTo ?? []) if (k is { Length: > 0 and <= 64 }) _giftTo.Add(k);
        _tDay = row.Day is { Length: <= 16 } d ? d : "";
        _tDayStart = double.IsFinite(row.DayStart) ? Math.Max(0, row.DayStart) : 0;
        _tClicks = Math.Max(0, row.DayClicks);
        _tNight = Math.Max(0, row.DayNight);
        _tCatch = Math.Max(0, row.DayCatch);
        _tRooster = row.DayRooster > now ? default : row.DayRooster;
        foreach (var k in row.Gifts ?? []) if (k is { Length: > 0 and <= 16 }) _gifts.Add(k);
    }

    // ---------- зі збереження, для цеху ----------

    /// <summary>Звання, як їх видно зі збереження: числа «перших в окрузі», обрані значки, що має і коли вибив.</summary>
    public sealed record SavedTitles(
        IReadOnlyDictionary<string, double> Values, IReadOnlyList<string> Show, IReadOnlyList<string> Has,
        IReadOnlyDictionary<string, DateTimeOffset> Earned);

    static readonly SavedTitles NoTitles = new(new Dictionary<string, double>(), [], [], new Dictionary<string, DateTimeOffset>());

    /// <summary>
    /// Те саме, що <see cref="TopValues"/> і <see cref="HasNow"/>, але зі збереження: цех так знайомиться з гончарями,
    /// які після оновлення ще не заходили. Зіпсоване чи порожнє — нічого.
    /// </summary>
    public static SavedTitles TitleStatsFromSave(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return NoTitles;
        JsonObject root;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return NoTitles;
            root = o;
        }
        catch (JsonException) { return NoTitles; }

        static double N(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var x) && double.IsFinite(x) ? Math.Max(0, x) : 0;
        static int Count(JsonNode? n) => n switch { JsonArray a => a.Count, JsonObject o => o.Count, _ => 0 };
        static double Sum(JsonNode? n) => n is JsonObject o ? o.Sum(kv => N(kv.Value)) : 0;

        var house = root["house"] as JsonObject;
        var craft = root["craft"] as JsonObject;
        var guild = root["guild"] as JsonObject;
        var fair = root["fair"] as JsonObject;
        var album = root["album"] as JsonObject;
        var titles = root["titles"] as JsonObject;
        var fired = Sum(craft?["firedBy"]);
        var values = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["first"] = N(root["stamps"]),
            ["hunter"] = N(root["caught"]),
            ["collector"] = album?["cells"] is JsonObject cells ? cells.Sum(kv => Count(kv.Value)) : 0,
            ["lord"] = Count(house?["decor"]),
            ["sage"] = Count(root["secrets"]),
            ["kilnlord"] = fired,
            ["generous"] = N(guild?["giftsSent"]),
            ["host"] = N(fair?["guests"]),
            ["fairman"] = N(fair?["delivered"]),
            ["lucky"] = N(root["lucky"]),
            ["catlover"] = N(root["petted"]),
            ["wonderer"] = Count(house?["wonders"]),
            ["pillar"] = N(guild?["given"]),
        };
        var earned = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (titles?["earned"] is JsonObject e)
            foreach (var (k, v) in e)
                if (TitleDef(k) is { Kind: not TitleTop and not TitleDay } && v is JsonValue jv && jv.TryGetValue<string>(out var s)
                    && DateTimeOffset.TryParse(s, out var at))
                    earned[k] = at;
        var has = earned.Keys.Where(k => k != TitleApprentice).ToList();
        if (N(guild?["rank"]) == 0 && fired >= ApprenticeFired) has.Add(TitleApprentice);
        var show = titles?["show"] is JsonArray sa
            ? sa.Select(x => x is JsonValue sv && sv.TryGetValue<string>(out var k) ? k : "").Where(k => TitleDef(k) is not null).Take(BadgesMax).ToList()
            : [];
        return new SavedTitles(values, show, has, earned);
    }

    /// <summary>Чи забрав гончар подарунок округи — тоді в його хаті пам'ятний глечик.</summary>
    public static bool KeepsakeIn(JsonObject root) =>
        root["titles"] is JsonObject t && t["gifts"] is JsonArray a
        && a.Any(x => x is JsonValue v && v.TryGetValue<string>(out var s) && s == GiftKey);
}
