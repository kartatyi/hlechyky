using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Ранг цеху: що треба, щоб його дістати (обпалено виробів за весь час, покладено на вози, розписів у колекції) і
/// майстерштук — виріб одного з <paramref name="PieceWares"/> у розписі з <paramref name="PieceStyles"/>
/// (порожній рядок — розпис будь-який) якості не нижче за <paramref name="PieceQuality"/>. Котрий саме — від
/// зерна ніка, щоб у друзів були різні.
/// </summary>
public sealed record ClickerGuildRank(string Key, string Name, long Fired, long Given, int Styles,
    string[] PieceWares, string[] PieceStyles, string Perk, int PieceQuality = 3);

/// <summary>
/// Цех: денний віз для всіх гончарів, дарунки, хата друга, похвала в Журнал, цехові ранги (контракт гачків —
/// docs/games/specs/clicker-v7.md §2.7, як реалізовано — docs/games/specs/clicker-v7-guild.md). Спільне живе в
/// <see cref="ClickerGuildService"/>; тут — те, що належить самому гончареві: ранг, скільки поклав на вози й
/// подарував, полиця дарунків, коли хвалився. Без сервісу (тести, гола гра) цех зачинений, але гра не падає, а
/// ранг і його перки — зі збереження — лишаються.
///
/// Нічого змагального: віз один на всіх, нагорода кожному однакова, пропущений день нічого не забирає, престиж
/// «Обпал» цеху не чіпає.
/// </summary>
public sealed partial class Clicker
{
    public static readonly ClickerGuildRank[] GuildRanks =
    [
        new("apprentice", "Учень", 0, 0, 0, [], [], "Кожен починав з глини на руках"),
        new("journeyman", "Челядник", 50, 10, 0, ["jug", "makitra"], [""],
            "Автогорно: підмайстри самі палять повну сушарню (якість звичайна)"),
        new("master", "Майстер", 500, 100, 3, ["dish", "candle", "whistle", "barrel"], ["gavarets", "vasylkiv", "bubnivka", "kosiv"],
            "+2 місця в горні й наступний виріб відкривається на щабель раніше"),
        new("guildmaster", "Цехмістр", 3000, 400, 6, ["tile", "kumanets", "ram"], ["kosiv", "opishnia", "mezhyhirya"],
            "Нагорода воза ×1,5, титул у хаті й у похвалах"),
        // v9 §E.4: п'ятий ранг для тих, кому цехмістра вже мало. Майстерштук один на всіх — розкішний лев:
        // розкішна якість буває лише з розписаної партії, тож це справді вінець роботи.
        new("elder", "Старійшина", 10_000, 1500, 8, ["lion"], [""],
            "+4 місця в горні, нагорода воза ×2 і титул «Старійшина»", 4),
    ];

    public const int GuildJourneyman = 1, GuildMaster = 2, GuildMasterOfGuild = 3, GuildElder = 4;
    /// <summary>Похвалитись у Журнал — не частіше.</summary>
    public static readonly TimeSpan BragEvery = TimeSpan.FromMinutes(15);
    /// <summary>Цехмістрові нагорода воза більша, старійшині — ще більша.</summary>
    public const double GuildmasterWagon = 1.5, ElderWagon = 2;
    /// <summary>Майстер цеху: стільки ще місць у горні (підключає горно); старійшина — стільки.</summary>
    public const int MasterKilnSlots = 2, ElderKilnSlots = 4;
    /// <summary>Дно нагороди воза на голому колі: стільки кліків за кожну хвилину.</summary>
    public const double WagonClicksPerMinute = 20;
    public const int GiftsForAchievement = 10, TreatsForAchievement = 10;
    /// <summary>
    /// Пай воза (§E.1): нагорода множиться на «мої вироби / <see cref="ClickerGuildService.PerPotter"/>», але не
    /// менше за пів паю і не більше за три. Хто возив сам-один — дістає втричі, хто заскочив на п'ятірку — половину.
    /// </summary>
    public const double ShareMin = 0.5, ShareMax = 3;

    /// <summary>Рід виробу для узгодження «дзвінкий/дзвінка/дзвінке» в похвалі.</summary>
    static readonly Dictionary<string, char> WareGender = new(StringComparer.Ordinal)
    {
        ["bowl"] = 'f', ["makitra"] = 'f', ["tile"] = 'f', ["barrel"] = 'n',
    };

