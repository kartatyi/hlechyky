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
            case 7: Level7(b); break;
            case 8: Level8(b); break;
            case 9: Level9(b); break;
            case 10: Level10(b); break;
            case 11: Level11(b); break;
            case 12: Level12(b); break;
            case 13: Level13(b); break;
            case 14: Level14(b); break;
            case 15: Level15(b); break;
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

    /// <summary>
    /// Проходження сам за двох: у кожен момент рухається лише один герой (b.Only), світ — у режимі соло. null — для
    /// рівня соло-плану нема (там і так нічого не треба робити двома руками одночасно).
    /// </summary>
    public static VohnykBot? PlaySolo(VohnykLevel level)
    {
        var b = new VohnykBot(level, solo: true);
        switch (level.N)
        {
            case 4: Level4Solo(b); break;
            default: return null;
        }
        b.Do(b.WaitFor(() => b.W.Cleared != 0), null);
        return b;
    }

    /// <summary>
    /// «Дві криниці» сам за двох: брами dF/dW — «все разом» на b1+b2. Удвох заходять на рахунок «три»; одному досить
    /// того, що кнопка брами тримається ще 2 с: Вогник зійшов із b1 у свою браму, Tab — і Крапля встигає у свою.
    /// </summary>
    static void Level4Solo(VohnykBot b)
    {
        b.Only(F, VohnykBot.Seq(b.RunJump(F, +1, 100, 30, C(5)), b.Go(F, C(5)), b.Jump(F, +1, 30, C(6)), b.Go(F, C(6)), b.Jump(F, +1, 30, C(8)), b.Go(F, C(9))));
        b.Only(Wt, VohnykBot.Seq(b.RunJump(Wt, -1, C(27), 30, C(24)), b.Go(Wt, C(24)), b.Jump(Wt, -1, 30, C(23)), b.Go(Wt, C(23)), b.Jump(Wt, -1, 30, C(21)), b.Go(Wt, C(20))));
        b.Only(F, VohnykBot.Seq(b.WaitFor(() => b.W.DoorO[0] >= 2 * T), b.Go(F, C(11))));
        b.Only(Wt, b.Go(Wt, C(14)));
        // Вогник сходинками на b3 — двері dX відчиняються; Крапля слідом і крізь них на b4
        b.Only(F, VohnykBot.Seq(b.Jump(F, +1, 30, C(13) - 6), b.Go(F, C(13) - 6), b.Jump(F, +1, 30, C(16)), b.Go(F, C(16)), b.Jump(F, +1, 30, C(18)), b.Go(F, C(18))));
        b.Only(Wt, VohnykBot.Seq(b.Jump(Wt, -1, 30, C(13) - 6), b.Go(Wt, C(13) - 6), b.Jump(Wt, +1, 30, C(16)), b.Go(Wt, C(16)),
            b.Jump(Wt, +1, 30, C(17)), b.WaitFor(() => b.W.DoorO[2] >= 2 * T), b.Go(Wt, C(20))));
        // Крапля тримає b4 — Вогник крізь dX до своїх дверей, потім Крапля до своїх
        b.Only(F, b.Go(F, C(25)));
        b.Only(Wt, b.Go(Wt, C(27)));
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

    static void Level7(VohnykBot b)
    {
        // Крапля стрибком через лаву — на ліфт; Вогник лавою (самоцвіт), повз ліфт, крізь важіль: вона їде, йому двері
        b.Do(null, VohnykBot.Seq(b.Jump(Wt, +1, 30, C(5) + 20), b.Go(Wt, C(5) + 20)));
        b.Do(VohnykBot.Seq(b.Go(F, C(9)), b.Go(F, C(16)), b.Jump(F, +1, 30, C(18)), b.Go(F, C(18))),
            VohnykBot.Seq(b.WaitFor(() => b.W.LiftY[0] == 6 * T), b.Go(Wt, C(9)), b.Jump(Wt, 0, 30), b.Go(Wt, C(11)), b.Go(Wt, C(17))));
        // Вогник на кнопці — двері нагорі відчинені; Крапля проходить і стрибає по самоцвіт
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.CenterPx(Wt) >= C(19) + 4), b.Jump(F, +1, 30, C(20)), b.Go(F, C(20)), b.Jump(F, +1, 30, C(21)), b.Go(F, C(21)),
                b.Jump(F, +1, 30, C(24)), b.Go(F, C(24)), b.Jump(F, +1, 30, C(25) + 8), b.Go(F, C(26)), b.Jump(F, 0, 30), b.Go(F, C(25))),
            VohnykBot.Seq(b.WaitFor(() => b.W.DoorO[1] >= 4 * T), b.Go(Wt, C(19)), b.Jump(Wt, 0, 30), b.Go(Wt, C(19) + 8), b.WaitFor(() => b.CenterPx(F) >= C(20)),
                b.Go(Wt, C(22)), b.Jump(Wt, +1, 30, C(23) + 8), b.Go(Wt, C(24) - 4), b.WaitFor(() => b.CenterPx(F) >= C(25)),
                b.Jump(Wt, +1, 30, C(26)), b.Go(Wt, C(26))));
    }

    static void Level8(VohnykBot b)
    {
        // двері: 0 U1, 1 L1, 2 U2, 3 L2, 4 U3, 5 L3. Вогник на bL0 — верхні U1 відчинені; Крапля через лаву й крізь U1 на bU1
        b.Do(b.Go(F, C(4)),
            VohnykBot.Seq(b.RunJump(Wt, +1, C(2), 30, C(5)), b.Go(Wt, C(6)), b.WaitFor(() => b.W.DoorO[0] >= 2 * T), b.Go(Wt, C(9))));
        // Крапля тримає bU1 — Вогник через калюжу (самоцвіт) і крізь L1 на bL1
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.W.DoorO[1] >= 2 * T), b.RunJump(F, +1, C(6), 30, C(9)), b.Go(F, C(13))), null);
        // Вогник тримає bL1 — Крапля через лаву (самоцвіт) і крізь U2 на bU2
        b.Do(null, VohnykBot.Seq(b.RunJump(Wt, +1, C(11), 30, C(14)), b.Go(Wt, C(15)), b.WaitFor(() => b.W.DoorO[2] >= 2 * T), b.Go(Wt, C(18))));
        // Крапля тримає bU2 — Вогник через калюжу (самоцвіт) і крізь L2 на bL2
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.W.DoorO[3] >= 2 * T), b.RunJump(F, +1, C(14), 30, C(17)), b.Go(F, C(22))), null);
        // Крапля ставить скриню на bU3 — нижні L3 відчинені назавжди; самоцвіт у стрибку; крізь U3 (Вогник тримає bL2) — до виходу
        b.Do(null, VohnykBot.Seq(b.Go(Wt, C(19)), b.RunUntil(+1, () => b.W.BoxX[0] >= 22 * T), b.Go(Wt, C(21)), b.Jump(Wt, 0, 30),
            b.Jump(Wt, +1, 30, C(23)), b.WaitFor(() => b.W.DoorO[4] >= 2 * T), b.Go(Wt, C(27))));
        b.Do(VohnykBot.Seq(b.Jump(F, +1, 30, C(24)), b.Go(F, C(24)), b.Go(F, C(27))), null);
    }

    static void Level9(VohnykBot b)
    {
        // ліфти: 0 пором, 1 підйомник. Вогник униз у рів і лавою на підйомник; Крапля стає на bV — підйомник везе його до стелі
        b.Do(VohnykBot.Seq(b.Go(F, C(10) + 20)),
            VohnykBot.Seq(b.WaitFor(() => b.CenterPx(F) >= C(10) && b.FeetPx(F) == 600 && b.W.Vx[F] == 0), b.Go(Wt, C(3))));
        // нагорі: полиці з самоцвітами ліворуч і праворуч, потім зістрибнути на правий берег
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.W.LiftY[1] == 6 * T), b.Go(F, C(8)), b.Go(F, C(7)), b.Go(F, C(10) + 20), b.Go(F, C(14)), b.Go(F, C(15) + 10)), null);
        // Крапля — на пором; Вогник — на поміст, на кнопку bF: пором везе її через ріку
        b.Do(VohnykBot.Seq(b.Go(F, C(20)), b.Jump(F, +1, 30, C(21)), b.Go(F, C(21)), b.Jump(F, -1, 30, C(18)), b.Go(F, C(17))),
            VohnykBot.Seq(b.Go(Wt, C(5) + 20), b.WaitFor(() => b.W.LiftX[0] == 15 * T), b.Go(Wt, C(20)), b.Jump(Wt, +1, 30, C(21)), b.Go(Wt, C(22)), b.Go(Wt, C(23)), b.Go(Wt, C(24)),
                b.Go(Wt, C(23)), b.Jump(Wt, -1, 30, C(22)), b.Go(Wt, C(21)), b.Jump(Wt, -1, 30, C(18)), b.Go(Wt, C(18))));
        // обоє з помосту на сходинку й стрибком на поміст виходів
        b.Do(VohnykBot.Seq(b.Jump(F, +1, 30, C(21)), b.Go(F, C(21)), b.RunJump(F, +1, C(22) + 8, 30, C(26)), b.Go(F, C(27))),
            VohnykBot.Seq(b.WaitFor(() => b.CenterPx(F) >= C(22)), b.Jump(Wt, +1, 30, C(21)), b.Go(Wt, C(21)), b.RunJump(Wt, +1, C(22) + 8, 30, C(26)), b.Go(Wt, C(26)), b.Go(Wt, C(28))));
    }

    static void Level10(VohnykBot b)
    {
        // ліфти: 0 підйомник, 1 над озером. Вогник — стрибок по самоцвіт і на підйомник; Крапля озером крізь lA
        b.Do(VohnykBot.Seq(b.Jump(F, 0, 30), b.Go(F, C(6) + 20)),
            VohnykBot.Seq(b.WaitFor(() => b.CenterPx(F) >= C(6) && b.Ground(F) && b.W.Vx[F] == 0), b.Go(Wt, C(10))));
        // нагорі: полиця з двома самоцвітами, назад на підйомник і на ліфт над озером
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.W.LiftY[0] == 7 * T), b.Go(F, C(4)), b.Go(F, C(3)), b.Go(F, C(8) + 20)),
            VohnykBot.Seq(b.WaitFor(() => b.CenterPx(F) >= C(8) && b.FeetPx(F) == 280 && b.W.Vx[F] == 0), b.Go(Wt, C(14))));
        // Крапля пройшла крізь lB — ліфт везе Вогника над озером; він стрибає на терасу, вона — водоспадом угору
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.W.LiftX[1] == 15 * T), b.RunJump(F, +1, C(16) + 8, 30, C(20)), b.Go(F, C(23)), b.Go(F, C(26))),
            VohnykBot.Seq(b.WaitFor(() => b.W.LiftX[1] == 15 * T), b.Go(Wt, C(15)), b.Jump(Wt, +1, 30, C(16)), b.Go(Wt, C(16)), b.Jump(Wt, +1, 30, C(17)), b.Go(Wt, C(17)),
                b.Jump(Wt, +1, 30, C(18)), b.Go(Wt, C(18)), b.Go(Wt, C(21)), b.Go(Wt, C(27))));
    }

    static void Level11(VohnykBot b)
    {
        // скрині: 0 — та, що їде ліфтом нагору; 1 — нижня. Вогник стрибком через скриню (самоцвіт) і лавовими сходами вгору
        b.Do(VohnykBot.Seq(b.Go(F, C(4)), b.Jump(F, +1, 30, C(7)), b.Go(F, C(11)), b.Jump(F, +1, 30, C(12)), b.Go(F, C(12)), b.Jump(F, +1, 30, C(13)), b.Go(F, C(13))),
            // Крапля заштовхує скриню на ліфт і сама стає поруч
            VohnykBot.Seq(b.WaitFor(() => b.CenterPx(F) >= C(9)), b.Go(Wt, C(4)), b.RunUntil(+1, () => b.W.BoxX[0] >= 7 * T), b.Go(Wt, C(6)),
                b.Jump(Wt, +1, 30, C(8) - 2), b.Go(Wt, C(8) - 2)));
        // Вогник стрибає в клітинку важеля нагорі й виходить праворуч — ліфт везе Краплю зі скринею; сам — у отвір униз, до нижньої скрині
        b.Do(VohnykBot.Seq(b.Jump(F, +1, 30, C(14)), b.Go(F, C(14)), b.Go(F, C(17)), b.RunUntil(+1, () => b.W.BoxX[1] >= 21 * T), b.Go(F, C(18)), b.Jump(F, 0, 30), b.Go(F, C(20))),
            // нагорі Крапля штовхає скриню ліворуч на кнопку b2, стрибає по самоцвіт, калюжею по два — і через отвори вниз
            VohnykBot.Seq(b.WaitFor(() => b.W.LiftY[0] == 9 * T), b.RunUntil(-1, () => b.W.BoxX[0] <= 4 * T), b.Go(Wt, C(5)), b.Jump(Wt, 0, 30),
                b.Go(Wt, C(11)), b.RunJump(Wt, +1, C(11) + 8, 30, C(15)), b.Go(Wt, C(15)), b.Go(Wt, C(17)), b.Go(Wt, C(19))));
        // брама all відчинена: обоє по скрині крізь браму — до своїх дверей
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.W.DoorO[0] >= 2 * T), b.Jump(F, +1, 30, C(21)), b.Go(F, C(23)), b.Jump(F, +1, 30, C(25)), b.Go(F, C(25))),
            VohnykBot.Seq(b.WaitFor(() => b.W.DoorO[0] >= 2 * T && b.CenterPx(F) >= C(22)), b.Jump(Wt, +1, 30, C(21)), b.Go(Wt, C(24)), b.Go(Wt, C(27))));
    }

    static void Level12(VohnykBot b)
    {
        // ліфти: 0 L1, 1 R1, 2 M (центральна шахта), 3 T. Поверх 1: Вогник на L1, Крапля на кнопці — він їде на другий
        b.Do(b.Go(F, C(2) + 10), VohnykBot.Seq(b.WaitFor(() => b.CenterPx(F) <= C(2) + 12 && b.W.Vx[F] == 0), b.Go(Wt, C(6))));
        // Вогник другим поверхом: лавою (самоцвіт), через калюжу — і крізь важіль l1, коли Крапля вже на R1
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.W.LiftY[0] == 11 * T), b.Go(F, C(6)), b.Go(F, C(16)), b.Jump(F, +1, 30, C(19)), b.Go(F, C(20)),
                b.WaitFor(() => b.CenterPx(Wt) >= C(26) + 10 && b.W.Vx[Wt] == 0 && b.FeetPx(Wt) == 600), b.Go(F, C(23))),
            VohnykBot.Seq(b.WaitFor(() => b.W.LiftY[0] == 11 * T && b.CenterPx(F) >= C(5)), b.RunJump(Wt, +1, C(8) + 4, 30, C(11)), b.Go(Wt, C(18)), b.Go(Wt, C(26) + 20)));
        // R1 привіз Краплю: вона водою (самоцвіт) до кнопки bm; Вогник через калюжу на ліфт M — угору на третій
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.W.LiftY[1] == 11 * T && b.CenterPx(Wt) <= C(24)), b.RunJump(F, -1, C(19) - 4, 30, C(16)), b.Go(F, C(14) + 20)),
            VohnykBot.Seq(b.WaitFor(() => b.W.LiftY[1] == 11 * T), b.Go(Wt, C(17)),
                b.WaitFor(() => b.CenterPx(F) <= C(15) && b.CenterPx(F) >= C(14) && b.W.Vx[F] == 0 && b.Ground(F)), b.Go(Wt, C(11))));
        // нагорі Вогник сходить праворуч; Крапля з кнопки — M по неї; вона на M — Вогник на bm2 — M везе її
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.W.LiftY[2] == 7 * T), b.Go(F, C(16)),
                b.WaitFor(() => b.W.LiftY[2] == 11 * T && b.CenterPx(Wt) >= C(14) && b.CenterPx(Wt) <= C(15) + 10 && b.W.Vx[Wt] == 0), b.Go(F, C(17))),
            VohnykBot.Seq(b.WaitFor(() => b.CenterPx(F) >= C(16) - 2), b.Go(Wt, C(12) + 10), b.WaitFor(() => b.W.LiftY[2] == 11 * T), b.Go(Wt, C(14) + 20)));
        // третій поверх: Крапля праворуч по самоцвіт; Вогник ліворуч (самоцвіт) крізь важіль lT на ліфт T — на четвертий
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.CenterPx(Wt) >= C(18)), b.Go(F, C(18)), b.RunJump(F, -1, C(16) - 4, 30, C(12)), b.Go(F, C(9)), b.Jump(F, 0, 30),
                b.Go(F, C(2) + 20), b.WaitFor(() => b.W.LiftY[3] == 3 * T), b.Go(F, C(12)), b.Go(F, C(20))),
            VohnykBot.Seq(b.WaitFor(() => b.W.LiftY[2] == 7 * T), b.Go(Wt, C(20)), b.Jump(Wt, 0, 30), b.WaitFor(() => b.W.LiftY[2] == 11 * T),
                b.RunJump(Wt, -1, C(16) - 4, 30, C(12)), b.WaitFor(() => b.W.LiftY[3] == 3 * T && b.FeetPx(F) == 120 && b.CenterPx(F) >= C(4)),
                b.Go(Wt, C(3)), b.Go(Wt, C(6)), b.WaitFor(() => b.W.LiftY[3] == 7 * T), b.Go(Wt, C(2) + 20), b.WaitFor(() => b.W.LiftY[3] == 3 * T),
                b.Go(Wt, C(8)), b.Go(Wt, C(22))));
    }

    static void Level13(VohnykBot b)
    {
        // двері: 0 dG, 1 dF. Вогник висячими лавовими приступками вгору; Крапля попід ними по три самоцвіти — на кнопку bG
        b.Do(VohnykBot.Seq(b.Go(F, C(2)), b.Jump(F, +1, 30, C(3)), b.Go(F, C(3)), b.Jump(F, +1, 30, C(4)), b.Go(F, C(4)),
                b.Jump(F, +1, 30, C(5)), b.Go(F, C(5)), b.Jump(F, +1, 30, C(6)), b.Go(F, C(6))),
            VohnykBot.Seq(b.WaitFor(() => b.FeetPx(F) <= 520), b.Go(Wt, C(4)), b.Jump(Wt, 0, 4), b.Go(Wt, C(6)), b.Go(Wt, C(8)), b.Go(Wt, C(9))));
        // двері dG відчинені: Вогник галереєю по лаві (п'ять самоцвітів), крізь важіль lK — і в отвір униз
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.W.DoorO[0] >= 2 * T), b.Go(F, C(9)), b.Go(F, C(12)), b.RunJump(F, +1, C(12) + 4, 30, C(15)), b.Go(F, C(16)),
                b.Go(F, C(17)), b.RunJump(F, +1, C(17) + 4, 30, C(21)), b.Go(F, C(21)), b.Go(F, C(24))),
            // Крапля відпускає bG, коли Вогник пройшов, штовхає скриню на bA, стрибає по самоцвіт і стає на bB
            VohnykBot.Seq(b.WaitFor(() => b.CenterPx(F) >= C(9)), b.Go(Wt, C(10)), b.RunUntil(+1, () => b.W.BoxX[0] >= 22 * T), b.Go(Wt, C(16)), b.Jump(Wt, 0, 30),
                b.Go(Wt, C(21)), b.Jump(Wt, +1, 30, C(22)), b.Go(Wt, C(24)), b.Go(Wt, C(25))));
        // три ключі на місці — брама all відчинена: Вогник проходить, Крапля зістрибує з кнопки й пірнає в браму, поки та не зачинилась
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.W.DoorO[1] >= 2 * T), b.Go(F, C(27))),
            VohnykBot.Seq(b.WaitFor(() => b.CenterPx(F) >= C(27) - 4), b.Go(Wt, C(28))));
    }

    static void Level14(VohnykBot b)
    {
        // двері: 0 TD1, 1 BD1 (inv), 2 TD2 (inv), 3 BD2, 4 A1, 5 A2
        // Крапля через лаву (самоцвіт) на кнопку b1: верхні TD1 відчинені, її власні BD1 — зачинені. Вогник через калюжу (самоцвіт) і крізь TD1
        b.Do(VohnykBot.Seq(b.RunJump(F, +1, C(3), 30, C(7)), b.Go(F, C(7)), b.WaitFor(() => b.W.DoorO[0] >= T), b.Go(F, C(11))),
            VohnykBot.Seq(b.RunJump(Wt, +1, C(2), 30, C(5)), b.Go(Wt, C(6))));
        // вона сходить з кнопки — її двері відчиняються; він через стовпчик, по самоцвіт і на кнопку b2 — її двері BD2 відчинені, його TD2 зачинені
        b.Do(VohnykBot.Seq(b.Jump(F, +1, 30, C(12)), b.Go(F, C(13)), b.Go(F, C(16)), b.Jump(F, 0, 30), b.Go(F, C(15))),
            VohnykBot.Seq(b.Go(Wt, C(7)), b.WaitFor(() => b.W.DoorO[1] >= T), b.Go(Wt, C(11)), b.Go(Wt, C(14)), b.Jump(Wt, +1, 30, C(15)), b.Go(Wt, C(16)), b.Go(Wt, C(18))));
        // Крапля крізь BD2, по самоцвіт — на кнопку b5 (комірчина Вогника); він з кнопки — крізь TD2, по самоцвіт, у комірчину й на кнопку b6
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.CenterPx(Wt) >= C(20)), b.Go(F, C(17)), b.WaitFor(() => b.W.DoorO[2] >= T), b.Go(F, C(21)), b.Jump(F, 0, 30),
                b.WaitFor(() => b.W.DoorO[4] >= T), b.Go(F, C(28)), b.Go(F, C(23))),
            VohnykBot.Seq(b.WaitFor(() => b.W.DoorO[3] >= T), b.Go(Wt, C(21)), b.Jump(Wt, 0, 30), b.Go(Wt, C(23)), b.WaitFor(() => b.CenterPx(F) >= C(26)),
                b.WaitFor(() => b.CenterPx(F) <= C(24)), b.WaitFor(() => b.W.DoorO[5] >= T), b.Go(Wt, C(28)), b.Go(Wt, C(22))));
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.CenterPx(Wt) <= C(22) + 4), b.Go(F, C(22))), null);
    }

    static void Level15(VohnykBot b)
    {
        // скрині: 0 — ліва (ліфтом на bX), 1 і 2 — праві (одна — місток у болоті, друга — на bY). Двері: 0 dR, 1 dE
        // Вогник: через скриню, самоцвіт у стрибку, назад через скриню — і ще один самоцвіт, чекає праворуч
        b.Do(VohnykBot.Seq(b.Go(F, C(10)), b.Jump(F, -1, 30, C(8)), b.Go(F, C(7)), b.Jump(F, 0, 30), b.Go(F, C(8)), b.Jump(F, +1, 30, C(12)),
                b.Go(F, C(12)), b.Jump(F, 0, 30), b.Go(F, C(12))),
            // Крапля: через скриню й горбик водою в куток (два самоцвіти), назад крізь важіль lR — праві двері dR відчинені;
            // потім заштовхує скриню на ліфт аж до горбика (стає рівно) і стає поруч
            VohnykBot.Seq(b.Go(Wt, C(10)), b.Jump(Wt, -1, 30, C(8)), b.Go(Wt, C(5)), b.Jump(Wt, -1, 30, C(3)), b.Go(Wt, C(1)), b.Go(Wt, C(3)),
                b.Jump(Wt, +1, 30, C(5)), b.Go(Wt, C(7)), b.WaitFor(() => b.CenterPx(F) >= C(12) - 4 && b.Ground(F)),
                b.Go(Wt, C(8)), b.Jump(Wt, +1, 30, C(10)), b.Go(Wt, C(10)), b.RunUntil(-1, () => b.W.BoxX[0] <= 5 * T), b.Go(Wt, C(6) - 2)));
        // Вогник на кнопку bL — ліфт везе Краплю зі скринею нагору; там скриня — на bX, два самоцвіти, і Крапля чекає біля калюжі
        b.Do(b.Go(F, C(11)),
            VohnykBot.Seq(b.WaitFor(() => b.W.LiftY[0] == 7 * T), b.RunUntil(-1, () => b.W.BoxX[0] <= 2 * T), b.Go(Wt, C(6)), b.Jump(Wt, 0, 30), b.Go(Wt, C(9))));
        // Вогник праворуч крізь dR: права скриня — у болото (місток), ліва — по містку на bY; самоцвіт; назад на ліфт
        b.Do(VohnykBot.Seq(b.Go(F, C(14)), b.Go(F, C(16)), b.Jump(F, +1, 30, C(18)), b.Go(F, C(18)), b.RunUntil(+1, () => b.W.BoxY[2] >= 15 * T),
                b.Go(F, C(19)), b.Jump(F, -1, 30, C(16)), b.Go(F, C(16)), b.RunUntil(+1, () => b.W.BoxX[1] >= 25 * T), b.Go(F, C(23)), b.Jump(F, 0, 30),
                b.Go(F, C(6) - 2)), null);
        // Крапля на bL2 нагорі — ліфт везе Вогника; обоє по самоцвіти — і крізь браму dE (обидві кнопки тримають скрині)
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.W.LiftY[0] == 7 * T), b.Go(F, C(4)), b.Jump(F, 0, 30), b.Go(F, C(7)), b.RunJump(F, +1, C(8) + 4, 30, C(11)), b.Go(F, C(14))),
            VohnykBot.Seq(b.Go(Wt, C(8)), b.WaitFor(() => b.W.LiftY[0] == 7 * T && b.CenterPx(F) <= C(4) + 4), b.WaitFor(() => b.CenterPx(F) >= C(8)), b.Go(Wt, C(16))));
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
