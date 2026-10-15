using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services;

// Срез эффективных возможностей персоны для генерации файла сабагента: гейты
// tasks/notes/web (EffectiveToolEnabled: Tool-привязка приоритетнее Persona.Tools),
// граф кода (ToolBindingActive — только явная активная привязка, не дефолт чатов)
// + статический индекс привязок (BuildSubagentIndex) + алиас-тир модели для пина
// (ModelAliasFor). Собирает PersonaAgentFileSync — генератор остаётся чистой функцией.
public sealed record PersonaAgentFileContext(bool WebAllowed, bool TasksAllowed,
    bool NotesAllowed, bool CodeGraphAllowed, string? BindingsBlock, string? ModelAlias = null);

// Контент персоны-сабагента: единственный источник для обоих каналов доставки —
// .md-файла (legacy, .claude/agents/) и per-run JSON-файла --agents (CLI ≥ 2.1.281).
// Description — строка маршрутизации (лимит 800); Prompt — системный промпт;
// Model — максимум алиас-тир (opus/sonnet/haiku), не конкретный ID: файл виден
// чатам всех провайдеров, чужой ID сделал бы сабагента незапускаемым.
public sealed record PersonaAgentSpec(
    string Description,
    string Prompt,
    IReadOnlyList<string> Tools,
    string? Model,
    IReadOnlyList<string>? McpServers,
    int MaxTurns,
    string? Effort);

// Генерация текста файлового сабагента Claude Code (.claude/agents/{handle}.md) из персоны.
// Чистая функция контента (без ФС) — файлами управляет PersonaAgentFileSync. Frontmatter —
// по фактической схеме CLI (name/description/tools/model/effort/color/mcpServers/maxTurns);
// тело markdown = системный промпт сабагента (без лимита размера — сюда влезают и полные
// регламенты пантеона OmO на десятки килобайт, которые не проходят в inline --agents JSON).
public sealed class PersonaAgentFileGenerator(PersonaPromptBuilder promptBuilder)
{
    // Лимит description: это строка маршрутизации в листинге Task, не промпт
    private const int DescriptionMaxChars = 800;

    // Потолок ходов консультации — страховка от зацикливания фонового сабагента
    private const int MaxTurns = 25;

    // У исполнителя потолок выше: реализация с проверкой (сборка/тесты) длиннее консультации
    private const int ExecutorMaxTurns = 50;

    // Маркер «файл наш» в description: сгенерированный .md-агент всегда несёт эту фразу,
    // по ней миграция отличает синхронизированные файлы от пользовательских
    public const string AgentDescriptionMarker = "Персона-консультант пользователя";

    // Наш сгенерированный .md-агент: YAML-frontmatter, в description которого маркер.
    // Пользовательский файл с тем же именем, но без маркера в description, — не наш
    // (миграция его не трогает). Маркер ищем только во frontmatter: в теле промпта
    // эта фраза не появляется.
    public static bool IsOurAgentFile(string text)
    {
        if (!text.StartsWith("---", StringComparison.Ordinal)) return false;
        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        var frontmatter = end < 0 ? text : text[..end];
        return frontmatter.Contains(AgentDescriptionMarker, StringComparison.Ordinal);
    }

    // Цвета, которые понимает CLI (Ky в бандле); прочие цвета палитры аватаров мапим на ближний
    private static readonly HashSet<string> CliColors =
        ["red", "blue", "green", "yellow", "purple", "orange", "pink", "cyan"];

    private static readonly Dictionary<string, string> ColorFallback = new(StringComparer.OrdinalIgnoreCase)
    {
        ["brown"] = "orange",
    };

