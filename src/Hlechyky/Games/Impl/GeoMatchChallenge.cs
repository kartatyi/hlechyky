using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// 🤝 Виклик «Зіграй мої 5 фото» (пакет 4) — частина ядра «Де це?», винесена окремо, щоб не перекроювати основне:
/// «кинути виклик» після свого тренування (<see cref="Challenge"/>), роздати рівно ті самі фото (<see cref="Deal"/>),
/// продовжити почате (<see cref="ResumeChallenge"/>) і свій рядок Журналу замість таблиці рекордів (<see cref="Ending"/>).
/// </summary>
public sealed partial class GeoMatch
{
    public const string ChallengeAct = "challenge";
    public const string ChallengeMade = "Виклик кинуто — друзі побачать його в «🤝 Виклики»";

    /// <summary>Роздати рівно ці місця й фото (виклик друга) замість випадкових. null — звичайна роздача.</summary>
    public IReadOnlyList<GeoChRound>? Deal { get; set; }

    /// <summary>
    /// Кінець виклику: рядок Журналу за сумою очок. Коли задано, очки не йдуть у таблицю рекордів (там змішались би
    /// різні виклики), а черепки й ачівки — як у тренуванні.
    /// </summary>
    public Func<long, string>? Ending { get; set; }

    /// <summary>Поле <c>ch</c> у виді для гри-виклику; null — ядро саме каже, чи можна кинути виклик.</summary>
    public Func<int, object?>? ChInfo { get; set; }

    /// <summary>Id виклику, кинутого з цієї партії («Ще раз» скидає).</summary>
    string? _made;

    /// <summary>Розіслати вид найближчим тиком (дія в реалтайм-грі сама видів не шле).</summary>
    public void Touch() => _dirty = _frameDirty = true;

    /// <summary>Тренування дограно повністю — його можна кинути друзям.</summary>
    bool Challengeable => _rules.Solo && Day is null && Ending is null && Deal is null && _phase == PhaseDone
        && _rounds.Count > 0 && _recap.Count == _rounds.Count && _rounds.All(r => r.Mine is null);

    object? ChView(int me) =>
        ChInfo is { } f ? f(me) : me == 0 && Challengeable ? new { can = _made is null, made = _made is not null } : null;

    ActResult Challenge(int seat)
    {
        if (!Challengeable && _phase != PhaseDone) return ActResult.Fail("Спершу дограй свої п'ять фото");
        if (!Challengeable || seat != 0) return ActResult.Fail("Виклик кидають після свого тренування");
        if (_made is not null) return ActResult.Fail("Цей виклик уже кинуто");
        var nick = _ctx.NickOf(0) ?? _nicks[0];
        if (nick is null || Auth.IsGuestNick(nick)) return ActResult.Fail("Кидати виклики можуть ті, хто назвався");
        if (_ctx.Services.GetService<GeoChallenges>() is not { } svc) return ActResult.Fail("Виклики зараз не працюють");
        var rounds = new List<GeoChRound>(_rounds.Count);
        for (var i = 0; i < _rounds.Count; i++)
            rounds.Add(new GeoChRound(_rounds[i].Place.Id, _rounds[i].Photo, _recap[i].Km, _recap[i].Points));
        var (ch, error) = svc.Add(nick, rounds, Day, _ctx.Clock.UtcNow);
        if (ch is null) return ActResult.Fail(error ?? "Не вийшло");
        _made = ch.Id;
        _ctx.Log($"📍 {nick} кидає виклик: зіграй мої {rounds.Count} фото — «Де це?» → 🤝 Виклики");
        _dirty = true;
        return ActResult.Accept(ChallengeMade);
    }

    /// <summary>Чи можна роздати ці фото просто зараз: null — так, інакше текст відмови (і штовхнути завантажувач).</summary>
    public string? Dealable(IReadOnlyList<GeoChRound> deal)
    {
        if (_photos is null) return NoBank;
        var mine = _mine ?? _ctx.Services.GetService<GeoMine>();
        foreach (var d in deal)
        {
            if (d.Place.StartsWith("u:", StringComparison.Ordinal))
            {
                if (mine?.Find(d.Place[2..]) is null) return "Одне з фото цього виклику автор уже прибрав";
                continue;
            }
            if (Place(d.Place) is not { } p || d.Photo < 0 || d.Photo >= p.Photos.Count) return "Одного з місць цього виклику вже нема в грі";
            if (!_photos.ReadyPhotos(p.Id).Contains(d.Photo)) { _photos.Poke(); return NoPhotos; }
        }
        return null;
    }

    GeoPlace? Place(string id)
    {
        foreach (var p in _photos!.Bank.Places) if (p.Id == id) return p;
        return null;
    }

    /// <summary>Роздача виклику: рівно ті місця й фото, у тому самому порядку (<see cref="Dealable"/> уже перевірив).</summary>
    void DealFixed()
    {
        var mine = _mine ?? _ctx.Services.GetService<GeoMine>();
        foreach (var d in Deal!)
        {
            if (d.Place.StartsWith("u:", StringComparison.Ordinal))
            {
                if (mine?.Find(d.Place[2..]) is { } mp) _rounds.Add(new Round(mp.Place(), 0, mine.PathOf(mp.Id), mp));
            }
            else if (_photos is not null && Place(d.Place) is { } p && d.Photo >= 0 && d.Photo < p.Photos.Count)
                _rounds.Add(new Round(p, d.Photo, _photos.PathFor(p.Photos[d.Photo])));
        }
        Begin();
    }

    /// <summary>Продовжити почату спробу виклику: зіграні раунди — з <paramref name="km"/>/<paramref name="points"/>.</summary>
    public void ResumeChallenge(IReadOnlyList<double?> km, IReadOnlyList<int> points)
    {
        var recap = new List<Recap>(points.Count);
        long total = 0;
        for (var i = 0; i < points.Count && i < _rounds.Count; i++)
        {
            var p = _rounds[i].Place;
            recap.Add(new Recap(p.Name, p.Region, null, [], km[i] is null ? null : 0, km[i], points[i]));
            total += points[i];
        }
        Resume(recap, total);
    }
}
