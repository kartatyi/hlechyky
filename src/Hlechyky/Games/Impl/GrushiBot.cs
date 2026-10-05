namespace Hlechyky.Games.Impl;

/// <summary>
/// Бот «Крадіїв груш». Бачить світ (<see cref="GrushiCore"/>) і вирішує те саме, що людина: куди йти (сектор) і чи
/// штовхнути — а <see cref="Grushi"/> подає це тим самим <c>Act</c>, тож швидкості, ноша й перезарядки в бота ті самі.
/// Рівні (бриф): легкий лише збирає найближче й не краде; звичайний краде, коли на землі пусто, і часом штовхає
/// навантажених; сильний вибирає вигідні груші, чатує на трус дерева, краде з найбагатшої комори, штовхає носіїв і
/// боронить свою комору. Сильний не ідеальний: думає раз на 3 тики, трохи блукає напрямком і не завжди встигає.
/// </summary>
public sealed class GrushiBot(LiveBots.Level level, int phase)
{
    /// <summary>Як часто думає (тиків), з якою ноші несе додому, як часто «промахується» сектором (%).</summary>
    static readonly int[] Every = [8, 4, 3];
    static readonly int[] HomeAt = [2, 4, 4];
    static readonly int[] Wobble = [45, 12, 5];

    readonly int _lvl = LiveBots.Index(level);
    int _sector = -1;
    Func<int, bool>? _person;

    bool Person(int s) => _person is null || _person(s);

    public LiveBots.Level Level => level;

    public bool Due(int t) => (t + phase) % Every[_lvl] == 0;

    public readonly record struct Move(int? Sector, bool Push);

    /// <param name="person">Чи місце — людина (null — невідомо, усі як люди). Сильний полює насамперед на людей-носіїв:
    /// двоє сильних ботів, що штовхають одне одного, лише марнують час, а людині цікавіше, коли полюють на неї.</param>
    public Move Think(GrushiCore c, int seat, Random rng, Func<int, bool>? person = null)
    {
        _person = person;
        var me = c.Bodies[seat];
        if (me.Stun > 0) return new(null, false);
        var push = WantPush(c, seat, me, rng);
        var (tx, ty, stop) = Target(c, seat, me, rng);
        var dx = tx - me.X;
        var dy = ty - me.Y;
        int sector;
        if (dx * dx + dy * dy <= stop * stop) sector = -1;
        else
        {
            sector = GrushiCore.SectorOf(Math.Atan2(dy, dx) * 180 / Math.PI);
            if (rng.Next(100) < Wobble[_lvl]) sector = (sector + (rng.Next(2) == 0 ? 15 : 1)) % 16;
        }
        // Легкий інколи «задумується» і йде, куди йшов.
        if (_lvl == 0 && _sector >= 0 && rng.Next(100) < 15) sector = _sector;
        // Намір підтверджуємо щодумки, як браузер, що досилає затиснутий напрямок: інакше він згасне за KeepTicks.
        _sector = sector;
        return new(sector, push);
    }

    bool WantPush(GrushiCore c, int seat, GrushiCore.Body me, Random rng)
    {
        if (_lvl == 0 || me.Cd > 0) return false;
        for (var s = 0; s < GrushiCore.Seats; s++)
        {
            var o = c.Bodies[s];
            if (s == seat || !o.Plays || o.Immune > 0) continue;
            var dx = o.X - me.X;
            var dy = o.Y - me.Y;
            if (dx * dx + dy * dy > (GrushiCore.PushR - 6) * (GrushiCore.PushR - 6)) continue;
            var thief = o.StealFrom == seat && o.StealProg > 0;
            if (_lvl == 2 && (o.Carry >= 2 || thief)) return rng.Next(100) < 85;
            if (_lvl == 1 && (o.Carry >= 3 || thief)) return rng.Next(100) < 45;
        }
        return false;
    }

