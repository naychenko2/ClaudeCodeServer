using System.Text.Json;

namespace AiHomeDesktop.App.Settings;

/// <summary>
/// Настройки клиента: адрес сервера и имя устройства. Секретов здесь нет — device-токен
/// живёт отдельно, под DPAPI (<see cref="DpapiSecretStore"/>).
/// </summary>
public sealed class ClientSettings
{
    /// <summary>Адрес веб-морды и API («https://home:5000»). Пусто — клиент не сопряжён.</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Имя устройства: им человек адресует руки в чате («на home открой…»).</summary>
    public string? DeviceName { get; set; }

    /// <summary>
    /// Что разрешено открывать вызовом desktop_open: пути приложений и папок, отмеченные
    /// человеком. Ссылки http/https разрешены как класс и в списке не нуждаются, оболочки
    /// вычеркнуты всегда. Экрана управления списком в этой версии нет — файл правится руками.
    /// </summary>
    public List<string> OpenAllowList { get; set; } = [];
}

/// <summary>
/// Настройки на диске (%APPDATA%\AiHomeDesktop\settings.json). Файл крохотный и правится
/// человеком руками, поэтому пишется целиком и с отступами.
///
/// Битый файл не роняет клиент: настройки — не данные, их дешевле забыть, чем упереться
/// в окно с ошибкой на старте.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _filePath;

    public SettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? ClientPaths.SettingsFile;
        Current = Load();
    }

    /// <summary>Текущие настройки. Меняются через <see cref="Save"/> — иначе не доедут на диск.</summary>
    public ClientSettings Current { get; private set; }

    public void Save(ClientSettings settings)
    {
        Current = settings;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, Json));
        }
        catch (Exception)
        {
            // Не сохранилось — клиент продолжает работать на том, что в памяти: адрес
            // сервера человек введёт заново, а канал держится device-токеном.
        }
    }

    private ClientSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new ClientSettings();
            return JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(_filePath), Json)
                   ?? new ClientSettings();
        }
        catch (Exception)
        {
            return new ClientSettings();
        }
    }
}
