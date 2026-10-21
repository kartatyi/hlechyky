using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Заморська торгівля — дванадцяте оновлення (docs/games/specs/clicker-v12.md §3). До Пристані на Ворсклі приходять
/// кораблі й просять партію виробів; за кожен завантажений виріб — «хвилини гри» й Слава у світі, а Слава відмикає
/// заморські майстерні (<see cref="SlavaGate"/>).
///
/// Навіщо так: у пізній грі все платило глеками, а глекам не було куди йти — щаблі світу тримав годинник Толоки, і хто
/// раз відстав на будову, той відставав назавжди (Smaug, 20.10). Вироби з рук — реальний час гри, і ніякий множник їх не
/// пришвидшить: Слава росте рівно з того, скільки ти наліпив і обпалив.
/// </summary>
public sealed partial class Clicker
{
    /// <summary>Скільки кораблів чекає біля Пристані щонайбільше.</summary>
    public const int TradeShipsMax = 3;
    /// <summary>Новий корабель — раз на стільки (офлайн теж, до <see cref="TradeShipsMax"/>), стоїть стільки.</summary>
    public static readonly TimeSpan TradeGap = TimeSpan.FromMinutes(40), TradeStay = TimeSpan.FromHours(8);
    /// <summary>
    /// «Перший корабель» чекає тиждень, а не вісім годин (калібрування 21.10, §17): він приходить на першій синхронізації
    /// після виходу v12 — і в того, чия вкладка відкрита цілодобово (Владік), за ніч відплив би, не побачений; а без нього
    /// перша майстерня чекала б звичайного корабля з потрібним виробом (у симуляції smaug — 13 год замість хвилин).
    /// </summary>
    public static readonly TimeSpan TradeFirstStay = TimeSpan.FromDays(7);
    /// <summary>Скільки виробів просить корабель; перший — <see cref="TradeFirstCount"/> (але не більше, ніж є, і не менше п'яти).</summary>
    public const int TradeMin = 30, TradeMax = 60, TradeFirstCount = 20, TradeFirstMin = 5;
    /// <summary>
    /// «Хвилини гри» (<see cref="PlayMinute"/>) за завантажений виріб: добрий, дзвінкий, розкішний; у розписі світу чи
    /// заморському — ×1,5; повний корабель — ще +10 %. Калібрування 21.10 (§17), заміряно на збереженнях лідерів: село
    /// платить 1,8–3,1 хвилини за виріб (шана й майдан), гість — 7–13, але гостей кілька на день. Корабель — найбільший і
    /// найвигідніший збут: добрий тут — як у селі (2, було 1 — дешевше за село), дзвінкий — дорожче, розкішний у розписі
    /// світу — як у гостя. Економіку це не ламає: лідер заробляє 13–21 хвилину гри за хвилину гри (pace.jsonl), кораблі
    /// в симуляції додають 6–10 — плюс 20–40 % до доби разом з офлайном.
    /// </summary>
    public const double TradePlayQ2 = 2, TradePlayQ3 = 3, TradePlayQ4 = 8, TradeStylePay = 1.5, TradeFullPay = 0.10;
    /// <summary>Слава за виріб: добрий, дзвінкий, розкішний; розпис світу чи заморський — ще +1; повний корабель — ще
    /// <see cref="SlavaFullPct"/> % (цілими: 10 × 0,1 у double — це 1,0000000000000009, і стеля давала б 2).</summary>
    public const int SlavaQ2 = 1, SlavaQ3 = 2, SlavaQ4 = 5, SlavaStyle = 1, SlavaFullPct = 10;
    /// <summary>Скільки повних кораблів — на ачівку «Морський торговець».</summary>
    public const int ShipsForAchievement = 100;
    /// <summary>Очки «Майстра дня» (ClickerDay.cs) за виріб з ручного горна: дзвінкий і розкішний.</summary>
    public const int DayPointsQ3 = 1, DayPointsQ4 = 3;

    /// <summary>Сервіс «Майстра дня» — один на сайт (null у тестах без нього).</summary>
    ClickerDayService? _daySvc;
    bool _dayLooked;

