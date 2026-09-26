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
            case 3: Level3(b); break;
            case 4: Level4(b); break;
            case 5: Level5(b); break;
            case 6: Level6(b); break;
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

    const int T = 640;

    static void Level3(VohnykBot b)
    {
        // Вогник: стрибком через воду, лавою по самоцвіт, до скрині — і штовхає її під полицю
        b.Do(
            VohnykBot.Seq(b.RunJump(F, +1, 140, 30, 300), b.Go(F, C(9)), b.RunUntil(+1, () => b.W.BoxX[0] >= 12 * T),
                b.Go(F, C(11)), b.Jump(F, +1, 30, C(12)), b.Go(F, C(12)), b.Jump(F, +1, 30, C(13) + 10), b.Go(F, C(13))),
            // Крапля: водою по самоцвіт, стрибком через лаву — і чекає, доки Вогник звільнить скриню
            VohnykBot.Seq(b.Go(Wt, C(4)), b.RunJump(Wt, +1, 266, 30, C(10)), b.Go(Wt, C(10))));
        // Крапля — теж зі скрині на полицю, по свій самоцвіт
        b.Do(null, VohnykBot.Seq(b.Go(Wt, C(11)), b.Jump(Wt, +1, 30, C(12)), b.Go(Wt, C(12)), b.Jump(Wt, +1, 30, C(14)), b.Go(Wt, C(15))));
        // обоє вниз, ліворуч від скрині; Вогник штовхає її тунелем у болото — місток
        b.Do(VohnykBot.Seq(b.Go(F, C(10)), b.RunUntil(+1, () => b.W.BoxY[0] >= 13 * T), b.Go(F, C(21)), b.Jump(F, +1, 30, C(22)), b.Go(F, C(22))),
            VohnykBot.Seq(b.Go(Wt, C(9)), b.WaitFor(() => b.W.BoxY[0] >= 13 * T), b.Go(Wt, C(21) - 4)));
        b.Do(null, VohnykBot.Seq(b.WaitFor(() => b.CenterPx(F) >= C(22) - 4), b.Go(Wt, C(21)), b.Jump(Wt, +1, 30, C(23)), b.Go(Wt, C(24))));
    }

    static void Level4(VohnykBot b)
    {
        // кожен своєю криницею: стрибком над чужою рідиною по самоцвіт, на стовпчик по другий — і на кнопку біля брами
        b.Do(
            VohnykBot.Seq(b.RunJump(F, +1, 100, 30, C(5)), b.Go(F, C(5)), b.Jump(F, +1, 30, C(6)), b.Go(F, C(6)), b.Jump(F, +1, 30, C(8)), b.Go(F, C(9))),
            VohnykBot.Seq(b.RunJump(Wt, -1, C(27), 30, C(24)), b.Go(Wt, C(24)), b.Jump(Wt, -1, 30, C(23)), b.Go(Wt, C(23)), b.Jump(Wt, -1, 30, C(21)), b.Go(Wt, C(20))));
        // обидві брами «all» відчинились — заходять разом, на рахунок «три»
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.W.DoorO[0] >= 2 * T), b.Go(F, C(11))),
            VohnykBot.Seq(b.WaitFor(() => b.W.DoorO[1] >= 2 * T), b.Go(Wt, C(14))));
        // Вогник угору сходинками на кнопку b3 — двері dX відчиняються
        b.Do(VohnykBot.Seq(b.Jump(F, +1, 30, C(13) - 6), b.Go(F, C(13) - 6), b.Jump(F, +1, 30, C(16)), b.Go(F, C(16)), b.Jump(F, +1, 30, C(18)), b.Go(F, C(18))),
            VohnykBot.Seq(b.WaitFor(() => b.CenterPx(F) >= C(15)), b.Jump(Wt, -1, 30, C(13) - 6), b.Go(Wt, C(13) - 6), b.Jump(Wt, +1, 30, C(16)), b.Go(Wt, C(16)),
                b.Jump(Wt, +1, 30, C(17)), b.WaitFor(() => b.W.DoorO[2] >= 2 * T), b.Go(Wt, C(20))));
        // Крапля тримає b4 за дверима — Вогник проходить; далі обоє до своїх дверей
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.CenterPx(Wt) >= C(20) - 4), b.Go(F, C(25))),
            VohnykBot.Seq(b.WaitFor(() => b.CenterPx(F) >= C(21)), b.Go(Wt, C(27))));
    }

    static void Level5(VohnykBot b)
    {
        // Вогник лавою ліворуч, крізь важіль l1 туди й назад — на ліфт f1; Крапля водою праворуч — на ліфт f2 і чекає
        b.Do(
            VohnykBot.Seq(b.Go(F, C(4)), b.Go(F, C(6) + 10), b.WaitFor(() => b.W.LiftY[0] == 5 * T),
                // нагорі: на полицю по самоцвіт, униз на поверх, стрибком через шахту — і крізь важіль l2 (ліфт Краплі їде)
                b.Go(F, C(4)), b.Go(F, C(3)), b.WaitFor(() => b.Ground(Wt) && b.CenterPx(Wt) >= C(20)),
                b.RunJump(F, +1, C(5), 30, C(8)), b.Go(F, C(10)), b.Go(F, C(13))),
            VohnykBot.Seq(b.Go(Wt, C(20) + 10), b.WaitFor(() => b.W.LiftY[1] == 5 * T),
                b.Go(Wt, C(23)), b.Go(Wt, C(25)), b.RunJump(Wt, -1, C(22) + 5, 30, C(19)), b.Go(Wt, C(14))));
    }

    static void Level6(VohnykBot b)
    {
        // скриня стоїть на кнопці й тримає двері зачиненими — поки що вона сходинка на полицю з самоцвітами
        b.Do(VohnykBot.Seq(b.Go(F, C(3)), b.Jump(F, +1, 30, C(4)), b.Go(F, C(4)), b.Jump(F, -1, 30, C(1)), b.Go(F, C(1))), null);
        b.Do(null, VohnykBot.Seq(b.Go(Wt, C(3)), b.Jump(Wt, +1, 30, C(4)), b.Go(Wt, C(4)), b.Jump(Wt, -1, 30, C(2)), b.Go(Wt, C(2))));
        // униз; Вогник зіпхне скриню з кнопки — двері відчиняться — і тунелем у болото
        b.Do(VohnykBot.Seq(b.Go(F, C(3)), b.RunUntil(+1, () => b.W.BoxY[0] >= 14 * T), b.Go(F, C(10)), b.Jump(F, +1, 4, C(11)), b.Go(F, C(11)),
                b.Jump(F, +1, 30, C(14)), b.Go(F, C(15)), b.Go(F, C(17))),
            VohnykBot.Seq(b.Go(Wt, C(3) - 10), b.WaitFor(() => b.W.BoxY[0] >= 14 * T && b.CenterPx(F) >= C(12)), b.Go(Wt, C(10)), b.Jump(Wt, +1, 4, C(11)),
                b.Go(Wt, C(12)), b.Go(Wt, C(14)), b.Jump(Wt, +1, 30, C(17)), b.Go(Wt, C(17) - 8)));
        // друга скриня — сходинка на верхню полицю, потім у друге болото
        b.Do(VohnykBot.Seq(b.RunUntil(+1, () => b.W.BoxX[1] >= 19 * T), b.Go(F, C(18)), b.Jump(F, +1, 30, C(19)), b.Go(F, C(19)), b.Jump(F, +1, 30, C(20)), b.Go(F, C(20))), null);
        b.Do(null, VohnykBot.Seq(b.Go(Wt, C(18)), b.Jump(Wt, +1, 30, C(19)), b.Go(Wt, C(19)), b.Jump(Wt, +1, 30, C(21)), b.Go(Wt, C(21))));
        b.Do(VohnykBot.Seq(b.Go(F, C(17)), b.RunUntil(+1, () => b.W.BoxY[1] >= 13 * T), b.Go(F, C(22)), b.RunJump(F, +1, 973, 30, C(27)), b.Go(F, C(27))),
            VohnykBot.Seq(b.Go(Wt, C(17)), b.WaitFor(() => b.CenterPx(F) >= C(26)), b.Go(Wt, C(22)), b.RunJump(Wt, +1, 973, 30, C(28)), b.Go(Wt, C(28))));
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
