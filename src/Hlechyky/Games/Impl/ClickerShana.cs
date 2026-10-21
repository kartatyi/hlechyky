namespace Hlechyky.Games.Impl;

/// <summary>
/// Гілка шани старійшини: ключ, значок, назва, що саме рахує (<paramref name="What"/> — для підпису смужки), пороги
/// п'яти щаблів і перк кожного щабля словами.
/// </summary>
public sealed record ClickerShanaTrack(string Key, string Icon, string Name, string What, long[] Need, string[] Perks);

/// <summary>
/// Шана старійшини (docs/games/specs/clicker-shana.md): вище за Старійшину рангу нема — зате відкриваються п'ять гілок,
/// кожна від свого лічильника за весь час (обпалено, покладено на вози, розкішних обпалено, допомог друзям, замовлень
/// сіл). Щабель — перк назавжди; лічильники ростуть і до Старійшини, але перки діють лише в нього. Усі п'ять на V —
/// звання «Легенда Опішні».
///
/// Перки — зручність і швидкість (горно, вози, ярмарок, кораблі, допомога друзям), а не множник кола: клейма вони не
/// женуть. Тут — каталог, лічильники, перки для інших пакетів і вид; гачки — у горні, цеху, ярмарку й торгівлі.
/// </summary>
public sealed partial class Clicker
{
    public const string ShanaKiln = "kiln", ShanaWagon = "wagon", ShanaLux = "lux", ShanaKind = "kind", ShanaFair = "fair";
    public const int ShanaMax = 5;

    public static readonly ClickerShanaTrack[] ShanaTracks =
    [
        new(ShanaKiln, "🔥", "Горно", "обпалено виробів", [15_000, 30_000, 60_000, 120_000, 250_000],
        [
            "Підмайстер розписує: автогорно палить у твоєму розписі з красою 15 — бувають і розкішні",
            "Горно остигає за 45 с замість 60",
            "+4 місця в горні",
            "Підмайстер розписує з красою 30",
            "Ще +4 місця в горні",
        ]),
        new(ShanaWagon, "🛒", "Вози", "покладено на вози", [3_000, 6_000, 12_000, 25_000, 50_000],
        [
            "Кожен виріб, покладений на віз, одразу платить подвійну ціну",
            "Хвилина воза — пів хвилини гри, а не чверть",
            "Обозний: твої вироби на возі рахуються за півтора — пай і рівень воза ростуть швидше",
            "Коли ти віз, кожен, хто віз того дня, забирає з нагородою ще 10 хв гри",
            "Нагорода воза ×1,5 і звання «Обозний»",
        ]),
        new(ShanaLux, "✨", "Розкіш", "розкішних обпалено", [100, 300, 1_000, 3_000, 10_000],
        [
            "+15 % розкішних у партії",
            "Корабель зараховує розкішний виріб за два",
            "Розкішне платить ×1,5: на базарі, на кораблі й у панських замовленнях",
            "Ще +15 % розкішних, і пани замовляють розкішне за ×5",
            "Скринька розкоші в хаті й ще +15 % розкішних",
        ]),
        new(ShanaKind, "🤝", "Сусідство", "дарунків, гостинців і підмайстрів друзям", [50, 100, 200, 400, 800],
        [
            "Підмайстра можна послати в гості двічі на день",
            "Дарунок несе другові ще й гостинець на 10 хв — тобі безплатно",
            "Твій гостинець удвічі щедріший",
            "Твоя похвала гріє дві години замість однієї",
            "Звання «Добра душа»",
        ]),
        new(ShanaFair, "🎪", "Ярмарок", "виконано замовлень сіл", [300, 600, 1_200, 2_500, 5_000],
        [
            "+1 місце на дошці замовлень",
            "Замовлення сіл чекають на 15 хв довше",
            "Прикажчик: звичайне замовлення, на яке є вільний товар, здає сам — і поки тебе нема",
            "Усі замовлення сіл платять ×1,25",
            "Кожен рівень шани сіл дає +3,5 % до всього замість +3 %",
        ]),
    ];

