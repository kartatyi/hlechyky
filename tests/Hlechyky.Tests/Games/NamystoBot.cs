using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Простий гравець «Намиста» для тестів: цілиться в намистину свого кольору, поруч із якою вже стоїть така сама
/// (вставка дасть трійку), найближчу до криниці; нема такої для поточної, а для наступної є — міняє. Стріляє по одній.
/// Детермінований: той самий сід — той самий журнал, тож годиться і для паритету C# ↔ JS.
/// </summary>
public static class NamystoBot
{
    /// <summary>Зіграти трасу до кінця (або до <paramref name="maxSteps"/>); журнал — [крок, вид, x, y] / [крок, 2].</summary>
    /// <param name="every">Раз на скільки кроків бот думає (людина — раз на пів секунди-секунду).</param>
    /// <param name="err">Похибка прицілу в пікселях (людська рука): рівномірно ±err, від сіда.</param>
    public static (NamystoSim Sim, List<int[]> Journal) Play(NamystoLevel lv, uint seed, int maxSteps = 60 * 60 * 8, int every = 6, int err = 0)
    {
        var sim = new NamystoSim(lv.Track, lv.Colors, lv.Beads, lv.Speed, lv.Par, seed);
        var journal = new List<int[]>();
        var rng = new Random((int)seed);
        while (sim.State == NamystoSim.Playing && sim.S < maxSteps)
        {
            if (sim.S % every == 0) Decide(sim, journal, rng, err);
            sim.Step();
        }
        return (sim, journal);
    }

    public static void Decide(NamystoSim sim, List<int[]> journal, Random? rng = null, int err = 0)
    {
        if (sim.CooldownLeft > 0 || sim.Shots.Count > 0) return;
        var good = Target(sim, sim.Cur, pair: true);
        if (good < 0 && Target(sim, sim.Next, pair: true) >= 0 && sim.Cur != sim.Next)
        {
            if (sim.Swap()) journal.Add([sim.S, NamystoSim.EvSwap]);
            return;
        }
        var j = good >= 0 ? good : Target(sim, sim.Cur, pair: false);
        if (j < 0) j = Visible(sim);
        if (j < 0) return;
        var (x, y) = sim.Track.At(sim.Beads[j].D);
        int tx = x / NamystoSim.Px, ty = y / NamystoSim.Px;
        if (err > 0 && rng is not null) { tx += rng.Next(-err, err + 1); ty += rng.Next(-err, err + 1); }
        if (sim.Fire(tx, ty)) journal.Add([sim.S, NamystoSim.EvFire, tx, ty]);
    }

    static int Target(NamystoSim sim, int c, bool pair)
    {
        var b = sim.Beads;
        for (var i = 0; i < b.Count; i++)
        {
            if (b[i].C != c || b[i].D < NamystoSim.Dm || b[i].D >= sim.End) continue;
            if (!pair) return i;
            var prev = i > 0 && b[i - 1].C == c && b[i - 1].D - b[i].D <= NamystoSim.Dm;
            var next = i < b.Count - 1 && b[i + 1].C == c && b[i].D - b[i + 1].D <= NamystoSim.Dm;
            if (prev || next) return i;
        }
        return -1;
    }

    static int Visible(NamystoSim sim)
    {
        for (var i = 0; i < sim.Beads.Count; i++)
            if (sim.Beads[i].D >= NamystoSim.Dm && sim.Beads[i].D < sim.End) return i;
        return -1;
    }

    /// <summary>Прогнати журнал на новій копії — так, як це робить сервер.</summary>
    public static NamystoSim Replay(NamystoLevel lv, uint seed, IReadOnlyList<int[]> journal, int maxSteps = 60 * 60 * 8, List<uint>? hashes = null, int every = 600)
    {
        var sim = new NamystoSim(lv.Track, lv.Colors, lv.Beads, lv.Speed, lv.Par, seed);
        var k = 0;
        while (sim.State == NamystoSim.Playing && sim.S < maxSteps)
        {
            while (k < journal.Count && journal[k][0] == sim.S)
            {
                var e = journal[k++];
                sim.Apply(e[1], e.Length > 2 ? e[2] : 0, e.Length > 3 ? e[3] : 0);
            }
            sim.Step();
            if (hashes is not null && sim.S % every == 0) hashes.Add(sim.Hash());
        }
        return sim;
    }
}
