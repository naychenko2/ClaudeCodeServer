using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

public class AppSettingsService
{
    private readonly string _storePath;
    private readonly string _configDefault;
    private AppSettings _settings = new();
    private readonly object _lock = new();

    public AppSettingsService(IConfiguration config)
    {
        var projectsPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _storePath = Path.Combine(Path.GetDirectoryName(projectsPath)!, "app-settings.json");
        _configDefault = config["DefaultProjectsPath"] ?? "";
        Load();
    }

    public AppSettings Get()
    {
        lock (_lock)
        {
            var path = string.IsNullOrWhiteSpace(_settings.DefaultProjectsPath)
                ? _configDefault
                : _settings.DefaultProjectsPath;
            return new AppSettings
            {
                DefaultProjectsPath = path,
                ClaudeBilling = string.IsNullOrWhiteSpace(_settings.ClaudeBilling) ? "subscription" : _settings.ClaudeBilling,
            };
        }
    }

    public AppSettings Save(AppSettings settings)
    {
        lock (_lock)
        {
            _settings = settings;
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
        }
        catch { /* первый запуск или повреждённый файл */ }
    }
}
