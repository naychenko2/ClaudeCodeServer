namespace ClaudeHomeServer.Services;

// Фоновая уборка временных заметок: раз в минуту проверяет все заметки
// владельцев и удаляет те, у которых срок жизни (expires в frontmatter) истёк.
// При удалении также убирает [[wikilinks]] на удаляемую заметку из других заметок.
public class NoteExpiryService(NotesService notes, ProjectManager projects, IConfiguration config,
    ILogger<NoteExpiryService> log) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);
    private readonly string _notesDir = Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json")))!,
        "notes");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await TickAsync(DateTime.UtcNow); }
                catch (Exception ex) { log.LogError(ex, "Ошибка тика уборки временных заметок"); }
            }
        }
        catch (OperationCanceledException) { /* остановка приложения */ }
    }

    public async Task TickAsync(DateTime nowUtc)
    {
        var seen = new HashSet<string>();

        // 1. Пользователи с личным vault (data/notes/{userId}/)
        if (Directory.Exists(_notesDir))
        {
            foreach (var userDir in Directory.EnumerateDirectories(_notesDir))
            {
                var userId = Path.GetFileName(userDir);
                if (string.IsNullOrEmpty(userId) || !seen.Add(userId)) continue;
                await ProcessUserAsync(userId, nowUtc);
            }
        }

        // 2. Владельцы проектов (у них могут быть заметки в notes/ проекта без личного vault)
        foreach (var p in projects.GetAll())
        {
            if (p.OwnerId is null || !seen.Add(p.OwnerId)) continue;
            await ProcessUserAsync(p.OwnerId, nowUtc);
        }
    }

    private async Task ProcessUserAsync(string userId, DateTime nowUtc)
    {
        try
        {
            var expired = notes.GetExpiredNotes(userId, nowUtc);
            foreach (var note in expired)
            {
                notes.RemoveWikilinksTo(userId, note.Title, note.Id);
                notes.Delete(userId, note.Id);
                log.LogInformation("Временная заметка {NoteId} «{Title}» удалена по истечении срока",
                    note.Id, note.Title);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Не удалось обработать истёкшие заметки пользователя {UserId}", userId);
        }
        await Task.CompletedTask;
    }

    // Публичный метод для тестов: один проход по указанному владельцу
    public Task TickForUserAsync(string userId, DateTime nowUtc) =>
        RemoveExpiredForUserAsync(userId, nowUtc);

    private async Task RemoveExpiredForUserAsync(string userId, DateTime nowUtc)
    {
        var expired = notes.GetExpiredNotes(userId, nowUtc);
        foreach (var note in expired)
        {
            notes.RemoveWikilinksTo(userId, note.Title, note.Id);
            notes.Delete(userId, note.Id);
            log.LogInformation("Временная заметка {NoteId} «{Title}» удалена по истечении срока",
                note.Id, note.Title);
        }
        await Task.CompletedTask;
    }
}