    /// <summary>Краса розпису в автогорні: щабель I «Горна» і щабель IV.</summary>
    public const double ShanaAutoPaint1 = 0.15, ShanaAutoPaint4 = 0.30;
    /// <summary>Горно остигає стільки з щабля II «Горна».</summary>
    public static readonly TimeSpan ShanaKilnCool = TimeSpan.FromSeconds(45);
    /// <summary>Місця в горні: щабель III і щабель V (кожен — стільки).</summary>
    public const int ShanaKilnSlotsStep = 4;
    /// <summary>Кожен щабель «Розкоші» з цим перком (I, IV, V) — розкішних у партії стільки більше, частка від свого.</summary>
    public const double ShanaLuxStep = 0.15;
    /// <summary>«Розкіш» III: розкішне платить стільки.</summary>
    public const double ShanaLuxPay = 1.5;
    /// <summary>«Розкіш» IV: панське замовлення на розкішне — такий множник (звичайне панське — ×4).</summary>
    public const double ShanaLuxLordMult = 5;
    /// <summary>«Розкіш» IV: стільки відсотків панських замовлень — на розкішне.</summary>
    public const int ShanaLuxLordPct = 40;
    /// <summary>«Вози» I: виріб на возі платить стільки своїх цін (ціна — як на ярмарку, у хвилинах гри).</summary>
    public const double ShanaWagonItemPay = 2;
    /// <summary>«Вози» II: хвилина воза — стільки-та частина хвилини гри (без перку — чверть).</summary>
    public const double ShanaWagonPlayPart = 2;
    /// <summary>«Вози» III: виріб на возі важить стільки.</summary>
    public const double ShanaWagonWeight = 1.5;
    /// <summary>«Вози» IV: стільки хвилин гри кожному, хто віз у той день, що й обозний.</summary>
    public const double ShanaObozMinutes = 10;
    /// <summary>«Вози» V: нагорода воза ще стільки разів.</summary>
    public const double ShanaWagonMult = 1.5;
    /// <summary>«Сусідство» II: гостинець, що їде разом із дарунком, — стільки хвилин (друг дістає вдвічі — як завжди).</summary>
    public const int ShanaGiftTreatMinutes = 10;
    /// <summary>«Ярмарок» II: замовлення чекають стільки довше.</summary>
    public static readonly TimeSpan ShanaOrderLonger = TimeSpan.FromMinutes(15);
    /// <summary>«Ярмарок» IV: усі замовлення сіл платять стільки.</summary>
    public const double ShanaFairPay = 1.25;
    /// <summary>«Ярмарок» V: кожен рівень шани сіл — стільки до всього (без перку — <see cref="FairRepAll"/>).</summary>
    public const double ShanaRepAll = 0.035;

    /// <summary>Розкішних обпалено за весь час (рахуємо з цього оновлення; старе збереження — з того, що лежить у коморі).</summary>
    long _luxFired;
    /// <summary>Підмайстрів, посланих друзям у гості (рахуємо з цього оновлення).</summary>
    int _lendsSent;
    /// <summary>Про який щабель кожної гілки вже сказали в Журнал: двічі одне й те саме не кажемо.</summary>
    readonly Dictionary<string, int> _shanaSeen = new(StringComparer.Ordinal);
    /// <summary>Нові щаблі, що відкрились цією дією, — у тост (після дії, як подарунок майстерні).</summary>
    string _shanaToast = "";

    static ClickerShanaTrack ShanaTrack(string key) => ShanaTracks.First(t => t.Key == key);

    /// <summary>Лічильник гілки — за весь час, і в нестарійшини теж (перки однаково відкриються, щойно стане старійшиною).</summary>
    long ShanaHave(string key) => key switch
    {
        ShanaKiln => FiredTotal,
        ShanaWagon => _guildGiven,
        ShanaLux => _luxFired,
        ShanaKind => (long)_giftsSent + _treatsSent + _lendsSent,
        ShanaFair => _mktDelivered,
        _ => 0,
    };

