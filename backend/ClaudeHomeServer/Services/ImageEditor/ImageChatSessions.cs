using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services;

// Адаптер чата картинки над SessionManager (ADR-018 §1, §10.1): ручки чатов — в модуле
// редактора, сессии создаёт и правит только ядро. Правило выбора собеседника и состав
// AutoAllowTools живут здесь, чтобы модуль не знал ни персон, ни провижна ассистента.
public sealed class ImageChatSessions(
    SessionManager sessions,
    PersonaManager personas,
    DefaultAssistantProvisioner provisioner) : IImageChatSessions
{
    public async Task<ImageChatCreateOutcome> CreateAsync(string ownerId, Project project, string sourcePath,
        string? personaId, CancellationToken ct)
    {
        string? chosen;
        if (!string.IsNullOrWhiteSpace(personaId))
        {
            // Собеседник из композера важнее правила. Чужая и несуществующая персона
            // неотличимы: иначе ручка выдавала бы существование чужих персон
            chosen = personas.Get(personaId.Trim(), ownerId)?.Id;
            if (chosen is null)
                return ImageChatCreateOutcome.Fail(ImageChatCreateOutcome.InvalidRequest, "Собеседник не найден");
        }
        else
        {
            // То же правило, что у REST создания чата: руководитель проекта, а без него —
            // личный ассистент. Резолв в живую персону: сирота не подменяет правило
            chosen = project.DefaultPersonaId is { } leadId ? personas.Get(leadId, ownerId)?.Id : null;
            chosen ??= (await provisioner.EnsureAsync(ownerId, ct))?.Id;
            if (chosen is null)
                return ImageChatCreateOutcome.Fail(ImageChatCreateOutcome.Unavailable,
                    "Не удалось подобрать собеседника для чата");
        }

        var session = await sessions.CreateAsync(project.Id, ClaudeMode.AcceptEdits,
            name: ImageChatDefaults.ChatName(sourcePath), personaId: chosen,
            imageChat: new SessionImageChat { CurrentPath = sourcePath },
            autoAllowTools: ImageChatDefaults.AutoAllowTools);
        return ImageChatCreateOutcome.Ok(session);
    }

    public Session? SetPath(string sessionId, string path) => sessions.SetImageChatPath(sessionId, path);

    public Task<Session?> MoveToFileAsync(string sessionId, string path) =>
        sessions.MoveImageChatToFileAsync(sessionId, path);

    public Session? RewritePaths(string sessionId, string currentPath, IReadOnlyList<string> lineage) =>
        sessions.RewriteImageChatPaths(sessionId, currentPath, lineage);

    public Task<Session?> AppendLaunchAsync(string sessionId, Protocol.StoredImageLaunchMessage launch) =>
        sessions.AppendImageLaunchAsync(sessionId, launch);
}
