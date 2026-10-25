using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Гараж 0.0.6 за столом «Чумаків» (specs/fura.md, «Гараж (0.0.6) — контракт»; ТЗ D:\or-wt\_fura\GARAGE.md). Гараж водія
/// (<see cref="FuraCareer"/>) приходить разом із фурою, коли нік сідає, і пишеться під тією самою заявкою — після рейсу,
/// покупки й вибору, не в тику. ДВ — у <c>deliver</c>, дії <c>garage</c> / <c>buy</c> / <c>pick</c>, вид <c>career</c> і
/// <c>car</c>, дошка під свої причепи. У <see cref="Fura"/> — лише гачки.
/// </summary>
public sealed partial class Fura
{
    /// <summary>Опція столу «⚖ Рівні фури»: усі їдуть «Чумаком» без тюнінгу (косметика лишається). Обирає господар при створенні.</summary>
    public const string EqualKey = "equal", EqualOff = "off", EqualOn = "on";

    public const string NotAccount = "Закріпи нік — тоді гараж твій";

    /// <summary>Слоти Лавки з наліпками: до трьох одночасно.</summary>
    static readonly string[] StickerSlots = [LavkaKind.FuraSticker, LavkaKind.FuraSticker + "2", LavkaKind.FuraSticker + "3"];

    bool _equal;
    FuraBook? _book;
    FuraWardrobe _wardrobe = new();
    IOptionsMonitor<FuraOptions>? _furaOpts;

    FuraBook Book => _book ??= FuraBook.Load();
    bool GarageOn => _furaOpts?.CurrentValue.Garage ?? true;

    /// <summary>Книгу гаража читаємо тут — поза замком кімнати (перший раз це диск).</summary>
    void ConfigureGarage(IReadOnlyDictionary<string, string> options)
    {
        _book = FuraBook.Load();
        _equal = options.TryGetValue(EqualKey, out var e) && e == EqualOn;
    }

    void StartGarage()
    {
        _furaOpts = Ctx.Services.GetService<IOptionsMonitor<FuraOptions>>();
        _wardrobe = Ctx.Services.GetService<FuraWardrobe>() ?? new();
    }

    /// <summary>
    /// Нік сів (фура вже його): гараж водія з бази. Запису нема (чи нечитний) — перше відкриття: ретро-досвід за зроблені
    /// рейси й одразу в базу. Дошка, складена під інші причепи (інший стіл, «⚖», куплене деінде), — наново.
    /// </summary>
    void SitCareer(FuraRig rig)
    {
        rig.Career = FuraCareer.Parse(_garage.LoadCareer(rig.NickKey), Book) ?? FuraCareer.Retro(rig.Trips, Book);
        Refresh(rig);
        if (rig.Phase == Free && !BoardFits(rig)) rig.Board = [];
        KeepCareer(rig);
    }

    /// <summary>Гараж водія — у базу, якщо змінився (з пулу потоків, за заявкою стола).</summary>
    void KeepCareer(FuraRig rig)
    {
        if (rig.Career is not { Dirty: true } c) return;
        c.Dirty = false;
        _garage.SaveCareer(rig.NickKey, rig.Claim, c.ToJson());
    }

    /// <summary>Акаунт і вдягнуте з Лавки (на «сів» і на <c>garage</c>). База не відповіла — гість без косметики, гра їде далі.</summary>
    void Refresh(FuraRig rig)
    {
        if (rig.Career is not { } c) return;
        try
        {
            c.Account = _wardrobe.IsAccount(rig.Nick);
            c.Worn = c.Account ? _wardrobe.Worn(rig.Nick) : new Dictionary<string, string>();
        }
        catch (Exception)
        {
            c.Account = false;
            c.Worn = new Dictionary<string, string>();
        }
    }

    /// <summary>«Чумак» без тюнінгу: стіл «⚖ Рівні фури», гараж вимкнено або гість.</summary>
    bool Equalized(FuraRig rig) => _equal || !GarageOn || rig.Career is not { Account: true };