    public PersonaAgentSpec BuildSpec(Persona persona, PersonaAgentFileContext context)
    {
        var tools = PersonaConsultantToolset.Build(persona, context.WebAllowed,
            context.TasksAllowed, context.NotesAllowed,
            notesWriteAllowed: context.NotesAllowed, tasksWriteAllowed: context.TasksAllowed,
            context.CodeGraphAllowed);
        var pmemKey = PersonaConsultantToolset.PmemServerKey(persona.Handle);

        // mcpServers: имена серверов из конфига хода, доступных сабагенту. pmem — своя
        // память; codegraph — по активной Tool-привязке (сервер графа есть в конфиге
        // только проектных сессий, поэтому ссылка появляется лишь при явной выдаче)
        var servers = new List<string>();
        if (persona.MemoryEnabled) servers.Add(pmemKey);
        if (context.CodeGraphAllowed) servers.Add(PersonaConsultantToolset.CodeGraphServerKey);

        // Характер персоны — тот же сборщик, что у собеседника чата и persona_ask, но
        // с провайдер-нейтральной дисциплиной: сабагент бежит на модели сессии, которая
        // на этапе генерации файла неизвестна
        var sb = new StringBuilder();
        sb.AppendLine(promptBuilder.BuildForSubagent(persona));

        // Консультационная рамка (по образцу PersonaAskService: персона не видит исходный разговор)
        sb.AppendLine();
        sb.AppendLine("## Ты — консультант");
        sb.AppendLine("Тебя привлекли как консультанта из другого разговора — сам разговор ты не видишь. " +
                      "Отвечай на переданный вопрос от своего лица и в своём характере, по существу; " +
                      "не здоровайся и не представляйся. Твой финальный ответ вернётся тому, кто спросил, — " +
                      "сделай его самодостаточным.");

        // Привязанные знания и правила — статический индекс (BuildSubagentIndex)
        if (!string.IsNullOrWhiteSpace(context.BindingsBlock))
        {
            sb.AppendLine();
            sb.AppendLine(context.BindingsBlock.Trim());
        }

        if (persona.MemoryEnabled)
        {
            sb.AppendLine();
            sb.AppendLine("## Твоя память");
            sb.AppendLine($"Начни работу с mcp__{pmemKey}__memory_recall (query = суть вопроса) — он вернёт " +
                          "твой рабочий фокус и самое релевантное из долгой памяти с учётом свежести и важности; " +
                          $"точечно ищи через mcp__{pmemKey}__memory_search; " +
                          $"важные новые факты сохраняй через mcp__{pmemKey}__memory_remember. " +
                          "Если вызов вернул «No such tool available» — сервер памяти ещё подключается: " +
                          "подожди мгновение и повтори тот же вызов. Если инструменты памяти недоступны — " +
                          "отвечай без неё.");
        }

        sb.AppendLine();
        if (PersonaConsultantToolset.IsExecutor(persona))
        {
            sb.AppendLine("## Рабочий доступ исполнителя");
            sb.AppendLine("Тебе доверен рабочий доступ: можешь править файлы и запускать команды, " +
                          "но ТОЛЬКО в рамках переданной задачи. Вноси наименьшие достаточные изменения, " +
                          "проверяй результат свидетельствами (сборка/тесты — покажи вывод), ничего не " +
                          "удаляй безвозвратно и не трогай файлы вне задачи. " +
                          "Не вызывай других сабагентов и персон — исполнитель здесь ты.");
        }
        else
        {
            sb.AppendLine("## Границы консультанта");
            sb.AppendLine("Ты работаешь только на чтение: изучай файлы, заметки, задачи и базы знаний, " +
                          "но ничего не изменяй (единственное исключение — твоя собственная память). " +
                          "Не вызывай других сабагентов и персон — консультант здесь ты.");
        }

        return new PersonaAgentSpec(
            Description: BuildDescription(persona),
            Prompt: sb.ToString(),
            Tools: tools,
            Model: context.ModelAlias?.Trim(),
            McpServers: servers.Count > 0 ? servers : null,
            MaxTurns: PersonaConsultantToolset.IsExecutor(persona) ? ExecutorMaxTurns : MaxTurns,
            Effort: persona.Effort?.Trim());
    }

