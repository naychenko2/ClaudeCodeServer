using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.ImageEditor;

// Область редактора — чем ключуются задачи, котировки, шаги рабочей папки, выбор человека и события
// (разрез docs/research/image-editor-personal-chats-cut-2026-09.md). У чата проекта это id проекта,
// у личного чата вне проекта — константа Personal, одна на владельца, а не на чат: ветвление копирует
// нити с шагами, и ключ «на чат» потерял бы в ветке все версии. Project есть только у области
// проекта: всё, что читает диск проекта, у личной области обязано отказать ДО обращения к RootPath.
// Единственная точка сопоставления «чат ↔ область»: инлайновых `?? "personal"` не заводить.
public sealed record ImageEditScope(string Key, Project? Project)
{
    // Без двоеточия: ImageProjectPrefsStore.Safe его режет; с id проекта (Guid) не пересекается
    public const string Personal = "personal";

    public static ImageEditScope Of(Project project) => new(project.Id, project);

    // Область чата для сопоставления по ключу; Project здесь не резолвится — диску проекта нужна
    // область из самого проекта (Of(Project)), которую вызывающий уже проверил
    public static ImageEditScope Of(Session session) => new(session.ProjectId ?? Personal, null);

    // Id проекта по ключу области — для записей, где проект обязан быть настоящим (трата): у личной null
    public static string? ProjectIdOf(string scopeKey) => scopeKey == Personal ? null : scopeKey;

    public bool IsPersonal => Key == Personal;
}
