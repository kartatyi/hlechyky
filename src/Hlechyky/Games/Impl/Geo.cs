using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Де це?» — фото звідкись з України: тицьни на мапу, де це знято. Що ближче шпилька, то більше очок: 5000 за
/// кілометр і ближче, 2885 за сто, крихти за пів країни. Стіл на 1–10 (господар може почати й сам), 5/7/10
/// раундів, 30/45/60 секунд, категорії місць, складність і три рівні підказок на мапі.
/// <para>
/// Уся логіка — у спільному ядрі <see cref="GeoMatch"/>; тут лише паспорт, опції й <see cref="CanStart"/>.
/// Тренування з таблицею рекордів — окрема гра <see cref="GeoSolo"/> у тому самому модулі.
/// </para>
/// </summary>
public sealed class Geo : Game
{
    public const int MaxSeats = 10;
    public const int TickMs = 500;
    static readonly int[] RoundChoices = [5, 7, 10];
    static readonly int[] SecondChoices = [30, 45, 60];

    public override GameInfo Info { get; } = new(
        "geo", "Де це?", "«Де це?»", GameGroup.Party, 1, MaxSeats,
        TickMs: TickMs, Start: StartMode.ByHost, Hidden: true, Rated: false, Score: ScoreOrder.HigherIsBetter,
        Options:
        [
            new GameOption("rounds", "Раундів", [.. RoundChoices.Select(n => (Str(n), Str(n)))], "5"),
            new GameOption("seconds", "Час на раунд", [.. SecondChoices.Select(n => (Str(n), $"{n} с"))], "45"),
            new GameOption("cat", "Місця", GeoCats.List, GeoCats.All, Multi: true),
            new GameOption("level", "Складність", [(GeoRules.LevelAll, "Усяка"), (GeoRules.LevelEasy, "Знайомі місця"), (GeoRules.LevelHard, "Для бувалих")], GeoRules.LevelAll),
            new GameOption("hints", "Мапа", [(GeoRules.HintsFull, "З підказками"), (GeoRules.HintsBorders, "Лише області"), (GeoRules.HintsNone, "Голий контур")], GeoRules.HintsFull),
        ],
        Hint: "Фото звідкись з України — тицьни на мапу, де це. Що ближче, то більше очок. П'ять раундів. Можна й самому");

    static string Str(int n) => n.ToString(CultureInfo.InvariantCulture);

    GeoMatch _m = null!;

    /// <summary>Місця — числами: на столі з десяти «гравець сьомий» у чіп не влізе.</summary>
    public override string SeatName(int seat) => (seat + 1).ToString(CultureInfo.InvariantCulture);

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        int Int(string key, int[] allowed, int fallback) =>
            options.TryGetValue(key, out var v) && int.TryParse(v, CultureInfo.InvariantCulture, out var n) && allowed.Contains(n) ? n : fallback;
        string Of(string key, string fallback, params string[] allowed) =>
            options.TryGetValue(key, out var v) && allowed.Contains(v) ? v : fallback;
        var rules = new GeoRules(
            Int("rounds", RoundChoices, 5),
            Int("seconds", SecondChoices, 45),
            GeoCats.Parse(options.GetValueOrDefault("cat")),
            Of("level", GeoRules.LevelAll, GeoRules.LevelAll, GeoRules.LevelEasy, GeoRules.LevelHard),
            Of("hints", GeoRules.HintsFull, GeoRules.HintsFull, GeoRules.HintsBorders, GeoRules.HintsNone),
            Solo: false);
        _m = new GeoMatch(Ctx, rules, Ctx.Services.GetService<GeoPhotos>(), MaxSeats, Info.Title);
    }

    public override string? CanStart() => _m.CanStart();
    public override void Start() => _m.Start();
    public override ActResult Act(int seat, string action, JsonElement payload) => _m.Act(seat, action, payload);
    public override TickResult Tick() => _m.Tick();
    public override object View(int? seat) => _m.View(seat);
    public override object? Frame() => _m.Frame();
    public override void OnLeave(int seat) => _m.OnLeave(seat);
}
