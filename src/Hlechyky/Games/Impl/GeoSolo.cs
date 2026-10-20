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
        _m = new GeoMatch(Ctx, GeoRules.Training, Ctx.Services.GetService<GeoPhotos>(), 1, Info.Title, Ctx.Services.GetService<GeoSeen>())
        {
            Extra = WeakView,
        };

    /// <summary>«Ще раз» без готових фото — чесна відмова тостом, а не порожня партія.</summary>
    public override string? CanStart() => _m.CanStart();

    public override void Start()
    {
        // «🎯 Моя слабка» — на одну партію: наступне «Ще раз» знову з усієї України
        _m.Region = _weakNext;
        _weakNext = null;
        _m.Start();
    }

    /// <summary>«🎯 Моя слабка» — тут; 🤝 «Кинути виклик» та решта — у ядро.</summary>
    public override ActResult Act(int seat, string action, JsonElement payload) =>
        action == Weak ? PickWeak(seat) : _m.Act(seat, action, payload);

    // ---------------------------------------------------------------- пакет 4: «🎯 Моя слабка»

    /// <summary>Дія «наступна партія — з моєї слабкої області» (за дограним столом, перед «Ще раз»).</summary>
    public const string Weak = "weak";
    public const string FewData = "Ще мало даних — зіграй кілька партій, і Глек скаже, де ти найчастіше мажеш";

    string? _weakNext;

    /// <summary>«🎯 Моя слабка» і 🤝 «Кинути виклик» — уже після партії, до «Ще раз».</summary>
    public override bool ActsBetween(string action) => action is Weak or GeoMatch.ChallengeAct;

    GeoWeakInfo? InfoOf(int seat) =>
        Ctx.NickOf(seat) is { } nick ? Ctx.Services.GetService<GeoRounds>()?.Info(nick) : null;

    ActResult PickWeak(int seat)
    {
        if (InfoOf(seat)?.Weak is not { } w) return ActResult.Fail(FewData);
        _weakNext = w.Region;
        return ActResult.Done;
    }

    /// <summary>
    /// У виді (<c>extra</c>, лише пам'ять): найслабша область і середні км, або «ще мало даних» (скільки раундів і
    /// областей уже є); <c>focus</c> — з якої області ця партія.
    /// </summary>
    object? WeakView(int? seat)
    {
        if (InfoOf(0) is not { } info) return _m.Region is null ? null : new { focus = _m.Region };
        return new
        {
            weak = info.Weak is { } w ? new { region = w.Region, km = (int)Math.Round(w.AvgKm), rounds = w.Rounds } : null,
            rounds = info.Rounds,
            regions = info.Regions,
            need = GeoRounds.MinRounds,
            needRegions = GeoRounds.MinRegions,
            focus = _m.Region,
        };
    }
    public override TickResult Tick() => _m.Tick();
    public override object View(int? seat) => _m.View(seat);
    public override object? Frame() => _m.Frame();
    public override void OnLeave(int seat) => _m.OnLeave(seat);
}
