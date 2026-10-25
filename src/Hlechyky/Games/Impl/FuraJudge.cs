namespace Hlechyky.Games.Impl;

/// <summary>
/// Знімок телеметрії з браузера (<c>Input pose</c>): тягач (X, Z), швидкість м/с зі знаком (V), зчеплено (C), ціле % (K),
/// центр причепа (Rx, Rz) і його курс (Ry, 0 = +Z, π/2 = +X), з етапу 2 — курс тягача (H) і прапорці (F: 1 сигнал, 2 фари,
/// 4 стоп-сигнал) для привидів. Час знімка ставить сервер (<see cref="IRoomContext.Clock"/>).
/// </summary>
public sealed record FuraPose(double X, double Z, double V, bool C, int K, double Rx, double Rz, double Ry, double H = 0, int F = 0);

/// <summary>
/// Суддя рейсу «Чумаків» (docs/games/specs/fura.md, «Суддя»): з потоку поз рахує годинник рейсу без пауз, проїдене в
/// haul, найменше ціле, порушення швидкості й причину, з якої рейс недійсний. Окремий клас без <see cref="IRoomContext"/> —
/// щоб кожне правило тестувалось напряму. Стан — публічні властивості: гра кладе суддю в Save як є.
/// </summary>
public sealed class FuraJudge
{
    // ---------- константи (з контракту, не з конфігу) ----------

    /// <summary>Коротший проміжок між знімками швидкість не міряє: знімок лишається «на потім».</summary>
    public const double MinDtSec = 0.15;
    /// <summary>Понад це — порушення.</summary>
    public const double WarnKmh = 160;
    /// <summary>Одне таке — і рейс недійсний (телепорт).</summary>
    public const double VoidKmh = 250;
    /// <summary>Скільки порушень ще можна; наступне робить рейс недійсним.</summary>
    public const int MaxViolations = 3;
    /// <summary>Стрибок пози одразу після «поставити на колеса», якого не рахуємо.</summary>
    public const double RescueSlackM = 3;
    /// <summary>Довший проміжок — пауза: час не йде в годинник рейсу.</summary>
    public const double PauseSec = 15;
    /// <summary>Після паузи тягач має стояти не далі за стільки від останньої пози.</summary>
    public const double PauseReturnM = 30;
    /// <summary>Швидше за це в середньому рейс не проїхати.</summary>
    public const double MaxAvgKmh = 100;
    /// <summary>Тягач має проїхати в haul хоча б таку частку кілометрів дороги.</summary>
    public const double MinDriven = 0.9;

    // ---------- стан ----------

    /// <summary>Від чого міряємо: остання поза, на якій суддя «поставив крапку» (між короткими знімками вона стоїть).</summary>
    public FuraPose? Ref { get; set; }
    public DateTimeOffset RefAt { get; set; }
    /// <summary>Від якої миті рахується годинник рейсу (узяв замовлення). Час до неї не йде.</summary>
    public DateTimeOffset Since { get; set; }
    /// <summary>Годинник рейсу без пауз, секунди (від take).</summary>
    public double TripSec { get; set; }
    /// <summary>Причіп зчеплено у відправника: далі йдуть HaulSec, HaulM і MinK.</summary>
    public bool Hauling { get; set; }
    public double HaulSec { get; set; }
    /// <summary>Скільки проїхав тягач у haul (сума відстаней між знімками), метри.</summary>
    public double HaulM { get; set; }
    /// <summary>Найменше ціле за haul — k лише спадає, хоч би що слав браузер далі.</summary>
    public int MinK { get; set; } = 100;
    public int Violations { get; set; }
    /// <summary>Чим рейс недійсний (людською мовою); null — поки все чесно.</summary>
    public string? Void { get; set; }
    /// <summary>Попросили «поставити на колеса»: наступний стрибок до <see cref="RescueSlackM"/> не рахуємо.</summary>
    public bool Rescue { get; set; }

