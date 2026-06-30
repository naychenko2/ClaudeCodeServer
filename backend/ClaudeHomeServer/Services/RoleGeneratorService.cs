using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services;

// Одно сообщение интервью: role = "assistant" (вопрос Claude) | "user" (ответ пользователя)
public record InterviewMessage(string Role, string Content);

// Итог хода: либо следующий вопрос, либо готовый черновик роли
public record InterviewResult(string? Question, RoleDraft? Role);

// Черновик роли, сгенерированный интервью (заполняет мастер на фронте)
public class RoleDraft
{
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Avatar { get; set; } = "";
    public string Color { get; set; } = "";
    public string Persona { get; set; } = "";
    public List<string> AgentNames { get; set; } = [];
    public string? SystemPrompt { get; set; }
    public string? Model { get; set; }
    public string? Effort { get; set; }
}

// Ведёт диалог-интервью для создания роли через одноразовые вызовы claude.exe.
// Без сессии: вся история диалога передаётся в каждый вызов (stateless).
public class RoleGeneratorService
{
    private readonly SkillsService _skills;

    public RoleGeneratorService(SkillsService skills) => _skills = skills;

    private static readonly string[] Colors =
        ["#D97757", "#6C5CB0", "#3E7CA6", "#5E8B4E", "#C9923E", "#B4452F", "#7A6A58", "#2A8C82"];

    public async Task<InterviewResult> InterviewAsync(string projectRootPath,
        IReadOnlyList<InterviewMessage> history, CancellationToken ct = default)
    {
        var agents = _skills.GetProjectAgents(projectRootPath);
        var systemPrompt = BuildSystemPrompt(agents);
        var conversation = BuildConversation(history);
        var raw = await RunClaudeAsync(projectRootPath, systemPrompt, conversation, ct);
        return ParseReply(raw);
    }

    private static string BuildSystemPrompt(IReadOnlyList<AgentInfo> agents)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты — дружелюбный тимлид. Проводишь короткое собеседование, чтобы оформить нового члена команды (виртуального сотрудника проекта).");
        sb.AppendLine("ВАЖНО: член команды может быть из ЛЮБОЙ сферы, не только разработка. Это может быть дизайнер, аналитик, маркетолог, копирайтер, контент-менеджер, поддержка, продажник, юрист, HR, переводчик — кто угодно. НЕ навязывай технические/программистские профессии и не предполагай, что это разработчик, пока человек сам этого не скажет. Держись нейтрально и иди за тем, что говорит собеседник.");
        sb.AppendLine("Задавай ПО ОДНОМУ короткому вопросу за раз, как на живом собеседовании. ОБЯЗАТЕЛЬНО выясни по очереди:");
        sb.AppendLine("1) как зовут; 2) чем будет заниматься (специализация); 3) характер и манеру речи — как общается (формально/неформально, дружелюбно/сдержанно, с юмором или строго); 4) ключевые навыки.");
        sb.AppendLine("Не пропускай вопрос про характер и стиль речи — он важен. По-русски, неформально и тепло, как будто знакомишься с новым коллегой. Не тяни, без лишней болтовни.");
        sb.AppendLine();
        sb.AppendLine("Когда узнал достаточно (как минимум имя, специализацию И характер/стиль речи) — оформи карточку сотрудника строго одной строкой и больше НИЧЕГО, в формате:");
        sb.AppendLine("<ROLE>{\"name\":\"\",\"title\":\"\",\"avatar\":\"<один эмодзи>\",\"color\":\"<hex>\",\"persona\":\"\",\"agentNames\":[],\"systemPrompt\":\"\",\"model\":\"\",\"effort\":\"\"}</ROLE>");
        sb.AppendLine($"Поле color выбери из: {string.Join(", ", Colors)}.");
        sb.AppendLine("Поля model и effort оставь пустыми строками, если пользователь явно не просил.");
        if (agents.Count > 0)
        {
            sb.AppendLine("Поле agentNames — выбери подходящие fileName из доступных агентов (или пустой массив):");
            foreach (var a in agents)
                sb.AppendLine($"- {a.FileName}: {a.Description}");
        }
        else
        {
            sb.AppendLine("Доступных агентов нет — agentNames оставь пустым массивом.");
        }
        return sb.ToString();
    }

    private static string BuildConversation(IReadOnlyList<InterviewMessage> history)
    {
        if (history.Count == 0)
            return "Начни собеседование: тепло поздоровайся и спроси, как зовут нового сотрудника.";

        var sb = new StringBuilder();
        sb.AppendLine("Собеседование на данный момент:");
        foreach (var m in history)
            sb.AppendLine($"[{(m.Role == "user" ? "Собеседник" : "Ты")}]: {m.Content}");
        sb.AppendLine();
        sb.AppendLine("Продолжи: задай следующий вопрос ИЛИ оформи карточку <ROLE>...</ROLE>, если узнал уже достаточно.");
        return sb.ToString();
    }

    private static async Task<string> RunClaudeAsync(string rootPath, string systemPrompt,
        string prompt, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FindClaudeExecutable(),
            WorkingDirectory = Directory.Exists(rootPath) ? rootPath : Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in new[]
        {
            "--print", "--output-format", "json",
            "--model", "claude-haiku-4-5-20251001",
            // Отключаем MCP-серверы: для генерации вопросов не нужны, а их загрузка
            // при старте claude.exe — основная задержка ответа
            "--strict-mcp-config", "--mcp-config", EmptyMcpConfigPath(),
            "--append-system-prompt", systemPrompt,
            prompt,
        })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Не удалось запустить claude");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            throw new TimeoutException("Claude не ответил вовремя");
        }

        var stdout = await stdoutTask;
        // claude --output-format json возвращает обёртку { "type":"result", "result":"<текст>" }
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
                return r.GetString() ?? "";
        }
        catch { /* не JSON — вернём как есть */ }
        return stdout;
    }

    private static InterviewResult ParseReply(string raw)
    {
        var match = Regex.Match(raw, "<ROLE>(.*?)</ROLE>", RegexOptions.Singleline);
        if (match.Success)
        {
            try
            {
                var json = match.Groups[1].Value.Trim();
                var draft = JsonSerializer.Deserialize<RoleDraft>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (draft is not null)
                    return new InterviewResult(null, draft);
            }
            catch { /* битый JSON — отдадим как вопрос ниже */ }
        }
        return new InterviewResult(raw.Trim(), null);
    }

    // Пустой MCP-конфиг (создаётся один раз) — чтобы claude.exe не грузил MCP-серверы
    private static string EmptyMcpConfigPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "claude-empty-mcp.json");
        if (!File.Exists(path))
            File.WriteAllText(path, "{\"mcpServers\":{}}");
        return path;
    }

    // Поиск claude.exe — та же логика, что в ClaudeSession
    private static string FindClaudeExecutable()
    {
        if (!OperatingSystem.IsWindows()) return "claude";
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var exePath = Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        if (File.Exists(exePath)) return exePath;
        try
        {
            var where = Process.Start(new ProcessStartInfo("where.exe", "claude.exe")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
            var line = where.StandardOutput.ReadLine();
            if (!string.IsNullOrEmpty(line) && File.Exists(line)) return line.Trim();
        }
        catch { }
        return "claude.exe";
    }
}
