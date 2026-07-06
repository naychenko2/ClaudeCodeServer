using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Llm.DeepSeek;

// Класс опасности инструмента — определяет, нужно ли спрашивать разрешение (по Mode сессии)
public enum ToolPermissionClass { ReadOnly, Edit, Execute }

public sealed record DsToolResult(string Content, bool IsError = false);

public interface IDeepSeekTool
{
    string Name { get; }
    string Description { get; }
    // JSON Schema параметров (поле parameters в описании функции)
    JsonObject BuildSchema();
    ToolPermissionClass PermissionClass { get; }
    Task<DsToolResult> ExecuteAsync(JsonElement args, CancellationToken ct);
}

// Реестр инструментов DeepSeek-сессии. Все файловые операции — через FileService.SafeJoin
// (защита от path traversal). Один экземпляр на сессию (привязан к rootPath).
public sealed class DeepSeekToolRegistry
{
    // Общий потолок текста одного tool-результата — большие выводы раздувают контекст
    internal const int MaxResultChars = 48_000;

    private readonly Dictionary<string, IDeepSeekTool> _tools;

    public DeepSeekToolRegistry(string rootPath, FileService files,
        bool enableShell = true, int shellTimeoutSeconds = 120)
    {
        var list = new List<IDeepSeekTool>
        {
            new ReadFileTool(rootPath),
            new ListDirTool(rootPath, files),
            new GrepSearchTool(rootPath),
            new GlobFilesTool(rootPath),
            new WriteFileTool(rootPath, files),
            new EditFileTool(rootPath, files),
            new WebFetchTool(),
        };
        // Запуск команд (класс Execute) спрашивает разрешение на КАЖДЫЙ вызов —
        // даже в auto/bypass (см. DeepSeekSession.AutoAllowedByMode)
        if (enableShell) list.Add(new RunCommandTool(rootPath, shellTimeoutSeconds));
        _tools = list.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IDeepSeekTool> All => _tools.Values;

    public IDeepSeekTool? Get(string name) => _tools.GetValueOrDefault(name);

    // Массив tools для запроса chat completions (OpenAI-формат)
    public JsonArray BuildToolsJson()
    {
        var arr = new JsonArray();
        foreach (var t in _tools.Values)
        {
            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.BuildSchema(),
                },
            });
        }
        return arr;
    }

    internal static string Truncate(string text) =>
        text.Length <= MaxResultChars ? text : text[..MaxResultChars] + "\n…(вывод обрезан)";

    internal static string? GetString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static int? GetInt(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    internal static bool GetBool(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.True;
}

// --- Инструменты ---

file sealed class ReadFileTool(string rootPath) : IDeepSeekTool
{
    private const int MaxLines = 2000;

    public string Name => "read_file";
    public string Description =>
        "Прочитать текстовый файл проекта. Возвращает строки с номерами. " +
        "Для больших файлов используй offset/limit (номер первой строки и число строк).";
    public ToolPermissionClass PermissionClass => ToolPermissionClass.ReadOnly;

    public JsonObject BuildSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Путь относительно корня проекта" },
            ["offset"] = new JsonObject { ["type"] = "integer", ["description"] = "Номер первой строки (с 1)" },
            ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Максимум строк" },
        },
        ["required"] = new JsonArray { "path" },
    };

    public Task<DsToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var path = DeepSeekToolRegistry.GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(new DsToolResult("Не указан параметр path", IsError: true));
        try
        {
            var full = FileService.SafeJoin(rootPath, path);
            if (!File.Exists(full))
                return Task.FromResult(new DsToolResult($"Файл не найден: {path}", IsError: true));

            var offset = Math.Max(1, DeepSeekToolRegistry.GetInt(args, "offset") ?? 1);
            var limit = Math.Clamp(DeepSeekToolRegistry.GetInt(args, "limit") ?? MaxLines, 1, MaxLines);

            var sb = new StringBuilder();
            var lineNo = 0;
            var taken = 0;
            foreach (var line in File.ReadLines(full))
            {
                ct.ThrowIfCancellationRequested();
                lineNo++;
                if (lineNo < offset) continue;
                if (taken >= limit) { sb.Append("…(есть ещё строки — продолжи с offset)"); break; }
                sb.Append(lineNo).Append('\t').AppendLine(line);
                taken++;
            }
            if (taken == 0 && lineNo < offset)
                return Task.FromResult(new DsToolResult($"В файле всего {lineNo} строк — offset {offset} за пределами", IsError: true));
            return Task.FromResult(new DsToolResult(DeepSeekToolRegistry.Truncate(sb.ToString())));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DsToolResult($"Ошибка чтения {path}: {ex.Message}", IsError: true));
        }
    }
}

