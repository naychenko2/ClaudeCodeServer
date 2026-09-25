using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Execution;

/// <summary>
/// Готовность устройства проекта для фоновой работы (ADR-016, вариант А плана §5). Одна точка
/// для пяти механизмов: исполнителя задач, волны штаба, очереди чата, автоматизаций персон и
/// опросов сторожей. Отдельный шов, чтобы тесты подменяли онлайн-статус без канала устройств.
/// </summary>
public interface IProjectDeviceGate
{
    ProjectBackgroundGate Check(Project project);
}

/// <summary>
/// Боевой гейт: состояние устройства из шва <see cref="IDeviceExecChannel"/> (реализует
/// Desktop) и вердикт матрицы <see cref="ProjectCapabilities.BackgroundGate"/>. Канала нет
/// (подсистема выключена) — у локального проекта устройства нет: <c>DeviceGone</c>.
/// Канал резолвится лениво, при первом вопросе: гейт берут в конструктор синглтоны ядра
/// (SessionManager), а канал через маршрутизатор устройств и его наблюдателей сам тянется к
/// ним — ранний резолв замкнул бы цикл в DI.
/// </summary>
public sealed class ProjectDeviceGate(Func<IDeviceExecChannel?> channel) : IProjectDeviceGate
{
    public ProjectBackgroundGate Check(Project project)
    {
        if (!ProjectCapabilities.IsDeviceBound(project)) return ProjectBackgroundGate.Ready;
        var status = project.OwnerId is { } owner ? channel()?.GetStatus(owner, project.DeviceId!) : null;
        return ProjectCapabilities.BackgroundGate(project, status);
    }
}

/// <summary>
/// Подписчик выхода устройства в онлайн (<c>DeviceOnlineDispatcher</c>). Каждый механизм
/// фоновой работы реализует свою пару: запуск ждущего по событию и периодический проход —
/// потолок ожидания и догон на случай, если событие потерялось (рестарт, гонка с отметкой).
/// </summary>
public interface IDeviceOnlineHandler
{
    /// <summary>Устройство владельца вышло в онлайн: запустить то, что его ждало.</summary>
    Task OnDeviceOnlineAsync(string ownerId, string deviceId, CancellationToken ct = default);

    /// <summary>Периодический проход: истёкшее ожидание — отказ с уведомлением, готовое — в работу.</summary>
    Task SweepAsync(DateTime nowUtc, CancellationToken ct = default);
}
