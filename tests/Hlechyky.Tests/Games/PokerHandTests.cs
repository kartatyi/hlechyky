using System.Diagnostics;
using Hlechyky.Games.Impl;
using Xunit;

namespace Hlechyky.Tests.Games;

public class PokerHandTests
{
    static int E(string cards) => PokerHand.Eval(PokerHand.ParseMany(cards));

    [Theory]
    [InlineData("As Ks Qs Js Ts 2d 3c", "роял-флеш")]
    [InlineData("9h 8h 7h 6h 5h Ad Ac", "стрит-флеш до дев'ятки")]
    [InlineData("5d 4d 3d 2d Ad Kc Kh", "стрит-флеш до п'ятірки")]
    [InlineData("7c 7d 7h 7s Kd 2c 3h", "каре: сімки")]
    [InlineData("Kc Kd Kh 3s 3d 2c 9h", "фул-хаус, королі на трійках")]
    [InlineData("3c 3d 3h Ks Kd 2c 9h", "фул-хаус, трійки на королях")]
    [InlineData("Qc Qd Qh 5s 5d 5c 9h", "фул-хаус, дами на п'ятірках")]
    [InlineData("Ah 9h 7h 4h 2h Kc Qd", "флеш, старша — туз")]
    [InlineData("Tc 9d 8h 7s 6d 2c 2h", "стрит до десятки")]
    [InlineData("Ac 2d 3h 4s 5d Kc Kh", "стрит до п'ятірки")]
    [InlineData("Ac Kd Qh Js Td 2c 2h", "стрит до туза")]
    [InlineData("8c 8d 8h As Kd 2c 4h", "трійка: вісімки")]
    [InlineData("Jc Jd 4h 4s Ad 2c 9h", "дві пари: валети й четвірки")]
    [InlineData("Ac Ad 9h 7s 4d 3c 2h", "пара: тузи")]
    [InlineData("Ac Jd 9h 7s 4d 3c 2h", "старша карта — туз")]
    public void Names(string cards, string name) => Assert.Equal(name, PokerHand.Name(E(cards)));

    [Theory]
    // категорії по старшинству
    [InlineData("As Ks Qs Js Ts", "9h 8h 7h 6h 5h")]   // роял > стрит-флеш
    [InlineData("6h 5h 4h 3h 2h", "Ac Ad Ah As Kd")]   // найменший стрит-флеш > каре
    [InlineData("2c 2d 2h 2s 3d", "Ac Ad Ah Kc Kd")]   // каре > фул-хаус
    [InlineData("2c 2d 2h 3s 3d", "Ah Kh Qh Jh 9h")]   // фул-хаус > флеш
    [InlineData("7h 5h 4h 3h 2h", "Ac Kd Qh Js Td")]   // флеш > стрит
    [InlineData("Ac 2d 3h 4s 5d", "Ac Ad Ah Kc Qd")]   // колесо > трійка
    [InlineData("2c 2d 2h 4s 5d", "Ac Ad Kh Kc Qd")]   // трійка > дві пари
    [InlineData("3c 3d 2h 2s 5d", "Ac Ad Kh Qc Jd")]   // дві пари > пара
    [InlineData("2c 2d 3h 4s 6d", "Ac Kd Qh Jc 9d")]   // пара > старша
    // однакові категорії: кікери
    [InlineData("Ac Ad Kh 4s 3d", "Ac Ad Qh Js Td")]   // пара тузів, кікер король > дама
    [InlineData("Kc Kd 7h 7s Ad", "Kc Kd 7h 7s Qd")]   // дві пари, кікер
    [InlineData("Kc Kd 8h 8s 2d", "Kc Kd 7h 7s Ad")]   // друга пара старша
    [InlineData("9c 9d 9h As 2d", "9c 9d 9h Ks Qd")]   // трійка, кікер
    [InlineData("Qc Qd Qh 2s 2d", "Jc Jd Jh As Ad")]   // фул: трійка важить більше
    [InlineData("Qc Qd Qh 3s 3d", "Qc Qd Qh 2s 2d")]   // фул: пара
    [InlineData("Ah Qh 9h 5h 3h", "Ah Qh 9h 5h 2h")]   // флеш: п'ята карта
    [InlineData("7c 7d 7h 7s Ad", "7c 7d 7h 7s Kd")]   // каре, кікер
    [InlineData("2c 3d 4h 5s 6d", "Ac 2d 3h 4s 5d")]   // стрит до шістки > колесо
    [InlineData("Ac Kd 9h 7s 4d", "Ac Kd 9h 7s 3d")]   // старша, п'ята карта
    public void Order(string better, string worse) => Assert.True(E(better) > E(worse), $"{better} має бути сильніша за {worse}");

