using System.Text.Json;

namespace ClaudeHomeServer.Services.Backup;

// Состав архива человеческими словами: «12 чатов · 5 персон · 34 задачи · 18 заметок».
// Считается один раз при снятии и живёт в манифесте — виджет и диалог восстановления
// показывают его, не вскрывая архив.
//
// Читаем не типизированными моделями, а длиной массива: сводка не должна падать из-за
// изменения модели и не должна тянуть за собой опции сериализации сторов.
public static class BackupSummaryBuilder
{
    public static BackupSummary Build(string dataDir, long totalBytes) => new()
    {
        Chats = CountArray(Path.Combine(dataDir, "sessions.json")),
        Personas = CountArray(Path.Combine(dataDir, "personas.json")),
        Tasks = CountArray(Path.Combine(dataDir, "tasks.json")),
        Projects = CountArray(Path.Combine(dataDir, "projects.json")),
        Users = CountUsers(Path.Combine(dataDir, "users.json")),
        Notes = CountNotes(Path.Combine(dataDir, "notes")),
        TotalBytes = totalBytes,
    };

    public static List<BackupOwner> ReadOwners(string dataDir)
    {
        var owners = new List<BackupOwner>();
        try
        {
            var path = Path.Combine(dataDir, "users.json");
            if (!File.Exists(path)) return owners;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("users", out var users)) return owners;

            foreach (var user in users.EnumerateArray())
            {
                owners.Add(new BackupOwner
                {
                    Id = user.TryGetProperty("Id", out var id) ? id.GetString() ?? "" : "",
                    Username = user.TryGetProperty("Username", out var name) ? name.GetString() ?? "" : "",
                });
            }
        }
        catch { /* сводка не обязана падать из-за формата */ }
        return owners;
    }

    private static int CountArray(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.GetArrayLength()
                : 0;
        }
        catch { return 0; }
    }

    private static int CountUsers(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("users", out var users)
                   && users.ValueKind == JsonValueKind.Array
                ? users.GetArrayLength()
                : 0;
        }
        catch { return 0; }
    }

    private static int CountNotes(string notesDir)
    {
        try
        {
            return Directory.Exists(notesDir)
                ? Directory.EnumerateFiles(notesDir, "*.md", SearchOption.AllDirectories).Count()
                : 0;
        }
        catch { return 0; }
    }
}
