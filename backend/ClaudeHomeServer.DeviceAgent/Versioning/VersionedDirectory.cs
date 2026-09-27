using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.DeviceAgent.Versioning;

/// <summary>
/// Версионный каталог (agent-distribution Р7): <c>{root}/versions/{v}/</c> — установленные
/// версии, <c>{root}/staging/</c> — недоустановленные, <c>{root}/active</c> — имя активной.
/// Общий для управляемой копии CLI и самообновления агента.
///
/// - Установка атомарная: всё готовится в staging, затем один <see cref="Directory.Move"/>
///   в <c>versions/</c>. Каталог версии либо есть целиком, либо его нет; прерванная
///   установка оставляет мусор только в staging, а его вычищает <see cref="ResetStaging"/>.
/// - Указатель <c>active</c> пишется через <c>.tmp</c> и замену: у читателя не бывает
///   мгновения без указателя или с половиной имени.
/// - Уборка удаляет всё, кроме версий, которые назвал вызывающий; неудавшееся удаление (на
///   Windows каталог держит запущенный процесс) — не ошибка, повтор при следующей уборке.
/// </summary>
internal sealed class VersionedDirectory
{
    private readonly ILogger _logger;

    public VersionedDirectory(string root, ILogger? logger = null)
    {
        Root = root;
        _logger = logger ?? NullLogger.Instance;
    }

    public string Root { get; }
    public string VersionsDir => Path.Combine(Root, "versions");
    public string StagingDir => Path.Combine(Root, "staging");
    public string ActiveFile => Path.Combine(Root, "active");

    /// <summary>Каталог версии. Имя версии проверяет вызывающий: оно идёт в путь как есть.</summary>
    public string VersionDir(string version) => Path.Combine(VersionsDir, version);

    /// <summary>Создать <c>versions/</c> и чистый <c>staging/</c>: хвосты прерванных установок — мусор.</summary>
    public void ResetStaging()
    {
        Directory.CreateDirectory(VersionsDir);
        TryDeleteDirectory(StagingDir);
        Directory.CreateDirectory(StagingDir);
    }

    /// <summary>Свежий каталог в staging под установку версии.</summary>
    public string CreateStaging(string version)
    {
        var staging = Path.Combine(StagingDir, $"{version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        return staging;
    }

    /// <summary>
    /// Перенести готовый staging в <c>versions/{version}</c>. Прежний каталог версии удаляется:
    /// раз его устанавливают заново, он не признан годной копией.
    /// </summary>
    public string Commit(string staging, string version)
    {
        var final = VersionDir(version);
        TryDeleteDirectory(final);
        Directory.Move(staging, final);
        return final;
    }

    public string? ReadActive()
    {
        try
        {
            return File.Exists(ActiveFile) ? File.ReadAllText(ActiveFile).Trim() : null;
        }
        catch (IOException) { return null; }
    }

    public void WriteActive(string version) => WriteAtomic(ActiveFile, version);

    /// <summary>Атомарная запись файла-указателя: .tmp рядом и замена.</summary>
    public static void WriteAtomic(string file, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>Удалить все версии, кроме тех, что <paramref name="keep"/> велит оставить.</summary>
    public void Sweep(Func<string, bool> keep)
    {
        List<string> dirs;
        try { dirs = Directory.EnumerateDirectories(VersionsDir).ToList(); }
        catch (IOException) { return; }

        foreach (var dir in dirs)
            if (!keep(Path.GetFileName(dir))) TryDeleteDirectory(dir);
    }

    public void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation(e, "Каталог {Dir} пока не удалён, повтор при следующей уборке", dir);
        }
    }
}
