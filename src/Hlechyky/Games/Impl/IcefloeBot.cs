namespace Hlechyky.Games.Impl;

/// <summary>
/// Бот Крижини, коли людина за столом сама (їх двоє: див. spec «Соло з ботом»). Грає тим самим вводом, що й людина:
/// сектор руху (0..15, −1 — відпустити), ривок, сніжок, молоток з берега — через <see cref="Icefloe"/>, з тими самими
/// перезарядками й фізикою. Бачить те, що й людина: тіла, їхній рух, край криги, тріщину, підбирачки.
/// <para>
/// На кризі: тримається подалі від краю (сильний — з прогнозом, куди його несе інерція), заходить суперникові з боку
/// центру й штовхає до краю, ривком — коли вирівнявся. Сильний керує швидкістю, а не напрямком («куди мені треба
/// їхати» мінус «куди вже їду»), тож не проскакує повз і не вилітає сам; легкий пре навпростець і сам інколи
/// доїжджає у воду. На березі — кидає сніжки (сильний — на впередження) і раз за раунд коле лід під суперником.
/// </para>
/// </summary>
public sealed class IcefloeBot
{
    /// <summary>Як часто думає (тиків по 40 мс) — легкий / звичайний / сильний: це і є його «реакція».</summary>
    static readonly int[] Every = [8, 5, 3];
    /// <summary>Шанс смикнути не в той сектор.</summary>
    static readonly double[] Wobble = [0.3, 0.12, 0.04];
    /// <summary>Запас до краю понад тіло (u) і на скільки секунд уперед бачить свій занос.</summary>
    static readonly double[] Margin = [30, 90, 130], Foresee = [0, 0.25, 0.45];
    /// <summary>Бажана швидкість, коли керує швидкістю (звичайний, сильний); легкий просто пре.</summary>
    static readonly double[] Cruise = [0, 520, 650];
    /// <summary>Ривок: наскільки має бути вирівняний на суперника (косинус кута) і з якої відстані.</summary>
    static readonly double[] DashAlign = [0.3, 0.8, 0.88];
    static readonly double[] DashReach = [200, 190, 210];
    /// <summary>Шанс ривка, коли можна (легкий часто не тисне, а часто — тисне не вчасно).</summary>
    static readonly double[] DashP = [0.35, 0.7, 0.9];
    /// <summary>Розкид сніжка з берега (σ, градуси) і шанс кинути, коли готовий.</summary>
    static readonly double[] ThrowSd = [16, 8, 4], ThrowP = [0.25, 0.5, 0.8];

    readonly int _lvl;
    int _next;

    public IcefloeBot(LiveBots.Level level, int offset)
    {
        _lvl = LiveBots.Index(level);
        _next = offset;
    }

    /// <summary>Що бот робить цього тика.</summary>
    public struct Move
    {
        /// <summary>Сектор (−1 — відпустити); null — не міняти.</summary>
        public int? Sector;
        public bool Dash, Throw, Chip;
    }

    /// <summary>Подумати раз на кілька тиків; решту тиків тримає, що вирішив (намір живе 30 тиків, тож оновлює його).</summary>
    public bool Due(int t)
    {
        if (t < _next) return false;
        _next = t + Every[_lvl];
        return true;
    }

    public Move Think(IcefloeCore c, int me, Random rng)
    {
        var b = c.Bodies[me];
        return b.Alive ? OnIce(c, me, rng) : OnBank(c, me, rng);
    }

    // ---------- на кризі ----------

