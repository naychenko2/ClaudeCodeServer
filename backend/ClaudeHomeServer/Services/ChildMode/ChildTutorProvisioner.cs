using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// Провижнер предмета ребёнка (Б17a, ADR-041 ред. 7, решения 69–70, И10): репетитор — обычная
/// проектная персона проекта-предмета и его руководитель, Project.DefaultPersonaId — единственная
/// точка правды. Цепочка: проект предмета (Б8b, ChildSubjectProjectProvisioner) → проектная
/// персона (владелец — ребёнок, Scope = Project, характер из шаблона, MemoryEnabled = false) →
/// руководитель проекта → кураторы (точка вызова через заглушку IChildSubjectCuratorSync,
/// реализация — Б14a). Всё идемпотентно: живой руководитель возвращается без создания.
///
/// Ключа репетитора у предмета больше нет (Б17c): репетитор предмета — только руководитель
/// проекта-предмета; контракт удаления сирот и ручки child-tutors сняты вместе с ним.
///
/// Тьютор профиля (EnsureAsync, KM-B1d): ключ в ChildProfile.TutorPersonaId, Scope = Global —
/// им ведётся чат правил (ChildModeGate.LiveRouteOf).
///
/// Вызывается только на точках записи детского профиля (KM-B1e), НИКОГДА на GET.
/// Секция child-safety в промпте — отдельный слой (KM-B6c), здесь её нет.
/// </summary>
public class ChildTutorProvisioner
{
    // Возможности репетитора: без web — интернет ребёнку не открывается вовсе;
    // собственные задачи и заметки остаются.
    private static readonly List<string> TutorTools = ["tasks", "notes"];

    // Шаблон характера репетитора предмета (решение 70): начальный текст контракта персоны;
    // дальше характер правит куратор штатными дверями персоны (ConfigurePersonas, Б16).
    private static PersonaContract SubjectTutorCharacter() => new()
    {
        Character = "Объясняет предмет по его материалам, проверяет ответы и ведёт занятие.",
    };

    private readonly UserStore _users;
    private readonly PersonaManager _personas;
    private readonly ISessionBroadcaster _broadcaster;
    private readonly ChildSubjectProjectProvisioner _subjectProjects;
    private readonly ProjectManager _projects;
    private readonly IChildSubjectCuratorSync? _curators;
    // Лог nullable — как у ChildSubjectProjectProvisioner: ручное конструирование (тесты)
    // допустимо без него.
    private readonly ILogger<ChildTutorProvisioner>? _log;

    // Идемпотентность провижна: перечитывание под per-ключевым семафором (паттерн
    // DefaultAssistantProvisioner). Реестр статический: сервис Singleton, а гонка межвызовная.
    // Ключи: EnsureAsync — childId (один тьютор на ребёнка); EnsureSubjectTutorAsync —
    // childId:subjectId (Б17a: один провижна на предмет, разные предметы одного ребёнка друг
    // друга не ждут — тот же ключ, что у замков ChildSubjectProjectProvisioner).
    // Тот же словарь: ключи не пересекаются (subjectId непустой),
    // а общий реестр держит одну запись на ключ для всех методов.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    // internal — общий замок точек записи предмета: к этому словарю по тому же ключу
    // childId:subjectId встаёт и стартовая миграция Б17b (ChildSubjectsMigration.MigrateSubjectAsync):
    // PUT профиля / создание урока (зовут EnsureSubjectTutorAsync) и миграция одного предмета
    // не провятся параллельно — иначе у предмета встают два руководителя: старый тьютор от
    // миграции и новая персона от провижнера.
    internal static SemaphoreSlim LockFor(string key) =>
        _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    public ChildTutorProvisioner(UserStore users, PersonaManager personas,
        ISessionBroadcaster broadcaster, ChildSubjectProjectProvisioner subjectProjects,
        ProjectManager projects, IChildSubjectCuratorSync? curators = null,
        ILogger<ChildTutorProvisioner>? log = null)
    {
        _users = users;
        _personas = personas;
        _broadcaster = broadcaster;
        _subjectProjects = subjectProjects;
        _projects = projects;
        _curators = curators;
        _log = log;
    }

