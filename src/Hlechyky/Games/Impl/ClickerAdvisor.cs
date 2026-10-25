namespace Hlechyky.Games.Impl;

/// <summary>Година гри біля кола за моделлю: глеків з кожного джерела (у глеках за годину).</summary>
public sealed record ClickerHour(double Passive, double Click, double Fall, double Eye, double Merchant, double Idle)
{
    /// <summary>Усе, що дає година уважної гри (пасив з ярмарком, клік, полиця, Око, купці).</summary>
    public double Play => Passive + Click + Fall + Eye + Merchant;
}

/// <summary>Глина гончаря в трьох ролях: година біля кола, година без гравця, робота на виріб (менше — швидше ліпиться).</summary>
public sealed record ClickerClayRow(string Key, string Name, int Level, ClickerHour Hour, double Work);

/// <summary>
/// Порада аналітика: <paramref name="Kind"/> — вид (за ним Глек бере фразу), <paramref name="Gain"/> — у скільки разів
/// більше стане <paramref name="Measure"/> («all» — до всього, «play» — година біля кола, «idle» — без гравця, «form» —
/// ліплення, «slava» — Слава), <paramref name="Cost"/> — скільки глеків коштує (0 — нічого), <paramref name="Where"/> —
/// куди вести кнопкою, <paramref name="Facts"/> — числа й назви для тексту.
/// </summary>
public sealed record ClickerTip(string Kind, double Gain, string Measure, double Cost, string Where, Dictionary<string, object> Facts);

/// <summary>Розклад множника «до всього»: назва складника, скільки зараз, скільки було б на стелі (0 — стелі нема).</summary>
public sealed record ClickerMultPart(string Key, double Now, double Max);

public sealed record ClickerAdvice(ClickerHour Hour, IReadOnlyList<ClickerClayRow> Clays, IReadOnlyList<ClickerMultPart> Mult,
    IReadOnlyList<ClickerTip> Tips);

/// <summary>
/// Аналітик кола (аналітика кола, етап 2; план D:/or-wt/_tools/clicker-analytics/PLAN.md). Те, що 25.10 рахував тимчасовий
/// заміряч рефлексією, тепер — у грі: модель години гри для кожної своєї глини, множники «до всього» поскладниково з тим,
/// чого бракує до стелі (альбом, села, гості, віхи пасиву), ворота прогресу (Слава до наступної заморської майстерні,
/// етап Толоки), гроші без діла й обпал. Кожна порада несе число — Дядько Глек (етап 4) лише вбирає її в слова.
///
/// Чому не ★ Майстерні: та вибирає найкоротшу окупність серед покупок за глеки. Тут — те, що за глеки не купиш або чого
/// ★ не бачить: глина, альбом, шана, кораблі, будови. Покупки тут лише одним рядком — «гроші лежать без діла».
/// Усе рахується на копії чисел, стан не змінюється (глину на мить підставляємо й одразу вертаємо).
/// </summary>
public sealed partial class Clicker
{
    /// <summary>Скільки кліків за секунду в моделі години гри (як і «хвилина гри»).</summary>
    public const double AdviseCps = PlayCps;

    /// <summary>Порада «гроші без діла» — лише коли купівля на весь гаманець дасть хоч стільки до пасиву.</summary>
    public const double AdviseWalletMin = 1.05;

    /// <summary>
    /// Покупки, до яких більше доби уважної гри, — не порада, а мрія: на ранній грі щабель світу дає «×10²¹», але й коштує
    /// стільки ж. Тому ворота й віхи з ціною показуємо, лише коли на них вистачає гаманця чи доби гри.
    /// </summary>
    public const double AdviseReachHours = 24;

    /// <summary>Скільки кроків щонайбільше робить жадібна купівля на весь гаманець (ціни ростуть ×1,15 — вистачає з запасом).</summary>
    const int AdviseGreedySteps = 20_000;