    /// <summary>Розпис у похвалі: «косівський розпис», «межигірський фаянс».</summary>
    static readonly Dictionary<string, string> StylePhrase = new(StringComparer.Ordinal)
    {
        [""] = "без розпису, чиста глина",
        ["gavarets"] = "гаварецька чорнодимлена",
        ["vasylkiv"] = "васильківська майоліка",
        ["bubnivka"] = "бубнівський розпис",
        ["kosiv"] = "косівський розпис",
        ["opishnia"] = "опішнянський розпис",
        ["mezhyhirya"] = "межигірський фаянс",
        ["petrykivka"] = "петриківський розпис",
        ["trypillia"] = "трипільський орнамент",
    };

    ClickerGuildService? _guildSvc;
    /// <summary>Чи вже сказали цеху «я тут» за життя цієї кімнати.</summary>
    bool _guildHello;
    int _guildRank;
    long _guildGiven;
    int _guildClaims;
    int _giftsSent;
    int _giftsGot;
    int _treatsSent;
    readonly List<GuildGift> _giftShelf = [];
    DateTimeOffset _bragAt;
    bool _autoKilnOff;
    /// <summary>Підмайстер друга гостює до цієї миті: ліплення ×0,5 роботи (§E.2).</summary>
    DateTimeOffset _lendUntil;
    string _lendFrom = "";
    /// <summary>Похвала друга гріє до цієї миті: +10 % до всього (§E.2).</summary>
    DateTimeOffset _cheerUntil;
    string _cheerFrom = "";
    /// <summary>Останній гостинець — для стрічки в отримувача (клієнт сам пам'ятає, який уже показував).</summary>
    DateTimeOffset _treatAt;
    string _treatFrom = "";
    double _treatPots;

    string GuildNick => (Ctx.NickOf(0) ?? "").Trim();
    string GuildKey => ClickerGuildService.Key(Ctx.NickOf(0));

    // ---------- перки для інших пакетів ----------

    /// <summary>
    /// Челядник: «автогорно» — підмайстри самі палять повну сушарню з якістю 1, без мінігри й без тріщин. Горно (B2)
    /// перевіряє цей прапорець у своєму Sync: сушарня повна й горно холодне → <c>TakeDry</c> усе сухе, <c>AddFired</c>,
    /// <c>PutItems(…, quality: 1)</c>. Гравець може вимкнути (дія <c>guild { op: "auto", on }</c>).
    /// </summary>
    internal bool GuildAutoKiln => _guildRank >= GuildJourneyman && !_autoKilnOff;

    /// <summary>Майстер цеху: +2 місця в горні, старійшина: +4 (горно додає до своєї місткості).</summary>
    internal int GuildKilnSlots => _guildRank >= GuildElder ? ElderKilnSlots : _guildRank >= GuildMaster ? MasterKilnSlots : 0;

    /// <summary>
    /// Ранг відкриває вироби наперед: майстер — на щабель раніше, цехмістр — на два, старійшина — на три
    /// (v9 §E.4; так і нові вироби «Ремесла» потрапляють у руки тим, хто вже виніс цех на плечах).
    /// Щабель рахується за таблицею <see cref="Wares"/>, тож ніякого окремого списку тримати не треба.
    /// </summary>
    bool GuildWareOpen(string ware)
    {
        var steps = _guildRank - GuildMaster + 1;
        if (steps <= 0) return false;
        var i = Array.FindIndex(Wares, w => w.Key == ware);
        return i > 0 && _total >= Wares[Math.Max(0, i - steps)].Unlock;
    }

    /// <summary>Похвала друга: +10 % до всього, поки не вистигла (§E.2).</summary>
    double GuildAllMult => Ctx.Clock.UtcNow < _cheerUntil ? ClickerGuildService.CheerMult : 1;

    /// <summary>Підмайстер друга в гостях: робота ліплення вдвічі менша, поки він не пішов (§E.2).</summary>
    internal double GuildWorkMult => Ctx.Clock.UtcNow < _lendUntil ? ClickerGuildService.LendWork : 1;

    /// <summary>Ранг рахує обпалені з <see cref="FiredTotal"/>, тож окремий лічильник тут не потрібен.</summary>
    void GuildOnFired(ItemInfo item, int n) { }

    // ---------- життя партії ----------

    /// <summary>
    /// Діючі бафи від друзів (§E.2): до котрої миті й від кого. Останній гостинець живе тут же — не як баф, а як
    /// пам'ять для стрічки: клієнт мусить сказати «Микола прислав гостинець», навіть якщо гончар саме клацав колом.
    /// Старе збереження їх не має — і добре.
    /// </summary>
    sealed record GuildBuffs(
        DateTimeOffset Lend = default, string? LendFrom = null, DateTimeOffset Cheer = default, string? CheerFrom = null,
        DateTimeOffset Treat = default, string? TreatFrom = null, double TreatPots = 0);

    sealed record GuildRow(
        int Rank = 0, long Given = 0, int Claims = 0, int GiftsSent = 0, int GiftsGot = 0,
        List<GuildGift>? Shelf = null, DateTimeOffset BragAt = default, bool AutoOff = false,
        GuildBuffs? Buffs = null, int TreatsSent = 0);