    /// <summary>Куди йти і на якій відстані зупинитись.</summary>
    (double X, double Y, double Stop) Target(GrushiCore c, int seat, GrushiCore.Body me, Random rng)
    {
        // Сильний боронить свою комору: злодій поруч з нею — туди, штовхнути.
        if (_lvl == 2 && me.Cd <= 10)
            for (var s = 0; s < GrushiCore.Seats; s++)
            {
                var o = c.Bodies[s];
                if (s == seat || !o.Plays || o.StealFrom != seat || o.Carry >= GrushiCore.CarryMax) continue;
                if (Far(me.X, me.Y, o.X, o.Y) < 350 && me.Carry < 3) return (o.X, o.Y, 20);
            }
        var home = (me.LarderX, me.LarderY, GrushiCore.LarderR - 30);
        if (me.Carry >= GrushiCore.CarryMax) return home;
        var pear = BestPear(c, seat, me);
        var homeD = Far(me.X, me.Y, me.LarderX, me.LarderY);
        if (me.Carry >= HomeAt[_lvl])
        {
            // Сильний і звичайний доберуть грушу, що майже по дорозі.
            if (_lvl == 0 || pear is not { } p || me.Carry + 1 >= GrushiCore.CarryMax
                || Far(me.X, me.Y, p.X, p.Y) + Far(p.X, p.Y, me.LarderX, me.LarderY) > homeD + 120) return home;
            return (p.X, p.Y, 4);
        }
        // Сильний полює на носія, що поруч, коли штовхан готовий: людину — завжди, бота — лише коли людей-носіїв поруч
        // нема і бот не біжить сам на нас (два мисливці, що зійшлись, просто обміняються штовханами).
        if (_lvl == 2 && me.Cd == 0 && Prey(c, seat, me) is { } prey) return (prey.X, prey.Y, 20);
        if (pear is { } q) return (q.X, q.Y, 4);
        if (me.Carry > 0 && (_lvl == 0 || homeD < 300)) return home;
        // Землю вибрали. Сильний чатує під деревом, що трусить, якщо воно ближче за найближчу чужу комору.
        if (_lvl == 2 && c.ShakeTree >= 0)
        {
            var (tx, ty) = GrushiCore.Trees[c.ShakeTree];
            if (Far(me.X, me.Y, tx, ty) < 300) return (tx, ty, 60);
        }
        if (_lvl >= 1 && Rob(c, seat, me) is { } v)
        {
            var o = c.Bodies[v];
            return (o.LarderX, o.LarderY, 30);
        }
        if (me.Carry > 0) return home;
        // Нічого — під найближче дерево (легкий — під те, що трусить, якщо трусить).
        var tree = c.ShakeTree >= 0 ? c.ShakeTree : Nearest(me);
        return (GrushiCore.Trees[tree].X, GrushiCore.Trees[tree].Y, 70);
    }

    GrushiCore.Body? Prey(GrushiCore c, int seat, GrushiCore.Body me)
    {
        GrushiCore.Body? bot = null;
        for (var s = 0; s < GrushiCore.Seats; s++)
        {
            var o = c.Bodies[s];
            if (s == seat || !o.Plays || o.Immune > 0 || o.Carry < 3 || Far(me.X, me.Y, o.X, o.Y) >= 220) continue;
            if (Person(s)) return o;
            if (o.Cd > 0 || o.Carry >= GrushiCore.CarryMax) bot ??= o;
        }
        return bot;
    }

    /// <summary>Чию комору красти: звичайний — найближчу непорожню, сильний — найвигіднішу (груші / відстань).</summary>
    int? Rob(GrushiCore c, int seat, GrushiCore.Body me)
    {
        int? best = null;
        var bestK = double.MinValue;
        for (var s = 0; s < GrushiCore.Seats; s++)
        {
            var o = c.Bodies[s];
            if (s == seat || !o.HasLarder || o.Larder <= 0) continue;
            var d = Far(me.X, me.Y, o.LarderX, o.LarderY);
            // Біля комори стоїть господар — сильний туди не лізе.
            if (_lvl == 2 && o.Plays && Far(o.X, o.Y, o.LarderX, o.LarderY) < 120) continue;
            var k = _lvl == 2 ? Math.Min(o.Larder, 8) / (d + 200) : -d;
            if (k > bestK) { bestK = k; best = s; }
        }
        return best;
    }

    GrushiCore.Pear? BestPear(GrushiCore c, int seat, GrushiCore.Body me)
    {
        GrushiCore.Pear? best = null;
        var bestK = double.MinValue;
        foreach (var p in c.Ground)
        {
            var d = Far(me.X, me.Y, p.X, p.Y);
            double k;
            if (_lvl == 0) k = -d;
            else
            {
                k = (p.Gold ? GrushiCore.GoldValue : 1) / (d + 80);
                // Сильний (і трохи слабше звичайний) не біжить по грушу, до якої чужий удвічі ближче.
                if (_lvl >= 1)
                    for (var s = 0; s < GrushiCore.Seats; s++)
                    {
                        var o = c.Bodies[s];
                        if (s == seat || !o.Plays || o.Carry >= GrushiCore.CarryMax) continue;
                        if (Far(o.X, o.Y, p.X, p.Y) * 2 < d) { k *= _lvl == 2 ? 0.3 : 0.6; break; }
                    }
            }
            if (k > bestK) { bestK = k; best = p; }
        }
        return best;
    }

    static int Nearest(GrushiCore.Body me)
    {
        var best = 0;
        var bd = double.MaxValue;
        for (var i = 0; i < GrushiCore.Trees.Length; i++)
        {
            var d = Far(me.X, me.Y, GrushiCore.Trees[i].X, GrushiCore.Trees[i].Y);
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    static double Far(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
}
