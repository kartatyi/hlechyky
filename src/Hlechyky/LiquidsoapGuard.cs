using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>
/// Ефір «замерзає»: Windows-збірка liquidsoap часом зупиняє годинник — процес живий, telnet відповідає, порт слухає,
/// а потік мовчить (8–9.10.2026: тричі за вісім годин). Наглядач start.ps1 дивиться раз на хвилину і чекає двох
/// перевірок поспіль, тож люди сиділи в тиші 2–3 хв. Сервер питає liquidsoap і так, тому стежить за годинником сам:
/// стоїть <see cref="LiquidsoapOptions.FreezeRestartSeconds"/> — перезапускає liquidsoap через <c>start.ps1 radio</c>
/// (там же замок запуску, лог і знімок завислого логу), а плеєри на сайті підключаються знову самі.
/// <para>
/// Працює лише там, де start.ps1 справді керує liquidsoap (є <c>tools\liquidsoap\liquidsoap.exe</c>): у D:\or-dev
/// і воркдеревах його нема, і <c>start.ps1 radio</c> звідти вбив би ЖИВИЙ ефір проду за портом 1234.
/// </para>
/// </summary>
public sealed class LiquidsoapGuard(LiquidsoapClient liq, IOptionsMonitor<LiquidsoapOptions> options, ILogger<LiquidsoapGuard> log) : BackgroundService
{
    static readonly TimeSpan Every = TimeSpan.FromSeconds(5);
    /// <summary>Не частіше: свіжий liquidsoap мусить устигнути піднятись, а коли він замерзає знову й знову — далі веде наглядач.</summary>
    static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);
    /// <summary>
    /// Пауза перед другим показанням: 21.10.2026 23:38 годинник простояв 19,56 с і рушив сам (liquidsoap доганяв
    /// 11 с ефіру за секунду), а сторож перезапустив його за мить після того на старому показанні.
    /// </summary>
    static readonly TimeSpan Recheck = TimeSpan.FromSeconds(2);

    static string StartScript => Path.Combine(Paths.Root, "start.ps1");
    static string LiqExe => Path.Combine(Paths.Root, "tools", "liquidsoap", "liquidsoap.exe");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var clock = new AirClock();
        var restartedAt = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(Every, ct); }
            catch (OperationCanceledException) { break; }
            var limit = options.CurrentValue.FreezeRestartSeconds;
            if (limit <= 0 || !File.Exists(LiqExe) || !File.Exists(StartScript)) continue;

            var dump = await DumpAsync(ct);
            if (ct.IsCancellationRequested) break;

            var now = DateTime.UtcNow;
            var stuck = clock.Observe(AirClock.Parse(dump), now);
            if (stuck < limit || now - restartedAt < Cooldown) continue;

            // ще одне показання перед перезапуском: годинник міг рушити сам саме зараз
            try { await Task.Delay(Recheck, ct); }
            catch (OperationCanceledException) { break; }
            var again = await DumpAsync(ct);
            if (ct.IsCancellationRequested) break;
            var then = DateTime.UtcNow;
            var (before, after) = (AirClock.Parse(dump), AirClock.Parse(again));
            if (AirClock.Thawed(before, after, (then - now).TotalSeconds))
            {
                clock.Reset();
                clock.Observe(after, then);
                if (after < before) log.LogWarning("годинник ефіру стояв {Seconds:0} с, а тепер почав з нуля — liquidsoap уже перезапустили, не чіпаю", stuck);
                else log.LogWarning("годинник ефіру відмерз сам через {Seconds:0} с — не перезапускаю liquidsoap\n{Dump}", stuck, again);
                continue;
            }

            restartedAt = now;
            clock.Reset();
            log.LogWarning("годинник ефіру стоїть {Seconds:0} с — перезапускаю liquidsoap (start.ps1 radio)\n{Dump}", stuck, again ?? dump);
            try { Restart($"годинник ефіру стоїть {stuck:0} с (помітив сервер)"); }
            catch (Exception ex) { log.LogWarning(ex, "start.ps1 radio не запустився"); }
        }
    }

    /// <summary>«clock.dump»; null — не відповів (liquidsoap лежить чи перезапускається — це справа наглядача) або сервер зупиняють.</summary>
    async Task<string?> DumpAsync(CancellationToken ct)
    {
        try { return await liq.CommandAsync("clock.dump", ct); }
        catch { return null; }
    }

    /// <summary>UseShellExecute — як у <see cref="Deploy"/>: powershell не успадковує хендлів сервера й живе сам по собі.</summary>
    static void Restart(string why)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            WorkingDirectory = Paths.Root,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", StartScript, "radio", "-Why", why }) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }
}

/// <summary>Чи йде годинник ефіру (час із telnet «clock.dump»). Окремо від сервісу — щоб перевіряти тестами.</summary>
public sealed class AirClock
{
    double? _clock;
    DateTime _at;

    /// <summary>
    /// Нове показання. Повертає, скільки секунд годинник стоїть (0 — іде). «Іде» — устиг хоча б пів того часу, що минув:
    /// перевантажена машина відстає, але не мовчить. Нема показання чи годинник скинувся (liquidsoap перезапустили) —
    /// починаємо відлік спочатку.
    /// </summary>
    public double Observe(double? clock, DateTime now)
    {
        if (clock is null || _clock is null || clock < _clock)
        {
            _clock = clock;
            _at = now;
            return 0;
        }
        var wall = (now - _at).TotalSeconds;
        if (clock - _clock >= wall / 2)
        {
            _clock = clock;
            _at = now;
            return 0;
        }
        return wall;
    }

    public void Reset() => _clock = null;

    /// <summary>
    /// Друге показання перед перезапуском, через <paramref name="wall"/> с після першого: годинник рушив (те саме «пів того
    /// часу, що минув») — відмерз сам; почав з нуля — liquidsoap уже перезапустили. І так, і так перезапуск не потрібен.
    /// Не відповів — не знаємо, що ожив, тож перезапускаємо.
    /// </summary>
    public static bool Thawed(double? before, double? after, double wall) =>
        before is { } b && after is { } a && (a < b || a > b && a - b >= wall / 2);

    /// <summary>«· radio (ticks: 3982739, time: 79654.78s, self_sync: false)» → 79654.78.</summary>
    public static double? Parse(string? dump)
    {
        var m = dump is null ? null : Regex.Match(dump, @"time: ([0-9.]+)s");
        return m is { Success: true } && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : null;
    }
}
