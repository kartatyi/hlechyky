using System.Text;
using System.Text.Json;
using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Паритет C# ↔ JS для «Намиста»: бот грає кілька трас, C# записує журнали й хеші стану кожні 300 кроків у
/// docs/games/dev/namysto-parity.js, а той самий файл у браузері проганяє журнали через web/games/namysto-sim.js і
/// звіряє хеші до біта (Node на машині нема). Тест стежить, щоб файл не застарів: змінилась симуляція — перезапиши
/// його (NAMYSTO_PARITY_WRITE=1) і прожени в браузері.
/// </summary>
public class NamystoParityTests
{
    const int Every = 300;

    static readonly (int Level, uint Seed, bool Daily)[] Cases =
        [(1, 1, false), (1, 2, false), (4, 3, false), (8, 4, false), (11, 5, false), (12, 6, false), (6, 7, true)];

    static string Root([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

    static string Build()
    {
        var cases = new List<object>();
        foreach (var (level, seed, daily) in Cases)
        {
            var lv = daily ? NamystoTracks.Level(level) with { Colors = 5, Beads = 120, Speed = 10, Par = 140 } : NamystoTracks.Level(level);
            var (_, journal) = NamystoBot.Play(lv, seed, every: 9, err: 6);
            var hashes = new List<uint>();
            var sim = NamystoBot.Replay(lv, seed, journal, hashes: hashes, every: Every);
            cases.Add(new
            {
                n = lv.N, colors = lv.Colors, beads = lv.Beads, speed = lv.Speed, par = lv.Par, seed,
                journal, every = Every, hashes, steps = sim.S, hash = sim.Hash(), score = sim.Score, state = sim.State,
            });
        }
        var json = JsonSerializer.Serialize(new { v = NamystoSetup.TracksVersion.Value, cases });
        var sb = new StringBuilder();
        sb.Append("""
            // «Намисто»: паритет C# ↔ JS. Файл пише тест NamystoParityTests (NAMYSTO_PARITY_WRITE=1) — руками не правити.
            // Журнали бота, записані C#, проганяє web/games/namysto-sim.js — хеші стану мусять збігтися до біта.
            // Запуск: на будь-якій сторінці дев-сервера вставити вміст у консоль (чи Browser-панель javascript_tool).
            // Друкує рядок на випадок: OK / MISMATCH, і підсумок «7/7».
            """.Replace("\r\n", "\n"));
        sb.Append('\n');
        sb.Append("(async () => {\n");
        sb.Append("const DATA = ").Append(json).Append(";\n");
        sb.Append("""
            if (!window.NamystoSim) {
              await new Promise((resolve, reject) => {
                const s = document.createElement('script');
                s.src = '/games/namysto-sim.js?cb=' + Date.now();
                s.onload = resolve;
                s.onerror = () => reject(new Error('namysto-sim.js не завантажився'));
                document.head.appendChild(s);
              });
            }
            const T = await (await fetch('/api/games/namysto/tracks')).json();
            const out = [];
            if (T.v !== DATA.v) out.push('УВАГА: траси на сервері (' + T.v + ') не ті, з якими записано (' + DATA.v + ')');
            let ok = 0;
            for (const c of DATA.cases) {
              const t = T.tracks.find((x) => x.n === c.n);
              const track = NamystoSim.Track.fromDelta(t.pts, t.sx, t.sy);
              const t0 = performance.now();
              const r = NamystoSim.replay(track, c, c.seed, c.journal, null, c.every);
              const ms = (performance.now() - t0).toFixed(1);
              let first = -1;
              for (let i = 0; i < Math.max(c.hashes.length, r.hashes.length); i++) if (c.hashes[i] !== r.hashes[i]) { first = i; break; }
              const same = first < 0 && r.sim.S === c.steps && r.sim.hash() === c.hash && r.sim.Score === c.score && r.sim.State === c.state;
              if (same) ok++;
              out.push((same ? 'OK' : 'MISMATCH') + ' траса ' + c.n + ', сід ' + c.seed + ': ' + r.sim.S + '/' + c.steps + ' кроків, '
                + r.sim.Score + '/' + c.score + ' очок, ' + c.journal.length + ' подій' + (first >= 0 ? ', розійшлось до кроку ' + (first + 1) * c.every : '') + ' (' + ms + ' мс)');
            }
            out.push(ok + '/' + DATA.cases.length);
            console.log(out.join('\n'));
            return out;
            })();
            """.Replace("\r\n", "\n"));
        sb.Append('\n');
        return sb.ToString();
    }

    [Fact]
    public void Parity_script_is_up_to_date_with_the_simulation()
    {
        var path = Path.Combine(Root(), "docs", "games", "dev", "namysto-parity.js");
        var want = Build();
        if (Environment.GetEnvironmentVariable("NAMYSTO_PARITY_WRITE") == "1") File.WriteAllText(path, want, new UTF8Encoding(false));
        Assert.True(File.Exists(path), "нема docs/games/dev/namysto-parity.js — запусти тест з NAMYSTO_PARITY_WRITE=1");
        var have = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.True(have == want, "паритет застарів: симуляція чи траси змінились — перезапиши файл (NAMYSTO_PARITY_WRITE=1) і прожени в браузері");
    }
}