    /// <summary>Типи вантажів на дошці: під свої причепи (тент — завжди); «Чумак без тюнінгу» — лише тент.</summary>
    IReadOnlyCollection<string> CargoTypes(FuraRig rig) => Equalized(rig) || rig.Career is not { } c
        ? [FuraCargo.Tent]
        : [.. c.Owned.Trailers.Select(t => Book.TrailerOf(t)?.Carries ?? FuraCargo.Tent).Append(FuraCargo.Tent).Distinct()];

    /// <summary>Далеке замовлення на дошці: з рівня <see cref="FuraBook.FarLvl"/>, не на «Чумаку без тюнінгу».</summary>
    bool FarOn(FuraRig rig) => !Equalized(rig) && rig.Career is { } c && Book.LevelOf(c.Xp) >= Book.FarLvl;

    /// <summary>
    /// Дошка складена саме під те, що зараз маєш (причепи, далеке): сьогоднішня дошка детермінована — тож порівнюємо з тією,
    /// яку склали б зараз. Учорашня «пасує» — її однаково замінить <see cref="EnsureBoard"/>.
    /// </summary>
    bool BoardFits(FuraRig rig) =>
        rig.BoardDay != Days.Today(Ctx.Clock) || rig.Board.Count == 0
        || rig.Board.SequenceEqual(MakeBoard(Map, rig.BoardDay, rig.NickKey, rig.Trips, CargoTypes(rig), FarOn(rig)));

    // ---------- ДВ ----------

    /// <summary>
    /// ДВ за щойно зданий рейс (<see cref="FuraRig.Trips"/> — уже його номер). Ідемпотентно за номером (<see cref="FuraCareer.Gain"/>).
    /// Новий рівень з титулом Лавки, 500-й рейс і п'ята ходка — ачівки (<c>Award(seat, 0, "ach:…")</c>).
    /// </summary>
    FuraGain? GainXp(int seat, FuraRig rig, FuraOrder order, int k, bool onTime, bool exact)
    {
        if (rig.Career is not { } c) return null;
        var b = Book;
        var (lvl0, stars0) = (b.LevelOf(c.Xp), b.StarsOf(c.Xp));
        var cargo = Map.CargoOf(order.Cargo);
        var trailerXp = cargo is { Trailer: not FuraCargo.Tent } ? b.TrailerFor(cargo.Trailer)?.Xp ?? 1 : 1;
        if (c.Gain(b, rig.Trips, order.Km, k, onTime, exact, order.Col, Mentor(rig, lvl0), trailerXp, order.Far, Days.Today(Ctx.Clock))
            is not { } gain) return null;
        var (lvl1, stars1) = (b.LevelOf(c.Xp), b.StarsOf(c.Xp));
        foreach (var (lvl, title) in b.Titles)
            if (lvl0 < lvl && lvl1 >= lvl) Ctx.Award(seat, 0, $"ach:fura-{title}");
        if (rig.Trips == GoldHornTrips) Ctx.Award(seat, 0, "ach:" + AchGoldHorn);
        if (stars0 < StarSticker && stars1 >= StarSticker) Ctx.Award(seat, 0, "ach:" + AchStars);
        return gain;
    }

    /// <summary>Ачівки «Чумаків» поза титулами рівнів: золотий ріжок (500-й рейс), наліпка «Зоряний чумак» (5 ходок).</summary>
    public const string AchGoldHorn = "fura-500", AchStars = "fura-stars5";
    public const int GoldHornTrips = 500, StarSticker = 5;

    /// <summary>Наставник: колонне, і за цим столом у колоні є інший, з різницею рівнів ≥ <see cref="FuraBook.XpRules.MentorGap"/> (обом ×1,2).</summary>
    bool Mentor(FuraRig rig, int lvl)
    {
        if (ColMember(rig) is null || _col is not { } col) return false;
        foreach (var m in col.Members)
        {
            if (m.Left || m.NickKey == rig.NickKey) continue;
            var s = SeatOfKey(m.NickKey);
            if (s >= 0 && _rigs[s]?.Career is { } other && Math.Abs(Book.LevelOf(other.Xp) - lvl) >= Book.Xp.MentorGap) return true;
        }
        return false;
    }

