using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Backup;

// Строгая проверка сторов ПЕРЕД тем, как трогать data (гейт 3 восстановления).
//
// Зачем отдельно от штатной загрузки: JsonFileStore.Load намеренно прощает ошибки —
// битый файл он переименовывает в .corrupt-*.bak и отдаёт пустой стор, чтобы один
// сломанный файл не ронял сервер. В рантайме это правильно, при восстановлении —
// катастрофа: раскатал архив, всё зелёное, а персон нет, и узнаёшь через день.
// Поэтому здесь читаем теми же опциями, что и сторы, но падаем на любой проблеме.
public static class BackupValidation
{
    // Опции обязаны совпадать с теми, что у сторов: PersonaManager сериализует enum'ы
    // camelCase-строками, SessionManager — обычными строками, ProjectManager читает
    // регистронезависимо. Прочитать чужими опциями = получить ложный вердикт.
    private static readonly JsonSerializerOptions PersonaOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly JsonSerializerOptions SessionOpts = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions CaseInsensitiveOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Проверить сторы в распакованном каталоге. Возвращает список проблем;
    /// пустой список = архив пригоден к восстановлению.
    /// </summary>
    public static List<string> Validate(string dataDir)
    {
        var problems = new List<string>();

        // users.json — единственный обязательный стор: без пользователей инстанс
        // не пускает никого, а «пустой список» тут почти всегда означает порчу
        var usersPath = Path.Combine(dataDir, "users.json");
        if (!File.Exists(usersPath))
        {
            problems.Add("users.json отсутствует в архиве");
        }
        else
        {
            try
            {
                var file = JsonSerializer.Deserialize<UsersFileShape>(
                    File.ReadAllText(usersPath), new JsonSerializerOptions());
                if (file?.Users is null || file.Users.Count == 0)
                    problems.Add("users.json не содержит ни одного пользователя");
            }
            catch (Exception ex)
            {
                problems.Add($"users.json не читается: {ex.Message}");
            }
        }

        CheckList<Project>(problems, dataDir, "projects.json", CaseInsensitiveOpts);
        CheckList<Session>(problems, dataDir, "sessions.json", SessionOpts);
        CheckList<Persona>(problems, dataDir, "personas.json", PersonaOpts);
        CheckList<TaskItem>(problems, dataDir, "tasks.json", CaseInsensitiveOpts);
        CheckList<ProjectGroup>(problems, dataDir, "groups.json", CaseInsensitiveOpts);

        return problems;
    }

    // Отсутствующий файл — не ошибка (стор мог не создаваться: нет задач, нет персон).
    // Ошибка — файл есть, но не разбирается или разбирается в null.
    private static void CheckList<T>(
        List<string> problems, string dataDir, string fileName, JsonSerializerOptions opts)
    {
        var path = Path.Combine(dataDir, fileName);
        if (!File.Exists(path)) return;

        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                problems.Add($"{fileName} пуст");
                return;
            }

            var list = JsonSerializer.Deserialize<List<T>>(json, opts);
            if (list is null) problems.Add($"{fileName} разобран как null");
        }
        catch (Exception ex)
        {
            problems.Add($"{fileName} не читается: {ex.Message}");
        }
    }

    // Форма users.json (UsersFile в UserStore объявлен file-scoped и снаружи недоступен)
    private sealed class UsersFileShape
    {
        public int Version { get; set; }

        [JsonPropertyName("users")]
        public List<User>? Users { get; set; }
    }
}