    /// <summary>
    /// Гарантирует, что у ребёнка есть персона-тьютор профиля (до Б17c — ведёт чат правил).
    /// Возвращает её id — уже существующий или только что созданный; null — только когда провижн
    /// невозможен (пользователя нет, профиля ребёнка нет, запрос отменён). Ошибка записи стора
    /// не глотается: исключение доходит до вызывающего (KM-B1e вернёт 500 с текстом).
    /// </summary>
    public async Task<string?> EnsureAsync(string childUserId, CancellationToken ct = default)
    {
        var me = _users.GetById(childUserId);
        if (me is null) return null;
        // Тьютор — только ребёнку: без профиля нечем закрепить персону, идемпотентность
        // не держится; обычному пользователю ассистента провит DefaultAssistantProvisioner.
        if (me.ChildMode is not { } child) return null;
        if (LiveTutorId(child, childUserId) is { } existingId) return existingId;

        var gate = LockFor(childUserId);
        try
        {
            await gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        string createdId;
        try
        {
            // Перечитываем ребёнка под локом: параллельный Ensure мог уже создать тьютора
            me = _users.GetById(childUserId);
            if (me?.ChildMode is not { } freshChild) return null;
            if (LiveTutorId(freshChild, childUserId) is { } freshId) return freshId;

            var created = _personas.Create(
                childUserId,
                "Помощник",
                role: "Репетитор 4–5 класс: математика, русский, английский",
                description: null,
                systemPrompt: null,
                model: null,
                effort: null,
                scope: PersonaScope.Global,
                projectId: null,
                color: "green",
                greeting: null,
                memoryEnabled: false,
                tools: TutorTools);
            createdId = created.Id;

            // Закрепляем ключ ДО вещания: обрыв вызова оставляет либо целую пару
            // (персона + ключ в ChildProfile), либо ничего — полуперсоны не бывает.
            // Отказ закрепления компенсируем удалением созданной персоны (доработка KM-B1d,
            // вердикт d4fc053c): иначе она осиротевает в personas.json без ключа и второй
            // Ensure плодит второго «Помощника». false из SetChildTutorPersona — профиль
            // ребёнка исчез на параллельной правке: это «профиля нет» из контракта метода —
            // удаляем персону и возвращаем null; исключение стора — удаляем и пробрасываем.
            // Исключение здесь способен дать только Save стора: у SetChildTutorPersona нет ct
            // (UserStore.cs:616), отдельная ветка OperationCanceledException была недостижимой
            // (хвост R-KM-B1d, ddbc7a1c). Сбой самого отката не должен маскировать исходную
            // причину — RollbackDelete глотает его с логом, наружу идёт исключение сохранения.
            bool keyed;
            try
            {
                keyed = _users.SetChildTutorPersona(childUserId, createdId);
            }
            catch
            {
                RollbackDelete(createdId, childUserId);
                throw;
            }
            if (!keyed)
            {
                RollbackDelete(createdId, childUserId);
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            gate.Release();
        }

        // Вещание по факту создания — то же, что у DefaultAssistantProvisioner: стор персон
        // узнаёт о новой персоне. «default» нет: тьютор не дефолт-персона ребёнка, связь
        // детского чата с ним держится на ChildProfile.TutorPersonaId.
        try
        {
            await _broadcaster.ToOwner(childUserId,
                new PersonasChangedMessage("created", createdId));
        }
        catch (OperationCanceledException) { /* клиент ушёл — персона всё равно создана */ }
        return createdId;
    }

    /// <summary>
    /// Гарантирует, что у предмета ребёнка есть его проект и его репетитор-руководитель (Б17a,
    /// решения 69–70). Возвращает id руководителя — уже живого или только что созданного;
    /// null — только когда провижн невозможен (пользователя нет, профиля ребёнка нет, предмета
    /// нет, домашняя папка не настроена, запрос отменён). Ошибка записи стора не глотается:
    /// исключение доходит до вызывающего (контроллер — 500 tutor_provision_failed с откатом
    /// профиля). Персона: владелец — ребёнок, Scope = Project, ProjectId — проект предмета,
    /// имя «Репетитор: {label}», характер из шаблона, MemoryEnabled = false, Tools без web;
    /// аватар — инициалы, не генерация (не списывает бюджет ребёнка, И5 №12).
    ///
    /// Сбой после создания проекта (персона или её закрепление): проект остаётся — повторный
    /// провижна подхватывает его по маркеру; персоны-сироты нет (созданная удалена откатом),
    /// наружу — исключение стора, не необработанное состояние.
    ///
    /// Created — «персону создал ЭТОТ вызов»: точный ответ знает только провижер под своим
    /// замком (минор R-Т2b 3a7f4e3c: сравнение с снимком контроллера ошибается на параллельном
    /// PUT). Вызывающий по нему решает, кого удалять при откате отказавшего запроса.
    /// </summary>
    public async Task<(string Id, bool Created)?> EnsureSubjectTutorAsync(string childUserId, string subjectId,
        CancellationToken ct = default)
    {
        var me = _users.GetById(childUserId);
        if (me?.ChildMode is not { } child) return null;
        // Предмет должен лежать в профиле: контроллер зовёт после записи нового (Т2a),
        // ручка кабинета (Т2c) — по существующему.
        if (FindSubject(child, subjectId) is not { } subject) return null;

        // Быстрый путь без лока: предмет уже провит — проект по маркеру, руководитель живой.
        // Тот же порядок, что у LiveTutorId и fast-path ChildSubjectProjectProvisioner.
        if (LiveSubjectLeadId(childUserId, subjectId) is { } existingId) return (existingId, false);

        var gate = LockFor(childUserId + ":" + subjectId);
        try
        {
            await gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        string createdId;
        string projectId;
        try
        {
            // Перечитываем ребёнка под локом: параллельный вызов того же ключа мог уже провить
            // предмет, параллельная правка профиля (PUT) или удаление ребёнка — мог удалить
            // предмет. Что было до семафора — не аргумент.
            me = _users.GetById(childUserId);
            if (me?.ChildMode is not { } freshChild) return null;
            if (FindSubject(freshChild, subjectId) is not { } freshSubject) return null;
            if (LiveSubjectLeadId(childUserId, subjectId) is { } freshId) return (freshId, false);

            // 1. Проект-предмета (Б8b). null — провижн невозможен (дом не настроен, предмета
            // нет); исключение диска — как есть, контракт класса.
            if (await _subjectProjects.EnsureSubjectProjectAsync(childUserId, freshSubject, ct)
                is not { } project) return null;
            projectId = project.Id;

            // 2. Проектная персона-репетитор: владелец — ребёнок, область — проект.
            var created = _personas.Create(
                childUserId,
                $"Репетитор: {freshSubject.Label}",
                role: null,
                description: null,
                systemPrompt: null,
                model: null,
                effort: null,
                scope: PersonaScope.Project,
                projectId: projectId,
                color: null,
                greeting: null,
                memoryEnabled: false,
                tools: TutorTools,
                contract: SubjectTutorCharacter());
            createdId = created.Id;

            // 3. Руководитель — ДО вещания (тот же порядок «ключ→вещание», что у EnsureAsync):
            // DefaultPersonaId — единственная точка правды репетитора (И10). Отказ закрепления
            // компенсируем удалением созданной персоны: полуперсоны-без-руководителя не остаётся
            // (образец — KM-B1d). SetDefaultPersona не имеет false-ветки (ProjectManager.cs:303):
            // проект исчез под параллельной правкой — KeyNotFoundException, та же ветка, что у
            // исключения стора: удаляем персону и пробрасываем. Проект при этом остаётся —
            // его повторный провижна подхватит (решение 70: всё идемпотентно).
            try
            {
                _projects.SetDefaultPersona(projectId, createdId);
            }
            catch
            {
                RollbackDelete(createdId, childUserId);
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            gate.Release();
        }

        // 4. Кураторы (решение 61, синхронизация — Б14a): точка вызова. Реализации нет —
        // вызова нет (заглушка до Б14a). Сбой синхронизации — лог: персона и руководитель уже
        // записаны, причина видна в логе (тот же приём, что у вещания).
        if (_curators is { } sync)
        {
            try
            {
                await sync.SyncCuratorsAsync(childUserId, projectId, ct);
            }
            catch (OperationCanceledException) { /* клиент ушёл — персона всё равно создана */ }
            catch (Exception e)
            {
                _log?.LogWarning(e, "Детский режим: синхронизация кураторов проекта-предмета не прошла (ребёнок {Owner}, проект {Project})",
                    childUserId, projectId);
            }
        }

        // Вещание по факту создания — то же, что у EnsureAsync: стор персон узнаёт о новой
        // персоне. «default» нет: репетитор предмета — руководитель проекта, не дефолт-персона
        // ребёнка. Отказ вещания (не отмена) — сбой запроса: созданная персона удаляется
        // откатом и исключение идёт наружу (контроллер — 500 с откатом профиля): отказанный
        // вызов не оставляет «Репетитор: …» в сторе (тот же контракт, что у Т2b, где ключ
        // служил доказательством создания; здесь доказательство — сам факт создания).
        // Отмена — не сбой: персона остаётся, она закреплённый руководитель проекта.
        try
        {
            await _broadcaster.ToOwner(childUserId,
                new PersonasChangedMessage("created", createdId));
        }
        catch (OperationCanceledException) { /* клиент ушёл — персона всё равно создана */ }
        catch
        {
            RollbackDelete(createdId, childUserId);
            throw;
        }
        return (createdId, true);
    }

    // Предмет по ключу — то же сравнение, что у ChildSubjectProjectProvisioner: провижнеры
    // должны находить одну и ту же запись.
    private static ChildSubject? FindSubject(ChildProfile child, string subjectId) =>
        child.Subjects.FirstOrDefault(s => s.Id == subjectId);

    // Проект-предмета по маркеру — только свои (per-owner), тот же поиск, что у
    // ChildSubjectProjectProvisioner.FindSubjectProject и ChildModeGate.LiveRouteOf.
    private Project? SubjectProject(string childId, string subjectId) =>
        _projects.GetByOwner(childId).FirstOrDefault(p => p.IsChildSubject && p.ChildSubjectId == subjectId);

    // Живой руководитель предмета: проект по маркеру → DefaultPersonaId → живая персона этого
    // ребёнка. Висящий ключ (персона удалена руками) не возвращается — предмет провится заново
    // и руководитель перезапишется (тот же смысл, что у LiveSubjectTutorId до Б17a).
    private string? LiveSubjectLeadId(string childId, string subjectId)
    {
        if (SubjectProject(childId, subjectId) is not { } project
            || string.IsNullOrWhiteSpace(project.DefaultPersonaId)) return null;
        return _personas.Get(project.DefaultPersonaId, childId) is not null
            ? project.DefaultPersonaId
            : null;
    }

    // Откат созданной персоны при отказе закрепления руководителя. Сбой самого удаления не должен
    // ни маскировать исходное исключение сохранения (ветка catch), ни ломать контракт метода
    // (null при пропавшем профиле): сирота в этом случае — меньшее зло, чем скрытая причина,
    // и она видна в логе (хвост R-KM-B1d, ddbc7a1c).
    private void RollbackDelete(string personaId, string childUserId)
    {
        try
        {
            _personas.Delete(personaId, childUserId);
        }
        catch (Exception e)
        {
            _log?.LogWarning(e, "Детский режим: откат персоны-тьютора не прошёл (ребёнок {Owner}, персона {Persona})",
                childUserId, personaId);
        }
    }

    // Ключ живёт, только когда резолвится в живую персону этого ребёнка: висящий id
    // (персона удалена руками) не возвращается — тьютор провится заново.
    private string? LiveTutorId(ChildProfile child, string childUserId) =>
        !string.IsNullOrWhiteSpace(child.TutorPersonaId)
        && _personas.Get(child.TutorPersonaId, childUserId) is not null
            ? child.TutorPersonaId
            : null;
}

/// <summary>
/// Синхронизация кураторов проекта-предмета (Б14a, ADR-041 ред. 7, решение 61): опекуны
/// профиля ребёнка становятся участниками проекта его предмета. Интерфейс-заглушка до Б14a:
/// провижер (Б17a) держит только точку вызова — реализации нет, вызова нет. Подпись и место
/// реализации уточняет Б14a.
/// </summary>
public interface IChildSubjectCuratorSync
{
    Task SyncCuratorsAsync(string childId, string subjectProjectId, CancellationToken ct = default);
}