    static string LevelNote(FuraGain? gain) => gain?.Up is { } up ? $" · 🎉 рівень {up}!" : "";

    // ---------- дії гаража ----------

    /// <summary>
    /// <c>garage</c> — перечитати акаунт і вдягнуте (вид — наступним тиком). <c>buy {id}</c> і <c>pick {tractor|trailer}</c> —
    /// лише акаунт, лише між рейсами (free) і лише коли гараж увімкнено (<c>Fura:Garage</c>).
    /// </summary>
    ActResult GarageAct(FuraRig rig, string action, JsonElement payload)
    {
        if (rig.Career is not { } c) return ActResult.Fail("Гараж ще не відчинився");
        if (action == "garage")
        {
            Refresh(rig);
            _dirty = true;
            return ActResult.Done;
        }
        if (!GarageOn) return ActResult.Fail("Гараж зачинено");
        if (rig.Phase != Free) return ActResult.Fail("Гараж — між рейсами");
        Refresh(rig);
        if (!c.Account) return ActResult.Fail(NotAccount);
        return action == "buy" ? Buy(rig, c, Str(payload, "id")) : Pick(c, payload);
    }

    /// <summary>
    /// Купити тягач, причіп чи наступний щабель. Ref <c>fura-buy:&lt;nickKey&gt;:&lt;id&gt;</c> ідемпотентний: подвійний клік
    /// не списує двічі, а покупка, що впала після списання (гараж не записався), повторюється без другої плати.
    /// </summary>
    ActResult Buy(FuraRig rig, FuraCareer c, string? id)
    {
        if (Book.Find(id) is not { } item) return ActResult.Fail("Такого в гаражі нема");
        if (c.Has(item)) return ActResult.Fail("Уже твоє");
        if (item.Step > 1 && c.TuneOf(item.Branch!) < item.Step - 1) return ActResult.Fail("Спершу попередній щабель");
        if (Book.LevelOf(c.Xp) < item.Lvl) return ActResult.Fail($"Це відкриється з рівня {item.Lvl}");
        if (Ctx.Services.GetService<IStakes>() is not { } wallet) return ActResult.Fail("Каса зачинена");
        var have = wallet.Balance(rig.Nick);
        bool paid;
        try { paid = wallet.TrySpend(rig.Nick, item.Price, $"fura-buy:{item.Id}", $"fura-buy:{rig.NickKey}:{item.Id}"); }
        catch (Exception) { return ActResult.Fail("Каса не відповіла — спробуй ще раз"); }
        if (!paid) return ActResult.Fail($"Бракує {Math.Max(1, item.Price - have)} 🏺");
        c.Grant(item);
        // купив — одразу їде ним: тягач і «Пух» обираються самі (назад — pick)
        if (item.Kind == FuraBook.Tractor) c.Pick.Tractor = item.Id;
        if (item.Kind == FuraBook.Trailer && Book.TrailerOf(item.Id)?.Carries == FuraCargo.Tent) c.Pick.Trailer = item.Id;
        if (!BoardFits(rig))   // новий причіп відкрив вантажі
        {
            rig.Board = [];
            EnsureBoard(rig);
        }
        _dirty = true;
        return ActResult.Accept($"🔧 {item.Title} — твоє назавжди");
    }

    ActResult Pick(FuraCareer c, JsonElement payload)
    {
        if (Str(payload, "tractor") is { } t)
        {
            if (Book.TractorOf(t) is null) return ActResult.Fail("Такого в гаражі нема");
            if (!c.Owned.Tractors.Contains(t)) return ActResult.Fail("Спершу купи");
            c.Pick.Tractor = t;
        }
        else if (Str(payload, "trailer") is { } tr)
        {
            if (Book.TrailerOf(tr) is not { } trailer) return ActResult.Fail("Такого в гаражі нема");
            if (trailer.Carries != FuraCargo.Tent) return ActResult.Fail("Цей причіп чіпляється сам під свій вантаж");
            if (!c.Owned.Trailers.Contains(tr)) return ActResult.Fail("Спершу купи");
            c.Pick.Trailer = tr;
        }
        else return ActResult.Fail("Що обрати?");
        c.Dirty = true;
        _dirty = true;
        return ActResult.Done;
    }

