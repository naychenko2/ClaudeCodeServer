using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Versioning;

namespace ClaudeHomeServer.Services;

// «Обсудить с Claude» из редактора картинок (ADR-017, раздел 6). Новой механики нет:
// новый чат проекта + вложение в .cc-attachments + обычное сообщение пользователя. Ответ
// фронт читает существующими событиями чата по возвращённому sessionId.
//
// В текущий открытый чат не пишем: он может быть занят чужой работой. Внутри одного сеанса
// редактора чат переиспользуется — фронт присылает его id; чужой, удалённый или из другого
// проекта id молча даёт новый чат, а не ошибку.
public sealed class ImageDiscussService(
    SessionManager sessions,
    PersonaManager personas,
    DefaultAssistantProvisioner provisioner,
    FileService files,
    ILogger<ImageDiscussService> log)
{
    public const int MaxTextChars = 20_000;

    public async Task<ImageEditCallResult<ImageDiscussResultDto>> StartAsync(
        string ownerId, Project project, ImageDiscussInput input, CancellationToken ct)
    {
        var text = input.Text?.Trim() ?? "";
        if (text.Length == 0) return Invalid("Напишите, что обсудить");
        if (text.Length > MaxTextChars) return Invalid($"Текст длиннее {MaxTextChars} символов");
        var ext = ImageFormatSniffer.DetectExtension(input.Annotated.Bytes);
        if (ext is null) return Invalid("Размеченная копия должна быть картинкой");

        var session = Reusable(ownerId, project.Id, input.SessionId);
        var created = session is null;
        if (session is null)
        {
            // Правило «новый чат человека — только с персоной» то же, что у REST создания
            // чата: руководитель проекта, а без него — личный ассистент
            var personaId = project.DefaultPersonaId is { } leadId ? personas.Get(leadId, ownerId)?.Id : null;
            personaId ??= (await provisioner.EnsureAsync(ownerId, ct))?.Id;
            if (personaId is null)
                return ImageEditCallResult<ImageDiscussResultDto>.Fail(ImageEditErrorCodes.Unavailable,
                    "Не удалось подобрать собеседника для чата");
            session = await sessions.CreateAsync(project.Id, ClaudeMode.AcceptEdits,
                name: ChatName(input), personaId: personaId);
        }

        // Вложение — как у загрузки в чат: своя подпапка с GUID, игнор вложений в git
        var attachment = $"{FileService.AttachmentsDir}/{Guid.NewGuid():N}/annotated{ext}";
        try { AttachmentsGitExclude.Ensure(project.RootPath); }
        catch (Exception ex) { log.LogWarning(ex, "Не удалось записать игнор вложений для {Root}", project.RootPath); }
        files.WriteFileBytes(project.RootPath, attachment, input.Annotated.Bytes);

        var attached = new List<string> { attachment };
        if (input.SourcePath is { Length: > 0 } source) attached.Add(source);

        // Папку персонажа вложением не отдаём (вложения инлайнятся как файлы) — только путём
        if (input.Character is { } character)
            text += $"\n\nПерсонаж «{character.Name}»: папка {character.Path}/ (character.json и фото лица).";

        await sessions.SendMessageAsync(session.Id, text, attached, cause: SessionManager.DeliveryCause.User);
        return ImageEditCallResult<ImageDiscussResultDto>.Ok(new ImageDiscussResultDto(session.Id, created, attached));
    }

    private Session? Reusable(string ownerId, string projectId, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        var session = sessions.GetOwned(sessionId, ownerId);
        return session is not null && session.ProjectId == projectId ? session : null;
    }

    private static string ChatName(ImageDiscussInput input) =>
        input.SourcePath is { Length: > 0 } source ? $"Картинка: {Path.GetFileName(source)}" : "Картинка";

    private static ImageEditCallResult<ImageDiscussResultDto> Invalid(string error) =>
        ImageEditCallResult<ImageDiscussResultDto>.Fail(ImageEditErrorCodes.InvalidRequest, error);
}

// SourcePath уже проверен контроллером: внутри проекта и существует. Character — Path
// папки персонажа от корня проекта.
public sealed record ImageDiscussInput(
    string Text,
    ImageBytes Annotated,
    string? SourcePath,
    ImageDiscussCharacter? Character,
    string? SessionId);

public sealed record ImageDiscussCharacter(string Name, string Path);

// SessionId — чат, куда ушло сообщение; Created — чат новый (false — переиспользован)
public sealed record ImageDiscussResultDto(string SessionId, bool Created, IReadOnlyList<string> Attachments);
