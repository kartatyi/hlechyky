namespace Hlechyky.Games.Impl;

/// <summary>
/// Колонний рейс одного столу (specs/fura.md, «Колонне замовлення»): спільне замовлення <c>id: "k"</c>, учасники в
/// порядку приєднання, кожному свій бокс відправника, хто й коли здав. Живе в столі (не в фурі): стіл зник — колона теж,
/// а фура довозить колонне як своє, без бонусу.
/// </summary>
public sealed class FuraConvoy
{
    /// <summary>Вікно бонусу від першої здачі; тоді ж колона й закінчується.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    /// <summary>Бонус кожному, хто здав у вікні, коли здав хоч другий.</summary>
    public const double BonusShare = 0.15;

    public int No { get; set; }
    /// <summary>Замовлення колони; <see cref="FuraOrder.Dock"/> — з якого боксу почати коло.</summary>
    public FuraOrder Order { get; set; } = null!;
    public List<FuraMember> Members { get; set; } = [];
    /// <summary>Перша здача — від неї вікно бонусу. Поки null, до колони можна приєднатися.</summary>
    public DateTimeOffset? FirstAt { get; set; }
    /// <summary>Скільки разів приєднувались (від цього — наступний бокс по колу).</summary>
    public int Joins { get; set; }

    public FuraMember? Member(string nickKey) => Members.FirstOrDefault(m => !m.Left && m.NickKey == nickKey);

    /// <summary>Хто ще в колоні (не вийшов) — здані теж: вони тримають свій бокс до кінця колони.</summary>
    public IEnumerable<FuraMember> In => Members.Where(m => !m.Left);

    /// <summary>Хто ще везе: не вийшов і не здав.</summary>
    public int Active => Members.Count(m => !m.Left && m.DoneAt is null);

    /// <summary>
    /// Вільний бокс для наступного: по колу боксів відправника від <see cref="FuraOrder.Dock"/>, зсув — скільки разів
    /// приєднувались; зайнятий кимось, хто ще в колоні, — пропускаємо. null — усі зайняті («Колона повна»).
    /// </summary>
    public string? NextDock(FuraPlace from)
    {
        var docks = from.Docks;
        var used = In.Select(m => m.Dock).ToHashSet(StringComparer.Ordinal);
        if (used.Count >= docks.Length) return null;
        var start = Math.Max(0, Array.FindIndex(docks, d => d.Id == Order.Dock));
        for (var i = 0; i < docks.Length; i++)
        {
            var d = docks[(start + Joins + i) % docks.Length];
            if (!used.Contains(d.Id)) return d.Id;
        }
        return null;
    }
}

/// <summary>Учасник колони. <see cref="AwaySince"/> — встав з-за столу, ще не здавши: за хвилину випадає з колони.</summary>
public sealed class FuraMember
{
    public string NickKey { get; set; } = "";
    public string Nick { get; set; } = "";
    public string Dock { get; set; } = "";
    /// <summary>Вийшов (cancel) або довго не повертався за стіл.</summary>
    public bool Left { get; set; }
    public DateTimeOffset? AwaySince { get; set; }
    public DateTimeOffset? DoneAt { get; set; }
    /// <summary>Його номер рейсу й база плати за здане колонне — з них бонус.</summary>
    public int Trip { get; set; }
    public int Base { get; set; }
    public bool Bonused { get; set; }
}