    /// <summary>Модель години уважної гри з поточною глиною: ловить усе, клацає <paramref name="cps"/> разів за секунду.</summary>
    public ClickerHour HourModel(double cps = AdviseCps)
    {
        var p = PassiveBase;
        var cb = ClickBase;
        var clay = ClayNow;
        var luckyF = 1 + LuckyShare * (LuckyMult - 1);
        var fm = FairMultNow;
        var gmid = Has("omen") ? (OmenMinSeconds + OmenMaxSeconds) / 2.0 : (GoldenMinSeconds + GoldenMaxSeconds) / 2.0;
        var g = gmid * clay.Events * (Tool("whistle") ? WhistleEvents : 1) * GuestsGoldenWait;
        // Частка часу під ярмарком і натхненням: розписний — раз на g секунд, ловиться весь.
        var f = 1 - Math.Exp(-0.4 * BuffLonger(FairFor).TotalSeconds / g);
        var i = 1 - Math.Exp(-0.3 * BuffLonger(InspireFor).TotalSeconds / g);
        var fairAvg = 1 + f * (fm - 1);
        var click = cps * 3600 * MomentumMax * luckyF * fairAvg * (cb * (1 - i) + (cb + InspireShare * p) * InspireMult * i);
        var fall = 3600 / FallWaitAvg * FallGain(plain: true) * fairAvg;
        return new ClickerHour(
            Passive: 3600 * p * fairAvg,
            Click: click,
            Fall: fall,
            Eye: cps * 3600 / AdviseEyeClicks * EyeGain,
            Merchant: 3600 / g * 0.3 * p * MerchantSecondsNow * clay.Loot,
            Idle: 3600 * p);
    }

    /// <summary>Скільки кліків у середньому між перевірками Ока майстра в моделі.</summary>
    const double AdviseEyeClicks = 8000;

    /// <summary>Своя глина в кожній ролі: на мить кладемо її на коло й одразу вертаємо ту, що була.</summary>
    public List<ClickerClayRow> ClayModels(double cps = AdviseCps)
    {
        var was = _clay;
        var rows = new List<ClickerClayRow>();
        try
        {
            foreach (var c in Clays)
            {
                if (c.Key.Length > 0 && !_clays.Contains(c.Key)) continue;
                _clay = c.Key;
                rows.Add(new ClickerClayRow(c.Key, c.Name, ClayLevelOf(c.Key), HourModel(cps), ClayNow.Work));
            }
        }
        finally { _clay = was; }
        return rows;
    }

    /// <summary>Сума драбини: рівні × дохід × віхи ×2 (без множників «до всього») — мірило для приростів пасиву.</summary>
    double Ladder(IReadOnlyDictionary<string, int>? levels = null, int[]? dbl = null)
    {
        var sum = 0.0;
        for (var u = 0; u < Shop.Length; u++)
            if (Shop[u].Kind == ClickerKind.Idle)
                sum += (levels is null ? Level(Shop[u].Key) : levels.GetValueOrDefault(Shop[u].Key)) * Shop[u].Rate * Math.Pow(2, (dbl ?? _dbl)[u]);
        return sum;
    }

    public ClickerAdvice Advise(double cps = AdviseCps)
    {
        var clays = ClayModels(cps);
        var hour = HourModel(cps);
        var tips = new List<ClickerTip>();
        var mult = new List<ClickerMultPart>();

        AdviseClay(clays, tips);
        AdviseAlbum(tips, mult);
        AdviseRep(tips, mult);
        var reach = Math.Max(_pots, hour.Play * AdviseReachHours);
        AdvisePassiveMarks(tips, mult, reach);
        AdviseGates(tips, reach);
        AdviseToloka(tips);
        AdviseWallet(tips);
        AdviseFire(tips, mult);
        mult.Insert(0, new ClickerMultPart("styles", 1 + StyleBonus * _styles.Count, 0));
        mult.Add(new ClickerMultPart("house", HouseAllMult, 0));
        mult.Add(new ClickerMultPart("toloka", TolokaAllMult, 0));
        return new ClickerAdvice(hour, clays, mult, [.. tips.OrderByDescending(t => t.Gain)]);
    }

    /// <summary>Лише розклад множника «до всього» (для щоденника): ті самі розрахунки, що й <see cref="Advise"/>, без порад.</summary>
    public List<ClickerMultPart> MultParts()
    {
        var tips = new List<ClickerTip>();
        var mult = new List<ClickerMultPart> { new("styles", 1 + StyleBonus * _styles.Count, 0) };
        AdviseAlbum(tips, mult);
        AdviseRep(tips, mult);
        AdvisePassiveMarks(tips, mult, 0);
        AdviseFire(tips, mult);
        mult.Add(new ClickerMultPart("house", HouseAllMult, 0));
        mult.Add(new ClickerMultPart("toloka", TolokaAllMult, 0));
        return mult;
    }

