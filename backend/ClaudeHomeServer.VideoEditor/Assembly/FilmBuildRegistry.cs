using ClaudeHomeServer.Services.VideoEditor.Contracts;

namespace ClaudeHomeServer.Services.VideoEditor.Assembly;

// Реестр идущих и недавно законченных сборок в памяти (ADR-022 §4). Потолок МОДУЛЯ поверх слота общего
// BuildConcurrencyGate: 1 сборка на владельца, 2 на инстанс (ждущая слот тоже считается). Это НЕ второй
// семафор тяжёлых запусков: тот один на процесс и живёт в Core; здесь только «сколько сборок заявлено», чтобы
// очередь ожидающих не росла безлимитно. Реестр не переживает рестарт: сборка — процесс, после рестарта её нет,
// а готовый файл и запись builds[] в .film остаются.
public sealed class FilmBuildRegistry
{
    public const int MaxPerOwner = 1;
    public const int MaxPerInstance = 2;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public sealed class Entry(string key, string ownerId, CancellationTokenSource cts)
    {
        public string Key { get; } = key;
        public string OwnerId { get; } = ownerId;
        public CancellationTokenSource Cts { get; } = cts;
        public FilmBuildStatusDto Status { get; set; } = new(FilmBuildStates.Waiting, 0, null, null, null);
        public bool Active => Status.State is FilmBuildStates.Waiting or FilmBuildStates.Running;
    }

    public enum BeginStatus { Started, FilmBusy, OwnerLimit, InstanceLimit }

    private static string KeyOf(string ownerId, string projectId, string filmPath) => $"{ownerId}\n{projectId}\n{filmPath}";

    public FilmBuildStatusDto? Get(string ownerId, string projectId, string filmPath)
    {
        lock (_gate) return _entries.TryGetValue(KeyOf(ownerId, projectId, filmPath), out var e) ? e.Status : null;
    }

    // Заявка на сборку: проверка потолков и регистрация — одним шагом под замком. Та же плёнка не собирается
    // дважды разом; законченная запись заменяется новой
    public BeginStatus TryBegin(string ownerId, string projectId, string filmPath, DateTime nowUtc, out Entry? entry)
    {
        lock (_gate)
        {
            var key = KeyOf(ownerId, projectId, filmPath);
            entry = null;
            if (_entries.TryGetValue(key, out var existing) && existing.Active) return BeginStatus.FilmBusy;
            if (_entries.Values.Count(e => e.Active && e.OwnerId == ownerId) >= MaxPerOwner) return BeginStatus.OwnerLimit;
            if (_entries.Values.Count(e => e.Active) >= MaxPerInstance) return BeginStatus.InstanceLimit;
            entry = new Entry(key, ownerId, new CancellationTokenSource())
            {
                Status = new FilmBuildStatusDto(FilmBuildStates.Waiting, 0, null, null, nowUtc),
            };
            _entries[key] = entry;
            return BeginStatus.Started;
        }
    }

    public void Set(Entry entry, FilmBuildStatusDto status)
    {
        lock (_gate) entry.Status = status;
    }

    // Отмена идущей сборки: true — была активна и сигнал отправлен
    public bool Cancel(string ownerId, string projectId, string filmPath)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(KeyOf(ownerId, projectId, filmPath), out var entry) || !entry.Active) return false;
            entry.Cts.Cancel();
            return true;
        }
    }

    // Активные сборки владельца — для остановки при гашении сервера и для тестов
    public int ActiveCount(string? ownerId = null)
    {
        lock (_gate) return _entries.Values.Count(e => e.Active && (ownerId is null || e.OwnerId == ownerId));
    }
}
