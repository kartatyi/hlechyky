using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Як пройти кожен із 15 рівнів — план для робота <see cref="VohnykBot"/>. З планів записано журнали
/// <c>solution</c> у файлах рівнів (VOHNYK_RECORD=1); самі тести грають уже записані журнали, тож план —
/// це «як автор рівня його пройшов», а не частина гри. Координати — центри героїв у px (плитка = 40 px:
/// центр клітинки c — c·40+20).
/// </summary>
public static class VohnykPlans
{
    const int F = 0, Wt = 1;

    /// <summary>Центр клітинки c у px.</summary>
    static int C(int c) => c * 40 + 20;

    public static VohnykBot Play(VohnykLevel level)
    {
        var b = new VohnykBot(level);
        switch (level.N)
        {
            case 1: Level1(b); break;
            case 2: Level2(b); break;
            default: throw new InvalidOperationException($"для рівня {level.N} плану ще нема");
        }
        // обоє у своїх дверях — стоїмо, доки рівень не зарахує вихід
        b.Do(b.WaitFor(() => b.W.Cleared != 0), b.Wait(0));
        return b;
    }

    static void Level1(VohnykBot b)
    {
        b.Do(
            VohnykBot.Seq(b.RunJump(F, +1, 180, 30, 330), b.Jump(F, +1, 8, C(10) + 10), b.Go(F, C(18))),
            VohnykBot.Seq(b.Go(Wt, C(9) - 4), b.Jump(Wt, +1, 8, C(10) + 10), b.RunJump(Wt, +1, 585, 30, 720), b.Go(Wt, C(20))));
    }

    static void Level2(VohnykBot b)
    {
        // Крапля — на кнопку b1; Вогник перестрибує воду, проходить двері, стає на b2
        b.Do(b.Wait(0), b.Go(Wt, C(5)));
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.W.DoorO[0] >= 60 * 16), b.RunJump(F, +1, C(6) - 4, 30, C(10)), b.Go(F, C(11))), null);
        // Крапля йде по воді (самоцвіт) і крізь двері; Вогник тримає b2
        b.Do(null, b.Go(Wt, C(12)));
        // Вогник по лаві (самоцвіт) — на ліфт через важіль; Крапля перестрибує лаву за ним
        b.Do(b.Go(F, C(18) + 8), b.WaitFor(() => b.CenterPx(F) > C(15)));
        // ліфт везе Вогника нагору; Крапля стрибає через лаву
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.W.LiftY[0] == 8 * 640), b.Go(F, C(20))),
            VohnykBot.Seq(b.RunJump(Wt, +1, C(12) + 8, 30, C(16)), b.WaitFor(() => b.CenterPx(F) >= C(20) - 4)));
        // Крапля: праворуч крізь важіль (нічого), ліворуч — ліфт униз, праворуч — нагору з нею
        b.Do(null, VohnykBot.Seq(b.Go(Wt, C(18) + 10), b.Go(Wt, C(16)), b.WaitFor(() => b.W.LiftY[0] == 13 * 640),
            b.Go(Wt, C(18) + 10), b.WaitFor(() => b.W.LiftY[0] == 8 * 640), b.Go(Wt, C(22))));
    }
}