file sealed class ListDirTool(string rootPath, FileService files) : IDeepSeekTool
{
    public string Name => "list_dir";
    public string Description =>
        "Показать содержимое папки проекта. recursive=true — дерево вложенных папок " +
        "(служебные вроде node_modules/.git исключаются).";
    public ToolPermissionClass PermissionClass => ToolPermissionClass.ReadOnly;

    public JsonObject BuildSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Папка относительно корня; пусто = корень" },
            ["recursive"] = new JsonObject { ["type"] = "boolean", ["description"] = "Дерево вложенных папок" },
        },
    };

    public Task<DsToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var path = DeepSeekToolRegistry.GetString(args, "path") ?? "";
        var recursive = DeepSeekToolRegistry.GetBool(args, "recursive");
        try
        {
            var entries = recursive ? files.Tree(rootPath, path) : files.List(rootPath, path);
            var sb = new StringBuilder();
            var count = 0;
            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                sb.Append(e.Path);
                if (e.IsDirectory) sb.Append('/');
                else if (e.Size is { } size) sb.Append("  (").Append(size).Append(" Б)");
                sb.AppendLine();
                count++;
            }
            if (count == 0) sb.Append("(пусто)");
            return Task.FromResult(new DsToolResult(DeepSeekToolRegistry.Truncate(sb.ToString())));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DsToolResult($"Ошибка списка {path}: {ex.Message}", IsError: true));
        }
    }
}

file sealed class GrepSearchTool(string rootPath) : IDeepSeekTool
{
    private const int MaxMatches = 100;
    private const long MaxFileBytes = 1024 * 1024;

    public string Name => "grep_search";
    public string Description =>
        "Поиск по содержимому файлов проекта регулярным выражением (.NET Regex, без учёта регистра). " +
        "glob фильтрует по имени файла (например *.cs). Возвращает до 100 совпадений «путь:строка: текст».";
    public ToolPermissionClass PermissionClass => ToolPermissionClass.ReadOnly;

    public JsonObject BuildSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["pattern"] = new JsonObject { ["type"] = "string", ["description"] = "Регулярное выражение" },
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Папка поиска относительно корня; пусто = весь проект" },
            ["glob"] = new JsonObject { ["type"] = "string", ["description"] = "Маска имени файла, например *.cs" },
        },
        ["required"] = new JsonArray { "pattern" },
    };

    public Task<DsToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var pattern = DeepSeekToolRegistry.GetString(args, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
            return Task.FromResult(new DsToolResult("Не указан параметр pattern", IsError: true));
        Regex rx;
        try { rx = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException ex)
        {
            return Task.FromResult(new DsToolResult($"Некорректное регулярное выражение: {ex.Message}", IsError: true));
        }

        var relRoot = DeepSeekToolRegistry.GetString(args, "path") ?? "";
        var glob = DeepSeekToolRegistry.GetString(args, "glob");
        try
        {
            var searchRoot = FileService.SafeJoin(rootPath, relRoot);
            if (!Directory.Exists(searchRoot))
                return Task.FromResult(new DsToolResult($"Папка не найдена: {relRoot}", IsError: true));

            var sb = new StringBuilder();
            var matches = 0;
            foreach (var file in EnumerateFiles(searchRoot))
            {
                ct.ThrowIfCancellationRequested();
                if (matches >= MaxMatches) break;
                if (glob is not null && !GlobMatches(glob, Path.GetFileName(file))) continue;
                if (new FileInfo(file).Length > MaxFileBytes) continue;

                var lineNo = 0;
                foreach (var line in ReadLinesSafe(file))
                {
                    lineNo++;
                    bool hit;
                    try { hit = rx.IsMatch(line); }
                    catch (RegexMatchTimeoutException) { break; } // патологический паттерн — файл пропускаем
                    if (!hit) continue;
                    var rel = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                    var text = line.Length > 300 ? line[..300] + "…" : line;
                    sb.Append(rel).Append(':').Append(lineNo).Append(": ").AppendLine(text.Trim());
                    if (++matches >= MaxMatches) { sb.Append("…(показаны первые 100 совпадений)"); break; }
                }
            }
            return Task.FromResult(new DsToolResult(
                matches == 0 ? "Совпадений не найдено" : DeepSeekToolRegistry.Truncate(sb.ToString())));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DsToolResult($"Ошибка поиска: {ex.Message}", IsError: true));
        }
    }

    // Обход с исключением служебных папок (node_modules, .git и т.п. — FileService.TreeExcludes)
    private static IEnumerable<string> EnumerateFiles(string dir)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(current);
                dirs = Directory.GetDirectories(current);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            foreach (var f in files) yield return f;
            foreach (var d in dirs)
                if (!FileService.TreeExcludes.Contains(Path.GetFileName(d)))
                    stack.Push(d);
        }
    }

    private static IEnumerable<string> ReadLinesSafe(string file)
    {
        IEnumerator<string>? e = null;
        try { e = File.ReadLines(file).GetEnumerator(); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }
        using (e)
        {
            while (true)
            {
                try { if (!e.MoveNext()) yield break; }
                catch (IOException) { yield break; }
                yield return e.Current;
            }
        }
    }

    // Маска имени файла: '*' — любая подстрока, '?' — один символ
    private static bool GlobMatches(string glob, string fileName)
    {
        var rx = "^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(fileName, rx, RegexOptions.IgnoreCase);
    }
}