    [Theory]
    [InlineData("Ac Kd 9h 7s 4d", "As Kc 9d 7h 4c")]   // ті самі ранги — нічия
    [InlineData("Ac Ad Kh Qs Jd", "Ah As Kd Qc Jh")]
    [InlineData("Th 9h 8h 7h 6h", "Td 9d 8d 7d 6d")]
    public void Ties(string a, string b) => Assert.Equal(E(a), E(b));

    [Fact]
    public void BestOfSeven()
    {
        // з 7 карт: флеш і стрит одночасно — флеш
        Assert.Equal(PokerHand.Flush, PokerHand.Category(E("2h 5h 9h Jh Kh Tc Qd")));
        // три пари — дві старші + старший кікер (зокрема з третьої пари)
        Assert.Equal(E("Ac Ad Kh Ks Qd"), E("Ac Ad Kh Ks Qd Qc 2h"));
        Assert.Equal(E("Ac Ad Kh Ks 9d"), E("Ac Ad Kh Ks 2d 2c 9h"));
        // дві трійки — фул-хаус зі старшою трійкою
        Assert.Equal(E("9c 9d 9h 4s 4d"), E("9c 9d 9h 4s 4d 4c 2h"));
        // стрит на 6 картах — старший
        Assert.Equal(E("9c 8d 7h 6s 5d"), E("9c 8d 7h 6s 5d 4c 2h"));
        // колесо з тузом і ще стрит до шістки
        Assert.Equal(PokerHand.Name(E("6c 5d 4h 3s 2d")), PokerHand.Name(E("Ac 2d 3h 4s 5d 6c Kh")));
        // каре + трійка — каре з кікером
        Assert.Equal(E("8c 8d 8h 8s Kd"), E("8c 8d 8h 8s Kd Kc Kh"));
        // стрит-флеш серед флешу
        Assert.Equal(PokerHand.StraightFlush, PokerHand.Category(E("Ah 2h 3h 4h 5h 9h Kd")));
    }

    [Fact]
    public void BestFiveAndCodes()
    {
        var cards = PokerHand.ParseMany("As Ks Qs Js Ts 2d 3c");
        var five = PokerHand.BestFive(cards).Select(PokerHand.Code).Order().ToArray();
        Assert.Equal(new[] { "As", "Js", "Ks", "Qs", "Ts" }, five);
        Assert.Equal("Td", PokerHand.Code(PokerHand.Parse("Td")));
        Assert.Equal("роял-флеш", PokerHand.Short(E("As Ks Qs Js Ts")));
        Assert.Equal("фул-хаус", PokerHand.Short(E("Kc Kd Kh 3s 3d")));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void ThousandDealsFast()
    {
        var rng = new Random(1);
        var deck = Enumerable.Range(0, 52).ToArray();
        var sw = Stopwatch.StartNew();
        var sum = 0L;
        for (var i = 0; i < 1000; i++)
        {
            for (var k = 0; k < 7; k++) { var j = k + rng.Next(52 - k); (deck[k], deck[j]) = (deck[j], deck[k]); }
            sum += PokerHand.Eval(deck.AsSpan(0, 7));
        }
        sw.Stop();
        Assert.True(sum > 0);
        Assert.True(sw.ElapsedMilliseconds < 50, $"1000 оцінок за {sw.ElapsedMilliseconds} мс");
    }
}
