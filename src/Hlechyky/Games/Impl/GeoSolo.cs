using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Де це? Тренування» — те саме ядро, фіксовані правила: п'ять фото по хвилині, усі місця, мапа з підказками,
/// до 25 000 очок. Окремий паспорт потрібен заради таблиці: рекорд/спроби каркас показує лише для
/// <c>MaxPlayers == 1</c>.
/// </summary>
public sealed class GeoSolo : Game
{
    public override GameInfo Info { get; } = new(
        "geo-solo", "Де це? Тренування", "тренування «Де це?»", GameGroup.Solo, 1, 1,
        TickMs: Geo.TickMs, Start: StartMode.Immediate, Private: true, Rated: false, Score: ScoreOrder.HigherIsBetter,
        Hint: "П'ять фото, по хвилині на кожне, до 25 000 очок. Найкращий результат — у таблицю",
        Client: "geo");

    GeoMatch _m = null!;

    public override string SeatName(int seat) => "1";

    public override void Configure(IReadOnlyDictionary<string, string> options) =>
        _m = new GeoMatch(Ctx, GeoRules.Training, Ctx.Services.GetService<GeoPhotos>(), 1, Info.Title);

    /// <summary>«Ще раз» без готових фото — чесна відмова тостом, а не порожня партія.</summary>
    public override string? CanStart() => _m.CanStart();
    public override void Start() => _m.Start();
    public override ActResult Act(int seat, string action, JsonElement payload) => _m.Act(seat, action, payload);
    public override TickResult Tick() => _m.Tick();
    public override object View(int? seat) => _m.View(seat);
    public override object? Frame() => _m.Frame();
    public override void OnLeave(int seat) => _m.OnLeave(seat);
}
