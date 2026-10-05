using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Hlechyky;

/// <summary>
/// Живий процес edge-tts (див. <see cref="EdgeTtsEngine"/>): python з уже імпортованим <c>edge_tts</c> читає запити
/// рядками JSON зі stdin і відповідає рядком у stdout. Один запит за раз — черга озвучки й так одна. stdin закрився
/// (сервер зупинився чи впав) — процес виходить сам; живий сервер при зупинці його вбиває (<see cref="Dispose"/>).
/// Скрипт лежить рядком тут і пишеться поруч із кешем озвучки — окремо деплоїти нічого не треба.
/// </summary>
public sealed class EdgeWorker : IDisposable
{
    /// <summary>Не піднявся — наступна спроба не раніше; поки що репліки йдуть окремими запусками.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);
    static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    public const string FileName = "edge-worker.py";

    public const string Script = """
        # Живий edge-tts для «Своєї гри» й «Додепів»: один процес, імпорт — один раз. Запит — рядок JSON у stdin
        # {"voice", "rate", "text", "out"}, відповідь — рядок JSON {"ok": true} або {"ok": false, "err": "..."}.
        # stdin закрився (сервер зупинився чи впав) — виходимо. Файл пише сервер (src/Hlechyky/TtsWorker.cs) — правити там.
        import asyncio, json, sys
        import edge_tts


        async def main():
            loop = asyncio.get_running_loop()
            while True:
                line = await loop.run_in_executor(None, sys.stdin.readline)
                if not line:
                    return
                try:
                    req = json.loads(line)
                    await edge_tts.Communicate(req["text"], req["voice"], rate=req["rate"]).save(req["out"])
                    reply = {"ok": True}
                except Exception as e:
                    reply = {"ok": False, "err": (type(e).__name__ + ": " + str(e))[:300]}
                sys.stdout.write(json.dumps(reply) + "\n")
                sys.stdout.flush()


        sys.stdout.write(json.dumps({"ready": True}) + "\n")
        sys.stdout.flush()
        asyncio.run(main())

        """;

    readonly Process _p;

    EdgeWorker(Process p) => _p = p;

