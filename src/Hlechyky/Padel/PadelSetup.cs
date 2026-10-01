using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hlechyky.Padel;

/// <summary>
/// Підключення «Падельні». Дві половини підключаються своїми файлами: гра — <see cref="PadelPlaySetup"/>
/// (гравці, табло, турніри, голос), гроші — <see cref="PadelMoneySetup"/> (збори, витрати, банки, рейтинг і
/// відзнаки). Тут — лише спільне: налаштування, розсилка, порожні заглушки на випадок, якщо котрась половина не
/// реєструє свого, і маршрути: <c>/api/padel/…</c>, хаб <c>/hub/padel</c>, сторінка <c>/padel/</c> (web/padel/index.html).
/// </summary>
public static class PadelSetup
{
    public static IServiceCollection AddHlechykyPadel(this IServiceCollection services, IConfiguration cfg)
    {
        services.Configure<PadelOptions>(cfg.GetSection("Padel"));
        services.TryAddSingleton<IPadelWire, HubPadelWire>();
        PadelMoneySetup.Add(services);
        PadelPlaySetup.Add(services);
        // Половина, що свого не дала, лишає порожнє — інша однаково працює
        services.TryAddSingleton<IPadelAgenda, NoPadelAgenda>();
        services.TryAddSingleton<IPadelHistory, NoPadelHistory>();
        return services;
    }

    public static WebApplication MapHlechykyPadel(this WebApplication app)
    {
        var api = app.MapGroup("/api/padel");
        PadelPlaySetup.Map(api);
        PadelMoneySetup.Map(api);
        app.MapHub<PadelHub>("/hub/padel");
        PadelPlaySetup.Start(app.Services);
        PadelMoneySetup.Start(app.Services);
        return app;
    }

    /// <summary>Відмова для клієнта: <c>{ ok: false, message }</c> з потрібним кодом.</summary>
    public static IResult Fail(string message, int status = 400) =>
        Results.Json(new { ok = false, message }, statusCode: status);
}
