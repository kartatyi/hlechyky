using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Де це? Виклик» (пакет 4) — прихована соло-гра: список викликів друзів, а взявся — рівно ті самі п'ять фото, що
/// грав автор, по хвилині, одна спроба. Почате живе в <see cref="GeoChallenges"/> (раунд за раундом), тож закрив
/// вкладку посеред — наступного разу гра сама продовжить з того фото, на якому зупинився.
/// <para>
/// Приховане: км і очки автора у вид ідуть лише по вже розкритих раундах цієї спроби; місць і фото наперед — ніде.
/// Наприкінці — порівняння по раундах (автор / я / решта, хто вже дограв) і рядок у Журнал.
/// </para>
/// </summary>
public sealed class GeoChallenge : Game, IUnlistedGame
{
    public const string Take = "take";
    public const string Busy = "Спершу дограй цей виклик";

    public override GameInfo Info { get; } = new(
        "geo-challenge", "Де це? Виклик", "виклик «Де це?»", GameGroup.Solo, 1, 1,
        TickMs: Geo.TickMs, Start: StartMode.Immediate, Private: true, Rated: false, Score: ScoreOrder.HigherIsBetter,
        Hint: "П'ять фото друга — ті самі, що грав він. Одна спроба, наприкінці порівняння по раундах",
        Client: "geo");

    GeoMatch _m = null!;
    GeoChallenges? _svc;
    /// <summary>Виклик, який зараз граємо; null — список.</summary>
    GeoCh? _ch;
    /// <summary>Скільки раундів спроби вже записано в сервіс.</summary>
    int _logged;
    string _nick = "", _key = "";

