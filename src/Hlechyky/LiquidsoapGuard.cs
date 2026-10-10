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

            string? dump = null;
            try { dump = await liq.CommandAsync("clock.dump", ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { /* liquidsoap лежить чи перезапускається — це справа наглядача */ }

            var now = DateTime.UtcNow;
            var stuck = clock.Observe(AirClock.Parse(dump), now);
            if (stuck < limit || now - restartedAt < Cooldown) continue;

            restartedAt = now;
            clock.Reset();
            log.LogWarning("годинник ефіру стоїть {Seconds:0} с — перезапускаю liquidsoap (start.ps1 radio)\n{Dump}", stuck, dump);
            try { Restart($"годинник ефіру стоїть {stuck:0} с (помітив сервер)"); }
            catch (Exception ex) { log.LogWarning(ex, "start.ps1 radio не запустився"); }
        }
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

    /// <summary>«· radio (ticks: 3982739, time: 79654.78s, self_sync: false)» → 79654.78.</summary>
    public static double? Parse(string? dump)
    {
        var m = dump is null ? null : Regex.Match(dump, @"time: ([0-9.]+)s");
        return m is { Success: true } && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : null;
    }
}