    Move OnIce(IcefloeCore c, int me, Random rng)
    {
        var m = new Move();
        var b = c.Bodies[me].B;
        double px = b.X, py = b.Y;
        // Де буду за мить (легкий — не думає про занос).
        double fx = px + b.Vx * Foresee[_lvl], fy = py + b.Vy * Foresee[_lvl];
        var slack = Slack(c, fx, fy);
        var danger = slack < IcefloeCore.BodyR + Margin[_lvl];
        // Тріщина під ногами (звичайний і сильний її помічають) — теж геть від краю.
        if (!danger && _lvl > 0 && c.CrackS >= 0 && slack < 260 && InCrack(c, px, py)) danger = true;
        if (danger)
        {
            m.Sector = Steer(c.Bodies[me], IcefloeCore.Cx - px, IcefloeCore.Cy - py, rng, Cruise[_lvl] + 150);
            return m;
        }

        var q = Nearest(c, me, px, py);
        // Підбирачка ближче за суперника — звичайний і сильний спершу по неї.
        if (_lvl > 0 && Pickup(c, px, py, q) is { } pk)
        {
            m.Sector = Steer(c.Bodies[me], pk.X - px, pk.Y - py, rng, Cruise[_lvl]);
            return m;
        }
        if (q < 0) { m.Sector = Steer(c.Bodies[me], IcefloeCore.Cx - px, IcefloeCore.Cy - py, rng, 200); return m; }

        var o = c.Bodies[q].B;
        // Сильний б'є туди, де суперник буде (інерція), решта — туди, де він є.
        double lead = _lvl == 2 ? 0.25 : 0;
        double qx = o.X + o.Vx * lead, qy = o.Y + o.Vy * lead;
        // Вісь «центр → суперник»: штовхати треба вздовж неї назовні, тож заходимо з боку центру.
        double nx = qx - IcefloeCore.Cx, ny = qy - IcefloeCore.Cy;
        var nl = Math.Sqrt(nx * nx + ny * ny);
        double dx = qx - px, dy = qy - py;
        var d = Math.Sqrt(dx * dx + dy * dy);
        if (nl < 80) { nx = dx; ny = dy; nl = Math.Max(1, d); }   // суперник у центрі — просто в лоб
        nx /= nl;
        ny /= nl;
        var align = d < 1 ? 1 : (dx * nx + dy * ny) / d;          // 1 — стою рівно між центром і ним
        double tx, ty;
        if (_lvl == 0 || align > 0.55)
        {
            // легкий завжди пре в лоб; решта — коли вже зайшли з боку центру
            tx = qx;
            ty = qy;
        }
        else
        {
            tx = qx - nx * 2.4 * IcefloeCore.BodyR;
            ty = qy - ny * 2.4 * IcefloeCore.BodyR;
        }
        m.Sector = Steer(c.Bodies[me], tx - px, ty - py, rng, Cruise[_lvl]);

        var body = c.Bodies[me];
        if (body.Cd == 0 && d < DashReach[_lvl] + 2 * IcefloeCore.BodyR && rng.NextDouble() < DashP[_lvl])
        {
            // ривок летить туди, куди дивишся: дивимось на суперника
            var face = SectorTo(dx, dy);
            var cos = Math.Cos((face - body.Face) * Math.PI / 8);
            if (_lvl == 0 || (align > DashAlign[_lvl] && cos > 0.7))
            {
                m.Sector = Aim(face, rng);
                m.Dash = true;
            }
        }
        // Сніжок з криги — коли суперник і так при краю (звичайний, сильний).
        if (_lvl > 0 && body.Ammo > 0 && body.ThrowCd == 0 && Slack(c, o.X, o.Y) < 260 && align > 0.6 && rng.NextDouble() < ThrowP[_lvl])
        {
            m.Sector = SectorTo(dx, dy);
            m.Throw = true;
        }
        return m;
    }

