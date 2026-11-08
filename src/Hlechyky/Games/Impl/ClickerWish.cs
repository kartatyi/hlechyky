using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Чарівний глек і бажання (ClickerWish.cs): коли зараз буде/була спроба, чи спіймано, яка пропозиція чекає (три картки),
/// і чинні ефекти бажань. Старе збереження — порожньо.
/// </summary>
/// <param name="Day">Київська доба, до якої належать спроби.</param>
/// <param name="Tries">Скільки спроб за добу вже витрачено (розбитий чарівний глек — спроба).</param>
/// <param name="At">Від якої миті чарівний глек може впасти замість звичайного (default — нема).</param>
/// <param name="Done">Чарівний глек за цю добу вже спіймано.</param>
/// <param name="Cards">Три картки бажань, що чекають вибору.</param>
/// <param name="Until">До якої миті пропозиція чекає (24 год від спійманого).</param>
/// <param name="Stoker">До якої миті палій пече як ручний.</param>
/// <param name="Cat">До якої миті кіт не спить.</param>
public sealed record WishRow(string Day = "", int Tries = 0, DateTimeOffset At = default, bool Done = false, List<string>? Cards = null,
    DateTimeOffset Until = default, DateTimeOffset Stoker = default, DateTimeOffset Cat = default);

/// <summary>
/// «✨ Глек з бажанням» (v13 Г7, docs/games/specs/clicker-v13.md). Раз на добу кожному: від першої дії руками за добу у
/// випадкову мить 2–40 хв замість звичайного глека з полиці падає чарівний — світиться й висить 12 с. Прогавив — ще до
/// двох спроб того ж дня, через 10–20 хв. Лише коли гончар справді тут (руки свіжі): глек, що впав без нього, спроби не
/// з'їдає. Спіймав — звичайний виграш і вікно з трьома картками бажань (щодня інші, лише доступні): обрати собі
/// (<c>wish {pick}</c>) чи подарувати другові (<c>wish {pick, gift}</c>). Незроблений вибір чекає 24 год.
/// </summary>
public sealed partial class Clicker
{
    /// <summary>Чарівний глек висить стільки, звичайний — 3,2 с; спроб на добу; коли чекати першого й повторного.</summary>
    public static readonly TimeSpan MagicShown = TimeSpan.FromSeconds(12), WishKeep = TimeSpan.FromHours(24);
    public const int MagicTries = 3, MagicFirstMin = 2, MagicFirstMax = 40, MagicRetryMin = 10, MagicRetryMax = 20;
    /// <summary>Скільки хвилин «Свята» й «Кошика» дають їхні бажання, хвилин гри — «Мішок глеків», Слави — «Слава».</summary>
    public const int WishBankMinutes = 30, WishMoneyMinutes = 60, WishSlava = 300;
    /// <summary>Палій з бажанням пече як ручний на ідеальному жарі (дзвінких 30 %); скільки це триває.</summary>
    public const double WishStokerShine = 1.0;
    public static readonly TimeSpan WishStokerFor = TimeSpan.FromHours(2);

    public sealed record WishDef(string Key, string Icon, string Name, string Text);

    public static readonly WishDef[] Wishes =
    [
        new("feast", "🎉", "Свято", "30 хв «Свята» в запас: година ×2 до всього, коли захочеш"),
        new("glory", "⭐", "Слава", "+300 Слави у світі"),
        new("basket", "🧺", "Кошик", "30 хв «Кошика» в запас: глеки з полиці ×3"),
        new("cat", "🐈", "Кіт на варті", "Кіт не спить до шостої ранку, глеки падають частіше"),
        new("stoker", "🔥", "Жар палія", "2 год палій пече як ручний: дзвінких 30 %"),
        new("ship", "🚢", "Корабель", "Наступний корабель приходить до Пристані одразу"),
        new("money", "💰", "Мішок глеків", "60 хв гри грошима"),
        new("shard", "🧱", "Уламок", "Уламок до Зали скарбів"),
    ];

    static WishDef WishOf(string key) => Wishes.First(w => w.Key == key);

    /// <summary>Джерело випадку для чарівного глека (окреме від <c>Ctx.Rng</c>, щоб не зсувати решту гри); тести підставляють своє.</summary>
    public Random WishRng { get; set; } = Random.Shared;

