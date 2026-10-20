using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Де це? дня» — ті самі п'ять фото всім на цілу київську добу, по хвилині на кожне, одна спроба. З пакета 4 —
/// у «Щоденному глеку» (<see cref="IDailyGame"/>): панель «☀ Сьогодні», серія 🔥 і нагадування. Каркас рахує
/// «менше — краще» (спроби, мілісекунди), тож у <c>daily_results</c> іде лише «зіграно» (<c>Ctx.Score(0, 0, 1)</c>),
/// а очки — у свою таблицю дня <see cref="GeoDailyBoard"/> (вона ж через <see cref="IDailyPoints"/> дає панелі
/// «✓ зіграно · N очок»). Наприкінці — рядок «📍 Де це? дня №12: 18 450 🟩🟩🟨⬛🟩», який можна скопіювати.
/// <para>
/// Стан (<c>Persistent</c>) — зіграні раунди й сума: закрив посеред дня — продовжиш з наступного фото, а не з
/// першого (інакше «переграти, вже знаючи відповіді» було б найкращою стратегією).
/// </para>
/// </summary>
public sealed class GeoDaily : Game, IDailyGame
{
    public const string Played = "Сьогоднішні п'ять фото вже зіграно — нові після півночі";

    public override GameInfo Info { get; } = new(
        "geo-daily", "Де це? дня", "«Де це? дня»", GameGroup.Solo, 1, 1,
        TickMs: Geo.TickMs, Start: StartMode.Immediate, Private: true, Persistent: true, Rated: false, Score: ScoreOrder.HigherIsBetter,
        Hint: "Ті самі п'ять фото всім на цілу добу, одна спроба. Хто сьогодні найзіркіший?",
        Client: "geo");

    GeoMatch _m = null!;
    string _day = "";
    GeoDailyBoard? _board;
    /// <summary>День уже пішов у таблицю й «Щоденний глек» — удруге не пишемо.</summary>
    bool _reported;

    public override string SeatName(int seat) => "1";

    /// <summary>
    /// Ключ щоденного каркаса: «daily:geo-daily:&lt;день&gt;:&lt;нік&gt;». До пакета 4 був «geo-daily:…» (без «daily:») —
    /// сьогоднішній стан під старим ключем просто не підхопиться (за весь час гру зіграно раз).
    /// </summary>
    public override string SoloKey(string nickKey, IClock clock) => $"daily:{Info.Id}:{Days.Today(clock)}:{nickKey}";

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _m = new GeoMatch(Ctx, GeoRules.Training, Ctx.Services.GetService<GeoPhotos>(), 1, Info.Title, Ctx.Services.GetService<GeoSeen>())
        {
            DailyDone = Report,
            Extra = DayView,
        };
        _board = Ctx.Services.GetService<GeoDailyBoard>();
    }

    /// <summary>«Ще раз» того самого дня — чесна відмова: день один, спроба одна.</summary>
    public override string? CanStart() =>
        _m.Phase == GeoMatch.PhaseDone && _day == Days.Today(Ctx.Clock) && _m.RecapList.Count > 0 ? Played : _m.CanStart();

    public override void Start()
    {
        var today = Days.Today(Ctx.Clock);
        if (_day != today) _reported = false;
        _day = today;
        _m.Day = _day;
        _m.Start();
    }

    /// <summary>
    /// Кінець дня (з <see cref="GeoMatch"/>, ще під замком — лише пам'ять і події): хоч одна шпилька — рядок у
    /// таблицю дня й «зіграно» в щоденний глек; жодної — «✗ не вийшло» (<c>DailyMiss</c>), серія не росте.
    /// Нагорода дня — як і була: <see cref="GeoMatch.DailyRewardFrom"/> очок, її платить ядро.
    /// </summary>
    void Report(int seat, bool pinned)
    {
        if (_reported) return;
        _reported = true;
        var nick = Ctx.NickOf(seat) ?? "";
        if (!pinned)
        {
            Ctx.DailyMiss(seat, 1);
            Ctx.Services.GetService<DailyCard>()?.NoteMiss(Info.Id, nick, 1);
            return;
        }
        _board?.Record(_day, nick, (int)Math.Min(int.MaxValue, _m.Total(seat)), _m.Squares(), Ctx.Clock.UtcNow);
        // Спроба в щоденному каркасі одна; очки там не лягають (він рахує «менше — краще»), тож час — 0.
        Ctx.Score(seat, 0, 1);
    }

    /// <summary>Скільки рядків таблиці дня показувати в грі.</summary>
    public const int BoardRows = 10;

    /// <summary>
    /// Таблиця дня у виді (<c>extra</c>). До кінця своїх п'яти фото — лише хто вже грав сьогодні, без очок і смужок
    /// (інакше це підказка «хтось набрав 24 000 — фото легкі»). Після — місце, нік, очки, 🟩🟨⬛ і серія 🔥.
    /// </summary>
    object? DayView(int? seat)
    {
        if (_day.Length == 0) return null;
        var top = _board?.Top(_day) ?? [];
        var nick = Ctx.NickOf(0) ?? "";
        var me = GeoDailyBoard.NickKey(nick);
        if (_m.Phase != GeoMatch.PhaseDone)
            return new
            {
                no = Days.Number(_day),
                played = top.Where(r => GeoDailyBoard.NickKey(r.Nick) != me).Select(r => r.Nick).ToArray(),
            };
        var place = top.ToList().FindIndex(r => GeoDailyBoard.NickKey(r.Nick) == me);
        return new
        {
            no = Days.Number(_day),
            board = top.Take(BoardRows).Select(r => new
            {
                nick = r.Nick,
                points = r.Points,
                marks = r.Marks,
                streak = _board!.Streak(r.Nick, _day),
                me = GeoDailyBoard.NickKey(r.Nick) == me,
            }).ToArray(),
            place = place < 0 ? (int?)null : place + 1,
            players = top.Count,
            streak = _board?.Streak(nick, _day) ?? 0,
        };
    }

    public override ActResult Act(int seat, string action, JsonElement payload) => _m.Act(seat, action, payload);
    public override TickResult Tick() => _m.Tick();
    public override object View(int? seat) => _m.View(seat);
    public override object? Frame() => _m.Frame();
    public override void OnLeave(int seat) => _m.OnLeave(seat);

    sealed record State(string Day, long Total, List<GeoMatch.Recap> Recap);

    public override string? Save() =>
        _m.RecapList.Count == 0 ? null : JsonSerializer.Serialize(new State(_day, _m.Total(0), [.. _m.RecapList]));

    public override void Load(string json)
    {
        State? s;
        try { s = JsonSerializer.Deserialize<State>(json); }
        catch (JsonException) { return; }
        if (s is null || s.Day != _day || s.Recap is null) return;
        _m.Resume(s.Recap, s.Total);
        // Відновили вже дограний день — таблиця й щоденний глек свого вже отримали.
        if (_m.Phase == GeoMatch.PhaseDone) _reported = true;
    }
}
