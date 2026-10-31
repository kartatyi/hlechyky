using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hlechyky.Tests;

/// <summary>
/// Секундомір для тестів швидкодії: той самий вигляд, що й <see cref="Stopwatch"/> (StartNew/Start/Stop/Restart/Reset,
/// Elapsed, ElapsedMilliseconds, ElapsedTicks, GetTimestamp/GetElapsedTime/Frequency), але міряє час процесора ПОТОКУ,
/// а не стінний годинник. У повному паралельному прогоні (16 потоків xUnit) чи коли поруч крутиться Chrome із замірами
/// потік тесту подовгу стоїть у черзі планувальника — стінний час росте в рази, а роботи стільки ж. Час потоку рахує
/// лише те, що потік справді виконував, тож межі в тестах лишаються тими самими й так само ловлять повільний код
/// (зайвий цикл, COUNT(*) замість кешу, квадратичний алгоритм — це все час процесора).
///
/// Як: Windows <c>QueryThreadCycleTime</c> (такти TSC, поки потік на ядрі; точно до наносекунд, на відміну від
/// GetThreadTimes з кроком 15,6 мс) ÷ частота TSC, яку один раз калібруємо проти Stopwatch (максимум відношення
/// з кількох коротких вікон: витіснення лише зменшує відношення, тож похибка калібрування робить замір суворішим,
/// а не м'якшим). Інтервал ніколи не довший за стінний. Якщо інтервал закінчився на іншому потоці (await) або
/// лічильника нема (не Windows) — чесно повертаємо стінний час.
///
/// Після await потік пулу міг між тим виконувати чужу роботу (A → B → A) — вона теж потрапить у замір, тож для
/// асинхронного тіла замір лише суворіший (і все одно не довший за стінний), а не м'якший.
/// Не для тестів, що міряють очікування (I/O, чужий процес, таймери, Task.Delay): там лишайте Stopwatch.
/// Тіло заміру не повинне розкидати роботу по інших потоках (Parallel/Task.Run з очікуванням) — її тут не видно.
/// </summary>
public sealed class CpuWatch
{
    public static readonly long Frequency = Stopwatch.Frequency;
    public static readonly bool IsHighResolution = true;

    /// <summary>Тактів TSC на один тик Stopwatch; 0 — точного лічильника нема, працюємо стінним годинником.</summary>
    static readonly Lazy<double> CyclesPerTick = new(Calibrate, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Чи міряємо справді час потоку (а не стінний годинник).</summary>
    public static bool IsThreadTime => CyclesPerTick.Value > 0;

    long _acc;
    bool _running;
    long _wall0, _cpu0;
    int _thread;

    public static CpuWatch StartNew()
    {
        var w = new CpuWatch();
        w.Start();
        return w;
    }

    public bool IsRunning => _running;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = Environment.CurrentManagedThreadId;
        _cpu0 = ThreadTicks();
        _wall0 = Stopwatch.GetTimestamp();
    }

    public void Stop()
    {
        if (!_running) return;
        _acc += Current();
        _running = false;
    }

    public void Reset()
    {
        _running = false;
        _acc = 0;
    }

    public void Restart()
    {
        Reset();
        Start();
    }

    /// <summary>У тиках <see cref="Frequency"/> — як <see cref="Stopwatch.ElapsedTicks"/>.</summary>
    public long ElapsedTicks => _acc + (_running ? Current() : 0);
    public TimeSpan Elapsed => ToTimeSpan(ElapsedTicks);
    public long ElapsedMilliseconds => ElapsedTicks * 1000 / Frequency;

    /// <summary>Мітка часу потоку в тиках <see cref="Frequency"/>; різницю брати на тому самому потоці.</summary>
    public static long GetTimestamp() => IsThreadTime ? ThreadTicks() : Stopwatch.GetTimestamp();
    public static TimeSpan GetElapsedTime(long start) => ToTimeSpan(GetTimestamp() - start);
    public static TimeSpan GetElapsedTime(long start, long end) => ToTimeSpan(end - start);

    long Current()
    {
        var wall = Stopwatch.GetTimestamp() - _wall0;
        if (!IsThreadTime || Environment.CurrentManagedThreadId != _thread) return wall;
        return Math.Clamp(ThreadTicks() - _cpu0, 0, wall);
    }

    static TimeSpan ToTimeSpan(long ticks) => new((long)(ticks * ((double)TimeSpan.TicksPerSecond / Frequency)));

    static long ThreadTicks()
    {
        var k = CyclesPerTick.Value;
        return k > 0 && QueryThreadCycleTime(GetCurrentThread(), out var c) ? (long)(c / k) : Stopwatch.GetTimestamp();
    }

    static double Calibrate()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try
        {
            var h = GetCurrentThread();
            var window = Frequency / 500;   // 2 мс
            var best = 0.0;
            for (var i = 0; i < 25; i++)
            {
                if (!QueryThreadCycleTime(h, out var c0)) return 0;
                var t0 = Stopwatch.GetTimestamp();
                long t1;
                while ((t1 = Stopwatch.GetTimestamp()) - t0 < window) { }
                QueryThreadCycleTime(h, out var c1);
                best = Math.Max(best, (c1 - c0) / (double)(t1 - t0));
            }
            // TSC тікає на сотнях МГц–ГГц; якщо вийшло щось дивне — не довіряємо, лишаємось на стінному
            return best * Frequency > 1e8 ? best : 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0;
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentThread();
}
