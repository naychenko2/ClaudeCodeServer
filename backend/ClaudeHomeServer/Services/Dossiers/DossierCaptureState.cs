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

    public string? Get(string key)
    {
        lock (_lock) return _map.GetValueOrDefault(key);
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
