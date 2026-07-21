using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

public class AppSettingsService
{
    private readonly string _storePath;
    // Источник истины для DefaultProjectsPath — ТОЛЬКО конфиг (appsettings.Local.json).
    // В рантайм-файле этот путь НЕ хранится: он не редактируется через UI, а лишь протекал бы
    // туда как побочный эффект сохранения других настроек (Save берёт весь AppSettings).
    // Осевшее устаревшее значение после смены окружения (напр. докеровский /projects при
    // переезде на хост) тихо затеняло бы конфиг и ломало резолв домашних папок — поэтому
    // читаем путь всегда из конфига, а в файл пишем лишь ClaudeBilling.
    private readonly string _configDefault;
    private AppSettings _settings = new();
    private readonly object _lock = new();

    public AppSettingsService(IConfiguration config, ILogger<AppSettingsService>? log = null)
    {
        var projectsPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _storePath = Path.Combine(Path.GetDirectoryName(projectsPath)!, "app-settings.json");
        _configDefault = config["DefaultProjectsPath"] ?? "";
        Load();
        log?.LogInformation(
            "AppSettings: DefaultProjectsPath = «{Path}» (источник: конфиг DefaultProjectsPath)",
            string.IsNullOrWhiteSpace(_configDefault) ? "<не задан>" : _configDefault);
    }

    public AppSettings Get()
    {
        lock (_lock)
        {
            return new AppSettings
            {
                DefaultProjectsPath = _configDefault,
                ClaudeBilling = string.IsNullOrWhiteSpace(_settings.ClaudeBilling) ? "subscription" : _settings.ClaudeBilling,
            };
        }
    }

    public AppSettings Save(AppSettings settings)
    {
        lock (_lock)
        {
            // Персистим только редактируемые поля; DefaultProjectsPath намеренно НЕ сохраняем
            // (иначе резолвнутое значение осело бы в файле и затенило конфиг после смены среды).
            _settings = new AppSettings { ClaudeBilling = settings.ClaudeBilling };
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            File.WriteAllText(_storePath, JsonSerializer.Serialize(_settings));
        }
        return Get();
    }

    private void Load()
    {
        if (!File.Exists(_storePath)) return;
        try
        {
            var json = File.ReadAllText(_storePath);
            _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new();
            // Гигиена: устаревший DefaultProjectsPath мог осесть здесь раньше — обнуляем, чтобы
            // он не читался и не сериализовался обратно (Get всё равно берёт путь из конфига).
            _settings.DefaultProjectsPath = "";
        }
        catch { /* первый запуск или повреждённый файл */ }
    }
}