file sealed class WriteFileTool(string rootPath, FileService files) : IDeepSeekTool
{
    public string Name => "write_file";
    public string Description =>
        "Создать или полностью перезаписать текстовый файл проекта. " +
        "Для точечных правок существующего файла используй edit_file.";
    public ToolPermissionClass PermissionClass => ToolPermissionClass.Edit;

    public JsonObject BuildSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Путь относительно корня проекта" },
            ["content"] = new JsonObject { ["type"] = "string", ["description"] = "Полное содержимое файла" },
        },
        ["required"] = new JsonArray { "path", "content" },
    };

    public Task<DsToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var path = DeepSeekToolRegistry.GetString(args, "path");
        var content = DeepSeekToolRegistry.GetString(args, "content");
        if (string.IsNullOrWhiteSpace(path) || content is null)
            return Task.FromResult(new DsToolResult("Нужны параметры path и content", IsError: true));
        try
        {
            // FileService.WriteFile не создаёт родительские папки — создаём сами (путь уже проверен SafeJoin)
            var full = FileService.SafeJoin(rootPath, path);
            if (Path.GetDirectoryName(full) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            files.WriteFile(rootPath, path, content);
            return Task.FromResult(new DsToolResult($"Файл записан: {path}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DsToolResult($"Ошибка записи {path}: {ex.Message}", IsError: true));
        }
    }
}

file sealed class EditFileTool(string rootPath, FileService files) : IDeepSeekTool
{
    public string Name => "edit_file";
    public string Description =>
        "Точечная правка файла: заменить old_string на new_string. " +
        "old_string должен встречаться в файле ровно один раз (добавь контекст вокруг, если неоднозначно).";
    public ToolPermissionClass PermissionClass => ToolPermissionClass.Edit;

    public JsonObject BuildSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Путь относительно корня проекта" },
            ["old_string"] = new JsonObject { ["type"] = "string", ["description"] = "Точный заменяемый фрагмент" },
            ["new_string"] = new JsonObject { ["type"] = "string", ["description"] = "Новый фрагмент" },
        },
        ["required"] = new JsonArray { "path", "old_string", "new_string" },
    };

    public Task<DsToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var path = DeepSeekToolRegistry.GetString(args, "path");
        var oldStr = DeepSeekToolRegistry.GetString(args, "old_string");
        var newStr = DeepSeekToolRegistry.GetString(args, "new_string");
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrEmpty(oldStr) || newStr is null)
            return Task.FromResult(new DsToolResult("Нужны параметры path, old_string и new_string", IsError: true));
        try
        {
            var full = FileService.SafeJoin(rootPath, path);
            if (!File.Exists(full))
                return Task.FromResult(new DsToolResult($"Файл не найден: {path}", IsError: true));

            var text = File.ReadAllText(full);
            var first = text.IndexOf(oldStr, StringComparison.Ordinal);
            if (first < 0)
                return Task.FromResult(new DsToolResult("old_string не найден в файле — проверь точное совпадение (пробелы, переносы)", IsError: true));
            if (text.IndexOf(oldStr, first + 1, StringComparison.Ordinal) >= 0)
                return Task.FromResult(new DsToolResult("old_string встречается несколько раз — добавь контекст, чтобы фрагмент стал уникальным", IsError: true));

            files.WriteFile(rootPath, path, text.Remove(first, oldStr.Length).Insert(first, newStr));
            return Task.FromResult(new DsToolResult($"Файл изменён: {path}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DsToolResult($"Ошибка правки {path}: {ex.Message}", IsError: true));
        }
    }
}