    GuildRow? SaveGuild() => new(_guildRank, _guildGiven, _guildClaims, _giftsSent, _giftsGot, _giftShelf.ToList(), _bragAt, _autoKilnOff,
        new GuildBuffs(_lendUntil, _lendFrom, _cheerUntil, _cheerFrom, _treatAt, _treatFrom, _treatPots), _treatsSent);

    void LoadGuild(GuildRow? row)
    {
        ClearGuild();
        if (row is null) return;
        _guildRank = Math.Clamp(row.Rank, 0, GuildRanks.Length - 1);
        _guildGiven = Math.Max(0, row.Given);
        _guildClaims = Math.Max(0, row.Claims);
        _giftsSent = Math.Max(0, row.GiftsSent);
        _giftsGot = Math.Max(0, row.GiftsGot);
        _treatsSent = Math.Max(0, row.TreatsSent);
        foreach (var g in row.Shelf ?? [])
            if (ValidGift(g) is { } ok && _giftShelf.Count < ClickerGuildService.ShelfSize) _giftShelf.Add(ok);
        _bragAt = row.BragAt;
        _autoKilnOff = row.AutoOff;
        if (row.Buffs is { } b)
        {
            // Баф не може тривати довше, ніж його заведено: правлена руками база не дасть вічного підмайстра.
            var now = Ctx.Clock.UtcNow;
            _lendUntil = Min(b.Lend, now.AddHours(ClickerGuildService.LendHours));
            _cheerUntil = Min(b.Cheer, now.AddMinutes(ClickerGuildService.CheerMinutes));
            _lendFrom = Who(b.LendFrom);
            _cheerFrom = Who(b.CheerFrom);
            _treatAt = Min(b.Treat, now);
            _treatFrom = Who(b.TreatFrom);
            _treatPots = ToPots(b.TreatPots);
        }
    }

    static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    /// <summary>Ім'я дарувальника з бази: коротке й без порожнечі.</summary>
    static string Who(string? nick)
    {
        var s = (nick ?? "").Trim();
        return s.Length > 24 ? s[..24] : s;
    }

    /// <summary>Дарунок зі збереження чи скриньки: відомий виріб, розпис і якість; ім'я дарувальника — коротке.</summary>
    static GuildGift? ValidGift(GuildGift? g)
    {
        if (g is null || WareOf(g.Ware) is null || g.Quality is < 1 or > QualityMax) return null;
        var style = g.Style ?? "";
        if (style.Length > 0 && Styles.All(s => s.Key != style)) return null;
        var from = (g.From ?? "").Trim();
        if (from.Length > 24) from = from[..24];
        return g with { From = from.Length == 0 ? "друг" : from, Style = style };
    }

    void ClearGuild()
    {
        _guildRank = 0;
        _guildGiven = 0;
        _guildClaims = 0;
        _giftsSent = 0;
        _giftsGot = 0;
        _treatsSent = 0;
        _giftShelf.Clear();
        _bragAt = default;
        _autoKilnOff = false;
        _lendUntil = default;
        _lendFrom = "";
        _cheerUntil = default;
        _cheerFrom = "";
        _treatAt = default;
        _treatFrom = "";
        _treatPots = 0;
    }

    void ResetGuild(DateTimeOffset now)
    {
        // Сервіс — з Ctx.Services, як словники в інших іграх. У тестах без нього цех просто зачинений.
        _guildSvc = Ctx.Services.GetService<ClickerGuildService>();
        _guildHello = false;
        ClearGuild();
    }

    /// <summary>Обпал цеху не чіпає: ранг, внески у вози, дарунки на полиці й лічильники — назавжди.</summary>
    void FireGuild(DateTimeOffset now) { }

    /// <summary>Клейма змінились (обпал): кажемо цеху, щоб наука майстра в решти округи рахувалась від свіжого числа.</summary>
    void GuildStampsChanged(DateTimeOffset now)
    {
        if (_guildSvc is not { } svc || GuildKey.Length == 0) return;
        _guildHello = true;
        svc.Hello(GuildKey, GuildNick, _guildRank, now, _stamps);
    }

    /// <summary>
    /// Найкращий гончар округи, крім себе: його нік і клейма (наука майстра рахується від них). Без цеху — нікого:
    /// тоді й науки нема.
    /// </summary>
    (string Nick, int Stamps) GuildTopStamps() =>
        _guildSvc is { } svc && GuildKey.Length > 0 ? svc.TopStamps(GuildKey) : ("", 0);