    ClickerDayService? DaySvc()
    {
        if (!_dayLooked) { _daySvc = Ctx.Services.GetService<ClickerDayService>(); _dayLooked = true; }
        return _daySvc;
    }

    /// <summary>Вид «day»: табло дня й моє місце.</summary>
    object? ViewDay(DateTimeOffset now) => DaySvc()?.Wire(Ctx.NickOf(0), now);

    /// <summary>Переможець дня, що зайшов уже після підбиття (сервіс видає ачівки сам, але база могла бути недоступна).</summary>
    void SyncDay()
    {
        if (DaySvc() is not { } svc) return;
        var wins = svc.WinsOf(Ctx.NickOf(0));
        if (wins >= 1) Achieve("potter-day-win");
        if (wins >= 7) Achieve("potter-day-7");
    }

    /// <summary>Звідки корабель: родовий відмінок для «Корабель з …» і розпис тих країв (порожній — свого нема).</summary>
    public sealed record ClickerPort(string Key, string From, string Style);

    /// <summary>Лише назви міст і земель; одноколірні вимпели на сцені (правило v10 — жодних державних прапорців).</summary>
    public static readonly ClickerPort[] Ports =
    [
        new("jingdezhen", "Цзиндечженя", "jingdezhen"),
        new("iznik", "Ізніка", "iznik"),
        new("delft", "Делфта", "delft"),
        new("meissen", "Майсена", "meissen"),
        new("sevres", "Севра", "sevres"),
        new("kyoto", "Кіото", "raku"),
        new("athens", "Афін", "athens"),
        new("faenza", "Фаєнци", "faenza"),
        new("lisbon", "Лісабона", "azulejo"),
        new("england", "Англії", "wedgwood"),
        new("korea", "Кореї", "celadon"),
        new("talavera", "Талавери", "talavera"),
    ];

    // ---------- стан ----------

    /// <summary>Корабель: що просить, скільки вже завантажено (і з них розкішних), що вже заплачено й скільки Слави дано.</summary>
    /// <param name="Ware">Порожній — будь-який виріб (лише перший корабель).</param>
    /// <param name="Lux">Скільки з <paramref name="Count"/> мусять бути розкішними.</param>
    public sealed record ShipRow(int Id, string Port, string Ware, string Style, int Quality, int Count, int Lux,
        DateTimeOffset At, DateTimeOffset Until, int Loaded = 0, int LoadedLux = 0, double Paid = 0, long SlavaGot = 0, bool First = false);

    /// <summary>Стан у збереженні. Усе необов'язкове: старе збереження — «кораблів ще не було».</summary>
    public sealed record TradeRow(long Slava = 0, List<ShipRow>? Ships = null, int ShipId = 0, DateTimeOffset Next = default,
        int Full = 0, bool FirstDone = false);

    long _slava;
    readonly List<ShipRow> _ships = [];
    int _shipId;
    DateTimeOffset _shipNext;
    /// <summary>Скільки кораблів відпливло повними за весь час (ачівки).</summary>
    int _shipsFull;
    bool _firstShipDone;

    /// <summary>Торгівля відкрита: Пристань на Ворсклі збудована.</summary>
    bool TradeOpen => TolokaHas("pier");

    static bool WorldStyle(string style) =>
        style.Length > 0 && (MasterTiers.Contains(style) || OverseasTiers.Contains(style));

    static int SlavaFor(int quality, string style) =>
        (quality >= 4 ? SlavaQ4 : quality >= 3 ? SlavaQ3 : quality >= 2 ? SlavaQ2 : 0) + (WorldStyle(style) ? SlavaStyle : 0);

    static double TradeMinutesFor(int quality, string style) =>
        (quality >= 4 ? TradePlayQ4 : quality >= 3 ? TradePlayQ3 : TradePlayQ2) * (WorldStyle(style) ? TradeStylePay : 1);

    static ClickerPort PortOf(string key) => Ports.FirstOrDefault(p => p.Key == key) ?? Ports[0];

