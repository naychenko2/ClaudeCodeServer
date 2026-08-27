using System.Text.Json;

namespace AiHomeDesktop.Core.Policies;

/// <summary>
/// Сколько байт уехало в модель. Считается по base64-полю кадра: человеку в ленте важно
/// видеть не только «ушло», но и сколько именно — кадр внутри сеанса уходит без отдельного
/// нажатия, и это единственное место, где его вес виден.
/// </summary>
public static class PayloadSize
{
    /// <summary>Человеческий размер полезной нагрузки либо null, если кадра в ней нет.</summary>
    public static string? Describe(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.Object
            || !image.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
            return null;

        // base64 раздувает байты на треть — считаем обратно.
        var bytes = (long)(data.GetString()?.Length ?? 0) * 3 / 4;
        return bytes < 1024 ? $"{bytes} Б"
            : bytes < 1024 * 1024 ? $"{bytes / 1024} КБ"
            : $"{bytes / (1024.0 * 1024):0.0} МБ";
    }
}
