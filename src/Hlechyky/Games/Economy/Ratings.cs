namespace Hlechyky.Games.Economy;

/// <summary>
/// Ело для рейтингових ігор на двох (<c>Rated &amp;&amp; MaxPlayers == 2</c>). Старт 1000, K=32, а перші
/// десять партій — K=48, щоб новачок швидше знайшов своє місце. Поразка через вихід із-за столу — звичайна поразка.
/// </summary>
public sealed class Ratings(EconomyStore store, IClock clock)
{
    public const int Start = 1000;
    public const int KNormal = 32;
    public const int KNew = 48;
    /// <summary>Скільки перших партій рахуються з більшим K.</summary>
    public const int NewGames = 10;

    /// <summary>Очікуваний результат гравця з рейтингом <paramref name="elo"/> проти <paramref name="opponent"/>.</summary>
    public static double Expected(int elo, int opponent) => 1.0 / (1.0 + Math.Pow(10, (opponent - elo) / 400.0));

    public static int K(int games) => games < NewGames ? KNew : KNormal;

    /// <summary>Новий рейтинг. <paramref name="score"/>: 1 — перемога, 0.5 — нічия, 0 — поразка.</summary>
    public static int Next(int elo, int games, double score, int opponent) =>
        elo + (int)Math.Round(K(games) * (score - Expected(elo, opponent)), MidpointRounding.AwayFromZero);

    /// <summary>
    /// Порахувати партію двох. <paramref name="scoreA"/> — 1/0.5/0 для першого. Обидва рейтинги читаються
    /// й пишуться в одній транзакції: інакше дві партії поспіль могли б порахуватись від старого числа.
    /// </summary>
    public (RatingRow A, RatingRow B) Apply(string game, (string Key, string Nick) a, (string Key, string Nick) b, double scoreA) =>
        store.UpdatePair(game, a, b, (ra, rb) =>
        {
            var scoreB = 1 - scoreA;
            var na = ra with
            {
                Elo = Next(ra.Elo, ra.Games, scoreA, rb.Elo),
                Games = ra.Games + 1,
                Wins = ra.Wins + (scoreA == 1 ? 1 : 0),
                Draws = ra.Draws + (scoreA == 0.5 ? 1 : 0),
                Losses = ra.Losses + (scoreA == 0 ? 1 : 0),
            };
            var nb = rb with
            {
                Elo = Next(rb.Elo, rb.Games, scoreB, ra.Elo),
                Games = rb.Games + 1,
                Wins = rb.Wins + (scoreB == 1 ? 1 : 0),
                Draws = rb.Draws + (scoreB == 0.5 ? 1 : 0),
                Losses = rb.Losses + (scoreB == 0 ? 1 : 0),
            };
            return (na, nb);
        }, clock.UtcNow);

    /// <summary>Рейтинг ніка в грі; нема запису — «ще не грав» із стартовою тисячею.</summary>
    public RatingRow Of(string nick, string game) =>
        store.Rating(Economy.Key(nick), game) ?? new RatingRow(Economy.Key(nick), nick, game, Start, 0, 0, 0, 0);

    public List<RatingRow> Top(string game, int n = 20) => store.TopRatings(game, n);

    public List<RatingRow> AllOf(string nick) => store.RatingsOf(Economy.Key(nick));
}
