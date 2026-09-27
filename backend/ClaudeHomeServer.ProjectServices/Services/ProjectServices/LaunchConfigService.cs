using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.ProjectServices;

/// <summary>
/// Одна конфигурация запуска из <c>.claude/launch.json</c> (формат Claude Desktop).
/// </summary>
public sealed class LaunchConfigEntry
{
    public string? Name { get; set; }
    public string? RuntimeExecutable { get; set; }   // напр. "npm", "dotnet", "node"
    public string[]? RuntimeArgs { get; set; }        // напр. ["run","dev"]
    public string? Program { get; set; }              // альтернатива: файл скрипта ("server.js")
    public string[]? Args { get; set; }
    public int? Port { get; set; }
    public bool? AutoPort { get; set; }
    public string? Cwd { get; set; }                  // относительно корня проекта
    public Dictionary<string, string>? Env { get; set; }
}

/// <summary>
/// Чтение/запись <c>.claude/launch.json</c> в корне проекта — конфиг сервисов Preview,
/// совместимый по формату с Claude Code Desktop.
/// </summary>
public sealed class LaunchConfigService
{
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ILogger<LaunchConfigService> _log;

    public LaunchConfigService(ILogger<LaunchConfigService> log) => _log = log;

    private const string RelativePath = ".claude/launch.json";

    // Защита пути через Core-примитив SafePath.Join: ссылка на FileService — это ссылка
    // на чужую вертикаль, сторож границ её ловит. CLAUDE.md «Соглашения».
    private static string PathFor(Project project) =>
        SafePath.Join(project.RootPath, RelativePath);

    /// <summary>Прочитать конфигурации. Файла нет / битый — пустой список.</summary>
    /// <param name="files">Шов файлов проекта; null — папка на этой машине напрямую (сервер).</param>
    public async Task<List<LaunchConfigEntry>> ReadAsync(Project project, IProjectFiles? files = null)
    {
        try
        {
            string json;
            if (files is null)
            {
                var path = PathFor(project);
                if (!File.Exists(path)) return [];
                json = await File.ReadAllTextAsync(path);
            }
            else
            {
                try { json = await files.ReadFileAsync(project, RelativePath); }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return []; }
            }
            return Parse(json);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ошибка чтения .claude/launch.json проекта {ProjectId}", project.Id);
            return [];
        }
    }

    private static List<LaunchConfigEntry> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        var root = doc.RootElement;

        // Формат Claude Desktop: { "configurations": [ ... ] }.
        // Терпимо принимаем и одиночный объект, и голый массив.
        JsonElement arr = root;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("configurations", out var configs))
            arr = configs;

        var list = new List<LaunchConfigEntry>();
        if (arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                var e = el.Deserialize<LaunchConfigEntry>(ReadOpts);
                if (e != null) list.Add(e);
            }
        }
        else if (arr.ValueKind == JsonValueKind.Object)
        {
            var e = arr.Deserialize<LaunchConfigEntry>(ReadOpts);
            if (e != null) list.Add(e);
        }
        return list;
    }

    /// <summary>Записать конфигурации в <c>{configurations:[...]}</c>, создав <c>.claude/</c> при необходимости.</summary>
    public async Task WriteAsync(Project project, List<LaunchConfigEntry> configs, IProjectFiles? files = null)
    {
        var json = JsonSerializer.Serialize(new { configurations = configs }, WriteOpts);
        if (files is not null)
        {
            await files.CreateDirectoryAsync(project, ".claude");
            await files.WriteFileAsync(project, RelativePath, json);
            return;
        }
        var path = PathFor(project);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json);
    }
}
