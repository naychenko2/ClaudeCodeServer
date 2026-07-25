namespace ClaudeHomeServer.Services.Backup;

// Отпечаток инстанса: data/instance-id.txt. Нужен, чтобы архив одного инстанса нельзя
// было по невнимательности раскатать в другой (у Гриши dev и prod на одной машине,
// у брата — свой; RootPath проектов абсолютные, пользователи разные, датасеты Dify общие).
public static class InstanceIdentity
{
    public const string FileName = "instance-id.txt";

    public static string PathIn(string dataDir) => Path.Combine(dataDir, FileName);

    /// <summary>Прочитать id инстанса; null — файла нет (свежая установка или пустая data).</summary>
    public static string? TryRead(string dataDir)
    {
        var path = PathIn(dataDir);
        if (!File.Exists(path)) return null;
        try
        {
            var value = File.ReadAllText(path).Trim();
            return value.Length == 0 ? null : value;
        }
        catch { return null; }
    }

    /// <summary>Прочитать id, а при отсутствии — создать новый.</summary>
    public static string GetOrCreate(string dataDir)
    {
        var existing = TryRead(dataDir);
        if (existing is not null) return existing;

        var id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(PathIn(dataDir), id);
        return id;
    }

    /// <summary>
    /// Принять id из восстанавливаемого архива. Вызывается, когда локального файла НЕТ —
    /// это восстановление на чистой машине (умер диск, свежий деплой). Генерировать новый
    /// id в этот момент нельзя: он не совпал бы с архивом, и штатный disaster-recovery
    /// требовал бы --force, то есть той же кнопки, что и опасное кросс-инстансное
    /// восстановление. Усыновление разводит эти два случая.
    /// </summary>
    public static void Adopt(string dataDir, string instanceId)
    {
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(PathIn(dataDir), instanceId);
    }

    // Короткий префикс для имени файла архива — чтобы в общей облачной папке
    // архивы dev и prod различались глазом.
    public static string Short(string instanceId) =>
        instanceId.Length <= 8 ? instanceId : instanceId[..8];
}