    /// <summary>Новий рейс від миті <paramref name="now"/>. Свіжа поза (не старша за паузу) — точка відліку, інакше відлік із першого знімка.</summary>
    public static FuraJudge Begin(DateTimeOffset now, FuraPose? last, DateTimeOffset lastAt)
    {
        var fresh = last is not null && (now - lastAt).TotalSeconds <= PauseSec;
        return new FuraJudge { Since = now, Ref = fresh ? last : null, RefAt = fresh ? lastAt : default };
    }

    /// <summary>Причіп зчеплено: від цієї пози рахуємо haul.</summary>
    public void StartHaul(FuraPose p)
    {
        Hauling = true;
        MinK = Math.Min(MinK, Math.Clamp(p.K, 0, 100));
    }

    /// <summary>
    /// Ще один знімок. true — змінилось те, що людина бачить (порушення, недійсність, ціле): тоді гра шле вид.
    /// </summary>
    public bool Feed(FuraPose p, DateTimeOffset at)
    {
        var before = (Violations, Void, MinK);
        if (Hauling) MinK = Math.Min(MinK, Math.Clamp(p.K, 0, 100));
        if (Ref is null)
        {
            Ref = p;
            RefAt = at;
            return before != (Violations, Void, MinK);
        }
        var dt = (at - RefAt).TotalSeconds;
        if (dt < MinDtSec) return before != (Violations, Void, MinK);   // точка відліку стоїть, дочекаємось довшого проміжку

        var d = Dist(Ref.X, Ref.Z, p.X, p.Z);
        if (dt > PauseSec)
        {
            // пауза (вкладка спала, сервер перезапускався): час не йде, а тягач мусить стояти там, де був
            if (d > PauseReturnM && Void is null)
                Void = $"Рейс недійсний: після паузи тягач опинився за {Math.Round(d):0} м від місця, де стояв";
        }
        else
        {
            var counted = Math.Max(0, (at - (RefAt > Since ? RefAt : Since)).TotalSeconds);
            TripSec += counted;
            if (Hauling) { HaulSec += counted; HaulM += d; }
            var jump = Rescue ? Math.Max(0, d - RescueSlackM) : d;
            var kmh = jump / dt * 3.6;
            if (Void is null)
            {
                if (kmh > VoidKmh)
                    Void = $"Рейс недійсний: тягач перескочив зі швидкістю {Math.Round(kmh):0} км/год — так фури не їздять";
                else if (kmh > WarnKmh && ++Violations > MaxViolations)
                    Void = $"Рейс недійсний: понад {WarnKmh:0} км/год уже {Violations} рази — фура так не літає";
            }
        }
        Rescue = false;
        Ref = p;
        RefAt = at;
        return before != (Violations, Void, MinK);
    }

    /// <summary>
    /// Чи сам рейс чесний на мить здачі (<paramref name="km"/> — шлях дорогами): null — так, інакше причина недійсності.
    /// Умови «тут і зараз» (зупинився, відчепив, у дворі) — <see cref="NotYet"/>.
    /// </summary>
    public string? Verdict(double km)
    {
        if (Void is not null) return Void;
        if (HaulSec < km / MaxAvgKmh * 3600)
            return "Рейс недійсний: так швидко цей шлях не проїхати";
        if (HaulM < MinDriven * km * 1000)
            return "Рейс недійсний: тягач проїхав менше, ніж є дороги до отримувача";
        return null;
    }

    // ---------- зони й здача ----------

    /// <summary>Свіжість останнього знімка на мить здачі.</summary>
    public const double FreshSec = 2;
    /// <summary>Швидше за це — ще їде.</summary>
    public const double StopMs = 0.5;
    /// <summary>На скільки розширено двір отримувача.</summary>
    public const double YardMarginM = 15;
    /// <summary>Або причіп настільки близько до будь-якого боксу отримувача.</summary>
    public const double DockNearM = 25;
    /// <summary>Підбір: причіп не далі за стільки від центру боксу відправника.</summary>
    public const double PickupM = 12;
    /// <summary>«Автозчеп» — тягач не далі за стільки від боксу.</summary>
    public const double AutoM = 30;
    /// <summary>«Точно в бокс»: центр причепа і курс.</summary>
    public const double ExactM = 2;
    public const double ExactYaw = 0.25;

