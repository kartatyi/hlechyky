namespace Hlechyky.Games.Impl;

/// <summary>
/// Бот-стрілець Дуелі, коли людина за столом сама. Стріляє тим самим пострілом, що й людина (<see cref="DuelBout.Shoot"/>),
/// і бачить те саме, що й вона: фазу «Цілься…»/«ВОГОНЬ!» та обманку — ні <c>fireAt</c>, ні довжини «Цілься…» він не знає.
/// <para>
/// Рівні — це людська рука: реакція (середня, розкид, нижня межа), інколи «задивився», інколи рука не витримала й
/// стрельнула в «Цілься…», інколи смикнувся на обманку. Сильний — як дуже швидка людина (≈230 мс), але не скрипт:
/// нижня межа 180 мс, а найшвидші люди на сайті б'ють і 170.
/// </para>
/// </summary>
public sealed class DuelBot
{
    /// <summary>Середня реакція, розкид (σ) і нижня межа, мс — легкий / звичайний / сильний.</summary>
    static readonly int[] MeanMs = [430, 305, 232], SdMs = [80, 45, 28], FloorMs = [260, 215, 180];
    /// <summary>«Задивився»: шанс і додаток до реакції (мс).</summary>
    static readonly double[] LateP = [0.12, 0.05, 0.02];
    static readonly int[] LateMinMs = [150, 100, 60], LateMaxMs = [400, 250, 150];
    /// <summary>Рука не витримала — фальстарт серед «Цілься…» (шанс на раунд).</summary>
    static readonly double[] FalseP = [0.10, 0.04, 0.015];
    /// <summary>Смикнувся на «ВОРОН!» (шанс на обманку).</summary>
    static readonly double[] BaitP = [0.35, 0.15, 0.05];

    int _lvl = 1;
    DuelPhase _seen = DuelPhase.Done;
    bool _baitSeen;
    DateTimeOffset? _shotAt;

    public void Reset(LiveBots.Level level)
    {
        _lvl = LiveBots.Index(level);
        _seen = DuelPhase.Done;
        _baitSeen = false;
        _shotAt = null;
    }

    /// <summary>Заплановано натиснути до <paramref name="now"/> — віддати мить натиску (і забути план).</summary>
    public DateTimeOffset? Due(DateTimeOffset now)
    {
        if (_shotAt is not { } at || at > now) return null;
        _shotAt = null;
        return at;
    }

    /// <summary>Подивитись на вулицю після тика поєдинку: що змінилось — те й планувати.</summary>
    public void See(DuelBout bout, DateTimeOffset now, Random rng)
    {
        var phase = bout.Phase;
        if (phase != _seen)
        {
            _seen = phase;
            switch (phase)
            {
                case DuelPhase.Aim:
                    _baitSeen = false;
                    // Коли саме не витримає рука, бот не знає, як і людина: десь за 0,9–3,2 с «Цілься…».
                    _shotAt = rng.NextDouble() < FalseP[_lvl] ? now.AddMilliseconds(900 + rng.Next(2300)) : null;
                    break;
                case DuelPhase.Fire:
                    // Фальстарт, що не встиг статись, уже не фальстарт — тепер просто реакція на сигнал.
                    _shotAt = now.AddMilliseconds(Reaction(rng));
                    break;
                default:
                    _shotAt = null;
                    break;
            }
        }
        if (phase == DuelPhase.Aim && bout.BaitShown && !_baitSeen)
        {
            _baitSeen = true;
            if (_shotAt is null && rng.NextDouble() < BaitP[_lvl]) _shotAt = now.AddMilliseconds(160 + rng.Next(200));
        }
    }

    /// <summary>Реакція за нормальним розподілом (Бокс — Мюллер) з нижньою межею й рідкісним «задивився».</summary>
    public int Reaction(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var g = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * rng.NextDouble());
        var ms = MeanMs[_lvl] + g * SdMs[_lvl];
        if (rng.NextDouble() < LateP[_lvl]) ms += LateMinMs[_lvl] + rng.Next(LateMaxMs[_lvl] - LateMinMs[_lvl]);
        return (int)Math.Clamp(Math.Round(ms), FloorMs[_lvl], Duel.FireWindowMs - 200);
    }
}
