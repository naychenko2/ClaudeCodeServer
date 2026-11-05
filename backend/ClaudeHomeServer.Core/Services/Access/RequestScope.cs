namespace ClaudeHomeServer.Services.Access;

/// <summary>
/// Область запроса: кто зовёт и чьё пространство затронуто. Резолвится один раз на запрос
/// (фильтр <see cref="ProjectScopeAttribute"/> или явный <see cref="IProjectAccess"/>).
/// <b>Ключ хранилищ в проектных эндпоинтах — только <see cref="TenantOwnerId"/></b>;
/// <see cref="CallerUserId"/> нужен для аудита и «автора», ключом не служит (ловушка ключа,
/// разрез §6.6).
/// </summary>
/// <param name="CallerUserId">Кто вызывает (claim <c>sub</c>).</param>
/// <param name="TenantOwnerId">Арендатор: владелец проекта (для чата вне проекта — владелец чата).</param>
/// <param name="Role">Роль вызывающего в проекте.</param>
public sealed record RequestScope(string CallerUserId, string TenantOwnerId, ProjectRole Role);