    /// <summary>
    /// Кермо. Легкий — сектор просто на ціль. Звичайний і сильний — на різницю «бажана швидкість мінус теперішня»:
    /// так гальмують перед ціллю й не проскакують повз неї у воду.
    /// </summary>
    int Steer(IcefloeBody body, double dx, double dy, Random rng, double cruise)
    {
        var d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1) return -1;
        if (_lvl == 0) return Aim(SectorTo(dx, dy), rng);
        var want = cruise * Math.Min(1, d / 260);
        double sx = dx / d * want - body.B.Vx, sy = dy / d * want - body.B.Vy;
        if (sx * sx + sy * sy < 40 * 40) return -1;             // їду як треба — відпускаю
        return Aim(SectorTo(sx, sy), rng);
    }

    int Aim(int sector, Random rng) =>
        rng.NextDouble() < Wobble[_lvl] ? (sector + (rng.Next(2) == 0 ? 15 : 1)) & 15 : sector;

    static int SectorTo(double dx, double dy) => IcefloeCore.SectorOf(Math.Atan2(dy, dx) * 180 / Math.PI);

    /// <summary>Скільки лишилось до краю криги по радіусу (від'ємне — уже над водою).</summary>
    static double Slack(IcefloeCore c, double x, double y)
    {
        double dx = x - IcefloeCore.Cx, dy = y - IcefloeCore.Cy;
        return c.EdgeAt(Math.Atan2(dy, dx)) - Math.Sqrt(dx * dx + dy * dy);
    }

    static bool InCrack(IcefloeCore c, double x, double y)
    {
        var a = Math.Atan2(y - IcefloeCore.Cy, x - IcefloeCore.Cx);
        if (a < 0) a += 2 * Math.PI;
        var v = (int)(a / (Math.PI / 12));
        var k = ((v - c.CrackS) % IcefloeCore.Vertices + IcefloeCore.Vertices) % IcefloeCore.Vertices;
        return k <= c.CrackL;
    }

    /// <summary>Найближчий живий суперник (у Крижині з ботами команд нема — кожен сам за себе); −1 — нікого.</summary>
    static int Nearest(IcefloeCore c, int me, double x, double y)
    {
        int best = -1;
        var bd = double.MaxValue;
        for (var s = 0; s < IcefloeCore.Seats; s++)
        {
            var o = c.Bodies[s];
            if (s == me || !o.Plays || !o.Alive) continue;
            var d = (o.B.X - x) * (o.B.X - x) + (o.B.Y - y) * (o.B.Y - y);
            if (d < bd) { bd = d; best = s; }
        }
        return best;
    }

    /// <summary>Підбирачка, до якої ближче, ніж до суперника, і яка не при самому краї.</summary>
    static (double X, double Y)? Pickup(IcefloeCore c, double x, double y, int q)
    {
        var qd = q < 0 ? double.MaxValue : (c.Bodies[q].B.X - x) * (c.Bodies[q].B.X - x) + (c.Bodies[q].B.Y - y) * (c.Bodies[q].B.Y - y);
        (double, double)? best = null;
        var bd = Math.Min(qd, 450.0 * 450);
        foreach (var p in c.Pickups)
        {
            if (!p.On || Slack(c, p.X, p.Y) < 2 * IcefloeCore.BodyR) continue;
            var d = (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y);
            if (d < bd) { bd = d; best = (p.X, p.Y); }
        }
        return best;
    }

    // ---------- на березі ----------

    Move OnBank(IcefloeCore c, int me, Random rng)
    {
        var m = new Move();
        var body = c.Bodies[me];
        var (bx, by) = c.BankPoint(body.BankAngle);
        var q = Nearest(c, me, bx, by);
        if (q < 0) return m;
        var o = c.Bodies[q].B;
        double tx = o.X, ty = o.Y;
        if (_lvl == 2)
        {
            // на впередження: доки сніжок долетить, суперник проїде своє
            var t = Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by)) / IcefloeCore.BallSpeed;
            tx += o.Vx * t;
            ty += o.Vy * t;
        }
        var deg = Math.Atan2(ty - by, tx - bx) * 180 / Math.PI + Gauss(rng) * ThrowSd[_lvl];
        var sector = IcefloeCore.SectorOf(deg);
        m.Sector = sector;
        var aligned = sector == body.Face;                        // сніжок летить туди, куди вже дивишся
        if (aligned && body.BankAmmo > 0 && body.ThrowCd == 0 && rng.NextDouble() < ThrowP[_lvl]) m.Throw = true;
        // Молоток: суперник біля того краю, куди дивлюсь, — відколоти лід під ним (легкий — навмання, зрідка).
        if (!body.ChipUsed && aligned && (_lvl == 0 ? rng.NextDouble() < 0.03 : Slack(c, o.X, o.Y) < 220 && rng.NextDouble() < 0.4))
            m.Chip = true;
        return m;
    }

    static double Gauss(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * rng.NextDouble());
    }
}
