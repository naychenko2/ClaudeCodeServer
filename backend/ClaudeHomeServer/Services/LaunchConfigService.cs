using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

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

    private static string PathFor(Project project) =>
        FileService.SafeJoinPublic(project.RootPath, ".claude/launch.json");

    /// <summary>Прочитать конфигурации. Файла нет / битый — пустой список.</summary>
    public async Task<List<LaunchConfigEntry>> ReadAsync(Project project)
    {
        try
        {
            var path = PathFor(project);
            if (!File.Exists(path)) return [];
            var json = await File.ReadAllTextAsync(path);
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
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ошибка чтения .claude/launch.json проекта {ProjectId}", project.Id);
            return [];
        }
    }

    /// <summary>Записать конфигурации в <c>{configurations:[...]}</c>, создав <c>.claude/</c> при необходимости.</summary>
    public async Task WriteAsync(Project project, List<LaunchConfigEntry> configs)
    {
        var path = PathFor(project);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new { configurations = configs }, WriteOpts);
        await File.WriteAllTextAsync(path, json);
    }
}
