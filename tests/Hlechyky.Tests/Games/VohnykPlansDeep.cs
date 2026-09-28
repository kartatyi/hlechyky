namespace Hlechyky.Tests.Games;

/// <summary>
/// Друга печера «Глибше» (рівні 16+): як робот проходить кожен рівень. Той самий підхід, що й у першій печері, —
/// план автора рівня, з якого записано solution (VOHNYK_RECORD=1). Координати — центри героїв у px.
/// </summary>
public static partial class VohnykPlans
{
    /// <summary>План рівня другої печери; false — плану нема.</summary>
    static bool Deep(VohnykBot b, int n)
    {
        switch (n)
        {
            case 16: Level16(b); return true;
            case 17: Level17(b); return true;
            case 18: Level18(b); return true;
            case 19: Level19(b); return true;
            case 20: Level20(b); return true;
            case 21: Level21(b); return true;
            case 22: Level22(b); return true;
            case 23: Level23(b); return true;
            case 24: Level24(b); return true;
            default: return false;
        }
    }

    /// <summary>
    /// «Дощата драбина»: Крапля тримає b1 — двері нагорі відчинені; Вогник дошками вгору (крізь них — знизу) і стає
    /// на b2 за дверима; тепер Крапля тим самим шляхом, і обоє до виходів.
    /// </summary>
    static void Level16(VohnykBot b)
    {
        b.Do(
            VohnykBot.Seq(b.Go(F, C(8)), b.RunJump(F, +1, 415, 30, C(14)), b.Go(F, C(16)), b.Jump(F, 0, 30, C(16)),
                b.RunJump(F, +1, 700, 30, C(19)), b.RunJump(F, -1, 740, 30, C(16)), b.RunJump(F, +1, 700, 30, C(19)), b.Go(F, C(22))),
            b.Go(Wt, C(5)));
        b.Do(null, VohnykBot.Seq(b.RunJump(Wt, +1, 262, 30, C(10)), b.Go(Wt, C(16)), b.Jump(Wt, 0, 30, C(16)),
            b.RunJump(Wt, +1, 700, 30, C(19)), b.RunJump(Wt, -1, 740, 30, C(16)), b.RunJump(Wt, +1, 700, 30, C(19)), b.Go(Wt, C(26))));
        b.Do(b.Go(F, C(24)), null);
    }

    /// <summary>
    /// «Перший промінь»: Вогник стає в червоний промінь — Крапля проходить за його спиною; обоє на полицю, крізь дзеркало
    /// праворуч і назад ліворуч — світло б'є вгору в кришталь, двері відчинені; униз, і тепер Крапля прикриває Вогника
    /// в синьому промені.
    /// </summary>
    static void Level17(VohnykBot b)
    {
        b.Do(VohnykBot.Seq(b.Go(F, C(8)), b.Jump(F, 0, 30, C(8))), null);
        b.Do(null, b.Go(Wt, C(10)));
        b.Do(VohnykBot.Seq(b.Go(F, C(10)), b.Jump(F, +1, 30, C(11) + 10), b.Go(F, C(16)), b.Go(F, C(11))),
            VohnykBot.Seq(b.Go(Wt, C(9)), b.WaitFor(() => b.CenterPx(F) >= C(12)), b.Go(Wt, C(10)), b.Jump(Wt, +1, 30, C(11) + 10), b.Go(Wt, C(15)), b.Go(Wt, C(11))));
        b.Do(b.Go(F, C(10) - 8), b.WaitFor(() => b.W.Sensor[0] != 0));
        b.Do(b.Go(F, C(9)), b.Go(Wt, C(9) + 20));
        b.Do(b.Go(F, C(16)), VohnykBot.Seq(b.Go(Wt, C(18)), b.Jump(Wt, 0, 30, C(18))));
        b.Do(b.Go(F, C(24)), null);
        b.Do(null, b.Go(Wt, C(26)));
    }

