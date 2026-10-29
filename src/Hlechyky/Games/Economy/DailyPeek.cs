namespace Hlechyky.Games.Economy;

/// <summary>
/// «Свою щоденну вже розгадав?» для «👁 Підглянути» (<see cref="Rooms.PeekRefusal(string, string)"/>). Рахується лише розгадана:
/// у сапері дня після вибуху можна «Ану ще раз» на тому самому полі, і підглянуте стало б підказкою (і очками Січі).
/// </summary>
public sealed class DailyPeek(Daily daily) : IDailyPeek
{
    public bool? Solved(string gameId, string nick) =>
        daily.Puzzles.Contains(gameId, StringComparer.Ordinal) ? daily.MyResult(nick, gameId)?.Solved == true : null;
}
