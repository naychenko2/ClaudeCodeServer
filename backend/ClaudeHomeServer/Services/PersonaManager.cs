using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
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
    private readonly ILogger<PersonaManager>? _log;

    public PersonaManager(IConfiguration config, ILogger<PersonaManager>? log = null)
    {
        _log = log;
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

    // Персона владельца по handle (@упоминания). Handle уникален per-owner.
    public Persona? GetByHandle(string userId, string handle) =>
        _personas.Values.FirstOrDefault(p => p.OwnerId == userId
            && string.Equals(p.Handle, handle, StringComparison.OrdinalIgnoreCase));

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
        List<string>? disallowedTools = null)
    {
        var persona = new Persona
        {
            OwnerId = userId,
            Name = string.IsNullOrWhiteSpace(name) ? "Персона" : name.Trim(),
            Role = string.IsNullOrWhiteSpace(role) ? null : role.Trim(),
            Description = description,
            SystemPrompt = systemPrompt,
            Contract = NormalizeContract(contract),
            Model = model,
            Effort = effort,
            Scope = scope,
            ProjectId = scope == PersonaScope.Project ? projectId : null,
            Greeting = greeting,
            MemoryEnabled = memoryEnabled,
            Tools = NormalizeTools(tools),
            Access = access,
            // Свой список запретов имеет смысл только при Custom-профиле
            DisallowedTools = access == PersonaAccess.Custom ? NormalizeDisallowed(disallowedTools) : null,
            Avatar = new PersonaAvatar { Kind = PersonaAvatarKind.Initials, Color = color },
        };
        // Генерация handle и вставка атомарны: без лока два одновременных Create
        // с одинаковым именем вычислили бы один и тот же слаг (гонка TOCTOU)
        lock (_saveLock)
        {
            persona.Handle = MakeUniqueHandle(persona.Name, userId);
            _personas[persona.Id] = persona;
        }
        Save();
        return persona;
    }

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

        var result = new List<Persona>();
        foreach (var t in wanted)
        {
            var existing = GetByTemplateKey(userId, t.Key);
            if (existing is not null) { result.Add(existing); continue; }

            var persona = Create(userId, t.Name, t.Role, t.Description, systemPrompt: null,
                t.Model, t.Effort, PersonaScope.Global, projectId: null,
                t.Color, t.Greeting, memoryEnabled: true, t.Tools, t.Contract, t.Access);
            persona.TemplateKey = t.Key;
            persona.TemplateInstructionsHash = HashInstructions(t.Contract.Instructions);
            result.Add(persona);
        }
        Save();
        return result;
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
        List<string>? disallowedTools = null)
    {
        var persona = Get(id, userId)
            ?? throw new KeyNotFoundException($"Персона не найдена: {id}");

        // Мутации полей — под _saveLock, чтобы параллельные читатели (Save/enumerate _personas)
        // не видели полу-обновлённую персону. Save/OnPersonaChanged — вне лока (внешний код).
        lock (_saveLock)
        {
            if (name is not null) persona.Name = string.IsNullOrWhiteSpace(name) ? persona.Name : name.Trim();
            if (role is not null) persona.Role = role.Length == 0 ? null : role.Trim();
            if (description is not null) persona.Description = description;
            if (systemPrompt is not null) persona.SystemPrompt = systemPrompt;
            // null — не менять; объект с пустыми слотами — сбросить контракт (нормализуется в null)
            if (contract is not null) persona.Contract = NormalizeContract(contract);
            if (model is not null) persona.Model = model.Length == 0 ? null : model;
            if (effort is not null) persona.Effort = effort.Length == 0 ? null : effort;
            if (scope is not null)
            {
                persona.Scope = scope.Value;
                persona.ProjectId = scope.Value == PersonaScope.Project ? projectId : null;
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
            persona.UpdatedAt = DateTime.UtcNow;
        }
        Save();
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
        lock (_saveLock)
        {
            var persona = Get(id, userId);
            if (persona is null) return false;
            _personas.TryRemove(id, out _);
        }
        // Чистим ассеты персоны (аватар)
        try
        {
            var dir = Path.Combine(AssetsDir, id);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* не критично */ }
        Save();
        return true;
    }

    // Уникальный per-owner slug из имени (латиница/цифры/дефис); коллизии — суффиксом -2, -3…
    private string MakeUniqueHandle(string name, string userId)
    {
        var baseSlug = Slugify(name);
        if (baseSlug.Length == 0) baseSlug = "agent";
        var existing = _personas.Values
            .Where(p => p.OwnerId == userId)
            .Select(p => p.Handle)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseSlug)) return baseSlug;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseSlug}-{i}";
            if (!existing.Contains(candidate)) return candidate;
        }
    }

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

    private static string Slugify(string s)
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

        // Проход 2: миграция legacy-персон без handle
        foreach (var p in _personas.Values.Where(p => string.IsNullOrEmpty(p.Handle)))
        {
            p.Handle = MakeUniqueHandle(p.Name, p.OwnerId);
            changed = true;
        }

        // Проход 3: самолечение дублей handle (могли появиться до фикса миграции и гонки
        // Create): handle остаётся у самой старой персоны, остальным перегенерируется
        foreach (var group in _personas.Values
                     .GroupBy(p => (p.OwnerId, Handle: p.Handle.ToLowerInvariant()))
                     .Where(g => g.Count() > 1).ToList())
        {
            foreach (var p in group.OrderBy(p => p.CreatedAt).Skip(1))
            {
                var old = p.Handle;
                p.Handle = MakeUniqueHandle(p.Name, p.OwnerId);
                _log?.LogWarning("Дубль handle у персон владельца {OwnerId}: «{Name}» переименована @{Old} → @{New}",
                    p.OwnerId, p.Name, old, p.Handle);
                changed = true;
            }
        }

        if (changed) Save();
    }

    private void Save()
    {
        lock (_saveLock)
            JsonFileStore.Save(_storePath, _personas.Values.ToList(), JsonOpts);
    }
}