    string _mwDay = "";
    int _mwTries;
    DateTimeOffset _mwAt;
    bool _mwDone;
    readonly List<string> _wishCards = [];
    DateTimeOffset _wishUntil, _wishStokerUntil, _catAwakeUntil;

    /// <summary>Кіт на варті (бажання): не спить і скидає глеки частіше.</summary>
    bool CatAwake => Ctx.Clock.UtcNow < _catAwakeUntil;

    /// <summary>Чи може це бажання спрацювати саме в цього гончаря (інакше воно в пул не потрапляє).</summary>
    bool WishCan(string key) => key switch
    {
        "glory" => TradeOpen,
        "stoker" => KilnAutoCan,
        "ship" => TradeOpen && _ships.Count < TradeShipsMax,
        "shard" => TreasureOpen && (_treasure & TreasureAllMask) != TreasureAllMask,
        "feast" or "basket" or "cat" or "money" => true,
        _ => false,
    };

    /// <summary>Наступні 06:00 за Києвом.</summary>
    static DateTimeOffset NextMorning(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Days.Kyiv);
        var six = local.Date.AddHours(6);
        if (local.DateTime >= six) six = six.AddDays(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(six, Days.Kyiv), TimeSpan.Zero);
    }

    // ---------- чарівний глек ----------

    /// <summary>
    /// Кожна справжня дія руками: нова доба — перша дія розкладає, коли впаде чарівний глек (2–40 хв від цієї миті).
    /// </summary>
    void WishTouch(DateTimeOffset now)
    {
        var day = Days.Today(Ctx.Clock);
        if (_mwDay == day) return;
        _mwDay = day;
        _mwTries = 0;
        _mwDone = false;
        _mwAt = now + TimeSpan.FromSeconds(MagicFirstMin * 60 + WishRng.NextDouble() * (MagicFirstMax - MagicFirstMin) * 60);
    }

    /// <summary>
    /// Чи наступний глек з полиці (який упаде о <paramref name="at"/>, розклад від <paramref name="from"/>) — чарівний: час настав,
    /// спроби лишились, доба та сама, а гончар біля кола (руки свіжі) і кіт не спить.
    /// </summary>
    bool MagicDue(DateTimeOffset from, DateTimeOffset at) =>
        _mwAt != default && !_mwDone && _mwTries < MagicTries && _mwDay == Days.Of(at) && at >= _mwAt
        && from - _handsAt <= EventGap && !Napping;

    /// <summary>
    /// Чарівний глек розбився. <paramref name="counted"/> — гончар був тут і мав його спіймати: спроба витрачена, наступна
    /// через 10–20 хв (якщо лишилась). Не тут (відійшов, Око майстра) — спроба ціла, глек чекає, поки гончар повернеться.
    /// </summary>
    void MagicMissed(bool counted, DateTimeOffset now)
    {
        if (!counted) return;
        _mwTries++;
        if (_mwTries >= MagicTries)
        {
            _mwAt = default;
            _feastToasts.Add("✨ Чарівний глек розбився — на сьогодні спроби скінчились, завтра він знову заглядатиме");
            return;
        }
        var wait = TimeSpan.FromSeconds(MagicRetryMin * 60 + WishRng.NextDouble() * (MagicRetryMax - MagicRetryMin) * 60);
        _mwAt = now + wait;
        _feastToasts.Add($"✨ Чарівний глек розбився — ще {MagicTries - _mwTries} {Plural(MagicTries - _mwTries, "спроба", "спроби", "спроб")} сьогодні");
    }

    /// <summary>Чарівний глек спіймано: три картки бажань чекають вибору добу.</summary>
    string MagicCaught(DateTimeOffset now)
    {
        _mwDone = true;
        _mwAt = default;
        var day = Days.Today(Ctx.Clock);
        var pool = Wishes.Where(w => WishCan(w.Key)).Select(w => w.Key).ToList();
        var rng = new Random(Days.Seed("wish:" + Auth.NickKey(Ctx.NickOf(0) ?? ""), day));
        for (var i = pool.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        _wishCards.Clear();
        _wishCards.AddRange(pool.Take(3));
        _wishUntil = now + WishKeep;
        _viewVersion++;
        return " · ✨ Чарівний глек! Обери бажання";
    }

    // ---------- вибір бажання ----------

    /// <summary><c>wish { pick }</c> — узяти собі; <c>wish { pick, gift }</c> — подарувати другові (він отримає те саме).</summary>
    ActResult MagicWish(JsonElement payload)
    {
        var now = Ctx.Clock.UtcNow;
        if (_wishCards.Count == 0 || _wishUntil <= now)
        {
            _wishCards.Clear();
            return ActResult.Fail("Бажання вже не чекає — чарівний глек заглянув знову завтра");
        }
        var pick = Str(payload, "pick");
        if (!_wishCards.Contains(pick)) return ActResult.Fail("Такої картки в бажанні нема");
        var def = WishOf(pick);
        var gift = Str(payload, "gift").Trim();
        if (gift.Length > 0)
        {
            if (_guildSvc is not { } svc || GuildKey.Length == 0) return ActResult.Fail("Цех зараз зачинений");
            if (svc.SendWish(GuildKey, GuildNick, gift, pick, now) is { } why) return ActResult.Fail(why);
            _wishCards.Clear();
            _viewVersion++;
            return ActResult.Accept($"🎁 Бажання «{def.Icon} {def.Name}» полетіло до {NickGen(gift)} — коли прийме, ти отримаєш {ClickerGuildService.WishThanksMinutes} хв гри");
        }
        if (!WishCan(pick)) return ActResult.Fail("Це бажання тобі зараз нікуди прикласти — обери інше");
        var text = WishApply(pick, now);
        _wishCards.Clear();
        _viewVersion++;
        return ActResult.Accept($"✨ Бажання збулося: {def.Icon} {def.Name}" + (text.Length > 0 ? " · " + text : ""));
    }

    /// <summary>Виконати бажання; повертає рядок для тосту (порожній, коли тост уже дала банка бафа).</summary>
    string WishApply(string key, DateTimeOffset now)
    {
        switch (key)
        {
            case "feast":
                BuffBank(FeastKind, WishBankMinutes, "Глек з бажанням");
                return "";
            case "basket":
                BuffBank(BasketKind, WishBankMinutes, "Глек з бажанням");
                return "";
            case "glory":
                _slava += WishSlava;
                return $"Слава +{WishSlava}";
            case "cat":
                _catAwakeUntil = NextMorning(now);
                if (Napping)
                {
                    _napAt = default;
                    if (FallParked) ScheduleFall(now);
                }
                return "кіт не спить до 06:00";
            case "stoker":
                _wishStokerUntil = (_wishStokerUntil > now ? _wishStokerUntil : now) + WishStokerFor;
                return "палій пече як ручний 2 год";
            case "ship":
            {
                var s = ShipNew(now);
                _ships.Add(s);
                if (_shipNext <= now) _shipNext = now + TradeGap;
                return $"до Пристані прийшов корабель з {PortOf(s.Port).From}";
            }
            case "shard":
            {
                var cases = Enumerable.Range(0, TreasureCases).Where(c => !TreasureCaseFull(_treasure, c)).ToList();
                if (cases.Count == 0) return WishApply("money", now);
                var c = cases[TreasureRng.Next(cases.Count)];
                _treasureShards[c]++;
                var text = $"🏛 уламок вітрини «{TreasureCaseNames[c]}» {Math.Min(_treasureShards[c], TreasureGlue)}/{TreasureGlue}";
                if (_treasureShards[c] >= TreasureGlue) text = TreasureGlueOne(c, text);
                return text;
            }
            default:
            {
                var pay = PlayPay(WishMoneyMinutes);
                Add(pay);
                return $"+{PotsShort(pay)}";
            }
        }
    }

    /// <summary>
    /// Друг подарував бажання (з пошти цеху). Застосовується при отриманні; чого цьому гончареві не прикласти (нема Пристані,
    /// палія, вільної вітрини) — стає «Мішком глеків». Дарувальнику лягає подяка в сервісі.
    /// </summary>
    void WishGiven(ClickerGuildService svc, GuildBoost b, string from, DateTimeOffset now)
    {
        var key = b.Kind[ClickerGuildService.WishPrefix.Length..];
        if (Wishes.All(w => w.Key != key)) return;
        var def = WishOf(key);
        var used = WishCan(key) ? key : "money";
        var text = WishApply(used, now);
        var said = $"🎁 {NickGen(from)} подарував(-ла) тобі бажання: {def.Icon} {def.Name}"
            + (used != key ? " — його було нікуди прикласти, тож це мішок глеків" : "")
            + (text.Length > 0 ? " · " + text : "");
        _feastToasts.Add(said);
        AwayNote(said);
        HelpGot("wish", from, now, def.Icon + " " + def.Name);
        svc.WishReceived(ClickerGuildService.Key(b.From), GuildNick, now);
    }

    /// <summary>Подяки за мої подаровані бажання: друг прийняв — +10 хв гри за кожне.</summary>
    void WishCredits(ClickerGuildService svc)
    {
        if (svc.TakeCredits(GuildKey) is not { } list) return;
        foreach (var c in list)
        {
            var pay = PlayPay(ClickerGuildService.WishThanksMinutes);
            Add(pay);
            _feastToasts.Add($"💰 {NickGen(Who(c.By) is { Length: > 0 } w ? w : c.By)} прийняв(-ла) твоє бажання: +{PotsShort(pay)} ({ClickerGuildService.WishThanksMinutes} хв гри)");
        }
    }

    // ---------- вид ----------

    /// <summary>Пропозиція з трьох карток і чинні ефекти. null — нічого не чекає й нічого не діє.</summary>
    object? ViewWish(DateTimeOffset now)
    {
        var offer = _wishCards.Count > 0 && _wishUntil > now;
        var stoker = _wishStokerUntil > now;
        var cat = _catAwakeUntil > now;
        if (!offer && !stoker && !cat && !_fall.Magic) return null;
        return new
        {
            // Глек з полиці, що висить зараз, — чарівний (клієнт світить його та показує довше).
            magic = _fall.Magic,
            offer = offer ? _wishCards.Select(k => new { k, icon = WishOf(k).Icon, name = WishOf(k).Name, text = WishOf(k).Text, can = WishCan(k) }) : null,
            until = offer ? _wishUntil : (DateTimeOffset?)null,
            stoker = stoker ? _wishStokerUntil : (DateTimeOffset?)null,
            cat = cat ? _catAwakeUntil : (DateTimeOffset?)null,
        };
    }

    // ---------- збереження ----------

    void ResetWish()
    {
        _mwDay = "";
        _mwTries = 0;
        _mwAt = default;
        _mwDone = false;
        _wishCards.Clear();
        _wishUntil = _wishStokerUntil = _catAwakeUntil = default;
    }

    WishRow? SaveWish() => _mwDay.Length == 0 && _wishCards.Count == 0 && _wishStokerUntil == default && _catAwakeUntil == default
        ? null
        : new WishRow(_mwDay, _mwTries, _mwAt, _mwDone, _wishCards.Count == 0 ? null : [.. _wishCards], _wishUntil, _wishStokerUntil, _catAwakeUntil);

    void LoadWish(WishRow? row)
    {
        ResetWish();
        if (row is null) return;
        var now = Ctx.Clock.UtcNow;
        _mwDay = row.Day ?? "";
        _mwTries = Math.Clamp(row.Tries, 0, MagicTries);
        _mwAt = row.At > now + TimeSpan.FromDays(1) ? now + TimeSpan.FromDays(1) : row.At;
        _mwDone = row.Done;
        foreach (var k in row.Cards ?? [])
            if (_wishCards.Count < 3 && Wishes.Any(w => w.Key == k) && !_wishCards.Contains(k)) _wishCards.Add(k);
        _wishUntil = _wishCards.Count == 0 ? default : row.Until > now + WishKeep ? now + WishKeep : row.Until;
        _wishStokerUntil = row.Stoker > now + WishStokerFor * 2 ? now + WishStokerFor * 2 : row.Stoker;
        _catAwakeUntil = row.Cat > now + TimeSpan.FromHours(30) ? now + TimeSpan.FromHours(30) : row.Cat;
    }
}
