using Hlechyky.Bets;
using Microsoft.AspNetCore.SignalR;

namespace Hlechyky;

/// <summary>
/// «🎲 Ставки» на столі — через хаб, а не HTTP: глядач — це з'єднання, що дивиться стіл (Rooms.Watch), і лише хаб знає,
/// яке воно. Відповідь — { ok, message, status, data } (дані панелі — { ok, on, show, … }, див. <see cref="TableBets.View"/>).
/// </summary>
public sealed partial class RadioHub
{
    TableBets TableBetsOf() => Context.GetHttpContext()!.RequestServices.GetRequiredService<TableBets>();

    BetActor Bettor()
    {
        var http = Context.GetHttpContext();
        return new BetActor(Nick(), http is not null && Auth.IsUser(http), http is not null && Auth.IsAdmin(http));
    }

    /// <summary>Панель ставок столу: ринки з кефами, мої й чужі ставки, підсумок щойно дограної партії.</summary>
    public object BetsOfTable(string roomId)
    {
        if (!Allow(input: true)) return new { ok = false, message = Games.Say.TooFast };
        return TableBetsOf().View(Bettor(), roomId ?? "", Context.ConnectionId);
    }

    /// <summary>Поставити на столі. <paramref name="odds"/> — кеф, який бачила людина; <paramref name="key"/> — від подвійного кліку.</summary>
    public object BetOnTable(string roomId, string market, string option, int stake, double? odds, string? key)
    {
        if (!Allow(input: false)) return new { ok = false, message = Games.Say.TooFast };
        var r = TableBetsOf().Place(Bettor(), roomId ?? "", Context.ConnectionId,
            new TableBets.TableBetRequest(market, option, stake, odds, key));
        return new { ok = r.Ok, message = r.Message, status = r.Status, data = r.Data };
    }
}