    /// <summary>Кожна синхронізація: вперше — «я тут» у список цеху; і дарунки зі скриньки — на полицю.</summary>
    void SyncGuild(DateTimeOffset now, TimeSpan paid)
    {
        if (_guildSvc is not { } svc || GuildKey.Length == 0) return;
        if (!_guildHello)
        {
            _guildHello = true;
            // Разом із клеймами: з них решта округи рахує науку майстра.
            svc.Hello(GuildKey, GuildNick, _guildRank, now, _stamps);
        }
        // Пошту забираємо лише на дії: забрана у виді жила б тільки в пам'яті кімнати (зберігає її каркас після дії), і
        // перезапуск сервера до першого кліка загубив би дарунок назавжди.
        if (!_inAct) return;
        TakeBoosts(svc, now);
        if (svc.TakeMail(GuildKey) is not { } mail) return;
        foreach (var raw in mail)
        {
            if (ValidGift(raw) is not { } g) continue;
            // На полицю, а не в комору: дарунок — пам'ять, а не товар. Інакше двоє друзів ганяли б вироби туди-сюди
            // й продавали. Зате дарунок відкриває клітинку альбому (без зірки й майстерності — див. AlbumOnGift).
            _giftShelf.Insert(0, g with { At = now });
            _giftsGot++;
            TitlesGiftFrom(g.From, now);
            AlbumOnGift(new ItemInfo(g.Ware, g.Style, g.Quality));
            AwayNote($"🎁 Дарунок від {g.From}: {ItemWords(new ItemInfo(g.Ware, g.Style, g.Quality))}");
        }
        if (_giftShelf.Count > ClickerGuildService.ShelfSize)
            _giftShelf.RemoveRange(ClickerGuildService.ShelfSize, _giftShelf.Count - ClickerGuildService.ShelfSize);
    }

    /// <summary>
    /// Допомога, що прийшла від друзів, поки нас не було (§E.2). Гостинець — глеки одразу (СВІЙ пасив за стільки
    /// хвилин), підмайстер і похвала — бафи, що цокають від миті, коли гончар їх прийняв: інакше друг, що допоміг
    /// уночі, подарував би самий лише сон.
    /// </summary>
    void TakeBoosts(ClickerGuildService svc, DateTimeOffset now)
    {
        if (svc.TakeBoosts(GuildKey) is not { } box) return;
        foreach (var b in box)
        {
            var from = Who(b.From) is { Length: > 0 } f ? f : "друг";
            var minutes = Math.Clamp(b.Minutes, 0, ClickerGuildService.LendHours * 60);
            switch (b.Kind)
            {
                case "treat":
                    var gain = TreatGain(minutes);
                    Add(gain);
                    _treatAt = now;
                    _treatFrom = from;
                    _treatPots = gain;
                    AwayNote($"🎁 Гостинець від {from}: +{Short(gain)} {Pots(gain)} — {minutes} хв твого пасиву");
                    break;
                case "lend":
                    _lendUntil = now.AddMinutes(minutes);
                    _lendFrom = from;
                    AwayNote($"🧑‍🎓 Підмайстер від {from} гостює добу — ліплення вдвічі швидше");
                    break;
                case "cheer":
                    _cheerUntil = now.AddMinutes(minutes);
                    _cheerFrom = from;
                    AwayNote($"👏 {from} хвалить твою роботу: +10 % до всього на годину");
                    break;
            }
        }
    }

    /// <summary>Скільки глеків несе гостинець на стільки хвилин: свій пасив, а на голому колі — дно з кліків.</summary>
    double TreatGain(double minutes) =>
        ToPots(Math.Max(PassiveBase * minutes * 60, ClickBase * minutes * WagonClicksPerMinute));

    // ---------- дія guild { op, … } ----------

    ActResult? ActGuild(string action, JsonElement payload)
    {
        if (action != "guild") return null;
        // Вимикач палія — річ горна, а не цеху: працює й без сервісу (прокачаний «Палій» без рангу).
        if (Str(payload, "op") == "auto") return GuildAuto(payload);
        if (_guildSvc is not { } svc || GuildKey.Length == 0) return ActResult.Fail("Цех зараз зачинений");
        var now = Ctx.Clock.UtcNow;
        return Str(payload, "op") switch
        {
            "give" => GuildGive(svc, payload, now),
            "claim" => GuildClaim(svc, payload, now),
            "gift" => GuildGiftTo(svc, payload, now),
            "treat" => GuildTreat(svc, payload, now),
            "lend" => GuildLend(svc, payload, now),
            "cheer" => GuildCheer(svc, payload, now),
            "brag" => GuildBrag(payload, now),
            "masterpiece" => GuildMasterpiece(svc, now),
            "auto" => GuildAuto(payload),
            _ => ActResult.Fail("Такого в цеху не роблять"),
        };
    }

