using System.Text.Json;
using ClaudeCodeServer.Models;

namespace ClaudeCodeServer.Services;

public class AppSettingsService
{
    private readonly string _storePath;
    private AppSettings _settings = new();
    private readonly object _lock = new();

    public AppSettingsService(IConfiguration config)
    {
        var projectsPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _storePath = Path.Combine(Path.GetDirectoryName(projectsPath)!, "app-settings.json");
        Load();
    }

    public AppSettings Get()
    {
        lock (_lock) return new AppSettings { DefaultProjectsPath = _settings.DefaultProjectsPath };
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
