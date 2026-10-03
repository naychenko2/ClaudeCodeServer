using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Services.VideoEditor.Films;

// Состояние фильма ВНЕ файла (ADR-022 §2): data/video-films/{owner}/{projectId}/{filmKey}.json, filmKey — хеш пути
// .film. Здесь то, что не должно ехать в git вместе с проектом: траты по сценам (включая отброшенные варианты —
// «Потрачено на фильм», только отображение, потолка нет), пометки «✦ Claude» и музыка, которую фильм ждёт из
// «Звука». Лежит в бэкапе (data/ целиком). Записи с замком и через временный файл; добавлять поля — только аддитивно.
public sealed class FilmSideStore(string root)
{
    public const string DirName = "video-films";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _gate = new();

    public string Root { get; } = root;

    public static FilmSideStore FromConfig(IConfiguration config)
    {
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        return new FilmSideStore(Path.Combine(Path.GetDirectoryName(dataPath)!, DirName));
    }

    public FilmSide Get(string ownerId, string projectId, string filmPath)
    {
        lock (_gate) return Read(PathOf(ownerId, projectId, filmPath), filmPath);
    }

    // Правка под замком; change вернул тот же объект — записи нет
    public FilmSide Update(string ownerId, string projectId, string filmPath, Func<FilmSide, FilmSide> change)
    {
        lock (_gate)
        {
            var path = PathOf(ownerId, projectId, filmPath);
            var current = Read(path, filmPath);
            var next = change(current);
            if (ReferenceEquals(next, current) || next == current) return current;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(next, Json));
            File.Move(tmp, path, overwrite: true);
            return next;
        }
    }

    // Фильмы владельца, ждущие музыку из нити звука: подписке на AudioVersionAdded нужен только этот вопрос.
    // ProjectId берётся из пути каталога
    public IReadOnlyList<(string ProjectId, FilmSide Side)> WaitingForMusic(string ownerId, string threadId)
    {
        var found = new List<(string, FilmSide)>();
        lock (_gate)
        {
            var dir = Path.Combine(Root, Safe(ownerId));
            if (!Directory.Exists(dir)) return found;
            foreach (var projectDir in Directory.EnumerateDirectories(dir))
                foreach (var file in Directory.EnumerateFiles(projectDir, "*.json"))
                {
                    var side = Read(file, null);
                    if (side.PendingMusic?.ThreadId == threadId && side.Path.Length > 0)
                        found.Add((Path.GetFileName(projectDir), side));
                }
        }
        return found;
    }

    private string PathOf(string ownerId, string projectId, string filmPath) =>
        Path.Combine(Root, Safe(ownerId), Safe(projectId), FilmPaths.KeyOf(filmPath) + ".json");

    private static FilmSide Read(string file, string? filmPath)
    {
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<FilmSide>(File.ReadAllText(file), Json) is { } side)
                return side with
                {
                    Path = side.Path ?? filmPath ?? "",
                    Spends = side.Spends ?? [],
                    ClaudeFiles = side.ClaudeFiles ?? [],
                };
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new FilmSide { Path = filmPath ?? "" };
    }

    private static string Safe(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment.Contains("..")
            || segment.IndexOfAny(['/', '\\', ':']) >= 0 || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Недопустимый идентификатор", nameof(segment));
        return segment;
    }
}

// Path — путь .film (ключ файла хеш, сам путь хранится для подписки, которой нужен фильм по нити)
public sealed record FilmSide
{
    public string Path { get; init; } = "";
    // Траты по версиям клипов сцен фильма, включая отброшенные: копятся, не убывают (удалённый чат не стирает счёт)
    public IReadOnlyList<FilmSpendEntry> Spends { get; init; } = [];
    // Файлы строк, которые правил агент: пометка «✦ Claude»
    public IReadOnlyList<string> ClaudeFiles { get; init; } = [];
    public FilmPendingMusic? PendingMusic { get; init; }
}

// Currency — usd | credits | local. LocalSeconds — время локального запуска от старта до готовности (очередь
// входит), записывается на первую версию запуска: «GPU-секунды» в счётчике — оценка, не замер GPU
public sealed record FilmSpendEntry(string VersionId, string Currency, double Amount, double LocalSeconds, DateTime At);

// Нить звука, из которой фильм ждёт музыку: первая готовая версия станет music/<фильм>.mp3
public sealed record FilmPendingMusic(string SessionId, string ThreadId, DateTime At);