    /// <summary>Покласти вироби з комори на віз дня.</summary>
    ActResult GuildGive(ClickerGuildService svc, JsonElement payload, DateTimeOffset now)
    {
        if (ParseItem(Str(payload, "key")) is not { } it) return ActResult.Fail("Такого виробу в коморі нема");
        var raw = Num(payload, "n");
        if (raw is <= 0) return ActResult.Fail("Скільки покласти — хоч один");
        var have = ItemCount(x => x == it);
        if (have <= 0) return ActResult.Fail("Такого виробу в коморі нема");
        var n = TakeItems(x => x == it, (int)Math.Min(Math.Clamp(raw ?? 1, 1, StoreCapNow), have));
        var r = svc.Give(GuildKey, GuildNick, it.Ware, n, now);
        _guildGiven += n;
        if (r.Reached > 0)
        {
            var who = string.Join(", ", r.Wagon.Givers.Select(g => g.Nick));
            Ctx.Log($"🐴 Віз цеху гончарів — {ClickerGuildService.TierNames[r.Reached]}! Разом клали: {who}. Хто поклав хоч "
                + $"{ClickerGuildService.MinGive}, забирайте нагороду у вкладці «Цех»");
            // Золотий віз — рідкість: саме тут щось у хаті й може знайтись (§E.5).
            if (r.Reached >= ClickerGuildService.TierShare.Length - 1) Wonder("wagon-gold");
        }
        var text = $"🐴 {n} × {WareOf(it.Ware)!.Name.ToLowerInvariant()} на віз — він повний на {Pct(r.Wagon)} %";
        if (r.Wagon.Mine < ClickerGuildService.MinGive) text += $" · ще {ClickerGuildService.MinGive - r.Wagon.Mine} — і нагорода твоя";
        return ActResult.Accept(text);
    }

    static string Pct(WagonInfo w) => w.Goal > 0 ? Math.Floor(100.0 * w.Total / w.Goal).ToString("0", Uk) : "0";

    /// <summary>
    /// Пай воза (§E.1): скільки нагороди належить тому, хто поклав <paramref name="mine"/> виробів. Дванадцять —
    /// повний пай, тридцять шість — три (більше не буває), п'ять — половина. Так «що 15, що 100 — однаково»
    /// більше не працює, але й той, хто ледве встиг, не лишається ні з чим.
    /// </summary>
    public static double WagonShare(int mine) =>
        Math.Clamp(mine / (double)ClickerGuildService.PerPotter, ShareMin, ShareMax);

    /// <summary>Множник нагороди воза за ранг: цехмістрові ×1,5, старійшині ×2.</summary>
    double WagonRank => _guildRank >= GuildElder ? ElderWagon : _guildRank >= GuildMasterOfGuild ? GuildmasterWagon : 1;

    /// <summary>Скільки глеків дасть нагорода воза за рівні від <paramref name="was"/> до <paramref name="tier"/>.</summary>
    double WagonReward(int tier, int was, int mine, out double minutes)
    {
        var last = ClickerGuildService.TierMinutes.Length - 1;
        minutes = (ClickerGuildService.TierMinutes[Math.Clamp(tier, 0, last)] - ClickerGuildService.TierMinutes[Math.Clamp(was, 0, last)])
            * WagonRank * WagonShare(mine);
        if (minutes <= 0) return 0;
        var byPassive = PassiveBase * minutes * 60;
        var floor = ClickBase * minutes * WagonClicksPerMinute;
        return ToPots(Math.Max(byPassive, floor));
    }

