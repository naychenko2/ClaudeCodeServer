using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.ImageEditor;

// Область редактора — чем ключуются задачи, котировки, шаги рабочей папки, выбор человека и события
// (разрез docs/research/image-editor-personal-chats-cut-2026-09.md). У чата проекта это id проекта,
// у личного чата вне проекта — константа Personal, одна на владельца, а не на чат: ветвление копирует
// нити с шагами, и ключ «на чат» потерял бы в ветке все версии. Project — проект области; у личной
// области это «Личное» владельца (ADR-040), когда гейт его нашёл, иначе null: всё, что читает диск проекта,
// без папки (HasFolder == false) обязано отказать ДО обращения к RootPath.
// Единственная точка сопоставления «чат ↔ область»: инлайновых `?? "personal"` не заводить.
public sealed record ImageEditScope(string Key, Project? Project)
{
    // Арендатор области: владелец проекта (для личной области — владелец чата). Ключ хранилищ
    // вертикали (нити, префы, задачи) берут отсюда, а не из id вызывающего: в общем проекте они расходятся.
    // Заполняет только гейт области; у Of(Session) для сопоставления по ключу не задан
    public string? TenantOwnerId { get; init; }

    // Ключ хранилищ проектных действий. Область без арендатора (Of(Session) мимо гейта) — отказ,
    // а не тихий откат на id вызывающего
    public string Tenant => TenantOwnerId
        ?? throw new InvalidOperationException("Область редактора не прошла гейт: TenantOwnerId не задан");

    // Без двоеточия: ImageProjectPrefsStore.Safe его режет; с id проекта (Guid) не пересекается
    public const string Personal = "personal";

    // Ключ хранилищ проектного действия по самому проекту (когда область не нужна); проект без владельца — отказ
    public static string TenantOf(Project project) => project.OwnerId
        ?? throw new InvalidOperationException("У проекта нет владельца: ключ хранилищ не определён");

    public static ImageEditScope Of(Project project) => new(project.Id, project) { TenantOwnerId = project.OwnerId };

    // Область чата для сопоставления по ключу; Project здесь не резолвится — диску проекта нужна
    // область из самого проекта (Of(Project)), которую вызывающий уже проверил
    public static ImageEditScope Of(Session session) => new(session.ProjectId ?? Personal, null);

    // Id проекта по ключу области — для записей, где проект обязан быть настоящим (трата): у личной null
    public static string? ProjectIdOf(string scopeKey) => scopeKey == Personal ? null : scopeKey;

    // Ключ личный: ключи хранилищ, трата, префы. Не говорит, есть ли диск — это HasFolder
    public bool IsPersonal => Key == Personal;

    // Есть папка, с которой можно читать и писать: у области проекта — сам проект, у личной — «Личное»
    // владельца, если гейт его положил (ADR-040 §4). Ключ области от этого не меняется
    public bool HasFolder => Project is not null;
}