    /// <summary>Причіп зчеплено біля боксу відправника — рейс пішов.</summary>
    public static bool PickedUp(FuraPose p, FuraDock dock) => p.C && Dist(p.Rx, p.Rz, dock.X, dock.Z) <= PickupM;

    /// <summary>Тягач досить близько до боксу, щоб попросити автозчеп.</summary>
    public static bool NearForAuto(FuraPose p, FuraDock dock) => Dist(p.X, p.Z, dock.X, dock.Z) <= AutoM;

    /// <summary>Чого бракує, щоб здати тут і зараз; null — можна здавати.</summary>
    public static string? NotYet(FuraPose? last, DateTimeOffset lastAt, DateTimeOffset now, FuraPlace to)
    {
        if (last is null || (now - lastAt).TotalSeconds > FreshSec) return "Нема свіжої телеметрії — постій секунду й спробуй ще";
        if (Math.Abs(last.V) >= StopMs) return "Спершу зупинись";
        if (last.C) return "Спершу відчепи причіп";
        if (!AtPlace(last, to)) return $"Причіп не в дворі: {to.Short}";
        return null;
    }

    /// <summary>Причіп у дворі (розширеному на <see cref="YardMarginM"/>) або біля будь-якого боксу місця.</summary>
    public static bool AtPlace(FuraPose p, FuraPlace place) =>
        place.Yard.Contains(p.Rx, p.Rz, YardMarginM) || place.Docks.Any(d => Dist(p.Rx, p.Rz, d.X, d.Z) <= DockNearM);

    /// <summary>Причіп рівно в якомусь боксі місця: центр ≤ 2 м і курс ≤ 0,25 рад.</summary>
    public static bool Exact(FuraPose p, FuraPlace place) =>
        place.Docks.Any(d => Dist(p.Rx, p.Rz, d.X, d.Z) <= ExactM && Math.Abs(Angle(p.Ry - d.Yaw)) <= ExactYaw);

    // ---------- плата ----------

    public const int PayMin = 15;
    public const int PayMax = 50;
    /// <summary>Нижче цього цілого — «усе розбито», плати нема.</summary>
    public const int BrokenK = 20;

    /// <summary>
    /// База плати (до × Boost): (8 + 9·км) × mult × k/100; × 1,1 вчасно; × 1,1 точно в бокс; × 0,9 автозчеп; округлити й
    /// обмежити 15…50. k &lt; 20 — 0.
    /// </summary>
    public static int Pay(double km, double mult, int k, bool onTime, bool exact, bool auto)
    {
        if (k < BrokenK) return 0;
        var b = (8 + 9 * km) * mult * k / 100.0;
        if (onTime) b *= 1.1;
        if (exact) b *= 1.1;
        if (auto) b *= 0.9;
        return Math.Clamp((int)Math.Round(b, MidpointRounding.AwayFromZero), PayMin, PayMax);
    }

    /// <summary>Скільки хвилин дає замовлення: ⌈км / 50 км/год × 60 × 1,5⌉ + 2.</summary>
    public static int Minutes(double km) => (int)Math.Ceiling(Math.Round(km / 50 * 60 * 1.5, 6)) + 2;

    public static double Dist(double x1, double z1, double x2, double z2) => Math.Sqrt((x2 - x1) * (x2 - x1) + (z2 - z1) * (z2 - z1));

    /// <summary>Кут у межах [−π, π].</summary>
    public static double Angle(double a)
    {
        a %= 2 * Math.PI;
        if (a > Math.PI) a -= 2 * Math.PI;
        if (a < -Math.PI) a += 2 * Math.PI;
        return a;
    }
}
