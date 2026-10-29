using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Access;

/// <summary>
/// Рамка хода — сколько личного пространства арендатора открыто агенту (разрез §И.3).
/// </summary>
public enum TurnFrameKind
{
    /// <summary>Всё личное пространство арендатора: инициатор хода достоверно сам арендатор.</summary>
    Wide,
    /// <summary>Только контекст этого проекта: актор не арендатор, либо в общем проекте инициатор неизвестен.</summary>
    Narrow,
}

/// <summary>
/// Рамка хода участника (S2a-2, разрез §И.3) — чистая функция над моделью, по образцу
/// <see cref="ProjectAccess"/>: без I/O и без зависимостей от Main. Ход участника идёт с
/// сервисным токеном арендатора, и без рамки агент участника получил бы всё личное
/// пространство владельца (<c>WorkspaceToolset</c>, подборка в ход). Вызывающие шаги
/// (S2a-3…S2a-4) сужают по вердикту платформенные тулсеты и подборку; здесь — только вердикт.
/// </summary>
public static class TurnFrame
{
    /// <summary>
    /// Рамка хода в проекте: Wide — если инициатор достоверно арендатор (<c>actorUserId ==
    /// tenantOwnerId</c>); Narrow — если актор есть, но он не арендатор. Актор неизвестен
    /// (null/пусто): в общем проекте — Narrow (fail-closed: фоновое действие или исполнитель
    /// без инициатора не получает личное пространство владельца, разрез §И.3), в личном —
    /// Wide: защищать не от кого, всё пространство и так принадлежит арендатору. Арендатор
    /// неизвестен при известном акторе — Narrow: что актор не чужой, нечем установить.
    /// «Общий проект» — у проекта есть участники (<c>Project.Members</c>, S1-1); null-проект
    /// общим быть не может (общий всегда с участниками) и относится к личным.
    /// </summary>
    public static TurnFrameKind Resolve(Project? project, string? tenantOwnerId, string? actorUserId)
    {
        if (string.IsNullOrEmpty(actorUserId))
            return IsSharedProject(project) ? TurnFrameKind.Narrow : TurnFrameKind.Wide;
        if (string.IsNullOrEmpty(tenantOwnerId))
            return TurnFrameKind.Narrow;
        return actorUserId == tenantOwnerId ? TurnFrameKind.Wide : TurnFrameKind.Narrow;
    }

    /// <summary>Общий проект — с непустым списком участников; пустой список нормализован в null (Project.Members).</summary>
    private static bool IsSharedProject(Project? project) => project?.Members is { Count: > 0 };
}