    public override string SeatName(int seat) => "1";

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _svc = Ctx.Services.GetService<GeoChallenges>();
        _m = NewMatch();
    }

    GeoMatch NewMatch() => new(Ctx, GeoRules.Training, Ctx.Services.GetService<GeoPhotos>(), 1, Info.Title,
        Ctx.Services.GetService<GeoSeen>(), Ctx.Services.GetService<GeoMine>()) { ChInfo = View_, Ending = End };

    /// <summary>Нова «партія» — це список; почата й не дограна спроба відкривається одразу.</summary>
    public override void Start()
    {
        _nick = Ctx.NickOf(0) ?? "";
        _key = GeoChallenges.KeyOf(_nick);
        _ch = null;
        _logged = 0;
        _m = NewMatch();
        if (_svc?.Unfinished(_nick) is { } open && open.TryOf(_key) is { } t) Begin(open, t);
    }

    string? Begin(GeoCh ch, GeoChTry t)
    {
        var m = NewMatch();
        if (m.Dealable(ch.Rounds) is { } error) return error;
        _m = m;
        _ch = ch;
        _m.Deal = ch.Rounds;
        _m.Start();
        _logged = t.Points.Length;
        if (t.Points.Length > 0) _m.ResumeChallenge(t.Km, t.Points);
        if (_m.Phase == GeoMatch.PhaseDone)
        {
            // усі раунди записано, а кінця не дочекались (рестарт між останнім розкриттям і підсумком): дограно
            _ch = _svc!.Finish(ch.Id, _key) ?? ch;
            Ctx.Finish([], "");
        }
        _m.Touch();
        return null;
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == Take) return TakeAct(payload);
        if (action == GeoMatch.ChallengeAct) return ActResult.Fail("Свій виклик кидають після тренування");
        return _m.Act(seat, action, payload);
    }

    ActResult TakeAct(JsonElement payload)
    {
        if (_ch is not null) return ActResult.Fail(Busy);
        if (_svc is null) return ActResult.Fail("Виклики зараз не працюють");
        var id = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("id", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        // перевірка фото — до того, як спроба зарахується: «фото ще качаються» не має з'їдати єдину спробу
        if (_svc.Find(id) is { } peek && NewMatch().Dealable(peek.Rounds) is { } notReady) return ActResult.Fail(notReady);
        var (ch, t, error) = _svc.Begin(id, _nick, Ctx.Clock.UtcNow);
        if (ch is null || t is null) return ActResult.Fail(error ?? GeoChallenges.Gone);
        return Begin(ch, t) is { } failed ? ActResult.Fail(failed) : ActResult.Done;
    }

    public override TickResult Tick()
    {
        var r = _m.Tick();
        Sync();
        return r;
    }

    /// <summary>Нові розкриті раунди — у сервіс (фоном у базу): так спроба переживе закриту вкладку й рестарт.</summary>
    void Sync()
    {
        if (_ch is null || _svc is null) return;
        var list = _m.RecapList;
        for (; _logged < list.Count; _logged++)
            _svc.Progress(_ch.Id, _key, list[_logged].Km, list[_logged].Points);
    }

    /// <summary>Кінець спроби: дограно, рядок у Журнал «📍 Виклик «Де це?»: Петро 18 450 : 17 900 Оля».</summary>
    string End(long total)
    {
        Sync();
        if (_ch is null || _svc is null) return "";
        _ch = _svc.Finish(_ch.Id, _key) ?? _ch;
        return $"📍 Виклик «Де це?»: {_nick} {GeoText.Num(total)} : {GeoText.Num(_ch.Total)} {_ch.Nick}";
    }

    public override object View(int? seat) => _m.View(seat);
    public override object? Frame() => _m.Frame();
    public override void OnLeave(int seat) => _m.OnLeave(seat);

    // ---------------------------------------------------------------------------------------
    // поле ch у виді
    // ---------------------------------------------------------------------------------------

    static object Km(double? km) => km is { } k ? Math.Round(k, 1, MidpointRounding.AwayFromZero) : null!;

    object? View_(int me)
    {
        if (me != 0 || _svc is null) return null;
        var now = Ctx.Clock.UtcNow;
        if (_ch is null)
            return new
            {
                pick = true,
                list = _svc.For(_nick, now).Select(c => (c, t: c.TryOf(_key))).Select(x => new
                {
                    id = x.c.Id,
                    by = x.c.Nick,
                    at = x.c.At,
                    total = x.c.Total,
                    rounds = x.c.Rounds.Length,
                    played = x.c.Tries.Count(y => y.Done),
                    state = x.t is null ? "new" : x.t.Done ? "done" : "open",
                    my = x.t is { Done: true } ? x.t.Total : (long?)null,
                }).ToArray(),
                mine = _svc.Mine(_nick, now).Select(c => new
                {
                    id = c.Id,
                    at = c.At,
                    total = c.Total,
                    tries = c.Tries.Where(x => x.Done).OrderByDescending(x => x.Total).Select(x => new { nick = x.Nick, total = x.Total }).ToArray(),
                }).ToArray(),
            };
        // свіжий стан виклику (решта могла дограти, поки я грав) — з пам'яті сервісу
        var ch = _svc.Find(_ch.Id) ?? _ch;
        var done = _m.Phase == GeoMatch.PhaseDone;
        var shown = done ? ch.Rounds.Length : Math.Min(_m.RecapList.Count, ch.Rounds.Length);
        return new
        {
            id = ch.Id,
            by = ch.Nick,
            // автор — лише по розкритих раундах: його км до моєї шпильки — підказка
            author = ch.Rounds.Take(shown).Select(r => new { km = Km(r.Km), points = r.Points }).ToArray(),
            total = done ? ch.Total : (long?)null,
            cmp = !done ? null : new
            {
                names = _m.RecapList.Select(r => r.Name).ToArray(),
                rows = ch.Tries.Where(t => t.Done && t.Key != _key).OrderByDescending(t => t.Total)
                    .Select(t => new { nick = t.Nick, total = t.Total, km = t.Km.Select(Km).ToArray(), points = t.Points })
                    .ToArray(),
            },
        };
    }
}