    /// <summary>
    /// «Кротові нори»: самоцвіти на дошках; Крапля тримає b1 — портал горить, Вогник через воду в нього й на той бік,
    /// лавою до b2 (тепер портал тримає він); Крапля водою в портал, стрибком через лаву, на дошку — і обоє до виходів.
    /// </summary>
    static void Level18(VohnykBot b)
    {
        b.Do(VohnykBot.Seq(b.Jump(F, 0, 30, C(3)), b.Go(F, C(5))),
            VohnykBot.Seq(b.WaitFor(() => b.CenterPx(F) >= C(4)), b.Go(Wt, C(3)), b.Jump(Wt, 0, 30, C(3)), b.Jump(Wt, +1, 30, C(5)), b.Go(Wt, C(5)), b.Go(Wt, C(7)), b.Go(Wt, C(6))));
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.W.Button[0] != 0), b.RunJump(F, +1, 300, 30, C(10) + 4), b.WaitFor(() => b.Ground(F)), b.Go(F, C(19)), b.Jump(F, 0, 30, C(19)), b.Go(F, C(21))), null);
        b.Do(null, VohnykBot.Seq(b.WaitFor(() => b.W.Button[1] != 0), b.RunUntil(+1, () => b.W.Y[Wt] < 9 * T), b.WaitFor(() => b.Ground(Wt)), b.Go(Wt, C(17)),
            b.RunJump(Wt, +1, 690, 30, C(21)), b.Go(Wt, C(21)), b.Jump(Wt, 0, 30, C(21)), b.Go(Wt, C(25))));
        b.Do(b.Go(F, C(23)), null);
    }

    /// <summary>
    /// «Скриня-щит»: Вогник зіштовхує скриню в нору — вона падає в синій промінь коридору й кидає тінь праворуч; Крапля
    /// дошкою над лавою до b1 (двері відчинені), Вогник у тіні скрині до b2; Крапля назад, у коридор по самоцвіт біля
    /// ліхтаря (їй синій не страшний) — і обоє до виходів.
    /// </summary>
    static void Level19(VohnykBot b)
    {
        b.Do(VohnykBot.Seq(b.Go(F, C(6) + 12), b.Wait(40)), null);
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.W.Button[0] != 0), b.Go(F, C(21))),
            VohnykBot.Seq(b.RunJump(Wt, +1, C(6), 30, C(9)), b.Go(Wt, C(10)), b.Jump(Wt, +1, 30, C(12)), b.Go(Wt, C(12)),
                b.Jump(Wt, +1, 30, C(17)), b.Go(Wt, C(18))));
        b.Do(null, VohnykBot.Seq(b.WaitFor(() => b.W.Button[1] != 0), b.Go(Wt, C(16)), b.Jump(Wt, -1, 30, C(12)), b.Jump(Wt, -1, 30, C(9)),
            b.RunUntil(-1, () => b.FeetPx(Wt) > 400), b.WaitFor(() => b.Ground(Wt)), b.Go(Wt, C(3)), b.Go(Wt, C(4)), b.RunJump(Wt, +1, C(5), 30, C(10)), b.Go(Wt, C(25))));
        b.Do(b.Go(F, C(23)), null);
    }

    /// <summary>
    /// «Дзеркальна зала»: обидва дзеркала спершу кидають світло ліворуч, а кришталі праворуч. Крапля внизу проходить
    /// дзеркало туди (самоцвіти) й назад — світло пішло в кришталь, — і перестрибує дзеркало з кришталем. Вогник
    /// сходами на дошки, так само туди й назад, униз з лівого краю і теж стрибком до дверей.
    /// </summary>
    static void Level20(VohnykBot b)
    {
        b.Do(VohnykBot.Seq(b.Go(F, C(4)), b.Jump(F, -1, 30, C(3)), b.Jump(F, +1, 30, C(5) + 10), b.Go(F, C(20)), b.Go(F, C(12))),
            VohnykBot.Seq(b.Go(Wt, C(18)), b.Go(Wt, C(9)), b.Go(Wt, C(6)), b.RunJump(Wt, +1, C(9) - 8, 30, C(14))));
        b.Do(VohnykBot.Seq(b.Go(F, C(4)), b.RunJump(F, +1, C(9) - 8, 30, C(14)), b.Go(F, C(24))), b.Go(Wt, C(26)));
    }

    /// <summary>
    /// «Пороми на дошках»: Вогник стоїть на ліфті біля скрині; Крапля по самоцвіт і назад крізь важіль — ліфт везе
    /// обох нагору, — і в портал, поки він горить. Вогник дошками штовхає скриню на кнопку (портал гасне, двері
    /// відчиняються), з ліфта вниз ліворуч і через лаву до дверей.
    /// </summary>
    static void Level21(VohnykBot b)
    {
        b.Do(null, VohnykBot.Seq(b.Go(Wt, C(4)), b.Go(Wt, C(7)), b.RunUntil(+1, () => b.W.Y[Wt] < 6 * T), b.WaitFor(() => b.Ground(Wt)), b.Go(Wt, C(22))));
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.FeetPx(F) <= 8 * 40 && b.Ground(F)), b.Go(F, 428), b.Go(F, C(1)), b.WaitFor(() => b.Ground(F)), b.Go(F, C(20)), b.Go(F, C(24))),
            b.Go(Wt, C(25)));
    }

    /// <summary>
    /// «Сторожові промені»: два коридори, у кожному — чужі сторожі, а кнопки від них — у сусіда. Естафета: Крапля на
    /// b1 — Вогник проходить синього до b2 — Крапля проходить червоного крізь важіль (другий синій гасне назавжди) —
    /// Вогник до b3 — Крапля повз останнього червоного — і обоє до виходів.
    /// </summary>
    static void Level22(VohnykBot b)
    {
        b.Do(b.Go(F, C(4)), b.Go(Wt, C(5)));
        b.Do(b.Go(F, C(9)), null);
        b.Do(null, VohnykBot.Seq(b.RunJump(Wt, +1, C(6), 30, C(10)), b.Go(Wt, C(14)), b.RunJump(Wt, +1, C(14) + 4, 30, C(17))));
        b.Do(VohnykBot.Seq(b.RunJump(F, +1, C(10) + 5, 30, C(14)), b.Go(F, C(17))), null);
        b.Do(null, b.Go(Wt, C(25)));
        b.Do(VohnykBot.Seq(b.RunJump(F, +1, C(18) + 5, 30, C(22)), b.Go(F, C(25))), null);
    }

    /// <summary>
    /// «Навхрест»: Крапля знизу в лавовій половині, Вогник — у водяній; портал кожного запалює друг. Крапля стрибає
    /// на кнопку між калюжами — Вогник через воду в свій портал і додому, дорогою перекидає важіль (горить портал
    /// Краплі й відчиняються її двері); Крапля порталом у свою половину й на кнопку дверей Вогника — він до виходу, тоді й вона.
    /// </summary>
    static void Level23(VohnykBot b)
    {
        b.Do(VohnykBot.Seq(b.WaitFor(() => b.W.Button[0] != 0), b.RunJump(F, -1, C(24) - 5, 30, C(21)), b.RunJump(F, -1, C(20) - 5, 30, C(17)),
                b.RunUntil(-1, () => b.W.Y[F] < 8 * T), b.WaitFor(() => b.Ground(F)), b.Go(F, C(6))),
            VohnykBot.Seq(b.RunJump(Wt, +1, C(3) + 5, 30, C(6) + 8), b.WaitFor(() => b.W.Lever[0] == 0), b.Go(Wt, C(7)), b.RunJump(Wt, +1, C(7) + 5, 30, C(10)),
                b.RunUntil(+1, () => b.W.Y[Wt] < 8 * T), b.WaitFor(() => b.Ground(Wt)), b.Go(Wt, C(21)), b.Go(Wt, C(18))));
        b.Do(b.Go(F, C(2)), null);
        b.Do(null, b.Go(Wt, C(25)));
    }

    /// <summary>
    /// «Світловод»: Крапля проходить нижнє дзеркало ліворуч (світло піде праворуч) і перестрибує його назад. Вогник
    /// сходинками-дошками нагору, крізь червоний промінь штовхає скриню на кнопку: двері на шляху світла відчинені, а
    /// скриня кидає тінь на полицю. Тепер і Крапля по свій самоцвіт нагору; униз тими ж дошками, праворуч крізь друге
    /// дзеркало (світло пішло вгору, у прикручене, і в кришталь) — двері до виходів відчинені.
    /// </summary>
    static void Level24(VohnykBot b)
    {
        b.Do(null, VohnykBot.Seq(b.Go(Wt, C(2)), b.Go(Wt, C(1)), b.RunJump(Wt, +1, C(2) + 6, 30, C(4))));
        b.Do(Stairs24(b, F), null);
        b.Do(b.Go(F, 628), Stairs24(b, Wt));
        b.Do(Down24(b, F), b.Go(Wt, C(15)));
        b.Do(b.Go(F, C(24)), VohnykBot.Seq(Down24(b, Wt), b.Go(Wt, C(26))));
    }

    /// <summary>Від підлоги біля старту дошками на полицю (стає на її лівий край).</summary>
    static IEnumerable<int> Stairs24(VohnykBot b, int h) => VohnykBot.Seq(b.Go(h, C(4)), b.Jump(h, +1, 30, C(5) + 10), b.Jump(h, +1, 30, C(8) + 10),
        b.Go(h, C(9)), b.Jump(h, +1, 30, C(10) + 10), b.Go(h, C(11)), b.Jump(h, +1, 30, 530));

    /// <summary>З полиці дошками вниз і праворуч крізь друге дзеркало.</summary>
    static IEnumerable<int> Down24(VohnykBot b, int h) => VohnykBot.Seq(b.Go(h, C(13)), b.Jump(h, -1, 30, C(11)), b.Go(h, C(9)), b.Go(h, C(7)), b.Go(h, C(20)));
}
