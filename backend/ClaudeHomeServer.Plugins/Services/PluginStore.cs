using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Plugins;

// Файловое хранилище плагинов владельца (ADR-031 §2.3), per-owner:
//   data/plugins/{owner}/packages/{id}/{version}/  — неизменяемые пакеты
//   data/plugins/{owner}/installed.json             — записи установки (PluginInstall)
//   data/plugins/{owner}/_drafts/{slug}/           — черновики build-it-yourself
//
// ВСЕ пути — только через SafePath.Join (единственный примитив защиты от path
// traversal): путь с «..» отклоняется исключением, а не нормализуется в сосед.
// Корень data/ резолвится как у всех сервисов (DataPath → каталог projects.json).
//
// Пакеты не попадают ни в ~/.claude/skills, ни в data/claude-profiles/*/plugins
// (ADR-031 §2.5, инвариант 3): общие каталоги CLI зеркалятся синком и дали бы
// утечку между владельцами.
public class PluginStore
{
    // Имена каталогов/файла — контракт раскладки, не переопределяются.
    public const string RootDirName = "plugins";
    public const string PackagesDirName = "packages";
    public const string DraftsDirName = "_drafts";
    // Отклонённые черновики (шаг 4.3c): вне _drafts и вне наблюдения наблюдателя черновиков.
    public const string RejectedDirName = "_rejected";
    public const string UploadsDirName = "_uploads";
    public const string InstalledFileName = "installed.json";

    private readonly string _dataDir;
    private readonly object _lock = new();
    // S6: брокастер события «установка плагина изменилась» (plugins_changed) — необязательный:
    // DI авто-инъектит ISessionBroadcaster (Main), в тестах и выключенной подсистеме — null.
    private readonly ISessionBroadcaster? _broadcaster;

    public PluginStore(IConfiguration config, ISessionBroadcaster? broadcaster = null)
    {
        _dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        _broadcaster = broadcaster;
    }

    // Корень владельца: data/plugins/{owner}.
    public string OwnerRoot(string ownerId) =>
        SafePath.Join(_dataDir, $"{RootDirName}/{ownerId}");

    // Корень плагинов: data/plugins (родитель всех OwnerRoot). Наблюдатель черновиков
    // (шаг 4.2) поднимает слежку отсюда — один inotify-экземпляр на процесс.
    public string PluginsRoot => SafePath.Join(_dataDir, RootDirName);

    // Каталог пакета: data/plugins/{owner}/packages/{id}/{version}.
    // M3: формат id/version проверяется ДО SafePath.Join (единый метод PluginId/PluginVersion):
    // «..» в id или version отклоняется как 400-валидация, а не как UnauthorizedAccessException
    // (500) от SafePath.Join.
    public string PackageDir(string ownerId, string id, string version)
    {
        if (!PluginId.IsValid(id))
            throw new ArgumentException($"Недопустимый формат id пакета: {id}", nameof(id));
        if (!PluginVersion.IsValid(version))
            throw new ArgumentException($"Недопустимый формат version пакета: {version}", nameof(version));
        return SafePath.Join(OwnerRoot(ownerId), $"{PackagesDirName}/{id}/{version}");
    }

    // Каталог черновика: data/plugins/{owner}/_drafts/{slug}.
    public string DraftDir(string ownerId, string slug) =>
        SafePath.Join(OwnerRoot(ownerId), $"{DraftsDirName}/{slug}");

    // Каталог отклонённого черновика: data/plugins/{owner}/_rejected/{slug} (шаг 4.3c).
    // Вне _drafts (агентский тулсет его не видит) и вне наблюдения наблюдателя черновиков.
    public string RejectedDir(string ownerId, string slug) =>
        SafePath.Join(OwnerRoot(ownerId), $"{RejectedDirName}/{slug}");

    // Каталог загруженного пакета: data/plugins/{owner}/_uploads/{uploadId}.
    public string UploadDir(string ownerId, string uploadId) =>
        SafePath.Join(OwnerRoot(ownerId), $"{UploadsDirName}/{uploadId}");

