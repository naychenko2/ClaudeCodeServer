using System.Collections.Concurrent;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

// Провижн авто-ассистента: нейтральная заготовка «Ассистент» (Coordinator + Full +
// привязки personas-manage/tasks/notes) становится дефолтной персоной пользователя, пока он
// не познакомится с ней в интервью. Вызывается ТОЛЬКО на точках записи (стартовый проход,
// создание пользователя, рубеж создания чата) — НИКОГДА на GET, иначе мутация проникала бы
// в /api/auth/me. Подробности и контракт — план
// docs/.omc/plans/onboarding-optional-consensus.md §2.
public class DefaultAssistantProvisioner
{
    private readonly UserStore _users;
    private readonly PersonaManager _personas;
    private readonly PersonaBindingsService _bindings;
    private readonly IHubContext<SessionHub> _hub;
    private readonly ILogger<DefaultAssistantProvisioner> _log;

    // Идемпотентность провижна: перечитывание под per-ключевым семафором (паттерн
    // OnboardingController). Реестр статический: сервис Singleton, а гонка межвызовная.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    private static SemaphoreSlim LockFor(string userId) =>
        _locks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));

    public DefaultAssistantProvisioner(UserStore users, PersonaManager personas,
        PersonaBindingsService bindings,
        IHubContext<SessionHub> hub, ILogger<DefaultAssistantProvisioner> log)
    {
        _users = users;
        _personas = personas;
        _bindings = bindings;
        _hub = hub;
        _log = log;
    }

    /// <summary>
    /// Гарантирует, что у пользователя есть действующая дефолт-персона. Возвращает её —
    /// уже существующую или только что созданную; null — только когда провижн невозможен
    /// (пользователя нет, создание упало или запрос отменён).
    /// </summary>
    public async Task<Persona?> EnsureAsync(string userId, CancellationToken ct = default)
    {
        // Действующий дефолт резолвится в живую персону — возвращаем её без создания
        var me = _users.GetById(userId);
        if (me is null) return null;
        if (me.DefaultPersonaId is { } existingId && _personas.Get(existingId, userId) is { } existing)
            return existing;

        var gate = LockFor(userId);
        try
        {
            await gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        Persona? created = null;
        try
        {
            // Перечитываем пользователя под локом: параллельный Ensure мог уже завести заготовку
            me = _users.GetById(userId);
            if (me is null) return null;

            // Снова проверяем дефолт — конкурентный вызов мог уже проставить его
            if (me.DefaultPersonaId is { } freshId && _personas.Get(freshId, userId) is { } fresh)
                return fresh;

            created = _personas.Create(
                userId,
                "Ассистент",
                role: "Личный помощник",
                description: null,
                systemPrompt: null,
                model: null,
                effort: null,
                scope: PersonaScope.Global,
                projectId: null,
                color: "orange",
                greeting: null,
                memoryEnabled: true,
                access: PersonaAccess.Full,
                specialty: PersonaSpecialty.Coordinator);

            // Назначаем дефолтом и фиксируем как заготовку ДО досева. Досев идемпотентен по
            // построению, но обрыв между Create и SetDefault плодил бы вторую персону при
            // следующем Ensure — поэтому порядок именно такой (план §2, «Идемпотентность»).
            _users.SetDefaultPersona(userId, created.Id);
            _users.SetAssistantPersona(userId, created.Id);

            // Досев привязок (personas-manage/tasks/notes + проектные дефолты) — идемпотентен
            created = _bindings.SeedDefaultPersonaProfile(userId, created);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            gate.Release();
        }

        // Broadcast строго по факту создания: created (стор персон узнаёт о новой персоне)
        // и default (lib/defaultPersona перечитает /me). Существующую персону не трогаем —
        // событие шлётся только когда заготовка создана в этом вызове.
        // Отмена запроса здесь уже ничего не значит: персона создана и зафиксирована, а
        // доставка события клиенту — best-effort. Без этого catch закрытая на полпути вкладка
        // (ct = HttpContext.RequestAborted из гейта создания чата) роняла бы необработанное
        // исключение в пайплайн и шумела бы ошибкой в логах и трейсах там, где ошибки нет.
        var groupName = "user_" + userId;
        try
        {
            await _hub.Clients.Group(groupName)
                .SendAsync("message", new PersonasChangedMessage("created", created.Id), ct);
            await _hub.Clients.Group(groupName)
                .SendAsync("message", new PersonasChangedMessage("default", created.Id), ct);
        }
        catch (OperationCanceledException) { /* клиент ушёл — персона всё равно создана */ }
        return created;
    }
}
