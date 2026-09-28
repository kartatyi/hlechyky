using System.Text.Json;

namespace Hlechyky.Tests.Support;

/// <summary>
/// Серіалізує вид гри так само, як його побачить браузер (camelCase, як у SignalR), і повертає JsonElement:
/// тести перевіряють форму на дроті, а не C#-типи.
/// </summary>
public static class Views
{
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    public static JsonElement Json(object? view) => JsonSerializer.SerializeToElement(view, Wire);

    public static string Text(object? view) => JsonSerializer.Serialize(view, Wire);

    static readonly JsonSerializerOptions WireUtf8 = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Скільки байт вид займе на дроті. <see cref="Text"/> екранує кирилицю як \uXXXX і завищує розмір утричі, а SignalR
    /// шле звичайний UTF-8 — для замірів розміру видів і кадрів беріть це.
    /// </summary>
    public static int WireBytes(object? view) => JsonSerializer.SerializeToUtf8Bytes(view, WireUtf8).Length;

    /// <summary>Payload для Game.Act так, як його передасть хаб: анонімний об'єкт → JsonElement.</summary>
    public static JsonElement Payload(object? payload) => payload is JsonElement e ? e : JsonSerializer.SerializeToElement(payload, Wire);

    public static bool Has(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out _);
}
