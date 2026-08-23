using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Services.Dossiers;

// Последний виденный HEAD на РАБОЧЕЕ ДЕРЕВО (не на проект — ADR-004 §2, major-правка Глеба
// №3): чат с отдельным worktree коммитит в свою ветку, и HEAD корня проекта такого коммита
// не увидит. Ключ — {ownerId}:{projectId}:{hash(EffectiveRoot)}, тот же принцип, что у
// ключа снимка CodeGraph (ADR-003). Первое наблюдение дерева НЕ бэкфиллит историю (решение
// владельца «ретроспектива — только вперёд») — TickAsync просто фиксирует HEAD и выходит.
public sealed class DossierCaptureState
{
    private readonly string _path;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _map;

    public DossierCaptureState(IConfiguration config)
    {
        var dataDir = Path.GetDirectoryName(Path.GetFullPath(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json")))!;
        _path = Path.Combine(dataDir, "dossiers", "state.json");
        _map = JsonFileStore.Load<Dictionary<string, string>>(_path) ?? new();
    }

    public static string RootKey(string ownerId, string projectId, string root)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root))))[..16];
        return $"{ownerId}:{projectId}:{hash}";
    }

    // Ключ импортного tip ветки паспортов (автоимпорт по новому tip): {owner}:{project}
    // без дерева — ветка ccs/dossiers/v1 одна на весь репозиторий, наблюдение идёт от
    // корня проекта. Префикс "import:" разводит его с ключами захвата HEAD в той же
    // карте state.json (ownerId — GUID, с "import" никогда не совпадает).
    public static string ImportKey(string ownerId, string projectId) => $"import:{ownerId}:{projectId}";

    // Зафиксировать tip ветки паспортов как созданный НАШЕЙ выгрузкой (авто- или ручной):
    // автоимпорт сравнивает наблюдаемый tip с этим ключом и свой tip пропускает — иначе
    // петля «автовыгрузка двигает tip → автоимпорт видит "новый" tip» завозит импортированные
    // копии собственных паспортов (разбор 23.08). Чужой tip с git pull отличается и
    // импортируется штатно. Неизвестный tip (null/пусто — ветки нет) не фиксируется.
    public void MarkOwnTip(string ownerId, string projectId, string? tipSha)
    {
        if (string.IsNullOrWhiteSpace(tipSha)) return;
        Set(ImportKey(ownerId, projectId), tipSha);
    }

    public string? Get(string key)
    {
        lock (_lock) return _map.GetValueOrDefault(key);
    }

    // Compare-and-set для курсора импорта: обновить ключ, только если его значение не
    // менялось с момента чтения (expected — что видел вызывающий ДО долгой операции).
    // Гонка курсора (разбор консилиума 23.08): импорт ветки идёт десятки секунд, и
    // безусловная запись по его завершении затёрла бы MarkOwnTip параллельной выгрузки —
    // следующий тик посчитал бы собственную ветку чужой и завёз её второй копией
    // Imported-записей. Возвращает false, если значение успело измениться.
    public bool SetIfUnchanged(string key, string? expected, string value)
    {
        lock (_lock)
        {
            if (!string.Equals(_map.GetValueOrDefault(key), expected, StringComparison.Ordinal))
                return false;
            _map[key] = value;
            JsonFileStore.Save(_path, _map);
            return true;
        }
    }

    public void Set(string key, string headSha)
    {
        lock (_lock)
        {
            _map[key] = headSha;
            JsonFileStore.Save(_path, _map);
        }
    }
}
