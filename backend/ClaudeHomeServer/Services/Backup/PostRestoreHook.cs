using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Backup;

// Разовая доводка после восстановления. Работает файлами напрямую, до того как сторы
// подняты: WorkspaceKnowledgeStore читает свой файл в конструкторе, и правку через DI
// он бы уже не увидел.
//
// Что делаем: обнуляем карту документов «файл проекта ↔ документ Dify». Восстановленная
// карта ссылается на DocId, которых в датасете может не быть, а сам датасет ушёл вперёд.
// Пустая карта включает штатный BootstrapDocsAsync — он перечитывает датасет с натуры,
// усыновляет документы по именам и схлопывает дубли.
//
// Сторы заметок и памяти персон намеренно НЕ трогаем: у них дифф-синк по хешам, и он сам
// приведёт датасеты к восстановленному состоянию (расхождение → переиндексация). Цена —
// документы, созданные в Dify после снятия снапшота, останутся сиротами.
public static class PostRestoreHook
{
    public static void RunIfNeeded(string dataDir, ILogger? log = null)
    {
        var marker = Path.Combine(dataDir, BackupPaths.PostRestoreMarker);
        if (!File.Exists(marker)) return;

        try
        {
            var reset = ResetWorkspaceDocs(Path.Combine(dataDir, "workspace-knowledge.json"));
            log?.LogInformation(
                "После восстановления: сброшены карты документов у {Count} рабочих папок — " +
                "базы знаний пересоберутся при первой синхронизации", reset);
        }
        catch (Exception ex)
        {
            // Не роняем старт: без сброса семантика останется неточной, но инстанс живой
            log?.LogWarning(ex, "Не удалось сбросить карты баз знаний после восстановления");
        }
        finally
        {
            try { File.Delete(marker); } catch { /* повторный проход безвреден */ }
        }
    }

    private static int ResetWorkspaceDocs(string storePath)
    {
        if (!File.Exists(storePath)) return 0;

        // Правим как дерево, а не через модель: формат стора может обрасти полями,
        // и лишний круг сериализации терял бы то, чего мы не знаем
        var root = JsonNode.Parse(File.ReadAllText(storePath));
        if (root is not JsonObject entries) return 0;

        var count = 0;
        foreach (var (_, value) in entries)
        {
            if (value is not JsonObject wk) continue;
            if (!wk.ContainsKey("Docs") && !wk.ContainsKey("docs")) continue;

            wk.Remove("Docs");
            wk.Remove("docs");
            count++;
        }

        if (count > 0)
            File.WriteAllText(storePath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return count;
    }
}