    ActResult GuildClaim(ClickerGuildService svc, JsonElement payload, DateTimeOffset now)
    {
        var day = Str(payload, "day");
        var r = svc.Claim(GuildKey, day.Length > 0 ? day : null, now);
        if (r.Error is { } why) return ActResult.Fail(why);
        var gain = WagonReward(r.Tier, r.Was, r.Mine, out var minutes);
        Add(gain);
        _guildClaims++;
        if (_guildClaims == 1) Achieve("potter-wagon");
        var pay = WagonShare(r.Mine);
        return ActResult.Accept($"🛒 Віз повернувся з ярмарку ({ClickerGuildService.TierNames[r.Tier]}): +{Short(gain)} {Pots(gain)} — "
            + $"{minutes.ToString("0", Uk)} хв твого пасиву за {pay.ToString("0.##", Uk)} {ShareWord(pay)}");
    }

    /// <summary>«1 пай», «2 паї», «1,25 паю» — дробові паї стоять у родовому однини.</summary>
    static string ShareWord(double pay) => pay % 1 != 0 ? "паю" : Plural(pay, "пай", "паї", "паїв");

    /// <summary>Дарунок другові: один виріб із комори.</summary>
    ActResult GuildGiftTo(ClickerGuildService svc, JsonElement payload, DateTimeOffset now)
    {
        if (ParseItem(Str(payload, "key")) is not { } it || ItemCount(x => x == it) <= 0)
            return ActResult.Fail("Такого виробу в коморі нема");
        var to = Str(payload, "nick").Trim();
        if (svc.Gift(GuildKey, GuildNick, to, it, now) is { } why) return ActResult.Fail(why);
        TakeItems(x => x == it, 1);
        _giftsSent++;
        TitlesGiftTo(to, now);
        if (_giftsSent == GiftsForAchievement) Achieve("potter-gift");
        return ActResult.Accept($"🎁 {Capital(ItemWords(it, style: false))} — дарунок для {to} уже в дорозі");
    }

    // ---------- допомога другові (§E.2) ----------

    /// <summary>
    /// Гостинець: віддаєш глеками стільки хвилин СВОГО пасиву, друг дістає вдвічі більше хвилин СВОГО. Тому
    /// багатий друг може нормально підбустити бідного (для нього це копійки, для того — день росту), а бідний
    /// багатого — ні: той дістане лише свої дві години на день, і не більше.
    /// </summary>
    ActResult GuildTreat(ClickerGuildService svc, JsonElement payload, DateTimeOffset now)
    {
        var minutes = (int)Math.Clamp(Num(payload, "minutes") ?? 0, 0, int.MaxValue);
        if (Array.IndexOf(ClickerGuildService.TreatSizes, minutes) < 0)
            return ActResult.Fail("Гостинець буває на 10, 30 або 60 хвилин");
        var cost = TreatGain(minutes);
        if (_pots < cost) return ActResult.Fail($"Гостинець на {minutes} хв коштує {Short(cost)} {Pots(cost)} — бракує {Short(cost - _pots)}");
        var to = Str(payload, "to").Trim();
        if (svc.Boost(GuildKey, GuildNick, to, "treat", minutes, now) is { } why) return ActResult.Fail(why);
        _pots -= cost;
        _treatsSent++;
        if (_treatsSent == TreatsForAchievement) Achieve("potter-treat");
        Wonder("treat");
        return ActResult.Accept($"🎁 Гостинець для {to}: −{Short(cost)} {Pots(cost)} у тебе, "
            + $"{minutes * ClickerGuildService.TreatBack} хв його власного пасиву — йому");
    }

    /// <summary>Підмайстер у гості: раз на день, і в друга добу ліплять удвічі швидше.</summary>
    ActResult GuildLend(ClickerGuildService svc, JsonElement payload, DateTimeOffset now)
    {
        var to = Str(payload, "to").Trim();
        if (svc.Boost(GuildKey, GuildNick, to, "lend", 0, now) is { } why) return ActResult.Fail(why);
        return ActResult.Accept($"🧑‍🎓 Підмайстер пішов до {to} на добу — там ліпитимуть удвічі швидше");
    }

    /// <summary>Похвала: раз на день на друга, і в нього годину +10 % до всього.</summary>
    ActResult GuildCheer(ClickerGuildService svc, JsonElement payload, DateTimeOffset now)
    {
        var to = Str(payload, "to").Trim();
        if (svc.Boost(GuildKey, GuildNick, to, "cheer", 0, now) is { } why) return ActResult.Fail(why);
        return ActResult.Accept($"👏 Добре слово для {to}: годину все йтиме на 10 % краще");
    }

    /// <summary>Похвалитись виробом із комори в Журнал — раз на чверть години.</summary>
    ActResult GuildBrag(JsonElement payload, DateTimeOffset now)
    {
        if (ParseItem(Str(payload, "key")) is not { } it || ItemCount(x => x == it) <= 0)
            return ActResult.Fail("Хвалитись можна лише тим, що лежить у коморі");
        if (now < _bragAt) return ActResult.Fail($"Дай селу надивуватись: наступна похвала за {Wait(_bragAt - now)}");
        _bragAt = now + BragEvery;
        Ctx.Log(BragText(GuildNick, _guildRank, it));
        return ActResult.Accept("🏺 Усе село вже знає!");
    }

    /// <summary>Титул перед ніком: цехмістр і старійшина його мають, решта — ні.</summary>
    internal static string TitleOf(int rank) =>
        rank >= GuildElder ? "Старійшина " : rank >= GuildMasterOfGuild ? "Цехмістр " : "";

    /// <summary>«🏺 Цехмістр Микола хвалиться: дзвінкий куманець — косівський розпис».</summary>
    internal static string BragText(string nick, int rank, ItemInfo it) =>
        $"🏺 {TitleOf(rank)}{nick} хвалиться: {ItemWords(it)}";

    /// <summary>«дзвінка макітра — бубнівський розпис» (без <paramref name="style"/> — лише «дзвінка макітра»).</summary>
    internal static string ItemWords(ItemInfo it, bool style = true)
    {
        var head = $"{QualityWord(it.Ware, it.Quality)} {(WareOf(it.Ware)?.Name ?? it.Ware).ToLowerInvariant()}";
        return style ? $"{head} — {StylePhrase.GetValueOrDefault(it.Style, it.Style)}" : head;
    }

    /// <summary>«дзвінка», «розкішне» — прикметник якості, узгоджений з родом виробу (розкішний — це Q4 з §B1.2).</summary>
    internal static string QualityWord(string ware, int quality)
    {
        string[] words = WareGender.GetValueOrDefault(ware, 'm') switch
        {
            'f' => ["", "звичайна", "добра", "дзвінка", "розкішна"],
            'n' => ["", "звичайне", "добре", "дзвінке", "розкішне"],
            _ => ["", "звичайний", "добрий", "дзвінкий", "розкішний"],
        };
        return words[Math.Clamp(quality, 1, 4)];
    }

    static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Майстерштук для рангу: виріб і розпис від зерна ніка, щоб друзі не здавали однакових.</summary>
    internal (string Ware, string Style, int Quality) PieceFor(int rank)
    {
        var r = GuildRanks[Math.Clamp(rank, 1, GuildRanks.Length - 1)];
        var seed = Days.Seed("clicker-masterpiece", $"{GuildKey}:{rank}");
        return (r.PieceWares[seed % r.PieceWares.Length], r.PieceStyles[(seed / 7) % r.PieceStyles.Length], r.PieceQuality);
    }

    /// <summary>Кращий за потрібне теж приймають: хочуть дзвінкий — розкішний не гірший (§B1.2).</summary>
    static bool PieceMatch(ItemInfo x, (string Ware, string Style, int Quality) piece) =>
        x.Ware == piece.Ware && x.Quality >= piece.Quality && (piece.Style.Length == 0 || x.Style == piece.Style);

    /// <summary>Чого бракує до наступного рангу (без майстерштука); порожньо — готовий.</summary>
    List<string> RankMissing(ClickerGuildRank next)
    {
        var miss = new List<string>();
        if (FiredTotal < next.Fired) miss.Add($"обпалити ще {Short(next.Fired - FiredTotal)} {WaresWord(next.Fired - FiredTotal)}");
        if (_guildGiven < next.Given) miss.Add($"покласти на вози ще {Short(next.Given - _guildGiven)}");
        if (_styles.Count < next.Styles) miss.Add($"зібрати ще {next.Styles - _styles.Count} розписи");
        return miss;
    }

    /// <summary>Здати майстерштук: умови рангу виконано й дзвінкий виріб потрібного розпису лежить у коморі.</summary>
    ActResult GuildMasterpiece(ClickerGuildService svc, DateTimeOffset now)
    {
        if (_guildRank >= GuildRanks.Length - 1) return ActResult.Fail("Ти вже старійшина — вище в цеху лише небо");
        var next = GuildRanks[_guildRank + 1];
        if (RankMissing(next) is { Count: > 0 } miss) return ActResult.Fail($"До рангу «{next.Name}» ще: {string.Join(", ", miss)}");
        var piece = PieceFor(_guildRank + 1);
        if (ItemCount(x => PieceMatch(x, piece)) <= 0)
            return ActResult.Fail($"Цех чекає майстерштук: {PieceWords(piece)}");
        TakeItems(x => PieceMatch(x, piece), 1);
        _guildRank++;
        svc.Hello(GuildKey, GuildNick, _guildRank, now);
        Ctx.Log($"🎓 {GuildNick} склав(ла) майстерштук — тепер {next.Name.ToLowerInvariant()} цеху гончарів");
        if (_guildRank == GuildMasterOfGuild) Achieve("potter-rank");
        if (_guildRank == GuildElder) Achieve("potter-elder");
        return ActResult.Accept($"🎓 Цех прийняв майстерштук: ти {next.Name.ToLowerInvariant()}! {next.Perk}");
    }

    static string PieceWords((string Ware, string Style, int Quality) piece)
    {
        var w = WareOf(piece.Ware)!;
        return $"{QualityWord(piece.Ware, piece.Quality)} {w.Name.ToLowerInvariant()}"
            + (piece.Style.Length > 0 ? $" — {StylePhrase[piece.Style]}" : " у будь-якому розписі");
    }

    ActResult GuildAuto(JsonElement payload)
    {
        if (!KilnAutoCan) return ActResult.Fail("Автогорно — перк челядника або прокачаного Палія");
        var on = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("on", out var v) && v.ValueKind == JsonValueKind.True;
        _autoKilnOff = !on;
        return ActResult.Accept(on ? "🔥 Підмайстри палитимуть повну сушарню самі" : "🔥 Горно — лише твоїми руками");
    }

    // ---------- вид і каталог ----------

    object? ViewGuild(DateTimeOffset now)
    {
        if (_guildSvc is not { } svc || GuildKey.Length == 0)
            return new { enabled = false, rank = _guildRank };
        var s = svc.Summary(GuildKey, now);
        return new
        {
            enabled = true,
            day = ClickerGuildService.WagonView(s.Today),
            // Учорашній віз — лише коли там є що забрати чи на що глянути.
            prev = s.Prev.Total > 0 ? ClickerGuildService.WagonView(s.Prev) : null,
            claims = new[] { s.Prev, s.Today }
                .Where(w => w.Mine >= ClickerGuildService.MinGive && w.Tier > w.Claimed)
                .Select(w => new
                {
                    day = w.Day, tier = w.Tier, was = w.Claimed, mine = w.Mine, share = WagonShare(w.Mine),
                    pots = WagonReward(w.Tier, w.Claimed, w.Mine, out var m), minutes = m,
                }),
            gifts = new { left = s.GiftsLeft, sent = _giftsSent, got = _giftsGot },
            // Що ще можна зробити для друзів сьогодні — і скільки гостинців прийняв сам (§E.2).
            help = new
            {
                treatLeft = s.Help.TreatLeft, lendLeft = s.Help.LendLeft, cheered = s.Help.Cheered,
                sizes = ClickerGuildService.TreatSizes.Select(m => new { minutes = m, pots = TreatGain(m) }),
                treats = _treatsSent,
            },
            // Діючі бафи: плашка в отримувача й підказка, від кого вони.
            buffs = new
            {
                lend = _lendUntil > now ? new { until = _lendUntil, from = _lendFrom } : null,
                cheer = _cheerUntil > now ? new { until = _cheerUntil, from = _cheerFrom } : null,
                // Гостинець — не баф, а подія: клієнт скаже про неї в стрічці, коли побачить нову мить.
                treat = _treatAt != default ? new { at = _treatAt, from = _treatFrom, pots = _treatPots } : null,
            },
            shelf = _giftShelf.Select(g => new { from = g.From, ware = g.Ware, style = g.Style, q = g.Quality, at = g.At }),
            rank = _guildRank,
            given = _guildGiven,
            next = NextRankView(),
            autoKiln = KilnAutoOn,
            autoOff = _autoKilnOff,
            kilnSlots = GuildKilnSlots,
            wagonMult = WagonRank,
            bragAt = _bragAt,
        };
    }

    object? NextRankView()
    {
        if (_guildRank >= GuildRanks.Length - 1) return null;
        var next = GuildRanks[_guildRank + 1];
        var piece = PieceFor(_guildRank + 1);
        return new
        {
            rank = _guildRank + 1,
            fired = new { have = FiredTotal, need = next.Fired },
            given = new { have = _guildGiven, need = next.Given },
            styles = new { have = _styles.Count, need = next.Styles },
            piece = new { ware = piece.Ware, style = piece.Style, q = piece.Quality, have = ItemCount(x => PieceMatch(x, piece)) > 0 },
            ready = RankMissing(next).Count == 0,
        };
    }

    object? CatalogGuild() => new
    {
        ranks = GuildRanks.Select(r => new
        {
            key = r.Key, name = r.Name, perk = r.Perk, fired = r.Fired, given = r.Given, styles = r.Styles, q = r.PieceQuality,
        }),
        tiers = ClickerGuildService.TierNames.Select((name, i) => new
        {
            name, share = ClickerGuildService.TierShare[i], minutes = ClickerGuildService.TierMinutes[i],
        }),
        minGive = ClickerGuildService.MinGive,
        perPotter = ClickerGuildService.PerPotter,
        giftsPerDay = ClickerGuildService.GiftsPerDay,
        shelfSize = ClickerGuildService.ShelfSize,
        bragMinutes = BragEvery.TotalMinutes,
        guildmasterWagon = GuildmasterWagon,
        elderWagon = ElderWagon,
        shareMin = ShareMin,
        shareMax = ShareMax,
        treatSizes = ClickerGuildService.TreatSizes,
        treatBack = ClickerGuildService.TreatBack,
        treatCap = ClickerGuildService.TreatCapMinutes,
        lendHours = ClickerGuildService.LendHours,
        lendWork = ClickerGuildService.LendWork,
        cheerMinutes = ClickerGuildService.CheerMinutes,
        cheerMult = ClickerGuildService.CheerMult,
        styleWords = StylePhrase,
    };
}
