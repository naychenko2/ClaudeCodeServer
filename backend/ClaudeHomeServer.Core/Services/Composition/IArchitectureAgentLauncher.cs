namespace ClaudeHomeServer.Services.Composition;

// Узкий шов «Сборка архитектуры», проход 2 (галочка «С агентом»): вертикаль Architecture
// ставит задачу агенту и запускает исполнителя, ничего не зная о Tasks/Personas/Main.
// Текст постановки собирает вертикаль (она знает контекст сборки), шов получает готовые
// Title/Description. Реализация — ArchitectureAgentLauncherAdapter (Main, Composition):
// путь TeamWaveService — TaskManager.Create → task_changed → TaskExecutionService.ExecuteAsync.
// Неймспейс — Composition, а не Services.Architecture: состав неймспейсов Core закрыт
// сторожем (CoreDll_СодержитТолькоРазрешённыеНеймспейсы), а шов по сути композиционный.

/// <summary>Постановка агенту: заголовок, описание (markdown) и метки задачи.</summary>
public sealed record ArchitectureAgentBrief(string Title, string Description, string[] Labels);

/// <summary>
/// Исход запуска. TaskId — созданная задача, а при <c>build_in_progress</c> — БЛОКИРУЮЩАЯ
/// (чтобы UI дал ссылку на идущую сборку); PersonaId — персона-архитектор (null — задача без
/// персоны, обычный исполнитель Claude); Error — код отказа:
/// <c>build_in_progress</c> (незавершённая задача с меткой <c>arch-build</c> в проекте) |
/// <c>launch_failed</c> (задача создана, исполнитель не стартовал).
/// </summary>
public sealed record ArchitectureAgentLaunch(string? TaskId, string? PersonaId, string? Error);

public interface IArchitectureAgentLauncher
{
    /// <summary>Метка задачи агентной сборки — по ней же ловится двойной запуск.</summary>
    const string BuildLabel = "arch-build";

    // Создаёт задачу персоне-архитектору проекта (Specialty == Planner с доступом к
    // arch_* на запись; нет — без персоны) и запускает исполнителя. ct запуск не отменяет:
    // созданную задачу бросать на полпути нельзя (см. реализацию).
    Task<ArchitectureAgentLaunch> LaunchAsync(
        string projectId, string ownerId, ArchitectureAgentBrief brief, CancellationToken ct);

    // Read-only проверка ДО прохода 1: id незавершённой задачи агентной сборки проекта или
    // null. Главная проверка — всё равно под замком в LaunchAsync (между ними окно гонки).
    string? IsBuildInProgress(string projectId, string ownerId);
}