    static Func<ItemInfo, bool> ShipMatch(ShipRow s, bool lux) =>
        it => (s.Ware.Length == 0 || it.Ware == s.Ware) && it.Quality >= (lux ? 4 : s.Quality)
              && (s.Style.Length == 0 || it.Style == s.Style);

    /// <summary>План вантажу корабля — <see cref="SparePlan"/> без винятків: корабель поступається всім.</summary>
    List<(ItemInfo Item, int N)> ShipPlan(Func<ItemInfo, bool> match, int n) => SparePlan(match, n);

    /// <summary>
    /// План забору лише з того, чого ніхто не чекає (<see cref="LadKept"/> — замовлення сіл і гостей, майстерштук,
    /// Толока; <paramref name="who"/> — чиїх вимог не рахувати). Корабель і склад толоки (v12) — необов'язкові покупці:
    /// чужого відкладеного не беруть ніколи, а не «коли іншого нема». Найгірша придатна якість — першою.
    /// </summary>
    internal List<(ItemInfo Item, int N)> SparePlan(Func<ItemInfo, bool> match, int n, TakeFor? who = null)
    {
        var plan = new List<(ItemInfo, int)>();
        if (n <= 0) return plan;
        var kept = LadKept(Ctx.Clock.UtcNow, who);
        foreach (var (item, count) in AllItems().Where(x => match(x.Item)).OrderBy(x => x.Item.Quality).ToList())
        {
            var free = count - kept.GetValueOrDefault(ItemKey(item.Ware, item.Style, item.Quality));
            var take = Math.Min(free, n);
            if (take <= 0) continue;
            plan.Add((item, take));
            n -= take;
            if (n <= 0) break;
        }
        return plan;
    }

    int ShipFree(Func<ItemInfo, bool> match) => ShipPlan(match, int.MaxValue / 2).Sum(x => x.N);

    // ---------- кораблі ----------

    ShipRow ShipNew(DateTimeOffset at)
    {
        var open = Wares.Where(w => WareOpen(w.Key)).ToList();
        if (open.Count == 0) open.Add(Wares[0]);
        var ware = open[Ctx.Rng.Next(open.Count)];
        var port = Ports[Ctx.Rng.Next(Ports.Length)];
        var quality = Ctx.Rng.Next(100) < 70 ? 3 : 2;
        var count = TradeMin + Ctx.Rng.Next(TradeMax - TradeMin + 1);
        // Розпис — лише з тих, що в гравця є (закриті щаблі світу не заважають): свій розпис порту — частіше.
        var style = "";
        if (_styles.Count > 0 && Ctx.Rng.Next(100) < 40)
        {
            var mine = _styles.Order(StringComparer.Ordinal).ToList();
            style = _styles.Contains(port.Style) && Ctx.Rng.Next(100) < 60 ? port.Style : mine[Ctx.Rng.Next(mine.Count)];
        }
        // «З них K розкішних» — на дзвінких замовленнях, третина кораблів; розкішні дає лише розписна партія.
        var lux = quality >= 3 && Ctx.Rng.Next(100) < 33 ? Math.Max(3, count * (10 + Ctx.Rng.Next(16)) / 100) : 0;
        return new ShipRow(++_shipId, port.Key, ware.Key, style, quality, count, lux, at, at + TradeStay);
    }

    /// <summary>
    /// «Перший корабель»: щойно торгівля відкрилась (у тому числі в мить виходу v12) — 20 будь-яких добрих і кращих, що
    /// є в коморі (не більше, ніж є вільних, і не менше п'яти). Повний — Слави вистачає на першу майстерню одразу.
    /// </summary>
    void ShipFirst(DateTimeOffset now)
    {
        if (_firstShipDone || _ships.Any(s => s.First)) return;
        var free = ShipFree(it => it.Quality >= 2);
        var count = Math.Clamp(free, TradeFirstMin, TradeFirstCount);
        var port = Ports.First(p => p.Key == "athens");
        _ships.Insert(0, new ShipRow(++_shipId, port.Key, "", "", 2, count, 0, now, now + TradeFirstStay, First: true));
        if (_shipNext == default) _shipNext = now + TradeGap;
    }

