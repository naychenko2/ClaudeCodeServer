using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Prompts;

namespace ClaudeHomeServer.Services;

// CRUD персон с изоляцией per-owner. Хранилище — data/personas.json
// (образец: ProjectManager + JsonFileStore). Все запросы фильтруются по OwnerId.
public class PersonaManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ConcurrentDictionary<string, Persona> _personas = new();
    private readonly string _storePath;
    private readonly Lock _saveLock = new();
    // Сериализация ConnectPantheon: check-then-create (GetByTemplateKey → Create) без лока
    // давал дубли персон с одним TemplateKey при параллельных connect. Отдельный от _saveLock,
    // чтобы не держать его во время Save/OnPersonaCreated внутри Create.
    private readonly Lock _connectLock = new();
    private readonly ILogger<PersonaManager>? _log;
    private readonly ProjectEventLogService? _events;

    public PersonaManager(IConfiguration config, ILogger<PersonaManager>? log = null, ProjectEventLogService? events = null)
    {
        _log = log;
        _events = events;
        // Стор — в каталоге DataPath (как у всех сервисов): в контейнере это /data (volume).
        // Прежний фолбэк AppContext.BaseDirectory/data жил ВНУТРИ контейнера, и персоны
        // с аватарами пропадали при каждом его пересоздании (деплое).
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _storePath = config["PersonasPath"] ?? Path.Combine(dataDir, "personas.json");
        Load();
        // Каталог пантеона мог обновиться с релизом — подтянуть регламенты нетронутых персон
        RefreshPantheonInstructions();
    }

    // Папка с ассетами персон (аватары): data/personas/
    public string AssetsDir => Path.Combine(Path.GetDirectoryName(_storePath)!, "personas");

    // Изменение персоны (профиль/возможности/привязки) — SessionManager сбрасывает адаптеры
    // её живых сессий, чтобы Tool-рубильники и MCP-серверы перемонтировались со следующего хода
    public event Action<Persona>? OnPersonaChanged;

    // Создание/удаление персоны — PersonaAgentFileSync генерирует/удаляет файл сабагента.
    // RefreshPantheonInstructions в ctor отрабатывает ДО подписок — это покрывает ленивый
    // полный reconcile синка перед ходом.
    public event Action<Persona>? OnPersonaCreated;
    public event Action<Persona>? OnPersonaDeleted;

    // Смена handle персоны (ручное переименование): PersonaAgentFileSync удаляет .md-файлы
    // по СТАРОМУ handle (2-й аргумент), затем OnPersonaChanged запишет новые. Поднимается
    // в Update ДО OnPersonaChanged.
    public event Action<Persona, string>? OnPersonaHandleChanged;

    public IReadOnlyCollection<Persona> GetByOwner(string userId) =>
        _personas.Values.Where(p => p.OwnerId == userId)
            .OrderByDescending(p => p.UpdatedAt).ToList();

    // Персоны, доступные в контексте: глобальные + привязанные к конкретному проекту
    public IReadOnlyCollection<Persona> GetForContext(string userId, string? projectId) =>
        _personas.Values.Where(p => p.OwnerId == userId
                && (p.Scope == PersonaScope.Global
                    || (p.Scope == PersonaScope.Project && p.ProjectId == projectId)))
            .OrderByDescending(p => p.UpdatedAt).ToList();

    // Персона по id с проверкой владельца (null — нет или чужая)
    public Persona? Get(string id, string userId) =>
        _personas.TryGetValue(id, out var p) && p.OwnerId == userId ? p : null;

    // Персона по id без проверки владельца — ТОЛЬКО для внутренних сервисов (авто-память),
    // где владелец берётся из самой персоны. Не использовать в обработчиках запросов.
    public Persona? GetByIdInternal(string id) => _personas.GetValueOrDefault(id);

    // Все персоны всех владельцев — ТОЛЬКО для фоновых сервисов (консолидация памяти),
    // где гейт по владельцу делается через саму персону. Не использовать в обработчиках запросов.
    public IReadOnlyCollection<Persona> GetAllInternal() => _personas.Values.ToList();

    // Персона владельца по handle без учёта контекста (legacy). Handle больше НЕ уникален
    // per-owner (проектные персоны разных проектов могут делить handle) — предпочитайте
    // перегрузку с projectId. Оставлено для мест, где контекст неизвестен.
    public Persona? GetByHandle(string userId, string handle) =>
        _personas.Values.FirstOrDefault(p => p.OwnerId == userId
            && string.Equals(p.Handle, handle, StringComparison.OrdinalIgnoreCase));

    // Персона по handle В КОНТЕКСТЕ (глобальные + проектные projectId). Именно так резолвятся
    // @упоминания и persona_ask: две проектные «маши» из разных проектов не путаются.
    // Страховка на случай остаточных дублей: проектная приоритетнее глобальной.
    public Persona? GetByHandle(string userId, string handle, string? projectId) =>
        _personas.Values.Where(p => p.OwnerId == userId
                && string.Equals(p.Handle, handle, StringComparison.OrdinalIgnoreCase)
                && (p.Scope == PersonaScope.Global
                    || (p.Scope == PersonaScope.Project && p.ProjectId == projectId)))
            .OrderByDescending(p => p.Scope == PersonaScope.Project)
            .FirstOrDefault();

    // Пул персон, ДОСТИЖИМЫХ из контекста вызывающего: глобальные + текущего проекта (как
    // GetForContext) + внешние кросс-проектные scope-ы (ProjectPersonas-привязки вызывающей
    // персоны): extraProjectIds — вся команда проекта, extraPersonaIds — точечные персоны.
    // Общая точка правды для persona_ask (и по handle, и по personaId) — обходить её нельзя,
    // иначе personaId стал бы лазейкой мимо привязок в ЛЮБУЮ персону владельца.
    private List<Persona> AccessiblePool(string userId, string? projectId,
        IReadOnlyList<string>? extraProjectIds, IReadOnlyList<string>? extraPersonaIds)
    {
        var pool = GetForContext(userId, projectId).ToList();
        if (extraProjectIds is { Count: > 0 } || extraPersonaIds is { Count: > 0 })
        {
            var seen = pool.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            var extraPersonaSet = (extraPersonaIds ?? []).ToHashSet(StringComparer.Ordinal);
            foreach (var p in GetByOwner(userId))
            {
                if (seen.Contains(p.Id)) continue;
                var included = extraPersonaSet.Contains(p.Id)
                    || (p.Scope == PersonaScope.Project && p.ProjectId is not null
                        && extraProjectIds is not null && extraProjectIds.Contains(p.ProjectId));
                if (!included) continue;
                pool.Add(p);
                seen.Add(p.Id);
            }
        }
        return pool;
    }

    // Кандидаты по handle в достижимом пуле (см. AccessiblePool). Используется persona_ask
    // при возможной коллизии handle между проектами — 0 совпадений, 1 (однозначно) или
    // >1 (клиент должен уточнить personaId).
    public IReadOnlyList<Persona> ResolveHandleCandidates(string userId, string handle, string? projectId,
        IReadOnlyList<string>? extraProjectIds, IReadOnlyList<string>? extraPersonaIds) =>
        AccessiblePool(userId, projectId, extraProjectIds, extraPersonaIds)
            .Where(p => string.Equals(p.Handle, handle, StringComparison.OrdinalIgnoreCase))
            .ToList();

    // Персона по id, ЕСЛИ она достижима из контекста вызывающего (см. AccessiblePool) — иначе
    // null, даже если персона существует и принадлежит тому же владельцу. Однозначный путь
    // persona_ask (personaId вместо handle) обязан идти через ту же проверку, что и резолв по
    // handle — иначе personaId стал бы обходом кросс-проектных привязок.
    public Persona? GetReachable(string userId, string id, string? projectId,
        IReadOnlyList<string>? extraProjectIds, IReadOnlyList<string>? extraPersonaIds) =>
        AccessiblePool(userId, projectId, extraProjectIds, extraPersonaIds)
            .FirstOrDefault(p => p.Id == id);

    // Известные ключи возможностей персоны. Полный набор эквивалентен «без ограничений»
    // и нормализуется в null (поведение как раньше, по фич-флагам владельца).
    private static readonly string[] AllTools = ["tasks", "notes", "web"];

    private static List<string>? NormalizeTools(List<string>? tools)
    {
        if (tools is null) return null;
        var clean = tools.Select(t => t.Trim().ToLowerInvariant())
            .Where(t => AllTools.Contains(t)).Distinct().ToList();
        return AllTools.All(clean.Contains) ? null : clean;
    }

    // Нормализация контракта (P1): трим слотов, выброс пустых элементов списков;
    // полностью пустой контракт эквивалентен отсутствию → null (legacy-режим).
    internal static PersonaContract? NormalizeContract(PersonaContract? contract)
    {
        if (contract is null) return null;
        var clean = new PersonaContract
        {
            Character = TrimToNull(contract.Character),
            Tone = TrimToNull(contract.Tone),
            MustDo = CleanList(contract.MustDo),
            MustNot = CleanList(contract.MustNot),
            OutputFormat = TrimToNull(contract.OutputFormat),
            SpeechExamples = CleanList(contract.SpeechExamples),
            Instructions = TrimToNull(contract.Instructions),
        };
        return clean.IsEmpty ? null : clean;
    }

    private static string? TrimToNull(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // Нормализация пользовательского списка запретов (Custom-профиль):
    // трим, выброс пустых, дедуп; пустой список → null
    internal static List<string>? NormalizeDisallowed(List<string>? tools)
    {
        var clean = tools?.Select(t => t.Trim()).Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal).ToList();
        return clean is { Count: > 0 } ? clean : null;
    }

    private static List<string>? CleanList(List<string>? items)
    {
        var clean = items?.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).ToList();
        return clean is { Count: > 0 } ? clean : null;
    }

    public Persona Create(string userId, string name, string? role, string? description, string? systemPrompt,
        string? model, string? effort, PersonaScope scope, string? projectId,
        string? color, string? greeting, bool memoryEnabled, List<string>? tools = null,
        PersonaContract? contract = null, PersonaAccess access = PersonaAccess.Full,
        List<string>? disallowedTools = null, PersonaSpecialty specialty = PersonaSpecialty.None,
        bool allProjectsAccess = false, bool subagentExecutor = false, string? handle = null)
    {
        var persona = new Persona
        {
            OwnerId = userId,
            Name = string.IsNullOrWhiteSpace(name) ? "Персона" : name.Trim(),
            Role = string.IsNullOrWhiteSpace(role) ? null : role.Trim(),
            Description = description,
            SystemPrompt = systemPrompt,
            Contract = NormalizeContract(contract),
            Model = LlmProviderRegistry.StripClaudeWindowAlias(model),
            Effort = effort,
            Specialty = specialty,
            Scope = scope,
            ProjectId = scope == PersonaScope.Project ? projectId : null,
            Greeting = greeting,
            MemoryEnabled = memoryEnabled,
            Tools = NormalizeTools(tools),
            Access = access,
            // Свой список запретов имеет смысл только при Custom-профиле
            DisallowedTools = access == PersonaAccess.Custom ? NormalizeDisallowed(disallowedTools) : null,
            // Write-доступ в сабагентах — только при полном профиле доступа
            SubagentExecutor = access == PersonaAccess.Full && subagentExecutor,
            // Доступ ко всем проектам имеет смысл только у глобальных персон
            AllProjectsAccess = scope == PersonaScope.Global && allProjectsAccess,
            Avatar = new PersonaAvatar { Kind = PersonaAvatarKind.Initials, Color = color },
        };
        // Генерация handle и вставка атомарны: без лока два одновременных Create
        // с одинаковым именем вычислили бы один и тот же слаг (гонка TOCTOU)
        lock (_saveLock)
        {
            if (!string.IsNullOrWhiteSpace(handle))
            {
                var norm = NormalizeHandle(handle);
                if (norm is null || PersonaAgentFileSync.IsReserved(norm)
                    || OccupiedHandles(userId, persona.Scope, persona.ProjectId, null).Contains(norm))
                    throw new ArgumentException($"Handle @{handle} занят или невалиден");
                persona.Handle = norm;
                persona.HandleCustom = true;
            }
            else
            {
                persona.Handle = MakeUniqueHandle(persona.Name, userId, persona.Scope,
                    persona.ProjectId, null, persona.Role);
            }
            _personas[persona.Id] = persona;
        }
        Save();
        // Проектная персона — член команды проекта; попадает в активность-ленту
        if (persona.Scope == PersonaScope.Project && !string.IsNullOrEmpty(persona.ProjectId))
            _events?.Append(persona.ProjectId, userId, ProjectEventTypes.TeamJoined, "user",
                $"В команде: {PersonaLabel(persona)}", persona.Id);
        OnPersonaCreated?.Invoke(persona);
        return persona;
    }

    // Подпись персоны для логов: «Роль (Имя)» либо просто имя
    internal static string PersonaLabel(Persona p) =>
        string.IsNullOrEmpty(p.Role) ? p.Name : $"{p.Role} ({p.Name})";

    // --- Подключаемая команда «Пантеон OmO» (built-in-подход, как у самих OmO) ---

    // Персона владельца, подключённая из каталога по ключу (null — не подключена)
    public Persona? GetByTemplateKey(string userId, string templateKey) =>
        _personas.Values.FirstOrDefault(p => p.OwnerId == userId
            && string.Equals(p.TemplateKey, templateKey, StringComparison.OrdinalIgnoreCase));

    // Идемпотентное подключение ролей пантеона: создаёт ГЛОБАЛЬНЫЕ персоны для ключей,
    // которых у владельца ещё нет (keys == null — все роли каталога). Существующие не трогает.
    public IReadOnlyList<Persona> ConnectPantheon(string userId, IReadOnlyList<string>? keys = null)
    {
        var wanted = keys is { Count: > 0 }
            ? keys.Select(k => OmoPantheonCatalog.Get(k)
                ?? throw new KeyNotFoundException($"Роль пантеона не найдена: {k}")).ToList()
            : OmoPantheonCatalog.All.ToList();

        // Весь connect атомарен: параллельный вызов ждёт и находит уже созданных
        lock (_connectLock)
        {
            var result = new List<Persona>();
            foreach (var t in wanted)
            {
                var existing = GetByTemplateKey(userId, t.Key);
                if (existing is not null) { result.Add(existing); continue; }

                var persona = Create(userId, t.Name, t.Role, t.Description, systemPrompt: null,
                    t.Model, t.Effort, PersonaScope.Global, projectId: null,
                    t.Color, t.Greeting, memoryEnabled: true, t.Tools, t.Contract, t.Access,
                    specialty: t.Specialty);
                persona.TemplateKey = t.Key;
                persona.TemplateInstructionsHash = HashInstructions(t.Contract.Instructions);
                result.Add(persona);
            }
            Save();
            return result;
        }
    }

    // Авто-обновление регламентов подключённых ролей при изменении каталога (апдейт сервера):
    // нетронутая инструкция (hash совпадает с поставленной) заменяется каталожной; правленная
    // пользователем — «пришпилена» и не трогается (поведение prompt-оверрайдов OmO).
    private void RefreshPantheonInstructions()
    {
        var updated = 0;
        foreach (var persona in _personas.Values)
        {
            if (persona.TemplateKey is null || persona.TemplateInstructionsHash is null) continue;
            var template = OmoPantheonCatalog.Get(persona.TemplateKey);
            if (template?.Contract.Instructions is not { } fresh) continue;

            var current = persona.Contract?.Instructions;
            if (HashInstructions(current) != persona.TemplateInstructionsHash) continue; // пришпилено
            if (current == fresh) continue; // уже актуальна

            persona.Contract ??= new PersonaContract();
            persona.Contract.Instructions = fresh;
            persona.TemplateInstructionsHash = HashInstructions(fresh);
            persona.UpdatedAt = DateTime.UtcNow;
            updated++;
        }
        if (updated > 0)
        {
            Save();
            Console.WriteLine($"[PersonaManager] Пантеон: обновлены регламенты {updated} нетронутых персон(ы)");
        }
    }

    internal static string HashInstructions(string? instructions) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instructions?.Trim() ?? "")));

    public Persona Update(string id, string userId, string? name, string? role, string? description,
        string? systemPrompt, string? model, string? effort, PersonaScope? scope, string? projectId,
        string? color, string? greeting, bool? memoryEnabled, List<string>? tools = null,
        PersonaContract? contract = null, PersonaAccess? access = null,
        List<string>? disallowedTools = null, PersonaSpecialty? specialty = null,
        bool? allProjectsAccess = null, bool? subagentExecutor = null, string? handle = null)
    {
        var persona = Get(id, userId)
            ?? throw new KeyNotFoundException($"Персона не найдена: {id}");

        var oldHandle = persona.Handle;
        // Мутации полей — под _saveLock, чтобы параллельные читатели (Save/enumerate _personas)
        // не видели полу-обновлённую персону. Save/OnPersonaChanged — вне лока (внешний код).
        lock (_saveLock)
        {
            // Эффективный контекст уникальности после этого Update — для валидации handle.
            // Валидируем ДО мутаций полей: при ошибке персона остаётся нетронутой.
            var effScope = scope ?? persona.Scope;
            var effProjectId = effScope == PersonaScope.Project
                ? (scope is not null ? projectId : projectId ?? persona.ProjectId)
                : null;
            // "" — маркер сброса кастомного handle (авто-генерация ниже); непустой — ручной handle
            string? resolvedHandle = null;
            if (handle is not null)
            {
                if (handle.Trim().Length == 0) resolvedHandle = "";
                else
                {
                    var norm = NormalizeHandle(handle);
                    if (norm is null || PersonaAgentFileSync.IsReserved(norm)
                        || OccupiedHandles(userId, effScope, effProjectId, persona.Id).Contains(norm))
                        throw new ArgumentException($"Handle @{handle} занят или невалиден");
                    resolvedHandle = norm;
                }
            }

            if (name is not null) persona.Name = string.IsNullOrWhiteSpace(name) ? persona.Name : name.Trim();
            if (role is not null) persona.Role = role.Length == 0 ? null : role.Trim();
            if (description is not null) persona.Description = description;
            if (systemPrompt is not null) persona.SystemPrompt = systemPrompt;
            // null — не менять; объект с пустыми слотами — сбросить контракт (нормализуется в null)
            if (contract is not null) persona.Contract = NormalizeContract(contract);
            if (model is not null) persona.Model = model.Length == 0 ? null : LlmProviderRegistry.StripClaudeWindowAlias(model);
            if (effort is not null) persona.Effort = effort.Length == 0 ? null : effort;
            // Специальность (функциональная роль): null — не менять; None — сбросить явно
            if (specialty is not null) persona.Specialty = specialty.Value;
            if (scope is not null)
            {
                persona.Scope = scope.Value;
                persona.ProjectId = scope.Value == PersonaScope.Project ? projectId : null;
                // Доступ ко всем проектам имеет смысл только у глобальных персон
                if (scope.Value == PersonaScope.Project) persona.AllProjectsAccess = false;
            }
            else if (projectId is not null && persona.Scope == PersonaScope.Project)
            {
                persona.ProjectId = projectId;
            }
            if (color is not null) persona.Avatar.Color = color.Length == 0 ? null : color;
            if (greeting is not null) persona.Greeting = greeting.Length == 0 ? null : greeting;
            if (memoryEnabled is not null) persona.MemoryEnabled = memoryEnabled.Value;
            // null — не менять; список — установить (полный набор нормализуется в null)
            if (tools is not null) persona.Tools = NormalizeTools(tools);
            // Профиль доступа: null — не менять; свой список запретов живёт только при Custom
            if (access is not null) persona.Access = access.Value;
            if (disallowedTools is not null) persona.DisallowedTools = NormalizeDisallowed(disallowedTools);
            if (persona.Access != PersonaAccess.Custom) persona.DisallowedTools = null;
            // Write-доступ в сабагентах: null — не менять; при не-Full профиле гаснет всегда
            if (subagentExecutor is not null) persona.SubagentExecutor = subagentExecutor.Value;
            if (persona.Access != PersonaAccess.Full) persona.SubagentExecutor = false;
            // null — не менять; иначе — только для глобальной персоны (после применения scope выше)
            if (allProjectsAccess is not null)
                persona.AllProjectsAccess = allProjectsAccess.Value && persona.Scope == PersonaScope.Global;

            // Handle: ручной ввод приоритетен; сброс ("") → авто-генерация; иначе авто-починка,
            // если смена scope/projectId столкнула авто-handle с чужим в новом контексте.
            if (resolvedHandle == "")
            {
                persona.HandleCustom = false;
                persona.Handle = MakeUniqueHandle(persona.Name, userId, persona.Scope,
                    persona.ProjectId, persona.Id, persona.Role);
            }
            else if (resolvedHandle is not null)
            {
                persona.Handle = resolvedHandle;
                persona.HandleCustom = true;
            }
            else if (!persona.HandleCustom
                     && OccupiedHandles(userId, persona.Scope, persona.ProjectId, persona.Id).Contains(persona.Handle))
            {
                persona.Handle = MakeUniqueHandle(persona.Name, userId, persona.Scope,
                    persona.ProjectId, persona.Id, persona.Role);
            }
            persona.UpdatedAt = DateTime.UtcNow;
        }
        Save();
        // Сначала чистка старых .md по прежнему handle, затем запись новых через OnPersonaChanged
        if (!string.Equals(oldHandle, persona.Handle, StringComparison.Ordinal))
            OnPersonaHandleChanged?.Invoke(persona, oldHandle);
        OnPersonaChanged?.Invoke(persona);
        return persona;
    }

    // Полная замена привязок персоны (фича persona-bindings); сохранение мгновенное.
    // Пустой список нормализуется в null (поведение как без привязок).
    public Persona UpdateBindings(string id, string userId, List<PersonaBinding>? bindings)
    {
        var persona = Get(id, userId)
            ?? throw new KeyNotFoundException($"Персона не найдена: {id}");
        lock (_saveLock)
        {
            persona.Bindings = bindings is { Count: > 0 } ? bindings : null;
            persona.UpdatedAt = DateTime.UtcNow;
        }
        Save();
        OnPersonaChanged?.Invoke(persona);
        return persona;
    }

    // Полная замена правил автоматизации персоны (событийно-управляемая проактивность);
    // сохранение мгновенное. Пустой список → null (поведение как без правил).
    // Конфигурация только: runtime-состояние (LastFiredAt/счётчики/снапшоты) живёт отдельно
    // в PersonaAutomationService, чтобы не переписывать personas.json на каждом тике.
    public Persona UpdateRules(string id, string userId, List<PersonaAutomationRule>? rules)
    {
        var persona = Get(id, userId)
            ?? throw new KeyNotFoundException($"Персона не найдена: {id}");
        lock (_saveLock)
        {
            if (rules is { Count: > 0 })
            {
                // Пустое условие нормализуется в null (как пустой контракт/набор инструментов)
                foreach (var r in rules)
                    if (r.Condition is { } c && c.IsEmpty) r.Condition = null;
                persona.AutomationRules = rules;
            }
            else persona.AutomationRules = null;
            persona.UpdatedAt = DateTime.UtcNow;
        }
        Save();
        OnPersonaChanged?.Invoke(persona);
        return persona;
    }

    // Установить сгенерированный аватар-картинку. Оригинал/кроп загруженного файла
    // при этом теряют смысл — чистим (и файл оригинала тоже).
    public Persona SetAvatarImage(string id, string userId, string imageFile)
    {
        var persona = Get(id, userId)
            ?? throw new KeyNotFoundException($"Персона не найдена: {id}");
        DeleteAsset(id, persona.Avatar.OriginalFile, keep: null);
        persona.Avatar.Kind = PersonaAvatarKind.Image;
        persona.Avatar.ImageFile = imageFile;
        persona.Avatar.OriginalFile = null;
        persona.Avatar.Crop = null;
        persona.UpdatedAt = DateTime.UtcNow;
        Save();
        return persona;
    }

    // Загруженный аватар: кропнутая картинка + оригинал (для перекропа) + параметры кропа.
    // Прежние avatar-*/original-* файлы удаляются.
    public Persona SetAvatarUploaded(string id, string userId, string imageFile, string originalFile,
        AvatarCropState? crop)
    {
        var persona = Get(id, userId)
            ?? throw new KeyNotFoundException($"Персона не найдена: {id}");
        DeleteAsset(id, persona.Avatar.ImageFile, keep: imageFile);
        DeleteAsset(id, persona.Avatar.OriginalFile, keep: originalFile);
        persona.Avatar.Kind = PersonaAvatarKind.Image;
        persona.Avatar.ImageFile = imageFile;
        persona.Avatar.OriginalFile = originalFile;
        persona.Avatar.Crop = crop;
        persona.UpdatedAt = DateTime.UtcNow;
        Save();
        return persona;
    }

    // Перекроп существующего оригинала: заменяется только кропнутая картинка и параметры.
    public Persona SetAvatarRecropped(string id, string userId, string imageFile, AvatarCropState? crop)
    {
        var persona = Get(id, userId)
            ?? throw new KeyNotFoundException($"Персона не найдена: {id}");
        DeleteAsset(id, persona.Avatar.ImageFile, keep: imageFile);
        persona.Avatar.Kind = PersonaAvatarKind.Image;
        persona.Avatar.ImageFile = imageFile;
        persona.Avatar.Crop = crop;
        persona.UpdatedAt = DateTime.UtcNow;
        Save();
        return persona;
    }

    // Удалить файл-ассет персоны (кроме keep); ошибки удаления не критичны
    private void DeleteAsset(string personaId, string? file, string? keep)
    {
        if (string.IsNullOrEmpty(file) || file == keep) return;
        try { File.Delete(Path.Combine(AssetsDir, personaId, file)); } catch { /* не критично */ }
    }

    public bool Delete(string id, string userId)
    {
        // Проверка владельца + удаление из словаря — атомарно под _saveLock (консистентно
        // с генерацией handle в Create и снапшотом в Save). Чистка ассетов/Save — вне лока.
        string? projectId = null;
        string? label = null;
        Persona? deleted;
        lock (_saveLock)
        {
            deleted = Get(id, userId);
            if (deleted is null) return false;
            // Запоминаем проектную принадлежность до удаления — для лога команды проекта
            if (deleted.Scope == PersonaScope.Project && !string.IsNullOrEmpty(deleted.ProjectId))
            {
                projectId = deleted.ProjectId;
                label = PersonaLabel(deleted);
            }
            _personas.TryRemove(id, out _);
        }
        if (projectId is not null && label is not null)
            _events?.Append(projectId, userId, ProjectEventTypes.TeamLeft, "user", $"Из команды: {label}", id);
        // Чистим ассеты персоны (аватар)
        try
        {
            var dir = Path.Combine(AssetsDir, id);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* не критично */ }
        Save();
        OnPersonaDeleted?.Invoke(deleted);
        return true;
    }

    // Зона уникальности handle. Глобальная персона видна и пишет файлы сабагентов во ВСЕ
    // проекты владельца — её handle не должен пересекаться ни с кем. Проектная конфликтует
    // только с глобальными и проектными СВОЕГО проекта (файлы в разных корнях, per-turn
    // контекст их не сводит вместе) — проектные других проектов могут делить handle.
    private HashSet<string> OccupiedHandles(string userId, PersonaScope scope, string? projectId, string? excludeId)
    {
        var scoped = _personas.Values.Where(p => p.OwnerId == userId && p.Id != excludeId);
        if (scope != PersonaScope.Global)
            scoped = scoped.Where(p => p.Scope == PersonaScope.Global
                || (p.Scope == PersonaScope.Project && p.ProjectId == projectId));
        return scoped.Select(p => p.Handle).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // Пересекаются ли зоны уникальности двух персон одного владельца (тогда одинаковый handle —
    // это коллизия). Глобальная видна везде → пересекается с любой; две проектные — только внутри
    // одного проекта. Симметрично.
    private static bool InUniquenessZone(Persona a, Persona b) =>
        a.OwnerId == b.OwnerId
        && (a.Scope == PersonaScope.Global || b.Scope == PersonaScope.Global
            || a.ProjectId == b.ProjectId);

    // Уникальный В КОНТЕКСТЕ slug из имени. При коллизии — осмысленный суффикс из роли
    // (masha-analitik), затем числовой (masha-2). Зону уникальности задаёт OccupiedHandles.
    private string MakeUniqueHandle(string name, string userId, PersonaScope scope,
        string? projectId, string? excludeId, string? role)
    {
        var baseSlug = Slugify(name);
        if (baseSlug.Length == 0) baseSlug = "agent";
        var occupied = OccupiedHandles(userId, scope, projectId, excludeId);
        if (!occupied.Contains(baseSlug)) return baseSlug;

        // Осмысленный суффикс из роли — вместо безликой цифры
        var roleSlug = Slugify(role ?? "");
        if (roleSlug.Length > 0 && !roleSlug.Equals(baseSlug, StringComparison.OrdinalIgnoreCase))
        {
            var byRole = $"{baseSlug}-{roleSlug}";
            if (!occupied.Contains(byRole)) return byRole;
        }
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseSlug}-{i}";
            if (!occupied.Contains(candidate)) return candidate;
        }
    }

    // Нормализация пользовательского handle: тот же slug-алгоритм, что и автоген (транслит
    // кириллицы, латиница/цифры, дефисы). null — если после нормализации пусто.
    public static string? NormalizeHandle(string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle)) return null;
        var slug = Slugify(handle.Trim().TrimStart('@'));
        return slug.Length == 0 ? null : slug;
    }

    // Свободен ли handle для ручного ввода: валиден, не зарезервирован встроенным агентом CLI
    // и не занят другой персоной в её зоне уникальности.
    public bool IsHandleAvailable(string userId, string handle, PersonaScope scope,
        string? projectId, string? excludeId)
    {
        var norm = NormalizeHandle(handle);
        if (norm is null || PersonaAgentFileSync.IsReserved(norm)) return false;
        return !OccupiedHandles(userId, scope, projectId, excludeId).Contains(norm);
    }

    // Лёгкий клон персоны с другим handle — чтобы PersonaAgentFileSync.ResolvePaths указал на
    // СТАРЫЕ файлы сабагента (для их удаления при переименовании handle / миграции).
    public static Persona WithHandle(Persona p, string handle) => new()
    {
        Id = p.Id,
        OwnerId = p.OwnerId,
        Scope = p.Scope,
        ProjectId = p.ProjectId,
        Handle = handle,
    };

    // Транслитерация кириллицы для slug: без неё русские имена давали handle «agent»,
    // и @упоминания превращались в безликие @agent-2
    private static readonly Dictionary<char, string> Translit = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e",
        ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m",
        ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u",
        ['ф'] = "f", ['х'] = "h", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch",
        ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    // public: переиспользуется GitServerService для имён репозиториев (транслит кириллицы)
    public static string Slugify(string s)
    {
        var sb = new StringBuilder();
        var prevDash = false;
        foreach (var ch in s.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) && ch < 128)
            {
                sb.Append(ch);
                prevDash = false;
            }
            else if (Translit.TryGetValue(ch, out var tr))
            {
                if (tr.Length > 0) { sb.Append(tr); prevDash = false; }
            }
            else if (!prevDash && sb.Length > 0)
            {
                sb.Append('-');
                prevDash = true;
            }
        }
        return sb.ToString().Trim('-');
    }

    private void Load()
    {
        var list = JsonFileStore.Load<List<Persona>>(_storePath, JsonOpts);
        if (list is null) return;

        // Проход 1: вставить всех как есть — генерация handle ниже должна видеть
        // ВСЕ занятые handle, иначе legacy-персона без handle могла занять чужой
        foreach (var p in list)
        {
            p.Avatar ??= new PersonaAvatar();
            _personas[p.Id] = p;
        }

        var changed = false;

        // Проход 2: миграция legacy-персон без handle (детерминированно по CreatedAt)
        foreach (var p in _personas.Values.Where(p => string.IsNullOrEmpty(p.Handle))
                     .OrderBy(p => p.CreatedAt).ToList())
        {
            p.Handle = MakeUniqueHandle(p.Name, p.OwnerId, p.Scope, p.ProjectId, p.Id, p.Role);
            changed = true;
        }

        // Проход 3: самолечение КОНТЕКСТНЫХ дублей (глобальные между собой + проектные внутри
        // одного проекта). Cross-project дубли проектных персон легитимны — не трогаем. Идём по
        // CreatedAt: старейшая держит handle, младшим (конфликтующим с уже закреплёнными в их
        // зоне уникальности) перегенерируется. Ручной handle (HandleCustom) не переопределяем.
        var processed = new List<Persona>();
        foreach (var p in _personas.Values.OrderBy(p => p.CreatedAt).ToList())
        {
            if (p.HandleCustom || string.IsNullOrEmpty(p.Handle)) { processed.Add(p); continue; }
            var conflict = processed.Any(q => InUniquenessZone(p, q)
                && string.Equals(q.Handle, p.Handle, StringComparison.OrdinalIgnoreCase));
            if (conflict)
            {
                var old = p.Handle;
                p.Handle = MakeUniqueHandle(p.Name, p.OwnerId, p.Scope, p.ProjectId, p.Id, p.Role);
                _log?.LogWarning("Дубль handle в контексте у владельца {OwnerId}: «{Name}» @{Old} → @{New}",
                    p.OwnerId, p.Name, old, p.Handle);
                changed = true;
            }
            processed.Add(p);
        }

        // Проход 4: нормализация legacy-моделей — тир-алиас с окном (opus[1m]) → базовый (opus).
        // Рантайм и так стрипает окно перед --model, но в сторе оставалось «конкретное» значение.
        foreach (var p in _personas.Values.Where(p => !string.IsNullOrEmpty(p.Model)))
        {
            var norm = LlmProviderRegistry.StripClaudeWindowAlias(p.Model);
            if (!string.Equals(norm, p.Model, StringComparison.Ordinal))
            {
                p.Model = norm;
                changed = true;
            }
        }

        if (changed) Save();
    }

    // Одноразовая миграция под контекстное правило: снимает ЛИШНИЙ числовой суффикс с handle
    // (masha-2 → masha), если базовый свободен в зоне уникальности персоны. ТОЛЬКО суффикс:
    // «тело» handle не пересчитывается по имени, иначе осмысленные короткие handle (@mark у
    // «Марк Слиянский») превратились бы в длинные @mark-sliyanskiy. Детерминированно по CreatedAt
    // (старейшая с данной базой держит её). Идемпотентно: повторный вызов на минимизированных
    // данных возвращает пустой список. Ручной handle (HandleCustom) не трогает. Возвращает пары
    // (персона, старый handle) — вызывающий чистит по ним старые .md-файлы сабагентов.
    public IReadOnlyList<(Persona Persona, string OldHandle)> MigrateContextualHandles()
    {
        var renamed = new List<(Persona, string)>();
        lock (_saveLock)
        {
            foreach (var p in _personas.Values.OrderBy(p => p.CreatedAt).ToList())
            {
                if (p.HandleCustom || string.IsNullOrEmpty(p.Handle)) continue;
                // Снимаем только числовой хвост «-N»; тело handle не меняем
                var m = SuffixRe.Match(p.Handle);
                if (!m.Success) continue;
                var baseSlug = m.Groups[1].Value;
                // База не должна быть зарезервирована и должна быть свободна в контексте персоны
                if (PersonaAgentFileSync.IsReserved(baseSlug)) continue;
                if (OccupiedHandles(p.OwnerId, p.Scope, p.ProjectId, p.Id).Contains(baseSlug)) continue;
                var old = p.Handle;
                p.Handle = baseSlug;
                p.UpdatedAt = DateTime.UtcNow;
                renamed.Add((p, old));
                _log?.LogInformation("Миграция handle {OwnerId}: «{Name}» @{Old} → @{New}",
                    p.OwnerId, p.Name, old, baseSlug);
            }
            if (renamed.Count > 0)
                JsonFileStore.Save(_storePath, _personas.Values.ToList(), JsonOpts);
        }
        return renamed;
    }

    // Числовой суффикс handle: «masha-2» → база «masha»
    private static readonly System.Text.RegularExpressions.Regex SuffixRe =
        new(@"^(.+)-\d+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void Save()
    {
        lock (_saveLock)
            JsonFileStore.Save(_storePath, _personas.Values.ToList(), JsonOpts);
    }
}