    // Файл записей установки: data/plugins/{owner}/installed.json.
    public string InstalledFile(string ownerId) =>
        SafePath.Join(OwnerRoot(ownerId), InstalledFileName);

    // Записи установки владельца; пустой список — файла нет или он пуст.
    public IReadOnlyList<PluginInstall> LoadInstalled(string ownerId)
    {
        lock (_lock)
        {
            var path = InstalledFile(ownerId);
            return JsonFileStore.Load<List<PluginInstall>>(path) ?? [];
        }
    }

    // Сохранить записи установки владельца (целым списком, атомарно).
    // <paramref name="fireEvent"/>: false — не испускать plugins_changed (итерация 2:
    // PluginUninstall схлопывает каскад в одно событие, вызывая с fireEvent: false).
    public void SaveInstalled(string ownerId, IEnumerable<PluginInstall> records, bool fireEvent = true)
    {
        lock (_lock)
        {
            var path = InstalledFile(ownerId);
            JsonFileStore.Save(path, records.ToList());
        }
        if (fireEvent)
            NotifyChanged(ownerId);
    }

    // Load → modify → save под одной блокировкой: атомарное изменение записей владельца.
    // <paramref name="mutate"/> получает текущий список (мутабельный) и возвращает новый;
    // результат пишется в installed.json. Возвращает итоговый список.
    // virtual: тесты (QA-фикс L4) подменяют, чтобы посчитать число записей installed.json
    // за accept (один раз, не два).
    public virtual List<PluginInstall> Update(string ownerId, Func<List<PluginInstall>, List<PluginInstall>> mutate)
    {
        List<PluginInstall> result;
        lock (_lock)
        {
            var path = InstalledFile(ownerId);
            var records = (JsonFileStore.Load<List<PluginInstall>>(path) ?? []).ToList();
            result = mutate(records);
            JsonFileStore.Save(path, result);
        }
        // S6: Update всегда пишет (install/enable/disable) → событие после записи, вне lock'а.
        NotifyChanged(ownerId);
        return result;
    }

    // Load → mutate → save под одной блокировкой, возвращает произвольный результат
    // <typeparamref name="T"/> (minor 1: grant/enable проверяют и мутируют запись, результат —
    // не список, а исход операции). <paramref name="mutate"/> мутирует <paramref name="records"/>
    // на месте и возвращает результат; метод сохраняет (мутированный) <paramref name="records"/>
    // и отдаёт результат.
    // minor: сохранять ТОЛЬКО при изменении (сравнение старого и нового): no-op операция
    // (NotFound/BadRequest) не должна переписывать файл лишний раз.
    public T Update<T>(string ownerId, Func<List<PluginInstall>, T> mutate)
    {
        T result;
        bool changed;
        lock (_lock)
        {
            var path = InstalledFile(ownerId);
            var records = (JsonFileStore.Load<List<PluginInstall>>(path) ?? []).ToList();
            var before = JsonSerializer.Serialize(records);
            result = mutate(records);
            changed = JsonSerializer.Serialize(records) != before;
            if (changed)
                JsonFileStore.Save(path, records);
        }
        // S6: событие только при фактическом изменении (no-op grant/NotFound не шумит), вне lock'а.
        if (changed)
            NotifyChanged(ownerId);
        return result;
    }

    // S6: после успешной записи установки — событие «установка изменилась» ВЛАДЕЛЬЦУ (ToOwner),
    // вне lock'а (рассылка не держит замок стора). Без брокастера (тесты/выключенная подсистема)
    // молчит — запись уже на диске, событие потеряно не считается сбоем.
    private void NotifyChanged(string ownerId)
    {
        if (_broadcaster is { } b)
            _ = b.ToOwner(ownerId, new PluginsChangedMessage());
    }

    // Критическая секция на lock-е стора: сериализует операции, которые гоняются за одним и тем
    // же файлом/каталогом (например, move пакета при параллельной установке того же id).
    // <paramref name="action"/> выполняется под тем же <c>_lock</c>, что Load/Save/Update.
    public void RunExclusive(Action action)
    {
        lock (_lock)
        {
            action();
        }
    }
}