    public bool Alive
    {
        get
        {
            try { return !_p.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    /// <summary>Підняти процес і дочекатись, що він імпортував edge_tts. null — не вийшло (нема python чи edge_tts).</summary>
    public static async Task<EdgeWorker?> StartAsync(string python, string dir, ILogger log, CancellationToken ct)
    {
        string script;
        try
        {
            Directory.CreateDirectory(dir);
            script = Path.Combine(dir, FileName);
            if (!File.Exists(script) || File.ReadAllText(script) != Script) File.WriteAllText(script, Script, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("edge-tts: скрипт живого процесу не записався: {Err}", ex.Message);
            return null;
        }
        var psi = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // без BOM: інакше перший же запит python не розбере як JSON
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Paths.Root,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(script);
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.LogWarning("edge-tts: живий процес не запустився: {Err}", ex.Message);
            return null;
        }
        if (p is null) return null;
        var w = new EdgeWorker(p);
        // stderr читаємо до кінця, щоб повний канал не зупинив python; що там — видно в останніх рядках при збої
        var err = p.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            var line = await p.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(StartTimeout, ct);
            if (line is not null && line.Contains("\"ready\"", StringComparison.Ordinal))
            {
                log.LogInformation("edge-tts: живий процес готовий (pid {Pid})", p.Id);
                return w;
            }
        }
        catch (TimeoutException) { /* нижче */ }
        w.Dispose();
        var tail = err.IsCompleted ? (await err).Trim().Split('\n').LastOrDefault() : "мовчить";
        log.LogWarning("edge-tts: живий процес не піднявся ({Err}) — репліки йдуть окремими запусками", tail);
        return null;
    }

    /// <summary>
    /// Озвучити одну репліку у файл. (true/false, помилка) — відповідь процесу; null — процес не відповів у час
    /// чи впав (такий процес треба прибрати: наступна відповідь уже була б не на той запит).
    /// </summary>
    public async Task<(bool? Ok, string Err)?> AskAsync(string voice, string text, string rate, string outPath, TimeSpan timeout, CancellationToken ct)
    {
        var request = JsonSerializer.Serialize(new Dictionary<string, string> { ["voice"] = voice, ["rate"] = rate, ["text"] = text, ["out"] = outPath });
        try
        {
            await _p.StandardInput.WriteLineAsync(request.AsMemory(), ct);
            await _p.StandardInput.FlushAsync(ct);
            var line = await _p.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(timeout, ct);
            if (line is null) return null;
            using var doc = JsonDocument.Parse(line);
            var ok = doc.RootElement.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            var err = doc.RootElement.TryGetProperty("err", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
            return (ok, err);
        }
        catch (OperationCanceledException)
        {
            Dispose();          // відповідь на цей запит ще може прийти — і збила б наступний
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        try { if (!_p.HasExited) _p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* уже вийшов */ }
        _p.Dispose();
    }
}

/// <summary>
/// Тривалість mp3 за його кадрами — без ffprobe (той коштує пів секунди на запуск процесу на кожну репліку).
/// MPEG-1/2/2.5 Layer III, CBR і VBR; ID3v2 на початку пропускається, службовий кадр Xing/Info не рахується.
/// 0 — не mp3 або не розібрався (тоді ffprobe).
/// </summary>
public static class Mp3Duration
{
    static readonly int[] KbpsV1 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    static readonly int[] KbpsV2 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    static readonly int[] HzV1 = [44100, 48000, 32000];
    static readonly int[] HzV2 = [22050, 24000, 16000];
    static readonly int[] HzV25 = [11025, 12000, 8000];

    public static double Seconds(ReadOnlySpan<byte> b)
    {
        var i = 0;
        if (b.Length >= 10 && b[0] == 'I' && b[1] == 'D' && b[2] == '3')
            i = 10 + ((b[6] & 0x7f) << 21 | (b[7] & 0x7f) << 14 | (b[8] & 0x7f) << 7 | (b[9] & 0x7f)) + ((b[5] & 0x10) != 0 ? 10 : 0);
        long samples = 0;
        var hz = 0;
        var frames = 0;
        var first = true;
        while (i + 4 <= b.Length)
        {
            if (b[i] != 0xFF || (b[i + 1] & 0xE0) != 0xE0) { i++; continue; }
            var ver = (b[i + 1] >> 3) & 3;           // 3 — MPEG-1, 2 — MPEG-2, 0 — MPEG-2.5, 1 — нема такого
            var layer = (b[i + 1] >> 1) & 3;         // 1 — Layer III
            var br = (b[i + 2] >> 4) & 15;
            var sr = (b[i + 2] >> 2) & 3;
            var pad = (b[i + 2] >> 1) & 1;
            if (ver == 1 || layer != 1 || br is 0 or 15 || sr == 3) { i++; continue; }
            var rate = ver == 3 ? HzV1[sr] : ver == 2 ? HzV2[sr] : HzV25[sr];
            var perFrame = ver == 3 ? 1152 : 576;
            var len = perFrame / 8 * (ver == 3 ? KbpsV1[br] : KbpsV2[br]) * 1000 / rate + pad;
            if (len < 8 || i + len > b.Length) break;
            // перший кадр із «Xing»/«Info» — службовий (заголовок енкодера), звуку в ньому нема
            var info = first && (b.Slice(i, len).IndexOf("Xing"u8) >= 0 || b.Slice(i, len).IndexOf("Info"u8) >= 0);
            first = false;
            if (!info)
            {
                if (hz != 0 && rate != hz) return 0;   // інша частота посеред файла — не наш випадок, хай міряє ffprobe
                hz = rate;
                samples += perFrame;
                frames++;
            }
            i += len;
        }
        return hz == 0 || frames < 2 ? 0 : (double)samples / hz;
    }
}