    void ResetTrade(DateTimeOffset now)
    {
        _slava = 0;
        _ships.Clear();
        _shipId = 0;
        _shipNext = default;
        _shipsFull = 0;
        _firstShipDone = false;
    }

    /// <summary>
    /// Прибуття й відплив кораблів за проміжок (офлайн теж): розклад накопичується до трьох — хто зник на добу, застає
    /// повну Пристань. Корабель, чий строк минув, відпливає з тим, що завантажили (за це вже заплачено при вантаженні).
    /// </summary>
    void SyncTrade(DateTimeOffset now)
    {
        SyncDay();
        if (!TradeOpen) return;
        // «Поки тебе не було» — один рядок на прибулі й один на ті, що відпливли (калібрування §17: неповних ~7 на добу,
        // і поштучні рядки витісняли купців, дарунки й Толоку з восьми нотаток).
        var gone = _ships.Where(s => s.Until <= now).ToList();
        foreach (var s in gone)
        {
            _ships.Remove(s);
            if (s.First) _firstShipDone = true;
        }
        if (gone.Count == 1)
            AwayNote(gone[0].Loaded > 0
                ? $"🚢 Корабель з {PortOf(gone[0].Port).From} відплив неповним: завантажили {gone[0].Loaded} з {gone[0].Count}"
                : $"🚢 Корабель з {PortOf(gone[0].Port).From} не дочекався вантажу — відплив");
        else if (gone.Count > 1)
        {
            var part = gone.Count(s => s.Loaded > 0);
            AwayNote($"🚢 Відпливли {gone.Count} {Plural(gone.Count, "корабель", "кораблі", "кораблів")}, не дочекавшись повного вантажу"
                + (part > 0 ? $": {part} — частково завантажені" : ""));
        }
        ShipFirst(now);
        if (_shipNext == default) _shipNext = now + TradeGap;
        var guard = 0;
        var came = new List<ShipRow>();
        while (_shipNext <= now && guard++ < 64)
        {
            if (_ships.Count < TradeShipsMax)
            {
                // Корабель, що прийшов офлайн, стоїть від своєї години прибуття, а не від «зараз».
                var s = ShipNew(_shipNext);
                if (s.Until > now)
                {
                    _ships.Add(s);
                    came.Add(s);
                }
            }
            _shipNext += TradeGap;
        }
        if (came.Count == 1)
            AwayNote($"🚢 До Пристані прийшов корабель з {PortOf(came[0].Port).From}: {WareOf(came[0].Ware)!.Name.ToLowerInvariant()} ×{came[0].Count}");
        else if (came.Count > 1)
            AwayNote($"🚢 До Пристані прийшли {came.Count} {Plural(came.Count, "корабель", "кораблі", "кораблів")} — "
                + string.Join(", ", came.Select(s => "з " + PortOf(s.Port).From)));
        // Біля Пристані повно — наступний з'явиться за інтервал після того, як звільниться місце.
        if (_shipNext <= now) _shipNext = now + TradeGap;
    }

    ActResult? ActTrade(string action, JsonElement payload)
    {
        if (action != "trade") return null;
        if (!TradeOpen) return ActResult.Fail("Кораблі прийдуть, коли збудуєш на Толоці «Пристань на Ворсклі»");
        var now = Ctx.Clock.UtcNow;
        var id = Num(payload, "id") ?? -1;
        var i = _ships.FindIndex(s => s.Id == id);
        if (i < 0 || _ships[i].Until <= now) return ActResult.Fail("Цей корабель уже відплив");
        return Str(payload, "do") switch
        {
            "load" => ShipLoad(i, (int)Math.Clamp(Num(payload, "n") ?? 1, 1, TradeMax), now),
            "loadAll" => ShipLoad(i, TradeMax, now),
            _ => ActResult.Fail("Біля Пристані так не торгують"),
        };
    }

