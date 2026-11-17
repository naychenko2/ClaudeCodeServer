using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.ChildMode;

// Провижн персоны-тьютора ребёнка (KM-B1d, ADR-041): «Помощник» — репетитор 4–5 класса,
// владелец — сам ребёнок. Признак «это тьютор» — id в ChildProfile.TutorPersonaId: провижер
// пишет его сам сразу после Create (паттерн DefaultAssistantProvisioner — обрыв между
// созданием и закреплением ключа родил бы второго тьютора при следующем Ensure).
// Вызывается только на точках записи детского профиля (KM-B1e), НИКОГДА на GET.
// Секция child-safety в промпте — KM-B6c, здесь её нет.
public class ChildTutorProvisioner
{
    // Возможности тьютора: без web — интернет ребёнку не открывается вовсе;
    // собственные задачи и заметки остаются.
    private static readonly List<string> TutorTools = ["tasks", "notes"];

    private readonly UserStore _users;
    private readonly PersonaManager _personas;
    private readonly ISessionBroadcaster _broadcaster;
    private readonly ILogger<ChildTutorProvisioner> _log;

    // Идемпотентность провижна: перечитывание под per-ключевым семафором (паттерн
    // DefaultAssistantProvisioner). Реестр статический: сервис Singleton, а гонка межвызовная.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    private static SemaphoreSlim LockFor(string childUserId) =>
        _locks.GetOrAdd(childUserId, _ => new SemaphoreSlim(1, 1));

    public ChildTutorProvisioner(UserStore users, PersonaManager personas,
        ISessionBroadcaster broadcaster, ILogger<ChildTutorProvisioner> log)
    {
        _users = users;
        _personas = personas;
        _broadcaster = broadcaster;
        _log = log;
    }

    /// <summary>
    /// Гарантирует, что у ребёнка есть персона-тьютор. Возвращает её id — уже существующий
    /// или только что созданный; null — только когда провижн невозможен (пользователя нет,
    /// профиля ребёнка нет, запрос отменён). Ошибка записи стора не глотается: исключение
    /// доходит до вызывающего (KM-B1e вернёт 500 с текстом).
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
            bool keyed;
            try
            {
                keyed = _users.SetChildTutorPersona(childUserId, createdId);
            }
            catch (OperationCanceledException)
            {
                _personas.Delete(createdId, childUserId);
                return null;
            }
            catch
            {
                _personas.Delete(createdId, childUserId);
                throw;
            }
            if (!keyed)
            {
                _personas.Delete(createdId, childUserId);
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

    // Ключ живёт, только когда резолвится в живую персону этого ребёнка: висящий id
    // (персона удалена руками) не возвращается — тьютор провится заново.
    private string? LiveTutorId(ChildProfile child, string childUserId) =>
        !string.IsNullOrWhiteSpace(child.TutorPersonaId)
        && _personas.Get(child.TutorPersonaId, childUserId) is not null
            ? child.TutorPersonaId
            : null;
}