file sealed class GlobFilesTool(string rootPath) : IDeepSeekTool
{
    private const int MaxResults = 200;

    public string Name => "glob_files";
    public string Description =>
        "Найти файлы по маске пути (glob): ** — любые папки, * — часть имени, ? — один символ. " +
        "Например: **/*.cs, src/**/test?.ts. Результат отсортирован по дате изменения (новые первыми).";
    public ToolPermissionClass PermissionClass => ToolPermissionClass.ReadOnly;

    public JsonObject BuildSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["pattern"] = new JsonObject { ["type"] = "string", ["description"] = "Glob-маска относительно корня проекта" },
        },
        ["required"] = new JsonArray { "pattern" },
    };

    public Task<DsToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var pattern = DeepSeekToolRegistry.GetString(args, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
            return Task.FromResult(new DsToolResult("Не указан параметр pattern", IsError: true));
        try
        {
            var rx = GlobToRegex(pattern);
            var hits = new List<(string Rel, DateTime Mtime)>();
            foreach (var file in EnumerateFiles(rootPath))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                if (!rx.IsMatch(rel)) continue;
                hits.Add((rel, File.GetLastWriteTimeUtc(file)));
            }
            if (hits.Count == 0)
                return Task.FromResult(new DsToolResult("Файлы по маске не найдены"));
            var sb = new StringBuilder();
            foreach (var (rel, _) in hits.OrderByDescending(h => h.Mtime).Take(MaxResults))
                sb.AppendLine(rel);
            if (hits.Count > MaxResults) sb.Append($"…(показаны первые {MaxResults} из {hits.Count})");
            return Task.FromResult(new DsToolResult(DeepSeekToolRegistry.Truncate(sb.ToString())));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DsToolResult($"Ошибка поиска по маске: {ex.Message}", IsError: true));
        }
    }

    // ** — любые сегменты пути, * — в пределах сегмента, ? — один символ
    private static Regex GlobToRegex(string glob)
    {
        var rx = Regex.Escape(glob.Replace('\\', '/'))
            .Replace(@"\*\*/", "(?:.*/)?")
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]");
        return new Regex("^" + rx + "$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
    }

    // Обход с исключением служебных папок — как у grep_search
    private static IEnumerable<string> EnumerateFiles(string dir)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(current);
                dirs = Directory.GetDirectories(current);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            foreach (var f in files) yield return f;
            foreach (var d in dirs)
                if (!FileService.TreeExcludes.Contains(Path.GetFileName(d)))
                    stack.Push(d);
        }
    }
}