    static Dictionary<string, object> Facts(params (string Key, object Value)[] kv) =>
        kv.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    // ---------- глина ----------

    /// <summary>Три ролі глини: біля кола, без гравця, ліплення — найкраща своя проти поточної.</summary>
    void AdviseClay(List<ClickerClayRow> clays, List<ClickerTip> tips)
    {
        var cur = clays.FirstOrDefault(c => c.Key == _clay);
        if (cur is null || clays.Count < 2) return;
        var play = clays.MaxBy(c => c.Hour.Play)!;
        if (play.Key != cur.Key && cur.Hour.Play > 0)
            tips.Add(new ClickerTip("clay-play", play.Hour.Play / cur.Hour.Play, "play", 0, "clay:" + play.Key,
                Facts(("clay", play.Key), ("name", play.Name), ("from", cur.Name), ("perHour", play.Hour.Play), ("nowPerHour", cur.Hour.Play))));
        var idle = clays.MaxBy(c => c.Hour.Idle)!;
        if (idle.Key != cur.Key && cur.Hour.Idle > 0)
            tips.Add(new ClickerTip("clay-leave", idle.Hour.Idle / cur.Hour.Idle, "idle", 0, "clay:" + idle.Key,
                Facts(("clay", idle.Key), ("name", idle.Name), ("from", cur.Name))));
        var form = clays.MinBy(c => c.Work)!;
        if (form.Key != cur.Key && form.Work > 0)
            tips.Add(new ClickerTip("clay-form", cur.Work / form.Work, "form", 0, "clay:" + form.Key,
                Facts(("clay", form.Key), ("name", form.Name), ("from", cur.Name))));
    }

    // ---------- альбом ----------

    void AdviseAlbum(List<ClickerTip> tips, List<ClickerMultPart> mult)
    {
        var now = AlbumAllMult;
        var stoveNow = AlbumStoveBonus(_stove);
        var full = Enumerable.Repeat(new StoveTile("", AlbumTopQuality), StoveSlots).ToList();
        var stoveGain = AlbumStoveBonus(full) - stoveNow;
        var cells = _albumCells.Select(m => m | HomeRowMask).ToArray();
        var stars = _albumStars.Select(m => m | HomeRowMask).ToArray();
        var gridGain = AlbumGridBonus(cells, stars) - AlbumGridBonus(_albumCells, _albumStars);
        mult.Add(new ClickerMultPart("album", now, now + stoveGain + gridGain));
        if (stoveGain > 1e-9)
            tips.Add(new ClickerTip("album-stove", (now + stoveGain) / now, "all", 0, "album:stove",
                Facts(("have", _stove.Count), ("slots", StoveSlots), ("add", stoveGain))));
        if (gridGain > 1e-9)
        {
            var noCell = new List<string>();
            var noStar = new List<string>();
            for (var r = 0; r < Wares.Length; r++)
                for (var c = 0; c < AlbumHomeColumns; c++)
                {
                    var bit = 1 << c;
                    var cell = $"{Wares[r].Key}|{(c == 0 ? "" : Styles[c - 1].Key)}";
                    if ((_albumCells[r] & bit) == 0) noCell.Add(cell);
                    if ((_albumStars[r] & bit) == 0) noStar.Add(cell);
                }
            tips.Add(new ClickerTip("album-grid", (now + gridGain) / now, "all", 0, "album:grid",
                Facts(("cells", noCell), ("stars", noStar), ("add", gridGain))));
        }
    }

    // ---------- шана сіл і гостей ----------

