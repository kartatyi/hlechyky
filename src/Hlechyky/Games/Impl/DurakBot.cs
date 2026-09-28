namespace Hlechyky.Games.Impl;

/// <summary>
/// Глек за столом дурня: б'є найдешевшою картою (спершу не козирем), не може — бере; переводить, коли є дешева
/// карта того самого номіналу; заходить наймолодшою; підкидає лише не козирі (козирі — коли колода скінчилась).
/// Бачить лише свою руку й стіл — як і людина.
/// </summary>
public static class DurakBot
{
    /// <summary>Ціна карти для Глека: козир дорожчий за будь-яку просту.</summary>
    static int Cost(DurakCore c, int card) => (DurakCards.Suit(card) == c.Trump ? 100 : 0) + DurakCards.Rank(card);

    /// <summary>Один крок Глека на місці <paramref name="seat"/>; кличеться, лише коли стіл на нього чекає (MayAct).</summary>
    public static void Move(DurakCore c, int seat, Random rng)
    {
        var hand = c.Hands[seat].OrderBy(x => Cost(c, x)).ToList();
        if (c.Phase == DurakPhase.Defend && seat == c.Defender)
        {
            if (c.CanTransfer(seat))
            {
                var rank = DurakCards.Rank(c.Table[0].Attack);
                var cheap = hand.FirstOrDefault(x => DurakCards.Rank(x) == rank && DurakCards.Suit(x) != c.Trump, -1);
                if (cheap >= 0 && rng.Next(3) > 0 && c.Transfer(seat, cheap) is null) return;
            }
            foreach (var pair in c.Table)
            {
                if (pair.Defend is not null) continue;
                var beat = hand.FirstOrDefault(x => c.Beats(x, pair.Attack), -1);
                if (beat < 0 || c.Defend(seat, pair.Attack, beat) is not null) c.Take(seat);
                return;
            }
            return;
        }
        if (c.Table.Count == 0)
        {
            if (hand.Count > 0) c.Attack(seat, hand[0]);
            return;
        }
        var ranks = c.Ranks();
        var endgame = c.Deck.Count == 0;
        foreach (var card in hand)
        {
            if (!ranks.Contains(DurakCards.Rank(card))) continue;
            if (!endgame && (DurakCards.Suit(card) == c.Trump || DurakCards.Rank(card) >= 6)) continue;   // дами й старші — при собі
            if (c.Attack(seat, card) is null) return;
        }
        c.Done(seat);
    }
}
