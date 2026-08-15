using System.Text.Json;

namespace ClaudeHomeServer.Services.Images;

// Виды сущностей, картинку которым догоняет фоновая генерация.
public static class ImageBackfillKinds
{
    public const string ProjectIcon = "project-icon";
    public const string PersonaAvatar = "persona-avatar";

    public static bool IsKnown(string? kind) =>
        kind is ProjectIcon or PersonaAvatar;
}

// Заявка очереди: «этой сущности не хватает картинки». Живёт до тех пор, пока картинка
// не появилась (сгенерировали или человек загрузил свою), сущность не удалили или не
// исчерпан пожизненный потолок попыток.
public sealed class ImageBackfillRequest
{
    // ImageBackfillKinds.*
    public string Kind { get; set; } = "";
    public string EntityId { get; set; } = "";
    // Владелец сущности: по нему группируются прогоны и адресуется событие фронту
    public string OwnerId { get; set; } = "";
    // Промпт генерации от инициатора; пусто — соберём из имени сущности
    public string? Prompt { get; set; }
    // Сколько раз генерация уже проваливалась (пожизненно, а не в рамках прогона)
    public int Attempts { get; set; }
    public string? LastFailReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Очередь догоняющей генерации картинок: data/image-backfill.json.
//
// Бэкап: в основной архив едет по общему правилу (BackupPaths.ShouldInclude работает от
// обратного), исключений не требует — файл мелкий, секретов не содержит, а восстановленная
// очередь просто доведёт картинки на новой машине. Ломающее изменение структуры =
// инкремент FormatVersion (правило BackupSchema).
//
// Образец — ImageGenerationSettingsStore: снимок неизменяемый, запись целиком под локом.
public sealed class ImageBackfillStore
{
    // Версия формата файла; инкремент при ломающем изменении структуры
    public const int FormatVersion = 1;
    public const string FileName = "image-backfill.json";

    public sealed class ImageBackfillFile
    {
        public int Version { get; set; } = FormatVersion;
        public List<ImageBackfillRequest> Requests { get; set; } = [];
    }

    private readonly string _storePath;
    private readonly ILogger<ImageBackfillStore>? _log;
    private readonly Lock _writeLock = new();
    private volatile ImageBackfillFile _file = new();

    public ImageBackfillStore(IConfiguration config, ILogger<ImageBackfillStore>? log = null)
    {
        _log = log;
        // Путь выводим ТОЛЬКО от DataPath (как остальные сторы): иначе файл ляжет рядом
        // с исполняемым и очередь потеряется при деплое.
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _storePath = Path.Combine(Path.GetDirectoryName(dataPath)!, FileName);
        Load();
    }

    /// <summary>Все заявки очереди (снимок).</summary>
    public IReadOnlyList<ImageBackfillRequest> All => _file.Requests;

    /// <summary>Владельцы, у которых есть заявки — по ним идёт прогон.</summary>
    public IReadOnlyList<string> Owners =>
        _file.Requests.Select(r => r.OwnerId).Distinct(StringComparer.Ordinal).ToList();

    public IReadOnlyList<ImageBackfillRequest> ByOwner(string ownerId) =>
        _file.Requests.Where(r => string.Equals(r.OwnerId, ownerId, StringComparison.Ordinal)).ToList();

    public ImageBackfillRequest? Find(string kind, string entityId) =>
        _file.Requests.FirstOrDefault(r => Matches(r, kind, entityId));