    void AdviseRep(List<ClickerTip> tips, List<ClickerMultPart> mult)
    {
        var top = FairRepLevels.Length - 1;
        var fairNow = FairAllMult;
        var fairMax = 1 + RepAllNow * top * FairVillages.Length;
        mult.Add(new ClickerMultPart("fair", fairNow, fairMax));
        if (fairMax > fairNow + 1e-9)
            tips.Add(new ClickerTip("fair-rep", fairMax / fairNow, "all", 0, "village",
                Facts(("need", FairVillages.Where(v => MktLevel(v.Key) < top)
                    .ToDictionary(v => v.Key, v => FairRepLevels[top] - MktPoints(v.Key)) as object))));

        var gTop = GuestRepLevels.Length - 1;
        var guestsNow = GuestsAllMult;
        var guestsMax = 1 + GuestRepAll * gTop * Guests.Length;
        mult.Add(new ClickerMultPart("guests", guestsNow, guestsMax));
        if (guestsMax > guestsNow + 1e-9)
            tips.Add(new ClickerTip("guest-rep", guestsMax / guestsNow, "all", 0, "guests",
                Facts(("need", Guests.Where(g => GuestLevelOf(GuestPoints(g.Key)) < gTop)
                    .ToDictionary(g => g.Key, g => GuestRepLevels[gTop] - GuestPoints(g.Key)) as object),
                    ("hall", TolokaHas("hall")))));
    }

    // ---------- віхи «пасив +25 %» ----------

    void AdvisePassiveMarks(List<ClickerTip> tips, List<ClickerMultPart> mult, double reach)
    {
        var perk = 1 + Perk(MarkEffect.Passive);
        var add = 0.0;
        foreach (var up in Shop)
        {
            if (!GateOpen(up.Key)) continue;
            for (var i = 0; i < up.Steps.Length; i++)
            {
                var m = up.Steps[i];
                if (m.Effect != MarkEffect.Passive || _marks.Contains(MarkKey(up, i))) continue;
                add += m.Amount;
                var level = Level(up.Key);
                var cost = up.MarkPrice(i);
                for (var l = level; l < m.Level; l++) cost += up.Price(l);
                if (cost > reach) continue;
                tips.Add(new ClickerTip("mark-passive", (perk + m.Amount) / perk, "all", cost, "mark:" + MarkKey(up, i),
                    Facts(("tier", up.Key), ("tierName", up.Name), ("mark", m.Name), ("at", m.Level), ("level", level))));
            }
        }
        mult.Add(new ClickerMultPart("passivePerk", perk, perk + add));
    }

    // ---------- ворота: Слава й будови ----------

    void AdviseGates(List<ClickerTip> tips, double reach)
    {
        var ladder = Ladder();
        if (!(ladder > 0)) return;
        // Перша заморська майстерня, яку ще тримає Слава (відмикаються по черзі).
        if (OverseasTiers.FirstOrDefault(k => _slava < SlavaGate[k]) is { } next && Shop.FirstOrDefault(u => u.Key == next) is { } up)
        {
            var gain = (ladder + 25 * up.Rate * 2) / ladder;
            var cost = up.MarkPrice(0);
            for (var l = 0; l < 25; l++) cost += up.Price(l);
            if (cost <= reach)
                tips.Add(new ClickerTip("slava", gain, "all", cost, "pier",
                Facts(("tier", next), ("tierName", up.Name), ("need", SlavaGate[next] - _slava), ("slava", _slava))));
        }
        // Щабель гончарів світу, що чекає саме ту будову Толоки, яку зараз будують.
        foreach (var (tier, building) in WorldGate)
        {
            if (TolokaHas(building) || TolokaCurrent()?.Key != building || Shop.FirstOrDefault(u => u.Key == tier) is not { } w) continue;
            var cost = w.MarkPrice(0);
            for (var l = 0; l < 25; l++) cost += w.Price(l);
            if (cost > reach) continue;
            tips.Add(new ClickerTip("toloka-gate", (ladder + 25 * w.Rate * 2) / ladder, "all", cost, "toloka",
                Facts(("tier", tier), ("tierName", w.Name), ("building", building), ("buildingName", TolokaName(building)))));
        }
    }

    // ---------- Толока: поточний етап ----------

    void AdviseToloka(List<ClickerTip> tips)
    {
        if (TolokaCurrent() is not { } b || _tLaidAt != default) return;
        var stage = Math.Clamp(_tStage, 0, b.Stages.Length - 1);
        var st = b.Stages[stage];
        var left = new List<object>();
        var ready = true;
        for (var i = 0; i < st.Needs.Length; i++)
        {
            var need = TolokaLeft(st, i);
            if (need <= 0) continue;
            var have = ItemCount(TolokaMatch(st.Needs[i]));
            if (have < need) ready = false;
            left.Add(new { ware = st.Needs[i].Ware, style = st.Needs[i].Style, q = st.Needs[i].Q, need, have });
        }
        // Остання будова етапу дає +5 % до всього; решта етапів — крок до неї.
        var gain = stage == b.Stages.Length - 1 ? (1 + TolokaAllBonus * (_tBuilt.Count + 1)) / (1 + TolokaAllBonus * _tBuilt.Count) : 1.0;
        tips.Add(new ClickerTip(ready ? "toloka-lay" : "toloka-collect", gain, "all", st.Pay, "toloka",
            Facts(("building", b.Key), ("buildingName", b.Name), ("stage", stage + 1), ("stages", b.Stages.Length),
                ("stageName", st.Name), ("left", left), ("reward", b.Reward))));
    }