    /// <summary>
    /// Завантажити до <paramref name="want"/> виробів: спершу розкішні, яких ще бракує в рядку «з них K розкішних», далі —
    /// решта, найгірша придатна якість першою. Платимо й даємо Славу одразу за кожен виріб; повний — бонус і відплив.
    /// </summary>
    ActResult ShipLoad(int index, int want, DateTimeOffset now)
    {
        var s = _ships[index];
        var luxLeft = Math.Max(0, s.Lux - s.LoadedLux);
        var left = s.Count - s.Loaded;
        var plan = ShipPlan(ShipMatch(s, lux: true), Math.Min(want, luxLeft));
        var luxTaken = plan.Sum(x => x.N);
        // Звичайні місця: скільки лишилось, мінус ті, що чекають розкішних (їх звичайним не займеш).
        var normal = Math.Min(want - luxTaken, left - luxLeft);
        if (normal > 0)
        {
            var taken = plan.ToDictionary(x => x.Item, x => x.N);
            foreach (var (item, n) in ShipPlan(ShipMatch(s, lux: false), normal + luxTaken))
            {
                var already = taken.GetValueOrDefault(item);
                var take = Math.Min(n - already, normal);
                if (take <= 0) continue;
                plan.Add((item, take));
                normal -= take;
                if (normal <= 0) break;
            }
        }
        var got = plan.Sum(x => x.N);
        if (got == 0) return ActResult.Fail(ShipLack(s, luxLeft));

        double minutes = 0;
        long slava = 0;
        foreach (var (item, n) in plan)
        {
            var key = ItemKey(item.Ware, item.Style, item.Quality);
            if (_items[key] == n) _items.Remove(key);
            else _items[key] -= n;
            minutes += TradeMinutesFor(item.Quality, item.Style) * n;
            slava += (long)SlavaFor(item.Quality, item.Style) * n;
        }
        var pay = PlayPay(minutes);
        Add(pay);
        _slava += slava;
        s = s with { Loaded = s.Loaded + got, LoadedLux = s.LoadedLux + luxTaken, Paid = s.Paid + pay, SlavaGot = s.SlavaGot + slava };
        if (s.Loaded < s.Count)
        {
            _ships[index] = s;
            return ActResult.Accept($"🚢 На корабель з {PortOf(s.Port).From}: +{got} · +{PotsShort(pay)} · Слава +{Count(slava)} · ще {s.Count - s.Loaded}");
        }

        // Повний — бонус і відплив.
        var bonusPay = s.Paid * TradeFullPay;
        var bonusSlava = (s.SlavaGot * SlavaFullPct + 99) / 100;
        if (s.First) bonusSlava = Math.Max(bonusSlava, SlavaGate[OverseasTiers[0]] - _slava);
        Add(bonusPay);
        _slava += Math.Max(0, bonusSlava);
        _ships.RemoveAt(index);
        _shipsFull++;
        if (s.First) _firstShipDone = true;
        if (_shipsFull == 1) Achieve("potter-ship-first");
        if (_shipsFull == ShipsForAchievement) Achieve("potter-ship-100");
        _viewVersion++;
        var opened = OverseasTiers.FirstOrDefault(k => _slava >= SlavaGate[k] && _slava - slava - Math.Max(0, bonusSlava) < SlavaGate[k]);
        var text = $"⛵ Корабель з {PortOf(s.Port).From} повний і відплив: +{PotsShort(pay + bonusPay)} · Слава +{Count(slava + Math.Max(0, bonusSlava))}";
        if (opened is not null) text += $" · 🌍 відкрито «{Shop.First(u => u.Key == opened).Name}» у Майстерні";
        return ActResult.Accept(text);
    }

    /// <summary>«Треба розкішних — їх дає лише розписна партія» / «Нема вільних …: усе, що є, відкладене під …».</summary>
    string ShipLack(ShipRow s, int luxLeft)
    {
        var what = s.Ware.Length == 0 ? "добрих і кращих виробів" : WareOf(s.Ware)!.Name.ToLowerInvariant();
        var style = s.Style.Length > 0 ? $" у розписі «{Styles.FirstOrDefault(x => x.Key == s.Style)?.Name ?? s.Style}»" : "";
        if (luxLeft > 0 && luxLeft == s.Count - s.Loaded)
            return $"Лишились місця для розкішних ({luxLeft}) — розкішні дає лише розписна партія, яку палиш сам";
        var all = ItemCount(ShipMatch(s, lux: false));
        return all > 0
            ? $"Вільних нема: {what}{style} у коморі відкладено під Толоку чи замовлення — корабель їх не бере"
            : $"У коморі нема: {what}{style}, {(s.Quality >= 3 ? "дзвінкі й кращі" : "добрі й кращі")}";
    }

