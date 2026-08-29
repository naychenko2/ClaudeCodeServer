using System.Collections.Concurrent;
using System.Text.Json;

namespace ClaudeHomeServer.Services;

/// <summary>Что мы помним о прошлом запуске сервиса: где слушал и каким процессом был.</summary>
public sealed record RememberedRun(int Port, int Pid);

/// <summary>
/// Последний известный порт сервиса (<c>data/dev-server-ports.json</c>).
///
/// Зачем. Реестр запущенных процессов живёт в памяти, а сами дев-серверы переживают
/// перезапуск продукта — в том числе выкатку на бой. После неё панель показывала сервис
/// остановленным при живом процессе, человек жал «Запустить», и запуск падал с
/// «порт уже занят» — своим же вчерашним процессом.
///
/// Проба «поднят снаружи» этого не ловила: она щупает только порты, известные из
/// конфигурации, а порт сплошь и рядом там не значится — автопорт, порт из вывода,
/// неподдерживаемый тип конфигурации. Память портов закрывает ровно эту дыру.
///
/// Помним НЕ факт запуска, а последний увиденный номер порта: живость всё равно
/// проверяется соединением. Протухшая запись поэтому безобидна — порт просто не ответит.
/// </summary>
public sealed class DevServerPortMemory
{
    private readonly string _path;
    private readonly ILogger<DevServerPortMemory> _log;
    private readonly ConcurrentDictionary<string, RememberedRun> _ports = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public DevServerPortMemory(IConfiguration config, ILogger<DevServerPortMemory> log)
    {
        _log = log;
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "dev-server-ports.json");
        Load();
    }

    private static string Key(string projectId, string serviceId) => $"{projectId}:{serviceId}";

    /// <summary>
    /// Запомнить, где и каким процессом поднялся сервис.
    ///
    /// PID нужен, чтобы после перезапуска продукта отличить СВОЙ осиротевший процесс от
    /// постороннего, занявшего тот же порт: своего гасим по кнопке, чужого — только с
    /// подтверждением человека.
    /// </summary>
    public void Remember(string projectId, string serviceId, int port, int pid)
    {
        if (port <= 0) return;
        var key = Key(projectId, serviceId);
        var run = new RememberedRun(port, pid);
        if (_ports.TryGetValue(key, out var known) && known == run) return;
        _ports[key] = run;
        Save();
    }

    /// <summary>Последний известный порт сервиса, либо null.</summary>
    public int? Get(string projectId, string serviceId) =>
        _ports.TryGetValue(Key(projectId, serviceId), out var run) ? run.Port : null;

    /// <summary>Что помним о прошлом запуске целиком (порт + процесс).</summary>
    public RememberedRun? GetRun(string projectId, string serviceId) =>
        _ports.TryGetValue(Key(projectId, serviceId), out var run) ? run : null;

    /// <summary>
    /// Забыть порт. Зовётся при штатной остановке: процесс погашен нами, и притворяться,
    /// что там кто-то может слушать, незачем.
    /// </summary>
    public void Forget(string projectId, string serviceId)
    {
        if (_ports.TryRemove(Key(projectId, serviceId), out _)) Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var items = JsonSerializer.Deserialize<Dictionary<string, RememberedRun>>(File.ReadAllText(_path), JsonOpts);
            if (items is null) return;
            foreach (var (k, v) in items) _ports[k] = v;
        }
        catch (Exception ex)
        {
            // Битый файл не должен ронять старт: худшее следствие — сервис покажется
            // остановленным, как было до этой памяти
            _log.LogWarning(ex, "Память портов не прочитана ({Path}), начинаем с пустой", _path);
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_ports.ToDictionary(p => p.Key, p => p.Value), JsonOpts));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Память портов не сохранена ({Path})", _path);
        }
    }
}
