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
}
