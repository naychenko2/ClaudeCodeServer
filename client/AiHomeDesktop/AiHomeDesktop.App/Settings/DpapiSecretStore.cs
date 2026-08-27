using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiHomeDesktop.Core.Abstractions;
using AiHomeDesktop.Core.Protocol;

namespace AiHomeDesktop.App.Settings;

/// <summary>
/// Device-токен под DPAPI CurrentUser (ADR-008, «Аутентификация и транспорт»): блоб
/// расшифровывается только этой учётной записью на этой машине, копирование файла на
/// другую машину его не оживляет.
///
/// Здесь лежит ТОЛЬКО учётка самого устройства. API-ключ владельца и его веб-JWT сюда не
/// попадают никогда: у клиента их нет и быть не должно — потребуй решение обратного, это
/// была бы ошибка проектирования, а не деталь реализации.
///
/// Энтропия — фиксированная строка продукта: она не секрет, а разделитель пространства
/// блобов, чтобы чужой DPAPI-блоб этой учётки случайно не разобрался как наш.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AiHomeDesktop/device-credentials/v1");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;

    public DpapiSecretStore(string? filePath = null) => _filePath = filePath ?? ClientPaths.CredentialsFile;

    /// <summary>Учётные данные устройства либо null: файла нет, он битый или расшифровать нечем.</summary>
    public DeviceCredentials? Read()
    {
        try
        {
            if (!File.Exists(_filePath)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_filePath), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<DeviceCredentials>(plain, Json);
        }
        catch (Exception)
        {
            // Нерасшифруемый блоб равен отсутствию сопряжения: человек пройдёт сопряжение
            // заново, а прежний токен на сервере умрёт при следующей регистрации машины.
            return null;
        }
    }

    public void Save(DeviceCredentials credentials)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(credentials, Json);
        File.WriteAllBytes(_filePath, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_filePath)) File.Delete(_filePath);
        }
        catch (Exception)
        {
            // Файл занят — токен всё равно мёртв: сервер отзывает устройство своей стороной.
        }
    }
}
