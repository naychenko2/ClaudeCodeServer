namespace ClaudeHomeServer.Services.Backup;

// Всё, что нужно снапшоту и восстановлению, собранное в одном месте: и хостовому сервису
// (через DI), и CLI-режимам (через голый IConfiguration — там DI ещё нет).
public class BackupContext
{
    public required string DataDir { get; init; }
    public required string BaseDirectory { get; init; }
    public required string BackupDir { get; init; }
    public required string SecretsDir { get; init; }

    /// <summary>
    /// Отпечаток инстанса, если он уже есть на диске; null — папки data ещё нет либо
    /// файла в ней нет. Читаем, а НЕ создаём: создание прямо здесь делало бы недостижимой
    /// ветку «усыновить id из архива» в восстановлении — на чистой машине (умер диск,
    /// свежий деплой) успел бы сгенерироваться случайный id, и штатный disaster recovery
    /// требовал бы --force наравне с опасным восстановлением чужого архива.
    /// Материализуется только на пути снапшота (<see cref="EnsureInstanceId"/>).
    /// </summary>
    public string? InstanceId { get; init; }

    /// <summary>Получить id инстанса, создав его при отсутствии. Только для снапшота.</summary>
    public string EnsureInstanceId() => InstanceId ?? InstanceIdentity.GetOrCreate(DataDir);
    public string Environment { get; init; } = "";
    public string DifyNamespace { get; init; } = "";
    public string AppVersion { get; init; } = "";
    // Имя docker-контейнера песочницы: перед восстановлением его надо погасить —
    // data/sandbox-* смонтированы внутрь и держат каталог
    public string SandboxContainerName { get; init; } = "";

    public static BackupContext FromConfiguration(IConfiguration config)
    {
        var options = BackupOptions.From(config);
        var dataPath = config["DataPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        // DataPath указывает на projects.json, а не на папку — общая договорённость всех сторов
        var dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))
            ?? Path.Combine(AppContext.BaseDirectory, "data");

        var baseDir = AppContext.BaseDirectory;

        return new BackupContext
        {
            DataDir = dataDir,
            BaseDirectory = baseDir,
            BackupDir = BackupPaths.ResolveBackupDir(options.Path, dataDir),
            SecretsDir = BackupPaths.ResolveSecretsDir(options.SecretsPath, baseDir),
            InstanceId = InstanceIdentity.TryRead(dataDir),
            Environment = config["ASPNETCORE_ENVIRONMENT"]
                ?? System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "",
            DifyNamespace = config["Dify:Namespace"] ?? "",
            AppVersion = typeof(BackupContext).Assembly.GetName().Version?.ToString() ?? "",
            SandboxContainerName = config["Sandbox:ContainerName"] ?? "cc-sandbox",
        };
    }
}
