using System.Collections.Concurrent;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// Провижн проекта-предмета ребёнка (Б8b, ADR-041 ред. 5, разрез §11.1): каждый предмет —
/// проект со своей папкой {дом}/{Предметы}/{subjectId} (путь собирает UserHomeResolver.SubjectHome,
/// единственное место сборки), владелец — сам ребёнок, связь — маркер Project.ChildSubjectId.
/// Идемпотентность: существующий проект возвращается по маркеру; правило «одна папка — один
/// проект на владельца» (EnsureRootFree в Create) не обходится. В папке сразу создаются
/// «Материалы/» и «Для репетитора/» — имена берутся из ChildPathPolicy, чтобы создание папок
/// и политика путей не расходились литералами.
/// Вызывается только на точках записи предмета (Б17a), НИКОГДА на GET — тот же порядок,
/// что у ChildTutorProvisioner.
/// Конкурентность: проверка «есть ли проект» и создание — под per-ключевым семафором
/// childId:subjectId (реестр статический: сервис Singleton, а гонка межвызовная — тот же
/// приём, что у ChildTutorProvisioner). Под локом профиль и список проектов перечитываются:
/// параллельный вызов того же ключа мог уже создать проект, параллельная правка профиля —
/// удалить предмет.
/// Сбой создания папки (IOException) — проект не записан (папка создаётся до записи, а Create
/// пишет запись только после папки), ошибка доходит до вызывающего как есть — тот же контракт,
/// что у ChildTutorProvisioner: ошибки диска не глотаются, контроллер ответит 500 с откатом;
/// полусостояния не остаётся, повторный вызов после устранения препятствия успешен.
/// null — провижн невозможен: пользователя нет, детского профиля нет, предмета в живом профиле
/// нет, домашняя папка не настроена, запрос отменён.
/// </summary>
public class ChildSubjectProjectProvisioner
{
    // Ключ — пара childId:subjectId: один предмет сериализован, разные предметы одного
    // ребёнка друг друга не ждут.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    private static SemaphoreSlim LockFor(string childId, string subjectId) =>
        _locks.GetOrAdd(childId + ":" + subjectId, _ => new SemaphoreSlim(1, 1));

    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly UserHomeResolver _homes;
    private readonly ILogger<ChildSubjectProjectProvisioner>? _log;

    public ChildSubjectProjectProvisioner(UserStore users, ProjectManager projects,
        UserHomeResolver homes, ILogger<ChildSubjectProjectProvisioner>? log = null)
    {
        _users = users;
        _projects = projects;
        _homes = homes;
        _log = log;
    }

    /// <summary>
    /// Гарантирует, что у предмета ребёнка есть его проект. Возвращает проект — уже
    /// существующий (по маркеру ChildSubjectId) или только что созданный; null — только
    /// когда провижн невозможен (контракт класса).
    /// </summary>
    public async Task<Project?> EnsureSubjectProjectAsync(string childId, ChildSubject subject,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subject?.Id)) return null;

        // Быстрый путь без лока: обычный повторный вызов (предмет уже провит) не входит
        // в семафор — тот же порядок, что у LiveTutorId в ChildTutorProvisioner.
        if (FindSubjectProject(childId, subject.Id) is { } existing) return existing;

        var gate = LockFor(childId, subject.Id);
        try
        {
            await gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            // Перечитываем под локом: параллельный вызов того же ключа мог уже создать
            // проект; параллельная правка профиля (PUT) или удаление ребёнка — мог удалить
            // предмет. Что было до семафора — не аргумент.
            var me = _users.GetById(childId);
            if (me?.ChildMode is not { } child) return null;
            if (FindSubject(child, subject.Id) is null) return null;
            if (FindSubjectProject(childId, subject.Id) is { } fresh) return fresh;

            var root = _homes.SubjectHome(me.Username, me.ExecutionEnvironment, subject.Id);
            if (root is null)
            {
                // Домашней папки, внутри которой лежит папка предмета, нет — база не
                // настроена. Fail-closed: провижна невозможна до настройки (тот же смысл,
                // что у отказа Create без DefaultProjectsPath).
                _log?.LogWarning(
                    "Детский режим: базовая папка для проекта-предмета не настроена (ребёнок {Owner})", childId);
                return null;
            }

            // Папка ДО записи: сбой создания папки не оставляет проекта (и Create пишет
            // запись только после папки). Подпапки — по именам ChildPathPolicy: граница
            // «Материалы»/«Для репетитора» в создании и в политике путей — одна строка.
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(SafePath.Join(root, ChildPathPolicy.MaterialsDir));
            Directory.CreateDirectory(SafePath.Join(root, ChildPathPolicy.TutorDir));

            // Создание — через ProjectManager.Create: EnsureRootFree не обходится (одна
            // папка — один проект на владельца); маркер пишется вместе с записью.
            return _projects.Create(subject.Label, root, childId, me.Username,
                createDirectory: true, childSubjectId: subject.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    // Проект-предмет по маркеру — только свои (per-owner): чужой проект с тем же id предмета
    // (имя slug совпало у другого ребёнка) — не этот случай.
    private Project? FindSubjectProject(string childId, string subjectId) =>
        _projects.GetByOwner(childId).FirstOrDefault(p => p.ChildSubjectId == subjectId);

    // Предмет по ключу — то же сравнение, что у записи стора и ChildTutorProvisioner.
    private static ChildSubject? FindSubject(ChildProfile child, string subjectId) =>
        child.Subjects.FirstOrDefault(s => s.Id == subjectId);
}