    /// <summary>
    /// Поставить заявку в очередь. Идемпотентно: повторная постановка на ту же сущность
    /// дубля не создаёт (обновляет промпт, если прислали новый). false — заявка уже была.
    /// </summary>
    public bool Enqueue(string kind, string entityId, string ownerId, string? prompt)
    {
        if (!ImageBackfillKinds.IsKnown(kind)
            || string.IsNullOrWhiteSpace(entityId) || string.IsNullOrWhiteSpace(ownerId))
            return false;

        lock (_writeLock)
        {
            var existing = _file.Requests.FirstOrDefault(r => Matches(r, kind, entityId));
            if (existing is not null)
            {
                if (string.IsNullOrWhiteSpace(prompt) || existing.Prompt == prompt.Trim()) return false;
                Write(_file.Requests
                    .Select(r => Matches(r, kind, entityId) ? Copy(r, prompt.Trim()) : r)
                    .ToList());
                return false;
            }

            Write([.. _file.Requests, new ImageBackfillRequest
            {
                Kind = kind,
                EntityId = entityId,
                OwnerId = ownerId,
                Prompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt.Trim(),
            }]);
            return true;
        }
    }

    /// <summary>Снять заявку (картинка появилась, сущность исчезла или повторять бессмысленно).</summary>
    public void Remove(string kind, string entityId)
    {
        lock (_writeLock)
        {
            var next = _file.Requests.Where(r => !Matches(r, kind, entityId)).ToList();
            if (next.Count == _file.Requests.Count) return;
            Write(next);
        }
    }

    /// <summary>
    /// Отметить неудачную попытку: Attempts += 1, причина сохраняется. Возвращает новое
    /// значение Attempts (0 — заявки уже нет), по нему вызывающий сверяется с потолком.
    /// </summary>
    public int MarkFailed(string kind, string entityId, string reason)
    {
        lock (_writeLock)
        {
            var existing = _file.Requests.FirstOrDefault(r => Matches(r, kind, entityId));
            if (existing is null) return 0;

            var attempts = existing.Attempts + 1;
            Write(_file.Requests
                .Select(r => Matches(r, kind, entityId) ? Copy(r, r.Prompt, attempts, reason) : r)
                .ToList());
            return attempts;
        }
    }

    private static bool Matches(ImageBackfillRequest r, string kind, string entityId) =>
        string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase)
        && string.Equals(r.EntityId, entityId, StringComparison.Ordinal);

    private static ImageBackfillRequest Copy(ImageBackfillRequest r, string? prompt,
        int? attempts = null, string? failReason = null) =>
        new()
        {
            Kind = r.Kind,
            EntityId = r.EntityId,
            OwnerId = r.OwnerId,
            Prompt = prompt,
            Attempts = attempts ?? r.Attempts,
            LastFailReason = failReason ?? r.LastFailReason,
            CreatedAt = r.CreatedAt,
        };

    // Подменить снимок и записать файл. Вызывать только под _writeLock.
    private void Write(List<ImageBackfillRequest> requests)
    {
        var next = new ImageBackfillFile { Version = FormatVersion, Requests = requests };
        _file = next;
        try
        {
            JsonFileStore.Save(_storePath, next);
        }
        catch (Exception ex)
        {
            // Очередь уже применена в памяти — теряем только персистентность до рестарта
            _log?.LogError(ex, "Не удалось записать {Path}", _storePath);
        }
    }

    // Чтение устойчиво к регистру имён свойств: стор пишет PascalCase, а руками файл
    // правят в camelCase (ловушка ImageGenerationSettingsStore).
    private static readonly JsonSerializerOptions LoadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private void Load()
    {
        var file = JsonFileStore.Load<ImageBackfillFile>(_storePath, LoadOptions, _log);
        if (file is null) return;
        if (file.Version > FormatVersion)
        {
            // Файл снят более новым кодом (восстановлен из свежего бэкапа): незнакомую
            // структуру не применяем — пустая очередь безопаснее.
            _log?.LogWarning(
                "{File} имеет формат {FileVersion} новее поддерживаемого {Version} — стартую с пустой очередью",
                FileName, file.Version, FormatVersion);
            return;
        }
        file.Requests = file.Requests?
            .Where(r => ImageBackfillKinds.IsKnown(r.Kind)
                && !string.IsNullOrWhiteSpace(r.EntityId) && !string.IsNullOrWhiteSpace(r.OwnerId))
            .ToList() ?? [];
        _file = file;
    }
}
