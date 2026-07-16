namespace ClaudeHomeServer.Services;

public class SkillInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? ArgumentHint { get; set; }
    public string FilePath { get; set; } = "";
}

public class AgentInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Color { get; set; }
    public string[] Tools { get; set; } = [];
    public string? PermissionMode { get; set; }
    public string FileName { get; set; } = ""; // без .md
}

public class SkillsService
{
    private static string GlobalSkillsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills");

    private static string GlobalWorkflowsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "workflows");

    private static string InstalledPluginsManifest =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "plugins", "installed_plugins.json");

    private static string GetProjectSkillsDir(string projectRootPath) =>
        Path.Combine(projectRootPath, ".claude", "skills");

    private static string GetAgentsDir(string projectRootPath) =>
        Path.Combine(projectRootPath, ".claude", "agents");

    // --- Чтение скиллов и агентов ---

    public IReadOnlyList<SkillInfo> GetGlobalSkills() => ReadSkillsFrom(GlobalSkillsDir);

    // Workflow-скрипты (~/.claude/workflows/*.js) — многоагентные оркестрации Claude Code
    // (например /panel-of-experts). Метаданные — из литерала `export const meta = {...}`
    // в начале скрипта (name/description); парсим эвристикой по строковым литералам,
    // полноценный JS-парсер не нужен (meta по контракту — чистый литерал).
    public IReadOnlyList<SkillInfo> GetGlobalWorkflows()
    {
        var dir = GlobalWorkflowsDir;
        if (!Directory.Exists(dir)) return [];

        var result = new List<SkillInfo>();
        foreach (var file in Directory.GetFiles(dir, "*.js"))
        {
            try
            {
                // meta — в начале файла; 4КБ хватает с запасом, весь скрипт не читаем
                using var reader = new StreamReader(file);
                var buf = new char[4096];
                var read = reader.Read(buf, 0, buf.Length);
                var head = new string(buf, 0, read);
                result.Add(new SkillInfo
                {
                    Name = ExtractMetaString(head, "name") ?? Path.GetFileNameWithoutExtension(file),
                    Description = ExtractMetaString(head, "description") ?? "",
                    ArgumentHint = null,
                    FilePath = file,
                });
            }
            catch { }
        }
        return result;
    }

    // Скиллы и команды установленных плагинов Claude Code (например oh-my-claudecode).
    // Источник — ~/.claude/plugins/installed_plugins.json (v2): plugins → "имя@marketplace" →
    // [{ installPath }]. Имена отдаём с namespace «плагин:имя» — ровно так их вызывает CLI
    // (/oh-my-claudecode:autopilot), поэтому вставка из попапа «/» работает как есть.
    public IReadOnlyList<SkillInfo> GetPluginSkills()
    {
        var manifest = InstalledPluginsManifest;
        if (!File.Exists(manifest)) return [];

        var result = new List<SkillInfo>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
            if (!doc.RootElement.TryGetProperty("plugins", out var plugins) ||
                plugins.ValueKind != System.Text.Json.JsonValueKind.Object)
                return [];

            foreach (var plugin in plugins.EnumerateObject())
            {
                var pluginName = plugin.Name.Split('@')[0];
                if (plugin.Value.ValueKind != System.Text.Json.JsonValueKind.Array) continue;

                foreach (var install in plugin.Value.EnumerateArray())
                {
                    if (!install.TryGetProperty("installPath", out var pathEl)) continue;
                    var installPath = pathEl.GetString();
                    if (string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath)) continue;
                    AddPluginEntries(result, pluginName, installPath);
                    break; // одна установка плагина (первая по scope)
                }
            }
        }
        catch { }
        return result;
    }

    // Скиллы (skills/*/SKILL.md) и команды (commands/*.md) одного плагина; дубли имён схлопываются.
    private static void AddPluginEntries(List<SkillInfo> result, string pluginName, string installPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var skill in ReadSkillsFrom(Path.Combine(installPath, "skills")))
        {
            if (!seen.Add(skill.Name)) continue;
            skill.Name = $"{pluginName}:{skill.Name}";
            result.Add(skill);
        }

        var commandsDir = Path.Combine(installPath, "commands");
        if (!Directory.Exists(commandsDir)) return;
        foreach (var file in Directory.GetFiles(commandsDir, "*.md"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!seen.Add(name)) continue;
            try
            {
                var meta = ParseFrontmatter(File.ReadAllText(file));
                result.Add(new SkillInfo
                {
                    Name = $"{pluginName}:{name}",
                    Description = meta.TryGetValue("description", out var d) ? d : "",
                    ArgumentHint = meta.TryGetValue("argument-hint", out var ah) ? ah : null,
                    FilePath = file,
                });
            }
            catch { }
        }
    }

    // Значение строкового поля из литерала meta: `key: 'значение'` / `key: "значение"`
    private static string? ExtractMetaString(string source, string key)
    {
        var match = System.Text.RegularExpressions.Regex.Match(source,
            key + @"\s*:\s*(['""])((?:\\.|(?!\1).)*)\1");
        return match.Success ? match.Groups[2].Value.Replace("\\'", "'").Replace("\\\"", "\"") : null;
    }

    // Скиллы уровня проекта (.claude/skills проекта) — сюда CLI устанавливает навыки в scope=project.
    public IReadOnlyList<SkillInfo> GetProjectSkills(string projectRootPath) =>
        ReadSkillsFrom(GetProjectSkillsDir(projectRootPath));

    // Общее чтение каталога навыков: каждая подпапка с SKILL.md → SkillInfo (frontmatter name/description).
    private static IReadOnlyList<SkillInfo> ReadSkillsFrom(string dir)
    {
        if (!Directory.Exists(dir)) return [];

        var result = new List<SkillInfo>();
        foreach (var skillDir in Directory.GetDirectories(dir))
        {
            var skillFile = Path.Combine(skillDir, "SKILL.md");
            if (!File.Exists(skillFile)) continue;
            try
            {
                var content = File.ReadAllText(skillFile);
                var meta = ParseFrontmatter(content);
                result.Add(new SkillInfo
                {
                    Name = meta.TryGetValue("name", out var n) ? n : Path.GetFileName(skillDir),
                    Description = meta.TryGetValue("description", out var d) ? d : "",
                    ArgumentHint = meta.TryGetValue("argument-hint", out var ah) ? ah : null,
                    FilePath = skillFile,
                });
            }
            catch { }
        }
        return result;
    }

    public IReadOnlyList<AgentInfo> GetProjectAgents(string projectRootPath)
    {
        var dir = GetAgentsDir(projectRootPath);
        if (!Directory.Exists(dir)) return [];

        var result = new List<AgentInfo>();
        foreach (var file in Directory.GetFiles(dir, "*.md"))
        {
            try
            {
                var content = File.ReadAllText(file);
                var meta = ParseFrontmatter(content);
                var toolsStr = meta.TryGetValue("tools", out var t) ? t : null;
                result.Add(new AgentInfo
                {
                    Name = meta.TryGetValue("name", out var n) ? n : Path.GetFileNameWithoutExtension(file),
                    Description = meta.TryGetValue("description", out var d) ? d : "",
                    Color = meta.TryGetValue("color", out var c) ? c : null,
                    Tools = toolsStr != null
                        ? toolsStr.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray()
                        : [],
                    PermissionMode = meta.TryGetValue("permissionMode", out var pm) ? pm : null,
                    FileName = Path.GetFileNameWithoutExtension(file),
                });
            }
            catch { }
        }
        return result;
    }

    // --- Получение содержимого файла ---

    public string? GetSkillContent(string skillName)
    {
        var file = Path.Combine(GlobalSkillsDir, skillName, "SKILL.md");
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    public string? GetAgentContent(string projectRootPath, string agentFileName)
    {
        var file = Path.Combine(GetAgentsDir(projectRootPath), agentFileName + ".md");
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    // --- Сохранение ---

    public void SaveGlobalSkill(string skillName, string fileContent)
    {
        var name = (skillName ?? "").Trim();
        // Защита от path traversal: имя навыка — только имя папки, без разделителей/"..".
        // Иначе Path.Combine с "../.." записал бы SKILL.md вне ~/.claude/skills.
        // (Path.GetFileName("..") == ".." — сам по себе разделителя не ловит, отсекаем явно.)
        if (string.IsNullOrEmpty(name) || name is "." or ".." || Path.GetFileName(name) != name)
            throw new ArgumentException("Недопустимое имя навыка", nameof(skillName));
        var dir = Path.Combine(GlobalSkillsDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), fileContent);
    }

    public void SaveProjectAgent(string projectRootPath, string agentFileName, string fileContent)
    {
        var dir = GetAgentsDir(projectRootPath);
        Directory.CreateDirectory(dir);
        var safeFileName = Path.GetFileNameWithoutExtension(agentFileName) + ".md";
        File.WriteAllText(Path.Combine(dir, safeFileName), fileContent);
    }

    // --- Расширение скилла в сообщении ---

    // Если сообщение начинается с /skill-name, возвращает раскрытый текст (как делает Claude Code CLI).
    // Возвращает null, если это не команда скилла или скилл не найден.
    public string? TryExpandSkill(string message)
    {
        if (string.IsNullOrEmpty(message) || message[0] != '/') return null;

        var rest = message[1..];
        var spaceIdx = rest.IndexOfAny([' ', '\n', '\r']);
        var skillName = spaceIdx >= 0 ? rest[..spaceIdx] : rest;
        var args = spaceIdx >= 0 ? rest[(spaceIdx + 1)..].Trim() : "";

        if (string.IsNullOrEmpty(skillName)) return null;

        var content = GetSkillContent(skillName);
        if (content is null) return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"<command-message>{skillName}</command-message>");
        sb.AppendLine($"<command-name>/{skillName}</command-name>");
        sb.Append(content);
        if (!string.IsNullOrEmpty(args))
        {
            sb.AppendLine();
            sb.AppendLine($"ARGUMENTS: {args}");
        }

        return sb.ToString();
    }

    // Загружает содержимое агента для инжекции в системный промпт сессии.
    // Возвращает null, если агент не найден.
    public string? GetAgentSystemPrompt(string projectRootPath, string agentFileName)
    {
        var content = GetAgentContent(projectRootPath, agentFileName);
        if (content is null) return null;

        // Стрипаем frontmatter — оставляем только тело файла как системный промпт
        var body = StripFrontmatter(content);
        return string.IsNullOrWhiteSpace(body) ? content : body;
    }

    // --- Парсинг frontmatter ---

    private static Dictionary<string, string> ParseFrontmatter(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!content.StartsWith("---")) return result;

        var end = content.IndexOf("\n---", 3);
        if (end < 0) return result;

        var frontmatter = content[3..end].Trim();
        var lines = frontmatter.Split('\n');

        string? multilineKey = null;
        var multilineLines = new List<string>();

        void FlushMultiline()
        {
            if (multilineKey is null) return;
            result[multilineKey] = string.Join(" ", multilineLines.Select(l => l.Trim())).Trim();
            multilineKey = null;
            multilineLines.Clear();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            // Продолжение многострочного значения (строка с отступом)
            if (multilineKey is not null && line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                multilineLines.Add(line);
                continue;
            }

            FlushMultiline();

            var colonIdx = line.IndexOf(':');
            if (colonIdx < 0) continue;
            var key = line[..colonIdx].Trim();
            var value = line[(colonIdx + 1)..].Trim().Trim('"').Trim('\'');
            if (string.IsNullOrEmpty(key)) continue;

            // YAML block scalar: folded (>) или literal (|), опционально с
            // chomping- (-/+) и/или indentation-индикатором (цифра): >-, |-, >2, |2- и т.п.
            // Следующие строки с отступом являются значением.
            if (IsBlockScalarHeader(value))
            {
                multilineKey = key;
            }
            else
            {
                result[key] = value;
            }
        }

        FlushMultiline();

        return result;
    }

    // Заголовок блочного скаляра YAML: '>' (folded) или '|' (literal),
    // далее любая комбинация chomping-индикаторов (-/+) и indentation-цифр.
    // Примеры: ">", "|", ">-", "|+", ">2", "|2-".
    private static bool IsBlockScalarHeader(string value)
    {
        if (value.Length == 0 || (value[0] != '>' && value[0] != '|')) return false;
        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (c is not ('-' or '+' or (>= '0' and <= '9'))) return false;
        }
        return true;
    }

    private static string StripFrontmatter(string content)
    {
        if (!content.StartsWith("---")) return content;
        var end = content.IndexOf("\n---", 3);
        if (end < 0) return content;
        // Пропускаем строку с "---"
        var afterEnd = end + 4;
        while (afterEnd < content.Length && (content[afterEnd] == '\r' || content[afterEnd] == '\n'))
            afterEnd++;
        return content[afterEnd..];
    }
}
