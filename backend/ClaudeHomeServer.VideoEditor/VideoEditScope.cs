using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.VideoEditor;

// Область модуля «Видео» — чем ключуются нити, задачи, рабочая папка, префы и события (ADR-022 §2, как у
// звука). У чата проекта это id проекта, у личного чата вне проекта — константа Personal, одна на
// владельца, а не на чат: ветвление копирует нити с версиями, и ключ «на чат» потерял бы их в ветке.
// Project есть только у области проекта: всё, что читает диск проекта (фильмы, сохранение, кадры-файлы,
// local), у личной области обязано отказать ДО обращения к RootPath.
// Единственная точка сопоставления «чат ↔ область»: инлайновых `?? "personal"` не заводить.
public sealed record VideoEditScope(string Key, Project? Project)
{
    // Без двоеточия: ключ идёт сегментом пути префов; с id проекта (Guid) не пересекается
    public const string Personal = "personal";

    public static VideoEditScope Of(Project project) => new(project.Id, project);

    // Область чата для сопоставления по ключу; Project здесь не резолвится — диску проекта нужна
    // область из самого проекта (Of(Project)), которую вызывающий уже проверил
    public static VideoEditScope Of(Session session) => new(session.ProjectId ?? Personal, null);

    // Id проекта по ключу области — для записей, где проект обязан быть настоящим (трата): у личной null
    public static string? ProjectIdOf(string scopeKey) => scopeKey == Personal ? null : scopeKey;

    public bool IsPersonal => Key == Personal;
}
