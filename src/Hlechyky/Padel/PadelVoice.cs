using Hlechyky.Games;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Hlechyky.Padel;

/// <summary>
/// Голос табло: Глек (Остап, той самий edge-tts і кеш, що в «Своїй грі»). Кліп віддаємо лише для фраз, які сервер
/// сам склав, — інакше <c>/api/padel/voice/…</c> став би озвучкою будь-чого за запитом.
/// </summary>
public interface IPadelVoice
{
    bool On { get; }
    /// <summary>URL готового кліпу або null (тоді фразу поставлено в чергу першою).</summary>
    string? Clip(string text);
    /// <summary>Озвучити наперед (фрази можливих наступних очок, імена команд, службові).</summary>
    void Want(IEnumerable<string> texts, bool urgent = false);
    /// <summary>Файл кліпу за id, лише для своїх фраз.</summary>
    string? File(string id);
}

public sealed class TtsPadelVoice(TtsService tts, IOptionsMonitor<PadelOptions> options) : IPadelVoice
{
    const int Memory = 3000;
    readonly object _lock = new();
    readonly Dictionary<string, string> _texts = [];
    readonly Queue<string> _order = new();

    static string VoiceName => Games.Impl.SvoyaPacks.VoiceName;
    public bool On => options.CurrentValue.Voice && tts.Enabled;

    string Remember(string text)
    {
        var id = PadelScore.VoiceId(text);
        lock (_lock)
        {
            if (_texts.TryAdd(id, text.Trim()))
            {
                _order.Enqueue(id);
                while (_order.Count > Memory) _texts.Remove(_order.Dequeue());
            }
        }
        return id;
    }

    public string? Clip(string text)
    {
        if (!On || string.IsNullOrWhiteSpace(text)) return null;
        var id = Remember(text);
        if (tts.TryGet(VoiceName, text) is not null) return $"/api/padel/voice/{id}.mp3";
        tts.Enqueue(VoiceName, [text], urgent: true);
        return null;
    }

    public void Want(IEnumerable<string> texts, bool urgent = false)
    {
        if (!On) return;
        var list = texts.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
        foreach (var t in list) Remember(t);
        tts.Enqueue(VoiceName, list, urgent);
    }

    public string? File(string id)
    {
        string? text;
        lock (_lock) _texts.TryGetValue(id, out text);
        return text is null ? null : tts.TryGet(VoiceName, text)?.FilePath;
    }
}

/// <summary>
/// Пінг лобі сайту: <c>wire.Lobby(Summary())</c> не частіше раз на секунду. Друге очко за пів секунди не губиться —
/// воно приїде одним відкладеним пінгом з уже свіжим видом.
/// </summary>
public sealed class PadelPing(IPadelWire wire, IClock clock)
{
    readonly object _lock = new();
    DateTimeOffset _sent = DateTimeOffset.MinValue;
    bool _pending;
    public Func<object>? Summary { get; set; }

    public void Ping()
    {
        TimeSpan wait;
        lock (_lock)
        {
            if (_pending) return;
            wait = _sent + TimeSpan.FromSeconds(1) - clock.UtcNow;
            if (wait > TimeSpan.Zero) _pending = true;
            else _sent = clock.UtcNow;
        }
        if (wait <= TimeSpan.Zero) Send();
        else _ = Later(wait);
    }

    async Task Later(TimeSpan wait)
    {
        await Task.Delay(wait > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait);
        lock (_lock) { _pending = false; _sent = clock.UtcNow; }
        Send();
    }

    void Send()
    {
        try { if (Summary is { } s) wire.Lobby(s()); } catch (Exception) { /* лобі — не головне; наступний пінг принесе */ }
    }
}

/// <summary>JSON для записів у базі Падельні: camelCase, як на дроті, null не пишемо.</summary>
public static class PadelJson
{
    public static readonly JsonSerializerOptions O = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write<T>(T v) => JsonSerializer.Serialize(v, O);
    public static T? Read<T>(string s) => JsonSerializer.Deserialize<T>(s, O);
}
