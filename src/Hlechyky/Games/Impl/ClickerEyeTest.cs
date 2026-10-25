using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Ручне тестування Ока майстра (24.10): полицю не чекати шість тисяч кліків і не підробляти автоклікер, а покликати
/// кнопкою з панелі «👁 тест». Лише коли в конфігу <c>Clicker:EyeTest</c> = true (дев): інакше дія відмовляє, а вид
/// панелі не шле. <c>eyetest {kind}</c>:
/// <list type="bullet">
/// <item><c>calm</c> — звичайна перевірка, наче минув повний спокійний крок: платить сповна;</item>
/// <item><c>rhythm</c>, <c>press</c> — полиця через підозрілий почерк (рівний ритм / миттєве відпускання), без платні;</item>
/// <item><c>soon</c> — звичайна перевірка через 20 кліків: майстер приходить сам, з усіма правилами (§A.2, бафи);</item>
/// <item><c>lock</c> — пауза на 10 хвилин, як за три промахи;</item>
/// <item><c>clear</c> — прибрати полицю й паузу.</item>
/// </list>
/// </summary>
public sealed partial class Clicker
{
    /// <summary>Через скільки кліків приходить майстер за <c>soon</c>.</summary>
    public const int EyeTestSoon = 20;

    bool EyeTestOn => Ctx.Services.GetService<IOptionsMonitor<ClickerOptions>>()?.CurrentValue.EyeTest == true;

    ActResult EyeTest(JsonElement payload)
    {
        if (!EyeTestOn) return ActResult.Fail("Тут так не ходять");
        var kind = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("kind", out var k)
            && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        var now = Ctx.Clock.UtcNow;
        switch (kind)
        {
            case "calm":
                _guard.TestCalm();
                return ActResult.Accept("👁 тест: майстер питає, як за розкладом" + EyeCheck(now));
            case "rhythm" or "press":
                EyeSuspect(kind, now);
                return ActResult.Accept(kind == "rhythm" ? "👁 тест: «надто рівний ритм»" : "👁 тест: «миттєве відпускання»");
            case "soon":
                _guard.TestSoon(EyeTestSoon);
                return ActResult.Accept($"👁 тест: майстер прийде через {EyeTestSoon} кліків");
            case "lock":
                _guard.TestLock(now);
                EyeRelease(now);
                return ActResult.Accept("👁 тест: коло стоїть 10 хвилин");
            case "clear":
                _guard.TestClear(Ctx.Rng);
                EyeRelease(now);
                return ActResult.Accept("👁 тест: майстра прибрано");
            default:
                return ActResult.Fail("Невідомий тест");
        }
    }

    /// <summary>Для панелі: скільки кліків до звичайної перевірки й чи пильнує майстер. null — тестування вимкнено.</summary>
    object? EyeTestView() => EyeTestOn
        ? new { left = _guard.Left, wary = _guard.Wary, calmPassed = _guard.CalmPassed }
        : null;
}