// internal (не file) — HtmlToText покрыт юнит-тестами через InternalsVisibleTo
internal sealed class WebFetchTool : IDeepSeekTool
{
    // Общий клиент на приложение: инструменты создаются per-session, плодить клиентов не нужно
    private static readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };
    private const int MaxBytes = 5 * 1024 * 1024;

    public string Name => "web_fetch";
    public string Description =>
        "Загрузить веб-страницу по URL (http/https) и вернуть её текст (HTML очищается от разметки). " +
        "Каждый запрос требует разрешения пользователя.";
    // Запрос во внешний мир — спрашиваем всегда, как run_command
    public ToolPermissionClass PermissionClass => ToolPermissionClass.Execute;

    public JsonObject BuildSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["url"] = new JsonObject { ["type"] = "string", ["description"] = "Полный URL (http/https)" },
        },
        ["required"] = new JsonArray { "url" },
    };

    public async Task<DsToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var url = DeepSeekToolRegistry.GetString(args, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new DsToolResult("Нужен корректный http/https URL", IsError: true);
        try
        {
            using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return new DsToolResult($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}", IsError: true);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var limited = new MemoryStream();
            var buf = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buf, ct)) > 0)
            {
                limited.Write(buf, 0, read);
                if (limited.Length > MaxBytes) break;
            }
            var raw = Encoding.UTF8.GetString(limited.ToArray());

            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
            var text = mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) || raw.Contains("<html", StringComparison.OrdinalIgnoreCase)
                ? HtmlToText(raw)
                : raw;
            return new DsToolResult(DeepSeekToolRegistry.Truncate(text));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new DsToolResult($"Ошибка загрузки {url}: {ex.Message}", IsError: true);
        }
    }

    // Грубая, но достаточная очистка HTML: script/style долой, теги в пробелы, entities по минимуму
    internal static string HtmlToText(string html)
    {
        var noScripts = Regex.Replace(html, @"<(script|style|noscript)\b[\s\S]*?</\1>", " ",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
        var noTags = Regex.Replace(noScripts, @"<[^>]+>", " ", RegexOptions.None, TimeSpan.FromSeconds(5));
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);
        return Regex.Replace(decoded, @"[ \t]*\n[ \t\n]*", "\n", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Replace("  ", " ").Trim();
    }
}

file sealed class RunCommandTool(string rootPath, int timeoutSeconds) : IDeepSeekTool
{
    public string Name => "run_command";
    public string Description =>
        "Выполнить команду оболочки в корне проекта (Windows — PowerShell, Linux — bash). " +
        $"Возвращает stdout/stderr и код выхода. Таймаут по умолчанию {timeoutSeconds} с. " +
        "Каждый запуск требует разрешения пользователя.";
    public ToolPermissionClass PermissionClass => ToolPermissionClass.Execute;

    public JsonObject BuildSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["command"] = new JsonObject { ["type"] = "string", ["description"] = "Команда оболочки" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Таймаут в секундах (макс 600)" },
        },
        ["required"] = new JsonArray { "command" },
    };

    public async Task<DsToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var command = DeepSeekToolRegistry.GetString(args, "command");
        if (string.IsNullOrWhiteSpace(command))
            return new DsToolResult("Не указан параметр command", IsError: true);
        var timeout = Math.Clamp(DeepSeekToolRegistry.GetInt(args, "timeout_seconds") ?? timeoutSeconds, 1, 600);

        var utf8NoBom = new UTF8Encoding(false);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            WorkingDirectory = rootPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = utf8NoBom,
            StandardErrorEncoding = utf8NoBom,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "powershell.exe";
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            // Принудительный UTF-8 вывода — иначе OEM code page даёт кракозябры в русском тексте
            psi.ArgumentList.Add("[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " + command);
        }
        else
        {
            psi.FileName = "/bin/bash";
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add(command);
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Не удалось запустить процесс оболочки");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* уже завершился */ }
                if (ct.IsCancellationRequested) throw; // interrupt хода — пробрасываем
                return new DsToolResult($"Команда не завершилась за {timeout} с и была прервана", IsError: true);
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var sb = new StringBuilder();
            if (stdout.Length > 0) sb.AppendLine(stdout.TrimEnd());
            if (stderr.Length > 0) sb.Append("[stderr]\n").AppendLine(stderr.TrimEnd());
            sb.Append("[exit code: ").Append(process.ExitCode).Append(']');
            return new DsToolResult(DeepSeekToolRegistry.Truncate(sb.ToString()), IsError: process.ExitCode != 0);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new DsToolResult($"Ошибка запуска команды: {ex.Message}", IsError: true);
        }
    }
}