    /// <summary>Вид «trade»: null, поки нема Пристані.</summary>
    object? ViewTrade(DateTimeOffset now)
    {
        if (!TradeOpen) return null;
        var next = OverseasTiers.FirstOrDefault(k => _slava < SlavaGate[k]);
        return new
        {
            slava = _slava,
            next = _ships.Count < TradeShipsMax ? _shipNext : (DateTimeOffset?)null,
            full = _shipsFull,
            gate = next is null ? null : new { key = next, name = Shop.First(u => u.Key == next).Name, need = SlavaGate[next] },
            ships = _ships.Select(s =>
            {
                var luxLeft = Math.Max(0, s.Lux - s.LoadedLux);
                var normalLeft = s.Count - s.Loaded - luxLeft;
                return new
                {
                    id = s.Id, port = s.Port, from = PortOf(s.Port).From, ware = s.Ware, style = s.Style, q = s.Quality,
                    n = s.Count, lux = s.Lux, loaded = s.Loaded, loadedLux = s.LoadedLux, until = s.Until, first = s.First,
                    // Скільки з вільного (не відкладеного) піде просто зараз — щоб кнопка не обіцяла більше, ніж візьме.
                    can = Math.Min(luxLeft, ShipFree(ShipMatch(s, lux: true))) + Math.Min(normalLeft, ShipFree(ShipMatch(s, lux: false))),
                    // Скільки «хвилин гри» й Слави за звичайний виріб цього замовлення (без бонусу повного).
                    minutes = TradeMinutesFor(s.Quality, s.Style), slavaPer = SlavaFor(s.Quality, s.Style),
                };
            }),
        };
    }

    TradeRow? SaveTrade() => _slava == 0 && _ships.Count == 0 && _shipsFull == 0 && !_firstShipDone && _shipNext == default
        ? null
        : new TradeRow(_slava, _ships.ToList(), _shipId, _shipNext, _shipsFull, _firstShipDone);

    /// <summary>Збереження торгівлі: чого не зрозуміли (невідомий порт, виріб, розпис) — відкидаємо корабель, а не все.</summary>
    void LoadTrade(TradeRow? row)
    {
        var now = Ctx.Clock.UtcNow;
        ResetTrade(now);
        if (row is null) return;
        _slava = Math.Max(0, row.Slava);
        foreach (var s in row.Ships ?? [])
        {
            if (s is null || s.Id <= 0 || s.Port is null || Ports.All(p => p.Key != s.Port) || s.Ware is null
                || (s.Ware.Length > 0 && WareOf(s.Ware) is null) || s.Style is null
                || (s.Style.Length > 0 && Styles.All(x => x.Key != s.Style)) || s.Quality is < 1 or > QualityMax
                || s.Count is < 1 or > TradeMax || s.Lux < 0 || s.Lux > s.Count || s.Loaded < 0 || s.Loaded >= s.Count
                || s.Until <= s.At || _ships.Count >= TradeShipsMax + 1)
                continue;
            _ships.Add(s with { LoadedLux = Math.Clamp(s.LoadedLux, 0, s.Lux), Paid = double.IsFinite(s.Paid) ? Math.Max(0, s.Paid) : 0,
                SlavaGot = Math.Max(0, s.SlavaGot) });
        }
        _shipId = Math.Max(Math.Max(0, row.ShipId), _ships.Count == 0 ? 0 : _ships.Max(s => s.Id));
        _shipNext = row.Next > now + TradeGap ? now + TradeGap : row.Next;
        _shipsFull = Math.Max(0, row.Full);
        _firstShipDone = row.FirstDone;
    }
}
