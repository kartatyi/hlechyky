using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Турнір тижня в автоматі (пакет 5, docs/games/specs/slot-tourney.md): дія <c>tspin</c> і блок <c>tourney</c> у виді.
/// Оберт на фішках спроби — у <see cref="SlotTourney"/> (пам'ять, без бази); тут лише сценарій для клієнта: гаманець,
/// Скарбничка, Ворожка, рекорди й ачівки автомата не чіпаються.
/// </summary>
public abstract partial class SlotGame
{
    public const string ActTourney = "tspin";

    SlotTourney? _tourney;

    void ConfigureTourney()
    {
        try { _tourney = Ctx?.Services?.GetService<SlotTourney>(); }
        catch { _tourney = null; }
    }

    object? TourneyView(int? seat) =>
        _tourney?.Wire(Info.Id, seat is { } s ? Ctx.NickOf(s) : null, Ctx.Clock.UtcNow);

    ActResult TourneySpin(int seat, JsonElement payload)
    {
        if (_tourney is null) return ActResult.Fail("Турніру зараз нема — Глек рахує фішки");
        if (_bank is not null && !_bank.O.Enabled) return ActResult.Fail(OffText);
        if (Ctx.NickOf(seat) is not { } nick) return ActResult.Fail("Тут так не ходять");
        // повтор запиту (клієнт шле seq, який бачив): оберт уже пішов
        if (Int(payload, "seq") is { } seen && seen != State.Seq) return ActResult.Done;
        var error = _tourney.Spin(nick, Info.Id, SlotMath, R, Ctx.Clock.UtcNow, out var res);
        if (error is not null || res is null) return ActResult.Fail(error ?? "Тут так не ходять");
        var script = res.Script;
        script["tourney"] = new JsonObject { ["chips"] = res.Chips, ["spin"] = res.Spin, ["of"] = res.Of, ["done"] = res.Done };
        State.Seq++;
        State.Last = script;
        State.Gamble = null;
        return ActResult.Done;
    }
}