    static string? Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            && e.GetString() is { Length: > 0 } s ? s.Trim().ToLowerInvariant() : null;

    // ---------- вид ----------

    /// <summary>
    /// Числа фізики фури зараз: «Чумак без тюнінгу» — <c>base</c>; інакше обраний тягач і щаблі. Причіп — під поточне
    /// замовлення: вантаж «Ізотерма» чи «Низькорамника» — той причіп, решта — обраний (тент/«Пух»).
    /// </summary>
    Dictionary<string, object> ParamsOf(FuraRig rig)
    {
        var type = rig.Order is { } o && Map.CargoOf(o.Cargo)?.Trailer is { } t && t != FuraCargo.Tent ? t : null;
        var special = type is null ? null : Book.TrailerFor(type)?.Id;
        if (Equalized(rig) || rig.Career is not { } c) return Book.Params(FuraBook.Chumak, special ?? FuraBook.Tent, null);
        return Book.Params(c.Pick.Tractor, special ?? c.Pick.Trailer, c.Tune);
    }

    /// <summary>Гараж водія у виді свого місця (контракт «Вид»); глядачу й без гаража — null.</summary>
    object? CareerView(FuraRig? rig)
    {
        if (rig?.Career is not { } c) return null;
        var b = Book;
        var lvl = b.LevelOf(c.Xp);
        var stars = b.StarsOf(c.Xp);
        var top = lvl >= b.Levels.Max;
        var from = top ? b.Total(b.Levels.Max) + (long)stars * b.Levels.Star : b.Total(lvl);
        var dayTrips = c.Day == Days.Today(Ctx.Clock) ? c.DayTrips : 0;
        return new
        {
            on = GarageOn,
            equal = _equal,
            account = c.Account,
            lvl,
            stars,
            xp = c.Xp,
            from,
            next = top ? from + b.Levels.Star : b.Total(lvl + 1),
            dayTrips,
            tired = dayTrips >= b.Xp.FullTrips,
            owned = new { tractors = c.Owned.Tractors, trailers = c.Owned.Trailers },
            tune = b.Branches.ToDictionary(x => x.Id, x => c.TuneOf(x.Id)),
            pick = new { tractor = c.Pick.Tractor, trailer = c.Pick.Trailer },
            @params = ParamsOf(rig),
            look = Look(c),
            last = c.Last,
        };
    }

    /// <summary>Табличка фури в <c>convoy</c>: «нік · Р12 ★», модель і косметика — видно всім за столом.</summary>
    object? CarTag(FuraRig rig) => rig.Career is not { } c ? null : new
    {
        lvl = Book.LevelOf(c.Xp),
        stars = Book.StarsOf(c.Xp),
        tractor = Equalized(rig) ? FuraBook.Chumak : c.Pick.Tractor,
        look = Look(c),
    };

    /// <summary>ДВ за рейс у підсумку (0 — нема чи ще не рахувався).</summary>
    static int ResultXp(FuraRig rig) => rig.Career?.Last is { } l && rig.Result is { } r && l.Trip == r.Trip ? l.Xp : 0;

    /// <summary>Що вдягнуто на фуру з Лавки (id речей); нічого — null.</summary>
    static object? Look(FuraCareer c)
    {
        var w = c.Worn;
        if (w.Count == 0) return null;
        string? Of(string slot) => w.TryGetValue(slot, out var id) ? id : null;
        return new
        {
            paint = Of(LavkaKind.FuraPaint),
            gloss = Of(LavkaKind.FuraGloss),
            art = Of(LavkaKind.FuraArt),
            horn = Of(LavkaKind.FuraHorn),
            dash = Of(LavkaKind.FuraDash),
            stickers = StickerSlots.Select(Of).Where(x => x is not null).ToArray(),
            flag = Of(LavkaKind.FuraFlag),
            lamp = Of(LavkaKind.FuraLamp),
        };
    }
}
