using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Стор проектов «Личное» (ADR-040 §1): data/personal-projects.json рядом с projects.json,
// одна запись на владельца. Файл неизвестен старому бинарнику, поэтому откат кода «Личное»
// не показывает. Создаётся лениво и только на записывающих путях (GetPersonalOf не пишет).
public sealed class PersonalProjectStore : IPersonalProjects
{
    public const string FolderName = "Personal";
    public const string ProjectName = "Личное";

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _storePath;
    private readonly UserStore _users;
    private readonly UserHomeResolver _homes;
    private readonly Execution.SandboxManager? _sandbox;
    // ProjectManager зависит от стора (EnsureRootFree), поэтому обратная ссылка — ленивая
    private readonly IServiceProvider? _services;
    private readonly ILogger<PersonalProjectStore>? _log;

    private readonly Lock _gate = new();
    private readonly List<Project> _items = [];
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public PersonalProjectStore(IConfiguration config, UserStore users, UserHomeResolver homes,
        Execution.SandboxManager? sandbox = null, IServiceProvider? services = null,
        ILogger<PersonalProjectStore>? log = null)
    {
        _users = users;
        _homes = homes;
        _sandbox = sandbox;
        _services = services;
        _log = log;
        var projectsPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _storePath = Path.Combine(Path.GetDirectoryName(projectsPath)!, "personal-projects.json");
        Load();
    }

    public Project? GetPersonalOf(string ownerId)
    {
        lock (_gate) return _items.FirstOrDefault(p => p.OwnerId == ownerId);
    }

    public Project? GetByIdIncludingPersonal(string id)
    {
        lock (_gate) return _items.FirstOrDefault(p => p.Id == id);
    }

    public IReadOnlyCollection<Project> GetAllPersonal()
    {
        lock (_gate) return _items.ToList();
    }

    public bool DeleteOf(string ownerId)
    {
        lock (_gate)
        {
            if (_items.RemoveAll(p => p.OwnerId == ownerId) == 0) return false;
            Save();
            return true;
        }
    }

    public async Task<Project> EnsurePersonalAsync(string ownerId, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(ownerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var user = _users.GetById(ownerId)
                ?? throw new InvalidOperationException($"Пользователь не найден: {ownerId}");

            var existing = GetPersonalOf(ownerId);
            if (existing is not null)
            {
                // Папка могла пропасть (бэкап на другой машине), а путь — стать недопустимым
                if (!IsRootAllowed(user, existing.RootPath))
                {
                    var moved = ResolveFreeRoot(user);
                    lock (_gate)
                    {
                        existing.RootPath = moved;
                        existing.UpdatedAt = DateTime.UtcNow;
                        Save();
                    }
                }
                Directory.CreateDirectory(existing.RootPath);
                return existing;
            }

            var root = ResolveFreeRoot(user);
            Directory.CreateDirectory(root);
            var project = new Project
            {
                Name = ProjectName,
                RootPath = root,
                OwnerId = ownerId,
                Kind = ProjectKind.Personal,
            };
            lock (_gate)
            {
                _items.Add(project);
                Save();
            }
            return project;
        }
        finally
        {
            gate.Release();
        }
    }

    // {home}/Personal; при совпадении с корнем обычного проекта владельца — Personal-2, …
    private string ResolveFreeRoot(User user)
    {
        var home = _homes.Resolve(user)
            ?? throw new InvalidOperationException(UserHomeResolver.NotConfiguredMessage(user.ExecutionEnvironment));
        var taken = RegularFolderKeys(user.Id);
        for (var n = 1; ; n++)
        {
            var candidate = Path.GetFullPath(Path.Combine(home, n == 1 ? FolderName : $"{FolderName}-{n}"));
            if (!taken.Contains(ProjectCapabilities.FolderKey(null, candidate))) return candidate;
        }
    }

    private HashSet<string> RegularFolderKeys(string ownerId)
    {
        var projects = _services?.GetService(typeof(IProjectManager)) as IProjectManager;
        return projects is null
            ? new HashSet<string>()
            : projects.GetByOwner(ownerId).Select(ProjectCapabilities.FolderKey).ToHashSet();
    }

    // Container-пользователь: claude видит только Sandbox:ProjectsRoot
    private bool IsRootAllowed(User user, string rootPath)
    {
        if (_sandbox is null || user.ExecutionEnvironment != ExecutionEnvironments.Container) return true;
        var sandboxRoot = _sandbox.Options.ProjectsRoot;
        return !string.IsNullOrWhiteSpace(sandboxRoot) && UserHomeResolver.IsInside(rootPath, sandboxRoot);
    }

    private void Load()
    {
        var list = JsonFileStore.Load<List<Project>>(_storePath, ReadOptions);
        if (list is null) return;
        // Инвариант «одна запись на владельца»: при дубле (ручная правка файла) живёт старейшая
        foreach (var group in list.Where(p => !string.IsNullOrEmpty(p.OwnerId)).GroupBy(p => p.OwnerId))
        {
            var ordered = group.OrderBy(p => p.CreatedAt).ToList();
            if (ordered.Count > 1)
                _log?.LogWarning("personal-projects.json: у владельца {Owner} {Count} записей, оставлена старейшая",
                    group.Key, ordered.Count);
            var keep = ordered[0];
            keep.Kind = ProjectKind.Personal;
            keep.Icon ??= new();
            _items.Add(keep);
        }
    }

    private void Save() => JsonFileStore.Save(_storePath, _items.ToList());
}
