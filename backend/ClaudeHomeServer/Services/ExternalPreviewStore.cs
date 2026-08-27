using System.Text.Json;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Выданная ссылка внешнего доступа. Самого токена здесь НЕТ — он у клиента, а мы храним
/// только его опознавательные данные. Поэтому файл реестра не секрет и в
/// <c>BackupPaths.SecretFileNames</c> не просится: по нему нельзя войти, им можно только
/// закрыть доступ.
/// </summary>
public sealed record ExternalPreviewLink(
    string Jti,
    string UserId,
    string ProjectId,
    string ServiceId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt <= now;
}

/// <summary>
/// Помнит, какие ссылки внешнего доступа сейчас живы (<c>data/external-preview.json</c>).
///
/// Зачем реестр, если ссылка и так подписана и с сроком. Подписанный JWT НЕ отзывается:
/// выдал на 12 часов — значит 12 часов он рабочий, что бы владелец ни делал. Для фичи,
/// выставляющей рабочую машину в интернет, это неприемлемо, поэтому живость ссылки
/// определяет присутствие её jti здесь: удалили запись — доступ умер на следующем же запросе.
///
/// Состояние держится В ПАМЯТИ и сбрасывается в файл при изменениях: сверка идёт на КАЖДОМ
/// запросе проксируемого сайта (а это сотни запросов на одну страницу), и чтение файла на
/// каждый из них было бы дефектом.
///
/// Файл, а не только память — потому что выкатка на бой гасит продукт, и без него каждая
/// публикация молча убивала бы все открытые ссылки.
/// </summary>
public sealed class ExternalPreviewStore
{
    /// <summary>
    /// Потолок живых ссылок на владельца. Смысл не в экономии места, а в том, что забытая
    /// открытая наружу витрина — это риск: пусть их будет столько, сколько можно удержать
    /// в голове. Одиннадцатая вытесняет самую старую (см. <see cref="Add"/>).
    /// </summary>
    public const int MaxPerOwner = 10;

    private readonly string _path;
    private readonly ILogger<ExternalPreviewStore> _log;
    private readonly Lock _lock = new();
    private Dictionary<string, ExternalPreviewLink> _links = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ExternalPreviewStore(IConfiguration config, ILogger<ExternalPreviewStore> log)
    {
        _log = log;
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "external-preview.json");
        Load();
    }

    /// <summary>
    /// Зарегистрировать выданную ссылку. Возвращает вытесненные потолком записи — вызывающий
    /// обязан показать их человеку: молча закрытая ссылка выглядит как поломка продукта,
    /// а не как сработавшее правило.
    /// </summary>
    public IReadOnlyList<ExternalPreviewLink> Add(ExternalPreviewLink link)
    {
        lock (_lock)
        {
            DropExpired(DateTimeOffset.UtcNow);
            _links[link.Jti] = link;

            var mine = _links.Values
                .Where(l => l.UserId == link.UserId)
                .OrderByDescending(l => l.IssuedAt)
                .ToList();
            var evicted = mine.Skip(MaxPerOwner).ToList();
            foreach (var e in evicted) _links.Remove(e.Jti);

            Save();
            return evicted;
        }
    }

    /// <summary>
    /// Жива ли ссылка. Горячий путь прокси: только память, без диска. Возвращает саму запись,
    /// чтобы вызывающему не пришлось искать её вторым обращением.
    /// </summary>
    public ExternalPreviewLink? Get(string jti)
    {
        lock (_lock)
        {
            if (!_links.TryGetValue(jti, out var link)) return null;
            // Протухшую не отдаём, даже если чистка до неё ещё не дошла: срок — тоже отзыв
            if (link.IsExpired(DateTimeOffset.UtcNow)) return null;
            return link;
        }
    }

    /// <summary>Живые ссылки владельца, свежие первыми. Сквозной список по всем его проектам.</summary>
    public IReadOnlyList<ExternalPreviewLink> ListFor(string userId)
    {
        lock (_lock)
        {
            if (DropExpired(DateTimeOffset.UtcNow)) Save();
            return _links.Values
                .Where(l => l.UserId == userId)
                .OrderByDescending(l => l.IssuedAt)
                .ToList();
        }
    }

    /// <summary>Отозвать одну ссылку владельца. false — её нет или она не его.</summary>
    public bool Revoke(string jti, string userId)
    {
        lock (_lock)
        {
            if (!_links.TryGetValue(jti, out var link) || link.UserId != userId) return false;
            _links.Remove(jti);
            Save();
            return true;
        }
    }

    /// <summary>Отозвать все ссылки владельца. Возвращает, сколько закрыли.</summary>
    public int RevokeAll(string userId)
    {
        lock (_lock)
        {
            var mine = _links.Values.Where(l => l.UserId == userId).Select(l => l.Jti).ToList();
            foreach (var jti in mine) _links.Remove(jti);
            if (mine.Count > 0) Save();
            return mine.Count;
        }
    }

    /// <summary>Убрать протухшие. Возвращает true, если что-то удалили (нужно сохраниться).</summary>
    private bool DropExpired(DateTimeOffset now)
    {
        var dead = _links.Values.Where(l => l.IsExpired(now)).Select(l => l.Jti).ToList();
        foreach (var jti in dead) _links.Remove(jti);
        return dead.Count > 0;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var items = JsonSerializer.Deserialize<List<ExternalPreviewLink>>(File.ReadAllText(_path), JsonOpts);
            if (items is null) return;
            _links = items
                .Where(l => !l.IsExpired(DateTimeOffset.UtcNow))
                .ToDictionary(l => l.Jti, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // Битый реестр не должен ронять старт: худшее следствие — ссылки придётся выдать
            // заново, а это безопасная сторона отказа
            _log.LogWarning(ex, "Реестр внешних ссылок не прочитан ({Path}), начинаем с пустого", _path);
            _links = new Dictionary<string, ExternalPreviewLink>(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_links.Values.ToList(), JsonOpts));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Реестр внешних ссылок не сохранён ({Path})", _path);
        }
    }
}