    /// <summary>Скільки щаблів гілки набрано лічильником (0…5), незалежно від рангу.</summary>
    int ShanaEarned(string key)
    {
        var have = ShanaHave(key);
        var need = ShanaTrack(key).Need;
        var n = 0;
        while (n < need.Length && have >= need[n]) n++;
        return n;
    }

    /// <summary>Чинний щабель гілки: перки діють лише в старійшини.</summary>
    int Shana(string key) => _guildRank >= GuildElder ? ShanaEarned(key) : 0;

    // ---------- перки для інших пакетів ----------

    /// <summary>«Горно» I/IV: з якою красою підмайстер розписує партію автогорна (0 — не розписує).</summary>
    internal double ShanaAutoPaint => Shana(ShanaKiln) switch { >= 4 => ShanaAutoPaint4, >= 1 => ShanaAutoPaint1, _ => 0 };

    /// <summary>«Горно» II: скільки горно остигає.</summary>
    internal TimeSpan KilnCoolNow => Shana(ShanaKiln) >= 2 ? ShanaKilnCool : KilnCool;

    /// <summary>«Горно» III і V: місця в горні понад усе інше.</summary>
    internal int ShanaKilnSlots => Shana(ShanaKiln) switch { >= 5 => 2 * ShanaKilnSlotsStep, >= 3 => ShanaKilnSlotsStep, _ => 0 };

    /// <summary>«Розкіш» I, IV, V: у скільки разів частіше розкішні (частка від своєї).</summary>
    internal double ShanaLuxMult
    {
        get
        {
            var l = Shana(ShanaLux);
            return 1 + ShanaLuxStep * ((l >= 1 ? 1 : 0) + (l >= 4 ? 1 : 0) + (l >= 5 ? 1 : 0));
        }
    }

    /// <summary>«Розкіш» II: корабель зараховує розкішний за два.</summary>
    internal int ShanaShipLuxWeight => Shana(ShanaLux) >= 2 ? 2 : 1;

    /// <summary>«Розкіш» III: множник плати за розкішне (якість 4).</summary>
    internal double ShanaLuxPayFor(int quality) => quality >= 4 && Shana(ShanaLux) >= 3 ? ShanaLuxPay : 1;

    /// <summary>«Розкіш» IV: пани замовляють і розкішне.</summary>
    internal bool ShanaLuxLord => Shana(ShanaLux) >= 4;

    /// <summary>«Вози» I: виріб на возі платить подвійну ціну.</summary>
    internal bool ShanaWagonPays => Shana(ShanaWagon) >= 1;

    /// <summary>«Вози» II: у скільки разів хвилина воза менша за хвилину гри (без перку — <see cref="FairPlayQuarter"/>).</summary>
    internal double ShanaWagonPart => Shana(ShanaWagon) >= 2 ? ShanaWagonPlayPart : FairPlayQuarter;

    /// <summary>«Вози» III: вага виробу на возі.</summary>
    internal double ShanaWagonWeightNow => Shana(ShanaWagon) >= 3 ? ShanaWagonWeight : 1;

    /// <summary>«Вози» IV: обозний — його віз дає всім, хто віз того дня, ще 10 хв гри.</summary>
    internal bool ShanaOboz => Shana(ShanaWagon) >= 4;

    /// <summary>«Вози» V: ще множник нагороди воза.</summary>
    internal double ShanaWagonMultNow => Shana(ShanaWagon) >= 5 ? ShanaWagonMult : 1;

    /// <summary>Що з «Сусідства» діє на допомогу друзям — сервіс цеху рахує межі дня й щедрість із цього.</summary>
    internal GuildPerks ShanaGuildPerks
    {
        get
        {
            var k = Shana(ShanaKind);
            return new GuildPerks(LendPerDay: k >= 1 ? 2 : 1, GiftTreat: k >= 2, TreatX2: k >= 3, CheerLong: k >= 4);
        }
    }

    /// <summary>«Ярмарок» I: місця на дошці.</summary>
    internal int ShanaBoard => Shana(ShanaFair) >= 1 ? 1 : 0;

    /// <summary>«Ярмарок» II: скільки довше чекають замовлення.</summary>
    internal TimeSpan ShanaOrderLife => Shana(ShanaFair) >= 2 ? ShanaOrderLonger : TimeSpan.Zero;