    // ---------- гроші без діла ----------

    /// <summary>Жадібно на весь гаманець: щоразу найкращий приріст драбини на глек — рівень чи віха ×2.</summary>
    void AdviseWallet(List<ClickerTip> tips)
    {
        var ladder = Ladder();
        if (!(ladder > 0) || !(_pots > 0)) return;
        var levels = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var up in Shop) levels[up.Key] = Level(up.Key);
        var dbl = (int[])_dbl.Clone();
        var owned = new HashSet<string>(_marks, StringComparer.Ordinal);
        var wallet = _pots;
        var spent = 0.0;
        var now = ladder;
        var bought = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var step = 0; step < AdviseGreedySteps; step++)
        {
            (double Ratio, int U, int Mark, double Cost, double Gain) best = default;
            for (var u = 0; u < Shop.Length; u++)
            {
                var up = Shop[u];
                if (up.Kind != ClickerKind.Idle || !GateOpen(up.Key) || CappedNow(up, levels[up.Key])) continue;
                var l = levels[up.Key];
                var w = up.Rate * Math.Pow(2, dbl[u]);
                var price = up.Price(l);
                if (price <= wallet && w / price > best.Ratio) best = (w / price, u, -1, price, w);
                // Найближча некуплена віха ×2: рівні до неї плюс сама віха.
                for (var i = 0; i < up.Steps.Length; i++)
                {
                    if (up.Steps[i].Effect != MarkEffect.Double || owned.Contains(MarkKey(up, i))) continue;
                    var at = up.Steps[i].Level;
                    var cost = up.MarkPrice(i);
                    for (var x = l; x < at; x++) cost += up.Price(x);
                    var newL = Math.Max(l, at);
                    var gain = newL * up.Rate * Math.Pow(2, dbl[u] + 1) - l * w;
                    if (cost <= wallet && gain / cost > best.Ratio) best = (gain / cost, u, i, cost, gain);
                    break;
                }
            }
            if (best.Cost <= 0) break;
            var b = Shop[best.U];
            wallet -= best.Cost;
            spent += best.Cost;
            now += best.Gain;
            if (best.Mark < 0) levels[b.Key]++;
            else
            {
                levels[b.Key] = Math.Max(levels[b.Key], b.Steps[best.Mark].Level);
                owned.Add(MarkKey(b, best.Mark));
                dbl[best.U]++;
            }
            bought[b.Key] = levels[b.Key];
        }
        var factor = now / ladder;
        if (factor < AdviseWalletMin) return;
        var top = bought.OrderByDescending(kv => Shop[ShopAt[kv.Key]].Rate).Take(4)
            .ToDictionary(kv => kv.Key, kv => (object)new { name = Shop[ShopAt[kv.Key]].Name, from = Level(kv.Key), to = kv.Value });
        tips.Add(new ClickerTip("wallet", factor, "all", spent, "shop", Facts(("pots", _pots), ("spent", spent), ("top", top))));
    }

    // ---------- обпал ----------

    void AdviseFire(List<ClickerTip> tips, List<ClickerMultPart> mult)
    {
        var gain = StampsFor(_total) - NaturalStamps;
        var bonus = Has("seal") ? SealStampBonus : StampBonus;
        var now = StampMult;
        var after = 1 + bonus * StampWeight(_stamps + Math.Max(0, gain) + (Tool("iron") ? IronStamps : 0));
        mult.Add(new ClickerMultPart("stamps", now, after));
        if (gain < 1) return;
        tips.Add(new ClickerTip("fire", after / now, "all", 0, "fire",
            Facts(("stamps", gain), ("items", ItemTotal), ("pots", _pots), ("keep", Math.Clamp(KeepShare, 0, 0.5)))));
    }
}
