using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Де це? дня» — ті самі п'ять фото всім на цілу київську добу, по хвилині на кожне, одна спроба. Рахунок — у
/// таблицю «за день» (як у «Забігу дня»: не <see cref="IDailyGame"/>, бо панель «Щоденного глека» читає число як
/// спроби чи мілісекунди, а тут очки — більше краще). Наприкінці — рядок «📍 Де це? дня №12: 18 450 🟩🟩🟨⬛🟩»,
/// який можна скопіювати й похвалитись у Балачках.
/// <para>
/// Стан (<c>Persistent</c>) — зіграні раунди й сума: закрив посеред дня — продовжиш з наступного фото, а не з
/// першого (інакше «переграти, вже знаючи відповіді» було б найкращою стратегією).
/// </para>
/// </summary>
public sealed class GeoDaily : Game
{
    public const string Played = "Сьогоднішні п'ять фото вже зіграно — нові після півночі";

    public override GameInfo Info { get; } = new(
        "geo-daily", "Де це? дня", "«Де це? дня»", GameGroup.Solo, 1, 1,
        TickMs: Geo.TickMs, Start: StartMode.Immediate, Private: true, Persistent: true, Rated: false, Score: ScoreOrder.HigherIsBetter,
        Hint: "Ті самі п'ять фото всім на цілу добу, одна спроба. Хто сьогодні найзіркіший?",
        Client: "geo");

    GeoMatch _m = null!;
    string _day = "";

    public override string SeatName(int seat) => "1";

    /// <summary>З днем, але без «daily:» — інакше очки лягли б у щоденний глек як «спроби» (як у «Забігу дня»).</summary>
    public override string SoloKey(string nickKey, IClock clock) => $"geo-daily:{Days.Today(clock)}:{nickKey}";

    public override void Configure(IReadOnlyDictionary<string, string> options) =>
        _m = new GeoMatch(Ctx, GeoRules.Training, Ctx.Services.GetService<GeoPhotos>(), 1, Info.Title, Ctx.Services.GetService<GeoSeen>());

    /// <summary>«Ще раз» того самого дня — чесна відмова: день один, спроба одна.</summary>
    public override string? CanStart() =>
        _m.Phase == GeoMatch.PhaseDone && _day == Days.Today(Ctx.Clock) && _m.RecapList.Count > 0 ? Played : _m.CanStart();

    public override void Start()
    {
        _day = Days.Today(Ctx.Clock);
        _m.Day = _day;
        _m.Start();
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
    }
}
