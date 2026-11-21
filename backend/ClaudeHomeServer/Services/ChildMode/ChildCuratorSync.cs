using System.Collections.Concurrent;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// Синхронизация кураторов проектов-предметов ребёнка (Б14a, ADR-041 ред. 6, решения 61–62,
/// разрез §12 «Назначение куратора»): кураторы — опекуны ChildProfile.GuardianUserIds,
/// одна точка правды; Members каждого проекта-предмета приводится к ним с ролью Editor,
/// остальных в списке нет. Реализация заглушки IChildSubjectCuratorSync: точка провижнера
/// предмета (ChildTutorProvisioner.cs:278) и точка сохранения профиля (UsersController,
/// PUT child-profile) зовут один и тот же метод под одним замком.
/// Админ куратором автоматически не становится: в desired попадает только то, что записано
/// в опекунaх. Флаг shared-projects у опекуна сервер за человека НЕ включает — это его
/// экспериментальная настройка (решение 61); здесь только ACL.
/// Конкурентность: per-childId семафор (тот же приём, что у ChildSubjectProjectProvisioner):
/// профиль и список проектов перечитываются ПОСЛЕ входа в замок — два одновременных сохранения
/// с разными опекунами дают один финальный обход, и его desired — живой профиль на момент
/// этого обхода, то есть последнего записанного. Что было до семафора — не аргумент.
/// Сбой записи членства одного проекта — лог: остальные проекты доходят, наружу исключение
/// не выходит (тот же контракт, что у RemoveOrphanTutorsAsync); следующий обход при новом
/// сохранении профиля доводит несинхронизированное.
/// </summary>
public class ChildCuratorSync : IChildSubjectCuratorSync
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly ProjectMembershipService _membership;
    // Лог nullable — как у провижнеров: ручное конструирование (тесты) допустимо без него.
    private readonly ILogger<ChildCuratorSync>? _log;

    public ChildCuratorSync(UserStore users, ProjectManager projects, ProjectMembershipService membership,
        ILogger<ChildCuratorSync>? log = null)
    {
        _users = users;
        _projects = projects;
        _membership = membership;
        _log = log;
    }

    /// <summary>
    /// Полный обход ребёнка: Desired = опекуны живого профиля, каждый проект-предмета
    /// (маркер ChildSubjectId) приводится к нему. Идемпотентен: повтор без правки профиля
    /// ничего не меняет. Ребёнка или профиля нет — обход ничего не делает (профиль мог
    /// исчезнуть под параллельным удалением).
    /// </summary>
    public async Task SyncChildCuratorsAsync(string childId, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(childId, _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            // Перечитываем профиль и список проектов под локом: параллельное сохранение
            // уже записало свой профиль — обход обязан ставить его, а не снимок до семафора.
            if (_users.GetById(childId)?.ChildMode is not { } child) return;
            var guardians = Desired(child);
            foreach (var project in _projects.GetByOwner(childId).Where(p => p.IsChildSubject).ToList())
            {
                // Отказ одного проекта — лог, обход продолжается (контракт класса).
                try
                {
                    await _membership.SyncSubjectProjectMembersAsync(childId, project.Id, guardians, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log?.LogWarning(e, "Детский режим: синхронизация кураторов не прошла (ребёнок {Owner}, проект {Project})",
                        childId, project.Id);
                }
            }
        }
        catch (OperationCanceledException) { /* клиент ушёл — обрыв обхода, синхронизированное остаётся */ }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Точка провижнера предмета (решение 61): свежий проект-предмет сразу получает кураторов
    /// живого профиля. Тот же Desired и тот же per-childId замок, что у полного обхода:
    /// параллельное сохранение профиля и этот вызов не пишут ACL друг поверх друга.
    /// </summary>
    public async Task SyncCuratorsAsync(string childId, string subjectProjectId, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(childId, _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_users.GetById(childId)?.ChildMode is not { } child) return;
            await _membership.SyncSubjectProjectMembersAsync(childId, subjectProjectId, Desired(child), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Контракт точки провижнера: сбой синхронизации — лог, персона и руководитель
            // уже записаны (ChildTutorProvisioner.cs:285-289); следующий обход доводит.
            _log?.LogWarning(e, "Детский режим: синхронизация кураторов проекта-предмета не прошла (ребёнок {Owner}, проект {Project})",
                childId, subjectProjectId);
        }
        finally
        {
            gate.Release();
        }
    }

    // Desired обхода — копия опекунского списка живого профиля: пустые id и дубли снимаются
    // здесь же, чтобы ни один обход не ставил в ACL пустого участника. Пустой список —
    // легальное состояние: обездолить прежних (опекун, вынесенный из профиля, теряет доступ).
    private static List<string> Desired(ChildProfile child) =>
        (child.GuardianUserIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
