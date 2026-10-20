using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>«📷 Глек дякує» (пакет 4) — гачок розкриття ядра «Де це?»: фото друга вгадали — сказати <see cref="GeoThanks"/>.</summary>
public sealed partial class GeoMatch
{
    /// <summary>
    /// Усі, хто влучив ближче за <see cref="GeoThanks.Km"/>, — кандидати на подяку автору (заплатить перший, що пройде).
    /// Решту (уперше, перші три фото, свої, гості) вирішує сервіс; тут лише пам'ять.
    /// </summary>
    void Thank(Round round, List<Row> rows)
    {
        if (round.Mine is not { } photo || _ctx.Services.GetService<GeoThanks>() is not { } thanks) return;
        foreach (var r in rows)
        {
            if (r.Own || r.Km is not { } k || k > GeoThanks.Km) continue;
            thanks.Guessed(photo, _ctx.NickOf(r.Seat) ?? _nicks[r.Seat]);
        }
    }
}