    public string Generate(Persona persona, PersonaAgentFileContext context)
    {
        var spec = BuildSpec(persona, context);
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"name: {persona.Handle}");
        sb.AppendLine($"description: {YamlQuote(spec.Description)}");
        sb.AppendLine($"tools: {string.Join(", ", spec.Tools)}");
        if (!string.IsNullOrWhiteSpace(spec.Model))
            sb.AppendLine($"model: {spec.Model}");
        if (!string.IsNullOrWhiteSpace(spec.Effort))
            sb.AppendLine($"effort: {spec.Effort}");
        if (MapColor(persona.Avatar?.Color) is { } color)
            sb.AppendLine($"color: {color}");
        if (spec.McpServers is { Count: > 0 } servers)
            sb.AppendLine($"mcpServers: [{string.Join(", ", servers)}]");
        sb.AppendLine($"maxTurns: {spec.MaxTurns}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(spec.Prompt);
        return sb.ToString();
    }

    // Определение для per-run JSON-файла --agents: name = handle (ключ объекта),
    // omitClaudeMd — по сессии (BareMode), остальные поля — из spec.
    public PersonaAgentDef ToAgentDef(Persona persona, PersonaAgentFileContext context, bool omitClaudeMd)
    {
        var spec = BuildSpec(persona, context);
        return new PersonaAgentDef(
            Name: persona.Handle,
            Description: spec.Description,
            Prompt: spec.Prompt,
            Tools: spec.Tools,
            Model: spec.Model,
            McpServers: spec.McpServers,
            MaxTurns: spec.MaxTurns,
            Effort: spec.Effort,
            OmitClaudeMd: omitClaudeMd);
    }

    // Строка маршрутизации: по ней модель решает, когда звать этого сабагента
    private static string BuildDescription(Persona persona)
    {
        var title = string.IsNullOrWhiteSpace(persona.Role)
            ? persona.Name
            : $"{persona.Role.Trim()} ({persona.Name})";
        var sb = new StringBuilder(title);
        if (!string.IsNullOrWhiteSpace(persona.Description))
            sb.Append(" — ").Append(persona.Description.Trim());
        sb.Append(" Персона-консультант пользователя: вызывай, когда нужна её экспертиза или " +
                  "пользователь упоминает её @handle. Вопрос в prompt формулируй самодостаточно — " +
                  "она не видит текущий разговор. Для вызова в Workflow (скрипт через /workflow " +
                  "или штатный запуск) используй agentType \"" + persona.Handle + "\" — это имя " +
                  "её .md-агента в файловой системе.");
        var isExecutor = PersonaConsultantToolset.IsExecutor(persona);
        if (isExecutor)
            sb.Append(" Исполнитель: может править файлы и запускать команды в рамках задачи.");
        // Нотация oh-my-claudecode: по специальности оркестратор замещает персоной
        // одноимённые типы плагина (см. OmcPersonaRouting; исполнительские — только с write)
        var omcTypes = Prompts.OmcPersonaRouting.AgentTypesFor(
            Prompts.OmcPersonaRouting.EffectiveSpecialty(persona), isExecutor);
        if (omcTypes.Length > 0)
            sb.Append(" Замещает типы субагентов: ")
              .Append(string.Join(", ", omcTypes.Select(t => "oh-my-claudecode:" + t)))
              .Append('.');
        var text = sb.ToString();
        return text.Length <= DescriptionMaxChars ? text : text[..DescriptionMaxChars].TrimEnd() + "…";
    }

    // YAML-safe однострочник в двойных кавычках
    private static string YamlQuote(string text)
    {
        var oneLine = text.Replace("\r", " ").Replace("\n", " ").Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{oneLine}\"";
    }

    private static string? MapColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return null;
        var c = color.Trim().ToLowerInvariant();
        if (CliColors.Contains(c)) return c;
        return ColorFallback.GetValueOrDefault(c);
    }
}
