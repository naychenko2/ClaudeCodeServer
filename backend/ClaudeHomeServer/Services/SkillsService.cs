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
    public string Scope { get; set; } = "project"; // "user" (глобальный ~/.claude/agents) | "project"
}

public class SkillsService
{
    private static string GlobalSkillsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills");

    private static string GetAgentsDir(string projectRootPath) =>
        Path.Combine(projectRootPath, ".claude", "agents");

    // Глобальные (пользовательские) агенты — доступны во всех проектах
    private static string GlobalAgentsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "agents");

    // --- Чтение скиллов и агентов ---

    public IReadOnlyList<SkillInfo> GetGlobalSkills()
    {
        var dir = GlobalSkillsDir;
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

    // Агенты, доступные в проекте: глобальные (~/.claude/agents) + проектные (.claude/agents).
    // Проектный агент перекрывает глобального с тем же именем файла (как в Claude Code CLI).
    public IReadOnlyList<AgentInfo> GetProjectAgents(string projectRootPath)
    {
        var byName = new Dictionary<string, AgentInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, scope) in new[] { (GlobalAgentsDir, "user"), (GetAgentsDir(projectRootPath), "project") })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.md"))
            {
                try
                {
                    var content = File.ReadAllText(file);
                    var meta = ParseFrontmatter(content);
                    var toolsStr = meta.TryGetValue("tools", out var t) ? t : null;
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    byName[fileName] = new AgentInfo
                    {
                        Name = meta.TryGetValue("name", out var n) ? n : fileName,
                        Description = meta.TryGetValue("description", out var d) ? d : "",
                        Color = meta.TryGetValue("color", out var c) ? c : null,
                        Tools = toolsStr != null
                            ? toolsStr.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray()
                            : [],
                        PermissionMode = meta.TryGetValue("permissionMode", out var pm) ? pm : null,
                        FileName = fileName,
                        Scope = scope,
                    };
                }
                catch { }
            }
        }
        return byName.Values.ToList();
    }

    // --- Получение содержимого файла ---

    public string? GetSkillContent(string skillName)
    {
        var file = Path.Combine(GlobalSkillsDir, skillName, "SKILL.md");
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    public string? GetAgentContent(string projectRootPath, string agentFileName)
    {
        // Проектный агент приоритетнее глобального
        var projectFile = Path.Combine(GetAgentsDir(projectRootPath), agentFileName + ".md");
        if (File.Exists(projectFile)) return File.ReadAllText(projectFile);
        var globalFile = Path.Combine(GlobalAgentsDir, agentFileName + ".md");
        return File.Exists(globalFile) ? File.ReadAllText(globalFile) : null;
    }

    // --- Сохранение ---

    public void SaveGlobalSkill(string skillName, string fileContent)
    {
        var dir = Path.Combine(GlobalSkillsDir, skillName);
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
