using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hlechyky;

/// <summary>
/// Автодеплой. GitHub шле сюди подію workflow_run; коли збірка в Actions закінчилась зеленою
/// на main, запускаємо deploy.ps1 окремим процесом — він переживе перезапуск сервера.
/// Тіло підписане HMAC-SHA256 (той самий секрет, що в налаштуваннях вебхука на GitHub);
/// без підпису або без секрета в конфізі сюди ніхто не достукається.
/// </summary>
public static class Deploy
{
    public static async Task<IResult> HandleAsync(HttpContext ctx, DeployOptions opt, ILogger log)
    {
        if (!opt.Enabled || string.IsNullOrWhiteSpace(opt.WebhookSecret)) return Results.NotFound();
        if (ctx.Request.ContentLength > 1_000_000) return Results.StatusCode(413);

        using var buffer = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(buffer, ctx.RequestAborted);
        var body = buffer.ToArray();

        if (!SignatureOk(body, ctx.Request.Headers["X-Hub-Signature-256"].ToString(), opt.WebhookSecret))
        {
            log.LogWarning("Деплой: запит із поганим підписом від {Ip}", ctx.Connection.RemoteIpAddress);
            return Results.StatusCode(401);
        }

        var evt = ctx.Request.Headers["X-GitHub-Event"].ToString();
        if (evt == "ping") return Ignored("Глечики слухають");
        if (evt != "workflow_run") return Ignored($"Подія {evt} мене не обходить");

        JsonElement root;
        try { root = JsonDocument.Parse(body).RootElement; }
        catch (JsonException) { return Results.BadRequest(new { ok = false, message = "Не JSON" }); }

        if (Str(root, "action") != "completed") return Ignored("Збірка ще біжить");
        if (!root.TryGetProperty("workflow_run", out var run)) return Ignored("Нема workflow_run");

        var name = Str(run, "name");
        var branch = Str(run, "head_branch");
        var conclusion = Str(run, "conclusion");
        var sha = Str(run, "head_sha");

        if (!string.IsNullOrEmpty(opt.Workflow) && !name.Equals(opt.Workflow, StringComparison.OrdinalIgnoreCase))
            return Ignored($"Чекаю на «{opt.Workflow}», а це «{name}»");
        if (!branch.Equals(opt.Branch, StringComparison.Ordinal) || Str(run, "event") != "push")
            return Ignored($"Не push у {opt.Branch} (гілка {branch})");
        if (conclusion != "success")
        {
            log.LogWarning("Деплой: збірка {Sha} червона ({Conclusion}), нічого не чіпаю", Short(sha), conclusion);
            return Ignored($"Збірка {conclusion}, деплою не буде");
        }

        log.LogInformation("Деплой: зелена збірка {Sha}, запускаю {Script}", Short(sha), opt.Script);
        try { Launch(opt.Script, sha); }
        catch (Exception ex)
        {
            log.LogError(ex, "Деплой: не вийшло запустити {Script}", opt.Script);
            return Results.StatusCode(500);
        }
        return Results.Accepted(value: new { ok = true, message = $"Деплою {Short(sha)}" });
    }

    static IResult Ignored(string message) => Results.Ok(new { ok = true, message });

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    static bool SignatureOk(byte[] body, string header, string secret)
    {
        const string prefix = "sha256=";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        byte[] got;
        try { got = Convert.FromHexString(header[prefix.Length..]); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(expected, got);
    }

    /// <summary>
    /// UseShellExecute — щоб дочірній процес не успадкував хендли сервера: за мить сервер
    /// приб'ють, а deploy.ps1 має спокійно доробити своє.
    /// </summary>
    static void Launch(string script, string sha)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            WorkingDirectory = Paths.Root,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(Paths.Resolve(script));
        if (!string.IsNullOrEmpty(sha))
        {
            psi.ArgumentList.Add("-Sha");
            psi.ArgumentList.Add(sha);
        }
        Process.Start(psi);
    }
}
