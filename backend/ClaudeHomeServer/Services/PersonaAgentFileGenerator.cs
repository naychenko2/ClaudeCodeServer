using System.Text;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

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

    // Цвета, которые понимает CLI (Ky в бандле); прочие цвета палитры аватаров мапим на ближний
    private static readonly HashSet<string> CliColors =
        ["red", "blue", "green", "yellow", "purple", "orange", "pink", "cyan"];

    private static readonly Dictionary<string, string> ColorFallback = new(StringComparer.OrdinalIgnoreCase)
    {
        ["brown"] = "orange",
    };

    public string Generate(Persona persona, bool webAllowed)
    {
        var tools = PersonaConsultantToolset.Build(persona, webAllowed);
        var pmemKey = PersonaConsultantToolset.PmemServerKey(persona.Handle);

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"name: {persona.Handle}");
        sb.AppendLine($"description: {YamlQuote(BuildDescription(persona))}");
        sb.AppendLine($"tools: {string.Join(", ", tools)}");
        // model НЕ пинится: файл виден чатам всех провайдеров (cwd проекта/Chats), а пин
        // чужой модели делает сабагента незапускаемым (Claude-чат не запустит deepseek-модель).
        // Сабагент бежит на модели сессии; личность даёт промпт + инструменты + память.
        if (!string.IsNullOrWhiteSpace(persona.Effort))
            sb.AppendLine($"effort: {persona.Effort.Trim()}");
        if (MapColor(persona.Avatar?.Color) is { } color)
            sb.AppendLine($"color: {color}");
        if (persona.MemoryEnabled)
            sb.AppendLine($"mcpServers: [{pmemKey}]");
        sb.AppendLine($"maxTurns: {(PersonaConsultantToolset.IsExecutor(persona) ? ExecutorMaxTurns : MaxTurns)}");
        sb.AppendLine("---");
        sb.AppendLine();

        // Характер персоны — тем же сборщиком, что у собеседника чата и persona_ask
        sb.AppendLine(promptBuilder.Build(persona, persona.Model, greeted: false));

        // Консультационная рамка (по образцу PersonaAskService: персона не видит исходный разговор)
        sb.AppendLine();
        sb.AppendLine("## Ты — консультант");
        sb.AppendLine("Тебя привлекли как консультанта из другого разговора — сам разговор ты не видишь. " +
                      "Отвечай на переданный вопрос от своего лица и в своём характере, по существу; " +
                      "не здоровайся и не представляйся. Твой финальный ответ вернётся тому, кто спросил, — " +
                      "сделай его самодостаточным.");

        if (persona.MemoryEnabled)
        {
            sb.AppendLine();
            sb.AppendLine("## Твоя память");
            sb.AppendLine($"Перед ответом поищи релевантное в своей долгой памяти (mcp__{pmemKey}__memory_search); " +
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

        return sb.ToString();
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