    /// <summary>«Ярмарок» III: прикажчик.</summary>
    internal bool ShanaClerk => Shana(ShanaFair) >= 3;

    /// <summary>«Ярмарок» IV: плата замовлень сіл.</summary>
    internal double ShanaFairPayNow => Shana(ShanaFair) >= 4 ? ShanaFairPay : 1;

    /// <summary>«Ярмарок» V: скільки до всього дає рівень шани сіл.</summary>
    internal double RepAllNow => Shana(ShanaFair) >= 5 ? ShanaRepAll : FairRepAll;

    /// <summary>«Розкіш» V: скринька розкоші в хаті (лише окраса).</summary>
    internal bool ShanaChest => Shana(ShanaLux) >= 5;

    // ---------- лічильники ----------

    /// <summary>Горно відкрилось: розкішні — у лічильник «Розкоші».</summary>
    void ShanaOnKiln(int lux)
    {
        if (lux > 0) _luxFired += lux;
    }

    /// <summary>Розкішних у коморі — початок лічильника для старого збереження: хоч стільки ти точно вже обпалив.</summary>
    long LuxInStore() => AllItems().Where(x => x.Item.Quality >= 4).Sum(x => (long)x.Count);

    // ---------- щаблі: Журнал, звання, тост ----------

    /// <summary>
    /// Після кожної дії: нові щаблі — один рядок у Журнал на всі разом (у мить виходу оновлення в лідерів їх кілька), тост
    /// гончареві про останній, звання за V. Не старійшина — мовчимо: його щаблі відкриються разом із рангом.
    /// </summary>
    void ShanaAfterAct()
    {
        if (_guildRank < GuildElder) return;
        var news = new List<string>();
        string? last = null;
        var full = 0;
        foreach (var t in ShanaTracks)
        {
            var lvl = ShanaEarned(t.Key);
            if (lvl >= ShanaMax) full++;
            var seen = _shanaSeen.GetValueOrDefault(t.Key);
            if (lvl <= seen) continue;
            _shanaSeen[t.Key] = lvl;
            news.Add($"{t.Icon} {t.Name} {Roman(lvl)}");
            last = $"🎖 Шана: {t.Icon} {t.Name} {Roman(lvl)} — {Lower(t.Perks[lvl - 1])}";
        }
        if (Shana(ShanaWagon) >= ShanaMax) TitleEarn("oboz");
        if (Shana(ShanaKind) >= ShanaMax) TitleEarn("kindsoul");
        if (full == ShanaTracks.Length) TitleEarn("legend");
        if (news.Count == 0) return;
        Ctx.Log($"🎖 {GuildNick} — шана старійшини: {string.Join(", ", news)}");
        _shanaToast = news.Count == 1 ? last! : $"🎖 Шана старійшини: {string.Join(", ", news)} — дивись у «Селі»";
    }

    /// <summary>Тост про нові щаблі — раз.</summary>
    string TakeShanaToast()
    {
        var t = _shanaToast;
        _shanaToast = "";
        return t;
    }

    static readonly string[] RomanDigits = ["", "I", "II", "III", "IV", "V"];
    static string Roman(int n) => RomanDigits[Math.Clamp(n, 0, RomanDigits.Length - 1)];

    // ---------- вид ----------

    /// <summary>Гілки для вкладки «Село»: лічильник, набрані щаблі, наступний поріг. До Старійшини — лише як обіцянка.</summary>
    object ViewShana() => new
    {
        on = _guildRank >= GuildElder,
        tracks = ShanaTracks.Select(t =>
        {
            var lvl = ShanaEarned(t.Key);
            return new { key = t.Key, have = ShanaHave(t.Key), level = lvl, next = lvl < ShanaMax ? t.Need[lvl] : (long?)null };
        }),
    };

    static object CatalogShana() => new
    {
        max = ShanaMax,
        tracks = ShanaTracks.Select(t => new { key = t.Key, icon = t.Icon, name = t.Name, what = t.What, need = t.Need, perks = t.Perks }),
    };
}
