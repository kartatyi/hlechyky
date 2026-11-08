using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Тижневі змагання з боку гончаря (v13 Г4 і Г5, сервіс — ClickerWeek.cs): «⛵ Вітрила тижня» (+20/10/5 % до всього за ріст),
/// «🤝» +10 % за спільне замовлення округи й сама дія <c>guild { op: "coop", n }</c> — довезти зі своєї комори. Нагороди
/// гончар забирає сам на синхронізації (<see cref="ClaimWeekPrizes"/>): ключ у збереженні (<c>week:{тиждень}:grow:{місце}</c>
/// чи <c>week:{тиждень}:coop</c>), повтор не платить. Відсоток і кінець зберігаються (FeastRow) і гаснуть самі.
/// </summary>
public sealed partial class Clicker
{
    /// <summary>Сервіс тижневих змагань — один на сайт (null у тестах без нього).</summary>
    ClickerWeekService? _weekSvc;
    bool _weekLooked;

    ClickerWeekService? WeekSvc()
    {
        if (!_weekLooked) { _weekSvc = Ctx.Services?.GetService<ClickerWeekService>(); _weekLooked = true; }
        return _weekSvc;
    }

    double _sailsPct, _coopPct;
    DateTimeOffset _sailsUntil, _coopUntil;

    /// <summary>«⛵ Вітрила тижня» (+% за ріст) і «🤝» (+% за спільне замовлення) — до всього, поки не скінчився строк.</summary>
    double WeekAllMult
    {
        get
        {
            var now = Ctx.Clock.UtcNow;
            return (now < _sailsUntil ? 1 + _sailsPct / 100 : 1) * (now < _coopUntil ? 1 + _coopPct / 100 : 1);
        }
    }

    /// <summary>Вид «week»: кінець тижня, табло росту й спільне замовлення. null — сервісу нема.</summary>
    object? ViewWeek(DateTimeOffset now) => WeekSvc()?.Wire(Ctx.NickOf(0) ?? "", now);

    /// <summary>Нагороди тижня, що ще діють, для виду бафів (вимпел на хаті, плашки): лише коли щось діє.</summary>
    object? ViewWeekBuffs(DateTimeOffset now)
    {
        var sails = _sailsUntil > now;
        var coop = _coopUntil > now;
        if (!sails && !coop) return null;
        return new { sails = sails ? new { pct = _sailsPct, until = _sailsUntil } : null, coop = coop ? new { pct = _coopPct, until = _coopUntil } : null };
    }

    /// <summary>Забрати нагороди тижня, що чекають цього гончаря в сервісі. Кличеться з <see cref="SyncDay"/>.</summary>
    void ClaimWeekPrizes(ClickerWeekService svc, DateTimeOffset now)
    {
        if (Ctx.NickOf(0) is not { Length: > 0 } nick) return;
        foreach (var p in svc.PrizesFor(nick, now))
        {
            if (!_prizeTaken.Add(p.Key)) continue;
            if (p.Until <= now) continue;
            if (p.Kind == "sails")
            {
                if (_sailsUntil > now && _sailsPct >= p.Pct) continue;
                _sailsPct = p.Pct;
                _sailsUntil = p.Until;
                _feastToasts.Add($"⛵ {p.Why}: +{p.Pct:0.##} % до всього до неділі");
            }
            else if (p.Kind == "coop")
            {
                if (_coopUntil > now && _coopPct >= p.Pct) continue;
                _coopPct = p.Pct;
                _coopUntil = p.Until;
                _feastToasts.Add($"🤝 {p.Why}: +{p.Pct:0.##} % до всього до неділі");
            }
            _viewVersion++;
        }
    }

    /// <summary>
    /// Довезти до спільного замовлення округи: <c>{ n }</c> (немає — скільки вільного є, але не більше, ніж ще бракує). Беремо лише
    /// те, чого ніхто не чекає (<see cref="SparePlan"/>: замовлення сіл і гостей, майстерштук, Толока), платимо як село —
    /// «як є», без торгу (<see cref="MktMult"/> і підлога за хвилинами гри).
    /// </summary>
    ActResult GuildCoop(JsonElement payload, DateTimeOffset now)
    {
        if (WeekSvc() is not { } svc || Ctx.NickOf(0) is not { Length: > 0 } nick) return ActResult.Fail("Спільне замовлення зараз зачинене");
        if (svc.CoopNow(now) is not { } order) return ActResult.Fail("Цього тижня спільного замовлення ще нема");
        if (order.Fulfilled || order.Done >= order.Volume) return ActResult.Fail("Округа вже виконала спільне замовлення — дякуємо!");
        if (WareOf(order.Ware) is not { } ware) return ActResult.Fail("Такого виробу не знаємо");
        var left = order.Volume - order.Done;
        var raw = Num(payload, "n");
        if (raw is <= 0) return ActResult.Fail("Скільки довезти — хоч один");
        var want = (int)Math.Min(raw ?? int.MaxValue, left);
        var plan = SparePlan(it => it.Ware == order.Ware && it.Quality >= order.Quality, want, new TakeFor("coop"));
        var free = plan.Sum(x => x.N);
        if (free <= 0)
            return ActResult.Fail($"Нема вільних: {ware.Name.ToLowerInvariant()} якості {order.Quality}+ (відкладене під замовлення й Толоку не чіпаю)");
        var accepted = svc.CoopAdd(nick, free, now);
        if (accepted <= 0) return ActResult.Fail("Замовлення вже не приймає — тиждень скінчився чи його виконали");
        var taker = new TakeFor("coop");
        var rest = accepted;
        double value = 0;
        foreach (var (item, k) in plan)
        {
            var t = Math.Min(k, rest);
            if (t <= 0) break;
            value += ItemValue(item.Ware, item.Style, item.Quality) * t;
            TakeItems(x => x == item, t, taker);
            rest -= t;
        }
        // Плата — як за замовлення села «як є» (ClickerFair.cs): множник дзвінкого виробу без шани села й торгу.
        var mult = FairOrderMult + FairOrderStep * ((order.Quality >= 2 ? 1 : 0) + (order.Quality >= 3 ? 1 : 0));
        var row = new FairOrderRow(0, "", -1, order.Ware, "", order.Quality, accepted, mult, now, now);
        var pay = Math.Max(1, ToPots(Math.Max(value * MktMult(row), MktPlayPay(row))));
        Add(pay);
        var mine = svc.CoopMine(nick, now);
        var min = ClickerWeekService.MinPart(order.Volume, svc.O.CoopMinShare);
        var text = $"🤝 Спільне замовлення: {accepted} × {ware.Name.ToLowerInvariant()} +{PotsShort(pay)} · округа {order.Done + accepted} з {order.Volume}";
        text += mine >= min ? $" · твій внесок {mine} — на бонус вистачає" : $" · твій внесок {mine}, до бонусу ще {min - mine}";
        if (order.Done + accepted >= order.Volume) text += " · 🎉 виконали!";
        _viewVersion++;
        return ActResult.Accept(text);
    }
}
